using System.Security.Cryptography;
using System.Text;
using MageRide.Shared.Errors;
using MageRide.Shared.Http.Idempotency;
using MageRide.Wallet.Domain;
using MageRide.Wallet.Ledger;
using MageRide.Wallet.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.Wallet.Endpoints;

/// <summary>
/// <c>/v1/internal/wallet/{driverId}/debit</c> and <c>/credit</c> — the ledger seam.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every service that moves a driver's money without owning the ledger comes through here</b>, so
/// <c>billing.journal_postings</c> keeps exactly one writer (D-09). subscription-svc charges the D-13
/// daily fee, fare-svc settles a D-05 penalty and pays out a tip, admin-bff reverses a fee — four
/// callers, one balanced-entry implementation, one non-negativity rule, one D-08 cache write-through and
/// one outbox.
/// </para>
/// <para>
/// <b>The <c>kind</c> whitelist is the boundary.</b> Without it this would be a "write me any entry"
/// API, and the reason the platform *cannot* record a per-transfer commission (AL-01) is precisely that
/// the kind vocabulary has no value for one. A caller may post the kinds a spec names for it
/// (<see cref="JournalKinds.InternalDebitKinds"/> / <see cref="JournalKinds.InternalCreditKinds"/>) and
/// nothing else — notably not <c>topup</c>, <c>voucher_purchase</c> or <c>driver_transfer</c>, which
/// have their own endpoints here and carry arithmetic and provider dedupe that would otherwise be
/// bypassed.
/// </para>
/// <para>
/// <b>Idempotency is the caller's ledger key, not a header.</b> The body's <c>idempotencyKey</c> becomes
/// <c>billing.journal_entries.idempotency_key</c>, which is UNIQUE — so a retry collides in the ledger
/// and the response says <c>replayed: true</c>. A second, header-based guard over the same money would
/// be weaker and would need its own table.
/// </para>
/// <para>
/// Protected like every other internal family: mTLS by D3' §0, refused at the gateway edge, and guarded
/// by <c>Wallet:InternalApiKey</c> until C042's mesh identity lands. <b>Without the key these routes are
/// not mapped at all</b> — they move money.
/// </para>
/// </remarks>
public static class InternalWalletEndpoints
{
    /// <summary>Carries <c>Wallet:InternalApiKey</c>. Replaced by the mesh peer identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    public static IEndpointRouteBuilder MapInternalWalletEndpoints(
        this IEndpointRouteBuilder endpoints, string internalApiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(internalApiKey);

        // AllowAnonymous because the caller is a service and presents no bearer; the filter is what
        // authenticates it, and the kernel's deny-by-default fallback would otherwise 401 before the
        // filter ran. Idempotency-exempt because the body carries the ledger key — see the remarks.
        var internalGroup = endpoints.MapGroup("/v1/internal/wallet")
            .WithTags("wallet")
            .AllowAnonymous()
            .AllowMissingIdempotencyKey()
            .AddEndpointFilter(new InternalKeyFilter(internalApiKey));

        internalGroup.MapPost("/{driverId}/debit", DebitAsync).WithName("internalWalletDebit");
        internalGroup.MapPost("/{driverId}/credit", CreditAsync).WithName("internalWalletCredit");

        return endpoints;
    }

    private static Task<Ok<LedgerPostingResultResponse>> DebitAsync(
        string driverId,
        LedgerPostingBody? body,
        IAccountRepository accounts,
        ILedgerService ledger,
        CancellationToken cancellationToken) =>
        PostAsync(driverId, body, debit: true, accounts, ledger, cancellationToken);

    private static Task<Ok<LedgerPostingResultResponse>> CreditAsync(
        string driverId,
        LedgerPostingBody? body,
        IAccountRepository accounts,
        ILedgerService ledger,
        CancellationToken cancellationToken) =>
        PostAsync(driverId, body, debit: false, accounts, ledger, cancellationToken);

    private static async Task<Ok<LedgerPostingResultResponse>> PostAsync(
        string driverId,
        LedgerPostingBody? body,
        bool debit,
        IAccountRepository accounts,
        ILedgerService ledger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(ledger);

        var driver = RequestIds.Require(driverId, "driverId");

        if (body?.AmountMinor is not { } amountMinor || amountMinor <= 0)
        {
            throw new MageRideException(
                MageRideErrors.InvalidAmount,
                "amountMinor is required and is unsigned — the route says which way the money moves.");
        }

        var kind = body.Kind;

        if (debit ? !JournalKinds.IsInternalDebit(kind) : !JournalKinds.IsInternalCredit(kind))
        {
            var allowed = debit ? JournalKinds.InternalDebitKinds : JournalKinds.InternalCreditKinds;

            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["kind"] = [$"'{kind}' cannot be posted here. This route accepts: {string.Join(", ", allowed)}."],
                },
                "A caller may post the entry kinds a spec names for it. Top-ups, voucher purchases and "
                + "driver transfers have their own endpoints, which carry arithmetic and provider dedupe "
                + "this route would bypass.");
        }

        if (string.IsNullOrWhiteSpace(body.IdempotencyKey))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["idempotencyKey"] =
                [
                    "idempotencyKey is required and must be composed from the business fact (e.g. "
                    + "daily_fee:{driverId}:{vehicleId}:{feeDate}), never randomly — it is what makes a "
                    + "retry a no-op.",
                ],
            });
        }

        var account = await accounts.EnsureDriverAccountAsync(driver, cancellationToken);
        var platform = await accounts.PlatformAccountAsync(cancellationToken);
        var signed = debit ? -amountMinor : amountMinor;

        var result = await ledger.PostAsync(
            new LedgerEntry(
                kind!,
                body.IdempotencyKey!,
                body.Description,
                [
                    new LedgerLeg(account.Id, signed, body.Reference),
                    new LedgerLeg(platform.Id, -signed),
                ]),
            beforeCommit: null,
            cancellationToken);

        var leg = result.For(account.Id)
                  ?? throw new MageRideException(
                      MageRideErrors.Conflict,
                      $"Idempotency key '{body.IdempotencyKey}' was used by an entry that does not touch "
                      + "this driver's wallet.");

        return TypedResults.Ok(new LedgerPostingResultResponse(
            result.EntryId, account.Id, leg.AmountMinor, leg.BalanceAfterMinor, result.Replayed));
    }
}

/// <summary>
/// Rejects a call that does not carry <c>Wallet:InternalApiKey</c>.
/// </summary>
/// <remarks>
/// Answers <c>404 not-found</c>, matching what the gateway returns for the <c>/v1/internal</c> prefix
/// (C008): a caller who is not entitled to the internal plane should not be able to map it. Fixed-time
/// comparison — a length-varying compare leaks the key a character at a time.
/// </remarks>
internal sealed class InternalKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(apiKey);

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[InternalWalletEndpoints.ApiKeyHeader].ToString();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected)
            ? next(context)
            : throw new MageRideException(MageRideErrors.NotFound, "No such resource.");
    }
}
