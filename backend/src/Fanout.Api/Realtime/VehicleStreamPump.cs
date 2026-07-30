using System.Collections.Concurrent;
using MageRide.Fanout.Configuration;
using MageRide.Fanout.Rides;
using MageRide.Fanout.Visibility;
using MageRide.Shared.Observability;
using MageRide.Shared.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.Fanout.Realtime;

/// <summary>
/// Serves the two audiences that follow a <i>vehicle</i> rather than a place: the entitled Mode B
/// watchers and the driver's own home map on <c>vehicle:{vehicleId}</c> (D-23, AL-31), and the
/// people on a ride on <c>ride:{rideId}</c> (US-6A.12, US-7.16).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are not served from the cell streams.</b> A cell stream only reaches a replica with
/// a subscriber in that cell, and none of these audiences is subscribed to a place. A passenger on a
/// ride is watching one car, and a long ride leaves the nineteen cells their app joined within a few
/// minutes; a Mode B watcher follows a school van across a city; a driver's home map has no map
/// square at all. Driving any of them from cell membership would make the position stop for reasons
/// the user cannot see.
/// </para>
/// <para>
/// <b>The cost is per subscribed vehicle, not per vehicle.</b> This replica reads exactly the
/// vehicles behind the groups it holds — a handful of grants per passenger and one car per ride —
/// so the same O(interested) shape as ADD §7.4's cell model, one level finer.
/// </para>
/// <para>
/// <b>US-7.16's second half lives here.</b> An engaged Mode C vehicle is absent from every public
/// group and its position reaches exactly one audience: <c>DriverPosition</c> to the ride. The first
/// half — keeping it off the public groups — is <see cref="CellStreamPump"/>'s.
/// </para>
/// </remarks>
internal sealed class VehicleStreamPump(
    IHubConnections connections,
    IVehicleSnapshotReader snapshots,
    IVisibilityIndex visibility,
    IRideProjection rides,
    IHubContext<LiveHub> hub,
    IOptions<FanoutOptions> options,
    TimeProvider clock,
    ILogger<VehicleStreamPump> logger) : BackgroundService
{
    private readonly FanoutOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// vehicle → the sample instant last pushed for it, so an unchanged position is not resent.
    /// </summary>
    /// <remarks>
    /// A stationary vehicle would otherwise produce one message per tick per watcher for as long as
    /// it is parked. The cell path gets this for free — a stream that gains no entries yields no
    /// batch — and a hash read does not, because the hash is always there.
    /// </remarks>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastSent = new();

    /// <summary>Vehicles currently withheld from their own group, so the removal is sent once.</summary>
    private readonly ConcurrentDictionary<Guid, string> _withheld = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var ticker = new PeriodicTimer(_options.BatchInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A vehicle fan-out tick failed; continuing");
            }

            try
            {
                if (!await ticker.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass over this replica's vehicle and ride groups. Internal so a test can step it.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await PushVehiclesAsync(now, cancellationToken);
        await PushRidesAsync(now, cancellationToken);

        Forget();
    }

    /// <summary>The Mode B entitled stream and the driver's own vehicle (D-23, AL-31).</summary>
    private async Task PushVehiclesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var watched = connections.WatchedVehicles;

        if (watched.Count == 0)
        {
            return;
        }

        var positions = await snapshots.ReadAsync(watched, cancellationToken);
        var states = await visibility.ReadAsync(watched, cancellationToken);

        foreach (var vehicleId in watched)
        {
            if (!positions.TryGetValue(vehicleId, out var snapshot))
            {
                continue;
            }

            var state = states.TryGetValue(vehicleId, out var known) ? known : VehicleState.Unknown;
            var verdict = VehicleVisibilityRules.Classify(
                snapshot.Frame.Mode, snapshot.SampleTs, state, now, _options.FreshnessWindow);

            // Staleness and the last will apply to a private stream exactly as they do to the public
            // one (US-7.17 says "removed from the map", not "removed from the public map"). The
            // engagement rule does not: a Mode C vehicle on hire is still the driver's own vehicle,
            // and AL-31's home map is the one place it should still be drawn.
            if (verdict.Audience == VehicleAudience.None)
            {
                await WithholdAsync(vehicleId, verdict.RemovalReason, cancellationToken);
                continue;
            }

            _withheld.TryRemove(vehicleId, out _);

            // A hash that has not been rewritten is the same position as last tick. Sending it again
            // costs every watcher a message and moves no marker.
            if (snapshot.SampleTs is { } stamped
                && _lastSent.TryGetValue(vehicleId, out var previous)
                && previous >= stamped)
            {
                continue;
            }

            if (snapshot.SampleTs is { } sent)
            {
                _lastSent[vehicleId] = sent;
            }

            await hub.Clients
                .Group(Contract.VehicleGroup(vehicleId))
                .SendAsync(Contract.Events.VehiclePositions, new[] { snapshot.Frame }, cancellationToken);

            MageRideDiagnostics.FanoutFramesSent.Add(1);
        }
    }

    /// <summary>The assigned ride's live driver position (US-6A.12, <c>signalr-hub.md</c> §3).</summary>
    private async Task PushRidesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var watched = connections.WatchedRides;

        if (watched.Count == 0)
        {
            return;
        }

        // ride → vehicle, resolved per tick rather than cached: a ride's vehicle changes when an
        // offer is re-placed after a driver goes unreachable (R-15), and a cached mapping would
        // stream the wrong car's position to the passenger.
        var vehicles = new Dictionary<Guid, Guid>(watched.Count);

        foreach (var rideId in watched)
        {
            if (await rides.ReadAsync(rideId, cancellationToken) is { VehicleId: { } vehicleId })
            {
                vehicles[rideId] = vehicleId;
            }
        }

        if (vehicles.Count == 0)
        {
            return;
        }

        var positions = await snapshots.ReadAsync([.. vehicles.Values.Distinct()], cancellationToken);

        foreach (var (rideId, vehicleId) in vehicles)
        {
            if (!positions.TryGetValue(vehicleId, out var snapshot))
            {
                continue;
            }

            // Freshness only. A ride's own vehicle is shown to the ride whatever its mode says and
            // whether or not it is engaged — being engaged on *this* ride is the reason these people
            // are watching it.
            if (snapshot.SampleTs is { } stamped && now - stamped > _options.FreshnessWindow)
            {
                continue;
            }

            await hub.Clients
                .Group(Contract.RideGroup(rideId))
                .SendAsync(
                    Contract.Events.DriverPosition,
                    new DriverPositionEvent(rideId, snapshot.Frame.Lat, snapshot.Frame.Lng, snapshot.Frame.Heading),
                    cancellationToken);
        }
    }

    private async Task WithholdAsync(Guid vehicleId, string? reason, CancellationToken cancellationToken)
    {
        var removal = reason ?? VehicleRemovalReasons.Stale;

        // Once per transition, not once per tick: a vehicle offline for an hour would otherwise send
        // its watchers eighteen hundred removals for a marker they dropped after the first.
        if (!_withheld.TryAdd(vehicleId, removal))
        {
            return;
        }

        MageRideDiagnostics.FanoutFramesFiltered.Add(1, new KeyValuePair<string, object?>("reason", removal));

        await hub.Clients
            .Group(Contract.VehicleGroup(vehicleId))
            .SendAsync(
                Contract.Events.VehicleRemoved, new VehicleRemovedEvent(vehicleId, removal), cancellationToken);
    }

    /// <summary>Drops the per-vehicle memory of vehicles nobody on this replica watches any more.</summary>
    private void Forget()
    {
        var watched = new HashSet<Guid>(connections.WatchedVehicles);

        foreach (var vehicleId in _lastSent.Keys)
        {
            if (!watched.Contains(vehicleId))
            {
                _lastSent.TryRemove(vehicleId, out _);
                _withheld.TryRemove(vehicleId, out _);
            }
        }
    }
}
