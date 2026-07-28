using System.Security.Cryptography;
using MageRide.Provisioning.Persistence;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace MageRide.Provisioning.Credentials;

/// <summary>
/// Publishes the certificate revocation list the MQTT broker checks device certificates against
/// (T-12).
/// </summary>
/// <remarks>
/// <para>
/// This is the MQTT half of "a revoked credential stops authenticating". The TCP half is the Redis
/// channel and the validate endpoint, because an adapter terminates the socket itself and can be
/// told directly; a broker cannot, and X.509's answer to "this certificate is no longer good" is a
/// CRL. EMQX fetches it from the distribution point written into each certificate and re-fetches
/// on <c>crl_cache.refresh_interval</c>, which is what puts a number on the 60 s.
/// </para>
/// <para>
/// <b>The CRL number is the publication instant in Unix seconds.</b> RFC 5280 requires it to be
/// monotonic so a verifier can tell a newer list from a replayed older one, and a counter in a
/// table would need its own transaction and its own recovery story for a number nothing else
/// reads. A clock that goes backwards would break it; so would a clock that goes backwards for
/// every other time-ordered decision in this service.
/// </para>
/// </remarks>
public interface ICrlService
{
    /// <summary>The current CRL, DER-encoded, and the PEM wrapper around it.</summary>
    Task<CrlDocument> GetAsync(CancellationToken cancellationToken);
}

/// <param name="Der">DER bytes — what <c>application/pkix-crl</c> carries.</param>
/// <param name="Pem">The same list, PEM-armoured, for <c>enable_crl_check</c>'s HTTP fetch.</param>
public sealed record CrlDocument(byte[] Der, string Pem, DateTimeOffset GeneratedAt);

/// <inheritdoc cref="ICrlService"/>
public sealed class CrlService(
    INpgsqlConnectionFactory connectionFactory,
    IDeviceCertificateRepository certificates,
    ICertificateAuthority authority,
    TimeProvider clock,
    ILogger<CrlService> logger) : ICrlService
{
    /// <summary>
    /// How long a built list is served before it is rebuilt.
    /// </summary>
    /// <remarks>
    /// Every broker in the cluster fetches this on the same refresh interval, so without a cache a
    /// revocation-free hour still costs one full table scan per broker per interval. Ten seconds
    /// is far inside the 60 s T-12 budget and turns that into one scan.
    /// </remarks>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a published list claims to be current.
    /// </summary>
    /// <remarks>
    /// A verifier that cannot re-fetch treats a CRL past its <c>nextUpdate</c> as unusable and
    /// fails the handshake. An hour is long enough to survive a deployment of this service and
    /// short enough that a broker cut off from it stops trusting a stale list the same day.
    /// </remarks>
    private static readonly TimeSpan Validity = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private CrlDocument? _cached;

    public async Task<CrlDocument> GetAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (_cached is { } cached && now - cached.GeneratedAt < CacheDuration)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            // Re-checked under the gate: a burst of broker fetches on a cold cache would otherwise
            // all build the same list.
            if (_cached is { } fresh && now - fresh.GeneratedAt < CacheDuration)
            {
                return fresh;
            }

            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            var revoked = await certificates.ListRevokedAsync(connection, null, now, cancellationToken);
            var der = authority.BuildCrl(revoked, now.ToUnixTimeSeconds(), now, Validity);

            var document = new CrlDocument(der, PemEncoding.WriteString("X509 CRL", der) + '\n', now);

            logger.LogDebug("Rebuilt the device CRL with {Count} entrie(s)", revoked.Count);

            _cached = document;
            return document;
        }
        finally
        {
            _gate.Release();
        }
    }
}
