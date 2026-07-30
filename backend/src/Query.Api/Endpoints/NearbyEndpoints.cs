using MageRide.Query.Configuration;
using MageRide.Query.Live;
using MageRide.Query.Destinations;
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
/// The live-map surface: <c>/v1/nearby</c>, <c>/v1/routes/{routeNumber}/buses</c> and
/// <c>/v1/transport-options</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authenticated, not role-gated.</b> D3' marks all three <c>Bearer (passenger)</c>, and a driver
/// opening the passenger side of their own app is a real principal — C020 decision 4 established that
/// holding one role does not imply another, and a role gate here would refuse a driver looking for a
/// bus home. What every one of them <em>does</em> need is a <c>sub</c>: the visibility rules are per
/// viewer (D-23's entitlement, US-7.16's own ride), so an anonymous nearby read is not a weaker version
/// of this endpoint, it is a different one — and it is public-bff's (C065), token-scoped.
/// </para>
/// <para>
/// <b>Validation is here and clamping is not.</b> A radius above the contract's ceiling is a
/// <c>400</c> rather than a silent clamp: a client asking for 50 km and receiving 20 km worth of
/// vehicles would conclude the country is empty. The <em>default</em> is applied silently, because an
/// absent parameter is the contract's own case.
/// </para>
/// </remarks>
public static class NearbyEndpoints
{
    public static IEndpointRouteBuilder MapNearbyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/v1/nearby", NearbyAsync)
            .WithTags("nearby")
            .WithName("getNearbyVehicles")
            .RequireAuthorization();

        endpoints.MapGet("/v1/routes/{routeNumber}/buses", RouteBusesAsync)
            .WithTags("nearby")
            .WithName("getBusesOnRoute")
            .RequireAuthorization();

        endpoints.MapGet("/v1/transport-options", TransportOptionsAsync)
            .WithTags("nearby")
            .WithName("getTransportOptions")
            .RequireAuthorization();

        return endpoints;
    }

    private static async Task<Ok<NearbyResponse>> NearbyAsync(
        double? lat,
        double? lng,
        int? radius,
        string? types,
        string? modes,
        HttpContext context,
        INearbyService nearby,
        IOptions<QueryOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(nearby);
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        var centre = RequirePoint(lat, lng);
        var radiusM = radius ?? settings.DefaultRadiusM;

        if (radiusM < 1 || radiusM > settings.MaxRadiusM)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["radius"] = [$"radius must be between 1 and {settings.MaxRadiusM} metres."],
            });
        }

        var snapshot = await nearby.SearchAsync(
            new NearbyQuery(
                context.User.RequireSubjectId(),
                centre,
                radiusM,
                ParseSet(types, "types", NormaliseType),
                ParseSet(modes, "modes", NormaliseMode)),
            cancellationToken);

        return TypedResults.Ok(NearbyResponse.From(snapshot));
    }

    /// <summary>
    /// US-7.9. A route nothing is running on is <c>200</c> with an empty list; a route number that
    /// exists nowhere is <c>404</c>.
    /// </summary>
    /// <remarks>
    /// The distinction is the difference between "the 138 has finished for the night" and "there is no
    /// 138", and a client cannot show US-7.14's "no vehicles active" message without it.
    /// </remarks>
    private static async Task<Ok<NearbyResponse>> RouteBusesAsync(
        string routeNumber,
        HttpContext context,
        ILiveReadRepository repository,
        INearbyService nearby,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(nearby);

        if (string.IsNullOrWhiteSpace(routeNumber) || routeNumber.Length > 32)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["routeNumber"] = ["routeNumber must be 1 to 32 characters."],
            });
        }

        var vehicles = await repository.ReadRouteVehiclesAsync(routeNumber, cancellationToken)
                       ?? throw new MageRideException(
                           MageRideErrors.NotFound, $"No route carries the number '{routeNumber}'.");

        // The same visibility filter as the map. A bus is Mode A and therefore always public, but the
        // freshness rule still applies: a vehicle whose driver's phone died an hour ago is not running
        // the route, whatever its session still says.
        var snapshot = await nearby.SnapshotAsync(
            context.User.RequireSubjectId(), vehicles, etaTarget: null, cancellationToken);

        return TypedResults.Ok(NearbyResponse.From(snapshot));
    }

    private static async Task<Ok<TransportOptionsResponse>> TransportOptionsAsync(
        double? toLat,
        double? toLng,
        double? fromLat,
        double? fromLng,
        IDestinationOptionsService destinations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destinations);

        var to = RequirePoint(toLat, toLng, "to");

        // D3' documents `fromLat`/`fromLng` as defaulting to "the caller's last known position", and
        // this service has no such thing for a *passenger*: `geo:live` is keyed by vehicle, because
        // EMQX authenticates a vehicle and a passenger's handset publishes nothing. Rather than
        // invent an origin, the origin is required — a client that has a map open knows where its map
        // is centred. Recorded as a finding in the C042 handoff.
        var from = fromLat.HasValue || fromLng.HasValue
            ? RequirePoint(fromLat, fromLng, "from")
            : throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["fromLat"] =
                [
                    "fromLat and fromLng are required: the platform holds no last-known position for a "
                    + "passenger, only for a vehicle.",
                ],
            });

        var options = await destinations.OptionsAsync(from, to, cancellationToken);

        return TypedResults.Ok(
            new TransportOptionsResponse([.. options.Select(TransportOptionResponse.From)]));
    }

    /// <summary>
    /// Canonical vehicle types are lower-case with underscores (AL-09: <c>three_wheeler</c>,
    /// <c>mini_van</c>).
    /// </summary>
    private static string NormaliseType(string value) => value.ToLowerInvariant();

    /// <summary>
    /// Operating modes are the single upper-case letters <c>A</c>, <c>B</c> and <c>C</c> (D5' §2), which
    /// is how <c>veh:meta.mode</c> holds them.
    /// </summary>
    /// <remarks>
    /// Normalised per field rather than once for both, because the two enumerations have opposite case
    /// conventions and a shared <c>ToLowerInvariant</c> silently turns <c>modes=C</c> into a filter that
    /// matches nothing — an empty map with no error anywhere, which is exactly the failure shape this
    /// service is written against.
    /// </remarks>
    private static string NormaliseMode(string value) => value.ToUpperInvariant();

    /// <summary>
    /// Parses a repeated or comma-separated query parameter into a set.
    /// </summary>
    /// <remarks>
    /// The contract declares both filters as <c>explode: false</c> arrays, which on the wire is
    /// <c>types=bus,train</c>. Repeated parameters are accepted too, because that is what several HTTP
    /// clients emit for an array regardless of the OpenAPI style, and refusing them would fail a
    /// request whose meaning is unambiguous.
    /// </remarks>
    private static IReadOnlySet<string> ParseSet(string? raw, string field, Func<string, string> normalise)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var values = raw
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(normalise)
            .ToHashSet(StringComparer.Ordinal);

        if (values.Count == 0)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [$"{field} was present but held no values."],
            });
        }

        return values;
    }

    private static GeoPoint RequirePoint(double? lat, double? lng, string prefix = "")
    {
        var latField = prefix.Length == 0 ? "lat" : prefix + "Lat";
        var lngField = prefix.Length == 0 ? "lng" : prefix + "Lng";

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (lat is null || double.IsNaN(lat.Value) || lat is < -90 or > 90)
        {
            errors[latField] = [$"{latField} is required and must be between -90 and 90."];
        }

        if (lng is null || double.IsNaN(lng.Value) || lng is < -180 or > 180)
        {
            errors[lngField] = [$"{lngField} is required and must be between -180 and 180."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        return new GeoPoint(lat!.Value, lng!.Value);
    }
}
