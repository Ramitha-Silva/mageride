using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MageRide.Shared.Primitives;
using MageRide.TcpAdapter.Protocols;

// `System.Net.Sockets` is in scope for the socket itself and has a ProtocolFamily of its own; the
// spec's word wins by name, the same alias C043's own harness takes.
using ProtocolFamily = MageRide.TcpAdapter.Protocols.ProtocolFamily;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// A hardware GPS tracker, as far as the platform is concerned: a real socket carrying real
/// protocol frames into tcp-adapter.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is C121's second fence, made of code.</b> "Tracker scenarios drive real protocol frames
/// through tcp-adapter, not synthetic MQTT" — so nothing here publishes to EMQX. A device opens a
/// TCP socket on the adapter's GT06, JT/T 808 or H02 listener (or sends a UDP datagram to its NMEA
/// one), writes the bytes its firmware would write, and everything after that is the platform's:
/// the IMEI is resolved through provisioning-svc, the frame is decoded by the family's codec, the
/// canonical sample is published to <c>veh/{vehicleId}/pos/live</c> as <c>svc-tcp-adapter</c>, and
/// mqtt-bridge-svc and position-processor-svc carry it the rest of the way.
/// </para>
/// <para>
/// <b>Every frame is assembled here, field by field, from D6' §4.1's layouts.</b> Nothing calls the
/// codec that is about to decode it — <c>Gt06Codec.BuildFrame</c> would have been available and is
/// deliberately not used, because a device that encodes with the decoder's own arithmetic can only
/// ever agree with it. What these frames share with the service is the <em>algorithm the format
/// names</em>: CRC-16/X-25 for GT06 and an XOR-8 for JT/T 808, both taken from
/// <see cref="Wire"/> because that is where the platform states them. The one independently
/// attestable fixed point in any of the four formats — GT06's documented login acknowledgement
/// <c>78 78 05 01 00 01 D9 DC 0D 0A</c> — is pinned against the same CRC by C043's
/// <c>WireTests</c>, so a wrong polynomial fails there rather than passing here.
/// </para>
/// <para>
/// <b>Every fix is stamped now.</b> A frame older than <c>Adapter:ReplayAge</c> is routed to
/// <c>pos/replay</c> and filtered against the <c>veh:seq</c> watermark (T-05), so a scenario that
/// back-dated a fix to make an arithmetic point would find it silently discarded as a device's
/// history arriving late.
/// </para>
/// </remarks>
internal sealed class TrackerDevice : IAsyncDisposable
{
    /// <summary>Colombo's speed for a bus in traffic, and comfortably under ADD §12.6's 120 km/h.</summary>
    private const double DefaultSpeedKph = 30;

    private readonly Socket _socket;
    private readonly ProtocolFamily _family;
    private readonly string _imei;
    private readonly bool _datagram;

    private ushort _serial = 1;

    private TrackerDevice(Socket socket, ProtocolFamily family, string imei, bool datagram)
    {
        _socket = socket;
        _family = family;
        _imei = imei;
        _datagram = datagram;
    }

    /// <summary>The IMEI this device presents on every frame.</summary>
    public string Imei => _imei;

    /// <summary>Which of D6' §4.1's four families it speaks.</summary>
    public ProtocolFamily Family => _family;

    /// <summary>
    /// Connects a device to the adapter's listener for <paramref name="family"/> and logs in where
    /// the protocol has a login.
    /// </summary>
    /// <remarks>
    /// GT06 is the only one of the four with a login handshake; the other three identify themselves
    /// on every frame (JT/T 808 in its header's BCD terminal number, H02 in its second field, NMEA
    /// in the <c>IMEI:</c> prefix this component defines because no spec gives one).
    /// </remarks>
    public static async Task<TrackerDevice> ConnectAsync(ModeAbFleet fleet, ProtocolFamily family, string imei)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentException.ThrowIfNullOrWhiteSpace(imei);

        var port = await fleet.TrackerPortAsync(family);
        var datagram = family == ProtocolFamily.NmeaUdp;

        var socket = datagram
            ? new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));

        var device = new TrackerDevice(socket, family, imei, datagram);

        if (family == ProtocolFamily.Gt06)
        {
            await device.SendAsync(Gt06Login(imei, device.NextSerial()));

            // The acknowledgement is the adapter saying the IMEI resolved to a vehicle. A device
            // whose login goes unanswered re-sends it and eventually reboots its modem, so waiting
            // for it here is both what the firmware does and the earliest point at which a scenario
            // can know the binding took.
            var ack = await device.ReceiveAsync(TimeSpan.FromSeconds(10));

            Assert.True(
                ack.Length > 0,
                $"tcp-adapter never acknowledged the GT06 login for IMEI {imei} — the binding did not resolve.");
        }

        return device;
    }

    /// <summary>Reports one position, in whatever this device's family calls a position frame.</summary>
    public Task<DateTimeOffset> ReportAsync(GeoPoint at, double speedKph = DefaultSpeedKph) =>
        ReportAsync(at, DateTimeOffset.UtcNow, speedKph);

    /// <summary>
    /// Reports one position captured at <paramref name="capturedAt"/>, and answers the instant the
    /// frame actually carries.
    /// </summary>
    /// <remarks>
    /// <b>Truncated to the whole second</b>, because all four of these formats stamp to the second —
    /// and the returned value is what a scenario waits on. "The platform has seen the fix I sent" is
    /// the only synchronisation point that works here: the frame crosses a broker, two Kafka topics
    /// and four services before it becomes a column, and a scenario that carried on as soon as
    /// <em>some</em> fix had landed would have the rest of them arrive later — on whatever session
    /// happened to be live by then.
    /// </remarks>
    public async Task<DateTimeOffset> ReportAsync(
        GeoPoint at, DateTimeOffset capturedAt, double speedKph = DefaultSpeedKph)
    {
        var utc = capturedAt.ToUniversalTime();
        var stamped = new DateTimeOffset(
            utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, TimeSpan.Zero);

        await SendAsync(_family switch
        {
            ProtocolFamily.Gt06 => Gt06Position(at, stamped, speedKph, NextSerial()),
            ProtocolFamily.Jt808 => Jt808Position(_imei, at, stamped, speedKph, NextSerial()),
            ProtocolFamily.H02 => Encoding.ASCII.GetBytes(H02Position(_imei, at, stamped, speedKph, ignitionOn: true)),
            ProtocolFamily.NmeaUdp => Encoding.ASCII.GetBytes(NmeaDatagram(_imei, at, stamped, speedKph)),
            _ => throw new ArgumentOutOfRangeException(nameof(_family), _family, "No frame layout for this family."),
        });

        return stamped;
    }

    /// <summary>
    /// Reports the ACC line, which is what auto-starts and auto-ends a journey (AL-32, US-3.22).
    /// </summary>
    /// <remarks>
    /// GT06 carries it in the <c>0x13</c> status frame's terminal-information byte, bit 1. The
    /// transition — not the level — is what the adapter reports onward, so sending the same state
    /// twice is a heartbeat and opens nothing.
    /// </remarks>
    public Task ReportIgnitionAsync(bool on)
    {
        Assert.True(
            _family == ProtocolFamily.Gt06,
            "Only the GT06 status frame carries an ACC line this device can set on its own; "
            + "H02 and JT/T 808 carry ignition inside a position frame's status word.");

        return SendAsync(Gt06Status(terminalInformation: on ? (byte)0x02 : (byte)0x00, NextSerial()));
    }

    /// <summary>
    /// Half-closes the socket — the FIN a device losing its uplink sends, and what T-04's retained
    /// <c>status=offline</c> is published from.
    /// </summary>
    /// <remarks>
    /// Deliberately not a full close: the adapter's read loop has to see <c>ReadAsync</c> return
    /// zero and treat it as the device going away, and a scenario has to be able to tell that apart
    /// from a socket this side tore down entirely.
    /// </remarks>
    public void LoseUplink()
    {
        Assert.False(_datagram, "A UDP device has no socket to half-close.");
        _socket.Shutdown(SocketShutdown.Send);
    }

    /// <summary>Whether the adapter closed this socket, which is how a refused device is observed.</summary>
    public async Task<bool> WasClosedAsync(TimeSpan? within = null)
    {
        var buffer = new byte[256];
        var deadline = DateTimeOffset.UtcNow + (within ?? TimeSpan.FromSeconds(10));

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            try
            {
                if (await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellation.Token) == 0)
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                // Nothing yet; the adapter may still be resolving the device.
            }
            catch (SocketException)
            {
                // A reset rather than a graceful close. Still closed.
                return true;
            }
        }

        return false;
    }

    public async Task SendAsync(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        await _socket.SendAsync(frame, SocketFlags.None);
    }

    public async Task<byte[]> ReceiveAsync(TimeSpan? timeout = null)
    {
        var buffer = new byte[512];

        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));

        try
        {
            var read = await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellation.Token);

            return buffer.AsSpan(0, read).ToArray();
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException)
        {
            return [];
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (!_datagram && _socket.Connected)
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // Already gone — which several of these scenarios arrange on purpose.
        }

        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    // -----------------------------------------------------------------------------------------
    // The four frame layouts (D6' §4.1)
    // -----------------------------------------------------------------------------------------

    /// <summary>GT06 protocol numbers, from the format rather than from the codec.</summary>
    private const byte Gt06LoginProtocol = 0x01;

    private const byte Gt06LocationProtocol = 0x12;

    private const byte Gt06StatusProtocol = 0x13;

    /// <summary>GT06 login, protocol <c>0x01</c>: eight BCD bytes of terminal id, then a model code.</summary>
    private static byte[] Gt06Login(string imei, ushort serial)
    {
        var content = new byte[10];
        Wire.WriteBcd("0" + imei, 8).CopyTo(content.AsSpan());

        // The two-byte model code a Concox unit appends. Not an identity; the adapter ignores it.
        content[8] = 0x36;
        content[9] = 0x08;

        return Gt06Frame(Gt06LoginProtocol, content, serial);
    }

    /// <summary>
    /// GT06 location, protocol <c>0x12</c>:
    /// <c>datetime(6) | gps(1) | lat(4) | lng(4) | speed(1) | course+status(2) | LBS(8)</c>.
    /// </summary>
    private static byte[] Gt06Position(GeoPoint at, DateTimeOffset capturedAt, double speedKph, ushort serial)
    {
        var utc = capturedAt.ToUniversalTime();
        var content = new byte[26];

        content[0] = (byte)(utc.Year % 100);
        content[1] = (byte)utc.Month;
        content[2] = (byte)utc.Day;
        content[3] = (byte)utc.Hour;
        content[4] = (byte)utc.Minute;
        content[5] = (byte)utc.Second;

        // High nibble is the GPS block length, low nibble the satellite count.
        content[6] = 0xC9;

        Wire.WriteUInt32(content, 7, (uint)Math.Round(at.Latitude * 1_800_000));
        Wire.WriteUInt32(content, 11, (uint)Math.Round(at.Longitude * 1_800_000));

        // Whole km/h in one byte, which is all the format has room for.
        content[15] = (byte)Math.Clamp(Math.Round(speedKph), 0, 255);

        // Positioned (bit 12), north (bit 10), east (bit 11 clear), course 90.
        Wire.WriteUInt16(content, 16, (ushort)(0x1000 | 0x0400 | 90));

        // LBS: MCC 413 (Sri Lanka), MNC 2, and a cell nobody reads.
        ReadOnlySpan<byte> lbs = [0x01, 0x9D, 0x02, 0x12, 0x34, 0x00, 0xAB, 0xCD];
        lbs.CopyTo(content.AsSpan(18));

        return Gt06Frame(Gt06LocationProtocol, content, serial);
    }

    /// <summary>GT06 status, protocol <c>0x13</c>. Bit 1 of the terminal-information byte is ACC.</summary>
    private static byte[] Gt06Status(byte terminalInformation, ushort serial) =>
        Gt06Frame(Gt06StatusProtocol, [terminalInformation, 0x06, 0x04, 0x00, 0x01], serial);

    /// <summary>
    /// Wraps a GT06 payload: <c>78 78 | len | protocol | content | serial | crc | 0D 0A</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>len</c> counts the protocol byte through the CRC — not the two start bytes and not the
    /// terminator — and the CRC is computed over exactly the bytes <c>len</c> counts up to and
    /// including the serial. Plain CRC-CCITT over the same bytes gives a different digest and a
    /// device using it is refused by every genuine adapter.
    /// </para>
    /// <para>
    /// Not private, so <c>TrackerPlaneScenario</c> can pin it against the one frame in any of these
    /// four formats that is independently attestable — GT06's documented login acknowledgement. A
    /// frame builder nothing checks is a frame builder that can drift into agreeing with a decoder
    /// that is also wrong.
    /// </remarks>
    internal static byte[] Gt06Frame(byte protocol, ReadOnlySpan<byte> content, ushort serial)
    {
        var declared = 1 + content.Length + 2 + 2;
        var frame = new byte[2 + 1 + declared + 2];

        frame[0] = 0x78;
        frame[1] = 0x78;
        frame[2] = (byte)declared;
        frame[3] = protocol;
        content.CopyTo(frame.AsSpan(4));

        Wire.WriteUInt16(frame, 4 + content.Length, serial);

        // The digest covers the length byte, the protocol byte, the content and the serial — the
        // four things `len` counts up to — and stops there.
        Wire.WriteUInt16(frame, 6 + content.Length, Wire.Crc16X25(frame.AsSpan(2, 4 + content.Length)));

        frame[^2] = 0x0D;
        frame[^1] = 0x0A;

        return frame;
    }

    /// <summary>
    /// JT/T 808 location report <c>0x0200</c> in the <b>2019</b> header shape.
    /// </summary>
    /// <remarks>
    /// The 2019 shape and not 2013's, because 2013's six-byte BCD terminal number holds twelve
    /// digits and an IMEI is fifteen — such a device decodes fine and authenticates never (C043
    /// finding 3). Properties bit 14 set (<c>0x4000</c>) is what says which shape this is; the BCD
    /// timestamp is Beijing time (§8.18), so it is written at the adapter's configured device
    /// offset rather than in UTC.
    /// </remarks>
    private static byte[] Jt808Position(
        string imei, GeoPoint at, DateTimeOffset capturedAt, double speedKph, ushort serial)
    {
        var beijing = capturedAt.ToOffset(TimeSpan.FromHours(8));

        var body = new byte[28];

        // Alarm flags, then the status word: bit 0 ACC on, bit 1 positioned.
        Wire.WriteUInt32(body, 0, 0);
        Wire.WriteUInt32(body, 4, 0x0000_0003);

        // Latitude and longitude in millionths of a degree.
        Wire.WriteUInt32(body, 8, (uint)Math.Round(Math.Abs(at.Latitude) * 1_000_000));
        Wire.WriteUInt32(body, 12, (uint)Math.Round(Math.Abs(at.Longitude) * 1_000_000));

        // Altitude (m), speed (0.1 km/h) and course.
        Wire.WriteUInt16(body, 16, 5);
        Wire.WriteUInt16(body, 18, (ushort)Math.Round(speedKph * 10));
        Wire.WriteUInt16(body, 20, 90);

        // YYMMDDhhmmss, BCD, in the device's own time zone.
        Wire.WriteBcd(beijing.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture), 6).CopyTo(body.AsSpan(22));

        var header = new byte[17];
        Wire.WriteUInt16(header, 0, 0x0200);

        // Properties: body length in bits 0-9, plus bit 14 for the 2019 shape.
        Wire.WriteUInt16(header, 2, (ushort)(0x4000 | body.Length));

        // Protocol version, then the ten-byte BCD terminal number the IMEI is written into.
        header[4] = 0x01;
        Wire.WriteBcd(imei.PadLeft(20, '0'), 10).CopyTo(header.AsSpan(5));
        Wire.WriteUInt16(header, 15, serial);

        return Jt808Frame(header, body);
    }

    /// <summary>
    /// Wraps a JT/T 808 message: <c>7E | header | body | xor8 | 7E</c>, byte-stuffed.
    /// </summary>
    /// <remarks>
    /// The checksum is an XOR over the header and body before stuffing; the stuffing then replaces
    /// <c>7E</c> with <c>7D 02</c> and <c>7D</c> with <c>7D 01</c> everywhere between the markers,
    /// which is what makes the delimiter unambiguous. Applying them in the other order produces a
    /// frame whose checksum covers the escapes and decodes nowhere.
    /// </remarks>
    private static byte[] Jt808Frame(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
    {
        var payload = new byte[header.Length + body.Length + 1];

        header.CopyTo(payload);
        body.CopyTo(payload.AsSpan(header.Length));
        payload[^1] = Wire.Xor8(payload.AsSpan(0, payload.Length - 1));

        var framed = new List<byte>(payload.Length + 8) { 0x7E };

        foreach (var value in payload)
        {
            switch (value)
            {
                case 0x7E:
                    framed.Add(0x7D);
                    framed.Add(0x02);
                    break;

                case 0x7D:
                    framed.Add(0x7D);
                    framed.Add(0x01);
                    break;

                default:
                    framed.Add(value);
                    break;
            }
        }

        framed.Add(0x7E);

        return [.. framed];
    }

    /// <summary>
    /// An H02 <c>V1</c> line.
    /// </summary>
    /// <remarks>
    /// Speed is in <b>knots</b> — read as km/h it understates a coach by 1.85× — and the status
    /// word's ACC bit is <b>active low</b>, which is what makes <c>FFFFFBFF</c> mean ignition on.
    /// Comma-delimited: D6' §4.1 calls the family pipe-delimited and the wire is commas; the
    /// adapter accepts both (C043 finding 4).
    /// </remarks>
    private static string H02Position(
        string imei, GeoPoint at, DateTimeOffset capturedAt, double speedKph, bool ignitionOn)
    {
        var utc = capturedAt.ToUniversalTime();
        var knots = speedKph / 1.852;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"*HQ,{imei},V1,{utc:HHmmss},A,{DegreesMinutes(at.Latitude, 2)},N," +
            $"{DegreesMinutes(at.Longitude, 3)},E,{knots:000.0},090,{utc:ddMMyy}," +
            $"{(ignitionOn ? "FFFFFBFF" : "FFFFFFFF")}#");
    }

    /// <summary>
    /// A generic-NMEA datagram: the <c>IMEI:</c> prefix, then RMC and GGA for the same second.
    /// </summary>
    /// <remarks>
    /// Generic NMEA carries no device identity at all, so the framing is C043's own and is stated in
    /// <c>NmeaCodec</c> and nowhere else. RMC carries the date and the speed; GGA carries the
    /// satellite count and the HDOP.
    /// </remarks>
    private static string NmeaDatagram(string imei, GeoPoint at, DateTimeOffset capturedAt, double speedKph)
    {
        var utc = capturedAt.ToUniversalTime();
        var latitude = DegreesMinutes(at.Latitude, 2);
        var longitude = DegreesMinutes(at.Longitude, 3);
        var knots = speedKph / 1.852;

        var rmc = Checksum(string.Create(
            CultureInfo.InvariantCulture,
            $"GPRMC,{utc:HHmmss}.00,A,{latitude},N,{longitude},E,{knots:000.0},090.0,{utc:ddMMyy},,,A"));

        var gga = Checksum(string.Create(
            CultureInfo.InvariantCulture,
            $"GPGGA,{utc:HHmmss}.00,{latitude},N,{longitude},E,1,09,0.9,5.0,M,,M,,"));

        return $"IMEI:{imei};{rmc}\r\n{gga}";
    }

    /// <summary><c>ddmm.mmmm</c> / <c>dddmm.mmmm</c>, as NMEA and H02 write a coordinate.</summary>
    private static string DegreesMinutes(double value, int degreeWidth)
    {
        var degrees = (int)Math.Abs(value);
        var minutes = (Math.Abs(value) - degrees) * 60;

        return degrees.ToString($"D{degreeWidth}", CultureInfo.InvariantCulture)
               + minutes.ToString("00.0000", CultureInfo.InvariantCulture);
    }

    /// <summary>Wraps a sentence body in <c>$…*hh</c>.</summary>
    private static string Checksum(string body)
    {
        byte checksum = 0;

        foreach (var character in body)
        {
            checksum ^= (byte)character;
        }

        return string.Create(CultureInfo.InvariantCulture, $"${body}*{checksum:X2}");
    }

    /// <summary>
    /// The next information serial.
    /// </summary>
    /// <remarks>
    /// Sixteen bits and it wraps in hours on a real device, which is exactly why the platform's
    /// <c>seq</c> is the capture instant rather than this number (C043). It still has to advance,
    /// because a codec that remembers the last serial is what a downlink reply is addressed with.
    /// </remarks>
    private ushort NextSerial() => _serial++;
}
