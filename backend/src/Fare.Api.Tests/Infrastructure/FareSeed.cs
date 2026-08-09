using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using MageRide.Shared.Auth;
using MageRide.TestKit;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Fare.Tests.Infrastructure;

/// <summary>A seeded ride and everything it needs to be priced.</summary>
internal sealed record SeededRide(Guid RideId, Guid PassengerId, Guid DriverId, Guid VehicleId);

/// <summary>
/// Rows this component prices but does not own: users, vehicles, Mode C rides and their tracks.
/// </summary>
/// <remarks>
/// Written with SQL rather than through ride-svc and registry-svc: standing those two up would test
/// C032 and C028 again and make this suite fail for reasons that are not this component's. The
/// columns set are exactly the NOT NULLs and the ones the fare reads, so a schema change that breaks
/// the read still breaks this suite.
/// </remarks>
internal sealed class FareSeed(PostgresFixture postgres)
{
    public async Task<Guid> UserAsync(string role)
    {
        var id = Guid.NewGuid();

        await using var connection = await postgres.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role, first_name) VALUES (@Id, @Phone, @Role, @Name);",
            new
            {
                Id = id,
                Phone = "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture),
                Role = role,
                Name = $"User {id.ToString()[..8]}",
            });

        return id;
    }

    public async Task<Guid> VehicleAsync(Guid ownerId, string vehicleType = "three_wheeler")
    {
        var id = Guid.NewGuid();

        await using var connection = await postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name, onboarding_status)
            VALUES (@Id, @OwnerId, @Registration, @VehicleType, 'C', 'APPROVED', 'Test Driver', 'approved');
            """,
            new
            {
                Id = id,
                OwnerId = ownerId,
                Registration = $"TEST-{Random.Shared.NextInt64(100_000, 999_999).ToString(CultureInfo.InvariantCulture)}",
                VehicleType = vehicleType,
            });

        return id;
    }

    /// <summary>
    /// A completed Mode C ride sitting in <c>PaymentPending</c>, which is where ride-svc leaves one
    /// for fare-svc (its "Completed is not terminal" rule).
    /// </summary>
    public async Task<SeededRide> RideAsync(
        string vehicleType = "three_wheeler",
        string state = "PaymentPending",
        string paymentMethod = "cash",
        long? fareEstimateMinor = 40_000,
        DateTimeOffset? requestedAt = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null,
        Guid? passengerId = null,
        Guid? bookerId = null)
    {
        var driverId = await UserAsync("driver");
        var passenger = passengerId ?? await UserAsync("passenger");
        var vehicleId = await VehicleAsync(driverId, vehicleType);
        var rideId = Guid.NewGuid();

        var requested = requestedAt ?? FareHarness.DefaultNow.AddMinutes(-30);

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO rides.rides
              (id, passenger_id, client_request_id, booker_id, vehicle_type, pickup_geo, dropoff_geo,
               state, accepted_driver_id, accepted_vehicle_id, payment_method, fare_estimate_minor,
               created_at)
            VALUES
              (@Id, @PassengerId, gen_random_uuid(), @BookerId, @VehicleType,
               ST_SetSRID(ST_MakePoint(79.8612, 6.9271), 4326)::geography,
               ST_SetSRID(ST_MakePoint(79.8740, 6.9010), 4326)::geography,
               @State, @DriverId, @VehicleId, @PaymentMethod, @FareEstimateMinor, @RequestedAt);
            """,
            new
            {
                Id = rideId,
                PassengerId = passenger,
                BookerId = bookerId ?? passenger,
                VehicleType = vehicleType,
                State = state,
                DriverId = driverId,
                VehicleId = vehicleId,
                PaymentMethod = paymentMethod,
                FareEstimateMinor = fareEstimateMinor,
                RequestedAt = requested,
            });

        // The audited transitions the travel window is read from. Without an InProgress row there is
        // no window at all, which is the "ride never started" case.
        if (startedAt is { } started)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO rides.transitions (ride_id, from_state, to_state, actor_type, ts)
                VALUES (@Id, 'DriverArrived', 'InProgress', 'driver', @Started),
                       (@Id, 'InProgress', 'Completed', 'driver', @Ended);
                """,
                new { Id = rideId, Started = started, Ended = endedAt ?? started.AddMinutes(15) });
        }

        return new SeededRide(rideId, passenger, driverId, vehicleId);
    }

    /// <summary>One <c>fares.ride_payments</c> attempt in a given state (D-10's retry chain).</summary>
    public async Task PaymentAsync(
        Guid rideId, string state, string method = "onepay", long amountMinor = 40_000, int attemptNo = 1)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO fares.ride_payments
                (ride_id, state, method, amount_minor, attempt_no, created_at, updated_at)
            VALUES (@RideId, @State, @Method, @AmountMinor::int, @AttemptNo::smallint, @CreatedAt, @CreatedAt);
            """,
            new
            {
                RideId = rideId,
                State = state,
                Method = method,
                AmountMinor = amountMinor,
                AttemptNo = attemptNo,
                CreatedAt = FareHarness.DefaultNow,
            });
    }

    /// <summary>D-11's OnePay merchant binding, as registry-svc leaves it on vehicle approval.</summary>
    public async Task MerchantAsync(Guid driverId, string merchantId)
    {
        await using var connection = await postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO registry.driver_payouts (driver_id, onepay_merchant_id)
            VALUES (@DriverId, @MerchantId)
            ON CONFLICT (driver_id) DO UPDATE
              SET onepay_merchant_id = EXCLUDED.onepay_merchant_id, status = 'ACTIVE';
            """,
            new { DriverId = driverId, MerchantId = merchantId });
    }

    /// <summary>
    /// A straight-line track for a vehicle, one fix per second — the rows E-04 measures.
    /// </summary>
    /// <remarks>
    /// Written straight into <c>telemetry.positions</c>, which is the hypertable the ingest plane
    /// fills. Clean rather than noisy: the filter's behaviour on noise is a unit test, and what this
    /// suite is asserting is that the settlement path reads the right rows for the right window.
    /// </remarks>
    public async Task TrackAsync(Guid vehicleId, DateTimeOffset from, int seconds, double speedMps)
    {
        await using var connection = await postgres.OpenAsync();

        const double originLat = 6.9271;
        const double originLng = 79.8612;
        var metresPerDegreeLng = 111_320.0 * Math.Cos(double.DegreesToRadians(originLat));

        for (var i = 0; i <= seconds; i++)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO telemetry.positions
                  (vehicle_id, sample_ts, seq, lat, lng, accuracy_m, source)
                VALUES (@VehicleId, @SampleTs, @Seq, @Lat, @Lng, 5, 0);
                """,
                new
                {
                    VehicleId = vehicleId,
                    SampleTs = from.AddSeconds(i),
                    Seq = (long)i,
                    Lat = originLat,
                    Lng = originLng + (i * speedMps / metresPerDegreeLng),
                });
        }
    }
}

/// <summary>
/// Mints the RS256 access tokens iam-svc would (D-29), signed by a key this test run owns.
/// </summary>
/// <remarks>
/// fare-svc is a token consumer: it holds no signing key and in production resolves iam-svc's public
/// half over JWKS. Standing a whole iam-svc up to get a bearer would test C020 again.
/// </remarks>
internal sealed class TestTokenIssuer
{
    private const string Issuer = "https://iam.mageride.test";

    private static readonly JsonWebTokenHandler Handler = new();

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly SigningCredentials _credentials;

    public TestTokenIssuer()
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
        _credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        PublicKey = new RsaSecurityKey(_rsa.ExportParameters(includePrivateParameters: false)) { KeyId = "test-key" };
    }

    /// <summary>What the harness gives the bearer handler in place of a JWKS fetch.</summary>
    public SecurityKey PublicKey { get; }

    public string IssuerName => Issuer;

    public string Passenger(Guid userId) => Issue(userId, MageRideRoles.Passenger, MageRideApps.Passenger);

    public string Driver(Guid userId) => Issue(userId, MageRideRoles.Driver, MageRideApps.Driver);

    /// <summary>A Finance Officer — E-05's refund is theirs.</summary>
    public string Finance(Guid userId) => Issue(userId, MageRideRoles.FinanceOfficer, MageRideApps.Admin);

    private string Issue(Guid userId, string role, string app)
    {
        var now = DateTime.UtcNow;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
                [MageRideClaims.Role] = role,
                [MageRideClaims.App] = app,
                [MageRideClaims.DeviceId] = "test-device",
            },
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(30),
            SigningCredentials = _credentials,
        };

        return Handler.CreateToken(descriptor);
    }
}
