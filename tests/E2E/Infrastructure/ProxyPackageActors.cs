namespace MageRide.E2E.Infrastructure;

/// <summary>
/// The 202 of <c>POST /v1/location-requests</c> (D3' <c>LocationRequest</c>).
/// </summary>
/// <param name="State">
/// <c>Pending</c> when iam-svc found the rider and <c>RiderNotRegistered</c> when it did not — the
/// one field that decides between the FCM round-trip and AL-45's SMS, and the reason iam-svc is in
/// this fleet at all. Neither is a failure: both answer 202, because <c>RiderNotRegistered</c> is a
/// live request the rider can still answer from a browser.
/// </param>
internal sealed record LocationRequest(Guid RequestId, string State, DateTimeOffset ExpiresAt, int Ttl);

/// <summary>
/// A <c>rides.location_requests</c> row, as a scenario reads it back.
/// </summary>
/// <param name="ResolvedLat">
/// <see langword="null"/> on everything but a confirmation. C122's definition of done is that a
/// decline stores no coordinates <em>anywhere</em>, and this pair plus
/// <paramref name="ResolvedAccuracyM"/> are the only columns on the platform that could hold one.
/// </param>
internal sealed record LocationRequestSnapshot(
    Guid Id,
    Guid RequestId,
    Guid BookerId,
    Guid? RiderId,
    string State,
    DateTimeOffset IssuedAt,
    int TtlSeconds,
    DateTimeOffset? ResolvedAt,
    double? ResolvedLat,
    double? ResolvedLng,
    decimal? ResolvedAccuracyM);

/// <summary>A <c>fares.ride_payments</c> attempt (D-10, P-04, P-08).</summary>
/// <param name="PayerRole">
/// <c>rider</c> or <c>booker</c> — P-04's routing, and the only column that records which side of a
/// proxy booking the money came from.
/// </param>
internal sealed record PaymentSnapshot(
    Guid Id, string State, string Method, long AmountMinor, string PayerRole, Guid? PayerUserId);

/// <summary>
/// A <c>safety.trip_share_tokens</c> row — the credential for a no-login page (AL-44).
/// </summary>
/// <remarks>
/// Read by scenarios to assert on the token's <em>lifecycle</em> — that a <c>pickup_confirm</c> is
/// burned on use (BR-29.1), that the metering counted a visit, that the window is AL-45's 300 s.
/// The value itself is never taken from here to open a page: that would be reaching past the SMS,
/// which is the token's only delivery path.
/// </remarks>
internal sealed record ShareTokenSnapshot(
    string Token,
    string Scope,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    int AccessCount,
    DateTimeOffset? LastAccessAt);

/// <summary>
/// A <c>safety.sos_events</c> row raised from a browser (US-25.5, AL-44).
/// </summary>
/// <param name="UserId">
/// <see langword="null"/>, always, on this path. A web SOS carries no app identity — the token is
/// who the caller is, which is what <c>ck_sos_events_actor</c> allows and what the <c>source</c>
/// column records.
/// </param>
internal sealed record WebSosSnapshot(
    Guid Id, Guid? UserId, string Source, double Lat, double Lng, string? ShareToken, string? SmsStatus);
