using Dapper;
using MageRide.TestKit;

namespace MageRide.Safety.Tests.Infrastructure;

/// <summary>An account this service can address.</summary>
internal sealed record SeededUser(Guid Id, string Phone, string? EmergencyContactPhone);

/// <summary>
/// The rows other bounded contexts own that this service reads.
/// </summary>
/// <remarks>
/// Written with SQL rather than by calling iam-svc, registry-svc and ride-svc: this suite is about
/// what safety-svc does with an account, a vehicle and a ride, and standing up three more services
/// to create them would make it fail for reasons that are not this component's. Every column set
/// here is one a real service writes — <c>emergency_contact_phone</c> in particular is the
/// denormalised pair iam-svc re-derives inside every mutation of <c>iam.emergency_contacts</c>
/// (AL-13), which is exactly why safety-svc reads it and never joins.
/// </remarks>
internal sealed class SafetySeed(PostgresFixture postgres)
{
    private readonly PostgresFixture _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));

    private int _counter;

    public async Task<SeededUser> UserAsync(
        string role = "passenger",
        string? emergencyContactPhone = null,
        string? emergencyContactName = null,
        string language = "en")
    {
        var id = Guid.NewGuid();
        var phone = $"+9477{Interlocked.Increment(ref _counter):D7}";

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users
              (id, phone, role, first_name, language, emergency_contact_name, emergency_contact_phone)
            VALUES (@Id, @Phone, @Role, 'Nimal', @Language, @ContactName, @ContactPhone);
            """,
            new
            {
                Id = id,
                Phone = phone,
                Role = role,
                Language = language,
                ContactName = emergencyContactName ?? (emergencyContactPhone is null ? null : "Kamala"),
                ContactPhone = emergencyContactPhone,
            });

        return new SeededUser(id, phone, emergencyContactPhone);
    }

    /// <summary>An approved vehicle, as registry-svc would leave one.</summary>
    public async Task<Guid> VehicleAsync(Guid ownerId, string vehicleType = "three_wheeler")
    {
        var id = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name, dispatch_state)
            VALUES (@Id, @OwnerId, @Reg, @VehicleType, 'C', 'APPROVED', 'Nimal', 'ACTIVE');
            """,
            new
            {
                Id = id,
                OwnerId = ownerId,
                Reg = $"WP-{Interlocked.Increment(ref _counter):D5}",
                VehicleType = vehicleType,
            });

        return id;
    }

    /// <summary>A Mode C ride in flight, as ride-svc would leave one.</summary>
    public async Task<Guid> RideAsync(
        Guid passengerId, Guid? driverId = null, Guid? vehicleId = null, string state = "InProgress")
    {
        var id = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO rides.rides
              (id, passenger_id, client_request_id, booker_id, vehicle_type, pickup_geo, dropoff_geo,
               state, accepted_driver_id, accepted_vehicle_id, payment_method)
            VALUES
              (@Id, @PassengerId, @ClientRequestId, @PassengerId, 'three_wheeler',
               ST_SetSRID(ST_MakePoint(79.861, 6.927), 4326)::geography,
               ST_SetSRID(ST_MakePoint(79.877, 6.901), 4326)::geography,
               @State, @DriverId, @VehicleId, 'cash');
            """,
            new
            {
                Id = id,
                PassengerId = passengerId,
                ClientRequestId = Guid.NewGuid(),
                State = state,
                DriverId = driverId,
                VehicleId = vehicleId,
            });

        return id;
    }

    /// <summary>Moves a ride to a terminal state, as ride-svc's own transition would.</summary>
    public async Task EndRideAsync(Guid rideId, string state = "Paid")
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            "UPDATE rides.rides SET state = @State, terminal_at = now() WHERE id = @Id;",
            new { Id = rideId, State = state });
    }

    /// <summary>
    /// The live position a shared link draws, written the way position-processor-svc writes it.
    /// </summary>
    /// <remarks>
    /// The field names are that service's and are the contract between them — the same arrangement
    /// query-svc's <c>LiveVehicleIndex</c> and fanout-svc's <c>VehicleSnapshotReader</c> are under.
    /// </remarks>
    public static async Task PositionAsync(
        RedisFixture redis, Guid vehicleId, double lat, double lng, DateTimeOffset sampledAt, int heading = 90)
    {
        var connection = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);

        await using (connection)
        {
            await connection.GetDatabase().HashSetAsync(
                MageRide.Shared.Caching.RedisKeys.VehicleMeta(vehicleId),
                [
                    new StackExchange.Redis.HashEntry("lat", lat.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new StackExchange.Redis.HashEntry("lng", lng.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new StackExchange.Redis.HashEntry("heading", heading),
                    new StackExchange.Redis.HashEntry("speed", "8.5"),
                    new StackExchange.Redis.HashEntry("type", "three_wheeler"),
                    new StackExchange.Redis.HashEntry("mode", "C"),
                    new StackExchange.Redis.HashEntry("sampleTs", sampledAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
                ]);
        }
    }

    /// <summary>A driver standing by, as dispatch-svc's presence heartbeat would leave them.</summary>
    public async Task PresenceAsync(Guid driverId, Guid vehicleId, double lat, double lng, string vehicleType = "three_wheeler")
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO dispatch.driver_presence (driver_id, vehicle_id, vehicle_type, state, geo, last_seen_at)
            VALUES (@DriverId, @VehicleId, @VehicleType, 'AVAILABLE',
                    ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography, now())
            ON CONFLICT (driver_id) DO UPDATE
               SET vehicle_id = EXCLUDED.vehicle_id,
                   vehicle_type = EXCLUDED.vehicle_type,
                   state = EXCLUDED.state,
                   geo = EXCLUDED.geo,
                   last_seen_at = EXCLUDED.last_seen_at;
            """,
            new { DriverId = driverId, VehicleId = vehicleId, VehicleType = vehicleType, Lat = lat, Lng = lng });
    }

    /// <summary>A P-12 audit row, as ride-svc writes one when a request resolves.</summary>
    public async Task LocationRequestAuditAsync(Guid bookerId, string decision, byte[]? riderPhoneHash = null)
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO safety.location_request_audit (booker_id, rider_phone_hash, request_id, decision)
            VALUES (@BookerId, @Hash, @RequestId, @Decision);
            """,
            new
            {
                BookerId = bookerId,
                Hash = riderPhoneHash ?? [0x01, 0x02, 0x03],
                RequestId = Guid.NewGuid(),
                Decision = decision,
            });
    }
}
