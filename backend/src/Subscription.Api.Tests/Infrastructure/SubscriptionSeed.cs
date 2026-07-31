using System.Globalization;
using Dapper;
using MageRide.TestKit;

namespace MageRide.Subscriptions.Tests.Infrastructure;

/// <summary>A seeded driver with a bearer, and — usually — a vehicle they are live on.</summary>
internal sealed record SeededDriver(Guid Id, string Bearer);

/// <summary>A seeded vehicle.</summary>
internal sealed record SeededVehicle(Guid Id, Guid OwnerId, string VehicleType, string Mode, string Registration);

/// <summary>
/// Rows this component's rule reads but does not own: drivers, vehicles, fleets and Mode C rides.
/// </summary>
/// <remarks>
/// Written with SQL rather than through registry-svc and ride-svc: standing those two up would test
/// C028 and C036 again and make this suite fail for reasons that are not this component's. The columns
/// set are exactly the NOT NULLs and the CHECK-relevant ones, so a schema change that breaks the read
/// still breaks this suite.
/// </remarks>
internal sealed class SubscriptionSeed(PostgresFixture postgres, SubscriptionHarness harness)
{
    /// <summary>An <c>iam.users</c> row with the driver role, plus a bearer for it.</summary>
    public async Task<SeededDriver> DriverAsync(long openingBalanceMinor = 0)
    {
        var id = await UserAsync("driver");

        await using (var connection = await postgres.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO registry.driver_profiles (driver_id, display_name)
                VALUES (@Id, @Name) ON CONFLICT (driver_id) DO NOTHING;
                """,
                new { Id = id, Name = $"Driver {id.ToString()[..8]}" });
        }

        if (openingBalanceMinor != 0)
        {
            await CreditAsync(id, openingBalanceMinor);
        }

        return new SeededDriver(id, harness.Tokens.Driver(id));
    }

    public async Task<Guid> UserAsync(string role)
    {
        var id = Guid.NewGuid();

        await using var connection = await postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name) VALUES (@Id, @Phone, @Role, @Name);
            INSERT INTO iam.user_roles (user_id, role) VALUES (@Id, @Role) ON CONFLICT DO NOTHING;
            """,
            new
            {
                Id = id,
                Phone = "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture),
                Role = role,
                Name = $"User {id.ToString()[..8]}",
            });

        return id;
    }

    /// <summary>An APPROVED vehicle owned by <paramref name="ownerId"/>.</summary>
    public async Task<SeededVehicle> VehicleAsync(
        Guid ownerId,
        string vehicleType = "three_wheeler",
        string mode = "C",
        DateTimeOffset? createdAt = null)
    {
        var id = Guid.NewGuid();
        var registration = $"TEST-{Random.Shared.NextInt64(100_000, 999_999).ToString(CultureInfo.InvariantCulture)}";

        await using var connection = await postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name,
               onboarding_status, created_at)
            VALUES
              (@Id, @OwnerId, @Registration, @VehicleType, @Mode, 'APPROVED', @DriverName,
               'approved', coalesce(@CreatedAt, now()));
            """,
            new
            {
                Id = id,
                OwnerId = ownerId,
                Registration = registration,
                VehicleType = vehicleType,
                Mode = mode,
                DriverName = $"Driver {ownerId.ToString()[..8]}",
                CreatedAt = createdAt,
            });

        return new SeededVehicle(id, ownerId, vehicleType, mode, registration);
    }

    /// <summary>US-9.6's "the single active vehicle selected in vehicle management".</summary>
    public async Task SelectLiveAsync(Guid driverId, Guid vehicleId)
    {
        await using var connection = await postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            UPDATE registry.driver_profiles
               SET active_vehicle_id = @VehicleId, active_vehicle_selected_at = now()
             WHERE driver_id = @DriverId;
            """,
            new { DriverId = driverId, VehicleId = vehicleId });
    }

    /// <summary>An APPROVED fleet owning the given vehicles (AL-03).</summary>
    public async Task<Guid> FleetAsync(Guid ownerId, params Guid[] vehicleIds)
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);

        var id = Guid.NewGuid();

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.fleets (id, owner_id, name, status)
            VALUES (@Id, @OwnerId, @Name, 'APPROVED');
            """,
            new { Id = id, OwnerId = ownerId, Name = $"Fleet {id.ToString()[..8]}" });

        foreach (var vehicleId in vehicleIds)
        {
            // `mode` is copied from the vehicle rather than passed in: 0306's CHECK admits A and B only
            // — a fleet never operates Mode C (AL-03) — and duplicating the value in the test would let
            // the roster disagree with the vehicle it names.
            await connection.ExecuteAsync(
                """
                INSERT INTO registry.fleet_vehicles (fleet_id, vehicle_id, mode)
                SELECT @FleetId, v.id, v.mode FROM registry.vehicles v WHERE v.id = @VehicleId;
                """,
                new { FleetId = id, VehicleId = vehicleId });
        }

        return id;
    }

    /// <summary>
    /// A Mode C ride and the <c>dispatch.offers</c> row that records the driver taking it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The offer is the part that matters.</b> D5' §2.2's <c>tripsToday</c> is counted from
    /// <c>dispatch.offers</c> — by dispatch-svc's D-08 gate and by this component's charge, which have
    /// to agree — so a trip that exists only as a <c>rides.rides</c> row would be invisible to both.
    /// The ride is written too because <c>dispatch.offers.ride_id</c> is a foreign key to it.
    /// </para>
    /// <para>
    /// Finished by default: <c>ux_rides_driver_busy</c> admits one live ride per driver (O2/R-10) and
    /// <c>ux_offers_driver_live</c> one un-released ACCEPTED offer, so a day's worth of trips has to be
    /// a day's worth of <em>finished</em> trips. <paramref name="at"/> is both the ride's
    /// <c>created_at</c> and the offer's <c>responded_at</c>, which is what the Colombo-day count is
    /// bounded by.
    /// </para>
    /// </remarks>
    /// <param name="offerStatus">
    /// <c>ACCEPTED</c> counts as a trip; <c>OFFERED</c>, <c>DECLINED</c> and <c>EXPIRED</c> do not.
    /// </param>
    /// <param name="live">
    /// <see langword="true"/> leaves <c>released_at</c> NULL — the trip the driver is on right now.
    /// Only one such offer may exist per driver.
    /// </param>
    public async Task<Guid> RideAsync(
        Guid driverId,
        Guid vehicleId,
        DateTimeOffset at,
        string state = "Completed",
        string offerStatus = "ACCEPTED",
        bool live = false)
    {
        var id = Guid.NewGuid();
        var passengerId = await UserAsync("passenger");

        await using var connection = await postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO rides.rides
              (id, passenger_id, client_request_id, booker_id, vehicle_type,
               pickup_geo, dropoff_geo, state, accepted_driver_id, accepted_vehicle_id, created_at)
            VALUES
              (@Id, @PassengerId, gen_random_uuid(), @PassengerId, 'three_wheeler',
               ST_SetSRID(ST_MakePoint(79.861, 6.927), 4326)::geography,
               ST_SetSRID(ST_MakePoint(79.874, 6.901), 4326)::geography,
               @State, @DriverId, @VehicleId, @At);

            INSERT INTO dispatch.offers
              (ride_id, driver_id, status, sent_at, expires_at, responded_at, released_at)
            VALUES
              (@Id, @DriverId, @OfferStatus, @At, @At + interval '15 seconds', @At,
               CASE WHEN @Live THEN NULL ELSE @At END);
            """,
            new
            {
                Id = id,
                PassengerId = passengerId,
                State = state,
                DriverId = driverId,
                VehicleId = vehicleId,
                At = at,
                OfferStatus = offerStatus,
                Live = live,
            });

        return id;
    }

    /// <summary>
    /// Gives a driver an opening balance the way an admin adjustment would — through wallet-svc.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <c>UPDATE billing.accounts SET balance_minor</c>: a balance that did not come
    /// from postings is a balance the ledger disagrees with, and this suite asserts that the ledger sums
    /// to zero after every fee.
    /// </remarks>
    public async Task CreditAsync(Guid driverId, long amountMinor)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/internal/wallet/{driverId}/credit")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new
            {
                amountMinor,
                kind = "adjustment",
                idempotencyKey = $"c047-opening:{driverId}:{Guid.NewGuid()}",
                description = "opening balance",
            }),
        };

        request.Headers.TryAddWithoutValidation(
            "X-MageRide-Internal-Key", SubscriptionHarness.WalletInternalApiKey);

        using var response = await harness.Wallet.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == System.Net.HttpStatusCode.OK,
            $"Seeding a balance for {driverId} returned {(int)response.StatusCode}: {text}");
    }
}
