using System.Globalization;
using MageRide.Ride.Domain;
using MageRide.Ride.Rides;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Ride.Endpoints;

/// <summary>
/// <c>/v1/rides</c> — the happy-path slice of <c>backend/contracts/ride.yaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ten of the contract's routes: booking, the two recovery reads, the detail and state reads, the
/// offer accept/decline pair and the three driver-side moves that carry a ride to
/// <c>PaymentPending</c>.
/// </para>
/// <para>
/// Δ C037 adds the four package routes — the two OTP gates, the photo-proof fallback and the
/// cash-on-delivery confirmation. The location-request family is its own group
/// (<see cref="LocationRequestEndpoints"/>): it is not about a ride, and at the moment it runs
/// there is no ride for it to be about.
/// </para>
/// <para>
/// Left unmapped rather than stubbed, each with the component that owns it: <c>/dispute</c>
/// (E-05, C049/C050) and <c>GET /history</c> (AL-36, C048). A stubbed route is worse than an
/// absent one — it answers 200 to a passenger and changes nothing.
/// </para>
/// </remarks>
public static class RideEndpoints
{
    public static IEndpointRouteBuilder MapRideEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Booking is the passenger's. A driver's token carries `role=driver` and is refused here,
        // which is AL-06 deny-by-default doing its job.
        var passenger = endpoints.MapGroup("/v1/rides")
            .WithTags("rides")
            .RequireMageRideRole(MageRideRoles.Passenger);

        passenger.MapPost("/request", RequestAsync).WithName("requestRide");

        // Reads are open to either party; which rides a caller may see is decided per ride by
        // RideRow.IsParticipant, not by the role.
        var reads = endpoints.MapGroup("/v1/rides")
            .WithTags("rides")
            .RequireMageRideRole(MageRideRoles.Passenger, MageRideRoles.Driver);

        reads.MapGet("/{rideId}", GetAsync).WithName("getRide");
        reads.MapGet("/{rideId}/state", GetStateAsync).WithName("getRideState");
        reads.MapGet("/passenger/{passengerId}/active", GetActivePassengerRideAsync).WithName("getActivePassengerRide");
        reads.MapGet("/driver/{driverId}/active", GetActiveDriverRideAsync).WithName("getActiveDriverRide");

        // D3' spells the auth on `cancel` as "Bearer" without a role: §11.12 gives both sides a row
        // and the passenger's Rs 50 and the driver's reputation hit are different rows of the same
        // table. Which one applies is decided from the ride, not from the token's role — a driver
        // who is not the accepted driver of *this* ride is 403 not-ride-participant, not 403
        // forbidden.
        reads.MapPost("/{rideId}/cancel", CancelAsync).WithName("cancelRide");

        var driver = endpoints.MapGroup("/v1/rides")
            .WithTags("rides")
            .RequireMageRideRole(MageRideRoles.Driver);

        driver.MapPost("/{rideId}/offer/{driverId}/accept", AcceptAsync).WithName("acceptRideOffer");
        driver.MapPost("/{rideId}/offer/{driverId}/decline", DeclineAsync).WithName("declineRideOffer");
        driver.MapPost("/{rideId}/arrive", ArriveAsync).WithName("markDriverArrived");
        driver.MapPost("/{rideId}/start", StartAsync).WithName("startRide");
        driver.MapPost("/{rideId}/complete", CompleteAsync).WithName("completeRide");

        // Δ C037 — the package delivery gates (P-07, P-08, P-10). All four are the driver's: the
        // sender and the recipient hold the codes, and the driver is who types them in.
        driver.MapPost("/{rideId}/package/pickup-otp", VerifyPickupOtpAsync).WithName("verifyPackagePickupOtp");
        driver.MapPost("/{rideId}/package/delivery-otp", VerifyDeliveryOtpAsync).WithName("verifyPackageDeliveryOtp");

        // DisableAntiforgery for the same reason provisioning-svc's bulk upload does: the request is
        // a Bearer-authenticated multipart POST from a mobile app, not a browser form, so there is
        // no cookie for a forgery to ride on and no token for a native client to fetch.
        driver.MapPost("/{rideId}/package/proof-photo", UploadProofPhotoAsync)
            .WithName("uploadPackageProofPhoto")
            .DisableAntiforgery();

        driver.MapPost("/{rideId}/cod-collected", ConfirmCashOnDeliveryAsync).WithName("confirmCashOnDelivery");

        return endpoints;
    }

    private static async Task<IResult> RequestAsync(
        RideRequestBody? body, HttpContext context, IRideService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var booking = await service.RequestAsync(
            new RequestRideCommand(
                PassengerId: context.User.RequireSubjectId(),
                ClientRequestId: body?.ClientRequestId,
                Kind: body?.Kind,
                Pickup: ToPlace(body?.Pickup),
                Dropoff: ToPlace(body?.Dropoff),
                VehicleType: body?.VehicleType,
                FareEstimateToken: body?.FareEstimateToken,
                PaymentMethod: body?.PaymentMethod,
                ScheduledAt: body?.ScheduledAt,
                IsProxy: body?.IsProxy,
                RiderName: body?.RiderName,
                RiderPhone: body?.RiderPhone,
                PackageSize: body?.PackageSize,
                PackageDescription: body?.PackageDescription,
                RecipientName: body?.RecipientName,
                RecipientPhone: body?.RecipientPhone),
            cancellationToken);

        // 202 either way. R-18 says a retry "returns the existing ride"; answering 200 the second
        // time would make the status itself carry state, and a client cannot tell a replay from a
        // fresh booking without one — which is exactly the property that makes the retry safe.
        return TypedResults.Accepted($"/v1/rides/{booking.Ride.Id}", RideRequestedResponse.From(booking));
    }

    // -----------------------------------------------------------------------------------------
    // Δ C037 — package delivery
    // -----------------------------------------------------------------------------------------

    private static async Task<Ok<RideStateChangeResponse>> VerifyPickupOtpAsync(
        string rideId,
        OtpAttemptBody? body,
        HttpContext context,
        IPackageService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var ride = await service.VerifyPickupOtpAsync(
            context.User.RequireSubjectId(), RequireRideId(rideId), body?.Otp, cancellationToken);

        return TypedResults.Ok(RideStateChangeResponse.From(ride));
    }

    private static async Task<Ok<RideStateChangeResponse>> VerifyDeliveryOtpAsync(
        string rideId,
        OtpAttemptBody? body,
        HttpContext context,
        IPackageService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var ride = await service.VerifyDeliveryOtpAsync(
            context.User.RequireSubjectId(), RequireRideId(rideId), body?.Otp, cancellationToken);

        return TypedResults.Ok(RideStateChangeResponse.From(ride));
    }

    /// <summary>
    /// P-10 — multipart, one <c>file</c> part, read as a stream rather than buffered: a delivery
    /// photo is megabytes and every one of them would otherwise sit in this process's heap.
    /// </summary>
    private static async Task<IResult> UploadProofPhotoAsync(
        string rideId, HttpContext context, IPackageService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        if (!context.Request.HasFormContentType)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["file"] = ["Expected multipart/form-data with a `file` part."],
            });
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files["file"]
                   ?? throw new MageRideValidationException(new Dictionary<string, string[]>
                   {
                       ["file"] = ["No `file` part in the upload."],
                   });

        await using var content = file.OpenReadStream();

        var proof = await service.UploadProofPhotoAsync(
            new ProofPhotoCommand(
                DriverId: context.User.RequireSubjectId(),
                RideId: RequireRideId(rideId),
                FileName: file.FileName,
                Length: file.Length,
                Content: content,
                CapturedGeo: ReadCapturedGeo(form),
                Note: form["note"].FirstOrDefault()),
            cancellationToken);

        // 201 with the artifact id, as the contract's `uploadPackageProofPhoto` declares. The state
        // and version ride along because the same call completed the delivery, and a client that
        // had to re-read the ride to discover that would be one round trip behind its own action.
        return TypedResults.Created(
            $"/v1/rides/{proof.Ride.Id}",
            new ProofArtifactResponse(proof.ArtifactId, proof.Ride.State, proof.Ride.Version));
    }

    /// <summary>
    /// The optional <c>lat</c>/<c>lng</c> parts behind <c>rides.proof_artifacts.captured_geo</c>
    /// (Δ C037). Absent or unparseable means no fix — a lift well or a warehouse basement — which is
    /// a photo without a position rather than a refused delivery.
    /// </summary>
    private static GeoPoint? ReadCapturedGeo(IFormCollection form) =>
        double.TryParse(form["lat"].FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
        double.TryParse(form["lng"].FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lng) &&
        lat is >= -90 and <= 90 && lng is >= -180 and <= 180
            ? new GeoPoint(lat, lng)
            : null;

    private static async Task<Ok<RideStateChangeResponse>> ConfirmCashOnDeliveryAsync(
        string rideId,
        CashCollectedBody? body,
        HttpContext context,
        IPackageService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var ride = await service.ConfirmCashOnDeliveryAsync(
            context.User.RequireSubjectId(), RequireRideId(rideId), body?.CollectedMinor, cancellationToken);

        return TypedResults.Ok(RideStateChangeResponse.From(ride));
    }

    private static async Task<Ok<RideDetailResponse>> GetAsync(
        string rideId, HttpContext context, IRideService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var view = await service.GetAsync(context.User.RequireSubjectId(), RequireRideId(rideId), cancellationToken);

        return TypedResults.Ok(RideDetailResponse.From(view));
    }

    private static async Task<Ok<RideStateResponse>> GetStateAsync(
        string rideId, HttpContext context, IRideService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var view = await service.GetAsync(context.User.RequireSubjectId(), RequireRideId(rideId), cancellationToken);

        return TypedResults.Ok(RideStateResponse.From(view.Ride));
    }

    private static async Task<IResult> GetActivePassengerRideAsync(
        string passengerId, HttpContext context, IRideService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var view = await service.GetActiveForPassengerAsync(
            context.User.RequireSubjectId(), RequireUserId(passengerId), cancellationToken);

        return NoRideOr(view);
    }

    private static async Task<IResult> GetActiveDriverRideAsync(
        string driverId, HttpContext context, IRideService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var view = await service.GetActiveForDriverAsync(
            context.User.RequireSubjectId(), RequireUserId(driverId), cancellationToken);

        return NoRideOr(view);
    }

    /// <summary>
    /// The contract types both recovery reads `oneOf: [RideDetail, null]`, so "no ride running" is
    /// the JSON literal `null` and a 200 — not a 404, which a cold-start read would show the user
    /// as an error, and not an empty body, which is not JSON at all. TypedResults.Ok(null) writes
    /// nothing, so the literal is written explicitly.
    /// </summary>
    private static IResult NoRideOr(RideView? view) =>
        view is null
            ? TypedResults.Content("null", "application/json; charset=utf-8")
            : TypedResults.Ok(RideDetailResponse.From(view));

    private static async Task<Ok<AcceptOfferResponse>> AcceptAsync(
        string rideId,
        string driverId,
        AcceptOfferBody? body,
        HttpContext context,
        IRideService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var view = await service.AcceptOfferAsync(
            RequireSelfDriver(context, driverId),
            RequireRideId(rideId),
            RequireOfferId(body?.OfferId),
            RequireVersion(body?.Version),
            cancellationToken);

        return TypedResults.Ok(AcceptOfferResponse.From(view));
    }

    private static async Task<Ok<RideStateChangeResponse>> DeclineAsync(
        string rideId,
        string driverId,
        DeclineOfferBody? body,
        HttpContext context,
        IRideService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var ride = await service.DeclineOfferAsync(
            RequireSelfDriver(context, driverId),
            RequireRideId(rideId),
            RequireOfferId(body?.OfferId),
            cancellationToken);

        return TypedResults.Ok(RideStateChangeResponse.From(ride));
    }

    private static async Task<Ok<RideStateChangeResponse>> ArriveAsync(
        string rideId,
        VersionedCommandBody? body,
        HttpContext context,
        IRideService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var ride = await service.ArriveAsync(
            context.User.RequireSubjectId(), RequireRideId(rideId), RequireVersion(body?.Version), cancellationToken);

        return TypedResults.Ok(RideStateChangeResponse.From(ride));
    }

    private static async Task<Ok<RideStateChangeResponse>> StartAsync(
        string rideId,
        StartRideBody? body,
        HttpContext context,
        IRideService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        // body.Otp is accepted and ignored — see StartRideBody.
        var ride = await service.StartAsync(
            context.User.RequireSubjectId(), RequireRideId(rideId), RequireVersion(body?.Version), cancellationToken);

        return TypedResults.Ok(RideStateChangeResponse.From(ride));
    }

    private static async Task<Ok<RideCompletedResponse>> CompleteAsync(
        string rideId,
        VersionedCommandBody? body,
        HttpContext context,
        IRideService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var ride = await service.CompleteAsync(
            context.User.RequireSubjectId(), RequireRideId(rideId), RequireVersion(body?.Version), cancellationToken);

        return TypedResults.Ok(RideCompletedResponse.From(ride));
    }

    private static async Task<Ok<RideCancelledResponse>> CancelAsync(
        string rideId,
        CancelRideBody? body,
        HttpContext context,
        IRideCancellationService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var cancellation = await service.CancelAsync(
            context.User.RequireSubjectId(),
            RequireRideId(rideId),
            body?.Reason,
            RequireVersion(body?.Version),
            cancellationToken);

        return TypedResults.Ok(RideCancelledResponse.From(cancellation));
    }

    private static RidePlace? ToPlace(PlaceBody? place) =>
        place is null ? null : new RidePlace(place.Lat, place.Lng, place.Address);

    /// <summary>
    /// A malformed ride id is <c>404 not-found</c> rather than a 400: the contract types it as an
    /// opaque ULID-or-UUID, so "not a well-formed identifier" and "no such ride" are the same
    /// answer to a caller and telling them apart leaks nothing useful.
    /// </summary>
    private static Guid RequireRideId(string? rideId) =>
        Ulids.TryParse(rideId, out var parsed)
            ? parsed
            : throw new MageRideException(MageRideErrors.NotFound, $"No ride '{rideId}'.");

    private static Guid RequireUserId(string? userId) =>
        Ulids.TryParse(userId, out var parsed)
            ? parsed
            : throw new MageRideException(MageRideErrors.NotFound, $"No account '{userId}'.");

    /// <summary>
    /// The <c>{driverId}</c> in the accept and decline paths must be the authenticated driver.
    /// D3' puts it in the path so the URL is self-describing, not so a driver can name another.
    /// </summary>
    private static Guid RequireSelfDriver(HttpContext context, string? driverId)
    {
        var authenticated = context.User.RequireSubjectId();

        if (!Ulids.TryParse(driverId, out var parsed) || parsed != authenticated)
        {
            throw new MageRideException(
                MageRideErrors.NotRideParticipant, "A driver may only answer an offer as themselves.");
        }

        return authenticated;
    }

    private static Guid RequireOfferId(string? offerId) =>
        Ulids.TryParse(offerId, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["offerId"] = ["offerId is required and must be a ULID or a UUID."],
            });

    /// <summary>
    /// Every mutation echoes the version it expects (D3' ride-svc header). Making it optional
    /// would turn a stale client's overwrite into a silent success, which is the whole reason the
    /// field exists.
    /// </summary>
    private static long RequireVersion(long? version) =>
        version is { } value && value >= 1
            ? value
            : throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["version"] = ["version is required and must be the version the client last saw."],
            });
}
