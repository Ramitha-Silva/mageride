using System.Net;
using System.Net.Http.Json;
using Dapper;
using MageRide.Wallet.Endpoints;
using MageRide.Wallet.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Wallet.Tests.Integration;

/// <summary>
/// AL-57 / AL-58 — the passenger wallet, the wallet-paid fare, and the weekly payout that
/// discharges what the fare creates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist at all.</b> OnePay supports one merchant account per merchant, so a card ride
/// fare could only ever land in MageRide's own account — D-11's per-driver merchant never existed.
/// Card acceptance moved one step earlier: a passenger tops up here, where MageRide legitimately
/// <em>is</em> the payee, and spends it with <c>method: "wallet"</c>. That makes MageRide a
/// custodian and a driver's balance a liability, which is what the payout rail exists to settle.
/// </para>
/// <para>
/// Every claim below is asserted against <c>billing.accounts</c> and <c>billing.journal_postings</c>
/// — the master, not the mirror — because the point of a double-entry ledger is that the entry is
/// the truth and every projection is a convenience.
/// </para>
/// </remarks>
[Trait("Category", "Custody")]
[Collection<WalletCollection>]
public sealed class CustodyTests(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    // ---------------------------------------------------------------------------------------
    // The passenger wallet
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// DoD: "a passenger top-up and a driver top-up produce structurally identical entries against
    /// different accounts."
    /// </summary>
    [Fact]
    public async Task A_passenger_and_a_driver_hold_the_same_kind_of_wallet()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var passengerId = await harness.CreateUserAsync("passenger");
        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 25_000);

        await harness.CreditPassengerDirectlyAsync(passengerId, 25_000);

        Assert.Equal(25_000, await harness.BalanceAsync(passengerId, "passenger"));
        Assert.Equal(25_000, await harness.BalanceAsync(driver.Id));

        await using var connection = await harness.OpenAsync();

        // One account per owner per currency, whichever kind of owner it is — ux_accounts_owner is
        // the same index for both, and `passenger` is simply a fifth value in the CHECK (1109).
        var owners = await connection.QueryAsync<(string OwnerType, int Count)>(
            """
            SELECT owner_type, count(*)::int
              FROM billing.accounts
             WHERE owner_id IN (@Passenger, @Driver)
             GROUP BY owner_type ORDER BY owner_type;
            """,
            new { Passenger = passengerId, Driver = driver.Id });

        Assert.Equal([("driver", 1), ("passenger", 1)], owners.ToArray());
    }

    [Fact]
    public async Task A_passenger_reads_their_own_balance_on_the_wallet_route()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var passengerId = await harness.CreateUserAsync("passenger");
        await harness.CreditPassengerDirectlyAsync(passengerId, 40_000);

        var wallet = await harness.GetAsync<WalletResponse>(
            $"/v1/wallet/{passengerId}", harness.Tokens.Passenger(passengerId));

        // Before AL-57 the read filtered owner_type IN ('driver','fleet') and a passenger's own
        // balance came back as a flat zero.
        Assert.Equal(40_000, wallet.BalanceMinor);
        Assert.Equal(40_000, wallet.AvailableMinor);
    }

    // ---------------------------------------------------------------------------------------
    // The wallet-paid fare (AL-57)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// DoD: "a trip payment moves the fare from the passenger's account to the driver's in one
    /// balanced entry, and replays to a no-op."
    /// </summary>
    [Fact]
    public async Task A_wallet_fare_moves_one_balanced_entry_and_replays_to_nothing()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var passengerId = await harness.CreateUserAsync("passenger");
        var driver = await harness.CreateDriverAsync();
        await harness.CreditPassengerDirectlyAsync(passengerId, 50_000);

        var ridePaymentId = Guid.NewGuid();
        var body = new { ridePaymentId, passengerId, driverId = driver.Id, amountMinor = 18_000 };

        using var first = await harness.PostAsync(
            "/v1/internal/wallet/trip-payment", body, internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var settled = (await first.Content.ReadFromJsonAsync<TripPaymentResultResponse>())!;

        Assert.False(settled.Replayed);
        Assert.Equal(32_000, settled.PassengerBalanceAfterMinor);
        Assert.Equal(18_000, settled.DriverBalanceAfterMinor);

        Assert.Equal(32_000, await harness.BalanceAsync(passengerId, "passenger"));
        Assert.Equal(18_000, await harness.BalanceAsync(driver.Id));

        // ONE entry with TWO wallet legs and no platform leg — MageRide is the custodian of the
        // balance, not a party to the fare. Σ = 0 is what makes that expressible.
        Assert.Equal(0, await harness.EntrySumAsync(settled.EntryId));

        await using var connection = await harness.OpenAsync();

        var legs = await connection.QuerySingleAsync<int>(
            "SELECT count(*)::int FROM billing.journal_postings WHERE entry_id = @Id;",
            new { Id = settled.EntryId });

        Assert.Equal(2, legs);

        // A retried settlement collides on the composed key `trip_payment:{ridePaymentId}` — which
        // is why the key is composed here and not accepted from the caller.
        using var second = await harness.PostAsync(
            "/v1/internal/wallet/trip-payment", body, internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var replay = (await second.Content.ReadFromJsonAsync<TripPaymentResultResponse>())!;

        Assert.True(replay.Replayed);
        Assert.Equal(settled.EntryId, replay.EntryId);
        Assert.Equal(32_000, await harness.BalanceAsync(passengerId, "passenger"));
        Assert.Equal(18_000, await harness.BalanceAsync(driver.Id));
    }

    [Fact]
    public async Task A_fare_a_passenger_cannot_afford_moves_nothing()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var passengerId = await harness.CreateUserAsync("passenger");
        var driver = await harness.CreateDriverAsync();
        await harness.CreditPassengerDirectlyAsync(passengerId, 5_000);

        using var response = await harness.PostAsync(
            "/v1/internal/wallet/trip-payment",
            new { ridePaymentId = Guid.NewGuid(), passengerId, driverId = driver.Id, amountMinor = 5_001 },
            internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Equal("insufficient-wallet", (await WalletHarness.ProblemAsync(response)).Code);

        // A passenger is a wallet owner, so the ledger's own non-negativity rule refuses it — there
        // is no second check here that could disagree. fare-svc turns this into "pay cash or scan
        // the driver's QR", never a silent fallback.
        Assert.Equal(5_000, await harness.BalanceAsync(passengerId, "passenger"));
        Assert.Equal(0, await harness.BalanceAsync(driver.Id));
        Assert.Equal(0, await harness.LedgerSumAsync());
    }

    [Fact]
    public async Task A_driver_cannot_be_paid_a_fare_by_themselves()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();

        using var response = await harness.PostAsync(
            "/v1/internal/wallet/trip-payment",
            new { ridePaymentId = Guid.NewGuid(), passengerId = driver.Id, driverId = driver.Id, amountMinor = 1_000 },
            internalKey: WalletHarness.InternalApiKey);

        // Σ = 0 would hold and the balance would not move, so the ledger would take it silently.
        // A no-op entry on somebody's statement is worse than a refusal.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // The weekly payout (AL-58)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_payout_sweeps_the_balance_and_a_failure_puts_it_back_exactly_once()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 62_500);
        var payoutId = Guid.NewGuid();

        using var swept = await harness.PostAsync(
            "/v1/internal/wallet/driver-payout",
            new { payoutId, driverId = driver.Id, amountMinor = 62_500 },
            internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, swept.StatusCode);

        // The full sweep: no minimum, no holdback (Payout:RetainMinor = 0).
        Assert.Equal(0, await harness.BalanceAsync(driver.Id));

        // A retried run collides on `driver_payout:{payoutId}` and pays nothing a second time.
        using var retried = await harness.PostAsync(
            "/v1/internal/wallet/driver-payout",
            new { payoutId, driverId = driver.Id, amountMinor = 62_500 },
            internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.True((await retried.Content.ReadFromJsonAsync<LedgerPostingResultResponse>())!.Replayed);
        Assert.Equal(0, await harness.BalanceAsync(driver.Id));

        // The bank rejects it. The money comes back under a SECOND key — sharing the debit's key
        // would make this a replay of the debit and restore nothing.
        using var returned = await harness.PostAsync(
            $"/v1/internal/wallet/driver-payout/{payoutId}/reverse",
            new { driverId = driver.Id, amountMinor = 62_500, failureReason = "account closed" },
            internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        Assert.Equal(62_500, await harness.BalanceAsync(driver.Id));

        // And exactly once: a redelivered bank result restores nothing further.
        using var redelivered = await harness.PostAsync(
            $"/v1/internal/wallet/driver-payout/{payoutId}/reverse",
            new { driverId = driver.Id, amountMinor = 62_500, failureReason = "account closed" },
            internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, redelivered.StatusCode);
        Assert.True((await redelivered.Content.ReadFromJsonAsync<LedgerPostingResultResponse>())!.Replayed);
        Assert.Equal(62_500, await harness.BalanceAsync(driver.Id));

        // The whole platform still balances to zero after four calls that moved money twice.
        Assert.Equal(0, await harness.LedgerSumAsync());
    }

    [Fact]
    public async Task A_payout_larger_than_the_balance_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 10_000);

        using var response = await harness.PostAsync(
            "/v1/internal/wallet/driver-payout",
            new { payoutId = Guid.NewGuid(), driverId = driver.Id, amountMinor = 10_001 },
            internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Equal(10_000, await harness.BalanceAsync(driver.Id));
    }

    [Fact]
    public async Task The_payout_kind_cannot_be_posted_through_the_debit_seam()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 10_000);

        using var response = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/debit",
            new { amountMinor = 5_000, kind = "driver_payout", idempotencyKey = $"driver_payout:{Guid.NewGuid()}" },
            internalKey: WalletHarness.InternalApiKey);

        // The whitelist is the boundary: `driver_payout` has its own route, which composes the key
        // from the payout id. A caller free to choose the key is a caller free to pay twice.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(10_000, await harness.BalanceAsync(driver.Id));
    }
}
