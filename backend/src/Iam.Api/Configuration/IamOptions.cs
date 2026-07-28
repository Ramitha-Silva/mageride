using System.ComponentModel.DataAnnotations;

namespace MageRide.Iam.Configuration;

/// <summary>
/// OTP minting and rate limiting (D-32; D7' §4.2 <c>Otp__ResendCooldownSec</c>=60,
/// <c>Otp__MaxPerHour</c>=5).
/// </summary>
public sealed class OtpOptions
{
    public const string SectionName = "Otp";

    /// <summary>Seconds a caller must wait between two sends to the same number (D-32).</summary>
    [Range(0, 3600)]
    public int ResendCooldownSec { get; set; } = 60;

    /// <summary>Sends allowed per rolling hour per number (D-32).</summary>
    [Range(1, 100)]
    public int MaxPerHour { get; set; } = 5;

    /// <summary>
    /// Wrong-code entries allowed against one <c>authId</c> before it answers <c>423 otp-locked</c>.
    /// D3' §0 names the 423 but no spec fixes the count; five matches the send budget.
    /// </summary>
    [Range(1, 20)]
    public int MaxVerifyAttempts { get; set; } = 5;

    /// <summary>
    /// How long a minted code stays usable. No spec fixes this either — five minutes is long
    /// enough for an SMS to land on a slow network and short enough that a leaked
    /// <c>iam.otp_attempts</c> row is worthless.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Secret keying the HMAC that hashes a code into <c>iam.otp_attempts.otp_hash</c>. Without
    /// it a database leak plus a known <c>authId</c> is a 10^6 offline search; with it the hash
    /// is useless on its own. Left empty only in Development, where an ephemeral key is minted.
    /// </summary>
    public string? PepperKey { get; set; }
}

/// <summary>SMS delivery for the OTP (D7' §4.2 <c>Sms__NotifyLkApiKey</c>).</summary>
public sealed class SmsOptions
{
    public const string SectionName = "Sms";

    public const string DevProvider = "dev";
    public const string NotifyLkProvider = "notifylk";

    /// <summary>
    /// <c>dev</c> logs the code instead of sending it; <c>notifylk</c> is the real gateway and
    /// lands with C026.
    /// </summary>
    [Required]
    public string Provider { get; set; } = DevProvider;

    public string? NotifyLkApiKey { get; set; }

    /// <summary>
    /// Guard rail: the dev sender writes a live OTP into the log, so outside Development it has
    /// to be asked for explicitly. Used by the replica, which runs on synthetic numbers.
    /// </summary>
    public bool AllowDevSenderOutsideDevelopment { get; set; }
}

/// <summary>
/// Token issuance (D-29). Binds the same <c>Jwt</c> section the kernel's
/// <see cref="MageRide.Shared.Auth.JwtOptions"/> reads for validation, so the issuer this service
/// signs with and the issuer every other service checks cannot drift apart.
/// </summary>
public sealed class TokenOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// RS256 private key, PKCS#8 or PKCS#1 PEM (D7' §4.2 <c>Jwt__SigningKeyPem</c>). Required
    /// outside Development; Development mints an ephemeral key and warns.
    /// </summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>
    /// <c>kid</c> published in the JWKS and stamped on every token. Derived from the RFC 7638
    /// thumbprint of the public key when empty, which keeps it stable across restarts of the
    /// same key and distinct across a rotation.
    /// </summary>
    public string? SigningKeyId { get; set; }

    /// <summary><c>iss</c>. Must match <c>Jwt:Issuer</c> wherever these tokens are validated.</summary>
    public string Issuer { get; set; } = "https://iam.mageride.lk";

    /// <summary>
    /// <c>aud</c>. Empty emits no audience claim, which is the platform default: D3' §0 lists
    /// <c>sub</c>, <c>role</c>, <c>fleet_role?</c>, <c>device_id</c> and <c>app</c> and no audience.
    /// </summary>
    public IList<string> Audiences { get; init; } = [];

    /// <summary>D-29 pins this at 30 minutes and the contract pins <c>expiresIn</c> at 1800.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "00:30:00")]
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>ADD §12.1: 30-day refresh. Sliding — a rotation restarts the window.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "365.00:00:00")]
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Key for the HMAC that binds an opaque refresh token to its <c>iam.sessions</c> row.
    /// Derived from the signing key when empty; set it explicitly so a 90-day signing-key
    /// rotation (D7' §13) does not invalidate every live refresh token.
    /// </summary>
    public string? RefreshTokenKey { get; set; }
}
