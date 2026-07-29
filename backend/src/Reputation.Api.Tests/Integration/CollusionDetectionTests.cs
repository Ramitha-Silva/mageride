using Dapper;
using MageRide.Reputation.Counters;
using MageRide.Reputation.Detection;
using MageRide.Reputation.Domain;
using MageRide.Reputation.Tests.Infrastructure;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Reputation.Tests.Integration;

/// <summary>
/// <b>DoD 3:</b> "a synthetic collusion pattern raises <c>fraud.suspected</c> exactly once per
/// detection window" — the three E-07 detectors and the uniqueness that bounds them.
/// </summary>
[Collection(ReputationCollection.Name)]
public sealed class CollusionDetectionTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>E-07's headline pattern: the same pair, over and over.</summary>
    [Fact]
    public async Task A_repeated_pair_is_flagged_exactly_once_per_window()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateDriverAsync();

        // Ten completed rides between one pair — over the default threshold of eight.
        await CompleteRidesAsync(harness, passenger, driver, 10);

        var first = await RunDetectorAsync(harness);
        var flag = Assert.Single(first, row => row.SubjectId == passenger && row.Kind == FraudFlagKinds.RepeatPair);

        Assert.Equal(driver, flag.RelatedId);
        Assert.Equal("open", flag.Status);
        Assert.Equal("2026-07-28", flag.WindowKey);
        Assert.Contains("10 completed rides", flag.Detail, StringComparison.Ordinal);

        // Second and third passes inside the same window find the same pattern and raise nothing.
        // This is the DoD: the detector's cadence must not fill the admin queue.
        //
        // Scoped to this test's own pair rather than asserting the pass raised nothing at all: the
        // TestKit shares one Postgres per collection, so another test's seeded device or
        // observation is a real signal that this pass will legitimately raise.
        Assert.Empty(await RaisedForAsync(harness, passenger));
        Assert.Empty(await RaisedForAsync(harness, passenger));

        Assert.Equal(1, await harness.CountFlagsAsync(passenger, FraudFlagKinds.RepeatPair));

        // And exactly one fraud.suspected — an admin queue fed by the topic sees it once too.
        var events = await harness.ReadOutboxAsync(passenger);
        var suspected = Assert.Single(events, e => e.EventType == ReputationEventTypes.FraudSuspected);
        Assert.Equal(FraudFlagKinds.RepeatPair, suspected.String("kind"));
        Assert.Equal(driver.ToString(), suspected.String("relatedId"));
        Assert.Equal("2026-07-28", suspected.String("windowKey"));
    }

    /// <summary>A new detection window is a new fact, and is raised again.</summary>
    [Fact]
    public async Task The_next_window_raises_the_pattern_again()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateDriverAsync();

        await CompleteRidesAsync(harness, passenger, driver, 10);

        Assert.NotEmpty(await RaisedForAsync(harness, passenger));
        Assert.Empty(await RaisedForAsync(harness, passenger));

        harness.Clock.Advance(TimeSpan.FromDays(1));

        var tomorrow = Assert.Single(
            await RunDetectorAsync(harness), row => row.SubjectId == passenger);

        Assert.Equal("2026-07-29", tomorrow.WindowKey);
        Assert.Equal(2, await harness.CountFlagsAsync(passenger, FraudFlagKinds.RepeatPair));
    }

    /// <summary>An honest pair below the threshold is not flagged.</summary>
    [Fact]
    public async Task A_pair_below_the_threshold_is_not_flagged()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateDriverAsync();

        await CompleteRidesAsync(harness, passenger, driver, 7);

        Assert.Empty(await RaisedForAsync(harness, passenger));
        Assert.Equal(0, await harness.CountFlagsAsync(passenger, FraudFlagKinds.RepeatPair));
    }

    /// <summary>Rides outside the 30-day window do not add up to a pattern.</summary>
    [Fact]
    public async Task Rides_outside_the_pair_window_do_not_count()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateDriverAsync();

        await CompleteRidesAsync(harness, passenger, driver, 10);

        harness.Clock.Advance(TimeSpan.FromDays(31));

        Assert.Empty(await RaisedForAsync(harness, passenger));
    }

    /// <summary>E-07's device-binding cross-check, over <c>iam.devices.device_key</c> (AL-08).</summary>
    [Fact]
    public async Task Two_accounts_on_one_device_binding_are_flagged()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var deviceKey = $"device-{Guid.NewGuid()}";
        var first = await harness.CreateUserAsync();
        var second = await harness.CreateDriverAsync();

        await harness.BindDeviceAsync(first, deviceKey);
        await harness.BindDeviceAsync(second, deviceKey);

        var raised = await RunDetectorAsync(harness);
        var flags = raised.Where(row => row.Kind == FraudFlagKinds.SharedDevice).ToArray();

        // One flag per account: the admin queue is per subject, and a single flag naming both would
        // be actionable against neither.
        Assert.Equal(2, flags.Length);
        Assert.Contains(flags, flag => flag.SubjectId == first && flag.RelatedId == second);
        Assert.Contains(flags, flag => flag.SubjectId == second && flag.RelatedId == first);

        // The device key itself is not published — the account list is what an admin works from.
        Assert.All(flags, flag => Assert.DoesNotContain(deviceKey, flag.Detail ?? string.Empty, StringComparison.Ordinal));

        Assert.Empty(await RaisedForAsync(harness, first));
    }

    /// <summary>
    /// E-07's IP/ASN clustering, over the observations <c>POST /v1/internal/reputation/observations</c>
    /// records. Nothing produces those yet (migration 0805), so this is what proves the detector
    /// works once something does.
    /// </summary>
    [Fact]
    public async Task Accounts_clustered_on_one_address_are_flagged()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var users = new List<Guid>();

        for (var i = 0; i < 4; i++)
        {
            var user = await harness.CreateUserAsync();
            users.Add(user);

            var response = await harness.PostAsync(
                "/v1/internal/reputation/observations",
                new { userId = user.ToString(), ip = "203.0.113.9", asn = 45_489, userAgent = "MageRide/1.0" },
                bearer: null,
                internalKey: ReputationHarness.InternalApiKey);

            Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        }

        var raised = await RunDetectorAsync(harness);
        var flags = raised.Where(row => row.Kind == FraudFlagKinds.NetworkCluster && users.Contains(row.SubjectId!.Value))
            .ToArray();

        Assert.Equal(4, flags.Length);

        // The address is personal data (E-06) and is not in the flag; the ASN is not, and is.
        Assert.All(flags, flag =>
        {
            Assert.DoesNotContain("203.0.113.9", flag.Detail ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("accounts seen on one", flag.Detail ?? string.Empty, StringComparison.Ordinal);
        });
    }

    /// <summary>The intake is closed to a caller without the interim internal key (D3' §0, C042).</summary>
    [Fact]
    public async Task The_observation_intake_refuses_a_caller_without_the_key()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var user = await harness.CreateUserAsync();

        var response = await harness.PostAsync(
            "/v1/internal/reputation/observations",
            new { userId = user.ToString(), ip = "203.0.113.9" },
            bearer: null);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>A flag never blocks anybody by itself (ADD §12.6, and this component's fence).</summary>
    [Fact]
    public async Task Raising_a_flag_changes_no_block_state()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateDriverAsync();

        await CompleteRidesAsync(harness, passenger, driver, 10);
        Assert.NotEmpty(await RaisedForAsync(harness, passenger));

        await using var scope = harness.Services.CreateAsyncScope();
        var reputation = scope.ServiceProvider.GetRequiredService<IReputationService>();

        Assert.Equal(BlockStates.Ok, (await reputation.GetStatusAsync(passenger, default)).State);
        Assert.Equal(BlockStates.Ok, (await reputation.GetStatusAsync(driver, default)).State);
    }

    // -----------------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<FraudFlagRow>> RunDetectorAsync(ReputationHarness harness)
    {
        await using var scope = harness.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<ICollusionDetector>().RunAsync(default);
    }

    /// <summary>
    /// One detection pass, narrowed to the flags it raised about <paramref name="subjectId"/>.
    /// </summary>
    /// <remarks>
    /// The TestKit shares one Postgres per collection and every test seeds into it, so "the pass
    /// raised nothing" is not a claim this suite can make — another test's device binding or
    /// network observation is a genuine signal. Every idempotency assertion here is therefore about
    /// one subject, which is the level the DoD's "exactly once per detection window" is about.
    /// </remarks>
    private static async Task<IReadOnlyList<FraudFlagRow>> RaisedForAsync(ReputationHarness harness, Guid subjectId)
    {
        var raised = await RunDetectorAsync(harness);

        return [.. raised.Where(row => row.SubjectId == subjectId)];
    }

    /// <summary>
    /// Completes <paramref name="count"/> rides between one pair, through the real intake — so the
    /// detector reads exactly what the <c>ride.events</c> consumer would have written.
    /// </summary>
    private static async Task CompleteRidesAsync(
        ReputationHarness harness, Guid passenger, Guid driver, int count)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var reputation = scope.ServiceProvider.GetRequiredService<IReputationService>();

        for (var i = 0; i < count; i++)
        {
            var rideId = Guid.NewGuid();
            var eventId = Guid.NewGuid();

            foreach (var (subject, role) in new[]
                     {
                         (passenger, SubjectRoles.Passenger),
                         (driver, SubjectRoles.Driver),
                     })
            {
                await reputation.RecordAsync(
                    new ReputationFact(
                        DedupeKey: $"{IntakeSources.RideEvents}:{eventId}:{role}",
                        Kind: IntakeKinds.Completion,
                        SubjectId: subject,
                        SubjectRole: role,
                        RideId: rideId,
                        Source: IntakeSources.RideEvents),
                    default);
            }
        }
    }
}
