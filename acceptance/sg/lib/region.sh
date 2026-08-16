#!/usr/bin/env bash
# =====================================================================================
# acceptance/sg/lib/region.sh — the fence, and the only thing that can authorise an
# acceptance figure.
#
# THE FENCE THIS COMPONENT EXISTS TO HOLD
# ------------------------------------------------------------------------------------
#   "These runs happen in the SINGAPORE region, not on the Contabo EU replica.
#    EU numbers are not acceptance evidence."   — build/prompts/C131.md
#
# Every other suite in this repository fences by refusing to run anywhere but the replica
# (`chaos/run-drills.sh --env replica`, `load/configure.sh`'s three checks). This one is the
# inverse and is harder, because "not the replica" is not the same as "Singapore", and a
# declaration in a config file is not evidence of anything.
#
# So the fence is made of three layers, and NONE of them is a self-declaration alone:
#
#   1. REFUSAL. The target must not be the replica, a loopback, or a private address. This
#      is a hard stop: it is the specific mistake the prompt names.
#   2. DECLARATION. env.json must carry `region: sgp` and a `clientLocation`. On its own this
#      proves nothing; it exists so the other two layers have something to contradict.
#   3. PHYSICS. Light in fibre travels ~200,000 km/s, so a round trip has a floor set by the
#      great-circle distance between the client and the target. Colombo->Singapore is
#      ~2,900 km (>=29 ms RTT); Colombo->Frankfurt is ~8,000 km (>=80 ms). A target that
#      answers a Colombo client in 40 ms CANNOT be in Europe, and one that takes 180 ms is
#      not in Singapore. The measurement cannot prove a location, but it can REFUTE one,
#      which is the direction that matters here.
#
# A run where any layer is unmet does not produce a smaller number or a caveated one. It
# produces NO acceptance figure at all: `run.sh` writes a report stamped NOT EVIDENCE and
# exits non-zero. That is C126's shape — `gtfs-day0-verify.sh` exits 2 naming exactly what is
# missing — and it is the only shape that keeps the fence honest, because a caveat in a
# document is something a reader can skip and an exit code is not.
# =====================================================================================

# Great-circle distances, km, from Colombo. Rounded down, so the derived floors are floors.
readonly COLOMBO_TO_SINGAPORE_KM=2880
readonly COLOMBO_TO_FRANKFURT_KM=7900

# Propagation in single-mode fibre: ~2/3 c. Generous — a real path is longer than the
# great circle and passes through equipment, so a MEASURED rtt below the derived floor is
# physically impossible rather than merely surprising.
readonly FIBRE_KM_PER_MS=200

# The replica's own markers. Any of them answering means this is the EU box.
readonly REPLICA_PROJECT="mageride-replica"

region_floor_ms() {
  # Round-trip floor for a one-way distance in km.
  local km="$1"
  awk -v km="$km" -v v="$FIBRE_KM_PER_MS" 'BEGIN { printf "%.1f", (2 * km) / v }'
}

# ------------------------------------------------------------------------------------
# Layer 1 — refusal.
# ------------------------------------------------------------------------------------
region_refuse_replica() {
  local host="$1"
  local failed=0

  case "$host" in
    127.*|localhost|::1|0.0.0.0)
      note_fail "target '$host' is loopback — that is this box, which is the EU replica"
      failed=1
      ;;
    10.*|192.168.*|172.1[6-9].*|172.2[0-9].*|172.3[01].*)
      note_fail "target '$host' is a private address — an acceptance run reaches a region, not a LAN"
      failed=1
      ;;
  esac

  # The replica is a compose project on this host. If it is running here and the target
  # resolves to this host's own address, the run would measure the EU box through a
  # public IP and look entirely legitimate.
  if docker compose ls --format json 2>/dev/null | grep -q "$REPLICA_PROJECT"; then
    local own
    own="$(curl -fsS --max-time 5 https://ifconfig.me 2>/dev/null || true)"

    if [ -n "$own" ] && [ "$host" = "$own" ]; then
      note_fail "target '$host' is this box's own public address, and the EU replica is running on it"
      failed=1
    fi
  fi

  return "$failed"
}

# ------------------------------------------------------------------------------------
# Layer 3 — physics. Measures a TCP handshake RTT, which needs no ICMP and no cooperation
# beyond a listening port.
# ------------------------------------------------------------------------------------
region_measure_rtt_ms() {
  local host="$1" port="$2" samples="${3:-7}"

  python3 - "$host" "$port" "$samples" <<'PY'
import socket, statistics, sys, time

host, port, samples = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
measured = []

for _ in range(samples):
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.settimeout(5)
    started = time.monotonic()
    try:
        sock.connect((host, port))
        measured.append((time.monotonic() - started) * 1000.0)
    except OSError:
        pass
    finally:
        sock.close()
    time.sleep(0.05)

# The minimum, not the mean: a handshake can be delayed by anything, and the floor check is
# a claim about the fastest path that exists, not about the typical one.
print(f"{min(measured):.2f}" if measured else "")
PY
}

# ------------------------------------------------------------------------------------
# The whole fence. Answers 0 only when an acceptance figure may be recorded.
# ------------------------------------------------------------------------------------
region_assert_singapore() {
  local host="$1" port="$2" declared_region="$3" client_location="$4"
  local verdict=0

  step "Region fence"

  # --- layer 2: declaration ---------------------------------------------------------
  if [ "$declared_region" != "sgp" ]; then
    note_fail "env.json declares region '$declared_region'; this harness only records figures for 'sgp'"
    verdict=1
  else
    ok "env.json declares the Singapore region"
  fi

  if [ -z "$client_location" ] || [ "$client_location" = "UNDECLARED" ]; then
    note_fail "env.json declares no clientLocation — a tracker RTT with no stated origin is not a measurement"
    verdict=1
  else
    ok "the client declares itself at '$client_location'"
  fi

  # --- layer 1: refusal -------------------------------------------------------------
  if region_refuse_replica "$host"; then
    ok "the target is not the replica, a loopback or a private address"
  else
    verdict=1
  fi

  # --- layer 3: physics -------------------------------------------------------------
  local rtt floor_sgp floor_eu
  rtt="$(region_measure_rtt_ms "$host" "$port")"
  floor_sgp="$(region_floor_ms "$COLOMBO_TO_SINGAPORE_KM")"
  floor_eu="$(region_floor_ms "$COLOMBO_TO_FRANKFURT_KM")"

  if [ -z "$rtt" ]; then
    note_fail "no TCP handshake completed against $host:$port — the region is not reachable, so nothing can be measured"
    return 1
  fi

  REGION_RTT_MS="$rtt"

  if [ "${client_location,,}" = "colombo" ]; then
    # A Colombo client CANNOT reach Europe faster than the EU floor. If it did, the target
    # is nearer than Europe — which is the refutation this layer is for.
    if awk -v r="$rtt" -v f="$floor_eu" 'BEGIN { exit !(r < f) }'; then
      ok "RTT ${rtt} ms from Colombo is below the ${floor_eu} ms floor for Europe — the target is not in the EU"
    else
      note_fail "RTT ${rtt} ms from Colombo is at or above the ${floor_eu} ms Europe floor — this may BE the EU replica"
      verdict=1
    fi

    if awk -v r="$rtt" -v f="$floor_sgp" 'BEGIN { exit !(r >= f) }'; then
      ok "RTT ${rtt} ms is at or above the ${floor_sgp} ms Colombo-Singapore floor — consistent with Singapore"
    else
      note_fail "RTT ${rtt} ms is BELOW the ${floor_sgp} ms Colombo-Singapore floor — the client is not in Colombo"
      verdict=1
    fi
  else
    note_fail "the physics check is defined for a Colombo client; '$client_location' has no floor in this file"
    verdict=1
  fi

  return "$verdict"
}
