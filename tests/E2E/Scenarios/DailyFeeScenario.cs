using MageRide.E2E.Infrastructure;
using MageRide.Shared.Time;
using MageRide.Subscriptions.Domain;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// D-13 — the daily platform fee: first trip free, charged before the second, once per Colombo day.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every trip in here is a real ride.</b> D5' §2.2 counts "completed+accepted today for driver",
/// and on this platform that is an <c>ACCEPTED</c> row in <c>dispatch.offers</c> with its
/// <c>responded_at</c> inside the Colombo day — written by dispatch-svc's own <c>ride.events</c>
/// consumer after a driver taps Accept. Both readers of that number are running here (dispatch-svc's
/// D-08 pre-dispatch gate and subscription-svc's charge), which is the point: the gate exists to
/// <em>predict</em> this charge, so a suite that wrote the offer rows by hand would be testing
/// neither of them.
/// </para>
/// <para>
/// <b>Every rupee a driver holds arrived through a rail the platform has.</b> These drivers are
/// created with an empty wallet and top up through OnePay — session, acquirer, signed callback — so
/// "the fee came out of the driver's balance" is a statement about money that was paid in rather
/// than about a number a fixture wrote.
/// </para>
/// <para>
/// <b>Nothing in the platform calls the charge.</b> subscription-svc's own route documentation says
/// D3' §325 has ride-svc call it "immediately after the conditional <c>UPDATE … AND version = :v</c>
/// that wins the offer", and ride-svc has no subscription client, no fee options and no such hop —
/// so the platform's only per-driver revenue line is never collected. That is a C123 finding, it is
/// asserted as a gap in <see cref="MoneyLedgerCoverage"/>, and every scenario here drives the charge
/// through the same internal route ride-svc would use.
/// </para>
/// </remarks>
[Collection<MoneyCollection>]
[Trait("Category", "Money")]
public sealed class DailyFeeScenario(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    : MoneyScenario(postgres, redis, redpanda)
{
    /// <summary>US-9.1 — the first trip of the day is free, and no wallet is even consulted.</summary>
    /// <remarks>
    /// The absence is the assertion, and it is made three ways: the row says
    /// <c>WAIVED_FIRST_TRIP</c> for zero, the driver has no ledger account at all, and no entry
    /// exists under the day's key. A driver whose wallet was checked and found empty would look
    /// identical on the first of those and different on the other two — which is exactly the bug
    /// D5' §2.2's "no wallet check (US-9.1)" is written to prevent.
    /// </remarks>
    [Fact]
    public Task The_first_trip_of_a_Colombo_day_is_free_and_no_wallet_is_consulted() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            var ride = await fleet.StartTripAsync(driver);
            var charge = await fleet.ChargeDailyFeeAsync(driver, ride.RideId);

            Assert.Equal("WAIVED_FIRST_TRIP", charge.GetProperty("status").GetString());
            Assert.Equal(0, charge.GetProperty("amountMinor").GetInt64());
            Assert.Equal(0, charge.GetProperty("tripsThatDay").GetInt32());

            var today = BusinessCalendar.BusinessDate(DateTimeOffset.UtcNow);
            var row = await fleet.ReadDailyFeeAsync(driver.DriverId, driver.VehicleId, today);

            Assert.True(row is not null, "The waiver is recorded, so that 'no row' keeps meaning 'not charged yet today'.");
            Assert.Equal("WAIVED_FIRST_TRIP", row!.Status);
            Assert.Equal(0, row.AmountMinor);

            // D-38: the business date carries an Asia/Colombo audit companion, and the two must
            // resolve to the same day. A UTC-stamped companion beside a Colombo date is the failure
            // that only shows up for the 5½ hours a day when they disagree.
            Assert.Equal(today, BusinessCalendar.BusinessDate(row.FeeDateTzAt));

            Assert.Null(await fleet.ReadAccountAsync("driver", driver.DriverId));
            Assert.Null(await fleet.ReadEntryAsync(DailyFeeRule.LedgerKey(driver.DriverId, driver.VehicleId, today)));
        });

    /// <summary>
    /// US-9.4 / D5' §2.1 — the second trip is charged the vehicle type's flat rate, once.
    /// </summary>
    /// <remarks>
    /// Rs 100 for a three-wheeler, and it is asserted as a number rather than read back from
    /// <c>billing.plans</c>: the rate table is seeded by migration 1901 and is admin-editable, so a
    /// test that read it would agree with whatever it found — including with a rate some other
    /// suite's <c>PUT /v1/admin/fees/rates</c> had changed. D5' §2.1 prints the seven tiers, and if
    /// the seed moves away from them this is what says so.
    /// </remarks>
    [Fact]
    public Task The_second_trip_of_a_Colombo_day_is_charged_the_vehicle_types_flat_rate() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            await fleet.TopUpAsync(driver, 50_000, $"c123-fee-{Guid.NewGuid():N}");

            var first = await fleet.StartTripAsync(driver);
            await fleet.ChargeDailyFeeAsync(driver, first.RideId);
            await fleet.FinishTripAsync(first);

            var balanceBefore = await fleet.BalanceOfAsync(driver.DriverId);
            var platformBefore = (await fleet.ReadPlatformAccountAsync()).BalanceMinor;

            var second = await fleet.StartTripAsync(driver);
            var charge = await fleet.ChargeDailyFeeAsync(driver, second.RideId);

            Assert.Equal("PAID", charge.GetProperty("status").GetString());
            Assert.Equal(MoneyFleet.ThreeWheelerDailyFeeMinor, charge.GetProperty("amountMinor").GetInt64());
            Assert.Equal("LKR", charge.GetProperty("currency").GetString());

            // One trip already taken — the ride being accepted is excluded, which is what stops the
            // first trip of every day arriving as `tripsToday = 1` and being charged.
            Assert.Equal(1, charge.GetProperty("tripsThatDay").GetInt32());

            Assert.Equal(
                balanceBefore - MoneyFleet.ThreeWheelerDailyFeeMinor,
                await fleet.BalanceOfAsync(driver.DriverId));

            // Double entry: what left the driver arrived at the platform. This is the platform's own
            // revenue line, and it is the half a single-sided "the driver was debited" assertion
            // would never notice going missing.
            Assert.Equal(
                platformBefore + MoneyFleet.ThreeWheelerDailyFeeMinor,
                (await fleet.ReadPlatformAccountAsync()).BalanceMinor);

            var today = BusinessCalendar.BusinessDate(DateTimeOffset.UtcNow);
            var entry = await fleet.ReadEntryAsync(
                DailyFeeRule.LedgerKey(driver.DriverId, driver.VehicleId, today));

            Assert.True(
                entry is not null,
                "The fee is keyed on daily_fee:{driverId}:{vehicleId}:{feeDate} — C005 decision 4, and a "
                + "cross-service contract. If it is not under that key it is not idempotent with anything.");

            Assert.Equal("daily_fee", entry!.Kind);
            Assert.Equal(0, entry.SumMinor);
            Assert.Equal(2, entry.Legs.Count);

            var account = await fleet.ReadAccountAsync("driver", driver.DriverId);
            Assert.Equal(-MoneyFleet.ThreeWheelerDailyFeeMinor, entry.For(account!.AccountId)!.AmountMinor);

            // The mirror dispatch-svc's D-08 gate falls back to when Redis misses.
            Assert.Equal(account.BalanceMinor, account.MirrorMinor);
        });

    /// <summary>
    /// D5' §2.1 — a single flat charge regardless of trip count. The third trip costs nothing more.
    /// </summary>
    /// <remarks>
    /// Two guards make this true and they guard different things: <c>billing.daily_fee_charges</c>'s
    /// composite primary key stops a second <em>row</em>, and the ledger key's UNIQUE index stops the
    /// <em>money</em> moving twice. Only the second is load-bearing — two replicas can decide to
    /// charge at the same instant and nothing serialises the decision — so the assertion that matters
    /// is the entry count under the day's key, not the row.
    /// </remarks>
    [Fact]
    public Task A_third_trip_on_the_same_Colombo_day_charges_nothing_more() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            await fleet.TopUpAsync(driver, 50_000, $"c123-fee-{Guid.NewGuid():N}");

            var first = await fleet.StartTripAsync(driver);
            await fleet.ChargeDailyFeeAsync(driver, first.RideId);
            await fleet.FinishTripAsync(first);

            var second = await fleet.StartTripAsync(driver);
            await fleet.ChargeDailyFeeAsync(driver, second.RideId);
            await fleet.FinishTripAsync(second);

            var afterSecond = await fleet.BalanceOfAsync(driver.DriverId);

            var third = await fleet.StartTripAsync(driver);
            var charge = await fleet.ChargeDailyFeeAsync(driver, third.RideId);

            Assert.Equal("PAID", charge.GetProperty("status").GetString());
            Assert.Equal(afterSecond, await fleet.BalanceOfAsync(driver.DriverId));

            var fees = await fleet.ReadEntriesForAsync("driver", driver.DriverId, "daily_fee");

            Assert.True(
                fees.Count == 1,
                $"The driver took three trips today and the ledger carries {fees.Count} daily_fee entries. "
                + "D5' §2.1 is a single flat charge regardless of trip count: "
                + string.Join(", ", fees.Select(fee => fee.IdempotencyKey)));

            // `charged_at` is not moved by the third trip either: the already-charged branch returns
            // the row untouched, so a settled day's record does not drift every time somebody drives.
            var row = await fleet.ReadDailyFeeAsync(
                driver.DriverId, driver.VehicleId, BusinessCalendar.BusinessDate(DateTimeOffset.UtcNow));

            Assert.Equal(MoneyFleet.ThreeWheelerDailyFeeMinor, row!.AmountMinor);
        });

    /// <summary>
    /// US-9.1 — a driver who cannot pay is refused, and nothing is taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two refusals, and the platform makes the first one before this suite can ask for it.</b>
    /// D-08's gate exists so that a driver is never offered a ride whose fee they cannot pay, and it
    /// fires here for real: the second ride is booked, dispatch-svc builds candidates, the gate
    /// reads Rs 50 against a Rs 100 fee, and the ride rests in <c>Matching</c> having been offered to
    /// nobody. <b>That was found by writing this test the obvious way</b> — as "take a second trip
    /// and watch the charge fail" — and waiting sixty seconds for an offer the platform was right not
    /// to make.
    /// </para>
    /// <para>
    /// The second refusal is the charge route's own, reached the way subscription-svc's own
    /// documentation says an out-of-band caller reaches it: with no ride to exclude from the count.
    /// <c>402 insufficient-wallet</c> travels out of wallet-svc's non-negativity rule and through
    /// subscription-svc unchanged, and carrying the code is the point — it is the D-08 gate's own
    /// answer arriving late, and the driver's app branches on it (US-9.1). Reshaped into a
    /// <c>503</c> it would look like an outage they could wait out instead of a balance they have to
    /// top up.
    /// </para>
    /// </remarks>
    [Fact]
    public Task A_driver_who_cannot_pay_the_fee_is_neither_offered_a_ride_nor_charged() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            // Rs 50 — a real top-up, and deliberately less than the three-wheeler's Rs 100 fee.
            await fleet.TopUpAsync(driver, 5_000, $"c123-short-{Guid.NewGuid():N}");

            var first = await fleet.StartTripAsync(driver);
            await fleet.ChargeDailyFeeAsync(driver, first.RideId);
            await fleet.FinishTripAsync(first);

            var before = await fleet.BalanceOfAsync(driver.DriverId);

            // D-08: the driver is on standby at this pickup and is the only candidate for it. The
            // ride still finds nobody.
            var withheld = await fleet.RequestRideAsync(await fleet.CreatePassengerAsync(), driver);

            await fleet.WaitForRideStateAsync(withheld.RideId, "Matching");

            Assert.False(
                await fleet.HasOfferAsync(withheld.RideId),
                "D-08's gate withholds an offer from a driver who could not pay the daily fee for it. An "
                + "offer here means the gate passed a driver whose accept would then fail with a 402, "
                + "which is the mispredicted charge D5' §9.2 exists to prevent.");

            // The charge itself, asked directly. No rideId to exclude: this driver's one trip today
            // is behind them, so the rule reaches the wallet and the wallet refuses.
            using var refused = await fleet.TryChargeDailyFeeAsync(driver);

            Assert.Equal(System.Net.HttpStatusCode.PaymentRequired, refused.StatusCode);
            Assert.Equal("insufficient-wallet", await MoneyFleet.ProblemCodeAsync(refused));

            Assert.Equal(before, await fleet.BalanceOfAsync(driver.DriverId));

            var today = BusinessCalendar.BusinessDate(DateTimeOffset.UtcNow);

            Assert.Null(await fleet.ReadEntryAsync(
                DailyFeeRule.LedgerKey(driver.DriverId, driver.VehicleId, today)));

            // The day's record still says WAIVED_FIRST_TRIP. A refused charge must not upgrade the
            // row to PAID — a driver marked paid who paid nothing is the failure no retry repairs,
            // because the row then says the day is settled.
            var row = await fleet.ReadDailyFeeAsync(driver.DriverId, driver.VehicleId, today);
            Assert.Equal("WAIVED_FIRST_TRIP", row!.Status);
        });

    /// <summary>
    /// D-13 — the charge is idempotent on the Colombo day, and a different day is a different charge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The idempotency key <em>is</em> the day: <c>daily_fee:{driverId}:{vehicleId}:{feeDate}</c>.
    /// So the property has two halves and they pull in opposite directions — replaying today's key
    /// must move nothing however many times a replica sends it, and a second Colombo day must be a
    /// second entry however soon it arrives. Both are asserted here against the platform's own
    /// composer (<c>DailyFeeRule.LedgerKey</c>) and the platform's own calendar
    /// (<c>BusinessCalendar</c>), so a change to either spelling breaks this test rather than
    /// silently starting to take a second fee.
    /// </para>
    /// <para>
    /// <b>The second day is reached through the ledger seam and not by moving a clock, and that is a
    /// deliberate limit.</b> <c>DailyFeeService</c> stamps the fee date from its own
    /// <c>TimeProvider</c> and no route lets a caller name one, so a scenario can either replace the
    /// service's clock — which would make it a stub of the component under test — or post the key
    /// the service itself would have composed on the other day. The second is what happens here: the
    /// key is <c>DailyFeeRule</c>'s and the route is the one subscription-svc uses, so what is
    /// exercised is the ledger's uniqueness over the real key rather than an assertion invented for
    /// the test. Recorded in the C123 handoff.
    /// </para>
    /// </remarks>
    [Fact]
    public Task The_charge_is_idempotent_on_the_Colombo_day_and_a_new_day_is_a_new_charge() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            await fleet.TopUpAsync(driver, 100_000, $"c123-days-{Guid.NewGuid():N}");

            var first = await fleet.StartTripAsync(driver);
            await fleet.ChargeDailyFeeAsync(driver, first.RideId);
            await fleet.FinishTripAsync(first);

            var second = await fleet.StartTripAsync(driver);
            var charge = await fleet.ChargeDailyFeeAsync(driver, second.RideId);

            var today = BusinessCalendar.BusinessDate(DateTimeOffset.UtcNow);

            // The service's own answer names the Colombo day it charged for, and it is the day the
            // platform's calendar says it is.
            Assert.Equal(today, DateOnly.Parse(charge.GetProperty("feeDate").GetString()!, null));

            var todayKey = DailyFeeRule.LedgerKey(driver.DriverId, driver.VehicleId, today);
            var afterFirstCharge = await fleet.BalanceOfAsync(driver.DriverId);
            var entryId = (await fleet.ReadEntryAsync(todayKey))!.EntryId;

            // Half one: today's key, replayed on the seam subscription-svc itself uses. This is what
            // a second replica deciding to charge at the same instant looks like from the ledger's
            // side, and it must be a no-op that reports the entry the first one wrote.
            using (var replay = await fleet.PostLedgerAsync(
                driver.DriverId,
                "debit",
                MoneyFleet.ThreeWheelerDailyFeeMinor,
                "daily_fee",
                todayKey,
                $"Daily platform fee {today:yyyy-MM-dd} (three_wheeler)"))
            {
                await MoneyFleet.AssertOkAsync(replay, "replaying today's daily-fee key");

                var body = await MoneyFleet.ReadJsonAsync(replay);

                Assert.True(
                    body.GetProperty("replayed").GetBoolean(),
                    "A second post of the day's key must report `replayed`, not write a second entry.");

                Assert.Equal(entryId, body.GetProperty("entryId").GetGuid());
            }

            Assert.Equal(afterFirstCharge, await fleet.BalanceOfAsync(driver.DriverId));

            // Half two: the Colombo day before this one. Same driver, same vehicle, same rate, same
            // kind — only the date in the key differs, and that alone makes it a second charge.
            var yesterdayKey = DailyFeeRule.LedgerKey(
                driver.DriverId, driver.VehicleId, today.AddDays(-1));

            Assert.NotEqual(todayKey, yesterdayKey);

            using (var yesterday = await fleet.PostLedgerAsync(
                driver.DriverId,
                "debit",
                MoneyFleet.ThreeWheelerDailyFeeMinor,
                "daily_fee",
                yesterdayKey,
                $"Daily platform fee {today.AddDays(-1):yyyy-MM-dd} (three_wheeler)"))
            {
                await MoneyFleet.AssertOkAsync(yesterday, "charging the previous Colombo day");

                var body = await MoneyFleet.ReadJsonAsync(yesterday);

                Assert.False(
                    body.GetProperty("replayed").GetBoolean(),
                    "A different Colombo day is a different charge. If this replayed, the key has stopped "
                    + "carrying the date and every driver is billed once and never again.");

                Assert.NotEqual(entryId, body.GetProperty("entryId").GetGuid());
            }

            Assert.Equal(
                afterFirstCharge - MoneyFleet.ThreeWheelerDailyFeeMinor,
                await fleet.BalanceOfAsync(driver.DriverId));

            var fees = await fleet.ReadEntriesForAsync("driver", driver.DriverId, "daily_fee");

            Assert.Equal(2, fees.Count);
            Assert.All(fees, fee => Assert.Equal(0, fee.SumMinor));
        });

    /// <summary>
    /// The trip count is per <b>driver</b>, so a second vehicle does not buy a second free trip.
    /// </summary>
    /// <remarks>
    /// D5' §2.2 counts "completed+accepted today for driver" and US-9.6 gives a driver one live
    /// vehicle at a time — so a driver who switches vehicles mid-day is an ordinary situation, and
    /// counting per vehicle would hand them a free first trip on each. The charge row is still keyed
    /// per <c>(driver, vehicle, day)</c>, which is why the second vehicle produces its own row and
    /// its own entry rather than replaying the first: the waiver is per person, the <em>charge</em>
    /// is per vehicle, and both are true at once.
    /// </remarks>
    [Fact]
    public Task Switching_vehicles_does_not_buy_a_second_free_trip() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            await fleet.TopUpAsync(driver, 50_000, $"c123-switch-{Guid.NewGuid():N}");

            var first = await fleet.StartTripAsync(driver);
            var waived = await fleet.ChargeDailyFeeAsync(driver, first.RideId);

            Assert.Equal("WAIVED_FIRST_TRIP", waived.GetProperty("status").GetString());

            await fleet.FinishTripAsync(first);

            // The same person, a different three-wheeler. Nothing about the second vehicle has been
            // charged for today, and the first trip of the driver's day is already behind them.
            var onSecondVehicle = await fleet.WithAnotherVehicleAsync(driver);

            var ride = await fleet.StartTripAsync(onSecondVehicle);
            var charge = await fleet.ChargeDailyFeeAsync(onSecondVehicle, ride.RideId);

            Assert.Equal("PAID", charge.GetProperty("status").GetString());
            Assert.Equal(MoneyFleet.ThreeWheelerDailyFeeMinor, charge.GetProperty("amountMinor").GetInt64());

            var today = BusinessCalendar.BusinessDate(DateTimeOffset.UtcNow);

            Assert.Equal(
                "WAIVED_FIRST_TRIP",
                (await fleet.ReadDailyFeeAsync(driver.DriverId, driver.VehicleId, today))!.Status);

            Assert.Equal(
                "PAID",
                (await fleet.ReadDailyFeeAsync(driver.DriverId, onSecondVehicle.VehicleId, today))!.Status);
        });
}
