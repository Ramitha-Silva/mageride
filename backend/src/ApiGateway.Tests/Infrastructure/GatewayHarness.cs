using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MageRide.ApiGateway.Tests.Infrastructure;

/// <summary>
/// A running gateway plus the stub it proxies to. Built through
/// <see cref="GatewayApplication.Build"/>, so the pipeline under test is the one the process runs.
/// </summary>
internal sealed class GatewayHarness : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly StubUpstream _upstream;

    private GatewayHarness(WebApplication app, StubUpstream upstream, HttpClient client, string baseAddress)
    {
        _app = app;
        _upstream = upstream;
        Client = client;
        BaseAddress = baseAddress;
    }

    public HttpClient Client { get; }

    public string BaseAddress { get; }

    public StubUpstream Upstream => _upstream;

    public IServiceProvider Services => _app.Services;

    /// <summary>Cluster ids declared in <c>gateway-routes.json</c>, read from the shipped file.</summary>
    public static IReadOnlyList<string> ClusterIds { get; } = ReadClusterIds();

    /// <summary>Route ids declared in <c>gateway-routes.json</c>.</summary>
    public static IReadOnlyDictionary<string, string> RouteClusters { get; } = ReadRouteClusters();

    public static async Task<GatewayHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        var upstream = await StubUpstream.StartAsync();

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            // One stub stands in for all 21 clusters; a test asserts which cluster was chosen
            // through the X-MageRide-Upstream response header instead of by address.
            ["Gateway:StateStore"] = "Memory",
            ["Gateway:EmitUpstreamHeader"] = "true",
            ["Gateway:Attestation:Mode"] = "Disabled",
            ["Otel:PrometheusEnabled"] = "false",
        };

        foreach (var cluster in ClusterIds)
        {
            overrides[$"ReverseProxy:Clusters:{cluster}:Destinations:primary:Address"] = upstream.BaseAddress;
        }

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var app = GatewayApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Staging,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a stack trace.
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }
                builder.Configuration.AddInMemoryCollection(overrides);
                builder.WebHost.UseUrls("http://127.0.0.1:0");
            });

        await app.StartAsync();

        var baseAddress = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        var client = new HttpClient
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = TimeSpan.FromSeconds(60),
        };

        return new GatewayHarness(app, upstream, client, baseAddress);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    private static JsonElement RoutesDocument()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "gateway-routes.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("ReverseProxy").Clone();
    }

    private static IReadOnlyList<string> ReadClusterIds() =>
        [.. RoutesDocument().GetProperty("Clusters").EnumerateObject()
            .Where(static p => !p.Name.StartsWith("//", StringComparison.Ordinal))
            .Select(static p => p.Name)];

    private static IReadOnlyDictionary<string, string> ReadRouteClusters() =>
        RoutesDocument().GetProperty("Routes").EnumerateObject()
            .Where(static p => !p.Name.StartsWith("//", StringComparison.Ordinal))
            .ToDictionary(static p => p.Name, static p => p.Value.GetProperty("ClusterId").GetString()!, StringComparer.Ordinal);
}
