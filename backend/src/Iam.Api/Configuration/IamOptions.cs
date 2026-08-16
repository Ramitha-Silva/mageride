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

/// <summary>
/// SMS delivery for the OTP (D7' §4.2 <c>Sms__FitSmsApiToken</c> / <c>Sms__SecondaryGateway</c>).
/// </summary>
/// <remarks>
/// <b>Fit SMS is the platform's only SMS gateway (AL-60).</b> D6' §7.3 named Notify.lk as the
/// primary and it was implemented as one; the account moved and the class is gone rather than
/// left as a switchable alternative, because a second gateway that nobody holds credentials for
/// is a code path no deployment exercises and no test can honestly cover. The
/// <c>Sms__SecondaryGateway</c> half of §7.3 is unchanged — it is a generic HTTP shape, not a
/// named provider, and D-33 still needs a second transport for the SOS.
/// </remarks>
public sealed class SmsOptions
{
    public const string SectionName = "Sms";

    public const string DevProvider = "dev";
    public const string FitSmsProvider = "fitsms";

    /// <summary>
    /// <c>dev</c> logs the code instead of sending it; <c>fitsms</c> is the real gateway.
    /// </summary>
    [Required]
    public string Provider { get; set; } = DevProvider;

    /// <summary>
    /// Fit SMS v4 REST base address. The sender posts <c>sms/send</c> relative to it, so it ends
    /// in a slash — see <see cref="MageRide.Iam.Otp.FitSmsOtpSender"/>.
    /// </summary>
    [Required]
    public string FitSmsBaseUrl { get; set; } = "https://app.fitsms.lk/api/v4/";

    /// <summary>
    /// Fit SMS bearer token — D7' §4.2's <c>Sms__FitSmsApiToken</c>. Their tokens are issued in the
    /// form <c>{id}|{secret}</c> and the whole string is the credential, pipe included.
    /// </summary>
    public string? FitSmsApiToken { get; set; }

    /// <summary>
    /// Registered sender mask on Fit SMS. Their limit is 11 characters for an alphanumeric mask
    /// (a mask that is a telephone number is not bound by it), which
    /// <c>AddSmsOptions</c> checks at start.
    /// </summary>
    [Required]
    public string FitSmsSenderId { get; set; } = "The Change";

    /// <summary>
    /// The <c>type</c> Fit SMS is told to send a non-ASCII body as.
    /// </summary>
    /// <remarks>
    /// AL-26 makes Sinhala the default language, so the <em>common</em> OTP on this platform is
    /// UCS-2 and not GSM-7 — which is why this is a setting rather than a constant. Their send
    /// documentation names only <c>plain</c> while the rest of their API lists <c>unicode</c>
    /// beside it; if a deployment finds their gateway refuses the latter, this becomes
    /// <c>plain</c> and no code changes. Empty means "send everything as
    /// <see cref="MageRide.Iam.Otp.FitSmsOtpSender.PlainType"/>".
    /// </remarks>
    public string FitSmsUnicodeType { get; set; } = "unicode";

    /// <summary>
    /// D7' §4.2 <c>Sms__SecondaryGateway</c> — the Dialog/Mobitel fallback of D6' §7.3. Empty
    /// disables the fallback, which is legal: a deployment with one gateway is a deployment with
    /// one gateway, not a broken one.
    /// </summary>
    public string? SecondaryGateway { get; set; }

    /// <summary>Bearer credential for <see cref="SecondaryGateway"/>.</summary>
    public string? SecondaryApiKey { get; set; }

    /// <summary>Sender mask on the secondary gateway, when it differs from the primary's.</summary>
    public string? SecondarySenderId { get; set; }

    /// <summary>
    /// Sends attempted against one gateway before the fallback is tried. D6' §7.3: "Retry:
    /// 2 attempts".
    /// </summary>
    [Range(1, 5)]
    public int MaxAttemptsPerGateway { get; set; } = 2;

    /// <summary>Per-attempt budget. An OTP that lands after the user has given up is not an OTP.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

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

    /// <summary>
    /// Keys that have been rotated out but whose tokens are still alive (D7' §13, 90 days).
    /// Published in the JWKS and accepted on validation; never used to sign.
    /// </summary>
    /// <remarks>
    /// A rotation is two deploys, not one: the incoming key becomes
    /// <see cref="SigningKeyPem"/> and the outgoing one moves here, where it stays for at least
    /// one <see cref="AccessTokenLifetime"/> plus the D-21 JWKS cache window. Dropping it in the
    /// same deploy that promotes the new key would 401 every token issued in the previous
    /// half hour.
    /// </remarks>
    public IList<string> RetiredSigningKeyPems { get; init; } = [];

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

/// <summary>
/// The three controls AL-37 kept when it removed the MFA/TOTP step: failed-attempt lock-out,
/// session binding and an optional IP allow-list on internal roles.
/// </summary>
/// <remarks>
/// Session binding is not configurable and so is not here — it is the <c>device_id</c> claim plus
/// the C003 partial unique index, which is why a portal sign-in from a second browser ends the
/// first (0107).
/// </remarks>
public sealed class AuthPolicyOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Consecutive wrong passwords before the account locks. No spec fixes the number; five
    /// matches D-32's OTP budget, which is the closest thing the platform has to a precedent.
    /// </summary>
    [Range(1, 20)]
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>
    /// How long a locked account stays locked. Long enough to make online guessing pointless,
    /// short enough that an admin locked out by a fat-fingered password is not paging anybody.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:30", "24:00:00")]
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Optional CIDRs the six internal roles may sign in from (AL-37). Empty disables the check
    /// — the ADD calls it optional, and a platform whose only admin is at home on DHCP would
    /// otherwise be one lease renewal from locked out.
    /// </summary>
    public IList<string> InternalRoleIpAllowList { get; init; } = [];

    /// <summary>
    /// Read the caller's address from <c>X-Forwarded-For</c>. True because every request arrives
    /// through the YARP gateway (C008), where the socket address is the gateway's own.
    /// </summary>
    public bool TrustForwardedFor { get; set; } = true;

    /// <summary>
    /// PBKDF2 iterations for new password hashes. Existing rows carry their own count, so raising
    /// this is safe and takes effect the next time a password is set.
    /// </summary>
    [Range(100_000, 5_000_000)]
    public int PasswordIterations { get; set; } = 600_000;

    /// <summary>The contract's <c>PasswordLogin.password</c> <c>minLength: 12</c>.</summary>
    [Range(8, 256)]
    public int MinimumPasswordLength { get; set; } = 12;

    /// <summary>
    /// HMAC key for <c>iam.phone_lookups.phone_hash</c> (P-03, migration 0108). Required outside
    /// Development; see <see cref="MageRide.Iam.Domain.PhoneHasher"/> for why an unkeyed digest of
    /// a Sri Lankan mobile number is not a hash of anything.
    /// </summary>
    /// <remarks>
    /// <b>Not rotatable in place.</b> Every existing digest is keyed with the old value, so a new
    /// key partitions the table rather than re-keying it. That is the trade for a lookup log that
    /// can correlate repeats without storing a number, and it is the reason this is not
    /// <c>Otp:PepperKey</c>, which is rotated freely.
    /// </remarks>
    public string? PhoneHashKey { get; set; }

    /// <summary>
    /// Shared secret for <c>GET /v1/users/lookup</c>, presented in
    /// <c>X-MageRide-Internal-Key</c>. D3' §0 puts the route on service-to-service mTLS and the
    /// gateway refuses <c>/v1/internal/**</c> at the edge — but this route is not under that
    /// prefix and <b>is</b> forwarded by the <c>iam-users</c> gateway route, so leaving it on the
    /// edge's good manners would publish a registration oracle. Unset means the route is not
    /// mapped at all: a deployment that forgets it gets 404s, not an open door. Replaced by the
    /// mTLS peer identity in C042.
    /// </summary>
    public string? InternalApiKey { get; set; }
}

/// <summary>
/// The two external identity providers AL-07 puts on the portals: Google (Admin + Fleet) and
/// Apple (Fleet).
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    public GoogleOidcOptions Google { get; init; } = new();

    public AppleOidcOptions Apple { get; init; } = new();
}

/// <summary>Google OIDC — ID-token sign-in and the Admin Portal's authorization-code arm.</summary>
public sealed class GoogleOidcOptions
{
    /// <summary>
    /// Accepted <c>aud</c> values — the portals' OAuth client ids. An ID token minted for
    /// somebody else's client is a valid Google token and must not be a MageRide session.
    /// </summary>
    public IList<string> ClientIds { get; init; } = [];

    /// <summary>Client secret for the <c>/v1/admin/auth/login</c> authorization-code exchange.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Where the code is exchanged for an <c>id_token</c>.</summary>
    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";

    /// <summary>Google's signing keys.</summary>
    public string JwksUrl { get; set; } = "https://www.googleapis.com/oauth2/v3/certs";

    /// <summary>Google mints both spellings and has done for years.</summary>
    public IList<string> Issuers { get; init; } = ["https://accounts.google.com", "accounts.google.com"];

    /// <summary>Fallback redirect when the request body carries none.</summary>
    public string? RedirectUri { get; set; }
}

/// <summary>Apple "Sign in with Apple" — Fleet Portal only (AL-07).</summary>
public sealed class AppleOidcOptions
{
    /// <summary>Accepted <c>aud</c> values — the Services ID of the Fleet Portal.</summary>
    public IList<string> ClientIds { get; init; } = [];

    public string JwksUrl { get; set; } = "https://appleid.apple.com/auth/keys";

    public IList<string> Issuers { get; init; } = ["https://appleid.apple.com"];
}

/// <summary>
/// The one MQTT fact iam-svc owns that the kernel's <see cref="MageRide.Shared.Mqtt.MqttOptions"/>
/// cannot: how long a ride is assumed to run when E-02 asks for <c>active-ride + 2 h</c>.
/// </summary>
/// <remarks>
/// Bound to the same <c>Mqtt</c> section, because it is the same knob-set from an operator's
/// point of view. See <c>MqttTokenService</c> for why an assumption is needed at all — nothing in
/// D4' §5 stores a ride's expected end.
/// </remarks>
public sealed class IamMqttOptions
{
    public const string SectionName = "Mqtt";

    /// <summary>
    /// The longest a Mode C ride is assumed to run, measured from its creation. The session
    /// token covers this plus <c>Mqtt:SessionTokenRideGrace</c>, floored at
    /// <c>Mqtt:SessionTokenMinimumTtl</c> (E-02).
    /// </summary>
    [Range(typeof(TimeSpan), "00:15:00", "24:00:00")]
    public TimeSpan MaxRideDuration { get; set; } = TimeSpan.FromHours(4);
}
