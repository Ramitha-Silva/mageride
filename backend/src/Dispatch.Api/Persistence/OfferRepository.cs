using Dapper;
using MageRide.Dispatch.Domain;
using Npgsql;

namespace MageRide.Dispatch.Persistence;

/// <summary>
/// <c>dispatch.offers</c> (migration 0702) — the authoritative half of the R-10 reservation pair.
/// </summary>
/// <remarks>
/// The Redis Lua <c>SET lock:driver-offer:{driverId} NX PX 15000</c> is the fast path so a driver
/// app never sees a phantom offer; <b>this table's <c>ux_offers_driver_live</c> partial unique
/// index is the guarantee that survives a Redis partition or flush</b> (ADD §11.11, "Why both").
/// Neither alone is sufficient, so the insert here deliberately does not carry an
/// <c>ON CONFLICT DO NOTHING</c> — the 23505 is the answer, and swallowing it would turn "this
/// driver already holds a live offer" into "no offer was created, reason unknown".
/// </remarks>
public interface IOfferRepository
{
    /// <summary>
    /// Reserves the driver. Returns <see langword="false"/> when <c>ux_offers_driver_live</c>
    /// rejected the row, which means this driver is already holding an OFFERED or ACCEPTED offer.
    /// </summary>
    Task<bool> TryInsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid offerId,
        Guid rideId,
        Guid driverId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    /// <summary>Realigns the mirror to ride-svc's authoritative deadline once the offer is armed.</summary>
    Task SetExpiryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid offerId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Settles an OFFERED row. Conditional on the current status, so a decline that races an
    /// expiry produces one winner and one no-op rather than two conflicting histories.
    /// </summary>
    Task<bool> TrySettleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid offerId,
        string toStatus,
        CancellationToken cancellationToken);

    /// <summary>The live offer on a ride, if it has one.</summary>
    Task<OfferRow?> FindLiveForRideAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid rideId, CancellationToken cancellationToken);

    /// <summary>
    /// The live offer a driver is holding, if any. At most one row can match — that is exactly what
    /// <c>ux_offers_driver_live</c> guarantees — so this needs no <c>LIMIT</c> and no tie-break.
    /// </summary>
    Task<OfferRow?> FindLiveForDriverAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);

    Task<OfferRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid offerId, CancellationToken cancellationToken);

    /// <summary>How many offers this ride has already been through — the cascade bound.</summary>
    Task<int> CountForRideAsync(NpgsqlConnection connection, Guid rideId, CancellationToken cancellationToken);

    /// <summary>Settles whichever offer a driver holds. Used when a ride ends without one.</summary>
    Task<OfferRow?> SettleDriversLiveOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        string toStatus,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stamps <c>released_at</c> on the driver's ACCEPTED offer — the ride is over and they are no
    /// longer on the hook for it (migration 0712).
    /// </summary>
    /// <remarks>
    /// Without this the partial unique index would treat a finished ride as a live offer for ever
    /// and refuse the driver's second ride, and every one after it. The status is deliberately left
    /// at ACCEPTED: it is what the driver did, and DECLINED or EXPIRED would make the audit lie.
    /// </remarks>
    /// <returns>
    /// The offer that was released, so the caller can drop its Redis reservation as well — ADD
    /// §11.12 makes both dispatch-svc's job on a terminal event, and the lock names the ride and
    /// the offer it belongs to.
    /// </returns>
    Task<OfferRow?> ReleaseAcceptedAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IOfferRepository"/>
public sealed class OfferRepository : IOfferRepository
{
    /// <summary>Postgres reports every unique-index breach as 23505.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>R-10: one OFFERED-or-ACCEPTED offer per driver (migration 0702).</summary>
    internal const string DriverLiveOfferIndex = "ux_offers_driver_live";

    private const string Columns = "id, ride_id, driver_id, status, sent_at, expires_at, responded_at";

    private static readonly string[] LiveStatuses = [.. OfferStatuses.Live];

    public async Task<bool> TryInsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid offerId,
        Guid rideId,
        Guid driverId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 INSERT INTO dispatch.offers (id, ride_id, driver_id, status, sent_at, expires_at)
                 VALUES (@OfferId, @RideId, @DriverId, '{OfferStatuses.Offered}', now(), @ExpiresAt);
                 """,
                new { OfferId = offerId, RideId = rideId, DriverId = driverId, ExpiresAt = expiresAt },
                transaction,
                cancellationToken: cancellationToken));

            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation && ex.ConstraintName == DriverLiveOfferIndex)
        {
            return false;
        }
    }

    public Task SetExpiryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid offerId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE dispatch.offers SET expires_at = @ExpiresAt
              WHERE id = @OfferId AND status = '{OfferStatuses.Offered}';
             """,
            new { OfferId = offerId, ExpiresAt = expiresAt },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> TrySettleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid offerId,
        string toStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE dispatch.offers
                SET status = @ToStatus, responded_at = now()
              WHERE id = @OfferId AND status = '{OfferStatuses.Offered}';
             """,
            new { OfferId = offerId, ToStatus = toStatus },
            transaction,
            cancellationToken: cancellationToken));

        return affected == 1;
    }

    public Task<OfferRow?> FindLiveForRideAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid rideId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<OfferRow>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM dispatch.offers
              WHERE ride_id = @RideId AND status = '{OfferStatuses.Offered}'
              ORDER BY sent_at DESC
              LIMIT 1;
             """,
            new { RideId = rideId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<OfferRow?> FindLiveForDriverAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<OfferRow>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM dispatch.offers
              WHERE driver_id = @DriverId AND status = ANY(@LiveStatuses) AND released_at IS NULL;
             """,
            new { DriverId = driverId, LiveStatuses },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<OfferRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid offerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<OfferRow>(new CommandDefinition(
            $"SELECT {Columns} FROM dispatch.offers WHERE id = @OfferId;",
            new { OfferId = offerId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<int> CountForRideAsync(NpgsqlConnection connection, Guid rideId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM dispatch.offers WHERE ride_id = @RideId;",
            new { RideId = rideId },
            cancellationToken: cancellationToken));
    }

    public Task<OfferRow?> SettleDriversLiveOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        string toStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // At most one row can match — that is exactly what ux_offers_driver_live guarantees — so
        // this needs no LIMIT and cannot settle an offer that belongs to a different round. The
        // `released_at IS NULL` predicate is the index's own (migration 0712), spelled the same way
        // here so a driver's finished rides are never candidates for settlement.
        return connection.QuerySingleOrDefaultAsync<OfferRow>(new CommandDefinition(
            $"""
             UPDATE dispatch.offers
                SET status = @ToStatus, responded_at = now()
              WHERE driver_id = @DriverId AND status = ANY(@LiveStatuses) AND released_at IS NULL
             RETURNING {Columns};
             """,
            new { DriverId = driverId, ToStatus = toStatus, LiveStatuses },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<OfferRow?> ReleaseAcceptedAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<OfferRow>(new CommandDefinition(
            $"""
             UPDATE dispatch.offers
                SET released_at = now()
              WHERE driver_id = @DriverId
                AND status = '{OfferStatuses.Accepted}'
                AND released_at IS NULL
             RETURNING {Columns};
             """,
            new { DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));
    }
}
