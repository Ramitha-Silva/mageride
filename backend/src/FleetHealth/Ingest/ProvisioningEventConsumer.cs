using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using MageRide.FleetHealth.Configuration;
using MageRide.FleetHealth.Domain;
using MageRide.FleetHealth.Persistence;
using MageRide.Shared.Messaging;
using MageRide.Shared.Observability;
using Microsoft.Extensions.Options;

namespace MageRide.FleetHealth.Ingest;

/// <summary>
/// Consumes <c>provisioning.events</c> and keeps the binding facts <c>telemetry.device_health</c>
/// needs: the IMEI, the fleet, the credential state and US-3.8's decommission.
/// </summary>
/// <remarks>
/// <para>
/// <b>The binding plane is the authority on three of the four US-3.13 states' precondition.</b> A
/// tracker is <c>Decommissioned</c> because provisioning-svc revoked its credentials (US-3.8) —
/// nothing on the telemetry plane can know that, and a revoked device stops publishing, so waiting
/// for silence would report it as merely <c>Offline</c> for ever.
/// </para>
/// <para>
/// <b><see cref="KafkaTopicConsumer"/> here, unlike the ping path.</b> Everything about this topic
/// suits the kernel's consumer: a handful of events a day rather than thousands a second, a bind that
/// happened while this service was down still has to be applied, and the work per message is one
/// upsert. <c>Earliest</c> is the base class's default and is the right answer — a fleet onboarded
/// last night must appear on the dashboard this morning.
/// </para>
/// <para>
/// <b>Six event types, four of which matter.</b> <c>tracker.credential_rotated</c> and
/// <c>tracker.source_switched</c> change nothing about health: a rotation deliberately leaves the
/// outgoing credential valid (C030: "rotation is not revocation, and conflating them bricks
/// devices"), and a source switch says which of a phone and a tracker publishes, not whether either
/// is alive. They are committed unread rather than mapped to a state, because mapping them would make
/// a routine 90-day renewal look like an outage.
/// </para>
/// <para>
/// <b><c>tracker.unbound</c> carries a reason and no fleet, so it is applied as a revocation.</b> The
/// event is emitted by an owner's unbind, an admin's decommission and a T-08 quarantine alike (C030),
/// and in every one of those the device stops publishing under that vehicle. A subsequent
/// <c>tracker.bound</c> for the same vehicle clears it — which is why <c>decommissioned_at</c> is
/// assigned rather than coalesced.
/// </para>
/// </remarks>
public sealed class ProvisioningEventConsumer(
    IDeviceHealthRepository repository,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<FleetHealthOptions> options,
    TimeProvider clock,
    ILogger<ProvisioningEventConsumer> logger) : KafkaTopicConsumer(kafkaOptions, logger)
{
    /// <summary>The header the outbox dispatcher stamps the event name on.</summary>
    private const string EventTypeHeader = "eventType";

    /// <summary>
    /// C030's <c>TrackerEventTypes</c>, spelled here rather than referenced.
    /// </summary>
    /// <remarks>
    /// This project does not depend on Provisioning.Api and must not — but the names have to agree
    /// exactly, and a divergence is silent: the consumer would commit every message unread and every
    /// fleet dashboard would show a roster of trackers that never leave <c>Offline</c>. The C044 test
    /// suite asserts these four against provisioning-svc's own constants, which is where a rename
    /// should fail.
    /// </remarks>
    internal const string TrackerBound = "tracker.bound";

    internal const string TrackerUnbound = "tracker.unbound";

    internal const string TrackerRevoked = "tracker.revoked";

    internal const string TrackerQuarantined = "tracker.quarantined";

    private readonly IDeviceHealthRepository _repository =
        repository ?? throw new ArgumentNullException(nameof(repository));

    private readonly FleetHealthOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private long _applied;

    /// <summary>Binding changes this replica has applied. Read by the ingest test.</summary>
    public long Applied => Interlocked.Read(ref _applied);

    protected override string Topic => EventTopics.ProvisioningEvents;

    protected override string GroupId => _options.ProvisioningConsumerGroup;

    protected override async Task HandleAsync(
        ConsumeResult<string, byte[]> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var eventType = ReadEventType(message);

        if (eventType is null || !TryMap(eventType, message, _clock.GetUtcNow(), out var change))
        {
            // Not one of the four, or an envelope with no vehicle in it. Committed, because
            // redelivering it produces the same nothing for ever.
            return;
        }

        await _repository.ApplyBindingChangeAsync(change, cancellationToken);

        Interlocked.Increment(ref _applied);
        MageRideDiagnostics.DeviceHealthUpdates.Add(1, new KeyValuePair<string, object?>("input", "binding"));
    }

    /// <summary>
    /// Maps one <c>provisioning.events</c> record onto a binding change, or refuses it.
    /// </summary>
    /// <remarks>
    /// <b>The vehicle comes from the record key, with the payload as a fallback.</b> The key is the
    /// topic's partition key and is what orders an unbind before the bind that replaced it (C030);
    /// the payload's <c>vehicleId</c> is the same value written twice, and reading the key first means
    /// a producer that stops repeating it in the body changes nothing here.
    /// </remarks>
    internal static bool TryMap(
        string eventType,
        ConsumeResult<string, byte[]> message,
        DateTimeOffset now,
        out TrackerBindingChange change)
    {
        change = null!;

        if (eventType is not (TrackerBound or TrackerUnbound or TrackerRevoked or TrackerQuarantined))
        {
            return false;
        }

        JsonElement payload;
        JsonDocument? document = null;

        try
        {
            document = JsonDocument.Parse(message.Message.Value);
            payload = document.RootElement;
        }
        catch (JsonException)
        {
            document?.Dispose();
            return false;
        }

        using (document)
        {
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var vehicleId = Guid.TryParse(message.Message.Key, out var keyed)
                ? keyed
                : ReadGuid(payload, "vehicleId");

            if (vehicleId == Guid.Empty)
            {
                return false;
            }

            var (state, decommissionedAt) = eventType switch
            {
                TrackerBound => (TrackerBindingStates.Active, (DateTimeOffset?)null),
                TrackerQuarantined => (TrackerBindingStates.Quarantined, null),
                _ => (TrackerBindingStates.Revoked, ReadTimestamp(payload, "revokedAt", "unboundAt") ?? now),
            };

            change = new TrackerBindingChange(
                vehicleId,
                Imei: ReadString(payload, "imei"),

                // Only a bind carries the fleet (C030's envelopes), and the statement coalesces — so a
                // null here keeps the stored value. A decommissioned tracker still belongs to the
                // fleet whose dashboard has to show it as decommissioned, which is the whole point of
                // US-3.13's fourth state.
                FleetId: eventType == TrackerBound ? ReadNullableGuid(payload, "fleetId") : null,
                BindingState: state,
                DecommissionedAt: decommissionedAt);

            return true;
        }
    }

    private static string? ReadEventType(ConsumeResult<string, byte[]> message)
    {
        if (message.Message.Headers?.TryGetLastBytes(EventTypeHeader, out var raw) == true)
        {
            return Encoding.UTF8.GetString(raw);
        }

        // A producer that omitted the header. The outbox dispatcher always stamps it, so this covers a
        // hand-published payload rather than a real producer.
        try
        {
            using var document = JsonDocument.Parse(message.Message.Value);

            return document.RootElement.TryGetProperty("eventType", out var type) ? type.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Guid ReadGuid(JsonElement payload, string name) => ReadNullableGuid(payload, name) ?? Guid.Empty;

    private static Guid? ReadNullableGuid(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private static string? ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (payload.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.TryGetDateTimeOffset(out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return null;
    }
}
