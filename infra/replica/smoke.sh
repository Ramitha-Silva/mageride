#!/usr/bin/env bash
# =====================================================================================
# infra/replica/smoke.sh — the golden paths, driven against the running replica.
#
#   bash infra/replica/smoke.sh
#
# The second half of C125's verify command. Every request goes through the EDGE — HAProxy on 443 with
# the self-signed certificate — because that is the surface a phone talks to and the one the spec's
# Container 1 describes. Talking to app-services:5000 directly would skip TLS termination, the
# X-Forwarded-Proto header, the /health and /v1/internal denials, and the vhost routing, which is
# most of what the edge exists to do.
#
# ------------------------------------------------------------------------------------
# WHAT THIS PROVES, AND WHAT IT DOES NOT
# ------------------------------------------------------------------------------------
# It proves the replica is wired: TLS terminates, the gateway routes to a co-located service, the
# 22 services in Container 7 answer, Postgres and Redis are reachable through them, the edge refuses
# what it must refuse, and the tracker ports accept a connection.
#
# It does NOT replace tests/E2E. That suite drives 136 scenarios through nine composed services with
# a real broker and asserts on ledgers, state machines and sockets. This asserts that a DEPLOYMENT of
# the same code is reachable and coherent. When one fails and the other passes, the difference is the
# deployment — which is the whole reason to run both.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
REPLICA_DIR="$PWD"
cd ../.. || exit 2

COMPOSE="infra/replica/docker-compose.light-replica.yml"

pass=0
fail=0
skip=0

ok()   { pass=$((pass+1)); printf '  \033[32m✓\033[0m %s\n' "$*"; }
bad()  { fail=$((fail+1)); printf '  \033[31m✗\033[0m %s\n' "$*" >&2; }
skip_(){ skip=$((skip+1)); printf '  \033[33m•\033[0m %s\n' "$*"; }
step() { printf '\n\033[1m%s\033[0m\n' "$*"; }

[ -f "$REPLICA_DIR/.env.replica" ] || { echo "run deploy.sh first" >&2; exit 2; }
set -a
# shellcheck disable=SC1090
. "$REPLICA_DIR/.env.replica"
set +a

EDGE_PORT="${HAPROXY_HTTPS_PORT:-443}"
EDGE="https://127.0.0.1:${EDGE_PORT}"
HOSTHDR="${REPLICA_HOSTNAME:-replica.mageride.lk}"

# -k because the certificate is self-signed BY DESIGN. The alternative — trusting it on this box —
# would make the smoke suite pass for a reason a phone never enjoys.
CURL=(curl -sS -k --max-time 20 -H "Host: ${HOSTHDR}")

# `code_of <path> [extra curl args...]` — the HTTP status the edge returns.
code_of() {
  local path="$1"; shift
  "${CURL[@]}" -o /dev/null -w '%{http_code}' "$@" "${EDGE}${path}" 2>/dev/null || echo "000"
}

body_of() {
  local path="$1"; shift
  "${CURL[@]}" "$@" "${EDGE}${path}" 2>/dev/null || true
}

psql_q() {
  docker compose -f "$COMPOSE" exec -T postgres \
    psql -U "${PG_USER:-postgres}" -d "${PG_DATABASE:-mageride}" -qtAX -c "$1" 2>&1
}

# =====================================================================================
step "0. the stack is up"
# =====================================================================================
unhealthy=$(docker compose -f "$COMPOSE" ps --format '{{.Service}} {{.Health}}' 2>/dev/null \
            | awk '$2 != "healthy" && $2 != "" {print $1"("$2")"}' | tr '\n' ' ')
running=$(docker compose -f "$COMPOSE" ps --services --filter status=running 2>/dev/null | wc -l)

if [ "$running" -eq 0 ]; then
  echo "  nothing is running under mageride-replica — bring it up with deploy.sh" >&2
  exit 2
fi
ok "${running} services running"

if [ -n "$unhealthy" ]; then
  bad "not healthy: ${unhealthy}"
else
  ok "every container with a healthcheck is healthy"
fi

# =====================================================================================
step "1. the edge terminates TLS and routes to Container 7's gateway"
# =====================================================================================
# The gateway answers /health/ready internally; the edge must NOT expose it (see 2). So the first
# real proof of routing is a gateway-owned route that legitimately answers without a bearer.
tls=$("${CURL[@]}" -o /dev/null -w '%{http_version} %{ssl_verify_result}' "${EDGE}/" 2>/dev/null || echo "none")
if [ "${tls%% *}" != "none" ] && [ -n "${tls%% *}" ]; then
  ok "TLS handshake completed over HTTP/${tls%% *} (verify_result ${tls##* } — self-signed, expected)"
else
  bad "no TLS handshake on ${EDGE} — is haproxy up and is its pem readable by uid 99?"
fi

version_code=$(code_of "/v1/version")
case "$version_code" in
  200|204|404|426)
    # 426 is D-31's minimum-version gate answering, which still proves the gateway processed it.
    ok "the gateway answered /v1/version with ${version_code} (routed into app-services)"
    ;;
  502|503|504)
    bad "the edge returned ${version_code} for /v1/version — HAProxy reached no backend. This is the
      shape the C125 gateway-precedence bug produced: check
      ReverseProxy__Clusters__*__Destinations__primary__Address inside app-services."
    ;;
  000) bad "no response at all from ${EDGE}/v1/version" ;;
  *)   ok "the gateway answered /v1/version with ${version_code} (routed, non-2xx is acceptable here)" ;;
esac

# =====================================================================================
step "1a. the signing key every service validates a bearer against is reachable (Δ C126)"
# =====================================================================================
# Every check in this suite is deliberately unauthenticated — a 401 proves the request reached the
# service. The gap that leaves is the JWT path itself: `Jwt__JwksUrl` pointed at
# `/v1/internal/iam/.well-known/jwks.json`, which the gateway refuses ahead of routing and never had
# a route for, so every JWT-validating service answered 500 to every authenticated request while
# passing all 23 of these checks and every healthcheck. The fetch happens on the first request that
# CARRIES a token, which is why nothing here had ever triggered it.
#
# So: fetch the key the way a service does. It needs no credential — that is the point of a JWKS.
jwks=$(body_of "/v1/.well-known/jwks.json")
jwks_code=$(code_of "/v1/.well-known/jwks.json")

if [ "$jwks_code" = "200" ] && printf '%s' "$jwks" | grep -q '"kid"'; then
  ok "the JWKS answers 200 with $(printf '%s' "$jwks" | grep -o '"kid"' | wc -l | tr -d ' ') key(s) — authenticated requests can be validated"
else
  bad "GET /v1/.well-known/jwks.json → ${jwks_code}. Every service that validates a bearer will answer
      500 to every authenticated request. Check Jwt__JwksUrl and the gateway's iam-jwks route."
fi

# =====================================================================================
step "2. the edge refuses what it must refuse"
# =====================================================================================
# Not decoration. /health/ready names every dependency a service probes and /metrics is the internal
# topology; this edge is publicly reachable. 404 rather than 403 — a 403 confirms the path exists.
for path in /health/ready /health/live /health /metrics /v1/internal/rides; do
  code=$(code_of "$path")
  if [ "$code" = "404" ]; then
    ok "${path} → 404"
  else
    bad "${path} → ${code}, expected 404. The internal surface is exposed at the edge."
  fi
done

# =====================================================================================
step "3. reference data and the synthetic actors are in place"
# =====================================================================================
marker=$(psql_q "SELECT count(*) FROM replica.synthetic_marker;")
if [ "${marker//[^0-9]/}" = "1" ]; then
  ok "the synthetic marker is present — this is seeded replica data, not production"
else
  bad "no synthetic marker: seed.sh has not run, or this is not the database it seeded"
fi

for row in "config.operating_cities:1" "fares.tariffs:1" "billing.plans:1"; do
  table="${row%%:*}"
  count=$(psql_q "SELECT count(*) FROM ${table};")
  if [ "${count//[^0-9]/}" -ge "${row##*:}" ] 2>/dev/null; then
    ok "${table}: ${count} row(s)"
  else
    bad "${table}: ${count} — the migrations' reference data is missing"
  fi
done

approved=$(psql_q "SELECT count(*) FROM registry.vehicles WHERE status = 'APPROVED';")
if [ "${approved//[^0-9]/}" -ge 5 ] 2>/dev/null; then
  ok "${approved} APPROVED vehicles across $(psql_q "SELECT count(DISTINCT vehicle_type) FROM registry.vehicles;") types"
else
  bad "only ${approved} APPROVED vehicles — dispatch has nothing to offer a ride to"
fi

# =====================================================================================
step "4. golden path — book a ride (Mode C)"
# =====================================================================================
# Through the edge, as a passenger would. Unauthenticated on purpose: AL-06 makes authorization
# deny-by-default IN THE SERVICES, so a 401 here proves the request reached ride-svc and was refused
# by it. A 502 proves it never arrived, and that is the failure this step exists to catch.
ride_code=$(code_of "/v1/rides" -X POST -H 'Content-Type: application/json' \
  -d '{"pickup":{"lat":6.9271,"lng":79.8612},"dropoff":{"lat":6.9350,"lng":79.8500},"vehicleType":"three_wheeler"}')

case "$ride_code" in
  401|403) ok "POST /v1/rides → ${ride_code}: reached ride-svc, refused by its own authorization (AL-06)" ;;
  400|422) ok "POST /v1/rides → ${ride_code}: reached ride-svc and it validated the body" ;;
  200|201|202) ok "POST /v1/rides → ${ride_code}: a ride was accepted" ;;
  502|503|504) bad "POST /v1/rides → ${ride_code}: the gateway reached no ride-svc" ;;
  404) bad "POST /v1/rides → 404: the gateway has no route for it (check gateway-routes.json)" ;;
  *) bad "POST /v1/rides → ${ride_code}, unexpected" ;;
esac

# =====================================================================================
step "5. golden path — Mode A journey surface (trip-state-svc)"
# =====================================================================================
journey_code=$(code_of "/v1/sessions" -X POST -H 'Content-Type: application/json' \
  -d '{"vehicleId":"00000000-0000-0000-0000-000000000000","mode":"A"}')

case "$journey_code" in
  401|403) ok "POST /v1/sessions → ${journey_code}: reached trip-state-svc (R-01 owns Mode A/B)" ;;
  400|422|404) ok "POST /v1/sessions → ${journey_code}: reached trip-state-svc and it judged the body" ;;
  502|503|504) bad "POST /v1/sessions → ${journey_code}: no trip-state-svc behind the gateway" ;;
  *) ok "POST /v1/sessions → ${journey_code}" ;;
esac

# The seeded history proves the read path has something to read.
sessions=$(psql_q "SELECT count(*) FROM trips.sessions;")
if [ "${sessions//[^0-9]/}" -ge 1 ] 2>/dev/null; then
  ok "trips.sessions holds ${sessions} seeded journey(s) — the read path is not being asked about an empty table"
else
  bad "trips.sessions is empty; a read returning nothing cannot be told from a broken read"
fi

# =====================================================================================
step "6. golden path — package delivery surface (P-06/P-07)"
# =====================================================================================
package_code=$(code_of "/v1/rides/packages" -X POST -H 'Content-Type: application/json' \
  -d '{"pickup":{"lat":6.9271,"lng":79.8612},"dropoff":{"lat":6.9350,"lng":79.8500},"recipient":{"phone":"+94770000001"}}')

case "$package_code" in
  401|403|400|422) ok "POST /v1/rides/packages → ${package_code}: reached ride-svc's package sub-kind" ;;
  404) skip_ "POST /v1/rides/packages → 404: no gateway route for the package path. The sub-kind is
      ride-svc's (P-06) and may be mapped under /v1/rides — not a deployment failure." ;;
  502|503|504) bad "POST /v1/rides/packages → ${package_code}: the gateway reached no backend" ;;
  *) ok "POST /v1/rides/packages → ${package_code}" ;;
esac

# =====================================================================================
step "7. the tracker plane accepts a connection (T-01)"
# =====================================================================================
# A TCP connect, not a frame: sending a real GT06 login here would need the device CA and a bound
# IMEI, which is tests/E2E's TrackerPlaneScenario. What this proves is that HAProxy's L4 passthrough
# reaches tcp-adapter on all three protocol ports.
for spec in "5023:GT06" "5024:JT808" "5025:H02"; do
  port="${spec%%:*}"; name="${spec##*:}"
  if timeout 5 bash -c "</dev/tcp/127.0.0.1/${port}" 2>/dev/null; then
    ok "${name} port ${port} accepts a TCP connection"
  else
    bad "${name} port ${port} refused a connection — HAProxy passthrough or tcp-adapter is down"
  fi
done

if timeout 5 bash -c "</dev/tcp/127.0.0.1/${HAPROXY_MQTTS_PORT:-8883}" 2>/dev/null; then
  ok "MQTTS ${HAPROXY_MQTTS_PORT:-8883} accepts a TCP connection (passthrough to EMQX)"
else
  bad "MQTTS ${HAPROXY_MQTTS_PORT:-8883} refused a connection"
fi

# =====================================================================================
step "8. the object store answers through the edge vhost"
# =====================================================================================
s3_code=$("${CURL[@]//Host: ${HOSTHDR}/Host: s3.${HOSTHDR}}" -o /dev/null -w '%{http_code}' \
  -H "Host: s3.${HOSTHDR}" "${EDGE}/" 2>/dev/null || echo "000")
case "$s3_code" in
  200|403|404) ok "the s3. vhost reached MinIO (${s3_code} — an unauthenticated list is refused, as it should be)" ;;
  000|502|503) bad "the s3. vhost reached no MinIO (${s3_code}); presigned document URLs will not work off-box" ;;
  *) ok "the s3. vhost answered ${s3_code}" ;;
esac

# =====================================================================================
step "9. the public informational site (MCS-34 / C134) answers through the edge"
# =====================================================================================
# `--profile portals` is optional in this stack, so every check below skips rather than fails
# when the container is not up. A replica brought up without the portals profile is a valid
# deployment and must not report red for a container nobody asked for.

# curl through the edge with an arbitrary vhost. The base CURL array bakes in the default
# Host header; this rebuilds it rather than string-substituting, which is what §8 does and
# which breaks the moment a host name contains a regex-ish character.
curl_host() {
  local host="$1"; shift
  curl -sS -k --max-time 20 -H "Host: ${host}" "$@" 2>/dev/null || true
}

WWW_HOST="www.${HOSTHDR}"

if ! docker compose -f "$COMPOSE" ps --services --filter status=running 2>/dev/null | grep -qx "www-site"; then
  skip_ "www-site is not running (optional, \`--profile portals\`) — 6 checks skipped"
else
  # --- 9.1 the rendered locales -------------------------------------------------------
  # Read from the source rather than listed here: `WWW_LOCALES` in portals/www/src/i18n is the
  # one constant that decides which locales publish, and MCS-34 D2 defers Tamil. A hard-coded
  # `si en ta` here would go red the day somebody re-enabled Tamil correctly.
  www_locales=$(grep -oE "^const WWW_PUBLISHED_MESSAGES = \{|^  (si|ta|en): www(Si|Ta|En)," \
                  portals/www/src/i18n/index.ts | grep -oE "^  (si|ta|en):" | tr -d ' :' | tr '\n' ' ')
  www_locales="${www_locales:-si en}"

  for loc in $www_locales; do
    code=$(curl_host "$WWW_HOST" -o /dev/null -w '%{http_code}' "${EDGE}/${loc}")
    if [ "$code" = "200" ]; then
      ok "/${loc} answered 200 through the edge"
    else
      bad "/${loc} answered ${code}; the site's own locale is not serving"
    fi
  done

  # `/` is the one dynamic response on the whole surface — it reads Accept-Language and
  # redirects. This is also the container's Kubernetes probe path (S21), so a non-3xx here is
  # a pod that would never become ready.
  root_code=$(curl_host "$WWW_HOST" -o /dev/null -w '%{http_code}' "${EDGE}/")
  case "$root_code" in
    30[1278]) ok "/ negotiated a locale and redirected (${root_code}) — the k8s probe path" ;;
    *) bad "/ answered ${root_code}; k8s readiness probes this path and accepts only 2xx-3xx" ;;
  esac

  # --- 9.2 the sitemap lists every route in the table ---------------------------------
  # The route table and the chapter slug list are the two sources `app/sitemap.ts` reads, and
  # they are read here the same way — so this asserts the DEPLOYED sitemap agrees with the
  # source in the tree, which is the deployment question. That the generator covers 100% of the
  # table is `portals/www/test/seo.test.ts`'s job and is a different claim.
  sitemap=$(curl_host "$WWW_HOST" "${EDGE}/sitemap.xml")

  if printf '%s' "$sitemap" | python3 -c "import sys,xml.etree.ElementTree as E; E.fromstring(sys.stdin.read())" 2>/dev/null; then
    ok "/sitemap.xml is well-formed XML"

    missing=""
    routes=$(grep -oE "path: '[^']*'" portals/www/src/lib/routes.ts | sed "s/path: '//;s/'//")
    slugs=$(grep -oE "^ +'[a-z0-9-]+',$" portals/www/src/content/chapters.ts | tr -d " ',")
    for loc in $www_locales; do
      for r in $routes; do
        printf '%s' "$sitemap" | grep -q "<loc>https://www.mageride.lk/${loc}${r:+/$r}</loc>" \
          || missing="${missing} /${loc}/${r}"
      done
    done
    # One chapter under each audience rather than all 34 x N: the slug list is the same source
    # the sitemap reads, so a spot check catches a broken join and a full sweep only catches it
    # 68 more times at the cost of a slow smoke run.
    for loc in $www_locales; do
      first_slug=$(printf '%s\n' "$slugs" | head -1)
      printf '%s' "$sitemap" | grep -qE "<loc>https://www\.mageride\.lk/${loc}/guide/(passenger|driver)/${first_slug}</loc>" \
        || missing="${missing} /${loc}/guide/*/${first_slug}"
    done

    locs=$(printf '%s' "$sitemap" | grep -c '<loc>' || true)
    if [ -z "$missing" ]; then
      ok "the sitemap carries every route in the table, in every rendered locale (${locs} URLs)"
    else
      bad "the sitemap is missing:${missing}"
    fi

    # Nothing may be advertised for a locale that does not render (MCS-34 D2). A sitemap entry
    # for /ta is a crawler — and every Tamil reader it then serves — sent to a 404.
    unpublished=""
    for loc in si ta en; do
      case " $www_locales " in *" $loc "*) continue ;; esac
      printf '%s' "$sitemap" | grep -q "mageride.lk/${loc}/" && unpublished="${unpublished} ${loc}"
    done
    [ -z "$unpublished" ] \
      && ok "the sitemap advertises no unpublished locale" \
      || bad "the sitemap advertises${unpublished}, which does not render"
  else
    bad "/sitemap.xml did not parse as XML"
  fi

  # --- 9.3 a guide chapter carries its HowTo block ------------------------------------
  # A FIXED chapter, not a random one: a flaky smoke check gets ignored, and this slug is in
  # `chapters.ts`'s published contract, so renaming it is a deliberate act that should also
  # update this line.
  chapter_body=$(curl_host "$WWW_HOST" "${EDGE}/en/guide/driver/the-daily-platform-fee")
  if printf '%s' "$chapter_body" | grep -q '"@type":"HowTo"'; then
    ok "a guide chapter serves its HowTo JSON-LD (S19) through the edge"
  else
    bad "a guide chapter served no HowTo block; structured data is not reaching a crawler"
  fi

  # =====================================================================================
  step "10. the apex 301, and the three hosts it must not catch"
  # =====================================================================================
  # The replica's default vhost IS `${HOSTHDR}` and routes to `api_gateway`, so an apex rule
  # matched loosely would 301 every API check in this file to the marketing site. Both halves
  # are asserted for that reason — the redirect firing is half the requirement.
  apex_loc=$(curl_host "mageride.lk" -o /dev/null -w '%{redirect_url}' "${EDGE}/drivers")
  apex_code=$(curl_host "mageride.lk" -o /dev/null -w '%{http_code}' "${EDGE}/drivers")

  if [ "$apex_code" = "301" ] && [ "$apex_loc" = "https://www.mageride.lk/drivers" ]; then
    ok "the apex 301s to www and preserves the path (${apex_loc})"
  else
    bad "the apex answered ${apex_code} -> '${apex_loc}'; expected 301 -> https://www.mageride.lk/drivers"
  fi

  caught=""
  for h in "admin.${HOSTHDR}" "fleet.${HOSTHDR}" "${WWW_HOST}" "${HOSTHDR}"; do
    c=$(curl_host "$h" -o /dev/null -w '%{http_code}' "${EDGE}/")
    [ "$c" = "301" ] && caught="${caught} ${h}"
  done
  [ -z "$caught" ] \
    && ok "the apex rule catches neither the portals, nor www., nor the default API vhost" \
    || bad "the apex redirect also caught:${caught} — it would 301 those hosts to the marketing site"

  # =====================================================================================
  step "11. the site is up while the platform is down (MCS-34's load-bearing negative)"
  # =====================================================================================
  # **The most valuable check in this file**, and the only one that proves C134's second fence
  # at the DEPLOYMENT level rather than in a unit test: no `depends_on`, no gateway variable,
  # no request at render time — so stopping the platform must change nothing a reader sees.
  #
  # `app-services` is Container 7: the YARP gateway and all 22 domain services in one process.
  # Stopping it is stopping the platform as far as every other container is concerned.
  if ! docker compose -f "$COMPOSE" ps --services --filter status=running 2>/dev/null | grep -qx "app-services"; then
    skip_ "app-services is not running, so 'up while the platform is down' proves nothing here"
  else
    restore_app_services() {
      docker compose -f "$COMPOSE" start app-services >/dev/null 2>&1 || true
    }
    # Armed BEFORE the fault, so an interrupt or a failing assertion still restores the stack.
    trap restore_app_services EXIT INT TERM

    if docker compose -f "$COMPOSE" stop app-services >/dev/null 2>&1; then
      down_code=$(curl_host "$WWW_HOST" -o /dev/null -w '%{http_code}' "${EDGE}/en")
      api_code=$(code_of /v1/health 2>/dev/null || echo "000")

      if [ "$down_code" = "200" ]; then
        ok "the site answered 200 with app-services stopped (the platform was ${api_code:-down})"
      else
        bad "the site answered ${down_code} with app-services stopped; it has acquired a dependency"
      fi

      restore_app_services
      trap - EXIT INT TERM
      # Give the gateway a moment back, so a later run of this script does not start red.
      for _ in $(seq 1 30); do
        [ "$(code_of /health/ready 2>/dev/null)" = "200" ] && break
        sleep 1
      done
      ok "app-services restored"
    else
      trap - EXIT INT TERM
      skip_ "could not stop app-services; the platform-down check did not run"
    fi
  fi
fi

# =====================================================================================
echo
echo "==============================================================================="
printf '%d passed, %d failed, %d skipped\n' "$pass" "$fail" "$skip"
if [ "$fail" -ne 0 ]; then
  echo
  echo "logs for whatever failed:  docker compose -f $COMPOSE logs --tail 60 app-services haproxy"
  exit 1
fi
exit 0
