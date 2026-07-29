using MageRide.Shared.Primitives;

namespace MageRide.Dispatch.Domain;

/// <summary>
/// The four values <c>dispatch.directional_filters.cleared_reason</c>'s CHECK allows
/// (migration 0707, DT-04).
/// </summary>
public static class DirectionalClearReasons
{
    /// <summary>The <c>max_duration</c> ran out and the durable timer fired.</summary>
    public const string Expiry = "expiry";

    /// <summary><c>DELETE /v1/standby/directional</c> — and it still consumes the use (US-6A.19).</summary>
    public const string Manual = "manual";

    /// <summary>The driver left standby, or R-15's last-will grace took them out of the pool.</summary>
    public const string Offline = "offline";

    /// <summary>
    /// The driver was matched to a ride and <c>clear_on_first_trip</c> is on — off by default, so
    /// the ordinary filter survives the hire it produced (D5' §12.2 gives no rule either way).
    /// </summary>
    public const string FirstMatchedTrip = "first_matched_trip";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Expiry, Manual, Offline, FirstMatchedTrip,
    };
}

/// <summary>A row of <c>dispatch.directional_filters</c> (migration 0707, DT-01/DT-03).</summary>
/// <param name="UsedDate">
/// The Asia/Colombo business date the activation was counted against (D-38). The daily-use limit is
/// <c>COUNT(*) per (driver_id, used_date)</c>, which is why a manual turn-off cannot give the use
/// back — the row stays, cleared.
/// </param>
public sealed record DirectionalFilterRow(
    Guid Id,
    Guid DriverId,
    GeoPoint Destination,
    string? Label,
    DateTimeOffset SetAt,
    DateTimeOffset ExpiresAt,
    DateOnly UsedDate,
    DateTimeOffset? ClearedAt,
    string? ClearedReason);

/// <summary>
/// The single row of <c>dispatch.directional_config</c> — DT-02's admin-tunable predicate
/// parameters, defaulted to D5' §12.1's values.
/// </summary>
/// <remarks>
/// A table rather than a <c>Dispatch:</c> configuration section because
/// <c>PUT /v1/admin/dispatch/directional-config</c> changes them at runtime and every replica has to
/// agree instantly (migration 0707's own header says so).
/// </remarks>
public sealed record DirectionalConfigRow(
    int ThetaMaxDeg,
    int DetourMaxM,
    int ProgressMinM,
    int MaxUsesPerDay,
    int MaxDurationSec,
    bool ClearOnFirstTrip)
{
    /// <summary>
    /// What the D5' §12.1 defaults are, used only when the singleton row is missing — which it
    /// never is in a migrated database (0707 seeds it) and would mean the schema is not applied.
    /// </summary>
    public static readonly DirectionalConfigRow Defaults = new(45, 2_000, 250, 2, 7_200, false);

    public TimeSpan MaxDuration => TimeSpan.FromSeconds(MaxDurationSec);
}

/// <summary>
/// The driver-facing state of Directional Travel — what <c>GET /v1/standby/directional</c> answers
/// and what the set and clear routes return (DT-08).
/// </summary>
/// <param name="UsesRemaining">
/// <c>max_uses_per_day − COUNT(*)</c> for today's Colombo date, floored at zero. The number the
/// driver-app banner shows (US-6A.21).
/// </param>
public sealed record DirectionalState(
    DirectionalFilterRow? Filter,
    int UsesRemaining,
    int MaxDurationSec,
    TimeSpan TimeRemaining)
{
    public bool Active => Filter is not null;
}
