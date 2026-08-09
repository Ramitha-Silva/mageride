using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MageRide.Shared.Observability;

/// <summary>
/// Observable gauges backed by a database read, done once instead of once per service (C119).
/// </summary>
/// <remarks>
/// <para>
/// ADD §13.3.1's stuck-state SLOs are each literally <c>count(rows WHERE …) &gt; 0</c>, so the
/// natural shape is an observable gauge over one indexed count, evaluated only when something
/// scrapes: the gauge is the metric and the Prometheus rule is the threshold. Three services need
/// that shape — ride-svc for six of the rows, fare-svc and registry-svc for the other two — and
/// every one of them needs the same four things around the query, each of which is easy to get
/// subtly wrong:
/// </para>
/// <list type="number">
/// <item><b>A meter it can dispose.</b> An <see cref="Instrument"/> is unpublished only when its
/// meter is, so gauges created on a process-static meter outlive the host that made them: a second
/// host in the same process (every integration test does this) leaves two live instruments under
/// one name, which is a duplicate series that Prometheus rejects for the whole scrape, each holding
/// a callback into a disposed provider. The meter here carries
/// <see cref="MageRideDiagnostics.MeterName"/>, so <c>AddMeter("MageRide")</c> still collects it and
/// the exposition is identical.</item>
/// <item><b>A scope per read.</b> Repositories are scoped; the callback runs on the scrape thread,
/// which has none.</item>
/// <item><b>A bounded wait.</b> A gauge that blocks for ever blocks the whole scrape, and with it
/// every other metric the process publishes.</item>
/// <item><b>Fail to a default, loudly.</b> A query that throws must not take the scrape down — but
/// the reading it returns instead is a lie, so it is logged at warning with the gauge's name.</item>
/// </list>
/// <para>
/// <b>Blocking is deliberate and <c>Task.Run</c> is deliberately absent.</b>
/// <see cref="ObservableGauge{T}"/>'s callback has no async form. An ASP.NET Core host has no
/// <see cref="System.Threading.SynchronizationContext"/>, so blocking directly is safe; wrapping in
/// <c>Task.Run</c> would occupy a pool worker *as well as* the scrape thread and can deadlock
/// against its own queue under load — which would make the gauge report its failure default during
/// exactly the incident it exists to catch.
/// </para>
/// <para>
/// Implements <see cref="IHostedService"/> so a host constructs it at start-up: the gauges are
/// pull-based and nothing else ever resolves the type, so without that they would not exist until
/// something happened to ask for them.
/// </para>
/// </remarks>
public sealed class ScrapedGauges : IHostedService, IDisposable
{
    /// <summary>Longest a single gauge read may take before it reports its failure default.</summary>
    /// <remarks>
    /// Shorter than any sane scrape timeout, so a slow database degrades one gauge rather than the
    /// endpoint.
    /// </remarks>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _logger;
    private readonly Meter _meter;

    public ScrapedGauges(IServiceScopeFactory scopes, ILogger logger)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _meter = new Meter(MageRideDiagnostics.MeterName);
    }

    /// <summary>Publishes one gauge whose value is read from a scoped service at scrape time.</summary>
    /// <param name="name">The instrument name — a <c>const</c> on <see cref="MageRideDiagnostics"/>.</param>
    /// <param name="unit">UCUM, or a <c>{brace}</c> annotation the exporter drops.</param>
    /// <param name="description">What a non-zero reading means, for the <c>HELP</c> line.</param>
    /// <param name="read">The query. Runs inside a fresh scope with a <see cref="ReadTimeout"/>.</param>
    /// <param name="tags">Constant tags — e.g. the state a §13.3.1 row is about.</param>
    public void Publish(
        string name,
        string unit,
        string description,
        Func<IServiceProvider, CancellationToken, Task<int>> read,
        params KeyValuePair<string, object?>[] tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(read);

        _meter.CreateObservableGauge(
            name,
            () => new Measurement<int>(Read(name, read), tags),
            unit,
            description);
    }

    /// <summary>
    /// Runs one gauge's query. Public so a test can assert the rule rather than the scrape.
    /// </summary>
    public int Read(string name, Func<IServiceProvider, CancellationToken, Task<int>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        try
        {
            using var scope = _scopes.CreateScope();
            using var timeout = new CancellationTokenSource(ReadTimeout);

            return read(scope.ServiceProvider, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception, "The {Gauge} gauge could not be measured; reporting 0 for this scrape", name);

            return 0;
        }
    }

    /// <summary>Constructing this is the whole of starting it — the gauges are pull-based.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Unpublishes every gauge. See the class remarks for why this is not optional.</summary>
    public void Dispose() => _meter.Dispose();
}
