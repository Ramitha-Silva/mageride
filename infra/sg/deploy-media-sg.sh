#!/usr/bin/env bash
# =====================================================================================
# infra/sg/deploy-media-sg.sh — deploy the VoIP media plane onto the Singapore media host.
#
#   MEDIA_SSH_PASSWORD=... bash infra/sg/deploy-media-sg.sh
#   ...                    bash infra/sg/deploy-media-sg.sh --dry-run
#   ...                    bash infra/sg/deploy-media-sg.sh --status
#
# Same shape as `infra/replica/nominatim/deploy-nominatim.sh`, and for the same reason: a
# thing deployed from a shell session exists only in that session's history. This is the
# LiveKit + coturn half of C131's first deliverable, reproducible from the repository.
#
# WHY A HOST AND NOT THE CLUSTER
# ------------------------------------------------------------------------------------
# 1,200 UDP relay ports have to be reachable as themselves. An Ingress terminates HTTP; a
# LoadBalancer Service would need a 1,200-entry port list and would still NAT the media.
# `infra/k8s/overlays/production/kustomization.yaml` and `infra/k8s/service-catalog.yaml`
# both already say the media plane is not in the manifests — this is where it is instead.
#
# WHAT THIS SCRIPT WILL NOT DO
# ------------------------------------------------------------------------------------
# It will not provision the host, and it will not point voip-svc at what it deploys. The
# first costs money and is a human's decision; the second is a change to a running
# deployment's configuration and belongs to whoever owns that deployment (C132). Both are
# printed as the next steps and neither is performed.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
SG_DIR="$PWD"
cd ../.. || exit 2
REPO_ROOT="$PWD"

HOST="${MEDIA_HOST:-}"
PORT="${MEDIA_SSH_PORT:-22}"
USER="${MEDIA_SSH_USER:-root}"
REMOTE_DIR="/opt/mageride-media"

dry_run=0
status_only=0

for argument in "$@"; do
  case "$argument" in
    --dry-run) dry_run=1 ;;
    --status)  status_only=1 ;;
    -h|--help) sed -n '2,26p' "$0"; exit 0 ;;
    *) printf 'unknown argument: %s\n' "$argument" >&2; exit 2 ;;
  esac
done

step() { printf '\n\033[1m▸ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
die()  { printf '  \033[31m✗\033[0m %s\n' "$*" >&2; exit 1; }
note() { printf '  \033[33m!\033[0m %s\n' "$*"; }

step "Preconditions"

[ -n "$HOST" ] || die "MEDIA_HOST is unset. There is no Singapore media host in this repository to default to,
    and defaulting to one would be inventing infrastructure. Provision a host in the
    Singapore region (DigitalOcean SGP1 alongside the DOKS cluster, per D7' §8's
    'LiveKit+coturn pinned SGP'), then set MEDIA_HOST."

for required in ssh scp; do
  command -v "$required" >/dev/null || die "$required is not installed"
done

# The relay tells peers to send to this address; getting it wrong is silent one-way audio.
MEDIA_PUBLIC_IP="${MEDIA_PUBLIC_IP:-$HOST}"
MEDIA_LISTEN_IP="${MEDIA_LISTEN_IP:-0.0.0.0}"
TURN_PUBLIC_HOST="${TURN_PUBLIC_HOST:-$MEDIA_PUBLIC_IP}"

ok "host $USER@$HOST:$PORT, advertising $MEDIA_PUBLIC_IP"

for required in LIVEKIT_API_KEY LIVEKIT_API_SECRET; do
  [ -n "${!required:-}" ] || die "$required is unset. It must equal voip-svc's Voip__LiveKit__ApiKey /
    Voip__LiveKit__ApiSecret — a mismatch is a refused join on every call."
done
ok "LiveKit keys present in the environment (never written to this repository)"

if [ -z "${TURN_SHARED_SECRET:-}" ]; then
  die "TURN_SHARED_SECRET is unset. coturn's use-auth-secret has nothing to verify against
    without it, and LiveKit cannot mint the ephemeral credentials it advertises. This is the
    value that is set in no compose file, no environment file and no k8s overlay today —
    see acceptance/sg/report.md finding C131-04."
fi
ok "TURN shared secret present"

remote() { ssh -p "$PORT" -o StrictHostKeyChecking=accept-new "$USER@$HOST" "$@"; }

if [ "$status_only" -eq 1 ]; then
  step "Status"
  remote "cd $REMOTE_DIR 2>/dev/null && docker compose -f docker-compose.media-sg.yml ps" \
    || die "nothing deployed at $REMOTE_DIR"
  exit 0
fi

step "Plan"
cat <<PLAN
  1. install Docker on $HOST if absent
  2. copy livekit.sg.yaml, turnserver.sg.conf, docker-compose.media-sg.yml to $REMOTE_DIR
  3. write $REMOTE_DIR/secrets/turn_shared_secret (0600) and $REMOTE_DIR/.env (0600)
  4. obtain a TLS certificate for \$TURN_PUBLIC_HOST into $REMOTE_DIR/certs
     — 5349 does not listen without one, which is finding C131-03
  5. open 3478/udp, 5349/tcp+udp, 7880/tcp, 7881/tcp and 50000-51200/udp
  6. docker compose up -d, then wait for both healthchecks

  NOT done by this script, and each is a deliberate stop:
  - provisioning the host (costs money; a human's decision)
  - pointing voip-svc's Voip__LiveKit__WsUrl / ApiUrl at it (C132 owns that deployment)
  - setting TripState__PublishCadenceHints (see report.md finding C131-06)
PLAN

if [ "$dry_run" -eq 1 ]; then
  step "Dry run"
  ok "plan printed; nothing was changed on $HOST"
  exit 0
fi

step "Deploy"
remote "command -v docker >/dev/null || (curl -fsSL https://get.docker.com | sh)" \
  || die "could not install Docker on $HOST"
ok "Docker present"

remote "mkdir -p $REMOTE_DIR/secrets $REMOTE_DIR/certs && chmod 700 $REMOTE_DIR/secrets"

scp -P "$PORT" -q \
  "$SG_DIR/livekit.sg.yaml" \
  "$SG_DIR/turnserver.sg.conf" \
  "$SG_DIR/docker-compose.media-sg.yml" \
  "$USER@$HOST:$REMOTE_DIR/" || die "could not copy the configuration"
ok "configuration copied"

remote "umask 077 && cat > $REMOTE_DIR/secrets/turn_shared_secret" <<<"$TURN_SHARED_SECRET"
remote "umask 077 && cat > $REMOTE_DIR/.env" <<ENVFILE
LIVEKIT_API_KEY=$LIVEKIT_API_KEY
LIVEKIT_API_SECRET=$LIVEKIT_API_SECRET
MEDIA_PUBLIC_IP=$MEDIA_PUBLIC_IP
MEDIA_LISTEN_IP=$MEDIA_LISTEN_IP
TURN_PUBLIC_HOST=$TURN_PUBLIC_HOST
ENVFILE
ok "secrets written at 0600 on the target and nowhere else"

remote "cd $REMOTE_DIR && docker compose -f docker-compose.media-sg.yml up -d" \
  || die "compose refused to start the media plane"

step "Verify"
remote "cd $REMOTE_DIR && docker compose -f docker-compose.media-sg.yml ps"

if remote "test -s $REMOTE_DIR/certs/tls.crt"; then
  ok "a TLS certificate is present, so 5349 can listen"
else
  note "no certs/tls.crt — 5349 will NOT listen (finding C131-03). Carriers that block"
  note "plain UDP 3478 have no fallback until one is installed."
fi

step "Next"
cat <<NEXT
  Point voip-svc at this host (C132's deployment, not this script's):
    Voip__LiveKit__WsUrl  = wss://$TURN_PUBLIC_HOST:7880
    Voip__LiveKit__ApiUrl = https://$TURN_PUBLIC_HOST:7880

  Then, from a Colombo-side client:
    bash acceptance/sg/configure.sh --region sgp --client colombo
    bash acceptance/sg/run.sh --report acceptance/sg/out/report.md
NEXT
