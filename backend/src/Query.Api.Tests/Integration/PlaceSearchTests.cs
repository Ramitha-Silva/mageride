using System.Net;
using MageRide.Query.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.Query.Tests.Integration;

/// <summary>
/// Destination search and reverse geocoding (AL-17, BR-23.1, D-14/D-15), including the Definition of
/// Done's fourth claim: search never returns a route row for a typed route number.
/// </summary>
[Collection(QuerySvcCollection.Name)]
public sealed class PlaceSearchTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint Fort = new(6.9344, 79.8428);

    /// <summary>
    /// Definition of Done: "search never returns a route row for a typed route number."
    /// </summary>
    /// <remarks>
    /// The route genuinely exists — a <c>spatial.routes</c> row numbered 138 with a shape, and an active
    /// Mode A session on it, so <c>GET /v1/routes/138/buses</c> in the same run returns vehicles. Search
    /// still cannot produce it, because the search path has no query that reads a route: AL-17 is held by
    /// an absence of capability rather than by a filter somebody could remove.
    /// </remarks>
    [Fact]
    public async Task Search_never_returns_a_route_row_for_a_typed_route_number()
    {
        await using var nominatim = await FakeNominatim.StartAsync();
        await using var harness = await StartAsync(nominatim);

        var routeId = await harness.CreateRouteAsync(
            "138", "Kottawa – Pettah", [new GeoPoint(6.84, 79.97), new GeoPoint(6.9344, 79.8428)]);

        var driver = await harness.CreateUserAsync("driver");
        var bus = await harness.CreateVehicleAsync(driver, mode: "A", vehicleType: "bus");
        await harness.CreateSessionAsync(driver, bus, mode: "A", state: "ACTIVE", routeId: routeId);
        await harness.Positions.PublishAsync(bus, Fort, mode: "A", vehicleType: "bus");

        var passenger = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Passenger(passenger);

        // Nominatim answers with a place, as it would for any string.
        nominatim.Places.Clear();
        nominatim.Places.Add(new FakeNominatim.GeocodedFixture(
            6.8410, 79.9720, "138, Highlevel Road, Kottawa", "Highlevel Road", "Colombo"));

        var search = await harness.GetJsonAsync("/v1/geo/search?q=138", bearer);
        var places = search.GetProperty("places").EnumerateArray().ToArray();

        Assert.NotEmpty(places);

        foreach (var place in places)
        {
            var source = place.GetProperty("source").GetString();

            // BR-23.1's three sources and no fourth.
            Assert.Contains(source, new[] { "nominatim", "saved", "recent" });

            // Nothing in the payload can identify a route: there is no field for one, and no value
            // returned is the route's id or name.
            Assert.False(place.TryGetProperty("routeNumber", out _));
            Assert.False(place.TryGetProperty("routeId", out _));
            Assert.NotEqual(routeId.ToString(), place.GetProperty("displayName").GetString());
        }

        // …and the route is genuinely there, on the endpoint that is allowed to know about it (US-7.9).
        var buses = await harness.GetJsonAsync("/v1/routes/138/buses", bearer);

        Assert.Equal(
            bus.ToString(),
            buses.GetProperty("vehicles").EnumerateArray().Single().GetProperty("vehicleId").GetString());
    }

    /// <summary>
    /// BR-23.1: predictions are geocoded places plus the caller's saved and recent addresses, and the
    /// caller's own come first with the label they gave them (AL-26).
    /// </summary>
    [Fact]
    public async Task Predictions_include_the_callers_saved_and_recent_places()
    {
        await using var nominatim = await FakeNominatim.StartAsync();
        await using var harness = await StartAsync(nominatim);

        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateUserAsync("driver");
        var taxi = await harness.CreateVehicleAsync(driver, mode: "C");

        await harness.AddSavedAddressAsync(
            passenger, "Home", "42 Office Road", "Colombo 05", new GeoPoint(6.8900, 79.8700), isHome: true);

        // A place this passenger has actually travelled to — a stronger prediction than one they typed.
        await harness.CreateRideAsync(
            passenger, Fort, new GeoPoint(6.9100, 79.8600),
            state: "Paid", driverId: driver, vehicleId: taxi, terminalAt: DateTimeOffset.UtcNow);

        var body = await harness.GetJsonAsync(
            "/v1/geo/search?q=Office", harness.Tokens.Passenger(passenger));

        var places = body.GetProperty("places").EnumerateArray().ToArray();
        var sources = places.Select(place => place.GetProperty("source").GetString()).ToArray();

        Assert.Equal("saved", sources[0]);
        Assert.Equal("Home", places[0].GetProperty("label").GetString());
        Assert.Contains("recent", sources);
        Assert.Contains("nominatim", sources);
    }

    /// <summary>
    /// The query this service builds and the shape it parses back are the contract with Nominatim, so both
    /// are asserted against a real HTTP exchange rather than a substituted interface.
    /// </summary>
    [Fact]
    public async Task A_forward_search_restricts_to_Sri_Lanka_and_maps_the_address_lines()
    {
        await using var nominatim = await FakeNominatim.StartAsync();
        await using var harness = await StartAsync(nominatim);

        var passenger = await harness.CreateUserAsync();

        var body = await harness.GetJsonAsync(
            "/v1/geo/search?q=Galle%20Face&lat=6.9271&lng=79.8612",
            harness.Tokens.Passenger(passenger));

        var place = body.GetProperty("places").EnumerateArray()
            .Single(entry => entry.GetProperty("source").GetString() == "nominatim");

        Assert.Equal("Olcott Mawatha", place.GetProperty("line1").GetString());
        Assert.Equal("Colombo", place.GetProperty("city").GetString());

        var request = Assert.Single(nominatim.Requests);

        Assert.StartsWith("/search?", request, StringComparison.Ordinal);
        Assert.Contains("format=jsonv2", request, StringComparison.Ordinal);
        Assert.Contains("addressdetails=1", request, StringComparison.Ordinal);
        Assert.Contains("countrycodes=lk", request, StringComparison.Ordinal);
        // A bias, not a bound: a town 200 km away must still be findable (viewbox + bounded=0).
        Assert.Contains("viewbox=", request, StringComparison.Ordinal);
        Assert.Contains("bounded=0", request, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cache is D-15's whole mitigation for a geocoder on its own VPS: a repeated search must not
    /// reach it twice.
    /// </summary>
    [Fact]
    public async Task An_identical_search_is_served_from_the_cache()
    {
        await using var nominatim = await FakeNominatim.StartAsync();
        await using var harness = await StartAsync(nominatim);

        var passenger = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Passenger(passenger);

        await harness.GetJsonAsync("/v1/geo/search?q=Colombo%20Fort", bearer);
        await harness.GetJsonAsync("/v1/geo/search?q=Colombo%20Fort", bearer);

        Assert.Single(nominatim.Requests);
    }

    [Fact]
    public async Task A_reverse_lookup_labels_a_dropped_pin()
    {
        await using var nominatim = await FakeNominatim.StartAsync();
        await using var harness = await StartAsync(nominatim);

        var passenger = await harness.CreateUserAsync();

        var body = await harness.GetJsonAsync(
            "/v1/geo/reverse?lat=6.9271&lng=79.8612", harness.Tokens.Passenger(passenger));

        Assert.Equal("Galle Face Green, Colombo 03", body.GetProperty("displayName").GetString());
        Assert.Equal("nominatim", body.GetProperty("source").GetString());

        var request = Assert.Single(nominatim.Requests);
        Assert.StartsWith("/reverse?", request, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nominatim answers a coordinate it cannot place — the middle of the sea — with a 404 and an error
    /// body. That is a real answer to a real question and must not read as an outage.
    /// </summary>
    [Fact]
    public async Task A_coordinate_with_no_addressable_place_is_not_found()
    {
        await using var nominatim = await FakeNominatim.StartAsync();
        await using var harness = await StartAsync(nominatim);

        nominatim.ReverseResult = null;

        var passenger = await harness.CreateUserAsync();

        using var response = await harness.GetAsync(
            "/v1/geo/reverse?lat=0&lng=0", harness.Tokens.Passenger(passenger));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// With no geocoder configured, search still answers from the caller's own places and reverse says it
    /// cannot answer. There is no third-party fallback anywhere in either path (D3' map hard rule).
    /// </summary>
    [Fact]
    public async Task Without_a_geocoder_search_degrades_and_reverse_refuses()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Passenger(passenger);

        await harness.AddSavedAddressAsync(
            passenger, "Work", "1 Union Place", "Colombo 02", new GeoPoint(6.9200, 79.8600));

        var search = await harness.GetJsonAsync("/v1/geo/search?q=Union", bearer);

        var place = Assert.Single(search.GetProperty("places").EnumerateArray().ToArray());
        Assert.Equal("saved", place.GetProperty("source").GetString());

        using var reverse = await harness.GetAsync("/v1/geo/reverse?lat=6.9271&lng=79.8612", bearer);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reverse.StatusCode);
        Assert.Equal("application/problem+json", reverse.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_missing_or_oversized_query_is_refused()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Passenger(passenger);

        using var empty = await harness.GetAsync("/v1/geo/search", bearer);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using var oversized = await harness.GetAsync("/v1/geo/search?q=" + new string('x', 201), bearer);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }

    private Task<QueryHarness> StartAsync(FakeNominatim nominatim) =>
        QueryHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?> { ["Query:NominatimBaseUrl"] = nominatim.BaseUrl });
}
