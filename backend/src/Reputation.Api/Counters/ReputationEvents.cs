using System.Text.Json;
using MageRide.Reputation.Domain;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;

namespace MageRide.Reputation.Counters;

/// <summary>The event types reputation-svc produces on <c>reputation.events</c>.</summary>
/// <remarks>
/// <para>
/// <b>Neither the topic nor either name is in D6' §2.1's registry</b> — a micro-change-set raised
/// in the C033 handoff, the same shape as <c>registry.events</c> (C028) and
/// <c>provisioning.events</c> (C030). <c>fraud.suspected</c> has a producer (this service, ADD §6
/// and §12.6) and a consumer (the admin fraud-review queue, C052) and no topic anywhere;
/// <c>reputation.block_state_changed</c> is this service's, and is what lets a dispatch-svc cache
/// invalidate on the fact rather than on a TTL.
/// </para>
/// <para>
/// Keyed by <b>userId</b>, not rideId. A block state is a fact about a person and the ordering that
/// has to hold is per person — two rides completing at once must not be able to deliver their
/// consequences out of order for the same passenger.
/// </para>
/// </remarks>
public static class ReputationEventTypes
{
    /// <summary>An E-07 detector raised a signal for admin review.</summary>
    public const string FraudSuspected = "fraud.suspected";

    /// <summary>The effective block state moved (D-04).</summary>
    public const string BlockStateChanged = "reputation.block_state_changed";
}

/// <summary>The <c>reputation.block_state_changed</c> payload.</summary>
public sealed record BlockStateChangedPayload(
    Guid UserId,
    string? PreviousState,
    string State,
    string Reason,
    string Source,
    DateTimeOffset? ExpiresAt,
    bool DispatchEligible,
    int CancellationsContinuous,
    int ReportsTotal,
    int NoShows,
    Guid? RideId,
    Guid? ActorId);

/// <summary>The <c>fraud.suspected</c> payload (E-07).</summary>
/// <param name="Detail">
/// The detector's evidence, verbatim from <c>reputation.fraud_flags.detail</c>. A consumer that
/// only wants to queue the flag reads <paramref name="Summary"/>; one that wants to re-score it
/// reads this.
/// </param>
public sealed record FraudSuspectedPayload(
    Guid FlagId,
    string Kind,
    Guid SubjectId,
    string SubjectType,
    Guid? RelatedId,
    string WindowKey,
    string Summary,
    IReadOnlyDictionary<string, object?> Detail);

/// <summary>Builds the outbox rows this service writes.</summary>
public static class ReputationEvents
{
    public static OutboxRecord BlockStateChanged(BlockStateChangedPayload payload, Guid eventId, DateTimeOffset ts)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return Build(payload.UserId, ReputationEventTypes.BlockStateChanged, payload, eventId, ts);
    }

    public static OutboxRecord FraudSuspected(FraudFlagRow flag, FraudSignal signal, Guid eventId, DateTimeOffset ts)
    {
        ArgumentNullException.ThrowIfNull(flag);
        ArgumentNullException.ThrowIfNull(signal);

        var payload = new FraudSuspectedPayload(
            FlagId: flag.Id,
            Kind: flag.Kind,
            SubjectId: signal.SubjectId,
            SubjectType: signal.SubjectType,
            RelatedId: signal.RelatedId,
            WindowKey: signal.WindowKey,
            Summary: signal.Summary,
            Detail: signal.Detail);

        return Build(signal.SubjectId, ReputationEventTypes.FraudSuspected, payload, eventId, ts);
    }

    /// <summary>
    /// The D6' §2.2 envelope shape every MageRide topic uses — <c>eventId</c> for the consumer's
    /// dedupe, the aggregate id under its own name, and the payload nested rather than flattened.
    /// </summary>
    private static OutboxRecord Build<TPayload>(
        Guid aggregateId, string eventType, TPayload payload, Guid eventId, DateTimeOffset ts)
    {
        var envelope = new
        {
            eventId,
            eventType,
            userId = aggregateId,
            ts,
            payload,
        };

        // MageRideJson.StorageOptions: camelCase, nulls omitted — so an absent rideId on an
        // admin-initiated change is an absent member rather than a claim about one.
        return OutboxRecord.Create(
            aggregateId, eventType, JsonSerializer.Serialize(envelope, MageRideJson.StorageOptions));
    }
}
