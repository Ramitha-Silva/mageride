using System.Net;
using MageRide.Shared.Time;
using MageRide.Subscriptions.Domain;
using MageRide.Subscriptions.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Subscriptions.Tests.Integration;

/// <summary>
/// The definition of done, item by item: debit once, first trip free, Mode A never charged.
/// </summary>
/// <remarks>
/// Every assertion here is against the two databases the guarantee actually lives in —
/// <c>billing.daily_fee_charges</c> for the record and <c>billing.accounts</c> /
/// <c>billing.journal_entries</c> for the money, the second of which belongs to a wallet-svc this
/// suite boots for real.
/// </remarks>
[Collection<SubscriptionCollection>]
public sealed class DailyFeeChargeTests(PostgresFixture postgres, RedisFixture redis)
{
    private const long ThreeWheelerRate = 10_000;

    /// <summary>DoD: "the first trip of a day never charges."</summary>
    [Fact]
    public async Task First_trip_of_the_colombo_day_is_free_and_takes_no_money()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        var charge = await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        Assert.Equal(FeeStatuses.WaivedFirstTrip, charge.Status);
        Assert.Equal(0, charge.AmountMinor);
        Assert.Equal(0, charge.TripsThatDay);

        // No wallet check at all on the first trip (US-9.1): the balance is untouched and there is no
        // entry, so a driver with an empty wallet would have been allowed through just the same.
        Assert.Equal(100_000, await harness.BalanceAsync(driver.Id));
        Assert.Equal(0, await harness.EntryCountAsync("daily_fee"));
    }

    /// <summary>DoD: "the second charges the vehicle-type rate."</summary>
    [Fact]
    public async Task Second_trip_of_the_colombo_day_charges_the_vehicle_type_rate()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        // The driver's first trip happened. The second accept is what the fee falls due before.
        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());

        var charge = await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        Assert.Equal(FeeStatuses.Paid, charge.Status);
        Assert.Equal(ThreeWheelerRate, charge.AmountMinor);
        Assert.Equal(1, charge.TripsThatDay);

        Assert.Equal(100_000 - ThreeWheelerRate, await harness.BalanceAsync(driver.Id));
        Assert.Equal(1, await harness.EntryCountAsync("daily_fee"));

        // Double entry: the driver's debit and the platform's credit sum to zero (D-09).
        Assert.Equal(0, await harness.LedgerSumAsync());

        // One row per (driver, vehicle, Colombo day) — the waiver was upgraded in place, not doubled.
        var rows = await harness.ChargeRowsAsync(driver.Id);
        Assert.Single(rows);
        Assert.Equal(FeeStatuses.Paid, rows[0].Status);
    }

    /// <summary>DoD: "charging twice on the same (driver, vehicle, Asia/Colombo date) debits once."</summary>
    [Fact]
    public async Task Charging_twice_on_one_colombo_day_debits_once()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.ChargeOkAsync(driver.Id, vehicle.Id);
        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());

        var first = await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        // Trips three, four and five. Each one asks again; none of them takes anything.
        for (var trip = 0; trip < 3; trip++)
        {
            await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());

            var repeat = await harness.ChargeOkAsync(driver.Id, vehicle.Id);

            Assert.Equal(FeeStatuses.Paid, repeat.Status);
            Assert.Equal(ThreeWheelerRate, repeat.AmountMinor);
            Assert.Equal(first.ChargedAt, repeat.ChargedAt);
        }

        Assert.Equal(100_000 - ThreeWheelerRate, await harness.BalanceAsync(driver.Id));
        Assert.Single(await harness.ChargeRowsAsync(driver.Id));

        var feeDate = BusinessCalendar.BusinessDate(harness.Clock.GetUtcNow());
        Assert.Equal(1, await harness.DailyFeeEntryCountAsync(driver.Id, vehicle.Id, feeDate));
    }

    /// <summary>
    /// The same claim under concurrency, which is the case the two indexes exist for.
    /// </summary>
    /// <remarks>
    /// Two replicas can decide to charge at the same instant — nothing serialises the decision. What
    /// makes the answer one deduction is the UNIQUE on
    /// <c>billing.journal_entries.idempotency_key</c>, and it lives in wallet-svc, which is why this
    /// suite boots the real one.
    /// </remarks>
    [Fact]
    public async Task Concurrent_charges_for_one_colombo_day_take_the_fee_once()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());

        var attempts = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => harness.ChargeAsync(driver.Id, vehicle.Id)));

        // The BODY, not just the status — the same courtesy `SubscriptionHarness.ChargeOkAsync`
        // already extends. A bare `Assert.Equal(OK, actual)` on a six-way race reports
        // "Expected: OK, Actual: InternalServerError" and throws the RFC 7807 problem details
        // away, which is the one thing that says which of the concurrent paths lost.
        foreach (var attempt in attempts)
        {
            var body = await attempt.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(
                attempt.StatusCode == HttpStatusCode.OK,
                $"one of six concurrent charges returned {(int)attempt.StatusCode}: {body}");
            attempt.Dispose();
        }

        Assert.Equal(100_000 - ThreeWheelerRate, await harness.BalanceAsync(driver.Id));
        Assert.Equal(1, await harness.EntryCountAsync("daily_fee"));
        Assert.Equal(0, await harness.LedgerSumAsync());
    }

    /// <summary>DoD: "a Mode A vehicle is never charged a daily fee."</summary>
    [Theory]
    [InlineData("bus")]
    [InlineData("train")]
    public async Task Mode_a_vehicles_are_never_charged(string vehicleType)
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id, vehicleType, mode: "A");

        // Well past the free first trip — a Mode A vehicle is free on its twentieth trip too.
        for (var trip = 0; trip < 4; trip++)
        {
            await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());

            var charge = await harness.ChargeOkAsync(driver.Id, vehicle.Id);
            Assert.Equal(0, charge.AmountMinor);
        }

        Assert.Equal(100_000, await harness.BalanceAsync(driver.Id));
        Assert.Equal(0, await harness.EntryCountAsync("daily_fee"));
    }

    /// <summary>
    /// A driver who switches vehicles mid-day gets one free trip, not one per vehicle.
    /// </summary>
    /// <remarks>
    /// D5' §2.2 counts trips <em>per driver</em> and keys the charge row per (driver, vehicle, day), so
    /// US-9.6's "the driver pays the daily payment per vehicle" holds without the waiver being
    /// re-earnable: the second vehicle's first trip is the driver's second trip of the day.
    /// </remarks>
    [Fact]
    public async Task Switching_vehicles_does_not_buy_a_second_free_trip()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var threeWheeler = await harness.Seed.VehicleAsync(driver.Id);
        var motorbike = await harness.Seed.VehicleAsync(driver.Id, "motorbike");

        var waived = await harness.ChargeOkAsync(driver.Id, threeWheeler.Id);
        Assert.Equal(FeeStatuses.WaivedFirstTrip, waived.Status);

        await harness.Seed.RideAsync(driver.Id, threeWheeler.Id, harness.Clock.GetUtcNow());

        var charged = await harness.ChargeOkAsync(driver.Id, motorbike.Id);

        Assert.Equal(FeeStatuses.Paid, charged.Status);
        Assert.Equal(5_000, charged.AmountMinor);
        Assert.Equal(100_000 - 5_000, await harness.BalanceAsync(driver.Id));
    }

    /// <summary>
    /// A ride cancelled after acceptance still counts, so the free trip cannot be farmed.
    /// </summary>
    /// <remarks>
    /// 0712 records the end of an accepted offer's liveness in <c>released_at</c> and leaves the
    /// status at <c>ACCEPTED</c>, so the driver's answer survives what the rider did next.
    /// </remarks>
    [Fact]
    public async Task A_ride_cancelled_after_accept_still_counts_as_the_free_trip()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.Seed.RideAsync(
            driver.Id, vehicle.Id, harness.Clock.GetUtcNow(), state: "CancelledByRiderAfterAccept");

        var charge = await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        Assert.Equal(FeeStatuses.Paid, charge.Status);
        Assert.Equal(ThreeWheelerRate, charge.AmountMinor);
    }

    /// <summary>
    /// An offer the driver never accepted is not a trip — a declined or expired cascade costs
    /// nothing.
    /// </summary>
    [Theory]
    [InlineData("DECLINED")]
    [InlineData("EXPIRED")]
    public async Task An_offer_the_driver_did_not_accept_does_not_use_up_the_free_trip(string offerStatus)
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        // A whole cascade of offers this driver let go past.
        for (var offer = 0; offer < 3; offer++)
        {
            await harness.Seed.RideAsync(
                driver.Id, vehicle.Id, harness.Clock.GetUtcNow(),
                state: "ExpiredNoDriver", offerStatus: offerStatus);
        }

        var charge = await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        Assert.Equal(FeeStatuses.WaivedFirstTrip, charge.Status);
        Assert.Equal(100_000, await harness.BalanceAsync(driver.Id));
    }

    /// <summary>
    /// The ride being accepted is excluded, so the first accept of the day is free either way.
    /// </summary>
    /// <remarks>
    /// D3' §325 has ride-svc charge <em>after</em> the conditional accept lands, so the offer being
    /// paid for is already <c>ACCEPTED</c> when the call arrives. Without the exclusion the very first
    /// trip of every day would be charged.
    /// </remarks>
    [Fact]
    public async Task The_ride_being_accepted_is_excluded_from_the_trip_count()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        var rideId = await harness.Seed.RideAsync(
            driver.Id, vehicle.Id, harness.Clock.GetUtcNow(), state: "Accepted", live: true);

        var charge = await harness.ChargeOkAsync(driver.Id, vehicle.Id, rideId);

        Assert.Equal(FeeStatuses.WaivedFirstTrip, charge.Status);
        Assert.Equal(100_000, await harness.BalanceAsync(driver.Id));
    }

    /// <summary>
    /// Not naming the ride charges the accept it is being asked about — which is exactly why
    /// <c>rideId</c> is in the body.
    /// </summary>
    /// <remarks>
    /// A caller that omits it is telling this service the accept has not landed yet. If it has, the
    /// driver's free trip is spent on the very trip it should have covered — so the omission is a
    /// choice ride-svc makes deliberately, and the two orderings are asserted side by side here.
    /// </remarks>
    [Fact]
    public async Task Omitting_the_ride_id_counts_the_accept_that_has_already_landed()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.Seed.RideAsync(
            driver.Id, vehicle.Id, harness.Clock.GetUtcNow(), state: "Accepted", live: true);

        var charge = await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        Assert.Equal(FeeStatuses.Paid, charge.Status);
        Assert.Equal(ThreeWheelerRate, charge.AmountMinor);
    }

    /// <summary>
    /// A new Colombo day is a new fee — and the previous day's row is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task A_new_colombo_day_charges_again()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        harness.Clock.Advance(TimeSpan.FromDays(1));

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        Assert.Equal(100_000 - (2 * ThreeWheelerRate), await harness.BalanceAsync(driver.Id));

        var rows = await harness.ChargeRowsAsync(driver.Id);
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].FeeDate, rows[1].FeeDate);
    }

    /// <summary>
    /// The day boundary is Colombo's, not UTC's.
    /// </summary>
    /// <remarks>
    /// Colombo is UTC+5:30, so its day rolls over five and a half hours <em>before</em> UTC's: at
    /// 19:30 UTC on the 30th it is already 01:00 on the 31st in Colombo. A service that keyed the fee
    /// on the UTC date would give that driver a second free trip and then charge them again five hours
    /// later, on the same working night (D-38, and <c>BusinessCalendar</c>'s own remarks).
    /// </remarks>
    [Fact]
    public async Task The_day_boundary_is_colombo_midnight_not_utc_midnight()
    {
        // 20:00 Colombo on the 30th.
        await using var harness = await SubscriptionHarness.StartAsync(
            postgres, redis, now: new DateTimeOffset(2026, 7, 30, 14, 30, 0, TimeSpan.Zero));

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        var first = await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        Assert.Equal(new DateOnly(2026, 7, 30), first.FeeDate);
        Assert.Equal(ThreeWheelerRate, first.AmountMinor);

        // 23:00 Colombo — still the 30th, still one fee.
        harness.Clock.Advance(TimeSpan.FromHours(3));

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        var same = await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        Assert.Equal(new DateOnly(2026, 7, 30), same.FeeDate);
        Assert.Equal(100_000 - ThreeWheelerRate, await harness.BalanceAsync(driver.Id));

        // 01:00 Colombo on the 31st. UTC is still on the 30th — this is the case the whole test is for.
        harness.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(30, harness.Clock.GetUtcNow().UtcDateTime.Day);

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        var next = await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        Assert.Equal(new DateOnly(2026, 7, 31), next.FeeDate);
        Assert.Equal(100_000 - (2 * ThreeWheelerRate), await harness.BalanceAsync(driver.Id));
    }

    /// <summary>
    /// A driver who cannot cover the fee is refused with the code the app branches on (US-9.1).
    /// </summary>
    [Fact]
    public async Task A_driver_who_cannot_cover_the_fee_is_refused_with_insufficient_wallet()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 5_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());

        using var response = await harness.ChargeAsync(driver.Id, vehicle.Id);

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);

        var (code, _) = await SubscriptionHarness.ProblemAsync(response);
        Assert.Equal("insufficient-wallet", code);

        // Nothing was written: no row claiming the day is settled, and no money moved.
        Assert.Equal(5_000, await harness.BalanceAsync(driver.Id));
        Assert.Empty(await harness.ChargeRowsAsync(driver.Id));
    }

    /// <summary>
    /// A vehicle type Finance has not priced cannot go online (§20's <c>truck</c> / <c>mini_truck</c>).
    /// </summary>
    [Fact]
    public async Task A_vehicle_type_with_no_configured_rate_is_refused_rather_than_guessed()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var truck = await harness.Seed.VehicleAsync(driver.Id, "truck");

        await harness.Seed.RideAsync(driver.Id, truck.Id, harness.Clock.GetUtcNow());

        using var response = await harness.ChargeAsync(driver.Id, truck.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(100_000, await harness.BalanceAsync(driver.Id));
    }

    /// <summary>The internal plane is unmappable without the key, exactly as the gateway answers.</summary>
    [Fact]
    public async Task The_internal_plane_is_a_404_without_the_key()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync();
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        using var unkeyed = await harness.PostAsync(
            $"/v1/internal/fees/{driver.Id}/charge-before-trip", new { vehicleId = vehicle.Id.ToString() });

        Assert.Equal(HttpStatusCode.NotFound, unkeyed.StatusCode);

        using var wrongKey = await harness.PostAsync(
            $"/v1/internal/fees/{driver.Id}/charge-before-trip",
            new { vehicleId = vehicle.Id.ToString() },
            internalKey: "not-the-key");

        Assert.Equal(HttpStatusCode.NotFound, wrongKey.StatusCode);
        Assert.Empty(await harness.ChargeRowsAsync(driver.Id));
    }

    /// <summary>
    /// A driver's own bearer does not reach the internal plane either. It is not an authenticated
    /// surface — it is an unmapped one.
    /// </summary>
    [Fact]
    public async Task A_driver_bearer_does_not_open_the_internal_plane()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync();
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        using var response = await harness.PostAsync(
            $"/v1/internal/fees/{driver.Id}/charge-before-trip",
            new { vehicleId = vehicle.Id.ToString() },
            bearer: driver.Bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// With wallet-svc unconfigured the charge is refused, not silently skipped.
    /// </summary>
    /// <remarks>
    /// The alternative — answering 200 with "nothing charged" — would cost the platform its only
    /// revenue while every dashboard stayed green. A 503 fails the accept, which is loud.
    /// </remarks>
    [Fact]
    public async Task Without_wallet_svc_the_charge_is_refused_rather_than_skipped()
    {
        await using var harness = await SubscriptionHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Subscription:WalletBaseUrl"] = null,
                ["Subscription:WalletInternalApiKey"] = null,
            });

        var driver = await harness.Seed.DriverAsync();
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());

        using var response = await harness.ChargeAsync(driver.Id, vehicle.Id);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(await harness.ChargeRowsAsync(driver.Id));
    }
}
