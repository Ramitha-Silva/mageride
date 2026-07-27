using System.Net;
using System.Text.Json;
using MageRide.Shared.Auth;
using MageRide.Shared.Health;
using MageRide.Shared.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MageRide.Shared.Tests.Health;

/// <summary>The two probes D7' §5.1 specifies for a stateless .NET service.</summary>
public sealed class HealthEndpointTests
{
    private static WebApplication BuildApp(HealthStatus dependencyStatus)
    {
        var builder = TestHosts.CreateBuilder();

        builder.Services.AddMageRideAuthorization();
        builder.Services.AddHealthChecks()
            .AddCheck("fake-dependency", () => new HealthCheckResult(dependencyStatus), [HealthTags.Ready, HealthTags.Database]);

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthorization();
        app.MapMageRideHealthChecks();

        return app;
    }

    /// <summary>
    /// Liveness must stay green while a dependency is down — otherwise a Redis outage gets every
    /// healthy pod restarted instead of just taken out of the load balancer.
    /// </summary>
    [Fact]
    public async Task Liveness_stays_healthy_when_a_dependency_is_down()
    {
        await using var app = BuildApp(HealthStatus.Unhealthy);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri(HealthEndpointExtensions.LivePath, UriKind.Relative));
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", payload.GetProperty("status").GetString());
        Assert.Empty(payload.GetProperty("checks").EnumerateArray());
    }

    [Fact]
    public async Task Readiness_reports_the_dependency_checks()
    {
        await using var app = BuildApp(HealthStatus.Healthy);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri(HealthEndpointExtensions.ReadyPath, UriKind.Relative));
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fake-dependency", payload.GetProperty("checks")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Readiness_fails_when_a_dependency_is_unreachable()
    {
        await using var app = BuildApp(HealthStatus.Unhealthy);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri(HealthEndpointExtensions.ReadyPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// kubelet sends no bearer token; with deny-by-default authorization (AL-06) an unprotected
    /// probe would 401 and the pod would never become ready.
    /// </summary>
    [Fact]
    public async Task Both_probes_are_anonymous_under_deny_by_default_authorization()
    {
        await using var app = BuildApp(HealthStatus.Healthy);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var live = await client.GetAsync(new Uri(HealthEndpointExtensions.LivePath, UriKind.Relative));
        var ready = await client.GetAsync(new Uri(HealthEndpointExtensions.ReadyPath, UriKind.Relative));

        Assert.NotEqual(HttpStatusCode.Unauthorized, live.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, ready.StatusCode);
    }
}
