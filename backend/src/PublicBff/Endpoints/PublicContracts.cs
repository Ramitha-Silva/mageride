namespace MageRide.PublicBff.Endpoints;

/// <summary>
/// The driver as a non-app viewer sees them (<c>public-bff.yaml#PublicDriver</c>).
/// </summary>
/// <param name="Phone">
/// AL-48's <c>tel:</c> target, in E.164. <b>The real number, not a masked one</b> — the proxy-DID
/// lease, the CPaaS bridge and the confirm-your-number step were removed in full (US-26.3), and
/// there is no code in this assembly that could produce a masked value.
/// </param>
public sealed record PublicDriverResponse(
    string Name, string? Photo, string? VehicleType, string RegNo, string? Phone);

/// <summary>One fix, and there is no type on this surface that can hold two.</summary>
public sealed record TrackedPositionResponse(double Lat, double Lng, DateTimeOffset Ts);

/// <summary>Where the parcel is going, as much of it as the aggregate records.</summary>
public sealed record DropoffResponse(string? Addr);

/// <summary>SCR-WT-004's map line.</summary>
public sealed record RouteResponse(string? Polyline);

/// <summary>
/// Who settles and how much (<c>public-bff.yaml#ProxyRideSnapshot/fare</c>).
/// </summary>
/// <remarks>
/// <b>There is no field for an instrument, and that is the fence.</b> P-09 forbids showing a proxy
/// rider the booker's payment methods; a type with nowhere to put a card, a wallet or a QR cannot
/// leak one whatever a later projection does.
/// </remarks>
public sealed record PublicFareResponse(long TotalMinor, string Currency, string PaidBy);

/// <summary><c>package_recipient</c> — SCR-WT-002 (US-20.5, P-07).</summary>
public sealed record PackageSnapshotResponse(
    string Kind,
    string Status,
    PublicDriverResponse Driver,
    TrackedPositionResponse? Position,
    string? DeliveryOtp,
    DropoffResponse? Dropoff,
    string? SenderNameMasked);

/// <summary><c>proxy_rider</c> — SCR-WT-004 (US-8.21/8.22).</summary>
public sealed record ProxyRideSnapshotResponse(
    string Kind,
    string State,
    PublicDriverResponse Driver,
    TrackedPositionResponse? Position,
    int? EtaMin,
    string? StartOtp,
    RouteResponse? Route,
    PublicFareResponse? Fare);

/// <summary>
/// <c>pickup_confirm</c> — SCR-WT-003 (P-02). Deliberately the narrowest of the three.
/// </summary>
/// <remarks>
/// No ride, no driver, no vehicle and no position: at this point in the flow there is no ride to
/// describe, and a rider being asked where they are has no business being told who is driving.
/// </remarks>
public sealed record PickupConfirmSnapshotResponse(
    string Kind,
    string BookerFirstName,
    GeoPointResponse? SuggestedPin,
    DateTimeOffset ExpiresAt,
    int TtlRemainingSec);

/// <summary><c>_shared.yaml#GeoPoint</c>.</summary>
public sealed record GeoPointResponse(double Lat, double Lng);

/// <summary>What the browser posts on Share (<c>_shared.yaml#GeoPointWithAccuracy</c>).</summary>
public sealed record GeoPointWithAccuracyBody(double? Lat, double? Lng, double? Accuracy);

/// <summary>The 200 on <c>pickup/confirm</c> and <c>pickup/decline</c>.</summary>
public sealed record PickupResolutionResponse(string State);

/// <summary>
/// The 202 on <c>sos</c>.
/// </summary>
/// <remarks>
/// <b><c>dispatchedAt</c> is nullable and <c>smsStatus</c> is beside it, for the reason C052 gave
/// on the app-side route.</b> Without the status a caller cannot tell "the alert went out" from
/// "the alert is on the admin console and nowhere else" — and on this surface the difference is
/// whether anybody has been told. The same micro-change-set, applied to the second surface that
/// raises an SOS; <c>public-bff.yaml</c> carries it.
/// </remarks>
public sealed record PublicSosResponse(Guid SosId, DateTimeOffset? DispatchedAt, string SmsStatus);

/// <summary>One frame of the live feed (<c>public-bff.yaml#TrackEvent</c>).</summary>
public sealed record TrackEventResponse(
    string Type,
    TrackedPositionResponse? Position,
    string? Status,
    DateTimeOffset At,
    string? Cursor);

/// <summary>The <c>?since</c> poll fallback's body.</summary>
public sealed record TrackEventBatchResponse(IReadOnlyList<TrackEventResponse> Events, string? Cursor);

/// <summary>SCR-WT-005 (<c>public-bff.yaml#Receipt</c>).</summary>
public sealed record ReceiptResponse(
    string Kind,
    string State,
    long? TotalMinor,
    string? Currency,
    string Proof,
    string? ProofPhotoUrl,
    PublicDriverResponse? Driver,
    DateTimeOffset CompletedAt);
