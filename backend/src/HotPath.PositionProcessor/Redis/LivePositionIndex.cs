using System.Globalization;
using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.Shared.Caching;
using MageRide.Shared.Telemetry;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.HotPath.PositionProcessor.Redis;

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
/// The live geospatial state a position sample produces (ADD §8, §9.4).
/// </summary>
public interface ILivePositionIndex
{
    /// <summary>
    /// Records <paramref name="sample"/> if it is newer than the vehicle's watermark.
    /// </summary>
    /// <returns>
    /// The cell it was written to, or <see langword="null"/> when the sample was a replay of
    /// something already seen (R-17, T-05).
    /// </returns>
    Task<string?> RecordAsync(PositionSample sample, CancellationToken cancellationToken);
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
/// <b>What this deliberately does not write.</b> R-08 gives position-processor-svc the
/// <c>driver:availability:{driverId}</c> heartbeat that keeps a driver in the dispatch candidate
/// pool. It is C039's, and this slice leaves it alone rather than half-refreshing it: dispatch-svc
/// already reads the durable <c>dispatch.driver_presence</c> row for its freshness gate precisely
/// because nothing refreshes that hash yet (C023 decision 10). A sample also carries no driverId,
/// so writing it would mean a registry lookup this component has no business doing.
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

    public async Task<string?> RecordAsync(PositionSample sample, CancellationToken cancellationToken)
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

        await WriteMetaAsync(db, sample, cell);
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

    private async Task WriteMetaAsync(IDatabase db, PositionSample sample, string cell)
    {
        var key = RedisKeys.VehicleMeta(sample.VehicleId);

        // The type and mode a marker is drawn from, denormalised onto the sample by the publisher so
        // no consumer needs a registry lookup on the hot path (mqtt-topics.md §2.1). `cell` is here
        // too, so a future VehicleRemoved (US-7.16/7.17, C041) knows which group to remove from
        // without recomputing a cell from a position that has since changed.
        var fields = new List<HashEntry>
        {
            new("cell", cell),
            new("lat", sample.Lat),
            new("lng", sample.Lng),
            new("seq", sample.Seq),
            new("sampleTs", sample.SampleTs.ToString("O", CultureInfo.InvariantCulture)),
        };

        if (sample.VehicleType is { Length: > 0 } type)
        {
            fields.Add(new HashEntry("type", type));
        }

        if (sample.Mode is { Length: > 0 } mode)
        {
            fields.Add(new HashEntry("mode", mode));
        }

        if (sample.HeadingDeg is { } heading)
        {
            fields.Add(new HashEntry("heading", heading));
        }

        if (sample.SpeedMps is { } speed)
        {
            fields.Add(new HashEntry("speed", speed));
        }

        await db.HashSetAsync(key, [.. fields]);
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
