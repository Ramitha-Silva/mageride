using MageRide.Analytics.Configuration;
using MageRide.Analytics.Persistence;
using MageRide.Analytics.Query;
using MageRide.Analytics.Rollup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MageRide.Analytics;

/// <summary>
/// The one call that gives a host the AL-38 read model: the rollup job, the period query and the
/// CSV export.
/// </summary>
/// <remarks>
/// <para>
/// Called by admin-bff (C062) after <c>AddMageRideDefaults</c> — this component reaches Postgres
/// through the kernel's <see cref="Shared.Persistence.INpgsqlConnectionFactory"/> and registers no
/// connection of its own.
/// </para>
/// <para>
/// <b>No endpoints and no authorization.</b> There is no <c>MapAnalyticsEndpoints</c> here on
/// purpose: <c>GET /v1/admin/dashboard/stats</c> is an operation of <c>admin-bff.yaml</c>, and
/// AL-06's deny-by-default plus D-35's audit interceptor belong where the caller's effective role
/// set is known. This assembly answers questions; it does not decide who may ask them.
/// </para>
/// </remarks>
public static class AnalyticsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the read model and the scheduled materialisation job.
    /// <paramref name="configuration"/> supplies the <c>Analytics</c> section.
    /// </summary>
    public static IServiceCollection AddMageRideAnalytics(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMageRideAnalyticsQueriesOnly(configuration);
        services.AddHostedService<AnalyticsRollupJob>();

        return services;
    }

    /// <summary>
    /// The read model without the scheduled job — for a host that only reads, or for a test that
    /// drives the rollup itself.
    /// </summary>
    /// <remarks>
    /// <see cref="IAnalyticsRollupService"/> is still registered: the difference is a timer, not a
    /// capability. Required in tests, because a timer firing underneath an assertion makes "the run
    /// did it" indistinguishable from "the job did" — the same reason C060's harness leaves its
    /// runner off by default.
    /// </remarks>
    public static IServiceCollection AddMageRideAnalyticsQueriesOnly(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<AnalyticsOptions>()
            .Bind(configuration.GetSection(AnalyticsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Scoped, so a repository's connection lifetime is the request's or the rollup pass's and
        // never the process's.
        services.TryAddScoped<DailyMetricsRepository>();
        services.TryAddScoped<LiveCountersRepository>();
        services.TryAddScoped<IAnalyticsRollupService, AnalyticsRollupService>();
        services.TryAddScoped<IDashboardStatsService, DashboardStatsService>();

        return services;
    }
}
