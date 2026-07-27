namespace MageRide.Shared.Http.Idempotency;

/// <summary>Configuration for <see cref="IdempotencyMiddleware"/> (D3' §0 "Idempotency").</summary>
public sealed class IdempotencyOptions
{
    /// <summary>Configuration section bound by <c>AddMageRideIdempotency</c>.</summary>
    public const string SectionName = "Idempotency";

    /// <summary>
    /// Methods that require an <c>Idempotency-Key</c>. D3' §0 mandates it on POST; a service may
    /// widen this, but narrowing it drops the R-14 guarantee.
    /// </summary>
    public HashSet<string> Methods { get; init; } = new(StringComparer.OrdinalIgnoreCase) { "POST" };

    /// <summary>Maximum key length (D3' §0: ULID/UUID ≤128).</summary>
    public int MaxKeyLength { get; set; } = 128;

    /// <summary>Minimum key length. A ULID is 26 characters, a hyphenated UUID 36.</summary>
    public int MinKeyLength { get; set; } = 16;

    /// <summary>
    /// Largest response body captured for replay. A response above this is returned to the first
    /// caller but not stored, so a retry re-executes instead of replaying — the middleware logs a
    /// warning when it happens. Ride and payment responses are far below this.
    /// </summary>
    public int MaxStoredResponseBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Largest request body hashed for reuse detection. Larger bodies are hashed as they stream,
    /// so this only bounds the buffer used for re-reading the body downstream.
    /// </summary>
    public int MaxBufferedRequestBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Statuses stored for replay. 5xx is deliberately excluded: a server error must not be
    /// pinned to the key, or the client's retry would replay the failure forever.
    /// </summary>
    public Func<int, bool> ShouldStore { get; set; } = static status => status is >= 200 and < 500;
}
