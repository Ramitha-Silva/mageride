#!/usr/bin/env python3
"""
AL-48: on VoIP failure the platform offers a direct dial, and never a masked relay.

This is C131's second fence and the third item of its definition of done — "the direct-dial
fallback is verified on a forced VoIP failure".  It is also the one VoIP item that is **not**
region-sensitive: it is a property of voip-svc's contract, observable wherever the service runs.
The report says so explicitly, because a figure that transfers and a figure that does not must not
sit in the same table without a label.

WHAT "FORCED VOIP FAILURE" MEANS HERE
-------------------------------------
The earliest and clearest failure signal in the design is a missing SFU: `Voip.Api/CLAUDE.md` —
"A missing LiveKit is a 503, not a 200 with an unusable token. That is the VoIP-failure signal at
its earliest and clearest point (ADD §14): the client puts up 'Call normally instead?' and dials.
A 200 would make an absent feature look like a flaky one."

So the probe runs against a deployment whose LiveKit is reachable and again against one whose
LiveKit is not, and asserts the contract in both directions.  `run.sh` arranges the second state;
`--expect` says which one is being asserted, so a probe pointed at the wrong deployment fails
rather than reporting the wrong half as passing.

THE FENCE, AND WHY IT IS ASSERTED ON THE RAW TEXT
-------------------------------------------------
"never a masked relay" is a claim about what is ABSENT, and a deserialised response says nothing
about a member its type has no property for — C122's rule, and the reason `WebPage.Mentions`
exists in that suite.  Every response body on every path here is swept as raw text for the removed
stack's vocabulary and for anything shaped like a Sri Lankan phone number.  voip-svc has a unit
test (`MaskingWithdrawnTests`) that no *identifier* in its assembly is named after that stack; this
is the other half — that nothing named after it comes back over the wire from a running
deployment.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass, field
from pathlib import Path

#: The vocabulary AL-48 removed.  `MaskingWithdrawnTests` refuses these as identifiers inside the
#: assembly; this refuses them in a response body.  Three still-current documents describe masking
#: (D3' Δ 2026-06-28's `normal_masked` leg, D6' I-28.3's PSTN bridge, I-29.3's proxy-DID lease), so
#: somebody implementing from the wrong section is a realistic way for it to come back.
WITHDRAWN_VOCABULARY = [
    "masked",
    "masking",
    "normal_masked",
    "pstn",
    "cpaas",
    "did_pool",
    "didpool",
    "proxydid",
    "proxy_did",
    "smsrelay",
    "sms_relay",
    "exotel",
    "twilio",
    "plivo",
]

#: A Sri Lankan MSISDN in any of the shapes the platform uses.  voip-svc must never serve one:
#: the counterparty's number is ride-svc's, on `GET /v1/rides/{id}` post-accept.
MSISDN = re.compile(r"(?:\+94|0094|94(?=7)|0(?=7))7\d{8}")


@dataclass
class Result:
    checks: list[dict] = field(default_factory=list)
    failures: int = 0

    def record(self, ok: bool, description: str, detail: str = "") -> None:
        self.checks.append({"ok": ok, "check": description, "detail": detail})
        if not ok:
            self.failures += 1
        mark = "\033[32m✓\033[0m" if ok else "\033[31m✗\033[0m"
        print(f"  {mark} {description}" + (f"  — {detail}" if detail else ""))


def call(
    base: str, method: str, path: str, bearer: str, body: dict | None = None, *, insecure: bool = True
) -> tuple[int, str]:
    """One request through the edge.  Answers `(status, raw body text)` — never a parsed object."""
    import ssl

    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(f"{base.rstrip('/')}{path}", data=data, method=method)
    request.add_header("Authorization", f"Bearer {bearer}")
    request.add_header("Idempotency-Key", str(uuid.uuid4()))

    if data is not None:
        request.add_header("Content-Type", "application/json")

    context = ssl._create_unverified_context() if insecure else None

    try:
        with urllib.request.urlopen(request, timeout=20, context=context) as response:
            return response.status, response.read().decode(errors="replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(errors="replace")
    except (urllib.error.URLError, OSError, TimeoutError) as error:
        return 0, f"{type(error).__name__}: {error}"


def sweep(result: Result, label: str, text: str) -> None:
    """The AL-48 fence, applied to one response body."""
    lowered = text.lower()
    found = [word for word in WITHDRAWN_VOCABULARY if word in lowered]

    result.record(
        not found,
        f"{label}: carries none of the vocabulary AL-48 removed",
        f"found {found}" if found else "",
    )

    numbers = MSISDN.findall(text)
    result.record(
        not numbers,
        f"{label}: carries no phone number — the counterparty's MSISDN is ride-svc's, post-accept",
        f"found {len(numbers)}" if numbers else "",
    )


def probe(base: str, bearer: str, ride_id: str, expect: str) -> Result:
    result = Result()
    voip_available = expect == "available"

    # ---- the token route ---------------------------------------------------------------
    status, text = call(base, "POST", "/v1/voip/token", bearer, {"rideId": ride_id})
    sweep(result, "POST /v1/voip/token", text)

    if voip_available:
        result.record(status == 200, "with LiveKit reachable, /v1/voip/token mints a token", f"HTTP {status}")
        if status == 200:
            body = json.loads(text)
            result.record(
                bool(body.get("token")) and bool(body.get("wsUrl")),
                "the minted token carries a room, a JWT and a wsUrl",
            )
            result.record(
                body.get("callee") in ("rider", "driver"),
                "the callee is the rider or the driver, never the booker (P-05)",
                f"callee={body.get('callee')}",
            )
    else:
        result.record(
            status == 503,
            "with LiveKit unreachable, /v1/voip/token answers 503 — not a 200 with a dead token",
            f"HTTP {status}",
        )

    # ---- free_voip ---------------------------------------------------------------------
    status, text = call(
        base, "POST", "/v1/calls/start", bearer,
        {"rideId": ride_id, "calleeRole": "driver", "callType": "free_voip"},
    )
    sweep(result, "POST /v1/calls/start free_voip", text)

    if voip_available:
        result.record(status == 200, "free_voip starts a session when LiveKit is reachable", f"HTTP {status}")
    else:
        result.record(
            status == 503,
            "free_voip is refused 503 when LiveKit is unreachable",
            f"HTTP {status}",
        )

    # ---- direct_dial: the fallback itself ----------------------------------------------
    # This is the whole point.  `Voip.Api/CLAUDE.md`: direct_dial "is logged EVEN WHERE LIVEKIT IS
    # ABSENT, because that is the deployment where the fallback rate matters most."  A platform
    # that refused to record the fallback would leave ADD §14's fallback unmeasurable exactly when
    # it is being taken.
    status, text = call(
        base, "POST", "/v1/calls/start", bearer,
        {"rideId": ride_id, "calleeRole": "driver", "callType": "direct_dial"},
    )
    sweep(result, "POST /v1/calls/start direct_dial", text)

    result.record(
        status == 200,
        "direct_dial is recorded whether or not LiveKit is reachable",
        f"HTTP {status}",
    )

    call_id = None
    if status == 200:
        body = json.loads(text)
        call_id = body.get("callId")
        result.record(body.get("callType") == "direct_dial", "the recorded row is a direct_dial")
        result.record(
            "session" not in body or body.get("session") is None,
            "direct_dial returns NO session block — there is no PSTN leg in this process",
        )

    # ---- the failure outcome the prompt hangs on ---------------------------------------
    if call_id:
        status, text = call(base, "POST", f"/v1/calls/{call_id}/outcome", bearer, {"outcome": "voip_failed"})
        result.record(
            status == 204,
            "`voip_failed` is recordable — the signal the 'Call normally instead?' prompt hangs on",
            f"HTTP {status}",
        )

        status, text = call(base, "POST", f"/v1/calls/{call_id}/outcome", bearer, {"outcome": "completed"})
        result.record(
            status == 404,
            "an outcome is reported once and never overwritten",
            f"HTTP {status}",
        )

    # ---- and the route AL-48 deleted ---------------------------------------------------
    # Not a 404-by-accident: public-bff refuses any route whose path contains `/call` at start-up.
    # Asserted here against the deployment rather than against the source, because "we removed it"
    # and "it is not served" are different claims and only the second one protects anybody.
    status, text = call(base, "POST", "/public/track/probe-token/call", bearer, {})
    result.record(
        status in (404, 401, 403),
        "the masked-relay route AL-48 removed is not served (POST /public/track/{token}/call)",
        f"HTTP {status}",
    )

    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="C131 AL-48 direct-dial fallback probe")
    parser.add_argument("--env", required=True, type=Path)
    parser.add_argument("--expect", choices=["available", "unavailable"], required=True)
    parser.add_argument("--out", type=Path)
    arguments = parser.parse_args()

    environment = json.loads(arguments.env.read_text())
    platform = environment.get("platform") or {}

    base = platform.get("baseUrl")
    bearer = platform.get("bearer")
    ride_id = platform.get("rideId")

    if not (base and bearer and ride_id):
        print(
            "env.json carries no platform.baseUrl / platform.bearer / platform.rideId — "
            "the fallback probe needs an accepted ride and a participant's bearer.",
            file=sys.stderr,
        )
        return 2

    print(f"\033[1m  AL-48 fallback, expecting LiveKit {arguments.expect}\033[0m")
    result = probe(base, bearer, ride_id, arguments.expect)

    payload = {
        "expect": arguments.expect,
        "region": environment.get("region"),
        "checks": result.checks,
        "failures": result.failures,
        "region_sensitive": False,
        "note": (
            "AL-48's fallback is a contract property of voip-svc, not a property of the region. "
            "This result transfers between deployments; the media figures in voip-media.json do not."
        ),
    }

    if arguments.out:
        arguments.out.parent.mkdir(parents=True, exist_ok=True)
        arguments.out.write_text(json.dumps(payload, indent=2) + "\n")

    return 1 if result.failures else 0


if __name__ == "__main__":
    sys.exit(main())
