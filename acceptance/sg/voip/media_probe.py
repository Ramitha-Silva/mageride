#!/usr/bin/env python3
"""
VoIP media acceptance: MOS, jitter, packet loss and TURN relay behaviour at concurrency.

WHAT IT DRIVES
--------------
One "call" is TWO TURN allocations on the platform's own relay — A and B — each with a channel
bound to the other's relayed address.  A streams 20 ms RTP at 50 pps to B through the relay and B
echoes each packet straight back.  Both sides relayed is the realistic worst case and the common
one: `turnserver.conf`'s own header calls symmetric NAT "a large minority" of Sri Lankan mobile
handsets and `specs/lightweight-production-replica.md` calls it "the common case on Sri Lankan
mobile carriers".  It is also the only topology that needs no assumption about the probe host's
own NAT — every packet rides a flow the client itself opened towards the relay.

    A --ChannelData--> [relay] --ChannelData--> B          one-way call path
    B --ChannelData--> [relay] --ChannelData--> A          the echo

The echo is two traversals of the one-way call path, so the one-way network delay is HALF the
measured RTT.  `rtpstats.relayed_call_delay_ms` is where that factor lives and where it is argued.

WHAT IT CANNOT SEE, AND SAYS SO
-------------------------------
* **Which fraction of real calls end up relayed.**  This probe relays unconditionally; it measures
  what a relayed call costs, not how many there are.  The share is coturn's own counter and is
  read by `collect.sh` from the server side, because only the server sees the calls that never
  allocated.
* **The handset's jitter buffer.**  Modelled, not measured — see `rtpstats.jitter_buffer_ms`.
* **Routing asymmetry.**  Unmeasurable from one clock.
* **Anything about the SFU.**  LiveKit's own media path is `signalling_probe.py`'s.

Usage:
    python3 media_probe.py --env env.json --calls 50 --seconds 30 --out out/voip-media.json
"""

from __future__ import annotations

import argparse
import json
import math
import selectors
import statistics
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from lib import emodel, rtpstats, turn  # noqa: E402

#: 20 ms packets, which is Opus's frame size in WebRTC and what `rtpstats.PACKET_MS` assumes.
PACKETS_PER_SECOND = 50

#: coturn's channel numbers must be in 0x4000-0x7FFE and unique per allocation.
CHANNEL_BASE = turn.CHANNEL_MIN


@dataclass
class CallProbe:
    """One relayed call: two allocations, a channel each way, and the statistics for the A leg."""

    index: int
    a: turn.TurnAllocation
    b: turn.TurnAllocation
    accumulator: rtpstats.StreamAccumulator = field(default_factory=rtpstats.StreamAccumulator)
    ssrc: int = 0
    sequence: int = 0
    established: bool = False
    failure: str | None = None


def establish(host: str, port: int, secret: str, index: int, timeout: float) -> CallProbe:
    """
    Brings up one call's two allocations and cross-binds their channels.

    **Each allocation takes its own credential name.**  coturn's `user-quota` counts allocations
    per *username*, and under `use-auth-secret` the username is an expiry timestamp — so every
    client that mints a credential in the same second shares one quota.  A per-participant name is
    what a correct deployment does; using a shared one here would have the probe measure
    `user-quota` rather than the relay.  The report records that as a live risk for whatever
    actually mints these credentials in production.
    """
    a = turn.TurnAllocation(host, port, secret, timeout=timeout)
    b = turn.TurnAllocation(host, port, secret, timeout=timeout)

    probe = CallProbe(index=index, a=a, b=b, ssrc=0xC1310000 + index)

    try:
        a.username, a.password = turn.ephemeral_credential(secret, name=f"c131-a{index}")
        b.username, b.password = turn.ephemeral_credential(secret, name=f"c131-b{index}")

        relayed_a = a.allocate()
        relayed_b = b.allocate()

        a.create_permission(*relayed_b)
        b.create_permission(*relayed_a)

        channel = CHANNEL_BASE + (index % 0x3FFE)
        a.bind_channel(*relayed_b, channel)
        b.bind_channel(*relayed_a, channel)

        probe.established = True
    except (turn.TurnError, OSError) as error:
        probe.failure = f"{type(error).__name__}: {error}"

    return probe


def run(
    host: str,
    port: int,
    secret: str,
    calls: int,
    seconds: float,
    timeout: float,
) -> dict:
    print(f"  establishing {calls} relayed calls against {host}:{port} …", flush=True)

    probes: list[CallProbe] = []
    setup_started = time.monotonic()

    for index in range(calls):
        probes.append(establish(host, port, secret, index, timeout))

    setup_seconds = time.monotonic() - setup_started
    live = [probe for probe in probes if probe.established]

    if not live:
        reasons = sorted({probe.failure or "unknown" for probe in probes})
        return {
            "established": 0,
            "requested": calls,
            "failures": reasons,
            "verdict": "no allocation succeeded — nothing was measured",
        }

    print(
        f"  {len(live)}/{calls} calls established in {setup_seconds:.1f}s; "
        f"streaming {PACKETS_PER_SECOND} pps for {seconds:.0f}s …",
        flush=True,
    )

    selector = selectors.DefaultSelector()
    by_socket: dict[int, tuple[CallProbe, str]] = {}

    for probe in live:
        selector.register(probe.a.socket, selectors.EVENT_READ)
        selector.register(probe.b.socket, selectors.EVENT_READ)
        by_socket[probe.a.socket.fileno()] = (probe, "a")
        by_socket[probe.b.socket.fileno()] = (probe, "b")
        probe.a.socket.setblocking(False)
        probe.b.socket.setblocking(False)

    started = time.monotonic()
    deadline = started + seconds
    owed = 0.0
    last_tick = started

    # Catch-up scheduling rather than a fixed sleep, for C129's reason: a timer that drifts turns
    # the harness's own scheduling shortfall into what reads as the platform failing to keep up.
    # The number of packets owed is derived from elapsed time, so a shortfall shows up as a send
    # the probe could not make rather than as loss the relay did not cause.
    while True:
        now = time.monotonic()

        if now >= deadline:
            break

        owed += (now - last_tick) * PACKETS_PER_SECOND
        last_tick = now

        while owed >= 1.0:
            owed -= 1.0
            for probe in live:
                packet = turn.rtp_packet(
                    probe.sequence, int(now * 48000) & 0xFFFFFFFF, probe.ssrc
                )
                try:
                    probe.a.send(packet)
                    probe.accumulator.on_send(probe.sequence, time.monotonic())
                except OSError:
                    pass
                probe.sequence += 1

        for key, _ in selector.select(timeout=0.002):
            probe, side = by_socket[key.fd]
            sock = key.fileobj

            while True:
                try:
                    datagram, _ = sock.recvfrom(2048)
                except (BlockingIOError, OSError):
                    break

                decoded = turn.decode_channel_data(datagram)

                if decoded is None:
                    continue

                _, payload = decoded

                if side == "b":
                    # B is the far handset: it echoes whatever arrives straight back through its
                    # own channel, which is the second traversal of the call path.
                    try:
                        probe.b.send(payload)
                    except OSError:
                        pass
                else:
                    sequence = turn.probe_sequence(payload)
                    if sequence is not None:
                        probe.accumulator.on_receive(sequence, time.monotonic())

    # Drain: packets in flight when the window closed are not losses.  C130's flood drill reported
    # a platform failing R-09 from exactly this — its own shutdown discarding five in-flight
    # acknowledgements.  Any probe that counts what came back needs the gap.
    drain_until = time.monotonic() + 2.0
    while time.monotonic() < drain_until:
        for key, _ in selector.select(timeout=0.05):
            probe, side = by_socket[key.fd]
            try:
                datagram, _ = key.fileobj.recvfrom(2048)
            except (BlockingIOError, OSError):
                continue
            decoded = turn.decode_channel_data(datagram)
            if decoded is None:
                continue
            _, payload = decoded
            if side == "b":
                try:
                    probe.b.send(payload)
                except OSError:
                    pass
            else:
                sequence = turn.probe_sequence(payload)
                if sequence is not None:
                    probe.accumulator.on_receive(sequence, time.monotonic())

    selector.close()

    per_call = []
    for probe in live:
        stats = probe.accumulator.finish()

        if not stats.rtt_ms:
            per_call.append({"call": probe.index, "verdict": "no packet returned", "sent": stats.sent})
            continue

        total_delay, terms = rtpstats.relayed_call_delay_ms(stats.rtt_percentile_ms(95), stats.jitter_ms)
        rating = emodel.rate(total_delay, stats.loss_percent, stats.burst_ratio)

        per_call.append(
            {
                "call": probe.index,
                "sent": stats.sent,
                "received": stats.received,
                "lost": stats.lost,
                "loss_percent": round(stats.loss_percent, 4),
                "duplicated": stats.duplicated,
                "reordered": stats.reordered,
                "jitter_ms": round(stats.jitter_ms, 3),
                "burst_ratio": round(stats.burst_ratio, 3),
                "rtt_mean_ms": round(stats.rtt_mean_ms, 3),
                "rtt_p50_ms": round(stats.rtt_percentile_ms(50), 3),
                "rtt_p95_ms": round(stats.rtt_percentile_ms(95), 3),
                "rtt_p99_ms": round(stats.rtt_percentile_ms(99), 3),
                "delay_terms_ms": {name: round(value, 3) for name, value in terms.items()},
                "emodel": rating.as_dict(),
            }
        )

    for probe in probes:
        probe.a.close()
        probe.b.close()

    measured = [call for call in per_call if "emodel" in call]

    summary = {
        "requested_calls": calls,
        "established_calls": len(live),
        "measured_calls": len(measured),
        "setup_seconds": round(setup_seconds, 2),
        "stream_seconds": round(seconds, 1),
        "packets_per_second_per_call": PACKETS_PER_SECOND,
        "establish_failures": sorted({p.failure for p in probes if p.failure} or set()),
        "codec_note": emodel.CODEC_NOTE,
        "advantage_factor": emodel.A_DEFAULT,
    }

    if measured:
        mos = [call["emodel"]["MOS_CQE"] for call in measured]
        loss = [call["loss_percent"] for call in measured]
        jitter = [call["jitter_ms"] for call in measured]
        rtt95 = [call["rtt_p95_ms"] for call in measured]

        summary |= {
            "mos_mean": round(statistics.fmean(mos), 3),
            "mos_min": round(min(mos), 3),
            "mos_p05": round(sorted(mos)[max(0, math.ceil(0.05 * len(mos)) - 1)], 3),
            "loss_percent_mean": round(statistics.fmean(loss), 4),
            "loss_percent_max": round(max(loss), 4),
            "jitter_ms_mean": round(statistics.fmean(jitter), 3),
            "jitter_ms_max": round(max(jitter), 3),
            "rtt_p95_ms_mean": round(statistics.fmean(rtt95), 3),
            "rtt_p95_ms_max": round(max(rtt95), 3),
            "one_way_network_ms_mean": round(statistics.fmean(rtt95) / 2.0, 3),
        }

    return {"summary": summary, "calls": per_call}


def main() -> int:
    parser = argparse.ArgumentParser(description="C131 VoIP media acceptance probe")
    parser.add_argument("--env", required=True, type=Path)
    parser.add_argument("--calls", type=int, default=50)
    parser.add_argument("--seconds", type=float, default=30.0)
    parser.add_argument("--timeout", type=float, default=5.0)
    parser.add_argument("--out", type=Path)
    arguments = parser.parse_args()

    environment = json.loads(arguments.env.read_text())
    turn_config = environment.get("turn") or {}

    host = turn_config.get("host")
    secret = turn_config.get("secret")

    if not host or not secret:
        print("env.json carries no turn.host / turn.secret — nothing to probe.", file=sys.stderr)
        return 2

    result = run(
        host,
        int(turn_config.get("port", 3478)),
        secret,
        arguments.calls,
        arguments.seconds,
        arguments.timeout,
    )
    result["target"] = {"host": host, "port": int(turn_config.get("port", 3478))}
    result["region"] = environment.get("region")

    text = json.dumps(result, indent=2)

    if arguments.out:
        arguments.out.parent.mkdir(parents=True, exist_ok=True)
        arguments.out.write_text(text + "\n")
        print(f"  wrote {arguments.out}")
    else:
        print(text)

    return 0 if result.get("summary", {}).get("measured_calls") else 1


if __name__ == "__main__":
    sys.exit(main())
