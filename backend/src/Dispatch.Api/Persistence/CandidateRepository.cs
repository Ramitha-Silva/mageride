using System.Text.Json;
using Dapper;
using MageRide.Dispatch.Domain;
using MageRide.Shared.Http;
using MageRide.Shared.Primitives;
using Npgsql;

namespace MageRide.Dispatch.Persistence;

/// <summary>
/// The <b>exact-distance post-filter</b> — D5' §3.1's mandatory second half — the D5' §3.2 hard
/// gates that are predicates on rows Postgres already holds, and the R-11 scoring audit.
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
/// <b>Five of D5' §3.2's gates are applied here</b>, because each is a predicate on a row this
/// query is already joining: the driver is AVAILABLE, on the requested tier, has a fresh position
/// (<c>2×expectedInterval</c>), is on a vehicle registry has not auto-suspended for lapsed
/// documents (E-03), and has not been blocked by this passenger (US-12.10). The two that need
/// another service — <c>reputation.block_state</c> over gRPC (D-04) and the D-08 wallet balance —
/// are applied by <see cref="Dispatching.CandidateScorer"/> over what
/// <see cref="Eligibility.IReputationGate"/> and <see cref="Eligibility.IWalletGate"/> fetched, and
/// P-11's package-size compatibility is a pure function evaluated there too.
/// </para>
/// <para>
/// <b>The Directional Travel predicate (DT-02) is C036's</b> and is deliberately absent rather than
/// stubbed — no filter can be set until <c>POST /v1/standby/directional</c> exists, and a predicate
/// that always passes reads like a predicate that works.
/// </para>
/// </remarks>
public interface ICandidateRepository
{
    /// <summary>
    /// Narrows <paramref name="driverIds"/> to those actually within <paramref name="query"/>'s
    /// radius and past the SQL-side hard gates, nearest first.
    /// </summary>
    Task<IReadOnlyList<Candidate>> NarrowAsync(
        NpgsqlConnection connection,
        CandidateQuery query,
        IReadOnlyCollection<Guid> driverIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the R-11 audit for one evaluation round: every candidate considered, not only the
    /// winner and not only the eligible. Immutable by design (<c>dispatch.candidate_scores</c>,
    /// migration 0703).
    /// </summary>
    Task RecordScoresAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        IReadOnlyList<ScoredCandidate> scored,
        int algorithmVersion,
        CancellationToken cancellationToken);
}

/// <summary>Everything the post-filter needs about the ride it is building candidates for.</summary>
/// <param name="PassengerId">
/// The US-12.10 block is directional and per pair, so the gate needs to know whose ride this is.
/// </param>
/// <param name="MaxPositionAge">D5' §3.2's <c>2×expectedInterval</c> GPS freshness bound.</param>
public sealed record CandidateQuery(
    Guid RideId,
    Guid? PassengerId,
    GeoPoint Pickup,
    string VehicleType,
    int RadiusM,
    TimeSpan MaxPositionAge);

/// <inheritdoc cref="ICandidateRepository"/>
public sealed class CandidateRepository : ICandidateRepository
{
    public async Task<IReadOnlyList<Candidate>> NarrowAsync(
        NpgsqlConnection connection,
        CandidateQuery query,
        IReadOnlyCollection<Guid> driverIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(driverIds);

        if (driverIds.Count == 0)
        {
            return [];
        }

        // ST_DWithin on geography is metres and uses the spheroid, so `radiusM` needs no
        // projection. `ix_presence_geo` is a partial GiST index on state = 'AVAILABLE', which is
        // why that predicate is spelled the same way here.
        //
        // The NOT EXISTS on dispatch.offers is the cascade's memory: a driver who let this ride's
        // offer lapse or declined it is not asked again in a later round (D5' §3.5 "decline/expire
        // → next eligible candidate"). It reads dispatch.offers rather than a Redis set because the
        // rounds outlive any TTL and R-10's guarantee already lives in that table.
        //
        // The join to registry.vehicles is E-03: registry-svc flips `dispatch_state` to
        // DISPATCH_SUSPENDED when a document lapses (migration 0312), and D5' §3.2 makes that a
        // hard gate at candidate-generation time — a driver already on standby when their insurance
        // expired has to stop receiving offers without being asked to go offline first.
        //
        // The NOT EXISTS on safety.blocked_drivers is US-12.10. One-directional: the passenger
        // blocked the driver, never the reverse (migration 0903).
        var rows = await connection.QueryAsync<Candidate>(new CommandDefinition(
            $"""
             SELECT p.driver_id AS DriverId,
                    p.vehicle_id AS VehicleId,
                    p.vehicle_type AS VehicleType,
                    ST_Distance(p.geo, @Pickup) AS DistanceM,
                    p.geo AS Geo
               FROM dispatch.driver_presence p
               JOIN registry.vehicles v ON v.id = p.vehicle_id
              WHERE p.driver_id = ANY(@DriverIds)
                AND p.state = '{PresenceStates.Available}'
                AND p.vehicle_type = @VehicleType
                AND p.geo IS NOT NULL
                AND p.last_seen_at >= now() - make_interval(secs => @MaxPositionAgeSeconds)
                AND ST_DWithin(p.geo, @Pickup, @RadiusM)
                AND v.dispatch_state = 'ACTIVE'
                AND NOT EXISTS (
                      SELECT 1 FROM dispatch.offers o
                       WHERE o.ride_id = @RideId AND o.driver_id = p.driver_id)
                AND NOT EXISTS (
                      SELECT 1 FROM safety.blocked_drivers b
                       WHERE b.passenger_id = @PassengerId AND b.driver_id = p.driver_id)
              ORDER BY ST_Distance(p.geo, @Pickup), p.driver_id;
             """,
            new
            {
                DriverIds = driverIds.ToArray(),
                Pickup = query.Pickup,
                query.VehicleType,
                MaxPositionAgeSeconds = query.MaxPositionAge.TotalSeconds,
                RadiusM = (double)query.RadiusM,
                query.RideId,

                // Guid.Empty rather than NULL: `b.passenger_id = NULL` is never true, so a ride
                // whose envelope carried no passenger would silently skip the block gate. An id
                // that cannot exist in iam.users makes the predicate evaluate and match nothing.
                PassengerId = query.PassengerId ?? Guid.Empty,
            },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task RecordScoresAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        IReadOnlyList<ScoredCandidate> scored,
        int algorithmVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(scored);

        if (scored.Count == 0)
        {
            return;
        }

        // One statement for the round rather than one per candidate: the audit is written on the
        // hot path inside a 15-second window, and a ten-candidate round should cost one round trip.
        // UNNEST over four arrays keeps it parameterised — no interpolation, no VALUES list built
        // from user data (AL-53's hand-written parameterised SQL, spelled out).
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dispatch.candidate_scores
              (ride_id, driver_id, score, package_size_compatible, breakdown, dispatch_algorithm_version)
            SELECT @RideId, s.driver_id, s.score, s.package_compatible, s.breakdown::jsonb, @AlgorithmVersion
              FROM unnest(@DriverIds::uuid[], @Scores::numeric[], @PackageCompatible::boolean[], @Breakdowns::text[])
                   AS s(driver_id, score, package_compatible, breakdown);
            """,
            new
            {
                RideId = rideId,
                DriverIds = scored.Select(static s => s.DriverId).ToArray(),
                Scores = scored.Select(static s => (decimal)s.Score).ToArray(),
                PackageCompatible = scored.Select(static s => s.Breakdown.PackageSizeCompatible).ToArray(),
                Breakdowns = scored
                    .Select(static s => JsonSerializer.Serialize(s.Breakdown, MageRideJson.StorageOptions))
                    .ToArray(),
                AlgorithmVersion = (short)algorithmVersion,
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}
