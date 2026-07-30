using MageRide.Query.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.Shared.Time;
using MageRide.TestKit;

namespace MageRide.Query.Tests.Integration;

/// <summary>
/// The driver earnings dashboard (US-9.22) and its per-ride breakdown.
/// </summary>
/// <remarks>
/// The claim under test is R-05: an earning posts only from a terminal payment state, so an in-flight
/// or disputed ride never inflates the dashboard. The gate is read off the <em>ride's</em> state, which
/// is the one predicate that cannot double-count a D-10 retry chain.
/// </remarks>
[Collection(QuerySvcCollection.Name)]
public sealed class EarningsTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint Fort = new(6.9344, 79.8428);
    private static readonly GeoPoint GalleFace = new(6.9271, 79.8449);

    [Fact]
    public async Task Only_rides_in_an_R05_terminal_state_are_counted()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var passenger = await harness.CreateUserAsync();
        var taxi = await harness.CreateVehicleAsync(driver, mode: "C");
        var today = Today();

        // Paid — counts.
        var paid = await Ride(harness, passenger, driver, taxi, "Paid", today);
        await harness.AddPaymentAsync(paid, amountMinor: 40_000);

        // Cash settled — counts, and has no payment row at all, which is the ordinary cash case.
        await Ride(harness, passenger, driver, taxi, "CashSettled", today);

        // Awaiting settlement — R-05's whole point: Completed is not Paid. Exactly one non-terminal
        // ride, and a different passenger: `ux_rides_driver_busy` (O2/R-10) permits one open ride per
        // driver and `ux_rides_open_passenger` one per passenger, so a driver holding both an InProgress
        // and a PaymentPending ride is a state the platform cannot reach and must not be asserted about.
        var settling = await harness.CreateUserAsync();
        var pending = await Ride(harness, settling, driver, taxi, "PaymentPending", today, terminal: false);
        await harness.AddPaymentAsync(pending, amountMinor: 70_000, state: "Initiated");

        // Disputed — money the driver does not have until Finance resolves it.
        var disputed = await Ride(harness, passenger, driver, taxi, "Disputed", today);
        await harness.AddPaymentAsync(disputed, amountMinor: 55_000, state: "Disputed");

        var body = await harness.GetJsonAsync(
            $"/v1/earnings/{driver}?period=today", harness.Tokens.Driver(driver));

        Assert.Equal(40_000, body.GetProperty("grossMinor").GetInt64());

        // Two journeys: the paid one and the cash one. The cash ride contributes a trip and no money,
        // because nothing wrote a payment row for it — a driver whose day was all cash must not read
        // "0 trips".
        Assert.Equal(2, body.GetProperty("trips").GetInt32());
    }

    /// <summary>
    /// The OnePay surcharge is the passenger's gateway cost (US-8.11), not the driver's income, and a
    /// tip is (E-10). The daily fee (D-13) comes off.
    /// </summary>
    [Fact]
    public async Task Gross_excludes_the_surcharge_and_net_subtracts_the_daily_fee()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var passenger = await harness.CreateUserAsync();
        var taxi = await harness.CreateVehicleAsync(driver, mode: "C");
        var today = Today();

        var ride = await Ride(harness, passenger, driver, taxi, "Paid", today);
        await harness.AddPaymentAsync(ride, amountMinor: 52_500, surchargeMinor: 2_500, tipMinor: 10_000);

        await harness.AddDailyFeeAsync(driver, taxi, today, amountMinor: 10_000);

        var body = await harness.GetJsonAsync(
            $"/v1/earnings/{driver}?period=today", harness.Tokens.Driver(driver));

        Assert.Equal(50_000, body.GetProperty("grossMinor").GetInt64());
        Assert.Equal(10_000, body.GetProperty("tipMinor").GetInt64());
        Assert.Equal(10_000, body.GetProperty("dailyFeeMinor").GetInt64());
        Assert.Equal(50_000 + 10_000 - 10_000, body.GetProperty("netMinor").GetInt64());
        Assert.Equal("LKR", body.GetProperty("currency").GetString());
    }

    /// <summary>
    /// D-10 chains a retry as a new row, so a ride paid on the third attempt has three payment rows and
    /// exactly one fare.
    /// </summary>
    [Fact]
    public async Task A_retry_chain_contributes_one_fare_and_not_three()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var passenger = await harness.CreateUserAsync();
        var taxi = await harness.CreateVehicleAsync(driver, mode: "C");
        var today = Today();

        var ride = await Ride(harness, passenger, driver, taxi, "Paid", today);

        await harness.AddPaymentAsync(ride, amountMinor: 30_000, state: "Failed", attemptNo: 1);
        await harness.AddPaymentAsync(ride, amountMinor: 30_000, state: "Failed", attemptNo: 2);
        await harness.AddPaymentAsync(ride, amountMinor: 30_000, state: "Succeeded", attemptNo: 3);

        var body = await harness.GetJsonAsync(
            $"/v1/earnings/{driver}?period=today", harness.Tokens.Driver(driver));

        Assert.Equal(30_000, body.GetProperty("grossMinor").GetInt64());
        Assert.Equal(1, body.GetProperty("trips").GetInt32());
    }

    /// <summary>
    /// D-05's Rs 50 is charged to a <em>passenger</em> and paid to the driver whose time was wasted, so on
    /// a driver's dashboard the penalty line <b>adds</b>. See <c>EarningsRepository</c>'s remarks and the
    /// C042 handoff — D3' calls the field <c>penaltyMinor</c> and its prose reads like a deduction, but
    /// nothing in D5' ever debits a driver.
    /// </summary>
    [Fact]
    public async Task A_settled_cancellation_penalty_is_credited_to_the_affected_driver()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var passenger = await harness.CreateUserAsync();
        var today = Today();

        await harness.AddSettledPenaltyAsync(
            passenger, driver, amountMinor: 5_000, createdAt: BusinessCalendar.StartOfDay(today).AddHours(9));

        var body = await harness.GetJsonAsync(
            $"/v1/earnings/{driver}?period=today", harness.Tokens.Driver(driver));

        Assert.Equal(5_000, body.GetProperty("penaltyMinor").GetInt64());
        Assert.Equal(5_000, body.GetProperty("netMinor").GetInt64());
    }

    /// <summary>
    /// Periods are Colombo business dates (D-13, D-38) and the range is reported back, so a driver can
    /// see which days a number covers.
    /// </summary>
    [Fact]
    public async Task Periods_are_resolved_in_Colombo_and_reported_back()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var today = Today();

        foreach (var period in new[] { "today", "week", "month" })
        {
            var body = await harness.GetJsonAsync(
                $"/v1/earnings/{driver}?period={period}", harness.Tokens.Driver(driver));

            Assert.Equal(period, body.GetProperty("period").GetString());
            Assert.Equal(today.ToString("yyyy-MM-dd"), body.GetProperty("rangeTo").GetString());
        }

        var monthly = await harness.GetJsonAsync(
            $"/v1/earnings/{driver}?period=month", harness.Tokens.Driver(driver));

        Assert.Equal(
            new DateOnly(today.Year, today.Month, 1).ToString("yyyy-MM-dd"),
            monthly.GetProperty("rangeFrom").GetString());

        using var badPeriod = await harness.GetAsync(
            $"/v1/earnings/{driver}?period=fortnight", harness.Tokens.Driver(driver));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badPeriod.StatusCode);
    }

    /// <summary>
    /// The per-ride breakdown pages newest first and carries only ride-level money — the daily fee and
    /// the D-05 penalty are facts about a day and about somebody else's cancellation, and splitting them
    /// across rides would make every row's net wrong in a different way.
    /// </summary>
    [Fact]
    public async Task The_sessions_breakdown_pages_and_carries_only_ride_level_money()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var passenger = await harness.CreateUserAsync();
        var taxi = await harness.CreateVehicleAsync(driver, mode: "C");
        var today = Today();

        await harness.AddDailyFeeAsync(driver, taxi, today, amountMinor: 10_000);

        for (var i = 0; i < 5; i++)
        {
            var terminalAt = BusinessCalendar.StartOfDay(today).AddHours(8 + i);
            var ride = await harness.CreateRideAsync(
                passenger, Fort, GalleFace,
                state: "Paid", driverId: driver, vehicleId: taxi,
                createdAt: terminalAt.AddMinutes(-20), terminalAt: terminalAt);

            await harness.AddPaymentAsync(ride, amountMinor: 20_000, tipMinor: 1_000);
        }

        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var url = $"/v1/earnings/{driver}/sessions?limit=2"
                      + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");

            var page = await harness.GetJsonAsync(url, harness.Tokens.Driver(driver));

            foreach (var row in page.GetProperty("items").EnumerateArray())
            {
                seen.Add(row.GetProperty("tripId").GetString()!);

                Assert.Equal(20_000, row.GetProperty("grossMinor").GetInt64());
                Assert.Equal(1_000, row.GetProperty("tipMinor").GetInt64());
                Assert.Equal(21_000, row.GetProperty("netMinor").GetInt64());

                // Not on a per-ride row, by design.
                Assert.False(row.TryGetProperty("dailyFeeMinor", out _));
                Assert.False(row.TryGetProperty("penaltyMinor", out _));
            }

            cursor = page.GetProperty("cursor").ValueKind == System.Text.Json.JsonValueKind.Null
                ? null
                : page.GetProperty("cursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_driver_may_not_read_another_drivers_earnings()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var mine = await harness.CreateUserAsync("driver");
        var theirs = await harness.CreateUserAsync("driver");

        using var response = await harness.GetAsync(
            $"/v1/earnings/{theirs}", harness.Tokens.Driver(mine));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static DateOnly Today() => BusinessCalendar.Today(TimeProvider.System);

    private static Task<Guid> Ride(
        QueryHarness harness,
        Guid passenger,
        Guid driver,
        Guid vehicle,
        string state,
        DateOnly businessDate,
        bool terminal = true)
    {
        var at = BusinessCalendar.StartOfDay(businessDate).AddHours(10);

        return harness.CreateRideAsync(
            passenger, Fort, GalleFace,
            state: state, driverId: driver, vehicleId: vehicle,
            createdAt: at.AddMinutes(-20), terminalAt: terminal ? at : null);
    }
}
