using MageRide.HotPath.MqttBridge.Bridging;
using MageRide.HotPath.MqttBridge.Configuration;
using MageRide.HotPath.MqttBridge.Throttling;
using MageRide.Shared;
using MageRide.Shared.Mqtt;

namespace MageRide.HotPath.MqttBridge;

/// <summary>
/// Composition root for mqtt-bridge-svc. Lives here rather than in <c>Program.cs</c> so the test
/// suite drives the same pipeline the process runs.
/// </summary>
public static class MqttBridgeApplication
{
    /// <summary>Service name for telemetry and the Kafka client id.</summary>
    public const string ServiceName = "mqtt-bridge-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // The bridge owns no data and answers no request, so a Postgres pool it never uses
            // would still fail readiness when the database blinks.
            UsePostgres = false,
            UseCommandLog = false,
            UseKafka = true,

            // Redis, on the other hand, is load-bearing as of C038. T-05's 20 samples/s/device and
            // D-17's 5 msg/s ceiling are both *per device across the whole group*, and a shared
            // subscription gives each replica a random slice of one device's stream — an in-process
            // bucket would let N replicas pass N times the limit. Both counters therefore live in
            // Redis (RedisKeys.VehiclePublishWindow, RateLimitPolicies.MqttReplay). Losing Redis
            // marks the bridge unready and both checks fail open: ingest continues, and EMQX's own
            // `messages_rate` ceiling is still in force underneath.
            UseRedis = true,

            // No HTTP surface means no bearer to validate. The two health probes are anonymous by
            // construction (the kernel maps them AllowAnonymous), so nothing here is exposed.
            UseAuthentication = false,
        };

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddMageRideMqtt(builder.Configuration);

        builder.Services.AddOptions<MqttBridgeOptions>()
            .Bind(builder.Configuration.GetSection(MqttBridgeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var bridge = builder.Configuration.GetSection(MqttBridgeOptions.SectionName).Get<MqttBridgeOptions>()
                     ?? new MqttBridgeOptions();

        builder.Services.AddSingleton<ReplayThrottle>();
        builder.Services.AddSingleton<PublishRateMonitor>();

        // Registered as singletons and exposed as the hosted services, so a test can read a
        // replica's own counters — "two replicas shared the stream" (E-08) and "the throttle
        // actually bit" (T-05) are not claims a broker-side assertion alone can make.
        builder.Services.AddSingleton<MqttBridgeWorker>();

        if (bridge.Enabled)
        {
            builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttBridgeWorker>());

            if (bridge.MonitorPublishRate)
            {
                builder.Services.AddHostedService(sp => sp.GetRequiredService<PublishRateMonitor>());
            }
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        return app;
    }
}
