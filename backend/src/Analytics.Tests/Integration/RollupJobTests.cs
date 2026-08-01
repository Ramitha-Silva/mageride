using MageRide.Analytics.Rollup;
using MageRide.Analytics.Tests.Infrastructure;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MageRide.Analytics.Tests.Integration;

/// <summary>
/// The scheduled materialisation job itself.
/// </summary>
/// <remarks>
/// Every other suite drives <see cref="Analytics.Rollup.IAnalyticsRollupService"/> directly and
/// leaves the timer off, because a background pass materialising a day underneath an assertion makes
/// "the run did it" indistinguishable from "the job did". These two tests turn it on and assert
/// exactly the thing that switch hides.
/// </remarks>
[Collection<AnalyticsCollection>]
public sealed class RollupJobTests(PostgresFixture postgres)
{
    /// <summary>The job's first pass covers today and the lookback window, with no tick needed.</summary>
    /// <remarks>
    /// A <c>do … while (WaitForNextTickAsync)</c> rather than a <c>while</c>: a pod that has just
    /// started should not leave the dashboard stale for a whole interval before its first pass.
    /// </remarks>
    [Fact]
    public async Task The_job_materialises_its_window_on_start()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await AnalyticsHarness.StartAsync(
            postgres,
            new Dictionary<string, string?>
            {
                ["Analytics:RollupLookbackDays"] = "3",
                ["Analytics:RollupInterval"] = "00:15:00",
            },
            withJob: true);

        var job = harness.Services.GetServices<IHostedService>().OfType<AnalyticsRollupJob>().Single();

        await job.StartAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(async () => await harness.MetricRowCountAsync() == 3);

            Assert.NotNull(await harness.MetricAsync(AnalyticsHarness.Today));
            Assert.NotNull(await harness.MetricAsync(AnalyticsHarness.Today.AddDays(-2)));

            var first = (await harness.MetricAsync(AnalyticsHarness.Today))!.RefreshedAt;

            // The timer runs on the same fake clock, so a whole interval is a statement rather
            // than a wait.
            harness.Clock.Advance(TimeSpan.FromMinutes(15));

            await WaitUntilAsync(async () => (await harness.MetricAsync(AnalyticsHarness.Today))!.RefreshedAt > first);

            // Still three rows: a second pass recomputes, it does not accumulate.
            Assert.Equal(3, await harness.MetricRowCountAsync());
        }
        finally
        {
            await job.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Switched off, nothing is ever materialised — and the switch is loud about it, because the
    /// failure is invisible from the outside: the endpoint keeps answering and only the period
    /// figures quietly stop moving.
    /// </summary>
    [Fact]
    public async Task A_disabled_job_materialises_nothing()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await AnalyticsHarness.StartAsync(
            postgres,
            new Dictionary<string, string?> { ["Analytics:RollupEnabled"] = "false" },
            withJob: true);

        var job = harness.Services.GetServices<IHostedService>().OfType<AnalyticsRollupJob>().Single();

        await job.StartAsync(CancellationToken.None);

        try
        {
            harness.Clock.Advance(TimeSpan.FromHours(2));
            await Task.Delay(250);

            Assert.Equal(0, await harness.MetricRowCountAsync());

            // A backfill still works — the switch is the timer, not the capability.
            await harness.RollupAsync(AnalyticsHarness.Today);
            Assert.Equal(1, await harness.MetricRowCountAsync());
        }
        finally
        {
            await job.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Polls a condition the background pass will satisfy, rather than sleeping for a guess.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"The rollup job did not reach the expected state within {timeoutMs} ms.");
    }
}
