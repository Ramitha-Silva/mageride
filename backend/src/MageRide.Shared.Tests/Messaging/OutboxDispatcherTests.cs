using System.Collections.Concurrent;
using System.Diagnostics;
using Dapper;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Shared.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MageRide.TestKit;

namespace MageRide.Shared.Tests.Messaging;

/// <summary>
/// The transactional outbox end to end against a real Postgres: D6' §2.4, R-13 (nothing published
/// before COMMIT) and E-09 (LISTEN/NOTIFY wakes the dispatcher in under 50 ms).
/// </summary>
/// <remarks>
/// The publisher is a capture double rather than a real Redpanda: what E-09 measures is the
/// commit-to-dispatch wake-up, and a broker in the path would measure Kafka's ack latency instead.
/// </remarks>
[Collection<PostgresCollection>]
public sealed class OutboxDispatcherTests(PostgresFixture postgres)
{
    /// <summary>The <c>rides.outbox</c> DDL from D4' §5, verbatim.</summary>
    private const string DdlTemplate =
        """
        CREATE SCHEMA IF NOT EXISTS {0};
        CREATE TABLE IF NOT EXISTS {0}.outbox (
          id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
          aggregate_id UUID NOT NULL,
          event_type   TEXT NOT NULL,
          payload      JSONB NOT NULL,
          created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
          dispatched_at TIMESTAMPTZ);
        CREATE INDEX IF NOT EXISTS ix_outbox_undispatched_{0} ON {0}.outbox(id) WHERE dispatched_at IS NULL;
        """;

    private sealed class CapturingPublisher : IEventPublisher
    {
        private readonly SemaphoreSlim _batches = new(0);

        public ConcurrentQueue<EventMessage> Published { get; } = new();

        /// <summary>Waits for the next published batch. Event-driven, so it adds no polling skew
        /// to the E-09 measurement.</summary>
        public Task<bool> WaitForBatchAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            _batches.WaitAsync(timeout, cancellationToken);

        public async Task<PublishReceipt> PublishAsync(
            EventMessage message, CancellationToken cancellationToken = default) =>
            (await PublishAsync([message], cancellationToken))[0];

        public Task<IReadOnlyList<PublishReceipt>> PublishAsync(
            IReadOnlyCollection<EventMessage> messages, CancellationToken cancellationToken = default)
        {
            var receipts = new List<PublishReceipt>(messages.Count);

            foreach (var message in messages)
            {
                Published.Enqueue(message);
                receipts.Add(PublishReceipt.None(message.Topic));
            }

            _batches.Release();
            return Task.FromResult<IReadOnlyList<PublishReceipt>>(receipts);
        }
    }

    private sealed class FailingPublisher : IEventPublisher
    {
        public int Attempts;

        public Task<PublishReceipt> PublishAsync(EventMessage message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("broker unreachable");

        public Task<IReadOnlyList<PublishReceipt>> PublishAsync(
            IReadOnlyCollection<EventMessage> messages, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Attempts);
            throw new InvalidOperationException("broker unreachable");
        }
    }

    private async Task<(NpgsqlConnectionFactory Factory, OutboxOptions Options)> PrepareAsync(string schema)
    {
        var factory = TestHosts.ConnectionFactory(postgres.ConnectionString);

        await using (var connection = await factory.OpenAsync())
        {
            await connection.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, DdlTemplate, schema));
        }

        return (factory, new OutboxOptions
        {
            Schema = schema,
            Table = "outbox",
            Channel = $"{schema}_outbox",
            Topic = "ride.events",
            // Far longer than the assertion window, so anything observed came from LISTEN/NOTIFY
            // and not from the safety-net poll.
            PollInterval = TimeSpan.FromMinutes(5),
        });
    }

    private static OutboxWriter Writer(OutboxOptions options) => new(Options.Create(options));

    private static OutboxDispatcher Dispatcher(
        INpgsqlConnectionFactory factory, IEventPublisher publisher, OutboxOptions options) =>
        new(factory, publisher, Options.Create(options), NullLogger<OutboxDispatcher>.Instance);

    /// <summary>
    /// The E-09 acceptance criterion: LISTEN/NOTIFY replaces the 250 ms poll so an
    /// <c>offer.created</c> reaches the dispatcher with a median under 50 ms from COMMIT.
    /// </summary>
    /// <remarks>
    /// One warm-up round runs first and is not measured. The first drain pays for opening a
    /// pooled connection, loading Npgsql's type catalogue and JIT — real costs, but paid once at
    /// start-up, not per offer, so including them would measure the wrong thing.
    /// </remarks>
    [Fact]
    public async Task Listen_notify_wakes_the_dispatcher_in_under_50ms()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        const int Rounds = 5;

        var (factory, options) = await PrepareAsync("e09");
        var publisher = new CapturingPublisher();
        var dispatcher = Dispatcher(factory, publisher, options);
        var unitOfWorkFactory = new NpgsqlUnitOfWorkFactory(factory);
        var writer = Writer(options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await dispatcher.StartAsync(cts.Token);

        try
        {
            await WaitForListenerAsync(dispatcher, cts.Token);

            async Task<TimeSpan> CommitAndAwaitDispatchAsync(string eventType)
            {
                var uow = await unitOfWorkFactory.BeginAsync(cancellationToken: cts.Token);
                await writer.WriteAsync(
                    uow,
                    OutboxRecord.Create(Guid.NewGuid(), eventType, """{"eventType":"offer.created","version":1}"""),
                    cts.Token);

                var stopwatch = Stopwatch.StartNew();
                await uow.CommitAsync(cts.Token);
                await uow.DisposeAsync();

                Assert.True(
                    await publisher.WaitForBatchAsync(TimeSpan.FromSeconds(10), cts.Token),
                    "The dispatcher never published the committed outbox row.");

                stopwatch.Stop();
                return stopwatch.Elapsed;
            }

            await CommitAndAwaitDispatchAsync("ride.requested");

            var measurements = new List<TimeSpan>(Rounds);
            for (var i = 0; i < Rounds; i++)
            {
                measurements.Add(await CommitAndAwaitDispatchAsync("offer.created"));
            }

            var median = measurements.OrderBy(m => m).ElementAt(Rounds / 2);
            var detail = string.Join(", ", measurements.Select(m => $"{m.TotalMilliseconds:0.0}"));

            Assert.True(
                median < TimeSpan.FromMilliseconds(50),
                $"E-09 requires a median commit-to-dispatch wake-up under 50 ms; measured [{detail}] ms, median {median.TotalMilliseconds:0.0} ms.");

            Assert.Equal(Rounds + 1, publisher.Published.Count);
            Assert.All(publisher.Published, m => Assert.Equal("ride.events", m.Topic));
            Assert.Equal("offer.created", publisher.Published.Last().Headers!["eventType"]);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            dispatcher.Dispose();
        }
    }

    /// <summary>R-13: an event describing a rolled-back change must never be published.</summary>
    [Fact]
    public async Task A_rolled_back_transaction_publishes_nothing()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var (factory, options) = await PrepareAsync("r13");
        var publisher = new CapturingPublisher();
        var dispatcher = Dispatcher(factory, publisher, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await dispatcher.StartAsync(cts.Token);

        try
        {
            await WaitForListenerAsync(dispatcher, cts.Token);

            await using (var uow = await new NpgsqlUnitOfWorkFactory(factory).BeginAsync(cancellationToken: cts.Token))
            {
                await Writer(options).WriteAsync(
                    uow, OutboxRecord.Create(Guid.NewGuid(), "offer.created", """{"offerId":"x"}"""), cts.Token);

                await uow.RollbackAsync(cts.Token);
            }

            // A NOTIFY inside a rolled-back transaction is discarded by Postgres, so nothing can
            // even wake the dispatcher. Give it a real window to prove it stays quiet.
            await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);

            Assert.Empty(publisher.Published);

            await using var connection = await factory.OpenAsync(cts.Token);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM r13.outbox"));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            dispatcher.Dispose();
        }
    }

    [Fact]
    public async Task Dispatched_rows_are_marked_and_not_published_twice()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var (factory, options) = await PrepareAsync("once");
        var publisher = new CapturingPublisher();
        var dispatcher = Dispatcher(factory, publisher, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await dispatcher.StartAsync(cts.Token);

        try
        {
            await WaitForListenerAsync(dispatcher, cts.Token);

            var rideId = Guid.NewGuid();
            await using (var uow = await new NpgsqlUnitOfWorkFactory(factory).BeginAsync(cancellationToken: cts.Token))
            {
                await Writer(options).WriteAsync(uow,
                [
                    OutboxRecord.Create(rideId, "ride.requested", """{"n":1}"""),
                    OutboxRecord.Create(rideId, "ride.accepted", """{"n":2}"""),
                    OutboxRecord.Create(rideId, "ride.started", """{"n":3}"""),
                ], cts.Token);

                await uow.CommitAsync(cts.Token);
            }

            await WaitUntilAsync(() => publisher.Published.Count == 3, TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromMilliseconds(300), cts.Token);

            Assert.Equal(3, publisher.Published.Count);

            // Ordering per aggregate is what consumers rely on (D6' §2.3).
            Assert.Equal(
                ["ride.requested", "ride.accepted", "ride.started"],
                publisher.Published.Select(m => m.Headers!["eventType"]).ToArray());
            Assert.All(publisher.Published, m => Assert.Equal(rideId.ToString(), m.Key));

            await using var connection = await factory.OpenAsync(cts.Token);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM once.outbox WHERE dispatched_at IS NULL"));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            dispatcher.Dispose();
        }
    }

    /// <summary>A broker outage must leave the row undispatched, not drop the event.</summary>
    [Fact]
    public async Task A_publish_failure_leaves_the_row_undispatched()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var (factory, options) = await PrepareAsync("retry");
        options.PublishRetryDelay = TimeSpan.FromMilliseconds(100);

        var publisher = new FailingPublisher();
        var dispatcher = Dispatcher(factory, publisher, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await dispatcher.StartAsync(cts.Token);

        try
        {
            await WaitForListenerAsync(dispatcher, cts.Token);

            await using (var uow = await new NpgsqlUnitOfWorkFactory(factory).BeginAsync(cancellationToken: cts.Token))
            {
                await Writer(options).WriteAsync(
                    uow, OutboxRecord.Create(Guid.NewGuid(), "ride.completed", """{"n":1}"""), cts.Token);
                await uow.CommitAsync(cts.Token);
            }

            await WaitUntilAsync(() => Volatile.Read(ref publisher.Attempts) > 0, TimeSpan.FromSeconds(10));

            await using var connection = await factory.OpenAsync(cts.Token);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM retry.outbox WHERE dispatched_at IS NULL"));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            dispatcher.Dispose();
        }
    }

    /// <summary>
    /// Rows already in the table when the process starts are drained without waiting for a
    /// notification — the crash-recovery path.
    /// </summary>
    [Fact]
    public async Task Rows_written_before_start_up_are_drained()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var (factory, options) = await PrepareAsync("backlog");

        await using (var uow = await new NpgsqlUnitOfWorkFactory(factory).BeginAsync())
        {
            await Writer(options).WriteAsync(uow, OutboxRecord.Create(Guid.NewGuid(), "ride.cancelled", """{"n":1}"""));
            await uow.CommitAsync();
        }

        var publisher = new CapturingPublisher();
        var dispatcher = Dispatcher(factory, publisher, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await dispatcher.StartAsync(cts.Token);

        try
        {
            Assert.True(await publisher.WaitForBatchAsync(TimeSpan.FromSeconds(10), cts.Token));
            Assert.Single(publisher.Published);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            dispatcher.Dispose();
        }
    }

    [Fact]
    public void The_advisory_lock_key_is_stable_and_table_specific()
    {
        Assert.Equal(OutboxDispatcher.AdvisoryLockKey("rides.outbox"), OutboxDispatcher.AdvisoryLockKey("rides.outbox"));
        Assert.NotEqual(OutboxDispatcher.AdvisoryLockKey("rides.outbox"), OutboxDispatcher.AdvisoryLockKey("dispatch.outbox"));
    }

    [Fact]
    public async Task An_outbox_record_needs_a_real_aggregate_id()
    {
        Assert.Throws<ArgumentException>(() => OutboxRecord.Create(Guid.Empty, "ride.accepted", "{}"));
        await Task.CompletedTask;
    }

    /// <summary>Blocks until the dispatcher's direct connection is actually listening.</summary>
    private static async Task WaitForListenerAsync(OutboxDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await WaitUntilAsync(() => dispatcher.IsListening, TimeSpan.FromSeconds(10));

        // The listener signals a start-up drain of the (empty) table; let that settle so the
        // timed test measures the notification path and not a start-up race.
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Condition was not met within {timeout}.");
    }
}
