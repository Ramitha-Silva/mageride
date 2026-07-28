using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MageRide.Shared.Mqtt;

public static class MqttServiceCollectionExtensions
{
    /// <summary>
    /// Broker settings and the MQTT session-token issuer (D6' §3).
    /// </summary>
    /// <remarks>
    /// Not part of <c>AddMageRideDefaults</c>: only the components that actually speak to EMQX —
    /// mqtt-bridge-svc, the TCP adapters (C043) and, once it exists, provisioning-svc (C030) — have
    /// any business holding the session-token secret. A service that never touches the device plane
    /// should fail to compile against a token issuer, not silently carry the key to mint one.
    /// </remarks>
    public static IServiceCollection AddMageRideMqtt(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MqttOptions>()
            .Bind(configuration.GetSection(MqttOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<MqttSessionTokenIssuer>();

        return services;
    }
}
