using System.Globalization;
using System.Text;

namespace MageRide.TcpAdapter.Protocols;

/// <summary>
/// Generic NMEA 0183 over UDP — the low-cost asset trackers on 5026 (D6' §4.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>NMEA carries no device identity, and this is the whole difficulty of the family.</b> A
/// <c>$GPRMC</c> sentence says where something is and never says what. No spec in this repository
/// gives the framing these devices use, so the accepted forms are stated here and nowhere else, and
/// the gap is raised in the C043 handoff:
/// </para>
/// <list type="bullet">
/// <item><c>IMEI:356938035643809;$GPRMC,…</c> — the <c>IMEI:</c> keyword, any of <c>; , |</c> or
/// whitespace closing it. This is the form the widest range of firmware in the family uses.</item>
/// <item><c>#356938035643809#$GPRMC,…</c> — the same identity between hashes.</item>
/// <item><c>356938035643809,$GPRMC,…</c> — a bare digit string ahead of the first sentence.</item>
/// </list>
/// <para>
/// Everything before the datagram's first <c>$</c> is treated as the identity region and reduced to
/// its digits; a datagram whose prefix holds no digits is refused, because an unidentified position
/// cannot be bound to a vehicle and there is nothing else to do with it.
/// </para>
/// <para>
/// <b>One datagram is one frame.</b> UDP has no stream to resynchronise, so every sentence in the
/// datagram is decoded and the fixes come out together — a device that sends RMC and GGA for the same
/// instant produces one fix carrying both halves, and one that bursts several instants produces
/// several.
/// </para>
/// <para>
/// <b>GGA alone has no date.</b> The sentence carries <c>hhmmss</c> and nothing else, so a datagram
/// with no RMC is stamped with the receive clock's UTC date and the sentence's time — with a
/// day-boundary correction, because a fix that reads twelve hours into the future is one whose date
/// rolled over between capture and receipt. That inference is why RMC is preferred whenever it is
/// present.
/// </para>
/// <para>
/// <b>There is no downlink.</b> Generic NMEA has no command grammar, and UDP gives no session to
/// write one back on. <see cref="TryBuildCommand"/> always answers null and
/// <see cref="Publishing.DownlinkRouter"/> counts the command as unsupported for the family.
/// </para>
/// </remarks>
public sealed class NmeaCodec(TimeProvider clock) : IProtocolCodec
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ProtocolFamily Family => ProtocolFamily.NmeaUdp;

    public bool TryDecode(ReadOnlySpan<byte> buffer, out TrackerFrame? frame, out int consumed)
    {
        frame = null;
        consumed = buffer.Length;

        if (buffer.IsEmpty)
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(buffer);
        var firstSentence = text.IndexOf('$', StringComparison.Ordinal);

        if (firstSentence < 0)
        {
            return true;
        }

        var identity = ReadIdentity(text.AsSpan(0, firstSentence));
        var fixes = ReadFixes(text[firstSentence..]);

        frame = new TrackerFrame(
            fixes.Count > 0 ? FrameKind.Position : FrameKind.Ignored,
            identity,
            Fixes: fixes.Count > 0 ? fixes : null,
            Detail: $"nmea {fixes.Count} fix(es)");

        return true;
    }

    public byte[]? TryBuildCommand(
        string command, IReadOnlyDictionary<string, string> arguments, string identity, ushort serial) => null;

    /// <summary>Digits out of the prefix, with the keywords the three accepted forms use removed.</summary>
    private static string? ReadIdentity(ReadOnlySpan<char> prefix)
    {
        var builder = new StringBuilder(prefix.Length);

        foreach (var character in prefix)
        {
            if (char.IsAsciiDigit(character))
            {
                builder.Append(character);
            }
        }

        if (builder.Length == 0)
        {
            return null;
        }

        var digits = builder.ToString().TrimStart('0');

        return digits.Length == 0 ? null : digits;
    }

    private List<TrackerFix> ReadFixes(string body)
    {
        var sentences = body.Split('$', StringSplitOptions.RemoveEmptyEntries);
        var fixes = new List<TrackerFix>();
        var pending = new List<Sentence>();

        foreach (var raw in sentences)
        {
            var sentence = raw.TrimEnd('\r', '\n', ' ');

            if (!Wire.VerifyNmeaChecksum($"${sentence}"))
            {
                // A corrupt sentence is dropped on its own. The rest of the datagram is still good:
                // UDP delivers or does not deliver a whole datagram, so damage here is a device's
                // buffer rather than the network.
                continue;
            }

            var parsed = Parse(sentence);

            if (parsed is not null)
            {
                pending.Add(parsed.Value);
            }
        }

        // RMC is the anchor because it is the only sentence with a date. A GGA is folded into the RMC
        // that names the same second, which is where the satellite count and the HDOP come from.
        foreach (var rmc in pending.Where(entry => entry.Kind == SentenceKind.Rmc))
        {
            var gga = pending.FirstOrDefault(
                entry => entry.Kind == SentenceKind.Gga && entry.Time == rmc.Time);

            fixes.Add(new TrackerFix(
                rmc.CapturedAt!.Value,
                rmc.Lat!.Value,
                rmc.Lng!.Value,
                rmc.Valid,
                rmc.SpeedMps,
                rmc.HeadingDeg,
                gga.SatCount,
                gga.Hdop));
        }

        if (fixes.Count > 0)
        {
            return fixes;
        }

        // No RMC: a GGA is still a position, and dropping it would lose the family's cheapest
        // devices entirely. The date is inferred — see the class remarks.
        foreach (var gga in pending.Where(entry => entry.Kind == SentenceKind.Gga && entry.Lat is not null))
        {
            var stamp = InferDate(gga.Time);

            if (stamp is null)
            {
                continue;
            }

            fixes.Add(new TrackerFix(
                stamp.Value, gga.Lat!.Value, gga.Lng!.Value, gga.Valid, SatCount: gga.SatCount, Hdop: gga.Hdop));
        }

        return fixes;
    }

    /// <summary>Today's UTC date at <paramref name="time"/>, corrected for a rollover.</summary>
    private DateTimeOffset? InferDate(TimeSpan? time)
    {
        if (time is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        var candidate = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero) + time.Value;

        // More than twelve hours ahead means the capture was yesterday and the receiving side has
        // crossed midnight; more than twelve behind means the opposite. Both are corrected by a day,
        // which is the best a sentence with no date allows.
        if (candidate - now > TimeSpan.FromHours(12))
        {
            candidate -= TimeSpan.FromDays(1);
        }
        else if (now - candidate > TimeSpan.FromHours(12))
        {
            candidate += TimeSpan.FromDays(1);
        }

        return candidate;
    }

    private static Sentence? Parse(string sentence)
    {
        var star = sentence.LastIndexOf('*');
        var fields = (star < 0 ? sentence : sentence[..star]).Split(',');

        if (fields.Length < 2 || fields[0].Length < 5)
        {
            return null;
        }

        // The talker id is the first two characters — GP, GN, GL, BD. Only the sentence type matters.
        var type = fields[0][2..];

        return type switch
        {
            "RMC" => ParseRmc(fields),
            "GGA" => ParseGga(fields),
            _ => null,
        };
    }

    private static Sentence? ParseRmc(string[] fields)
    {
        // $xxRMC,hhmmss.ss,A,ddmm.mmmm,N,dddmm.mmmm,E,speedKnots,courseDeg,ddmmyy,…
        if (fields.Length < 10)
        {
            return null;
        }

        var time = ReadTime(fields[1]);
        var lat = Wire.ReadDegreesMinutes(fields[3], First(fields[4], 'N'));
        var lng = Wire.ReadDegreesMinutes(fields[5], First(fields[6], 'E'));

        if (time is null || lat is null || lng is null || fields[9].Length < 6)
        {
            return null;
        }

        static bool Two(string text, int at, out int value) =>
            int.TryParse(text.AsSpan(at, 2), NumberStyles.None, CultureInfo.InvariantCulture, out value);

        if (!Two(fields[9], 0, out var day) || !Two(fields[9], 2, out var month) || !Two(fields[9], 4, out var year))
        {
            return null;
        }

        var date = Wire.Compose(2000 + year, month, day, 0, 0, 0, TimeSpan.Zero);

        if (date is null)
        {
            return null;
        }

        return new Sentence
        {
            Kind = SentenceKind.Rmc,
            Time = time,
            CapturedAt = date.Value + time.Value,
            Valid = fields[2] is "A" or "a",
            Lat = lat,
            Lng = lng,
            SpeedMps = double.TryParse(fields[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var knots)
                ? knots * Wire.MetresPerSecondPerKnot
                : null,
            HeadingDeg = double.TryParse(fields[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var course)
                && course is >= 0 and < 360
                ? (int)Math.Round(course)
                : null,
        };
    }

    private static Sentence? ParseGga(string[] fields)
    {
        // $xxGGA,hhmmss.ss,ddmm.mmmm,N,dddmm.mmmm,E,quality,satellites,hdop,altitude,M,…
        if (fields.Length < 9)
        {
            return null;
        }

        var time = ReadTime(fields[1]);
        var lat = Wire.ReadDegreesMinutes(fields[2], First(fields[3], 'N'));
        var lng = Wire.ReadDegreesMinutes(fields[4], First(fields[5], 'E'));

        if (time is null)
        {
            return null;
        }

        return new Sentence
        {
            Kind = SentenceKind.Gga,
            Time = time,
            Lat = lat,
            Lng = lng,
            // Quality 0 is "no fix"; 1 GPS, 2 DGPS, 4/5 RTK. Anything non-zero is positioned.
            Valid = int.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out var quality)
                    && quality > 0,
            SatCount = int.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out var sats)
                ? sats
                : null,
            Hdop = double.TryParse(fields[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var hdop)
                ? hdop
                : null,
        };
    }

    private static char First(string field, char fallback) => field.Length > 0 ? field[0] : fallback;

    private static TimeSpan? ReadTime(string field)
    {
        if (field.Length < 6)
        {
            return null;
        }

        static bool Two(string text, int at, out int value) =>
            int.TryParse(text.AsSpan(at, 2), NumberStyles.None, CultureInfo.InvariantCulture, out value);

        if (!Two(field, 0, out var hour) || !Two(field, 2, out var minute) || !Two(field, 4, out var second)
            || hour > 23 || minute > 59 || second > 60)
        {
            return null;
        }

        var fractional = 0.0;

        if (field.Length > 7 && field[6] == '.')
        {
            _ = double.TryParse(field.AsSpan(6), NumberStyles.Float, CultureInfo.InvariantCulture, out fractional);
        }

        return new TimeSpan(0, hour, minute, Math.Min(second, 59)) + TimeSpan.FromSeconds(fractional);
    }

    private enum SentenceKind
    {
        Rmc,
        Gga,
    }

    private readonly record struct Sentence
    {
        public SentenceKind Kind { get; init; }

        public TimeSpan? Time { get; init; }

        public DateTimeOffset? CapturedAt { get; init; }

        public bool Valid { get; init; }

        public double? Lat { get; init; }

        public double? Lng { get; init; }

        public double? SpeedMps { get; init; }

        public int? HeadingDeg { get; init; }

        public int? SatCount { get; init; }

        public double? Hdop { get; init; }
    }
}
