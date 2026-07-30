using System.Globalization;
using System.Security.Claims;
using System.Text;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using MageRide.Wallet.Configuration;
using MageRide.Wallet.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace MageRide.Wallet.Endpoints;

/// <summary>
/// <c>/v1/wallet/{userId}</c>, its transaction history and the transfer history (US-9.7, US-9A.19,
/// US-9A.11).
/// </summary>
/// <remarks>
/// <para>
/// <b>The balance is read from <c>billing.accounts</c>, the master.</b> §10 makes the ledger
/// authoritative and <c>billing.wallets</c> a mirror that exists for dispatch-svc's hot path; a wallet
/// screen that read the mirror would show a driver a number that lags their own top-up.
/// </para>
/// <para>
/// <b>A <c>{userId}</c> in the path is checked against the token, in one place</b>
/// (<see cref="SubjectScope.Require"/>), because the rule has to be identical on all three routes that
/// carry one. The six back-office roles pass — US-24.9/24.10's read-only tabs — and the PII_READ audit
/// for that is admin-bff's (D-35), not this service's.
/// </para>
/// </remarks>
public static class WalletEndpoints
{
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var wallet = endpoints.MapGroup("/v1/wallet").WithTags("wallet").RequireAuthorization();

        wallet.MapGet("/{userId}", GetWalletAsync).WithName("getWallet");
        wallet.MapGet("/{userId}/transactions", GetTransactionsAsync).WithName("listWalletTransactions");
        wallet.MapGet("/{driverId}/transfers", GetTransfersAsync).WithName("listWalletTransfers");

        return endpoints;
    }

    private static async Task<Ok<WalletResponse>> GetWalletAsync(
        string userId,
        HttpContext context,
        IAccountRepository accounts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(accounts);

        var owner = SubjectScope.Require(context.User, userId);

        // A driver who has never had a movement has no account row — and a wallet of zero is the honest
        // answer to "what is my balance", not a 404. The row is created by the first credit.
        var summary = await accounts.ReadSummaryAsync(owner, cancellationToken);

        return TypedResults.Ok(new WalletResponse(
            owner,
            summary?.BalanceMinor ?? 0,
            summary?.AvailableMinor ?? 0,
            summary?.OutstandingDebtMinor ?? 0,
            summary?.Currency ?? "LKR",
            summary?.UpdatedAt ?? DateTimeOffset.UnixEpoch));
    }

    /// <remarks>
    /// <c>Accept: text/csv</c> returns the same window as a statement (US-9A.19). PDF is declared by the
    /// contract and is **not** produced here — see the C046 handoff: a PDF needs a renderer and a
    /// document template that no spec provides, and answering with a PDF-shaped CSV would be worse than
    /// saying so.
    /// </remarks>
    private static async Task<IResult> GetTransactionsAsync(
        string userId,
        string? from,
        string? to,
        HttpContext context,
        IAccountRepository accounts,
        ILedgerRepository ledger,
        IOptions<WalletOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(options);

        var owner = SubjectScope.Require(context.User, userId);
        var window = StatementWindow.Parse(from, to);

        var summary = await accounts.ReadSummaryAsync(owner, cancellationToken);

        if (summary is null)
        {
            return TypedResults.Ok(CursorPage<WalletTransactionResponse>.Empty);
        }

        var wantsCsv = context.Request.Headers.Accept
            .Any(value => value?.Contains("text/csv", StringComparison.OrdinalIgnoreCase) == true);

        if (wantsCsv)
        {
            var rows = await ledger.ReadTransactionsAsync(
                summary.AccountId,
                window.From,
                window.To,
                before: null,
                beforeId: null,
                limit: options.Value.MaxStatementRows,
                cancellationToken);

            return Results.Text(Statement.ToCsv(rows), "text/csv", Encoding.UTF8);
        }

        if (context.Request.Headers.Accept
            .Any(value => value?.Contains("application/pdf", StringComparison.OrdinalIgnoreCase) == true))
        {
            // Returned rather than thrown, and that is not a style choice: the kernel's exception handler
            // writes through `IProblemDetailsService`, which honours the request's `Accept` — so a client
            // that asked for `application/pdf` and nothing else would receive a 415 with an **empty body**
            // and no explanation. `MageRideResults.Problem` writes problem+json regardless.
            return MageRideResults.Problem(
                MageRideErrors.UnsupportedMediaType,
                "A PDF statement is declared by the contract and not implemented: it needs a document "
                + "renderer and a template no spec provides. Use text/csv or JSON.");
        }

        var page = PageRequest.FromQuery(context.Request);
        var (before, beforeId) = TransactionCursor.Decode(page.Cursor);

        var slab = await ledger.ReadTransactionsAsync(
            summary.AccountId,
            window.From,
            window.To,
            before,
            beforeId,
            page.OverfetchLimit,
            cancellationToken);

        // Paged over the *rows* and projected afterwards: the keyset is `(ts, id)` and `id` is the
        // BIGINT identity of the projection line, which the response shape does not carry. Building the
        // page from responses would leave the cursor with nothing to break a timestamp tie on.
        return TypedResults.Ok(
            CursorPage<WalletTransactionRow>
                .FromOverfetch(slab, page.Limit, TransactionCursor.Encode)
                .Select(Statement.ToResponse));
    }

    private static async Task<Ok<CursorPage<TransferResponse>>> GetTransfersAsync(
        string driverId,
        string? direction,
        HttpContext context,
        ITransferRepository transfers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transfers);

        var owner = SubjectScope.Require(context.User, driverId);
        var page = PageRequest.FromQuery(context.Request);
        var (before, beforeId) = TransferCursor.Decode(page.Cursor);

        var wanted = direction?.ToLowerInvariant() switch
        {
            "sent" => "sent",
            "received" => "received",
            null or "" or "all" => "all",
            _ => throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["direction"] = ["direction is sent, received or all."],
            }),
        };

        var slab = await transfers.ReadForDriverAsync(
            owner, wanted, before, beforeId, page.OverfetchLimit, cancellationToken);

        return TypedResults.Ok(
            CursorPage<TransferRow>
                .FromOverfetch(slab, page.Limit, TransferCursor.Encode)
                .Select(row => Transfers.ToResponse(row, owner)));
    }
}

/// <summary>
/// The <c>{userId}</c>-in-the-path rule, once.
/// </summary>
/// <remarks>
/// The same shape query-svc uses. A malformed id is <c>403</c> rather than a detailed validation error:
/// answering "that is not a ULID" for a value that is not the caller's own id anyway tells a prober
/// which of their guesses are well formed.
/// </remarks>
internal static class SubjectScope
{
    internal static Guid Require(ClaimsPrincipal? principal, string requestedUserId)
    {
        var caller = principal.RequireSubjectId();

        if (!Ulids.TryParse(requestedUserId, out var requested) || requested == Guid.Empty)
        {
            throw new MageRideException(MageRideErrors.Forbidden, "This wallet is not yours.");
        }

        if (requested == caller)
        {
            return requested;
        }

        // AL-02/AL-06: the six back-office roles read a driver's wallet from the Admin Portal
        // (US-24.9/24.10, and the finance transactions report). The D-35 PII_READ audit for that is
        // admin-bff's, not this service's — but the read itself has to be possible.
        if (principal.Roles().Any(MageRideRoles.Internal.Contains))
        {
            return requested;
        }

        throw new MageRideException(MageRideErrors.Forbidden, "This wallet is not yours.");
    }
}

/// <summary>The optional <c>?from=&amp;to=</c> business-date window on the statement.</summary>
/// <remarks>
/// Dates are <c>Asia/Colombo</c> business dates (D-38) and are widened to the whole day: a driver asking
/// for "3 July" means the day they worked, not an instant. The upper bound is exclusive, so a single-day
/// window is <c>[00:00, next 00:00)</c> and two adjacent windows cannot both contain one transaction.
/// </remarks>
internal static class StatementWindow
{
    private static readonly TimeZoneInfo Colombo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Colombo");

    internal static (DateTimeOffset? From, DateTimeOffset? To) Parse(string? from, string? to) =>
        (StartOfDay(from), StartOfDay(to)?.AddDays(1));

    private static DateTimeOffset? StartOfDay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["from"] = ["A statement window is two ISO business dates, e.g. 2026-07-30."],
            });
        }

        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        return new DateTimeOffset(local, Colombo.GetUtcOffset(local));
    }
}

/// <summary>Projections and the CSV statement.</summary>
internal static class Statement
{
    internal static WalletTransactionResponse ToResponse(WalletTransactionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new WalletTransactionResponse(
            // `billing.wallet_transactions.id` is a BIGINT identity, and the contract types
            // `transactionId` as a Ulid — so the stable 128-bit id a client can carry is the entry's,
            // with the line number folded in for uniqueness within it. A projection row is not an
            // aggregate; the entry is.
            EntryScopedId(row.EntryId, row.Id),
            row.EntryId,
            row.Kind,
            row.AmountMinor,
            "LKR",
            row.BalanceAfterMinor,
            row.Description,
            row.Ts);
    }

    /// <remarks>
    /// RFC 4180: fields with a comma, a quote or a newline are quoted and inner quotes doubled. A
    /// description carries a driver-supplied reference, so this is not theoretical.
    /// </remarks>
    internal static string ToCsv(IReadOnlyList<WalletTransactionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var csv = new StringBuilder("occurredAt,kind,amountMinor,balanceAfterMinor,currency,reference\n");

        foreach (var row in rows)
        {
            csv.Append(row.Ts.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append(',')
               .Append(Escape(row.Kind)).Append(',')
               .Append(row.AmountMinor.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(row.BalanceAfterMinor.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append("LKR").Append(',')
               .Append(Escape(row.Description)).Append('\n');
        }

        return csv.ToString();
    }

    private static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return string.Empty;
        }

        return field.AsSpan().IndexOfAny(",\"\n\r") < 0
            ? field
            : $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>A stable id for a projection line: the entry id with the row number mixed into its tail.</summary>
    private static Guid EntryScopedId(Guid entryId, long lineId)
    {
        var bytes = entryId.ToByteArray();
        var line = BitConverter.GetBytes(lineId);

        for (var i = 0; i < line.Length; i++)
        {
            bytes[^(i + 1)] ^= line[i];
        }

        return new Guid(bytes);
    }
}

/// <summary>Which side of a transfer the caller was on (US-9A.11).</summary>
internal static class Transfers
{
    internal static TransferResponse ToResponse(TransferRow row, Guid caller)
    {
        ArgumentNullException.ThrowIfNull(row);

        var sent = row.SenderDriverId == caller;

        return new TransferResponse(
            row.Id,
            sent ? row.RecipientDriverId : row.SenderDriverId,
            sent ? row.RecipientName : row.SenderName,
            row.AmountMinor,
            row.Currency,
            sent ? "sent" : "received",
            row.Status,
            row.CreatedAt);
    }
}

/// <summary>
/// Keyset cursor over <c>(ts, id)</c> for the statement.
/// </summary>
/// <remarks>
/// Two lines can share a microsecond — a transfer posts both legs at once — so a cursor on the timestamp
/// alone would skip or repeat rows. Unsigned, and it does not matter: the query is scoped by the
/// account the token resolved to and the cursor contributes only an ordering bound. An unparseable
/// cursor is the first page rather than a 400, so a client that upgraded across a format change sees the
/// top of its history.
/// </remarks>
internal static class TransactionCursor
{
    private const char Separator = '|';

    internal static string Encode(WalletTransactionRow last)
    {
        ArgumentNullException.ThrowIfNull(last);

        return CursorCodec.Unsigned.EncodeString(
            string.Create(CultureInfo.InvariantCulture, $"{last.Ts.UtcDateTime:O}{Separator}{last.Id}"));
    }

    internal static (DateTimeOffset? Before, long? BeforeId) Decode(string? cursor)
    {
        if (!CursorCodec.Unsigned.TryDecodeString(cursor, out var raw))
        {
            return (null, null);
        }

        var parts = raw.Split(Separator);

        return parts.Length == 2
               && DateTimeOffset.TryParse(
                   parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var before)
               && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var id)
            ? (before, id)
            : (null, null);
    }
}

/// <summary>Keyset cursor over <c>(created_at, id)</c> for transfer history.</summary>
internal static class TransferCursor
{
    private const char Separator = '|';

    internal static string Encode(TransferRow last)
    {
        ArgumentNullException.ThrowIfNull(last);

        return CursorCodec.Unsigned.EncodeString(
            string.Create(CultureInfo.InvariantCulture, $"{last.CreatedAt.UtcDateTime:O}{Separator}{last.Id}"));
    }

    internal static (DateTimeOffset? Before, Guid? BeforeId) Decode(string? cursor)
    {
        if (!CursorCodec.Unsigned.TryDecodeString(cursor, out var raw))
        {
            return (null, null);
        }

        var parts = raw.Split(Separator);

        return parts.Length == 2
               && DateTimeOffset.TryParse(
                   parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var before)
               && Guid.TryParse(parts[1], out var id)
            ? (before, id)
            : (null, null);
    }
}
