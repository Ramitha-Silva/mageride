#!/usr/bin/env python3
"""
Hardware-tracker round-trip acceptance: Sri Lankan device, Singapore ingest.

WHAT IS MEASURED, AND WHY THE TWO FAMILIES MEASURE DIFFERENT FRAMES
-------------------------------------------------------------------
`tcp-adapter` answers different frames in each family, and that is a property of the protocols
rather than a choice made here (see `lib/frames.py`):

    GT06 login (0x01)      -> acknowledged.  SESSION ESTABLISHMENT round trip: it includes the
                              IMEI resolution through provisioning-svc, so it is the slowest and
                              the one a device pays on every reconnect — which on a moving vehicle
                              with intermittent coverage is often.
    GT06 status (0x13)     -> acknowledged.  STEADY-STATE round trip on an established session.
    GT06 location (0x12)   -> NOT acknowledged.  "The protocol does not ask for one and some
                              firmware drops the session on an unexpected reply" (Gt06Codec).
                              There is no round trip to measure here and the probe does not invent
                              one.
    JT/T 808 0x0200        -> answered with a platform general response (0x8001).  A POSITION
                              report's own round trip, which is the closest reading of this
                              component's words, and the reason both families are driven.

Each is reported separately.  Averaging a heartbeat round trip with a position round trip would
produce a number describing neither.

THE DOWNLINK, AND THE PRODUCER THAT IS NOT THERE
------------------------------------------------
"Downlink command latency" is the second half of the deliverable, and measuring it end to end
means causing the platform to emit a command.  In this build almost nothing does:

    * `tcp-adapter` SUBSCRIBES `veh/+/cmd` and translates all five commands (`DownlinkRouter`).
    * The ONLY producer anywhere in the platform is `trip-state-svc`'s `CadencePublisher`, which
      pushes a `setPosRate` hint on a Mode A/B session transition (D5' §5.2, R-07).
    * It is behind `TripState:PublishCadenceHints`, which **defaults to false and is set in no
      environment file, no compose file and no k8s overlay** — the same shape as C130's finding
      about `Dispatch__LastWillEnabled`.
    * `pingNow`, `reboot` and `setGeofence` have no producer at all.

So there are two measurable paths and they are not the same measurement:

    --downlink platform   Start/End a Mode A/B journey through the API and time until the
                          TIMER,{n}# frame lands on the device socket.  This is the WHOLE path —
                          Colombo -> Singapore (trip-state-svc) -> EMQX -> tcp-adapter ->
                          Colombo — and it needs `TripState:PublishCadenceHints` on.
    --downlink broker     Publish the envelope onto `veh/{vehicleId}/cmd` directly and time the
                          same arrival.  This covers the BROKER-AND-ADAPTER leg only; it omits
                          the API hop and the service that would have decided to send it.  It is
                          the fallback for a deployment where the producer is off, and every
                          figure it produces is labelled `leg: broker-to-device` so it can never
                          be read as the end-to-end number.

The `broker` path publishes on a topic a platform service owns, which is a stand-in for a
component rather than for the outside world — the line `tests/E2E`'s fence draws.  It is gated
behind an explicit flag and labelled in the output for exactly that reason.
"""

from __future__ import annotations

import argparse
import json
import selectors
import socket
import ssl
import statistics
import struct
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from lib import frames  # noqa: E402

#: D6' §4.1's family order, positional — the same order `Adapter:Ports` carries.
PORT_GT06 = 5023
PORT_JT808 = 5024

#: Colombo Fort, and a step small enough that repeated fixes stay under every ADD §12.6 ceiling.
COLOMBO = (6.9271, 79.8612)


def percentile(values: list[float], p: float) -> float:
    """Nearest rank, matching `rtpstats.StreamStats.rtt_percentile_ms`."""
    if not values:
        return 0.0
    ordered = sorted(values)
    rank = max(1, min(len(ordered), int(round(p / 100.0 * len(ordered) + 0.5))))
    return ordered[rank - 1]


def summarise(name: str, samples: list[float], detail: dict | None = None) -> dict:
    result = {
        "measurement": name,
        "samples": len(samples),
        "region_sensitive": True,
    }

    if samples:
        result |= {
            "rtt_mean_ms": round(statistics.fmean(samples), 3),
            "rtt_p50_ms": round(percentile(samples, 50), 3),
            "rtt_p95_ms": round(percentile(samples, 95), 3),
            "rtt_p99_ms": round(percentile(samples, 99), 3),
            "rtt_min_ms": round(min(samples), 3),
            "rtt_max_ms": round(max(samples), 3),
        }
    else:
        result["verdict"] = "no reply was received — nothing was measured"

    return result | (detail or {})


# =============================================================================================
# GT06
# =============================================================================================


def probe_gt06(host: str, port: int, imei: str, rounds: int, timeout: float) -> list[dict]:
    """Login RTT once, then `rounds` status RTTs on the established session."""
    results: list[dict] = []
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    sock.settimeout(timeout)

    try:
        connect_started = time.monotonic()
        sock.connect((host, port))
        connect_ms = (time.monotonic() - connect_started) * 1000.0

        serial = 1
        buffer = b""

        # --- login ---------------------------------------------------------------------
        started = time.monotonic()
        sock.sendall(frames.gt06_login(imei, serial))
        login_ms: list[float] = []

        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                chunk = sock.recv(1024)
            except (TimeoutError, socket.timeout):
                break
            if not chunk:
                break
            buffer += chunk
            parsed, buffer = frames.gt06_split(buffer)
            matched = [f for f in parsed if f[0] == frames.GT06_LOGIN]
            if matched:
                login_ms.append((time.monotonic() - started) * 1000.0)
                break

        results.append(
            summarise(
                "gt06_login_rtt",
                login_ms,
                {
                    "note": (
                        "session establishment: includes the IMEI resolution through "
                        "provisioning-svc, so it is what a device pays on every reconnect"
                    ),
                    "tcp_connect_ms": round(connect_ms, 3),
                },
            )
        )

        if not login_ms:
            results.append(
                summarise(
                    "gt06_status_rtt",
                    [],
                    {"verdict": "the login was never acknowledged — the binding did not resolve"},
                )
            )
            return results

        # --- status heartbeats ---------------------------------------------------------
        status_ms: list[float] = []

        for _ in range(rounds):
            serial += 1
            started = time.monotonic()
            sock.sendall(frames.gt06_status(serial))

            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                try:
                    chunk = sock.recv(1024)
                except (TimeoutError, socket.timeout):
                    break
                if not chunk:
                    break
                buffer += chunk
                parsed, buffer = frames.gt06_split(buffer)
                matched = [f for f in parsed if f[0] == frames.GT06_STATUS and f[1] == serial]
                if matched:
                    status_ms.append((time.monotonic() - started) * 1000.0)
                    break

            time.sleep(0.2)

        results.append(
            summarise(
                "gt06_status_rtt",
                status_ms,
                {"note": "steady-state heartbeat round trip on an established session"},
            )
        )

        # GT06 location frames are deliberately unacknowledged; recorded as a ledger entry so
        # the gap is visible rather than looking like a measurement somebody forgot.
        results.append(
            {
                "measurement": "gt06_location_rtt",
                "samples": 0,
                "verdict": "not measurable by design",
                "note": (
                    "GT06 does not acknowledge a location frame (0x12) — the protocol does not "
                    "ask for one and some firmware drops the session on an unexpected reply. "
                    "JT/T 808's 0x0200 is the position report that does have a round trip."
                ),
            }
        )
    except OSError as error:
        results.append(
            summarise("gt06_login_rtt", [], {"verdict": f"{type(error).__name__}: {error}"})
        )
    finally:
        sock.close()

    return results


# =============================================================================================
# JT/T 808
# =============================================================================================


def probe_jt808(host: str, port: int, imei: str, rounds: int, timeout: float) -> list[dict]:
    """Position-report round trips: 0x0200 out, 0x8001 back, correlated on the serial."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    sock.settimeout(timeout)
    samples: list[float] = []

    try:
        sock.connect((host, port))
        buffer = b""
        serial = 0

        for step in range(rounds):
            serial += 1
            # A closed orbit at ~10 m a step: 36 km/h over the probe's own cadence, under every
            # ADD §12.6 ceiling.  A refused sample never becomes the position the next one is
            # measured against, so one over-long step would poison the rest of the track — C129's
            # rule, and it applies to any probe that moves a vehicle.
            latitude = COLOMBO[0] + 0.00009 * (step % 12)
            longitude = COLOMBO[1] + 0.00009 * ((step // 12) % 12)

            from datetime import datetime, timezone

            started = time.monotonic()
            sock.sendall(
                frames.jt808_position(
                    imei, latitude, longitude, datetime.now(timezone.utc), 30.0, serial
                )
            )

            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                try:
                    chunk = sock.recv(2048)
                except (TimeoutError, socket.timeout):
                    break
                if not chunk:
                    break
                buffer += chunk
                messages, buffer = frames.jt808_split(buffer)
                matched = [
                    m
                    for m in messages
                    if m[0] == frames.JT808_PLATFORM_GENERAL and m[1] == serial
                ]
                if matched:
                    samples.append((time.monotonic() - started) * 1000.0)
                    break

            time.sleep(0.2)
    except OSError as error:
        return [summarise("jt808_position_rtt", [], {"verdict": f"{type(error).__name__}: {error}"})]
    finally:
        sock.close()

    return [
        summarise(
            "jt808_position_rtt",
            samples,
            {
                "note": (
                    "a POSITION report's own round trip — 0x0200 out, 0x8001 platform general "
                    "response back, correlated on the message serial"
                )
            },
        )
    ]


# =============================================================================================
# The downlink
# =============================================================================================


def mqtt_publish(host: str, port: int, username: str, password: str, topic: str, payload: bytes) -> None:
    """
    A minimal MQTT 3.1.1 CONNECT + PUBLISH (QoS 0) over TLS.

    Enough of a client to put one message on one topic and no more.  Used only by the `broker`
    downlink path, which is labelled `leg: broker-to-device` wherever it appears.
    """
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE

    raw = socket.create_connection((host, port), timeout=10)
    sock = context.wrap_socket(raw, server_hostname=host)

    try:

        def string(value: bytes) -> bytes:
            return struct.pack("!H", len(value)) + value

        client_id = f"c131-{uuid.uuid4().hex[:12]}".encode()
        body = (
            string(b"MQTT")
            + bytes([4])          # protocol level 4 = MQTT 3.1.1
            + bytes([0xC2])       # username + password + clean session
            + struct.pack("!H", 30)
            + string(client_id)
            + string(username.encode())
            + string(password.encode())
        )
        sock.sendall(bytes([0x10]) + remaining_length(len(body)) + body)

        if sock.recv(4)[:1] != b"\x20":
            raise OSError("the broker refused the CONNECT")

        publish = string(topic.encode()) + payload
        sock.sendall(bytes([0x30]) + remaining_length(len(publish)) + publish)
    finally:
        sock.close()


def remaining_length(length: int) -> bytes:
    out = bytearray()
    while True:
        byte = length % 128
        length //= 128
        out.append(byte | (0x80 if length else 0x00))
        if not length:
            return bytes(out)


def probe_downlink(
    host: str,
    imei: str,
    timeout: float,
    *,
    mode: str,
    vehicle_id: str | None,
    mqtt: dict | None,
    platform: dict | None,
) -> dict:
    """
    Times a downlink command from its trigger to the frame landing on the device's own socket.

    The device is a live GT06 session, because a downlink is written onto an OPEN socket
    (`SessionRegistry`) — a command for a vehicle whose device is not connected is dropped and
    counted, not queued.
    """
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    sock.settimeout(timeout)

    try:
        sock.connect((host, PORT_GT06))
        sock.sendall(frames.gt06_login(imei, 1))

        buffer = b""
        deadline = time.monotonic() + timeout
        logged_in = False

        while time.monotonic() < deadline and not logged_in:
            chunk = sock.recv(1024)
            if not chunk:
                break
            buffer += chunk
            parsed, buffer = frames.gt06_split(buffer)
            logged_in = any(f[0] == frames.GT06_LOGIN for f in parsed)

        if not logged_in:
            return {
                "measurement": "downlink_command_latency",
                "verdict": "the device never logged in — no socket for a command to be written to",
            }

        # --- fire the trigger ----------------------------------------------------------
        started = time.monotonic()
        leg = "end-to-end"

        if mode == "platform":
            if not platform:
                return {
                    "measurement": "downlink_command_latency",
                    "verdict": "--downlink platform needs platform.baseUrl / bearer / sessionVehicleId",
                }
            trigger_session_transition(platform)
        elif mode == "broker":
            if not (mqtt and vehicle_id):
                return {
                    "measurement": "downlink_command_latency",
                    "verdict": "--downlink broker needs mqtt.{host,port,username,password} and a vehicleId",
                }
            leg = "broker-to-device"
            envelope = json.dumps(
                {
                    "cmd": "setPosRate",
                    "args": {"seconds": 10},
                    "expiresAt": time.strftime(
                        "%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + 300)
                    ),
                }
            ).encode()
            mqtt_publish(
                mqtt["host"], int(mqtt.get("port", 8883)),
                mqtt["username"], mqtt["password"],
                f"veh/{vehicle_id}/cmd", envelope,
            )
        else:
            return {"measurement": "downlink_command_latency", "verdict": f"unknown mode {mode}"}

        # --- wait for the translated frame ---------------------------------------------
        deadline = time.monotonic() + timeout
        selector = selectors.DefaultSelector()
        selector.register(sock, selectors.EVENT_READ)

        while time.monotonic() < deadline:
            if not selector.select(timeout=0.05):
                continue
            try:
                chunk = sock.recv(1024)
            except (TimeoutError, socket.timeout):
                continue
            if not chunk:
                break
            buffer += chunk
            parsed, buffer = frames.gt06_split(buffer)

            for protocol, _, content in parsed:
                if protocol != frames.GT06_COMMAND:
                    continue

                text = frames.gt06_command_text(content)
                latency = (time.monotonic() - started) * 1000.0

                return {
                    "measurement": "downlink_command_latency",
                    "leg": leg,
                    "latency_ms": round(latency, 3),
                    "command_text": text,
                    "expected_text_prefix": "TIMER,",
                    "matched": bool(text and text.startswith("TIMER,")),
                    "region_sensitive": True,
                    "note": (
                        "end-to-end: API hop -> trip-state-svc -> EMQX -> tcp-adapter -> device"
                        if leg == "end-to-end"
                        else "BROKER-AND-ADAPTER LEG ONLY — the API hop and the service that "
                        "would have decided to send it are not in this figure"
                    ),
                }

        return {
            "measurement": "downlink_command_latency",
            "leg": leg,
            "verdict": f"no command frame arrived within {timeout:.0f}s of the trigger",
            "likely_cause": (
                "TripState:PublishCadenceHints defaults to false and is set in no environment "
                "file, no compose file and no k8s overlay — so the only producer of veh/+/cmd "
                "in the platform is off"
                if leg == "end-to-end"
                else "Adapter:DownlinkEnabled off, the device on another pod, or the command expired"
            ),
        }
    except OSError as error:
        return {
            "measurement": "downlink_command_latency",
            "verdict": f"{type(error).__name__}: {error}",
        }
    finally:
        sock.close()


def trigger_session_transition(platform: dict) -> None:
    """Starts a Mode A/B journey, which is what makes `CadencePublisher` push a `setPosRate`."""
    context = ssl._create_unverified_context()
    request = urllib.request.Request(
        f"{platform['baseUrl'].rstrip('/')}/v1/sessions/start",
        data=json.dumps({"vehicleId": platform["sessionVehicleId"]}).encode(),
        method="POST",
    )
    request.add_header("Authorization", f"Bearer {platform['bearer']}")
    request.add_header("Content-Type", "application/json")
    request.add_header("Idempotency-Key", str(uuid.uuid4()))

    try:
        urllib.request.urlopen(request, timeout=20, context=context).read()
    except urllib.error.HTTPError as error:
        # A 409 is a session already open, which still counts as arriving; anything else is
        # reported by the absence of a frame rather than swallowed here.
        error.read()


def main() -> int:
    parser = argparse.ArgumentParser(description="C131 tracker round-trip acceptance probe")
    parser.add_argument("--env", required=True, type=Path)
    parser.add_argument("--rounds", type=int, default=20)
    parser.add_argument("--timeout", type=float, default=10.0)
    parser.add_argument("--downlink", choices=["platform", "broker", "skip"], default="platform")
    parser.add_argument("--out", type=Path)
    arguments = parser.parse_args()

    environment = json.loads(arguments.env.read_text())
    tracker = environment.get("tracker") or {}

    host = tracker.get("host")
    if not host:
        print("env.json carries no tracker.host — nothing to probe.", file=sys.stderr)
        return 2

    results: list[dict] = []

    print(f"  GT06 against {host}:{PORT_GT06} …", flush=True)
    results += probe_gt06(host, PORT_GT06, tracker["gt06Imei"], arguments.rounds, arguments.timeout)

    print(f"  JT/T 808 against {host}:{PORT_JT808} …", flush=True)
    results += probe_jt808(host, PORT_JT808, tracker["jt808Imei"], arguments.rounds, arguments.timeout)

    if arguments.downlink != "skip":
        print(f"  downlink ({arguments.downlink}) …", flush=True)
        results.append(
            probe_downlink(
                host,
                tracker["gt06Imei"],
                arguments.timeout,
                mode=arguments.downlink,
                vehicle_id=tracker.get("vehicleId"),
                mqtt=environment.get("mqtt"),
                platform=environment.get("platform"),
            )
        )

    payload = {
        "region": environment.get("region"),
        "target": {"host": host, "gt06": PORT_GT06, "jt808": PORT_JT808},
        "client_location": tracker.get("clientLocation", "UNDECLARED"),
        "measurements": results,
    }

    text = json.dumps(payload, indent=2)

    if arguments.out:
        arguments.out.parent.mkdir(parents=True, exist_ok=True)
        arguments.out.write_text(text + "\n")
        print(f"  wrote {arguments.out}")
    else:
        print(text)

    measured = [r for r in results if r.get("samples") or r.get("latency_ms")]

    return 0 if measured else 1


if __name__ == "__main__":
    sys.exit(main())
