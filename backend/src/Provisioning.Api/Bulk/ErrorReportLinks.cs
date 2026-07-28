using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Provisioning.Bulk;

/// <summary>
/// Signs and verifies the <c>errorReportUrl</c> D3' promises with a bulk job.
/// </summary>
/// <remarks>
/// <para>
/// D3' calls it "a signed URL of the per-row error report", which in a deployment with object
/// storage would be a pre-signed S3 link. There is no bucket in front of this service and the
/// report is a projection of <c>prov.bulk_job_rows</c> that would have to be materialised to put
/// one there, so the service serves it and the link carries an HMAC instead. The properties D3'
/// wanted are the ones that matter: the link is unguessable, it expires, and following it needs no
/// bearer token — which is what lets the Admin Portal hand it to a browser download.
/// </para>
/// <para>
/// The signature covers the fleet as well as the job, so a link cannot be re-pointed at another
/// fleet's job by editing the path.
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
public sealed class ErrorReportLinks : IErrorReportLinks
{
    private readonly ProvisioningOptions _options;
    private readonly TimeProvider _clock;
    private readonly byte[] _key;

    public ErrorReportLinks(
        IOptions<ProvisioningOptions> options, TimeProvider clock, ILogger<ErrorReportLinks> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _clock = clock ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(_options.ErrorReportSigningKey))
        {
            // A generated key is correct for one instance and wrong for several: a link minted by
            // replica A does not verify on replica B, and the operator sees an expired-looking 403
            // on a link they were handed a second ago.
            _key = RandomNumberGenerator.GetBytes(32);

            logger.LogWarning(
                "Provisioning:ErrorReportSigningKey is not configured; bulk error-report links are signed with a " +
                "key generated for this process. They will not verify on another replica or survive a restart.");
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

        return $"/v1/fleets/{fleetId}/trackers/bulk/{jobId}/errors.csv" +
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
            presented = Base64Url.Decode(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(presented, Base64Url.Decode(Sign(fleetId, jobId, expires)));
    }

    private string Sign(Guid fleetId, Guid jobId, string expires) =>
        Base64Url.Encode(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{fleetId}|{jobId}|{expires}")));
}
