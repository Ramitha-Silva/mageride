namespace MageRide.AdminBff.Domain;

/// <summary>
/// The three directories' search criteria, as one query object per subject (AL-40/41/42).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every criterion is optional and they combine with AND.</b> BR-28.8 lists them as a set an
/// operator picks from, and SCR-AP-010/012/014 render them as a row of filters rather than as a
/// mode switch — so "name = nimal AND level = 1" has to be one query rather than a choice between
/// two. Each is a plain conjunct for that reason.
/// </para>
/// <para>
/// <b>The cursor is the ordering key and never a criterion.</b> The position is
/// (created_at, id) of the last row returned, which is what migration 0109/0317's indexes are
/// shaped for; a filter that changed between pages would re-anchor the scan rather than corrupt it.
/// </para>
/// </remarks>
/// <param name="Search">
/// A pre-escaped <c>ILIKE</c> pattern, or null. Built once in the repository so the two
/// <c>LIKE</c> wildcards cannot be smuggled in through a search box.
/// </param>
public sealed record PassengerSearchQuery(
    Guid? Id,
    string? Name,
    string? Mobile,
    string? Email,
    DateTimeOffset? CursorAt,
    Guid? CursorId,
    int Limit);

/// <param name="Status">
/// One of <see cref="DriverDirectoryStatuses"/>. D3' defaults it to <c>verified</c>, which is
/// US-24.10's "search **verified** drivers" — the directory is for the people currently driving.
/// </param>
public sealed record DriverSearchQuery(
    Guid? Id,
    string? Name,
    string? Mobile,
    string? Nic,
    string? RegNo,
    int? Level,
    string Status,
    DateTimeOffset? CursorAt,
    Guid? CursorId,
    int Limit);

public sealed record VehicleSearchQuery(
    Guid? Id,
    string? RegNo,
    string? Type,
    string? Mode,
    string? OwnerMobile,
    string? FleetOrg,
    string? Status,
    DateTimeOffset? CursorAt,
    Guid? CursorId,
    int Limit);

/// <summary><c>GET /v1/admin/drivers?status=</c> — admin-bff.yaml's enum.</summary>
/// <remarks>
/// <b>Derived, not stored.</b> There is no driver status column: a driver is verified when
/// <c>registry.driver_profiles.verified_at</c> is set (C063 writes it), suspended when
/// <c>iam.users.is_blocked</c> is set (US-14.3's moderation write, C062), and pending otherwise.
/// Suspension wins over verification because it is the later fact and the one an operator is
/// looking at the row to find out.
/// </remarks>
public static class DriverDirectoryStatuses
{
    public const string Verified = "verified";
    public const string Pending = "pending";
    public const string Suspended = "suspended";

    /// <summary>No status filter at all — every driver account, whatever its verdict.</summary>
    public const string All = "all";

    public static bool IsKnown(string? status) =>
        status is Verified or Pending or Suspended or All;
}

/// <summary><c>PassengerRow.status</c> — admin-bff.yaml's enum.</summary>
/// <remarks>
/// <c>deleted</c> is on the contract and is never answered today: a PDPA erasure is C065's, and
/// until it lands there is no column that records one. Answering <c>active</c> for an account
/// nobody erased is the honest value; inventing a third state from <c>is_blocked</c> would make a
/// suspended passenger look erased.
/// </remarks>
public static class PassengerDirectoryStatuses
{
    public const string Active = "active";
    public const string Blocked = "blocked";
    public const string Deleted = "deleted";
}

/// <summary>
/// The ride states that count as a trip somebody took (R-05).
/// </summary>
/// <remarks>
/// <b>Not <c>Completed</c> alone.</b> A ride never rests in <c>Completed</c> — it moves on to
/// <c>Paid</c>, <c>CashSettled</c> or <c>CashOnDeliveryCollected</c> as soon as the fare settles,
/// which is why C061's rollup counts <c>rides.transitions</c> instead. A directory row wants the
/// count rather than the moment, so the whole terminal-successful set is named here and the count
/// is one index scan of <c>rides.rides</c> rather than a join to the transition log.
/// </remarks>
public static class CompletedRideStates
{
    public static readonly string[] All =
    [
        "Completed",
        "Paid",
        "CashSettled",
        "CashOnDeliveryCollected",
    ];
}

// -------------------------------------------------------------------------------------------------
// Passenger directory (AL-40)
// -------------------------------------------------------------------------------------------------

/// <summary>One row of SCR-AP-010, before masking.</summary>
/// <param name="Mobile">
/// The clear MSISDN as stored. <b>Masked before it reaches the wire, for every role</b> — the list
/// field is <c>mobileMasked</c> and the clear number is only ever handed out by the audited detail
/// read. See <c>PiiView</c>.
/// </param>
public sealed record PassengerRow(
    Guid PassengerId,
    string Name,
    string? Mobile,
    DateTimeOffset JoinedAt,
    int Trips,
    string Status);

/// <summary>SCR-AP-011's profile block, before masking.</summary>
public sealed record PassengerProfileRow(
    Guid PassengerId,
    string Name,
    string? Mobile,
    string? Email,
    DateTimeOffset JoinedAt,
    double? Rating,
    string DefaultPay,
    string Status);

/// <summary>One emergency contact (<c>iam.emergency_contacts</c>, AL-13).</summary>
public sealed record SosContactRow(string Name, string Phone);

/// <summary>
/// One row of a Trips tab, whichever surface produced the journey.
/// </summary>
/// <param name="Kind">
/// <c>ride</c> for a Mode C booking (<c>rides.rides</c>, R-01) or <c>session</c> for a Mode A/B
/// journey (<c>trips.sessions</c>, D-03). <b>Both, because a directory is not mode-aware.</b> A
/// vehicle detail that showed only <c>rides.rides</c> would render an empty Trips tab for every bus
/// on the platform, and the ride-svc / trip-state-svc boundary is about who *writes* the row, not
/// about what an operator is allowed to read back.
/// </param>
public sealed record TripRow(
    Guid TripId,
    string Kind,
    string State,
    string? VehicleType,
    Guid? VehicleId,
    string? RegNo,
    Guid? CounterpartyId,
    string? CounterpartyName,
    long? FareMinor,
    string? Currency,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

/// <summary>One row of SCR-AP-011's Payments tab (<c>fares.ride_payments</c>, D-10).</summary>
public sealed record PaymentRow(
    Guid PaymentId,
    Guid RideId,
    string Method,
    string State,
    long AmountMinor,
    long SurchargeMinor,
    long TipMinor,
    string Currency,
    short AttemptNo,
    DateTimeOffset CreatedAt);

/// <summary>One row of SCR-AP-011's Packages tab — a <c>kind = 2</c> ride (P-06).</summary>
/// <param name="RecipientPhone">
/// Stored in the clear because AL-21 SMSes it (0609's own comment), and masked here by the same
/// rule as the passenger's own number: it is a third party's MSISDN and the operator reading a
/// delivery has no more claim on it.
/// </param>
public sealed record PackageRow(
    Guid RideId,
    string State,
    string? PackageSize,
    string? Description,
    string? RecipientName,
    string? RecipientPhone,
    long? FareMinor,
    string? Currency,
    DateTimeOffset CreatedAt,
    DateTimeOffset? TerminalAt);

/// <summary>One row of SCR-AP-011's Disputes tab (<c>support.tickets</c>, US-16.2).</summary>
/// <remarks>
/// The whole ticket list rather than a "disputes" subset: <c>support.tickets.category</c> is free
/// text (1303 puts no CHECK on it and US-9.23's daily-fee refund request rides in the same column),
/// so a server-side filter on a category vocabulary nobody has fixed would silently hide tickets.
/// The category is on every row and SCR-AP-011 can group by it.
/// </remarks>
public sealed record DisputeRow(
    Guid TicketId,
    string Category,
    string Status,
    string? Description,
    string? Response,
    Guid? RideId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// -------------------------------------------------------------------------------------------------
// Driver directory (AL-41)
// -------------------------------------------------------------------------------------------------

/// <summary>One row of SCR-AP-012, before masking.</summary>
/// <param name="Level">
/// <c>dispatch.driver_levels.level</c>, defaulted to <b>3</b> for a driver with no row — which is
/// the column's own default (0705), so a driver who has never been scored reads the same here as
/// they do to dispatch-svc. <c>?level=1</c> is how ADD Appendix C's Level-1 list is obtained.
/// </param>
public sealed class DriverRow
{
    public Guid DriverId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Mobile { get; set; }

    /// <summary>Registration numbers of the vehicles this driver owns. Aggregated per page row.</summary>
    public string[]? Vehicles { get; set; }

    public int Level { get; set; }

    public int Trips { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTimeOffset JoinedAt { get; set; }
}

/// <summary>SCR-AP-013's profile block, before masking.</summary>
public sealed record DriverProfileRow(
    Guid DriverId,
    string Name,
    string? Mobile,
    string? Nic,
    DateTimeOffset JoinedAt,
    double? Rating,
    long WalletMinor,
    string Currency,
    int Level,
    int Points,
    string Status,
    DateTimeOffset? VerifiedAt);

/// <summary>
/// A vehicle chip on SCR-AP-013 — the jump-off to the vehicle detail.
/// </summary>
/// <remarks>
/// <b>Owned <em>or</em> assigned.</b> A Mode C driver owns their vehicle
/// (<c>registry.vehicles.owner_id</c>); a fleet's driver owns nothing and drives what
/// <c>registry.fleet_assignments</c> gives them (AL-03). "Linked vehicles" (US-24.10) is both, and
/// a directory that showed only ownership would render an empty chip row for every driver a fleet
/// employs.
/// </remarks>
public sealed record LinkedVehicleRow(
    Guid VehicleId,
    string RegNo,
    string Type,
    string Mode,
    string Status,
    string DispatchState,
    bool Owned);

/// <summary>One row of the Wallet-ledger tab (<c>billing.wallet_transactions</c>, D-09 §10).</summary>
public sealed record WalletLedgerRow(
    long EntryNo,
    string Kind,
    long AmountMinor,
    long BalanceAfterMinor,
    string? Description,
    DateTimeOffset Ts);

/// <summary>One row of the Daily-fee tab (<c>billing.daily_fee_charges</c>, D-13).</summary>
public sealed record DailyFeeRow(
    DateOnly FeeDate,
    Guid DriverId,
    Guid VehicleId,
    string? RegNo,
    long AmountMinor,
    string Currency,
    int TripsThatDay,
    string Status,
    DateTimeOffset ChargedAt);

/// <summary>
/// One row of the Credit-transfers tab (<c>billing.credit_transfers</c>, US-9.13/9.21).
/// </summary>
/// <param name="Direction">
/// <c>out</c> when this driver is the sender, <c>in</c> when they are the recipient. The stored
/// <c>direction</c> column answers a different question (REQUESTED vs DIRECT — who started it), and
/// a tab that showed that instead would leave an operator unable to tell money leaving from money
/// arriving.
/// </param>
public sealed record CreditTransferRow(
    Guid TransferId,
    string Direction,
    string Initiation,
    Guid CounterpartyId,
    string? CounterpartyName,
    long AmountMinor,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>One row of a Reports tab (<c>safety.vehicle_reports</c>, US-12.6).</summary>
public sealed record VehicleReportRow(
    Guid ReportId,
    Guid VehicleId,
    string? RegNo,
    string Reason,
    string Status,
    DateTimeOffset CreatedAt);

// -------------------------------------------------------------------------------------------------
// Vehicle directory (AL-42)
// -------------------------------------------------------------------------------------------------

/// <summary>One row of SCR-AP-014.</summary>
public sealed record VehicleDirectoryRow(
    Guid VehicleId,
    string Type,
    string Mode,
    string? Owner,
    string? FleetOrg,
    string RegNo,
    int Trips,
    string Status,
    DateTimeOffset RegisteredAt);

/// <summary>SCR-AP-015's info block.</summary>
/// <param name="InsuranceExpiry">
/// From <c>registry.documents</c> (E-03's expiry column), newest document of the kind. Null where
/// nothing of that kind was ever uploaded — which for a Mode C vehicle is a vehicle AL-10 will not
/// approve, and is therefore worth seeing as an absence rather than as a date that is not there.
/// </param>
public sealed record VehicleInfoRow(
    Guid VehicleId,
    string Type,
    string RegNo,
    string Mode,
    Guid OwnerId,
    string? Owner,
    Guid? FleetId,
    string? FleetOrg,
    string Status,
    string DispatchState,
    string OnboardingStatus,
    DateOnly? InsuranceExpiry,
    DateOnly? RevenueLicenceExpiry,
    DateTimeOffset RegisteredAt);

/// <summary>The tracker bound to the vehicle, if one is (T-08).</summary>
/// <param name="LastSeenAt">
/// <c>prov.tracker_bindings.last_seen_at</c>. Whether that counts as online is decided against
/// US-3.13's 30-minute silence, which is the same threshold fleet-health (C044) defaults
/// <c>Health:OfflineAfter</c> to — a directory that disagreed with the fleet-health screen about
/// whether a tracker is up would be two answers to one question.
/// </param>
public sealed record TrackerRow(string Imei, string State, DateTimeOffset? LastSeenAt);

/// <summary>One row of SCR-AP-015's Earnings tab — a Colombo business day (D-38).</summary>
/// <remarks>
/// Derived from <c>fares.ride_payments</c> over the vehicle's rides rather than read from
/// <c>fares.driver_earnings</c>: that rollup is keyed <c>(driver_id, earn_date)</c> and a vehicle
/// driven by two people on one day appears in it twice under neither vehicle. The Colombo date is
/// taken the same way D-38 takes it everywhere else.
/// </remarks>
public sealed record VehicleEarningsRow(
    DateOnly EarnDate, int Trips, long GrossMinor, string Currency);
