using MageRide.Reputation.Counters;
using MageRide.Reputation.Domain;
using MageRide.Reputation.Tests.Infrastructure;
using MageRide.Reputation.Workers;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Reputation.Tests.Integration;

/// <summary>
/// <b>DoD 1 and 4:</b> "block_status transitions match the D5 rules for cancellation, no-show and
/// report thresholds" and "a completed ride resets the consecutive-cancellation counter".
/// </summary>
/// <remarks>
/// Driven through <see cref="IReputationService"/> against a real Postgres, because everything
/// asserted here is a database guarantee: the <c>FOR UPDATE</c> read-modify-write, the intake
/// ledger's exactly-once claim, and the outbox row that commits with the state change.
/// </remarks>
[Collection(ReputationCollection.Name)]
public sealed class BlockStatusTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>D5' §7.2 / US-6A.10b / AL-16, end to end.</summary>
    [Fact]
    public async Task Three_post_acceptance_cancels_disable_booking()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var passenger = await harness.CreateUserAsync();

        await using var scope = harness.Services.CreateAsyncScope();
        var reputation = scope.ServiceProvider.GetRequiredService<IReputationService>();

        var first = await reputation.RecordAsync(Cancellation(passenger), default);
        Assert.Equal(BlockStates.Ok, first.Status.State);

        var second = await reputation.RecordAsync(Cancellation(passenger), default);
        Assert.Equal(BlockStates.Warn, second.Status.State);

        var third = await reputation.RecordAsync(Cancellation(passenger), default);
        Assert.Equal(BlockStates.BookingDisabled, third.Status.State);
        Assert.Equal(BlockReasons.CancellationsDisabled, third.Status.Reason);
        Assert.False(third.Status.AllowsDispatch);

        var stored = await harness.ReadBlockStateAsync(passenger);
        Assert.Equal(BlockStates.BookingDisabled, stored!.State);
        Assert.Equal(BlockSources.Auto, stored.Source);

        // AL-16's configurable cooldown, stamped so the sweep can lift it without an admin.
        Assert.Equal(harness.Clock.GetUtcNow().AddHours(24), stored.ExpiresAt);

        // Two state changes, two events — OK→WARN and WARN→BOOKING_DISABLED. The first cancel
        // changed no state and must therefore have published nothing.
        var events = await harness.ReadOutboxAsync(passenger);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(ReputationEventTypes.BlockStateChanged, e.EventType));
        Assert.Equal(BlockStates.BookingDisabled, events[^1].String("state"));
        Assert.Equal(BlockStates.Warn, events[^1].String("previousState"));
    }

    /// <summary>DoD 4. D5' §7.2: "Counter resets to 0 on any completed ride."</summary>
    [Fact]
    public async Task A_completed_ride_resets_the_consecutive_cancellation_counter()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var passenger = await harness.CreateUserAsync();

        await using var scope = harness.Services.CreateAsyncScope();
        var reputation = scope.ServiceProvider.GetRequiredService<IReputationService>();

        await reputation.RecordAsync(Cancellation(passenger), default);
        await reputation.RecordAsync(Cancellation(passenger), default);

        var warned = await harness.ReadCountersAsync(passenger);
        Assert.Equal(2, warned!.CancellationsContinuous);
        Assert.Equal(BlockStates.Warn, (await harness.ReadBlockStateAsync(passenger))!.State);

        var completed = await reputation.RecordAsync(
            Fact(IntakeKinds.Completion, passenger, SubjectRoles.Passenger), default);

        Assert.Equal(0, (await harness.ReadCountersAsync(passenger))!.CancellationsContinuous);
        Assert.Equal(BlockStates.Ok, completed.Status.State);

        // And the run really starts over rather than resuming: three more cancels are needed, not
        // one. This is the difference between "reset" and "paused".
        await reputation.RecordAsync(Cancellation(passenger), default);
        await reputation.RecordAsync(Cancellation(passenger), default);
        Assert.Equal(BlockStates.Warn, (await harness.ReadBlockStateAsync(passenger))!.State);

        await reputation.RecordAsync(Cancellation(passenger), default);
        Assert.Equal(BlockStates.BookingDisabled, (await harness.ReadBlockStateAsync(passenger))!.State);
    }

    /// <summary>US-12.6 / D5' §4.2: three confirmed reports auto-delist and cost a level.</summary>
    [Fact]
    public async Task Three_confirmed_reports_delist_the_driver_and_cost_a_level()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var driver = await harness.CreateDriverAsync();

        await using var scope = harness.Services.CreateAsyncScope();
        var reputation = scope.ServiceProvider.GetRequiredService<IReputationService>();

        await reputation.RecordAsync(Fact(IntakeKinds.Report, driver, SubjectRoles.Driver), default);
        await reputation.RecordAsync(Fact(IntakeKinds.Report, driver, SubjectRoles.Driver), default);

        // Everyone starts at 3 (D5' §4.2) and two reports have cost nothing yet.
        Assert.Equal(3, (await reputation.GetLevelAsync(driver, default)).Level);

        var third = await reputation.RecordAsync(Fact(IntakeKinds.Report, driver, SubjectRoles.Driver), default);

        Assert.Equal(BlockStates.Delisted, third.Status.State);
        Assert.Equal(BlockReasons.ReportsDelist, third.Status.Reason);
        Assert.Equal(2, third.Level);
        Assert.Equal(2, await harness.ReadLevelAsync(driver));

        // "temporary delisting … time-boxed" — not a ban.
        var stored = await harness.ReadBlockStateAsync(driver);
        Assert.Equal(harness.Clock.GetUtcNow().AddDays(7), stored!.ExpiresAt);

        // The automatic decrement is audited with no actor: the rule decided, not a person.
        var audit = await harness.ReadAuditAsync(driver);
        var decrement = Assert.Single(audit, entry => entry.Action == "REPUTATION_LEVEL_DECREMENT");
        Assert.Null(decrement.ActorId);
    }

    /// <summary>
    /// A report under review is recorded and moves nothing (US-12.6 counts confirmed reports), and
    /// the later CONFIRMED for the same report id is not counted twice.
    /// </summary>
    [Fact]
    public async Task A_pending_report_moves_no_counter()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var driver = await harness.CreateDriverAsync();

        await using var scope = harness.Services.CreateAsyncScope();
        var reputation = scope.ServiceProvider.GetRequiredService<IReputationService>();

        var reportId = Guid.NewGuid();
        var pending = Fact(IntakeKinds.Report, driver, SubjectRoles.Driver) with
        {
            DedupeKey = $"{IntakeKinds.Report}:{reportId}",
            Counted = false,
        };

        var outcome = await reputation.RecordAsync(pending, default);

        Assert.False(outcome.Counted);
        Assert.Equal(BlockStates.Ok, outcome.Status.State);
        Assert.Equal(0, (await harness.ReadCountersAsync(driver))!.ReportsTotal);

        // safety-svc confirming the same report later carries the same report id — the ledger
        // treats it as the fact it already holds. Deliberate: a report is one fact whose status
        // changed, and counting it on confirmation as well would be counting it twice.
        var confirmed = pending with { Counted = true };
        Assert.True((await reputation.RecordAsync(confirmed, default)).Duplicate);
    }

    /// <summary>ADD §11.12: a driver-side cancel is a brief delist, and it lapses.</summary>
    [Fact]
    public async Task A_driver_cancel_delists_briefly_and_then_lapses()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var driver = await harness.CreateDriverAsync();

        await using var scope = harness.Services.CreateAsyncScope();
        var reputation = scope.ServiceProvider.GetRequiredService<IReputationService>();

        var outcome = await reputation.RecordAsync(Cancellation(driver, SubjectRoles.Driver), default);

        Assert.Equal(BlockStates.Delisted, outcome.Status.State);
        Assert.Equal(BlockReasons.DriverCancelDelist, outcome.Status.Reason);
        Assert.False(outcome.Status.AllowsDispatch);

        // The read path applies the box without the sweep — a driver whose 30 minutes are up is
        // dispatchable the moment they are asked about.
        harness.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(BlockStates.Ok, (await reputation.GetStatusAsync(driver, default)).State);

        // The sweep is what makes it durable and what tells anybody who is not asking. At least
        // one, not exactly one: the TestKit shares a Postgres per collection, so another test's
        // time-boxed state may legitimately be due in the same pass.
        var worker = harness.Services.GetRequiredService<BlockStateExpiryWorker>();
        Assert.True(await worker.RunOnceAsync(default) >= 1);

        Assert.Equal(BlockStates.Ok, (await harness.ReadBlockStateAsync(driver))!.State);

        var events = await harness.ReadOutboxAsync(driver);
        Assert.Equal(2, events.Count);
        Assert.Equal(BlockStates.Delisted, events[0].String("state"));
        Assert.Equal(BlockStates.Ok, events[1].String("state"));
    }

    /// <summary>
    /// A served delisting forgives the reports that caused it — otherwise the recompute would
    /// re-delist the driver the instant the box lapsed, and a "temporary" delisting would be
    /// permanent.
    /// </summary>
    [Fact]
    public async Task A_served_report_delisting_clears_the_reports_that_caused_it()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var driver = await harness.CreateDriverAsync();

        await using var scope = harness.Services.CreateAsyncScope();
        var reputation = scope.ServiceProvider.GetRequiredService<IReputationService>();

        for (var i = 0; i < 3; i++)
        {
            await reputation.RecordAsync(Fact(IntakeKinds.Report, driver, SubjectRoles.Driver), default);
        }

        Assert.Equal(BlockStates.Delisted, (await harness.ReadBlockStateAsync(driver))!.State);

        harness.Clock.Advance(TimeSpan.FromDays(8));
        await harness.Services.GetRequiredService<BlockStateExpiryWorker>().RunOnceAsync(default);

        var settled = await harness.ReadBlockStateAsync(driver);
        Assert.Equal(BlockStates.Ok, settled!.State);
        Assert.Equal(0, (await harness.ReadCountersAsync(driver))!.ReportsTotal);
    }

    /// <summary>D6' §2.3 is at-least-once; the intake ledger is what makes that safe.</summary>
    [Fact]
    public async Task A_redelivered_fact_is_counted_once()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var passenger = await harness.CreateUserAsync();

        await using var scope = harness.Services.CreateAsyncScope();
        var reputation = scope.ServiceProvider.GetRequiredService<IReputationService>();

        var fact = Cancellation(passenger);

        var first = await reputation.RecordAsync(fact, default);
        Assert.False(first.Duplicate);

        for (var i = 0; i < 4; i++)
        {
            var replay = await reputation.RecordAsync(fact, default);
            Assert.True(replay.Duplicate);
            Assert.False(replay.Counted);
        }

        // Four redeliveries of one cancel must not disable a passenger who cancelled once.
        Assert.Equal(1, (await harness.ReadCountersAsync(passenger))!.CancellationsContinuous);
        Assert.Equal(BlockStates.Ok, (await reputation.GetStatusAsync(passenger, default)).State);
    }

    /// <summary>Concurrent facts for one user cannot lose an increment (the FOR UPDATE lock).</summary>
    [Fact]
    public async Task Concurrent_facts_for_one_user_are_all_counted()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var passenger = await harness.CreateUserAsync();

        var facts = Enumerable.Range(0, 6).Select(_ => Cancellation(passenger)).ToArray();

        // Each on its own scope, as six Kafka partitions' worth of handlers would be.
        await Task.WhenAll(facts.Select(async fact =>
        {
            await using var scope = harness.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IReputationService>().RecordAsync(fact, default);
        }));

        Assert.Equal(6, (await harness.ReadCountersAsync(passenger))!.CancellationsContinuous);
    }

    private static ReputationFact Cancellation(Guid subjectId, string role = SubjectRoles.Passenger) =>
        Fact(IntakeKinds.Cancellation, subjectId, role);

    private static ReputationFact Fact(string kind, Guid subjectId, string role) =>
        new(
            DedupeKey: $"{kind}:{Guid.NewGuid()}",
            Kind: kind,
            SubjectId: subjectId,
            SubjectRole: role,
            RideId: Guid.NewGuid(),
            Source: IntakeSources.RideEvents);
}
