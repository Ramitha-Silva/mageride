using Dapper;
using MageRide.Iam.Auth;
using MageRide.Shared.Auth;
using Npgsql;

namespace MageRide.Iam.Tests.Infrastructure;

/// <summary>
/// Creates the rows a portal sign-in or an MQTT token needs and no endpoint in this component
/// can make.
/// </summary>
/// <remarks>
/// <para>
/// This is not laziness about using the API. A portal account is <em>provisioned</em>: internal
/// roles by a Super Admin (AL-06) and fleet users by their owner (AL-03), through admin-bff (C062)
/// and fleet-svc (C058), neither of which exists. A vehicle is registry-svc's (C021/C028) and a
/// ride is ride-svc's (C022). Reaching for those services here would make this suite depend on
/// four others to test one.
/// </para>
/// <para>
/// Every insert is the real DDL with its real constraints, so a seeded row is a row the owning
/// service could have written — and a mistake here fails on a CHECK rather than passing a test.
/// </para>
/// </remarks>
internal sealed class IamSeed(string connectionString, PasswordHasher passwords)
{
    /// <summary>An email address no other test in this run will use.</summary>
    public static string NextEmail(string prefix) =>
        $"{prefix}.{Guid.NewGuid():N}@mageride.test";

    /// <summary>
    /// A portal account: <c>iam.users</c> with an email, the role grant, and — when a password is
    /// given — the <c>iam.user_credentials</c> verifier.
    /// </summary>
    public async Task<Guid> PortalUserAsync(
        string email, string role, string? password = null, bool isBlocked = false)
    {
        var userId = Guid.NewGuid();

        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, email, role, first_name, is_blocked)
            VALUES (@Id, @Email, @Role, 'Portal', @IsBlocked);
            """,
            new { Id = userId, Email = email, Role = role, IsBlocked = isBlocked });

        await connection.ExecuteAsync(
            "INSERT INTO iam.user_roles (user_id, role) VALUES (@Id, @Role) ON CONFLICT DO NOTHING;",
            new { Id = userId, Role = role });

        if (password is not null)
        {
            await connection.ExecuteAsync(
                "INSERT INTO iam.user_credentials (user_id, password_hash) VALUES (@Id, @Hash);",
                new { Id = userId, Hash = passwords.Hash(password) });
        }

        return userId;
    }

    /// <summary>
    /// Gives an existing account a portal credential — the union an app user who is also staff
    /// ends up with (<c>iam.users</c> allows both a phone and an email).
    /// </summary>
    public async Task SetEmailAsync(Guid userId, string email, string? password)
    {
        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            "UPDATE iam.users SET email = @Email WHERE id = @Id;",
            new { Id = userId, Email = email });

        if (password is not null)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO iam.user_credentials (user_id, password_hash)
                VALUES (@Id, @Hash)
                ON CONFLICT (user_id) DO UPDATE SET password_hash = EXCLUDED.password_hash;
                """,
                new { Id = userId, Hash = passwords.Hash(password) });
        }
    }

    /// <summary>The provider binding a federated sign-in left behind (0107).</summary>
    public async Task<(string Provider, string Subject)> FederatedIdentityAsync(Guid userId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<(string, string)>(
            "SELECT provider, subject FROM iam.federated_identities WHERE user_id = @Id;",
            new { Id = userId });
    }

    /// <summary>Blocks an account, as moderation would (URD §2.3 "End-user account management").</summary>
    public async Task BlockAsync(Guid userId)
    {
        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            "UPDATE iam.users SET is_blocked = true WHERE id = @Id;",
            new { Id = userId });
    }

    /// <summary>Grants a second canonical role, as a Super Admin would (AL-06).</summary>
    public async Task GrantRoleAsync(Guid userId, string role)
    {
        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            "INSERT INTO iam.user_roles (user_id, role) VALUES (@Id, @Role) ON CONFLICT DO NOTHING;",
            new { Id = userId, Role = role });
    }

    /// <summary>A fleet and a membership in it — the <c>fleet_role</c>/<c>fleet_id</c> pair (AL-03).</summary>
    public async Task<Guid> FleetMemberAsync(Guid userId, string fleetRole = FleetRoles.Owner)
    {
        var fleetId = Guid.NewGuid();

        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.fleets (id, owner_id, name, status)
            VALUES (@FleetId, @UserId, 'Test Fleet', 'APPROVED');
            """,
            new { FleetId = fleetId, UserId = userId });

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.fleet_members (fleet_id, user_id, fleet_role)
            VALUES (@FleetId, @UserId, @FleetRole);
            """,
            new { FleetId = fleetId, UserId = userId, FleetRole = fleetRole });

        return fleetId;
    }

    /// <summary>Reads back the lock-out columns AL-37 replaced the second factor with.</summary>
    public async Task<(short FailedAttempts, DateTimeOffset? LockedUntil)> CredentialStateAsync(Guid userId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<(short, DateTimeOffset?)>(
            "SELECT failed_attempts, locked_until FROM iam.user_credentials WHERE user_id = @Id;",
            new { Id = userId });
    }

    /// <summary>The surface a user's live sessions belong to — <c>iam.sessions.app</c> (0107).</summary>
    public async Task<IReadOnlyList<string>> ActiveSessionAppsAsync(Guid userId)
    {
        await using var connection = await OpenAsync();

        var apps = await connection.QueryAsync<string>(
            "SELECT app FROM iam.sessions WHERE user_id = @Id AND revoked_at IS NULL ORDER BY app;",
            new { Id = userId });

        return [.. apps];
    }

    /// <summary>The platform recorded for a user's device rows (0107 adds <c>web</c>).</summary>
    public async Task<IReadOnlyList<string>> DevicePlatformsAsync(Guid userId)
    {
        await using var connection = await OpenAsync();

        var platforms = await connection.QueryAsync<string>(
            "SELECT platform FROM iam.devices WHERE user_id = @Id ORDER BY platform;",
            new { Id = userId });

        return [.. platforms];
    }

    /// <summary>The push token verify wrote onto the device row (C020 gap (e), closed by 0107).</summary>
    public async Task<string?> DeviceFcmTokenAsync(Guid userId, string deviceKey)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT fcm_apns_token FROM iam.devices WHERE user_id = @Id AND device_key = @DeviceKey;",
            new { Id = userId, DeviceKey = deviceKey });
    }

    /// <summary>A driver account with an APPROVED Mode C vehicle, as registry-svc would leave it.</summary>
    public async Task<Guid> ApprovedVehicleAsync(Guid ownerId)
    {
        var vehicleId = Guid.NewGuid();

        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, onboarding_status, driver_name)
            VALUES
              (@VehicleId, @OwnerId, @Plate, 'three_wheeler', 'C', 'APPROVED', 'approved', 'Test Driver');
            """,
            new
            {
                VehicleId = vehicleId,
                OwnerId = ownerId,
                // ux_vehicles_regno_active is unique over PENDING/APPROVED (D-37), so two tests
                // registering "WP-QA-0001" would collide.
                Plate = $"WP-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            });

        return vehicleId;
    }

    /// <summary>
    /// A ride in one of the four states <c>ux_rides_driver_busy</c> allows — what "the driver has
    /// an active ride" means (O2, R-10).
    /// </summary>
    public async Task<Guid> ActiveRideAsync(
        Guid passengerId, Guid driverId, Guid vehicleId, string state = "InProgress", DateTimeOffset? startedAt = null)
    {
        var rideId = Guid.NewGuid();

        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO rides.rides
              (id, passenger_id, booker_id, client_request_id, vehicle_type,
               pickup_geo, dropoff_geo, state, accepted_driver_id, accepted_vehicle_id, created_at)
            VALUES
              (@RideId, @PassengerId, @PassengerId, @ClientRequestId, 'three_wheeler',
               ST_SetSRID(ST_MakePoint(79.8612, 6.9271), 4326)::geography,
               ST_SetSRID(ST_MakePoint(79.8712, 6.9371), 4326)::geography,
               @State, @DriverId, @VehicleId, @CreatedAt);
            """,
            new
            {
                RideId = rideId,
                PassengerId = passengerId,
                ClientRequestId = Guid.NewGuid(),
                State = state,
                DriverId = driverId,
                VehicleId = vehicleId,
                CreatedAt = startedAt ?? DateTimeOffset.UtcNow,
            });

        return rideId;
    }

    /// <summary>A passenger account, for the far end of a seeded ride.</summary>
    public async Task<Guid> PassengerAsync(string phone)
    {
        var userId = Guid.NewGuid();

        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, 'passenger');",
            new { Id = userId, Phone = phone });

        return userId;
    }

    /// <summary>A Mode A/B tracking session, as trip-state-svc would leave it (C038's table).</summary>
    public async Task<Guid> ActiveTripSessionAsync(Guid driverId, Guid vehicleId, string mode = "B")
    {
        var sessionId = Guid.NewGuid();

        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO trips.sessions (id, vehicle_id, driver_id, mode, state)
            VALUES (@Id, @VehicleId, @DriverId, @Mode, 'ACTIVE');
            """,
            new { Id = sessionId, VehicleId = vehicleId, DriverId = driverId, Mode = mode });

        return sessionId;
    }

    /// <summary>A day's <c>fares.driver_earnings</c> rollup, as fare-svc would leave it (C047's table).</summary>
    public async Task EarningsAsync(Guid driverId, DateOnly businessDate, int trips, int grossMinor, int dailyFeeMinor)
    {
        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO fares.driver_earnings (driver_id, earn_date, trips, gross_minor, daily_fee_minor)
            VALUES (@DriverId, @EarnDate, @Trips, @Gross, @Fee)
            ON CONFLICT (driver_id, earn_date) DO UPDATE
              SET trips = EXCLUDED.trips, gross_minor = EXCLUDED.gross_minor, daily_fee_minor = EXCLUDED.daily_fee_minor;
            """,
            new { DriverId = driverId, EarnDate = businessDate, Trips = trips, Gross = grossMinor, Fee = dailyFeeMinor });
    }

    /// <summary>
    /// The denormalised SOS contact on <c>iam.users</c> — the two columns <c>POST /v1/sos</c> reads
    /// inside D-33's five-second budget.
    /// </summary>
    public async Task<(string? Name, string? Phone)> PrimaryEmergencyContactAsync(Guid userId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<(string?, string?)>(
            "SELECT emergency_contact_name, emergency_contact_phone FROM iam.users WHERE id = @Id;",
            new { Id = userId });
    }

    /// <summary>The raw <c>notif_prefs</c> document, so a test can prove a key survived the round trip.</summary>
    public async Task<string?> NotificationPreferencesJsonAsync(Guid userId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT notif_prefs::text FROM iam.users WHERE id = @Id;",
            new { Id = userId });
    }

    /// <summary>Every column of <c>iam.phone_lookups</c> a test cares about (P-03, 0108).</summary>
    public async Task<IReadOnlyList<(byte[] PhoneHash, bool Registered, Guid? UserId, string? Caller)>>
        PhoneLookupsAsync()
    {
        await using var connection = await OpenAsync();

        var rows = await connection.QueryAsync<(byte[], bool, Guid?, string?)>(
            "SELECT phone_hash, registered, user_id, caller FROM iam.phone_lookups ORDER BY looked_up_at;");

        return [.. rows];
    }

    /// <summary>The caller's PDPA rows (E-06).</summary>
    public async Task<IReadOnlyList<(Guid Id, string Kind, string Status, DateTimeOffset DueBy)>>
        PdpaRequestsAsync(Guid userId)
    {
        await using var connection = await OpenAsync();

        var rows = await connection.QueryAsync<(Guid, string, string, DateTimeOffset)>(
            "SELECT id, kind, status, due_by FROM pdpa.requests WHERE user_id = @Id ORDER BY requested_at;",
            new { Id = userId });

        return [.. rows];
    }

    /// <summary>The <c>granted_by</c> provenance AL-06 wants on an internal role grant.</summary>
    public async Task<Guid?> RoleGrantedByAsync(Guid userId, string role)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT granted_by FROM iam.user_roles WHERE user_id = @Id AND role = @Role;",
            new { Id = userId, Role = role });
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }
}
