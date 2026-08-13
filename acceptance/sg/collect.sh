#!/usr/bin/env bash
# =====================================================================================
# acceptance/sg/collect.sh — the server's own view of the run.
#
#   bash acceptance/sg/collect.sh --env acceptance/sg/env.json --out acceptance/sg/out/server-side.json
#
# C129's rule, and the reason its central finding exists at all: **the client's view is
# never the only view.** k6 reported "99.5 % of target, 0 broker errors" while EMQX was
# discarding nine samples in ten, because every publish had been PUBACKed before the
# discard. The equivalent here is a media plane that answers every allocation and relays
# nothing, or one that relays for the probe while no real handset ever gets a relay
# candidate to try.
#
# THE ONE NUMBER ONLY THE SERVER HAS
# ------------------------------------------------------------------------------------
# **TURN relay share.** `voip/media_probe.py` allocates unconditionally, so it measures what
# a relayed call COSTS and can say nothing about how many calls are relayed. Only coturn
# knows how many allocations real handsets made, and only LiveKit knows how many sessions
# there were to compare them against. The share is
#
#     relayed sessions / total sessions
#
# and both halves come from here. A share of zero after a real pilot is not "peer-to-peer
# worked" — it is also exactly what C131-01 looks like, where no client is ever told the
# relay exists.
# =====================================================================================
set -uo pipefail

ENV_FILE=""
OUT=""

while [ $# -gt 0 ]; do
  case "$1" in
    --env) ENV_FILE="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    *) printf 'unknown argument: %s\n' "$1" >&2; exit 2 ;;
  esac
done

[ -f "$ENV_FILE" ] || { printf 'no env file at %s\n' "$ENV_FILE" >&2; exit 2; }

jqr() { jq -r "$1 // empty" "$ENV_FILE"; }

MEDIA_HOST="$(jqr '.turn.host')"
SSH_USER="${MEDIA_SSH_USER:-root}"
REMOTE_DIR="/opt/mageride-media"

remote() { ssh -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 "$SSH_USER@$MEDIA_HOST" "$@" 2>/dev/null; }

# coturn's allocation counters come out of its log: `simple-log` writes one line per
# allocation and one per session close. There is no admin API to ask, and `no-cli` is set on
# purpose — the CLI is a credentialled surface on a container reachable from the internet.
allocations="$(remote "docker logs --tail 20000 mageride-media-sg-coturn-1 2>&1 | grep -c 'allocation'" || echo "")"
sessions="$(remote "docker logs --tail 20000 mageride-media-sg-coturn-1 2>&1 | grep -c 'session .* closed'" || echo "")"
livekit_up="$(remote "curl -fsS -o /dev/null -w '%{http_code}' http://127.0.0.1:7880/" || echo "")"

# The question C131-01 turns on, asked of the deployed file rather than of the repository:
# does the SFU actually declare an external TURN server to its clients?
advertises_turn="$(remote "grep -c 'turn_servers' $REMOTE_DIR/livekit.sg.yaml" || echo "")"
tls_listener="$(remote "docker logs --tail 500 mageride-media-sg-coturn-1 2>&1 | grep -ci 'cannot start TLS'" || echo "")"

python3 - "$allocations" "$sessions" "$livekit_up" "$advertises_turn" "$tls_listener" "${OUT:-}" <<'PY'
import json, sys

allocations, sessions, livekit, advertises, tls_failed, out = sys.argv[1:7]

def number(value):
    try:
        return int(value)
    except (TypeError, ValueError):
        return None

payload = {
    "source": "the media host itself, not the probe",
    "coturn_allocations_seen": number(allocations),
    "coturn_sessions_closed": number(sessions),
    "livekit_signalling_http": livekit or None,
    "livekit_declares_external_turn": (number(advertises) or 0) > 0,
    "coturn_tls_listener_failed": (number(tls_failed) or 0) > 0,
    "relay_share": None,
    "notes": [],
}

if not payload["livekit_declares_external_turn"]:
    payload["notes"].append(
        "The SFU declares NO external TURN server, so no client is ever offered a relay "
        "candidate. A relay share of zero here means the relay is unreachable, not unneeded "
        "(finding C131-01)."
    )

if payload["coturn_tls_listener_failed"]:
    payload["notes"].append(
        "coturn could not start its TLS/DTLS listeners — 5349 is not listening, so carriers "
        "blocking plain UDP 3478 have no fallback (finding C131-03)."
    )

if payload["coturn_allocations_seen"] is None:
    payload["notes"].append(
        "The media host could not be reached over SSH, so the server-side view is absent. "
        "The probe's own figures describe only what the probe saw."
    )

text = json.dumps(payload, indent=2)

if out:
    with open(out, "w") as handle:
        handle.write(text + "\n")
    print(f"  wrote {out}")
else:
    print(text)
PY
