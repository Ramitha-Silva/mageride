using System.Globalization;
using Dapper;
using MageRide.TestKit;

namespace MageRide.Analytics.Tests.Infrastructure;

/// <summary>A completed ride and the fare that settled it.</summary>
internal sealed record SeededRide(Guid Id, Guid PassengerId, DateTimeOffset CompletedAt, long? SettledFareMinor);

/// <summary>
/// Seeds the rows other services own — the facts this read model is derived from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every row is shaped the way its owning service writes it</b>, because that is the only version
/// of these tables the rollup will ever meet in production. A completed ride is a
/// <c>rides.rides</c> row in a settled terminal state <em>plus</em> the append-only
/// <c>rides.transitions</c> row ride-svc writes on the <c>InProgress → Completed</c> edge (C022's
/// <c>RideService.CompleteAsync</c>) — not a row with <c>state = 'Completed'</c>, which is a state
/// no ride ever rests in. A "new rider" is an <c>iam.user_roles</c> grant, which is what iam-svc
/// inserts and never rewrites. A daily fee is a <c>billing.daily_fee_charges</c> row keyed by its
/// Asia/Colombo <c>fee_date</c>, which subscription-svc derives under D-13.
/// </para>
/// <para>
/// <b>Every seeded instant is explicit.</b> Nothing here relies on a column default of <c>now()</c>:
/// the whole component is about which Colombo day a fact belongs to, and a suite that let the
/// database stamp its own rows could not put one on either side of midnight.
/// </para>
/// </remarks>
internal sealed class AnalyticsSeed(PostgresFixture postgres)
{
    private int _plate;

    // -----------------------------------------------------------------------------------------
    // iam
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A user and the role grant that makes them a new rider or a new driver on
    /// <paramref name="grantedAt"/>.
    /// </summary>
    /// <remarks>
    /// <c>iam.users.created_at</c> is stamped with the same instant, so a test that wanted to count
    /// from the account rather than the grant would find the two agree — which is what makes
    /// <c>NewRidersAreCountedFromTheRoleGrantAndNotFromTheAccountsPrimaryRole</c> a real
    /// distinction rather than an artefact of the seed.
    /// </remarks>
    public async Task<Guid> CreateUserAsync(string role, DateTimeOffset? grantedAt = null)
    {
        var id = Guid.NewGuid();
        var at = grantedAt ?? AnalyticsHarness.DefaultNow;

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name, created_at, updated_at)
            VALUES (@Id, @Phone, @Role, @Name, @At, @At);
            INSERT INTO iam.user_roles (user_id, role, granted_at)
            VALUES (@Id, @Role, @At) ON CONFLICT DO NOTHING;
            """,
            new
            {
                Id = id,
                Phone = "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture),
                Role = role,
                Name = $"C061 {id.ToString()[..8]}",
                At = at,
            });

        return id;
    }

    /// <summary>Adds a second role grant to an existing account, on its own date.</summary>
    /// <remarks>
    /// The case that separates "count the grant" from "count the account": a passenger who signs up
    /// to drive three days later is one new rider on day one and one new driver on day four.
    /// </remarks>
    public async Task GrantRoleAsync(Guid userId, string role, DateTimeOffset grantedAt)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.user_roles (user_id, role, granted_at)
            VALUES (@UserId, @Role, @At) ON CONFLICT DO NOTHING;
            UPDATE iam.users SET role = @Role WHERE id = @UserId;
            """,
            new { UserId = userId, Role = role, At = grantedAt });
    }

    // -----------------------------------------------------------------------------------------
    // registry
    // -----------------------------------------------------------------------------------------

    public async Task<Guid> CreateVehicleAsync(
        Guid ownerId, string mode = "C", string status = "APPROVED", string vehicleType = "three_wheeler")
    {
        var id = Guid.NewGuid();
        var plate = $"C061-{Interlocked.Increment(ref _plate):D4}";

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
                (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            VALUES (@Id, @OwnerId, @Plate, @VehicleType, @Mode, @Status, 'C061 driver');
            """,
            new { Id = id, OwnerId = ownerId, Plate = plate, VehicleType = vehicleType, Mode = mode, Status = status });

        return id;
    }

    /// <summary>A driving licence with <paramref name="pendingFields"/> fields awaiting an officer (AL-29).</summary>
    public async Task<Guid> PendingLicenceAsync(Guid driverId, int pendingFields = 1)
    {
        var documentId = Guid.NewGuid();

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.documents (id, driver_id, kind, file_url)
            VALUES (@Id, @DriverId, 'driving_license', 'https://example.invalid/c061.jpg');
            """,
            new { Id = documentId, DriverId = driverId });

        for (var i = 0; i < pendingFields; i++)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO registry.document_fields (document_id, field_key, field_value, source, verify_status)
                VALUES (@DocumentId, @Key, 'value', 'manual', 'pending');
                """,
                new { DocumentId = documentId, Key = $"c061_field_{i}" });
        }

        return documentId;
    }

    /// <summary>A vehicle held in the registration queue by a <c>pending_review</c> step (AL-30).</summary>
    public async Task PendingOnboardingStepAsync(Guid vehicleId, string step = "insurance")
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.onboarding_steps (vehicle_id, step, status)
            VALUES (@VehicleId, @Step, 'pending_review')
            ON CONFLICT (vehicle_id, step) DO UPDATE SET status = EXCLUDED.status;
            """,
            new { VehicleId = vehicleId, Step = step });
    }

    /// <summary>An organisation awaiting Verification-Officer approval (AL-03, US-13.A7).</summary>
    public async Task<Guid> CreateFleetAsync(Guid ownerId, string status = "PENDING")
    {
        var id = Guid.NewGuid();

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.fleets (id, owner_id, name, business_reg, status)
            VALUES (@Id, @OwnerId, @Name, @Reg, @Status);
            """,
            new
            {
                Id = id,
                OwnerId = ownerId,
                Name = $"C061 Transport {id.ToString()[..8]}",
                Reg = $"PV-{id.ToString()[..8]}",
                Status = status,
            });

        return id;
    }

    // -----------------------------------------------------------------------------------------
    // dispatch, support
    // -----------------------------------------------------------------------------------------

    public async Task SetPresenceAsync(Guid driverId, Guid vehicleId, string state, DateTimeOffset lastSeenAt)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO dispatch.driver_presence (driver_id, vehicle_id, vehicle_type, state, last_seen_at)
            VALUES (@DriverId, @VehicleId, 'three_wheeler', @State, @LastSeenAt)
            ON CONFLICT (driver_id) DO UPDATE
              SET state = EXCLUDED.state, last_seen_at = EXCLUDED.last_seen_at;
            """,
            new { DriverId = driverId, VehicleId = vehicleId, State = state, LastSeenAt = lastSeenAt });
    }

    /// <summary>
    /// A support ticket. A <c>RESOLVED</c> one carries <c>resolved_at</c>, because migration 1309's
    /// <c>ck_tickets_resolution</c> makes the two inseparable — "an OPEN ticket with a resolution
    /// timestamp reads as answered on a queue nobody has answered".
    /// </summary>
    public async Task<Guid> CreateTicketAsync(Guid userId, string status = "OPEN")
    {
        var id = Guid.NewGuid();

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO support.tickets (id, user_id, category, description, status, resolved_at)
            VALUES (@Id, @UserId, 'c061', 'seeded', @Status, @ResolvedAt);
            """,
            new
            {
                Id = id,
                UserId = userId,
                Status = status,
                ResolvedAt = status == "RESOLVED" ? AnalyticsHarness.DefaultNow : (DateTimeOffset?)null,
            });

        return id;
    }

    // -----------------------------------------------------------------------------------------
    // rides + fares
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A ride that reached <c>Completed</c> at <paramref name="completedAt"/>, optionally with a
    /// settled fare.
    /// </summary>
    /// <param name="settledFareMinor">
    /// Null leaves the ride in <c>PaymentPending</c> with no payment row — a trip that happened and
    /// whose money has not arrived, which is the state every ride passes through.
    /// </param>
    /// <param name="paymentState">
    /// The <c>fares.ride_payments</c> state. One of R-05's four terminals collects the fare; anything
    /// else (<c>Initiated</c>, <c>Disputed</c>, <c>Refunded</c>) is a fare that did not.
    /// </param>
    public async Task<SeededRide> CompleteRideAsync(
        Guid passengerId,
        DateTimeOffset completedAt,
        long? settledFareMinor = 50_000,
        string paymentState = "Succeeded",
        string method = "onepay")
    {
        var rideId = Guid.NewGuid();

        // 'Paid' is terminal, so ux_rides_open_passenger lets one passenger hold as many of these as
        // a test needs. A ride with no settlement stays in PaymentPending, which is not exempt —
        // hence one passenger per unsettled ride in the tests that seed them.
        var state = settledFareMinor is null ? "PaymentPending" : "Paid";

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO rides.rides
                (id, passenger_id, booker_id, client_request_id, vehicle_type,
                 pickup_geo, dropoff_geo, state, fare_estimate_minor, created_at, updated_at, terminal_at)
            VALUES
                (@Id, @PassengerId, @PassengerId, gen_random_uuid(), 'three_wheeler',
                 ST_SetSRID(ST_MakePoint(79.861, 6.927), 4326)::geography,
                 ST_SetSRID(ST_MakePoint(79.884, 6.901), 4326)::geography,
                 @State, @FareMinor, @CreatedAt, @CompletedAt, @TerminalAt);

            INSERT INTO rides.transitions (ride_id, from_state, to_state, actor_type, ts)
            VALUES (@Id, 'InProgress', 'Completed', 'driver', @CompletedAt);
            """,
            new
            {
                Id = rideId,
                PassengerId = passengerId,
                State = state,
                FareMinor = settledFareMinor ?? 50_000,
                CreatedAt = completedAt.AddMinutes(-20),
                CompletedAt = completedAt,
                TerminalAt = settledFareMinor is null ? (DateTimeOffset?)null : completedAt.AddMinutes(2),
            });

        if (settledFareMinor is { } amount)
        {
            await AddPaymentAsync(rideId, amount, paymentState, method, completedAt.AddMinutes(1), attemptNo: 1);
        }

        return new SeededRide(rideId, passengerId, completedAt, settledFareMinor);
    }

    /// <summary>Adds one <c>fares.ride_payments</c> attempt to an existing ride (D-10's retry chain).</summary>
    public async Task AddPaymentAsync(
        Guid rideId, long amountMinor, string state, string method, DateTimeOffset createdAt, int attemptNo)
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
                CreatedAt = createdAt,
            });
    }

    /// <summary>
    /// A day of Mode C volume in two statements — <paramref name="count"/> completed and settled
    /// rides, all on <paramref name="completedAt"/>.
    /// </summary>
    /// <remarks>
    /// Set-based rather than a loop of round trips, because this exists to give the "completes
    /// within its window" test something to measure and the seeding must not be the slow part.
    /// </remarks>
    public async Task BulkCompletedRidesAsync(
        Guid passengerId, DateTimeOffset completedAt, int count, long fareMinor)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            WITH inserted AS (
                INSERT INTO rides.rides
                    (passenger_id, booker_id, client_request_id, vehicle_type,
                     pickup_geo, dropoff_geo, state, fare_estimate_minor, created_at, updated_at, terminal_at)
                SELECT @PassengerId, @PassengerId, gen_random_uuid(), 'three_wheeler',
                       ST_SetSRID(ST_MakePoint(79.861, 6.927), 4326)::geography,
                       ST_SetSRID(ST_MakePoint(79.884, 6.901), 4326)::geography,
                       'Paid', @FareMinor, @CreatedAt, @CompletedAt, @CompletedAt
                  FROM generate_series(1, @Count)
                RETURNING id
            ),
            moved AS (
                INSERT INTO rides.transitions (ride_id, from_state, to_state, actor_type, ts)
                SELECT id, 'InProgress', 'Completed', 'driver', @CompletedAt FROM inserted
                RETURNING ride_id
            )
            INSERT INTO fares.ride_payments (ride_id, state, method, amount_minor, attempt_no, created_at, updated_at)
            SELECT ride_id, 'Succeeded', 'onepay', @FareMinor::int, 1, @CompletedAt, @CompletedAt FROM moved;
            """,
            new
            {
                PassengerId = passengerId,
                Count = count,
                FareMinor = fareMinor,
                CreatedAt = completedAt.AddMinutes(-20),
                CompletedAt = completedAt,
            },
            commandTimeout: 120);
    }

    // -----------------------------------------------------------------------------------------
    // billing
    // -----------------------------------------------------------------------------------------

    /// <summary>One D-13 daily fee, on its Asia/Colombo <c>fee_date</c>.</summary>
    public async Task ChargeDailyFeeAsync(
        Guid driverId, Guid vehicleId, DateOnly feeDate, long amountMinor, string status = "PAID")
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO billing.daily_fee_charges
                (driver_id, vehicle_id, fee_date, amount_minor, trips_that_day, status)
            VALUES (@DriverId, @VehicleId, @FeeDate, @AmountMinor::int, 2, @Status)
            ON CONFLICT (driver_id, vehicle_id, fee_date) DO UPDATE
              SET amount_minor = EXCLUDED.amount_minor, status = EXCLUDED.status;
            """,
            new
            {
                DriverId = driverId,
                VehicleId = vehicleId,
                FeeDate = feeDate,
                AmountMinor = amountMinor,
                Status = status,
            });
    }
}
