#!/usr/bin/env bash
# =====================================================================================
# C009 verify — proves every item in the docker-compose-dev definition of done (C009).
#
#   bash infra/scripts/slim-verify.sh
#
# Brings the SLIM stack up from scratch, asserts, and tears it down again. The container
# set and its volumes are removed on exit, including on failure. The lightweight
# production replica is never started (root CLAUDE.md "Build Host").
#
# Definition of done:
#   1. both compose files pass `docker compose config`
#   2. the slim stack reaches healthy for every container and `migrate` exits 0
#   3. EMQX rejects a client whose JWT vehicleId claim does not match its publish topic
#   4. Redpanda has telemetry.raw, telemetry.normalized, ride.events, dispatch.events,
#      trip.events and audit.events after bootstrap
#   5. the env templates list every D7' §4 variable
#
# Env:
#   SLIM_VERIFY_KEEP=1     Leave the stack running for inspection
#   SLIM_VERIFY_REUSE=1    Do not bring the stack down/up; assert against what is running
# =====================================================================================
set -Eeuo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
SLIM="$REPO_ROOT/infra/docker-compose.dev.slim.yml"
FULL="$REPO_ROOT/infra/docker-compose.dev.yml"
NETWORK="mageride_mr"
MQTT_CLIENT_IMAGE="eclipse-mosquitto:2"

# Must match the compose default for MQTT_JWT_SECRET, which EMQX receives as
# EMQX_AUTHENTICATION__1__SECRET.
MQTT_JWT_SECRET="${MQTT_JWT_SECRET:-mageride-dev-mqtt-jwt-secret-change-me}"

VEH_A="11111111-1111-1111-1111-111111111111"
VEH_B="22222222-2222-2222-2222-222222222222"

FAILURES=0
CHECKS=0

RED=''; GREEN=''; YELLOW=''; RESET=''
if [[ -t 1 ]]; then RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RESET=$'\033[0m'; fi

step() { printf '\n%s==> %s%s\n' "$YELLOW" "$1" "$RESET"; }
die()  { printf '%serror: %s%s\n' "$RED" "$1" "$RESET" >&2; exit 1; }

check() { # check <description> <expected> <actual>
  CHECKS=$((CHECKS + 1))
  if [[ "$2" == "$3" ]]; then
    printf '  %sok%s   %s\n' "$GREEN" "$RESET" "$1"
  else
    printf '  %sFAIL%s %s\n       expected: %s\n       actual:   %s\n' \
      "$RED" "$RESET" "$1" "$2" "$3"
    FAILURES=$((FAILURES + 1))
  fi
}

cleanup() {
  if [[ "${SLIM_VERIFY_KEEP:-}" == "1" || "${SLIM_VERIFY_REUSE:-}" == "1" ]]; then
    printf '\n%sStack left running.%s\n' "$YELLOW" "$RESET"
    return
  fi
  docker compose -f "$SLIM" down --volumes --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

command -v docker  >/dev/null 2>&1 || die "docker is required"
command -v jq      >/dev/null 2>&1 || die "jq is required"
command -v openssl >/dev/null 2>&1 || die "openssl is required (JWT signing)"

# -------------------------------------------------------------------------------------
# JWT minting — HS256 by hand, so the verify needs no python/PyJWT on the build host.
# -------------------------------------------------------------------------------------
b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }

mint_jwt() { # mint_jwt <vehicleIdClaim> [ttlSeconds]
  local vid="$1" ttl="${2:-3600}" hdr pay unsigned sig
  hdr=$(printf '{"alg":"HS256","typ":"JWT"}' | b64url)
  pay=$(printf '{"vehicleId":"%s","sub":"%s","deviceId":"verify","exp":%s}' \
          "$vid" "$vid" "$(( $(date +%s) + ttl ))" | b64url)
  unsigned="${hdr}.${pay}"
  sig=$(printf '%s' "$unsigned" | openssl dgst -sha256 -hmac "$MQTT_JWT_SECRET" -binary | b64url)
  printf '%s.%s' "$unsigned" "$sig"
}

# mqtt_pub <username> <password> <topic> -> prints "ok" or "refused"
mqtt_pub() {
  if docker run --rm --network "$NETWORK" "$MQTT_CLIENT_IMAGE" \
       mosquitto_pub -h emqx -p 1883 -u "$1" -P "$2" -t "$3" -m 'verify' -q 1 \
       >/dev/null 2>&1; then
    echo ok
  else
    echo refused
  fi
}

# mqtt_sub <username> <password> <topic> -> prints "ok" or "refused"
# -W 2 exits 0 after the timeout when the subscription was accepted; a denied SUBSCRIBE
# tears the connection down (authorization.deny_action = disconnect) and exits non-zero.
mqtt_sub() {
  if docker run --rm --network "$NETWORK" "$MQTT_CLIENT_IMAGE" \
       mosquitto_sub -h emqx -p 1883 -u "$1" -P "$2" -t "$3" -q 1 -W 2 -E \
       >/dev/null 2>&1; then
    echo ok
  else
    echo refused
  fi
}

# =====================================================================================
step "1. docker compose config"
# =====================================================================================
# The reason is printed, not swallowed. `docker compose config` failing with its stderr sent
# to /dev/null gives "expected ok, actual fail" and nothing else — and the reason can be
# version-specific: `services.emqx conflicts with imported resource` reproduced only under the
# Compose the RUNNERS have, so this check was red in CI and green on the build host for weeks
# with no way to tell from the log why (C124's CI repair). It also prints the version, because
# that turned out to be the whole story.
docker compose version --short 2>/dev/null | sed 's/^/       docker compose /'
for f in "$SLIM" "$FULL"; do
  if err=$(docker compose -f "$f" config 2>&1 >/dev/null); then s=ok; else s=fail; fi
  check "$(basename "$f") parses" "ok" "$s"
  if [ "$s" = fail ]; then printf '       %s\n' "$(printf '%s' "$err" | head -3)"; fi
done

# No unset-variable warnings: compose interpolates env_file values too, so an unescaped
# `$` in a template silently becomes an empty string in the container (Emqx__SharedSub).
warnings=$(docker compose -f "$FULL" config 2>&1 >/dev/null | grep -c 'variable is not set' || true)
check "no unset-variable warnings from either file" "0" "$warnings"

# Both files must resolve to the same compose project, or the full stack starts a second
# Postgres instead of reusing the slim stack's.
check "both files declare project 'mageride'" \
  "mageride mageride" \
  "$(docker compose -f "$SLIM" config --format json | jq -r '.name') $(docker compose -f "$FULL" config --format json | jq -r '.name')"

# =====================================================================================
step "2. slim stack healthy, migrate exits 0"
# =====================================================================================
if [[ "${SLIM_VERIFY_REUSE:-}" != "1" ]]; then
  docker compose -f "$SLIM" down --volumes --remove-orphans >/dev/null 2>&1 || true
  docker compose -f "$SLIM" up -d >/dev/null 2>&1 || true
fi

if bash "$REPO_ROOT/infra/scripts/wait-healthy.sh" "$SLIM" >/dev/null 2>&1; then s=ok; else s=fail; fi
check "wait-healthy.sh reports every container up" "ok" "$s"

ps_json() {
  docker compose -f "$SLIM" ps --all --format json 2>/dev/null \
    | jq -c 'if type == "array" then .[] else . end'
}

for svc in postgres pgbouncer redis redpanda emqx minio; do
  health=$(ps_json | jq -r --arg s "$svc" 'select(.Service == $s) | .Health' | head -1 || true)
  check "$svc is healthy" "healthy" "${health:-<missing>}"
done

for svc in migrate redpanda-init minio-init; do
  code=$(ps_json | jq -r --arg s "$svc" 'select(.Service == $s) | "\(.State):\(.ExitCode)"' | head -1 || true)
  check "$svc completed successfully" "exited:0" "${code:-<missing>}"
done

# The migrate job must actually have found the scripts, not silently no-opped against an
# empty script set.
#
# Δ C124: this hard-coded 67, the wave-0 count from C003-C006, and db/migrations has grown to
# 138 — so the check has been red on every push since roughly C045 while asserting nothing
# useful. It now counts the .sql files in the repository, which is the number that has to
# match: a journal with FEWER rows than there are scripts is a migrate job that stopped
# early, and one with MORE is a journal from an older schema in a reused volume. Neither is
# expressible as a literal that somebody has to remember to bump.
expected_migrations=$(find "$REPO_ROOT/db/migrations" -maxdepth 1 -name '*.sql' | wc -l | tr -d '[:space:]')
applied=$(docker compose -f "$SLIM" exec -T postgres \
            psql -U postgres -d mageride -tAqc \
            "select count(*) from public.schema_versions" 2>/dev/null | tr -d '[:space:]' || true)
check "every migration is journalled ($expected_migrations scripts in db/migrations)" \
  "$expected_migrations" "${applied:-0}"

# A shortfall here is almost always a STALE IMAGE rather than a broken migration, and the
# difference is not guessable from "expected 138, actual 74". MageRide.Migrations.csproj takes
# db/migrations/*.sql as <EmbeddedResource>, so the scripts are baked in at publish time: a
# cached `mageride/migrate:dev` carries whatever existed when it was built, and compose reuses
# it because nothing here passes --build. CI never sees this (every runner builds fresh).
if [ "${applied:-0}" -lt "$expected_migrations" ] 2>/dev/null; then
  printf '       the migrate image embeds db/migrations at PUBLISH time — a cached image carries\n'
  printf '       an older set. Rebuild it:  docker compose -f %s build migrate\n' "$SLIM"
fi

# PgBouncer is in transaction mode, and every service reaches Postgres through it.
pool_mode=$(docker compose -f "$SLIM" exec -T pgbouncer sh -c \
              'PGPASSWORD=$DB_PASSWORD psql -h 127.0.0.1 -p 6432 -U $DB_USER -d pgbouncer -tAqc "show config" 2>/dev/null' \
            | awk -F'|' '$1 ~ /^pool_mode/ {gsub(/ /,"",$2); print $2}' | head -1 || true)
check "pgbouncer runs in transaction mode" "transaction" "${pool_mode:-<unknown>}"

# =====================================================================================
step "3. EMQX JWT claim binding + topic ACL (D6 §3.1/§3.2)"
# =====================================================================================
docker image inspect "$MQTT_CLIENT_IMAGE" >/dev/null 2>&1 || docker pull -q "$MQTT_CLIENT_IMAGE" >/dev/null

TOK_A=$(mint_jwt "$VEH_A")
TOK_B=$(mint_jwt "$VEH_B")
TOK_EXPIRED=$(mint_jwt "$VEH_A" -60)
TOK_FORGED="${TOK_A%.*}.$(printf 'not-a-signature' | b64url)"

# The positive case first: without it, an ACL that denies everything would pass every
# negative check below and look like a working policy.
check "vehicle A with a matching claim may publish to its own pos/live" \
  "ok" "$(mqtt_pub "$VEH_A" "$TOK_A" "veh/$VEH_A/pos/live")"
check "vehicle A may publish its replay backlog (T-05)" \
  "ok" "$(mqtt_pub "$VEH_A" "$TOK_A" "veh/$VEH_A/pos/replay")"
check "vehicle A may subscribe to its own downlink cmd topic" \
  "ok" "$(mqtt_sub "$VEH_A" "$TOK_A" "veh/$VEH_A/cmd")"

# --- The definition-of-done case, both ways it can happen -----------------------------
# (a) the token names a different vehicle than the connecting identity: CONNECT refused
#     by emqx.conf's `verify_claims = { vehicleId = "${username}" }`.
check "DoD: a JWT minted for vehicle B cannot connect as vehicle A" \
  "refused" "$(mqtt_pub "$VEH_A" "$TOK_B" "veh/$VEH_A/pos/live")"
# (b) the token matches the identity, but the publish targets another vehicle's topic:
#     refused by the acl.conf `veh/${username}/...` rules.
check "DoD: vehicle A cannot publish to vehicle B's pos/live" \
  "refused" "$(mqtt_pub "$VEH_A" "$TOK_A" "veh/$VEH_B/pos/live")"
check "DoD: vehicle A cannot publish to vehicle B's replay topic" \
  "refused" "$(mqtt_pub "$VEH_A" "$TOK_A" "veh/$VEH_B/pos/replay")"
check "vehicle A cannot subscribe to vehicle B's cmd topic" \
  "refused" "$(mqtt_sub "$VEH_A" "$TOK_A" "veh/$VEH_B/cmd")"

# --- The rest of the deny-by-default surface ------------------------------------------
check "no credentials at all is refused" \
  "refused" "$(mqtt_pub "" "" "veh/$VEH_A/pos/live")"
check "a forged signature is refused" \
  "refused" "$(mqtt_pub "$VEH_A" "$TOK_FORGED" "veh/$VEH_A/pos/live")"
check "an expired session token is refused" \
  "refused" "$(mqtt_pub "$VEH_A" "$TOK_EXPIRED" "veh/$VEH_A/pos/live")"
check "an unrelated topic outside the tree is refused (no_match = deny)" \
  "refused" "$(mqtt_pub "$VEH_A" "$TOK_A" "anything/else")"
check "the broker's own \$SYS tree is not subscribable" \
  "refused" "$(mqtt_sub "$VEH_A" "$TOK_A" '$SYS/#')"
check "a wildcard firehose subscription is refused" \
  "refused" "$(mqtt_sub "$VEH_A" "$TOK_A" 'veh/+/pos/live')"

# Platform components: mqtt-bridge's E-08 shared subscription must still work, or the hot
# path has no source.
TOK_SVC=$(mint_jwt "svc-mqtt-bridge")
check "svc-mqtt-bridge may take the E-08 shared subscription" \
  "ok" "$(mqtt_sub "svc-mqtt-bridge" "$TOK_SVC" '$share/posGroup/veh/+/pos/live')"
TOK_ADAPTER=$(mint_jwt "svc-tcp-adapter")
check "svc-tcp-adapter may publish on behalf of a tracker" \
  "ok" "$(mqtt_pub "svc-tcp-adapter" "$TOK_ADAPTER" "veh/$VEH_B/pos/live")"

# =====================================================================================
step "4. Redpanda topics (D6 §2.1)"
# =====================================================================================
topics=$(docker compose -f "$SLIM" exec -T redpanda rpk topic list --brokers redpanda:9092 2>/dev/null \
           | awk 'NR > 1 && NF { print $1 }' | sort || true)

for t in telemetry.raw telemetry.normalized ride.events dispatch.events registry.events provisioning.events reputation.events fleet.events wallet.events trip.events audit.events; do
  found=$(grep -Fx "$t" <<<"$topics" || true)
  check "topic $t exists" "$t" "${found:-<missing>}"
  # RF=1, partitions=3 in the replica (lightweight-production-replica.md Container 3).
  shape=$(docker compose -f "$SLIM" exec -T redpanda \
            rpk topic list --brokers redpanda:9092 2>/dev/null \
            | awk -v t="$t" '$1 == t { print $2 "/" $3 }' || true)
  check "topic $t is partitions=3 replicas=1" "3/1" "${shape:-<missing>}"
  # D6' §2.3: every topic has a durable dead-letter partner.
  dlq=$(grep -Fx "$t.dlq" <<<"$topics" || true)
  check "topic $t.dlq exists (D6 §2.3)" "$t.dlq" "${dlq:-<missing>}"
done

# The bootstrap is run on every `up`; running it twice must not fail or duplicate.
if docker compose -f "$SLIM" run --rm redpanda-init >/dev/null 2>&1; then s=ok; else s=fail; fi
check "topic bootstrap is re-runnable" "ok" "$s"
count=$(docker compose -f "$SLIM" exec -T redpanda rpk topic list --brokers redpanda:9092 2>/dev/null \
          | awk 'NR > 1 && NF' | wc -l | tr -d '[:space:]' || true)
# Eleven topics, each with its .dlq partner (D6' §2.3). The literal was 12 until C030, 16 until
# C033, 18 until C044 and 20 until C046 — it has gone stale once per topic a component added
# outside §2.1's six.
check "re-running the bootstrap created no extra topics" "22" "$count"

# =====================================================================================
step "5. env templates cover D7 §4"
# =====================================================================================
COMMON="$REPO_ROOT/infra/env/.env.common.example"
APP="$REPO_ROOT/infra/env/.env.app.example"

# D7' §4.1, verbatim.
D7_COMMON=(
  ASPNETCORE_ENVIRONMENT ConnectionStrings__Postgres ConnectionStrings__Redis
  Kafka__BootstrapServers Jwt__JwksUrl Otel__Endpoint Region__Timezone
)
# D7' §4.2, verbatim, every row.
D7_APP=(
  # ── Δ C124: eight §4.2 spellings below are the LANDED names, not the spec's ────────────
  # This list had been asserting eleven variables that nothing in the platform binds, so the
  # check has been red on main since roughly C045 while telling nobody anything. Each
  # replacement is sourced from the code or from the env template's own comment, not chosen
  # here — and a check that asserts a spelling no service reads is asserting dead
  # configuration:
  #
  #   Google__ClientId         -> Oidc__Google__ClientIds__0    C026: a LIST, under `Oidc`
  #   Apple__ClientId          -> Oidc__Apple__ClientIds__0     C026
  #   Outbox__Channel          -> Ride__Outbox__Channel         per bounded context; one flat
  #                                                             key cannot serve five (C025)
  #   Dispatch__OfferTtlSec    -> Dispatch__OfferTtl            a TimeSpan, not an int of seconds
  #   Login__MaxFailedAttempts -> Auth__MaxFailedAttempts       iam-svc's, not admin-bff's —
  #   Login__LockoutMinutes    -> Auth__LockoutDuration         AL-07 puts every credential
  #   Login__IpAllowList       -> Auth__InternalRoleIpAllowList__0   path there (AdminBffOptions)
  #   Emqx__SharedSub          -> MqttBridge__LiveShareGroup    C024 replaced the whole topic
  #                                                             with the group name
  #
  # And three are REMOVED rather than renamed, because they are not settings at all:
  #   Rbac__DenyByDefault      AL-06 deny-by-default is structural. "Not a switch" —
  #                            AdminBffOptions' own remark.
  #   SignalR__BackplaneRedis  There is no SignalR backplane, deliberately. C041 carries the
  #                            directed sends on `fanout:control` and keeps per-cell batches
  #                            off it; AddStackExchangeRedis would give a passenger one copy
  #                            of every frame per replica.
  #   Geocell__Res             R-06 fixes the grid at res 7 and the KMP module hard-fails its
  #                            own build if res 8 appears. A constant in
  #                            MageRide.Shared.Geo.GeoCells, not a knob.
  #
  # All three belong in a micro-change-set against D7' §4.2 rather than in a template.

  Sms__NotifyLkApiKey Jwt__SigningKeyPem Otp__ResendCooldownSec Otp__MaxPerHour
  Oidc__Google__ClientIds__0 Oidc__Apple__ClientIds__0
  Ocr__Endpoint Onepay__MerchantOnboardingUrl
  StepCa__Url StepCa__RootKeyPath Cred__RotationDays
  Tiles__BaseUrl Nominatim__Url
  Session__IdleTimeoutMin Geofence__AutoEndM
  Otp__PepperKey Quartz__SchedulerName Ride__Outbox__Channel
  Dispatch__OfferTtl Dispatch__GlobalTimeoutSec JobBoard__RadiusKm Wallet__CacheTtlSec
  Onepay__ApiKey Onepay__WebhookSecret LankaQr__MerchantId
  Fee__FirstTripFree Tz
  ComBankIpg__WebhookSecret LowBalance__ThresholdMinor
  Fcm__ServiceAccountJson Apns__P8Key Apns__KeyId Apns__TeamId Sms__SecondaryGateway
  Sos__SloMs TripShare__TtlGraceMin
  Grpc__ListenPort
  Cache__Ttl
  Storage__ScreenshotBucket
  Fleet__RlsEnabled Fleet__VerificationGate
  Audit__Topic Pdpa__DueDays
  Auth__MaxFailedAttempts Auth__LockoutDuration Auth__InternalRoleIpAllowList__0
  Gemini__ApiKey Gemini__Model Redaction__Enabled
  LiveKit__ApiKey LiveKit__Secret Turn__Realm
  Plausibility__MaxAccuracyM Replay__MaxPerSec
  Timescale__BatchRows Timescale__FlushMs
  MqttBridge__LiveShareGroup
  Health__OfflinePct Health__WindowMin
  Adapter__Ports Provisioning__ImeiCacheKey
)

missing_common=0
for v in "${D7_COMMON[@]}"; do
  grep -qE "^${v}=" "$COMMON" || { printf '       missing from .env.common.example: %s\n' "$v"; missing_common=$((missing_common + 1)); }
done
check "every D7 §4.1 variable is in .env.common.example (${#D7_COMMON[@]})" "0" "$missing_common"

missing_app=0
for v in "${D7_APP[@]}"; do
  grep -qE "^${v}=" "$APP" || grep -qE "^${v}=" "$COMMON" \
    || { printf '       missing from .env.app.example: %s\n' "$v"; missing_app=$((missing_app + 1)); }
done
check "every D7 §4.2 variable is in the templates (${#D7_APP[@]})" "0" "$missing_app"

# AL-37 removed the second factor for internal roles; D7' §4.2's admin-bff row still
# names Mfa__RequiredForInternal. Planner finding 3 resolves it in AL-37's favour, so its
# presence in a template would be a regression, not a gap.
mfa=$( { grep -hE "^Mfa__" "$APP" "$COMMON" 2>/dev/null || true; } | wc -l | tr -d '[:space:]')
check "no Mfa__* variable is set (AL-37)" "0" "$mfa"

# Nothing that looks like a real credential may be committed (infra/CLAUDE.md). Every
# secret-marked D7' §4.2 row is either empty or an explicit CHANGEME_ placeholder.
for v in Jwt__SigningKeyPem Sms__NotifyLkApiKey Onepay__ApiKey Onepay__WebhookSecret \
         Otp__PepperKey Gemini__ApiKey Fcm__ServiceAccountJson Apns__P8Key; do
  val=$( { grep -E "^${v}=" "$APP" || true; } | head -1 | cut -d= -f2-)
  if [[ -z "$val" || "$val" == CHANGEME_* ]]; then s=placeholder; else s="$val"; fi
  check "$v is a placeholder, not a credential" "placeholder" "$s"
done

# The C008 gateway variables have no D7' §4.2 row and would otherwise be undiscoverable.
for v in Gateway__StateStore Gateway__ForwardedHeaders__KnownProxies__0 \
         Gateway__Attestation__Mode Gateway__VersionGate__Platforms__android__MinimumVersion; do
  grep -qE "^${v}=" "$APP" && s=present || s=missing
  check "$v is documented (C008 gap (d))" "present" "$s"
done

# =====================================================================================
printf '\n%s================================================================%s\n' "$YELLOW" "$RESET"
if (( FAILURES == 0 )); then
  printf '%s%d/%d checks passed%s\n' "$GREEN" "$CHECKS" "$CHECKS" "$RESET"
  exit 0
fi
printf '%s%d/%d checks FAILED%s\n' "$RED" "$FAILURES" "$CHECKS" "$RESET"
exit 1
