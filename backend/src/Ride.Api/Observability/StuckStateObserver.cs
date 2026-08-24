using System.Diagnostics.Metrics;
using MageRide.Ride.Domain;
using MageRide.Ride.Persistence;
using MageRide.Shared.Observability;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MageRide.Ride.Observability;

/// <summary>One row of ADD §13.3.1's stuck-state table.</summary>
/// <param name="Age">How long a ride may sit in <paramref name="State"/> before it counts as stuck.</param>
public sealed record StuckStateRule(string State, TimeSpan Age);

/// <summary>
/// Publishes the R-20 stuck-state business SLOs as gauges (ADD §13.3.1, §13.4).
/// </summary>
/// <remarks>
/// <para>
/// Every row of §13.3.1 is literally <c>count(rides WHERE state=S AND age &gt; T)</c>, so each is
/// an observable gauge over one indexed count, evaluated only when something scrapes. They are
/// published on the platform meter (<see cref="MageRideDiagnostics.MeterName"/>) so the Prometheus
/// endpoint the kernel already exposes carries them with no extra wiring.
/// </para>
/// <para>
/// <b>Three of the table's rows are approximated, and the approximation is stated.</b> "Accepted
/// AND <c>accepted_driver_id</c> has no live pos for &gt; 60 s" and "InProgress AND no GPS sample
/// for &gt; 5 min" both ask about telemetry; ride-svc holds no positions — <c>telemetry.positions</c>
/// is the hot path's (C038–C040) and this service deliberately consumes none of it (R-01's
/// boundary is what keeps the ride aggregate small). What is published instead is time in state,
/// which is a superset: every ride whose driver has gone dark is in it, along with rides that are
/// simply slow. The precise version belongs with whoever owns the position index, and is recorded
/// in the C032 handoff. The <c>Completed+PaymentPending</c> row is exact, because ride-svc's own
/// <c>PaymentPending</c> is what §13.3.1 means by it.
/// </para>
/// </remarks>
public sealed class StuckStateObserver : IDisposable
{
    /// <summary>ADD §13.3.1, top to bottom. The two GPS-qualified rows carry their state age.</summary>
    public static readonly IReadOnlyList<StuckStateRule> Rules =
    [
        new(RideStates.Matching, TimeSpan.FromSeconds(60)),
        new(RideStates.Offered, TimeSpan.FromSeconds(20)),
        new(RideStates.Accepted, TimeSpan.FromSeconds(60)),
        new(RideStates.DriverArrived, TimeSpan.FromMinutes(10)),
        new(RideStates.InProgress, TimeSpan.FromMinutes(5)),
        new(RideStates.PaymentPending, TimeSpan.FromMinutes(10)),
    ];

    /// <summary>ADD §13.4: "<c>rides.timers</c> backlog &gt; 100 fired_at IS NULL AND fire_at &lt; now()-30s".</summary>
    private static readonly TimeSpan BacklogWindow = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly ILogger<StuckStateObserver> _logger;
    private readonly List<ObservableGauge<int>> _gauges = [];

    public StuckStateObserver(IServiceProvider services, ILogger<StuckStateObserver> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        foreach (var rule in Rules)
        {
            var captured = rule;

            _gauges.Add(MageRideDiagnostics.Meter.CreateObservableGauge(
                MageRideDiagnostics.RidesStuckGauge,
                () => new Measurement<int>(
                    CountStuck(captured),
                    new KeyValuePair<string, object?>("state", captured.State)),
                "{ride}",
                "Rides past the ADD §13.3.1 window for their state (R-20)."));
        }

        _gauges.Add(MageRideDiagnostics.Meter.CreateObservableGauge(
            MageRideDiagnostics.RideTimerBacklogGauge,
            CountBacklog,
            "{timer}",
            "Unfired rides.timers rows more than 30 s overdue (ADD §13.4)."));
    }

    /// <summary>
    /// The count behind one gauge. Exposed so a test can assert the rule rather than the scrape.
    /// </summary>
    public int CountStuck(StuckStateRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return Measure(
            async (connection, rides, _, cancellationToken) =>
                await rides.CountStuckAsync(connection, rule.State, rule.Age, cancellationToken),
            fallback: 0);
    }

    /// <summary>Overdue timers this service owns (ADD §13.4's runbook trigger).</summary>
    public int CountBacklog() =>
        Measure(
            async (connection, _, timers, cancellationToken) =>
                await timers.CountBacklogAsync(connection, BacklogWindow, cancellationToken),
            fallback: 0);

    /// <summary>
    /// <em>Which</em> rides are behind one gauge — the question on-call asks second.
    /// </summary>
    /// <remarks>
    /// Not on the scrape path: the gauge stays an indexed <c>count(*)</c> and this is a separate
    /// call over the same predicate. It is also what lets a test assert about the ride it made
    /// stuck rather than about a global tally — the gauge counts every ride on the platform, so a
    /// count taken before and after cannot tell this ride's arrival from any other ride's.
    /// </remarks>
    public IReadOnlyCollection<Guid> StuckRideIds(StuckStateRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return Measure(
            async (connection, rides, _, cancellationToken) =>
                await rides.StuckRideIdsAsync(connection, rule.State, rule.Age, cancellationToken),
            fallback: Array.Empty<Guid>());
    }

    /// <summary>Whose timers are behind the §13.4 backlog gauge.</summary>
    public IReadOnlyCollection<Guid> BacklogRideIds() =>
        Measure(
            async (connection, _, timers, cancellationToken) =>
                await timers.BacklogRideIdsAsync(connection, BacklogWindow, cancellationToken),
            fallback: Array.Empty<Guid>());

    public void Dispose()
    {
        // The gauges hold callbacks into this instance; dropping the references is what stops a
        // disposed harness's meter from querying a closed connection pool on the next scrape.
        _gauges.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Runs one measurement query.
    /// </summary>
    /// <remarks>
    /// Synchronous by necessity — <see cref="ObservableGauge{T}"/>'s callback has no async form —
    /// and blocking is acceptable here because it happens on the metrics scrape thread, at the
    /// scrape interval, against one indexed count. A query that fails reports 0 and logs: a broken
    /// gauge must not take the whole scrape (and with it every other service's metrics) down.
    /// </remarks>
    private T Measure<T>(
        Func<Npgsql.NpgsqlConnection, IRideRepository, IRideTimerRepository, CancellationToken, Task<T>> query,
        T fallback)
    {
        try
        {
            using var scope = _services.CreateScope();

            var connectionFactory = scope.ServiceProvider.GetRequiredService<INpgsqlConnectionFactory>();
            var rides = scope.ServiceProvider.GetRequiredService<IRideRepository>();
            var timers = scope.ServiceProvider.GetRequiredService<IRideTimerRepository>();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            return Task.Run(
                async () =>
                {
                    await using var connection = await connectionFactory.OpenAsync(timeout.Token);
                    return await query(connection, rides, timers, timeout.Token);
                },
                timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A stuck-state measurement failed; reporting the empty answer for this scrape");
            return fallback;
        }
    }
}
