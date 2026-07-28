using Dapper;
using MageRide.Dispatch.Domain;
using MageRide.Shared.Primitives;
using Npgsql;

namespace MageRide.Dispatch.Persistence;

/// <summary>
/// The <b>exact-distance post-filter</b> — D5' §3.1's mandatory second half, and the R-11 scoring
/// audit that records what it decided.
/// </summary>
/// <remarks>
/// <para>
/// The H3 pre-filter (<see cref="Shared.Geo.H3Grid"/> plus the Redis GEO sets) hands this repository a
/// raw list of driver ids drawn from a <c>gridDisk(2)</c> of res-5 cells. That disk spans roughly
/// 40 km, so it is a *set of keys to read*, never a distance bound (R-06). What decides is
/// <c>ST_DWithin(geo, pickup, radius)</c> on <c>dispatch.driver_presence</c>, exactly as ADD §6
/// prescribes, plus the hard gates a Redis set cannot express.
/// </para>
/// <para>
/// <b>The gates applied here</b> are only those in this slice's scope: the driver is AVAILABLE, on
/// the requested tier, has a fresh position (D5' §3.2's GPS-freshness rule, since the durable row
/// has no TTL of its own), and has not already had this ride offered to them. The wallet/daily-fee
/// gate (D-08), Driver Level, <c>reputation.block_state</c> (D-04), <c>safety.blocked_drivers</c>
/// (US-12.10), package-size compatibility (P-11), <c>DISPATCH_SUSPENDED</c> (E-03) and the
/// Directional predicate (DT-02) are <b>C034/C036</b> and are deliberately absent rather than
/// stubbed — a gate that always passes reads like a gate that works.
/// </para>
/// </remarks>
public interface ICandidateRepository
{
    /// <summary>
    /// Narrows <paramref name="driverIds"/> to those actually within <paramref name="radiusM"/> of
    /// <paramref name="pickup"/>, nearest first.
    /// </summary>
    Task<IReadOnlyList<Candidate>> NarrowAsync(
        NpgsqlConnection connection,
        Guid rideId,
        IReadOnlyCollection<Guid> driverIds,
        GeoPoint pickup,
        string vehicleType,
        int radiusM,
        TimeSpan maxPositionAge,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the R-11 audit for one evaluation round: every candidate considered, not only the
    /// winner. Immutable by design (<c>dispatch.candidate_scores</c>, migration 0703).
    /// </summary>
    Task RecordScoresAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        IReadOnlyList<Candidate> ranked,
        int algorithmVersion,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICandidateRepository"/>
public sealed class CandidateRepository : ICandidateRepository
{
    public async Task<IReadOnlyList<Candidate>> NarrowAsync(
        NpgsqlConnection connection,
        Guid rideId,
        IReadOnlyCollection<Guid> driverIds,
        GeoPoint pickup,
        string vehicleType,
        int radiusM,
        TimeSpan maxPositionAge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(driverIds);

        if (driverIds.Count == 0)
        {
            return [];
        }

        // ST_DWithin on geography is metres and uses the spheroid, so `radiusM` needs no
        // projection. `ix_presence_geo` is a partial GiST index on state = 'AVAILABLE', which is
        // why that predicate is spelled the same way here.
        //
        // The NOT EXISTS is the cascade's memory: a driver who let this ride's offer lapse or
        // declined it is not asked again in a later round (D5' §3.5 "decline/expire → next
        // eligible candidate"). It reads dispatch.offers rather than a Redis set because the
        // rounds outlive any TTL and R-10's guarantee already lives in that table.
        var rows = await connection.QueryAsync<Candidate>(new CommandDefinition(
            $"""
             SELECT p.driver_id AS DriverId,
                    p.vehicle_id AS VehicleId,
                    p.vehicle_type AS VehicleType,
                    ST_Distance(p.geo, @Pickup) AS DistanceM,
                    p.geo AS Geo
               FROM dispatch.driver_presence p
              WHERE p.driver_id = ANY(@DriverIds)
                AND p.state = '{PresenceStates.Available}'
                AND p.vehicle_type = @VehicleType
                AND p.geo IS NOT NULL
                AND p.last_seen_at >= now() - make_interval(secs => @MaxPositionAgeSeconds)
                AND ST_DWithin(p.geo, @Pickup, @RadiusM)
                AND NOT EXISTS (
                      SELECT 1 FROM dispatch.offers o
                       WHERE o.ride_id = @RideId AND o.driver_id = p.driver_id)
              ORDER BY ST_Distance(p.geo, @Pickup), p.driver_id;
             """,
            new
            {
                DriverIds = driverIds.ToArray(),
                Pickup = pickup,
                VehicleType = vehicleType,
                MaxPositionAgeSeconds = maxPositionAge.TotalSeconds,
                RadiusM = (double)radiusM,
                RideId = rideId,
            },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task RecordScoresAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        IReadOnlyList<Candidate> ranked,
        int algorithmVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(ranked);

        for (var rank = 0; rank < ranked.Count; rank++)
        {
            var candidate = ranked[rank];

            // `score` is a pure proximity score in (0,1] — nearest is 1. It is NOT the D5' §3.3
            // weighted formula and does not pretend to be: `dispatch_algorithm_version` is 0 for
            // exactly that reason, and the breakdown names the algorithm so a later audit of a
            // version-0 decision is not misread as a scoring bug. C034 lands version 1.
            var score = 1d / (1d + (candidate.DistanceM / 1000d));

            var breakdown =
                $$"""
                  {"algorithm":"nearest-only","distanceM":{{candidate.DistanceM.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}},"rank":{{rank}}}
                  """;

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO dispatch.candidate_scores
                  (ride_id, driver_id, score, breakdown, dispatch_algorithm_version)
                VALUES (@RideId, @DriverId, @Score, @Breakdown::jsonb, @AlgorithmVersion);
                """,
                new
                {
                    RideId = rideId,
                    candidate.DriverId,
                    Score = score,
                    Breakdown = breakdown,
                    AlgorithmVersion = (short)algorithmVersion,
                },
                transaction,
                cancellationToken: cancellationToken));
        }
    }
}
