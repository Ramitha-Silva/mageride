using System.Text.Json.Serialization;
using MageRide.Shared.Errors;
using MageRide.Transit.Domain;
using MageRide.Transit.Feed;
using MageRide.Transit.Geo;
using MageRide.Transit.Routing;

namespace MageRide.Transit.Endpoints;

/// <summary>One boarding-to-alighting ride on a single route.</summary>
public sealed record TransitLegBody(
    string RouteId,
    string RouteShortName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Headsign,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Description,
    string BoardStopId,
    string AlightStopId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Shape);

/// <summary>A direct ride, or one with a transfer (AL-18).</summary>
public sealed record TransitOptionBody(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? TotalDurationSec,
    int WalkingDistanceM,
    IReadOnlyList<TransitLegBody> Legs);

/// <summary>The answer to `GET /v1/transit/options`.</summary>
/// <param name="Coverage">
/// <b>Δ C056.</b> `active` when the answer came from a live feed, `no_feed` when none is activated.
/// Without it an empty list means two different things — "no bus goes there" and "we cannot
/// tell" — and SCR-PA-009 has to render them differently: the first is an answer, the second is
/// AL-55's degradation, where the screen keeps live buses and private tiers and hides route
/// matching.
/// </param>
public sealed record TransitOptionsResponse(
    IReadOnlyList<TransitOptionBody> Options,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FeedVersion,
    string Coverage);

/// <summary>A halt on a route, or near the caller.</summary>
public sealed record TransitStopBody(
    string StopId,
    string Name,
    double Lat,
    double Lng,
    int? Sequence = null,
    int? DistanceM = null);

/// <summary>The answer to `GET /v1/transit/routes/{routeId}`.</summary>
public sealed record TransitRouteResponse(
    string RouteId,
    string RouteShortName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RouteLongName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? AgencyName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Shape,
    IReadOnlyList<TransitStopBody> Stops,
    IReadOnlyList<TransitStopBody>? NearestStops);

/// <summary>The answer to `GET /v1/geo/parse-maps-link`.</summary>
public sealed record ParsedLinkResponse(double Lat, double Lng, string? Label);

/// <summary>
/// transit-svc's routing surface: two GTFS reads and the paste-link resolver.
/// </summary>
/// <remarks>
/// <para>
/// <b>AL-17 is held by an absence of capability.</b> A destination is a geo-location: every route
/// on this surface takes coordinates, and there is no parameter anywhere that accepts a route
/// number as a <em>destination</em>. `{routeId}` on the detail route is the opposite direction —
/// the passenger has already chosen an option and is looking at it.
/// </para>
/// <para>
/// The GTFS Dataset Manager (`/v1/admin/transit/gtfs/**`, AL-54) is <b>C057</b> and is deliberately
/// not mapped here; this component only consumes what activation publishes.
/// </para>
/// </remarks>
public static class TransitEndpoints
{
    /// <summary>Δ C056 — the two values of `coverage`.</summary>
    public const string CoverageActive = "active";

    /// <summary>AL-55's safety net: no feed has been activated, so route matching cannot answer.</summary>
    public const string CoverageNoFeed = "no_feed";

    public static IEndpointRouteBuilder MapTransitEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var transit = routes.MapGroup("/v1/transit").RequireAuthorization().WithTags("transit");

        transit.MapGet("/options", GetOptions)
            .WithName("getTransitOptions")
            .WithSummary("All direct and transfer routes between two points (BR-23.2).");

        transit.MapGet("/routes/{routeId}", GetRoute)
            .WithName("getTransitRoute")
            .WithSummary("Route detail with its shape and nearest halts.");

        routes.MapGroup("/v1/geo").RequireAuthorization().WithTags("transit")
            .MapGet("/parse-maps-link", ParseMapsLink)
            .WithName("parseMapsLink")
            .WithSummary("Resolve a shared Google Maps link to a coordinate (AL-20).");

        return routes;
    }

    private static IResult GetOptions(
        double fromLat, double fromLng, double toLat, double toLng,
        ITransitRouting routing,
        IGtfsFeedCache cache)
    {
        RequireCoordinate(fromLat, fromLng, "from");
        RequireCoordinate(toLat, toLng, "to");

        var result = routing.Options(fromLat, fromLng, toLat, toLng);
        var feed = cache.Current;

        return Results.Ok(new TransitOptionsResponse(
            [.. result.Options.Select(option => ToBody(option, feed))],
            result.FeedVersion,
            result.HasActiveFeed ? CoverageActive : CoverageNoFeed));
    }

    private static IResult GetRoute(string routeId, double? lat, double? lng, IGtfsFeedCache cache)
    {
        var feed = cache.Current;
        var route = feed.Route(routeId);

        if (route is null)
        {
            // The same answer whether no feed is loaded or the feed has no such route: a route id
            // is only ever obtained from an options answer, so both mean "not in the active feed".
            throw new MageRideException(MageRideErrors.NotFound, "No such route in the active feed.");
        }

        // The route's fullest pattern is what a detail screen draws: it is the sequence that
        // reaches every halt the route serves, and a short-turn working would draw half the line.
        var pattern = feed.Patterns
            .Where(candidate => string.Equals(candidate.RouteId, routeId, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.StopIds.Count)
            .FirstOrDefault();

        var stops = pattern is null
            ? []
            : pattern.StopIds
                .Select((stopId, index) => (Stop: feed.Stop(stopId), Index: index))
                .Where(entry => entry.Stop is not null)
                .Select(entry => new TransitStopBody(
                    entry.Stop!.StopId, entry.Stop.Name, entry.Stop.Lat, entry.Stop.Lng, entry.Index))
                .ToArray();

        IReadOnlyList<TransitStopBody>? nearest = null;

        if (lat is { } referenceLat && lng is { } referenceLng)
        {
            RequireCoordinate(referenceLat, referenceLng, "reference");

            // Nearest halts *on this route*, not in the feed — the question the screen asks is
            // "where do I catch this bus", and the closest halt overall may be on another line.
            nearest = [.. stops
                .Select(stop => stop with
                {
                    DistanceM = (int)Math.Round(Haversine.DistanceM(referenceLat, referenceLng, stop.Lat, stop.Lng)),
                })
                .OrderBy(stop => stop.DistanceM)
                .Take(5)];
        }

        return Results.Ok(new TransitRouteResponse(
            route.RouteId,
            route.ShortName ?? route.RouteId,
            route.LongName,
            route.Agency,
            feed.Shape(pattern?.ShapeId),
            stops,
            nearest));
    }

    private static async Task<IResult> ParseMapsLink(
        string url, IMapsLinkResolver resolver, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048)
        {
            throw new MageRideException(MageRideErrors.ValidationFailed, "url is required.");
        }

        var location = await resolver.ResolveAsync(url, cancellationToken);

        if (location is null)
        {
            // BR-23.4's Error state: "couldn't read that link — pick on map". A 422 rather than a
            // 400, because the request was well-formed and the *link* is what could not be read.
            throw new MageRideException(
                MageRideErrors.RouteUnavailable, "That link could not be read as a location.");
        }

        return Results.Ok(new ParsedLinkResponse(location.Lat, location.Lng, location.Label));
    }

    private static TransitOptionBody ToBody(RouteOption option, GtfsFeed feed) => new(
        option.IsDirect ? "direct" : "transit",
        option.TotalDurationSec,
        option.WalkingDistanceM,
        [.. option.Legs.Select(leg =>
        {
            var route = feed.Route(leg.Pattern.RouteId);

            return new TransitLegBody(
                leg.Pattern.RouteId,
                route?.ShortName ?? leg.Pattern.RouteId,
                // A feed that carries no `trip_headsign` still needs a destination on the card, and
                // `route_long_name` is what every Sri Lankan feed puts it in ("Colombo – Kandy").
                leg.Pattern.Headsign ?? route?.LongName,
                route?.LongName,
                leg.BoardStop.StopId,
                leg.AlightStop.StopId,
                feed.Shape(leg.Pattern.ShapeId));
        })]);

    private static void RequireCoordinate(double lat, double lng, string name)
    {
        if (lat is < -90 or > 90 || lng is < -180 or > 180 || double.IsNaN(lat) || double.IsNaN(lng))
        {
            throw new MageRideException(
                MageRideErrors.ValidationFailed, $"{name} is not a coordinate on the globe.");
        }
    }
}
