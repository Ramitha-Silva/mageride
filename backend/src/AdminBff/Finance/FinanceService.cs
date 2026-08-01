using MageRide.AdminBff.Configuration;
using MageRide.AdminBff.Domain;
using MageRide.AdminBff.Persistence;
using MageRide.AdminBff.Reporting;
using MageRide.Shared.Errors;
using MageRide.Shared.Time;
using Microsoft.Extensions.Options;

namespace MageRide.AdminBff.Finance;

/// <summary>What one transactions query resolved to, and what it found.</summary>
public sealed record TransactionsResult(FinanceWindow Window, string? Kind, IReadOnlyList<TransactionRow> Rows);

/// <summary>
/// What one reconciliation query resolved to, and what it found.
/// </summary>
/// <remarks>
/// The window travels with the rows rather than being re-derived by the caller, because it is the
/// answer to "what does this screen cover" and the rows cannot supply it: a window with no
/// settlement in it has no dates to read the window back off.
/// </remarks>
public sealed record SettlementResult(FinanceWindow Window, IReadOnlyList<SettlementDayRow> Days);

/// <summary>
/// SCR-AP-006's reads: gateway settlement, the transactions report and the two review queues that
/// hang off the finance surface (US-9A.15, D6' §7.2, E-03, E-07).
/// </summary>
/// <remarks>
/// <b>Every method here is a read.</b> The two things this surface can change — a wallet reversal
/// and a refund — are <see cref="IWalletAdjustmentService"/> and <see cref="IRefundService"/>, both
/// of which forward to the service that owns the rows. Keeping the reads apart from them is what
/// makes "the Finance console moves no money by itself" checkable rather than asserted.
/// </remarks>
public interface IFinanceService
{
    Task<SettlementResult> SettlementAsync(
        DateOnly? from, DateOnly? to, string? method, CancellationToken cancellationToken);

    Task<IReadOnlyList<SettlementExceptionRow>> SettlementExceptionsAsync(
        string? kind, int limit, CancellationToken cancellationToken);

    Task<TransactionsResult> TransactionsAsync(
        DateOnly? from, DateOnly? to, string? kind, Guid? partyId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<DocumentExpiryRow>> DocumentExpiryQueueAsync(
        int? withinDays, string? kind, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<FraudFlagRow>> FraudQueueAsync(
        string? status, string? kind, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFinanceService"/>
internal sealed class FinanceService(
    IFinanceRepository finance, IOptions<AdminBffOptions> options, TimeProvider clock) : IFinanceService
{
    /// <summary>
    /// The default window when the caller names none: the last 30 Colombo days, ending today.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> Bounded rather than open-ended for the same reason
    /// <c>AuditLogDefaultWindow</c> is: <c>billing.journal_entries</c> is append-only and unbounded,
    /// and an "everything ever" default would get slower every day until somebody noticed. Thirty
    /// days is a monthly reconciliation cycle, which is what SCR-AP-006 is opened to do.
    /// </remarks>
    private const int DefaultWindowDays = 30;

    /// <summary>
    /// The widest window a single report may cover.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> A year and a day, matching C061's <c>MaxRangeDays</c>, so "the last twelve
    /// months" and "this year" both fit and a typo'd date is a named 400 rather than a query that
    /// walks the whole ledger.
    /// </remarks>
    private const int MaxWindowDays = 366;

    /// <summary>
    /// How far ahead the document-expiry queue looks when the caller names no horizon.
    /// </summary>
    /// <remarks>
    /// <b>E-03's own outermost reminder.</b> The nightly job notifies at T−30, T−7, T−1 and on
    /// expiry, so 30 days is exactly the set of documents the holder has already been written to
    /// about — which is what makes the queue actionable rather than speculative. A caller who wants
    /// only the urgent ones asks for 7.
    /// </remarks>
    private const int DefaultExpiryHorizonDays = 30;

    private readonly AdminBffOptions.FinanceOptions _finance =
        (options ?? throw new ArgumentNullException(nameof(options))).Value.Finance;

    public async Task<SettlementResult> SettlementAsync(
        DateOnly? from, DateOnly? to, string? method, CancellationToken cancellationToken)
    {
        if (method is not null && method is not ("onepay" or "lankaqr"))
        {
            // AL-05, as a 400 rather than an empty page. `ck_topups_method` admits exactly these
            // two — there is no bank-transfer rail — and answering 200 with nothing for `?method=
            // bank_transfer` would read as "no bank transfers settled yesterday", which is a
            // different and much more misleading statement than "that rail does not exist".
            throw Invalid("method", "method is onepay or lankaqr — there is no bank-transfer rail (AL-05).");
        }

        var window = Window(from, to);

        return new SettlementResult(window, await finance.SettlementAsync(window, method, cancellationToken));
    }

    public Task<IReadOnlyList<SettlementExceptionRow>> SettlementExceptionsAsync(
        string? kind, int limit, CancellationToken cancellationToken)
    {
        if (kind is not null && !SettlementExceptionKinds.IsKnown(kind))
        {
            throw Invalid("kind", $"kind is one of: {string.Join(", ", SettlementExceptionKinds.All)}.");
        }

        return finance.SettlementExceptionsAsync(
            kind, clock.GetUtcNow() - _finance.SettlementGracePeriod, limit, cancellationToken);
    }

    public async Task<TransactionsResult> TransactionsAsync(
        DateOnly? from, DateOnly? to, string? kind, Guid? partyId, int limit, CancellationToken cancellationToken)
    {
        if (kind is not null && !TransactionKinds.IsKnown(kind))
        {
            throw Invalid("kind", $"kind is one of: {string.Join(", ", TransactionKinds.All)}.");
        }

        var window = Window(from, to);

        return new TransactionsResult(
            window, kind, await finance.TransactionsAsync(window, kind, partyId, limit, cancellationToken));
    }

    public Task<IReadOnlyList<DocumentExpiryRow>> DocumentExpiryQueueAsync(
        int? withinDays, string? kind, int limit, CancellationToken cancellationToken)
    {
        var horizon = withinDays ?? DefaultExpiryHorizonDays;

        if (horizon is < 0 or > 365)
        {
            throw Invalid("withinDays", "withinDays is between 0 and 365. 0 lists only what has already expired.");
        }

        return finance.DocumentExpiryQueueAsync(horizon, kind, limit, cancellationToken);
    }

    public Task<IReadOnlyList<FraudFlagRow>> FraudQueueAsync(
        string? status, string? kind, int limit, CancellationToken cancellationToken)
    {
        if (status is not null && status is not ("open" or "dismissed" or "actioned"))
        {
            // reputation.yaml's FraudFlag.status enum and ck_fraud_flags_status (migration 0804),
            // transcribed rather than referenced: this project does not depend on Reputation.Api,
            // and the database is the backstop either way.
            throw Invalid("status", "status is one of open, dismissed or actioned.");
        }

        return finance.FraudQueueAsync(status, kind, limit, cancellationToken);
    }

    /// <summary>
    /// Resolves the reporting window and refuses the two ways it can be nonsense.
    /// </summary>
    /// <remarks>
    /// An inverted range and an over-wide one are both 400s naming the parameter rather than a
    /// silently swapped or truncated window: a finance report that quietly answered for a different
    /// period than the one asked for would put the right number under the wrong heading, and nothing
    /// downstream could tell.
    /// </remarks>
    private FinanceWindow Window(DateOnly? from, DateOnly? to)
    {
        var today = BusinessCalendar.Today(clock);
        var end = to ?? today;
        var start = from ?? end.AddDays(-(DefaultWindowDays - 1));

        if (start > end)
        {
            throw Invalid("from", "from must not be after to.");
        }

        if (end.DayNumber - start.DayNumber >= MaxWindowDays)
        {
            throw Invalid("from", $"the window must be at most {MaxWindowDays} days.");
        }

        return new FinanceWindow(start, end);
    }

    private static MageRideValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}
