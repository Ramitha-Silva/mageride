using Dapper;
using MageRide.Iam.Domain;
using MageRide.Shared.Primitives;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary>
/// The four facts the eager-fetch payload needs that iam-svc does not own: the caller's active
/// trip on either plane, the driver's live session, today's earnings rollup, and the launch-city
/// list (AL-14, US-1.14/1.15).
/// </summary>
/// <remarks>
/// <para>
/// Reads across bounded-context lines — <c>rides.rides</c> is ride-svc's, <c>trips.sessions</c>
/// trip-state-svc's, <c>fares.driver_earnings</c> fare-svc's and <c>config.operating_cities</c>
/// content-svc's — and the same argument as <see cref="PublisherRepository"/> applies, only more
/// so. NFR-51 requires this payload to be one round trip and US-1.14 requires it to restore a
/// mid-trip device switch; four synchronous HTTP calls would make a login fail whenever any of
/// four services is redeploying, on the one request a user cannot proceed without. Read-only,
/// one statement each, no writes — the outbox rule is about state changes, and nothing here
/// changes anything.
/// </para>
/// <para>
/// Nothing here is unbounded. Every query returns at most one row except the city list, which is
/// the three-row (today) admin-managed launch set (US-1.16, NFR-51).
/// </para>
/// </remarks>
public interface IBootstrapRepository
{
    /// <summary>The caller's non-terminal Mode C ride, whichever end of it they are on.</summary>
    Task<ActiveTrip?> FindActiveRideAsync(
        NpgsqlConnection connection, Guid userId, CancellationToken cancellationToken);

    /// <summary>The caller's live Mode A/B tracking session, as its driver.</summary>
    Task<ActiveTrip?> FindActiveSessionAsync(
        NpgsqlConnection connection, Guid driverId, CancellationToken cancellationToken);

    /// <summary>Today's <c>fares.driver_earnings</c> rollup, or <see langword="null"/> if the driver has not earned today.</summary>
    Task<DriverEarnings?> FindEarningsAsync(
        NpgsqlConnection connection, Guid driverId, DateOnly businessDate, CancellationToken cancellationToken);

    /// <summary>Active launch cities in <c>sort_order</c> (AL-27).</summary>
    Task<IReadOnlyList<OperatingCity>> ActiveCitiesAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken);
}

/// <summary>A row of <c>fares.driver_earnings</c> for one Asia/Colombo business day (D-38).</summary>
/// <remarks>
/// The two money fields are <see cref="int"/> because the columns are <c>INTEGER</c> and Dapper
/// matches a record's constructor against the column types exactly — widening them to
/// <see cref="long"/> here fails materialisation rather than converting. They widen into
/// <c>Money.AmountMinor</c> at the call site instead.
/// </remarks>
public sealed record DriverEarnings(int Trips, int GrossMinor, int DailyFeeMinor, string Currency);

/// <inheritdoc cref="IBootstrapRepository"/>
public sealed class BootstrapRepository : IBootstrapRepository
{
    /// <summary>
    /// The terminal set of D5' §6, spelled exactly as <c>ux_rides_open_passenger</c> spells it
    /// (C004). Kept as SQL rather than a parameter list so the two read the same — including the
    /// part that surprises people, that <c>Completed</c> is in the exempt set while the ride
    /// still owes a payment.
    /// </summary>
    private const string RideTerminalStates =
        "('Completed','Paid','CashSettled','CashOnDeliveryCollected','Disputed'," +
        "'CancelledByRiderBeforeAccept','CancelledByRiderAfterAccept','CancelledByDriver'," +
        "'ExpiredNoDriver','NoShowRider','NoShowDriver')";

    public Task<ActiveTrip?> FindActiveRideAsync(
        NpgsqlConnection connection, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Four ways to be on a ride: its rider, its booker (proxy, P-01), the named registered
        // rider of somebody else's proxy booking, or the driver who accepted it. `role` is
        // computed here rather than inferred by the client, because a proxy booker watching a
        // ride they are not travelling on is a passenger-shaped screen and a driver's is not.
        //
        // ORDER BY: ux_rides_driver_busy and ux_rides_open_passenger each allow one row, but a
        // person can be the driver of one ride and the booker of another at the same time. The
        // one they are driving wins — it is the one with a vehicle moving.
        return connection.QuerySingleOrDefaultAsync<ActiveTrip>(new CommandDefinition(
            $"""
             SELECT id                                                        AS trip_id,
                    'ride'                                                    AS kind,
                    CASE WHEN accepted_driver_id = @UserId THEN 'driver'
                         ELSE 'passenger' END                                 AS role,
                    state,
                    'C'                                                       AS mode,
                    accepted_vehicle_id                                       AS vehicle_id,
                    CASE WHEN accepted_driver_id = @UserId THEN passenger_id
                         ELSE accepted_driver_id END                          AS counterparty_id,
                    pickup_geo                                                AS pickup,
                    dropoff_geo                                               AS dropoff,
                    created_at                                                AS started_at
               FROM rides.rides
              WHERE state NOT IN {RideTerminalStates}
                AND (passenger_id = @UserId
                     OR booker_id = @UserId
                     OR rider_id = @UserId
                     OR accepted_driver_id = @UserId)
              ORDER BY CASE WHEN accepted_driver_id = @UserId THEN 0 ELSE 1 END, created_at DESC
              LIMIT 1;
             """,
            new { UserId = userId },
            cancellationToken: cancellationToken));
    }

    public Task<ActiveTrip?> FindActiveSessionAsync(
        NpgsqlConnection connection, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // ux_sessions_active_driver allows at most one ACTIVE row per driver (D-03, US-9.6).
        return connection.QuerySingleOrDefaultAsync<ActiveTrip>(new CommandDefinition(
            """
            SELECT id           AS trip_id,
                   'session'    AS kind,
                   'driver'     AS role,
                   state,
                   mode,
                   vehicle_id,
                   NULL::uuid   AS counterparty_id,
                   NULL::geography AS pickup,
                   destination_geo AS dropoff,
                   started_at
              FROM trips.sessions
             WHERE driver_id = @DriverId AND state = 'ACTIVE'
             LIMIT 1;
            """,
            new { DriverId = driverId },
            cancellationToken: cancellationToken));
    }

    public Task<DriverEarnings?> FindEarningsAsync(
        NpgsqlConnection connection, Guid driverId, DateOnly businessDate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<DriverEarnings>(new CommandDefinition(
            """
            SELECT trips, gross_minor, daily_fee_minor, currency
              FROM fares.driver_earnings
             WHERE driver_id = @DriverId AND earn_date = @EarnDate;
            """,
            new { DriverId = driverId, EarnDate = businessDate },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<OperatingCity>> ActiveCitiesAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<OperatingCity>(new CommandDefinition(
            """
            SELECT code, name_en, name_si, name_ta, centroid_lat, centroid_lng, sort_order
              FROM config.operating_cities
             WHERE is_active
             ORDER BY sort_order, code;
            """,
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}
