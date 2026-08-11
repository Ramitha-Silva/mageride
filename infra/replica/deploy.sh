#!/usr/bin/env bash
# =====================================================================================
# infra/replica/deploy.sh — bring the lightweight production replica up, or prove it could.
#
#   bash infra/replica/deploy.sh --dry-run   # every check, nothing built and nothing started
#   bash infra/replica/deploy.sh             # the checks, then build and up, then wait for healthy
#   bash infra/replica/deploy.sh --with-monitoring   # ...and C119's observability stack beside it
#
# The dry run is the first half of C125's verify command and is meant to be safe on a box that is
# doing something else: it reads, renders and validates, and the only thing it WRITES is the
# generated secrets file and the certificates, both of which are prerequisites rather than actions
# (and both are idempotent — an existing .env.replica is never overwritten).
#
# ORDER MATTERS AND IS NOT ARBITRARY:
#   1. guardrail   — a heavy build running, or a budget that does not fit, stops everything.
#   2. secrets     — the compose file has `${VAR:?}` on four values so it refuses to render without
#                    them; better a generated secret than a default nobody changed.
#   3. certificates— EMQX reads the device CA chain as its 8883 cacertfile AT LISTENER START, so it
#                    must exist before the container does. HAProxy's pem likewise.
#   4. validate    — compose renders, both haproxy configs pass `haproxy -c`, the images' Dockerfiles
#                    exist.
#   5. up          — build, start, wait for every healthcheck, then seed.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
REPLICA_DIR="$PWD"
cd ../.. || exit 2
REPO_ROOT="$PWD"

COMPOSE="infra/replica/docker-compose.light-replica.yml"
CERT_DIR="infra/deploy/certs"
REPLICA_PEM="$CERT_DIR/replica.pem"
ENV_FILE="$REPLICA_DIR/.env.replica"

dry_run=0
with_monitoring=0
for arg in "$@"; do
  case "$arg" in
    --dry-run)         dry_run=1 ;;
    --with-monitoring) with_monitoring=1 ;;
    -h|--help)         sed -n '2,22p' "$0"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

step()  { printf '\n\033[1m▸ %s\033[0m\n' "$*"; }
ok()    { printf '  \033[32m✓\033[0m %s\n' "$*"; }
die()   { printf '  \033[31m✗\033[0m %s\n' "$*" >&2; exit 1; }
note()  { printf '  \033[33m!\033[0m %s\n' "$*"; }

# -------------------------------------------------------------------------------------
step "1/5  guardrails"
# -------------------------------------------------------------------------------------
# Not advisory, and not skippable with a flag. The root CLAUDE.md is explicit that a build and the
# replica do not fit in this box together, and the failure mode is the OOM killer rather than a slow
# build.
if bash infra/replica/guardrail.sh; then
  ok "guardrail passed"
else
  die "guardrail failed — see above. Nothing was started."
fi

# -------------------------------------------------------------------------------------
step "2/5  secrets"
# -------------------------------------------------------------------------------------
# The compose file marks four values `${VAR:?}` so it cannot render without them. Generating them
# here rather than shipping defaults is the point: a replica whose MQTT secret is
# "mageride-dev-mqtt-jwt-secret-change-me" on a publicly reachable edge is a different risk than a
# dev box on loopback.
if [ -f "$ENV_FILE" ]; then
  ok ".env.replica exists — left alone (it is the only place the generated secrets live)"
else
  cp "$REPLICA_DIR/.env.replica.example" "$ENV_FILE"

  gen() { openssl rand -base64 33 | tr -d '\n=' | tr '+/' '-_'; }

  # `|` as the sed delimiter: base64 contains `/`.
  sed -i "s|CHANGEME_POSTGRES|$(gen)|"    "$ENV_FILE"
  sed -i "s|CHANGEME_MQTT_JWT|$(gen)|"    "$ENV_FILE"
  sed -i "s|CHANGEME_MINIO_ROOT|$(gen)|"  "$ENV_FILE"
  # MinIO's SSE-KMS key must be exactly 32 bytes, base64-encoded — not the URL-safe variant.
  sed -i "s|CHANGEME_MINIO_KMS_BASE64|$(openssl rand -base64 32 | tr -d '\n')|" "$ENV_FILE"
  # Δ C126. The signature on SCR-AP-016's feed-zip download link is the whole credential on that
  # route (`security: []` in contracts/transit.yaml), and env/.env.app.example's placeholder is a
  # published constant that TransitOptions happily accepts.
  sed -i "s|CHANGEME_GTFS_SIGNING|$(gen)|" "$ENV_FILE"

  # A real RS256 signing key, per deployment. Not ephemeral: SigningKeyRing's own comment says an
  # ephemeral key "would invalidate every live token on restart and give each replica a different
  # JWKS", and a demo whose logins die when a container restarts is a demo that looks broken.
  #
  # Appended rather than substituted because it is multi-line. A compose env_file does carry a
  # quoted multi-line value — verified, not assumed — and .env.replica is the LAST env_file the
  # compose loads, so this wins over the empty default in env/.env.app.example.
  {
    echo
    echo "# --- generated by deploy.sh: the replica's RS256 signing key (D7' §13) ---"
    printf 'Jwt__SigningKeyPem="'
    openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 2>/dev/null
    printf '"\n'
  } >> "$ENV_FILE"

  if ! grep -q 'BEGIN PRIVATE KEY' "$ENV_FILE"; then
    die "could not generate an RS256 signing key into $ENV_FILE"
  fi

  # The DATABASE ROLE the services connect as.
  #
  # env/.env.common.example hardcodes `Username=mageride;Password=mageride_dev` into both DSNs, while
  # the postgres container initialises with POSTGRES_USER — which defaults to `postgres`. So the role
  # the services ask for does not exist, and every one of them dies with
  # `28P01: password authentication failed for user "mageride"`. The dev slim stack has the same
  # mismatch and nobody had found it, because nothing had ever started app-services against it.
  #
  # Resolved by initialising the database AS `mageride` rather than by pointing the services at
  # `postgres`: migrations then own every object as the role that will read them, and the services do
  # not connect as a superuser. One variable, and the whole chain agrees. POSTGRES_USER only applies
  # when the data directory is first created, which is why this must be set before the first deploy.
  pg_password=$(grep -m1 '^PG_PASSWORD=' "$ENV_FILE" | cut -d= -f2-)
  {
    echo
    echo "# --- generated by deploy.sh: the role the services connect as ---"
    echo "PG_USER=mageride"
    # QUOTED. Both DSNs contain `Maximum Pool Size=20` — a space — and every script here reads this
    # file with `. .env.replica`, where an unquoted space makes bash run `Maximum` as a command and
    # truncates the variable at it. docker compose parses either form.
    echo "ConnectionStrings__Postgres=\"Host=pgbouncer;Port=6432;Database=mageride;Username=mageride;Password=${pg_password};Pooling=true;Maximum Pool Size=20\""
    echo "ConnectionStrings__PostgresDirect=\"Host=postgres;Port=5432;Database=mageride;Username=mageride;Password=${pg_password};Pooling=true;Maximum Pool Size=5\""
  } >> "$ENV_FILE"

  # The MQTT session-token secret, on both sides of the broker.
  #
  # EMQX validates with EMQX_AUTHENTICATION__1__SECRET=${MQTT_JWT_SECRET}; the services SIGN with
  # `Mqtt__SessionTokenSecret`, which env/.env.common.example leaves at the dev literal
  # `mageride-dev-mqtt-jwt-secret-change-me`. With a generated MQTT_JWT_SECRET the two disagree and
  # EMQX answers every service's CONNECT with NotAuthorized — fanout, mqtt-bridge and tcp-adapter all
  # fail, and the message names the broker rather than the mismatch.
  mqtt_secret=$(grep -m1 '^MQTT_JWT_SECRET=' "$ENV_FILE" | cut -d= -f2-)
  {
    echo
    echo "# --- generated by deploy.sh: the MQTT session-token secret, matching EMQX's validator ---"
    echo "Mqtt__SessionTokenSecret=${mqtt_secret}"
  } >> "$ENV_FILE"

  # The object store's credentials must be the MinIO root pair generated above. Sourced back out of
  # the file rather than recomputed, so the two can never disagree.
  minio_user=$(grep -m1 '^MINIO_ROOT_USER=' "$ENV_FILE" | cut -d= -f2-)
  minio_pass=$(grep -m1 '^MINIO_ROOT_PASSWORD=' "$ENV_FILE" | cut -d= -f2-)
  {
    echo
    echo "# --- generated by deploy.sh: the object store's credentials, matching MinIO's root pair ---"
    echo "Storage__S3__AccessKey=${minio_user:-mageride_replica}"
    echo "Storage__S3__SecretKey=${minio_pass}"
  } >> "$ENV_FILE"

  chmod 600 "$ENV_FILE"

  # ASSIGNMENTS only. The first version grepped the whole file and matched the comment at the top
  # that explains what CHANGEME means, so it refused to deploy over its own documentation.
  if grep -nE '^[A-Za-z_][A-Za-z0-9_]*=.*CHANGEME' "$ENV_FILE"; then
    die "a CHANGEME survived in $ENV_FILE — refusing to start with a placeholder secret"
  fi

  ok "generated $ENV_FILE (mode 600, gitignored)"
fi

# The env file is what the compose file's `${VAR:?}` reads. Export it for every docker compose call
# below, and for the validation that renders the file.
set -a
# shellcheck disable=SC1090
. "$ENV_FILE"
set +a

# A regenerated .env.replica against an EXISTING postgres volume is an auth failure whose message
# names neither cause. POSTGRES_PASSWORD is only applied when the data directory is initialised, so a
# new password in the env file never reaches a volume that already exists, and `migrate` dies 120
# seconds later with `28P01: password authentication failed for user "postgres"`. Cost 20 minutes the
# first time; saying so costs four lines.
if docker volume inspect mageride-replica_pgdata >/dev/null 2>&1; then
  probe=$(docker run --rm --network mageride-replica_internal \
            -e PGPASSWORD="${PG_PASSWORD:-}" timescale/timescaledb-ha:pg16 \
            psql -h postgres -U "${PG_USER:-postgres}" -d postgres -qtAX -c "SELECT 1" 2>&1 || true)
  case "$probe" in
    *28P01*|*"authentication failed"*)
      die "the postgres volume mageride-replica_pgdata exists but rejects the password in
      .env.replica. POSTGRES_PASSWORD only applies when the data directory is first initialised, so a
      regenerated .env.replica cannot reach an existing volume. Either restore the old password, or
      discard the data:  bash infra/replica/down.sh --volumes" ;;
  esac
fi

# -------------------------------------------------------------------------------------
step "3/5  certificates"
# -------------------------------------------------------------------------------------
# The device CA first: provisioning-svc mints from it, EMQX reads certs/ca_chain.crt as its 8883
# cacertfile at listener start, and tcp-adapter mounts it read-only. Extracted into its own script by
# C124 precisely so something other than dev-up.sh could call it.
if bash infra/scripts/ensure-device-ca.sh >/dev/null 2>&1; then
  ok "device CA present (infra/deploy/device-ca)"
else
  die "infra/scripts/ensure-device-ca.sh failed — EMQX cannot start its TLS listener without it"
fi

# provisioning-svc's embedded step-ca WRITES into /var/step, and the compose file bind-mounts
# infra/deploy/device-ca there so EMQX can read the same certs/ca_chain.crt as its 8883 cacertfile.
# A bind mount carries the HOST directory's ownership and overrides whatever the image chowned, so a
# root-owned directory makes provisioning-svc die with "Access to the path '/var/step/secrets' is
# denied" — service 3 of 23, taking the whole co-located container with it.
#
# 1654 is the `app` user in both container images, and the same uid C124's Kubernetes manifests run
# as. The mode bits are left alone: certs stay 644 so EMQX (a different user) can still read the
# chain, and the CA private keys stay 600 — now readable by app rather than by root.
if [ -d infra/deploy/device-ca ]; then
  chown -R 1654:1654 infra/deploy/device-ca 2>/dev/null \
    || note "could not chown infra/deploy/device-ca to 1654 — provisioning-svc will not start"
fi

mkdir -p "$CERT_DIR"

if [ -f "$REPLICA_PEM" ]; then
  ok "$REPLICA_PEM exists"
else
  openssl req -x509 -newkey rsa:2048 -sha256 -days 365 -nodes \
    -subj "/C=LK/ST=Western/L=Colombo/O=MageRide Replica/CN=${REPLICA_HOSTNAME:-replica.mageride.lk}" \
    -addext "subjectAltName=DNS:${REPLICA_HOSTNAME:-replica.mageride.lk},DNS:*.${REPLICA_HOSTNAME:-replica.mageride.lk},DNS:localhost,IP:127.0.0.1" \
    -keyout "$CERT_DIR/replica.key" \
    -out    "$CERT_DIR/replica.crt" 2>/dev/null \
    || die "openssl could not generate the replica certificate"

  cat "$CERT_DIR/replica.key" "$CERT_DIR/replica.crt" > "$REPLICA_PEM"

  # uid 99 is the `haproxy` user inside haproxy:2.9-alpine. A 600 root-owned pem is unreadable to it
  # and the edge exits with "cannot open the file" — which is one of the two reasons the dev stack's
  # haproxy had never started (the other was `crt <directory>`, fixed in Δ C125).
  chmod 600 "$REPLICA_PEM" "$CERT_DIR/replica.key"
  chown 99:99 "$REPLICA_PEM" 2>/dev/null || note "could not chown $REPLICA_PEM to uid 99 — haproxy may not read it"

  ok "generated a SELF-SIGNED replica certificate. Nothing trusts it, and nothing should."
fi

# -------------------------------------------------------------------------------------
step "4/5  validate"
# -------------------------------------------------------------------------------------
compose_render="$(mktemp)"
trap 'rm -f "$compose_render"' EXIT

if docker compose -f "$COMPOSE" config >"$compose_render" 2>&1; then
  services=$(python3 -c "
import yaml,sys
d=yaml.safe_load(open('$compose_render'))
print(len(d.get('services',{})))
")
  ok "compose renders — $services services (core + one-shots; optional ones are behind profiles)"
else
  die "compose did not render:
      $(tail -3 "$compose_render" | sed 's/^/      /')"
fi

# Both haproxy configs, in one run: the replica's is a deliberate duplicate of the dev one differing
# in a single backend, and a syntax error in either is worth catching here rather than at `up`.
for cfg in infra/deploy/haproxy.cfg infra/replica/haproxy.replica.cfg; do
  case "$cfg" in
    *replica*) pem_dir="$CERT_DIR" ;;
    *)         pem_dir="$CERT_DIR" ;;
  esac

  if docker run --rm \
       -v "$REPO_ROOT/$cfg:/usr/local/etc/haproxy/haproxy.cfg:ro" \
       -v "$REPO_ROOT/$pem_dir:/usr/local/etc/haproxy/certs:ro" \
       haproxy:2.9-alpine haproxy -c -f /usr/local/etc/haproxy/haproxy.cfg >/dev/null 2>&1; then
    ok "$cfg passes haproxy -c"
  else
    detail=$(docker run --rm \
      -v "$REPO_ROOT/$cfg:/usr/local/etc/haproxy/haproxy.cfg:ro" \
      -v "$REPO_ROOT/$pem_dir:/usr/local/etc/haproxy/certs:ro" \
      haproxy:2.9-alpine haproxy -c -f /usr/local/etc/haproxy/haproxy.cfg 2>&1 | grep ALERT | head -2)
    if [ "$cfg" = "infra/deploy/haproxy.cfg" ]; then
      # The dev config names mageride-dev.pem, which only exists after dev-up.sh has run. Its
      # absence is not a reason to refuse a replica deploy.
      note "$cfg did not validate here (its own cert may be absent): ${detail}"
    else
      die "$cfg failed haproxy -c:
      ${detail}"
    fi
  fi
done

for f in infra/docker/Dockerfile.appservices \
         infra/docker/Dockerfile.service backend/src/TcpAdapter/Dockerfile \
         backend/src/MageRide.Migrations/Dockerfile infra/replica/haproxy.replica.cfg; do
  [ -f "$f" ] || die "$f is missing and the compose file references it"
done
ok "every Dockerfile and config the compose file mounts exists"

if [ "$dry_run" = 1 ]; then
  printf '\n\033[1m✓ dry run complete — nothing was built and nothing was started.\033[0m\n'
  echo "  to deploy:  bash infra/replica/deploy.sh"
  exit 0
fi

# -------------------------------------------------------------------------------------
step "5/5  build and up"
# -------------------------------------------------------------------------------------
note "this builds three images and starts eleven containers. It is the point of no return for the
      ~16.6 GiB budget — the guardrail above is what decided that is affordable."

docker compose -f "$COMPOSE" build || die "build failed"
ok "images built"

docker compose -f "$COMPOSE" up -d --wait --wait-timeout 600 \
  || {
    echo
    note "one or more containers did not reach healthy. What each is doing:"
    docker compose -f "$COMPOSE" ps
    echo
    note "the most likely cause on a first deploy is a service whose option validator wants a key the
      env templates do not supply — Container 7 drops the referenced projects' appsettings.json by
      design, so every setting comes from the environment. The failing service names the key:"
    docker compose -f "$COMPOSE" logs --tail 40 app-services hot-path 2>&1 | tail -40
    die "stack did not come up healthy"
  }

ok "every container reached healthy"

docker compose -f "$COMPOSE" ps

if [ "$with_monitoring" = 1 ]; then
  step "monitoring (C119's stack, beside this one)"
  bash infra/scripts/observability-up.sh || die "observability stack failed to start"
  ok "observability up — its ~1.6 GB is counted by the guardrail's optional total"
fi

step "seeding synthetic data"
bash infra/replica/seed.sh || die "seeding failed"

step "live budget"
bash infra/replica/guardrail.sh --running

printf '\n\033[1m✓ replica up.\033[0m  smoke it with:  bash infra/replica/smoke.sh\n'
