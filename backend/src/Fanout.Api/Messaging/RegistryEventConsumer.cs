using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using MageRide.Fanout.Configuration;
using MageRide.Fanout.Realtime;
using MageRide.Fanout.Visibility;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;
using Microsoft.Extensions.Options;

namespace MageRide.Fanout.Messaging;

/// <summary>The <c>registry.events</c> types the Mode B entitlement cache is built from.</summary>
public static class RegistryEventTypes
{
    /// <summary>US-4.3b — the grantee accepted, so visibility begins.</summary>
    public const string ShareGranted = "share.granted";

    /// <summary>D-22 — the owner revoked, or the passenger unsubscribed.</summary>
    public const string ShareRevoked = "share.revoked";
}

/// <summary>
/// The share payload registry-svc writes into <c>registry.outbox</c> (C028).
/// </summary>
/// <remarks>
/// <b>There is no envelope on this topic, unlike <c>ride.events</c>.</b> The outbox dispatcher
/// publishes the payload column verbatim and carries the type in a Kafka <c>eventType</c> header
/// (<c>OutboxDispatcher.ToMessage</c>); ride-svc's rows happen to <em>contain</em> a full envelope
/// because <c>RideEvents.Build</c> serialises one into that column, and registry-svc's do not.
/// Deserialising this body looking for an <c>eventType</c> member would find none and silently
/// discard every share event.
/// </remarks>
/// <param name="PassengerId">
/// The field D6' §5.1 leaves out and §5.2 needs. registry-svc adds it deliberately (C028): a
/// <c>ShareRevoked</c> carrying only a vehicle id leaves this service two options, both wrong —
/// remove everybody watching the vehicle, or query registry-svc on the hot path to find out who.
/// </param>
public sealed record RegistryEventPayload(Guid? VehicleId, Guid? PassengerId)
{
    public static RegistryEventPayload? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RegistryEventPayload>(json, MageRideJson.StorageOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Consumes <c>registry.events</c> and keeps <c>share:{userId}</c> in step with it (D-23), then
/// pushes D-22's directed removal.
/// </summary>
/// <remarks>
/// <para>
/// <b>This service is the only writer of that SET.</b> registry-svc owns the durable grants and
/// publishes them; the cache is a projection shaped for one question asked on a socket connect.
/// A second writer would have to agree about what a missing key means, and it means "no Mode B
/// visibility" — see <see cref="IEntitlementCache"/>.
/// </para>
/// <para>
/// <b><see cref="AutoOffsetReset.Earliest"/></b>, for the same reason as the ride consumer and one
/// more: a fresh consumer group replays the topic, which is how a deployment with an empty Redis
/// rebuilds every passenger's entitlements. That is a rebuild bounded by the topic's retention, not
/// a guarantee — the C041 handoff records the gap.
/// </para>
/// </remarks>
public sealed class RegistryEventConsumer(
    IEntitlementCache entitlements,
    IFanoutControlPlane control,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<FanoutOptions> fanoutOptions,
    ILogger<RegistryEventConsumer> logger) : KafkaTopicConsumer(kafkaOptions, logger)
{
    private readonly FanoutOptions _fanout =
        fanoutOptions?.Value ?? throw new ArgumentNullException(nameof(fanoutOptions));

    protected override string Topic => EventTopics.RegistryEvents;

    protected override string GroupId => _fanout.ConsumerGroup;

    protected override async Task HandleAsync(
        ConsumeResult<string, byte[]> message, CancellationToken cancellationToken)
    {
        var eventType = EventTypeOf(message);

        if (eventType is not (RegistryEventTypes.ShareGranted or RegistryEventTypes.ShareRevoked))
        {
            // registry-svc publishes nine types here; the other seven are about vehicles and
            // documents and change nothing a socket is showing. A message with no header at all
            // lands here too, which is the right answer — it is not a share event.
            return;
        }

        var json = message.Message.Value is { Length: > 0 } value ? Encoding.UTF8.GetString(value) : string.Empty;
        var payload = RegistryEventPayload.TryParse(json);

        if (payload is not { VehicleId: { } vehicleId, PassengerId: { } passengerId })
        {
            // Not poison — a share event that names no passenger is a producer this service cannot
            // act on, and stalling the partition over it would stop every later revocation too.
            Logger.LogWarning(
                "A {EventType} at offset {Offset} carried no passenger or vehicle; "
                + "the entitlement cache is unchanged",
                eventType,
                message.Offset.Value);

            return;
        }

        if (eventType == RegistryEventTypes.ShareGranted)
        {
            await entitlements.GrantAsync(passengerId, vehicleId, cancellationToken);

            await control.PublishAsync(
                new FanoutSignal(FanoutSignalKinds.ShareGranted, passengerId, vehicleId),
                cancellationToken);

            return;
        }

        // The cache first, then the broadcast. A passenger removed from the group but still in the
        // SET would be put straight back on their next JoinGeocells; the other order leaves a window
        // of at most one round trip in which they are out of the SET and still receiving frames,
        // which the broadcast closes.
        await entitlements.RevokeAsync(passengerId, vehicleId, cancellationToken);

        await control.PublishAsync(
            new FanoutSignal(FanoutSignalKinds.ShareRevoked, passengerId, vehicleId),
            cancellationToken);

        logger.LogDebug(
            "Share on vehicle {VehicleId} revoked for passenger {PassengerId} (D-22)", vehicleId, passengerId);
    }

    /// <summary>
    /// The <c>eventType</c> Kafka header the outbox dispatcher stamps on every row it publishes.
    /// </summary>
    private static string? EventTypeOf(ConsumeResult<string, byte[]> message) =>
        message.Message.Headers is { } headers
        && headers.TryGetLastBytes("eventType", out var value)
            ? Encoding.UTF8.GetString(value)
            : null;
}
