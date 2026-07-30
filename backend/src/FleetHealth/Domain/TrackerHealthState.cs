namespace MageRide.FleetHealth.Domain;

/// <summary>
/// US-3.13's four tracker states, as <c>telemetry.device_health.observed_state</c> spells them and
/// as <c>backend/contracts/fleet-health.yaml</c>'s <c>TrackerState</c> renders them.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no C# classifier here, and that is the point.</b> The state is decided by
/// <c>telemetry.device_health_state()</c> (migration 1805) — one SQL expression called by both the
/// fleet dashboard read and the transition sweep. A second implementation in this assembly would be
/// a second opinion about the same row, and the two would show an operator one thing while alerting
/// on another. What lives here is the vocabulary and the two spellings' mapping.
/// </para>
/// <para>
/// The stored form is upper-case (a <c>ck_device_health_state</c> CHECK domain, matching
/// <c>prov.tracker_bindings.state</c> and every other enumerated column on the platform) and the
/// wire form is lower-case (D3' §0 renders enums lower-case). Both are written down once.
/// </para>
/// </remarks>
public static class TrackerHealthStates
{
    /// <summary>A ping inside <c>Health:StaleAfter</c> and no unanswered last will.</summary>
    public const string Online = "ONLINE";

    /// <summary>
    /// Silent longer than <c>Health:StaleAfter</c> (US-3.13's "no ping &gt; 5 min"), or the broker's
    /// last will has fired since the last ping.
    /// </summary>
    public const string Stale = "STALE";

    /// <summary>Silent longer than <c>Health:OfflineAfter</c> (US-3.13's "no ping &gt; 30 min").</summary>
    public const string Offline = "OFFLINE";

    /// <summary>
    /// The tracker's credentials were revoked (US-3.8). <b>Not</b> a T-08 quarantine, which is a
    /// binding held pending an admin decision (US-3.4) and may return to service.
    /// </summary>
    public const string Decommissioned = "DECOMMISSIONED";

    /// <summary>Every state, in the order a dashboard reads them — best to worst.</summary>
    public static readonly IReadOnlyList<string> All = [Online, Stale, Offline, Decommissioned];

    private static readonly Dictionary<string, string> WireNames = new(StringComparer.Ordinal)
    {
        [Online] = "online",
        [Stale] = "stale",
        [Offline] = "offline",
        [Decommissioned] = "decommissioned",
    };

    /// <summary>
    /// The contract's rendering of a stored state.
    /// </summary>
    /// <remarks>
    /// An unrecognised value is returned verbatim rather than coerced to <c>offline</c>: the CHECK
    /// domain makes it impossible, and guessing would hide a migration that widened it.
    /// </remarks>
    public static string ToWire(string? stored) =>
        stored is not null && WireNames.TryGetValue(stored, out var wire) ? wire : stored ?? "offline";

    /// <summary>Whether <paramref name="stored"/> is one of the four.</summary>
    public static bool IsKnown(string? stored) => stored is not null && WireNames.ContainsKey(stored);
}

/// <summary>
/// The binding lifecycle states <c>prov.tracker_bindings.state</c> carries (migration 0401, T-08).
/// Mirrored onto <c>telemetry.device_health.binding_state</c> from <c>provisioning.events</c>.
/// </summary>
public static class TrackerBindingStates
{
    public const string Active = "ACTIVE";

    /// <summary>T-08 anti-clone hold, pending the US-3.4 admin resolution.</summary>
    public const string Quarantined = "QUARANTINED";

    /// <summary>US-3.8 decommission, or an unbind. No further ingest is possible.</summary>
    public const string Revoked = "REVOKED";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Active, Quarantined, Revoked };

    public static bool IsKnown(string? state) => state is not null && All.Contains(state);
}
