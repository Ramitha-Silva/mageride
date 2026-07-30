using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MageRide.Query.Tests.Infrastructure;

/// <summary>
/// Stand-ins for the two services <c>/v1/transport-options</c> delegates to — transit-svc's
/// <c>GET /v1/transit/options</c> (C061) and fare-svc's <c>GET /v1/fare/estimate</c>.
/// </summary>
/// <remarks>
/// <para>
/// Real HTTP on a real socket rather than substituted interfaces, because what is worth asserting is
/// the part between the services: the query strings query-svc builds, the JSON shapes it parses back,
/// and — the fence that matters — that a private tier is constructed <b>without</b> an ETA whatever the
/// downstream returns (AL-19/BR-23.3: a pre-match Mode C tier is price-only, because no driver has been
/// matched and "4 minutes away" would be about a vehicle nobody has reserved).
/// </para>
/// <para>
/// Both live on one instance because query-svc points two named clients at two base URLs and the paths
/// do not collide; a test that wanted them to fail independently sets the other's base URL to nothing.
/// </para>
/// </remarks>
internal sealed class FakeDownstream : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<string> _requests = [];
    private readonly Lock _gate = new();

    private FakeDownstream(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    /// <summary>Every path+query asked for, in order.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>What fare-svc quotes per tier, keyed by canonical vehicle type. Absent tiers 404.</summary>
    public Dictionary<string, long> TierPrices { get; } = new(StringComparer.Ordinal)
    {
        ["three_wheeler"] = 42_000,
        ["sedan"] = 68_000,
    };

    /// <summary>What transit-svc returns. Empty is C061's no-feed degradation.</summary>
    public List<TransitFixture> Routes { get; } =
    [
        new("138", "Kottawa – Pettah", "bus", 0),
        new("EX01", "Colombo – Kandy intercity", "train", 1),
    ];

    public static async Task<FakeDownstream> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        FakeDownstream? instance = null;

        app.MapGet("/v1/transit/options", (HttpContext context) =>
        {
            instance!.Record(context);

            return Results.Json(new
            {
                options = instance.Routes.Select(route => new
                {
                    shortName = route.ShortName,
                    headsign = route.Headsign,
                    vehicleType = route.VehicleType,
                    transfers = route.Transfers,
                }).ToArray(),
            });
        });

        app.MapGet("/v1/fare/estimate", (HttpContext context, string vehicleType) =>
        {
            instance!.Record(context);

            return instance.TierPrices.TryGetValue(vehicleType, out var amount)
                // The real fare-svc 200 carries a token and a breakdown as well; only the two fields
                // query-svc reads are served, so a test failure here means query-svc read something it
                // has no business reading.
                ? Results.Json(new { amountMinor = amount, currency = "LKR" })
                : Results.Json(new { error = "no tariff" }, statusCode: 404);
        });

        await app.StartAsync();

        var baseUrl = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        instance = new FakeDownstream(app, baseUrl);

        return instance;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private void Record(HttpContext context)
    {
        lock (_gate)
        {
            _requests.Add(context.Request.Path + context.Request.QueryString);
        }
    }

    /// <summary>One transit-svc option (C061's shape, only the fields query-svc reads).</summary>
    internal sealed record TransitFixture(string ShortName, string Headsign, string VehicleType, int Transfers);
}
