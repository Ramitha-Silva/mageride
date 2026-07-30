using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Interceptors;
using MageRide.Query.Configuration;
using MageRide.Query.Endpoints;
using MageRide.Query.Geo;
using MageRide.Query.Live;
using MageRide.Query.Persistence;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Options;

namespace MageRide.Query.Grpc;

/// <summary>
/// <c>query.v1.Query</c> — the internal read surface (ADD §6, D3' §0).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every RPC delegates to the same service the HTTP route uses.</b> Not for tidiness: the
/// visibility rules, the R-05 earning gate and the polyline's provenance each have exactly one
/// implementation, and a gRPC surface that reached for the repositories directly would be a second
/// path through which one of them could be forgotten. What is different here is only the transport and
/// the authentication.
/// </para>
/// <para>
/// <b>The viewer is a parameter, not the caller.</b> On the HTTP side the visibility rules are applied
/// on behalf of whoever presented the bearer token. Here the caller is a service and the subject is
/// somebody else, so <c>viewer_user_id</c> is required — a portal BFF rendering a passenger's map has
/// to say whose map it is, or the entitlement and own-ride rules have nothing to test against. A blank
/// value is refused rather than treated as "the public map": silently widening a per-viewer read is how
/// a back-office screen ends up showing an engaged taxi.
/// </para>
/// </remarks>
public sealed class QueryGrpcService(
    INearbyService nearby,
    ITripRepository trips,
    IEarningsRepository earnings,
    IOptions<QueryOptions> options,
    TimeProvider clock) : Query.QueryBase
{
    private readonly QueryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public override async Task<NearbyVehicles> GetNearbyVehicles(
        NearbyRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var viewerId = RequireId(request.ViewerUserId, nameof(request.ViewerUserId));
        var radiusM = request.RadiusM > 0 ? request.RadiusM : _options.DefaultRadiusM;

        if (radiusM > _options.MaxRadiusM)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, $"radius_m must not exceed {_options.MaxRadiusM}."));
        }

        var snapshot = await nearby.SearchAsync(
            new NearbyQuery(
                viewerId,
                RequirePoint(request.Lat, request.Lng),
                radiusM,
                // The two enumerations have opposite case conventions — canonical types are lower-case
                // (AL-09), modes are the upper-case letters A/B/C (D5' §2) — and getting either wrong
                // produces an empty map with no error, so both are normalised here as they are on the
                // HTTP side.
                request.VehicleTypes.Select(static type => type.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal),
                request.Modes.Select(static mode => mode.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal)),
            context.CancellationToken);

        var response = new NearbyVehicles
        {
            AsOf = Timestamp.FromDateTimeOffset(snapshot.AsOf),
            LimitedLive = snapshot.LimitedLive,
        };

        foreach (var vehicle in snapshot.Vehicles)
        {
            var message = new NearbyVehicle
            {
                VehicleId = vehicle.VehicleId.ToString(),
                Type = vehicle.Type,
                Mode = vehicle.Mode,
                Lat = vehicle.Point.Latitude,
                Lng = vehicle.Point.Longitude,
            };

            // proto3 `optional` distinguishes unset from zero, which matters for all four of these: a
            // stationary vehicle has a heading, a speed of 0 is a real measurement, and an ETA of 0
            // means "arriving now".
            if (vehicle.HeadingDeg is { } heading)
            {
                message.Heading = heading;
            }

            if (vehicle.SpeedMps is { } speed)
            {
                message.SpeedMps = speed;
            }

            if (vehicle.DriverName is { } driverName)
            {
                message.DriverName = driverName;
            }

            if (vehicle.RegistrationNumber is { } registration)
            {
                message.RegistrationNumber = registration;
            }

            if (vehicle.EtaSeconds is { } etaSeconds)
            {
                message.EtaSeconds = etaSeconds;
            }

            response.Vehicles.Add(message);
        }

        return response;
    }

    public override async Task<TripDetail> GetTripDetail(TripRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var userId = RequireId(request.UserId, nameof(request.UserId));
        var tripId = RequireId(request.TripId, nameof(request.TripId));

        var detail = await trips.GetAsync(userId, tripId, context.CancellationToken)
                     ?? throw new RpcException(new Status(StatusCode.NotFound, "No such trip."));

        var message = new TripDetail
        {
            TripId = detail.Summary.TripId.ToString(),
            Plane = detail.Summary.Plane,
            Currency = detail.Summary.Currency,
            StartedAt = Timestamp.FromDateTimeOffset(detail.Summary.StartedAt),
            GeometrySource = detail.GeometrySource,
        };

        if (detail.Summary.Mode is { } mode)
        {
            message.Mode = mode;
        }

        if (ToPlace(detail.Summary.Pickup) is { } pickup)
        {
            message.Pickup = pickup;
        }

        if (ToPlace(detail.Summary.Dropoff) is { } dropoff)
        {
            message.Dropoff = dropoff;
        }

        if (detail.Summary.FareMinor is { } fareMinor)
        {
            message.FareMinor = fareMinor;
        }

        if (detail.Summary.EndedAt is { } endedAt)
        {
            message.EndedAt = Timestamp.FromDateTimeOffset(endedAt);
        }

        if (EncodedPolyline.Encode(detail.Path) is { } polyline)
        {
            message.Polyline = polyline;
        }

        if (detail.DistanceKm is { } distanceKm)
        {
            message.DistanceKm = distanceKm;
        }

        if (detail.DurationSec is { } durationSec)
        {
            message.DurationSec = durationSec;
        }

        if (detail.DriverId is { } driverId)
        {
            message.Driver = new Driver { DriverId = driverId.ToString() };

            if (detail.DriverName is { } name)
            {
                message.Driver.Name = name;
            }

            if (detail.RegistrationNumber is { } registration)
            {
                message.Driver.RegistrationNumber = registration;
            }
        }

        if (detail.Rating is { } rating)
        {
            message.Rating = rating;
        }

        return message;
    }

    public override async Task<EarningsSummary> GetDriverEarnings(
        EarningsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var driverId = RequireId(request.DriverId, nameof(request.DriverId));
        var period = string.IsNullOrWhiteSpace(request.Period) ? EarningsPeriods.Today : request.Period;

        (DateOnly from, DateOnly to) range;

        try
        {
            range = EarningsPeriods.Resolve(period, clock);
        }
        catch (Shared.Errors.MageRideValidationException failure)
        {
            // The HTTP surface turns this into a 400 problem+json; over gRPC the equivalent is
            // InvalidArgument. Translated rather than propagated: an RpcException carrying an
            // ASP.NET-shaped problem document would be unreadable to a gRPC client.
            throw new RpcException(new Status(StatusCode.InvalidArgument, failure.Message));
        }

        var totals = await earnings.TotalsAsync(driverId, range.from, range.to, context.CancellationToken);

        return new EarningsSummary
        {
            Period = period,
            RangeFrom = range.from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            RangeTo = range.to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            GrossMinor = totals.GrossMinor,
            DailyFeeMinor = totals.DailyFeeMinor,
            PenaltyMinor = totals.PenaltyMinor,
            TipMinor = totals.TipMinor,
            NetMinor = totals.NetMinor,
            Currency = "LKR",
            Trips = totals.Trips,
        };
    }

    private static Place? ToPlace(GeoPoint? point) =>
        point is { } value ? new Place { Lat = value.Latitude, Lng = value.Longitude } : null;

    private static Guid RequireId(string? value, string field) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new RpcException(new Status(StatusCode.InvalidArgument, $"{field} is required."));

    private static GeoPoint RequirePoint(double lat, double lng)
    {
        if (double.IsNaN(lat) || lat is < -90 or > 90 || double.IsNaN(lng) || lng is < -180 or > 180)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "lat/lng are out of range."));
        }

        return new GeoPoint(lat, lng);
    }
}

/// <summary>
/// The interim shared secret guarding <c>query.v1.Query</c> until the mesh lands.
/// </summary>
/// <remarks>
/// The same arrangement as reputation-svc's gRPC surface and trip-state-svc's
/// <c>/v1/internal/sessions/**</c>: D3' §0 puts service-to-service traffic on mTLS
/// (Linkerd/SPIFFE) and this is what stands in until it exists. <c>Query:InternalApiKey</c> unset leaves
/// the service <b>unmapped</b>, so a deployment that forgets the secret gets UNIMPLEMENTED rather than
/// an open read surface over every passenger's trip history.
/// </remarks>
public sealed class InternalKeyInterceptor(string apiKey) : Interceptor
{
    /// <summary>Metadata key carrying <c>Query:InternalApiKey</c>. Lower-case: gRPC metadata keys are.</summary>
    public const string HeaderName = "x-mageride-internal-key";

    private readonly string _apiKey = string.IsNullOrWhiteSpace(apiKey)
        ? throw new ArgumentException("An internal API key is required.", nameof(apiKey))
        : apiKey;

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(continuation);

        var presented = context.RequestHeaders.GetValue(HeaderName);

        // Fixed-time compare: the secret is long-lived and a caller can retry without limit, which is
        // exactly the shape a timing oracle needs to be useful.
        if (presented is null
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(presented),
                System.Text.Encoding.UTF8.GetBytes(_apiKey)))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Internal key missing or invalid."));
        }

        return continuation(request, context);
    }
}
