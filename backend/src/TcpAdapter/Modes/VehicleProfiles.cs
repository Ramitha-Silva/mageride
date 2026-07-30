using System.Collections.Concurrent;
using Dapper;
using MageRide.Shared.Persistence;
using MageRide.TcpAdapter.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.TcpAdapter.Modes;

/// <summary>
/// What the adapter needs to know about a vehicle: its mode, its type and its fleet.
/// </summary>
/// <param name="VehicleId">The vehicle.</param>
/// <param name="Mode"><c>A</c>, <c>B</c> or <c>C</c> — <c>registry.vehicles.mode</c>.</param>
/// <param name="VehicleType">The canonical type (AL-09), denormalised onto every sample.</param>
/// <param name="FleetId">The operator whose roster carries it, or null.</param>
public sealed record VehicleProfile(Guid VehicleId, string Mode, string VehicleType, Guid? FleetId)
{
    /// <summary>Mode C — on-demand ride-hailing, and the one mode T-11 gates.</summary>
    public const string ModeC = "C";

    /// <summary>Mode A — public transport. The tracker is the authoritative and only source.</summary>
    public const string ModeA = "A";

    /// <summary>Whether this vehicle is a Mode C one.</summary>
    public bool IsModeC => string.Equals(Mode, ModeC, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The read-only window onto registry-svc's schema the adapter needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>D7' §2.1 gives Container 9 no database, and this component needs one.</b> Raised as a
/// micro-change-set in the C043 handoff. Two obligations force it:
/// </para>
/// <list type="number">
/// <item><b>T-11.</b> "Tracker GPS for a Mode C vehicle is ingested only while the vehicle is Online"
/// (§7.7.7). The gate needs the vehicle's <i>mode</i>, and <c>registry.vehicles.mode</c> is the only
/// place it exists. <c>prov.tracker_bindings</c> does not carry it; <c>imei:{imei}</c> holds a vehicle
/// id and nothing else; <c>veh:meta:{vehicleId}</c> is written by position-processor-svc <i>from
/// accepted samples</i>, so reading the mode from there to decide whether to accept a sample is
/// circular and empty for exactly the tracker-only vehicles that need it.</item>
/// <item><b>The canonical sample's denormalised fields.</b> <c>mqtt-topics.md</c> §2.1 puts
/// <c>mode</c>, <c>vehicleType</c> and <c>fleetId</c> on every <c>PositionSample</c> "so a consumer
/// needs no registry lookup" — and fanout-svc's visibility rules and query-svc's live map both read
/// them. A publisher that left them null would make every tracker-sourced vehicle untyped on the map.</item>
/// </list>
/// <para>
/// <b>Reads only, and never a write.</b> Vehicle lifecycle is registry-svc's (C028/C029); the same
/// read-only cross-context window provisioning-svc opens for a bind. The alternative considered and
/// rejected was widening provisioning-svc's <c>validate</c> response with a mode — that endpoint's
/// fence is "this service only mints, binds and revokes", and a per-connect HTTP hop already exists on
/// this path, so the cost would have been a contract change to another component for data it does not
/// own either.
/// </para>
/// <para>
/// <b>One indexed lookup per device connect, not per sample.</b> The primary key is the vehicle id and
/// the result is cached for <c>Adapter:VehicleProfileTtl</c>; a tracker at 0.2 Hz would otherwise put
/// twelve queries a minute per device onto the pooler.
/// </para>
/// </remarks>
public interface IVehicleProfileRepository
{
    /// <summary>The vehicle's mode, type and fleet, or null when there is no such vehicle.</summary>
    Task<VehicleProfile?> FindAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVehicleProfileRepository"/>
public sealed class VehicleProfileRepository(INpgsqlConnectionFactory connections) : IVehicleProfileRepository
{
    public async Task<VehicleProfile?> FindAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var row = await connection.QueryFirstOrDefaultAsync<ProfileRow>(new CommandDefinition(
            """
            SELECT v.id           AS "VehicleId",
                   v.mode         AS "Mode",
                   v.vehicle_type AS "VehicleType",
                   fv.fleet_id    AS "FleetId"
              FROM registry.vehicles v
              LEFT JOIN registry.fleet_vehicles fv ON fv.vehicle_id = v.id
             WHERE v.id = @VehicleId
             LIMIT 1;
            """,
            new { VehicleId = vehicleId },
            cancellationToken: cancellationToken));

        return row is null ? null : new VehicleProfile(row.VehicleId, row.Mode, row.VehicleType, row.FleetId);
    }

    private sealed record ProfileRow(Guid VehicleId, string Mode, string VehicleType, Guid? FleetId);
}

/// <summary>
/// The profile lookup with a TTL in front of it, and a stale entry behind it.
/// </summary>
/// <remarks>
/// <b>An expired entry is kept, not evicted.</b> A vehicle's mode changes when a human re-registers
/// it; a database that is briefly unreachable changes nothing about it. So a lapsed entry is refreshed
/// when the query succeeds and served when it does not, which turns a Postgres blip into stale
/// metadata rather than into a Mode A fleet disappearing from the live map — see
/// <c>Adapter:PublishWhenModeUnknown</c> for the case where there is no entry at all.
/// </remarks>
public sealed class VehicleProfileCache(
    IVehicleProfileRepository repository,
    IOptions<AdapterOptions> options,
    TimeProvider clock,
    ILogger<VehicleProfileCache> logger)
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    private readonly AdapterOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>The profile, from cache when it is fresh and from Postgres when it is not.</summary>
    public async Task<VehicleProfile?> GetAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (_entries.TryGetValue(vehicleId, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Profile;
        }

        try
        {
            var profile = await repository.FindAsync(vehicleId, cancellationToken);

            _entries[vehicleId] = new Entry(profile, now + _options.VehicleProfileTtl);

            return profile;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            logger.LogError(
                exception,
                "Could not read vehicle {VehicleId}'s registry profile; {Fallback}",
                vehicleId,
                cached.Profile is null ? "the T-11 gate has no mode to work from" : "serving the stale entry");

            return cached.Profile;
        }
    }

    /// <summary>Drops an entry — used when a binding moves an IMEI to another vehicle.</summary>
    public void Forget(Guid vehicleId) => _entries.TryRemove(vehicleId, out _);

    private readonly record struct Entry(VehicleProfile? Profile, DateTimeOffset ExpiresAt);
}
