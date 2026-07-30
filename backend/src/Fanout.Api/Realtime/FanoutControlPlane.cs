using System.Text.Json;
using MageRide.Fanout.Configuration;
using MageRide.Shared.Caching;
using MageRide.Shared.Http;
using MageRide.Shared.Observability;
using MageRide.Shared.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.Fanout.Realtime;

/// <summary>
/// Publishes a directed send to every replica (D6' §5, "Redis backplane (MVP)").
/// </summary>
public interface IFanoutControlPlane
{
    /// <summary>Broadcasts <paramref name="signal"/>. Applied here too — the publisher is a subscriber.</summary>
    Task PublishAsync(FanoutSignal signal, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFanoutControlPlane"/>
/// <remarks>
/// A plain Redis <c>PUBLISH</c>, not <c>AddStackExchangeRedis()</c>. SignalR's own backplane is a
/// property of the <c>HubLifetimeManager</c> and therefore applies to <em>every</em> group send this
/// process makes — including the per-cell position batches, which every replica already produces
/// independently. Turning it on would multiply each batch by the replica count, and the symptom (a
/// map that works, with markers stuttering) hides the cause. The control channel carries the sends
/// that genuinely have to cross replicas and nothing else.
/// </remarks>
public sealed class RedisFanoutControlPlane(
    IConnectionMultiplexer redis,
    IOptions<FanoutOptions> options,
    TimeProvider clock,
    FanoutSignalApplier applier,
    ILogger<RedisFanoutControlPlane> logger) : IFanoutControlPlane
{
    private readonly FanoutOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task PublishAsync(FanoutSignal signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var stamped = signal with { IssuedAt = signal.IssuedAt ?? clock.GetUtcNow() };

        if (!_options.ControlPlaneEnabled)
        {
            // A single-replica deployment, or a test that wants no broker in the loop. The send
            // still has to happen — it is applied here rather than everywhere.
            await applier.ApplyAsync(stamped, cancellationToken);
            return;
        }

        try
        {
            await redis.GetSubscriber().PublishAsync(
                RedisChannel.Literal(RedisKeys.FanoutControlChannel),
                JsonSerializer.Serialize(stamped, MageRideJson.StorageOptions));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Applying locally is strictly better than losing the send: the connection this signal
            // is addressed to is as likely to be here as anywhere, and the alternative is a
            // passenger who keeps seeing a vehicle whose grant was revoked.
            logger.LogError(exception, "Could not publish a {Kind} signal; applying it locally only", signal.Kind);

            await applier.ApplyAsync(stamped, cancellationToken);
        }
    }
}

/// <summary>
/// Holds the <c>fanout:control</c> subscription and hands each signal to
/// <see cref="FanoutSignalApplier"/>.
/// </summary>
/// <remarks>
/// A <see cref="BackgroundService"/> rather than a subscription taken in the composition root, so
/// its lifetime is the host's and a shutdown unsubscribes rather than leaving a callback running
/// against a disposed container.
/// </remarks>
internal sealed class FanoutControlSubscriber(
    IConnectionMultiplexer redis,
    FanoutSignalApplier applier,
    ILogger<FanoutControlSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var channel = RedisChannel.Literal(RedisKeys.FanoutControlChannel);
        var subscriber = redis.GetSubscriber();
        var queue = await subscriber.SubscribeAsync(channel);

        logger.LogInformation("Fan-out control channel {Channel} subscribed (D6' §5)", RedisKeys.FanoutControlChannel);

        // The sequential form of OnMessage: two signals about one passenger must not overtake each
        // other, and a granted-then-revoked pair applied out of order leaves visibility that was
        // taken away.
        queue.OnMessage(async message =>
        {
            try
            {
                var signal = JsonSerializer.Deserialize<FanoutSignal>(
                    message.Message.ToString(), MageRideJson.StorageOptions);

                if (signal is not null)
                {
                    await applier.ApplyAsync(signal, stoppingToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Could not apply a fan-out control signal");
            }
        });

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            await subscriber.UnsubscribeAsync(channel);
        }
    }
}

/// <summary>
/// Applies a signal to <b>this replica's own connections</b> and to nobody else's.
/// </summary>
/// <remarks>
/// Every replica runs this against the same broadcast and their connection sets are disjoint, so
/// each client is served exactly once. That is the whole reason a custom channel is used instead of
/// SignalR's backplane: here the send is local by construction.
/// </remarks>
public sealed class FanoutSignalApplier(
    IHubContext<LiveHub> hub,
    IHubConnections connections,
    TimeProvider clock,
    ILogger<FanoutSignalApplier> logger)
{
    public async Task ApplyAsync(FanoutSignal signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);

        switch (signal.Kind)
        {
            case FanoutSignalKinds.ShareRevoked:
                await RevokeAsync(signal, cancellationToken);
                break;

            case FanoutSignalKinds.ShareGranted:
                await GrantAsync(signal, cancellationToken);
                break;

            case FanoutSignalKinds.RideStateChanged when signal.RideState is { } state:
                await hub.Clients
                    .Group(Contract.RideGroup(state.RideId))
                    .SendAsync(Contract.Events.RideStateChanged, state, cancellationToken);
                break;

            case FanoutSignalKinds.LocationRequestResolved
                when signal.LocationRequest is { } resolved && signal.BookerId is { } bookerId:
                await hub.Clients
                    .Group(Contract.BookerLocationRequestGroup(bookerId, resolved.RequestId))
                    .SendAsync(Contract.Events.LocationRequestResolved, resolved, cancellationToken);
                break;

            case FanoutSignalKinds.PackageStatus when signal.Package is { } package:
                await hub.Clients
                    .Group(Contract.RideGroup(package.RideId))
                    .SendAsync(Contract.Events.PackageStatus, package, cancellationToken);
                break;

            default:
                logger.LogWarning("Ignoring an unusable fan-out signal of kind {Kind}", signal.Kind);
                return;
        }

        MageRideDiagnostics.FanoutSignalsApplied.Add(1, new KeyValuePair<string, object?>("kind", signal.Kind));
    }

    /// <summary>D-22: the passenger leaves the vehicle's stream and is told, in under 200 ms.</summary>
    private async Task RevokeAsync(FanoutSignal signal, CancellationToken cancellationToken)
    {
        if (signal.UserId is not { } userId || signal.VehicleId is not { } vehicleId)
        {
            return;
        }

        var group = Contract.VehicleGroup(vehicleId);
        var affected = connections.ConnectionsOf(userId);

        foreach (var connectionId in affected)
        {
            // The group removal is what stops the frames; the event is what makes the client drop a
            // marker that would otherwise sit at its last position looking live. Removing first
            // means no batch can arrive between the two and put the marker straight back.
            await hub.Groups.RemoveFromGroupAsync(connectionId, group, cancellationToken);
            connections.LeaveVehicle(connectionId, vehicleId);
        }

        if (affected.Count > 0)
        {
            await hub.Clients
                .Clients([.. affected])
                .SendAsync(Contract.Events.ShareRevoked, new ShareRevokedEvent(vehicleId), cancellationToken);
        }

        if (signal.IssuedAt is { } issuedAt)
        {
            MageRideDiagnostics.FanoutRevocationMs.Record(
                Math.Max(0, (clock.GetUtcNow() - issuedAt).TotalMilliseconds));
        }
    }

    /// <summary>
    /// The counterpart. A passenger who accepts a grant while already connected should not have to
    /// reconnect to see the vehicle — the socket is long-lived, and a map that only works after a
    /// restart is a map that looks broken.
    /// </summary>
    private async Task GrantAsync(FanoutSignal signal, CancellationToken cancellationToken)
    {
        if (signal.UserId is not { } userId || signal.VehicleId is not { } vehicleId)
        {
            return;
        }

        foreach (var connectionId in connections.ConnectionsOf(userId))
        {
            await hub.Groups.AddToGroupAsync(connectionId, Contract.VehicleGroup(vehicleId), cancellationToken);
            connections.JoinVehicle(connectionId, vehicleId);
        }
    }
}
