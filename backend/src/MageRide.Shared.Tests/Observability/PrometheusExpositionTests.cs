using System.Diagnostics.Metrics;
using System.Reflection;
using MageRide.Shared.Observability;
using MageRide.Shared.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Shared.Tests.Observability;

/// <summary>
/// The names <c>/metrics</c> actually exposes (C119).
/// </summary>
/// <remarks>
/// <para>
/// Nothing in C# says what a <see cref="Meter"/> instrument is called once the Prometheus exporter
/// has mangled it, and the Prometheus alert rules and Grafana dashboards under
/// <c>infra/observability/</c> are written against the mangled name — so a rename here is a silent
/// dashboard of "No data" and, worse, an SLO alert that can never fire. These tests pin the
/// mangling: dot to underscore, a <c>_total</c> suffix on every counter, the unit appended and
/// spelled out (<c>ms</c> becomes <c>milliseconds</c>), and a brace-wrapped annotation unit
/// (<c>{ride}</c>) dropped entirely.
/// </para>
/// <para>
/// <c>infra/scripts/verify-observability.sh</c> derives the same names from
/// <see cref="MageRideDiagnostics"/> by reading the source and checks every <c>mageride_*</c> series
/// named in a rule or a dashboard against them. This test is what makes that derivation legitimate.
/// </para>
/// </remarks>
public sealed class PrometheusExpositionTests
{
    /// <summary>
    /// The exposition the platform meter produces, once per test class — building the host and
    /// scraping is the expensive part and every case asserts against the same text.
    /// </summary>
    private static readonly Lazy<string> Exposition = new(ScrapeAsync().GetAwaiter().GetResult);

    /// <summary>A counter with an annotation unit keeps its name and gains <c>_total</c>.</summary>
    [Fact]
    public void A_counter_is_exposed_with_a_total_suffix_and_no_unit()
    {
        Assert.Contains("# TYPE mageride_rides_stuck_detected_total counter", Exposition.Value);
        Assert.Contains("# TYPE mageride_outbox_dispatched_total counter", Exposition.Value);
    }

    /// <summary>
    /// A millisecond histogram is exposed as <c>_milliseconds</c>. The whole D-19 / D-33 latency
    /// SLO family is written against this suffix.
    /// </summary>
    [Fact]
    public void A_millisecond_histogram_is_exposed_with_a_spelled_out_unit()
    {
        Assert.Contains("# TYPE mageride_positions_e2e_latency_milliseconds histogram", Exposition.Value);
        Assert.Contains("# TYPE mageride_sos_dispatch_latency_milliseconds histogram", Exposition.Value);
        Assert.Contains("mageride_positions_e2e_latency_milliseconds_bucket{", Exposition.Value);
        Assert.Contains("mageride_positions_e2e_latency_milliseconds_count{", Exposition.Value);
    }

    /// <summary>
    /// An observable gauge keeps its tags, which is what makes the §13.3.1 stuck-state alerts
    /// per-state rather than one number.
    /// </summary>
    [Fact]
    public void An_observable_gauge_is_exposed_with_its_tags()
    {
        Assert.Contains("# TYPE mageride_rides_stuck gauge", Exposition.Value);
        Assert.Contains("state=\"Matching\"", Exposition.Value);
    }

    /// <summary>
    /// The service identity lands on <c>target_info</c> and on nothing else, so a rule that wants
    /// to name a service reads the scrape job's own label instead — which is why
    /// <c>prometheus.yml</c> stamps <c>service</c> on every job.
    /// </summary>
    [Fact]
    public void The_service_name_is_only_on_target_info()
    {
        Assert.Contains("target_info{", Exposition.Value);
        Assert.Contains("service_name=\"c119-exposition\"", Exposition.Value);

        var onASeries = Exposition.Value
            .Split('\n')
            .Where(line => line.StartsWith("mageride_", StringComparison.Ordinal))
            .Any(line => line.Contains("service_name=", StringComparison.Ordinal));

        Assert.False(onASeries, "service_name leaked onto a metric series; the dashboards join on the scrape label.");
    }

    /// <summary>
    /// ASP.NET Core's own golden signals, which ADD §13.2 wants from every service and which no
    /// MageRide code emits — they arrive with <c>AddAspNetCoreInstrumentation</c> and would vanish
    /// silently if that call were dropped.
    /// </summary>
    [Fact]
    public void The_aspnetcore_golden_signals_are_exposed()
    {
        Assert.Contains("# TYPE http_server_request_duration_seconds histogram", Exposition.Value);
        Assert.Contains("# TYPE http_server_active_requests gauge", Exposition.Value);
        Assert.Contains("# TYPE dotnet_gc_collections_total counter", Exposition.Value);
    }

    /// <summary>
    /// Every instrument the platform meter declares reaches the scrape. A new histogram that
    /// nobody records is invisible to Prometheus until its first measurement, so this walks
    /// <see cref="MageRideDiagnostics"/> by reflection and records one value on each — the same
    /// list <c>verify-observability.sh</c> derives its inventory from.
    /// </summary>
    [Fact]
    public void Every_declared_instrument_reaches_the_scrape()
    {
        var missing = DeclaredInstruments()
            .Select(PrometheusName)
            .Where(name => !Exposition.Value.Contains($"# TYPE {name} ", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(missing);
    }

    /// <summary>Every public instrument field on <see cref="MageRideDiagnostics"/>.</summary>
    private static IEnumerable<Instrument> DeclaredInstruments() =>
        typeof(MageRideDiagnostics)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => typeof(Instrument).IsAssignableFrom(field.FieldType))
            .Select(field => (Instrument)field.GetValue(null)!);

    /// <summary>
    /// The exporter's name mangling, reimplemented so the test asserts the rule rather than a list
    /// of literals. Kept in step with <c>verify-observability.sh</c>'s <c>prometheus_name</c>.
    /// </summary>
    private static string PrometheusName(Instrument instrument)
    {
        var name = instrument.Name.Replace('.', '_');

        var unit = instrument.Unit switch
        {
            null or "" => null,
            var u when u.StartsWith('{') => null,
            "ms" => "milliseconds",
            "s" => "seconds",
            "By" => "bytes",
            var u => u,
        };

        if (unit is not null)
        {
            name = $"{name}_{unit}";
        }

        return instrument is Counter<long> ? $"{name}_total" : name;
    }

    /// <summary>
    /// Builds a host with the real telemetry kernel, records one measurement on every declared
    /// instrument, and returns what <c>/metrics</c> answers.
    /// </summary>
    private static async Task<string> ScrapeAsync()
    {
        var builder = TestHosts.CreateBuilder(new Dictionary<string, string?>
        {
            ["Otel:ServiceName"] = "c119-exposition",
            ["Otel:PrometheusEnabled"] = "true",
            // No collector in a unit test; an endpoint here would make every scrape wait on a
            // connection refused.
            ["Otel:Endpoint"] = string.Empty,
        });

        builder.Services.AddMageRideTelemetry(builder.Configuration, "c119-exposition");

        await using var app = builder.Build();
        app.MapMageRideMetrics();
        app.MapGet("/c119", () => "ok");
        await app.StartAsync();

        // One measurement each: an instrument with no measurement is not in the exposition, and
        // the observable gauges are only read by the scrape itself.
        foreach (var instrument in DeclaredInstruments())
        {
            switch (instrument)
            {
                case Counter<long> counter:
                    counter.Add(1);
                    break;
                case Histogram<double> histogram:
                    histogram.Record(1);
                    break;
            }
        }

        // The three gauge names are constants rather than instruments (their callbacks belong to
        // the services that own the data), so the scrape only sees them if something registers one.
        using var meter = new Meter(MageRideDiagnostics.MeterName);
        meter.CreateObservableGauge(
            MageRideDiagnostics.RidesStuckGauge,
            () => new Measurement<int>(0, new KeyValuePair<string, object?>("state", "Matching")));

        using var client = app.GetTestClient();

        // ASP.NET Core's request histogram is recorded when a request *finishes*, so the scrape
        // itself never appears in its own exposition. Without a completed request first, the
        // golden signals every dashboard is built on are simply absent.
        await client.GetStringAsync("/c119");

        return await client.GetStringAsync("/metrics");
    }
}
