using System.Globalization;
using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.Shared.Caching;
using MageRide.Shared.Primitives;
using MageRide.Shared.Telemetry;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.HotPath.PositionProcessor.Redis;

/// <summary>The fields <c>veh:meta:{vehicleId}</c> carries (ADD §9.4).</summary>
/// <remarks>
/// Read back by this service on the next sample — the D-18 filter measures a step against
/// <see cref="Lat"/>/<see cref="Lng"/>/<see cref="SampleTs"/> — and by fanout-svc's join seed, so
/// the names are a contract in both directions rather than an implementation detail.
/// </remarks>
public static class MetaFields
{
    public const string Cell = "cell";
    public const string Lat = "lat";
    public const string Lng = "lng";
    public const string Seq = "seq";
    public const string SampleTs = "sampleTs";
    public const string Type = "type";
    public const string Mode = "mode";
    public const string Heading = "heading";
    public const string Speed = "speed";

    /// <summary>
    /// The <c>geo:drivers:available:{type}:{res5cell}</c> key this vehicle's driver was last put in
    /// (R-08), or absent.
    /// </summary>
    /// <remarks>
    /// <b>Not in ADD §9.4's shape</b> — a micro-change-set in the C039 handoff. It exists because a
    /// GEO set has no TTL and <c>driver:availability:{driverId}</c> does: when the availability hash
    /// expires there is nothing left anywhere that says which cell key still holds the driver, so
    /// the membership would leak for ever. This is that memory, and it lives on the vehicle's hash
    /// because the vehicle is what the next sample arrives for.
    /// </remarks>
    public const string PoolCell = "poolCell";
}

/// <summary>The fields one entry on a <c>cell:{h3index}</c> stream carries.</summary>
/// <remarks>
/// These names are the contract between this service and fanout-svc. They are deliberately the
/// <see cref="Shared.Realtime.VehicleFrame"/> field names — <c>signalr-hub.md</c> §3's
/// <c>VehiclePositions</c> payload — so the fan-out step is a projection and not a translation.
/// </remarks>
public static class CellStreamFields
{
    public const string VehicleId = "vehicleId";
    public const string Lat = "lat";
    public const string Lng = "lng";
    public const string Heading = "heading";
    public const string Speed = "speed";
    public const string Type = "type";
    public const string Mode = "mode";

    /// <summary>The sample's GNSS instant, so a consumer can age a frame it reads late.</summary>
    public const string SampleTs = "sampleTs";

    /// <summary>The R-17 sequence, carried so a reader can tell two frames apart.</summary>
    public const string Seq = "seq";
}

/// <summary>
/// The vehicle's last accepted fix, as <c>veh:meta:{vehicleId}</c> remembers it.
/// </summary>
/// <remarks>
/// <para>
/// The D-18 plausibility filter measures a step against this, which is why it is read from the
/// meta hash rather than kept in the process: a vehicle's samples are keyed to one Redpanda
/// partition and therefore to one consumer, but that assignment moves on every rebalance, and an
/// in-process "last position" would reset to nothing every time a replica restarted — switching the
/// teleport gate off for one sample per vehicle, exactly when the platform is least stable.
/// </para>
/// <para>
/// <see cref="PositionProcessorOptions.VehicleMetaTtl"/> is therefore also the filter's horizon: a
/// vehicle silent for longer than that gets its next sample accepted with no step measured. That is
/// the right way round — the alternative is judging a step over an unknown gap.
/// </para>
/// </remarks>
/// <param name="Point">Where the last accepted sample put the vehicle.</param>
/// <param name="SampleTs">That sample's GNSS instant — T-07's monotonic watermark.</param>
/// <param name="Seq">Its R-17 sequence.</param>
/// <param name="Cell">The res-7 fan-out cell it was written to.</param>
/// <param name="Pool">
/// Where this service last put the vehicle's driver in the R-08 candidate index, or
/// <see langword="null"/>. Remembered here because the availability hash that would otherwise name
/// it has a 60 s TTL and the GEO membership it created does not — see
/// <see cref="IDriverAvailabilityIndex"/>.
/// </param>
public sealed record LastAcceptedPosition(
    GeoPoint Point, DateTimeOffset SampleTs, long Seq, string? Cell, PoolMembership? Pool);

/// <summary>
/// The live geospatial state a position sample produces (ADD §8, §9.4).
/// </summary>
public interface ILivePositionIndex
{
    /// <summary>
    /// Reads what the vehicle's last accepted sample left behind, or <see langword="null"/> when
    /// there is nothing — a vehicle's first sample, or one after the meta hash's TTL lapsed.
    /// </summary>
    Task<LastAcceptedPosition?> ReadLastAcceptedAsync(Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="sample"/> if it is newer than the vehicle's watermark.
    /// </summary>
    /// <param name="sample">The accepted sample.</param>
    /// <param name="pool">
    /// The candidate-pool membership to remember for this vehicle's driver, or
    /// <see langword="null"/> to forget it. Written in the same hash write as everything else so the
    /// hot path costs one round trip rather than two.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// The cell it was written to, or <see langword="null"/> when the sample was a replay of
    /// something already seen (R-17, T-05).
    /// </returns>
    Task<string?> RecordAsync(PositionSample sample, PoolMembership? pool, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILivePositionIndex"/>
/// <remarks>
/// <para>
/// Three writes, all through <see cref="RedisKeys"/> so no two services can disagree about where a
/// value lives: <c>geo:live</c> (every active vehicle's last position),
/// <c>veh:meta:{vehicleId}</c> (the denormalised type and mode a map marker needs) and
/// <c>cell:{h3index}</c> (the per-cell stream fanout-svc reads).
/// </para>
/// <para>
/// <b>Redis is the live index, not the record.</b> ADD §9.5 is explicit: Timescale is the system of
/// record for telemetry and Redis serves the hot "where is it now" lookup. Losing the whole
/// keyspace costs the live map until the next sample from each vehicle — seconds — and costs
/// history nothing.
/// </para>
/// <para>
/// <b>The R-08 candidate index is not written here</b> — it is <see cref="DriverAvailabilityIndex"/>
/// (C039). Kept separate because the two have different subjects: everything in this type is keyed
/// by the <i>vehicle</i> EMQX authenticated, and R-08's keys are about the <i>driver</i> on standby
/// with it, which is a fact this service has to look up and may not find.
/// </para>
/// </remarks>
public sealed class LivePositionIndex(
    IConnectionMultiplexer redis,
    IOptions<PositionProcessorOptions> options,
    ILogger<LivePositionIndex> logger) : ILivePositionIndex
{
    /// <summary>
    /// The R-17 watermark, compare-and-set in one round trip.
    /// </summary>
    /// <remarks>
    /// Ordering per vehicle is already guaranteed — <c>telemetry.raw</c> is keyed by
    /// <c>vehicleId</c>, so one partition and therefore one consumer owns a vehicle at a time. The
    /// script is still atomic because that guarantee lapses for a few seconds during a consumer
    /// group rebalance, and a lost-update there would let a replayed sample overwrite a live one.
    ///
    /// <para>
    /// <b>The comparison is <c>&gt;=</c>, and <c>seq</c> has SECOND resolution — so one sample per
    /// vehicle per second is a ceiling, not a guideline.</b> tcp-adapter sets
    /// <c>seq = CapturedAt.ToUnixTimeMilliseconds()</c> (<c>Ingest/TrackerSamples.cs</c>) and all four
    /// protocol families of D6' §4.1 stamp to the whole second, so every seq ends in <c>000</c>. Two
    /// genuinely distinct fixes captured inside one second therefore carry the *same* seq, and this
    /// script cannot tell the second one from the replay it exists to discard: it returns 0 and the
    /// sample is refused. That is correct for a replay and lossy for a burst, and nothing downstream
    /// can recover it — <c>veh:meta</c> is not updated, so the refused fix does not even become the
    /// position the next one is measured against. Note the D-18 plausibility gate runs BEFORE this
    /// (see the pipeline order in this project's <c>CLAUDE.md</c>), so its deliberate handling of a
    /// same-second burst — <c>MinStepInterval</c> is a clamp, not a skip — cannot save the sample:
    /// whatever the gate lets through arrives here and is dropped anyway.
    /// </para>
    /// <para>
    /// <b>This is lower than the rate limits either side of it.</b> AL-12's fastest scheduled cadence is
    /// 1 call/s, which is safe — but it is bounded by ADD §12.4's 5 msg/s/vehicle broker ceiling, and
    /// this service's own D-17 line admits 10 msg/s over 10 s. A vehicle publishing 2–5 msg/s is inside
    /// both and loses every fix but each second's first, counted <c>replayed</c> rather than dropped as
    /// anything an operator would look at.
    /// Raising the ceiling means giving seq resolution the timestamp does not have — a device frame
    /// counter, which <c>TcpAdapter/CLAUDE.md</c> explains was rejected for good reasons (16 bits,
    /// wraps in hours, survives neither a reboot nor a pod move) — so it is a spec question, not an
    /// edit here. Found 2026-08-11 while diagnosing an E2E scenario that was itself publishing two
    /// fixes in one second; the harness was at fault, not this.
    /// </para>
    ///
    /// KEYS[1] = veh:seq:{vehicleId}   ARGV[1] = seq   ARGV[2] = ttl seconds
    /// </remarks>
    private const string AdvanceWatermarkScript =
        """
        local last = redis.call('GET', KEYS[1])
        if last and tonumber(last) >= tonumber(ARGV[1]) then
          return 0
        end
        redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[2])
        return 1
        """;

    private readonly PositionProcessorOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<LastAcceptedPosition?> ReadLastAcceptedAsync(
        Guid vehicleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var values = await redis.GetDatabase().HashGetAsync(
            RedisKeys.VehicleMeta(vehicleId),
            [MetaFields.Lat, MetaFields.Lng, MetaFields.SampleTs, MetaFields.Seq, MetaFields.Cell,
             MetaFields.PoolCell]);

        // Any of the four load-bearing fields missing means the hash is not one this service wrote —
        // an expired key reads as all-null, and a partial one is not something to guess at.
        if (!values[0].TryParse(out double lat)
            || !values[1].TryParse(out double lng)
            || !DateTimeOffset.TryParse(
                values[2].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sampleTs)
            || !values[3].TryParse(out long seq))
        {
            return null;
        }

        return new LastAcceptedPosition(
            new GeoPoint(lat, lng),
            sampleTs,
            seq,
            values[4].IsNullOrEmpty ? null : values[4].ToString(),
            PoolMembership.TryParse(values[5].IsNullOrEmpty ? null : values[5].ToString()));
    }

    public async Task<string?> RecordAsync(
        PositionSample sample, PoolMembership? pool, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        cancellationToken.ThrowIfCancellationRequested();

        var db = redis.GetDatabase();

        if (!await AdvanceWatermarkAsync(db, sample))
        {
            logger.LogDebug(
                "Discarding replayed sample seq {Seq} for vehicle {VehicleId}", sample.Seq, sample.VehicleId);

            return null;
        }

        var cell = Shared.Geo.GeoCells.ViewCell(sample.Point);

        // GEOADD is a move, not an append: the member is the vehicle id, so a second write replaces
        // the first rather than leaving the vehicle discoverable in two places.
        await db.GeoAddAsync(
            RedisKeys.GeoLive,
            new GeoEntry(sample.Lng, sample.Lat, sample.VehicleId.ToString()));

        await WriteMetaAsync(db, sample, cell, pool);
        await AppendToCellAsync(db, cell, sample);

        return cell;
    }

    private async Task<bool> AdvanceWatermarkAsync(IDatabase db, PositionSample sample)
    {
        var result = await db.ScriptEvaluateAsync(
            AdvanceWatermarkScript,
            [RedisKeys.VehicleSeq(sample.VehicleId)],
            [sample.Seq, (long)_options.SeqWatermarkTtl.TotalSeconds]);

        return (long)result == 1;
    }

    private async Task WriteMetaAsync(IDatabase db, PositionSample sample, string cell, PoolMembership? pool)
    {
        var key = RedisKeys.VehicleMeta(sample.VehicleId);

        // The type and mode a marker is drawn from, denormalised onto the sample by the publisher so
        // no consumer needs a registry lookup on the hot path (mqtt-topics.md §2.1). `cell` is here
        // too, so a future VehicleRemoved (US-7.16/7.17, C041) knows which group to remove from
        // without recomputing a cell from a position that has since changed.
        var fields = new List<HashEntry>
        {
            new(MetaFields.Cell, cell),
            new(MetaFields.Lat, sample.Lat),
            new(MetaFields.Lng, sample.Lng),
            new(MetaFields.Seq, sample.Seq),
            new(MetaFields.SampleTs, sample.SampleTs.ToString("O", CultureInfo.InvariantCulture)),
        };

        if (sample.VehicleType is { Length: > 0 } type)
        {
            fields.Add(new HashEntry(MetaFields.Type, type));
        }

        if (sample.Mode is { Length: > 0 } mode)
        {
            fields.Add(new HashEntry(MetaFields.Mode, mode));
        }

        if (sample.HeadingDeg is { } heading)
        {
            fields.Add(new HashEntry(MetaFields.Heading, heading));
        }

        if (sample.SpeedMps is { } speed)
        {
            fields.Add(new HashEntry(MetaFields.Speed, speed));
        }

        if (pool is not null)
        {
            fields.Add(new HashEntry(MetaFields.PoolCell, pool.ToString()));
        }

        await db.HashSetAsync(key, [.. fields]);

        if (pool is null)
        {
            // Deleted rather than left stale: the field's only purpose is to say which GEO key still
            // holds this vehicle's driver, and a value that outlives the membership would make the
            // next removal a GEOREM against the wrong cell — leaving the driver in the pool.
            await db.HashDeleteAsync(key, MetaFields.PoolCell);
        }

        await db.KeyExpireAsync(key, _options.VehicleMetaTtl);
    }

    private async Task AppendToCellAsync(IDatabase db, string cell, PositionSample sample)
    {
        var key = RedisKeys.Cell(cell);

        var entries = new List<NameValueEntry>
        {
            new(CellStreamFields.VehicleId, sample.VehicleId.ToString()),
            new(CellStreamFields.Lat, sample.Lat),
            new(CellStreamFields.Lng, sample.Lng),
            new(CellStreamFields.Seq, sample.Seq),
            new(CellStreamFields.SampleTs, sample.SampleTs.ToString("O", CultureInfo.InvariantCulture)),
        };

        if (sample.HeadingDeg is { } heading)
        {
            entries.Add(new NameValueEntry(CellStreamFields.Heading, heading));
        }

        if (sample.SpeedMps is { } speed)
        {
            entries.Add(new NameValueEntry(CellStreamFields.Speed, speed));
        }

        if (sample.VehicleType is { Length: > 0 } type)
        {
            entries.Add(new NameValueEntry(CellStreamFields.Type, type));
        }

        if (sample.Mode is { Length: > 0 } mode)
        {
            entries.Add(new NameValueEntry(CellStreamFields.Mode, mode));
        }

        await db.StreamAddAsync(
            key,
            [.. entries],
            messageId: null,
            maxLength: _options.CellStreamMaxLength,
            // Approximate: Redis trims on whole radix nodes instead of walking the stream on every
            // write. The exact length of a fan-out buffer is not a property anything depends on.
            useApproximateMaxLength: true);

        // Refreshed on every write, so a cell no vehicle has been in for an hour drops out of the
        // keyspace rather than living forever at MAXLEN.
        await db.KeyExpireAsync(key, _options.CellStreamTtl);
    }
}
