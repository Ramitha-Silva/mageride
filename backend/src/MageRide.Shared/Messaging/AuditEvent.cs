using System.Text.Json;
using System.Text.Json.Serialization;
using MageRide.Shared.Http;

namespace MageRide.Shared.Messaging;

/// <summary>
/// The <c>audit.events</c> envelope, exactly as D6' §2.2 prints it:
/// <c>{ eventId, actorId, action, entityType, entityId, before, after, ts }</c> (D-35).
/// </summary>
/// <remarks>
/// <para>
/// <b>The partition key is <see cref="EntityId"/></b>, per the D6' §2.1 registry — so every fact
/// recorded about one entity arrives in the order it happened, which is the only property that
/// makes a <c>before</c>/<c>after</c> pair readable as a history rather than as two loose rows.
/// </para>
/// <para>
/// D6' §2.1 names the producer as "all (admin-bff interceptor)". mqtt-bridge-svc is the first
/// component to write one, because D-17's <c>mqtt.rate_violation</c> has no admin request behind
/// it — the actor is a device. The shape lives in the kernel rather than in the bridge so the
/// interceptor, and every service that follows it, cannot invent a second one.
/// </para>
/// </remarks>
/// <param name="EventId">Idempotency key for a consumer (D6' §2.3 "consumers key on eventId").</param>
/// <param name="ActorId">Who caused it — a user id, or a device/service principal. Never null in
/// practice; a fact with no actor is a fact nobody can be asked about.</param>
/// <param name="Action">Dotted verb, e.g. <c>mqtt.rate_violation</c>.</param>
/// <param name="EntityType">Aggregate the action was against, e.g. <c>vehicle</c>.</param>
/// <param name="EntityId">Aggregate id. Also the partition key.</param>
/// <param name="Before">State before, or <see langword="null"/> for an observation rather than a change.</param>
/// <param name="After">State after, or the observation itself.</param>
/// <param name="Ts">When it happened.</param>
public sealed record AuditEvent(
    Guid EventId,
    string ActorId,
    string Action,
    string EntityType,
    string EntityId,
    [property: JsonPropertyName("before")] JsonElement? Before,
    [property: JsonPropertyName("after")] JsonElement? After,
    DateTimeOffset Ts)
{
    /// <summary><c>audit.events</c> action for D-17's per-vehicle publish ceiling breach.</summary>
    /// <remarks>
    /// Named by D6' §3.3 and ADD §7.5.2, and by <c>backend/contracts/realtime/mqtt-topics.md</c> §4
    /// — the only <c>audit.events</c> action any spec spells for the MQTT plane.
    /// </remarks>
    public const string MqttRateViolation = "mqtt.rate_violation";

    /// <summary><see cref="EntityType"/> for a vehicle-scoped audit fact.</summary>
    public const string VehicleEntity = "vehicle";

    /// <summary>
    /// Builds an observation — an event with no <c>before</c>, because nothing changed state.
    /// </summary>
    public static AuditEvent Observed(
        string action, string entityType, string entityId, string actorId, object after, DateTimeOffset ts) =>
        new(
            Guid.CreateVersion7(),
            actorId,
            action,
            entityType,
            entityId,
            Before: null,
            After: JsonSerializer.SerializeToElement(after, MageRideJson.Options),
            ts);

    /// <summary>Serialises onto <c>audit.events</c>, keyed by <see cref="EntityId"/>.</summary>
    public EventMessage ToEventMessage() => new(
        EventTopics.AuditEvents,
        EntityId,
        JsonSerializer.SerializeToUtf8Bytes(this, MageRideJson.Options),
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["eventId"] = EventId.ToString(),
            ["action"] = Action,
        });
}
