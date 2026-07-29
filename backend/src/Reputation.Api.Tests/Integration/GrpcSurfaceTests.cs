using System.Diagnostics;
using Grpc.Core;
using MageRide.Reputation.Counters;
using MageRide.Reputation.Domain;
using MageRide.Reputation.Grpc;
using MageRide.Reputation.Tests.Infrastructure;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Reputation.Tests.Integration;

/// <summary>
/// <b>DoD 2:</b> "the gRPC call answers in under 20 ms p95 against a warm cache" — plus the rest of
/// the <c>reputation.v1</c> surface D3' declares.
/// </summary>
/// <remarks>
/// Every call here goes over a real socket to a real Kestrel through the generated client, which is
/// the only way the latency number means anything. The client and the server compile the same
/// <c>backend/contracts/proto/reputation.v1.proto</c>.
/// </remarks>
[Collection(ReputationCollection.Name)]
public sealed class GrpcSurfaceTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task An_unknown_user_is_OK_and_dispatchable()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var status = await harness.Reputation.GetBlockStatusAsync(
            new DriverRef { UserId = Guid.NewGuid().ToString() },
            ReputationHarness.InternalCallCredentials);

        // Nothing has ever happened to this user. Deliberately not persisted — a dispatch round
        // asking about a thousand drivers must not create a thousand rows.
        Assert.Equal(BlockState.Ok, status.State);
        Assert.True(status.DispatchEligible);
        Assert.Equal(0, status.CancellationsContinuous);
    }

    /// <summary>D5' §3.2's hard gate, over the wire.</summary>
    [Fact]
    public async Task A_delisted_driver_is_not_dispatch_eligible()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var driver = await harness.CreateDriverAsync();

        var ack = await harness.Reputation.ReportCancellationAsync(
            new CancellationEvent
            {
                UserId = driver.ToString(),
                RideId = Guid.NewGuid().ToString(),
                Role = SubjectRole.Driver,
                ReasonCode = "DRIVER_CANCELLED",
                EventId = Guid.NewGuid().ToString(),
            },
            ReputationHarness.InternalCallCredentials);

        Assert.True(ack.Counted);
        Assert.False(ack.Duplicate);
        Assert.Equal(BlockState.Delisted, ack.State);

        var status = await harness.Reputation.GetBlockStatusAsync(
            new DriverRef { UserId = driver.ToString() }, ReputationHarness.InternalCallCredentials);

        Assert.Equal(BlockState.Delisted, status.State);
        Assert.False(status.DispatchEligible);
        Assert.Equal(BlockReasons.DriverCancelDelist, status.Reason);
        Assert.NotNull(status.ExpiresAt);
    }

    /// <summary>D5' §4.2: everyone starts at Level 3; Level 1 loses the Job Board (US-6A.8).</summary>
    [Fact]
    public async Task Driver_level_starts_at_three_and_falls_on_no_shows()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var driver = await harness.CreateDriverAsync();
        var reference = new DriverRef { UserId = driver.ToString() };

        var initial = await harness.Reputation.GetDriverLevelAsync(
            reference, ReputationHarness.InternalCallCredentials);

        Assert.Equal(3, initial.Level_);
        Assert.True(initial.JobBoardEligible);
        Assert.Equal(500, initial.LevelUpThreshold);

        for (var i = 0; i < 2; i++)
        {
            await harness.Reputation.ReportNoShowAsync(
                new NoShowEvent
                {
                    UserId = driver.ToString(),
                    RideId = Guid.NewGuid().ToString(),
                    Role = SubjectRole.Driver,
                    EventId = Guid.NewGuid().ToString(),
                },
                ReputationHarness.InternalCallCredentials);
        }

        var fallen = await harness.Reputation.GetDriverLevelAsync(
            reference, ReputationHarness.InternalCallCredentials);

        Assert.Equal(1, fallen.Level_);
        Assert.False(fallen.JobBoardEligible);
    }

    /// <summary>Level 1 is a floor, not a step towards a ban (D5' §4.2: "NOT a permanent ban").</summary>
    [Fact]
    public async Task Level_one_is_a_floor()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var driver = await harness.CreateDriverAsync();

        for (var i = 0; i < 5; i++)
        {
            await harness.Reputation.ReportNoShowAsync(
                new NoShowEvent
                {
                    UserId = driver.ToString(),
                    RideId = Guid.NewGuid().ToString(),
                    Role = SubjectRole.Driver,
                    EventId = Guid.NewGuid().ToString(),
                },
                ReputationHarness.InternalCallCredentials);
        }

        Assert.Equal(1, await harness.ReadLevelAsync(driver));
    }

    /// <summary>US-12.6, over the wire: only a CONFIRMED report counts.</summary>
    [Fact]
    public async Task Only_confirmed_vehicle_reports_count()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var driver = await harness.CreateDriverAsync();
        var reporter = await harness.CreateUserAsync();
        var vehicle = Guid.NewGuid();

        var pending = await harness.Reputation.ReportVehicleAsync(
            Report(driver, reporter, vehicle, ReportStatus.Pending), ReputationHarness.InternalCallCredentials);

        Assert.False(pending.Counted);
        Assert.Equal(BlockState.Ok, pending.State);

        for (var i = 0; i < 3; i++)
        {
            await harness.Reputation.ReportVehicleAsync(
                Report(driver, reporter, vehicle, ReportStatus.Confirmed), ReputationHarness.InternalCallCredentials);
        }

        var status = await harness.Reputation.GetBlockStatusAsync(
            new DriverRef { UserId = driver.ToString() }, ReputationHarness.InternalCallCredentials);

        Assert.Equal(BlockState.Delisted, status.State);
        Assert.Equal(3, status.ReportsTotal);
    }

    /// <summary>A retried RPC is expected, not an error (D6' §2.3).</summary>
    [Fact]
    public async Task A_retried_report_is_acknowledged_as_a_duplicate()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var passenger = await harness.CreateUserAsync();

        var request = new CancellationEvent
        {
            UserId = passenger.ToString(),
            RideId = Guid.NewGuid().ToString(),
            Role = SubjectRole.Passenger,
            ReasonCode = "RIDER_CANCELLED_AFTER_ACCEPT",
            EventId = Guid.NewGuid().ToString(),
        };

        Assert.False((await harness.Reputation.ReportCancellationAsync(
            request, ReputationHarness.InternalCallCredentials)).Duplicate);

        var retry = await harness.Reputation.ReportCancellationAsync(
            request, ReputationHarness.InternalCallCredentials);

        Assert.True(retry.Duplicate);
        Assert.Equal(1, (await harness.ReadCountersAsync(passenger))!.CancellationsContinuous);
    }

    /// <summary>A caller that mints no event id is still deduplicated, on (kind, ride, subject).</summary>
    [Fact]
    public async Task A_report_without_an_event_id_is_deduplicated_on_the_ride()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);
        var passenger = await harness.CreateUserAsync();
        var rideId = Guid.NewGuid().ToString();

        var request = new CancellationEvent
        {
            UserId = passenger.ToString(),
            RideId = rideId,
            Role = SubjectRole.Passenger,
            ReasonCode = "RIDER_CANCELLED_AFTER_ACCEPT",
        };

        await harness.Reputation.ReportCancellationAsync(request, ReputationHarness.InternalCallCredentials);
        var retry = await harness.Reputation.ReportCancellationAsync(request, ReputationHarness.InternalCallCredentials);

        Assert.True(retry.Duplicate);
    }

    [Fact]
    public async Task A_malformed_id_is_invalid_argument()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var thrown = await Assert.ThrowsAsync<RpcException>(() => harness.Reputation
            .GetBlockStatusAsync(new DriverRef { UserId = "not-an-id" }, ReputationHarness.InternalCallCredentials)
            .ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, thrown.StatusCode);
    }

    /// <summary>
    /// The interim mTLS stand-in: a caller without the key is answered 404, matching what the
    /// gateway does for the internal HTTP prefix.
    /// </summary>
    [Fact]
    public async Task A_caller_without_the_internal_key_cannot_see_the_service()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        var thrown = await Assert.ThrowsAsync<RpcException>(() => harness.Reputation
            .GetBlockStatusAsync(new DriverRef { UserId = Guid.NewGuid().ToString() })
            .ResponseAsync);

        Assert.Equal(StatusCode.NotFound, thrown.StatusCode);

        var wrongKey = new Metadata { { InternalKeyInterceptor.MetadataKey, "not-the-key" } };

        var refused = await Assert.ThrowsAsync<RpcException>(() => harness.Reputation
            .GetBlockStatusAsync(new DriverRef { UserId = Guid.NewGuid().ToString() }, wrongKey)
            .ResponseAsync);

        Assert.Equal(StatusCode.NotFound, refused.StatusCode);
    }

    /// <summary>
    /// <b>DoD 2.</b> 200 calls against a warm cache; the 95th percentile must be under 20 ms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured end to end — a real HTTP/2 connection, the interceptor, the Redis read and the
    /// protobuf round trip — because that is what dispatch-svc will pay per candidate.
    /// </para>
    /// <para>
    /// The first calls are excluded: the first one on a channel pays TLS-less connection setup and
    /// the first per user is a cache miss that reads Postgres, and neither is what "warm" means.
    /// A budget check, not a benchmark — it fails when something structural is wrong (a per-call
    /// connection, a missing cache, an N+1 read), not when the box is briefly busy.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_block_status_call_answers_under_20ms_at_p95()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ReputationHarness.StartAsync(postgres, redis);

        // A realistic pool: some blocked, most not, all of them warm.
        var drivers = new List<Guid>();

        for (var i = 0; i < 10; i++)
        {
            var driver = await harness.CreateDriverAsync();
            drivers.Add(driver);

            if (i % 4 == 0)
            {
                await using var scope = harness.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IReputationService>().RecordAsync(
                    new ReputationFact(
                        $"{IntakeKinds.Cancellation}:{Guid.NewGuid()}", IntakeKinds.Cancellation, driver,
                        SubjectRoles.Driver, Guid.NewGuid(), IntakeSources.Grpc),
                    default);
            }
        }

        var references = drivers.Select(id => new DriverRef { UserId = id.ToString() }).ToArray();

        // Warm the channel and every cache entry.
        foreach (var reference in references)
        {
            await harness.Reputation.GetBlockStatusAsync(reference, ReputationHarness.InternalCallCredentials);
        }

        var samples = new List<double>(200);

        for (var i = 0; i < 200; i++)
        {
            var started = Stopwatch.GetTimestamp();

            await harness.Reputation.GetBlockStatusAsync(
                references[i % references.Length], ReputationHarness.InternalCallCredentials);

            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        samples.Sort();
        var p95 = samples[(int)Math.Floor(samples.Count * 0.95) - 1];

        Assert.True(
            p95 < 20,
            $"p95 was {p95:0.00} ms (p50 {samples[samples.Count / 2]:0.00} ms, max {samples[^1]:0.00} ms).");
    }

    private static VehicleReport Report(Guid driverId, Guid reporterId, Guid vehicleId, ReportStatus status) =>
        new()
        {
            ReportId = Guid.NewGuid().ToString(),
            DriverId = driverId.ToString(),
            VehicleId = vehicleId.ToString(),
            ReporterId = reporterId.ToString(),
            RideId = Guid.NewGuid().ToString(),
            Reason = "unsafe_driving",
            Status = status,
        };
}
