using System.Text.Json;
using Confluent.Kafka;
using MageRide.HotPath.PersistenceWriter.Configuration;
using MageRide.Shared.Messaging;
using Microsoft.Extensions.Options;

namespace MageRide.HotPath.PersistenceWriter.Summaries;

/// <summary>
/// Consumes <c>trip.events</c> and turns each <c>session.ended</c> into ADD §9.2's trip summary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Event-driven, not swept.</b> A summary needs the journey to be over, and the only thing that
/// knows when a Mode A/B journey is over is trip-state-svc — through its idle timer, its arrival
/// geofence, a driver's tap, or a last will (C031). Polling <c>trips.sessions</c> for newly-closed
/// rows would duplicate that service's own clock and would still have to be idempotent, so the event
/// it already publishes is both cheaper and less to go wrong.
/// </para>
/// <para>
/// <b>Earliest, and per-message.</b> This is the kernel's <see cref="KafkaTopicConsumer"/> because
/// everything about it suits: a handful of events a minute rather than thousands of rows a second, a
/// session that ended while the writer was down still needs its summary, and the work per message is
/// a couple of queries rather than a batch. Nothing here is on the hot path.
/// </para>
/// <para>
/// <b>The three failure modes are told apart deliberately.</b> An event whose session cannot be found
/// is <i>retried</i> — most likely its transaction was not yet visible to this connection. One whose
/// session is <c>ACTIVE</c> again is a US-5.10 restart and is <i>committed</i>, because the next end
/// will bring it back. Anything that is not a <c>session.ended</c> is committed unread.
/// </para>
/// </remarks>
public sealed class TripEventConsumer(
    IServiceProvider services,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<PersistenceWriterOptions> writerOptions,
    ILogger<TripEventConsumer> logger) : KafkaTopicConsumer(kafkaOptions, logger)
{
    /// <summary>The event that closes a journey — C031's <c>SessionEventTypes.SessionEnded</c>.</summary>
    /// <remarks>
    /// Spelled here rather than referenced: this project does not depend on TripState.Api and must
    /// not. Asserted against that service's own constant in the C040 test suite, which is where a
    /// rename should fail rather than in production as summaries that silently stop being written.
    /// </remarks>
    public const string SessionEndedEvent = "session.ended";

    /// <summary>The header C031's outbox dispatcher stamps the event name on.</summary>
    private const string EventTypeHeader = "eventType";

    private readonly PersistenceWriterOptions _options =
        writerOptions?.Value ?? throw new ArgumentNullException(nameof(writerOptions));

    /// <summary>Summaries this replica has written. Read by the C040 tests.</summary>
    public long Summarised => Interlocked.Read(ref _summarised);

    private long _summarised;

    protected override string Topic => EventTopics.TripEvents;

    protected override string GroupId => _options.TripConsumerGroup;

    protected override async Task HandleAsync(
        ConsumeResult<string, byte[]> message, CancellationToken cancellationToken)
    {
        if (!IsSessionEnded(message))
        {
            return;
        }

        var ended = Parse(message);

        await using var scope = services.CreateAsyncScope();
        var summaries = scope.ServiceProvider.GetRequiredService<ITripSummaryService>();

        var summary = await summaries.SummariseAsync(ended, cancellationToken);

        switch (summary.Status)
        {
            case SummaryStatus.Written:
                Interlocked.Increment(ref _summarised);
                return;

            case SummaryStatus.SessionActive:
                // A US-5.10 restart. The journey is running again and may run for another hour, so
                // this offset is committed and the next `session.ended` does the work.
                return;

            default:
                // The event outran its own transaction. Thrown, so the offset stays uncommitted and
                // the record is redelivered — a summary lost to that race is a journey with no
                // record of how far it went.
                throw new SummaryNotReadyException(
                    $"Session {ended.SessionId} has no trips.sessions row yet; leaving the offset uncommitted.");
        }
    }

    /// <summary>
    /// Whether this record is a <c>session.ended</c>.
    /// </summary>
    /// <remarks>
    /// The header first, because that is what the outbox dispatcher stamps and reading it costs no
    /// parse. Falling back to the body covers a producer that omitted the header — and a record with
    /// neither is not this consumer's business either way.
    /// </remarks>
    private static bool IsSessionEnded(ConsumeResult<string, byte[]> message)
    {
        if (message.Message.Headers?.TryGetLastBytes(EventTypeHeader, out var raw) == true)
        {
            return System.Text.Encoding.UTF8.GetString(raw) == SessionEndedEvent;
        }

        try
        {
            using var document = JsonDocument.Parse(message.Message.Value);

            return document.RootElement.TryGetProperty("eventType", out var type)
                   && type.GetString() == SessionEndedEvent;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private EndedSession Parse(ConsumeResult<string, byte[]> message)
    {
        try
        {
            using var document = JsonDocument.Parse(message.Message.Value);
            var root = document.RootElement;

            // The outbox dispatcher may publish either the payload alone or an envelope wrapping it;
            // C031 writes the payload, and reading through a `payload` property when there is one
            // means this consumer survives either shape.
            var body = root.TryGetProperty("payload", out var payload) ? payload : root;

            return new EndedSession(
                body.GetProperty("sessionId").GetGuid(),
                body.GetProperty("vehicleId").GetGuid(),
                body.GetProperty("driverId").GetGuid(),
                body.TryGetProperty("mode", out var mode) ? mode.GetString() ?? "B" : "B",
                body.TryGetProperty("endReason", out var reason) ? reason.GetString() : null,
                body.TryGetProperty("endedAt", out var endedAt)
                    ? endedAt.GetDateTimeOffset()
                    : DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException
                                      or InvalidOperationException)
        {
            // A session.ended this service cannot read will not become readable. Committing past it
            // costs one summary; stalling the partition costs every summary behind it.
            throw new PoisonMessageException(
                $"A {EventTopics.TripEvents} record at offset {message.Offset.Value} is not a readable " +
                $"{SessionEndedEvent}.",
                ex);
        }
    }

    /// <summary>
    /// A session that is not summarisable <i>yet</i>. Left uncommitted so it is redelivered.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="PoisonMessageException"/>: an event that outran its own
    /// transaction becomes readable a moment later, and committing past it would lose the summary for
    /// a journey that really happened.
    /// </remarks>
    private sealed class SummaryNotReadyException(string message) : Exception(message);
}
