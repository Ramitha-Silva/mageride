using Dapper;
using MageRide.Registry.Domain;
using Npgsql;

namespace MageRide.Registry.Persistence;

/// <summary><c>registry.vehicles</c> (server_db_schema.md §2, D4' §2; migration 0303).</summary>
public interface IVehicleRepository
{
    /// <summary>
    /// Inserts a registration. Returns <see langword="null"/> when
    /// <c>ux_vehicles_regno_active</c> already holds the plate (D-37) rather than throwing —
    /// the duplicate is an expected answer on this route, not a fault.
    /// </summary>
    Task<Vehicle?> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid ownerId,
        string registrationNumber,
        string vehicleType,
        string mode,
        string driverName,
        string? driverPhotoUrl,
        CancellationToken cancellationToken);

    Task<Vehicle?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>Every vehicle the driver owns, oldest first (US-2.8).</summary>
    Task<IReadOnlyList<Vehicle>> ListByOwnerAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a PENDING vehicle to APPROVED, as the dev seed path (C021) and the AL-30 auto-approve
    /// (C029) both need. Returns the updated row, or <see langword="null"/> when the vehicle was
    /// REJECTED or DEACTIVATED and so cannot be approved from where it stands.
    /// </summary>
    Task<Vehicle?> ApproveAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>
    /// Takes a vehicle off the map (US-2.16). Returns <see langword="null"/> when it is already
    /// DEACTIVATED, so a repeat is a <c>409</c> rather than a silent success that emits a second
    /// round of revocations.
    /// </summary>
    Task<Vehicle?> DeactivateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>
    /// Updates the driver name and photo a passenger sees for this vehicle (US-2.12). Cosmetic —
    /// it does not touch the AL-29 verified identity fields on <c>registry.driver_profiles</c>.
    /// </summary>
    Task<Vehicle?> UpdateDriverProfileAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        string? driverName,
        string? driverPhotoUrl,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVehicleRepository"/>
public sealed class VehicleRepository : IVehicleRepository
{
    /// <summary>Unique-violation. Postgres reports every unique index breach as 23505.</summary>
    private const string UniqueViolation = "23505";

    private const string Columns =
        "id, owner_id, registration_number, vehicle_type, mode, status, onboarding_status, " +
        "dispatch_state, driver_name, driver_photo_url, created_at";

    public async Task<Vehicle?> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid ownerId,
        string registrationNumber,
        string vehicleType,
        string mode,
        string driverName,
        string? driverPhotoUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            // No pre-flight SELECT: two registrations racing on one plate would both pass it and
            // one would still die on the index. The index is the check.
            return await connection.QuerySingleAsync<Vehicle>(new CommandDefinition(
                $"""
                 INSERT INTO registry.vehicles
                   (owner_id, registration_number, vehicle_type, mode, driver_name, driver_photo_url)
                 VALUES (@OwnerId, @RegistrationNumber, @VehicleType, @Mode, @DriverName, @DriverPhotoUrl)
                 RETURNING {Columns};
                 """,
                new { OwnerId = ownerId, RegistrationNumber = registrationNumber, VehicleType = vehicleType, Mode = mode, DriverName = driverName, DriverPhotoUrl = driverPhotoUrl },
                transaction,
                cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation && ex.ConstraintName == "ux_vehicles_regno_active")
        {
            return null;
        }
    }

    public Task<Vehicle?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<Vehicle>(new CommandDefinition(
            $"SELECT {Columns} FROM registry.vehicles WHERE id = @VehicleId;",
            new { VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Vehicle>> ListByOwnerAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid ownerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // `id` breaks the tie so a page rendered twice in the same millisecond does not reorder.
        var vehicles = await connection.QueryAsync<Vehicle>(new CommandDefinition(
            $"SELECT {Columns} FROM registry.vehicles WHERE owner_id = @OwnerId ORDER BY created_at, id;",
            new { OwnerId = ownerId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. vehicles];
    }

    public Task<Vehicle?> ApproveAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Conditional on the current status, so approving twice is a no-op that still returns the
        // row, while a REJECTED or DEACTIVATED vehicle returns nothing and the caller answers 409.
        // status and onboarding_status move together: AL-30 treats "approved" as the derived view
        // of the same fact, and leaving them apart is what makes a vehicle Approved on one screen
        // and Incomplete on the next.
        return connection.QuerySingleOrDefaultAsync<Vehicle>(new CommandDefinition(
            $"""
             UPDATE registry.vehicles
                SET status = '{RegistrationStatuses.Approved}',
                    onboarding_status = '{OnboardingStatuses.Approved}',
                    rejection_reason = NULL
              WHERE id = @VehicleId
                AND status IN ('{RegistrationStatuses.Pending}', '{RegistrationStatuses.Approved}')
             RETURNING {Columns};
             """,
            new { VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<Vehicle?> DeactivateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Conditional on not already being deactivated, so the second call returns nothing and the
        // caller answers 409 — deactivating twice would otherwise emit a second `vehicle.deactivated`
        // and a second round of share revocations for grants that are already gone.
        //
        // DEACTIVATED is outside ux_vehicles_regno_active's predicate, so this releases the plate
        // (D-37) and the same registration can be onboarded again later — which is exactly what
        // US-2.16 asks for and the reason the index is partial.
        return connection.QuerySingleOrDefaultAsync<Vehicle>(new CommandDefinition(
            $"""
             UPDATE registry.vehicles
                SET status = '{RegistrationStatuses.Deactivated}',
                    onboarding_status = '{OnboardingStatuses.Incomplete}'
              WHERE id = @VehicleId
                AND status <> '{RegistrationStatuses.Deactivated}'
             RETURNING {Columns};
             """,
            new { VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<Vehicle?> UpdateDriverProfileAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        string? driverName,
        string? driverPhotoUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // COALESCE on the name and a sentinel on the photo: the contract makes both optional, but
        // a driver clearing their photo is a real edit, so `photoUrl: ""` must be able to null the
        // column while an absent field leaves it alone.
        return connection.QuerySingleOrDefaultAsync<Vehicle>(new CommandDefinition(
            $"""
             UPDATE registry.vehicles
                SET driver_name = COALESCE(@DriverName, driver_name),
                    driver_photo_url = CASE WHEN @UpdatePhoto THEN @DriverPhotoUrl ELSE driver_photo_url END
              WHERE id = @VehicleId
             RETURNING {Columns};
             """,
            new
            {
                VehicleId = vehicleId,
                DriverName = driverName,
                UpdatePhoto = driverPhotoUrl is not null,
                DriverPhotoUrl = string.IsNullOrEmpty(driverPhotoUrl) ? null : driverPhotoUrl,
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}
