using System.Text;
using MageRide.Shared.Observability;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Shared.Messaging;

/// <summary>
/// Drains the transactional outbox to Redpanda, woken by Postgres <c>LISTEN/NOTIFY</c>
/// (D6' §2.4, E-09).
/// </summary>
/// <remarks>
/// <para>
/// E-09 replaces a 250 ms poll with a notification so an <c>offer.created</c> reaches the driver
/// in under 50 ms from commit. Two loops make that work: a listener holding a direct connection
/// (PgBouncer in transaction mode drops session-scoped <c>LISTEN</c> registrations, so this must
/// not go through the pooler), and a drain loop it signals. <see cref="OutboxOptions.PollInterval"/>
/// is only a safety net for rows committed while the listener was reconnecting.
/// </para>
/// <para>
/// Every replica of a service runs this. A transaction-scoped advisory lock elects one drainer at
/// a time, which is what preserves the per-aggregate ordering consumers depend on (D6' §2.3) —
/// concurrent drainers could otherwise publish two events for the same ride out of order.
/// </para>
/// <para>
/// Delivery is at least once: the row is marked dispatched only after the broker acknowledges, so
/// a crash between publish and commit re-publishes. Consumers key on <c>eventId</c>/<c>seq</c> to
/// deduplicate (D6' §2.3).
/// </para>
/// </remarks>
public sealed class OutboxDispatcher : BackgroundService
{
    private readonly INpgsqlConnectionFactory _connectionFactory;
    private readonly IEventPublisher _publisher;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly SemaphoreSlim _wakeUp = new(1, 1);
    private readonly string _claimSql;
    private readonly string _markDispatchedSql;
    private readonly string _listenSql;
    private readonly long _advisoryLockKey;

    public OutboxDispatcher(
        INpgsqlConnectionFactory connectionFactory,
        IEventPublisher publisher,
        IOptions<OutboxOptions> options,
        ILogger<OutboxDispatcher> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var table = $"{OutboxWriter.Identifier(_options.Schema)}.{OutboxWriter.Identifier(_options.Table)}";

        // FOR UPDATE SKIP LOCKED is belt-and-braces behind the advisory lock: if a drainer is
        // somehow running elsewhere, rows are never handed to two of them.
        _claimSql =
            $"""
             WITH claimed AS (
               SELECT id
                 FROM {table}
                WHERE dispatched_at IS NULL
                ORDER BY id
                LIMIT $1
                FOR UPDATE SKIP LOCKED)
             SELECT o.id, o.aggregate_id, o.event_type, o.payload, o.created_at
               FROM {table} o
               JOIN claimed c ON c.id = o.id
              ORDER BY o.id;
             """;

        _markDispatchedSql =
            $"""
             UPDATE {table}
                SET dispatched_at = now()
              WHERE id = ANY($1);
             """;

        // The channel name cannot be parameterised; it is validated as an identifier.
        _listenSql = $"LISTEN {OutboxWriter.Identifier(_options.Channel)};";

        _advisoryLockKey = AdvisoryLockKey($"{_options.Schema}.{_options.Table}");
    }

    /// <summary>Rows published since start-up. Exposed for the E-09 integration test.</summary>
    internal long DispatchedCount;

    /// <summary><see langword="true"/> while a direct connection holds an active LISTEN.</summary>
    internal volatile bool IsListening;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbox dispatcher starting: {Schema}.{Table} -> {Topic} on LISTEN {Channel}",
            _options.Schema, _options.Table, _options.Topic, _options.Channel);

        var listener = ListenAsync(stoppingToken);
        var drainer = DrainLoopAsync(stoppingToken);

        await Task.WhenAll(listener, drainer);
    }

    /// <summary>
    /// Holds a direct connection with <c>LISTEN</c> registered and signals the drain loop on every
    /// notification. Reconnects with a backoff if the connection drops.
    /// </summary>
    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _connectionFactory.OpenDirectAsync(stoppingToken);
                connection.Notification += OnNotification;

                try
                {
                    await using (var command = new NpgsqlCommand(_listenSql, connection))
                    {
                        await command.ExecuteNonQueryAsync(stoppingToken);
                    }

                    _logger.LogInformation("Listening on Postgres channel {Channel}", _options.Channel);
                    IsListening = true;

                    // Anything committed before the LISTEN took effect is still undispatched.
                    Signal();

                    while (!stoppingToken.IsCancellationRequested)
                    {
                        await connection.WaitAsync(stoppingToken);
                    }
                }
                finally
                {
                    IsListening = false;
                    connection.Notification -= OnNotification;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LISTEN on {Channel} dropped; reconnecting in {Delay}", _options.Channel, _options.ReconnectDelay);

                try
                {
                    await Task.Delay(_options.ReconnectDelay, _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void OnNotification(object? sender, NpgsqlNotificationEventArgs args) => Signal();

    private void Signal()
    {
        // The semaphore is a latch, not a queue: one pending wake-up is enough, the drain loop
        // always empties the table.
        if (_wakeUp.CurrentCount == 0)
        {
            try
            {
                _wakeUp.Release();
            }
            catch (SemaphoreFullException)
            {
                // Raced with another signal; a wake-up is already pending.
            }
        }
    }

    private async Task DrainLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wakeUp.WaitAsync(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                while (await DrainOnceAsync(stoppingToken) == _options.BatchSize)
                {
                    // A full batch means there is probably more waiting.
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                MageRideDiagnostics.OutboxPublishFailures.Add(1);
                _logger.LogError(ex, "Outbox drain failed; rows stay undispatched and will be retried");

                try
                {
                    await Task.Delay(_options.PublishRetryDelay, _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Claims, publishes and marks one batch. Returns how many rows were published.</summary>
    private async Task<int> DrainOnceAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (!await TryAcquireDrainLockAsync(connection, transaction, cancellationToken))
        {
            // Another replica is draining; it will publish these rows.
            return 0;
        }

        var rows = await ClaimAsync(connection, transaction, cancellationToken);
        if (rows.Count == 0)
        {
            return 0;
        }

        var messages = rows.Select(ToMessage).ToArray();
        await _publisher.PublishAsync(messages, cancellationToken);

        await MarkDispatchedAsync(connection, transaction, rows, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var now = _timeProvider.GetUtcNow();
        foreach (var row in rows)
        {
            MageRideDiagnostics.OutboxDispatchLatencyMs.Record((now - row.CreatedAt).TotalMilliseconds);
        }

        MageRideDiagnostics.OutboxDispatched.Add(rows.Count);
        Interlocked.Add(ref DispatchedCount, rows.Count);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Dispatched {Count} outbox rows to {Topic} (up to id {LastId})",
                rows.Count, _options.Topic, rows[^1].Id);
        }

        return rows.Count;
    }

    private async Task<bool> TryAcquireDrainLockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1);", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = _advisoryLockKey });

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<List<OutboxRow>> ClaimAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_claimSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = _options.BatchSize });

        var rows = new List<OutboxRow>(_options.BatchSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OutboxRow(
                Id: reader.GetInt64(0),
                AggregateId: reader.GetGuid(1),
                EventType: reader.GetString(2),
                Payload: reader.GetString(3),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return rows;
    }

    private async Task MarkDispatchedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, List<OutboxRow> rows, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_markDispatchedSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            Value = rows.Select(static r => r.Id).ToArray(),
        });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private EventMessage ToMessage(OutboxRow row) => new(
        Topic: _options.Topic,
        Key: row.AggregateId.ToString(),
        Value: Encoding.UTF8.GetBytes(row.Payload),
        Headers: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["eventType"] = row.EventType,
            ["outboxId"] = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

    public override void Dispose()
    {
        _wakeUp.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Deterministic 64-bit key for the advisory lock. FNV-1a rather than Postgres <c>hashtext</c>
    /// so the value cannot shift with a server version.
    /// </summary>
    internal static long AdvisoryLockKey(string name)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            hash ^= b;
            hash *= prime;
        }

        return unchecked((long)hash);
    }
}
