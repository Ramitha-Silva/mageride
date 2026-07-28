using MageRide.Shared.Primitives;

namespace MageRide.Iam.Domain;

/// <summary>
/// <c>iam.users</c> as the <em>profile</em> half of iam-svc reads it — every column, including
/// the four the auth half deliberately leaves alone.
/// </summary>
/// <remarks>
/// A second record over the same table rather than a wider <see cref="IamUser"/>. The two halves
/// ask different questions: a sign-in wants the identity and the flags that gate it, and a
/// profile read wants what the user sees. Widening <see cref="IamUser"/> would have every OTP
/// verify drag a JSONB column and two emergency-contact strings through the token path for no
/// reader, and would put the SOS fast-path columns one careless <c>SELECT *</c> away from a
/// sign-in response.
/// </remarks>
public sealed record UserProfile(
    Guid Id,
    string? Phone,
    string? Email,
    string Role,
    string? FirstName,
    string? PhotoUrl,
    string Language,
    string? OperatingCityCode,
    string DefaultPaymentMethod,
    IReadOnlyDictionary<string, bool> NotifPrefs,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    bool IsBlocked,
    DateTimeOffset CreatedAt);

/// <summary>
/// A row of <c>iam.saved_addresses</c> — Home, Work and the free-form places (AL-14, AL-26).
/// </summary>
/// <remarks>
/// <para>
/// <b>The two shapes C003 landed as a union are settled here, and the answer is the union.</b>
/// C003 note (c) records that <c>server_db_schema.md</c> §1 / D4' §2 address Home and Work
/// through <see cref="Label"/> while D4' Δ 2026-06-21 (AL-26) models them as
/// <see cref="IsHome"/> / <see cref="IsWork"/> booleans with partial unique indexes, and asks
/// C027 to collapse them to one representation. They cannot be collapsed, because
/// <c>iam.yaml</c>'s <c>SavedAddressInput</c> — which is the contract, and wins — carries
/// <c>label</c> as <b>required</b> alongside <c>isHome</c> and <c>isWork</c>. They are also not
/// redundant: only the booleans can express "at most one Home" as an index, and only the label
/// gives the ModalBottomSheet's "Save Address As" somewhere to go (D2 SCR-PA-026). Neither
/// column is dropped; the invariant that they agree is enforced in
/// <c>SavedAddressService</c> and by the two partial unique indexes.
/// </para>
/// </remarks>
public sealed record SavedAddress(
    Guid Id,
    Guid UserId,
    string Label,
    string Line1,
    string? Line2,
    string? Line3,
    GeoPoint Geo,
    bool IsHome,
    bool IsWork,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A row of <c>iam.emergency_contacts</c> — the driver SOS fan-out list (AL-13).</summary>
/// <param name="IsPrimary">
/// Whether this contact is the one denormalised onto <c>iam.users.emergency_contact_name</c> /
/// <c>.emergency_contact_phone</c>. D-33 budgets five seconds for the whole SOS fan-out, so
/// safety-svc reads the flat columns and never joins; this list is the editable truth behind
/// them.
/// </param>
public sealed record EmergencyContact(
    Guid Id,
    Guid UserId,
    string Name,
    string Phone,
    bool IsPrimary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A row of <c>pdpa.requests</c> — an export or erasure the data subject asked for (E-06).</summary>
public sealed record PdpaRequest(
    Guid Id,
    Guid UserId,
    string Kind,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset DueBy);

/// <summary>
/// The non-terminal journey the caller is part of, on whichever plane owns it (US-1.14).
/// </summary>
/// <param name="Kind"><c>ride</c> for a Mode C <c>rides.rides</c> row, <c>session</c> for a Mode A/B
/// <c>trips.sessions</c> row. R-01 keeps them apart and so does this.</param>
/// <param name="Role">Which end of it the caller is on.</param>
public sealed record ActiveTrip(
    Guid TripId,
    string Kind,
    string Role,
    string State,
    string? Mode,
    Guid? VehicleId,
    Guid? CounterpartyId,
    GeoPoint? Pickup,
    GeoPoint? Dropoff,
    DateTimeOffset StartedAt);

/// <summary>
/// A driver's shift and today's earnings — US-1.15 item 5.
/// </summary>
/// <param name="BusinessDate">
/// The <c>Asia/Colombo</c> day <c>fares.driver_earnings</c> is keyed by (D-38). Not "today" in
/// UTC: a trip finished at 02:00 Colombo belongs to the day that started the evening before in
/// UTC terms, and reading the wrong key shows a driver an empty earnings card.
/// </param>
public sealed record DriverShift(
    bool IsOnline,
    Guid? ActiveSessionId,
    Guid? ActiveVehicleId,
    DateOnly BusinessDate,
    int TodayTrips,
    Money TodayGross,
    Money TodayDailyFee);

/// <summary>A row of <c>config.operating_cities</c> (AL-27), as the first-run screen needs it.</summary>
public sealed record OperatingCity(
    string Code,
    string NameEn,
    string NameSi,
    string NameTa,
    double CentroidLat,
    double CentroidLng,
    int SortOrder);
