using System.Diagnostics;
using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.HotPath.PositionProcessor.Redis;
using MageRide.Shared.Messaging;
using MageRide.Shared.Observability;
using MageRide.Shared.Telemetry;
using Microsoft.Extensions.Options;

namespace MageRide.HotPath.PositionProcessor.Processing;

/// <summary>What became of one device payload.</summary>
public enum PositionOutcome
{
    /// <summary>Normalised, indexed and published.</summary>
    Indexed,

    /// <summary>Not decodable as a position sample. Counted and dropped.</summary>
    Undecodable,

    /// <summary>Decoded but outside the CHECK domains the sink enforces (0/999 degrees, negative seq).</summary>
    Malformed,

    /// <summary>A <c>seq</c> at or below the vehicle's watermark — a replay (R-17, T-05).</summary>
    Replayed,
}

/// <summary>The result of processing one payload, and the cell it landed in.</summary>
public sealed record PositionResult(PositionOutcome Outcome, PositionSample? Sample = null, string? Cell = null);

/// <summary>Normalises one device payload into the live indexes.</summary>
public interface IPositionProcessor
{
    /// <summary>
    /// Processes a <c>telemetry.raw</c> payload.
    /// </summary>
    /// <param name="payload">The device's bytes, exactly as they came off EMQX.</param>
    /// <param name="vehicleIdFromTopic">The vehicle the <b>topic</b> named. EMQX authenticated this;
    /// the payload's own <c>vehicleId</c> did not go through any such check.</param>
    Task<PositionResult> ProcessAsync(
        ReadOnlyMemory<byte> payload, Guid vehicleIdFromTopic, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPositionProcessor"/>
/// <remarks>
/// <para>
/// Four steps, in this order: decode, check the sample is a position at all, discard replays on the
/// <c>seq</c> watermark, then write the live indexes and republish onto
/// <c>telemetry.normalized</c>.
/// </para>
/// <para>
/// <b>The topic's vehicle wins over the payload's.</b> EMQX bound the topic to the device's
/// credential (<c>acl.conf</c> + <c>emqx.conf</c>'s <c>verify_claims</c>); the payload is whatever
/// the device chose to write. A handset that publishes its neighbour's <c>vehicleId</c> inside an
/// otherwise valid sample is publishing on its own authenticated topic, so the sample is rebound to
/// the topic's vehicle and the disagreement is logged. Trusting the payload here would undo the
/// whole point of the ACL — and the anti-spoof work that would investigate the handset properly is
/// C040's.
/// </para>
/// <para>
/// <b>A bad sample is dropped, never retried.</b> Redelivering an unparseable payload produces the
/// same nothing forever, and one misbehaving handset must not stall the partition every other
/// vehicle in its shard shares. Each drop is counted by reason so the shape of the badness is
/// visible without reading logs.
/// </para>
/// </remarks>
public sealed class PositionProcessor(
    ILivePositionIndex index,
    IEventPublisher publisher,
    IOptions<PositionProcessorOptions> options,
    TimeProvider clock,
    ILogger<PositionProcessor> logger) : IPositionProcessor
{
    private readonly PositionProcessorOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<PositionResult> ProcessAsync(
        ReadOnlyMemory<byte> payload, Guid vehicleIdFromTopic, CancellationToken cancellationToken)
    {
        using var activity = MageRideDiagnostics.ActivitySource.StartActivity(
            "position-processor.process", ActivityKind.Consumer);
        activity?.SetTag("mageride.vehicle_id", vehicleIdFromTopic);

        var decoded = PositionSampleCodec.TryDecode(payload.Span);

        if (decoded is null)
        {
            Drop(PositionOutcome.Undecodable, vehicleIdFromTopic);
            return new PositionResult(PositionOutcome.Undecodable);
        }

        var sample = Rebind(decoded, vehicleIdFromTopic);

        if (!sample.IsWellFormed)
        {
            logger.LogWarning(
                "Dropping a malformed sample from vehicle {VehicleId}: seq {Seq}, ({Lat}, {Lng}), source {Source}",
                sample.VehicleId, sample.Seq, sample.Lat, sample.Lng, sample.Source);

            Drop(PositionOutcome.Malformed, sample.VehicleId);
            return new PositionResult(PositionOutcome.Malformed, sample);
        }

        // The platform's receive clock, stamped once here so every downstream consumer reads the
        // same value rather than each measuring its own arrival.
        var stamped = sample.ReceivedTs is null ? sample with { ReceivedTs = clock.GetUtcNow() } : sample;

        var cell = await index.RecordAsync(stamped, cancellationToken);

        if (cell is null)
        {
            Drop(PositionOutcome.Replayed, stamped.VehicleId);
            return new PositionResult(PositionOutcome.Replayed, stamped);
        }

        if (_options.PublishNormalized)
        {
            await publisher.PublishAsync(
                new EventMessage(
                    EventTopics.TelemetryNormalized,
                    // Same partition key as telemetry.raw, so per-vehicle ordering holds across the
                    // whole plane (D6' §2.1).
                    stamped.VehicleId.ToString(),
                    PositionSampleCodec.Encode(stamped)),
                cancellationToken);
        }

        activity?.SetTag("mageride.cell", cell);

        MageRideDiagnostics.PositionsProcessed.Add(1);
        MageRideDiagnostics.PositionIngestLatencyMs.Record(
            Math.Max(0, (clock.GetUtcNow() - stamped.SampleTs).TotalMilliseconds));

        return new PositionResult(PositionOutcome.Indexed, stamped, cell);
    }

    /// <summary>
    /// Replaces the payload's self-asserted vehicle with the one EMQX authenticated.
    /// </summary>
    private PositionSample Rebind(PositionSample sample, Guid vehicleIdFromTopic)
    {
        if (vehicleIdFromTopic == Guid.Empty || sample.VehicleId == vehicleIdFromTopic)
        {
            return sample;
        }

        logger.LogWarning(
            "Sample published on vehicle {TopicVehicleId}'s topic claims to be from {PayloadVehicleId}; " +
            "using the authenticated topic. C040 owns the anti-spoof follow-up.",
            vehicleIdFromTopic, sample.VehicleId);

        return sample with { VehicleId = vehicleIdFromTopic };
    }

    private static void Drop(PositionOutcome outcome, Guid vehicleId)
    {
        MageRideDiagnostics.PositionsDropped.Add(
            1,
            new KeyValuePair<string, object?>("reason", outcome.ToString().ToLowerInvariant()),
            new KeyValuePair<string, object?>("vehicle_id", vehicleId));
    }
}
