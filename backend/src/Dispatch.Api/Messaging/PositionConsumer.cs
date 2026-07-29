using Confluent.Kafka;
using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Presence;
using MageRide.Shared.Messaging;
using MageRide.Shared.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Messaging;

/// <summary>
/// R-08's other half: <c>telemetry.normalized</c> keeps <c>dispatch.driver_presence</c> and the
/// Redis candidate index at the driver's live position.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why dispatch-svc and not position-processor-svc.</b> C024's position-processor writes the
/// three live-map indexes (<c>geo:live</c>, <c>veh:meta</c>, <c>cell:{h3}</c>) and stops there, and
/// its CLAUDE.md says why: "a sample carries no driverId, so writing
/// <c>driver:availability:{driverId}</c> would need a registry lookup this component has no
/// business doing on the hot path". dispatch-svc needs no lookup — <c>dispatch.driver_presence</c>
/// already holds the (driver, vehicle) pair, because the driver told this service which vehicle
/// they went on standby with. The join happens where the fact already lives.
/// </para>
/// <para>
/// <b><c>AutoOffsetReset.Latest</c>, like position-processor and unlike every other consumer on the
/// platform.</b> A presence index is *current state*: replaying an hour of positions from the
/// earliest offset would walk every driver through an hour of history, oldest last, and leave the
/// pool describing where everybody was rather than where they are. A booking committed while this
/// service was down still has to be dispatched, which is why <see cref="RideEventConsumer"/> reads
/// from the earliest — the two topics want opposite things and say so here.
/// </para>
/// <para>
/// <b>A sample for a vehicle nobody is on standby with is a no-op, not an error.</b> This topic
/// carries every Mode A bus and every Mode B shared vehicle on the platform; dispatch has a
/// presence row for a small fraction of them, and the update simply matches nothing.
/// </para>
/// </remarks>
public sealed class PositionConsumer(
    IServiceProvider services,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<DispatchOptions> dispatchOptions,
    ILogger<PositionConsumer> logger) : KafkaTopicConsumer(kafkaOptions, logger)
{
    private readonly DispatchOptions _dispatch =
        dispatchOptions?.Value ?? throw new ArgumentNullException(nameof(dispatchOptions));

    protected override string Topic => EventTopics.TelemetryNormalized;

    protected override string GroupId => _dispatch.PositionConsumerGroup;

    protected override AutoOffsetReset OffsetReset => AutoOffsetReset.Latest;

    protected override async Task HandleAsync(
        ConsumeResult<string, byte[]> message, CancellationToken cancellationToken)
    {
        // CBOR, not JSON: telemetry travels as PositionSampleCodec's compact encoding the whole way
        // from the handset (mqtt-topics.md §2.1), which is why the base class hands out bytes.
        var sample = message.Message.Value is { Length: > 0 } payload
            ? PositionSampleCodec.TryDecode(payload)
            : null;

        if (sample is null || !sample.IsWellFormed)
        {
            // position-processor already dropped the undecodable and the malformed before
            // republishing here, so anything that reaches this branch is a producer that has
            // changed shape. Replaying it produces the same nothing forever.
            throw new PoisonMessageException(
                $"Undecodable {EventTopics.TelemetryNormalized} message at offset {message.Offset.Value}.");
        }

        await using var scope = services.CreateAsyncScope();
        var presence = scope.ServiceProvider.GetRequiredService<IPresenceService>();

        // SampleTs, not ReceivedTs: D5' §3.2's freshness rule is about how old the GPS *fix* is, and
        // a replayed backlog (R-17) arrives with a fresh receive time and a stale capture time. The
        // whole point of the gate is to refuse the second one.
        await presence.RecordPositionAsync(
            sample.VehicleId, sample.Point, sample.SampleTs, cancellationToken);
    }
}
