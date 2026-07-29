using System.Globalization;
using System.Text.Json;
using Dapper;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Shared.Caching;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// <b>DoD 2 — "a driver below the daily-fee balance is refused from the 2nd trip of the day but
/// allowed the 1st".</b> D5' §2.1 / §9.2's pre-dispatch wallet gate (D-08, US-9.1).
/// </summary>
[Collection<DispatchCollection>]
public sealed class WalletGateTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint Nearest = new(6.9350, 79.8430);

    /// <summary>Rs 100 — <c>billing.plans</c>'s seeded three-wheeler rate (migration 1901).</summary>
    private const long ThreeWheelerDailyFeeMinor = 10_000;

    [Fact]
    public async Task The_first_trip_of_the_day_is_offered_with_an_empty_wallet_and_the_second_is_not()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetWalletBalanceAsync(driver.DriverId, 0);

        // --- trip 1: free, and the balance is not even consulted (US-9.1, D-13) ----------------
        var firstRideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var first = await OfferLoopTests.DispatchAsync(harness, firstRideId);

        Assert.Equal(DispatchResult.Offered, first.Result);
        Assert.Equal(driver.DriverId, first.DriverId);

        using (var breakdown = JsonDocument.Parse((await harness.ReadScoresAsync(firstRideId))[0].Breakdown))
        {
            Assert.True(breakdown.RootElement.GetProperty("walletOk").GetBoolean());
        }

        await CompleteTripAsync(harness, firstRideId, driver.DriverId);

        // --- trip 2: the flat daily fee is now due, and Rs 0 does not cover Rs 100 --------------
        var secondRideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var second = await OfferLoopTests.DispatchAsync(harness, secondRideId);

        Assert.Equal(DispatchResult.NoCandidate, second.Result);
        Assert.Equal(1, second.CandidateCount);
        Assert.Equal(0, second.EligibleCount);

        var row = Assert.Single(await harness.ReadScoresAsync(secondRideId));
        using var refused = JsonDocument.Parse(row.Breakdown);

        Assert.Equal(EligibilityGates.Wallet, refused.RootElement.GetProperty("rejectedBy").GetString());
        Assert.False(refused.RootElement.GetProperty("walletOk").GetBoolean());
    }

    [Fact]
    public async Task A_driver_who_can_cover_the_daily_fee_keeps_taking_trips()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetWalletBalanceAsync(driver.DriverId, ThreeWheelerDailyFeeMinor);

        var firstRideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        Assert.Equal(DispatchResult.Offered, (await OfferLoopTests.DispatchAsync(harness, firstRideId)).Result);

        await CompleteTripAsync(harness, firstRideId, driver.DriverId);

        var secondRideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var second = await OfferLoopTests.DispatchAsync(harness, secondRideId);

        Assert.Equal(DispatchResult.Offered, second.Result);
        Assert.Equal(driver.DriverId, second.DriverId);
    }

    /// <summary>
    /// Exactly at the fee, and one minor unit short of it. The boundary is where a gate is either
    /// right or expensive.
    /// </summary>
    [Theory]
    [InlineData(ThreeWheelerDailyFeeMinor, true)]
    [InlineData(ThreeWheelerDailyFeeMinor - 1, false)]
    public async Task The_second_trip_turns_on_whether_the_balance_reaches_the_tier_rate(
        long balanceMinor, bool offered)
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetWalletBalanceAsync(driver.DriverId, balanceMinor);

        var firstRideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        await OfferLoopTests.DispatchAsync(harness, firstRideId);
        await CompleteTripAsync(harness, firstRideId, driver.DriverId);

        var second = await OfferLoopTests.DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        Assert.Equal(offered ? DispatchResult.Offered : DispatchResult.NoCandidate, second.Result);
    }

    /// <summary>
    /// US-9.4: "single flat charge regardless of trip count". A driver who has already paid today
    /// takes trip 3 with an empty wallet.
    /// </summary>
    [Fact]
    public async Task A_driver_who_already_paid_today_is_not_asked_for_it_again()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetWalletBalanceAsync(driver.DriverId, 0);

        var firstRideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        await OfferLoopTests.DispatchAsync(harness, firstRideId);
        await CompleteTripAsync(harness, firstRideId, driver.DriverId);

        // What subscription-svc (C047) writes when it charges the fee on an accept.
        await using (var connection = await harness.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO billing.daily_fee_charges (driver_id, vehicle_id, amount_minor, status, trips_that_day)
                VALUES (@DriverId, @VehicleId, @AmountMinor, 'PAID', 1);
                """,
                new { driver.DriverId, driver.VehicleId, AmountMinor = (int)ThreeWheelerDailyFeeMinor });
        }

        var second = await OfferLoopTests.DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        Assert.Equal(DispatchResult.Offered, second.Result);
        Assert.Equal(driver.DriverId, second.DriverId);
    }

    /// <summary>
    /// A <c>WAIVED_FIRST_TRIP</c> row moved no money (migration 1103's own CHECK), so it must not
    /// read as "already charged" — otherwise the free first trip would silently buy the whole day.
    /// </summary>
    [Fact]
    public async Task A_waived_first_trip_charge_does_not_pay_for_the_second()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetWalletBalanceAsync(driver.DriverId, 0);

        var firstRideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        await OfferLoopTests.DispatchAsync(harness, firstRideId);
        await CompleteTripAsync(harness, firstRideId, driver.DriverId);

        await using (var connection = await harness.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO billing.daily_fee_charges (driver_id, vehicle_id, amount_minor, status, trips_that_day)
                VALUES (@DriverId, @VehicleId, 0, 'WAIVED_FIRST_TRIP', 1);
                """,
                new { driver.DriverId, driver.VehicleId });
        }

        Assert.Equal(
            DispatchResult.NoCandidate,
            (await OfferLoopTests.DispatchAsync(
                harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()))).Result);
    }

    /// <summary>
    /// D-08's cache: the gate reads <c>wallet:bal:{driverId}</c> first and populates it read-through
    /// on a miss, with the 5 s TTL D5' §9.2 gives it.
    /// </summary>
    [Fact]
    public async Task The_gate_reads_and_populates_the_five_second_wallet_cache()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetWalletBalanceAsync(driver.DriverId, ThreeWheelerDailyFeeMinor);

        var key = RedisKeys.WalletBalance(driver.DriverId);
        Assert.False(await harness.Redis.GetDatabase().KeyExistsAsync(key));

        await OfferLoopTests.DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        // Written through from billing.wallets, with the documented TTL.
        var cached = await harness.Redis.GetDatabase().StringGetAsync(key);
        Assert.Equal(ThreeWheelerDailyFeeMinor, long.Parse(cached.ToString(), CultureInfo.InvariantCulture));

        var ttl = await harness.Redis.GetDatabase().KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The cache is what the gate reads, so a value in it decides — which is the whole point of
    /// D-08 and of wallet-svc's debit invalidation.
    /// </summary>
    [Fact]
    public async Task A_cached_balance_is_what_the_second_trip_is_judged_on()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        // The durable read model says the driver is broke; the cache says they topped up. wallet-svc
        // (C046) writes the second on `wallet.credited`, ahead of its own projection catching up.
        await harness.SetWalletBalanceAsync(driver.DriverId, 0);

        await harness.Redis.GetDatabase().StringSetAsync(
            RedisKeys.WalletBalance(driver.DriverId),
            ThreeWheelerDailyFeeMinor.ToString(CultureInfo.InvariantCulture),
            TimeSpan.FromMinutes(1));

        var firstRideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        await OfferLoopTests.DispatchAsync(harness, firstRideId);
        await CompleteTripAsync(harness, firstRideId, driver.DriverId);

        var second = await OfferLoopTests.DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        Assert.Equal(DispatchResult.Offered, second.Result);
    }

    /// <summary>
    /// Migration 1901 leaves <c>truck</c> and <c>mini_truck</c> without a rate on purpose — "no
    /// default row, so a delivery vehicle cannot go online until Finance sets one". Inventing one
    /// here would quietly overrule that, so the second trip is refused instead.
    /// </summary>
    [Fact]
    public async Task A_tier_with_no_billing_plan_is_refused_from_the_second_trip()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest, vehicleType: "mini_truck");
        await harness.SetWalletBalanceAsync(driver.DriverId, 10_000_000);

        // Booked as deliveries: `mini_truck` carries parcels, not people (AL-09), and ride-svc
        // enforces that since Δ C037. The daily fee does not care which it was — P-06 counts
        // deliveries and passenger rides together — so the gate under test is unchanged.
        var firstRideId = await harness.RequestRideAsync(
            await harness.CreatePassengerAsync(), vehicleType: "mini_truck", packageSize: "M");

        Assert.Equal(DispatchResult.Offered, (await OfferLoopTests.DispatchAsync(harness, firstRideId)).Result);

        await CompleteTripAsync(harness, firstRideId, driver.DriverId);

        var secondRideId = await harness.RequestRideAsync(
            await harness.CreatePassengerAsync(), vehicleType: "mini_truck", packageSize: "M");

        Assert.Equal(DispatchResult.NoCandidate, (await OfferLoopTests.DispatchAsync(harness, secondRideId)).Result);

        using var breakdown = JsonDocument.Parse((await harness.ReadScoresAsync(secondRideId))[0].Breakdown);
        Assert.Equal(EligibilityGates.Wallet, breakdown.RootElement.GetProperty("rejectedBy").GetString());
    }

    /// <summary>The gate can be switched off, and then it is off rather than always passing.</summary>
    [Fact]
    public async Task With_the_gate_disabled_a_broke_driver_takes_a_second_trip()
    {
        await using var harness = await StartAsync(
            new Dictionary<string, string?> { ["Dispatch:WalletGateEnabled"] = "false" });

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetWalletBalanceAsync(driver.DriverId, 0);

        var firstRideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        await OfferLoopTests.DispatchAsync(harness, firstRideId);
        await CompleteTripAsync(harness, firstRideId, driver.DriverId);

        var secondRideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        Assert.Equal(DispatchResult.Offered, (await OfferLoopTests.DispatchAsync(harness, secondRideId)).Result);

        // And the audit says the gate did not run, rather than that it passed.
        using var breakdown = JsonDocument.Parse((await harness.ReadScoresAsync(secondRideId))[0].Breakdown);
        Assert.True(breakdown.RootElement.GetProperty("walletOk").GetBoolean());
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Takes the driver through one whole trip, exactly as <c>IRideEventHandler</c> would on
    /// <c>ride.accepted</c> and then <c>ride.completed</c>: the offer becomes ACCEPTED — which is
    /// what D5' §2.2's <c>tripsToday</c> counts — and is then released so R-10's partial unique
    /// index stops holding the driver (migration 0712).
    /// </summary>
    private static async Task CompleteTripAsync(DispatchHarness harness, Guid rideId, Guid driverId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

        await dispatch.MarkAcceptedAsync(rideId, driverId, TestContext.Current.CancellationToken);
        await dispatch.ReturnToPoolAsync(driverId, TestContext.Current.CancellationToken);
    }

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }
}
