namespace MageRide.Shared.Observability;

/// <summary>OpenTelemetry wiring (D7' §4.1 <c>Otel__Endpoint</c>, D7' §12).</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Otel";

    /// <summary>
    /// OTLP collector endpoint. Unset disables OTLP export — the local-dev default, where traces
    /// go nowhere and the metrics endpoint is still scrapeable.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>Service name in the OTel resource. Defaults to the host's application name.</summary>
    public string? ServiceName { get; set; }

    public string? ServiceVersion { get; set; }

    /// <summary>Expose Prometheus metrics on <see cref="MetricsPath"/> for scraping (D7' §12).</summary>
    public bool PrometheusEnabled { get; set; } = true;

    public string MetricsPath { get; set; } = "/metrics";

    /// <summary>Trace sampling ratio. 1.0 in dev; lowered in production by SLO budget (D-19).</summary>
    public double TraceSampleRatio { get; set; } = 1.0;

    /// <summary>Route ASP.NET Core logs through OTel to Loki as well as the console.</summary>
    public bool LoggingEnabled { get; set; } = true;
}
