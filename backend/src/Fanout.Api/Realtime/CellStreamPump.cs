using MageRide.Fanout.Configuration;
using MageRide.Shared.Observability;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.Fanout.Realtime;

/// <summary>
/// Drains the <c>cell:{h3index}</c> streams this replica has members in and pushes each cell's
/// batch to its group (ADD §7.4 step 7, <c>signalr-hub.md</c> §3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pull, not push, and that is what makes the cost model work.</b> ADD §7.4's whole argument is
/// that fan-out must be O(updates × subscribers-per-cell) rather than O(passengers × vehicles): this
/// replica reads only the cells it actually has someone in, and sends one batch per cell however
/// many passengers are in it. A replica with no subscribers does no Redis work at all.
/// </para>
/// <para>
/// <b>Every replica reads independently and pushes locally.</b> That is why there is no SignalR
/// backplane here — see <see cref="ICellSubscriptions"/>. Coverage is complete because each replica
/// covers its own connections; adding a backplane would multiply every frame by the replica count.
/// </para>
/// <para>
/// <b>Batched, never per fix.</b> One <c>VehiclePositions</c> per cell per tick carrying the newest
/// frame per vehicle. A per-fix fan-out would be five messages a second per vehicle times every
/// subscriber of its cell, which is the cost ADD §7.4 exists to avoid.
/// </para>
/// </remarks>
internal sealed class CellStreamPump(
    ICellSubscriptions subscriptions,
    ICellStreamReader streams,
    IHubContext<LiveHub> hub,
    IOptions<FanoutOptions> options,
    TimeProvider clock,
    ILogger<CellStreamPump> logger) : BackgroundService
{
    private readonly FanoutOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

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
                // A Redis blip must not end the pump. The next tick re-reads from the same
                // positions, so nothing is skipped — at worst a batch arrives one interval late.
                logger.LogError(ex, "A fan-out tick failed; continuing");
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

    /// <summary>One drain of every active cell. Internal so a test can step it deterministically.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await ApplyDueLeavesAsync(cancellationToken);

        foreach (var cell in subscriptions.ActiveCells)
        {
            var batch = await streams.ReadAsync(
                cell, subscriptions.PositionOf(cell), _options.MaxEntriesPerCellPerTick, cancellationToken);

            if (batch is null)
            {
                continue;
            }

            // Advanced whether or not anything is sent: an entry that produced no drawable frame
            // has still been read, and re-reading it every tick would pin the position forever.
            if (!batch.Position.IsNull)
            {
                subscriptions.Advance(cell, batch.Position);
            }

            if (batch.Frames.Count == 0)
            {
                continue;
            }

            await hub.Clients
                .Group(Contract.CellGroup(cell))
                .SendAsync(Contract.Events.VehiclePositions, batch.Frames, cancellationToken);

            MageRideDiagnostics.FanoutFramesSent.Add(batch.Frames.Count);

            if (batch.OldestEntryAt is { } written)
            {
                MageRideDiagnostics.FanoutLatencyMs.Record(
                    Math.Max(0, (clock.GetUtcNow() - written).TotalMilliseconds));
            }
        }
    }

    /// <summary>
    /// Applies the group removals whose 30 s hysteresis window has elapsed (ADD §7.4 step 6).
    /// </summary>
    /// <remarks>
    /// Ridden on the pump's own tick rather than given a timer of its own: the window is 30 s and
    /// the tick is seconds, so the resolution is ample, and one loop is one thing to reason about
    /// when a group does not empty.
    /// </remarks>
    private async Task ApplyDueLeavesAsync(CancellationToken cancellationToken)
    {
        foreach (var leave in subscriptions.DrainDueLeaves(clock.GetUtcNow()))
        {
            await hub.Groups.RemoveFromGroupAsync(
                leave.ConnectionId, Contract.CellGroup(leave.Cell), cancellationToken);
        }
    }
}
