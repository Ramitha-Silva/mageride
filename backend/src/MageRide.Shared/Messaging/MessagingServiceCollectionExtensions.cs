using MageRide.Shared.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace MageRide.Shared.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// The Redpanda producer and its readiness health check (D6' §2, D7' §5.1).
    /// </summary>
    public static IServiceCollection AddMageRideKafka(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<KafkaOptions>()
            .Bind(configuration.GetSection(KafkaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IEventPublisher, KafkaEventPublisher>();

        services.AddHealthChecks().AddCheck<KafkaHealthCheck>(
            "kafka",
            HealthStatus.Unhealthy,
            [HealthTags.Ready, HealthTags.Messaging]);

        return services;
    }

    /// <summary>
    /// The transactional outbox: the writer services call inside their transaction, and (unless
    /// disabled) the LISTEN/NOTIFY dispatcher that drains it (D6' §2.4, R-13, E-09).
    /// </summary>
    /// <remarks>
    /// Only services that own an outbox table call this — today ride-svc and dispatch-svc.
    /// Configure the table, channel and topic under the <c>Outbox</c> section; the defaults
    /// describe ride-svc.
    /// </remarks>
    public static IServiceCollection AddMageRideOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IOutboxWriter, OutboxWriter>();
        services.TryAddSingleton(TimeProvider.System);

        var outbox = configuration.GetSection(OutboxOptions.SectionName).Get<OutboxOptions>() ?? new OutboxOptions();
        if (outbox.DispatcherEnabled)
        {
            services.AddSingleton<OutboxDispatcher>();
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OutboxDispatcher>());
        }

        return services;
    }
}
