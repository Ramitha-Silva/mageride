using System.Text.Json.Serialization;
using MageRide.Shared.Http;

namespace MageRide.Safety.Endpoints;

// =============================================================================================
// The wire shapes of backend/contracts/safety.yaml. The contract wins over this file: it is what
// C012/C013 generate the KMP client from and what C118 asserts the running service against.
// =============================================================================================

/// <summary>`POST /v1/sos`.</summary>
public sealed record RaiseSosBody(string? RideId, double? Lat, double? Lng, string? Role);

/// <summary>The 200 of `POST /v1/sos`.</summary>
/// <param name="DispatchedAt">
/// When a gateway took the alert — <see langword="null"/> when none did. D3' prints the field
/// unconditionally; answering with an instant that never happened would tell somebody in trouble
/// that help was on the way.
/// </param>
/// <param name="SmsStatus">
/// <c>Dispatched</c> | <c>Failed</c> | <c>NoContact</c>. **Δ C052** — without it a client cannot
/// tell "the alert went out" from "the alert is on the admin console and nowhere else", which is
/// the difference the SOS screen has to draw.
/// </param>
public sealed record RaiseSosResponse(Guid SosId, DateTimeOffset? DispatchedAt, string SmsStatus);

/// <summary>One row of `GET /v1/sos/{userId}/history`.</summary>
public sealed record SosEventResponse(
    Guid SosId,
    Guid? RideId,
    string Role,
    double Lat,
    double Lng,
    string Source,
    string? SmsStatus,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset RaisedAt);

/// <summary>A cursor page, as `_shared.yaml#/components/schemas/CursorPage` shapes one.</summary>
public sealed record CursorPageResponse<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>The 201 of `POST /v1/trip-share/{tripId}`.</summary>
/// <remarks>
/// <b>This is the one route on the platform that returns a share token</b>, and it is D-34 rather
/// than AL-44: the passenger is sharing their own trip and has to be given something to send. The
/// three AL-44 scopes are the opposite case — their recipients are people the platform is reaching
/// *out* to, so notification-svc mints those and puts them straight into an SMS.
/// </remarks>
public sealed record TripShareResponse(string Token, string Url, DateTimeOffset ExpiresAt);

/// <summary>`GET /v1/trip-share/public/{token}` — live only, no track (D-34).</summary>
public sealed record SharedTripResponse(
    string State,
    GeoPointResponse? Position,
    int? Heading,
    VehicleResponse? Vehicle,
    string? DriverName,
    DateTimeOffset AsOf,
    DateTimeOffset ExpiresAt);

/// <summary>The shared `GeoPoint` shape — <c>{"lat":…,"lng":…}</c> (D6' §2.2).</summary>
public sealed record GeoPointResponse(double Lat, double Lng);

/// <summary>What a shared link says about the car at the kerb, and no more.</summary>
public sealed record VehicleResponse(string? Type, string? RegistrationNumber);

/// <summary>`POST /v1/reports/vehicle`.</summary>
public sealed record ReportVehicleBody(string? VehicleId, string? Reason, string? TripId);

/// <summary>D3' <c>VehicleReport</c>.</summary>
public sealed record VehicleReportResponse(
    Guid ReportId, Guid VehicleId, string? Reason, Guid? TripId, string Status, DateTimeOffset CreatedAt);

/// <summary>`POST /v1/drivers/{driverId}/block`.</summary>
public sealed record BlockDriverBody(string? Reason);

/// <summary>`POST /v1/internal/safety/reports/{reportId}/resolve` (Δ C052).</summary>
public sealed record ResolveReportBody(string? Decision, string? Note, string? ResolvedBy);

/// <summary>What a moderation decision produced.</summary>
public sealed record ResolveReportResponse(
    Guid ReportId, string Status, int ConfirmedTotal, bool Delisted);

/// <summary>`POST /v1/internal/safety/trips/{tripId}/close` (Δ C052).</summary>
public sealed record CloseTripSharesResponse(Guid TripId, int Revoked);

/// <summary>
/// `POST /v1/internal/safety/sos/web` (Δ C066) — AL-44/US-25.5's alert from an SCR-WT page.
/// </summary>
/// <remarks>
/// <b>No ride id, no role and no recipient.</b> All three are facts about the token, and accepting
/// any of them from the caller would let public-bff raise an alert against a ride whose link it does
/// not hold, or aim one at a number it chose. <c>accuracy</c> is likewise absent:
/// <c>safety.sos_events</c> has no column for it and D-33's alert is a place, not a measurement.
/// </remarks>
public sealed record WebSosBody(string? ShareToken, double? Lat, double? Lng);

/// <summary>One outcome of a proxy location request (P-12).</summary>
/// <remarks>
/// The subject is a digest, never a number: 0904 stores <c>rider_phone_hash</c> because the rider
/// is frequently somebody with no account, and an investigation asks "how often did this booker get
/// declined", not "who did they ping".
/// </remarks>
public sealed record LocationRequestAuditResponse(
    Guid RequestId, string Decision, DateTimeOffset At, string RiderPhoneFingerprint);

/// <summary>The P-12 forensic answer: the outcomes, and how they add up.</summary>
/// <remarks>
/// The converter on <c>Totals</c> is not decoration. Its keys are the values
/// <c>safety.location_request_audit.decision</c> actually holds — <c>Confirmed</c>, <c>Declined</c>,
/// <c>Expired</c>, <c>NotRegistered</c> — and the kernel's camelCase dictionary-key policy would
/// answer <c>declined</c>, so a screen filtering on the stored value finds nothing.
/// </remarks>
public sealed record LocationRequestAuditPage(
    Guid BookerId,
    [property: JsonConverter(typeof(LiteralKeyDictionaryConverter<int>))]
    IReadOnlyDictionary<string, int> Totals,
    IReadOnlyList<LocationRequestAuditResponse> Items);
