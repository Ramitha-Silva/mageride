#!/usr/bin/env bash
# =====================================================================================
# 20 — the edge's security configuration, read from the files that decide it
#      (D-30 attestation, D-31 minimum version, ADD §12.2 transport, ADD §12.3 authorization)
#
# These are assertions about configuration rather than about a running process, so they hold on a
# clean checkout with no replica — which is where the verify command runs. What a running edge
# actually does with the same configuration is check 30.
# =====================================================================================

# shellcheck shell=bash

step "20. the edge is configured to enforce D-30, D-31 and the internal-plane refusal"

# Δ C127: the edge policy is its own file. It was a section of appsettings.json, which is
# what made it vanish from the co-located AppServices image (finding 02).
GATEWAY_SETTINGS=backend/src/ApiGateway/gateway-policy.json

python3 -c "import json,sys; json.load(open('$GATEWAY_SETTINGS'))" 2>/dev/null \
  || { bad "$GATEWAY_SETTINGS does not parse; nothing below can be trusted"; return 0 2>/dev/null || exit 0; }

# -------------------------------------------------------------------------------------
# 20.1 — D-30 attestation defaults to Enforce
#
# The default matters more than any one deployment's value: `.env.app.example` sets Disabled for
# dev and the replica (neither app exists before Wave 4a/4b, and Enforce with no client rejects
# every request to the 22 sensitive operations), and no k8s manifest sets the key at all — so
# production inherits whatever this file says.
# -------------------------------------------------------------------------------------
mode=$(python3 -c "
import json
print(json.load(open('$GATEWAY_SETTINGS')).get('Gateway',{}).get('Attestation',{}).get('Mode','(unset)'))")

if [ "$mode" = "Enforce" ]; then
  ok "Gateway:Attestation:Mode defaults to Enforce, so a deployment that sets nothing enforces D-30"
else
  bad "Gateway:Attestation:Mode defaults to '${mode}'. No k8s manifest overrides it, so production
      would run with attestation ${mode}. Disabled belongs in .env.app.example, not in the default."
fi

sensitive=$(python3 -c "
import json
print(len(json.load(open('$GATEWAY_SETTINGS')).get('Gateway',{}).get('Attestation',{}).get('SensitiveOperations',[])))")

if [ "$sensitive" -ge 20 ]; then
  ok "${sensitive} operations are marked D-30 sensitive (auth, payments, wallet, ride accept, SOS)"
else
  bad "only ${sensitive} operation(s) are marked D-30 sensitive; D3' §0 names five families"
fi

# -------------------------------------------------------------------------------------
# 20.2 — D-31 minimum-version gate
# -------------------------------------------------------------------------------------
platforms=$(python3 -c "
import json
gate=json.load(open('$GATEWAY_SETTINGS')).get('Gateway',{}).get('VersionGate',{})
print(','.join(sorted(k for k in gate.get('Platforms',{}) if not k.startswith('//'))))")

case "$platforms" in
  *android*) case "$platforms" in
               *ios*) ok "the D-31 version floor is configured for android and ios" ;;
               *) bad "the D-31 version floor names no ios platform (${platforms:-none})" ;;
             esac ;;
  *) bad "the D-31 version floor names no android platform (${platforms:-none})" ;;
esac

# -------------------------------------------------------------------------------------
# 20.3 — the internal plane is refused at the edge (Δ C127 finding 04)
#
# `/v1/internal` is the convention; three operations declare `security: [{ mtls: [] }]` without
# carrying it and were published until C127. The contract is what says which plane an operation is
# on, so the list below is derived from the contracts rather than hard-coded — a fourth such
# operation fails here on the day it is written.
# -------------------------------------------------------------------------------------
unblocked=$(python3 - <<'PY'
import json, re, glob, os

blocked = json.load(open('backend/src/ApiGateway/gateway-policy.json')) \
    .get('Gateway', {}).get('BlockedPathPrefixes', [])

def refused(path):
    return any(path == p or path.startswith(p.rstrip('/') + '/') for p in blocked)

missing = []
for f in sorted(glob.glob('backend/contracts/*.yaml')):
    lines = open(f).read().split('\n')
    path = method = None
    buf = []

    def flush():
        if path and method and any(re.match(r'\s*- mtls: \[\]', b) for b in buf):
            concrete = re.sub(r'\{[^}]+\}', 'x', path)
            if not refused(concrete):
                missing.append(f"{os.path.basename(f)}  {method.upper():<6} {path}")

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

print('\n'.join(missing))
PY
)

if [ -z "$unblocked" ]; then
  ok "every operation the contracts put on the mTLS plane is in Gateway:BlockedPathPrefixes"
else
  bad "operation(s) declared \`security: [{ mtls: [] }]\` are routable from the public edge:"
  while IFS= read -r line; do note "$line"; done <<<"$unblocked"
  note "Add the path to Gateway:BlockedPathPrefixes in $GATEWAY_SETTINGS."
fi

# -------------------------------------------------------------------------------------
# 20.4 — transport (ADD §12.2): no plaintext MQTT is PUBLISHED
#
# The question is which listeners leave the cluster, not which exist. EMQX binds 1883 for
# in-cluster clients — mqtt-bridge and the adapters, which reach it over the mesh — and
# infra/CLAUDE.md's fence is that 8883 (mTLS, hardware) and 8084 (WSS + JWT, mobile) are the only
# two an edge publishes. A grep for the number reports the ClusterIP and the health-check
# annotation as findings; what matters is a LoadBalancer, a NodePort, or a host port mapping.
# -------------------------------------------------------------------------------------
step "20b. transport security (ADD §12.2)"

published=$(python3 - <<'PY'
import glob, re

findings = []

# Compose: a `ports:` entry publishes a host port. `expose:` and a bare container port do not.
for f in glob.glob('infra/**/*.yml', recursive=True) + glob.glob('infra/**/*.yaml', recursive=True):
    if '/k8s/' in f:
        continue
    in_ports = False
    for n, line in enumerate(open(f), 1):
        if re.match(r'^\s*ports:\s*$', line):
            in_ports = True
            continue
        if in_ports and not re.match(r'^\s*-', line):
            in_ports = False
        if in_ports and re.search(r'(^|[^0-9])1883:', line):
            findings.append(f"{f}:{n}: {line.strip()}  (compose host port)")

# Kubernetes: only a LoadBalancer or NodePort Service leaves the cluster.
for f in glob.glob('infra/k8s/**/*.yaml', recursive=True):
    for doc in open(f).read().split('\n---'):
        if 'kind: Service' not in doc:
            continue
        if not re.search(r'^\s*type:\s*(LoadBalancer|NodePort)\s*$', doc, re.M):
            continue
        for n, line in enumerate(doc.split('\n'), 1):
            if re.search(r'\bport:\s*1883\b', line):
                findings.append(f"{f}: {line.strip()}  (published Service)")

print('\n'.join(findings))
PY
)

if [ -z "$published" ]; then
  ok "no compose host port and no LoadBalancer/NodePort publishes MQTT 1883 (ADD §12.2)"
  note "EMQX still binds 1883 for in-cluster clients; 8883 and 8084 are the two an edge publishes."
else
  bad "plaintext MQTT (1883) is PUBLISHED (ADD §12.2 disables it):"
  while IFS= read -r line; do note "$line"; done <<<"$published"
fi

if grep -qE 'listeners\.ssl\.default|8883' infra/deploy/emqx/emqx.conf 2>/dev/null; then
  ok "EMQX exposes the 8883 mTLS listener the hardware plane needs (ADD §12.2, T-02)"
else
  warn "no 8883 listener found in infra/deploy/emqx/emqx.conf — hardware trackers need mTLS"
fi

# -------------------------------------------------------------------------------------
# 20.5 — the controls the schema carries (ADD §12.6 insider DB access, D-35)
# -------------------------------------------------------------------------------------
step "20c. the database's own controls exist as migrations"

[ -f db/migrations/1305__audit_events.sql ] \
  && ok "audit.events exists (D-35, migration 1305)" \
  || bad "no migration creates audit.events; D-35 has nowhere to write"

[ -f db/migrations/2001__least_privilege_roles.sql ] \
  && ok "the least-privilege roles exist (C127, migration 2001)" \
  || bad "no migration creates mageride_app/mageride_migrate/mageride_readonly. Without them every
      service connects as the table owner, audit.events is mutable and every RLS policy is inert."

rls_tables=$(grep -rl 'ENABLE ROW LEVEL SECURITY' db/migrations/*.sql 2>/dev/null | wc -l)
[ "$rls_tables" -ge 3 ] \
  && ok "row-level security is applied by ${rls_tables} migration(s) (ADD §9.5 item 8, §12.6)" \
  || bad "row-level security appears in ${rls_tables} migration(s); ADD §12.6 requires it on PII tables"
