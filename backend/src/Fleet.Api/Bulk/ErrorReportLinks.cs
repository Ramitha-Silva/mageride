using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MageRide.Fleet.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet.Bulk;

/// <summary>
/// Signs and verifies the <c>errorReportUrl</c> <c>fleet.yaml</c> promises with a bulk job.
/// </summary>
/// <remarks>
/// <para>
/// US-13.1 asks for the Epic 3 "downloadable error report" and the contract types the field as a
/// URI. In a deployment with object storage that would be a pre-signed link; there is no bucket in
/// front of this service (C125) and the report is a projection of
/// <c>registry.fleet_bulk_job_rows</c> that would have to be materialised to put one there — so
/// the service serves it and the link carries an HMAC. The properties that matter survive: the link
/// is unguessable, it expires, and following it needs no bearer, which is what lets the Fleet
/// Portal hand it straight to a browser download.
/// </para>
/// <para>
/// The signature covers the fleet as well as the job, so a link cannot be re-pointed at another
/// organisation's job by editing the path — and the read behind it goes through
/// <c>registry.fleet_bulk_job_rows_fleet</c>, which is scoped anyway. Two locks, the same
/// arrangement as everywhere else in this service.
/// </para>
/// <para>
/// The shape provisioning-svc's <c>IErrorReportLinks</c> takes, deliberately: an operator who has
/// used the tracker CSV recognises the flow, and a future object-storage swap replaces both at
/// once.
/// </para>
/// </remarks>
public interface IErrorReportLinks
{
    /// <summary>The relative URL, query string included.</summary>
    string Create(Guid fleetId, Guid jobId);

    /// <summary>Whether a presented signature is one this service issued and has not expired.</summary>
    bool Verify(Guid fleetId, Guid jobId, string? expires, string? signature);
}

/// <inheritdoc cref="IErrorReportLinks"/>
internal sealed class ErrorReportLinks : IErrorReportLinks
{
    private readonly FleetOptions _options;
    private readonly TimeProvider _clock;
    private readonly byte[] _key;

    public ErrorReportLinks(
        IOptions<FleetOptions> options, TimeProvider clock, ILogger<ErrorReportLinks> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _clock = clock ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(_options.ErrorReportSigningKey))
        {
            // A generated key is correct for one instance and wrong for several: a link minted by
            // replica A does not verify on replica B, and the operator sees a 404 on a link they
            // were handed a second ago.
            _key = RandomNumberGenerator.GetBytes(32);

            logger.LogWarning(
                "Fleet:ErrorReportSigningKey is not configured; bulk error-report links are signed with a key "
                + "generated for this process. They will not verify on another replica or survive a restart.");
        }
        else
        {
            _key = Encoding.UTF8.GetBytes(_options.ErrorReportSigningKey);
        }
    }

    public string Create(Guid fleetId, Guid jobId)
    {
        var expires = (_clock.GetUtcNow() + _options.ErrorReportUrlTtl).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        return $"/v1/fleets/{fleetId}/vehicles/bulk/{jobId}/errors.csv" +
               $"?expires={expires}&signature={Sign(fleetId, jobId, expires)}";
    }

    public bool Verify(Guid fleetId, Guid jobId, string? expires, string? signature)
    {
        if (string.IsNullOrWhiteSpace(expires) || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        if (!long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var expiresUnix)
            || DateTimeOffset.FromUnixTimeSeconds(expiresUnix) <= _clock.GetUtcNow())
        {
            return false;
        }

        byte[] presented;

        try
        {
            presented = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        // Hex rather than base64url, matching subscription-svc's signed document links: the value
        // is a query-string parameter that ends up in a browser address bar and in a support
        // ticket, and hex survives being copied out of both without a padding argument.
        return CryptographicOperations.FixedTimeEquals(
            presented, Convert.FromHexString(Sign(fleetId, jobId, expires)));
    }

    private string Sign(Guid fleetId, Guid jobId, string expires) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{fleetId}|{jobId}|{expires}")));
}
