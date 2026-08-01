using System.Globalization;
using Dapper;
using MageRide.Shared.Caching;
using MageRide.TestKit;
using StackExchange.Redis;

namespace MageRide.PublicBff.Tests.Infrastructure;

/// <summary>A ride and everybody attached to it, as the services that own the rows would write them.</summary>
internal sealed record SeededRide(
    Guid RideId, Guid BookerId, Guid DriverId, Guid VehicleId, string BookerPhone, string DriverPhone);

/// <summary>
/// Rows written the way their owning services write them.
/// </summary>
/// <remarks>
/// <b>The share tokens are inserted the way notification-svc mints them</b> (C051's
/// <c>ShareTokenMinter</c>: base64url over 32 random bytes, the token as its own primary key, the
/// trip or the location request as the subject) rather than by calling that service — C066 is a
/// reader of those rows, and booting a fourth process to write three columns would test C051's
/// minting rather than this surface's reading.
/// </remarks>
internal sealed class PublicBffSeed(PostgresFixture postgres, IConnectionMultiplexer redis)
{
    /// <summary>Colombo Fort, near enough.</summary>
    public const double PickupLat = 6.9344;

    public const double PickupLng = 79.8428;

    /// <summary>Dehiwala, about 9 km south — far enough to be a journey.</summary>
    public const double DropoffLat = 6.8511;

    public const double DropoffLng = 79.8653;

    /// <summary>
    /// A ride with a booker, an accepted driver and a vehicle.
    /// </summary>
    /// <param name="kind">0 passenger · 1 proxy · 2 package (0601's <c>ck_rides_kind</c>).</param>
    public async Task<SeededRide> RideAsync(
        string state = "InProgress",
        short kind = 2,
        string paymentMethod = "cod",
        long? fareEstimateMinor = 45_000,
        string? recipientName = "Nimali",
        DateTimeOffset? terminalAt = null)
    {
        await using var connection = await postgres.OpenAsync();

        var suffix = Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture);

        var bookerId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var rideId = Guid.NewGuid();

        var bookerPhone = $"+9477{suffix}1";
        var driverPhone = $"+9477{suffix}2";

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name)
            VALUES (@BookerId, @BookerPhone, 'passenger', 'Sanduni'),
                   (@DriverId, @DriverPhone, 'driver', 'Kasun');

            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name, driver_photo_url)
            VALUES (@VehicleId, @DriverId, @RegNo, 'three_wheeler', 'C', 'APPROVED', 'Kasun Perera',
                    'https://cdn.mageride.test/drivers/kasun.jpg');

            INSERT INTO registry.driver_profiles (driver_id, display_name, photo_url, verified_at)
            VALUES (@DriverId, 'Kasun Perera', 'https://cdn.mageride.test/drivers/kasun.jpg', now());
            """,
            new
            {
                BookerId = bookerId,
                BookerPhone = bookerPhone,
                DriverId = driverId,
                DriverPhone = driverPhone,
                VehicleId = vehicleId,
                RegNo = $"WP-CAB-{suffix}",
            });

        // A package needs both OTP digests and a recipient number (ck_rides_package_complete,
        // ck_rides_package_recipient); a proxy needs a rider name and a way to reach them
        // (ck_rides_proxy_identity). Written as the constraints demand rather than around them.
        await connection.ExecuteAsync(
            """
            INSERT INTO rides.rides
              (id, passenger_id, client_request_id, booker_id, rider_id, rider_name, rider_phone_hash, is_proxy, kind,
               vehicle_type, pickup_geo, dropoff_geo, state, accepted_driver_id, accepted_vehicle_id,
               package_size, pickup_otp_hash, delivery_otp_hash, recipient_name, recipient_phone,
               payment_method, fare_estimate_minor, currency, terminal_at)
            VALUES
              (@RideId, @BookerId, gen_random_uuid(), @BookerId,
               NULL,
               CASE WHEN @Kind = 1 THEN 'Tharindu' END,
               CASE WHEN @Kind = 1 THEN '\x02'::bytea END,
               @Kind = 1,
               @Kind,
               'three_wheeler',
               ST_SetSRID(ST_MakePoint(@PickupLng, @PickupLat), 4326)::geography,
               ST_SetSRID(ST_MakePoint(@DropoffLng, @DropoffLat), 4326)::geography,
               @State, @DriverId, @VehicleId,
               CASE WHEN @Kind = 2 THEN 'M' END,
               CASE WHEN @Kind = 2 THEN '\x00'::bytea END,
               CASE WHEN @Kind = 2 THEN '\x01'::bytea END,
               CASE WHEN @Kind = 2 THEN @RecipientName END,
               CASE WHEN @Kind = 2 THEN @RecipientPhone END,
               @PaymentMethod, @FareEstimateMinor, 'LKR', @TerminalAt);
            """,
            new
            {
                RideId = rideId,
                BookerId = bookerId,
                DriverId = driverId,
                VehicleId = vehicleId,
                Kind = kind,
                State = state,
                PickupLat,
                PickupLng,
                DropoffLat,
                DropoffLng,
                RecipientName = recipientName,
                RecipientPhone = $"+9477{suffix}3",
                PaymentMethod = paymentMethod,
                FareEstimateMinor = fareEstimateMinor,
                TerminalAt = terminalAt,
            });

        return new SeededRide(rideId, bookerId, driverId, vehicleId, bookerPhone, driverPhone);
    }

    /// <summary>A trip-scoped token, exactly as notification-svc mints one.</summary>
    public async Task<string> TokenAsync(
        Guid tripId,
        string scope,
        DateTimeOffset expiresAt,
        DateTimeOffset? revokedAt = null)
    {
        var token = NewToken();

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO safety.trip_share_tokens (token, trip_id, scope, expires_at, revoked_at)
            VALUES (@Token, @TripId, @Scope, @ExpiresAt, @RevokedAt);
            """,
            new { Token = token, TripId = tripId, Scope = scope, ExpiresAt = expiresAt, RevokedAt = revokedAt });

        return token;
    }

    /// <summary>
    /// AL-45's pair: a live <c>rides.location_requests</c> row and the <c>pickup_confirm</c> token
    /// bound to it.
    /// </summary>
    /// <remarks>
    /// The token points at the surrogate <c>id</c> and never at the public <c>request_id</c> handle —
    /// 0901's foreign key is onto the primary key and 0606 keeps the two distinct on purpose. Getting
    /// this wrong is the mistake the test is here to catch.
    /// </remarks>
    public async Task<(string Token, Guid Id, Guid RequestId, Guid BookerId)> PickupRequestAsync(
        DateTimeOffset issuedAt,
        string state = "RiderNotRegistered",
        int ttlSeconds = 300,
        Guid? rideId = null,
        DateTimeOffset? tokenExpiresAt = null)
    {
        await using var connection = await postgres.OpenAsync();

        var bookerId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var token = NewToken();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name)
            VALUES (@BookerId, @Phone, 'passenger', 'Sanduni');

            INSERT INTO rides.location_requests
              (id, ride_id, request_id, booker_id, rider_phone_hash, state, issued_at, ttl_seconds)
            VALUES (@Id, @RideId, @RequestId, @BookerId, '\x03'::bytea, @State, @IssuedAt, @TtlSeconds);

            INSERT INTO safety.trip_share_tokens (token, scope, location_request_id, expires_at)
            VALUES (@Token, 'pickup_confirm', @Id, @ExpiresAt);
            """,
            new
            {
                BookerId = bookerId,
                Phone = $"+9477{Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture)}9",
                Id = id,
                RideId = rideId,
                RequestId = requestId,
                State = state,
                IssuedAt = issuedAt,
                TtlSeconds = ttlSeconds,
                Token = token,
                ExpiresAt = tokenExpiresAt ?? issuedAt.AddSeconds(ttlSeconds),
            });

        return (token, id, requestId, bookerId);
    }

    /// <summary>A settled payment attempt, as fare-svc writes one.</summary>
    public async Task PaymentAsync(Guid rideId, string state, string method, int amountMinor)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO fares.ride_payments (ride_id, state, method, amount_minor, currency)
            VALUES (@RideId, @State, @Method, @AmountMinor, 'LKR');
            """,
            new { RideId = rideId, State = state, Method = method, AmountMinor = amountMinor });
    }

    /// <summary>P-10's photograph, as ride-svc files one.</summary>
    public async Task ProofPhotoAsync(Guid rideId, string storageUrl = "s3://mageride-docs/ephemeral/proof/1.jpg")
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO rides.proof_artifacts (ride_id, kind, storage_url, sha256)
            VALUES (@RideId, 'delivery_photo', @StorageUrl, '\x04'::bytea);
            """,
            new { RideId = rideId, StorageUrl = storageUrl });
    }

    /// <summary>position-processor-svc's <c>veh:meta</c> hash, with the field names it writes.</summary>
    public async Task PositionAsync(
        Guid vehicleId, double lat, double lng, DateTimeOffset sampledAt, double? speedMps = 8.0)
    {
        await redis.GetDatabase().HashSetAsync(
            RedisKeys.VehicleMeta(vehicleId),
            [
                new HashEntry("lat", lat.ToString(CultureInfo.InvariantCulture)),
                new HashEntry("lng", lng.ToString(CultureInfo.InvariantCulture)),
                new HashEntry("speed", (speedMps ?? 0).ToString(CultureInfo.InvariantCulture)),
                new HashEntry("sampleTs", sampledAt.ToString("O", CultureInfo.InvariantCulture)),
            ]);
    }

    /// <summary>The plaintext delivery code, where notification-svc's <c>DeliveryCodeStore</c> leaves it.</summary>
    public Task DeliveryCodeAsync(Guid rideId, string code) =>
        redis.GetDatabase().StringSetAsync(RedisKeys.PackageDeliveryCode(rideId), code, TimeSpan.FromHours(4));

    /// <summary>Base64url over 32 random bytes — C051's shape, and 43 characters long.</summary>
    private static string NewToken() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
