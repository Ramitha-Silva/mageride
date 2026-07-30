using System.Diagnostics;
using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.HotPath.PositionProcessor.Plausibility;
using MageRide.HotPath.PositionProcessor.Redis;
using MageRide.HotPath.PositionProcessor.Throttling;
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

    /// <summary>Over D-17's second-line ceiling. Dropped and flagged onto <c>audit.events</c>.</summary>
    RateLimited,

    /// <summary>Refused by the D-18/T-07 anti-spoof filter — a teleport, a wide fix, a bad clock.</summary>
    Implausible,

    /// <summary>A <c>seq</c> at or below the vehicle's watermark — a replay (R-17, T-05).</summary>
    Replayed,
}

/// <summary>The result of processing one payload, the cell it landed in, and why it was refused.</summary>
/// <param name="Outcome">What happened to it.</param>
/// <param name="Sample">The decoded sample, when it decoded.</param>
/// <param name="Cell">The res-7 cell it was written to, when it was indexed.</param>
/// <param name="Check">
/// Which plausibility gate refused it, when <paramref name="Outcome"/> is
/// <see cref="PositionOutcome.Implausible"/>.
/// </param>
/// <param name="Pool">What the sample did to the R-08 candidate pool.</param>
public sealed record PositionResult(
    PositionOutcome Outcome,
    PositionSample? Sample = null,
    string? Cell = null,
    PlausibilityCheck Check = PlausibilityCheck.None,
    PoolChange Pool = PoolChange.NoDriver);

/// <summary>Normalises one device payload into the live indexes.</summary>
public interface IPositionProcessor
{
    /// <summary>
    /// Processes a <c>telemetry.raw</c> payload.
    /// </summary>
    /// <param name="payload">The device's bytes, exactly as they came off EMQX.</param>
    /// <param name="vehicleIdFromTopic">The vehicle the <b>topic</b> named. EMQX authenticated this;
    /// the payload's own <c>vehicleId</c> did not go through any such check.</param>
    /// <param name="stream">
    /// The bridge's <c>stream</c> header — <c>live</c> or <c>replay</c>. A backlog skips the checks
    /// that compare a sample against the vehicle's current state, and never touches presence.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PositionResult> ProcessAsync(
        ReadOnlyMemory<byte> payload,
        Guid vehicleIdFromTopic,
        string stream,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPositionProcessor"/>
/// <remarks>
/// <para>
/// Seven steps, in this order and for a reason: decode, check the sample is a position at all,
/// apply D-17's second-line ceiling, read what the vehicle's last accepted sample left behind,
/// judge the new one against it (D-18/T-07), write the live indexes on the <c>seq</c> watermark
/// (R-17/T-05), reconcile the R-08 candidate pool, republish onto <c>telemetry.normalized</c>.
/// </para>
/// <para>
/// <b>The rate check comes before the anti-spoof filter and after the parse.</b> It counts what a
/// vehicle actually published, so it has to see samples the filter would reject — a spoofer
/// flooding the platform is exactly the case D-17 exists for. It comes after the parse because a
/// payload that is not a position is not this vehicle publishing positions too fast.
/// </para>
/// <para>
/// <b>The anti-spoof filter comes before the watermark.</b> A refused sample must not advance
/// <c>veh:seq</c> — the watermark is "the newest sample we accepted", and letting a rejected one
/// move it would make every genuine sample behind it look like a replay. It also must not become the
/// <c>veh:meta</c> position the <i>next</i> sample is measured against, or one spoofed jump would
/// drag the plausible envelope along with it.
/// </para>
/// <para>
/// <b>The topic's vehicle wins over the payload's.</b> EMQX bound the topic to the device's
/// credential (<c>acl.conf</c> + <c>emqx.conf</c>'s <c>verify_claims</c>); the payload is whatever
/// the device chose to write. A handset that publishes its neighbour's <c>vehicleId</c> inside an
/// otherwise valid sample is publishing on its own authenticated topic, so the sample is rebound to
/// the topic's vehicle and the disagreement is logged. Trusting the payload here would undo the
/// whole point of the ACL.
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
    IDriverAvailabilityIndex availability,
    IPlausibilityFilter plausibility,
    IIngestRateGuard rateGuard,
    IEventPublisher publisher,
    IOptions<PositionProcessorOptions> options,
    TimeProvider clock,
    ILogger<PositionProcessor> logger) : IPositionProcessor
{
    private readonly PositionProcessorOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<PositionResult> ProcessAsync(
        ReadOnlyMemory<byte> payload,
        Guid vehicleIdFromTopic,
        string stream,
        CancellationToken cancellationToken)
    {
        using var activity = MageRideDiagnostics.ActivitySource.StartActivity(
            "position-processor.process", ActivityKind.Consumer);
        activity?.SetTag("mageride.vehicle_id", vehicleIdFromTopic);
        activity?.SetTag("mageride.stream", stream);

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
        var now = clock.GetUtcNow();
        var stamped = sample.ReceivedTs is null ? sample with { ReceivedTs = now } : sample;

        // D-17, second line. Counted (and dropped) before anything reads or writes Redis state for
        // this vehicle, so a flood costs one INCR rather than a full pipeline pass.
        if (_options.RateCheckEnabled
            && !await rateGuard.AdmitAsync(stamped.VehicleId, now, cancellationToken))
        {
            return new PositionResult(PositionOutcome.RateLimited, stamped);
        }

        var isReplay = TelemetryHeaders.IsReplay(stream);
        var previous = await index.ReadLastAcceptedAsync(stamped.VehicleId, cancellationToken);

        if (_options.PlausibilityEnabled)
        {
            var verdict = plausibility.Judge(stamped, previous, isReplay);

            if (!verdict.IsPlausible)
            {
                return Refuse(stamped, verdict);
            }
        }

        // R-08, before the write, so the membership the meta hash remembers is the one this sample
        // actually created. Live only: a backlog carries a stale capture time, and refreshing
        // presence from it would advertise a driver as available where they were an hour ago —
        // exactly what D5' §3.2's freshness gate exists to refuse.
        var pool = _options.AvailabilityIndexEnabled && !isReplay
            ? await availability.ReconcileAsync(stamped, previous?.Pool, cancellationToken)
            : new PoolReconciliation(PoolChange.NoDriver, previous?.Pool);

        var cell = await index.RecordAsync(stamped, pool.Membership, cancellationToken);

        if (cell is null)
        {
            Drop(PositionOutcome.Replayed, stamped.VehicleId);
            return new PositionResult(PositionOutcome.Replayed, stamped, Pool: pool.Change);
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

        return new PositionResult(PositionOutcome.Indexed, stamped, cell, Pool: pool.Change);
    }

    /// <summary>Counts and logs a sample the D-18/T-07 filter refused.</summary>
    /// <remarks>
    /// Two counters on purpose. <c>positions.dropped{reason=implausible}</c> keeps every refusal on
    /// one series beside the other four so "what fraction of ingest is being dropped" stays a single
    /// query; <c>positions.implausible{check,vehicle_type}</c> is what says <i>which</i> gate and on
    /// which tier, which is the number an operator retuning ADD §12.6's table needs.
    /// </remarks>
    private PositionResult Refuse(PositionSample sample, PlausibilityVerdict verdict)
    {
        var check = verdict.Check.ToString().ToLowerInvariant();

        Drop(PositionOutcome.Implausible, sample.VehicleId);

        MageRideDiagnostics.PositionsImplausible.Add(
            1,
            new KeyValuePair<string, object?>("check", check),
            new KeyValuePair<string, object?>("vehicle_type", sample.VehicleType ?? "unknown"));

        // Warning, not debug: D5' §13.1 counts a refused sample toward a per-device fraud score, and
        // until something owns that score (see the C039 handoff) the log line is the whole trail.
        logger.LogWarning(
            "Refused a {Check} sample from vehicle {VehicleId} (seq {Seq}, source {Source}): {Detail}",
            check, sample.VehicleId, sample.Seq, sample.Source, verdict.Detail);

        return new PositionResult(PositionOutcome.Implausible, sample, Check: verdict.Check);
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
            "using the authenticated topic.",
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
