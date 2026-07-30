using MageRide.FleetHealth.Configuration;
using MageRide.FleetHealth.Domain;
using MageRide.FleetHealth.Persistence;
using MageRide.Shared.Observability;
using Microsoft.Extensions.Options;

namespace MageRide.FleetHealth.Rollups;

/// <summary>
/// Notices that a device has changed state, and pushes its diagnostics back onto
/// <c>prov.tracker_bindings</c> (US-3.12).
/// </summary>
/// <remarks>
/// <para>
/// <b>A device going quiet sends nothing, so only a clock can move it.</b> That is this worker's whole
/// reason to exist: every other input to the health plane is an event, and the two transitions US-3.13
/// is written around — Online → Stale at five minutes, Stale → Offline at thirty — are the absence of
/// one.
/// </para>
/// <para>
/// <b>It is not what the dashboard depends on.</b> <c>GET /v1/fleets/{fleetId}/health</c> derives every
/// state fresh from <c>telemetry.device_health_state()</c>, so with this worker off the counts are still
/// correct to the second and only three things stop: the transition counters an operator's alert rule
/// watches, the <c>since</c> timestamp on a device, and the diagnostics sync. That split is deliberate —
/// a dashboard whose correctness depended on a sweep would be a dashboard that lies for a minute after
/// every restart.
/// </para>
/// <para>
/// <b>The two jobs run on different clocks.</b> The transition sweep touches only the devices that
/// moved, so it is cheap and runs every minute. The diagnostics sync touches every device whose ping
/// advanced, and <c>prov.tracker_bindings</c> carries an <c>updated_at</c> trigger — at T-10's 100k
/// trackers that is a real write per device per pass, so it runs on its own five-minute interval
/// against a value C030 already says may be stale.
/// </para>
/// </remarks>
public sealed class HealthSweepWorker(
    IDeviceHealthRepository repository,
    IOptions<FleetHealthOptions> options,
    TimeProvider clock,
    ILogger<HealthSweepWorker> logger) : BackgroundService
{
    private readonly FleetHealthOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    private DateTimeOffset _bindingSyncDue = DateTimeOffset.MinValue;
    private long _passes;
    private long _transitions;

    /// <summary>Sweep passes this replica has completed. Read by the state-ladder test.</summary>
    public long Passes => Interlocked.Read(ref _passes);

    /// <summary>Device state changes recorded.</summary>
    public long Transitions => Interlocked.Read(ref _transitions);

    /// <summary>
    /// Runs one sweep pass and returns what moved. Exposed so a test drives the pass directly rather
    /// than waiting on a timer.
    /// </summary>
    public async Task<IReadOnlyList<HealthTransition>> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var moved = await repository.SweepTransitionsAsync(
            new HealthThresholds(_options.StaleAfter, _options.OfflineAfter),
            now,
            _options.SweepBatchSize,
            cancellationToken);

        Interlocked.Increment(ref _passes);
        Interlocked.Add(ref _transitions, moved.Count);

        foreach (var transition in moved)
        {
            MageRideDiagnostics.DeviceHealthTransitions.Add(
                1,
                new KeyValuePair<string, object?>("from", TrackerHealthStates.ToWire(transition.FromState)),
                new KeyValuePair<string, object?>("to", TrackerHealthStates.ToWire(transition.ToState)));
        }

        if (moved.Count > 0)
        {
            logger.LogInformation("{Moved} tracker(s) changed health state", moved.Count);
        }

        if (moved.Count == _options.SweepBatchSize)
        {
            // No silent caps: the pass was bounded, so say which devices are still waiting rather than
            // letting a partial sweep read as a complete one.
            logger.LogWarning(
                "The health sweep filled its {BatchSize}-row batch, so more devices are still waiting; " +
                "the next pass in {Interval} picks them up",
                _options.SweepBatchSize, _options.SweepInterval);
        }

        if (_options.BindingSyncEnabled && now >= _bindingSyncDue)
        {
            _bindingSyncDue = now + _options.BindingSyncInterval;
            await SyncBindingsAsync(cancellationToken);
        }

        return moved;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var timer = new PeriodicTimer(_options.SweepInterval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Retried on the next tick. A missed pass costs a late transition, not a wrong count —
                // the read derives the state itself.
                logger.LogError(exception, "The health sweep failed; retrying on the next tick");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SyncBindingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var synced = await repository.SyncBindingDiagnosticsAsync(_options.SweepBatchSize, cancellationToken);

            if (synced > 0)
            {
                logger.LogDebug("Synced last-seen and diagnostics onto {Synced} tracker binding(s)", synced);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Swallowed on purpose: this is a courtesy write into another bounded context's table for
            // US-3.12's admin panel. A failure must not take the health plane's own sweep down with it.
            logger.LogWarning(exception, "Could not sync diagnostics onto prov.tracker_bindings");
        }
    }
}
