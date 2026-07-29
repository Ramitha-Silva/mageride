using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// DoD item 1: "a proxy ride notifies both booker and rider on every state change, and the driver
/// only ever sees the rider" (P-01, P-03, P-05, AL-48).
/// </summary>
[Collection<RideCollection>]
public sealed class ProxyBookingTests(PostgresFixture postgres)
{
    /// <summary>
    /// Every <c>ride.events</c> envelope names both channels, so notification-svc can fan out to
    /// the booker and the rider without a second read — and the driver's number, wherever it
    /// appears, is the rider's.
    /// </summary>
    [Fact]
    public async Task Every_state_change_on_a_proxy_ride_names_both_the_booker_and_the_rider()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();
        var rider = await harness.CreateUserAsync();
        var driver = await harness.CreateDriverAsync();

        var response = await harness.RequestProxyRideAsync(booker.Bearer, rider.Phone, riderName: "Nimal");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var rideId = (await RideHarness.ReadJsonAsync(response)).GetProperty("rideId").GetGuid();

        // Walk it to PaymentPending so every lifecycle event has been written.
        var offer = await harness.OfferAsync(rideId, driver);

        var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var version = (await RideHarness.ReadJsonAsync(accepted)).GetProperty("version").GetInt64();

        foreach (var command in new[] { "arrive", "start", "complete" })
        {
            var moved = await harness.PostAsync($"/v1/rides/{rideId}/{command}", new { version }, driver.Bearer);
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
            version = (await RideHarness.ReadJsonAsync(moved)).GetProperty("version").GetInt64();
        }

        var events = await harness.ReadEventsAsync(rideId);
        Assert.Equal(
            ["ride.requested", "offer.created", "ride.accepted", "ride.driver_arrived", "ride.started", "ride.completed"],
            events);

        foreach (var eventType in events)
        {
            var payload = (await harness.ReadEventPayloadAsync(rideId, eventType)).GetProperty("payload");

            Assert.Equal("proxy", payload.GetProperty("kind").GetString());
            Assert.True(payload.GetProperty("isProxy").GetBoolean());

            // Both channels, on every one of them. The booker arranged the ride; the rider is in
            // the car. P-05 is about who the *driver* reaches, not about who is told.
            Assert.Equal(booker.Id, payload.GetProperty("bookerId").GetGuid());
            Assert.Equal(rider.Id, payload.GetProperty("riderId").GetGuid());
            Assert.Equal("Nimal", payload.GetProperty("riderName").GetString());
        }
    }

    /// <summary>P-05/AL-48: the driver dials the rider, and the booker's number is nowhere.</summary>
    [Fact]
    public async Task The_driver_sees_the_riders_number_and_never_the_bookers()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();
        var rider = await harness.CreateUserAsync();
        var driver = await harness.CreateDriverAsync();

        var booked = await RideHarness.ReadJsonAsync(
            await harness.RequestProxyRideAsync(booker.Bearer, rider.Phone));

        var rideId = booked.GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver);

        // Before acceptance nobody has a number: AL-48 exposes one "only after driver acceptance".
        var beforeAccept = await ReadDetailAsync(harness, rideId, driver.Bearer);
        Assert.Null(Phone(beforeAccept, "counterpartyPhone"));

        var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var driverView = await ReadDetailAsync(harness, rideId, driver.Bearer);
        Assert.Equal(rider.Phone, Phone(driverView, "counterpartyPhone"));
        Assert.NotEqual(booker.Phone, Phone(driverView, "counterpartyPhone"));

        // And the booker's side of the same field is the driver's number.
        var bookerView = await ReadDetailAsync(harness, rideId, booker.Bearer);
        Assert.Equal(driver.Phone, Phone(bookerView, "counterpartyPhone"));

        // The rider is a participant too — they are the one being carried.
        var riderView = await ReadDetailAsync(harness, rideId, rider.Bearer);
        Assert.Equal(driver.Phone, Phone(riderView, "counterpartyPhone"));
    }

    /// <summary>
    /// A driver who was offered the ride and lost it may still read it — that is what
    /// <c>offered_driver_id</c> is for — and has nobody to call. Handing them the winner's number
    /// would leak one driver's MSISDN to another.
    /// </summary>
    [Fact]
    public async Task A_driver_who_only_held_an_offer_gets_no_number()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passengerId = await harness.CreatePassengerAsync();
        var passenger = harness.Tokens.Passenger(passengerId);
        var loser = await harness.CreateDriverAsync();
        var winner = await harness.CreateDriverAsync();

        var booked = await harness.RequestRideAsync(passenger);
        var rideId = booked.GetProperty("rideId").GetGuid();

        // The offer is reserved for one driver and — through the ADD §11.11 accept, which is
        // deliberately not bound to `offered_driver_id` — won by another.
        var offer = await harness.OfferAsync(rideId, loser);

        var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{winner.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            winner.Bearer);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var loserView = await ReadDetailAsync(harness, rideId, loser.Bearer);
        Assert.Null(Phone(loserView, "counterpartyPhone"));
        Assert.DoesNotContain(winner.Phone, loserView.ToString(), StringComparison.Ordinal);

        // The winner's own view is unaffected.
        var winnerView = await ReadDetailAsync(harness, rideId, winner.Bearer);
        Assert.NotNull(Phone(winnerView, "counterpartyPhone"));
    }

    /// <summary>
    /// P-03: an unregistered rider is a booking, not an error. Their number is stored as a digest
    /// and the ride names them by <c>rider_name</c> alone.
    /// </summary>
    [Fact]
    public async Task An_unregistered_rider_is_stored_as_a_hash_and_never_as_a_number()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();
        var phone = IamLookupStub.UnregisteredPhone();

        var response = await harness.RequestProxyRideAsync(booker.Bearer, phone, riderName: "Amara");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var rideId = (await RideHarness.ReadJsonAsync(response)).GetProperty("rideId").GetGuid();

        await using var connection = await harness.OpenAsync();
        var row = await connection.QuerySingleAsync<(Guid PassengerId, Guid BookerId, Guid? RiderId, byte[]? Hash, string? Name, bool IsProxy, short Kind)>(
            """
            SELECT passenger_id, booker_id, rider_id, rider_phone_hash, rider_name, is_proxy, kind
              FROM rides.rides WHERE id = @RideId;
            """,
            new { RideId = rideId });

        Assert.True(row.IsProxy);
        Assert.Equal(1, row.Kind);
        Assert.Null(row.RiderId);
        Assert.Equal("Amara", row.Name);
        Assert.NotNull(row.Hash);
        Assert.Equal(32, row.Hash!.Length);

        // The number itself is not in the row, in any encoding.
        Assert.DoesNotContain(phone, System.Text.Encoding.UTF8.GetString(row.Hash), StringComparison.Ordinal);

        // passenger_id is the booking account, not the rider — the rider may have no account at
        // all, and every invariant hung off passenger_id (R-18, AL-16, one-open-ride) is the
        // booker's.
        Assert.Equal(booker.Id, row.PassengerId);
        Assert.Equal(booker.Id, row.BookerId);
    }

    /// <summary>
    /// AL-48 and P-03 pull in opposite directions here, and P-03 wins: there is no number to give
    /// the driver for a rider the platform deliberately does not keep one for.
    /// </summary>
    [Fact]
    public async Task An_unregistered_riders_number_is_absent_from_the_drivers_view()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();
        var driver = await harness.CreateDriverAsync();

        var booked = await RideHarness.ReadJsonAsync(
            await harness.RequestProxyRideAsync(booker.Bearer, IamLookupStub.UnregisteredPhone()));

        var rideId = booked.GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver);

        var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var driverView = await ReadDetailAsync(harness, rideId, driver.Bearer);

        Assert.Null(Phone(driverView, "counterpartyPhone"));

        // Least of all the booker's, which is the one number P-05 forbids outright.
        Assert.DoesNotContain(booker.Phone, driverView.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proxy_booking_needs_a_name_and_a_reachable_number()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();

        var noName = await harness.PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                kind = "proxy",
                pickup = new { lat = RideHarness.Pickup.Latitude, lng = RideHarness.Pickup.Longitude },
                dropoff = new { lat = RideHarness.Dropoff.Latitude, lng = RideHarness.Dropoff.Longitude },
                vehicleType = "three_wheeler",
                fareEstimateToken = harness.IssueFareToken(),
                paymentMethod = "cash",
                riderPhone = IamLookupStub.UnregisteredPhone(),
            },
            booker.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);

        var badPhone = await harness.RequestProxyRideAsync(booker.Bearer, riderPhone: "0112345678");
        await ProblemDocument.AssertAsync(badPhone, HttpStatusCode.BadRequest, "invalid-phone");
    }

    /// <summary>
    /// The two members a client can contradict itself with — <c>kind</c> and <c>isProxy</c> — are
    /// reconciled once, and a genuine contradiction is refused rather than silently resolved.
    /// </summary>
    [Fact]
    public async Task Kind_and_isProxy_must_agree()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();

        var contradiction = await harness.PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                kind = "passenger",
                isProxy = true,
                pickup = new { lat = RideHarness.Pickup.Latitude, lng = RideHarness.Pickup.Longitude },
                dropoff = new { lat = RideHarness.Dropoff.Latitude, lng = RideHarness.Dropoff.Longitude },
                vehicleType = "three_wheeler",
                fareEstimateToken = harness.IssueFareToken(),
                paymentMethod = "cash",
                riderName = "Nimal",
                riderPhone = IamLookupStub.UnregisteredPhone(),
            },
            booker.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, contradiction.StatusCode);

        // …and `isProxy` alone is enough to mean a proxy booking, because D3' offers both spellings.
        var justTheFlag = await harness.PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                isProxy = true,
                pickup = new { lat = RideHarness.Pickup.Latitude, lng = RideHarness.Pickup.Longitude },
                dropoff = new { lat = RideHarness.Dropoff.Latitude, lng = RideHarness.Dropoff.Longitude },
                vehicleType = "three_wheeler",
                fareEstimateToken = harness.IssueFareToken(),
                paymentMethod = "cash",
                riderName = "Nimal",
                riderPhone = IamLookupStub.UnregisteredPhone(),
            },
            booker.Bearer);

        Assert.Equal(HttpStatusCode.Accepted, justTheFlag.StatusCode);

        var rideId = (await RideHarness.ReadJsonAsync(justTheFlag)).GetProperty("rideId").GetGuid();
        var detail = await ReadDetailAsync(harness, rideId, booker.Bearer);

        Assert.Equal("proxy", detail.GetProperty("kind").GetString());
    }

    /// <summary>
    /// A passenger booking that names somebody else is refused rather than quietly stripped: the
    /// client believes a third party is being carried, and a ride that dropped that belief would
    /// send a driver to the wrong person.
    /// </summary>
    [Fact]
    public async Task A_passenger_booking_may_not_carry_proxy_members()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = await harness.CreateUserAsync();

        var response = await harness.PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                pickup = new { lat = RideHarness.Pickup.Latitude, lng = RideHarness.Pickup.Longitude },
                dropoff = new { lat = RideHarness.Dropoff.Latitude, lng = RideHarness.Dropoff.Longitude },
                vehicleType = "three_wheeler",
                fareEstimateToken = harness.IssueFareToken(),
                paymentMethod = "cash",
                riderPhone = IamLookupStub.UnregisteredPhone(),
            },
            passenger.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The registration answer decides which columns are written, so it cannot be guessed at: with
    /// iam-svc down the booking is refused (D6' §8.3) rather than downgraded to the SMS path.
    /// </summary>
    [Fact]
    public async Task A_proxy_booking_is_refused_when_iam_cannot_answer()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var booker = await harness.CreateUserAsync();
        harness.Iam.Offline = true;

        var response = await harness.RequestProxyRideAsync(booker.Bearer, IamLookupStub.UnregisteredPhone());

        await ProblemDocument.AssertAsync(
            response, HttpStatusCode.ServiceUnavailable, "dependency-unavailable");

        // Nothing was written: a refused booking is not half a ride.
        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM rides.rides WHERE passenger_id = @Id;", new { Id = booker.Id }));
    }

    private static async Task<JsonElement> ReadDetailAsync(RideHarness harness, Guid rideId, string bearer)
    {
        var response = await harness.GetAsync($"/v1/rides/{rideId}", bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await RideHarness.ReadJsonAsync(response);
    }

    /// <summary>An absent member and a JSON <c>null</c> both mean "no number".</summary>
    private static string? Phone(JsonElement detail, string member) =>
        detail.TryGetProperty(member, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
