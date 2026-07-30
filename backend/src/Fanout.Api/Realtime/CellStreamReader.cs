using System.Globalization;
using MageRide.Shared.Caching;
using MageRide.Shared.Realtime;
using StackExchange.Redis;

namespace MageRide.Fanout.Realtime;

/// <summary>
/// One vehicle's newest frame in a batch, with the two fields the wire payload does not carry.
/// </summary>
/// <param name="Frame">Exactly what goes on the socket (<c>signalr-hub.md</c> §3).</param>
/// <param name="SampleTs">
/// The sample's GNSS instant. Read but never sent: it is what US-7.17's freshness rule is measured
/// against, and it is what keeps a replayed backlog (<c>veh/{id}/pos/replay</c>, R-17) off the live
/// map — those samples travel the same cell stream as live ones and are indistinguishable without it.
/// </param>
public sealed record CellFrame(VehicleFrame Frame, DateTimeOffset? SampleTs);

/// <summary>A batch drained from one cell's stream, and where the read stopped.</summary>
/// <param name="Cell">The res-7 cell.</param>
/// <param name="Frames">Newest frame per vehicle, oldest vehicle first.</param>
/// <param name="Position">The last stream id read, to resume from.</param>
/// <param name="OldestEntryAt">When the oldest entry in the batch was written, for the SLO
/// histogram. Null when the stream ids carried no usable timestamp.</param>
public sealed record CellBatch(
    string Cell, IReadOnlyList<CellFrame> Frames, RedisValue Position, DateTimeOffset? OldestEntryAt);

/// <summary>
/// Turns <c>cell:{h3index}</c> stream entries into <c>VehiclePositions</c> batches.
/// </summary>
/// <remarks>
/// The entry field names are position-processor-svc's
/// <c>MageRide.HotPath.PositionProcessor.Redis.CellStreamFields</c> and they are deliberately the
/// <see cref="VehicleFrame"/> property names, so this is a projection rather than a translation.
/// The two services cannot reference each other, so the names are the contract — the pump asserts
/// them in <c>HotPath.Tests</c> against a stream a real processor wrote.
/// </remarks>
public interface ICellStreamReader
{
    /// <summary>
    /// Reads everything after <paramref name="position"/>, or seeds from the tail when there is no
    /// position yet.
    /// </summary>
    /// <param name="cell">The res-7 cell.</param>
    /// <param name="position">Where the last read stopped, or null to start.</param>
    /// <param name="count">Ceiling on entries read.</param>
    Task<CellBatch?> ReadAsync(string cell, RedisValue? position, int count, CancellationToken cancellationToken);

    /// <summary>
    /// The tail of a cell's buffer, for a client that has just joined.
    /// </summary>
    /// <returns>The most recent frames and the id to resume from — the latter even when
    /// <paramref name="count"/> is 0, because a reader still has to know where the stream currently
    /// ends.</returns>
    Task<CellBatch?> ReadTailAsync(string cell, int count, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICellStreamReader"/>
public sealed class CellStreamReader(IConnectionMultiplexer redis) : ICellStreamReader
{
    public async Task<CellBatch?> ReadAsync(
        string cell, RedisValue? position, int count, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cell);
        cancellationToken.ThrowIfCancellationRequested();

        if (position is not { } from)
        {
            // A safety net, not the normal path: the hub fixes a cell's resume point before the
            // cell becomes active, precisely so nothing written between a join and the first tick
            // is skipped. Reaching here means a cell went active without one, and resolving the
            // stream's current end is the least-surprising recovery.
            //
            // `$` is never used. A non-blocking XREAD from `$` resolves to the stream's last id and
            // therefore always returns nothing — a pump that appears to run and never delivers.
            return await ReadTailAsync(cell, count: 0, cancellationToken);
        }

        var entries = await redis.GetDatabase().StreamReadAsync(RedisKeys.Cell(cell), from, count);

        return entries.Length == 0 ? null : Build(cell, entries);
    }

    public async Task<CellBatch?> ReadTailAsync(string cell, int count, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cell);
        cancellationToken.ThrowIfCancellationRequested();

        // Descending from the newest, so the tail is bounded whatever the stream holds. At least one
        // entry is always read: the caller needs the resume id even when it wants no frames.
        var entries = await redis.GetDatabase().StreamRangeAsync(
            RedisKeys.Cell(cell), minId: "-", maxId: "+", count: Math.Max(1, count), messageOrder: Order.Descending);

        if (entries.Length == 0)
        {
            // An empty or absent stream. "0-0" is "everything from the beginning", which for a
            // stream with nothing in it is the same as "from now" and needs no extra round trip.
            return new CellBatch(cell, [], "0-0", null);
        }

        // The descending read put the newest first; that is also the resume id.
        var position = entries[0].Id;
        var wanted = count <= 0 ? [] : entries.Take(count).Reverse().ToArray();

        var batch = Build(cell, wanted);
        return batch with { Position = position };
    }

    private static CellBatch Build(string cell, StreamEntry[] entries)
    {
        // Newest frame per vehicle. A vehicle that reported four times inside one batch window is
        // in exactly one place now, and sending its whole history would make the marker jitter
        // backwards on the map.
        var newest = new Dictionary<Guid, CellFrame>();
        var order = new List<Guid>();
        DateTimeOffset? oldest = null;

        foreach (var entry in entries)
        {
            var frame = ToFrame(entry);

            if (frame is null)
            {
                continue;
            }

            if (newest.TryAdd(frame.Frame.VehicleId, frame))
            {
                order.Add(frame.Frame.VehicleId);
            }
            else
            {
                newest[frame.Frame.VehicleId] = frame;
            }

            if (TimestampOf(entry.Id) is { } written && (oldest is null || written < oldest))
            {
                oldest = written;
            }
        }

        return new CellBatch(
            cell,
            [.. order.Select(id => newest[id])],
            entries.Length == 0 ? RedisValue.Null : entries[^1].Id,
            oldest);
    }

    private static CellFrame? ToFrame(StreamEntry entry)
    {
        Guid? vehicleId = null;
        double? lat = null;
        double? lng = null;
        int? heading = null;
        double? speed = null;
        string? type = null;
        string? mode = null;
        DateTimeOffset? sampleTs = null;

        foreach (var field in entry.Values)
        {
            switch (field.Name.ToString())
            {
                case "sampleTs":
                    sampleTs = DateTimeOffset.TryParse(
                        field.Value.ToString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var stamped)
                        ? stamped
                        : null;
                    break;
                case "vehicleId":
                    vehicleId = Guid.TryParse(field.Value.ToString(), out var parsed) ? parsed : null;
                    break;
                case "lat":
                    lat = ReadDouble(field.Value);
                    break;
                case "lng":
                    lng = ReadDouble(field.Value);
                    break;
                case "heading":
                    heading = (int?)ReadDouble(field.Value);
                    break;
                case "speed":
                    speed = ReadDouble(field.Value);
                    break;
                case "type":
                    type = field.Value.ToString();
                    break;
                case "mode":
                    mode = field.Value.ToString();
                    break;
                default:
                    // seq is carried for consumers that need it; a frame does not.
                    break;
            }
        }

        // A partial entry is one nobody can draw. Skipping it costs one vehicle one tick; failing
        // the batch would cost every vehicle in the cell.
        return vehicleId is { } id && lat is { } latitude && lng is { } longitude
            ? new CellFrame(new VehicleFrame(id, latitude, longitude, heading, speed, type, mode), sampleTs)
            : null;
    }

    private static double? ReadDouble(RedisValue value) =>
        double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// The write time encoded in a stream id. Redis mints ids as <c>{unixMillis}-{seq}</c> from its
    /// own clock, which is what makes the fan-out latency histogram measurable without a second
    /// timestamp field.
    /// </summary>
    private static DateTimeOffset? TimestampOf(RedisValue id)
    {
        var text = id.ToString();
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var head = dash < 0 ? text : text[..dash];

        return long.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis)
            ? DateTimeOffset.FromUnixTimeMilliseconds(millis)
            : null;
    }
}
