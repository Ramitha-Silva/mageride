namespace MageRide.AdminBff.Domain;

/// <summary>
/// A <c>registry.vehicles</c> row as the moderation and train surfaces read it.
/// </summary>
/// <remarks>
/// Deliberately not the whole table: admin-bff reads what it is about to change and what the
/// audit's before-image needs, and nothing else. The full vehicle view is C064's directory.
/// </remarks>
public sealed record AdminVehicle(
    Guid Id,
    Guid OwnerId,
    string RegistrationNumber,
    string VehicleType,
    string Mode,
    string Status,
    string DispatchState,
    string DriverName);

/// <summary>What one suspension changed. The audit row's after-image, and the 200's body.</summary>
public sealed record ModerationOutcome(Guid SubjectId, string Status, string? Reason);

/// <summary>A <c>config.operating_cities</c> row (AL-27).</summary>
public sealed record OperatingCity(
    string Code,
    string NameEn,
    string NameSi,
    string NameTa,
    double CentroidLat,
    double CentroidLng,
    int SortOrder,
    bool IsActive);

/// <summary>A <c>config.feature_flags</c> row (migration 0202, URD §2.3).</summary>
public sealed record FeatureFlag(
    string Key,
    bool Enabled,
    string? Description,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A Mode C rate-card row of <c>fares.tariffs</c> (US-14.4).
/// </summary>
/// <remarks>
/// Versioned by <c>effective_from</c> and never mutated: a completed ride must stay reconcilable
/// against the rate that priced it (D-10). Publishing is therefore an insert of a new version, not
/// an update of the current one.
/// </remarks>
public sealed record TariffRow(
    string VehicleType,
    long FirstKmMinor,
    long PerKmMinor,
    int PeakSurchargePct,
    int NightSurchargePct,
    string Currency);

/// <summary>
/// A recurring daily surcharge window of <c>fares.peak_windows</c>, Asia/Colombo wall-clock.
/// </summary>
/// <remarks>
/// <see cref="EndLocal"/> may be <em>earlier</em> than <see cref="StartLocal"/> — the night window
/// wraps midnight (22:00–05:00) and migration 1001 declines to constrain the ordering for exactly
/// that reason. Nothing here may "fix" it.
/// </remarks>
public sealed record PeakWindowRow(string Kind, TimeOnly StartLocal, TimeOnly EndLocal, int MultiplierPct);

/// <summary>A train, which is a Mode A <c>registry.vehicles</c> row and not its own table (US-2.17).</summary>
/// <remarks>
/// <b>There is no <c>registry.trains</c>.</b> AL-09 puts <c>train</c> in the canonical vehicle-type
/// enum and D4' §2 gives a train the same registration, mode and status columns every other vehicle
/// has — so a second table would be a second answer to "which vehicles exist", and query-svc,
/// fanout-svc and the position pipeline all read the first one. The <c>trainNumber</c> the contract
/// takes is the registration number; <c>name</c> is the <c>driver_name</c> column, which is the
/// label passengers see beside a vehicle (US-2.12).
/// </remarks>
public sealed record Train(Guid TrainId, string Name, string TrainNumber, Guid? RouteId, bool Active);

/// <summary>
/// One row of <c>audit.events</c> as <c>GET /v1/admin/audit-log</c> answers it (US-19.3).
/// </summary>
/// <remarks>
/// <c>Before</c>, <c>After</c> and <c>Detail</c> arrive as raw JSON strings and are re-emitted
/// verbatim. Round-tripping them through a CLR shape would mean an image written by a component
/// that has since changed came back reshaped — an audit trail that edits its own history on read.
/// </remarks>
public sealed record AuditLogRow(
    Guid EventId,
    Guid? ActorId,
    string? ActorRole,
    string Action,
    string? EntityType,
    Guid? EntityId,
    string? Before,
    string? After,
    string? Ip,
    string? Detail,
    DateTimeOffset Ts);
