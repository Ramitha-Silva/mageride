using MageRide.Fleet.Bulk;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Operations;
using MageRide.Fleet.Vehicles;

namespace MageRide.Fleet.Endpoints;

// =================================================================================================
// C059's wire shapes. Names and casing come from backend/contracts/fleet.yaml, which is normative;
// the kernel serialises camelCase and omits nulls, so an optional member absent from the JSON is
// the contract's optional member being absent.
// =================================================================================================

/// <summary><c>POST /v1/fleets/{fleetId}/vehicles</c> (US-13.1).</summary>
public sealed record AddFleetVehicleBody(
    string? RegistrationNumber,
    string? VehicleType,
    string? Mode,
    string? ModeBBilling,
    long? DefaultMonthlyFareMinor);

/// <summary>The org's roster page. Δ C059 — <c>fleet.yaml</c> adds vehicles and never lists them.</summary>
public sealed record FleetVehiclesResponse(IReadOnlyList<FleetVehicleResponse> Items);

/// <summary>The 202 of <c>POST /v1/fleets/{fleetId}/vehicles/bulk</c> (US-13.1).</summary>
public sealed record BulkJobResponse(
    string JobId,
    int TotalRows,
    int ImportedRows,
    int FailedRows,
    string Status,
    string? ErrorReportUrl)
{
    public static BulkJobResponse From(BulkImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new BulkJobResponse(
            result.Job.Id.ToString(),
            result.Job.TotalRows,
            result.Job.SucceededRows,
            result.Job.FailedRows,
            result.Job.Status,
            result.ErrorReportUrl);
    }
}

/// <summary><c>fleet.yaml#/components/schemas/VehicleDocumentSlot</c> (AL-50, SCR-FP-004).</summary>
/// <remarks>
/// <c>thumbUrl</c> and <c>fullUrl</c> are deliberately absent. The contract offers both and this
/// service holds no signing key and no object-storage client (C125) — the same split C058 recorded
/// for the payout documents, where admin-bff mints the signed URLs US-24.8 wants. Emitting a
/// <c>file://</c> path would be a URL no browser can follow and a storage layout on the wire.
/// </remarks>
public sealed record VehicleDocumentSlotResponse(
    string? DocId,
    string Kind,
    string Status,
    bool Required,
    DateOnly? ExpiresAt,
    IReadOnlyList<ExtractedFieldResponse> Fields)
{
    public static VehicleDocumentSlotResponse From(VehicleDocumentSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        return new VehicleDocumentSlotResponse(
            slot.DocumentId?.ToString(),
            slot.Kind,
            slot.Status,
            slot.IsRequired,
            slot.ExpiresAt is { } expiry ? DateOnly.FromDateTime(expiry.UtcDateTime) : null,
            [.. slot.Fields.Select(ExtractedFieldResponse.From)]);
    }
}

/// <summary>The slot list.</summary>
public sealed record VehicleDocumentSlotsResponse(IReadOnlyList<VehicleDocumentSlotResponse> Items);

/// <summary><c>_shared.yaml#/components/schemas/ExtractedField</c>.</summary>
public sealed record ExtractedFieldResponse(
    string Key, string? Value, string Source, decimal? Confidence, string VerifyStatus)
{
    public static ExtractedFieldResponse From(VehicleDocumentField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        return new ExtractedFieldResponse(
            field.FieldKey, field.FieldValue, field.Source, field.Confidence, field.VerifyStatus);
    }
}

/// <summary><c>POST /v1/fleets/{fleetId}/assignments</c> (US-13.2).</summary>
/// <remarks>
/// <c>driverPhone</c> is <b>Δ C059</b>: US-13.2 has the operator assign "by User ID / phone" and
/// the contract's body types <c>driverId</c> alone, which an operator standing in a depot with a
/// phone number cannot supply. Exactly one of the two is required.
/// </remarks>
public sealed record AssignDriverBody(
    string? DriverId, string? DriverPhone, string? VehicleId, DateTimeOffset? From, DateTimeOffset? To);

/// <summary><c>fleet.yaml#/components/schemas/Assignment</c>.</summary>
/// <param name="ValidFrom">
/// The contract's <c>from</c>, spelled out and mapped back by <c>JsonPropertyName</c>: a positional
/// parameter called <c>From</c> would collide with the <c>From</c> factory every response shape in
/// this service carries, and renaming the factory for one record would be the more surprising of
/// the two.
/// </param>
public sealed record AssignmentResponse(
    string AssignmentId,
    string DriverId,
    string VehicleId,
    string? DriverName,
    string? DriverPhone,
    string RegistrationNumber,
    [property: System.Text.Json.Serialization.JsonPropertyName("from")] DateTimeOffset ValidFrom,
    DateTimeOffset? To,
    DateTimeOffset? RevokedAt,
    bool Active)
{
    public static AssignmentResponse From(FleetAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        return new AssignmentResponse(
            assignment.Id.ToString(),
            assignment.DriverId.ToString(),
            assignment.VehicleId.ToString(),
            assignment.DriverName,
            assignment.DriverPhone,
            assignment.RegistrationNumber,
            assignment.ValidFrom,
            assignment.ExpiresAt,
            assignment.RevokedAt,
            assignment.IsActive);
    }
}

/// <summary>The assignment list SCR-FP-005 renders. Δ C059 — the contract has no read-back.</summary>
public sealed record AssignmentsResponse(IReadOnlyList<AssignmentResponse> Items);

/// <summary><c>POST /v1/fleets/{fleetId}/trackers/bind</c> (US-13.12).</summary>
public sealed record BindTrackerBody(string? Imei, string? VehicleId, bool? AutoStartSession);

/// <summary>The 201 body <c>bindFleetTracker</c> declares.</summary>
public sealed record TrackerBindingResponse(string BindingId, string Imei, string VehicleId)
{
    public static TrackerBindingResponse From(TrackerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return new TrackerBindingResponse(
            binding.BindingId.ToString(), binding.Imei, binding.VehicleId.ToString());
    }
}

/// <summary><c>POST /v1/fleets/{fleetId}/schedules</c> (US-13.11).</summary>
public sealed record CreateScheduleBody(
    string? VehicleId, string? RouteId, DateTimeOffset? DepartAt, int? NotStartedAlarmMinutes);

/// <summary><c>fleet.yaml#/components/schemas/FleetSchedule</c>.</summary>
public sealed record FleetScheduleResponse(
    string ScheduleId,
    string VehicleId,
    string? RouteId,
    DateTimeOffset DepartAt,
    int NotStartedAlarmMinutes,
    string Status,
    DateTimeOffset? AlarmRaisedAt)
{
    public static FleetScheduleResponse From(FleetSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return new FleetScheduleResponse(
            schedule.Id.ToString(),
            schedule.VehicleId.ToString(),
            schedule.RouteId?.ToString(),
            schedule.DepartAt,
            schedule.NotStartedAlarmMinutes,
            schedule.Status,
            schedule.AlarmRaisedAt);
    }
}

/// <summary>The schedule list SCR-FP-008 renders. Δ C059 — the contract has no read-back.</summary>
public sealed record FleetSchedulesResponse(IReadOnlyList<FleetScheduleResponse> Items);

/// <summary><c>fleet.yaml#/components/schemas/FleetVehiclePosition</c> (US-13.3).</summary>
public sealed record FleetVehiclePositionResponse(
    string VehicleId,
    string? RegistrationNumber,
    double Lat,
    double Lng,
    int? Heading,
    double? SpeedMps,
    DateTimeOffset SampleTs)
{
    public static FleetVehiclePositionResponse From(FleetVehiclePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        return new FleetVehiclePositionResponse(
            position.VehicleId.ToString(),
            position.RegistrationNumber,
            position.Lat,
            position.Lng,
            position.HeadingDeg,
            position.SpeedMps,
            position.SampleTs);
    }
}

/// <summary>
/// The map payload.
/// </summary>
/// <param name="AsOf">
/// The instant the staleness horizon was measured from, so a portal can say "as of 09:41" rather
/// than implying every marker is current.
/// </param>
public sealed record FleetMapResponse(IReadOnlyList<FleetVehiclePositionResponse> Vehicles, DateTimeOffset AsOf);

/// <summary><c>fleet.yaml#/components/schemas/VehicleAnalytics</c> (US-13.4).</summary>
public sealed record VehicleAnalyticsResponse(
    string VehicleId,
    string RegistrationNumber,
    int TripCount,
    double DistanceKm,
    double ActiveHours,
    double UtilisationPct,
    long? EarningsMinor,
    string? Currency)
{
    private const string Lkr = "LKR";

    public static VehicleAnalyticsResponse From(VehicleAnalytics analytics)
    {
        ArgumentNullException.ThrowIfNull(analytics);

        return new VehicleAnalyticsResponse(
            analytics.VehicleId.ToString(),
            analytics.RegistrationNumber,
            analytics.TripCount,
            analytics.DistanceKm,
            analytics.ActiveHours,
            analytics.UtilisationPct,
            analytics.EarningsMinor,
            // A currency beside a null amount is a fact about nothing, exactly as on FleetVehicle.
            analytics.EarningsMinor is null ? null : Lkr);
    }
}

/// <summary>The analytics table.</summary>
public sealed record FleetAnalyticsResponse(IReadOnlyList<VehicleAnalyticsResponse> Items);

/// <summary>
/// <c>GET /v1/fleets/{fleetId}/alerts</c> — the Phase 3 alert page, empty in Phase 1.
/// </summary>
/// <remarks>
/// The shape is fixed now so the Fleet Portal can render its empty state without a later breaking
/// change, which is what <c>fleet.yaml</c>'s own description asks for. Nothing in this build
/// produces a route-deviation or geofence alert, so the page is empty by construction rather than
/// by filtering.
/// </remarks>
public sealed record FleetAlertsResponse(IReadOnlyList<object> Items, string? Cursor, bool HasMore);

/// <summary><c>PUT /v1/fleets/{fleetId}/geofences</c> (US-13.5).</summary>
public sealed record SetGeofencesBody(IReadOnlyList<GeofenceBody>? Geofences);

/// <summary>One polygon on the wire.</summary>
public sealed record GeofenceBody(string? Name, IReadOnlyList<GeoPointBody>? Polygon);

/// <summary>A vertex on the wire — <c>{"lat":6.9271,"lng":79.8612}</c>, D6' §2.2's shape.</summary>
public sealed record GeoPointBody(double? Lat, double? Lng);

/// <summary>What <c>setFleetGeofences</c> answers.</summary>
public sealed record GeofenceCountResponse(int Count);

/// <summary>The org's stored geofences. Δ C059 — the contract replaces them and never reads them back.</summary>
public sealed record GeofencesResponse(IReadOnlyList<GeofenceResponse> Items);

/// <summary>One stored polygon.</summary>
public sealed record GeofenceResponse(string GeofenceId, string? Name, IReadOnlyList<GeoPointBody> Polygon)
{
    public static GeofenceResponse From(FleetGeofence geofence)
    {
        ArgumentNullException.ThrowIfNull(geofence);

        return new GeofenceResponse(
            geofence.Id.ToString(),
            geofence.Name,
            [.. geofence.Polygon.Select(point => new GeoPointBody(point.Latitude, point.Longitude))]);
    }
}

// -------------------------------------------------------------------------------------------------
// Internal plane — the Verification Officer's per-vehicle decision (Δ C059)
// -------------------------------------------------------------------------------------------------

/// <summary>What an officer's decision on one fleet vehicle produced.</summary>
public sealed record VehicleDecisionResponse(
    FleetVehicleResponse Vehicle,
    string DocsStatus,
    IReadOnlyList<VehicleDocumentSlotResponse> Documents)
{
    public static VehicleDecisionResponse From(VehicleDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new VehicleDecisionResponse(
            FleetVehicleResponse.From(decision.Vehicle, decision.DocsStatus),
            decision.DocsStatus,
            [.. decision.Slots.Select(VehicleDocumentSlotResponse.From)]);
    }
}

/// <summary>The officer's reason, forwarded by admin-bff from their own bearer.</summary>
public sealed record VehicleDecisionBody(string? OfficerId, string? Reason);
