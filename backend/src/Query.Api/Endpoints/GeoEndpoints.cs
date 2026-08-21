using MageRide.Query.Configuration;
using MageRide.Query.Geo;
using MageRide.Query.Persistence;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace MageRide.Query.Endpoints;

/// <summary>
/// <c>/v1/geo</c> — forward and reverse geocoding against the self-hosted Nominatim (D-14, D-15).
/// </summary>
/// <remarks>
/// <para>
/// <b>AL-17 is held by the shape of this code, not by a filter.</b> "Destination is a geo-location
/// only. Search returns geocoded places + saved/recent (no route rows)" — and the search path has no
/// access to <c>spatial.routes</c>, <c>transit.gtfs_routes</c> or any other relation that holds a route.
/// <see cref="IPlaceRepository"/> can reach exactly two tables and <see cref="IGeocoder"/> reaches an OSM
/// place index. A passenger typing "138" gets whatever Nominatim believes "138" is — a house number, an
/// address fragment — and cannot get a bus route, because nothing here is able to return one. A filter
/// would be a line of code somebody could delete; this is an absence of capability.
/// </para>
/// <para>
/// <b>Predictions are the union of three sources and the caller's own come first.</b> BR-23.1's set is
/// "geocoded places (Nominatim/Photon) + saved/recent addresses". A saved Home is a better answer than a
/// geocoder's guess at the same string, so saved sorts above recent and recent above geocoded — and each
/// row says which it is (<c>source</c>), because a client renders a house icon, a clock and a pin
/// differently.
/// </para>
/// <para>
/// <b>An unconfigured geocoder degrades forward search and refuses reverse.</b> Search still has the
/// caller's own places to offer, which is a real answer. A reverse lookup has no local equivalent at all
/// — there is nothing to reverse a coordinate against — so it is <c>503 dependency-unavailable</c> rather
/// than a <c>404</c> claiming no such place exists.
/// </para>
/// </remarks>
public static class GeoEndpoints
{
    public static IEndpointRouteBuilder MapGeoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var geo = endpoints.MapGroup("/v1/geo").WithTags("geo").RequireAuthorization();

        geo.MapGet("/search", SearchAsync).WithName("searchPlaces");
        geo.MapGet("/reverse", ReverseAsync).WithName("reverseGeocode");

        return endpoints;
    }

    private static async Task<Ok<PlaceSearchResponse>> SearchAsync(
        string? q,
        double? lat,
        double? lng,
        int? limit,
        string? lang,
        HttpContext context,
        IGeocoder geocoder,
        IPlaceRepository places,
        IOptions<QueryOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(geocoder);
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(q) || q.Length > 200)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["q"] = ["q is required and must be 1 to 200 characters."],
            });
        }

        var settings = options.Value;
        var page = PageRequest.Create(null, limit);
        var userId = context.User.RequireSubjectId();
        var bias = lat.HasValue && lng.HasValue && lat is >= -90 and <= 90 && lng is >= -180 and <= 180
            ? new GeoPoint(lat.Value, lng.Value)
            : (GeoPoint?)null;

        var saved = await places.SavedAsync(userId, q, settings.SavedPlaceLimit, cancellationToken);
        var recent = await places.RecentAsync(userId, settings.RecentPlaceLimit, cancellationToken);

        // Only the geocoded half of the answer has a language. The caller's saved and recent rows
        // carry labels the passenger typed or chose — "Home", "Amma's place" — and re-rendering
        // those in a script the platform picked would be translating someone's own words back at
        // them. They are returned as stored, which is also why they sort first.
        var geocoded = await geocoder.SearchAsync(
            q, bias, page.Limit, GeoLanguages.TryNormalise(lang), cancellationToken);

        // Deduplicated against the caller's own places by coordinate: a geocoded hit on the same
        // building as a saved "Home" is one place, and the saved one carries the label the passenger
        // gave it. Four decimals is ~11 m, the same grain the reverse cache uses.
        var known = saved
            .Concat(recent)
            .Select(GeocodedPlaceResponse.From)
            .ToArray();

        var seen = known.Select(Fingerprint).ToHashSet(StringComparer.Ordinal);

        var merged = known
            .Concat(geocoded
                .Select(GeocodedPlaceResponse.From)
                .Where(place => seen.Add(Fingerprint(place))))
            .ToArray();

        return TypedResults.Ok(new PlaceSearchResponse(merged));
    }

    private static async Task<Ok<GeocodedPlaceResponse>> ReverseAsync(
        double? lat,
        double? lng,
        string? lang,
        IGeocoder geocoder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(geocoder);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (lat is null || double.IsNaN(lat.Value) || lat is < -90 or > 90)
        {
            errors["lat"] = ["lat is required and must be between -90 and 90."];
        }

        if (lng is null || double.IsNaN(lng.Value) || lng is < -180 or > 180)
        {
            errors["lng"] = ["lng is required and must be between -180 and 180."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        if (!geocoder.IsConfigured)
        {
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "Reverse geocoding is unavailable: no Nominatim endpoint is configured.");
        }

        var place = await geocoder.ReverseAsync(
                        new GeoPoint(lat!.Value, lng!.Value),
                        GeoLanguages.TryNormalise(lang),
                        cancellationToken)
                    ?? throw new MageRideException(
                        MageRideErrors.NotFound, "No addressable place was found at that coordinate.");

        return TypedResults.Ok(GeocodedPlaceResponse.From(place));
    }

    private static string Fingerprint(GeocodedPlaceResponse place) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{Math.Round(place.Lat, 4)}:{Math.Round(place.Lng, 4)}");
}
