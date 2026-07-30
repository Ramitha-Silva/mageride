using Dapper;
using MageRide.HotPath.PersistenceWriter.Configuration;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.HotPath.PersistenceWriter.Sampling;

/// <summary>A vehicle's live Mode A/B tracking session, as the downsample needs it.</summary>
/// <param name="SessionId">The session the samples belong to.</param>
/// <param name="StartedAt">
/// When it started. A fix captured before this belongs to no journey — a vehicle publishing at the
/// depot before the driver pressed Start Journey — and attributing one would put the depot in the
/// trip's polyline.
/// </param>
public sealed record ActiveSession(Guid SessionId, DateTimeOffset StartedAt);

/// <summary>A vehicle's owning fleet.</summary>
internal sealed record VehicleFleet(Guid FleetId);

/// <summary>
/// The two facts about a vehicle the write path needs and a position sample does not carry.
/// </summary>
public interface IVehicleContextResolver
{
    /// <summary>
    /// The fleet each of <paramref name="vehicleIds"/> belongs to, for the vehicles that belong to
    /// one. Absent from the result means "no fleet", which is the common case.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> ResolveFleetsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<Guid> vehicleIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// The live tracking session for each of <paramref name="vehicleIds"/> that has one. Absent
    /// means the vehicle is not on a Mode A/B journey — every Mode C vehicle, all day (R-01).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ActiveSession>> ResolveSessionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<Guid> vehicleIds,
        CancellationToken cancellationToken);

    /// <summary>Forgets what is cached about a vehicle. Used when a session ends.</summary>
    void Forget(Guid vehicleId);
}

/// <inheritdoc cref="IVehicleContextResolver"/>
/// <remarks>
/// <para>
/// <b>One query per batch, not per row.</b> Both lookups take the batch's distinct unknown vehicle
/// ids and resolve them with a single <c>= ANY</c> against an index — <c>ix_fleet_vehicles_vehicle</c>
/// (0306) and <c>ux_sessions_active_driver</c>'s sibling <c>ix_sessions_vehicle</c> (0501). A batch of
/// a thousand samples from a hundred vehicles costs two round trips on a cold cache and none on a
/// warm one, against ADD §9.5's 40k rows/s target.
/// </para>
/// <para>
/// <b>Both caches cache their misses</b> — see <see cref="VehicleLookupCache{T}"/>. The session TTL
/// is short (30 s) because a journey that just started must be picked up promptly; the fleet TTL is
/// long (10 minutes) because fleet membership is an admin action measured in months.
/// </para>
/// <para>
/// <b>Reads run inside the caller's transaction.</b> The resolver is handed the connection the
/// <c>COPY</c> is running on, so the session it reads and the samples it attributes to that session
/// are one consistent snapshot — a session that ends mid-flush cannot have half its minute buckets
/// attributed and half dropped.
/// </para>
/// </remarks>
public sealed class VehicleContextResolver : IVehicleContextResolver
{
    private const string FleetSql =
        """
        SELECT vehicle_id AS VehicleId, fleet_id AS FleetId
          FROM registry.fleet_vehicles
         WHERE vehicle_id = ANY(@VehicleIds);
        """;

    /// <summary>
    /// The vehicle's live session. <c>state = 'ACTIVE'</c> is the only predicate: a vehicle has at
    /// most one, enforced for the driver by <c>ux_sessions_active_driver</c> and for the vehicle by
    /// trip-state-svc refusing a second start. <c>DISTINCT ON</c> is belt and braces — a duplicate
    /// here would otherwise make one arbitrary sample's session the whole batch's.
    /// </summary>
    private const string SessionSql =
        """
        SELECT DISTINCT ON (vehicle_id)
               vehicle_id AS VehicleId, id AS SessionId, started_at AS StartedAt
          FROM trips.sessions
         WHERE vehicle_id = ANY(@VehicleIds)
           AND state = 'ACTIVE'
         ORDER BY vehicle_id, started_at DESC;
        """;

    private readonly VehicleLookupCache<VehicleFleet> _fleets;
    private readonly VehicleLookupCache<ActiveSession> _sessions;

    public VehicleContextResolver(IOptions<PersistenceWriterOptions> options, TimeProvider clock)
    {
        var settings = options?.Value ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(clock);

        _fleets = new VehicleLookupCache<VehicleFleet>(
            settings.FleetCacheTtl, settings.LookupCacheCapacity, clock);

        _sessions = new VehicleLookupCache<ActiveSession>(
            settings.SessionCacheTtl, settings.LookupCacheCapacity, clock);
    }

    /// <summary>Entries held by the two caches. Diagnostics and tests.</summary>
    internal (int Fleets, int Sessions) CacheSize => (_fleets.Count, _sessions.Count);

    public Task<IReadOnlyDictionary<Guid, Guid>> ResolveFleetsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<Guid> vehicleIds,
        CancellationToken cancellationToken) =>
        ResolveAsync(
            vehicleIds,
            _fleets,
            static fleet => fleet.FleetId,
            async unknown =>
            {
                var rows = await connection.QueryAsync<FleetRow>(
                    new CommandDefinition(
                        FleetSql, new { VehicleIds = unknown }, transaction,
                        cancellationToken: cancellationToken));

                return rows.Select(row => (row.VehicleId, new VehicleFleet(row.FleetId))).ToList();
            },
            cancellationToken);

    public Task<IReadOnlyDictionary<Guid, ActiveSession>> ResolveSessionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<Guid> vehicleIds,
        CancellationToken cancellationToken) =>
        ResolveAsync(
            vehicleIds,
            _sessions,
            static session => session,
            async unknown =>
            {
                var rows = await connection.QueryAsync<SessionRow>(
                    new CommandDefinition(
                        SessionSql, new { VehicleIds = unknown }, transaction,
                        cancellationToken: cancellationToken));

                return rows
                    .Select(row => (row.VehicleId, new ActiveSession(row.SessionId, row.StartedAt)))
                    .ToList();
            },
            cancellationToken);

    public void Forget(Guid vehicleId)
    {
        _fleets.Invalidate(vehicleId);
        _sessions.Invalidate(vehicleId);
    }

    /// <summary>
    /// Cache-then-query, with the negatives cached. The query is a delegate so each lookup keeps its
    /// own row shape — one shared Dapper type spanning both would have to carry columns neither
    /// query selects.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, TResult>> ResolveAsync<T, TResult>(
        IReadOnlyCollection<Guid> vehicleIds,
        VehicleLookupCache<T> cache,
        Func<T, TResult> project,
        Func<List<Guid>, Task<List<(Guid VehicleId, T Value)>>> query,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = new Dictionary<Guid, TResult>(vehicleIds.Count);
        List<Guid>? unknown = null;

        foreach (var vehicleId in vehicleIds)
        {
            if (cache.TryGet(vehicleId, out var cached))
            {
                if (cached is not null)
                {
                    resolved[vehicleId] = project(cached);
                }

                continue;
            }

            (unknown ??= []).Add(vehicleId);
        }

        if (unknown is null)
        {
            return resolved;
        }

        var found = new HashSet<Guid>();

        foreach (var (vehicleId, value) in await query(unknown))
        {
            cache.Set(vehicleId, value);
            resolved[vehicleId] = project(value);
            found.Add(vehicleId);
        }

        foreach (var vehicleId in unknown)
        {
            if (!found.Contains(vehicleId))
            {
                // The negative, cached. This is the common answer for both lookups and the reason
                // the write path does not issue a query per vehicle per batch.
                cache.Set(vehicleId, null);
            }
        }

        return resolved;
    }

    private sealed record FleetRow(Guid VehicleId, Guid FleetId);

    private sealed record SessionRow(Guid VehicleId, Guid SessionId, DateTimeOffset StartedAt);
}
