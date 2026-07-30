using System.Globalization;
using System.Text;

namespace MageRide.TcpAdapter.Protocols;

/// <summary>
/// JT/T 808, the Chinese national tracker standard — both the 2013 and the 2019 header shapes
/// (D6' §4.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Frame.</b> <c>7E | header | body | xor(1) | 7E</c>, with <c>0x7D</c> and <c>0x7E</c> escaped
/// inside as <c>7D 01</c> and <c>7D 02</c> — so the delimiter cannot occur in the payload and framing
/// is a scan for the next <c>7E</c>. The check byte is a plain XOR over the unescaped header and
/// body.
/// </para>
/// <para>
/// <b>Two header shapes, told apart by one bit.</b> Bit 14 of the properties word is the 2019
/// version flag: set, a one-byte protocol version and a <b>ten-byte</b> BCD terminal phone number
/// follow; clear, the 2013 shape's <b>six-byte</b> BCD number follows immediately. Both are in the
/// field and a decoder that assumes one reads the other's body at the wrong offset — which passes the
/// XOR, because the checksum does not know where the header ended.
/// </para>
/// <para>
/// <b>Six BCD bytes cannot hold an IMEI, and that is a provisioning gap rather than a decode bug.</b>
/// The 2013 terminal phone number is twelve digits; an IMEI is fifteen, and
/// <c>provisioning.yaml</c> constrains a binding's IMEI to <c>^\d{15}$</c>. So a 2013-header device
/// presents an identity that cannot have been bound, and this service refuses it at connect with that
/// reason named. The 2019 shape's twenty digits carry a zero-padded IMEI comfortably. Raised as a
/// finding in the C043 handoff — resolving it needs either a 2019-capable firmware or an alias index
/// in provisioning-svc, and inventing a mapping here would authenticate a device against a guess.
/// </para>
/// <para>
/// <b>The timestamp is Beijing time.</b> §8.18 writes it as BCD <c>YY-MM-DD-hh-mm-ss</c> in UTC+8;
/// <c>Adapter:Jt808DeviceUtcOffset</c> is the knob for a unit that was re-flashed for another market.
/// </para>
/// </remarks>
public sealed class Jt808Codec(TimeSpan deviceUtcOffset) : IProtocolCodec
{
    /// <summary>Terminal registration. Answered with <c>0x8100</c>.</summary>
    public const ushort MessageRegister = 0x0100;

    /// <summary>Terminal authentication — the one frame in the four families with a credential field.</summary>
    public const ushort MessageAuthenticate = 0x0102;

    /// <summary>Heartbeat.</summary>
    public const ushort MessageHeartbeat = 0x0002;

    /// <summary>Logout.</summary>
    public const ushort MessageLogout = 0x0003;

    /// <summary>Location report.</summary>
    public const ushort MessageLocation = 0x0200;

    /// <summary>Bulk location upload — the backlog a device sends after a coverage gap (T-05).</summary>
    public const ushort MessageLocationBatch = 0x0704;

    /// <summary>Platform general response.</summary>
    public const ushort MessagePlatformGeneral = 0x8001;

    /// <summary>Platform registration response.</summary>
    public const ushort MessageRegisterReply = 0x8100;

    /// <summary>Set terminal parameters — how the cadence is changed.</summary>
    public const ushort MessageSetParameters = 0x8103;

    /// <summary>Terminal control — command word 4 is a reset.</summary>
    public const ushort MessageTerminalControl = 0x8105;

    /// <summary>Position information query — the pingNow equivalent.</summary>
    public const ushort MessagePositionQuery = 0x8201;

    /// <summary>Set circular geofence areas.</summary>
    public const ushort MessageSetCircularArea = 0x8600;

    /// <summary>Parameter id for the default reporting interval, in seconds (§8.4's table).</summary>
    public const uint ParameterReportInterval = 0x0029;

    private const byte Delimiter = 0x7E;
    private const byte Escape = 0x7D;
    private const int LocationBodyLength = 28;

    private readonly TimeSpan _deviceUtcOffset = deviceUtcOffset;

    /// <summary>
    /// The header shape the device used, remembered so replies go back in the same one.
    /// </summary>
    /// <remarks>
    /// A 2019 device sent a <c>0x8001</c> in the 2013 shape reads the result byte out of the middle of
    /// the header. The codec is therefore per session — which it has to be anyway, because
    /// <see cref="_phone"/> is what a downlink frame is addressed to.
    /// </remarks>
    private bool _version2019;

    private string? _phone;

    public ProtocolFamily Family => ProtocolFamily.Jt808;

    public bool TryDecode(ReadOnlySpan<byte> buffer, out TrackerFrame? frame, out int consumed)
    {
        frame = null;
        consumed = 0;

        var start = buffer.IndexOf(Delimiter);

        if (start < 0)
        {
            // Nothing framed yet. Everything seen so far is between frames, so it goes.
            consumed = buffer.Length;
            return buffer.Length > 0;
        }

        if (start > 0)
        {
            consumed = start;
            return true;
        }

        var end = buffer[1..].IndexOf(Delimiter);

        if (end < 0)
        {
            return false;
        }

        if (end == 0)
        {
            // `7E 7E` — a device that terminates and starts with the same byte, or a keep-alive
            // delimiter. Drop the first and look again.
            consumed = 1;
            return true;
        }

        consumed = end + 2;

        var escaped = buffer[1..(end + 1)];
        var payload = Unescape(escaped);

        if (payload is null || payload.Length < 2)
        {
            return true;
        }

        var check = Wire.Xor8(payload.AsSpan(..^1));

        if (check != payload[^1])
        {
            return true;
        }

        frame = Interpret(payload.AsSpan(..^1));
        return true;
    }

    public byte[]? TryBuildCommand(
        string command, IReadOnlyDictionary<string, string> arguments, string identity, ushort serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);

        var phone = _phone ?? identity;

        switch (command)
        {
            case TrackerCommands.SetPosRate:
            {
                if (!arguments.TryGetValue("seconds", out var text)
                    || !uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                    || seconds is 0 or > 86_400)
                {
                    return null;
                }

                // One parameter: count, id, length, DWORD value.
                var body = new byte[1 + 4 + 1 + 4];
                body[0] = 1;
                Wire.WriteUInt32(body, 1, ParameterReportInterval);
                body[5] = 4;
                Wire.WriteUInt32(body, 6, seconds);

                return BuildFrame(MessageSetParameters, body, phone, serial);
            }

            case TrackerCommands.PingNow:
                return BuildFrame(MessagePositionQuery, [], phone, serial);

            case TrackerCommands.Reboot:
                // §8.7's terminal control: command word 4, terminal reset. The parameter list a
                // few of the other words take is empty for this one.
                return BuildFrame(MessageTerminalControl, [4], phone, serial);

            case TrackerCommands.SetGeofence:
            {
                if (!TryReadDouble(arguments, "lat", out var lat)
                    || !TryReadDouble(arguments, "lng", out var lng)
                    || !TryReadDouble(arguments, "radiusM", out var radius)
                    || radius is <= 0 or > 1_000_000)
                {
                    return null;
                }

                // Setting attribute 0 = replace the device's whole area list, one area, no time
                // window and no speed limit — attribute word 0, so no optional trailer.
                var body = new byte[1 + 1 + 4 + 2 + 4 + 4 + 4];
                body[0] = 0;
                body[1] = 1;
                Wire.WriteUInt32(body, 2, 1);
                Wire.WriteUInt16(body, 6, 0);
                Wire.WriteUInt32(body, 8, (uint)Math.Abs(Math.Round(lat * 1_000_000)));
                Wire.WriteUInt32(body, 12, (uint)Math.Abs(Math.Round(lng * 1_000_000)));
                Wire.WriteUInt32(body, 16, (uint)Math.Round(radius));

                return BuildFrame(MessageSetCircularArea, body, phone, serial);
            }

            default:
                return null;
        }
    }

    /// <summary>Frames, escapes and checksums a platform message.</summary>
    internal byte[] BuildFrame(ushort messageId, ReadOnlySpan<byte> body, string phone, ushort serial)
    {
        // The device's own header shape, except when its identity cannot be expressed in it: the 2013
        // terminal-number field is twelve digits and an IMEI is fifteen, so addressing one in that shape
        // would truncate it into a frame meant for a different device.
        var version2019 = _version2019 || phone.Length > 12;
        var phoneLength = version2019 ? 10 : 6;
        var properties = (ushort)(body.Length & 0x03FF);

        if (version2019)
        {
            properties |= 1 << 14;
        }

        var header = new byte[4 + (version2019 ? 1 : 0) + phoneLength + 2];
        Wire.WriteUInt16(header, 0, messageId);
        Wire.WriteUInt16(header, 2, properties);

        var offset = 4;

        if (version2019)
        {
            header[offset++] = 1;
        }

        Wire.WriteBcd(phone, phoneLength).CopyTo(header.AsSpan(offset));
        offset += phoneLength;
        Wire.WriteUInt16(header, offset, serial);

        var payload = new byte[header.Length + body.Length + 1];
        header.CopyTo(payload.AsSpan());
        body.CopyTo(payload.AsSpan(header.Length));
        payload[^1] = Wire.Xor8(payload.AsSpan(..^1));

        var escaped = Escape2(payload);
        var frame = new byte[escaped.Length + 2];

        frame[0] = Delimiter;
        escaped.CopyTo(frame.AsSpan(1));
        frame[^1] = Delimiter;

        return frame;
    }

    private static byte[]? Unescape(ReadOnlySpan<byte> escaped)
    {
        var output = new byte[escaped.Length];
        var length = 0;

        for (var index = 0; index < escaped.Length; index++)
        {
            if (escaped[index] != Escape)
            {
                output[length++] = escaped[index];
                continue;
            }

            if (index + 1 >= escaped.Length)
            {
                // A trailing escape byte with nothing to escape. The frame is truncated, not ours.
                return null;
            }

            // Only two sequences are defined. Any other is refused rather than guessed at: every
            // reading of it shifts the bytes that follow, and the XOR check cannot tell which.
            switch (escaped[++index])
            {
                case 0x01: output[length++] = Escape; break;
                case 0x02: output[length++] = Delimiter; break;
                default: return null;
            }
        }

        return output[..length];
    }

    private static byte[] Escape2(ReadOnlySpan<byte> payload)
    {
        var output = new List<byte>(payload.Length + 8);

        foreach (var value in payload)
        {
            switch (value)
            {
                case Delimiter:
                    output.Add(Escape);
                    output.Add(0x02);
                    break;
                case Escape:
                    output.Add(Escape);
                    output.Add(0x01);
                    break;
                default:
                    output.Add(value);
                    break;
            }
        }

        return [.. output];
    }

    private static bool TryReadDouble(IReadOnlyDictionary<string, string> arguments, string key, out double value)
    {
        value = 0;

        return arguments.TryGetValue(key, out var text)
               && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private TrackerFrame Interpret(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12)
        {
            return TrackerFrame.Ignored;
        }

        var messageId = Wire.ReadUInt16(payload, 0);
        var properties = Wire.ReadUInt16(payload, 2);

        _version2019 = (properties & (1 << 14)) != 0;

        var offset = 4 + (_version2019 ? 1 : 0);
        var phoneLength = _version2019 ? 10 : 6;

        if (payload.Length < offset + phoneLength + 2)
        {
            return TrackerFrame.Ignored;
        }

        var phone = Wire.ReadBcd(payload.Slice(offset, phoneLength), trimLeadingZeros: true);
        offset += phoneLength;

        var serial = Wire.ReadUInt16(payload, offset);
        offset += 2;

        if ((properties & (1 << 13)) != 0)
        {
            // Fragmented: package total and index follow the serial. The fragments are not
            // reassembled — the only message big enough to need it is a media upload, which this
            // service does not consume, and a half-body would decode to a plausible-looking fix.
            offset += 4;

            return new TrackerFrame(
                FrameKind.Ignored, phone, Detail: $"jt808 0x{messageId:X4} fragment (not reassembled)");
        }

        var declared = properties & 0x03FF;
        var available = payload.Length - offset;
        var body = payload[offset..(offset + Math.Min(declared, Math.Max(0, available)))];

        if (phone is not null)
        {
            _phone = phone;
        }

        var general = BuildFrame(
            MessagePlatformGeneral,
            [(byte)(serial >> 8), (byte)(serial & 0xFF), (byte)(messageId >> 8), (byte)(messageId & 0xFF), 0],
            phone ?? "0",
            serial);

        switch (messageId)
        {
            case MessageRegister:
            {
                // §8.6: reply serial, result, then the authentication code on success. The code is
                // the device's own identifier echoed back — this service mints nothing, because the
                // credential that matters is provisioning-svc's and is presented on 0x0102.
                var authCode = Encoding.ASCII.GetBytes(phone ?? "0");
                var reply = new byte[3 + authCode.Length];
                Wire.WriteUInt16(reply, 0, serial);
                reply[2] = 0;
                authCode.CopyTo(reply.AsSpan(3));

                return new TrackerFrame(
                    FrameKind.Login,
                    phone,
                    Reply: BuildFrame(MessageRegisterReply, reply, phone ?? "0", serial),
                    Detail: "jt808 register");
            }

            case MessageAuthenticate:
                return new TrackerFrame(
                    FrameKind.Login, phone, ReadAuthCode(body), Reply: general, Detail: "jt808 authenticate");

            case MessageHeartbeat:
                return new TrackerFrame(FrameKind.Heartbeat, phone, Reply: general, Detail: "jt808 heartbeat");

            case MessageLogout:
                return new TrackerFrame(FrameKind.Ignored, phone, Reply: general, Detail: "jt808 logout");

            case MessageLocation:
            {
                var fix = ReadLocation(body, buffered: false);

                return new TrackerFrame(
                    fix is null ? FrameKind.Ignored : FrameKind.Position,
                    phone,
                    Fixes: fix is null ? null : [fix],
                    Reply: general,
                    Ignition: ReadIgnition(body),
                    Detail: "jt808 location");
            }

            case MessageLocationBatch:
                return new TrackerFrame(
                    FrameKind.Position,
                    phone,
                    Fixes: ReadBatch(body),
                    Reply: general,
                    Detail: "jt808 batch location");

            default:
                // Every message id the standard defines and this service does not consume — media,
                // parameter queries, driver-identity cards. Answered so the device stops retrying.
                return new TrackerFrame(FrameKind.Ignored, phone, Reply: general, Detail: $"jt808 0x{messageId:X4}");
        }
    }

    /// <summary>
    /// The credential out of a <c>0x0102</c> body.
    /// </summary>
    /// <remarks>
    /// 2013's body is the authentication code alone. 2019 prefixes it with a length byte and appends
    /// the IMEI and a firmware version, so a length-prefixed read is tried first and a bare string is
    /// the fallback. Either way what comes out is a candidate credential and nothing more —
    /// <see cref="Identity.PskCredentials"/> decides whether it is one this platform minted.
    /// </remarks>
    private static string? ReadAuthCode(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        var prefixed = body[0];

        if (prefixed > 0 && prefixed <= body.Length - 1)
        {
            var candidate = Encoding.ASCII.GetString(body.Slice(1, prefixed));

            if (candidate.All(char.IsAsciiLetterOrDigit) || candidate.Contains('.', StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        var text = Encoding.ASCII.GetString(body).TrimEnd('\0');

        return text.Length == 0 ? null : text;
    }

    private static bool? ReadIgnition(ReadOnlySpan<byte> body) =>
        body.Length < LocationBodyLength ? null : (Wire.ReadUInt32(body, 4) & 0x01) != 0;

    private IReadOnlyList<TrackerFix> ReadBatch(ReadOnlySpan<byte> body)
    {
        // §8.36: count(2), type(1 — 0 normal batch, 1 blind-area supplement), then length-prefixed
        // location bodies. Both types are backlog: a device only ever batches what it could not send
        // at the time, which is precisely T-05's case, so every fix in here is routed to
        // `pos/replay` whatever its age.
        if (body.Length < 3)
        {
            return [];
        }

        var count = Wire.ReadUInt16(body, 0);
        var fixes = new List<TrackerFix>(Math.Min((int)count, 512));
        var offset = 3;

        while (offset + 2 <= body.Length && fixes.Count < count)
        {
            var length = Wire.ReadUInt16(body, offset);
            offset += 2;

            if (length == 0 || offset + length > body.Length)
            {
                break;
            }

            var fix = ReadLocation(body.Slice(offset, length), buffered: true);

            if (fix is not null)
            {
                fixes.Add(fix);
            }

            offset += length;
        }

        return fixes;
    }

    private TrackerFix? ReadLocation(ReadOnlySpan<byte> body, bool buffered)
    {
        if (body.Length < LocationBodyLength)
        {
            return null;
        }

        var status = Wire.ReadUInt32(body, 4);
        var latitude = Wire.ReadUInt32(body, 8) / 1_000_000.0;
        var longitude = Wire.ReadUInt32(body, 12) / 1_000_000.0;
        var speedDeciKph = Wire.ReadUInt16(body, 18);
        var direction = Wire.ReadUInt16(body, 20);
        var capturedAt = Wire.ReadBcdTimestamp(body.Slice(22, 6), _deviceUtcOffset);

        if (capturedAt is null)
        {
            return null;
        }

        // bit 0 ACC, bit 1 positioned, bit 2 south latitude, bit 3 west longitude.
        var positioned = (status & 0x02) != 0;
        var south = (status & 0x04) != 0;
        var west = (status & 0x08) != 0;

        int? satellites = null;

        // Additional items: id(1), length(1), value. 0x31 is the GNSS satellite count, which T-07's
        // minimum-satellite check reads when a device reports one.
        for (var offset = LocationBodyLength; offset + 2 <= body.Length;)
        {
            var id = body[offset];
            var length = body[offset + 1];
            offset += 2;

            if (offset + length > body.Length)
            {
                break;
            }

            if (id == 0x31 && length == 1)
            {
                satellites = body[offset];
            }

            offset += length;
        }

        return new TrackerFix(
            capturedAt.Value,
            south ? -latitude : latitude,
            west ? -longitude : longitude,
            positioned,
            SpeedMps: speedDeciKph / 10.0 * Wire.MetresPerSecondPerKph,
            HeadingDeg: direction <= 359 ? direction : null,
            SatCount: satellites,
            Buffered: buffered);
    }
}
