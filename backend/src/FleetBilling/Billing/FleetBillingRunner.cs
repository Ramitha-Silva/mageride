using MageRide.FleetBilling.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.FleetBilling.Billing;

/// <summary>
/// Keeps the current Colombo month invoiced, settled and dunned.
/// </summary>
/// <remarks>
/// <para>
/// <b>An interval, not a monthly alarm.</b> Every phase is idempotent — generation is three upserts,
/// settlement is guarded by the ledger's unique key, dunning is a claim — so re-running costs three
/// statements and catches everything a once-a-month timer would miss: a vehicle approved on the 9th,
/// a deployment that was rolling at midnight on the 1st, a replica whose clock moved, a wallet
/// topped up an hour ago. A monthly alarm gets exactly one attempt per month to be running, and its
/// failure mode is a month nobody is billed for. subscription-svc's argument for the per-vehicle
/// charge run this consolidates, one level up.
/// </para>
/// <para>
/// <b>Three phases in this order, and the order matters.</b> Generate before settling, or the month
/// just opened is settled empty; settle before dunning, or an invoice a fresh top-up already covers
/// is announced as overdue. All three run every tick and each is cheap when there is nothing to do.
/// </para>
/// <para>
/// <b>Every replica runs it and there is no lease.</b> A lock would protect operations that are
/// already idempotent, and would introduce a way for billing to stop entirely when the lock holder
/// dies badly.
/// </para>
/// </remarks>
internal sealed class FleetBillingRunner(
    IServiceScopeFactory scopes,
    IOptions<FleetBillingOptions> options,
    TimeProvider clock,
    ILogger<FleetBillingRunner> logger) : BackgroundService
{
    private readonly FleetBillingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.InvoicingEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.RunInterval, _clock);

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A pass that threw must not take the host down with it.
            catch (Exception exception)
            {
                // Swallowed on purpose: an unhandled exception here would end the BackgroundService
                // for the process's lifetime, so one bad database moment would silence fleet billing
                // until somebody restarted the pod. The next tick retries.
                logger.LogError(exception, "The fleet billing run failed. Retrying next tick.");
            }
#pragma warning restore CA1031
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass. Internal so a test and the internal route can drive it without a timer.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();

        var generation = scope.ServiceProvider.GetRequiredService<IInvoiceRunService>();
        var settlement = scope.ServiceProvider.GetRequiredService<IInvoiceSettlementService>();
        var dunning = scope.ServiceProvider.GetRequiredService<IDunningService>();

        await generation.RunAsync(generation.CurrentPeriod(), cancellationToken);

        if (_options.AutoSettle)
        {
            await settlement.RunAsync(fleetId: null, cancellationToken);
        }

        await dunning.RunAsync(cancellationToken);
    }
}
