using System.Text.Json;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MageRide.Shared.Health;

public static class HealthEndpointExtensions
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    /// <summary>
    /// Maps the two probes D7' §5.1 specifies for a stateless .NET service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>/health/live</c> answers as long as the process is serving: it runs no checks at all, so
    /// a Redis outage cannot get healthy pods restarted.
    /// </para>
    /// <para>
    /// <c>/health/ready</c> runs the <see cref="HealthTags.Ready"/> checks — DB, Redis and Kafka —
    /// and failing it only takes the pod out of the load-balancer.
    /// </para>
    /// <para>
    /// Both are anonymous: kubelet sends no bearer token, and the deny-by-default fallback policy
    /// (AL-06) would otherwise 401 every probe.
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapMageRideHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = static _ => false,
            ResponseWriter = WriteResponseAsync,
        }).AllowAnonymous().WithName("health-live");

        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions
        {
            Predicate = static registration => registration.Tags.Contains(HealthTags.Ready),
            ResponseWriter = WriteResponseAsync,
        }).AllowAnonymous().WithName("health-ready");

        return endpoints;
    }

    private static async Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = entry.Value.Duration.TotalMilliseconds,
                // The exception is deliberately not serialised: a connection-refused message
                // carries the DSN, and the probe response is not an authenticated surface.
                error = entry.Value.Exception?.GetType().Name,
            }).ToArray(),
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, MageRideJson.Options), context.RequestAborted);
    }
}
