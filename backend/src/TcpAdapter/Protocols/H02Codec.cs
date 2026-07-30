using System.Globalization;
using System.Text;

namespace MageRide.TcpAdapter.Protocols;

/// <summary>
/// H02 / H02X — the ASCII protocol the older bus trackers speak (D6' §4.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Frame.</b> One message per <c>*…#</c> pair, fields delimited, e.g.
/// <c>*HQ,356938035643809,V1,041530,A,0656.0640,N,07950.5680,E,016.2,090,300726,FFFFFBFF#</c> —
/// vendor tag, device id, message type, <c>hhmmss</c>, validity, <c>ddmm.mmmm</c> latitude and
/// hemisphere, <c>dddmm.mmmm</c> longitude and hemisphere, speed in knots, course, <c>ddmmyy</c>, and
/// a status word as eight hex digits.
/// </para>
/// <para>
/// <b>The spec says "pipe-delimited" and the wire says comma.</b> D6' §4.1 and ADD §7.7.1 both call
/// H02 "ASCII pipe-delimited"; every field reference for the family, and every device in it, uses
/// commas. Both characters are accepted here — it costs one entry in a separator set — and the
/// mismatch is raised as a finding in the C043 handoff rather than resolved by picking one and
/// refusing the other, because a bus that stops reporting is a worse outcome than a redundant
/// separator.
/// </para>
/// <para>
/// <b>Speed is in knots.</b> Not km/h — the family's field reference gives the unit as knots and
/// that is what field-tested decoders read it as. Reading it as km/h understates a highway coach by
/// a factor of 1.85, which is inside every plausibility threshold ADD §12.6 sets and would therefore
/// never be caught downstream.
/// </para>
/// <para>
/// <b>The binary H02 variant is not decoded.</b> Some firmware sends the same fields BCD-packed
/// behind a <c>0x24</c> marker. D6' §4.1 types this family as ASCII, so a frame that does not start
/// with <c>*</c> is resynchronised past rather than guessed at.
/// </para>
/// </remarks>
public sealed class H02Codec(TimeProvider clock) : IProtocolCodec
{
    /// <summary>The position message type.</summary>
    public const string TypePosition = "V1";

    /// <summary>Heartbeat, battery only.</summary>
    public const string TypeHeartbeat = "HTBT";

    /// <summary>Link keep-alive, carrying signal and satellite counts.</summary>
    public const string TypeLink = "LINK";

    private const char Start = '*';
    private const char End = '#';

    private static readonly char[] Separators = [',', '|'];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ProtocolFamily Family => ProtocolFamily.H02;

    public bool TryDecode(ReadOnlySpan<byte> buffer, out TrackerFrame? frame, out int consumed)
    {
        frame = null;
        consumed = 0;

        var text = Encoding.ASCII.GetString(buffer);
        var start = text.IndexOf(Start, StringComparison.Ordinal);

        if (start < 0)
        {
            consumed = buffer.Length;
            return buffer.Length > 0;
        }

        if (start > 0)
        {
            consumed = start;
            return true;
        }

        var end = text.IndexOf(End, StringComparison.Ordinal);

        if (end < 0)
        {
            // Some firmware terminates with a newline instead of '#'. Accept that as a frame end so
            // a device with a stricter-than-documented line ending is not simply never parsed.
            end = text.IndexOfAny(['\r', '\n']);

            if (end < 0)
            {
                return false;
            }
        }

        consumed = end + 1;
        frame = Interpret(text[1..end]);
        return true;
    }

    public byte[]? TryBuildCommand(
        string command, IReadOnlyDictionary<string, string> arguments, string identity, ushort serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);

        // One command, and that is deliberate. H02's command set is the vendor's, published per
        // device family rather than with the protocol, and the only spelling with a consistent
        // meaning across the units in this population is S71's reporting interval. Every other
        // command answers null — reported as unsupported by DownlinkRouter — because an ASCII
        // command a device does not recognise is silently discarded by it, which is
        // indistinguishable from a command that arrived and did nothing.
        if (command != TrackerCommands.SetPosRate
            || !arguments.TryGetValue("seconds", out var text)
            || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 1 or > 9_999)
        {
            return null;
        }

        var stamp = _clock.GetUtcNow().ToString("HHmmss", CultureInfo.InvariantCulture);

        return Encoding.ASCII.GetBytes(
            $"*HQ,{identity},S71,{stamp},22,{seconds.ToString("0000", CultureInfo.InvariantCulture)}#");
    }

    private TrackerFrame Interpret(string message)
    {
        var fields = message.Split(Separators, StringSplitOptions.TrimEntries);

        // vendor tag, id, type — anything shorter is not a message.
        if (fields.Length < 3)
        {
            return TrackerFrame.Ignored;
        }

        var identity = Wire.NormaliseIdentity(fields[1]);
        var type = fields[2];

        if (type is TypeHeartbeat or TypeLink)
        {
            return new TrackerFrame(FrameKind.Heartbeat, identity, Detail: $"h02 {type}");
        }

        if (type != TypePosition || fields.Length < 13)
        {
            // V4 (a command acknowledgement), NBR (neighbour cells), XT and the vendor extensions.
            // Read for their identity and otherwise ignored.
            return new TrackerFrame(FrameKind.Ignored, identity, Detail: $"h02 {type}");
        }

        var fix = ReadFix(fields);

        return new TrackerFrame(
            fix is null ? FrameKind.Ignored : FrameKind.Position,
            identity,
            Fixes: fix is null ? null : [fix],
            Ignition: ReadIgnition(fields),
            Detail: "h02 V1");
    }

    /// <summary>
    /// The ACC line out of the status word.
    /// </summary>
    /// <remarks>
    /// Bit 10, <b>inverted</b>: the word is a set of active-low fault and input flags, so a clear bit
    /// means the input is asserted. That inversion is not in any document this repository has — it is
    /// what the field-tested decoders for the family do, and it is the reading that makes
    /// <c>FFFFFBFF</c> (the value a unit with the engine running sends) mean ignition-on. Recorded in
    /// the C043 handoff.
    /// </remarks>
    private static bool? ReadIgnition(string[] fields)
    {
        if (fields.Length < 13
            || !uint.TryParse(fields[12], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var status))
        {
            return null;
        }

        return (status & (1u << 10)) == 0;
    }

    private TrackerFix? ReadFix(string[] fields)
    {
        var time = fields[3];
        var valid = fields[4] is "A" or "a";
        var date = fields[11];

        var capturedAt = ReadStamp(date, time);

        if (capturedAt is null)
        {
            return null;
        }

        var latitude = Wire.ReadDegreesMinutes(fields[5], fields[6].Length > 0 ? fields[6][0] : 'N');
        var longitude = Wire.ReadDegreesMinutes(fields[7], fields[8].Length > 0 ? fields[8][0] : 'E');

        if (latitude is null || longitude is null)
        {
            return null;
        }

        double? speed = double.TryParse(fields[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var knots)
            ? knots * Wire.MetresPerSecondPerKnot
            : null;

        int? course = double.TryParse(fields[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var heading)
            && heading is >= 0 and < 360
            ? (int)Math.Round(heading)
            : null;

        return new TrackerFix(capturedAt.Value, latitude.Value, longitude.Value, valid, speed, course);
    }

    /// <summary>
    /// <c>ddmmyy</c> + <c>hhmmss</c>, both UTC.
    /// </summary>
    /// <remarks>
    /// The date field comes eight positions after the time and is <b>day first</b> where every other
    /// protocol here is year first. A message with an unreadable date is dropped rather than stamped
    /// from the receive clock — see <see cref="Gt06Codec"/> for why that matters to T-07.
    /// </remarks>
    private DateTimeOffset? ReadStamp(string date, string time)
    {
        if (date.Length < 6 || time.Length < 6)
        {
            return null;
        }

        static bool Two(string text, int at, out int value) =>
            int.TryParse(text.AsSpan(at, 2), NumberStyles.None, CultureInfo.InvariantCulture, out value);

        if (!Two(date, 0, out var day) || !Two(date, 2, out var month) || !Two(date, 4, out var year)
            || !Two(time, 0, out var hour) || !Two(time, 2, out var minute) || !Two(time, 4, out var second))
        {
            return null;
        }

        return Wire.Compose(2000 + year, month, day, hour, minute, second, TimeSpan.Zero);
    }
}
