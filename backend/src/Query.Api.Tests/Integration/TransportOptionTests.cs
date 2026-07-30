using System.Net;
using MageRide.Query.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Query.Tests.Integration;

/// <summary>
/// <c>GET /v1/transport-options</c> — US-7.15's "trains alongside other transport options".
/// </summary>
/// <remarks>
/// This endpoint aggregates and computes nothing: GTFS route matching is transit-svc's (C061, AL-18)
/// and the Mode C tariff is fare-svc's (D5' §1.3). What is under test is therefore the seam — the
/// requests query-svc makes, the ordering it applies (BR-23.2), the AL-19 fence that a pre-match tier
/// carries no ETA, and each degradation.
/// </remarks>
[Collection(QuerySvcCollection.Name)]
public sealed class TransportOptionTests(PostgresFixture postgres, RedisFixture redis)
{
    private const string Query =
        "/v1/transport-options?fromLat=6.9344&fromLng=79.8428&toLat=7.2906&toLng=80.6337";

    /// <summary>
    /// Public routes first, then the private tiers — BR-23.2's ordering, and the reason for it: a
    /// passenger comparing a Rs 30 bus with a Rs 680 taxi wants the bus at the top.
    /// </summary>
    [Fact]
    public async Task Public_routes_come_first_and_trains_are_among_them()
    {
        await using var downstream = await FakeDownstream.StartAsync();
        await using var harness = await StartAsync(downstream, transit: true, fare: true);

        var passenger = await harness.CreateUserAsync();

        var body = await harness.GetJsonAsync(Query, harness.Tokens.Passenger(passenger));
        var options = body.GetProperty("options").EnumerateArray().ToArray();

        var kinds = options.Select(option => option.GetProperty("kind").GetString()!).ToArray();

        Assert.Equal(["public", "public", "private", "private"], kinds);

        // A direct route (transfers = 0) above a transit one (AL-18).
        Assert.Equal(0, options[0].GetProperty("transfers").GetInt32());
        Assert.Equal(1, options[1].GetProperty("transfers").GetInt32());

        // US-7.15 is specifically about trains being offered, and MAP-03's rail icon needs the type —
        // which is passed through from transit-svc rather than guessed at.
        Assert.Equal("train", options[1].GetProperty("vehicleType").GetString());
        Assert.Equal("EX01", options[1].GetProperty("routeNumber").GetString());

        // The origin reached both downstreams; nothing was defaulted or dropped on the way.
        Assert.Contains(
            downstream.Requests,
            request => request.StartsWith("/v1/transit/options?", StringComparison.Ordinal)
                       && request.Contains("fromLat=6.9344", StringComparison.Ordinal)
                       && request.Contains("toLng=80.6337", StringComparison.Ordinal));
    }

    /// <summary>
    /// AL-19/BR-23.3: before dispatch a Mode C tier exposes the **upfront price only**. The field is
    /// absent by construction — the private option is built without one — so this cannot regress to "we
    /// happened not to have an ETA today".
    /// </summary>
    [Fact]
    public async Task A_private_tier_is_price_only()
    {
        await using var downstream = await FakeDownstream.StartAsync();
        await using var harness = await StartAsync(downstream, transit: false, fare: true);

        var passenger = await harness.CreateUserAsync();

        var body = await harness.GetJsonAsync(Query, harness.Tokens.Passenger(passenger));
        var options = body.GetProperty("options").EnumerateArray().ToArray();

        // Only the two tiers the fake prices; a tier with no tariff is absent rather than priced at 0.
        Assert.Equal(2, options.Length);

        foreach (var option in options)
        {
            Assert.Equal("private", option.GetProperty("kind").GetString());
            Assert.Equal("LKR", option.GetProperty("currency").GetString());
            Assert.True(option.GetProperty("estimatedFareMinor").GetInt64() > 0);

            Assert.False(option.TryGetProperty("etaSeconds", out _));
            Assert.False(option.TryGetProperty("transfers", out _));
        }

        Assert.Equal(
            42_000,
            options.Single(o => o.GetProperty("vehicleType").GetString() == "three_wheeler")
                .GetProperty("estimatedFareMinor").GetInt64());
    }

    /// <summary>
    /// One downstream missing degrades to the other's half. With no feed the passenger still sees the
    /// private tiers, which is C061's own documented no-coverage behaviour.
    /// </summary>
    [Fact]
    public async Task A_missing_transit_service_leaves_the_private_tiers()
    {
        await using var downstream = await FakeDownstream.StartAsync();
        await using var harness = await StartAsync(downstream, transit: true, fare: true);

        downstream.Routes.Clear();

        var passenger = await harness.CreateUserAsync();

        var body = await harness.GetJsonAsync(Query, harness.Tokens.Passenger(passenger));

        Assert.All(
            body.GetProperty("options").EnumerateArray(),
            option => Assert.Equal("private", option.GetProperty("kind").GetString()));
    }

    /// <summary>
    /// Neither configured is a 503, not an empty list: an empty options screen reads as "there is no way
    /// to get there", which is a false statement about a journey nobody has looked up.
    /// </summary>
    [Fact]
    public async Task With_neither_downstream_configured_the_endpoint_refuses()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();

        using var response = await harness.GetAsync(Query, harness.Tokens.Passenger(passenger));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// The origin is required: `geo:live` is keyed by vehicle because EMQX authenticates a vehicle, so
    /// the platform holds no last-known position for a *passenger* to default to.
    /// </summary>
    [Fact]
    public async Task The_origin_is_required_rather_than_invented()
    {
        await using var downstream = await FakeDownstream.StartAsync();
        await using var harness = await StartAsync(downstream, transit: true, fare: true);

        var passenger = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Passenger(passenger);

        using var noOrigin = await harness.GetAsync(
            "/v1/transport-options?toLat=7.2906&toLng=80.6337", bearer);

        Assert.Equal(HttpStatusCode.BadRequest, noOrigin.StatusCode);

        using var halfOrigin = await harness.GetAsync(
            "/v1/transport-options?fromLat=6.9344&toLat=7.2906&toLng=80.6337", bearer);

        Assert.Equal(HttpStatusCode.BadRequest, halfOrigin.StatusCode);

        using var noDestination = await harness.GetAsync(
            "/v1/transport-options?fromLat=6.9344&fromLng=79.8428", bearer);

        Assert.Equal(HttpStatusCode.BadRequest, noDestination.StatusCode);

        // Nothing was asked of either downstream for a request that could not be answered.
        Assert.Empty(downstream.Requests);
    }

    private Task<QueryHarness> StartAsync(FakeDownstream downstream, bool transit, bool fare) =>
        QueryHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?>
            {
                ["Query:TransitBaseUrl"] = transit ? downstream.BaseUrl : null,
                ["Query:FareBaseUrl"] = fare ? downstream.BaseUrl : null,
            });
}
