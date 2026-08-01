using MageRide.Shared.Errors;

namespace MageRide.FleetBilling.Domain;

/// <summary>
/// <c>billing.fleet_invoices.status</c> (migration 1106, extended by 1108) — and
/// <c>fleet.yaml</c>'s <c>FleetInvoice.status</c>, which is the same four values.
/// </summary>
/// <remarks>
/// <b>The contract shipped four and the CHECK admitted three.</b> 1106 printed
/// <c>('FREE','DUE','PAID')</c> while <c>fleet.yaml</c> has always returned
/// <c>[FREE, DUE, PAID, OVERDUE]</c>, so the state C060's dunning deliverable is about could not be
/// stored. 1108 closes it; a micro-change-set is raised in the C060 handoff.
/// </remarks>
internal static class InvoiceStatuses
{
    /// <summary>Nothing is owed: every vehicle is in its first month, or the fleet runs Mode A only.</summary>
    /// <remarks>
    /// A FREE invoice is written rather than skipped — 1106's own table comment says so: "the row is
    /// the evidence the run considered them". It never posts, which <c>ck_fleet_invoices_free</c>
    /// enforces.
    /// </remarks>
    public const string Free = "FREE";

    /// <summary>Issued and unpaid, inside its payment term.</summary>
    public const string Due = "DUE";

    /// <summary>Issued, unpaid and past <c>due_at</c>. Dunning has been signalled.</summary>
    public const string Overdue = "OVERDUE";

    /// <summary>Settled against the fleet wallet by a balanced <c>fleet_invoice</c> entry.</summary>
    public const string Paid = "PAID";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Free, Due, Overdue, Paid };

    /// <summary>Whether money is still owed on this invoice.</summary>
    public static bool IsOpen(string status) =>
        string.Equals(status, Due, StringComparison.Ordinal)
        || string.Equals(status, Overdue, StringComparison.Ordinal);
}

/// <summary>
/// <c>billing.fleet_invoice_lines.status</c> — the per-vehicle charge's own state at generation.
/// </summary>
/// <remarks>
/// Two values where the invoice has four, and deliberately: a line is a statement about one
/// vehicle's month, and whether the fleet has since paid is a fact about the invoice. Copied from
/// <c>billing.monthly_subscriptions.status</c>, which C047 raises as FREE or DUE and never as PAID
/// — this service marks those rows PAID when the invoice settles, and the line keeps saying what
/// was billed.
/// </remarks>
internal static class InvoiceLineStatuses
{
    /// <summary>This vehicle's first Colombo month on the platform. Worth zero (D5' §2.1, §20).</summary>
    public const string Free = "FREE";

    /// <summary>An ordinary month at the configured per-vehicle rate.</summary>
    public const string Due = "DUE";
}

/// <summary>
/// The two top-up methods AL-05 leaves, and the values <c>ck_fleet_topups_method</c> admits.
/// </summary>
/// <remarks>
/// <b>There is no bank transfer</b>, here or in wallet-svc: AL-05 removed it as a top-up method, so
/// there is no route, no <c>method</c> value, no receipt column and no manual reconciliation queue.
/// <c>onepay</c> covers both the card and the OnePay-wallet rails — D6' §7.1 describes them as one
/// gateway and D3' Part 2 lists one route for them, which is why there is no separate <c>card</c>.
/// </remarks>
internal static class TopupMethods
{
    public const string Onepay = "onepay";
    public const string LankaQr = "lankaqr";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Onepay, LankaQr };
}

/// <summary><c>billing.fleet_topups.state</c> — the three <c>Topup.state</c> the contract prints.</summary>
internal static class TopupStates
{
    public const string Pending = "Pending";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

/// <summary><c>registry.fleets.status</c> (migration 0301).</summary>
internal static class FleetStatuses
{
    public const string Approved = "APPROVED";
}

/// <summary>
/// How this service composes <c>billing.journal_entries.idempotency_key</c>, and the one journal
/// <c>kind</c> it may post.
/// </summary>
/// <remarks>
/// The column is UNIQUE, which is what makes a retried money operation a no-op rather than a second
/// posting — so the key is derived from the business fact and never from a random value. Both
/// spellings are recorded in migration 1108's header as well, so the two cannot drift, and
/// <c>LedgerKeyTests</c> asserts the literal strings: a well-meaning reformat of one of them would
/// silently take a second month's money.
/// </remarks>
internal static class LedgerKeys
{
    /// <summary><c>billing.journal_entries.kind</c> for the consolidated monthly charge (Δ C060, 1108).</summary>
    public const string FleetInvoiceKind = "fleet_invoice";

    /// <summary><c>kind</c> for the credit a settled fleet top-up posts.</summary>
    public const string TopupKind = "topup";

    public static string FleetInvoice(Guid invoiceId) => $"fleet_invoice:{invoiceId}";

    public static string FleetTopup(Guid topupId) => $"fleet_topup:{topupId}";
}

/// <summary>
/// The codes this service raises, named once so a route does not have to reach across the whole
/// kernel registry.
/// </summary>
/// <remarks>
/// <b>Aliases, not declarations</b> — every one is declared in <see cref="MageRideErrors"/>, because
/// the platform keeps a single collision-free key space (D3' §0) and the Fleet Portal branches on
/// these codes without referencing this assembly. C057, C058 and C059 declared theirs the same way.
/// </remarks>
internal static class FleetBillingErrors
{
    /// <inheritdoc cref="MageRideErrors.FleetNotApproved"/>
    public static readonly ErrorCode FleetNotApproved = MageRideErrors.FleetNotApproved;

    /// <inheritdoc cref="MageRideErrors.NotFleetMember"/>
    public static readonly ErrorCode NotFleetMember = MageRideErrors.NotFleetMember;

    /// <inheritdoc cref="MageRideErrors.FleetRoleInsufficient"/>
    public static readonly ErrorCode FleetRoleInsufficient = MageRideErrors.FleetRoleInsufficient;

    /// <inheritdoc cref="MageRideErrors.FleetNotFound"/>
    public static readonly ErrorCode FleetNotFound = MageRideErrors.FleetNotFound;

    /// <summary>
    /// The invoice carries nothing to pay — it is FREE, or it has already been settled. <b>Δ C060.</b>
    /// </summary>
    /// <remarks>
    /// A code of its own rather than a bare <see cref="MageRideErrors.Conflict"/>, because
    /// SCR-FP-010 draws a different thing for each: "already paid" is a receipt to open and
    /// "nothing to pay" is a month that cost nothing. Declared in the kernel registry beside the
    /// other Epic 13 codes.
    /// </remarks>
    public static readonly ErrorCode InvoiceNotPayable = MageRideErrors.InvoiceNotPayable;
}
