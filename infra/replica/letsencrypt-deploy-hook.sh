#!/usr/bin/env bash
# =====================================================================================
# MageRide replica — rebuild HAProxy's combined PEM from the Let's Encrypt live files
# and reload the edge.
#
#   bash infra/replica/letsencrypt-deploy-hook.sh          # run it by hand
#
# Certbot runs this after every successful renewal, via the thin wrapper installed at
#   /etc/letsencrypt/renewal-hooks/deploy/mageride-haproxy.sh
# which execs this file. The wrapper is outside the repository and would be lost if this
# box were rebuilt; this script is the part worth keeping, so the wrapper holds no logic.
#
# --- WHY A COMBINED PEM --------------------------------------------------------------
# haproxy.replica.cfg binds `ssl crt /usr/local/etc/haproxy/certs/replica.pem` — ONE file
# carrying both key and chain, which is what `crt <file>` means. Certbot writes the two
# halves separately, so they are concatenated here. Same file, same path and same
# ownership deploy.sh created for the self-signed cert, so nothing else changes.
#
# --- WHY THIS DOES NOT FIGHT deploy.sh -----------------------------------------------
# deploy.sh generates a self-signed replica.pem ONLY when the file is absent
# (`if [ -f "$REPLICA_PEM" ]` -> "exists"). Once this hook has written a real certificate
# there, deploy.sh leaves it alone. Delete replica.pem and the self-signed one comes back.
#
# --- PERMISSIONS ARE LOAD-BEARING ----------------------------------------------------
# uid 99 is `haproxy` inside haproxy:2.9-alpine. A root-owned 600 pem is unreadable to it
# and the edge exits with "cannot open the file" — deploy.sh carries the same note for the
# same reason. The mount is read-only from the container's side; the host writes it.
#
# --- PORT 80 --------------------------------------------------------------------------
# Renewal re-runs the standalone HTTP-01 challenge, which binds :80 on the host for a few
# seconds. HAProxy publishes 443/8883/8084/5023-5026 and never 80, so there is no clash.
# If anything is ever put on :80, switch this cert to the webroot or DNS-01 plugin.
# =====================================================================================
set -Eeuo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"

# The lineage is named for the FIRST -d passed at issuance, not for the replica.
LIVE_DIR="${LIVE_DIR:-/etc/letsencrypt/live/admin.mageride.lk}"
PEM="$REPO_ROOT/infra/deploy/certs/replica.pem"
CONTAINER="${HAPROXY_CONTAINER:-mageride-replica-haproxy-1}"

[ -r "$LIVE_DIR/privkey.pem" ]   || { echo "error: no readable $LIVE_DIR/privkey.pem" >&2; exit 1; }
[ -r "$LIVE_DIR/fullchain.pem" ] || { echo "error: no readable $LIVE_DIR/fullchain.pem" >&2; exit 1; }

# Write via a temp file in the same directory and mv into place: HAProxy may read this
# path at any moment, and a half-written pem is an edge that will not reload.
TMP="$(mktemp "$(dirname "$PEM")/.replica.pem.XXXXXX")"
trap 'rm -f "$TMP"' EXIT
cat "$LIVE_DIR/privkey.pem" "$LIVE_DIR/fullchain.pem" > "$TMP"
chmod 600 "$TMP"
chown 99:99 "$TMP" 2>/dev/null || echo "warn: could not chown to uid 99 — haproxy may not read it" >&2
mv -f "$TMP" "$PEM"
trap - EXIT

echo "wrote $PEM from $LIVE_DIR"

# SIGUSR2 to the master (-W -db) is a graceful reload: workers finish in-flight requests
# and new ones pick up the new certificate. A restart would drop every WSS session the
# fanout container is holding, which a certificate swap has no business doing.
if docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
  docker kill -s USR2 "$CONTAINER" >/dev/null && echo "reloaded $CONTAINER (SIGUSR2)"
else
  echo "note: $CONTAINER is not running — the new pem is in place for its next start"
fi
