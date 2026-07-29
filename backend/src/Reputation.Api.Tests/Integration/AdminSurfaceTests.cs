using System.Net;
using MageRide.Reputation.Counters;
using MageRide.Reputation.Detection;
using MageRide.Reputation.Domain;
using MageRide.Reputation.Grpc;
using MageRide.Reputation.Tests.Infrastructure;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Reputation.Tests.Integration;

/// <summary>
/// The admin surface: the two routes D3' declares and the three C033 adds, plus the audit rows
/// that make "with audit" mean something.
/// </summary>
[Collection(ReputationCollection.Name)]
public sealed class AdminSurfaceTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// The deliverable: a manual override, audited, and — the part that matters — <b>not</b> undone
    /// by the next counted fact.
    /// </summary>
    [Fact]
    public async Task An_admin_override_is_audited_and_survives_the_next_recompute()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateUserAsync("admin");
        var passenger = await harness.CreateUserAsync();

        await using var scope = harness.Services.CreateAsyncScope();
        var reputation = scope.ServiceProvider.GetRequiredService<IReputationService>();

        for (var i = 0; i < 3; i++)
        {
            await reputation.RecordAsync(Cancellation(passenger), default);
        }

        Assert.Equal(BlockStates.BookingDisabled, (await harness.ReadBlockStateAsync(passenger))!.State);

        // AL-16's "admin/CSR reinstatement".
        var lifted = await harness.PutAsync(
            $"/v1/admin/reputation/users/{passenger}/block-state",
            new { state = "OK", reason = "Appeal upheld — the driver never arrived." },
            harness.Tokens.Admin(admin));

        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);

        var body = await ReputationHarness.ReadJsonAsync(lifted);
        Assert.Equal("OK", body.GetProperty("state").GetString());

        var audit = await harness.ReadAuditAsync(passenger);
        var entry = Assert.Single(audit, row => row.Action == "REPUTATION_BLOCK_STATE_OVERRIDE");
        Assert.Equal(admin, entry.ActorId);
        Assert.Contains("BOOKING_DISABLED", entry.Before!, StringComparison.Ordinal);
        Assert.Contains("Appeal upheld", entry.After!, StringComparison.Ordinal);

        // Reinstatement forgave the counters, so the next cancel starts a fresh run rather than
        // finding three already there and re-disabling them on the spot.
        Assert.Equal(0, (await harness.ReadCountersAsync(passenger))!.CancellationsContinuous);

        await reputation.RecordAsync(Cancellation(passenger), default);

        var after = await harness.ReadBlockStateAsync(passenger);
        Assert.Equal(BlockStates.Ok, after!.State);
        Assert.Equal(BlockSources.Auto, after.Source);
    }

    /// <summary>A pinned block is not lifted by the rules while it holds (migration 0804 (a)).</summary>
    [Fact]
    public async Task A_pinned_block_is_not_recomputed_away()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateUserAsync("admin");
        var driver = await harness.CreateDriverAsync();

        var pinned = await harness.PutAsync(
            $"/v1/admin/reputation/users/{driver}/block-state",
            new { state = "DELISTED", reason = "Under investigation for ride-farming (E-07 flag 12)." },
            harness.Tokens.Admin(admin));

        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);

        var stored = await harness.ReadBlockStateAsync(driver);
        Assert.Equal(BlockStates.Delisted, stored!.State);
        Assert.Equal(BlockSources.Manual, stored.Source);
        Assert.Equal(admin, stored.SetBy);

        // A completed ride would ordinarily recompute the state to OK. It must not lift an
        // investigation.
        await using var scope = harness.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IReputationService>().RecordAsync(
            new ReputationFact(
                $"{IntakeKinds.Completion}:{Guid.NewGuid()}", IntakeKinds.Completion, driver, SubjectRoles.Driver,
                Guid.NewGuid(), IntakeSources.RideEvents),
            default);

        var after = await harness.ReadBlockStateAsync(driver);
        Assert.Equal(BlockStates.Delisted, after!.State);
        Assert.Equal(BlockSources.Manual, after.Source);

        // And the gate agrees over gRPC, which is the only reading dispatch-svc ever takes.
        var status = await harness.Reputation.GetBlockStatusAsync(
            new DriverRef { UserId = driver.ToString() }, ReputationHarness.InternalCallCredentials);

        Assert.Equal(BlockState.Delisted, status.State);
        Assert.False(status.DispatchEligible);
    }

    /// <summary>US-6A.8 / D3' <c>POST /v1/admin/drivers/{driverId}/level/restore</c>.</summary>
    [Fact]
    public async Task An_appeal_restores_a_level_and_records_the_decision()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateUserAsync("admin");
        var driver = await harness.CreateDriverAsync();

        await using var scope = harness.Services.CreateAsyncScope();
        var reputation = scope.ServiceProvider.GetRequiredService<IReputationService>();

        await reputation.RecordAsync(
            new ReputationFact(
                $"{IntakeKinds.NoShow}:{Guid.NewGuid()}", IntakeKinds.NoShow, driver, SubjectRoles.Driver,
                Guid.NewGuid(), IntakeSources.Grpc),
            default);

        Assert.Equal(2, await harness.ReadLevelAsync(driver));

        var restored = await harness.PostAsync(
            $"/v1/admin/drivers/{driver}/level/restore",
            new { level = 3, reason = "No-show was a platform outage, not the driver." },
            harness.Tokens.Admin(admin));

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        var body = await ReputationHarness.ReadJsonAsync(restored);
        Assert.Equal(3, body.GetProperty("level").GetInt32());
        Assert.Equal(3, await harness.ReadLevelAsync(driver));

        var audit = await harness.ReadAuditAsync(driver);
        Assert.Contains(audit, row => row.Action == "REPUTATION_LEVEL_RESTORE" && row.ActorId == admin);
    }

    /// <summary>
    /// Restoring to the level a driver already holds is a no-op that still records the decision —
    /// an appeal that was heard and refused is exactly what an auditor later looks for.
    /// </summary>
    [Fact]
    public async Task A_restore_to_the_same_level_still_records_the_decision()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateUserAsync("admin");
        var driver = await harness.CreateDriverAsync();

        var response = await harness.PostAsync(
            $"/v1/admin/drivers/{driver}/level/restore",
            new { level = 3, reason = "Appeal refused; level already correct." },
            harness.Tokens.Admin(admin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await harness.ReadAuditAsync(driver), row => row.Action == "REPUTATION_LEVEL_RESTORE");
    }

    /// <summary>The E-07 queue: list, filter, and resolve.</summary>
    [Fact]
    public async Task Flags_are_listed_and_resolved()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateUserAsync("admin");
        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateDriverAsync();

        await CompleteRidesAsync(harness, passenger, driver, 10);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICollusionDetector>().RunAsync(default);
        }

        var listed = await harness.GetAsync(
            $"/v1/admin/reputation/flags?kind={FraudFlagKinds.RepeatPair}&status=open&limit=100",
            harness.Tokens.Admin(admin));

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var page = await ReputationHarness.ReadJsonAsync(listed);
        var items = page.GetProperty("items").EnumerateArray().ToArray();
        var mine = items.Single(item => item.GetProperty("subjectId").GetString() == passenger.ToString());
        var flagId = mine.GetProperty("flagId").GetString();

        // The cursor member is always present, `null` on the last page (C002 decision 9).
        Assert.True(page.TryGetProperty("cursor", out _));

        var resolved = await harness.PostAsync(
            $"/v1/admin/reputation/flags/{flagId}/resolve",
            new { status = "dismissed", note = "Regular commute — same office run every weekday." },
            harness.Tokens.Admin(admin));

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal("dismissed", (await ReputationHarness.ReadJsonAsync(resolved)).GetProperty("status").GetString());

        // Re-resolving with the same verdict is the no-op the contract describes; changing it is a
        // conflict.
        var again = await harness.PostAsync(
            $"/v1/admin/reputation/flags/{flagId}/resolve",
            new { status = "dismissed", note = "Confirmed with the driver." },
            harness.Tokens.Admin(admin));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var changed = await harness.PostAsync(
            $"/v1/admin/reputation/flags/{flagId}/resolve",
            new { status = "actioned" },
            harness.Tokens.Admin(admin));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);

        // A dismissed flag is out of the default queue.
        var open = await harness.GetAsync(
            "/v1/admin/reputation/flags?status=open&limit=100", harness.Tokens.Admin(admin));

        var stillOpen = (await ReputationHarness.ReadJsonAsync(open)).GetProperty("items").EnumerateArray()
            .Any(item => item.GetProperty("flagId").GetString() == flagId);

        Assert.False(stillOpen);
    }

    /// <summary>The subject read an admin looks at before overriding anything.</summary>
    [Fact]
    public async Task The_subject_read_shows_the_counters_behind_the_state()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateUserAsync("admin");
        var driver = await harness.CreateDriverAsync();

        await using var scope = harness.Services.CreateAsyncScope();
        var reputation = scope.ServiceProvider.GetRequiredService<IReputationService>();

        await reputation.RecordAsync(
            new ReputationFact(
                $"{IntakeKinds.Report}:{Guid.NewGuid()}", IntakeKinds.Report, driver, SubjectRoles.Driver,
                Guid.NewGuid(), IntakeSources.Grpc),
            default);

        var response = await harness.GetAsync(
            $"/v1/admin/reputation/users/{driver}", harness.Tokens.Admin(admin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReputationHarness.ReadJsonAsync(response);
        Assert.Equal("OK", body.GetProperty("state").GetString());
        Assert.Equal(1, body.GetProperty("counters").GetProperty("reportsTotal").GetInt32());
        Assert.Equal(3, body.GetProperty("level").GetInt32());
    }

    /// <summary>AL-06 deny-by-default: a driver's own token opens no admin route.</summary>
    [Fact]
    public async Task A_driver_may_not_reach_the_admin_surface()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var driver = await harness.CreateDriverAsync();

        var listed = await harness.GetAsync("/v1/admin/reputation/flags", harness.Tokens.Driver(driver));
        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);

        var overridden = await harness.PutAsync(
            $"/v1/admin/reputation/users/{driver}/block-state",
            new { state = "OK", reason = "let me out" },
            harness.Tokens.Driver(driver));
        Assert.Equal(HttpStatusCode.Forbidden, overridden.StatusCode);
    }

    /// <summary>An auditor reads and does not decide (AL-06's nine roles).</summary>
    [Fact]
    public async Task An_auditor_reads_but_does_not_resolve()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var auditor = await harness.CreateUserAsync("auditor");

        var listed = await harness.GetAsync("/v1/admin/reputation/flags", harness.Tokens.Auditor(auditor));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var resolved = await harness.PostAsync(
            $"/v1/admin/reputation/flags/{Guid.NewGuid()}/resolve",
            new { status = "dismissed" },
            harness.Tokens.Auditor(auditor));

        Assert.Equal(HttpStatusCode.Forbidden, resolved.StatusCode);
    }

    /// <summary>An override with no reason cannot be audited, so it is refused.</summary>
    [Fact]
    public async Task An_override_without_a_reason_is_rejected()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateUserAsync("admin");
        var user = await harness.CreateUserAsync();

        var missingReason = await harness.PutAsync(
            $"/v1/admin/reputation/users/{user}/block-state",
            new { state = "DELISTED" },
            harness.Tokens.Admin(admin));
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);

        var badState = await harness.PutAsync(
            $"/v1/admin/reputation/users/{user}/block-state",
            new { state = "BANNED", reason = "because" },
            harness.Tokens.Admin(admin));
        Assert.Equal(HttpStatusCode.BadRequest, badState.StatusCode);
    }

    private static ReputationFact Cancellation(Guid subjectId) =>
        new(
            DedupeKey: $"{IntakeKinds.Cancellation}:{Guid.NewGuid()}",
            Kind: IntakeKinds.Cancellation,
            SubjectId: subjectId,
            SubjectRole: SubjectRoles.Passenger,
            RideId: Guid.NewGuid(),
            Source: IntakeSources.RideEvents);

    private static async Task CompleteRidesAsync(ReputationHarness harness, Guid passenger, Guid driver, int count)
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
                        $"{IntakeSources.RideEvents}:{eventId}:{role}", IntakeKinds.Completion, subject, role,
                        rideId, IntakeSources.RideEvents),
                    default);
            }
        }
    }
}
