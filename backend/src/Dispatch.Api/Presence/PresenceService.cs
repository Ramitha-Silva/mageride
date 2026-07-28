using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Persistence;
using MageRide.Dispatch.Redis;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Presence;

/// <summary>The body of <c>POST /v1/standby/online</c>, before validation.</summary>
public sealed record GoOnlineCommand(Guid DriverId, string? VehicleId, StandbyPlace? Position, StandbyPlace? DriverHome);

/// <summary>A coordinate as the contract's <c>GeoPoint</c> arrives.</summary>
public sealed record StandbyPlace(double? Lat, double? Lng);

/// <summary>Driver presence — the standby half of the walking skeleton (US-6A.1, R-08).</summary>
public interface IPresenceService
{
    Task<PresenceRow> GoOnlineAsync(GoOnlineCommand command, CancellationToken cancellationToken);

    Task<PresenceRow> GoOfflineAsync(Guid driverId, CancellationToken cancellationToken);

    Task<PresenceRow?> GetAsync(Guid driverId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPresenceService"/>
public sealed class PresenceService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IPresenceRepository presence,
    IDriverIndex index,
    IOptions<DispatchOptions> options,
    ILogger<PresenceService> logger) : IPresenceService
{
    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<PresenceRow> GoOnlineAsync(GoOnlineCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var vehicleId = RequireVehicleId(command.VehicleId);
        var position = RequirePlace(command.Position, "position");
        var driverHome = OptionalPlace(command.DriverHome, "driverHome");

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var vehicle = await presence.FindVehicleAsync(connection, command.DriverId, vehicleId, cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.VehicleNotFound,
                $"No vehicle {vehicleId} belonging to this driver. Register it first (POST /v1/vehicles).");

        // AL-30 / E-03. An unapproved vehicle carrying passengers is the failure the whole
        // onboarding machine exists to prevent, so this is a hard gate rather than a warning.
        if (vehicle.Status != "APPROVED")
        {
            throw new MageRideException(
                MageRideErrors.VehicleNotApproved,
                $"Vehicle {vehicleId} is {vehicle.Status}. Only an APPROVED vehicle can go on standby (AL-30).");
        }

        // Mode C only. A Mode A bus or a Mode B shared vehicle belongs to trip-state-svc's tracking
        // plane, not to the on-demand candidate pool — the R-01 boundary this component must not
        // cross, expressed where a driver would otherwise cross it.
        if (vehicle.Mode != "C")
        {
            throw new MageRideException(
                MageRideErrors.ModeNotAllowed,
                $"Vehicle {vehicleId} is Mode {vehicle.Mode}. Mode C dispatch is the only thing standby serves (R-01).");
        }

        if (!DispatchVehicleTypes.IsKnown(vehicle.VehicleType))
        {
            // Only reachable if registry-svc's CHECK and this list drift apart. Refusing beats
            // indexing under a key nothing will ever read.
            throw new MageRideException(
                MageRideErrors.InvalidVehicleType,
                $"'{vehicle.VehicleType}' is not a tier the Mode C candidate index is keyed by (AL-09).");
        }

        var existing = await presence.FindAsync(connection, null, command.DriverId, cancellationToken);

        // Changing vehicles while holding an offer or carrying a passenger. The presence row is
        // one-per-driver, so this would silently rewrite which vehicle the live ride is being
        // served by — including the plate the passenger is watching for.
        if (existing is { } current &&
            current.VehicleId != vehicleId &&
            current.State is PresenceStates.Offered or PresenceStates.OnRide)
        {
            throw new MageRideException(
                MageRideErrors.DriverAlreadyLive,
                $"This driver is {current.State} on another vehicle. Finish or decline that ride before switching.");
        }

        PresenceRow row;
        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            row = await presence.GoOnlineAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                command.DriverId,
                vehicleId,
                vehicle.VehicleType,
                position,
                driverHome,
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        // Redis after the commit, never inside the transaction: a cache write that a ROLLBACK
        // cannot take back would advertise a driver the database says is offline. The other order
        // costs a few milliseconds of a driver not being found, which the next heartbeat repairs.
        await index.IndexAvailableAsync(
            command.DriverId, vehicleId, vehicle.VehicleType, position, cancellationToken);

        var grid = new H3Grid(_options.H3Resolution, _options.H3RingK);

        logger.LogInformation(
            "Driver {DriverId} is on standby on {VehicleType} {VehicleId}, indexed in res-{Resolution} cell {Cell}",
            command.DriverId, vehicle.VehicleType, vehicleId, _options.H3Resolution, grid.CellAt(position));

        return row;
    }

    public async Task<PresenceRow> GoOfflineAsync(Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var row = await presence.GoOfflineAsync(connection, null, driverId, cancellationToken);

        // Redis is cleared whether or not a row existed: a stale index entry with no durable row
        // behind it is the one failure mode that survives a restart, and DEL is free.
        await index.ForgetAsync(driverId, cancellationToken);

        if (row is null)
        {
            // The contract has no "you were never online" error and going offline twice is not a
            // fault, so a driver with no presence row gets the state they asked for.
            logger.LogInformation("Driver {DriverId} went offline without ever being on standby", driverId);

            return new PresenceRow(
                driverId, Guid.Empty, string.Empty, PresenceStates.Offline, null, null, null, DateTimeOffset.UtcNow);
        }

        // DT-04 says going offline also clears any Directional Travel filter and emits
        // `directional.cleared`. Nothing here can set one — POST /v1/standby/directional is C036 —
        // so there is deliberately no clear-and-emit that would always be a no-op.
        logger.LogInformation("Driver {DriverId} left standby", driverId);

        return row;
    }

    public async Task<PresenceRow?> GetAsync(Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await presence.FindAsync(connection, null, driverId, cancellationToken);
    }

    private static Guid RequireVehicleId(string? value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["vehicleId"] = ["vehicleId is required and must be a ULID or a UUID."],
            });

    private static GeoPoint RequirePlace(StandbyPlace? place, string field) =>
        OptionalPlace(place, field)
        ?? throw new MageRideValidationException(new Dictionary<string, string[]>
        {
            [$"{field}.lat"] = [$"{field} is required."],
        });

    private static GeoPoint? OptionalPlace(StandbyPlace? place, string field)
    {
        if (place is null)
        {
            return null;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (place.Lat is not { } lat || double.IsNaN(lat) || lat is < -90 or > 90)
        {
            errors[$"{field}.lat"] = [$"{field}.lat is required and must be between -90 and 90."];
        }

        if (place.Lng is not { } lng || double.IsNaN(lng) || lng is < -180 or > 180)
        {
            errors[$"{field}.lng"] = [$"{field}.lng is required and must be between -180 and 180."];
        }

        return errors.Count == 0
            ? new GeoPoint(place.Lat!.Value, place.Lng!.Value)
            : throw new MageRideValidationException(errors);
    }
}
