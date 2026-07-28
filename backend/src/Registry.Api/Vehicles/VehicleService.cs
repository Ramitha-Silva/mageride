using MageRide.Registry.Domain;
using MageRide.Registry.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace MageRide.Registry.Vehicles;

/// <param name="OwnerId">The authenticated driver — vehicles are owned by whoever registered them.</param>
/// <param name="DriverName">
/// Optional. Defaults from <c>registry.driver_profiles</c> when the driver has already been
/// through Profile Setup; required when they have not, because
/// <c>registry.vehicles.driver_name</c> is NOT NULL and is what a passenger sees (US-2.12).
/// </param>
public sealed record RegisterVehicleCommand(
    Guid OwnerId, string? RegistrationNumber, string? VehicleType, string? Mode, string? DriverName);

/// <summary>A vehicle plus whether it is the driver's currently selected one (US-9.6).</summary>
public sealed record OwnedVehicle(Vehicle Vehicle, bool IsSelected);

/// <summary>The outcome of <see cref="IVehicleService.SelectLiveAsync"/>.</summary>
public sealed record LiveSelection(Vehicle Vehicle, DateTimeOffset SelectedAt);

/// <summary>
/// The walking skeleton's vehicle identity: register a Mode C vehicle, list what a driver owns,
/// and choose the single one they may go live on.
/// </summary>
public interface IVehicleService
{
    Task<Vehicle> RegisterAsync(RegisterVehicleCommand command, CancellationToken cancellationToken);

    Task<IReadOnlyList<OwnedVehicle>> ListMineAsync(Guid ownerId, CancellationToken cancellationToken);

    Task<LiveSelection> SelectLiveAsync(Guid driverId, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>The dev seed path's approval. See <see cref="Configuration.RegistryOptions"/>.</summary>
    Task<Vehicle> ApproveAsync(Guid driverId, Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVehicleService"/>
public sealed class VehicleService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IVehicleRepository vehicles,
    IDriverProfileRepository profiles,
    ILogger<VehicleService> logger) : IVehicleService
{
    public async Task<Vehicle> RegisterAsync(RegisterVehicleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var registrationNumber = RequireRegistrationNumber(command.RegistrationNumber);
        RequireDriverAppVehicleType(command.VehicleType);
        RequireModeC(command.Mode);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        // The profile and the vehicle commit together: a registration that failed on the plate
        // must not leave a profile row behind naming a driver who registered nothing.
        var profile = await ResolveProfileAsync(unitOfWork, command, cancellationToken);

        var vehicle = await vehicles.CreateAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            command.OwnerId,
            registrationNumber,
            command.VehicleType!,
            OperatingModes.C,
            profile.DisplayName,
            profile.PhotoUrl,
            cancellationToken);

        if (vehicle is null)
        {
            throw new MageRideException(
                MageRideErrors.RegistrationExists,
                $"Registration {registrationNumber} is already held by a live vehicle. A rejected or " +
                "deactivated registration frees its plate (D-37).");
        }

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Registered {VehicleType} {VehicleId} for driver {DriverId}",
            vehicle.VehicleType, vehicle.Id, command.OwnerId);

        return vehicle;
    }

    public async Task<IReadOnlyList<OwnedVehicle>> ListMineAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var owned = await vehicles.ListByOwnerAsync(connection, null, ownerId, cancellationToken);
        var profile = await profiles.FindAsync(connection, null, ownerId, cancellationToken);

        return [.. owned.Select(vehicle => new OwnedVehicle(vehicle, vehicle.Id == profile?.ActiveVehicleId))];
    }

    public async Task<LiveSelection> SelectLiveAsync(Guid driverId, Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var vehicle = await RequireOwnedVehicleAsync(unitOfWork, driverId, vehicleId, cancellationToken);

        if (!vehicle.IsSelectable)
        {
            throw new MageRideException(
                MageRideErrors.VehicleNotApproved,
                $"Vehicle {vehicleId} is {vehicle.Status}. Only an APPROVED vehicle can be taken live (US-9.6).");
        }

        // A driver who has a vehicle has a profile — RegisterAsync creates one — but a row
        // seeded straight into registry.vehicles need not, so create it rather than 404 on an
        // account that plainly exists.
        await profiles.EnsureAsync(
            unitOfWork.Connection, unitOfWork.Transaction, driverId, vehicle.DriverName, cancellationToken);

        // EnsureAsync just guaranteed the row, so a miss here means something removed it between
        // the two statements inside one transaction — an invariant break, not a caller error.
        if (!await profiles.SelectActiveVehicleAsync(
                unitOfWork.Connection, unitOfWork.Transaction, driverId, vehicleId, cancellationToken))
        {
            throw new MageRideException(
                MageRideErrors.InternalError, "The driver profile disappeared while the selection was being written.");
        }

        var profile = await profiles.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, driverId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Driver {DriverId} selected vehicle {VehicleId} as their live publisher", driverId, vehicleId);

        // The instant comes back from the row rather than from the process clock, so the value a
        // caller is told is the value the dashboard will read (US-9.7).
        return new LiveSelection(vehicle, profile?.ActiveVehicleSelectedAt ?? DateTimeOffset.UtcNow);
    }

    public async Task<Vehicle> ApproveAsync(Guid driverId, Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var vehicle = await RequireOwnedVehicleAsync(unitOfWork, driverId, vehicleId, cancellationToken);

        var approved = await vehicles.ApproveAsync(
            unitOfWork.Connection, unitOfWork.Transaction, vehicle.Id, cancellationToken);

        if (approved is null)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"Vehicle {vehicleId} is {vehicle.Status} and cannot be approved from there.");
        }

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogWarning(
            "Vehicle {VehicleId} was approved through the dev seed path — AL-10's insurance " +
            "requirement and the AL-30 onboarding steps were NOT checked",
            vehicleId);

        return approved;
    }

    /// <summary>
    /// Loads a vehicle and asserts the caller owns it. 404 for a vehicle that does not exist,
    /// 403 for one that belongs to somebody else — both are what the registry-svc contract's
    /// <c>x-error-codes</c> lists, and conflating them would make an ownership failure
    /// indistinguishable from a typo.
    /// </summary>
    private async Task<Vehicle> RequireOwnedVehicleAsync(
        IUnitOfWork unitOfWork, Guid driverId, Guid vehicleId, CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, vehicleId, cancellationToken)
            ?? throw new MageRideException(MageRideErrors.VehicleNotFound, $"No vehicle {vehicleId}.");

        return vehicle.OwnerId == driverId
            ? vehicle
            : throw new MageRideException(MageRideErrors.NotOwner, "This vehicle belongs to another driver.");
    }

    private async Task<DriverProfile> ResolveProfileAsync(
        IUnitOfWork unitOfWork, RegisterVehicleCommand command, CancellationToken cancellationToken)
    {
        var existing = await profiles.FindAsync(
            unitOfWork.Connection, unitOfWork.Transaction, command.OwnerId, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        // D3' has Profile Setup (PUT /v1/drivers/profile) precede vehicle onboarding, and it is
        // where the display name and photo come from. That endpoint needs a driving-licence
        // upload and AL-29 OCR, both fenced out of this slice, so the skeleton takes the name off
        // the registration body instead and creates the minimal profile row here. C029 replaces
        // this with the real Profile Setup; recorded in the C021 handoff.
        var displayName = command.DriverName?.Trim();

        if (string.IsNullOrEmpty(displayName) || displayName.Length > 200)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["driverName"] =
                [
                    "driverName is required until the driver has completed Profile Setup, and must be at " +
                    "most 200 characters.",
                ],
            });
        }

        return await profiles.EnsureAsync(
            unitOfWork.Connection, unitOfWork.Transaction, command.OwnerId, displayName, cancellationToken);
    }

    private static string RequireRegistrationNumber(string? value)
    {
        if (!RegistrationNumbers.TryNormalise(value, out var normalised))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["registrationNumber"] =
                [
                    $"registrationNumber is required, must be at most {RegistrationNumbers.MaxLength} characters, " +
                    "and may contain only letters, digits, spaces and hyphens.",
                ],
            });
        }

        return normalised;
    }

    /// <summary>
    /// AL-09 in two halves: a value outside the canonical ten is a 400, and one of the two Mode A
    /// types is a 403 — it is a real vehicle type, just not one this surface onboards.
    /// </summary>
    private static void RequireDriverAppVehicleType(string? vehicleType)
    {
        if (!VehicleTypes.IsCanonical(vehicleType))
        {
            throw new MageRideException(
                MageRideErrors.InvalidVehicleType,
                $"'{vehicleType}' is not a canonical vehicle type. AL-09 renamed 'car' to 'sedan'; the set is " +
                string.Join(", ", VehicleTypes.All.Order(StringComparer.Ordinal)) + ".");
        }

        if (!VehicleTypes.IsDriverApp(vehicleType))
        {
            throw new MageRideException(
                MageRideErrors.ModeNotAllowed,
                $"'{vehicleType}' is a Mode A vehicle. Buses are registered in the Fleet Portal and trains by " +
                "admin-bff; the Driver App onboards Mode C only.");
        }
    }

    private static void RequireModeC(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["mode"] = ["mode is required and must be 'C'."],
            });
        }

        if (!string.Equals(mode, OperatingModes.C, StringComparison.Ordinal))
        {
            throw new MageRideException(
                MageRideErrors.ModeNotAllowed,
                "The Driver App onboards Mode C only. Mode A and Mode B vehicles are onboarded in the Fleet " +
                "Portal (SCR-FP-004).");
        }
    }
}
