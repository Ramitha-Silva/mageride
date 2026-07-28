using System.Net;
using System.Text.Json;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.Shared.Time;
using MageRide.TestKit;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// DoD: "the login payload matches the AL-14 eager-fetch shape in one round trip" —
/// <c>GET /v1/me/bootstrap</c> and US-1.15's six items.
/// </summary>
[Collection<IamCollection>]
public sealed class BootstrapTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task The_payload_carries_every_item_US_1_15_lists()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        await harness.PostAsync(
            "/v1/me/saved-addresses",
            new { label = "home", line1 = "42 Galle Road", lat = 6.9271, lng = 79.8612, isHome = true },
            bearer: session.AccessToken);
        await harness.PostAsync(
            "/v1/me/emergency-contacts", new { name = "Amma", phone = "+94771234567" }, bearer: session.AccessToken);
        await harness.PutAsync(
            "/v1/me/prefs/payment-method", new { defaultPaymentMethod = "lankaqr" }, session.AccessToken);

        var response = await harness.GetAsync("/v1/me/bootstrap", session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await IamHarness.ReadJsonAsync(response);

        // (1) profile
        Assert.Equal(session.UserId, body.GetProperty("profile").GetProperty("userId").GetString());
        // (2) saved addresses
        Assert.Equal(1, body.GetProperty("savedAddresses").GetArrayLength());
        Assert.True(body.GetProperty("savedAddresses")[0].GetProperty("isHome").GetBoolean());
        // (3) payment-method metadata
        Assert.Equal("lankaqr", body.GetProperty("defaultPaymentMethod").GetString());
        Assert.Equal(
            ["cash", "lankaqr", "onepay"],
            body.GetProperty("paymentMethods").EnumerateArray().Select(m => m.GetString()).Order(StringComparer.Ordinal));
        // (4) active trip — none, and absent rather than null
        Assert.False(body.TryGetProperty("activeTrip", out _));
        // (5) driver shift — absent for an account with no driver role
        Assert.False(body.TryGetProperty("driver", out _));
        // (6) app config
        var cities = body.GetProperty("config").GetProperty("cities");
        Assert.Equal(3, cities.GetArrayLength());
        Assert.Equal("colombo", cities[0].GetProperty("code").GetString());
        Assert.Equal(6.9271, cities[0].GetProperty("centroid").GetProperty("lat").GetDouble(), 4);

        // AL-13's SOS list travels with the profile, and the RBAC model the portals render from.
        Assert.Equal(1, body.GetProperty("emergencyContacts").GetArrayLength());
        Assert.Equal(
            21, body.GetProperty("permissions").GetProperty("permissions").GetArrayLength());
    }

    /// <summary>
    /// DoD: one round trip. Measured rather than asserted by inspection — the payload has to be
    /// complete enough that a client never needs a second call to open its home screen (NFR-51).
    /// </summary>
    [Fact]
    public async Task The_whole_eager_set_arrives_in_a_single_request()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var body = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/bootstrap", session.AccessToken));

        foreach (var member in new[] { "profile", "savedAddresses", "emergencyContacts", "defaultPaymentMethod", "paymentMethods", "config", "permissions" })
        {
            Assert.True(body.TryGetProperty(member, out var value), $"The eager-fetch payload has no '{member}'.");
            Assert.NotEqual(JsonValueKind.Null, value.ValueKind);
        }

        Assert.True(body.GetProperty("config").TryGetProperty("featureFlags", out var flags));
        // No feature-flag store exists yet (C027 handoff); the field is present so a client can
        // rely on it and starts answering the day the store lands.
        Assert.Empty(flags.EnumerateObject());
    }

    /// <summary>US-1.14: a driver who switches handsets mid-trip restores the trip from this call alone.</summary>
    [Fact]
    public async Task A_driver_mid_ride_gets_the_active_trip_back()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);
        var phone = IamHarness.NextPhone();
        var driver = await harness.SignInAsync(phone, "old-handset", "driver");
        var driverId = Guid.Parse(driver.UserId);

        var passengerId = await harness.Seed.PassengerAsync(IamHarness.NextPhone());
        var vehicleId = await harness.Seed.ApprovedVehicleAsync(driverId);
        var rideId = await harness.Seed.ActiveRideAsync(passengerId, driverId, vehicleId);

        // The new handset signs in, which revokes the old session (AL-08) and issues a fresh one.
        var replacement = await harness.SignInAsync(phone, "new-handset", "driver");

        var body = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/bootstrap", replacement.AccessToken));

        var trip = body.GetProperty("activeTrip");
        Assert.Equal(rideId.ToString(), trip.GetProperty("tripId").GetString());
        Assert.Equal("ride", trip.GetProperty("kind").GetString());
        Assert.Equal("driver", trip.GetProperty("role").GetString());
        Assert.Equal("InProgress", trip.GetProperty("state").GetString());
        Assert.Equal("C", trip.GetProperty("mode").GetString());
        Assert.Equal(vehicleId.ToString(), trip.GetProperty("vehicleId").GetString());
        Assert.Equal(passengerId.ToString(), trip.GetProperty("counterpartyId").GetString());
        Assert.Equal(6.9271, trip.GetProperty("pickup").GetProperty("lat").GetDouble(), 4);
    }

    [Fact]
    public async Task A_passenger_mid_ride_gets_the_same_trip_from_the_other_end()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var passenger = await harness.SignInAsync(IamHarness.NextPhone(), "handset");
        var passengerId = Guid.Parse(passenger.UserId);

        var driverId = await harness.Seed.PassengerAsync(IamHarness.NextPhone());
        var vehicleId = await harness.Seed.ApprovedVehicleAsync(driverId);
        var rideId = await harness.Seed.ActiveRideAsync(passengerId, driverId, vehicleId);

        var body = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/bootstrap", passenger.AccessToken));

        var trip = body.GetProperty("activeTrip");
        Assert.Equal(rideId.ToString(), trip.GetProperty("tripId").GetString());
        Assert.Equal("passenger", trip.GetProperty("role").GetString());
        Assert.Equal(driverId.ToString(), trip.GetProperty("counterpartyId").GetString());
    }

    /// <summary>A terminal ride is history, and history is lazy-fetched (US-1.16).</summary>
    [Fact]
    public async Task A_finished_ride_is_not_an_active_trip()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var passenger = await harness.SignInAsync(IamHarness.NextPhone(), "handset");
        var passengerId = Guid.Parse(passenger.UserId);

        var driverId = await harness.Seed.PassengerAsync(IamHarness.NextPhone());
        var vehicleId = await harness.Seed.ApprovedVehicleAsync(driverId);
        await harness.Seed.ActiveRideAsync(passengerId, driverId, vehicleId, state: "CashSettled");

        var body = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/bootstrap", passenger.AccessToken));

        Assert.False(body.TryGetProperty("activeTrip", out _));
    }

    /// <summary>
    /// R-01 keeps the two planes apart: a Mode A/B journey is a <c>trips.sessions</c> row, not a
    /// ride, and the eager set restores either.
    /// </summary>
    [Fact]
    public async Task A_driver_on_a_Mode_B_session_gets_it_as_the_active_trip()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var driver = await harness.SignInAsync(IamHarness.NextPhone(), "handset", "driver");
        var driverId = Guid.Parse(driver.UserId);

        var vehicleId = await harness.Seed.ApprovedVehicleAsync(driverId);
        var sessionId = await harness.Seed.ActiveTripSessionAsync(driverId, vehicleId);

        var body = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/bootstrap", driver.AccessToken));

        var trip = body.GetProperty("activeTrip");
        Assert.Equal(sessionId.ToString(), trip.GetProperty("tripId").GetString());
        Assert.Equal("session", trip.GetProperty("kind").GetString());
        Assert.Equal("B", trip.GetProperty("mode").GetString());

        var shift = body.GetProperty("driver");
        Assert.True(shift.GetProperty("isOnline").GetBoolean());
        Assert.Equal(sessionId.ToString(), shift.GetProperty("activeSessionId").GetString());
    }

    /// <summary>US-1.15 item 5 — today's earnings summary, on the Asia/Colombo business day (D-38).</summary>
    [Fact]
    public async Task A_driver_gets_todays_earnings_summary_on_the_Colombo_business_day()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var driver = await harness.SignInAsync(IamHarness.NextPhone(), "handset", "driver");
        var driverId = Guid.Parse(driver.UserId);

        var today = BusinessCalendar.Today(TimeProvider.System);
        await harness.Seed.EarningsAsync(driverId, today, trips: 7, grossMinor: 452_500, dailyFeeMinor: 10_000);

        // Yesterday's row must not leak into today's card.
        await harness.Seed.EarningsAsync(driverId, today.AddDays(-1), trips: 99, grossMinor: 9_999_900, dailyFeeMinor: 0);

        var response = await harness.GetAsync("/v1/me/bootstrap", driver.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var shift = (await IamHarness.ReadJsonAsync(response)).GetProperty("driver");

        Assert.Equal(
            today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            shift.GetProperty("businessDate").GetString());
        Assert.Equal(7, shift.GetProperty("todayTrips").GetInt32());
        Assert.Equal(452_500, shift.GetProperty("todayGross").GetProperty("amountMinor").GetInt64());
        Assert.Equal("LKR", shift.GetProperty("todayGross").GetProperty("currency").GetString());
        Assert.Equal(10_000, shift.GetProperty("todayDailyFee").GetProperty("amountMinor").GetInt64());
        Assert.False(shift.GetProperty("isOnline").GetBoolean());
    }

    [Fact]
    public async Task A_driver_who_has_not_earned_today_gets_zeroes_rather_than_a_missing_card()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var driver = await harness.SignInAsync(IamHarness.NextPhone(), "handset", "driver");

        var shift = (await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/bootstrap", driver.AccessToken)))
            .GetProperty("driver");

        Assert.Equal(0, shift.GetProperty("todayTrips").GetInt32());
        Assert.Equal(0, shift.GetProperty("todayGross").GetProperty("amountMinor").GetInt64());
        Assert.Equal("LKR", shift.GetProperty("todayGross").GetProperty("currency").GetString());
    }

    [Fact]
    public async Task The_bootstrap_needs_a_token()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        await ProblemDocument.AssertAsync(
            await harness.GetAsync("/v1/me/bootstrap"), HttpStatusCode.Unauthorized, "unauthorized");
    }
}
