namespace MageRide.E2E.Infrastructure;

/// <summary>
/// A Finance Officer, as iam-svc would have left one (AL-02).
/// </summary>
/// <remarks>
/// The only back-office actor in this assembly. <c>POST /v1/admin/fare/refund</c> is E-05's whole
/// surface and it is role-gated to <c>finance_officer</c>/<c>admin</c>/<c>super_admin</c>, so a
/// refund cannot be driven by a passenger, a driver or a fleet owner — which is itself one of the
/// things <see cref="MageRide.E2E.Scenarios.RidePaymentScenario"/> asserts.
/// </remarks>
internal sealed record FinanceOfficer(Guid Id, string Bearer);

/// <summary>
/// An organisation whose money can actually be collected: an Owner, a <b>verified</b> payout profile
/// and one <b>Paid</b> Mode B vehicle.
/// </summary>
/// <remarks>
/// Distinct from C121's <see cref="FleetOrg"/>, which is an organisation and its bearers and nothing
/// about money. Every field here exists because Epic 23 collection needs it, and the three of them
/// are inseparable: AL-49 gives a Paid vehicle nowhere to send a passenger's fare without a verified
/// profile, and BR-31.1 refuses to classify a vehicle Paid without one — so an org that has one and
/// not the others cannot appear in a payment scenario at all.
/// </remarks>
/// <param name="PayoutProfileId">
/// The <c>registry.fleet_payout_profiles</c> row a Verification Officer approved. AL-49 makes it the
/// thing Epic 23 money routes to, and <c>ux_payout_profile_verified</c> makes it singular per org —
/// so this id is what a scenario asserts a pay sheet was composed from.
/// </param>
internal sealed record PaidFleetOrg(
    Guid FleetId,
    Guid OwnerId,
    string OwnerBearer,
    Guid VehicleId,
    string Plate,
    Guid PayoutProfileId);

/// <summary>
/// An organisation whose bank details are submitted and not yet verified — BR-31.1's gate, standing.
/// </summary>
internal sealed record PendingFleetOrg(Guid FleetId, Guid OwnerId, string OwnerBearer);

/// <summary>A Mode B subscription a passenger holds on one vehicle, after the owner accepted.</summary>
internal sealed record ModeBSubscription(
    Guid SubscriptionId, Guid GrantId, Passenger Passenger, PaidFleetOrg Org, long MonthlyFareMinor);

/// <summary>
/// One <c>billing.journal_entries</c> row with its legs, as a scenario reads it back.
/// </summary>
/// <param name="IdempotencyKey">
/// The business fact the entry was keyed on. Asserting on it rather than on a row count is what makes
/// "the retry did not move money twice" a statement about the platform's own key rather than about
/// how many rows happened to be there.
/// </param>
internal sealed record LedgerEntrySnapshot(
    Guid EntryId,
    string Kind,
    string IdempotencyKey,
    string? Description,
    DateTimeOffset At,
    IReadOnlyList<LedgerPostingSnapshot> Legs)
{
    /// <summary>Σ of the legs. Zero, or the entry should never have committed (D-09, <c>trg_balanced</c>).</summary>
    public long SumMinor => Legs.Sum(leg => leg.AmountMinor);

    /// <summary>This entry's leg against one account, or <see langword="null"/> if it did not touch it.</summary>
    public LedgerPostingSnapshot? For(Guid accountId) =>
        Legs.FirstOrDefault(leg => leg.AccountId == accountId);
}

/// <summary>One leg of an entry — a signed amount against one <c>billing.accounts</c> row.</summary>
internal sealed record LedgerPostingSnapshot(
    long PostingId, Guid AccountId, string OwnerType, Guid? OwnerId, long AmountMinor);

/// <summary>A <c>billing.accounts</c> row with its <c>billing.wallets</c> mirror.</summary>
/// <param name="MirrorMinor">
/// <c>billing.wallets.balance_minor</c> — the read model dispatch-svc's D-08 gate falls back to.
/// <see langword="null"/> for the two platform-side accounts, which own no wallet.
/// </param>
internal sealed record AccountSnapshot(
    Guid AccountId, string OwnerType, Guid? OwnerId, long BalanceMinor, long? MirrorMinor);

/// <summary>A <c>billing.daily_fee_charges</c> row (D-13).</summary>
internal sealed record DailyFeeSnapshot(
    Guid DriverId,
    Guid VehicleId,
    DateOnly FeeDate,
    DateTimeOffset FeeDateTzAt,
    long AmountMinor,
    string Currency,
    int TripsThatDay,
    string Status);

/// <summary>A <c>billing.topups</c> row (migration 1107) — the session a callback settles.</summary>
internal sealed record TopupSnapshot(
    Guid TopupId,
    Guid DriverId,
    string Method,
    long AmountMinor,
    string State,
    string OrderId,
    string? ProviderTransactionId,
    Guid? EntryId);

/// <summary>A <c>fares.ride_payments</c> row, as this suite asserts against it (D-10).</summary>
internal sealed record RidePaymentSnapshot(
    Guid PaymentId,
    Guid RideId,
    string State,
    string Method,
    long AmountMinor,
    long SurchargeMinor,
    long TipAmountMinor,
    string? PayerRole,
    Guid? PayerUserId,
    int AttemptNo,
    string? ProviderTransactionId);

/// <summary>A <c>fares.refunds</c> row. The partial index over the open statuses <b>is</b> the Finance queue.</summary>
internal sealed record RefundSnapshot(
    Guid RefundId,
    Guid PaymentId,
    string Kind,
    long AmountMinor,
    string Status,
    string ReasonCode,
    Guid? RequestedBy);

/// <summary>A <c>support.tickets</c> row — where an AL-47 dispute and its evidence land.</summary>
internal sealed record SupportTicketSnapshot(
    Guid TicketId, Guid UserId, string Category, string Status, string Description, Guid? RideId);

/// <summary>A <c>subscription.payments</c> row (Epic 23, migration 1202).</summary>
/// <remarks>
/// There is deliberately no posting id on this record, because there is no such column: §18b makes
/// Mode B subscription money a pass-through to the fleet owner that never enters
/// <c>billing.journal_*</c> at all, and migration 1202 gives the table no field that could hold one.
/// </remarks>
internal sealed record SubscriptionPaymentSnapshot(
    Guid PaymentId,
    Guid SubscriptionId,
    Guid VehicleId,
    Guid PassengerId,
    DateOnly PeriodMonth,
    string Method,
    long AmountMinor,
    string Status,
    string? SlipUrl,
    string? GatewayRef,
    Guid? ConfirmedBy,
    DateTimeOffset? PaidAt);
