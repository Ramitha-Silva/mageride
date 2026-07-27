namespace MageRide.Shared.Http;

/// <summary>The MageRide-specific request headers named by D3' §0.</summary>
public static class MageRideHeaders
{
    /// <summary>ULID/UUID, ≤128 chars. Required on POST mutations (R-14, R-18).</summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>Play Integrity (Android) / App Attest (iOS) token. Validated at the gateway (D-30).</summary>
    public const string Attestation = "X-Attestation";

    /// <summary>Client app version; below the per-platform floor the gateway answers 426 (D-31).</summary>
    public const string AppVersion = "X-App-Version";

    /// <summary><c>android</c> | <c>ios</c>. Selects the minimum-version row (D-31).</summary>
    public const string Platform = "X-Platform";
}

/// <summary>Values for <see cref="MageRideHeaders.Platform"/> (D-31).</summary>
public static class ClientPlatforms
{
    public const string Android = "android";
    public const string Ios = "ios";
}
