using Grpc.Core;
using Grpc.Net.Client;
using MageRide.Query.Grpc;
using QueryGrpc = MageRide.Query.Grpc.Query;
using MageRide.Query.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.Query.Tests.Integration;

/// <summary>
/// The <c>query.v1.Query</c> internal surface (ADD §6, D3' §0).
/// </summary>
/// <remarks>
/// Driven through a real gRPC channel against the running service, so the interceptor, the metadata
/// header and the generated stubs are the ones a caller meets. What is worth proving is that the RPC and
/// the HTTP route reach the <em>same</em> visibility filter — the reason the surface delegates rather than
/// reading the repositories itself.
/// </remarks>
[Collection(QuerySvcCollection.Name)]
public sealed class InternalGrpcTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint Fort = new(6.9344, 79.8428);
    private static readonly GeoPoint GalleFace = new(6.9271, 79.8449);

    [Fact]
    public async Task The_internal_nearby_read_applies_the_same_visibility_rules()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var passenger = await harness.CreateUserAsync();

        var idle = await harness.CreateVehicleAsync(driver, mode: "C");
        var engaged = await harness.CreateVehicleAsync(driver, mode: "C");
        var sharedVan = await harness.CreateVehicleAsync(driver, mode: "B", vehicleType: "van");

        await harness.Positions.PublishAsync(idle, Fort, mode: "C");
        await harness.Positions.PublishAsync(engaged, Fort, mode: "C");
        await harness.Positions.PublishAsync(sharedVan, Fort, mode: "B", vehicleType: "van");

        await harness.Positions.EngageAsync(engaged, Guid.NewGuid());

        using var channel = Channel(harness);
        var client = new QueryGrpc.QueryClient(channel);

        var response = await client.GetNearbyVehiclesAsync(
            new NearbyRequest
            {
                Lat = Fort.Latitude,
                Lng = Fort.Longitude,
                RadiusM = 3_000,
                ViewerUserId = passenger.ToString(),
            },
            Metadata());

        var ids = response.Vehicles.Select(vehicle => vehicle.VehicleId).ToArray();

        Assert.Contains(idle.ToString(), ids);
        // Somebody else's hire, and a van this passenger is not entitled to: neither is theirs to see,
        // whichever transport asked.
        Assert.DoesNotContain(engaged.ToString(), ids);
        Assert.DoesNotContain(sharedVan.ToString(), ids);
        Assert.False(response.LimitedLive);

        // …and the entitlement is what changes the answer, not the transport.
        await harness.Positions.ShareAsync(passenger, sharedVan);

        var entitled = await client.GetNearbyVehiclesAsync(
            new NearbyRequest
            {
                Lat = Fort.Latitude,
                Lng = Fort.Longitude,
                RadiusM = 3_000,
                ViewerUserId = passenger.ToString(),
            },
            Metadata());

        Assert.Contains(sharedVan.ToString(), entitled.Vehicles.Select(vehicle => vehicle.VehicleId));
    }

    /// <summary>
    /// Two of the four rules are per viewer, so a call that names nobody cannot be answered — and must
    /// not be answered with the public map.
    /// </summary>
    [Fact]
    public async Task A_nearby_read_without_a_viewer_is_refused()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        using var channel = Channel(harness);
        var client = new QueryGrpc.QueryClient(channel);

        var failure = await Assert.ThrowsAsync<RpcException>(() =>
            client.GetNearbyVehiclesAsync(
                new NearbyRequest { Lat = Fort.Latitude, Lng = Fort.Longitude },
                Metadata()).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, failure.StatusCode);
    }

    [Fact]
    public async Task A_call_without_the_internal_key_is_unauthenticated()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        using var channel = Channel(harness);
        var client = new QueryGrpc.QueryClient(channel);

        var missing = await Assert.ThrowsAsync<RpcException>(() =>
            client.GetNearbyVehiclesAsync(
                new NearbyRequest
                {
                    Lat = Fort.Latitude,
                    Lng = Fort.Longitude,
                    ViewerUserId = Guid.NewGuid().ToString(),
                }).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, missing.StatusCode);

        var wrong = await Assert.ThrowsAsync<RpcException>(() =>
            client.GetNearbyVehiclesAsync(
                new NearbyRequest
                {
                    Lat = Fort.Latitude,
                    Lng = Fort.Longitude,
                    ViewerUserId = Guid.NewGuid().ToString(),
                },
                Metadata("not-the-key")).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, wrong.StatusCode);
    }

    /// <summary>
    /// Without <c>Query:InternalApiKey</c> the service is not mapped at all, so no call can be answered.
    /// </summary>
    /// <remarks>
    /// The status is <c>Unauthenticated</c> and not <c>Unimplemented</c>: the kernel's deny-by-default
    /// fallback policy (AL-06) applies to a request that matched no endpoint too, so the 401 is written
    /// before routing's 404 would be. That is the right way round — "this surface does not exist here"
    /// and "your key is wrong" are indistinguishable to a caller holding neither, which is one fewer
    /// thing an unauthenticated prober learns about the cluster.
    /// </remarks>
    [Fact]
    public async Task Without_a_configured_key_no_call_is_answered()
    {
        await using var harness = await QueryHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Query:InternalApiKey"] = "" });

        using var channel = Channel(harness);
        var client = new QueryGrpc.QueryClient(channel);

        var failure = await Assert.ThrowsAsync<RpcException>(() =>
            client.GetNearbyVehiclesAsync(
                new NearbyRequest
                {
                    Lat = Fort.Latitude,
                    Lng = Fort.Longitude,
                    ViewerUserId = Guid.NewGuid().ToString(),
                },
                Metadata()).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, failure.StatusCode);
    }

    [Fact]
    public async Task Trip_detail_and_earnings_answer_over_the_internal_surface()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateUserAsync("driver");
        var taxi = await harness.CreateVehicleAsync(driver, mode: "C", driverName: "Kamala Silva");

        var terminalAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var ride = await harness.CreateRideAsync(
            passenger, Fort, GalleFace,
            state: "Paid", driverId: driver, vehicleId: taxi,
            createdAt: terminalAt.AddMinutes(-20), terminalAt: terminalAt);

        await harness.AddPaymentAsync(ride, amountMinor: 40_000, tipMinor: 2_000);

        using var channel = Channel(harness);
        var client = new QueryGrpc.QueryClient(channel);

        var trip = await client.GetTripDetailAsync(
            new TripRequest { UserId = passenger.ToString(), TripId = ride.ToString() }, Metadata());

        Assert.Equal("ride", trip.Plane);
        Assert.Equal(40_000, trip.FareMinor);
        Assert.Equal("Kamala Silva", trip.Driver.Name);

        var earnings = await client.GetDriverEarningsAsync(
            new EarningsRequest { DriverId = driver.ToString(), Period = "today" }, Metadata());

        Assert.Equal(40_000, earnings.GrossMinor);
        Assert.Equal(2_000, earnings.TipMinor);
        Assert.Equal("LKR", earnings.Currency);
    }

    /// <summary>
    /// The scoping is the same as the HTTP route's: a trip that is not this user's does not exist.
    /// </summary>
    [Fact]
    public async Task A_trip_belonging_to_somebody_else_is_not_found_over_gRPC_either()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var owner = await harness.CreateUserAsync();
        var stranger = await harness.CreateUserAsync();
        var driver = await harness.CreateUserAsync("driver");
        var taxi = await harness.CreateVehicleAsync(driver, mode: "C");

        var ride = await harness.CreateRideAsync(
            owner, Fort, GalleFace, state: "Paid", driverId: driver, vehicleId: taxi,
            terminalAt: DateTimeOffset.UtcNow);

        using var channel = Channel(harness);
        var client = new QueryGrpc.QueryClient(channel);

        var failure = await Assert.ThrowsAsync<RpcException>(() =>
            client.GetTripDetailAsync(
                new TripRequest { UserId = stranger.ToString(), TripId = ride.ToString() },
                Metadata()).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, failure.StatusCode);
    }

    /// <summary>An h2c channel against the harness's HTTP/2 listener.</summary>
    private static GrpcChannel Channel(QueryHarness harness) =>
        GrpcChannel.ForAddress(harness.GrpcAddress);

    private static Metadata Metadata(string key = QueryHarness.InternalApiKey) =>
        new() { { InternalKeyInterceptor.HeaderName, key } };
}
