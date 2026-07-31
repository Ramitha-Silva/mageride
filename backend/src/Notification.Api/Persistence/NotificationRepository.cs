using Dapper;
using MageRide.Notification.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.Notification.Persistence;

/// <summary>What to enqueue.</summary>
public sealed record NewNotification(
    string DedupeKey,
    string NotificationType,
    string? TemplateKey,
    string Channel,
    Guid? RecipientUserId,
    string? RecipientPhone,
    string Language,
    string Priority,
    string Payload,
    string Status,
    DateTimeOffset? NextAttemptAt,
    Guid? FallbackOf = null);

/// <summary>
/// <c>comms.notifications</c> (migration 1308) — the queue, the log and the E-01 fence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every method here is a claim, not a read-then-write.</b> The enqueue is
/// <c>ON CONFLICT DO NOTHING</c> on <c>dedupe_key</c>; the delivery claim and the ack sweep are
/// each one <c>UPDATE … FROM (SELECT … FOR UPDATE SKIP LOCKED) … RETURNING</c>. That is the whole
/// concurrency argument: two replicas, a redelivered Kafka message and a retried sweep all resolve
/// against the same rows and the database picks the winner. Nothing in this service holds a lock in
/// memory, and no transaction is held open across a network call to a gateway.
/// </para>
/// </remarks>
public interface INotificationRepository
{
    /// <summary>
    /// Claims <paramref name="notification"/>. <see langword="null"/> means somebody already has it
    /// — the redelivery case, which is not an error.
    /// </summary>
    Task<NotificationRow?> EnqueueAsync(NewNotification notification, CancellationToken cancellationToken);

    /// <summary>Reads one row back, for the ack route and for the tests.</summary>
    Task<NotificationRow?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary><see cref="FindAsync"/> by the producer's claim.</summary>
    Task<NotificationRow?> FindByDedupeKeyAsync(string dedupeKey, CancellationToken cancellationToken);

    /// <summary>
    /// Leases up to <paramref name="batchSize"/> due notifications to this worker pass, pushing
    /// their next attempt out by <paramref name="lease"/> so another replica does not take them
    /// while this one is talking to a gateway.
    /// </summary>
    /// <remarks>
    /// The lease is a *deadline on the row*, not a lock held in a process: a worker that dies
    /// mid-send leaves rows that become due again when it elapses, which is what makes the queue
    /// self-healing without a reaper. It is the same shape as ride-svc's timer lease (R-04).
    /// </remarks>
    Task<IReadOnlyList<NotificationRow>> LeaseDueAsync(
        DateTimeOffset now, TimeSpan lease, int batchSize, CancellationToken cancellationToken);

    /// <summary>The transport accepted it.</summary>
    Task MarkSentAsync(
        Guid id,
        string provider,
        string? providerMessageId,
        DateTimeOffset? ackDeadlineAt,
        CancellationToken cancellationToken);

    /// <summary>Another attempt is due at <paramref name="nextAttemptAt"/> (D-27's backoff).</summary>
    Task MarkRetryAsync(
        Guid id, string? provider, string error, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken);

    /// <summary>Out of attempts, or undeliverable on arrival.</summary>
    Task MarkFailedAsync(Guid id, string? provider, string error, CancellationToken cancellationToken);

    /// <summary>Muted by preference (US-10.7) or refused by a limit (P-12).</summary>
    Task MarkSuppressedAsync(Guid id, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// E-01: the device confirmed it woke up. Guarded on <c>Sent</c>, so an ack that arrives after
    /// the fallback has already fired changes nothing and answers false.
    /// </summary>
    Task<bool> TryAckAsync(Guid id, Guid? deviceOwner, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// E-01's sweep: claims offer pushes whose three seconds elapsed with no ack, moving each to
    /// <c>FellBackToSms</c> in the same statement that selects it.
    /// </summary>
    /// <remarks>
    /// <b>This is where "exactly once" lives.</b> The <c>UPDATE … WHERE status = 'Sent' AND
    /// acked_at IS NULL AND ack_deadline_at &lt;= now RETURNING</c> is atomic, so two replicas
    /// sweeping the same instant produce one claimed row between them and the loser gets nothing to
    /// send. A worker that read first and updated afterwards would send two SMS to one driver; one
    /// that updated after sending would send two after a crash.
    /// </remarks>
    Task<IReadOnlyList<NotificationRow>> ClaimUnackedOffersAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    /// <summary>Deletes settled rows older than <paramref name="before"/> (and their numbers).</summary>
    Task<int> PurgeBeforeAsync(DateTimeOffset before, int batchSize, CancellationToken cancellationToken);

    /// <summary>Everything sent to one recipient — the §14.4 throttles and the tests.</summary>
    Task<IReadOnlyList<NotificationRow>> ListForRecipientAsync(
        Guid recipientUserId, string? notificationType, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// The two diagnostic columns an inline dispatch reports back to its caller.
    /// </summary>
    /// <remarks>
    /// Off <see cref="NotificationRow"/> deliberately: <c>provider</c> and <c>last_error</c> are
    /// operator-facing diagnostics rather than state, and putting them on the row every worker pass
    /// reads back would carry a 500-character error string through the queue for the one caller that
    /// wants it.
    /// </remarks>
    Task<(string? Provider, string? LastError)> ReadOutcomeAsync(Guid id, CancellationToken cancellationToken);
}

/// <inheritdoc cref="INotificationRepository"/>
internal sealed class NotificationRepository(INpgsqlConnectionFactory connections) : INotificationRepository
{
    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private const string Columns =
        """
        id, dedupe_key, notification_type, template_key, channel, recipient_user_id, recipient_phone,
        language, priority, payload::text AS payload, status, attempts, next_attempt_at,
        ack_deadline_at, acked_at, fallback_of, created_at
        """;

    public async Task<NotificationRow?> EnqueueAsync(
        NewNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        await using var connection = await _connections.OpenAsync(cancellationToken);

        // ON CONFLICT DO NOTHING with no RETURNING on the conflict path: a redelivered event finds
        // nothing and sends nothing, which is the point. The caller tells "claimed" from "already
        // claimed" by the null.
        return await connection.QuerySingleOrDefaultAsync<NotificationRow>(
            new CommandDefinition(
                $"""
                 INSERT INTO comms.notifications
                   (dedupe_key, notification_type, template_key, channel, recipient_user_id, recipient_phone,
                    language, priority, payload, status, next_attempt_at, fallback_of)
                 VALUES
                   (@DedupeKey, @NotificationType, @TemplateKey, @Channel, @RecipientUserId, @RecipientPhone,
                    @Language, @Priority, @Payload::jsonb, @Status, @NextAttemptAt, @FallbackOf)
                 ON CONFLICT (dedupe_key) DO NOTHING
                 RETURNING {Columns};
                 """,
                notification,
                cancellationToken: cancellationToken));
    }

    public async Task<NotificationRow?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<NotificationRow>(
            new CommandDefinition(
                $"SELECT {Columns} FROM comms.notifications WHERE id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken));
    }

    public async Task<NotificationRow?> FindByDedupeKeyAsync(string dedupeKey, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<NotificationRow>(
            new CommandDefinition(
                $"SELECT {Columns} FROM comms.notifications WHERE dedupe_key = @DedupeKey;",
                new { DedupeKey = dedupeKey },
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<NotificationRow>> LeaseDueAsync(
        DateTimeOffset now, TimeSpan lease, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<NotificationRow>(
            new CommandDefinition(
                $"""
                 WITH due AS (
                   SELECT id
                     FROM comms.notifications
                    WHERE status = 'Pending'
                      AND (next_attempt_at IS NULL OR next_attempt_at <= @Now)
                    ORDER BY next_attempt_at NULLS FIRST, created_at
                    LIMIT @BatchSize
                      FOR UPDATE SKIP LOCKED)
                 UPDATE comms.notifications AS n
                    SET next_attempt_at = @LeaseUntil
                   FROM due
                  WHERE n.id = due.id
                 RETURNING {Qualified("n")};
                 """,
                new { Now = now, LeaseUntil = now + lease, BatchSize = batchSize },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task MarkSentAsync(
        Guid id,
        string provider,
        string? providerMessageId,
        DateTimeOffset? ackDeadlineAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE comms.notifications
                   SET status = 'Sent',
                       attempts = attempts + 1,
                       provider = @Provider,
                       provider_message_id = @ProviderMessageId,
                       ack_deadline_at = @AckDeadlineAt,
                       next_attempt_at = NULL,
                       last_error = NULL
                 WHERE id = @Id;
                """,
                new { Id = id, Provider = provider, ProviderMessageId = providerMessageId, AckDeadlineAt = ackDeadlineAt },
                cancellationToken: cancellationToken));
    }

    public async Task MarkRetryAsync(
        Guid id, string? provider, string error, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE comms.notifications
                   SET attempts = attempts + 1,
                       provider = COALESCE(@Provider, provider),
                       last_error = @Error,
                       next_attempt_at = @NextAttemptAt
                 WHERE id = @Id;
                """,
                new { Id = id, Provider = provider, Error = Truncate(error), NextAttemptAt = nextAttemptAt },
                cancellationToken: cancellationToken));
    }

    public async Task MarkFailedAsync(
        Guid id, string? provider, string error, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE comms.notifications
                   SET status = 'Failed',
                       attempts = attempts + 1,
                       provider = COALESCE(@Provider, provider),
                       last_error = @Error,
                       next_attempt_at = NULL
                 WHERE id = @Id;
                """,
                new { Id = id, Provider = provider, Error = Truncate(error) },
                cancellationToken: cancellationToken));
    }

    public async Task MarkSuppressedAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE comms.notifications
                   SET status = 'Suppressed',
                       last_error = @Reason,
                       next_attempt_at = NULL
                 WHERE id = @Id;
                """,
                new { Id = id, Reason = Truncate(reason) },
                cancellationToken: cancellationToken));
    }

    public async Task<bool> TryAckAsync(
        Guid id, Guid? deviceOwner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        // Bound to `status = 'Sent'`: an ack that arrives after the sweep has already fallen back
        // finds a `FellBackToSms` row and matches nothing. The driver gets both messages once,
        // which is the honest outcome of a slow handset — an ack cannot un-send an SMS.
        //
        // `recipient_user_id = @Owner` is authorisation inside the statement rather than in a prior
        // read: without it, an ack is a claim about somebody else's notification.
        var updated = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE comms.notifications
                   SET status = 'Acked', acked_at = @Now
                 WHERE id = @Id
                   AND status = 'Sent'
                   AND acked_at IS NULL
                   AND (@Owner::uuid IS NULL OR recipient_user_id = @Owner);
                """,
                new { Id = id, Owner = deviceOwner, Now = now },
                cancellationToken: cancellationToken));

        return updated == 1;
    }

    public async Task<IReadOnlyList<NotificationRow>> ClaimUnackedOffersAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<NotificationRow>(
            new CommandDefinition(
                $"""
                 WITH due AS (
                   SELECT id
                     FROM comms.notifications
                    WHERE status = 'Sent'
                      AND acked_at IS NULL
                      AND ack_deadline_at IS NOT NULL
                      AND ack_deadline_at <= @Now
                    ORDER BY ack_deadline_at
                    LIMIT @BatchSize
                      FOR UPDATE SKIP LOCKED)
                 UPDATE comms.notifications AS n
                    SET status = 'FellBackToSms'
                   FROM due
                  WHERE n.id = due.id
                 RETURNING {Qualified("n")};
                 """,
                new { Now = now, BatchSize = batchSize },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<int> PurgeBeforeAsync(DateTimeOffset before, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        // Settled rows only. A `Pending` one older than the retention window is a bug worth seeing
        // rather than a row worth deleting, and deleting it would drop a message nobody sent.
        return await connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM comms.notifications
                 WHERE id IN (
                   SELECT id FROM comms.notifications
                    WHERE created_at < @Before
                      AND status IN ('Sent','Acked','Failed','Suppressed','FellBackToSms')
                    LIMIT @BatchSize);
                """,
                new { Before = before, BatchSize = batchSize },
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<NotificationRow>> ListForRecipientAsync(
        Guid recipientUserId, string? notificationType, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<NotificationRow>(
            new CommandDefinition(
                $"""
                 SELECT {Columns}
                   FROM comms.notifications
                  WHERE recipient_user_id = @RecipientUserId
                    AND (@NotificationType::text IS NULL OR notification_type = @NotificationType)
                  ORDER BY created_at DESC
                  LIMIT @Limit;
                 """,
                new { RecipientUserId = recipientUserId, NotificationType = notificationType, Limit = limit },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<(string? Provider, string? LastError)> ReadOutcomeAsync(
        Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<OutcomeRow>(
            new CommandDefinition(
                "SELECT provider, last_error FROM comms.notifications WHERE id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken));

        return (row?.Provider, row?.LastError);
    }

    private sealed record OutcomeRow(string? Provider, string? LastError);

    /// <summary><see cref="Columns"/> qualified by an alias, for the RETURNING of an UPDATE … FROM.</summary>
    private static string Qualified(string alias) =>
        $"""
         {alias}.id, {alias}.dedupe_key, {alias}.notification_type, {alias}.template_key, {alias}.channel,
         {alias}.recipient_user_id, {alias}.recipient_phone, {alias}.language, {alias}.priority,
         {alias}.payload::text AS payload, {alias}.status, {alias}.attempts, {alias}.next_attempt_at,
         {alias}.ack_deadline_at, {alias}.acked_at, {alias}.fallback_of, {alias}.created_at
         """;

    /// <summary>
    /// <c>last_error</c> is a diagnostic, not a log. A gateway that answers with a page of HTML must
    /// not put a page of HTML into every row of the queue.
    /// </summary>
    private static string Truncate(string error) =>
        error.Length <= 500 ? error : error[..500];
}
