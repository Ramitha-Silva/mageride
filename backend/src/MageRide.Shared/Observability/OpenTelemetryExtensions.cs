using MageRide.Shared.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MageRide.Shared.Observability;

public static class OpenTelemetryExtensions
{
    /// <summary>
    /// Traces, metrics and logs per D7' §12: Prometheus scrape on <c>/metrics</c>, OTLP to the
    /// collector for Tempo and Loki, Npgsql and HttpClient instrumented.
    /// </summary>
    public static IServiceCollection AddMageRideTelemetry(
        this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        services.AddOptions<TelemetryOptions>()
            .Bind(configuration.GetSection(TelemetryOptions.SectionName))
            .ValidateOnStart();

        var telemetry = configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();
        var resolvedName = telemetry.ServiceName ?? serviceName;

        void ApplyResource(ResourceBuilder resource)
        {
            resource.AddService(
                serviceName: resolvedName,
                serviceVersion: telemetry.ServiceVersion ?? typeof(OpenTelemetryExtensions).Assembly.GetName().Version?.ToString(),
                serviceInstanceId: Environment.MachineName);

            resource.AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] =
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production,
            });
        }

        var builder = services.AddOpenTelemetry().ConfigureResource(ApplyResource);

        builder.WithTracing(tracing =>
        {
            tracing
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(telemetry.TraceSampleRatio)))
                .AddSource(MageRideDiagnostics.ActivitySourceName)
                .AddAspNetCoreInstrumentation(o =>
                {
                    // Probes and the scrape endpoint fire every few seconds and would swamp Tempo.
                    o.Filter = context => !IsInfrastructurePath(context.Request.Path, telemetry.MetricsPath);
                    o.RecordException = true;
                })
                .AddHttpClientInstrumentation()
                .AddNpgsql();

            if (!string.IsNullOrWhiteSpace(telemetry.Endpoint))
            {
                tracing.AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.Endpoint));
            }
        });

        builder.WithMetrics(metrics =>
        {
            metrics
                .AddMeter(MageRideDiagnostics.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddNpgsqlInstrumentation();

            if (telemetry.PrometheusEnabled)
            {
                metrics.AddPrometheusExporter();
            }

            if (!string.IsNullOrWhiteSpace(telemetry.Endpoint))
            {
                metrics.AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.Endpoint));
            }
        });

        if (telemetry.LoggingEnabled)
        {
            var logResource = ResourceBuilder.CreateDefault();
            ApplyResource(logResource);

            services.AddLogging(logging => logging.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(logResource);
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;

                if (!string.IsNullOrWhiteSpace(telemetry.Endpoint))
                {
                    options.AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.Endpoint));
                }
            }));
        }

        return services;
    }

    /// <summary>Maps the Prometheus scrape endpoint (D7' §12). Anonymous, like the health probes.</summary>
    public static WebApplication MapMageRideMetrics(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var telemetry = app.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value;
        if (!telemetry.PrometheusEnabled)
        {
            return app;
        }

        app.MapPrometheusScrapingEndpoint(telemetry.MetricsPath).AllowAnonymous();
        return app;
    }

    private static bool IsInfrastructurePath(PathString path, string metricsPath) =>
        path.StartsWithSegments(HealthEndpointExtensions.LivePath) ||
        path.StartsWithSegments(HealthEndpointExtensions.ReadyPath) ||
        path.StartsWithSegments(metricsPath);
}
