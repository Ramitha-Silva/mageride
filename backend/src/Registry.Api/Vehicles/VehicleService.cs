using MageRide.Registry.Domain;
using MageRide.Registry.Persistence;
using MageRide.Registry.Sharing;
using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;

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

/// <summary>
/// One entry of <c>GET /v1/vehicles/mine</c> — an entitlement, how it was come by, and whether it
/// is the selected one.
/// </summary>
/// <param name="Entitlement">
/// The <c>registry.driver_eligible_vehicles</c> row. <c>Source</c> is what splits the response
/// into US-13.9's two groups: the driver's own registrations, and the ones a fleet lent them.
/// </param>
public sealed record DriverVehicle(EligibleVehicle Entitlement, bool IsSelected);

/// <summary>The outcome of <see cref="IVehicleService.SelectLiveAsync"/>.</summary>
public sealed record LiveSelection(EligibleVehicle Vehicle, Guid? ReleasedVehicleId, DateTimeOffset SelectedAt);

/// <summary>Body of <c>PUT /v1/vehicles/{vehicleId}/driver-profile</c> (US-2.12).</summary>
public sealed record UpdateVehicleDriverProfileCommand(Guid DriverId, Guid VehicleId, string? Name, string? PhotoUrl);

/// <summary>
/// Vehicle identity and lifecycle: register, read, list, deactivate, and choose the single
/// vehicle a driver may go live on.
/// </summary>
public interface IVehicleService
{
    Task<Vehicle> RegisterAsync(RegisterVehicleCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Everything the driver may operate — their own registrations and the vehicles a fleet has
    /// lent them (US-2.8, US-13.9).
    /// </summary>
    Task<IReadOnlyList<DriverVehicle>> ListMineAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>One vehicle, visible to the owner and to an assigned driver.</summary>
    Task<DriverVehicle> GetAsync(Guid driverId, Guid vehicleId, CancellationToken cancellationToken);

    Task<LiveSelection> SelectLiveAsync(Guid driverId, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>Takes a vehicle off the map and revokes every live share on it (US-2.16).</summary>
    Task DeactivateAsync(Guid ownerId, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>Updates the driver name and photo passengers see for this vehicle (US-2.12).</summary>
    Task<DriverVehicle> UpdateDriverProfileAsync(
        UpdateVehicleDriverProfileCommand command, CancellationToken cancellationToken);

    /// <summary>The dev seed path's approval. See <see cref="Configuration.RegistryOptions"/>.</summary>
    Task<Vehicle> ApproveAsync(Guid driverId, Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVehicleService"/>
public sealed class VehicleService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IVehicleRepository vehicles,
    IDriverProfileRepository profiles,
    IEligibilityRepository eligibility,
    IShareRepository shares,
    IOutboxWriter outbox,
    IDriverLiveVehicleCache liveVehicles,
    TimeProvider clock,
    ILogger<VehicleService> logger) : IVehicleService
{
    /// <summary><c>registry.yaml</c>'s <c>maxLength: 200</c> on the driver-profile name.</summary>
    private const int MaxDriverNameLength = 200;

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

    public async Task<IReadOnlyList<DriverVehicle>> ListMineAsync(Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        // The projection, not registry.vehicles: US-13.9's assigned driver did not register the
        // vehicle and would not appear in an owner-scoped read at all.
        var entitlements = await eligibility.ListAsync(connection, null, driverId, cancellationToken);
        var profile = await profiles.FindAsync(connection, null, driverId, cancellationToken);

        return
        [
            .. entitlements.Select(entitlement =>
                new DriverVehicle(entitlement, entitlement.VehicleId == profile?.ActiveVehicleId)),
        ];
    }

    public async Task<DriverVehicle> GetAsync(Guid driverId, Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var entitlement = await RequireEntitlementAsync(connection, null, driverId, vehicleId, cancellationToken);
        var profile = await profiles.FindAsync(connection, null, driverId, cancellationToken);

        return new DriverVehicle(entitlement, entitlement.VehicleId == profile?.ActiveVehicleId);
    }

    public async Task<LiveSelection> SelectLiveAsync(Guid driverId, Guid vehicleId, CancellationToken cancellationToken)
    {
        Guid? released;
        EligibleVehicle vehicle;
        DateTimeOffset selectedAt;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            vehicle = await RequireEntitlementAsync(
                unitOfWork.Connection, unitOfWork.Transaction, driverId, vehicleId, cancellationToken);

            if (!vehicle.IsGoLiveEligible)
            {
                throw new MageRideException(
                    MageRideErrors.VehicleNotApproved,
                    $"Vehicle {vehicleId} is {vehicle.Status}/{vehicle.DispatchState}. Only an APPROVED vehicle that " +
                    "is not document-suspended can be taken live (US-9.6, E-03).");
            }

            // A driver who registered a vehicle has a profile — RegisterAsync creates one — but an
            // assigned driver (US-13.9) may never have registered anything, and a row seeded
            // straight into registry.vehicles need not have one either. Create it rather than 404
            // on an account that plainly exists.
            await profiles.EnsureAsync(
                unitOfWork.Connection, unitOfWork.Transaction, driverId, vehicle.DriverName, cancellationToken);

            var before = await profiles.FindAsync(
                unitOfWork.Connection, unitOfWork.Transaction, driverId, cancellationToken);
            released = before?.ActiveVehicleId == vehicleId ? null : before?.ActiveVehicleId;

            // One UPDATE of one column on a row whose primary key is the driver. That is what
            // makes "selecting a vehicle live releases the previous one atomically" true — there
            // is no window in which two are selected, because there is only one place to put the
            // answer (US-9.6, migration 0308).
            if (!await profiles.SelectActiveVehicleAsync(
                    unitOfWork.Connection, unitOfWork.Transaction, driverId, vehicleId, cancellationToken))
            {
                throw new MageRideException(
                    MageRideErrors.InternalError, "The driver profile disappeared while the selection was being written.");
            }

            var after = await profiles.FindAsync(
                unitOfWork.Connection, unitOfWork.Transaction, driverId, cancellationToken);

            // The instant comes back from the row rather than from the process clock, so the value
            // a caller is told is the value the dashboard will read (US-9.7).
            selectedAt = after?.ActiveVehicleSelectedAt ?? clock.GetUtcNow();

            await unitOfWork.CommitAsync(cancellationToken);
        }

        // After COMMIT. Publishing the selection before it is durable would let a rolled-back
        // transaction leave the dispatch and tracking planes pointing at a vehicle the registry
        // never selected — the same ordering the outbox exists to enforce (D-03).
        await liveVehicles.PublishAsync(driverId, vehicleId, cancellationToken);

        logger.LogInformation(
            "Driver {DriverId} selected {Source} vehicle {VehicleId} as their live publisher (released {ReleasedVehicleId})",
            driverId, vehicle.Source, vehicleId, released);

        return new LiveSelection(vehicle, released, selectedAt);
    }

    public async Task DeactivateAsync(Guid ownerId, Guid vehicleId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        bool wasSelected;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            // Ownership, not entitlement: an assigned driver may operate a fleet vehicle and may
            // not retire it (US-13.7 puts that on the fleet operator, in the Fleet Portal).
            var vehicle = await RequireOwnedVehicleAsync(unitOfWork, ownerId, vehicleId, cancellationToken);

            var deactivated = await vehicles.DeactivateAsync(
                unitOfWork.Connection, unitOfWork.Transaction, vehicle.Id, cancellationToken);

            if (deactivated is null)
            {
                throw new MageRideException(
                    MageRideErrors.Conflict, $"Vehicle {vehicleId} is already deactivated.");
            }

            // Everybody watching it loses visibility with it. Doing this in the same transaction
            // is the point: a vehicle that is off the map while a grant still says otherwise is
            // exactly the leak D-22 is about.
            var revoked = await shares.RevokeAllForVehicleAsync(
                unitOfWork.Connection, unitOfWork.Transaction, vehicle.Id, now, cancellationToken);

            var events = new List<OutboxRecord>(revoked.Count + 1)
            {
                ShareEvents.VehicleDeactivated(vehicle.Id, ownerId),
            };

            events.AddRange(revoked.Select(grant => ShareEvents.ShareRevoked(grant, "vehicle-deactivated")));

            await outbox.WriteAsync(unitOfWork, events, cancellationToken);

            // fk_driver_profiles_active_vehicle is ON DELETE SET NULL, not ON UPDATE, so a status
            // change does not clear the selection — the C021 handoff left this to C028 and this is
            // it. A DEACTIVATED vehicle that stayed selected would fail the eligibility gate on
            // every go-online with no way for the driver to see why.
            wasSelected = await profiles.ClearActiveVehicleAsync(
                unitOfWork.Connection, unitOfWork.Transaction, ownerId, vehicle.Id, cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Vehicle {VehicleId} deactivated by {OwnerId}; revoked {RevokedCount} live share grants",
                vehicleId, ownerId, revoked.Count);
        }

        if (wasSelected)
        {
            await liveVehicles.ClearAsync(ownerId, cancellationToken);
        }
    }

    public async Task<DriverVehicle> UpdateDriverProfileAsync(
        UpdateVehicleDriverProfileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var name = command.Name?.Trim();

        if (name is { Length: > MaxDriverNameLength } or "")
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["name"] = [$"name must be between 1 and {MaxDriverNameLength} characters."],
            });
        }

        var photoUrl = command.PhotoUrl?.Trim();

        // An empty string clears the photo; anything else has to be a URL a client can render.
        if (!string.IsNullOrEmpty(photoUrl) && !Uri.TryCreate(photoUrl, UriKind.Absolute, out _))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["photoUrl"] = ["photoUrl must be an absolute URI, or empty to clear it."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var vehicle = await RequireOwnedVehicleAsync(unitOfWork, command.DriverId, command.VehicleId, cancellationToken);

        _ = await vehicles.UpdateDriverProfileAsync(
            unitOfWork.Connection, unitOfWork.Transaction, vehicle.Id, name, photoUrl, cancellationToken);

        var entitlement = await RequireEntitlementAsync(
            unitOfWork.Connection, unitOfWork.Transaction, command.DriverId, command.VehicleId, cancellationToken);

        var profile = await profiles.FindAsync(
            unitOfWork.Connection, unitOfWork.Transaction, command.DriverId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return new DriverVehicle(entitlement, entitlement.VehicleId == profile?.ActiveVehicleId);
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

    /// <summary>
    /// Loads the caller's entitlement to a vehicle — owned, or assigned by a fleet (US-13.9).
    /// </summary>
    /// <remarks>
    /// The distinction <see cref="RequireOwnedVehicleAsync"/> makes between 404 and 403 is not
    /// available here and must not be faked. The projection is scoped by driver, so a vehicle the
    /// caller has no entitlement to and a vehicle that does not exist are literally the same
    /// query result; answering 403 for one of them would require a second read whose only purpose
    /// is to tell a stranger that somebody else's plate is registered.
    /// </remarks>
    private async Task<EligibleVehicle> RequireEntitlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        CancellationToken cancellationToken) =>
        await eligibility.FindAsync(connection, transaction, driverId, vehicleId, cancellationToken)
        ?? throw new MageRideException(
            MageRideErrors.VehicleNotFound,
            $"No vehicle {vehicleId} that this driver owns or is assigned to.");

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
