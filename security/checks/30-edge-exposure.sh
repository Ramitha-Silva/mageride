#!/usr/bin/env bash
# =====================================================================================
# 30 — what the RUNNING edge actually publishes (ASVS V1.4 / V4.1 / V13, ADD §12.2/§12.3)
#
# Everything here needs a deployed replica and skips without one. It is the half of the review a
# file cannot answer: a configuration that blocks a path is not the same fact as an edge that
# refuses it, and C126 found `Jwt__JwksUrl` pointing at a path the gateway refuses only by sending
# a request that carried a token.
#
# THE FENCE: this is pointed at the replica and never at production. `asvs-lib.sh` reads the edge
# out of `infra/replica/.env.replica` or `MAGERIDE_LIVE_EDGE`; it discovers nothing by itself.
#
# Every request below is a READ or a request the platform refuses before any handler. Nothing here
# changes state — the C118 live sweep's first run filed two real PDPA obligations by being less
# careful, and `tests/Contract/Live/LiveRequestPlan.cs` is the write-up.
# =====================================================================================

# shellcheck shell=bash

step "30. the running edge (ASVS V1.4, V4.1)"

if ! edge_available; then
  skip_ "no replica edge to probe — bring one up with infra/replica/deploy.sh, or set MAGERIDE_LIVE_EDGE"
  note "Checks 30.1–30.6 assert what a file cannot: the operational surface, the internal plane,"
  note "the version gate, and that every privileged route refuses an anonymous caller."
  return 0 2>/dev/null || exit 0
fi

probe GET /v1/config/cities -H 'X-App-Version: 99.0.0' -H 'X-Platform: android'
if [ "$ASVS_STATUS" = "000" ]; then
  skip_ "the edge at ${EDGE} did not answer; treating the live checks as not run"
  return 0 2>/dev/null || exit 0
fi

ok "the edge at ${EDGE} answers (Host: ${HOSTHDR})"

# -------------------------------------------------------------------------------------
# 30.1 — the operational surface is not published
#
# The kernel maps /health/live, /health/ready and /metrics anonymously on EVERY service, which is
# correct — a liveness probe that needed a token could not run before the token issuer was up. The
# control is that HAProxy does not put them on the internet, and api-gateway's own CLAUDE.md says
# so in as many words. /metrics is the one that matters most: it carries per-route request counts,
# cluster names and the platform's whole internal topology.
# -------------------------------------------------------------------------------------
for path in /health /health/live /health/ready /metrics; do
  probe GET "$path"
  case "$ASVS_STATUS" in
    404|403|401) ok "GET ${path} is not published at the edge (${ASVS_STATUS})" ;;
    000)         warn "GET ${path} — no answer; inconclusive" ;;
    *)           bad "GET ${path} answered ${ASVS_STATUS} at the public edge. The kernel maps it
      anonymously on every service and HAProxy must not publish it (api-gateway CLAUDE.md)." ;;
  esac
done

# -------------------------------------------------------------------------------------
# 30.2 — the mTLS plane is refused
#
# Driven off the contracts, so it covers the three operations that declare `security: [{ mtls: [] }]`
# without carrying the /v1/internal prefix as well as the forty-six that do.
# -------------------------------------------------------------------------------------
internal_reachable=0
internal_checked=0

while IFS='|' read -r method path; do
  [ -n "$method" ] || continue
  internal_checked=$((internal_checked+1))

  if [ "$method" = "POST" ] || [ "$method" = "PUT" ] || [ "$method" = "PATCH" ]; then
    probe "$method" "$path" -H 'Idempotency-Key: c127-0000-0000-0000-000000000001' \
      -H 'Content-Type: application/json' -d '{}'
  else
    probe "$method" "$path"
  fi

  case "$ASVS_STATUS" in
    404) ;;
    000) warn "${method} ${path} — no answer; inconclusive" ;;
    *)   internal_reachable=$((internal_reachable+1))
         bad "${method} ${path} answered ${ASVS_STATUS} at the public edge; the contract declares it
      mTLS-only (D3' §0) and the edge must answer 404 ahead of routing." ;;
  esac
done < <(python3 - <<'PY'
import re, glob

rows = []
for f in sorted(glob.glob('backend/contracts/*.yaml')):
    lines = open(f).read().split('\n')
    path = method = None
    buf = []

    def flush():
        if not (path and method):
            return
        declared = any(re.match(r'\s*- mtls: \[\]', b) for b in buf)
        if declared or path.startswith('/v1/internal/'):
            rows.append(f"{method.upper()}|{re.sub(r'{[^}]+}', '01JZZZZZZZZZZZZZZZZZZZZZZZ', path)}")

    for ln in lines:
        m = re.match(r'^  (/\S+):\s*$', ln)
        if m:
            flush(); path, method, buf = m.group(1), None, []
            continue
        m = re.match(r'^    (get|post|put|patch|delete):\s*$', ln)
        if m:
            flush(); method, buf = m.group(1), []
            continue
        buf.append(ln)
    flush()

print('\n'.join(sorted(set(rows))))
PY
)

[ "$internal_reachable" -eq 0 ] \
  && ok "all ${internal_checked} mTLS-plane operations answer 404 at the edge" \
  || note "${internal_reachable} of ${internal_checked} mTLS-plane operations were reachable."

# -------------------------------------------------------------------------------------
# 30.3 — privileged routes refuse an anonymous caller (AL-06 deny-by-default, observed)
#
# The in-process probe reads every endpoint's authorization metadata; this asks the deployment.
# One route per privileged family rather than all 444 — the structural sweep is exhaustive and
# this is the transport check that the sweep's premise holds through TLS, HAProxy and the gateway.
# -------------------------------------------------------------------------------------
while read -r method path label; do
  [ -n "$method" ] || continue

  if [ "$method" = "POST" ]; then
    probe "$method" "$path" -H 'Idempotency-Key: c127-0000-0000-0000-000000000002' \
      -H 'Content-Type: application/json' -d '{}'
  else
    probe "$method" "$path"
  fi

  case "$ASVS_STATUS" in
    401|403|404|426) ok "${method} ${path} refuses an anonymous caller (${ASVS_STATUS}) — ${label}" ;;
    000)             warn "${method} ${path} — no answer; inconclusive" ;;
    *)               bad "${method} ${path} answered ${ASVS_STATUS} with NO credential — ${label}" ;;
  esac
done <<'ROUTES'
GET /v1/admin/rbac/matrix the RBAC matrix
GET /v1/admin/audit-log the D-35 audit trail
GET /v1/admin/passengers the passenger directory (AL-40)
GET /v1/admin/drivers the driver directory (AL-41)
GET /v1/admin/pdpa/queue the E-06 data-rights queue
GET /v1/users/me the caller's own profile
GET /v1/wallet/01JZZZZZZZZZZZZZZZZZZZZZZZ a wallet balance
POST /v1/wallet/topup a wallet top-up
GET /v1/admin/finance/reconciliation the settlement position
GET /v1/admin/transit/gtfs/versions the GTFS feed history
ROUTES

# -------------------------------------------------------------------------------------
# 30.4 — the D-31 minimum-version gate, observed
# -------------------------------------------------------------------------------------
probe GET /v1/config/cities -H 'X-App-Version: 0.0.1' -H 'X-Platform: android'
[ "$ASVS_STATUS" = "426" ] \
  && ok "an app below the floor is refused 426 Upgrade Required (D-31)" \
  || bad "an app reporting version 0.0.1 answered ${ASVS_STATUS}; D-31 requires 426 Upgrade Required"

probe GET /v1/config/cities -H 'X-App-Version: 99.0.0' -H 'X-Platform: android'
[ "$ASVS_STATUS" = "200" ] \
  && ok "a current app is served normally, so the gate refuses versions rather than everything" \
  || bad "a current app answered ${ASVS_STATUS} on a public route; the version gate is refusing too much"

# -------------------------------------------------------------------------------------
# 30.5 — the JWKS is published, and carries no private material
# -------------------------------------------------------------------------------------
probe GET /v1/.well-known/jwks.json
if [ "$ASVS_STATUS" = "200" ]; then
  ok "the signing key set is published, so a bearer can be validated at all (D-29, D-21)"

  if printf '%s' "$ASVS_BODY" | grep -qE '"(d|p|q|dp|dq|qi)"'; then
    bad "the published JWKS contains RSA PRIVATE parameters. Every token on the platform is forgeable."
  else
    ok "it carries only the public members (kty, use, alg, kid, n, e)"
  fi

  keys=$(printf '%s' "$ASVS_BODY" | grep -o '"kid"' | wc -l)
  note "${keys} key(s) published — one is a settled ring, two is a rotation overlap (D7' §13)"
else
  bad "GET /v1/.well-known/jwks.json answered ${ASVS_STATUS}; every authenticated request 500s without it"
fi

# -------------------------------------------------------------------------------------
# 30.6 — a forged and an expired bearer are both refused
#
# Cheap, and it is the assertion that says the bearer pipeline is wired at all: a service that
# could not reach the JWKS answers 500 rather than 401, which is how C126 found the JwksUrl defect.
# -------------------------------------------------------------------------------------
forged='eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJjMTI3Iiwicm9sZSI6InN1cGVyX2FkbWluIiwiZXhwIjo0MTAyNDQ0ODAwfQ.c127notasignature'

probe GET /v1/admin/rbac/matrix -H "Authorization: Bearer ${forged}"
case "$ASVS_STATUS" in
  401|403) ok "an HS256 super_admin token with an invented signature is refused (${ASVS_STATUS})" ;;
  5*)      bad "a forged bearer produced ${ASVS_STATUS}. A 5xx means the bearer pipeline is broken,
      not that the token was refused — check Jwt__JwksUrl and the gateway's iam-jwks route (C126)." ;;
  000)     warn "no answer; inconclusive" ;;
  *)       bad "a forged HS256 super_admin token answered ${ASVS_STATUS}. This is algorithm confusion
      and it is a full authentication bypass — the JWKS is public." ;;
esac
