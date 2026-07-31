using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using MageRide.Shared.Messaging;
using MageRide.Voip.Configuration;
using MageRide.Voip.Persistence;
using MageRide.Voip.Signalling;
using Microsoft.Extensions.Options;

namespace MageRide.Voip.Messaging;

/// <summary>
/// Closes a ride's room when the ride ends — the half of "expiring at trip end" a token cannot do.
/// </summary>
/// <remarks>
/// <para>
/// A LiveKit token's <c>exp</c> is checked at <em>join</em> and never again, so a call that
/// connected a minute before the ride ended would run for as long as the two parties kept talking.
/// D6' §6 says signalling is scoped to the ride and expires with it; deleting the room is what makes
/// that true, and it is also what makes an unexpired token worthless — there is nothing left to join.
/// </para>
/// <para>
/// <b>The database row is closed whether or not LiveKit answered.</b> What
/// <c>comms.voip_sessions.ended_at</c> records is that the ride ended, which is a fact about the
/// ride; whether the SFU acknowledged the teardown is an operational detail that is logged. Getting
/// this the other way round would leave a session open for ever every time LiveKit restarted.
/// </para>
/// </remarks>
internal sealed class RideTerminalHandler
{
    private readonly IVoipRepository _repository;
    private readonly ILiveKitRoomService _rooms;
    private readonly ILogger<RideTerminalHandler> _logger;

    public RideTerminalHandler(
        IVoipRepository repository, ILiveKitRoomService rooms, ILogger<RideTerminalHandler> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Ends every open session on <paramref name="rideId"/> and closes its rooms.</summary>
    /// <remarks>
    /// Idempotent: the <c>UPDATE</c> is bound to <c>ended_at IS NULL</c>, so a redelivered event
    /// returns no rooms and does nothing. That is what lets the consumer prefer redelivery to a
    /// commit on any failure.
    /// </remarks>
    public async Task<int> EndRideAsync(Guid rideId, CancellationToken cancellationToken)
    {
        var rooms = await _repository.EndSessionsAsync(rideId, cancellationToken);

        foreach (var room in rooms)
        {
            await _rooms.CloseRoomAsync(room, cancellationToken);
        }

        if (rooms.Count > 0)
        {
            _logger.LogInformation(
                "Ride {RideId} ended; {Rooms} in-app call room(s) closed.", rideId, rooms.Count);
        }

        return rooms.Count;
    }
}

/// <summary>
/// The <c>ride.events</c> loop.
/// </summary>
/// <remarks>
/// <para>
/// <b>The trigger is the ride's state, not the event name.</b> ride-svc publishes sixteen event
/// types and ten of them are terminals; a consumer keyed on names would need all ten and would
/// silently miss the eleventh. Every event on this topic carries the ride id as its key, so the
/// handler reads <c>rides.rides.state</c> and asks the one question this service cares about —
/// <em>has this ride ended</em> — which is also the same question <c>CallService</c> asks when it
/// refuses to mint. One rule, two callers.
/// </para>
/// <para>
/// <b>An unparseable message is poison and is committed past</b>; anything else is left uncommitted
/// for redelivery, which is safe because ending a ride's sessions is idempotent.
/// </para>
/// </remarks>
internal sealed class RideEventConsumer : KafkaTopicConsumer
{
    private readonly IServiceProvider _services;
    private readonly VoipOptions _options;

    public RideEventConsumer(
        IServiceProvider services,
        IOptions<KafkaOptions> kafkaOptions,
        IOptions<VoipOptions> voipOptions,
        ILogger<RideEventConsumer> logger)
        : base(kafkaOptions, logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = voipOptions?.Value ?? throw new ArgumentNullException(nameof(voipOptions));
    }

    protected override string Topic => EventTopics.RideEvents;

    protected override string GroupId => _options.ConsumerGroup;

    protected override async Task HandleAsync(
        ConsumeResult<string, byte[]> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (RideKey(message) is not { } rideId)
        {
            throw new PoisonMessageException(
                $"ride.events message at offset {message.Offset.Value} carries no ride id "
                + "(neither the message key nor a rideId member).");
        }

        await using var scope = _services.CreateAsyncScope();

        var repository = scope.ServiceProvider.GetRequiredService<IVoipRepository>();
        var handler = scope.ServiceProvider.GetRequiredService<RideTerminalHandler>();

        var ride = await repository.FindRideAsync(rideId, cancellationToken);

        // A ride that is not terminal, or one this service cannot see at all, has nothing to close.
        // Both are ordinary: most of the sixteen event types are moves through the machine.
        if (ride?.IsTerminal is not true)
        {
            return;
        }

        await handler.EndRideAsync(rideId, cancellationToken);
    }

    /// <summary>
    /// The ride this event is about.
    /// </summary>
    /// <remarks>
    /// The message key first, because D6' §2.1 makes <c>rideId</c> the partition key of this topic
    /// and every producer sets it. The body is the fallback for the four <c>location.request.*</c>
    /// events ride-svc keys by request id instead — they carry no ride and are skipped, which is
    /// what the parse returning null expresses.
    /// </remarks>
    private static Guid? RideKey(ConsumeResult<string, byte[]> message)
    {
        if (Guid.TryParse(message.Message.Key, out var keyed))
        {
            return keyed;
        }

        if (message.Message.Value is not { Length: > 0 } value)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(value));

            var body = document.RootElement.TryGetProperty("payload", out var payload)
                       && payload.ValueKind == JsonValueKind.Object
                ? payload
                : document.RootElement;

            return body.TryGetProperty("rideId", out var rideId)
                   && rideId.ValueKind == JsonValueKind.String
                   && Guid.TryParse(rideId.GetString(), out var parsed)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
