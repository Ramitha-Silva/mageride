using MageRide.Fare.Endpoints;
using MageRide.Fare.Estimates;
using MageRide.Shared;
using MageRide.Shared.Fares;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MageRide.Fare;

/// <summary>
/// Composition root for the C022 fare-svc <b>stub</b>. Lives here rather than in
/// <c>Program.cs</c> so the test suite drives the same pipeline the process runs.
/// </summary>
public static class FareApplication
{
    /// <summary>Service name for telemetry and the Kafka/Postgres client id.</summary>
    public const string ServiceName = "fare-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // The stub prices from a hard-coded table and holds no state at all: no tariff read,
            // no payment row, no cache, no event. Declaring those dependencies would give the
            // service a readiness probe that fails while it is perfectly able to serve every
            // request it can answer. C049/C050 turn Postgres, Redis and Kafka back on together
            // with the tables and events that need them.
            UsePostgres = false,
            UseRedis = false,
            UseKafka = false,
            UseCommandLog = false,
            UseOutbox = false,
        };

        builder.AddMageRideDefaults(serviceOptions);

        // The issuing half of the fareEstimateToken contract; ride-svc registers the same codec
        // and verifies with the same key.
        builder.Services.AddMageRideFareTokens(builder.Configuration);
        builder.Services.AddSingleton<FareEstimator>();

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapFareEndpoints();

        // Loud, once, at start-up. Anyone who finds this line in a production log has shipped a
        // price that ignores distance detours and every peak window.
        app.Logger.LogWarning(
            "fare-svc is running the C022 walking-skeleton STUB: straight-line distance, no peak or " +
            "night surcharge, a hard-coded tariff table and no payment endpoints. C049/C050 replace it.");

        return app;
    }
}
