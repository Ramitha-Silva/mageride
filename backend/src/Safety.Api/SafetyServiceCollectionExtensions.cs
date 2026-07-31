using MageRide.Safety.Clients;
using MageRide.Safety.Configuration;
using MageRide.Safety.Live;
using MageRide.Safety.Persistence;
using MageRide.Safety.Reports;
using MageRide.Safety.Sharing;
using MageRide.Safety.Sos;
using MageRide.Shared.RateLimiting;
using ReputationClient = MageRide.Reputation.Grpc.Reputation.ReputationClient;

namespace MageRide.Safety;

/// <summary>safety-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class SafetyServiceCollectionExtensions
{
    public static IServiceCollection AddSafetyServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SafetyOptions>()
            .Bind(configuration.GetSection(SafetyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var settings = configuration.GetSection(SafetyOptions.SectionName).Get<SafetyOptions>() ?? new SafetyOptions();

        // Singletons: each holds a connection factory and a handful of SQL strings.
        services.AddSingleton<ISosRepository, SosRepository>();
        services.AddSingleton<IShareTokenRepository, ShareTokenRepository>();
        services.AddSingleton<ITripReadRepository, TripReadRepository>();
        services.AddSingleton<IReportRepository, ReportRepository>();
        services.AddSingleton<IDriverDirectory, DriverDirectory>();
        services.AddSingleton<ILocationRequestAuditRepository, LocationRequestAuditRepository>();
        services.AddSingleton<ILivePositionReader, LivePositionReader>();
        services.AddSingleton<ITokenBucketRateLimiter, RedisTokenBucketRateLimiter>();
        services.AddSingleton<INotificationClient, NotificationClient>();

        // Scoped: each takes IUnitOfWorkFactory, which the kernel registers scoped so one request
        // holds at most one transaction at a time.
        services.AddScoped<ISosService, SosService>();
        services.AddScoped<IReportService, ReportService>();

        // The share service takes no unit of work — every one of its writes is a single guarded
        // statement — so it is a singleton like the repositories it composes.
        services.AddSingleton<ITripShareService, TripShareService>();

        // The D-33 hop. Its timeout is bounded by the SLO rather than by D6' §8.3's internal
        // default: the alert is *delivered* on this call, so it has to cover two gateways answering
        // in parallel and still fit inside five seconds.
        //
        // **No resilience pipeline.** A retry inside the client would spend the budget the SLO is
        // measured against, and the alert is already durable before the hop is made — an SOS that
        // reached no gateway is recorded as such rather than retried into the p99.
        services.AddHttpClient(NotificationClient.HttpClientName, client =>
        {
            if (!string.IsNullOrWhiteSpace(settings.NotificationBaseUrl))
            {
                client.BaseAddress = new Uri(
                    settings.NotificationBaseUrl.EndsWith('/') ? settings.NotificationBaseUrl : settings.NotificationBaseUrl + "/");
            }

            client.Timeout = settings.NotificationTimeout;
        });

        AddReputationReporter(services, settings);

        return services;
    }

    /// <summary>
    /// US-12.5's counter hop. Registered whether or not reporting is enabled, so the composition
    /// root stays one shape and turning the flag on needs no other change (C034's arrangement).
    /// </summary>
    /// <remarks>
    /// <c>AddGrpcClient</c> rather than a hand-built channel: the channel then lives in
    /// <c>IHttpClientFactory</c>'s handler pool with every other outbound hop, so it is rotated,
    /// instrumented and configured the same way. HTTP/2 without TLS is the interim — D3' §0 puts
    /// this family on mTLS and the mesh is what will provide it; until then the hop carries the
    /// shared secret <see cref="ReputationReporter.InternalKeyHeader"/>.
    /// </remarks>
    private static void AddReputationReporter(IServiceCollection services, SafetyOptions settings)
    {
        services.AddGrpcClient<ReputationClient>(client =>
            client.Address = new Uri(settings.ReputationGrpcAddress));

        services.AddSingleton<IReputationReporter, ReputationReporter>();
    }
}
