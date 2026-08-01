namespace MageRide.AdminBff.Domain;

/// <summary>One row of <c>pdpa.requests</c> (§16, E-06).</summary>
public sealed record PdpaRequestRow(
    Guid Id,
    Guid UserId,
    string Kind,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset DueBy,
    DateTimeOffset? FulfilledAt,
    string? HoldReason,
    Guid? DecidedBy,
    string? DecisionReason);

/// <summary>One row of <c>pdpa.fulfillment_artifacts</c> — what was handed over (§16).</summary>
public sealed record PdpaArtifactRow(
    Guid Id,
    Guid RequestId,
    string Kind,
    string StorageUrl,
    byte[]? Sha256,
    DateTimeOffset? SignedAt);

/// <summary>The two <c>pdpa.requests.kind</c> values.</summary>
public static class PdpaKinds
{
    public const string Export = "export";
    public const string Erasure = "erasure";

    public static readonly IReadOnlyList<string> All = [Export, Erasure];
}

/// <summary>The five <c>pdpa.requests.status</c> values, in the order a request moves through them.</summary>
public static class PdpaStatuses
{
    public const string Received = "Received";
    public const string InProgress = "InProgress";

    /// <summary>Erased everywhere a statute did not force retention. <c>hold_reason</c> names the statute.</summary>
    public const string FulfilledHold = "FulfilledHold";

    public const string Fulfilled = "Fulfilled";
    public const string Rejected = "Rejected";

    /// <summary>The two that mean "still work in flight" — <c>ix_pdpa_requests_due</c>'s predicate.</summary>
    public static readonly IReadOnlyList<string> Open = [Received, InProgress];

    public static bool IsOpen(string status) => Open.Contains(status, StringComparer.Ordinal);
}

/// <summary>The two <c>pdpa.fulfillment_artifacts.kind</c> values.</summary>
public static class PdpaArtifactKinds
{
    /// <summary>The archive an export hands the subject.</summary>
    public const string ExportZip = "export_zip";

    /// <summary>What an erasure removed and what it was forced to keep — the compliance record.</summary>
    public const string ErasureLog = "erasure_log";
}

/// <summary>
/// One reason a subject's data cannot be erased, or cannot be erased in full.
/// </summary>
/// <param name="Code">
/// A stable machine code, never a sentence: every string this platform shows a user is trilingual
/// (D-26) and the Passenger App owns the Si/Ta/En bundles. The code is what the portal and the app
/// look a message up by, and what <c>pdpa.requests.hold_reason</c> stores.
/// </param>
/// <param name="Blocking">
/// <see langword="true"/> when the hold stops the erasure happening at all — an in-flight ride, an
/// unresolved dispute. <see langword="false"/> when it merely bounds it: the erasure proceeds and a
/// statutorily-retained subset survives, which is what <c>FulfilledHold</c> means.
/// </param>
/// <param name="Count">How many records the hold is about, so an operator can see whether it is one or a thousand.</param>
public sealed record StatutoryHold(string Code, bool Blocking, int Count);

/// <summary>
/// The statutory hold list (E-06, ADD §6 admin-bff).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two kinds of hold, and conflating them is the mistake this type exists to prevent.</b> A
/// <em>blocking</em> hold is a live operation that anonymising the account would break — a
/// passenger mid-ride whose driver is looking at their name, an open dispute whose whole subject is
/// who did what. A <em>retention</em> hold is a record a statute requires be kept: the financial
/// ledger and the immutable audit trail. The first means "not yet"; the second means "yes, and this
/// part stays". A workflow that treated them the same would either refuse every erasure for ever
/// (every account has a ledger) or anonymise somebody who is in a car right now.
/// </para>
/// <para>
/// <b>The audit subset is on the list by name and is never touched.</b> D-35's log is append-only
/// and the erasure's own fulfilment writes a row to it; a "right to erasure" that deleted the record
/// of the erasure would leave the platform unable to prove it complied.
/// </para>
/// </remarks>
public static class StatutoryHolds
{
    /// <summary>A ride or tracking session that has not reached a terminal state.</summary>
    public const string ActiveRide = "active-ride";

    /// <summary>An open <c>support.tickets</c> row — a dispute nobody has answered (US-16.3).</summary>
    public const string OpenDispute = "open-dispute";

    /// <summary>A payment attempt that is neither settled nor abandoned, or a refund in flight (E-05).</summary>
    public const string UnsettledPayment = "unsettled-payment";

    /// <summary>Ledger postings. Retained: financial records outlive the account (D-09).</summary>
    public const string FinancialRecords = "financial-records";

    /// <summary><c>audit.events</c>. Retained and never rewritten (D-35).</summary>
    public const string AuditTrail = "audit-trail";

    /// <summary>A wallet with money still in it — the driver has to be paid before they can vanish.</summary>
    public const string WalletBalance = "wallet-balance";
}
