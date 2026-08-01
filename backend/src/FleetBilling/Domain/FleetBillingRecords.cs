namespace MageRide.FleetBilling.Domain;

/// <summary>The organisation, as this service needs it.</summary>
/// <param name="Status"><c>registry.fleets.status</c>. Billing routes need APPROVED.</param>
public sealed record FleetOrganisation(Guid Id, string Name, string Status, Guid OwnerId)
{
    public bool IsApproved => string.Equals(Status, FleetStatuses.Approved, StringComparison.Ordinal);
}

/// <summary>One <c>billing.fleet_invoices</c> row.</summary>
/// <param name="TotalMinor">Σ of the invoice's lines, fixed at generation.</param>
/// <param name="JournalEntryId">
/// The balanced <c>fleet_invoice</c> entry that settled it. Non-null exactly when
/// <see cref="Status"/> is PAID — <c>ck_fleet_invoices_posting</c> and
/// <c>ck_fleet_invoices_settled</c> (1108) make that a database fact rather than a convention.
/// </param>
public sealed record FleetInvoice(
    Guid Id,
    Guid FleetId,
    DateOnly PeriodMonth,
    DateTimeOffset PeriodMonthTzAt,
    long TotalMinor,
    string Currency,
    string Status,
    Guid? JournalEntryId,
    DateTimeOffset? DueAt,
    DateTimeOffset? OverdueAt,
    DateTimeOffset? SettledAt,
    DateTimeOffset CreatedAt,
    int VehicleCount);

/// <summary>One <c>billing.fleet_invoice_lines</c> row — one vehicle's month.</summary>
/// <remarks>
/// <see cref="RegistrationNumber"/> and <see cref="VehicleType"/> are the values at generation, not
/// the vehicle's current ones: an operator re-reading last March must see the plate they were
/// billed for, and a bus can be re-plated or leave the organisation entirely.
/// </remarks>
public sealed record FleetInvoiceLine(
    Guid VehicleId,
    string RegistrationNumber,
    string VehicleType,
    long AmountMinor,
    string Currency,
    string Status);

/// <summary>An invoice with the breakdown US-13.10 asks for.</summary>
public sealed record FleetInvoiceDetail(FleetInvoice Invoice, IReadOnlyList<FleetInvoiceLine> Lines)
{
    /// <summary>Σ of the lines, computed rather than read. The DoD's first clause is this against
    /// <see cref="FleetInvoice.TotalMinor"/>.</summary>
    public long LineSumMinor => Lines.Sum(line => line.AmountMinor);
}

/// <summary>What one generation pass did for one month.</summary>
/// <param name="RaisedIds">
/// The invoices this pass created — the insert's own <c>RETURNING</c>, so two replicas running in
/// the same millisecond each announce only the rows they won. Empty on a re-run.
/// </param>
/// <param name="LinesAdded">
/// Per-vehicle lines appended. Non-zero on a re-run when a vehicle was approved mid-month — the
/// invoice already existed and grew, which is what makes re-running catch up rather than duplicate.
/// </param>
public sealed record InvoiceRunResult(
    DateOnly PeriodMonth, IReadOnlyList<Guid> RaisedIds, int LinesAdded, long TotalMinor)
{
    /// <summary>Fleets that had no invoice for the month and now do.</summary>
    public int InvoicesRaised => RaisedIds.Count;
}

/// <summary>What one settlement pass did.</summary>
/// <param name="Settled">Invoices that moved to PAID.</param>
/// <param name="Insufficient">Invoices left open because the fleet wallet could not cover them.</param>
public sealed record SettlementRunResult(int Attempted, int Settled, int Insufficient);

/// <summary>What one dunning pass did.</summary>
/// <param name="MarkedOverdue">DUE invoices past their term that became OVERDUE.</param>
/// <param name="Notified">Organisations a dunning notice was sent for.</param>
public sealed record DunningRunResult(int MarkedOverdue, int Notified);

/// <summary>One <c>billing.fleet_topups</c> row.</summary>
public sealed record FleetTopup(
    Guid Id,
    Guid FleetId,
    Guid AccountId,
    Guid InitiatedBy,
    string Method,
    long AmountMinor,
    string Currency,
    string State,
    string? ProviderOrderId,
    string? ProviderTransactionId,
    Guid? JournalEntryId,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SettledAt);

/// <summary>The fleet wallet, as SCR-FP-010 draws it.</summary>
/// <remarks>
/// The balance comes from <c>billing.accounts</c> — §10's master — and not from the
/// <c>billing.wallets</c> mirror, for wallet-svc's reason: the mirror exists for dispatch-svc's hot
/// path, and a billing screen reading it would show an operator a number that lags their own top-up.
/// </remarks>
public sealed record FleetWalletSummary(
    Guid? AccountId, long BalanceMinor, string Currency, long OutstandingMinor, DateTimeOffset? UpdatedAt)
{
    /// <summary>What is left after everything already invoiced and unpaid.</summary>
    public long AvailableMinor => BalanceMinor - OutstandingMinor;
}

/// <summary>One line of the fleet wallet's history (<c>billing.wallet_transactions</c>).</summary>
public sealed record FleetWalletMovement(
    long Id,
    Guid EntryId,
    string Kind,
    long AmountMinor,
    long BalanceAfterMinor,
    string? Description,
    DateTimeOffset Ts);

/// <summary>An overdue invoice, with what the dunning notice needs to say.</summary>
public sealed record OverdueInvoice(
    Guid InvoiceId,
    Guid FleetId,
    string FleetName,
    DateOnly PeriodMonth,
    long TotalMinor,
    DateTimeOffset DueAt,
    IReadOnlyList<Guid> OwnerIds);
