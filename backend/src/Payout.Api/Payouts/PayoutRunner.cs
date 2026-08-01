using MageRide.Payout.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Payout.Payouts;

/// <summary>
/// Wakes on an interval and asks whether this Colombo week has been swept yet (AL-58).
/// </summary>
/// <remarks>
/// <para>
/// <b>An interval, not a weekly alarm</b> — fleet-billing-svc's argument, and it matters more here
/// because the thing being missed is somebody's wages. The sweep is idempotent on the business date
/// (<c>run_date</c> is UNIQUE), so re-asking costs one indexed read and catches everything an alarm
/// would miss: a deployment rolling at midnight on Sunday, a replica whose clock moved, a run that
/// died halfway. A weekly alarm gets exactly one chance per week to be running, and its failure mode
/// is a week nobody is paid.
/// </para>
/// <para>
/// <b>Every replica runs it and there is no lease.</b> The batch insert collides on
/// <c>run_date</c> and each instruction on <c>ux_payouts_batch_driver</c>, so concurrency is
/// resolved by the database rather than by a lock — and a lock would introduce a way for payouts to
/// stop entirely when its holder dies badly.
/// </para>
/// </remarks>
internal sealed class PayoutRunner(
    IServiceScopeFactory scopes,
    IOptions<PayoutOptions> options,
    TimeProvider clock,
    ILogger<PayoutRunner> logger) : BackgroundService
{
    private readonly PayoutOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            // Announced at start-up as an error. Nothing here logs per tick — a switch that is off
            // on purpose should not fill a log.
            return;
        }

        using var timer = new PeriodicTimer(_options.PollInterval, _clock);

        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Never let one bad tick end the loop: the next one re-derives everything from the
                // database and resumes exactly where this left off, because every write is
                // idempotent by index.
                logger.LogError(exception, "The payout sweep failed this tick; it will be retried.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();

        var run = scope.ServiceProvider.GetRequiredService<PayoutRunService>();

        if (!run.IsRunDay(_clock.GetUtcNow()))
        {
            return;
        }

        // `force: false` — an already-swept day is "done", not a conflict. The conflict belongs to
        // Finance asking for a date explicitly, where being told is the point.
        await run.RunAsync(run.Today(), force: false, cancellationToken);
    }
}
