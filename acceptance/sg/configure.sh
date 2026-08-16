#!/usr/bin/env bash
# =====================================================================================
# acceptance/sg/configure.sh — writes acceptance/sg/env.json (0600, gitignored).
#
#   bash acceptance/sg/configure.sh --region sgp --client colombo \
#        --media-host 203.0.113.10 --tracker-host 203.0.113.11 \
#        --platform https://api.mageride.lk
#
# RUN THIS FROM THE COLOMBO-SIDE CLIENT, not from the build host. Every figure C131 produces
# is about a path, and the path starts wherever this ran. `--client` is written into
# env.json and printed in the report beside every RTT for that reason; `run.sh`'s region
# fence refuses a run whose client location is undeclared.
#
# NOTHING HERE HOLDS A CREDENTIAL IN A COMMITTED FILE. The TURN shared secret and the
# bearers live in env.json at 0600, which is gitignored — `load/configure.sh` and
# `chaos/configure.sh`'s rule, and the bearers are obtained through the real routes
# (`POST /v1/auth/otp/request` + `verify`) rather than minted, because iam-svc's RS256 key is
# not something an acceptance harness may hold.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
SG_DIR="$PWD"
OUT="$SG_DIR/env.json"

REGION=""
CLIENT=""
MEDIA_HOST=""
TRACKER_HOST=""
PLATFORM=""
TURN_PORT=3478
GT06_IMEI="${C131_GT06_IMEI:-}"
JT808_IMEI="${C131_JT808_IMEI:-}"

step() { printf '\n\033[1m▸ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
die()  { printf '  \033[31m✗\033[0m %s\n' "$*" >&2; exit 1; }
note() { printf '  \033[33m!\033[0m %s\n' "$*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --region)       REGION="$2"; shift 2 ;;
    --client)       CLIENT="$2"; shift 2 ;;
    --media-host)   MEDIA_HOST="$2"; shift 2 ;;
    --turn-port)    TURN_PORT="$2"; shift 2 ;;
    --tracker-host) TRACKER_HOST="$2"; shift 2 ;;
    --platform)     PLATFORM="$2"; shift 2 ;;
    -h|--help)      sed -n '2,20p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

step "Refusals"

# The inverse of load/ and chaos/'s fence. Those three suites refuse to run ANYWHERE BUT the
# replica; this one refuses to write a Singapore descriptor that points at it, which is the
# specific mistake C131's first fence names.
if docker compose ls --format json 2>/dev/null | grep -q mageride-replica; then
  note "the EU replica is running on this box"

  case "$MEDIA_HOST" in
    127.*|localhost|"") die "refusing to write a '$REGION' descriptor from a box running the EU replica
    with no external media host. Run this from the Colombo-side client." ;;
  esac
fi

[ "$REGION" = "sgp" ] || die "--region must be 'sgp'. C131 records acceptance figures for the Singapore
    region only; anything else is a rehearsal and belongs to 'run.sh --rehearse'."
[ -n "$CLIENT" ] || die "--client is required. A tracker RTT with no stated origin is not a measurement."
[ -n "$MEDIA_HOST" ] || die "--media-host is required — deploy it with infra/sg/deploy-media-sg.sh first."
[ -n "$TRACKER_HOST" ] || die "--tracker-host is required."
[ -n "$PLATFORM" ] || die "--platform is required — the AL-48 fallback is driven through the edge."

ok "region '$REGION', client '$CLIENT'"

step "TURN shared secret"
[ -n "${TURN_SHARED_SECRET:-}" ] || die "TURN_SHARED_SECRET is unset. It must equal the media host's
    static-auth-secret; without it no allocation can be made and no media figure exists."
ok "present in the environment"

step "Tracker identities"
if [ -z "$GT06_IMEI" ] || [ -z "$JT808_IMEI" ]; then
  die "C131_GT06_IMEI and C131_JT808_IMEI must name IMEIs that are BOUND in prov.tracker_bindings
    for this deployment. tcp-adapter publishes nothing before the vehicle is known, and an
    unbound IMEI is refused silently from the device's side — the probe would report a
    platform that never answered."
fi
ok "GT06 $GT06_IMEI, JT/T 808 $JT808_IMEI"

step "Writing $OUT"
umask 077

cat > "$OUT" <<JSON
{
  "region": "$REGION",
  "generatedBy": "acceptance/sg/configure.sh",
  "turn": {
    "host": "$MEDIA_HOST",
    "port": $TURN_PORT,
    "secret": "$TURN_SHARED_SECRET"
  },
  "tracker": {
    "host": "$TRACKER_HOST",
    "clientLocation": "$CLIENT",
    "gt06Imei": "$GT06_IMEI",
    "jt808Imei": "$JT808_IMEI",
    "vehicleId": "${C131_VEHICLE_ID:-}"
  },
  "platform": {
    "baseUrl": "$PLATFORM",
    "bearer": "${C131_BEARER:-}",
    "rideId": "${C131_RIDE_ID:-}",
    "sessionVehicleId": "${C131_SESSION_VEHICLE_ID:-}"
  },
  "mqtt": {
    "host": "$TRACKER_HOST",
    "port": 8883,
    "username": "${C131_MQTT_USERNAME:-}",
    "password": "${C131_MQTT_PASSWORD:-}"
  },
  "voip": {
    "forcedFailureArranged": false
  }
}
JSON

chmod 600 "$OUT"
ok "written at 0600 (gitignored)"

step "Still needed"
cat <<'NEEDED'
  env.json is written, and three fields it carries are placeholders until an operator fills
  them. `run.sh` names each one it is missing rather than proceeding without it:

    platform.bearer / platform.rideId
        an ACCEPTED ride and a participant's bearer. Obtain the bearer through the real
        routes — POST /v1/auth/otp/request then /verify — never mint one.

    platform.sessionVehicleId
        a Mode A/B vehicle with the GT06 tracker bound, for the end-to-end downlink
        measurement. Needs TripState__PublishCadenceHints=true on the deployment, which is
        set nowhere today (report.md finding C131-06).

    voip.forcedFailureArranged
        set to true only once LiveKit has actually been made unreachable in-region. It is
        the third item of the definition of done and `run.sh` records it as a blocker until
        it is arranged, because a fallback nobody forced is a fallback nobody verified.
NEEDED
