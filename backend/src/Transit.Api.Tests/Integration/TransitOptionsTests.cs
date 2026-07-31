using System.Net;
using MageRide.Shared.Geo;
using MageRide.TestKit;
using MageRide.Transit.Endpoints;
using MageRide.Transit.Tests.Infrastructure;

namespace MageRide.Transit.Tests.Integration;

/// <summary>
/// <b>Definition of done: "a corridor with a known direct route returns it with the correct shape
/// polyline."</b>
/// </summary>
/// <remarks>
/// The corridor is Colombo Fort → Kottawa and the route is 138, at their real coordinates, loaded
/// through the same tables C057's importer will write and the same cache the process runs.
/// </remarks>
[Collection(TransitCollection.Name)]
public sealed class TransitOptionsTests(PostgresFixture postgres)
{
    private const string FortToKottawa =
        "/v1/transit/options?fromLat=6.9344&fromLng=79.8428&toLat=6.8410&toLng=79.9653";

    [Fact]
    public async Task A_corridor_with_a_known_direct_route_returns_it_with_its_shape()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        await harness.Seed.ActivateAsync();

        var answer = await harness.WaitForAsync<TransitOptionsResponse>(
            FortToKottawa, result => result.Options.Count > 0);

        Assert.Equal(TransitEndpoints.CoverageActive, answer.Coverage);
        Assert.Equal("2026-07-01", answer.FeedVersion);

        var direct = Assert.Single(answer.Options, option => option.Kind == "direct");
        var leg = Assert.Single(direct.Legs);

        Assert.Equal("R138", leg.RouteId);
        Assert.Equal("138", leg.RouteShortName);
        Assert.Equal("Kottawa", leg.Headsign);
        Assert.Equal("FORT", leg.BoardStopId);
        Assert.Equal("KTW", leg.AlightStopId);

        // The polyline is DECODED rather than compared with a string: an encoder checked against
        // its author's own expectation is checked against nothing.
        var shape = EncodedPolyline.Decode(leg.Shape);

        Assert.Equal(5, shape.Count);
        Assert.Equal(6.9344, shape[0].Latitude, 5);
        Assert.Equal(79.8428, shape[0].Longitude, 5);
        Assert.Equal(6.8410, shape[^1].Latitude, 5);
        Assert.Equal(79.9653, shape[^1].Longitude, 5);

        // 55 minutes end to end, from the seeded stop times.
        Assert.Equal(55 * 60, direct.TotalDurationSec);
    }

    [Fact]
    public async Task The_return_direction_is_direct_too_and_carries_its_own_shape()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        await harness.Seed.ActivateAsync();

        await harness.WaitForAsync<TransitOptionsResponse>(FortToKottawa, result => result.Options.Count > 0);

        var answer = await harness.GetAsync<TransitOptionsResponse>(
            "/v1/transit/options?fromLat=6.8410&fromLng=79.9653&toLat=6.9344&toLng=79.8428");

        var leg = Assert.Single(Assert.Single(answer.Options, option => option.Kind == "direct").Legs);

        Assert.Equal("R138", leg.RouteId);
        Assert.Equal("Colombo Fort", leg.Headsign);

        var shape = EncodedPolyline.Decode(leg.Shape);

        Assert.Equal(6.8410, shape[0].Latitude, 5);
    }

    [Fact]
    public async Task A_route_a_hundred_kilometres_away_is_not_an_option()
    {
        // "All direct routes" is a claim about the corridor, not about the feed.
        await using var harness = await TransitHarness.StartAsync(postgres);

        await harness.Seed.ActivateAsync();

        var answer = await harness.WaitForAsync<TransitOptionsResponse>(
            FortToKottawa, result => result.Options.Count > 0);

        Assert.DoesNotContain(answer.Options.SelectMany(option => option.Legs), leg => leg.RouteId == "R999");
    }

    [Fact]
    public async Task A_corridor_needing_a_change_is_offered_as_a_transit_option()
    {
        // Fort → Battaramulla: the 138 to Nugegoda, then the 154. Listed as `transit`, below any
        // direct option (BR-23.2).
        await using var harness = await TransitHarness.StartAsync(postgres);

        await harness.Seed.ActivateAsync();

        var answer = await harness.WaitForAsync<TransitOptionsResponse>(
            "/v1/transit/options?fromLat=6.9344&fromLng=79.8428&toLat=6.8991&toLng=79.9188",
            result => result.Options.Count > 0);

        var option = Assert.Single(answer.Options, candidate => candidate.Kind == "transit");

        Assert.Equal(2, option.Legs.Count);
        Assert.Equal("R138", option.Legs[0].RouteId);
        Assert.Equal("NUG", option.Legs[0].AlightStopId);
        Assert.Equal("R154", option.Legs[1].RouteId);
        Assert.Equal("NUG", option.Legs[1].BoardStopId);
        Assert.Equal("BTM", option.Legs[1].AlightStopId);
    }

    [Fact]
    public async Task Route_detail_carries_the_shape_and_the_halts_in_order()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        await harness.Seed.ActivateAsync();

        await harness.WaitForAsync<TransitOptionsResponse>(FortToKottawa, result => result.Options.Count > 0);

        var route = await harness.GetAsync<TransitRouteResponse>("/v1/transit/routes/R138");

        Assert.Equal("138", route.RouteShortName);
        Assert.Equal("Colombo Fort – Kottawa", route.RouteLongName);
        Assert.Equal("SLTB", route.AgencyName);

        // The fullest pattern, not the short-turn working: a detail screen draws the whole line.
        Assert.Equal(["FORT", "MRD", "NUG", "MHR", "KTW"], route.Stops.Select(stop => stop.StopId));
        Assert.Equal([0, 1, 2, 3, 4], route.Stops.Select(stop => stop.Sequence));
        Assert.Equal(5, EncodedPolyline.Decode(route.Shape).Count);
        Assert.Null(route.NearestStops);
    }

    [Fact]
    public async Task Route_detail_names_the_nearest_halts_when_a_reference_point_is_given()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        await harness.Seed.ActivateAsync();

        await harness.WaitForAsync<TransitOptionsResponse>(FortToKottawa, result => result.Options.Count > 0);

        // Standing at Maharagama.
        var route = await harness.GetAsync<TransitRouteResponse>(
            "/v1/transit/routes/R138?lat=6.8482&lng=79.9265");

        Assert.NotNull(route.NearestStops);
        Assert.Equal("MHR", route.NearestStops![0].StopId);
        Assert.InRange(route.NearestStops[0].DistanceM!.Value, 0, 50);

        // Nearest on THIS route, ordered outward — the question is "where do I catch this bus".
        Assert.True(route.NearestStops.Zip(route.NearestStops.Skip(1))
            .All(pair => pair.First.DistanceM <= pair.Second.DistanceM));
    }

    [Fact]
    public async Task A_route_that_is_not_in_the_active_feed_is_a_404()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        await harness.Seed.ActivateAsync();

        await harness.WaitForAsync<TransitOptionsResponse>(FortToKottawa, result => result.Options.Count > 0);

        using var response = await harness.GetAsync("/v1/transit/routes/R000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_coordinate_that_is_not_on_the_globe_is_refused()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        using var response = await harness.GetAsync(
            "/v1/transit/options?fromLat=400&fromLng=79.8428&toLat=6.8410&toLng=79.9653");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        using var response = await harness.GetAsync(FortToKottawa, authenticated: false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task There_is_no_way_to_ask_for_a_route_number_as_a_destination()
    {
        // AL-17 is held by an absence of capability: every parameter on this surface is a
        // coordinate, so a passenger who types "138" has nowhere to send it. Asserted against the
        // running route table rather than by reading the source.
        await using var harness = await TransitHarness.StartAsync(postgres);

        var routes = harness.Services
            .GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))
            .ToArray();

        Assert.Equal(
            ["/v1/geo/parse-maps-link", "/v1/transit/options", "/v1/transit/routes/{routeId}"],
            routes
                .Where(route => route.StartsWith("/v1/", StringComparison.Ordinal))
                .Where(route => !route.StartsWith("/v1/admin/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));

        // Δ C057 — the GTFS Dataset Manager is mapped here now, and it does not weaken AL-17: every
        // parameter on it names a *feed version*, which is a dataset an operator uploaded, never a
        // place a passenger is going. Named in full so a route added to either half fails this
        // test rather than quietly widening the surface.
        Assert.Equal(
            [
                "/v1/admin/transit/gtfs/objects/{feedVersionId:guid}",
                "/v1/admin/transit/gtfs/uploads",
                "/v1/admin/transit/gtfs/uploads/{feedVersionId:guid}",
                "/v1/admin/transit/gtfs/uploads/{feedVersionId:guid}/activate",
                "/v1/admin/transit/gtfs/uploads/{feedVersionId:guid}/report",
                "/v1/admin/transit/gtfs/versions",
                "/v1/admin/transit/gtfs/versions/{feedVersionId:guid}/download",
            ],
            routes
                .Where(route => route.StartsWith("/v1/admin/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));
    }
}
