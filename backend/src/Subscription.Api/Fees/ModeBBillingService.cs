using MageRide.Shared.Time;
using MageRide.Subscriptions.Configuration;
using MageRide.Subscriptions.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Subscriptions.Fees;

/// <summary>
/// The platform's Mode B monthly charge: ~Rs 300 per vehicle per Colombo month, first month free
/// (D5' §2.1, ADD §19, AL-03).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the platform charging the vehicle's owner. It is not C048's money.</b> The two flows share
/// the words "Mode B" and "monthly" and nothing else: <c>subscription.payments</c> is a passenger's fare
/// to the fleet owner, a pass-through MageRide never holds and never ledgers (§18b is explicit), while
/// this is MageRide's own fee and the only one of the pair that ever posts. Netting them against each
/// other would be the single most expensive mistake available in this schema.
/// </para>
/// <para>
/// <b>The charge is raised here and settled by C060.</b> §10 gives
/// <c>billing.monthly_subscriptions</c> no <c>journal_entry_id</c>, gives
/// <c>billing.fleet_invoices</c> one, and offers no journal <c>kind</c> a monthly fee could be posted
/// under — so the per-vehicle row is deliberately a statement of what is owed, and the consolidated
/// invoice, the fleet wallet it settles against and the per-vehicle breakdown are fleet-billing-svc's
/// deliverables. What this service owes them is a month's worth of correct, idempotent lines.
/// </para>
/// </remarks>
internal sealed class ModeBBillingService(
    IModeBBillingRepository billing,
    IOptions<SubscriptionOptions> options,
    TimeProvider clock,
    ILogger<ModeBBillingService> logger)
{
    private readonly SubscriptionOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>The Colombo month an instant falls in — the first of that month.</summary>
    public DateOnly CurrentPeriod()
    {
        var today = BusinessCalendar.Today(clock);
        return new DateOnly(today.Year, today.Month, 1);
    }

    /// <summary>
    /// Raises every missing per-vehicle charge for a month. Safe to call as often as you like.
    /// </summary>
    public async Task<ModeBRunResult> RunAsync(DateOnly periodMonth, CancellationToken cancellationToken)
    {
        var period = new DateOnly(periodMonth.Year, periodMonth.Month, 1);

        var result = await billing.RaiseMonthAsync(
            period, _options.ModeBMonthlyFeeMinor, clock.GetUtcNow(), cancellationToken);

        if (result.Raised > 0)
        {
            logger.LogInformation(
                "Mode B platform charge for {Period}: {Raised} vehicle(s) raised, {Free} in their first "
                + "month (free), {TotalMinor} minor units due. fleet-billing-svc (C060) consolidates these "
                + "into billing.fleet_invoices.",
                period,
                result.Raised,
                result.FreeMonths,
                result.TotalMinor);
        }

        return result;
    }

    /// <summary>The month's lines, optionally narrowed to one fleet — the C060 hand-off.</summary>
    public Task<IReadOnlyList<ModeBCharge>> ListAsync(
        DateOnly periodMonth, Guid? fleetId, CancellationToken cancellationToken) =>
        billing.ListAsync(new DateOnly(periodMonth.Year, periodMonth.Month, 1), fleetId, cancellationToken);
}

/// <summary>
/// Keeps the current Colombo month's Mode B charges raised.
/// </summary>
/// <remarks>
/// <para>
/// <b>An interval, not a monthly alarm.</b> The run is an idempotent upsert keyed by
/// <c>(vehicle_id, period_month)</c>, so re-running costs one statement and catches everything a
/// once-a-month timer would miss: a vehicle approved on the 9th, a deployment that was rolling at
/// midnight on the 1st, a replica whose clock moved. A monthly alarm gets exactly one attempt per month
/// to be running, and the failure mode is a month nobody is billed for.
/// </para>
/// <para>
/// <b>Every replica runs it and that is fine</b> — the ON CONFLICT DO NOTHING is the arbiter, so the
/// second one through raises nothing. A lease would be a distributed lock protecting an operation that
/// is already idempotent.
/// </para>
/// </remarks>
internal sealed class ModeBBillingRunner(
    IServiceProvider services,
    IOptions<SubscriptionOptions> options,
    TimeProvider clock,
    ILogger<ModeBBillingRunner> logger) : BackgroundService
{
    private readonly SubscriptionOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ModeBBillingEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.ModeBBillingInterval, clock);

        do
        {
            try
            {
                using var scope = services.CreateScope();
                var billing = scope.ServiceProvider.GetRequiredService<ModeBBillingService>();

                await billing.RunAsync(billing.CurrentPeriod(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Swallowed on purpose: an unhandled exception here would take the BackgroundService
                // down for the process's lifetime, so one bad database moment would silence Mode B
                // billing until somebody restarted the pod. The next tick retries.
                logger.LogError(exception, "The Mode B monthly platform charge run failed. Retrying next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
