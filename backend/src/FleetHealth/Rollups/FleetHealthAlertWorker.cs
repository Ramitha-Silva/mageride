using MageRide.FleetHealth.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MageRide.FleetHealth.Rollups;

/// <summary>
/// Ticks, works out which window has just closed, and hands it to
/// <see cref="IFleetHealthAlertService"/> (US-3.16).
/// </summary>
/// <remarks>
/// <para>
/// <b>The tick is not the window.</b> A tick aligned to the five-minute boundary would evaluate a bucket
/// the instant it closed, and 1802's refresh policy has a five-minute <c>end_offset</c> — so the rows for
/// it may not be materialised yet and the explicit refresh may be racing the policy. Checking every
/// minute means a closed window is evaluated at most a minute late and re-checked while it is still the
/// most recent one; re-checking costs one query and can raise nothing twice, because
/// <c>ux_fleet_health_alert_window</c> has already claimed it.
/// </para>
/// <para>
/// <b>The aggregate is verified on the first pass, not at start-up.</b> A database check in
/// <c>Build</c> would make the service refuse to start while Postgres was still coming up, which in the
/// dev compose and on DOKS is a normal few seconds — and the check is a diagnostic, not a precondition.
/// </para>
/// </remarks>
public sealed class FleetHealthAlertWorker(
    IServiceProvider services,
    IOptions<FleetHealthOptions> options,
    TimeProvider clock,
    ILogger<FleetHealthAlertWorker> logger) : BackgroundService
{
    private readonly FleetHealthOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    private bool _verified;
    private long _passes;
    private long _alertsRaised;

    /// <summary>Evaluation passes this replica has completed. Read by the alert test.</summary>
    public long Passes => Interlocked.Read(ref _passes);

    /// <summary>Alerts this replica raised.</summary>
    public long AlertsRaised => Interlocked.Read(ref _alertsRaised);

    /// <summary>
    /// Runs one evaluation pass over the window that closed most recently.
    /// </summary>
    /// <remarks>
    /// Exposed so a test drives the pass directly rather than waiting on a timer — the pattern every
    /// other sweep on the platform follows.
    /// </remarks>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        if (!_verified)
        {
            await scope.ServiceProvider.GetRequiredService<IAggregateMaintainer>().VerifyAsync(cancellationToken);
            _verified = true;
        }

        var alerts = scope.ServiceProvider.GetRequiredService<IFleetHealthAlertService>();
        var bucket = TimeBuckets.LastClosedStart(clock.GetUtcNow(), _options.Window);

        var raised = await alerts.EvaluateWindowAsync(bucket, cancellationToken);

        Interlocked.Increment(ref _passes);
        Interlocked.Add(ref _alertsRaised, raised.Count);

        return raised.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var timer = new PeriodicTimer(_options.AlertCheckInterval, clock);

        // A first pass immediately, so a deployment that comes up after an outage reports on the window
        // it missed rather than waiting a full interval to notice.
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
                // The next tick tries again. Nothing is lost: the window is still the most recent closed
                // one for another few minutes, and the claim index makes a repeated pass free.
                logger.LogError(exception, "Fleet-health window evaluation failed; retrying on the next tick");
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
}
