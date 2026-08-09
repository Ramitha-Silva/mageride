namespace MageRide.E2E.Infrastructure;

/// <summary>A fleet operator's organisation, and bearers for the three sub-roles that act on it.</summary>
/// <param name="OwnerBearer">
/// The Owner's token, carrying the <c>fleet_id</c>/<c>fleet_role</c> pair iam-svc mints (AL-03).
/// The claim is not the authority — <c>FleetAccessFilter</c> re-reads <c>iam.fleet_members</c> on
/// every request — but it is what gets past deny-by-default authorization.
/// </param>
internal sealed record FleetOrg(Guid FleetId, Guid OwnerId, string OwnerBearer, string BusinessReg);

/// <summary>A driver account, and the bearer their app would hold.</summary>
internal sealed record ModeAbDriver(Guid DriverId, string Phone, string Bearer);

/// <summary>A passenger account, as iam-svc would have left it.</summary>
internal sealed record ModeAbPassenger(Guid Id, string Phone, string Bearer);

/// <summary>
/// A Mode A or Mode B vehicle on an organisation's roster, and the driver it is assigned to.
/// </summary>
/// <param name="Imei">
/// The tracker bound to it, or <see langword="null"/> when the vehicle has no hardware. A tracker
/// is what makes ignition auto-start reachable (AL-32) and what puts the vehicle on the map at all
/// in a fleet with no driver handsets in it.
/// </param>
internal sealed record FleetVehicle(
    Guid VehicleId,
    Guid FleetId,
    string Mode,
    string VehicleType,
    string Plate,
    ModeAbDriver Driver,
    string? Imei = null);

/// <summary>The slice of <c>trips.sessions</c> a Mode A/B scenario asserts against.</summary>
/// <param name="State">
/// <c>ACTIVE</c> or <c>COMPLETED</c> — the two the column admits. The contract's third value,
/// <c>AUTO_ENDED</c>, is derived from <paramref name="EndReason"/> and never stored, so a scenario
/// asking "was this auto-ended" asks about the reason.
/// </param>
/// <param name="DestinationArmed">
/// Whether US-5.4's fence has a centre. <paramref name="AutoEndAtDestination"/> is what the driver
/// asked for; this is whether there was a previous journey to arm it from. A vehicle's first
/// journey has nowhere to arrive at, so the two differ exactly once per vehicle.
/// </param>
internal sealed record SessionRow(
    Guid Id,
    Guid VehicleId,
    Guid DriverId,
    string Mode,
    string State,
    string? EndReason,
    string StartedBy,
    string? EndedBy,
    bool AutoEndAtDestination,
    bool DestinationArmed,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset? LastMovementAt,
    DateTimeOffset? LastPositionAt,
    DateTimeOffset? OfflineSince)
{
    /// <summary>Whether the platform ended it rather than the driver (US-5.10's restart condition).</summary>
    public bool WasAutoEnded =>
        EndReason is not null and not "driver_ended" and not "admin";
}

/// <summary>What <c>POST /v1/sessions/start</c> answered — the session the driver's app now holds.</summary>
internal sealed record StartedSession(Guid SessionId, Guid VehicleId, string Mode, string State);

/// <summary>A Mode B access request, its grant and the subscription an accept started (Epic 23).</summary>
internal sealed record ModeBGrant(Guid RequestId, Guid GrantId, Guid SubscriptionId);

/// <summary>One row of <c>telemetry.positions</c> — the far end of the whole tracker plane (T-06).</summary>
/// <param name="Source">
/// D6' §4.1's family code, stamped by the adapter that decoded the frame: 1 GT06, 2 JT/T 808,
/// 3 H02, 4 NMEA. Generic UDP-NMEA shares 4 with NMEA-over-MQTT — <c>ck_positions_source</c> admits
/// 0…4 and coining a fifth would need a migration for a distinction no consumer reads (C043).
/// </param>
internal sealed record TelemetryRow(
    double Lat, double Lng, double? SpeedMps, short Source, Guid? FleetId, DateTimeOffset SampleTs);
