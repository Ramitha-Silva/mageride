using System.Text.Json.Serialization;
using MageRide.Query.Geo;
using MageRide.Query.Live;
using MageRide.Query.Destinations;
using MageRide.Query.Persistence;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;

namespace MageRide.Query.Endpoints;

/// <summary>D3' <c>NearbyVehicle</c>.</summary>
public sealed record NearbyVehicleResponse(
    string VehicleId,
    string Type,
    string Mode,
    double Lat,
    double Lng,
    int? Heading,
    double? Speed,
    string? DriverName,
    int? EtaSeconds,
    string? RegistrationNumber)
{
    public static NearbyVehicleResponse From(NearbyVehicleView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new NearbyVehicleResponse(
            view.VehicleId.ToString(),
            view.Type,
            view.Mode,
            view.Point.Latitude,
            view.Point.Longitude,
            view.HeadingDeg,
            view.SpeedMps,
            view.DriverName,
            view.EtaSeconds,
            view.RegistrationNumber);
    }
}

/// <summary>
/// The 200 of <c>GET /v1/nearby</c> and of <c>GET /v1/routes/{routeNumber}/buses</c>.
/// </summary>
/// <param name="Vehicles">What the caller may see.</param>
/// <param name="AsOf">
/// When the snapshot was taken. A client holding a socket frame uses it to decide which is newer —
/// <c>signalr-hub.md</c> §1.1 makes this endpoint the resync path, and a resync that overwrote fresher
/// live frames would make every reconnect jump the markers backwards.
/// </param>
/// <param name="LimitedLive">
/// ADD §12's degradation flag: the live index was unreachable, so this is not a full answer. Always
/// serialised, including when false — a client that could not tell "no vehicles" from "we do not know"
/// would render an outage as an empty city.
/// </param>
public sealed record NearbyResponse(
    IReadOnlyList<NearbyVehicleResponse> Vehicles,
    DateTimeOffset AsOf,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool LimitedLive)
{
    public static NearbyResponse From(NearbySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new NearbyResponse(
            [.. snapshot.Vehicles.Select(NearbyVehicleResponse.From)],
            snapshot.AsOf,
            snapshot.LimitedLive);
    }
}

/// <summary>D3' <c>Place</c>.</summary>
public sealed record PlaceResponse(double Lat, double Lng, string? DisplayName)
{
    public static PlaceResponse? From(GeoPoint? point, string? displayName = null) =>
        point is { } value ? new PlaceResponse(value.Latitude, value.Longitude, displayName) : null;
}

/// <summary>D3' <c>TripSummary</c>.</summary>
public sealed record TripSummaryResponse(
    string TripId,
    string Plane,
    string? Mode,
    PlaceResponse? Pickup,
    PlaceResponse? Dropoff,
    long? FareMinor,
    string Currency,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt)
{
    public static TripSummaryResponse From(TripSummaryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new TripSummaryResponse(
            row.TripId.ToString(),
            row.Plane,
            row.Mode,
            PlaceResponse.From(row.Pickup),
            PlaceResponse.From(row.Dropoff),
            row.FareMinor,
            row.Currency,
            row.StartedAt,
            row.EndedAt);
    }
}

/// <summary>The driver half of a trip receipt.</summary>
public sealed record TripDriverResponse(string DriverId, string? Name, string? RegistrationNumber);

/// <summary>D3' <c>TripDetail</c>.</summary>
/// <param name="Polyline">
/// The track, as an encoded polyline (Google's algorithm, precision 5 — what MapLibre's
/// <c>LineLayer</c> consumes for MAP-08). Absent when the journey produced fewer than two points.
/// </param>
/// <param name="GeometrySource">
/// Which relation the line came from and at what grain. <b>Not in D3'</b> — a C042 micro-change-set,
/// added to <c>query.yaml</c> in the same change. It is the difference between a full-resolution
/// Mode A/B track and a Mode C line sampled once a minute, and a client drawing both without knowing
/// which is which would present a twelve-point approximation as the route that was driven.
/// </param>
public sealed record TripDetailResponse(
    string TripId,
    string Plane,
    string? Mode,
    PlaceResponse? Pickup,
    PlaceResponse? Dropoff,
    long? FareMinor,
    string Currency,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? Polyline,
    string GeometrySource,
    double? DistanceKm,
    int? DurationSec,
    TripDriverResponse? Driver,
    int? Rating)
{
    public static TripDetailResponse From(TripDetailRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new TripDetailResponse(
            row.Summary.TripId.ToString(),
            row.Summary.Plane,
            row.Summary.Mode,
            PlaceResponse.From(row.Summary.Pickup),
            PlaceResponse.From(row.Summary.Dropoff),
            row.Summary.FareMinor,
            row.Summary.Currency,
            row.Summary.StartedAt,
            row.Summary.EndedAt,
            EncodedPolyline.Encode(row.Path),
            row.GeometrySource,
            row.DistanceKm,
            row.DurationSec,
            row.DriverId is { } driverId
                ? new TripDriverResponse(driverId.ToString(), row.DriverName, row.RegistrationNumber)
                : null,
            row.Rating);
    }
}

/// <summary>D3' <c>EarningsSummary</c>.</summary>
public sealed record EarningsSummaryResponse(
    string Period,
    DateOnly RangeFrom,
    DateOnly RangeTo,
    long GrossMinor,
    long DailyFeeMinor,
    long PenaltyMinor,
    long TipMinor,
    long NetMinor,
    string Currency,
    int Trips)
{
    public static EarningsSummaryResponse From(
        string period, DateOnly from, DateOnly to, EarningsTotals totals)
    {
        ArgumentNullException.ThrowIfNull(totals);

        return new EarningsSummaryResponse(
            period,
            from,
            to,
            totals.GrossMinor,
            totals.DailyFeeMinor,
            totals.PenaltyMinor,
            totals.TipMinor,
            totals.NetMinor,
            "LKR",
            totals.Trips);
    }
}

/// <summary>D3' <c>SessionEarning</c>.</summary>
/// <remarks>
/// <c>dailyFeeMinor</c> and <c>penaltyMinor</c> are deliberately absent from a per-ride row. A daily
/// fee is a fact about a <em>day</em> (D-13 charges it once, before the second trip) and the D-05
/// penalty is a fact about a cancellation on some other journey; splitting either across the rides of
/// a day would make every row's net wrong in a different way, and D3' marks both optional on this
/// object while requiring them on the summary. The summary is where they are reported.
/// </remarks>
public sealed record SessionEarningResponse(
    string TripId, long GrossMinor, long TipMinor, long NetMinor, string Currency, DateTimeOffset EndedAt)
{
    public static SessionEarningResponse From(SessionEarningRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new SessionEarningResponse(
            row.TripId.ToString(), row.GrossMinor, row.TipMinor, row.NetMinor, row.Currency, row.EndedAt);
    }
}

/// <summary>D3' <c>GeocodedPlace</c>.</summary>
/// <param name="Source">
/// <c>nominatim</c> | <c>saved</c> | <c>recent</c> — BR-23.1's three prediction sources, and never a
/// fourth (AL-17).
/// </param>
/// <param name="Label">The user's own name for a saved address (AL-26); null for the other two.</param>
public sealed record GeocodedPlaceResponse(
    double Lat, double Lng, string DisplayName, string? Line1, string? City, string Source, string? Label)
{
    public static GeocodedPlaceResponse From(GeocodedPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);

        return new GeocodedPlaceResponse(
            place.Point.Latitude,
            place.Point.Longitude,
            place.DisplayName,
            place.Line1,
            place.City,
            PlaceSources.Geocoded,
            null);
    }

    public static GeocodedPlaceResponse From(KnownPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);

        return new GeocodedPlaceResponse(
            place.Point.Latitude,
            place.Point.Longitude,
            place.DisplayName,
            place.Line1,
            place.City,
            place.Source,
            place.Label);
    }
}

/// <summary>The 200 of <c>GET /v1/geo/search</c>.</summary>
public sealed record PlaceSearchResponse(IReadOnlyList<GeocodedPlaceResponse> Places);

/// <summary>D3' <c>TransportOption</c>.</summary>
public sealed record TransportOptionResponse(
    string Kind,
    string Label,
    string? VehicleType,
    string? RouteNumber,
    long? EstimatedFareMinor,
    string? Currency,
    int? Transfers)
{
    public static TransportOptionResponse From(TransportOption option)
    {
        ArgumentNullException.ThrowIfNull(option);

        return new TransportOptionResponse(
            option.Kind,
            option.Label,
            option.VehicleType,
            option.RouteNumber,
            option.EstimatedFareMinor,
            option.Currency,
            option.Transfers);
    }
}

/// <summary>The 200 of <c>GET /v1/transport-options</c>.</summary>
public sealed record TransportOptionsResponse(IReadOnlyList<TransportOptionResponse> Options);
