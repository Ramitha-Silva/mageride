using Dapper;
using MageRide.TestKit;

namespace MageRide.Voip.Tests.Infrastructure;

/// <summary>A ride, and the three accounts P-05 distinguishes between.</summary>
/// <param name="RiderId">
/// Null when the proxy rider is unregistered (P-03) — the case with no in-app call at all.
/// </param>
internal sealed record SeededRide(Guid Id, Guid PassengerId, Guid BookerId, Guid? RiderId, Guid DriverId);

/// <summary>
/// The rows other bounded contexts own that this service reads.
/// </summary>
/// <remarks>
/// Written with SQL rather than by calling iam-svc, registry-svc and ride-svc: this suite is about
/// what voip-svc does with a ride, and standing up three more services to create one would make it
/// fail for reasons that are not this component's. Every column set here is one a real service
/// writes.
/// </remarks>
internal sealed class VoipSeed(PostgresFixture postgres)
{
    private readonly PostgresFixture _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));

    private int _counter;

    public async Task<Guid> UserAsync(string role = "passenger")
    {
        var id = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role, first_name) VALUES (@Id, @Phone, @Role, 'Nimal');",
            new { Id = id, Phone = $"+9478{Interlocked.Increment(ref _counter):D7}", Role = role });

        return id;
    }

    /// <summary>An approved vehicle, as registry-svc would leave one.</summary>
    public async Task<Guid> VehicleAsync(Guid ownerId)
    {
        var id = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name, dispatch_state)
            VALUES (@Id, @OwnerId, @Reg, 'three_wheeler', 'C', 'APPROVED', 'Nimal', 'ACTIVE');
            """,
            new { Id = id, OwnerId = ownerId, Reg = $"WP-{Interlocked.Increment(ref _counter):D5}" });

        return id;
    }

    /// <summary>
    /// A Mode C ride in flight, as ride-svc would leave one.
    /// </summary>
    /// <param name="proxy">
    /// P-01's third-party booking: the booker is not the rider, and P-05 says the driver is bound to
    /// the rider. Every assertion about who may call turns on this flag.
    /// </param>
    /// <param name="registeredRider">
    /// False models P-03 — a proxy rider with no account, whose number is kept only as a digest.
    /// </param>
    public async Task<SeededRide> RideAsync(
        string state = "InProgress",
        bool proxy = false,
        bool registeredRider = true,
        bool accepted = true)
    {
        var passengerId = await UserAsync();
        var driverId = await UserAsync("driver");
        var vehicleId = await VehicleAsync(driverId);

        var riderId = proxy && registeredRider ? await UserAsync() : proxy ? null : (Guid?)null;

        var id = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO rides.rides
              (id, passenger_id, client_request_id, booker_id, rider_id, rider_name, is_proxy, kind,
               vehicle_type, pickup_geo, dropoff_geo, state, accepted_driver_id, accepted_vehicle_id,
               payment_method, rider_phone_hash)
            VALUES
              (@Id, @PassengerId, @ClientRequestId, @PassengerId, @RiderId, @RiderName, @IsProxy, @Kind,
               'three_wheeler',
               ST_SetSRID(ST_MakePoint(79.861, 6.927), 4326)::geography,
               ST_SetSRID(ST_MakePoint(79.877, 6.901), 4326)::geography,
               @State, @DriverId, @VehicleId, 'cash', @RiderPhoneHash);
            """,
            new
            {
                Id = id,
                PassengerId = passengerId,
                ClientRequestId = Guid.NewGuid(),
                RiderId = riderId,
                // ck_rides_proxy: a proxy ride needs a rider name and either an id or a phone digest.
                RiderName = proxy ? "Kamala" : null,
                IsProxy = proxy,
                Kind = proxy ? 1 : 0,
                State = state,
                DriverId = accepted ? driverId : (Guid?)null,
                VehicleId = accepted ? vehicleId : (Guid?)null,
                RiderPhoneHash = proxy && !registeredRider ? new byte[32] : null,
            });

        return new SeededRide(id, passengerId, passengerId, riderId, driverId);
    }
}
