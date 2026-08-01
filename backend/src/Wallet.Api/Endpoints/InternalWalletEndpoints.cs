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
/// <c>/v1/internal/wallet/{driverId}/debit</c> · <c>/credit</c> and
/// <c>/v1/internal/wallet/fleet/{fleetId}/debit</c> · <c>/credit</c> — the ledger seam.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every service that moves somebody's money without owning the ledger comes through here</b>, so
/// <c>billing.journal_postings</c> keeps exactly one writer (D-09). subscription-svc charges the D-13
/// daily fee, fare-svc settles a D-05 penalty and pays out a tip, admin-bff reverses a fee,
/// fleet-billing-svc settles a consolidated fleet invoice and credits a fleet top-up — five callers,
/// one balanced-entry implementation, one non-negativity rule, one D-08 cache write-through and
/// one outbox.
/// </para>
/// <para>
/// <b>The fleet pair is a second route family and not a second ledger (<b>Δ C060</b>, AL-03).</b>
/// ADD §6 gives fleet-billing-svc "posts to the same <c>billing</c> ledger with
/// <c>owner_type='fleet'</c>", and the only thing it needs that the driver routes cannot give it is
/// an account resolved by <em>fleet</em> id. Everything downstream — the lock ordering, the Σ = 0
/// check, the <c>billing.wallets</c> mirror, the history line, the outbox row — is
/// <c>LedgerService.PostAsync</c>, unchanged. The path is <c>/fleet/{fleetId}/…</c> rather than a
/// <c>ownerType</c> field on the body because a body field that picks whose wallet is debited is one
/// typo away from taking a month's fleet invoice out of a driver's balance.
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

        // Δ C060. Three segments where the driver pair has two, so routing resolves them without an
        // ambiguity rule: `/fleet/{fleetId}/debit` cannot be read as `/{driverId}/debit`.
        internalGroup.MapPost("/fleet/{fleetId}/debit", FleetDebitAsync).WithName("internalFleetWalletDebit");
        internalGroup.MapPost("/fleet/{fleetId}/credit", FleetCreditAsync).WithName("internalFleetWalletCredit");
        internalGroup.MapPost("/fleet/{fleetId}/account", FleetAccountAsync).WithName("internalFleetWalletAccount");

        // Δ AL-57 / AL-58. Two movements the debit/credit seam deliberately cannot express: one has
        // two wallet legs rather than a wallet and the platform, and the other composes its own
        // ledger key so a retried payout run cannot pay twice.
        internalGroup.MapPost("/trip-payment", TripPaymentAsync).WithName("internalWalletTripPayment");
        internalGroup.MapPost("/driver-payout", DriverPayoutAsync).WithName("internalWalletDriverPayout");
        internalGroup.MapPost("/driver-payout/{payoutId}/reverse", DriverPayoutReversalAsync)
            .WithName("internalWalletDriverPayoutReverse");

        return endpoints;
    }

    /// <summary>
    /// Resolves — creating if needed — the organisation's ledger account. Moves no money. <b>Δ C060.</b>
    /// </summary>
    /// <remarks>
    /// The account is created lazily by the first posting, which leaves fleet-billing-svc unable to
    /// say which wallet a top-up session will credit until the organisation has already been
    /// invoiced once. The alternatives were both worse: posting a synthetic movement to force the
    /// row into existence puts a transaction nobody made on a customer's statement, and letting a
    /// second service <c>INSERT INTO billing.accounts</c> gives the ledger a second writer for the
    /// sake of one row. So the create is exposed on its own, idempotent by
    /// <c>ux_accounts_owner</c> — and it is the only route in this family that is not about money.
    /// </remarks>
    private static async Task<Ok<LedgerAccountResponse>> FleetAccountAsync(
        string fleetId, IAccountRepository accounts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var fleet = RequestIds.Require(fleetId, "fleetId");
        var account = await accounts.EnsureFleetAccountAsync(fleet, cancellationToken);

        return TypedResults.Ok(new LedgerAccountResponse(
            account.Id, fleet, account.OwnerType, account.Currency, account.BalanceMinor));
    }

    private static Task<Ok<LedgerPostingResultResponse>> DebitAsync(
        string driverId,
        LedgerPostingBody? body,
        IAccountRepository accounts,
        ILedgerService ledger,
        CancellationToken cancellationToken) =>
        PostAsync(driverId, body, debit: true, fleet: false, accounts, ledger, cancellationToken);

    private static Task<Ok<LedgerPostingResultResponse>> CreditAsync(
        string driverId,
        LedgerPostingBody? body,
        IAccountRepository accounts,
        ILedgerService ledger,
        CancellationToken cancellationToken) =>
        PostAsync(driverId, body, debit: false, fleet: false, accounts, ledger, cancellationToken);

    private static Task<Ok<LedgerPostingResultResponse>> FleetDebitAsync(
        string fleetId,
        LedgerPostingBody? body,
        IAccountRepository accounts,
        ILedgerService ledger,
        CancellationToken cancellationToken) =>
        PostAsync(fleetId, body, debit: true, fleet: true, accounts, ledger, cancellationToken);

    private static Task<Ok<LedgerPostingResultResponse>> FleetCreditAsync(
        string fleetId,
        LedgerPostingBody? body,
        IAccountRepository accounts,
        ILedgerService ledger,
        CancellationToken cancellationToken) =>
        PostAsync(fleetId, body, debit: false, fleet: true, accounts, ledger, cancellationToken);

    /// <summary>
    /// The AL-57 wallet fare: the passenger's balance becomes the driver's, in one balanced entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this cannot be two calls to the debit/credit seam.</b> Those post one wallet leg
    /// against the platform, so a fare would be a passenger debit and a driver credit in two
    /// entries — and a crash between them either creates money or destroys it. Here the two legs are
    /// one <c>trip_payment</c> entry that Σ = 0 by construction: MageRide is the custodian of the
    /// balance, not a party to the fare.
    /// </para>
    /// <para>
    /// <b>The key is composed here, not accepted.</b> 1101's own header fixes the spelling
    /// (<c>'trip_payment:' || ride_payment_id</c>), and a caller free to choose it is a caller free
    /// to pay one fare twice. That is also why <c>trip_payment</c> stays out of this route's reach
    /// through the seam's whitelist for the passenger direction.
    /// </para>
    /// <para>
    /// <b>A short balance is the ledger's own refusal.</b> <c>LedgerService</c> already enforces
    /// non-negativity for every wallet owner and a passenger is one (Δ AL-57), so an insufficient
    /// balance surfaces as <c>402 insufficient-wallet</c> without a second check here that could
    /// disagree with it. fare-svc turns that into "pay cash or scan the driver's QR" — never a
    /// silent fallback.
    /// </para>
    /// </remarks>
    private static async Task<Ok<TripPaymentResultResponse>> TripPaymentAsync(
        TripPaymentBody? body,
        IAccountRepository accounts,
        ILedgerService ledger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(ledger);

        var ridePaymentId = RequestIds.Require(body?.RidePaymentId, "ridePaymentId");
        var passengerId = RequestIds.Require(body?.PassengerId, "passengerId");
        var driverId = RequestIds.Require(body?.DriverId, "driverId");
        var amountMinor = RequireAmount(body?.AmountMinor);

        if (passengerId == driverId)
        {
            // Σ = 0 would still hold and the balance would not move, so the ledger would accept it
            // silently. A fare paid by its own driver is a caller bug, and a no-op entry on somebody's
            // statement is worse than a refusal.
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["driverId"] = ["A passenger cannot pay a fare to themselves."],
            });
        }

        var passenger = await accounts.EnsurePassengerAccountAsync(passengerId, cancellationToken);
        var driver = await accounts.EnsureDriverAccountAsync(driverId, cancellationToken);

        var result = await ledger.PostAsync(
            new LedgerEntry(
                JournalKinds.TripPayment,
                LedgerKeys.TripPayment(ridePaymentId),
                body!.Description ?? $"Ride fare {ridePaymentId:D}",
                [
                    new LedgerLeg(passenger.Id, -amountMinor, ridePaymentId.ToString()),
                    new LedgerLeg(driver.Id, amountMinor, ridePaymentId.ToString()),
                ]),
            beforeCommit: null,
            cancellationToken);

        var passengerLeg = RequireLeg(result, passenger.Id, LedgerKeys.TripPayment(ridePaymentId));
        var driverLeg = RequireLeg(result, driver.Id, LedgerKeys.TripPayment(ridePaymentId));

        return TypedResults.Ok(new TripPaymentResultResponse(
            result.EntryId,
            passenger.Id,
            passengerLeg.BalanceAfterMinor,
            driver.Id,
            driverLeg.BalanceAfterMinor,
            amountMinor,
            result.Replayed));
    }

    /// <summary>
    /// The AL-58 sweep: a driver's balance leaves the platform for their bank.
    /// </summary>
    /// <remarks>
    /// The mirror of a top-up — driver debit, platform credit — because the platform account is the
    /// counterparty of every movement of real-world cash. payout-svc calls this inside the same
    /// transaction as the <c>billing.payouts</c> row it raises: an instruction with no debit pays a
    /// driver twice on a retry, and a debit with no instruction loses their money.
    /// </remarks>
    private static Task<Ok<LedgerPostingResultResponse>> DriverPayoutAsync(
        DriverPayoutBody? body,
        IAccountRepository accounts,
        ILedgerService ledger,
        CancellationToken cancellationToken) =>
        PayoutMovementAsync(
            RequestIds.Require(body?.PayoutId, "payoutId"),
            body?.DriverId,
            body?.AmountMinor,
            debit: true,
            body?.Description,
            accounts,
            ledger,
            cancellationToken);

    /// <summary>
    /// The reversal a <c>FAILED</c> instruction earns: the money goes back on the driver's wallet.
    /// </summary>
    /// <remarks>
    /// A second ledger key rather than a second kind — the movement is still about that payout and
    /// belongs beside it in the driver's history. Sharing the debit's key would make this a replay
    /// of the debit and restore nothing, which is the failure mode worth naming: a driver whose
    /// bank rejected the transfer would silently lose the week.
    /// </remarks>
    private static Task<Ok<LedgerPostingResultResponse>> DriverPayoutReversalAsync(
        string payoutId,
        DriverPayoutReversalBody? body,
        IAccountRepository accounts,
        ILedgerService ledger,
        CancellationToken cancellationToken) =>
        PayoutMovementAsync(
            RequestIds.Require(payoutId, "payoutId"),
            body?.DriverId,
            body?.AmountMinor,
            debit: false,
            body?.FailureReason is { Length: > 0 } reason ? $"Payout returned: {reason}" : "Payout returned",
            accounts,
            ledger,
            cancellationToken);

    private static async Task<Ok<LedgerPostingResultResponse>> PayoutMovementAsync(
        Guid payoutId,
        string? driverId,
        long? amountMinor,
        bool debit,
        string? description,
        IAccountRepository accounts,
        ILedgerService ledger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(ledger);

        var driver = RequestIds.Require(driverId, "driverId");
        var amount = RequireAmount(amountMinor);

        var account = await accounts.EnsureDriverAccountAsync(driver, cancellationToken);
        var platform = await accounts.PlatformAccountAsync(cancellationToken);
        var signed = debit ? -amount : amount;

        var key = debit ? LedgerKeys.DriverPayout(payoutId) : LedgerKeys.DriverPayoutReversal(payoutId);

        var result = await ledger.PostAsync(
            new LedgerEntry(
                JournalKinds.DriverPayout,
                key,
                description ?? $"Weekly payout {payoutId:D}",
                [
                    new LedgerLeg(account.Id, signed, payoutId.ToString()),
                    new LedgerLeg(platform.Id, -signed),
                ]),
            beforeCommit: null,
            cancellationToken);

        var leg = RequireLeg(result, account.Id, key);

        return TypedResults.Ok(new LedgerPostingResultResponse(
            result.EntryId, account.Id, leg.AmountMinor, leg.BalanceAfterMinor, result.Replayed));
    }

    private static long RequireAmount(long? amountMinor) =>
        amountMinor is { } amount && amount > 0
            ? amount
            : throw new MageRideException(
                MageRideErrors.InvalidAmount,
                "amountMinor is required and is unsigned — the route says which way the money moves.");

    /// <summary>
    /// The leg for an account this entry must have touched.
    /// </summary>
    /// <remarks>
    /// A miss means the key was spent by an entry about somebody else — a composed key colliding is
    /// a caller bug worth a conflict rather than a null-reference. The same guard the debit/credit
    /// routes make, extracted because three call sites now need it.
    /// </remarks>
    private static PostedLeg RequireLeg(LedgerResult result, Guid accountId, string key) =>
        result.For(accountId)
        ?? throw new MageRideException(
            MageRideErrors.Conflict,
            $"Idempotency key '{key}' was used by an entry that does not touch this wallet.");

    private static async Task<Ok<LedgerPostingResultResponse>> PostAsync(
        string ownerId,
        LedgerPostingBody? body,
        bool debit,
        bool fleet,
        IAccountRepository accounts,
        ILedgerService ledger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(ledger);

        var owner = RequestIds.Require(ownerId, fleet ? "fleetId" : "driverId");

        if (body?.AmountMinor is not { } amountMinor || amountMinor <= 0)
        {
            throw new MageRideException(
                MageRideErrors.InvalidAmount,
                "amountMinor is required and is unsigned — the route says which way the money moves.");
        }

        var kind = body.Kind;

        var allowed = (fleet, debit) switch
        {
            (true, true) => JournalKinds.FleetDebitKinds,
            (true, false) => JournalKinds.FleetCreditKinds,
            (false, true) => JournalKinds.InternalDebitKinds,
            (false, false) => JournalKinds.InternalCreditKinds,
        };

        if (Array.IndexOf(allowed, kind) < 0)
        {
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

        var account = fleet
            ? await accounts.EnsureFleetAccountAsync(owner, cancellationToken)
            : await accounts.EnsureDriverAccountAsync(owner, cancellationToken);

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
                      + (fleet ? "this fleet's wallet." : "this driver's wallet."));

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
