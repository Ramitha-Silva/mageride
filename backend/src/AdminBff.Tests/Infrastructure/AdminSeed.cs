using Dapper;
using MageRide.TestKit;

namespace MageRide.AdminBff.Tests.Infrastructure;

/// <summary>
/// Rows the admin surface needs to have something to act on, written straight to Postgres.
/// </summary>
/// <remarks>
/// <b>Written directly rather than through the owning services.</b> Standing up registry-svc,
/// ride-svc, fare-svc and iam-svc to make one suspendable vehicle would make this suite a test of
/// four other components; every insert here is a plain row in a shape those components' own suites
/// already assert. Where a value has to agree with a rule — a Colombo business date, R-05's
/// terminal payment states — the seed says so at the insert.
/// </remarks>
internal sealed class AdminSeed(PostgresFixture postgres)
{
    /// <summary>An internal account, so a foreign key to <c>iam.users</c> resolves.</summary>
    public async Task<Guid> InternalUserAsync(string role)
    {
        var id = Guid.CreateVersion7();

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, email, role, first_name)
            VALUES (@Id, @Email, @Role, 'Test Operator');
            INSERT INTO iam.user_roles (user_id, role) VALUES (@Id, @Role) ON CONFLICT DO NOTHING;
            """,
            new { Id = id, Email = $"{id:N}@mageride.test", Role = role });

        return id;
    }

    /// <summary>A driver account and one APPROVED Mode C vehicle they own.</summary>
    public async Task<(Guid DriverId, Guid VehicleId)> DriverWithVehicleAsync(Guid? vehicleId = null)
    {
        var driverId = Guid.CreateVersion7();
        var id = vehicleId ?? Guid.CreateVersion7();

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name)
            VALUES (@DriverId, @Phone, 'driver', 'Test Driver');

            INSERT INTO iam.user_roles (user_id, role) VALUES (@DriverId, 'driver') ON CONFLICT DO NOTHING;

            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name, onboarding_status)
            VALUES
              (@VehicleId, @DriverId, @RegNo, 'three_wheeler', 'C', 'APPROVED', 'Test Driver', 'approved');
            """,
            new
            {
                DriverId = driverId,
                VehicleId = id,
                // +947 E.164, unique per seed: iam.users.phone is UNIQUE.
                Phone = $"+9477{Random.Shared.Next(1000000, 9999999)}",
                // D-37 makes the number unique across PENDING/APPROVED, so every seeded vehicle needs
                // its own — the whole id, not a prefix two v7 GUIDs minted in the same
                // millisecond would share.
                RegNo = $"T{id:N}",
            });

        return (driverId, id);
    }

    /// <summary>A live Mode A/B tracking session, so a suspension has something to end.</summary>
    public async Task<Guid> LiveSessionAsync(Guid driverId, Guid vehicleId)
    {
        var id = Guid.CreateVersion7();

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO trips.sessions (id, vehicle_id, driver_id, mode, state)
            VALUES (@Id, @VehicleId, @DriverId, 'B', 'ACTIVE');
            """,
            new { Id = id, VehicleId = vehicleId, DriverId = driverId });

        return id;
    }

    /// <summary>An AVAILABLE presence row — what dispatch-svc's candidate query reads (R-08).</summary>
    public async Task PresenceAsync(Guid driverId, Guid vehicleId)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO dispatch.driver_presence (driver_id, vehicle_id, vehicle_type, state, last_seen_at)
            VALUES (@DriverId, @VehicleId, 'three_wheeler', 'AVAILABLE', now())
            ON CONFLICT (driver_id) DO UPDATE SET state = 'AVAILABLE', vehicle_id = EXCLUDED.vehicle_id;
            """,
            new { DriverId = driverId, VehicleId = vehicleId });
    }

    /// <summary>
    /// One completed Mode C ride on <paramref name="colomboDate"/>, with the fare that collected.
    /// </summary>
    /// <remarks>
    /// The pieces C061 counts, and only those: the <c>rides.transitions</c> row whose
    /// <c>to_state = 'Completed'</c> <em>is</em> the trip's end, and one <c>fares.ride_payments</c>
    /// row in R-05's terminal set. The instant is 06:00 Colombo, which is 00:30 UTC — comfortably
    /// inside the day whichever way a boundary is read, so a failure here is about the query and
    /// never about the fixture straddling midnight.
    /// </remarks>
    public async Task CompletedRideAsync(Guid driverId, Guid passengerId, DateOnly colomboDate, long fareMinor)
    {
        var rideId = Guid.CreateVersion7();
        var at = new DateTimeOffset(colomboDate.ToDateTime(new TimeOnly(6, 0)), TimeSpan.FromHours(5.5));

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO rides.rides
                (id, passenger_id, booker_id, client_request_id, accepted_driver_id, vehicle_type,
                 pickup_geo, dropoff_geo, state, fare_estimate_minor, created_at, updated_at, terminal_at)
            VALUES
                (@RideId, @PassengerId, @PassengerId, gen_random_uuid(), @DriverId, 'three_wheeler',
                 ST_SetSRID(ST_MakePoint(79.861, 6.927), 4326)::geography,
                 ST_SetSRID(ST_MakePoint(79.884, 6.901), 4326)::geography,
                 'Paid', @FareMinor::int, @At, @At, @At);

            -- The transition IS the trip's end: a ride never rests in Completed, so the C061 rollup
            -- counts this row rather than rides.rides.state (see Analytics/CLAUDE.md).
            INSERT INTO rides.transitions (ride_id, from_state, to_state, actor_type, ts)
            VALUES (@RideId, 'InProgress', 'Completed', 'driver', @At);

            -- 'Succeeded' is one of R-05's four terminals — the set fare-svc's RidePaymentStates
            -- .Terminal holds and the set C061's gross-fare query sums.
            INSERT INTO fares.ride_payments
              (ride_id, attempt_no, method, amount_minor, currency, state, created_at, updated_at)
            VALUES (@RideId, 1::smallint, 'onepay', @FareMinor::int, 'LKR', 'Succeeded', @At, @At);
            """,
            new { RideId = rideId, PassengerId = passengerId, DriverId = driverId, At = at, FareMinor = fareMinor });
    }

    /// <summary>A passenger account whose <c>iam.user_roles</c> grant lands on a given Colombo day.</summary>
    public async Task<Guid> PassengerJoinedOnAsync(DateOnly colomboDate)
    {
        var id = Guid.CreateVersion7();
        var at = new DateTimeOffset(colomboDate.ToDateTime(new TimeOnly(6, 0)), TimeSpan.FromHours(5.5));

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name, created_at)
            VALUES (@Id, @Phone, 'passenger', 'Test Rider', @At);

            INSERT INTO iam.user_roles (user_id, role, granted_at)
            VALUES (@Id, 'passenger', @At) ON CONFLICT DO NOTHING;
            """,
            new { Id = id, Phone = $"+9476{Random.Shared.Next(1000000, 9999999)}", At = at });

        return id;
    }

    // -------------------------------------------------------------------------------------------
    // Verification (C063)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A driver who finished Profile Setup with one field a Verification Officer must decide.
    /// </summary>
    /// <remarks>
    /// The exact shape AL-29 produces: <c>licence_no</c> extracted with high confidence and
    /// therefore <c>auto_verified</c>, <c>nic_no</c> typed by the driver because the scan was
    /// unclear and therefore <c>manual</c> + <c>pending</c>. Only the second puts the driver in the
    /// queue, which is AL-27's fence — the first is invisible to the officer.
    /// </remarks>
    public async Task<(Guid DriverId, Guid DocId, Guid UploadId)> DriverAwaitingLicenceAsync()
    {
        var driverId = Guid.CreateVersion7();
        var docId = Guid.CreateVersion7();
        var uploadId = Guid.CreateVersion7();
        var storageUrl = $"s3://mageride-docs/licences/{uploadId:N}.jpg";

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name)
            VALUES (@DriverId, @Phone, 'driver', 'Nimal');
            INSERT INTO iam.user_roles (user_id, role) VALUES (@DriverId, 'driver') ON CONFLICT DO NOTHING;

            INSERT INTO registry.driver_profiles (driver_id, display_name, nic_no)
            VALUES (@DriverId, 'Nimal Perera', '199012345678');

            INSERT INTO docs.uploads (id, owner_id, storage_url, kind, captured_via)
            VALUES (@UploadId, @DriverId, @StorageUrl, 'driving_license', 'camera_dragcrop');

            INSERT INTO registry.documents (id, driver_id, vehicle_id, kind, file_url)
            VALUES (@DocId, @DriverId, NULL, 'driving_license', @StorageUrl);

            INSERT INTO registry.document_fields
                (document_id, field_key, field_value, confidence, source, verify_status)
            VALUES
                (@DocId, 'licence_no',    'B1234567',     0.960, 'ai',     'auto_verified'),
                (@DocId, 'nic_no',        '199012345678', NULL,  'manual', 'pending');
            """,
            new
            {
                DriverId = driverId,
                DocId = docId,
                UploadId = uploadId,
                StorageUrl = storageUrl,
                Phone = $"+9471{Random.Shared.Next(1000000, 9999999)}",
            });

        return (driverId, docId, uploadId);
    }

    /// <summary>
    /// A Mode C vehicle mid-wizard: three steps verified, the insurance step held by one doubtful
    /// field (AL-30).
    /// </summary>
    public async Task<(Guid DriverId, Guid VehicleId, Guid DocId)> VehicleAwaitingReviewAsync()
    {
        var driverId = Guid.CreateVersion7();
        var vehicleId = Guid.CreateVersion7();
        var docId = Guid.CreateVersion7();
        var storageUrl = $"s3://mageride-docs/insurance/{docId:N}.jpg";

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name)
            VALUES (@DriverId, @Phone, 'driver', 'Sunil');
            INSERT INTO iam.user_roles (user_id, role) VALUES (@DriverId, 'driver') ON CONFLICT DO NOTHING;

            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name, onboarding_status)
            VALUES (@VehicleId, @DriverId, @RegNo, 'three_wheeler', 'C', 'PENDING', 'Sunil Silva', 'incomplete');

            INSERT INTO docs.uploads (id, owner_id, storage_url, kind, captured_via)
            VALUES (gen_random_uuid(), @DriverId, @StorageUrl, 'insurance', 'gallery');

            INSERT INTO registry.documents (id, driver_id, vehicle_id, kind, file_url, expires_at)
            VALUES (@DocId, @DriverId, @VehicleId, 'insurance', @StorageUrl, now() + interval '300 days');

            -- One doubtful field is the whole reason this vehicle is in the queue (AL-29's
            -- below-threshold rule; registry-svc writes exactly this row).
            INSERT INTO registry.document_fields
                (document_id, field_key, field_value, confidence, source, verify_status)
            VALUES (@DocId, 'insurance_expiry', '2027-05-01', 0.410, 'ai', 'pending');

            INSERT INTO registry.onboarding_steps (vehicle_id, step, status, saved_at) VALUES
                (@VehicleId, 'details',   'verified',       now()),
                (@VehicleId, 'insurance', 'pending_review', now()),
                (@VehicleId, 'revenue',   'verified',       now()),
                (@VehicleId, 'photos',    'verified',       now());
            """,
            new
            {
                DriverId = driverId,
                VehicleId = vehicleId,
                DocId = docId,
                StorageUrl = storageUrl,
                Phone = $"+9472{Random.Shared.Next(1000000, 9999999)}",
                RegNo = $"V{vehicleId:N}",
            });

        return (driverId, vehicleId, docId);
    }

    /// <summary>
    /// A PENDING fleet organisation with a pending payout profile and its two AL-49 documents.
    /// </summary>
    public async Task<(Guid OrgId, Guid OwnerId, Guid ProfileId, Guid ProofUploadId)> FleetOrgAwaitingKycAsync()
    {
        var ownerId = Guid.CreateVersion7();
        var orgId = Guid.CreateVersion7();
        var profileId = Guid.CreateVersion7();
        var proofUploadId = Guid.CreateVersion7();
        var vehicleId = Guid.CreateVersion7();

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, email, role, first_name)
            VALUES (@OwnerId, @Email, 'fleet_owner', 'Ranjith');
            INSERT INTO iam.user_roles (user_id, role) VALUES (@OwnerId, 'fleet_owner') ON CONFLICT DO NOTHING;

            INSERT INTO registry.fleets (id, owner_id, name, business_reg, status, contact_phone, address)
            VALUES (@OrgId, @OwnerId, 'Ruhunu Transport (Pvt) Ltd', @BusinessReg, 'PENDING',
                    '+94112345678', '12 Galle Road, Colombo 03');

            INSERT INTO docs.uploads (id, owner_id, storage_url, kind)
            VALUES (@ProofUploadId, @OwnerId, @StorageUrl, 'bank_statement');

            INSERT INTO registry.fleet_payout_profiles
                (id, fleet_id, bank, branch, account_no, account_holder_name, proof_upload_id, status)
            VALUES (@ProfileId, @OrgId, 'Bank of Ceylon', 'Kollupitiya', '0071234567',
                    'Ruhunu Transport (Pvt) Ltd', @ProofUploadId, 'pending_verification');

            -- One vehicle on the roster, so OrgQueueRow.vehicleCount is a real count.
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            VALUES (@VehicleId, @OwnerId, @RegNo, 'bus', 'A', 'PENDING', 'Ruhunu Transport');

            INSERT INTO registry.fleet_vehicles (fleet_id, vehicle_id, mode)
            VALUES (@OrgId, @VehicleId, 'A');
            """,
            new
            {
                OwnerId = ownerId,
                OrgId = orgId,
                ProfileId = profileId,
                ProofUploadId = proofUploadId,
                VehicleId = vehicleId,
                Email = $"{ownerId:N}@fleet.test",
                BusinessReg = $"PV{orgId:N}"[..12],
                StorageUrl = $"s3://mageride-docs/payout/{proofUploadId:N}.pdf",
                RegNo = $"F{vehicleId:N}",
            });

        return (orgId, ownerId, profileId, proofUploadId);
    }

    /// <summary>
    /// What registry-svc writes when the driver re-uploads after a rejection: a fresh document with
    /// a fresh doubtful field, and the step it belongs to back at <c>pending_review</c>.
    /// </summary>
    public async Task<Guid> ReuploadInsuranceAsync(Guid vehicleId)
    {
        var docId = Guid.CreateVersion7();

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.documents (id, driver_id, vehicle_id, kind, file_url, expires_at)
            SELECT @DocId, v.owner_id, v.id, 'insurance', @StorageUrl, now() + interval '360 days'
              FROM registry.vehicles v WHERE v.id = @VehicleId;

            INSERT INTO registry.document_fields
                (document_id, field_key, field_value, confidence, source, verify_status)
            VALUES (@DocId, 'insurance_expiry', '2027-07-01', 0.520, 'ai', 'pending');

            UPDATE registry.onboarding_steps
               SET status = 'pending_review', saved_at = now()
             WHERE vehicle_id = @VehicleId AND step = 'insurance';
            """,
            new { DocId = docId, VehicleId = vehicleId, StorageUrl = $"s3://mageride-docs/insurance/{docId:N}.jpg" });

        return docId;
    }

    public async Task<string> RegistrationNumberAsync(Guid vehicleId)
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.QuerySingleAsync<string>(
            "SELECT registration_number FROM registry.vehicles WHERE id = @Id;", new { Id = vehicleId });
    }

    /// <summary>One <c>registry.document_fields</c> row, read back after a decision.</summary>
    public async Task<DocumentFieldSnapshot?> FieldAsync(Guid documentId, string fieldKey)
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<DocumentFieldSnapshot>(
            """
            SELECT field_value AS FieldValue, source AS Source, confidence AS Confidence,
                   verify_status AS VerifyStatus, confirmed_by AS ConfirmedBy, confirmed_at AS ConfirmedAt
              FROM registry.document_fields
             WHERE document_id = @DocumentId AND field_key = @FieldKey;
            """,
            new { DocumentId = documentId, FieldKey = fieldKey });
    }

    public async Task<(string Status, string? RejectionReason)> VehicleVerdictAsync(Guid vehicleId)
    {
        await using var connection = await postgres.OpenAsync();

        var row = await connection.QuerySingleAsync<(string Status, string? RejectionReason)>(
            "SELECT status, rejection_reason FROM registry.vehicles WHERE id = @Id;", new { Id = vehicleId });

        return row;
    }

    public async Task<(DateTimeOffset? VerifiedAt, string? RejectionReason)> DriverVerdictAsync(Guid driverId)
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.QuerySingleAsync<(DateTimeOffset? VerifiedAt, string? RejectionReason)>(
            "SELECT verified_at, rejection_reason FROM registry.driver_profiles WHERE driver_id = @Id;",
            new { Id = driverId });
    }

    /// <summary>Exactly what subscription-svc's pay sheet reads: the org's <c>verified</c> rows.</summary>
    public async Task<IReadOnlyList<string>> PayoutProfileStatusesAsync(Guid fleetId)
    {
        await using var connection = await postgres.OpenAsync();

        var rows = await connection.QueryAsync<string>(
            "SELECT status FROM registry.fleet_payout_profiles WHERE fleet_id = @Id ORDER BY created_at;",
            new { Id = fleetId });

        return [.. rows];
    }

    /// <summary>Every audit row written for one entity, newest first.</summary>
    public async Task<IReadOnlyList<AuditRowSnapshot>> AuditRowsAsync(Guid entityId)
    {
        await using var connection = await postgres.OpenAsync();

        var rows = await connection.QueryAsync<AuditRowSnapshot>(
            """
            SELECT event_id    AS EventId,
                   actor_id    AS ActorId,
                   actor_role  AS ActorRole,
                   action      AS Action,
                   entity_type AS EntityType,
                   entity_id   AS EntityId,
                   before::text AS Before,
                   after::text  AS After,
                   ip          AS Ip,
                   detail::text AS Detail,
                   ts          AS Ts
              FROM audit.events
             WHERE entity_id = @EntityId
             ORDER BY id DESC;
            """,
            new { EntityId = entityId });

        return [.. rows];
    }

    /// <summary>Audit rows carrying a given action, whatever they are about.</summary>
    public async Task<IReadOnlyList<AuditRowSnapshot>> AuditRowsByActionAsync(string action)
    {
        await using var connection = await postgres.OpenAsync();

        var rows = await connection.QueryAsync<AuditRowSnapshot>(
            """
            SELECT event_id    AS EventId,
                   actor_id    AS ActorId,
                   actor_role  AS ActorRole,
                   action      AS Action,
                   entity_type AS EntityType,
                   entity_id   AS EntityId,
                   before::text AS Before,
                   after::text  AS After,
                   ip          AS Ip,
                   detail::text AS Detail,
                   ts          AS Ts
              FROM audit.events
             WHERE action = @Action
             ORDER BY id DESC;
            """,
            new { Action = action });

        return [.. rows];
    }

    public async Task<string> VehicleDispatchStateAsync(Guid vehicleId)
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.QuerySingleAsync<string>(
            "SELECT dispatch_state FROM registry.vehicles WHERE id = @Id;", new { Id = vehicleId });
    }

    public async Task<bool> DriverIsBlockedAsync(Guid driverId)
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.QuerySingleAsync<bool>(
            "SELECT is_blocked FROM iam.users WHERE id = @Id;", new { Id = driverId });
    }

    public async Task<string?> SessionStateAsync(Guid sessionId)
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT state FROM trips.sessions WHERE id = @Id;", new { Id = sessionId });
    }

    public async Task<string?> PresenceStateAsync(Guid driverId)
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT state FROM dispatch.driver_presence WHERE driver_id = @Id;", new { Id = driverId });
    }
}

/// <summary>One <c>registry.document_fields</c> row as a test reads it back.</summary>
internal sealed record DocumentFieldSnapshot(
    string? FieldValue,
    string Source,
    decimal? Confidence,
    string VerifyStatus,
    Guid? ConfirmedBy,
    DateTimeOffset? ConfirmedAt);

/// <summary>One <c>audit.events</c> row as a test reads it back.</summary>
internal sealed record AuditRowSnapshot(
    Guid EventId,
    Guid? ActorId,
    string? ActorRole,
    string Action,
    string? EntityType,
    Guid? EntityId,
    string? Before,
    string? After,
    string? Ip,
    string? Detail,
    DateTimeOffset Ts);
