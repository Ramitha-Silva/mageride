"""
GT06 and JT/T 808 frames, from D6' §4.1's layouts.

WHAT THIS IS FOR
----------------
C131 measures "GT06/JT808 device round-trip from Sri Lanka to the Singapore ingest".  A round trip
needs the platform to answer, so the first question is which frames tcp-adapter answers at all —
and the two families differ in a way that decides what each one can measure:

    GT06   acknowledges login (0x01), status (0x13) and alarm (0x16).  It does NOT acknowledge a
           location frame (0x12): "the protocol does not ask for one and some firmware drops the
           session on an unexpected reply" (Gt06Codec).  So GT06's measurable round trip is the
           STATUS frame — the device's heartbeat — and not a position report.

    JT/T 808 answers a platform general response (0x8001) to the location report (0x0200) itself,
           as well as to the heartbeat (0x0002) and the authenticate (0x0102).  So JT808 measures
           the round trip of an actual POSITION report, which is the closer reading of the
           prompt's words.

Both are reported, separately and labelled, because averaging a heartbeat round trip with a
position round trip would produce a number describing neither.  The difference is a property of
the protocols, not a choice this harness made.

WHY THE FRAMES ARE ASSEMBLED HERE RATHER THAN CALLED OUT OF THE CODEC
---------------------------------------------------------------------
The same rule `tests/E2E`'s `TrackerDevice` states: a device that encodes with the decoder's own
arithmetic can only ever agree with it.  What this module shares with the platform is the
*algorithm the format names* — CRC-16/X-25 for GT06, an XOR-8 for JT/T 808 — reimplemented here,
and pinned by `selftest.py` against the one independently attestable fixed point in either format:
GT06's documented login acknowledgement `78 78 05 01 00 01 D9 DC 0D 0A`.  A wrong polynomial fails
there rather than passing silently against a decoder that is wrong in the same direction.
"""

from __future__ import annotations

import struct
from datetime import datetime, timedelta, timezone

# --- GT06 -------------------------------------------------------------------------------------
GT06_START = 0x78
GT06_LOGIN = 0x01
GT06_LOCATION = 0x12
GT06_STATUS = 0x13
GT06_ALARM = 0x16
GT06_COMMAND = 0x80

#: The one frame in any of these four formats that is attestable against the published protocol
#: rather than against this repository.  `selftest.py` pins the CRC on it.
GT06_DOCUMENTED_LOGIN_ACK = bytes.fromhex("787805010001D9DC0D0A")

# --- JT/T 808 ---------------------------------------------------------------------------------
JT808_FLAG = 0x7E
JT808_LOCATION = 0x0200
JT808_HEARTBEAT = 0x0002
JT808_REGISTER = 0x0100
JT808_AUTHENTICATE = 0x0102
JT808_PLATFORM_GENERAL = 0x8001
JT808_REGISTER_REPLY = 0x8100

#: JT/T 808 §8.18 stamps in Beijing time, and `Adapter:Jt808DeviceUtcOffset` defaults to +08:00.
#: Getting this wrong shifts every fix eight hours, which T-07's clock gate then refuses — the
#: probe would read a platform correctly rejecting a bad frame as a platform that never answered.
JT808_DEVICE_OFFSET = timedelta(hours=8)


def crc16_x25(data: bytes) -> int:
    """
    CRC-16/X-25: reflected 0x8408, init 0xFFFF, final XOR 0xFFFF.

    **Not CRC-CCITT**, which is the same polynomial run the other way and produces a different
    digest over the same bytes.  A decoder using it rejects every genuine frame, and a frame
    builder using it is rejected by every genuine decoder — the failure is total rather than
    subtle, which is the one merciful thing about it.
    """
    crc = 0xFFFF

    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = (crc >> 1) ^ 0x8408 if crc & 1 else crc >> 1

    return crc ^ 0xFFFF


def xor8(data: bytes) -> int:
    """JT/T 808's checksum: an XOR over the header and body, before byte stuffing."""
    checksum = 0

    for byte in data:
        checksum ^= byte

    return checksum


def bcd(digits: str, length: int) -> bytes:
    """Packed BCD, two digits a byte, left-padded to `length` bytes."""
    padded = digits.rjust(length * 2, "0")

    return bytes(
        (int(padded[i]) << 4) | int(padded[i + 1]) for i in range(0, length * 2, 2)
    )


# =============================================================================================
# GT06
# =============================================================================================


def gt06_frame(protocol: int, content: bytes, serial: int) -> bytes:
    """
    `78 78 | len | protocol | content | serial | crc | 0D 0A`.

    `len` counts the protocol byte through the CRC — not the two start bytes and not the
    terminator — and the CRC covers exactly the bytes `len` counts, starting at the length byte
    itself and stopping after the serial.
    """
    declared = 1 + len(content) + 2 + 2
    body = bytes([declared, protocol]) + content + struct.pack("!H", serial)

    return bytes([GT06_START, GT06_START]) + body + struct.pack("!H", crc16_x25(body)) + b"\x0d\x0a"


def gt06_login(imei: str, serial: int) -> bytes:
    """Protocol 0x01: eight BCD bytes of terminal id, then the two-byte model code."""
    return gt06_frame(GT06_LOGIN, bcd("0" + imei, 8) + bytes([0x36, 0x08]), serial)


def gt06_status(serial: int, *, ignition_on: bool = True) -> bytes:
    """
    Protocol 0x13.  Bit 1 of the terminal-information byte is ACC.

    **This is the frame the GT06 round-trip measurement uses.**  It is what the firmware sends as
    a heartbeat, tcp-adapter answers it, and it carries no position — so repeating it at the probe
    rate does not push a vehicle through the plausibility gates the way repeated location frames
    would (`PlausibilityFilter` judges implied speed over the gap between fixes).
    """
    terminal = 0x02 if ignition_on else 0x00

    return gt06_frame(GT06_STATUS, bytes([terminal, 0x06, 0x04, 0x00, 0x01]), serial)


def gt06_position(
    latitude: float, longitude: float, captured_at: datetime, speed_kph: float, serial: int
) -> bytes:
    """Protocol 0x12: `datetime(6) | gps(1) | lat(4) | lng(4) | speed(1) | course+status(2) | LBS(8)`."""
    utc = captured_at.astimezone(timezone.utc)
    content = bytearray(26)

    content[0:6] = bytes(
        [utc.year % 100, utc.month, utc.day, utc.hour, utc.minute, utc.second]
    )
    # High nibble is the GPS block length, low nibble the satellite count.
    content[6] = 0xC9

    struct.pack_into("!I", content, 7, round(abs(latitude) * 1_800_000))
    struct.pack_into("!I", content, 11, round(abs(longitude) * 1_800_000))

    content[15] = max(0, min(255, round(speed_kph)))

    # Positioned (bit 12), north (bit 10), east (bit 11 clear), course 90.
    struct.pack_into("!H", content, 16, 0x1000 | 0x0400 | 90)

    # LBS: MCC 413 (Sri Lanka), MNC 2, and a cell nobody reads.
    content[18:26] = bytes([0x01, 0x9D, 0x02, 0x12, 0x34, 0x00, 0xAB, 0xCD])

    return gt06_frame(GT06_LOCATION, bytes(content), serial)


def gt06_split(buffer: bytes) -> tuple[list[tuple[int, int, bytes]], bytes]:
    """
    Pulls whole GT06 frames out of a receive buffer.

    Answers `(frames, remainder)` where each frame is `(protocol, serial, content)`.  A partial
    frame stays in the remainder: TCP is a byte stream and the adapter's own reply plus a downlink
    command can arrive in one read, which is exactly the case the downlink-latency measurement
    depends on getting right.
    """
    frames: list[tuple[int, int, bytes]] = []
    offset = 0

    while True:
        start = buffer.find(b"\x78\x78", offset)

        if start < 0 or start + 3 > len(buffer):
            break

        declared = buffer[start + 2]
        end = start + 2 + 1 + declared + 2

        if end > len(buffer):
            break

        protocol = buffer[start + 3]
        content = buffer[start + 4 : start + 2 + 1 + declared - 4]
        serial = struct.unpack_from("!H", buffer, start + 2 + 1 + declared - 4)[0]

        frames.append((protocol, serial, content))
        offset = end

    return frames, buffer[offset:]


def gt06_command_text(content: bytes) -> str | None:
    """
    Reads the ASCII command out of a 0x80 downlink frame.

    Layout, from `Gt06Codec.TryBuildCommand`: `len(1) | server flag(4) | command | language(2)`.
    The server flag is the correlation id the device echoes; the probe reads the command text
    because that is what identifies WHICH of the five commands arrived, and the downlink
    measurement asserts it was the one published rather than merely that some bytes came back.
    """
    if len(content) < 7:
        return None

    declared = content[0]

    if declared < 4 or 1 + declared > len(content):
        return None

    return content[5 : 1 + declared].decode("ascii", errors="replace")


# =============================================================================================
# JT/T 808
# =============================================================================================


def jt808_frame(header: bytes, body: bytes) -> bytes:
    """
    `7E | header | body | xor8 | 7E`, byte-stuffed.

    The checksum is computed **before** stuffing and the stuffing is applied after.  In the other
    order the digest covers the escape bytes and the frame decodes nowhere — and the symptom is a
    device that connects, sends and is never answered, which reads as a network problem.
    """
    payload = header + body
    payload += bytes([xor8(payload)])

    stuffed = bytearray([JT808_FLAG])

    for byte in payload:
        if byte == 0x7E:
            stuffed += b"\x7d\x02"
        elif byte == 0x7D:
            stuffed += b"\x7d\x01"
        else:
            stuffed.append(byte)

    stuffed.append(JT808_FLAG)

    return bytes(stuffed)


def jt808_unstuff(frame: bytes) -> bytes:
    """Reverses the stuffing on a frame already stripped of its 0x7E markers."""
    out = bytearray()
    index = 0

    while index < len(frame):
        if frame[index] == 0x7D and index + 1 < len(frame):
            out.append(0x7E if frame[index + 1] == 0x02 else 0x7D)
            index += 2
        else:
            out.append(frame[index])
            index += 1

    return bytes(out)


def jt808_header(message_id: int, imei: str, serial: int, body_length: int) -> bytes:
    """
    The **2019** header shape: properties bit 14 set, a version byte, a ten-byte BCD terminal
    number.

    2013's six-byte BCD terminal number holds twelve digits and an IMEI is fifteen (C043 finding
    3), so a 2013-shaped device decodes fine and authenticates never.  A probe built on the 2013
    shape would measure the platform refusing it and report a round trip that never happened.
    """
    header = bytearray(17)

    struct.pack_into("!H", header, 0, message_id)
    struct.pack_into("!H", header, 2, 0x4000 | body_length)
    header[4] = 0x01
    header[5:15] = bcd(imei.rjust(20, "0"), 10)
    struct.pack_into("!H", header, 15, serial)

    return bytes(header)


def jt808_position(
    imei: str, latitude: float, longitude: float, captured_at: datetime, speed_kph: float, serial: int
) -> bytes:
    """
    Location report 0x0200 — the frame whose round trip the JT808 measurement times.

    The timestamp is written in the device's own time zone (§8.18), which the adapter reads back
    at `Adapter:Jt808DeviceUtcOffset`.
    """
    device_time = captured_at.astimezone(timezone.utc) + JT808_DEVICE_OFFSET
    body = bytearray(28)

    struct.pack_into("!I", body, 0, 0)  # alarm flags
    struct.pack_into("!I", body, 4, 0x0000_0003)  # bit 0 ACC on, bit 1 positioned
    struct.pack_into("!I", body, 8, round(abs(latitude) * 1_000_000))
    struct.pack_into("!I", body, 12, round(abs(longitude) * 1_000_000))
    struct.pack_into("!H", body, 16, 5)  # altitude, m
    struct.pack_into("!H", body, 18, round(speed_kph * 10))  # 0.1 km/h
    struct.pack_into("!H", body, 20, 90)  # course
    body[22:28] = bcd(device_time.strftime("%y%m%d%H%M%S"), 6)

    return jt808_frame(jt808_header(JT808_LOCATION, imei, serial, len(body)), bytes(body))


def jt808_heartbeat(imei: str, serial: int) -> bytes:
    """Terminal heartbeat 0x0002 — an empty body, answered with a general response."""
    return jt808_frame(jt808_header(JT808_HEARTBEAT, imei, serial, 0), b"")


def jt808_split(buffer: bytes) -> tuple[list[tuple[int, int, bytes]], bytes]:
    """
    Pulls whole JT/T 808 messages out of a receive buffer.

    Answers `(messages, remainder)` where each message is `(message_id, reply_serial, body)`.
    `reply_serial` is the serial out of the 0x8001 general response's body — the serial of the
    message being answered — which is what correlates a reply with the frame that provoked it.
    """
    messages: list[tuple[int, int, bytes]] = []
    offset = 0

    while True:
        start = buffer.find(bytes([JT808_FLAG]), offset)

        if start < 0:
            break

        end = buffer.find(bytes([JT808_FLAG]), start + 1)

        if end < 0:
            break

        raw = jt808_unstuff(buffer[start + 1 : end])
        offset = end + 1

        if len(raw) < 13:
            continue

        message_id = struct.unpack_from("!H", raw, 0)[0]
        properties = struct.unpack_from("!H", raw, 2)[0]
        header_length = 17 if properties & 0x4000 else 13
        body = raw[header_length:-1]

        # 0x8001's body is `reply serial(2) | reply id(2) | result(1)`.
        reply_serial = struct.unpack_from("!H", body, 0)[0] if len(body) >= 2 else 0

        messages.append((message_id, reply_serial, body))

    return messages, buffer[offset:]
