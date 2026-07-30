using System.Globalization;
using System.Text;

namespace MageRide.TcpAdapter.Protocols;

/// <summary>
/// Concox GT06 / GT06N, and the TK103 and ST-901 clones that speak it (D6' §4.1, ADD §11.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Frame.</b> <c>78 78 | len | protocol | content | serial(2) | crc(2) | 0D 0A</c>, with an
/// extended form that starts <c>79 79</c> and carries a two-byte length. <c>len</c> counts the
/// protocol byte, the content, the serial and the CRC — everything except the start bytes, the length
/// field itself and the stop bytes — so a standard frame is <c>len + 5</c> bytes end to end and an
/// extended one is <c>len + 6</c>.
/// </para>
/// <para>
/// <b>The CRC covers the length field.</b> <see cref="Wire.Crc16X25"/> is computed from the length
/// byte through the serial number inclusive, which is what makes the documented login
/// acknowledgement <c>78 78 05 01 00 01 D9 DC 0D 0A</c> verify — that frame is the one fixed point
/// available to check the convention against, and the test suite asserts it.
/// </para>
/// <para>
/// <b>The login packet has no credential field.</b> Protocol <c>0x01</c> carries eight BCD bytes of
/// terminal id and an optional two-byte type code, and that is the whole of it. ADD §7.7.3's
/// "per-device pre-shared bearer + IMEI signature" is not expressible here, which is why
/// <c>Adapter:RequireCredential</c> defaults off — see its declaration.
/// </para>
/// <para>
/// <b>Acknowledgement is not optional.</b> A device whose login goes unanswered re-sends it, and
/// after a few attempts most firmware reboots the modem. The reply is the same protocol number with
/// the same serial and an empty body, except for the time request <c>0x8A</c>, which is answered with
/// the current UTC.
/// </para>
/// </remarks>
public sealed class Gt06Codec(TimeProvider clock) : IProtocolCodec
{
    /// <summary>Login — terminal id, and the only frame that must arrive first.</summary>
    public const byte ProtocolLogin = 0x01;

    /// <summary>Location. <c>0x22</c> is the GT06N spelling of the same leading layout.</summary>
    public const byte ProtocolLocation = 0x12;

    /// <summary>Status / heartbeat, carrying the terminal information byte the ACC line lives in.</summary>
    public const byte ProtocolStatus = 0x13;

    /// <summary>Alarm — a location frame with the status bytes appended.</summary>
    public const byte ProtocolAlarm = 0x16;

    /// <summary>The device asking the server for the time.</summary>
    public const byte ProtocolTime = 0x8A;

    /// <summary>Server to device: an ASCII command inside a length-prefixed envelope.</summary>
    public const byte ProtocolCommand = 0x80;

    private const byte StandardStart = 0x78;
    private const byte ExtendedStart = 0x79;
    private const byte StopHigh = 0x0D;
    private const byte StopLow = 0x0A;

    /// <summary>Bytes of the location layout shared by every position-bearing protocol number.</summary>
    private const int FixLength = 18;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ProtocolFamily Family => ProtocolFamily.Gt06;

    public bool TryDecode(ReadOnlySpan<byte> buffer, out TrackerFrame? frame, out int consumed)
    {
        frame = null;
        consumed = 0;

        // Resynchronise. A device that reconnects mid-frame, or a middlebox that injected a
        // keep-alive, leaves bytes in front of the next start marker; dropping them one at a time is
        // what stops the stream deadlocking on garbage it will never be able to parse.
        var start = IndexOfStart(buffer);

        if (start < 0)
        {
            // Keep at most one byte: the second half of a start marker may be in the next read.
            consumed = Math.Max(0, buffer.Length - 1);
            return false;
        }

        if (start > 0)
        {
            consumed = start;
            return true;
        }

        var extended = buffer[0] == ExtendedStart;
        var lengthSize = extended ? 2 : 1;

        if (buffer.Length < 2 + lengthSize)
        {
            return false;
        }

        var declared = extended ? Wire.ReadUInt16(buffer, 2) : buffer[2];

        if (declared < 5)
        {
            // Shorter than protocol + serial + crc. Not a frame; drop the marker and resynchronise.
            consumed = 2;
            return true;
        }

        var total = 2 + lengthSize + declared + 2;

        if (buffer.Length < total)
        {
            return false;
        }

        consumed = total;

        if (buffer[total - 2] != StopHigh || buffer[total - 1] != StopLow)
        {
            // The length field pointed somewhere that is not a frame end. Resynchronise from after
            // the start marker rather than trusting the length again.
            consumed = 2;
            return true;
        }

        // From the length field through the serial number inclusive.
        var covered = buffer[2..(2 + lengthSize + declared - 2)];
        var expected = Wire.ReadUInt16(buffer, 2 + lengthSize + declared - 2);

        if (Wire.Crc16X25(covered) != expected)
        {
            frame = null;
            return true;
        }

        var protocol = buffer[2 + lengthSize];
        var content = buffer[(2 + lengthSize + 1)..(2 + lengthSize + declared - 4)];
        var serial = Wire.ReadUInt16(buffer, 2 + lengthSize + declared - 4);

        frame = Interpret(protocol, content, serial);
        return true;
    }

    public byte[]? TryBuildCommand(
        string command, IReadOnlyDictionary<string, string> arguments, string identity, ushort serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);

        // The ASCII vocabulary is the vendor's, not the protocol's — GT06 carries an opaque command
        // string and each firmware family names its own. These four are the Concox spellings, which
        // is what the TK103/ST-901 clones in the target population implement. An unmapped command is
        // a null rather than a guess: a wrong string is silently ignored by the device, which looks
        // exactly like a delivered command that did nothing.
        var text = command switch
        {
            TrackerCommands.SetPosRate when arguments.TryGetValue("seconds", out var seconds)
                && int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                && value is > 0 and <= 86_400 => $"TIMER,{value.ToString(CultureInfo.InvariantCulture)}#",
            TrackerCommands.PingNow => "DWXX#",
            TrackerCommands.Reboot => "RESET#",
            _ => null,
        };

        if (text is null)
        {
            return null;
        }

        var ascii = Encoding.ASCII.GetBytes(text);

        // Content: command length (server flag + command), server flag, command, language.
        // The server flag is echoed back in the device's reply, so it is the correlation id.
        var content = new byte[1 + 4 + ascii.Length + 2];
        content[0] = (byte)(4 + ascii.Length);
        Wire.WriteUInt32(content, 1, serial);
        ascii.CopyTo(content.AsSpan(5));
        Wire.WriteUInt16(content, 5 + ascii.Length, 0x0002);

        return BuildFrame(ProtocolCommand, content, serial);
    }

    /// <summary>Builds a standard frame — used for the acknowledgements and the downlink.</summary>
    internal static byte[] BuildFrame(byte protocol, ReadOnlySpan<byte> content, ushort serial)
    {
        var declared = 1 + content.Length + 2 + 2;

        if (declared > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(content), "A standard GT06 frame cannot carry more than 250 content bytes.");
        }

        var frame = new byte[2 + 1 + declared + 2];

        frame[0] = StandardStart;
        frame[1] = StandardStart;
        frame[2] = (byte)declared;
        frame[3] = protocol;
        content.CopyTo(frame.AsSpan(4));
        Wire.WriteUInt16(frame, 4 + content.Length, serial);
        Wire.WriteUInt16(frame, 6 + content.Length, Wire.Crc16X25(frame.AsSpan(2, 1 + 1 + content.Length + 2)));
        frame[^2] = StopHigh;
        frame[^1] = StopLow;

        return frame;
    }

    private static int IndexOfStart(ReadOnlySpan<byte> buffer)
    {
        for (var index = 0; index + 1 < buffer.Length; index++)
        {
            if ((buffer[index] == StandardStart && buffer[index + 1] == StandardStart)
                || (buffer[index] == ExtendedStart && buffer[index + 1] == ExtendedStart))
            {
                return index;
            }
        }

        return -1;
    }

    private TrackerFrame Interpret(byte protocol, ReadOnlySpan<byte> content, ushort serial)
    {
        switch (protocol)
        {
            case ProtocolLogin:
            {
                // Eight BCD bytes hold sixteen nibbles and an IMEI is fifteen digits, so the first
                // nibble is padding. The type code that may follow is the model, not an identity.
                var identity = content.Length >= 8 ? Wire.ReadBcd(content[..8], trimLeadingZeros: true) : null;

                return new TrackerFrame(
                    FrameKind.Login,
                    identity,
                    Reply: BuildFrame(ProtocolLogin, [], serial),
                    Detail: "gt06 login");
            }

            case ProtocolStatus:
            case 0x23:
            {
                // Terminal information byte: bit 1 is the ACC line. The rest — armed state, charging,
                // the three alarm bits, GPS tracking, oil-and-electricity — is fleet-health's, and
                // fleet-health-svc (C044) is the service that will read it off `sys/diag`.
                bool? ignition = content.Length >= 1 ? (content[0] & 0x02) != 0 : null;

                return new TrackerFrame(
                    FrameKind.Heartbeat,
                    Reply: BuildFrame(protocol, [], serial),
                    Ignition: ignition,
                    Detail: "gt06 status");
            }

            case ProtocolTime:
            {
                // The device is asking what time it is, because its own RTC has no battery. Answering
                // matters more than it looks: every fix it sends afterwards is stamped from this.
                var now = _clock.GetUtcNow().UtcDateTime;

                byte[] stamp =
                [
                    (byte)(now.Year % 100), (byte)now.Month, (byte)now.Day,
                    (byte)now.Hour, (byte)now.Minute, (byte)now.Second,
                ];

                return new TrackerFrame(
                    FrameKind.Ignored, Reply: BuildFrame(ProtocolTime, stamp, serial), Detail: "gt06 time sync");
            }

            case ProtocolLocation:
            case 0x22:
            case ProtocolAlarm:
            case 0x26:
            {
                var fix = ReadFix(content);

                if (fix is null)
                {
                    return new TrackerFrame(FrameKind.Ignored, Detail: $"gt06 0x{protocol:X2} without a readable fix");
                }

                // An alarm frame appends the three status bytes after the LBS block; a plain location
                // frame does not have them. Offsets: 18 bytes of fix, 8 of LBS, then terminal info.
                var kind = protocol is ProtocolAlarm or 0x26 ? FrameKind.Alarm : FrameKind.Position;

                bool? ignition = kind == FrameKind.Alarm && content.Length >= FixLength + 9
                    ? (content[FixLength + 8] & 0x02) != 0
                    : null;

                // Only the alarm frames are acknowledged. A location frame is not: the protocol does
                // not ask for one and devices that receive an unexpected reply on that number log an
                // error and, on some firmware, drop the session.
                var reply = kind == FrameKind.Alarm ? BuildFrame(protocol, [], serial) : null;

                return new TrackerFrame(
                    kind, Fixes: [fix], Reply: reply, Ignition: ignition, Detail: $"gt06 0x{protocol:X2}");
            }

            default:
                // Command replies (0x15/0x21), information transmission (0x94), and everything a
                // vendor added. Parsed, counted, ignored — a protocol number this service does not
                // know is not a reason to drop a device's session.
                return new TrackerFrame(FrameKind.Ignored, Detail: $"gt06 0x{protocol:X2}");
        }
    }

    /// <summary>
    /// The eighteen-byte location layout: <c>datetime(6) | gps(1) | lat(4) | lng(4) | speed(1) |
    /// course+status(2)</c>.
    /// </summary>
    private TrackerFix? ReadFix(ReadOnlySpan<byte> content)
    {
        if (content.Length < FixLength)
        {
            return null;
        }

        var capturedAt = Wire.ReadBinaryTimestamp(content[..6], TimeSpan.Zero);

        if (capturedAt is null)
        {
            // A device with a dead backup cell stamps 00-00-00 until the 0x8A exchange fixes it.
            // Publishing the fix with a receive-time stamp would make it indistinguishable from a
            // real one to T-07's monotonic-clock check, so it is dropped instead.
            return null;
        }

        var satellites = content[6] & 0x0F;
        var latitude = Wire.ReadUInt32(content, 7) / 1_800_000.0;
        var longitude = Wire.ReadUInt32(content, 11) / 1_800_000.0;
        var speedKph = content[15];
        var flags = Wire.ReadUInt16(content, 16);

        // bits 0-9 course, bit 10 north, bit 11 west, bit 12 positioned.
        var course = flags & 0x03FF;
        var north = (flags & 0x0400) != 0;
        var west = (flags & 0x0800) != 0;
        var positioned = (flags & 0x1000) != 0;

        return new TrackerFix(
            capturedAt.Value,
            north ? latitude : -latitude,
            west ? -longitude : longitude,
            positioned,
            SpeedMps: speedKph * Wire.MetresPerSecondPerKph,
            HeadingDeg: course <= 359 ? course : null,
            SatCount: satellites > 0 ? satellites : null);
    }
}
