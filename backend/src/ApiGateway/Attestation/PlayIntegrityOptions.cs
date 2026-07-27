using System.ComponentModel.DataAnnotations;

namespace MageRide.ApiGateway.Attestation;

/// <summary>
/// Play Integrity settings for the Android half of D-30. The gateway decodes the integrity token
/// server-side through Google's <c>decodeIntegrityToken</c> API rather than unwrapping the JWE
/// locally, so the decryption and verification keys never have to leave Google Play Console.
/// </summary>
public sealed class PlayIntegrityOptions
{
    /// <summary>Application id of the Driver / Passenger Android app, e.g. <c>lk.mageride.driver</c>.</summary>
    public string PackageName { get; set; } = string.Empty;

    /// <summary>Play Integrity API base. Overridable so tests and the replica can point elsewhere.</summary>
    [Url]
    public string Endpoint { get; set; } = "https://playintegrity.googleapis.com";

    /// <summary>
    /// Service-account key JSON (<c>Gateway__Attestation__PlayIntegrity__ServiceAccountJson</c>, secret).
    /// Takes precedence over <see cref="ServiceAccountJsonPath"/>.
    /// </summary>
    public string? ServiceAccountJson { get; set; }

    /// <summary>Path to the same JSON, for a mounted secret file.</summary>
    public string? ServiceAccountJsonPath { get; set; }

    /// <summary>OAuth scope for the Play Integrity API.</summary>
    public string Scope { get; set; } = "https://www.googleapis.com/auth/playintegrity";

    /// <summary>
    /// Accepted <c>appIntegrity.appRecognitionVerdict</c> values. <c>PLAY_RECOGNIZED</c> alone
    /// rejects a repackaged APK; add <c>UNRECOGNIZED_VERSION</c> only while a build is in internal
    /// testing, and never in production.
    /// </summary>
    public IList<string> RequiredAppVerdicts { get; init; } = ["PLAY_RECOGNIZED"];

    /// <summary>
    /// Accepted <c>deviceIntegrity.deviceRecognitionVerdict</c> labels — the response carries a
    /// list, and a match on any one of these passes. <c>MEETS_DEVICE_INTEGRITY</c> is the baseline;
    /// requiring <c>MEETS_STRONG_INTEGRITY</c> would exclude most of the Sri Lankan device mix.
    /// </summary>
    public IList<string> RequiredDeviceVerdicts { get; init; } = ["MEETS_DEVICE_INTEGRITY"];

    /// <summary>
    /// Accepted <c>accountDetails.appLicensingVerdict</c> values. Empty disables the check, which
    /// is the default: the Driver App is distributed through Play but a legitimate device that has
    /// never opened the Play Store reports <c>UNEVALUATED</c>, and drivers must not lose SOS.
    /// </summary>
    public IList<string> RequiredLicensingVerdicts { get; init; } = [];

    /// <summary>
    /// How stale a token's <c>requestDetails.timestampMillis</c> may be. Bounds how long a captured
    /// token stays useful; Play Integrity tokens are otherwise valid for as long as Google will
    /// decode them.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
    public TimeSpan MaxTokenAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a verdict for the same token is reused. A cold start makes several sensitive calls
    /// back to back; without this each one is a round trip to Google inside the request path.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:30:00")]
    public TimeSpan VerdictCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Timeout for the decode call. Kept well under D6' §8.3's 15 s API budget.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:00:30")]
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
