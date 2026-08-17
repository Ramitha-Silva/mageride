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

# --- EMQX's copy ----------------------------------------------------------------------
# HAProxy passes 8883 and 8084 through at L4, so EMQX terminates that TLS ITSELF and needs
# its own copy — the combined pem above is HAProxy's format and EMQX wants the halves
# separately. Without this it serves the self-signed pair shipped in its image
# (`C=CN, ST=hangzhou, O=EMQ, CN=Server`) and every mobile client refuses the handshake on
# both an untrusted issuer and a hostname mismatch. This is the C125 debt emqx.conf names.
#
# uid 1000 is `emqx` inside emqx/emqx:5.8, as uid 99 is `haproxy` in its image. The key is
# 600 and owned by that uid: a world-readable private key on a box with a public IP is not
# made acceptable by the directory being mounted read-only.
EMQX_CERT="$(dirname "$PEM")/platform-cert.pem"
EMQX_KEY="$(dirname "$PEM")/platform-key.pem"

install -m 0644 -o 1000 -g 1000 "$LIVE_DIR/fullchain.pem" "$EMQX_CERT"
install -m 0600 -o 1000 -g 1000 "$LIVE_DIR/privkey.pem"   "$EMQX_KEY"
echo "wrote $EMQX_CERT and $EMQX_KEY for emqx"

# EMQX reads its listener certificates at listener start, so this is a RESTART and not a
# reload — every MQTT session drops and reconnects. Acceptable at renewal cadence (~60 days)
# and unavoidable without an operator-facing reload path; mobile clients reconnect on the
# backoff ADD §18.2 already requires of them.
EMQX_CONTAINER="${EMQX_CONTAINER:-mageride-replica-emqx-1}"
if docker ps --format '{{.Names}}' | grep -qx "$EMQX_CONTAINER"; then
  docker restart "$EMQX_CONTAINER" >/dev/null && echo "restarted $EMQX_CONTAINER for the new certificate"
else
  echo "note: $EMQX_CONTAINER is not running — it will read the new certificate at its next start"
fi

# SIGUSR2 to the master (-W -db) is a graceful reload: workers finish in-flight requests
# and new ones pick up the new certificate. A restart would drop every WSS session the
# fanout container is holding, which a certificate swap has no business doing.
if docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
  docker kill -s USR2 "$CONTAINER" >/dev/null && echo "reloaded $CONTAINER (SIGUSR2)"
else
  echo "note: $CONTAINER is not running — the new pem is in place for its next start"
fi
