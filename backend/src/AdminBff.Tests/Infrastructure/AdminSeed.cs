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
    /// A driver whose bank &amp; payout profile is waiting on an officer (AL-58, AL-59).
    /// </summary>
    /// <param name="alreadyVerified">
    /// When true the driver already has a <c>verified</c> profile and the pending row is an edit —
    /// which is the case BR-31.1 exists for, and the one the supersede ordering is about.
    /// </param>
    public async Task<(Guid DriverId, Guid ProfileId, Guid ProofUploadId, Guid QrUploadId)>
        DriverAwaitingPayoutAsync(bool alreadyVerified = false)
    {
        var driverId = Guid.CreateVersion7();
        var profileId = Guid.CreateVersion7();
        var proofUploadId = Guid.CreateVersion7();
        var qrUploadId = Guid.CreateVersion7();

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, email, role, first_name)
            VALUES (@DriverId, @Email, 'driver', 'Nimal');
            INSERT INTO iam.user_roles (user_id, role) VALUES (@DriverId, 'driver') ON CONFLICT DO NOTHING;

            INSERT INTO registry.driver_profiles (driver_id, display_name)
            VALUES (@DriverId, 'Nimal Perera')
            ON CONFLICT (driver_id) DO NOTHING;

            INSERT INTO docs.uploads (id, owner_id, storage_url, kind)
            VALUES (@ProofUploadId, @DriverId, @ProofUrl, 'bank_statement'),
                   (@QrUploadId,    @DriverId, @QrUrl,    'lankaqr_code');

            -- Explicitly older than the pending row. Both INSERTs share one transaction, so a
            -- bare now() would give them the SAME created_at and "which version is current" would
            -- be a coin toss. It is also what actually happened: the incumbent was approved before
            -- the driver edited it.
            INSERT INTO registry.driver_payout_profiles
                (driver_id, bank, branch, account_no, account_holder_name, status,
                 verified_at, created_at)
            SELECT @DriverId, 'Bank of Ceylon', 'Kollupitiya', '0070000001', 'Nimal Perera',
                   'verified', now() - interval '30 days', now() - interval '30 days'
             WHERE @AlreadyVerified;

            INSERT INTO registry.driver_payout_profiles
                (id, driver_id, bank, branch, account_no, account_holder_name,
                 proof_upload_id, lankaqr_upload_id, status)
            VALUES (@ProfileId, @DriverId, 'Sampath Bank', 'Nugegoda', '0079999999', 'Nimal Perera',
                    @ProofUploadId, @QrUploadId, 'pending_verification');
            """,
            new
            {
                DriverId = driverId,
                ProfileId = profileId,
                ProofUploadId = proofUploadId,
                QrUploadId = qrUploadId,
                AlreadyVerified = alreadyVerified,
                Email = $"{driverId:N}@driver.test",
                ProofUrl = $"s3://mageride-docs/payout/{proofUploadId:N}.pdf",
                QrUrl = $"s3://mageride-docs/payout/{qrUploadId:N}.png",
            });

        return (driverId, profileId, proofUploadId, qrUploadId);
    }

    /// <summary>Every version of a driver's payout profile, newest first — the BR-31.1 assertion.</summary>
    public async Task<IReadOnlyList<string>> DriverPayoutStatusesAsync(Guid driverId)
    {
        await using var connection = await postgres.OpenAsync();

        var rows = await connection.QueryAsync<string>(
            """
            SELECT status FROM registry.driver_payout_profiles
             WHERE driver_id = @Id ORDER BY created_at DESC;
            """,
            new { Id = driverId });

        return [.. rows];
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

    // -------------------------------------------------------------------------------------------
    // Directories (C064)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// One of everything the three directories read, wired together the way the platform wires it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One fixture rather than one per assertion</b>, because the claims C064's DoD makes are
    /// about a *joined* view: "every documented criterion, singly and combined" is only meaningful
    /// against a subject that has a name and a mobile and an NIC and a plate and a level at once,
    /// and a Trips tab that renders is one where the ride, the vehicle and the payment all point at
    /// each other.
    /// </para>
    /// <para>
    /// Two vehicles, deliberately: a Mode C three-wheeler the driver owns (which is what the driver
    /// directory's reg-no search, the daily fee and the reports hang off) and a Mode B van on a
    /// fleet's roster (which is what the vehicle directory's <c>fleetOrg</c> search needs).
    /// <c>registry.fleet_vehicles.mode</c> admits only A and B — AL-03 keeps Mode C out of fleets —
    /// so one vehicle could not have played both parts.
    /// </para>
    /// </remarks>
    public async Task<DirectoryFixture> DirectoryFixtureAsync()
    {
        var fixture = new DirectoryFixture(
            PassengerId: Guid.CreateVersion7(),
            PassengerName: $"Ayesha {Guid.NewGuid():N}"[..24],
            PassengerPhone: $"+9470{Random.Shared.Next(1000000, 9999999)}",
            PassengerEmail: $"ayesha{Guid.NewGuid():N}@rider.test",
            SosPhone: $"+9470{Random.Shared.Next(1000000, 9999999)}",
            DriverId: Guid.CreateVersion7(),
            DriverName: $"Nimal {Guid.NewGuid():N}"[..22],
            DriverPhone: $"+9471{Random.Shared.Next(1000000, 9999999)}",
            DriverNic: $"1990{Random.Shared.Next(10000000, 99999999)}",
            CounterpartyDriverId: Guid.CreateVersion7(),
            VehicleId: Guid.CreateVersion7(),
            FleetVehicleId: Guid.CreateVersion7(),
            FleetId: Guid.CreateVersion7(),
            FleetName: $"Ruhunu Transport {Guid.NewGuid():N}"[..28],
            RideId: Guid.CreateVersion7(),
            PackageRideId: Guid.CreateVersion7(),
            TicketId: Guid.CreateVersion7(),
            ReportId: Guid.CreateVersion7(),
            InsuranceDocId: Guid.CreateVersion7(),
            RevenueDocId: Guid.CreateVersion7(),
            Imei: $"3579{Random.Shared.Next(10000000, 99999999)}0",
            RegNo: $"WP-{Guid.NewGuid():N}"[..14].ToUpperInvariant(),
            FleetRegNo: $"NB-{Guid.NewGuid():N}"[..14].ToUpperInvariant());

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            -- The passenger, their emergency contact and the account they pay with.
            INSERT INTO iam.users (id, phone, email, role, first_name, default_payment_method)
            VALUES (@PassengerId, @PassengerPhone, @PassengerEmail, 'passenger', @PassengerName, 'onepay');
            INSERT INTO iam.user_roles (user_id, role)
            VALUES (@PassengerId, 'passenger') ON CONFLICT DO NOTHING;
            INSERT INTO iam.emergency_contacts (user_id, name, phone)
            VALUES (@PassengerId, 'Amma', @SosPhone);

            -- A verified Level-1 driver, which is the directory's default population.
            INSERT INTO iam.users (id, phone, role, first_name)
            VALUES (@DriverId, @DriverPhone, 'driver', @DriverName);
            INSERT INTO iam.user_roles (user_id, role) VALUES (@DriverId, 'driver') ON CONFLICT DO NOTHING;
            INSERT INTO registry.driver_profiles (driver_id, display_name, nic_no, verified_at)
            VALUES (@DriverId, @DriverName, @DriverNic, now() - interval '10 days');
            INSERT INTO dispatch.driver_levels (driver_id, level, rating_points)
            VALUES (@DriverId, 1, 620);

            -- The other side of the credit transfer (US-9.13: the pair is two drivers).
            INSERT INTO iam.users (id, phone, role, first_name)
            VALUES (@CounterpartyDriverId, @CounterpartyPhone, 'driver', 'Kamal');
            INSERT INTO iam.user_roles (user_id, role)
            VALUES (@CounterpartyDriverId, 'driver') ON CONFLICT DO NOTHING;

            -- The driver's own Mode C vehicle.
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name, onboarding_status)
            VALUES (@VehicleId, @DriverId, @RegNo, 'three_wheeler', 'C', 'APPROVED', @DriverName, 'approved');

            -- A fleet and its Mode B van, so `?fleetOrg=` has something to match (AL-03).
            INSERT INTO iam.users (id, email, role, first_name)
            VALUES (@FleetOwnerId, @FleetEmail, 'fleet_owner', 'Ruhunu Owner');
            INSERT INTO registry.fleets (id, owner_id, name, status)
            VALUES (@FleetId, @FleetOwnerId, @FleetName, 'APPROVED');
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            VALUES (@FleetVehicleId, @FleetOwnerId, @FleetRegNo, 'van', 'B', 'APPROVED', 'Ruhunu Owner');
            INSERT INTO registry.fleet_vehicles (fleet_id, vehicle_id, mode)
            VALUES (@FleetId, @FleetVehicleId, 'B');

            -- E-03's two dated documents, each with the upload row AL-43's provenance comes from.
            INSERT INTO docs.uploads (id, owner_id, storage_url, kind, captured_via) VALUES
                (gen_random_uuid(), @DriverId, @InsuranceUrl, 'insurance',       'camera_dragcrop'),
                (gen_random_uuid(), @DriverId, @RevenueUrl,   'revenue_license', 'gallery');
            INSERT INTO registry.documents (id, driver_id, vehicle_id, kind, file_url, expires_at) VALUES
                (@InsuranceDocId, @DriverId, @VehicleId, 'insurance',       @InsuranceUrl,
                 timestamptz '2027-05-01 00:00:00+05:30'),
                (@RevenueDocId,   @DriverId, @VehicleId, 'revenue_license', @RevenueUrl,
                 timestamptz '2026-12-31 00:00:00+05:30');

            -- A live tracker (T-08): ACTIVE and pinging, so `online` is true.
            INSERT INTO prov.tracker_bindings
              (imei, vehicle_id, credential_serial, credential_type, state, rotates_at, last_seen_at)
            VALUES (@Imei, @VehicleId, @Imei || '-cert', 'psk', 'ACTIVE', now() + interval '90 days', now());

            -- One completed, paid Mode C ride and the fare that collected on it (R-05).
            INSERT INTO rides.rides
                (id, passenger_id, booker_id, client_request_id, accepted_driver_id, accepted_vehicle_id,
                 vehicle_type, pickup_geo, dropoff_geo, state, payment_method, created_at, terminal_at)
            VALUES
                (@RideId, @PassengerId, @PassengerId, gen_random_uuid(), @DriverId, @VehicleId,
                 'three_wheeler',
                 ST_SetSRID(ST_MakePoint(79.861, 6.927), 4326)::geography,
                 ST_SetSRID(ST_MakePoint(79.884, 6.901), 4326)::geography,
                 'Paid', 'onepay', now() - interval '2 days', now() - interval '2 days');
            INSERT INTO fares.ride_payments
                (ride_id, attempt_no, method, amount_minor, surcharge_minor, tip_amount_minor,
                 currency, state, created_at)
            VALUES (@RideId, 1::smallint, 'onepay', 45000, 2250, 1000, 'LKR', 'Succeeded',
                    now() - interval '2 days');

            -- One delivery (P-06). The OTP hashes and the recipient number are what
            -- ck_rides_package_complete and ck_rides_package_recipient demand of a kind=2 row.
            INSERT INTO rides.rides
                (id, passenger_id, booker_id, client_request_id, accepted_driver_id, accepted_vehicle_id,
                 vehicle_type, pickup_geo, dropoff_geo, state, kind, package_size, package_description,
                 recipient_name, recipient_phone, pickup_otp_hash, delivery_otp_hash,
                 created_at, terminal_at)
            VALUES
                (@PackageRideId, @PassengerId, @PassengerId, gen_random_uuid(), @DriverId, @VehicleId,
                 'motorbike',
                 ST_SetSRID(ST_MakePoint(79.861, 6.927), 4326)::geography,
                 ST_SetSRID(ST_MakePoint(79.884, 6.901), 4326)::geography,
                 'CashOnDeliveryCollected', 2::smallint, 'S', 'Documents',
                 'Sunil', @RecipientPhone, '\x00'::bytea, '\x01'::bytea,
                 now() - interval '1 day', now() - interval '1 day');

            -- The delivery's own settlement, one Colombo day after the ride's — so the vehicle's
            -- Earnings tab has two buckets and the day boundary is something an assertion can see.
            INSERT INTO fares.ride_payments
                (ride_id, attempt_no, method, amount_minor, currency, state, created_at)
            VALUES (@PackageRideId, 1::smallint, 'cod', 12000, 'LKR', 'CashOnDeliveryCollected',
                    now() - interval '1 day');

            -- A dispute and a report, which are the two tabs fed by other services' tables.
            INSERT INTO support.tickets (id, user_id, category, description, ride_id, status)
            VALUES (@TicketId, @PassengerId, 'fare_dispute', 'Charged twice for one ride', @RideId, 'OPEN');
            INSERT INTO safety.vehicle_reports (id, reporter_id, vehicle_id, ride_id, reason, status)
            VALUES (@ReportId, @PassengerId, @VehicleId, @RideId, 'Reckless driving', 'PENDING');

            -- The driver's wallet: a ledger account, its read-model mirror and one posting's
            -- projection (D-09 §10). The journal entry exists because wallet_transactions.entry_id
            -- references it; no postings, so the balanced-entry trigger has nothing to reject.
            INSERT INTO billing.accounts (id, owner_type, owner_id, currency, balance_minor)
            VALUES (@AccountId, 'driver', @DriverId, 'LKR', 125000);
            INSERT INTO billing.wallets (account_id, balance_minor) VALUES (@AccountId, 125000);
            INSERT INTO billing.journal_entries (id, kind, idempotency_key, description)
            VALUES (@EntryId, 'topup', 'directory-fixture:' || @EntryId::text, 'Wallet top-up');
            INSERT INTO billing.wallet_transactions
                (account_id, entry_id, kind, amount_minor, balance_after_minor, description)
            VALUES (@AccountId, @EntryId, 'topup', 125000, 125000, 'Wallet top-up');

            -- D-13's charge for today's Colombo business date.
            INSERT INTO billing.daily_fee_charges
                (driver_id, vehicle_id, amount_minor, trips_that_day, status)
            VALUES (@DriverId, @VehicleId, 15000, 4, 'PAID');

            -- US-9.13: an approved driver-to-driver transfer, exact value, no commission (AL-01).
            INSERT INTO billing.credit_transfers
                (sender_driver_id, recipient_driver_id, amount_minor, direction, status)
            VALUES (@DriverId, @CounterpartyDriverId, 50000, 'DIRECT', 'APPROVED');
            """,
            new
            {
                fixture.PassengerId,
                fixture.PassengerName,
                fixture.PassengerPhone,
                fixture.PassengerEmail,
                fixture.SosPhone,
                fixture.DriverId,
                fixture.DriverName,
                fixture.DriverPhone,
                fixture.DriverNic,
                fixture.CounterpartyDriverId,
                fixture.VehicleId,
                fixture.FleetVehicleId,
                fixture.FleetId,
                fixture.FleetName,
                fixture.RideId,
                fixture.PackageRideId,
                fixture.TicketId,
                fixture.ReportId,
                fixture.InsuranceDocId,
                fixture.RevenueDocId,
                fixture.Imei,
                fixture.RegNo,
                fixture.FleetRegNo,
                CounterpartyPhone = $"+9472{Random.Shared.Next(1000000, 9999999)}",
                RecipientPhone = $"+9473{Random.Shared.Next(1000000, 9999999)}",
                FleetOwnerId = Guid.CreateVersion7(),
                FleetEmail = $"{Guid.NewGuid():N}@fleet.test",
                AccountId = Guid.CreateVersion7(),
                EntryId = Guid.CreateVersion7(),
                InsuranceUrl = $"s3://mageride-docs/insurance/{fixture.InsuranceDocId:N}.jpg",
                RevenueUrl = $"s3://mageride-docs/revenue/{fixture.RevenueDocId:N}.jpg",
            });

        return fixture;
    }

    /// <summary>
    /// Fills the passenger directory to the size C064's performance claim is made against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One statement rather than ten thousand round trips, and <c>ANALYZE</c> afterwards because a
    /// table the planner still believes is empty is a sequential scan whatever indexes exist — which
    /// would make the measurement a test of the planner's statistics rather than of the query.
    /// </para>
    /// <para>
    /// <b>No <c>iam.user_roles</c> rows.</b> These accounts exist to be paged over; giving them
    /// grant rows would add ten thousand riders to C061's "new riders" rollup and move a number
    /// another suite asserts on.
    /// </para>
    /// </remarks>
    public async Task BulkPassengersAsync(int count)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, email, role, first_name, created_at)
            SELECT gen_random_uuid(),
                   '+9478' || lpad(g::text, 7, '0'),
                   'bulk' || g || '@mageride.test',
                   'passenger',
                   'Bulk Rider ' || g,
                   now() - (g || ' minutes')::interval
              FROM generate_series(1, @Count) AS g
             ON CONFLICT DO NOTHING;
            """,
            new { Count = count },
            commandTimeout: 120);

        await connection.ExecuteAsync("ANALYZE iam.users;", commandTimeout: 120);
    }

    /// <summary>How many accounts the passenger directory can see, for the size assertion.</summary>
    public async Task<int> PassengerCountAsync()
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM iam.users WHERE role = 'passenger';");
    }

    // -------------------------------------------------------------------------------------------
    // Finance (C065)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// One driver's gateway sessions, one per exception class the reconciliation queue derives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mismatch is seeded as the ledger disagreeing with the session, which is the only
    /// mismatch the schema can hold.</b> wallet-svc refuses a callback whose amount disagrees and
    /// leaves the session <c>Pending</c> — there is no exception column — so a session that was
    /// settled and posted for a <em>different</em> figure is what "the gateway and the ledger do not
    /// agree" looks like in the database, and it is what the DoD's fourth item is about.
    /// </para>
    /// <para>
    /// The postings are written as a balanced pair against the platform account, because
    /// <c>trg_balanced</c> is a constraint trigger and a lone leg would be refused at COMMIT.
    /// </para>
    /// </remarks>
    public async Task<SettlementFixture> SettlementFixtureAsync()
    {
        var fixture = new SettlementFixture(
            DriverId: Guid.CreateVersion7(),
            AccountId: Guid.CreateVersion7(),
            MatchedTopupId: Guid.CreateVersion7(),
            MismatchedTopupId: Guid.CreateVersion7(),
            UnpostedTopupId: Guid.CreateVersion7(),
            StaleTopupId: Guid.CreateVersion7(),
            FailedTopupId: Guid.CreateVersion7(),
            MatchedMinor: 500_00,
            MismatchedMinor: 300_00,
            PostedForMismatchMinor: 250_00);

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name)
            VALUES (@DriverId, @Phone, 'driver', 'Settlement Driver');
            INSERT INTO iam.user_roles (user_id, role) VALUES (@DriverId, 'driver') ON CONFLICT DO NOTHING;

            INSERT INTO billing.accounts (id, owner_type, owner_id, currency, balance_minor)
            VALUES (@AccountId, 'driver', @DriverId, 'LKR', 0);
            INSERT INTO billing.wallets (account_id, balance_minor) VALUES (@AccountId, 0);

            -- (1) settled and posted for exactly what the gateway confirmed: NOT an exception.
            INSERT INTO billing.journal_entries (id, kind, idempotency_key, description)
            VALUES (@MatchedEntryId, 'topup', 'topup:' || @MatchedTopupId::text, 'Top-up');
            INSERT INTO billing.journal_postings (entry_id, account_id, amount_minor) VALUES
                (@MatchedEntryId, @AccountId, @MatchedMinor),
                (@MatchedEntryId, (SELECT id FROM billing.accounts
                                    WHERE owner_type = 'platform' AND owner_id IS NULL AND currency = 'LKR'),
                 -@MatchedMinor);
            INSERT INTO billing.topups
                (id, driver_id, account_id, method, amount_minor, state,
                 provider_order_id, provider_transaction_id, journal_entry_id, created_at, settled_at)
            VALUES (@MatchedTopupId, @DriverId, @AccountId, 'onepay', @MatchedMinor, 'Succeeded',
                    'ord-' || @MatchedTopupId::text, 'txn-' || @MatchedTopupId::text, @MatchedEntryId,
                    now() - interval '3 hours', now() - interval '3 hours');

            -- (2) settled and posted for LESS than the gateway confirmed: amount-mismatch.
            INSERT INTO billing.journal_entries (id, kind, idempotency_key, description)
            VALUES (@MismatchEntryId, 'topup', 'topup:' || @MismatchedTopupId::text, 'Top-up');
            INSERT INTO billing.journal_postings (entry_id, account_id, amount_minor) VALUES
                (@MismatchEntryId, @AccountId, @PostedForMismatchMinor),
                (@MismatchEntryId, (SELECT id FROM billing.accounts
                                     WHERE owner_type = 'platform' AND owner_id IS NULL AND currency = 'LKR'),
                 -@PostedForMismatchMinor);
            INSERT INTO billing.topups
                (id, driver_id, account_id, method, amount_minor, state,
                 provider_order_id, provider_transaction_id, journal_entry_id, created_at, settled_at)
            VALUES (@MismatchedTopupId, @DriverId, @AccountId, 'onepay', @MismatchedMinor, 'Succeeded',
                    'ord-' || @MismatchedTopupId::text, 'txn-' || @MismatchedTopupId::text, @MismatchEntryId,
                    now() - interval '2 hours', now() - interval '2 hours');

            -- (3) settled with no ledger entry at all: settled-not-posted. ck_topups_posting admits
            --     it — only a Pending session is forbidden a journal entry, not the reverse.
            INSERT INTO billing.topups
                (id, driver_id, account_id, method, amount_minor, state,
                 provider_order_id, provider_transaction_id, created_at, settled_at)
            VALUES (@UnpostedTopupId, @DriverId, @AccountId, 'lankaqr', 100000, 'Succeeded',
                    'ord-' || @UnpostedTopupId::text, 'txn-' || @UnpostedTopupId::text,
                    now() - interval '90 minutes', now() - interval '90 minutes');

            -- (4) open well past the grace period: unsettled. This is also where a refused amount
            --     mismatch lands, which is why the queue names the class rather than guessing.
            INSERT INTO billing.topups
                (id, driver_id, account_id, method, amount_minor, state, provider_order_id, created_at)
            VALUES (@StaleTopupId, @DriverId, @AccountId, 'onepay', 75000, 'Pending',
                    'ord-' || @StaleTopupId::text, now() - interval '2 days');

            -- (5) the gateway reported FAILED after issuing its own reference: gateway-failed.
            INSERT INTO billing.topups
                (id, driver_id, account_id, method, amount_minor, state,
                 provider_order_id, provider_transaction_id, failure_reason, created_at, settled_at)
            VALUES (@FailedTopupId, @DriverId, @AccountId, 'onepay', 50000, 'Failed',
                    'ord-' || @FailedTopupId::text, 'txn-' || @FailedTopupId::text,
                    'gateway reported FAILED', now() - interval '4 hours', now() - interval '4 hours');
            """,
            new
            {
                fixture.DriverId,
                fixture.AccountId,
                fixture.MatchedTopupId,
                fixture.MismatchedTopupId,
                fixture.UnpostedTopupId,
                fixture.StaleTopupId,
                fixture.FailedTopupId,
                fixture.MatchedMinor,
                fixture.MismatchedMinor,
                fixture.PostedForMismatchMinor,
                MatchedEntryId = Guid.CreateVersion7(),
                MismatchEntryId = Guid.CreateVersion7(),
                Phone = $"+9475{Random.Shared.Next(1000000, 9999999)}",
            });

        return fixture;
    }

    /// <summary>D-13's charge for one Colombo business day — what a reversal compensates.</summary>
    public async Task<DateOnly> DailyFeeChargeAsync(
        Guid driverId, Guid vehicleId, long amountMinor, string status = "PAID")
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<DateOnly>(
            """
            INSERT INTO billing.daily_fee_charges
                (driver_id, vehicle_id, amount_minor, trips_that_day, status)
            VALUES (@DriverId, @VehicleId, @AmountMinor, 3, @Status)
            ON CONFLICT (driver_id, vehicle_id, fee_date)
            DO UPDATE SET amount_minor = EXCLUDED.amount_minor, status = EXCLUDED.status
            RETURNING fee_date;
            """,
            new { DriverId = driverId, VehicleId = vehicleId, AmountMinor = amountMinor, Status = status });
    }

    /// <summary>The driver's wallet ledger, newest first — the DoD's "appears in the driver's ledger".</summary>
    public async Task<IReadOnlyList<(string Kind, long AmountMinor, long BalanceAfterMinor)>>
        WalletLedgerAsync(Guid driverId)
    {
        await using var connection = await postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string Kind, long AmountMinor, long BalanceAfterMinor)>(
            """
            SELECT wt.kind, wt.amount_minor, wt.balance_after_minor
              FROM billing.wallet_transactions wt
              JOIN billing.accounts a ON a.id = wt.account_id
             WHERE a.owner_type = 'driver' AND a.owner_id = @DriverId
             ORDER BY wt.ts DESC, wt.id DESC;
            """,
            new { DriverId = driverId });

        return [.. rows];
    }

    /// <summary>Σ of a journal entry's legs. Zero, or the ledger is not a ledger (D-09).</summary>
    public async Task<long> EntryBalanceAsync(Guid entryId)
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<long>(
            "SELECT COALESCE(sum(amount_minor), 0)::bigint FROM billing.journal_postings WHERE entry_id = @Id;",
            new { Id = entryId });
    }

    /// <summary>
    /// A ride whose payment reached R-19's <c>Overpaid</c> and for which no refund has been raised.
    /// </summary>
    /// <remarks>
    /// ADD §11.14's shape exactly: the rider paid the driver in cash, the ride closed, and a late
    /// gateway callback then arrived. The absence of the <c>fares.refunds</c> row is the point —
    /// that is the half of the queue a list of raised refunds cannot show.
    /// </remarks>
    public async Task<(Guid PassengerId, Guid PaymentId, Guid RideId, long AmountMinor)> OverpaidPaymentAsync()
    {
        var passengerId = Guid.CreateVersion7();
        var rideId = Guid.CreateVersion7();
        var paymentId = Guid.CreateVersion7();
        const long amountMinor = 82_50;

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name)
            VALUES (@PassengerId, @Phone, 'passenger', 'Overpaid Rider');

            INSERT INTO rides.rides
                (id, passenger_id, booker_id, client_request_id, vehicle_type,
                 pickup_geo, dropoff_geo, state, created_at, terminal_at)
            VALUES (@RideId, @PassengerId, @PassengerId, gen_random_uuid(), 'three_wheeler',
                    ST_SetSRID(ST_MakePoint(79.861, 6.927), 4326)::geography,
                    ST_SetSRID(ST_MakePoint(79.884, 6.901), 4326)::geography,
                    'CashSettled', now() - interval '1 day', now() - interval '1 day');

            INSERT INTO fares.ride_payments
                (id, ride_id, attempt_no, method, amount_minor, currency, state, provider_transaction_id,
                 created_at)
            VALUES (@PaymentId, @RideId, 1::smallint, 'cash', @AmountMinor, 'LKR', 'Overpaid',
                    'late-' || @PaymentId::text, now() - interval '1 day');
            """,
            new
            {
                PassengerId = passengerId,
                RideId = rideId,
                PaymentId = paymentId,
                AmountMinor = amountMinor,
                Phone = $"+9479{Random.Shared.Next(1000000, 9999999)}",
            });

        return (passengerId, paymentId, rideId, amountMinor);
    }

    /// <summary>One journal entry of each of the four kinds the transactions report covers.</summary>
    public async Task<TransactionsFixture> TransactionsFixtureAsync()
    {
        var fixture = new TransactionsFixture(
            DriverId: Guid.CreateVersion7(),
            RecipientId: Guid.CreateVersion7(),
            TopupMinor: 200_00,
            DailyFeeMinor: 150_00,
            VoucherMinor: 1000_00,
            TransferMinor: 500_00);

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name) VALUES
                (@DriverId,    @DriverPhone,    'driver', 'Report Driver'),
                (@RecipientId, @RecipientPhone, 'driver', 'Report Recipient');

            INSERT INTO billing.accounts (id, owner_type, owner_id, currency, balance_minor) VALUES
                (@DriverAccountId,    'driver', @DriverId,    'LKR', 0),
                (@RecipientAccountId, 'driver', @RecipientId, 'LKR', 0);

            INSERT INTO billing.journal_entries (id, kind, idempotency_key, description) VALUES
                (@TopupEntryId,    'topup',            'c065-topup:'    || @TopupEntryId::text,    'Top-up'),
                (@FeeEntryId,      'daily_fee',        'c065-fee:'      || @FeeEntryId::text,      'Daily fee'),
                (@VoucherEntryId,  'voucher_purchase', 'c065-voucher:'  || @VoucherEntryId::text,  'Bulk voucher'),
                (@TransferEntryId, 'driver_transfer',  'c065-transfer:' || @TransferEntryId::text, 'Credit transfer');

            -- Every entry balanced against the platform account, except the transfer, whose two legs
            -- are two drivers' wallets (AL-01: exact value, no commission leg).
            INSERT INTO billing.journal_postings (entry_id, account_id, amount_minor)
            SELECT @TopupEntryId, @DriverAccountId, @TopupMinor
            UNION ALL SELECT @TopupEntryId, p.id, -@TopupMinor FROM billing.accounts p
                       WHERE p.owner_type = 'platform' AND p.owner_id IS NULL AND p.currency = 'LKR'
            UNION ALL SELECT @FeeEntryId, @DriverAccountId, -@DailyFeeMinor
            UNION ALL SELECT @FeeEntryId, p.id, @DailyFeeMinor FROM billing.accounts p
                       WHERE p.owner_type = 'platform' AND p.owner_id IS NULL AND p.currency = 'LKR'
            UNION ALL SELECT @VoucherEntryId, @DriverAccountId, @VoucherMinor
            UNION ALL SELECT @VoucherEntryId, p.id, -@VoucherMinor FROM billing.accounts p
                       WHERE p.owner_type = 'platform' AND p.owner_id IS NULL AND p.currency = 'LKR'
            UNION ALL SELECT @TransferEntryId, @DriverAccountId, -@TransferMinor
            UNION ALL SELECT @TransferEntryId, @RecipientAccountId, @TransferMinor;
            """,
            new
            {
                fixture.DriverId,
                fixture.RecipientId,
                fixture.TopupMinor,
                fixture.DailyFeeMinor,
                fixture.VoucherMinor,
                fixture.TransferMinor,
                DriverAccountId = Guid.CreateVersion7(),
                RecipientAccountId = Guid.CreateVersion7(),
                TopupEntryId = Guid.CreateVersion7(),
                FeeEntryId = Guid.CreateVersion7(),
                VoucherEntryId = Guid.CreateVersion7(),
                TransferEntryId = Guid.CreateVersion7(),
                DriverPhone = $"+9474{Random.Shared.Next(1000000, 9999999)}",
                RecipientPhone = $"+9474{Random.Shared.Next(1000000, 9999999)}",
            });

        return fixture;
    }

    /// <summary>
    /// A vehicle whose insurance expires in <paramref name="inDays"/>, with the E-03 notice already
    /// sent — plus a superseded older copy the queue must <em>not</em> show.
    /// </summary>
    public async Task<(Guid DriverId, Guid VehicleId, Guid CurrentDocId, Guid SupersededDocId)>
        ExpiringDocumentAsync(int inDays, short noticeDays = 30)
    {
        var (driverId, vehicleId) = await DriverWithVehicleAsync();
        var currentDocId = Guid.CreateVersion7();
        var supersededDocId = Guid.CreateVersion7();

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            -- The superseded copy is written first and with an older created_at, so "newest per
            -- (vehicle, kind)" has something to actually choose between.
            INSERT INTO registry.documents (id, driver_id, vehicle_id, kind, file_url, expires_at, status, created_at)
            VALUES (@SupersededDocId, @DriverId, @VehicleId, 'insurance', @SupersededUrl,
                    now() - interval '40 days', 'EXPIRED', now() - interval '400 days');

            INSERT INTO registry.documents (id, driver_id, vehicle_id, kind, file_url, expires_at, status, created_at)
            VALUES (@CurrentDocId, @DriverId, @VehicleId, 'insurance', @CurrentUrl,
                    now() + make_interval(days => @InDays), 'EXPIRING', now() - interval '30 days');

            INSERT INTO registry.document_notices (document_id, threshold_days)
            VALUES (@CurrentDocId, @NoticeDays) ON CONFLICT DO NOTHING;
            """,
            new
            {
                DriverId = driverId,
                VehicleId = vehicleId,
                CurrentDocId = currentDocId,
                SupersededDocId = supersededDocId,
                InDays = inDays,
                NoticeDays = noticeDays,
                CurrentUrl = $"s3://mageride-docs/insurance/{currentDocId:N}.jpg",
                SupersededUrl = $"s3://mageride-docs/insurance/{supersededDocId:N}.jpg",
            });

        return (driverId, vehicleId, currentDocId, supersededDocId);
    }

    /// <summary>One E-07 flag awaiting review, with two named subjects for the queue to join.</summary>
    public async Task<(Guid FlagId, Guid SubjectId, Guid RelatedId, string SubjectName)> FraudFlagAsync(
        string status = "open")
    {
        var flagId = Guid.CreateVersion7();
        var subjectId = Guid.CreateVersion7();
        var relatedId = Guid.CreateVersion7();
        var subjectName = $"Flagged {Guid.NewGuid():N}"[..20];

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name) VALUES
                (@SubjectId, @SubjectPhone, 'driver',    @SubjectName),
                (@RelatedId, @RelatedPhone, 'passenger', 'Repeat Rider');

            INSERT INTO reputation.fraud_flags
                (id, kind, subject_id, subject_type, related_id, window_key, status, detail, ts)
            VALUES (@FlagId, 'repeat_pair', @SubjectId, 'driver', @RelatedId, '2026-W31', @Status,
                    '{"rides": 11, "threshold": 8}'::jsonb, now() - interval '2 hours');
            """,
            new
            {
                FlagId = flagId,
                SubjectId = subjectId,
                RelatedId = relatedId,
                SubjectName = subjectName,
                Status = status,
                SubjectPhone = $"+9478{Random.Shared.Next(1000000, 9999999)}",
                RelatedPhone = $"+9478{Random.Shared.Next(1000000, 9999999)}",
            });

        return (flagId, subjectId, relatedId, subjectName);
    }

    // -------------------------------------------------------------------------------------------
    // PDPA (C065)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A data subject with one of everything an export gathers and an erasure removes.
    /// </summary>
    /// <param name="withBlockingHold">
    /// Adds a non-terminal ride, so the erasure hits <see cref="StatutoryHolds.ActiveRide"/> — the
    /// case the fulfilment must refuse rather than fulfil.
    /// </param>
    public async Task<PdpaSubjectFixture> PdpaSubjectAsync(bool withBlockingHold = false)
    {
        var fixture = new PdpaSubjectFixture(
            UserId: Guid.CreateVersion7(),
            Name: $"Erasable {Guid.NewGuid():N}"[..22],
            Phone: $"+9477{Random.Shared.Next(1000000, 9999999)}",
            Email: $"erasable{Guid.NewGuid():N}@rider.test",
            SosPhone: $"+9477{Random.Shared.Next(1000000, 9999999)}",
            AccountId: Guid.CreateVersion7(),
            RideId: Guid.CreateVersion7());

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, email, role, first_name, photo_url,
                                   emergency_contact_name, emergency_contact_phone)
            VALUES (@UserId, @Phone, @Email, 'passenger', @Name, 'https://cdn.test/me.jpg',
                    'Amma', @SosPhone);
            INSERT INTO iam.user_roles (user_id, role) VALUES (@UserId, 'passenger') ON CONFLICT DO NOTHING;

            INSERT INTO iam.emergency_contacts (user_id, name, phone) VALUES (@UserId, 'Amma', @SosPhone);

            INSERT INTO iam.saved_addresses (user_id, label, line1, geo, is_home)
            VALUES (@UserId, 'home', '12 Galle Road', ST_SetSRID(ST_MakePoint(79.86, 6.92), 4326)::geography, true);

            INSERT INTO iam.phone_lookups (phone_hash, registered, user_id, caller)
            VALUES (sha256(convert_to(@Phone, 'UTF8')), true, @UserId, 'test');

            INSERT INTO iam.devices (id, user_id, platform, device_key)
            VALUES (@DeviceId, @UserId, 'android', @DeviceKey);
            INSERT INTO iam.sessions (jti, user_id, device_id, app)
            VALUES (gen_random_uuid(), @UserId, @DeviceId, 'passenger');

            -- A closed ride and a settled payment: financial history, which a statute retains.
            INSERT INTO rides.rides
                (id, passenger_id, booker_id, client_request_id, vehicle_type,
                 pickup_geo, dropoff_geo, state, created_at, terminal_at)
            VALUES (@RideId, @UserId, @UserId, gen_random_uuid(), 'sedan',
                    ST_SetSRID(ST_MakePoint(79.861, 6.927), 4326)::geography,
                    ST_SetSRID(ST_MakePoint(79.884, 6.901), 4326)::geography,
                    'Paid', now() - interval '9 days', now() - interval '9 days');
            INSERT INTO fares.ride_payments
                (ride_id, attempt_no, method, amount_minor, currency, state, created_at)
            VALUES (@RideId, 1::smallint, 'cash', 45000, 'LKR', 'FellBackToCash', now() - interval '9 days');

            -- A ledger account with postings and a zero balance: a retention hold, never a blocking
            -- one. A non-zero balance would be `wallet-balance`, which IS blocking.
            INSERT INTO billing.accounts (id, owner_type, owner_id, currency, balance_minor)
            VALUES (@AccountId, 'passenger', @UserId, 'LKR', 0);
            INSERT INTO billing.journal_entries (id, kind, idempotency_key, description)
            VALUES (@EntryId, 'topup', 'c065-pdpa:' || @EntryId::text, 'Top-up');
            INSERT INTO billing.journal_postings (entry_id, account_id, amount_minor) VALUES
                (@EntryId, @AccountId, 0),
                (@EntryId, (SELECT id FROM billing.accounts
                             WHERE owner_type = 'platform' AND owner_id IS NULL AND currency = 'LKR'), 0);

            -- A resolved ticket, so `open-dispute` does NOT fire: the hold list has to distinguish
            -- an open dispute from one that was answered.
            INSERT INTO support.tickets (user_id, category, description, status, resolved_at)
            VALUES (@UserId, 'fare_dispute', 'Resolved long ago', 'RESOLVED', now() - interval '5 days');

            -- Optional: the in-flight ride that makes the erasure refusable.
            INSERT INTO rides.rides
                (id, passenger_id, booker_id, client_request_id, vehicle_type,
                 pickup_geo, dropoff_geo, state, created_at)
            SELECT gen_random_uuid(), @UserId, @UserId, gen_random_uuid(), 'sedan',
                   ST_SetSRID(ST_MakePoint(79.861, 6.927), 4326)::geography,
                   ST_SetSRID(ST_MakePoint(79.884, 6.901), 4326)::geography,
                   'InProgress', now()
             WHERE @Blocking;
            """,
            new
            {
                fixture.UserId,
                fixture.Name,
                fixture.Phone,
                fixture.Email,
                fixture.SosPhone,
                fixture.AccountId,
                fixture.RideId,
                DeviceId = Guid.CreateVersion7(),
                DeviceKey = $"dev-{Guid.NewGuid():N}",
                EntryId = Guid.CreateVersion7(),
                Blocking = withBlockingHold,
            });

        return fixture;
    }

    /// <summary>The identifying columns after an erasure — the DoD's "soft anonymisation".</summary>
    public async Task<AnonymisedUser> UserAfterErasureAsync(Guid userId)
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.QuerySingleAsync<AnonymisedUser>(
            """
            SELECT u.phone AS Phone, u.email AS Email, u.first_name AS FirstName, u.photo_url AS PhotoUrl,
                   u.emergency_contact_phone AS EmergencyContactPhone, u.anonymised_at AS AnonymisedAt,
                   (SELECT count(*)::int FROM iam.emergency_contacts c WHERE c.user_id = u.id) AS EmergencyContacts,
                   (SELECT count(*)::int FROM iam.saved_addresses a  WHERE a.user_id = u.id) AS SavedAddresses,
                   (SELECT count(*)::int FROM iam.phone_lookups p    WHERE p.user_id = u.id) AS PhoneLookups,
                   (SELECT count(*)::int FROM iam.sessions s
                     WHERE s.user_id = u.id AND s.revoked_at IS NULL) AS LiveSessions,
                   (SELECT count(*)::int FROM rides.rides r          WHERE r.passenger_id = u.id) AS Rides,
                   (SELECT count(*)::int FROM billing.journal_postings jp
                      JOIN billing.accounts a2 ON a2.id = jp.account_id
                     WHERE a2.owner_id = u.id) AS LedgerPostings,
                   (SELECT count(*)::int FROM audit.events e
                     WHERE e.actor_id = u.id OR e.entity_id = u.id) AS AuditEvents
              FROM iam.users u WHERE u.id = @Id;
            """,
            new { Id = userId });
    }

    /// <summary>The <c>pdpa.fulfillment_artifacts</c> row a fulfilment left behind.</summary>
    public async Task<(string Kind, string StorageUrl, byte[]? Sha256)?> PdpaArtifactAsync(Guid requestId)
    {
        await using var connection = await postgres.OpenAsync();

        var row = await connection.QuerySingleOrDefaultAsync<(string Kind, string StorageUrl, byte[]? Sha256)>(
            """
            SELECT kind, storage_url, sha256 FROM pdpa.fulfillment_artifacts
             WHERE request_id = @Id ORDER BY signed_at DESC NULLS LAST LIMIT 1;
            """,
            new { Id = requestId });

        return row.Kind is null ? null : row;
    }
}

/// <summary>Everything <see cref="AdminSeed.SettlementFixtureAsync"/> minted.</summary>
internal sealed record SettlementFixture(
    Guid DriverId,
    Guid AccountId,
    Guid MatchedTopupId,
    Guid MismatchedTopupId,
    Guid UnpostedTopupId,
    Guid StaleTopupId,
    Guid FailedTopupId,
    long MatchedMinor,
    long MismatchedMinor,
    long PostedForMismatchMinor);

/// <summary>Everything <see cref="AdminSeed.TransactionsFixtureAsync"/> minted.</summary>
internal sealed record TransactionsFixture(
    Guid DriverId, Guid RecipientId, long TopupMinor, long DailyFeeMinor, long VoucherMinor, long TransferMinor);

/// <summary>Everything <see cref="AdminSeed.PdpaSubjectAsync"/> minted.</summary>
internal sealed record PdpaSubjectFixture(
    Guid UserId, string Name, string Phone, string Email, string SosPhone, Guid AccountId, Guid RideId);

/// <summary>What survived an erasure and what did not.</summary>
internal sealed record AnonymisedUser(
    string? Phone,
    string? Email,
    string? FirstName,
    string? PhotoUrl,
    string? EmergencyContactPhone,
    DateTimeOffset? AnonymisedAt,
    int EmergencyContacts,
    int SavedAddresses,
    int PhoneLookups,
    int LiveSessions,
    int Rides,
    int LedgerPostings,
    int AuditEvents);

/// <summary>Every id the directory fixture minted, so an assertion can name what it is about.</summary>
internal sealed record DirectoryFixture(
    Guid PassengerId,
    string PassengerName,
    string PassengerPhone,
    string PassengerEmail,
    string SosPhone,
    Guid DriverId,
    string DriverName,
    string DriverPhone,
    string DriverNic,
    Guid CounterpartyDriverId,
    Guid VehicleId,
    Guid FleetVehicleId,
    Guid FleetId,
    string FleetName,
    Guid RideId,
    Guid PackageRideId,
    Guid TicketId,
    Guid ReportId,
    Guid InsuranceDocId,
    Guid RevenueDocId,
    string Imei,
    string RegNo,
    string FleetRegNo);

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
