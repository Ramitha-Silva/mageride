using Dapper;
using MageRide.Analytics.Configuration;
using MageRide.Analytics.Domain;
using MageRide.Analytics.Persistence;
using MageRide.Analytics.Query;
using MageRide.Analytics.Rollup;
using MageRide.Shared.Persistence;
using MageRide.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MageRide.Analytics.Tests.Infrastructure;

/// <summary>
/// The read model wired the way admin-bff (C062) will wire it, over a real migrated Postgres.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composed through the component's own <c>AddMageRideAnalytics…</c> extension</b>, not by
/// newing up the services: the registration is part of what C062 consumes, so a suite that bypassed
/// it would leave the one line of integration surface this component has untested.
/// </para>
/// <para>
/// <b>The rollup job is off.</b> <c>AddMageRideAnalyticsQueriesOnly</c> registers everything except
/// the timer, so a background pass cannot materialise a day underneath an assertion — which would
/// make "the run did it" indistinguishable from "the job did". <c>RollupJobTests</c> is the one
/// place the job itself is started, and it starts it deliberately.
/// </para>
/// <para>
/// <b>The clock is a <see cref="FakeTimeProvider"/>.</b> "Today" in Asia/Colombo is the axis this
/// whole component turns on; a suite on the wall clock would pass or fail depending on which side of
/// 18:30 UTC it happened to run.
/// </para>
/// </remarks>
internal sealed class AnalyticsHarness : IAsyncDisposable
{
    /// <summary>
    /// 09:00 UTC on 15 July 2026 — 14:30 in Colombo. Mid-month and mid-afternoon, so a test that
    /// moves the clock a few hours crosses neither a business date nor a month boundary by accident,
    /// and the UTC and Colombo dates are the same day (which is what makes an accidental UTC
    /// boundary in the production SQL visible rather than hidden).
    /// </summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The Colombo date <see cref="DefaultNow"/> falls on.</summary>
    public static readonly DateOnly Today = new(2026, 7, 15);

    private readonly ServiceProvider _provider;
    private readonly PostgresFixture _postgres;

    private AnalyticsHarness(ServiceProvider provider, PostgresFixture postgres, FakeTimeProvider clock)
    {
        _provider = provider;
        _postgres = postgres;
        Clock = clock;
        Seed = new AnalyticsSeed(postgres);
    }

    public FakeTimeProvider Clock { get; }

    public AnalyticsSeed Seed { get; }

    public IServiceProvider Services => _provider;

    /// <summary>Boots the read model against <paramref name="postgres"/> with a clean slate.</summary>
    /// <param name="settings">Configuration overrides, e.g. <c>Analytics:WeekStartsOn</c>.</param>
    /// <param name="now">The instant the fake clock starts at.</param>
    /// <param name="withJob">Registers the rollup <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> as well.</param>
    public static async Task<AnalyticsHarness> StartAsync(
        PostgresFixture postgres,
        IDictionary<string, string?>? settings = null,
        DateTimeOffset? now = null,
        bool withJob = false)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();
        await ResetAsync(postgres);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer.
            ["Postgres:PgBouncerTransactionMode"] = "false",
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(overrides).Build();
        var clock = new FakeTimeProvider(now ?? DefaultNow);
        var services = new ServiceCollection();

        services.AddLogging(logging =>
        {
            // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a trace.
            if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
            {
                logging.ClearProviders();
            }
        });

        services.AddSingleton<IConfiguration>(configuration);

        // Ahead of the component's own TryAddSingleton, so every business date and every presence
        // cutoff runs on the test's clock.
        services.AddSingleton<TimeProvider>(clock);

        services.AddMageRidePostgres(configuration);

        if (withJob)
        {
            services.AddMageRideAnalytics(configuration);
        }
        else
        {
            services.AddMageRideAnalyticsQueriesOnly(configuration);
        }

        return new AnalyticsHarness(services.BuildServiceProvider(), postgres, clock);
    }

    // -----------------------------------------------------------------------------------------
    // Driving the component
    // -----------------------------------------------------------------------------------------

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        using var scope = _provider.CreateScope();

        return await action(scope.ServiceProvider);
    }

    public Task<RollupRunResult> RollupAsync(DateOnly date) =>
        WithScopeAsync(services =>
            services.GetRequiredService<IAnalyticsRollupService>().RunDayAsync(date, CancellationToken.None));

    public Task<RollupRunResult> RollupRangeAsync(DateOnly from, DateOnly to) =>
        WithScopeAsync(services =>
            services.GetRequiredService<IAnalyticsRollupService>().RunRangeAsync(from, to, CancellationToken.None));

    public Task<RollupRunResult> ScheduledPassAsync() =>
        WithScopeAsync(services =>
            services.GetRequiredService<IAnalyticsRollupService>().RunScheduledPassAsync(CancellationToken.None));

    public Task<DashboardStats> StatsAsync(string? period = null, DateOnly? from = null, DateOnly? to = null) =>
        WithScopeAsync(services =>
            services.GetRequiredService<IDashboardStatsService>().GetAsync(period, from, to, CancellationToken.None));

    public Task<byte[]> CsvAsync(string? period = null, DateOnly? from = null, DateOnly? to = null) =>
        WithScopeAsync(services =>
            services.GetRequiredService<IDashboardStatsService>().ExportCsvAsync(period, from, to, CancellationToken.None));

    public Task<DailyMetric?> MetricAsync(DateOnly date) =>
        WithScopeAsync(services =>
            services.GetRequiredService<DailyMetricsRepository>().FindAsync(date, CancellationToken.None));

    // -----------------------------------------------------------------------------------------
    // Asserting against the database itself
    // -----------------------------------------------------------------------------------------

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>How many days the job has materialised in total.</summary>
    public async Task<int> MetricRowCountAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM analytics.daily_metrics;");
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
    }

    /// <summary>
    /// Empties what this suite seeds, and nothing else.
    /// </summary>
    /// <remarks>
    /// The TestKit shares one container per collection and does not reset between tests, and every
    /// assertion here is a count or a sum over a whole table — "gross fare for the 14th" is a claim
    /// about every payment row in the database, so a ride another test left behind is part of this
    /// test's answer. Users and vehicles are removed by the <c>C061</c> marker they were created
    /// with rather than wholesale, so the migrations' own reference data (<c>iam.roles</c>,
    /// <c>billing.plans</c>, the notification templates) survives.
    /// </remarks>
    private static async Task ResetAsync(PostgresFixture postgres)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            TRUNCATE analytics.daily_metrics;
            DELETE FROM rides.transitions;
            DELETE FROM fares.ride_payments;
            DELETE FROM rides.rides;
            DELETE FROM billing.daily_fee_charges;
            DELETE FROM dispatch.driver_presence;
            DELETE FROM registry.document_fields;
            DELETE FROM registry.documents;
            DELETE FROM registry.onboarding_steps;
            DELETE FROM support.tickets;
            DELETE FROM registry.fleets WHERE name LIKE 'C061%';
            DELETE FROM registry.vehicles WHERE registration_number LIKE 'C061%';
            DELETE FROM iam.user_roles
             WHERE user_id IN (SELECT id FROM iam.users WHERE first_name LIKE 'C061%');
            DELETE FROM iam.users WHERE first_name LIKE 'C061%';
            """);
    }
}
