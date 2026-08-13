#!/usr/bin/env python3
"""
The measurement code's own tests.  `run.sh` runs this FIRST and refuses to measure anything if it
fails.

WHY THIS EXISTS, AND WHY IT IS A GATE RATHER THAN A CONVENIENCE
---------------------------------------------------------------
This harness runs against the Singapore region.  That run is expensive to arrange, is not
repeatable on a whim, and produces the numbers a go-live decision is made on.  It is also, on the
day it happens, the FIRST time most of this code will have executed against anything real — which
is exactly the shape of C130's finding about `infra/replica/restore.sh`: a recovery script that
could never recover, whose own verification never exercised the broken path.

So every calculation here is pinned against something that is true independently of this
repository:

  * the E-model, against ITU-T G.107's own published values and the fixed points its formulae
    are continuous at;
  * the RFC 3550 jitter estimator, against a stream whose jitter is known by construction;
  * the burst-ratio term, against loss patterns whose burst structure is known by construction;
  * the STUN/TURN encoding, against RFC 5389's own worked message-type values;
  * the GT06 frame builder, against the one independently attestable frame in the format —
    the documented login acknowledgement `78 78 05 01 00 01 D9 DC 0D 0A`;
  * the JT/T 808 builder, against a round trip through its own unstuffing plus the byte-stuffing
    the standard specifies.

Nothing here needs a network, a region, a device or a broker.  It runs in under a second on any
box and is a hard gate on the acceptance run.

    python3 acceptance/sg/selftest.py            # 0 = the instruments are sound
"""

from __future__ import annotations

import math
import struct
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from lib import emodel, frames, rtpstats, turn  # noqa: E402

FAILURES: list[str] = []
CHECKS = 0


def check(condition: bool, description: str) -> None:
    global CHECKS
    CHECKS += 1

    if condition:
        print(f"  \033[32m✓\033[0m {description}")
    else:
        print(f"  \033[31m✗\033[0m {description}")
        FAILURES.append(description)


def close(actual: float, expected: float, tolerance: float, description: str) -> None:
    check(
        abs(actual - expected) <= tolerance,
        f"{description}  (got {actual:.4f}, expected {expected:.4f} ±{tolerance})",
    )


def section(title: str) -> None:
    print(f"\n\033[1m▸ {title}\033[0m")


# =============================================================================================
# G.107 — the E-model
# =============================================================================================


def test_emodel() -> None:
    section("ITU-T G.107 E-model")

    # G.107's default connection: R0 = 93.2 with every Table 1 default, no delay, no loss.  The
    # MOS this produces — 4.41 — is the single most widely quoted output of the model and is what
    # an unimpaired G.711 connection scores.  If this drifts, everything downstream is wrong.
    default = emodel.rate(ta_ms=0.0, ppl_percent=0.0)
    close(default.r, 93.2, 1e-9, "R for the G.107 default connection is 93.2")
    close(default.mos, 4.4092, 5e-4, "MOS-CQE for the default connection is 4.41")

    # Annex B's transform, at the three points where it is defined by inspection rather than by
    # arithmetic: it is clamped at both ends, and the cubic term vanishes at R = 60 and R = 100.
    close(emodel.r_to_mos(0.0), 1.0, 1e-12, "R = 0 clamps to MOS 1.0")
    close(emodel.r_to_mos(100.0), 4.5, 1e-12, "R = 100 clamps to MOS 4.5")
    close(emodel.r_to_mos(-20.0), 1.0, 1e-12, "R < 0 clamps to MOS 1.0")
    close(emodel.r_to_mos(60.0), 3.1, 1e-12, "at R = 60 the cubic term vanishes: MOS = 1 + 0.035*60")

    # Idd is defined piecewise with a break at 100 ms, and G.107 makes it continuous there — the
    # X = log2(Ta/100) substitution is zero at Ta = 100 and the bracket collapses to (1 - 3 + 2).
    # A sign slip anywhere in that expression shows up here as a discontinuity.
    close(emodel.idd(99.999), 0.0, 1e-9, "Idd is zero below 100 ms")
    close(emodel.idd(100.0), 0.0, 1e-9, "Idd is continuous at 100 ms — the break point is zero")
    check(emodel.idd(150.0) > 0.0, "Idd is positive above 100 ms")

    # Two points computed from Annex B's own formula, which is the only way to pin a curve that
    # has no closed form worth quoting: at 200 ms the impairment is still small, and by 400 ms it
    # has grown eightfold.  That acceleration is the whole reason relay placement matters.
    close(emodel.idd(200.0), 3.0444, 1e-3, "Idd(200 ms) ≈ 3.04")
    close(emodel.idd(400.0), 24.0706, 1e-3, "Idd(400 ms) ≈ 24.07")
    check(emodel.idd(400.0) > 7.0 * emodel.idd(200.0), "Idd grows faster than linearly with delay")

    # Ie_eff: zero loss leaves the codec's own impairment untouched, and loss is monotonic.
    close(emodel.ie_effective(0.0), emodel.IE_G711_PLC, 1e-12, "Ie_eff at zero loss is Ie")
    check(
        emodel.ie_effective(1.0) < emodel.ie_effective(5.0) < emodel.ie_effective(20.0),
        "Ie_eff rises monotonically with packet loss",
    )

    # BurstR divides the loss term, so bursty loss must rate WORSE than scattered loss at the
    # same percentage.  Getting this backwards would make the model reward a relay that drops
    # packets in clumps — which is exactly what a saturated relay does.
    scattered = emodel.ie_effective(2.0, burst_ratio=1.0)
    bursty = emodel.ie_effective(2.0, burst_ratio=4.0)
    check(bursty > scattered, "the same loss in bursts is a larger impairment than scattered loss")

    # The advantage factor only ever helps, and is off by default.
    check(emodel.A_DEFAULT == 0.0, "the advantage factor A defaults to 0 — the model is not flattered")
    check(
        emodel.rate(300.0, 1.0, a=emodel.A_MOBILE_VEHICLE).r
        == emodel.rate(300.0, 1.0).r + emodel.A_MOBILE_VEHICLE,
        "A enters the rating additively, exactly as G.107 §3 states",
    )

    # The delay budget is the inverse used by the Colombo-TURN recommendation.  It has to be
    # monotonic in the floor, and it has to agree with a forward evaluation at its own boundary.
    budget = emodel.delay_budget_ms(4.0)
    check(budget > 0.0, "a MOS 4.0 floor admits a non-zero delay budget")
    close(emodel.rate(budget, 0.0).mos, 4.0, 1e-3, "the delay budget's boundary evaluates back to its floor")
    check(
        emodel.delay_budget_ms(4.2) < emodel.delay_budget_ms(4.0) < emodel.delay_budget_ms(3.6),
        "a stricter MOS floor buys a smaller delay budget",
    )
    check(
        emodel.delay_budget_ms(4.0, ppl_percent=2.0) < emodel.delay_budget_ms(4.0, ppl_percent=0.0),
        "packet loss eats into the delay budget",
    )


# =============================================================================================
# RFC 3550 — jitter, loss and the delay breakdown
# =============================================================================================


def test_rtpstats() -> None:
    section("RFC 3550 stream statistics")

    # A stream with constant transit time has zero jitter by definition.  The estimator is a
    # first-order filter over |D|, so a constant D of zero can only decay towards zero.
    steady = rtpstats.StreamAccumulator()
    for seq in range(200):
        steady.on_send(seq, seq * 0.02)
        steady.on_receive(seq, seq * 0.02 + 0.050)
    stats = steady.finish()
    close(stats.jitter_ms, 0.0, 1e-9, "a constant-delay stream has zero jitter")
    close(stats.rtt_mean_ms, 50.0, 1e-9, "a constant 50 ms transit reports a 50 ms mean RTT")
    check(stats.lost == 0 and stats.loss_percent == 0.0, "a complete stream reports zero loss")

    # Alternating transit times give |D| = the swing on every packet, and the filter converges to
    # it.  100 packets is ~6 time constants at a gain of 16, so convergence is well inside the
    # tolerance.
    swinging = rtpstats.StreamAccumulator()
    for seq in range(400):
        swinging.on_send(seq, seq * 0.02)
        swinging.on_receive(seq, seq * 0.02 + (0.050 if seq % 2 else 0.070))
    stats = swinging.finish()
    close(stats.jitter_ms, 20.0, 0.5, "a ±10 ms alternating transit converges to 20 ms jitter")

    # Loss: sent and never received.
    lossy = rtpstats.StreamAccumulator()
    for seq in range(100):
        lossy.on_send(seq, seq * 0.02)
        if seq % 10 != 0:
            lossy.on_receive(seq, seq * 0.02 + 0.050)
    stats = lossy.finish()
    check(stats.lost == 10, "ten dropped packets in a hundred are counted as ten lost")
    close(stats.loss_percent, 10.0, 1e-9, "ten in a hundred is 10 % loss")

    # Scattered single losses are burst length 1, so BurstR is 1 (up to the (1-p) correction).
    close(stats.burst_ratio, 1.0, 0.15, "isolated losses give a burst ratio of ~1")

    # The same loss percentage arriving in one run of ten must give a larger burst ratio.
    clumped = rtpstats.StreamAccumulator()
    for seq in range(100):
        clumped.on_send(seq, seq * 0.02)
        if not 40 <= seq < 50:
            clumped.on_receive(seq, seq * 0.02 + 0.050)
    clumped_stats = clumped.finish()
    check(clumped_stats.lost == 10, "a ten-packet outage is ten lost packets")
    check(
        clumped_stats.burst_ratio > 5.0 * stats.burst_ratio,
        "one run of ten rates far burstier than ten scattered singles",
    )

    # Duplicates and reordering are counted, not silently folded into loss.
    odd = rtpstats.StreamAccumulator()
    for seq in range(10):
        odd.on_send(seq, seq * 0.02)
    odd.on_receive(1, 0.10)
    odd.on_receive(0, 0.11)
    odd.on_receive(1, 0.12)
    odd_stats = odd.finish()
    check(odd_stats.reordered == 1, "a packet arriving after a higher sequence is counted as reordered")
    check(odd_stats.duplicated == 1, "a repeated sequence is counted as a duplicate, not as an arrival")
    check(odd_stats.received == 2, "a duplicate does not inflate the received count")

    # Percentiles are a nearest-rank over observed samples: p100 is the largest sample and
    # nothing between two samples is invented.
    ranked = rtpstats.StreamAccumulator()
    for seq in range(100):
        ranked.on_send(seq, 0.0)
        ranked.on_receive(seq, (seq + 1) / 1000.0)
    ranked_stats = ranked.finish()
    close(ranked_stats.rtt_percentile_ms(100), 100.0, 1e-9, "p100 is the largest observed sample")
    check(
        ranked_stats.rtt_percentile_ms(95) in [float(v) for v in range(1, 101)],
        "a percentile is an observed sample, never an interpolation between two",
    )

    section("the relayed-call delay budget")

    # THE factor this whole component turns on.  The probe's echo path A -> relay -> B -> relay -> A
    # is two traversals of the call's one-way path, so the network term must be half the probe RTT.
    total, terms = rtpstats.relayed_call_delay_ms(probe_rtt_ms=140.0, jitter_ms=0.0)
    close(terms["network_one_way_ms"], 70.0, 1e-9, "the network term is HALF the probe's echo RTT")
    close(
        total,
        70.0 + rtpstats.jitter_buffer_ms(0.0) + rtpstats.CODEC_DELAY_MS,
        1e-9,
        "the delay budget is the sum of its three published terms",
    )
    check(
        set(terms) == {"network_one_way_ms", "jitter_buffer_ms", "codec_and_packetisation_ms"},
        "the budget is reported as three separable terms, so a reader can substitute their own",
    )

    # The jitter buffer grows with jitter and is bounded at both ends.
    check(
        rtpstats.jitter_buffer_ms(0.0) < rtpstats.jitter_buffer_ms(10.0) < rtpstats.jitter_buffer_ms(30.0),
        "the modelled jitter buffer grows with measured jitter",
    )
    close(rtpstats.jitter_buffer_ms(1000.0), 200.0, 1e-9, "the modelled jitter buffer is capped at 200 ms")
    check(rtpstats.jitter_buffer_ms(0.0) >= rtpstats.PACKET_MS, "the buffer is never below one packet")


# =============================================================================================
# RFC 5389 / RFC 5766 — the TURN encoding
# =============================================================================================


def test_turn() -> None:
    section("RFC 5389 / RFC 5766 message encoding")

    # RFC 5389 §6 interleaves the method and class bits.  These four values are printed in the
    # RFCs themselves, which is what makes them a test rather than a restatement of the code.
    check(turn.message_type(turn.METHOD_BINDING, turn.CLASS_REQUEST) == 0x0001, "Binding request is 0x0001")
    check(turn.message_type(turn.METHOD_ALLOCATE, turn.CLASS_REQUEST) == 0x0003, "Allocate request is 0x0003")
    check(
        turn.message_type(turn.METHOD_ALLOCATE, turn.CLASS_SUCCESS) == 0x0103,
        "Allocate success response is 0x0103",
    )
    check(
        turn.message_type(turn.METHOD_ALLOCATE, turn.CLASS_ERROR) == 0x0113,
        "Allocate error response is 0x0113",
    )
    check(turn.message_type(turn.METHOD_DATA, turn.CLASS_INDICATION) == 0x0017, "Data indication is 0x0017")
    check(
        turn.message_type(turn.METHOD_CREATE_PERMISSION, turn.CLASS_REQUEST) == 0x0008,
        "CreatePermission request is 0x0008",
    )

    # A message this module builds must parse back through its own decoder, method and class
    # intact — the encode and decode paths derive the interleaving independently.
    built = turn.build_message(
        turn.METHOD_ALLOCATE, turn.CLASS_REQUEST, b"0123456789ab",
        [(turn.ATTR_REQUESTED_TRANSPORT, struct.pack("!BBBB", 17, 0, 0, 0))],
    )
    parsed = turn.parse_message(built)
    check(parsed is not None, "a built Allocate parses back as a STUN message")
    assert parsed is not None
    check(parsed.method == turn.METHOD_ALLOCATE, "the method survives the round trip")
    check(parsed.cls == turn.CLASS_REQUEST, "the class survives the round trip")
    check(parsed.transaction == b"0123456789ab", "the transaction id survives the round trip")
    check(
        parsed.first(turn.ATTR_REQUESTED_TRANSPORT) == struct.pack("!BBBB", 17, 0, 0, 0),
        "REQUESTED-TRANSPORT survives the round trip as UDP (17)",
    )

    # The header carries the magic cookie in the right place, which is what distinguishes a STUN
    # message from ChannelData on a shared socket.
    check(struct.unpack_from("!I", built, 4)[0] == turn.MAGIC_COOKIE, "the magic cookie is at offset 4")

    # MESSAGE-INTEGRITY: the length field must ALREADY count the 24 bytes the attribute occupies
    # when the digest is computed, and must equal the real body length afterwards.  Getting it
    # wrong is a 401 that looks exactly like a wrong shared secret.
    with_integrity = turn.build_message(
        turn.METHOD_ALLOCATE, turn.CLASS_REQUEST, b"0123456789ab", [],
        credential=("1700000000:c131", "voip.mageride.lk", "cGFzc3dvcmQ="),
    )
    declared = struct.unpack_from("!H", with_integrity, 2)[0]
    check(declared == len(with_integrity) - 20, "the final length field matches the real body length")
    integrity = turn.parse_message(with_integrity)
    assert integrity is not None
    digest = integrity.first(turn.ATTR_MESSAGE_INTEGRITY)
    check(digest is not None and len(digest) == 20, "MESSAGE-INTEGRITY is a 20-byte HMAC-SHA1")

    # Recomputing the digest by hand, the way a server does, must agree.
    import hashlib
    import hmac

    key = hashlib.md5(
        b"1700000000:c131:voip.mageride.lk:cGFzc3dvcmQ=", usedforsecurity=False
    ).digest()
    header = struct.pack("!HHI12s", turn.message_type(turn.METHOD_ALLOCATE, turn.CLASS_REQUEST),
                         24, turn.MAGIC_COOKIE, b"0123456789ab")
    check(
        hmac.new(key, header, hashlib.sha1).digest() == digest,
        "the integrity digest is computed over the length INCLUDING its own 24 bytes",
    )

    # XOR addresses: encode and decode are separate implementations of the same masking.
    encoded = turn.encode_xor_peer_address("203.0.113.7", 50123)
    check(turn.decode_xor_address(encoded) == ("203.0.113.7", 50123), "an XOR peer address round-trips")
    check(
        encoded[4:8] != bytes([203, 0, 113, 7]),
        "the address really is XOR-masked rather than written in the clear",
    )

    # coturn's ephemeral credential: the username is an expiry and the password is its HMAC.
    username, password = turn.ephemeral_credential("shared-secret", ttl_seconds=3600, name="c131")
    check(username.endswith(":c131"), "the ephemeral username carries the configured name")
    expiry = int(username.split(":", 1)[0])
    check(expiry > 0, "the ephemeral username is an expiry timestamp")
    recomputed = hmac.new(b"shared-secret", username.encode(), hashlib.sha1).digest()
    import base64

    check(
        password == base64.b64encode(recomputed).decode(),
        "the ephemeral password is base64(HMAC-SHA1(secret, username)) — coturn's use-auth-secret scheme",
    )

    # ChannelData framing, and the discrimination between it and STUN on one socket.
    channel_data = turn.encode_channel_data(0x4001, b"payload-bytes")
    check(turn.decode_channel_data(channel_data) == (0x4001, b"payload-bytes"), "ChannelData round-trips")
    check(turn.parse_message(channel_data) is None, "ChannelData is not mistaken for a STUN message")
    check(turn.decode_channel_data(built) is None, "a STUN message is not mistaken for ChannelData")

    # The RTP packet the probe sends, and the 32-bit sequence it hides in the payload because
    # RTP's own field is 16 bits and wraps every 22 minutes at 50 pps.
    packet = turn.rtp_packet(sequence=70000, timestamp=123456, ssrc=0xDEADBEEF, payload_bytes=160)
    check(len(packet) == 12 + 160, "an RTP probe packet is a 12-byte header plus a 160-byte payload")
    check(packet[0] == 0x80, "RTP version 2, no padding, no extension, no CSRC")
    check(packet[1] == 0x6F, "the payload type is 111 — what WebRTC negotiates for Opus")
    check(turn.probe_sequence(packet) == 70000, "the 32-bit probe sequence survives past RTP's 16-bit wrap")
    check(
        struct.unpack_from("!H", packet, 2)[0] == 70000 & 0xFFFF,
        "RTP's own 16-bit sequence field stays conformant even as the probe counter runs past it",
    )
    check(
        turn.rtp_packet(1, 0, 1)[16:] != turn.rtp_packet(1, 0, 1)[16:],
        "the payload is incompressible, so nothing on the path can make the stream look cheaper",
    )


# =============================================================================================
# D6' §4.1 — the tracker frames
# =============================================================================================


def test_frames() -> None:
    section("D6' §4.1 tracker frames")

    # THE fixed point.  `78 78 05 01 00 01 D9 DC 0D 0A` is GT06's documented login
    # acknowledgement — the one frame in any of these four formats that is attestable against the
    # published protocol rather than against this repository.  It pins the CRC polynomial, the
    # bytes the length field counts, and the span the digest covers, all at once.
    rebuilt = frames.gt06_frame(frames.GT06_LOGIN, b"", 1)
    check(
        rebuilt == frames.GT06_DOCUMENTED_LOGIN_ACK,
        "the GT06 frame builder reproduces the documented login acknowledgement byte for byte",
    )
    check(
        frames.crc16_x25(bytes.fromhex("05010001")) == 0xD9DC,
        "CRC-16/X-25 over the acknowledgement's counted bytes is 0xD9DC",
    )
    # CRC-CCITT over the same bytes gives something else; a builder using it would be refused by
    # every genuine adapter, and this is what makes that a caught error rather than a silent one.
    check(frames.crc16_x25(b"123456789") == 0x906E, "CRC-16/X-25 over the standard check string is 0x906E")

    # Frame geometry: the length byte counts the protocol byte through the CRC.
    login = frames.gt06_login("358899051234567", 1)
    check(login[:2] == b"\x78\x78", "a GT06 frame starts 78 78")
    check(login[-2:] == b"\x0d\x0a", "a GT06 frame ends 0D 0A")
    check(login[2] == len(login) - 5, "the GT06 length byte counts protocol..CRC, excluding start and terminator")
    check(login[3] == frames.GT06_LOGIN, "a login frame carries protocol 0x01")

    # The splitter has to survive the case the downlink measurement depends on: two frames
    # arriving in one TCP read, and a third arriving half-complete.
    status = frames.gt06_status(2)
    parsed, remainder = frames.gt06_split(login + status)
    check(len(parsed) == 2, "two GT06 frames in one buffer are split into two")
    check(parsed[0][0] == frames.GT06_LOGIN and parsed[1][0] == frames.GT06_STATUS, "each keeps its protocol")
    check(parsed[1][1] == 2, "each keeps its serial")
    check(remainder == b"", "a buffer of whole frames leaves no remainder")

    parsed, remainder = frames.gt06_split(login + status[:6])
    check(len(parsed) == 1, "a partial trailing frame is not decoded")
    check(remainder == status[:6], "a partial trailing frame is left in the remainder for the next read")

    # The downlink command frame, read the way the probe reads it off the wire.
    command = frames.gt06_frame(
        frames.GT06_COMMAND,
        bytes([4 + len(b"DWXX#")]) + struct.pack("!I", 7) + b"DWXX#" + struct.pack("!H", 2),
        7,
    )
    decoded, _ = frames.gt06_split(command)
    check(len(decoded) == 1 and decoded[0][0] == frames.GT06_COMMAND, "a 0x80 downlink frame is split out")
    check(
        frames.gt06_command_text(decoded[0][2]) == "DWXX#",
        "the ASCII command text is read back out of the downlink frame — pingNow is DWXX#",
    )

    # JT/T 808: the stuffing is applied AFTER the checksum, and both markers are 0x7E.
    position = frames.jt808_position(
        "358899051234567", 6.9271, 79.8612, datetime(2026, 8, 13, 4, 30, 0, tzinfo=timezone.utc), 30.0, 1
    )
    check(position[0] == frames.JT808_FLAG and position[-1] == frames.JT808_FLAG, "a JT808 frame is 7E-delimited")
    body = frames.jt808_unstuff(position[1:-1])
    check(frames.xor8(body[:-1]) == body[-1], "the XOR-8 checksum covers the unstuffed header and body")
    check(struct.unpack_from("!H", body, 0)[0] == frames.JT808_LOCATION, "the message id is 0x0200")
    check(struct.unpack_from("!H", body, 2)[0] & 0x4000 != 0, "properties bit 14 is set — the 2019 header shape")
    check(
        struct.unpack_from("!H", body, 2)[0] & 0x03FF == 28,
        "the properties field carries the 28-byte body length",
    )

    # 2013's six-byte BCD terminal number cannot hold a fifteen-digit IMEI (C043 finding 3); the
    # 2019 shape's ten bytes can, and that is why this builder uses it.
    check(len(body[5:15]) == 10, "the 2019 terminal number is ten BCD bytes")
    check(body[5:15] == frames.bcd("358899051234567".rjust(20, "0"), 10), "the IMEI is written as packed BCD")

    # Byte stuffing: a frame whose payload contains 0x7E or 0x7D must be escaped, and unstuffing
    # must invert it exactly.  Applying the checksum after stuffing instead is the classic error
    # and produces a frame that decodes nowhere.
    for raw in (b"\x7e", b"\x7d", b"\x7e\x7d\x7e", b"\x00\x7d\x02\x7e"):
        check(
            frames.jt808_unstuff(
                frames.jt808_frame(b"", raw)[1:-1]
            )[:-1] == raw,
            f"stuffing round-trips a payload containing {raw.hex()}",
        )

    # And the splitter, on the reply shape the round-trip measurement correlates against.
    general = frames.jt808_frame(
        frames.jt808_header(frames.JT808_PLATFORM_GENERAL, "358899051234567", 9, 5),
        struct.pack("!HHB", 41, frames.JT808_LOCATION, 0),
    )
    messages, remainder = frames.jt808_split(general)
    check(len(messages) == 1, "a JT808 general response is split out")
    check(messages[0][0] == frames.JT808_PLATFORM_GENERAL, "its message id is 0x8001")
    check(
        messages[0][1] == 41,
        "its body's first field is the serial of the message being answered — the correlation key",
    )
    check(remainder == b"", "a whole JT808 message leaves no remainder")


def main() -> int:
    print("\033[1mC131 acceptance harness — instrument self-test\033[0m")
    print("Nothing here needs a network, a region, a device or a broker.")

    test_emodel()
    test_rtpstats()
    test_turn()
    test_frames()

    print()

    if FAILURES:
        print(f"\033[31m{len(FAILURES)} of {CHECKS} checks failed:\033[0m")
        for failure in FAILURES:
            print(f"  - {failure}")
        print("\nThe instruments are NOT sound. No acceptance figure from this tree may be reported.")
        return 1

    print(f"\033[32m{CHECKS} checks passed.\033[0m The instruments are sound.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
