using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MageRide.Voip.Endpoints;
using MageRide.Voip.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Voip.Tests.Integration;

/// <summary>
/// `POST /v1/voip/token` — who may mint, what they get, and P-05.
/// </summary>
[Collection(VoipCollection.Name)]
public sealed class TokenTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_passenger_is_connected_to_the_driver_and_a_driver_to_the_rider()
    {
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        var passenger = await harness.PostAsync<VoipTokenResponse>(
            "/v1/voip/token", new { rideId = ride.Id }, harness.Tokens.Passenger(ride.PassengerId));

        var driver = await harness.PostAsync<VoipTokenResponse>(
            "/v1/voip/token", new { rideId = ride.Id }, harness.Tokens.Driver(ride.DriverId));

        Assert.Equal("driver", passenger.Callee);
        Assert.Equal("rider", driver.Callee);

        // One ride, one room (D3': `ride_{id}`), so the two tokens are for the same conversation.
        Assert.Equal($"ride_{ride.Id:D}", passenger.RoomName);
        Assert.Equal(passenger.RoomName, driver.RoomName);
        Assert.Equal(VoipHarness.WsUrl, passenger.WsUrl);
    }

    [Fact]
    public async Task A_proxy_rides_driver_token_resolves_to_the_rider_identity_never_the_booker()
    {
        // Definition of done #2, in three parts: the driver's counterparty is the rider, the
        // passenger-side token is minted for the RIDER's account, and the booker cannot obtain a
        // token at all — so the booker's id can never appear as a participant in that room.
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync(proxy: true);

        var driver = await harness.PostAsync<VoipTokenResponse>(
            "/v1/voip/token", new { rideId = ride.Id }, harness.Tokens.Driver(ride.DriverId));

        Assert.Equal("rider", driver.Callee);

        var rider = await harness.PostAsync<VoipTokenResponse>(
            "/v1/voip/token", new { rideId = ride.Id }, harness.Tokens.Passenger(ride.RiderId!.Value));

        Assert.Equal(ride.RiderId!.Value.ToString("D"), Subject(rider.Token));
        Assert.NotEqual(ride.BookerId.ToString("D"), Subject(rider.Token));

        using var booker = await harness.PostAsync(
            "/v1/voip/token", new { rideId = ride.Id }, harness.Tokens.Passenger(ride.BookerId));

        Assert.Equal(HttpStatusCode.Forbidden, booker.StatusCode);
    }

    [Fact]
    public async Task A_proxy_ride_whose_rider_has_no_account_has_no_in_app_call_at_all()
    {
        // P-03 keeps only a digest of an unregistered rider's number, so there is nobody to admit to
        // a room — and, as ride-svc records from its own side, nobody to direct-dial either. The
        // driver is refused rather than being quietly connected to the booker, which is the failure
        // this whole fence exists to make impossible.
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync(proxy: true, registeredRider: false);

        using var driver = await harness.PostAsync(
            "/v1/voip/token", new { rideId = ride.Id }, harness.Tokens.Driver(ride.DriverId));

        // The driver IS a participant, so the refusal is about the ride and not about them.
        Assert.Equal(HttpStatusCode.BadRequest, driver.StatusCode);

        using var booker = await harness.PostAsync(
            "/v1/voip/token", new { rideId = ride.Id }, harness.Tokens.Passenger(ride.BookerId));

        Assert.Equal(HttpStatusCode.Forbidden, booker.StatusCode);
    }

    [Fact]
    public async Task A_stranger_is_not_a_participant()
    {
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();
        var stranger = await harness.Seed.UserAsync();

        using var response = await harness.PostAsync(
            "/v1/voip/token", new { rideId = ride.Id }, harness.Tokens.Passenger(stranger));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_ride_that_does_not_exist_is_a_404()
    {
        await using var harness = await VoipHarness.StartAsync(postgres);

        var stranger = await harness.Seed.UserAsync();

        using var response = await harness.PostAsync(
            "/v1/voip/token", new { rideId = Guid.NewGuid() }, harness.Tokens.Passenger(stranger));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Before_a_driver_accepts_there_is_nobody_to_call()
    {
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync(state: "Requested", accepted: false);

        using var response = await harness.PostAsync(
            "/v1/voip/token", new { rideId = ride.Id }, harness.Tokens.Passenger(ride.PassengerId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task With_no_LiveKit_configured_calling_is_absent_and_says_so()
    {
        // The VoIP-failure signal at its earliest point: 503 is what makes the client offer
        // "Call normally instead?" (ADD §14, AL-48). A 200 with an unusable token would look like
        // a call that failed for some other reason.
        await using var harness = await VoipHarness.StartAsync(postgres, withLiveKit: false);

        var ride = await harness.Seed.RideAsync();

        using var response = await harness.PostAsync(
            "/v1/voip/token", new { rideId = ride.Id }, harness.Tokens.Passenger(ride.PassengerId));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("dependency-unavailable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/voip/token")
        {
            Content = JsonContent.Create(new { rideId = ride.Id }),
        };

        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The <c>sub</c> claim of a LiveKit token — the identity that will join the room.</summary>
    internal static string? Subject(string token)
    {
        var payload = token.Split('.')[1];

        return JsonDocument.Parse(Base64Url.DecodeFromChars(payload)).RootElement
            .GetProperty("sub").GetString();
    }
}
