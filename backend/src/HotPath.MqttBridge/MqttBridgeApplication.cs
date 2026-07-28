using MageRide.HotPath.MqttBridge.Bridging;
using MageRide.HotPath.MqttBridge.Configuration;
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

            // The bridge owns no data and answers no request. It holds one broker connection and
            // one Kafka producer, so everything else the kernel offers is dead weight here — and a
            // Postgres pool it never uses would still fail readiness when the database blinks.
            UsePostgres = false,
            UseRedis = false,
            UseCommandLog = false,
            UseKafka = true,

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

        // Registered as a singleton and exposed as the hosted service, so a test can read the
        // replica's own forwarded count — "two replicas shared the stream" (E-08) is not a claim a
        // broker-side assertion alone can make.
        builder.Services.AddSingleton<MqttBridgeWorker>();

        var bridge = builder.Configuration.GetSection(MqttBridgeOptions.SectionName).Get<MqttBridgeOptions>()
                     ?? new MqttBridgeOptions();

        if (bridge.Enabled)
        {
            builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttBridgeWorker>());
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        return app;
    }
}
