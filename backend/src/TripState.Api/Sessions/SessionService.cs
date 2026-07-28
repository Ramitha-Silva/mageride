using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using MageRide.TripState.Configuration;
using MageRide.TripState.Domain;
using MageRide.TripState.Mqtt;
using MageRide.TripState.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.TripState.Sessions;

/// <summary><c>POST /v1/sessions/start</c> (US-5.1, D-03).</summary>
public sealed record StartSessionCommand(
    Guid DriverId, string? VehicleId, string? Mode, string? RouteId, bool AutoEndAtDestination);

/// <summary>An ignition transition reported by the tracker plane (US-3.22/3.23, AL-32).</summary>
/// <param name="On"><see langword="true"/> for ACC on.</param>
public sealed record IgnitionCommand(string? VehicleId, bool On, DateTimeOffset? At);

/// <summary>What an ignition report did, so the caller can log something true.</summary>
public enum IgnitionOutcome
{
    /// <summary>Nothing to do — the vehicle already was in the state the ignition implies.</summary>
    NoChange,

    /// <summary>ACC on opened a session (US-3.22/3.23).</summary>
    Started,

    /// <summary>ACC off closed one.</summary>
    Ended,

    /// <summary>The vehicle is not one this service auto-starts for, or no driver could be resolved.</summary>
    Declined,
}

/// <summary>The Mode A/B tracking-session lifecycle.</summary>
public interface ISessionService
{
    Task<Session> StartAsync(StartSessionCommand command, CancellationToken cancellationToken);

    /// <summary>US-5.2 — the dashboard End Journey, which overrides the device (AL-32).</summary>
    Task<Session> EndAsync(Guid driverId, string? sessionId, CancellationToken cancellationToken);

    /// <summary>US-5.10 — resume an auto-ended session inside the 5-minute grace.</summary>
    Task<Session> RestartAsync(Guid driverId, string? sessionId, CancellationToken cancellationToken);

    /// <summary>The vehicle's live session, or <see langword="null"/> — the client's cold-start read.</summary>
    Task<Session?> GetActiveAsync(Guid actorId, string? vehicleId, CancellationToken cancellationToken);

    /// <summary>US-5.9 — a fired timer, or the broker's last will, closes the session.</summary>
    Task<Session> AutoEndAsync(string? sessionId, string? reason, CancellationToken cancellationToken);

    /// <summary>ACC on/off from a paired tracker (US-3.22/3.23).</summary>
    Task<IgnitionOutcome> IgnitionAsync(IgnitionCommand command, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISessionService"/>
public sealed class SessionService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    ISessionRepository sessions,
    ITripEventRepository tripEvents,
    IVehicleLookupRepository vehicles,
    IDriverSessionCache cache,
    ICadencePublisher cadence,
    IOutboxWriter outbox,
    IOptions<TripStateOptions> options,
    TimeProvider clock,
    ILogger<SessionService> logger) : ISessionService
{
    /// <summary>Postgres' unique-violation SQLSTATE — here, <c>ux_sessions_active_driver</c>.</summary>
    private const string UniqueViolation = "23505";

    private readonly TripStateOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<Session> StartAsync(StartSessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var request = Validate(command);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var vehicle = await RequireEligibleAsync(
            unitOfWork.Connection, unitOfWork.Transaction, command.DriverId, request.VehicleId, cancellationToken);

        // R-01, restated at the row this time. The request said A or B; the *vehicle* is whatever
        // registry says it is, and a Mode C three-wheeler must not acquire a tracking session
        // because its driver typed 'B'.
        if (!OperatingModes.IsTracked(vehicle.Mode))
        {
            throw new MageRideException(
                MageRideErrors.ModeNotAllowed,
                $"Vehicle {request.VehicleId} is Mode {vehicle.Mode}. A Mode C journey is a ride, and ride-svc owns it (R-01).");
        }

        if (!string.Equals(vehicle.Mode, request.Mode, StringComparison.Ordinal))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["mode"] = [$"Vehicle {request.VehicleId} is registered as Mode {vehicle.Mode}."],
            });
        }

        Session session;

        try
        {
            session = await OpenAsync(
                unitOfWork, vehicle, command.DriverId, request.Mode, request.RouteId,
                command.AutoEndAtDestination, SessionActors.Driver, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
        {
            // ux_sessions_active_driver refused it: the driver already holds a live session. This
            // is D-03's mutex doing its job, and it is caught here rather than pre-checked because
            // a SELECT-then-INSERT loses exactly the race the index exists to settle — ten
            // concurrent starts all read "no session yet".
            throw new MageRideException(
                MageRideErrors.DriverAlreadyLive,
                "You already have a live session. End it before starting another (D-03, US-9.6).",
                exception);
        }

        await unitOfWork.CommitAsync(cancellationToken);
        await AnnounceStartAsync(session, cancellationToken);

        logger.LogInformation(
            "Session {SessionId} started for driver {DriverId} on Mode {Mode} vehicle {VehicleId}",
            session.Id, session.DriverId, session.Mode, session.VehicleId);

        return session;
    }

    public async Task<Session> EndAsync(Guid driverId, string? sessionId, CancellationToken cancellationToken)
    {
        var id = RequireSessionId(sessionId);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var existing = await sessions.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, id, cancellationToken)
                       ?? throw NoSession(id);

        // 403, not 404: the session exists and belongs to somebody else. The driver was given this
        // id by their own app, so confirming it exists tells them nothing they did not know.
        if (existing.DriverId != driverId)
        {
            throw new MageRideException(MageRideErrors.Forbidden, "This session belongs to another driver.");
        }

        // AL-32 / US-5.12 in one line: the dashboard writes the transition whatever the device is
        // doing. A tracker that is still publishing does not get a veto — it gets a cadence hint
        // telling it to stand down, and its next position arrives on a vehicle with no session.
        if (existing.StartedBy == SessionActors.Device)
        {
            await tripEvents.RecordAsync(
                unitOfWork.Connection, unitOfWork.Transaction, existing.Id, TripEventKinds.DeviceOverridden,
                new { action = "end", startedBy = existing.StartedBy, by = SessionActors.Driver },
                clock.GetUtcNow(), cancellationToken);
        }

        var ended = await CloseAsync(
            unitOfWork, existing, EndReasons.DriverEnded, SessionActors.Driver, cancellationToken);

        if (ended is null)
        {
            // Already closed. The driver asked for the journey to be over and it is; a 409 here
            // would make the button fail for a driver whose idle timer beat them to it by a second.
            await unitOfWork.RollbackAsync(cancellationToken);

            return await sessions.FindAsync(unitOfWork.Connection, null, id, cancellationToken) ?? existing;
        }

        await unitOfWork.CommitAsync(cancellationToken);
        await AnnounceEndAsync(ended, cancellationToken);

        return ended;
    }

    public async Task<Session> RestartAsync(Guid driverId, string? sessionId, CancellationToken cancellationToken)
    {
        var id = RequireSessionId(sessionId);
        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var existing = await sessions.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, id, cancellationToken)
                       ?? throw NoSession(id);

        if (existing.DriverId != driverId)
        {
            throw new MageRideException(MageRideErrors.Forbidden, "This session belongs to another driver.");
        }

        if (existing.IsActive)
        {
            throw new MageRideException(
                MageRideErrors.Conflict, "This session is still live; there is nothing to restart.");
        }

        // A driver's own End Journey is not restartable, at any distance — see EndReasons.
        if (!existing.WasAutoEnded)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                "Only an auto-ended session can be restarted; start a new one (US-5.10).");
        }

        // Expired grace is 410 Gone rather than 409: the request was well formed and would have
        // worked a minute ago, which is exactly what Gone means. The contract lists it.
        if (existing.RestartableUntil(_options.RestartGrace) is { } deadline && deadline <= now)
        {
            throw new MageRideException(
                MageRideErrors.SessionRestartExpired,
                $"The {_options.RestartGrace.TotalMinutes:0}-minute restart window closed at {deadline:O}.");
        }

        Session restarted;

        try
        {
            restarted = await sessions.RestartAsync(
                            unitOfWork.Connection, unitOfWork.Transaction, id, now - _options.RestartGrace, now,
                            cancellationToken)
                        ?? throw new MageRideException(
                            MageRideErrors.Conflict,
                            "This session is no longer restartable — it changed while the request was in flight.");
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
        {
            // The driver started something else during the grace window. Re-taking the mutex is
            // the whole reason a restart is not just an UPDATE, and this is the case that proves
            // the index rather than a prior read is what enforces it.
            throw new MageRideException(
                MageRideErrors.DriverAlreadyLive,
                "You already have another live session; end it before restarting this one.", exception);
        }

        await tripEvents.RecordAsync(
            unitOfWork.Connection, unitOfWork.Transaction, restarted.Id, TripEventKinds.SessionRestarted,
            new { previousEndReason = existing.EndReason, previousEndedAt = existing.EndedAt },
            now, cancellationToken);

        await outbox.WriteAsync(unitOfWork, SessionEvents.SessionRestarted(restarted), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        await AnnounceStartAsync(restarted, cancellationToken);

        logger.LogInformation(
            "Session {SessionId} restarted inside the grace window after {Reason}", restarted.Id, existing.EndReason);

        return restarted;
    }

    public async Task<Session?> GetActiveAsync(Guid actorId, string? vehicleId, CancellationToken cancellationToken)
    {
        var id = RequireVehicleId(vehicleId);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        // Entitlement is checked before the read, not after: "is this vehicle live" is not a fact
        // a stranger gets to ask, and answering `null` for a vehicle they cannot see would still
        // tell them it exists.
        _ = await vehicles.FindEligibleAsync(connection, null, actorId, id, cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.VehicleNotFound, $"No vehicle {id} you may operate.");

        return await sessions.FindActiveByVehicleAsync(connection, null, id, cancellationToken);
    }

    public async Task<Session> AutoEndAsync(string? sessionId, string? reason, CancellationToken cancellationToken)
    {
        var id = RequireSessionId(sessionId);

        if (!EndReasons.IsTimerReason(reason))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["reason"] = ["reason must be 'idle_timeout', 'destination_geofence' or 'mqtt_offline'."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var existing = await sessions.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, id, cancellationToken)
                       ?? throw NoSession(id);

        var ended = await CloseAsync(unitOfWork, existing, reason!, SessionActors.System, cancellationToken);

        if (ended is null)
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            // A timer that fires against a session the driver has already ended is the normal
            // race, not an error — but it must not overwrite `driver_ended`, which is what
            // decides whether the grace window opens.
            throw new MageRideException(MageRideErrors.Conflict, "This session is no longer live.");
        }

        await unitOfWork.CommitAsync(cancellationToken);
        await AnnounceEndAsync(ended, cancellationToken);

        return ended;
    }

    public async Task<IgnitionOutcome> IgnitionAsync(IgnitionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var vehicleId = RequireVehicleId(command.VehicleId);
        var now = command.At ?? clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var existing = await sessions.FindActiveByVehicleAsync(
            unitOfWork.Connection, unitOfWork.Transaction, vehicleId, cancellationToken);

        if (command.On)
        {
            if (existing is not null)
            {
                // Already live — a driver who pressed Start before turning the key, or a tracker
                // re-reporting ACC on. Logged and left alone; the dashboard already says
                // "journey started", which is what US-5.12 asks for.
                await tripEvents.RecordAsync(
                    unitOfWork.Connection, unitOfWork.Transaction, existing.Id, TripEventKinds.Ignition,
                    new { on = true, outcome = "already-live", startedBy = existing.StartedBy },
                    now, cancellationToken);

                await unitOfWork.CommitAsync(cancellationToken);
                return IgnitionOutcome.NoChange;
            }

            var vehicle = await vehicles.FindTrackerVehicleAsync(
                unitOfWork.Connection, unitOfWork.Transaction, vehicleId, cancellationToken);

            // Declined rather than guessed. US-3.22/3.23 auto-start Mode A and Mode B only, and a
            // session needs a driver: attributing one to the wrong person takes their D-03 mutex
            // and blocks the journey they are trying to start themselves.
            if (vehicle is null || !OperatingModes.IsTracked(vehicle.Mode) || !vehicle.IsGoLiveEligible)
            {
                await unitOfWork.RollbackAsync(cancellationToken);

                logger.LogInformation(
                    "Ignition-on for vehicle {VehicleId} did not auto-start a session: {Reason}",
                    vehicleId,
                    vehicle is null ? "no owner on the eligibility projection"
                        : !OperatingModes.IsTracked(vehicle.Mode) ? $"Mode {vehicle.Mode} is not tracked here"
                        : "the vehicle is not go-live eligible");

                return IgnitionOutcome.Declined;
            }

            Session started;

            try
            {
                started = await OpenAsync(
                    unitOfWork, vehicle, vehicle.DriverId, vehicle.Mode, routeId: null,
                    autoEndAtDestination: false, SessionActors.Device, cancellationToken);
            }
            catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
            {
                // The owner is live on another vehicle. The tracker does not win that argument —
                // D-03 is one session per driver, and the vehicle they are actually driving keeps
                // it.
                await unitOfWork.RollbackAsync(cancellationToken);

                logger.LogInformation(
                    exception,
                    "Ignition-on for vehicle {VehicleId} did not auto-start: driver {DriverId} is live elsewhere",
                    vehicleId, vehicle.DriverId);

                return IgnitionOutcome.Declined;
            }

            await tripEvents.RecordAsync(
                unitOfWork.Connection, unitOfWork.Transaction, started.Id, TripEventKinds.Ignition,
                new { on = true, outcome = "started" }, now, cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
            await AnnounceStartAsync(started, cancellationToken);

            logger.LogInformation(
                "Ignition auto-started session {SessionId} for vehicle {VehicleId} (US-3.22/3.23)",
                started.Id, vehicleId);

            return IgnitionOutcome.Started;
        }

        if (existing is null)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            return IgnitionOutcome.NoChange;
        }

        // ACC off ends a session the device started. It does **not** end one the driver started
        // from the dashboard: AL-32 makes the dashboard authoritative in both directions, and a
        // driver who pressed Start with the engine off — waiting at a depot, or on a vehicle whose
        // tracker mis-reports — has said what they want.
        if (existing.StartedBy != SessionActors.Device)
        {
            await tripEvents.RecordAsync(
                unitOfWork.Connection, unitOfWork.Transaction, existing.Id, TripEventKinds.Ignition,
                new { on = false, outcome = "ignored", startedBy = existing.StartedBy }, now, cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Ignition-off on vehicle {VehicleId} left session {SessionId} alone: it was started from the dashboard (AL-32)",
                vehicleId, existing.Id);

            return IgnitionOutcome.NoChange;
        }

        var closed = await CloseAsync(
            unitOfWork, existing, EndReasons.IgnitionOff, SessionActors.Device, cancellationToken);

        if (closed is null)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            return IgnitionOutcome.NoChange;
        }

        await tripEvents.RecordAsync(
            unitOfWork.Connection, unitOfWork.Transaction, closed.Id, TripEventKinds.Ignition,
            new { on = false, outcome = "ended" }, now, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);
        await AnnounceEndAsync(closed, cancellationToken);

        return IgnitionOutcome.Ended;
    }

    /// <summary>
    /// Opens a session on the caller's transaction: the row, the domain event and the outbox row.
    /// </summary>
    /// <remarks>
    /// Shared by the dashboard start, the ignition auto-start and (through the repository) the
    /// grace restart, so the three cannot leave the tables in different shapes. The unique
    /// violation is deliberately <b>not</b> caught here — the callers mean different things by it.
    /// </remarks>
    private async Task<Session> OpenAsync(
        IUnitOfWork unitOfWork,
        EligibleVehicle vehicle,
        Guid driverId,
        string mode,
        Guid? routeId,
        bool autoEndAtDestination,
        string startedBy,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // US-5.4's fence is centred on "the previous journey's end position", so it is only armed
        // when there is one. A driver's first journey in a vehicle has nowhere to arrive at, and
        // arming an empty fence would either never fire or fire immediately.
        GeoPoint? destination = null;

        if (autoEndAtDestination)
        {
            destination = await sessions.FindLastEndPointAsync(
                unitOfWork.Connection, unitOfWork.Transaction, vehicle.VehicleId, cancellationToken);

            if (destination is null)
            {
                logger.LogInformation(
                    "Vehicle {VehicleId} asked for auto-end-at-destination and has no previous journey end; " +
                    "the session starts with no fence armed (US-5.4)",
                    vehicle.VehicleId);
            }
        }

        var session = await sessions.InsertAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            vehicle.VehicleId,
            driverId,
            mode,
            routeId,
            autoEndAtDestination,
            destination,
            startedBy,
            now,
            cancellationToken);

        await tripEvents.RecordAsync(
            unitOfWork.Connection, unitOfWork.Transaction, session.Id, TripEventKinds.SessionStarted,
            new { startedBy, mode, routeId, fenceArmed = destination is not null }, now, cancellationToken);

        await outbox.WriteAsync(unitOfWork, SessionEvents.SessionStarted(session), cancellationToken);

        return session;
    }

    /// <summary>Closes a session on the caller's transaction, or reports that it was already closed.</summary>
    private async Task<Session?> CloseAsync(
        IUnitOfWork unitOfWork,
        Session session,
        string endReason,
        string endedBy,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var ended = await sessions.EndAsync(
            unitOfWork.Connection, unitOfWork.Transaction, session.Id, endReason, endedBy, now, cancellationToken);

        if (ended is null)
        {
            return null;
        }

        await tripEvents.RecordAsync(
            unitOfWork.Connection, unitOfWork.Transaction, ended.Id, TripEventKinds.SessionEnded,
            new { endReason, endedBy }, now, cancellationToken);

        // The event and the state change commit together. An end that committed and then failed to
        // publish leaves fanout-svc showing a finished journey on the passenger map with no way
        // for the driver to take it off (R-13).
        await outbox.WriteAsync(
            unitOfWork,
            SessionEvents.SessionEnded(ended, ended.RestartableUntil(_options.RestartGrace)),
            cancellationToken);

        return ended;
    }

    /// <summary>The after-COMMIT half of a start: publish the fact and hint the device's cadence.</summary>
    private async Task AnnounceStartAsync(Session session, CancellationToken cancellationToken)
    {
        await cache.PublishAsync(session.DriverId, session.Id, cancellationToken);
        await cadence.PublishAsync(session.VehicleId, _options.SessionCadence, cancellationToken);
    }

    private async Task AnnounceEndAsync(Session session, CancellationToken cancellationToken)
    {
        await cache.ClearAsync(session.DriverId, cancellationToken);

        // D5' §5.2: a vehicle with no session drops to standby idle. Sent even when the device is
        // a tracker that ended the session itself — a GT06 that has just switched ACC off will not
        // hear it, and one that mis-reported ignition needs to.
        await cadence.PublishAsync(session.VehicleId, _options.StandbyCadence, cancellationToken);
    }

    private async Task<EligibleVehicle> RequireEligibleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        // A missing entitlement is 404, not 403: the projection is driver-scoped, so "not yours"
        // and "does not exist" are the same query result, and telling them apart would leak a
        // stranger's vehicle. Same reasoning C028 records for select-live.
        var vehicle = await vehicles.FindEligibleAsync(connection, transaction, driverId, vehicleId, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.VehicleNotFound, $"No vehicle {vehicleId} you may operate.");

        // The raw columns are read rather than `is_go_live_eligible` alone, because this service
        // owes the driver a specific answer: an unapproved vehicle and a document-suspended one
        // need different things done about them.
        if (!vehicle.IsGoLiveEligible)
        {
            throw new MageRideException(
                MageRideErrors.VehicleNotApproved,
                vehicle.Status != "APPROVED"
                    ? $"Vehicle {vehicleId} is {vehicle.Status}; finish onboarding before starting a journey."
                    : $"Vehicle {vehicleId} is {vehicle.DispatchState} — a document has expired (E-03).");
        }

        return vehicle;
    }

    private static StartRequest Validate(StartSessionCommand command)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (!Guid.TryParse(command.VehicleId, out var vehicleId) || vehicleId == Guid.Empty)
        {
            errors["vehicleId"] = ["vehicleId is required and must be an identifier."];
        }

        // Mode C is named in the message rather than lumped into "unknown": a client that sent it
        // is not malformed, it is talking to the wrong service, and R-01 is worth saying out loud.
        if (command.Mode == OperatingModes.C)
        {
            errors["mode"] = ["Mode C is a ride, not a tracking session — POST /v1/rides/request (R-01)."];
        }
        else if (!OperatingModes.IsTracked(command.Mode))
        {
            errors["mode"] = ["mode must be 'A' or 'B'."];
        }

        Guid? routeId = null;

        if (!string.IsNullOrWhiteSpace(command.RouteId))
        {
            if (Guid.TryParse(command.RouteId, out var parsed))
            {
                routeId = parsed;
            }
            else
            {
                errors["routeId"] = ["routeId must be an identifier."];
            }
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        return new StartRequest(vehicleId, command.Mode!, routeId);
    }

    private static Guid RequireSessionId(string? sessionId) =>
        Guid.TryParse(sessionId, out var parsed)
            ? parsed
            : throw new MageRideException(MageRideErrors.NotFound, "No such session.");

    private static Guid RequireVehicleId(string? vehicleId) =>
        Guid.TryParse(vehicleId, out var parsed)
            ? parsed
            : throw new MageRideException(MageRideErrors.VehicleNotFound, "No such vehicle.");

    private static MageRideException NoSession(Guid sessionId) =>
        new(MageRideErrors.NotFound, $"No session {sessionId}.");

    private sealed record StartRequest(Guid VehicleId, string Mode, Guid? RouteId);
}
