using Dapper;
using MageRide.AdminBff.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.AdminBff.Persistence;

/// <summary>Everything SCR-AP-011's four tabs render, read in one round trip.</summary>
public sealed record PassengerTabs(
    IReadOnlyList<SosContactRow> SosContacts,
    IReadOnlyList<TripRow> Trips,
    IReadOnlyList<PaymentRow> Payments,
    IReadOnlyList<PackageRow> Packages,
    IReadOnlyList<DisputeRow> Disputes);

/// <summary>Everything SCR-AP-013's five tabs and its vehicle chips render.</summary>
public sealed record DriverTabs(
    IReadOnlyList<LinkedVehicleRow> Vehicles,
    IReadOnlyList<TripRow> Trips,
    IReadOnlyList<WalletLedgerRow> WalletLedger,
    IReadOnlyList<DailyFeeRow> DailyFee,
    IReadOnlyList<CreditTransferRow> CreditTransfers,
    IReadOnlyList<VehicleReportRow> Reports);

/// <summary>Everything SCR-AP-015's four tabs and its document grid render.</summary>
public sealed record VehicleTabs(
    TrackerRow? Tracker,
    IReadOnlyList<VerificationDocumentRow> Documents,
    IReadOnlyList<TripRow> Trips,
    IReadOnlyList<VehicleEarningsRow> Earnings,
    IReadOnlyList<DailyFeeRow> DailyFee,
    IReadOnlyList<VehicleReportRow> Reports);

/// <summary>
/// The read models behind the three directories (AL-40/41/42, I-28.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads only, and never a service's write path.</b> C064's fence in as many words. Every
/// statement here is a <c>SELECT</c> against tables five other services own, and the one thing this
/// component writes on a directory request is the <c>PII_READ</c> row — which is
/// <c>audit.events</c>, admin-bff's own table.
/// </para>
/// <para>
/// <b>The page is chosen before anything is counted, and that is the whole performance
/// design.</b> Each search is a CTE that filters, orders on migration 0109/0317's keyset index and
/// takes <c>LIMIT</c> — and only then are the per-row facts a directory row carries (the trip
/// count, the registration numbers, the owning organisation) joined on by <c>LATERAL</c>. So the
/// aggregates run at most <c>limit + 1</c> times whatever the size of the platform, which is what
/// makes the DoD's "10k-row directory, first page under 500 ms p95" a property of the query shape
/// rather than of the current row count. Counting first and paging afterwards would be the same
/// answer at a cost that grows with the table.
/// </para>
/// <para>
/// <b>Cursor pagination, never <c>OFFSET</c>.</b> The position is the last row's
/// <c>(created_at, id)</c>, which is the index's own order; an <c>OFFSET</c> deep into a directory
/// re-reads every row it skips and, worse, silently drops a row when somebody registers while an
/// operator is paging.
/// </para>
/// <para>
/// <b>Substring search, and the wildcards are escaped.</b> An operator has half a plate or half a
/// name — the same reason C063's queues search that way. <see cref="Pattern"/> neutralises
/// <c>%</c> and <c>_</c> so a search for "10%" means "10%", and every criterion is a bound
/// parameter: none of them is concatenated into SQL.
/// </para>
/// </remarks>
public interface IDirectoryRepository
{
    /// <summary>SCR-AP-010's page. Reads <c>limit + 1</c> rows so the cursor can be decided.</summary>
    Task<IReadOnlyList<PassengerRow>> SearchPassengersAsync(
        PassengerSearchQuery query, CancellationToken cancellationToken);

    /// <summary>SCR-AP-011's profile, or null when the id names no passenger account.</summary>
    Task<PassengerProfileRow?> FindPassengerAsync(Guid passengerId, CancellationToken cancellationToken);

    /// <summary>SCR-AP-011's four tabs, capped at <paramref name="rows"/> each.</summary>
    Task<PassengerTabs> PassengerTabsAsync(Guid passengerId, int rows, CancellationToken cancellationToken);

    /// <summary>SCR-AP-012's page.</summary>
    Task<IReadOnlyList<DriverRow>> SearchDriversAsync(
        DriverSearchQuery query, CancellationToken cancellationToken);

    /// <summary>SCR-AP-013's profile, or null when the id names no driver account.</summary>
    Task<DriverProfileRow?> FindDriverAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>SCR-AP-013's chips and five tabs.</summary>
    Task<DriverTabs> DriverTabsAsync(Guid driverId, int rows, CancellationToken cancellationToken);

    /// <summary>SCR-AP-014's page.</summary>
    Task<IReadOnlyList<VehicleDirectoryRow>> SearchVehiclesAsync(
        VehicleSearchQuery query, CancellationToken cancellationToken);

    /// <summary>SCR-AP-015's info block, or null when the id names no vehicle.</summary>
    Task<VehicleInfoRow?> FindVehicleAsync(Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>SCR-AP-015's tracker, document grid and four tabs.</summary>
    Task<VehicleTabs> VehicleTabsAsync(Guid vehicleId, int rows, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDirectoryRepository"/>
internal sealed class DirectoryRepository(INpgsqlConnectionFactory connections) : IDirectoryRepository
{
    /// <summary>
    /// <c>iam.users.role</c> for the account kinds the two people-directories list.
    /// </summary>
    /// <remarks>
    /// <b>The primary role column, not the <c>iam.user_roles</c> union — and deliberately not the
    /// choice C061's rollup made.</b> That component counts new riders out of the grant table
    /// because a *historical count* must not move when somebody later signs up to drive; this one
    /// is answering "which directory does this account live in", and an account lives in one. Using
    /// the union would put every driver who has ever booked a ride into the passenger directory a
    /// CSR is searching, and would put an internal operator granted <c>passenger</c> for a test
    /// there beside them. iam-svc writes this column at account creation
    /// (<c>UserRepository.CreateAsync</c>) and the grant row alongside it.
    /// </remarks>
    private const string PassengerRole = "passenger";

    private const string DriverRole = "driver";

    // ---------------------------------------------------------------------------------------
    // Passenger directory (AL-40)
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// The trip count is <c>rides.rides</c> only: a passenger has no Mode A/B session of their own
    /// — <c>trips.sessions</c> is the *vehicle's* journey and carries a driver, not a rider (D-03).
    /// </remarks>
    private const string SearchPassengersSql =
        """
        WITH page AS (
          SELECT u.id, COALESCE(u.first_name, '') AS name, u.phone, u.created_at, u.is_blocked
            FROM iam.users u
           WHERE u.role = @Role
             AND (@Id::uuid          IS NULL OR u.id = @Id)
             AND (@Name::text        IS NULL OR u.first_name ILIKE @Name)
             AND (@Mobile::text      IS NULL OR u.phone      ILIKE @Mobile)
             AND (@Email::text       IS NULL OR u.email      ILIKE @Email)
             AND (@CursorAt::timestamptz IS NULL OR (u.created_at, u.id) < (@CursorAt, @CursorId))
           ORDER BY u.created_at DESC, u.id DESC
           LIMIT @Limit
        )
        SELECT p.id                       AS "PassengerId",
               p.name                     AS "Name",
               p.phone                    AS "Mobile",
               p.created_at               AS "JoinedAt",
               COALESCE(t.trips, 0)::int  AS "Trips",
               CASE WHEN p.is_blocked THEN 'blocked' ELSE 'active' END AS "Status"
          FROM page p
          LEFT JOIN LATERAL (
                SELECT count(*) AS trips
                  FROM rides.rides r
                 WHERE r.passenger_id = p.id
                   AND r.state = ANY(@CompletedStates)) t ON true
         ORDER BY p.created_at DESC, p.id DESC;
        """;

    public async Task<IReadOnlyList<PassengerRow>> SearchPassengersAsync(
        PassengerSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<PassengerRow>(new CommandDefinition(
            SearchPassengersSql,
            new
            {
                Role = PassengerRole,
                query.Id,
                Name = Pattern(query.Name),
                Mobile = Pattern(query.Mobile),
                Email = Pattern(query.Email),
                query.CursorAt,
                query.CursorId,
                query.Limit,
                CompletedStates = CompletedRideStates.All,
            },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <remarks>
    /// The rating is what drivers gave <em>them</em> — <c>direction = 'driver_to_passenger'</c>
    /// (US-18.2). Reading the other direction would put a driver's own score on a passenger's
    /// profile, which is the same column and the opposite fact.
    /// </remarks>
    private const string FindPassengerSql =
        """
        SELECT u.id                        AS "PassengerId",
               COALESCE(u.first_name, '')  AS "Name",
               u.phone                     AS "Mobile",
               u.email                     AS "Email",
               u.created_at                AS "JoinedAt",
               r.rating                    AS "Rating",
               u.default_payment_method    AS "DefaultPay",
               CASE WHEN u.is_blocked THEN 'blocked' ELSE 'active' END AS "Status"
          FROM iam.users u
          LEFT JOIN LATERAL (
                SELECT round(avg(ra.stars)::numeric, 2)::float8 AS rating
                  FROM trips.ratings ra
                 WHERE ra.ratee_id = u.id
                   AND ra.direction = 'driver_to_passenger') r ON true
         WHERE u.id = @Id AND u.role = @Role;
        """;

    public async Task<PassengerProfileRow?> FindPassengerAsync(
        Guid passengerId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<PassengerProfileRow>(new CommandDefinition(
            FindPassengerSql,
            new { Id = passengerId, Role = PassengerRole },
            cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <para>
    /// <b>Five statements, one round trip.</b> A tabbed screen needs all of them before it renders
    /// anything, and five sequential awaits would pay the network five times for a page that is one
    /// screen.
    /// </para>
    /// <para>
    /// <b>Every tab hangs off <c>rides.passenger_id</c>.</b> That is the column
    /// <c>ix_rides_passenger_hist</c> indexes and the one <c>GET /v1/rides/history</c> reads, so the
    /// operator's view of somebody's trips is the same set that person sees in their own app. A
    /// proxy booking made <em>for</em> somebody else is the booker's ride and appears on the
    /// booker's detail, which is where the payment instrument and the dispute would be.
    /// </para>
    /// <para>
    /// <b>Packages are a tab and not a trip.</b> <c>kind = 2</c> is the same state machine over the
    /// same table (ADD Appendix B.2 invariant 6), and SCR-AP-011 shows them apart because what an
    /// operator asks about a delivery — who received it, was the OTP used — is not what they ask
    /// about a ride.
    /// </para>
    /// </remarks>
    private const string PassengerTabsSql =
        """
        SELECT c.name AS "Name", c.phone AS "Phone"
          FROM iam.emergency_contacts c
         WHERE c.user_id = @Id
         ORDER BY c.created_at
         LIMIT @Rows;

        SELECT r.id                  AS "TripId",
               'ride'::text          AS "Kind",
               r.state               AS "State",
               r.vehicle_type        AS "VehicleType",
               r.accepted_vehicle_id AS "VehicleId",
               v.registration_number AS "RegNo",
               r.accepted_driver_id  AS "CounterpartyId",
               COALESCE(du.first_name, v.driver_name) AS "CounterpartyName",
               pay.amount_minor::bigint AS "FareMinor",
               pay.currency          AS "Currency",
               r.created_at          AS "StartedAt",
               r.terminal_at         AS "EndedAt"
          FROM rides.rides r
          LEFT JOIN registry.vehicles v ON v.id = r.accepted_vehicle_id
          LEFT JOIN iam.users du        ON du.id = r.accepted_driver_id
          LEFT JOIN LATERAL (
                SELECT p.amount_minor, p.currency
                  FROM fares.ride_payments p
                 WHERE p.ride_id = r.id
                 ORDER BY p.attempt_no DESC, p.created_at DESC
                 LIMIT 1) pay ON true
         WHERE r.passenger_id = @Id AND r.kind <> 2
         ORDER BY r.created_at DESC
         LIMIT @Rows;

        SELECT p.id                       AS "PaymentId",
               p.ride_id                  AS "RideId",
               p.method                   AS "Method",
               p.state                    AS "State",
               p.amount_minor::bigint     AS "AmountMinor",
               p.surcharge_minor::bigint  AS "SurchargeMinor",
               p.tip_amount_minor::bigint AS "TipMinor",
               p.currency                 AS "Currency",
               p.attempt_no               AS "AttemptNo",
               p.created_at               AS "CreatedAt"
          FROM fares.ride_payments p
          JOIN rides.rides r ON r.id = p.ride_id
         WHERE r.passenger_id = @Id
         ORDER BY p.created_at DESC
         LIMIT @Rows;

        SELECT r.id                  AS "RideId",
               r.state               AS "State",
               r.package_size        AS "PackageSize",
               r.package_description AS "Description",
               r.recipient_name      AS "RecipientName",
               r.recipient_phone     AS "RecipientPhone",
               pay.amount_minor::bigint AS "FareMinor",
               pay.currency          AS "Currency",
               r.created_at          AS "CreatedAt",
               r.terminal_at         AS "TerminalAt"
          FROM rides.rides r
          LEFT JOIN LATERAL (
                SELECT p.amount_minor, p.currency
                  FROM fares.ride_payments p
                 WHERE p.ride_id = r.id
                 ORDER BY p.attempt_no DESC, p.created_at DESC
                 LIMIT 1) pay ON true
         WHERE r.passenger_id = @Id AND r.kind = 2
         ORDER BY r.created_at DESC
         LIMIT @Rows;

        SELECT t.id             AS "TicketId",
               t.category       AS "Category",
               t.status         AS "Status",
               t.description    AS "Description",
               t.admin_response AS "Response",
               t.ride_id        AS "RideId",
               t.created_at     AS "CreatedAt",
               t.updated_at     AS "UpdatedAt"
          FROM support.tickets t
         WHERE t.user_id = @Id
         ORDER BY t.created_at DESC
         LIMIT @Rows;
        """;

    public async Task<PassengerTabs> PassengerTabsAsync(
        Guid passengerId, int rows, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(new CommandDefinition(
            PassengerTabsSql,
            new { Id = passengerId, Rows = rows },
            cancellationToken: cancellationToken));

        return new PassengerTabs(
            [.. await reader.ReadAsync<SosContactRow>()],
            [.. await reader.ReadAsync<TripRow>()],
            [.. await reader.ReadAsync<PaymentRow>()],
            [.. await reader.ReadAsync<PackageRow>()],
            [.. await reader.ReadAsync<DisputeRow>()]);
    }

    // ---------------------------------------------------------------------------------------
    // Driver directory (AL-41)
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// <b>The status filter is spelled out in the predicate rather than compared to the derived
    /// column.</b> A CASE alias is not visible to the same SELECT's WHERE, and pushing the filter
    /// into a wrapper would apply it <em>after</em> the LIMIT — which is a page of twenty that
    /// answers with three.
    /// </para>
    /// <para>
    /// <b>Suspended outranks verified</b> because it is the later fact and the one an operator
    /// opened the row to find: a driver whose licence an officer approved in March and whom
    /// moderation blocked in July is suspended, not verified.
    /// </para>
    /// </remarks>
    private const string SearchDriversSql =
        """
        WITH page AS (
          SELECT u.id,
                 COALESCE(p.display_name, u.first_name, '') AS name,
                 u.phone,
                 u.created_at,
                 COALESCE(dl.level, 3) AS level,
                 CASE WHEN u.is_blocked            THEN 'suspended'
                      WHEN p.verified_at IS NOT NULL THEN 'verified'
                      ELSE 'pending' END AS status
            FROM iam.users u
            LEFT JOIN registry.driver_profiles p ON p.driver_id = u.id
            LEFT JOIN dispatch.driver_levels  dl ON dl.driver_id = u.id
           WHERE u.role = @Role
             AND (@Status = 'all'
                  OR (@Status = 'suspended' AND u.is_blocked)
                  OR (@Status = 'verified'  AND NOT u.is_blocked AND p.verified_at IS NOT NULL)
                  OR (@Status = 'pending'   AND NOT u.is_blocked AND p.verified_at IS NULL))
             AND (@Id::uuid     IS NULL OR u.id = @Id)
             AND (@Name::text   IS NULL OR COALESCE(p.display_name, u.first_name, '') ILIKE @Name)
             AND (@Mobile::text IS NULL OR u.phone ILIKE @Mobile)
             AND (@Nic::text    IS NULL OR p.nic_no ILIKE @Nic)
             AND (@Level::int   IS NULL OR COALESCE(dl.level, 3) = @Level)
             AND (@RegNo::text  IS NULL OR EXISTS (
                    SELECT 1 FROM registry.vehicles v
                     WHERE v.owner_id = u.id AND v.registration_number ILIKE @RegNo))
             AND (@CursorAt::timestamptz IS NULL OR (u.created_at, u.id) < (@CursorAt, @CursorId))
           ORDER BY u.created_at DESC, u.id DESC
           LIMIT @Limit
        )
        SELECT pg.id      AS "DriverId",
               pg.name    AS "Name",
               pg.phone   AS "Mobile",
               COALESCE(veh.regs, ARRAY[]::text[]) AS "Vehicles",
               pg.level::int AS "Level",
               (COALESCE(rd.n, 0) + COALESCE(ss.n, 0))::int AS "Trips",
               pg.status  AS "Status",
               pg.created_at AS "JoinedAt"
          FROM page pg
          LEFT JOIN LATERAL (
                SELECT array_agg(v.registration_number ORDER BY v.created_at DESC) AS regs
                  FROM registry.vehicles v
                 WHERE v.owner_id = pg.id AND v.status <> 'DEACTIVATED') veh ON true
          LEFT JOIN LATERAL (
                SELECT count(*) AS n
                  FROM rides.rides r
                 WHERE r.accepted_driver_id = pg.id
                   AND r.state = ANY(@CompletedStates)) rd ON true
          LEFT JOIN LATERAL (
                SELECT count(*) AS n
                  FROM trips.sessions s
                 WHERE s.driver_id = pg.id AND s.state = 'COMPLETED') ss ON true
         ORDER BY pg.created_at DESC, pg.id DESC;
        """;

    public async Task<IReadOnlyList<DriverRow>> SearchDriversAsync(
        DriverSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<DriverRow>(new CommandDefinition(
            SearchDriversSql,
            new
            {
                Role = DriverRole,
                query.Id,
                Name = Pattern(query.Name),
                Mobile = Pattern(query.Mobile),
                Nic = Pattern(query.Nic),
                RegNo = Pattern(query.RegNo),
                query.Level,
                query.Status,
                query.CursorAt,
                query.CursorId,
                query.Limit,
                CompletedStates = CompletedRideStates.All,
            },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <remarks>
    /// The wallet is <c>billing.wallets</c>, which 1102 calls a rebuildable mirror of
    /// <c>billing.accounts.balance_minor</c> — the read model built for exactly this kind of read.
    /// A driver with no ledger account has no row and reads zero, which is their balance.
    /// </remarks>
    private const string FindDriverSql =
        """
        SELECT u.id                                   AS "DriverId",
               COALESCE(p.display_name, u.first_name, '') AS "Name",
               u.phone                                AS "Mobile",
               p.nic_no                               AS "Nic",
               u.created_at                           AS "JoinedAt",
               rt.rating                              AS "Rating",
               COALESCE(acc.balance_minor, 0)::bigint AS "WalletMinor",
               COALESCE(acc.currency, 'LKR')          AS "Currency",
               COALESCE(dl.level, 3)::int             AS "Level",
               COALESCE(dl.rating_points, 0)::int     AS "Points",
               CASE WHEN u.is_blocked              THEN 'suspended'
                    WHEN p.verified_at IS NOT NULL THEN 'verified'
                    ELSE 'pending' END                AS "Status",
               p.verified_at                          AS "VerifiedAt"
          FROM iam.users u
          LEFT JOIN registry.driver_profiles p ON p.driver_id = u.id
          LEFT JOIN dispatch.driver_levels  dl ON dl.driver_id = u.id
          LEFT JOIN LATERAL (
                SELECT w.balance_minor, a.currency
                  FROM billing.accounts a
                  JOIN billing.wallets  w ON w.account_id = a.id
                 WHERE a.owner_type = 'driver' AND a.owner_id = u.id
                 ORDER BY a.created_at
                 LIMIT 1) acc ON true
          LEFT JOIN LATERAL (
                SELECT round(avg(ra.stars)::numeric, 2)::float8 AS rating
                  FROM trips.ratings ra
                 WHERE ra.ratee_id = u.id
                   AND ra.direction = 'passenger_to_driver') rt ON true
         WHERE u.id = @Id AND u.role = @Role;
        """;

    public async Task<DriverProfileRow?> FindDriverAsync(Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<DriverProfileRow>(new CommandDefinition(
            FindDriverSql,
            new { Id = driverId, Role = DriverRole },
            cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <para>
    /// <b>The Trips tab is a union and has to be.</b> A Mode C driver's journeys are
    /// <c>rides.rides</c>; a fleet driver's are <c>trips.sessions</c>. The two are different
    /// aggregates owned by different services (ride-svc ≠ trip-state-svc) and this reads both
    /// without writing either — which is what a directory is.
    /// </para>
    /// <para>
    /// <b>The credit-transfer direction is computed against this driver</b>, not read from the
    /// <c>direction</c> column: that column records who <em>started</em> the transfer
    /// (REQUESTED / DIRECT, US-9.13/9.21) and an operator reading a wallet needs to know which way
    /// the money went. Both are on the row, under names that say which is which.
    /// </para>
    /// </remarks>
    private const string DriverTabsSql =
        """
        SELECT v.id                  AS "VehicleId",
               v.registration_number AS "RegNo",
               v.vehicle_type        AS "Type",
               v.mode::text          AS "Mode",
               v.status              AS "Status",
               v.dispatch_state      AS "DispatchState",
               true                  AS "Owned"
          FROM registry.vehicles v
         WHERE v.owner_id = @Id
        UNION
        SELECT v.id, v.registration_number, v.vehicle_type, v.mode::text, v.status, v.dispatch_state, false
          FROM registry.vehicles v
          JOIN registry.fleet_assignments fa ON fa.vehicle_id = v.id
         WHERE fa.driver_id = @Id AND fa.revoked_at IS NULL AND v.owner_id <> @Id
         ORDER BY 2;

        SELECT t."TripId", t."Kind", t."State", t."VehicleType", t."VehicleId", t."RegNo",
               t."CounterpartyId", t."CounterpartyName", t."FareMinor", t."Currency",
               t."StartedAt", t."EndedAt"
          FROM (
            SELECT r.id                  AS "TripId",
                   'ride'::text          AS "Kind",
                   r.state               AS "State",
                   r.vehicle_type        AS "VehicleType",
                   r.accepted_vehicle_id AS "VehicleId",
                   v.registration_number AS "RegNo",
                   r.passenger_id        AS "CounterpartyId",
                   pu.first_name         AS "CounterpartyName",
                   pay.amount_minor::bigint AS "FareMinor",
                   pay.currency          AS "Currency",
                   r.created_at          AS "StartedAt",
                   r.terminal_at         AS "EndedAt"
              FROM rides.rides r
              LEFT JOIN registry.vehicles v ON v.id = r.accepted_vehicle_id
              LEFT JOIN iam.users pu        ON pu.id = r.passenger_id
              LEFT JOIN LATERAL (
                    SELECT p.amount_minor, p.currency
                      FROM fares.ride_payments p
                     WHERE p.ride_id = r.id
                     ORDER BY p.attempt_no DESC, p.created_at DESC
                     LIMIT 1) pay ON true
             WHERE r.accepted_driver_id = @Id
             UNION ALL
            SELECT s.id, 'session', s.state, v.vehicle_type, s.vehicle_id, v.registration_number,
                   NULL::uuid, NULL::text, NULL::bigint, NULL::text, s.started_at, s.ended_at
              FROM trips.sessions s
              JOIN registry.vehicles v ON v.id = s.vehicle_id
             WHERE s.driver_id = @Id) t
         ORDER BY t."StartedAt" DESC
         LIMIT @Rows;

        SELECT wt.id                     AS "EntryNo",
               wt.kind                   AS "Kind",
               wt.amount_minor           AS "AmountMinor",
               wt.balance_after_minor    AS "BalanceAfterMinor",
               wt.description            AS "Description",
               wt.ts                     AS "Ts"
          FROM billing.wallet_transactions wt
          JOIN billing.accounts a ON a.id = wt.account_id
         WHERE a.owner_type = 'driver' AND a.owner_id = @Id
         ORDER BY wt.ts DESC, wt.id DESC
         LIMIT @Rows;

        SELECT f.fee_date              AS "FeeDate",
               f.driver_id             AS "DriverId",
               f.vehicle_id            AS "VehicleId",
               v.registration_number   AS "RegNo",
               f.amount_minor::bigint  AS "AmountMinor",
               f.currency              AS "Currency",
               f.trips_that_day        AS "TripsThatDay",
               f.status                AS "Status",
               f.charged_at            AS "ChargedAt"
          FROM billing.daily_fee_charges f
          LEFT JOIN registry.vehicles v ON v.id = f.vehicle_id
         WHERE f.driver_id = @Id
         ORDER BY f.fee_date DESC
         LIMIT @Rows;

        SELECT ct.id AS "TransferId",
               CASE WHEN ct.sender_driver_id = @Id THEN 'out' ELSE 'in' END AS "Direction",
               ct.direction AS "Initiation",
               CASE WHEN ct.sender_driver_id = @Id
                    THEN ct.recipient_driver_id ELSE ct.sender_driver_id END AS "CounterpartyId",
               cp.first_name       AS "CounterpartyName",
               ct.amount_minor     AS "AmountMinor",
               ct.currency         AS "Currency",
               ct.status           AS "Status",
               ct.created_at       AS "CreatedAt"
          FROM billing.credit_transfers ct
          LEFT JOIN iam.users cp
                 ON cp.id = CASE WHEN ct.sender_driver_id = @Id
                                 THEN ct.recipient_driver_id ELSE ct.sender_driver_id END
         WHERE ct.sender_driver_id = @Id OR ct.recipient_driver_id = @Id
         ORDER BY ct.created_at DESC
         LIMIT @Rows;

        SELECT vr.id                  AS "ReportId",
               vr.vehicle_id          AS "VehicleId",
               v.registration_number  AS "RegNo",
               vr.reason              AS "Reason",
               vr.status              AS "Status",
               vr.created_at          AS "CreatedAt"
          FROM safety.vehicle_reports vr
          JOIN registry.vehicles v ON v.id = vr.vehicle_id
         WHERE v.owner_id = @Id
         ORDER BY vr.created_at DESC
         LIMIT @Rows;
        """;

    public async Task<DriverTabs> DriverTabsAsync(Guid driverId, int rows, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(new CommandDefinition(
            DriverTabsSql,
            new { Id = driverId, Rows = rows },
            cancellationToken: cancellationToken));

        return new DriverTabs(
            [.. await reader.ReadAsync<LinkedVehicleRow>()],
            [.. await reader.ReadAsync<TripRow>()],
            [.. await reader.ReadAsync<WalletLedgerRow>()],
            [.. await reader.ReadAsync<DailyFeeRow>()],
            [.. await reader.ReadAsync<CreditTransferRow>()],
            [.. await reader.ReadAsync<VehicleReportRow>()]);
    }

    // ---------------------------------------------------------------------------------------
    // Vehicle directory (AL-42)
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// <b>The owning organisation is a LATERAL with a LIMIT, not a join.</b>
    /// <c>registry.fleet_vehicles</c> is keyed <c>(fleet_id, vehicle_id)</c> and does not forbid a
    /// vehicle appearing under two organisations; a plain join would then return the vehicle twice
    /// and break the page's row count against its cursor.
    /// </para>
    /// <para>
    /// The trip count is rides <em>plus</em> completed sessions, because a bus and a three-wheeler
    /// are both in this directory and only one of them has rides.
    /// </para>
    /// </remarks>
    private const string SearchVehiclesSql =
        """
        WITH page AS (
          SELECT v.id, v.vehicle_type, v.mode, v.registration_number, v.status,
                 v.owner_id, v.created_at
            FROM registry.vehicles v
           WHERE (@Id::uuid       IS NULL OR v.id = @Id)
             AND (@RegNo::text    IS NULL OR v.registration_number ILIKE @RegNo)
             AND (@Type::text     IS NULL OR v.vehicle_type = @Type)
             AND (@Mode::text     IS NULL OR v.mode::text = @Mode)
             AND (@Status::text   IS NULL OR v.status = @Status)
             AND (@OwnerMobile::text IS NULL OR EXISTS (
                    SELECT 1 FROM iam.users o
                     WHERE o.id = v.owner_id AND o.phone ILIKE @OwnerMobile))
             AND (@FleetOrg::text IS NULL OR EXISTS (
                    SELECT 1 FROM registry.fleet_vehicles fv
                      JOIN registry.fleets f ON f.id = fv.fleet_id
                     WHERE fv.vehicle_id = v.id AND f.name ILIKE @FleetOrg))
             AND (@CursorAt::timestamptz IS NULL OR (v.created_at, v.id) < (@CursorAt, @CursorId))
           ORDER BY v.created_at DESC, v.id DESC
           LIMIT @Limit
        )
        SELECT pg.id                 AS "VehicleId",
               pg.vehicle_type       AS "Type",
               pg.mode::text         AS "Mode",
               o.first_name          AS "Owner",
               fl.name               AS "FleetOrg",
               pg.registration_number AS "RegNo",
               (COALESCE(rd.n, 0) + COALESCE(ss.n, 0))::int AS "Trips",
               pg.status             AS "Status",
               pg.created_at         AS "RegisteredAt"
          FROM page pg
          LEFT JOIN iam.users o ON o.id = pg.owner_id
          LEFT JOIN LATERAL (
                SELECT f.name
                  FROM registry.fleet_vehicles fv
                  JOIN registry.fleets f ON f.id = fv.fleet_id
                 WHERE fv.vehicle_id = pg.id
                 LIMIT 1) fl ON true
          LEFT JOIN LATERAL (
                SELECT count(*) AS n
                  FROM rides.rides r
                 WHERE r.accepted_vehicle_id = pg.id
                   AND r.state = ANY(@CompletedStates)) rd ON true
          LEFT JOIN LATERAL (
                SELECT count(*) AS n
                  FROM trips.sessions s
                 WHERE s.vehicle_id = pg.id AND s.state = 'COMPLETED') ss ON true
         ORDER BY pg.created_at DESC, pg.id DESC;
        """;

    public async Task<IReadOnlyList<VehicleDirectoryRow>> SearchVehiclesAsync(
        VehicleSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<VehicleDirectoryRow>(new CommandDefinition(
            SearchVehiclesSql,
            new
            {
                query.Id,
                RegNo = Pattern(query.RegNo),
                query.Type,
                query.Mode,
                OwnerMobile = Pattern(query.OwnerMobile),
                FleetOrg = Pattern(query.FleetOrg),
                query.Status,
                query.CursorAt,
                query.CursorId,
                query.Limit,
                CompletedStates = CompletedRideStates.All,
            },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <remarks>
    /// <para>
    /// The two expiries are the newest document of each kind — E-03 auto-suspends dispatch on the
    /// one that lapses, so the date an operator needs is the one currently in force, not the first
    /// ever uploaded.
    /// </para>
    /// <para>
    /// <b>The date is taken in Asia/Colombo and not in the session's timezone.</b> A certificate
    /// expires on a Sri Lankan calendar day (D-38), and a bare <c>::date</c> would render an expiry
    /// stored at midnight Colombo as the previous day on a UTC-clocked connection — an operator
    /// reading a cover note as one day short of what it says.
    /// </para>
    /// </remarks>
    private const string FindVehicleSql =
        """
        SELECT v.id                    AS "VehicleId",
               v.vehicle_type          AS "Type",
               v.registration_number   AS "RegNo",
               v.mode::text            AS "Mode",
               v.owner_id              AS "OwnerId",
               o.first_name            AS "Owner",
               fl.fleet_id             AS "FleetId",
               fl.name                 AS "FleetOrg",
               v.status                AS "Status",
               v.dispatch_state        AS "DispatchState",
               v.onboarding_status     AS "OnboardingStatus",
               (ins.expires_at AT TIME ZONE 'Asia/Colombo')::date AS "InsuranceExpiry",
               (rev.expires_at AT TIME ZONE 'Asia/Colombo')::date AS "RevenueLicenceExpiry",
               v.created_at            AS "RegisteredAt"
          FROM registry.vehicles v
          LEFT JOIN iam.users o ON o.id = v.owner_id
          LEFT JOIN LATERAL (
                SELECT f.id AS fleet_id, f.name
                  FROM registry.fleet_vehicles fv
                  JOIN registry.fleets f ON f.id = fv.fleet_id
                 WHERE fv.vehicle_id = v.id
                 LIMIT 1) fl ON true
          LEFT JOIN LATERAL (
                SELECT d.expires_at
                  FROM registry.documents d
                 WHERE d.vehicle_id = v.id AND d.kind = 'insurance'
                 ORDER BY d.created_at DESC
                 LIMIT 1) ins ON true
          LEFT JOIN LATERAL (
                SELECT d.expires_at
                  FROM registry.documents d
                 WHERE d.vehicle_id = v.id AND d.kind = 'revenue_license'
                 ORDER BY d.created_at DESC
                 LIMIT 1) rev ON true
         WHERE v.id = @Id;
        """;

    public async Task<VehicleInfoRow?> FindVehicleAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<VehicleInfoRow>(new CommandDefinition(
            FindVehicleSql, new { Id = vehicleId }, cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <para>
    /// <b>The document grid is <c>registry.documents</c> read exactly as C063 reads it</b> —
    /// including the LATERAL onto <c>docs.uploads</c> for AL-43 provenance — because SCR-AP-015's
    /// thumbnails open the same SCR-AP-003b viewer, and two queries producing the same grid would
    /// be two chances for one of them to hand out an unaudited link.
    /// </para>
    /// <para>
    /// <b>Earnings are derived, not read from <c>fares.driver_earnings</c>.</b> That rollup is
    /// keyed <c>(driver_id, earn_date)</c> — a vehicle two drivers shared on one day appears in it
    /// twice under neither of them. So the tab sums the settled payment of each of the vehicle's
    /// rides, one row per ride (<c>DISTINCT ON</c> over the retry chain, D-10) and bucketed by the
    /// Colombo business date, which is how D-38 defines a day everywhere else.
    /// </para>
    /// </remarks>
    private const string VehicleTabsSql =
        """
        SELECT tb.imei         AS "Imei",
               tb.state        AS "State",
               tb.last_seen_at AS "LastSeenAt"
          FROM prov.tracker_bindings tb
         WHERE tb.vehicle_id = @Id
         ORDER BY (tb.state = 'ACTIVE') DESC, tb.created_at DESC
         LIMIT 1;

        SELECT d.id           AS "DocId",
               d.kind         AS "Kind",
               d.file_url     AS "StorageUrl",
               up.captured_via AS "CapturedVia",
               d.created_at   AS "CreatedAt"
          FROM registry.documents d
          LEFT JOIN LATERAL (
                SELECT u.captured_via
                  FROM docs.uploads u
                 WHERE u.storage_url = d.file_url
                 ORDER BY u.created_at DESC
                 LIMIT 1) up ON true
         WHERE d.vehicle_id = @Id
         ORDER BY d.created_at DESC, d.id DESC
         LIMIT @Rows;

        SELECT t."TripId", t."Kind", t."State", t."VehicleType", t."VehicleId", t."RegNo",
               t."CounterpartyId", t."CounterpartyName", t."FareMinor", t."Currency",
               t."StartedAt", t."EndedAt"
          FROM (
            SELECT r.id                  AS "TripId",
                   'ride'::text          AS "Kind",
                   r.state               AS "State",
                   r.vehicle_type        AS "VehicleType",
                   r.accepted_vehicle_id AS "VehicleId",
                   v.registration_number AS "RegNo",
                   r.accepted_driver_id  AS "CounterpartyId",
                   du.first_name         AS "CounterpartyName",
                   pay.amount_minor::bigint AS "FareMinor",
                   pay.currency          AS "Currency",
                   r.created_at          AS "StartedAt",
                   r.terminal_at         AS "EndedAt"
              FROM rides.rides r
              JOIN registry.vehicles v ON v.id = r.accepted_vehicle_id
              LEFT JOIN iam.users du    ON du.id = r.accepted_driver_id
              LEFT JOIN LATERAL (
                    SELECT p.amount_minor, p.currency
                      FROM fares.ride_payments p
                     WHERE p.ride_id = r.id
                     ORDER BY p.attempt_no DESC, p.created_at DESC
                     LIMIT 1) pay ON true
             WHERE r.accepted_vehicle_id = @Id
             UNION ALL
            SELECT s.id, 'session', s.state, v.vehicle_type, s.vehicle_id, v.registration_number,
                   s.driver_id, du.first_name, NULL::bigint, NULL::text, s.started_at, s.ended_at
              FROM trips.sessions s
              JOIN registry.vehicles v ON v.id = s.vehicle_id
              LEFT JOIN iam.users du   ON du.id = s.driver_id
             WHERE s.vehicle_id = @Id) t
         ORDER BY t."StartedAt" DESC
         LIMIT @Rows;

        WITH settled AS (
          SELECT DISTINCT ON (p.ride_id)
                 p.ride_id, p.amount_minor, p.currency, p.created_at
            FROM fares.ride_payments p
            JOIN rides.rides r ON r.id = p.ride_id
           WHERE r.accepted_vehicle_id = @Id
             AND p.state = ANY(@SettledStates)
           ORDER BY p.ride_id, p.attempt_no DESC, p.created_at DESC
        )
        SELECT (s.created_at AT TIME ZONE 'Asia/Colombo')::date AS "EarnDate",
               count(*)::int                                    AS "Trips",
               sum(s.amount_minor)::bigint                      AS "GrossMinor",
               s.currency                                       AS "Currency"
          FROM settled s
         GROUP BY 1, s.currency
         ORDER BY 1 DESC
         LIMIT @Rows;

        SELECT f.fee_date              AS "FeeDate",
               f.driver_id             AS "DriverId",
               f.vehicle_id            AS "VehicleId",
               v.registration_number   AS "RegNo",
               f.amount_minor::bigint  AS "AmountMinor",
               f.currency              AS "Currency",
               f.trips_that_day        AS "TripsThatDay",
               f.status                AS "Status",
               f.charged_at            AS "ChargedAt"
          FROM billing.daily_fee_charges f
          LEFT JOIN registry.vehicles v ON v.id = f.vehicle_id
         WHERE f.vehicle_id = @Id
         ORDER BY f.fee_date DESC
         LIMIT @Rows;

        SELECT vr.id                 AS "ReportId",
               vr.vehicle_id         AS "VehicleId",
               v.registration_number AS "RegNo",
               vr.reason             AS "Reason",
               vr.status             AS "Status",
               vr.created_at         AS "CreatedAt"
          FROM safety.vehicle_reports vr
          JOIN registry.vehicles v ON v.id = vr.vehicle_id
         WHERE vr.vehicle_id = @Id
         ORDER BY vr.created_at DESC
         LIMIT @Rows;
        """;

    public async Task<VehicleTabs> VehicleTabsAsync(Guid vehicleId, int rows, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(new CommandDefinition(
            VehicleTabsSql,
            new { Id = vehicleId, Rows = rows, SettledStates = SettledPaymentStates },
            cancellationToken: cancellationToken));

        return new VehicleTabs(
            await reader.ReadSingleOrDefaultAsync<TrackerRow>(),
            [.. await reader.ReadAsync<VerificationDocumentRow>()],
            [.. await reader.ReadAsync<TripRow>()],
            [.. await reader.ReadAsync<VehicleEarningsRow>()],
            [.. await reader.ReadAsync<DailyFeeRow>()],
            [.. await reader.ReadAsync<VehicleReportRow>()]);
    }

    /// <summary>
    /// The <c>fares.ride_payments.state</c> values that mean the fare was collected (R-05).
    /// </summary>
    /// <remarks>
    /// <b>C061's list, not a copy of it.</b> "Which payments count as money the platform took" is
    /// one fact, and this process already hosts the analytics read model that owns the platform's
    /// transcription of it — whose own suite compares it against fare-svc's
    /// <c>RidePaymentStates.Terminal</c>. A second literal here would be a third copy, and the one
    /// that nobody notices drifting is the one on the screen an operator reads.
    /// </remarks>
    private static readonly string[] SettledPaymentStates =
        MageRide.Analytics.Domain.AnalyticsVocabulary.SettledPaymentStates;

    /// <summary>
    /// Turns a search box into a bound <c>ILIKE</c> pattern, or null for "no filter".
    /// </summary>
    /// <remarks>
    /// Substring rather than prefix, and the two <c>LIKE</c> wildcards plus the escape character
    /// are neutralised first — so a search for <c>10%</c> means <c>10%</c> and a search for
    /// <c>%</c> is not a request for the whole table.
    /// </remarks>
    private static string? Pattern(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : $"%{value.Trim()
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal)}%";
}
