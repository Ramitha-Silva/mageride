using MageRide.ApiGateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// Which configuration source decides a cluster's upstream address.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> <c>GatewayApplication.Build</c> adds
/// <c>gateway-routes.json</c> <i>after</i> <c>WebApplication.CreateBuilder</c> has already added the
/// environment and the command line, and the last source added wins — so until Δ C125 the shipped
/// file outranked both, and every
/// <c>ReverseProxy__Clusters__*__Destinations__primary__Address</c> in the repository was silently
/// ignored. Two deployment descriptors depended on that override and both were therefore dead:
/// <c>infra/docker-compose.dev.yml</c> pointed all 24 clusters at <c>http://app-services:5000/</c>
/// (right, because the 22 domain services are co-located in that container) and
/// <c>infra/k8s/base/config/service-endpoints.yaml</c> pointed them at <c>http://iam-svc/</c>
/// (right, because the generated Service listens on port 80). Both would have used the file's
/// <c>http://iam-svc:5000/</c> instead: a host that does not exist in compose, and a port the
/// Kubernetes Service does not expose. Every route, 502, in every environment.
/// </para>
/// <para>
/// These four tests pin the whole chain so neither the behaviour nor the claim can drift again:
/// the file loses to the environment, the environment loses to the command line, a source added
/// through <c>configure</c> beats all of them, and the file still supplies the value when nothing
/// overrides it. Every override inside this repository uses the <c>configure</c> mechanism,
/// including <see cref="Infrastructure.GatewayHarness"/>'s.
/// </para>
/// </remarks>
public sealed class ClusterAddressPrecedenceTests
{
    private const string Key = "ReverseProxy:Clusters:iam-svc:Destinations:primary:Address";
    private const string EnvironmentKey =
        "ReverseProxy__Clusters__iam-svc__Destinations__primary__Address";

    [Fact]
    public void The_shipped_file_supplies_the_address_when_nothing_overrides_it()
    {
        using var application = Build(configure: null);

        Assert.Equal("http://iam-svc:5000/", application.Configuration[Key]);
    }

    [Fact]
    public void A_source_added_through_configure_wins_because_it_is_added_after_the_file()
    {
        using var application = Build(builder => builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { [Key] = "http://127.0.0.1:5101/" }));

        Assert.Equal("http://127.0.0.1:5101/", application.Configuration[Key]);
    }

    /// <summary>
    /// The mechanism both deployment descriptors depend on.
    /// </summary>
    /// <remarks>
    /// This test failed before Δ C125 and is the reason the fix exists. `CreateBuilder` adds the
    /// environment source, then `Build` added `gateway-routes.json` on top of it, and the last source
    /// wins — so the replica's compose (24 clusters at <c>http://app-services:5000/</c>) and
    /// Kubernetes's ConfigMap (<c>http://iam-svc/</c>) were both silently ignored in favour of a host
    /// that does not exist in compose and a port the Kubernetes Service does not expose.
    /// </remarks>
    [Fact]
    public void An_environment_variable_wins_over_the_shipped_file()
    {
        var restore = Environment.GetEnvironmentVariable(EnvironmentKey);
        Environment.SetEnvironmentVariable(EnvironmentKey, "http://app-services:5000/");

        try
        {
            using var application = Build(configure: null);

            Assert.Equal("http://app-services:5000/", application.Configuration[Key]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentKey, restore);
        }
    }

    /// <summary>The command line outranks the environment, as it does everywhere else in .NET.</summary>
    [Fact]
    public void The_command_line_wins_over_an_environment_variable()
    {
        var restore = Environment.GetEnvironmentVariable(EnvironmentKey);
        Environment.SetEnvironmentVariable(EnvironmentKey, "http://app-services:5000/");

        try
        {
            using var application = GatewayApplication.Build(
                new WebApplicationOptions
                {
                    EnvironmentName = Environments.Staging,
                    ContentRootPath = AppContext.BaseDirectory,
                    Args = [$"--{Key}=http://from-the-command-line/"],
                },
                builder =>
                {
                    builder.Configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["Gateway:StateStore"] = "Memory",
                            ["Gateway:Attestation:Mode"] = "Disabled",
                            ["Otel:PrometheusEnabled"] = "false",
                        });
                    builder.Logging.ClearProviders();
                });

            Assert.Equal("http://from-the-command-line/", application.Configuration[Key]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentKey, restore);
        }
    }

    private static WebApplication Build(Action<WebApplicationBuilder>? configure) =>
        GatewayApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Staging,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                builder.Configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        // Reaches no Redis and starts no listener: these tests read the resolved
                        // configuration and never serve a request.
                        ["Gateway:StateStore"] = "Memory",
                        ["Gateway:Attestation:Mode"] = "Disabled",
                        ["Otel:PrometheusEnabled"] = "false",
                    });

                builder.Logging.ClearProviders();
                configure?.Invoke(builder);
            });
}
