#!/usr/bin/env bash
# =====================================================================================
# infra/replica/contract-live-verify.sh — drive the contract suite against the RUNNING replica.
#
#   bash infra/replica/contract-live-verify.sh
#
# The other half of C118's definition of done, and of the wave-5 gate: "contract + E2E suites green
# against the deployed replica". C118 could not do it — `docker-compose.dev.yml` had no images for
# app-services, hot-path or fanout, so the suite composed every service in-process instead and its
# csproj says why. C124/C125 built those images; this is the transport that was waiting for them.
#
# ------------------------------------------------------------------------------------
# WHAT THIS ADDS OVER THE IN-PROCESS SUITE
# ------------------------------------------------------------------------------------
# The in-process suite reads each service's own route table, which is exact and cannot flake — and
# says nothing about TLS termination, the gateway's cluster addresses, the version gate, or whether
# the bearer a caller presents can be validated at all. C126 found the last of those the hard way:
# `Jwt__JwksUrl` named a path the gateway refuses ahead of routing, so EVERY authenticated request to
# either compose stack answered 500 while every in-process assertion stayed green.
#
# ------------------------------------------------------------------------------------
# WHY THE CREDENTIAL IS MINTED HERE AND NOT IN THE SUITE
# ------------------------------------------------------------------------------------
# Signing in means reading `.env.replica` and knowing which of the nine roles the account holds.
# Those are facts about a deployment, and a contract suite that knew them would be a deployment
# script. It receives a ready bearer in MAGERIDE_LIVE_TOKEN and asserts; this reads the secret.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
REPLICA_DIR="$PWD"
cd ../.. || exit 2

# gtfs-lib.sh already owns the operator, the sign-in and the edge — reusing it is what stops a second
# copy of "how do you get an Admin bearer on this box" from drifting away from the first.
# shellcheck source=infra/replica/gtfs-lib.sh
. "$REPLICA_DIR/gtfs-lib.sh"

step "0. the replica answers, and we can authenticate against it"

running=$(docker compose -f "$COMPOSE" ps --services --filter status=running 2>/dev/null | wc -l)
[ "$running" -gt 0 ] || die "nothing is running under mageride-replica — bring it up with deploy.sh"
ok "${running} services running"

require_token
ok "bearer obtained for ${GTFS_ADMIN_EMAIL} (role admin)"

# Fail fast and unmistakably if the JWKS route is not serving: without it every authenticated
# operation in the sweep answers 500 and the report is 380 identical failures.
api GET /v1/.well-known/jwks.json
if [ "$API_STATUS" != "200" ]; then
  die "GET /v1/.well-known/jwks.json answered ${API_STATUS}. Every bearer-carrying operation will
      answer 500 until this serves — check Jwt__JwksUrl and the gateway's iam-jwks route (C126)."
fi
ok "the JWKS is published, so a bearer can actually be validated"

step "1. the sweep"

note "382 operations over TLS, one request each. Reads go through as reads; POSTs are refused by the"
note "kernel before any handler (no Idempotency-Key); PUT/PATCH/DELETE are driven ONLY where the path"
note "carries an id, so the absent id makes them a no-op; a named list is never sent at all."
note ""
note "That rule exists because the FIRST run of this sweep did change state: three no-id PUTs were"
note "accepted, and two PDPA operations filed real 30-day obligations against the operator account."
note "Both holes are closed (LiveRequestPlan), the rows were removed, and the audit trail was kept."
note "Read LiveRequestPlan before pointing this at a deployment whose state you care about."

export MAGERIDE_LIVE_EDGE="${EDGE}"
export MAGERIDE_LIVE_HOST="${HOSTHDR}"
export MAGERIDE_LIVE_TOKEN="${GTFS_TOKEN}"

# Which optional containers are NOT running, as contract-document names. A 503 from an operation
# whose service was never deployed is the platform answering correctly about an absent dependency,
# not drift — and only this script can tell the difference, because only it can ask docker.
# `voip` is the only optional profile that owns a contract document — admin-portal and fleet-portal
# own none, and every other service is co-located in a container that is always up. The names below
# are CONTRACT DOCUMENT stems, because that is what the suite compares against.
absent=""
for document in voip; do
  if ! docker compose -f "$COMPOSE" ps --services --filter status=running 2>/dev/null | grep -qx "$document"; then
    absent="${absent:+$absent,}${document}"
  fi
done
export MAGERIDE_LIVE_ABSENT="$absent"
[ -z "$absent" ] || note "not deployed on this target, so their operations are skipped: ${absent}"

# Only the live transport. The in-process suite is `dotnet test tests/Contract -c Release` and is
# unaffected by these variables: without MAGERIDE_LIVE_EDGE its theories skip.
dotnet test tests/Contract -c Release \
  --filter "FullyQualifiedName~MageRide.Contract.Tests.Live" \
  --logger "console;verbosity=normal" 2>&1 | tail -60

result=${PIPESTATUS[0]}

echo
echo "==============================================================================="
if [ "$result" -eq 0 ]; then
  echo "the deployed edge conforms to backend/contracts/*.yaml"
else
  echo "conformance failures above. Each names the operation, the status and the body:"
  echo "  · a 5xx        the deployment is broken, not the request refused"
  echo "  · an undeclared status   the service answers something no contract describes"
  echo "  · a schema violation     real drift between the contract and what is served"
  echo "Route EXISTENCE is not asserted here and cannot be — the gateway's own 404 for an unrouted"
  echo "path is problem+json too, exactly like a service's. Runtime/RouteTableTests owns that, from"
  echo "each service's EndpointDataSource, and it also catches the reverse direction."
  echo
  echo "Do not soften an assertion to make it pass: fix the service, or add the finding to"
  echo "tests/Contract/Live/LiveDrift.cs with its owner (tests/Contract/CLAUDE.md)."
fi
exit "$result"
