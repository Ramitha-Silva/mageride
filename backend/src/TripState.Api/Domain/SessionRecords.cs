using MageRide.Shared.Primitives;

namespace MageRide.TripState.Domain;

/// <summary>
/// <c>trips.sessions.state</c> — the stored lifecycle (0501's <c>ck_sessions_state</c>).
/// </summary>
/// <remarks>
/// Two values, not the contract's three. <c>trip-state.yaml</c>'s <c>SessionState</c> is
/// <c>ACTIVE | ENDED | AUTO_ENDED</c> while the DDL both specs print is
/// <c>ACTIVE | COMPLETED</c>, and the difference is not a disagreement about the lifecycle — it is
/// the same lifecycle with the *reason* folded into the state. A stored third value would
/// duplicate <see cref="EndReasons"/>, and the two could then contradict each other. So the wire
/// value is derived: see <see cref="SessionViews"/>.
/// </remarks>
public static class SessionStates
{
    /// <summary>Live. Covered by <c>ux_sessions_active_driver</c>, which is the D-03 mutex.</summary>
    public const string Active = "ACTIVE";

    /// <summary>Closed, however it closed. <c>end_reason</c> says which.</summary>
    public const string Completed = "COMPLETED";
}

/// <summary>
/// <c>trips.sessions.end_reason</c> (migration 0504), which is also the contract's <c>endReason</c>.
/// </summary>
public static class EndReasons
{
    /// <summary>The dashboard End Journey button (US-5.2). The only reason that is not auto.</summary>
    public const string DriverEnded = "driver_ended";

    /// <summary>Thirty minutes without movement (US-5.3).</summary>
    public const string IdleTimeout = "idle_timeout";

    /// <summary>Arrived inside the 100 m destination fence (US-5.4).</summary>
    public const string DestinationGeofence = "destination_geofence";

    /// <summary>The broker's last will said the vehicle went away (R-15, T-04).</summary>
    public const string MqttOffline = "mqtt_offline";

    /// <summary>ACC off on a tracker-equipped vehicle (US-3.22/3.23, AL-32).</summary>
    public const string IgnitionOff = "ignition_off";

    /// <summary>A support force-end.</summary>
    public const string Admin = "admin";

    /// <summary>
    /// Whether this reason opens the 5-minute restart grace (US-5.10).
    /// </summary>
    /// <remarks>
    /// Everything except the driver's own End Journey. A driver who pressed the button meant it,
    /// and offering to undo it would make the button ambiguous; every other reason is the platform
    /// deciding on their behalf, which is exactly what a grace window exists to let them correct.
    /// </remarks>
    public static bool IsAutomatic(string? reason) =>
        reason is not null && reason != DriverEnded;

    /// <summary>The reasons <c>POST /v1/internal/sessions/{id}/auto-end</c> accepts.</summary>
    public static bool IsTimerReason(string? reason) =>
        reason is IdleTimeout or DestinationGeofence or MqttOffline;
}

/// <summary>Who caused a transition — <c>started_by</c> / <c>ended_by</c> (0504, AL-32).</summary>
public static class SessionActors
{
    /// <summary>The Mode A/B dashboard. Overrides the device, always (AL-32, US-5.12).</summary>
    public const string Driver = "driver";

    /// <summary>A paired tracker's ignition (US-3.22/3.23).</summary>
    public const string Device = "device";

    /// <summary>A timer, a sweep or a grace restart.</summary>
    public const string System = "system";
}

/// <summary>The two modes this service owns. <c>C</c> is ride-svc's and is refused (R-01).</summary>
public static class OperatingModes
{
    /// <summary>Public passenger transport — a bus or a train.</summary>
    public const string A = "A";

    /// <summary>Private or shared transport — a school bus, a book-hire, a family vehicle.</summary>
    public const string B = "B";

    /// <summary>Mode C. Named so the rejection can be specific rather than "unknown mode".</summary>
    public const string C = "C";

    public static bool IsTracked(string? mode) => mode is A or B;
}

/// <summary>Kinds written to <c>trips.events</c> (0502) — the domain log, not the outbox.</summary>
public static class TripEventKinds
{
    public const string SessionStarted = "session.started";
    public const string SessionEnded = "session.ended";
    public const string SessionRestarted = "session.restarted";

    /// <summary>ACC on/off from a paired tracker, whether or not it changed anything.</summary>
    public const string Ignition = "ignition";

    /// <summary>The broker's last will, before the offline grace has decided anything.</summary>
    public const string VehicleOffline = "vehicle.offline";

    /// <summary>A dashboard action that contradicted the device's state (AL-32).</summary>
    public const string DeviceOverridden = "device.overridden";
}

/// <summary>A row of <c>trips.sessions</c>.</summary>
public sealed record Session(
    Guid Id,
    Guid VehicleId,
    Guid DriverId,
    string Mode,
    string State,
    Guid? RouteId,
    bool AutoEndAtDestination,
    string? EndReason,
    string StartedBy,
    string? EndedBy,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset? LastMovementAt)
{
    public bool IsActive => State == SessionStates.Active;

    /// <summary>Whether this session ended by something other than the driver's own button.</summary>
    public bool WasAutoEnded => !IsActive && EndReasons.IsAutomatic(EndReason);

    /// <summary>
    /// End of the 5-minute restart grace, or <see langword="null"/> when there is none (US-5.10).
    /// </summary>
    public DateTimeOffset? RestartableUntil(TimeSpan grace) =>
        WasAutoEnded && EndedAt is { } endedAt ? endedAt + grace : null;
}

/// <summary>How a session is spelled on the wire (<c>trip-state.yaml</c>'s <c>SessionState</c>).</summary>
public static class SessionViews
{
    public const string Active = "ACTIVE";
    public const string Ended = "ENDED";
    public const string AutoEnded = "AUTO_ENDED";

    /// <summary>
    /// The stored state plus the reason, as the contract's three-valued enum.
    /// </summary>
    /// <remarks>
    /// This is the whole of the reconciliation described on <see cref="SessionStates"/>: one
    /// stored fact, two vocabularies, and the mapping in one place rather than at every response.
    /// </remarks>
    public static string From(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.IsActive ? Active : session.WasAutoEnded ? AutoEnded : Ended;
    }
}

/// <summary>The vehicle facts a session start needs, from registry-svc's projection.</summary>
/// <param name="Source"><c>owned</c> or <c>assigned</c> (US-13.9).</param>
/// <param name="IsGoLiveEligible">APPROVED and not E-03 suspended, as registry computes it.</param>
public sealed record EligibleVehicle(
    Guid VehicleId,
    Guid DriverId,
    string Source,
    Guid? FleetId,
    Guid OwnerId,
    string Mode,
    string Status,
    string DispatchState,
    bool IsGoLiveEligible);

/// <summary>A row of <c>trips.ratings</c> (0502).</summary>
public sealed record SessionRating(
    Guid Id, Guid SubjectId, Guid RaterId, Guid RateeId, short Stars, string? Comment, string Direction, DateTimeOffset CreatedAt);

/// <summary>Rating directions — <c>ck_ratings_direction</c>.</summary>
public static class RatingDirections
{
    public const string PassengerToDriver = "passenger_to_driver";
    public const string DriverToPassenger = "driver_to_passenger";
}

/// <summary>Where a vehicle was, and when — what the geofence and idle rules are evaluated on.</summary>
public sealed record VehicleFix(Guid VehicleId, GeoPoint Point, DateTimeOffset SampleTs, double? SpeedMps);
