using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Levels;
using MageRide.Dispatch.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Scheduling;

/// <summary>The body of <c>POST /v1/rides/schedule</c>, before validation.</summary>
/// <param name="DestLat">
/// AL-36 item 2's whole point: "select the location to go" is <b>mandatory</b>. A missing
/// destination is <c>400 validation-failed</c> at the service boundary, not a nullable column
/// filled in later.
/// </param>
public sealed record ScheduleRideCommand(
    Guid PassengerId,
    double? PickupLat,
    double? PickupLng,
    double? DestLat,
    double? DestLng,
    DateTimeOffset? PickupTime,
    string? VehicleType,
    string? PaymentMethod);

/// <summary>
/// Advance bookings, the D-06 Job Board and its intents (D5' §3.7, US-6A.4/6A.5/6A.15, AL-36).
/// </summary>
public interface IScheduledRideService
{
    Task<ScheduledRideRow> ScheduleAsync(ScheduleRideCommand command, CancellationToken cancellationToken);

    /// <summary>Withdraws a booking that dispatch has not yet materialised.</summary>
    Task CancelAsync(Guid passengerId, Guid scheduledRideId, CancellationToken cancellationToken);

    /// <summary>The D-06 Job Board, for a driver who is allowed to see it.</summary>
    Task<CursorPage<JobBoardEntry>> JobBoardAsync(
        Guid driverId, GeoPoint origin, int? radiusM, PageRequest page, CancellationToken cancellationToken);

    /// <summary>US-6A.5: posting intent is not accepting. Returns the intent id.</summary>
    Task<Guid> PostIntentAsync(Guid driverId, Guid scheduledRideId, CancellationToken cancellationToken);

    /// <summary>US-6A.15: the scheduled rides this driver has been assigned.</summary>
    Task<CursorPage<ScheduledRideRow>> UpcomingForDriverAsync(
        Guid driverId, PageRequest page, CancellationToken cancellationToken);

    /// <summary>
    /// One T-30 sweep: materialise every booking whose pickup is inside the lead time.
    /// </summary>
    /// <returns>How many bookings were claimed.</returns>
    Task<int> MaterialiseDueAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IScheduledRideService"/>
public sealed class ScheduledRideService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IScheduledRideRepository scheduledRides,
    IDriverLevelService levels,
    IRideServiceClient rideService,
    IOptions<DispatchOptions> options,
    TimeProvider timeProvider,
    ILogger<ScheduledRideService> logger) : IScheduledRideService
{
    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<ScheduledRideRow> ScheduleAsync(ScheduleRideCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        // AL-36, first and by itself: the destination is what this change set added, and a booking
        // without one is refused before anything else is looked at so the 400 names it alone.
        var dropoff = RequirePoint(command.DestLat, command.DestLng, "dest", errors);
        var pickup = RequirePoint(command.PickupLat, command.PickupLng, "pickup", errors);

        if (!DispatchVehicleTypes.IsKnown(command.VehicleType))
        {
            errors["vehicleType"] =
                [$"vehicleType must be one of {string.Join(", ", DispatchVehicleTypes.All.Order(StringComparer.Ordinal))}."];
        }

        var paymentMethod = command.PaymentMethod ?? ScheduledPaymentMethods.Cash;

        if (!ScheduledPaymentMethods.IsKnown(paymentMethod))
        {
            errors["paymentMethod"] =
                [$"paymentMethod must be one of {string.Join(", ", ScheduledPaymentMethods.All.Order(StringComparer.Ordinal))}."];
        }

        var now = timeProvider.GetUtcNow();

        if (command.PickupTime is not { } pickupTime)
        {
            errors["pickupTime"] = ["pickupTime is required."];
        }
        else if (pickupTime < now.Add(_options.ScheduledMinimumLead))
        {
            // Below the lead time the ride would be materialised by the very next sweep, which is
            // an immediate booking made through the wrong endpoint.
            errors["pickupTime"] =
                [$"pickupTime must be at least {_options.ScheduledMinimumLead.TotalMinutes:0} minutes from now. " +
                 "Use POST /v1/rides/request for an immediate ride."];
        }
        else if (pickupTime > now.Add(_options.ScheduledMaximumLead))
        {
            errors["pickupTime"] =
                [$"pickupTime must be within {_options.ScheduledMaximumLead.TotalDays:0} days."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        // US-6A.8: a Level-1 driver loses the Job Board — and a *passenger* who has been booking-
        // disabled by AL-16 may not put a ride on it either. Checked here rather than only at T-30
        // so the refusal reaches the person who can act on it, at the moment they act.
        await levels.RequirePassengerMayBookAsync(command.PassengerId, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var row = await scheduledRides.CreateAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            command.PassengerId,
            pickup!.Value,
            dropoff!.Value,
            command.VehicleType!,
            paymentMethod,
            command.PickupTime!.Value,
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Passenger {PassengerId} scheduled ride {ScheduledRideId} on {VehicleType} for {PickupTime:O}",
            command.PassengerId, row.Id, row.VehicleType, row.PickupTime);

        return row;
    }

    public async Task CancelAsync(Guid passengerId, Guid scheduledRideId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var row = await scheduledRides.FindAsync(connection, null, scheduledRideId, cancellationToken)
            ?? throw new MageRideException(MageRideErrors.NotFound, $"No scheduled ride '{scheduledRideId}'.");

        if (row.PassengerId != passengerId)
        {
            // Not 404: the caller is authenticated and the row exists, so "you may not" is the
            // truthful answer and the one the contract's x-error-codes lists.
            throw new MageRideException(
                MageRideErrors.Forbidden, "This scheduled ride belongs to another passenger.");
        }

        if (row.Status == ScheduledRideStatuses.Cancelled)
        {
            // Cancelling twice is not a fault. The row is already where the caller wants it.
            return;
        }

        if (!await scheduledRides.CancelAsync(
                connection, null, scheduledRideId, ScheduledRideStatuses.Cancelled, cancellationToken))
        {
            // 409, which is the status `dispatch.yaml` prints for this case. The code is `conflict`
            // and not `illegal-transition`: C002's registry maps the latter to **400** and ride-svc
            // answers 400 with it, so reusing the name here would give one kebab code two statuses
            // across the platform. The contract's x-error-codes were corrected to match (Δ C035).
            throw new MageRideException(
                MageRideErrors.Conflict,
                "This scheduled ride has already been dispatched. Cancel the ride itself with " +
                "POST /v1/rides/{rideId}/cancel, which applies the §11.12 penalty matrix.");
        }

        logger.LogInformation("Scheduled ride {ScheduledRideId} withdrawn by its passenger", scheduledRideId);
    }

    public async Task<CursorPage<JobBoardEntry>> JobBoardAsync(
        Guid driverId, GeoPoint origin, int? radiusM, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        await levels.RequireJobBoardAccessAsync(driverId, cancellationToken);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var rows = await scheduledRides.JobBoardAsync(
            connection,
            new JobBoardQuery(
                driverId,
                origin,
                Math.Clamp(radiusM ?? _options.JobBoardRadiusM, 1_000, _options.JobBoardRadiusM),

                // A booking whose pickup has passed is not work anybody can take. The board shows
                // the future, which is also what the T-30 sweep is racing toward.
                timeProvider.GetUtcNow(),
                DecodeCursor(page.Cursor),
                page.OverfetchLimit),
            cancellationToken);

        return CursorPage<JobBoardEntry>.FromOverfetch(
            rows, page.Limit, static entry => EncodeCursor(entry.Ride));
    }

    public async Task<Guid> PostIntentAsync(Guid driverId, Guid scheduledRideId, CancellationToken cancellationToken)
    {
        await levels.RequireJobBoardAccessAsync(driverId, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var row = await scheduledRides.FindAsync(
                unitOfWork.Connection, unitOfWork.Transaction, scheduledRideId, cancellationToken)
            ?? throw new MageRideException(MageRideErrors.NotFound, $"No scheduled ride '{scheduledRideId}'.");

        if (row.Status != ScheduledRideStatuses.Scheduled)
        {
            // The T-30 offer has already gone out, or the passenger withdrew it. Either way there
            // is nothing left to post intent on, and the board this card came from is stale.
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"Scheduled ride {scheduledRideId} is {row.Status}; the Job Board no longer holds it.");
        }

        var intentId = await scheduledRides.AddIntentAsync(
            unitOfWork.Connection, unitOfWork.Transaction, scheduledRideId, driverId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Driver {DriverId} posted intent {IntentId} on scheduled ride {ScheduledRideId} " +
            "(not an acceptance — the offer arrives at T-{Lead})",
            driverId, intentId, scheduledRideId, _options.ScheduledLeadTime);

        return intentId;
    }

    public async Task<CursorPage<ScheduledRideRow>> UpcomingForDriverAsync(
        Guid driverId, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var rows = await scheduledRides.AssignedToDriverAsync(
            connection, driverId, DecodeCursor(page.Cursor), page.OverfetchLimit, cancellationToken);

        return CursorPage<ScheduledRideRow>.FromOverfetch(rows, page.Limit, EncodeCursor);
    }

    public async Task<int> MaterialiseDueAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ScheduledRideRow> due;

        // The claim is held for the whole batch: `FOR UPDATE SKIP LOCKED` is what stops two
        // replicas materialising the same booking, and releasing it before the ride-svc call would
        // give both of them the row. The batch is small (Dispatch:ScheduledBatchSize) and each
        // item is one HTTP round trip, so the transaction is short by construction.
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        due = await scheduledRides.ClaimDueAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            _options.ScheduledLeadTime,
            _options.ScheduledBatchSize,
            cancellationToken);

        foreach (var booking in due)
        {
            await MaterialiseOneAsync(unitOfWork, booking, cancellationToken);
        }

        await unitOfWork.CommitAsync(cancellationToken);

        return due.Count;
    }

    private async Task MaterialiseOneAsync(
        IUnitOfWork unitOfWork, ScheduledRideRow booking, CancellationToken cancellationToken)
    {
        var abandonAfter = booking.PickupTime.Add(_options.ScheduledDispatchGrace);

        if (timeProvider.GetUtcNow() > abandonAfter)
        {
            // The passenger stayed mid-ride, or ride-svc was down, for as long as this booking was
            // ever advertised. Retrying for ever would keep a row in every sweep's batch and would
            // eventually dispatch a ride for a pickup time that is hours in the past.
            await scheduledRides.CancelAsync(
                unitOfWork.Connection, unitOfWork.Transaction, booking.Id, ScheduledRideStatuses.Cancelled,
                cancellationToken);

            logger.LogWarning(
                "Scheduled ride {ScheduledRideId} could not be materialised by {AbandonAfter:O}; abandoning it",
                booking.Id, abandonAfter);

            return;
        }

        var materialised = await rideService.MaterialiseScheduledAsync(
            new MaterialiseScheduledRide(
                booking.Id, booking.PassengerId, booking.Pickup, booking.Dropoff, booking.VehicleType,
                booking.PaymentMethod),
            cancellationToken);

        if (!materialised.Succeeded || materialised.RideId is not { } rideId)
        {
            // Left SCHEDULED and still due, so the next sweep picks it up. A 409 here is the
            // ordinary case — the passenger is on a ride right now — and is information rather than
            // a failure; the grace above is what bounds the retrying.
            logger.LogInformation(
                "ride-svc would not materialise scheduled ride {ScheduledRideId} ({Status}/{ErrorCode}); " +
                "retrying until {AbandonAfter:O}",
                booking.Id, (int)materialised.Status, materialised.ErrorCode, abandonAfter);

            return;
        }

        await scheduledRides.MarkDispatchedAsync(
            unitOfWork.Connection, unitOfWork.Transaction, booking.Id, rideId, cancellationToken);

        // Nothing is dispatched from here. ride-svc emitted `ride.requested` inside its own
        // transaction (R-13), the consumer picks it up, and the round that runs is the ordinary one
        // — which discovers the booking behind the ride and restricts itself to the intent list
        // (D5' §3.7). One dispatch path, driven by one event, rather than a second entry point that
        // would race this sweep for the same ride.
        logger.LogInformation(
            "Scheduled ride {ScheduledRideId} materialised as ride {RideId}; the T-{Lead} offer follows on ride.requested",
            booking.Id, rideId, _options.ScheduledLeadTime);
    }

    private static GeoPoint? RequirePoint(
        double? lat, double? lng, string field, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var ok = true;

        if (lat is not { } latitude || double.IsNaN(latitude) || latitude is < -90 or > 90)
        {
            errors[$"{field}Lat"] = [$"{field}Lat is required and must be between -90 and 90."];
            ok = false;
        }

        if (lng is not { } longitude || double.IsNaN(longitude) || longitude is < -180 or > 180)
        {
            errors[$"{field}Lng"] = [$"{field}Lng is required and must be between -180 and 180."];
            ok = false;
        }

        return ok ? new GeoPoint(lat!.Value, lng!.Value) : null;
    }

    private static string EncodeCursor(ScheduledRideRow row) =>
        CursorCodec.Unsigned.Encode(new ScheduledRideCursor(row.PickupTime, row.Id));

    private static ScheduledRideCursor? DecodeCursor(string? cursor) =>
        CursorCodec.Unsigned.TryDecode<ScheduledRideCursor>(cursor, out var position) ? position : null;
}
