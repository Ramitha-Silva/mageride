#!/usr/bin/env bash
# =====================================================================================
# The C132 definition of done — production readiness for DOKS Singapore.
#
#   bash infra/k8s/verify-readiness.sh
#
# The component's printed verify command is
#
#   kubectl apply --dry-run=client -k infra/k8s/overlays/production && bash infra/k8s/verify-readiness.sh
#
# and its first clause NEEDS A REACHABLE API SERVER despite saying `client`: kubectl builds a
# RESTMapper from the server's discovery document, so with no cluster it fails with "connection
# refused" before it reads a manifest. §9 below runs that clause when a cluster is reachable and
# reports it skipped when it is not, the same way `k8s-verify.sh` §9 does — so this script is a
# superset of the printed command and needs nothing but python3.
#
# --- THREE EXIT CODES, AND THE MIDDLE ONE IS THE POINT -----------------------------------
#
#   0   every check passed AND no go-live blocker is open. The platform is ready to launch.
#   1   a MECHANICAL check failed — drift, a missing rule, a manifest that lost the launch
#       topology. Somebody broke something and it is fixable here.
#   2   the mechanics are correct and GO-LIVE IS BLOCKED on things outside this repository:
#       a cluster that does not exist, a security finding that is still open, a GTFS feed
#       nobody has been given. §10 names every one of them.
#
# Exit 2 is the honest state of the world today and it is deliberately not exit 0. It follows
# `infra/replica/gtfs-day0-verify.sh` (C126) and `acceptance/sg/run.sh` (C131): a component
# blocked on something it cannot do states the blockage in its exit code, not in a paragraph a
# reader can skip.
# =====================================================================================
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO" || exit 1

K8S=infra/k8s
COMPONENT="$K8S/components/launch-topology"
OBS=infra/observability
DOCS=docs/production

pass=0; failures=0; skipped=0
blockers=()

ok()   { pass=$((pass + 1)); printf '  \033[32m✓\033[0m %s\n' "$*"; }
bad()  { failures=$((failures + 1)); printf '  \033[31m✗\033[0m %s\n' "$*" >&2; }
skip() { skipped=$((skipped + 1)); printf '  \033[33m•\033[0m %s\n' "$*"; }
head_() { printf '\n\033[1m%s\033[0m\n' "$*"; }

command -v python3 >/dev/null || { echo "error: python3 is required" >&2; exit 1; }
python3 -c 'import yaml' 2>/dev/null || { echo "error: PyYAML is required" >&2; exit 1; }

KUSTOMIZE=""
if command -v kubectl >/dev/null; then KUSTOMIZE="kubectl kustomize"
elif command -v kustomize >/dev/null; then KUSTOMIZE="kustomize build"
fi

BUILD_DIR="$(mktemp -d)"
trap 'rm -rf "$BUILD_DIR"' EXIT

echo "=== MageRide production readiness — C132 verify ================================="

# -------------------------------------------------------------------------------------
head_ "1. the two DOKS overlays render"
# -------------------------------------------------------------------------------------
if [ -z "$KUSTOMIZE" ]; then
  skip "neither kubectl nor kustomize is installed — §1 to §6 need a rendered overlay"
else
  for env in staging production; do
    if $KUSTOMIZE "$K8S/overlays/$env" > "$BUILD_DIR/$env.yaml" 2>"$BUILD_DIR/$env.err"; then
      ok "overlays/$env renders ($(grep -c '^kind:' "$BUILD_DIR/$env.yaml") resources)"
    else
      bad "overlays/$env does not render:"; sed 's/^/      /' "$BUILD_DIR/$env.err" >&2
    fi
  done
fi

# -------------------------------------------------------------------------------------
head_ "2. D7' §8's launch topology is in both of them"
# -------------------------------------------------------------------------------------
# "EMQX 2-node, Redpanda 3-node RF=3, Redis Sentinel, Postgres Patroni 1P+2R + PgBouncer"
# — asserted against the RENDERED overlay, because an overlay patch can undo anything the
# component says and the cluster only ever sees the rendering.
if [ -f "$BUILD_DIR/production.yaml" ]; then
  for env in staging production; do
    python3 - "$BUILD_DIR/$env.yaml" "$env" <<'PY'
import sys, yaml
path, env = sys.argv[1], sys.argv[2]
docs = [d for d in yaml.safe_load_all(open(path)) if d]
by = {(d['kind'], d['metadata']['name']): d for d in docs}
problems, notes = [], []

def sts(name):
    return by.get(('StatefulSet', name))

# --- Postgres: Patroni, three members, and the label that makes them see each other ------
pg = sts('postgres')
if pg is None:
    problems.append("there is no `postgres` StatefulSet")
else:
    if pg['spec']['replicas'] != 3:
        problems.append(f"postgres has {pg['spec']['replicas']} replicas, not the 1P+2R D7' §8 asks for")
    c = pg['spec']['template']['spec']['containers'][0]
    if c.get('command', [None])[0] != 'patroni':
        problems.append("the postgres container is not started by patroni — this is the single-instance form")
    labels = pg['spec']['template']['metadata']['labels']
    scope = yaml.safe_load(open('infra/k8s/components/launch-topology/patroni.yml'))['scope']
    if labels.get('cluster-name') != scope:
        problems.append(
            f"the postgres pod template has cluster-name={labels.get('cluster-name')!r}, patroni.yml "
            f"has scope={scope!r}. Patroni's pod selector is `labels + scope_label`, so a mismatch "
            "means every member sees only itself: a leader is elected, the database serves, and no "
            "replica can EVER be built. See postgres-patroni.yaml's note.")
    env_names = {e['name'] for e in c.get('env', [])}
    for needed in ('PATRONI_KUBERNETES_POD_IP', 'PATRONI_POSTGRESQL_CONNECT_ADDRESS',
                   'PATRONI_REPLICATION_PASSWORD', 'PGBACKREST_CONFIG'):
        if needed not in env_names:
            problems.append(f"the postgres container has no {needed}")
    aff = pg['spec']['template']['spec'].get('affinity', {}).get('podAntiAffinity', {})
    if not aff.get('requiredDuringSchedulingIgnoredDuringExecution'):
        problems.append("postgres has no REQUIRED pod anti-affinity — two members can land on one node")

# --- the leader Service has to follow the leader -----------------------------------------
svc = by.get(('Service', 'postgres'))
if svc is None:
    problems.append("there is no `postgres` Service — both DSNs name it as their host")
elif svc['spec'].get('selector', {}).get('role') != 'primary':
    problems.append(f"Service/postgres selects {svc['spec'].get('selector')} — it must select "
                    "role=primary, which is the label Patroni moves on promotion")

# --- Redis Sentinel -----------------------------------------------------------------------
rd = sts('redis')
if rd is None:
    problems.append("there is no `redis` StatefulSet")
else:
    if rd['spec']['replicas'] != 3:
        problems.append(f"redis has {rd['spec']['replicas']} replicas, not the 3 D7' §8 asks for")
    names = {c['name'] for c in rd['spec']['template']['spec']['containers']}
    if 'sentinel' not in names:
        problems.append("the redis pod has no sentinel container — this is the single-instance form")
if ('Service', 'redis') in by:
    problems.append("Service/redis still exists. A Sentinel group has no fixed primary address, so "
                    "a client using it would write to whichever member the selector picked")

# --- the client side of the Sentinel change ----------------------------------------------
cfg = by.get(('ConfigMap', 'common-config'))
conn = (cfg or {}).get('data', {}).get('ConnectionStrings__Redis', '')
if 'serviceName=' not in conn or '26379' not in conn:
    problems.append(f"ConnectionStrings__Redis is {conn!r} — without `serviceName=` and the sentinel "
                    "ports, StackExchange.Redis connects to one node and never follows a failover")

# --- the four D7' §8 rows that are only a number ------------------------------------------
emqx = sts('emqx')
if emqx is None or emqx['spec']['replicas'] != 2:
    problems.append("EMQX is not 2-node")
rp = sts('redpanda')
if rp is None or rp['spec']['replicas'] != 3:
    problems.append("Redpanda is not 3-node")
job = by.get(('Job', 'redpanda-topics'))
if job:
    e = {v['name']: v['value'] for v in job['spec']['template']['spec']['containers'][0]['env']}
    rf = e.get('REDPANDA_REPLICAS')
    if rf != '3':
        problems.append(f"the topic Job creates topics at RF={rf}, not 3 — three brokers with RF=1 "
                        "topics is three brokers and no durability")
if ('Deployment', 'pgbouncer') not in by:
    problems.append("there is no PgBouncer")

# --- a PDB on each half of the data plane -------------------------------------------------
for n in ('postgres', 'redis'):
    pdb = by.get(('PodDisruptionBudget', n))
    if pdb is None:
        problems.append(f"{n} has no PodDisruptionBudget — a DOKS node upgrade can take the quorum")
    elif pdb['spec'].get('minAvailable') != 2:
        problems.append(f"{n}'s PDB is minAvailable={pdb['spec'].get('minAvailable')}, not 2")

for p in problems:
    print(f"  \033[31m✗\033[0m {env}: {p}")
for n in notes:
    print(f"  \033[33m•\033[0m {env}: {n}")
if not problems:
    print(f"  \033[32m✓\033[0m {env}: Patroni 1P+2R + PgBouncer, Redis Sentinel ×3, EMQX ×2, "
          f"Redpanda ×3 RF=3, PDBs, anti-affinity")
sys.exit(1 if problems else 0)
PY
    [ $? -eq 0 ] && pass=$((pass + 1)) || failures=$((failures + 1))
  done
else
  skip "§2 needs a rendered overlay"
fi

# -------------------------------------------------------------------------------------
head_ "3. every pod can be admitted by its own namespace (C132-01)"
# -------------------------------------------------------------------------------------
# base/namespace.yaml is `pod-security.kubernetes.io/enforce: restricted`, and PSA is enforced
# when the CONTROLLER creates the pod — NOT when the StatefulSet is applied. So a workload that
# violates it applies cleanly, reports Synced in ArgoCD, creates its PVC, and never produces a
# pod. That is exactly what the production Postgres did until C132 fixed it, and no other check
# in this repository looks: `--dry-run=server` only warns, and the CI job that runs it is
# `continue-on-error: true`.
if [ -f "$BUILD_DIR/production.yaml" ]; then
  python3 - "$BUILD_DIR/production.yaml" <<'PY'
import sys, yaml
bad = []
for d in yaml.safe_load_all(open(sys.argv[1])):
    if not d or d['kind'] not in ('Deployment', 'StatefulSet', 'Job', 'CronJob'):
        continue
    name = d['metadata']['name']
    spec = (d['spec']['jobTemplate']['spec']['template']['spec'] if d['kind'] == 'CronJob'
            else d['spec']['template']['spec'] if d['kind'] != 'Job'
            else d['spec']['template']['spec'])
    psc = spec.get('securityContext', {}) or {}
    for c in spec.get('containers', []):
        csc = c.get('securityContext', {}) or {}
        nonroot = csc.get('runAsNonRoot', psc.get('runAsNonRoot'))
        seccomp = (csc.get('seccompProfile') or psc.get('seccompProfile') or {}).get('type')
        uid = csc.get('runAsUser', psc.get('runAsUser'))
        if nonroot is not True:
            bad.append(f"{name}/{c['name']}: runAsNonRoot is not true")
        if seccomp not in ('RuntimeDefault', 'Localhost'):
            bad.append(f"{name}/{c['name']}: seccompProfile is {seccomp!r}")
        if nonroot is True and not isinstance(uid, int):
            bad.append(f"{name}/{c['name']}: runAsNonRoot with no NUMERIC runAsUser — kubelet "
                       "cannot verify a named image user and refuses the pod")
for b in bad:
    print(f"  \033[31m✗\033[0m {b}")
if not bad:
    print("  \033[32m✓\033[0m every container satisfies `restricted` — non-root, numeric uid, seccomp")
sys.exit(1 if bad else 0)
PY
  [ $? -eq 0 ] && pass=$((pass + 1)) || failures=$((failures + 1))
else
  skip "§3 needs a rendered overlay"
fi

# -------------------------------------------------------------------------------------
head_ "4. the backup path is coherent (ADD §15: RPO 5 min / RTO 30 min)"
# -------------------------------------------------------------------------------------
python3 - "$BUILD_DIR/production.yaml" <<'PY'
import os, re, sys, yaml
problems = []
patroni = yaml.safe_load(open('infra/k8s/components/launch-topology/patroni.yml'))
p = patroni['bootstrap']['dcs']['postgresql']['parameters']

if p.get('archive_mode') != 'on':
    problems.append(f"archive_mode is {p.get('archive_mode')!r} — there is no RPO without WAL archiving")
t = p.get('archive_timeout')
if not isinstance(t, int) or t > 300:
    problems.append(f"archive_timeout is {t!r}. ADD §15's RPO is 5 minutes and this is the setting "
                    "that bounds it on an idle platform")
if 'pgbackrest' not in str(p.get('archive_command', '')):
    problems.append(f"archive_command is {p.get('archive_command')!r}")
if not p.get('wal_log_hints'):
    problems.append("wal_log_hints is off — pg_rewind cannot run and every failover becomes a full rebuild")

conf = open('infra/k8s/components/launch-topology/pgbackrest.conf').read()
def opt(k):
    m = re.search(rf'^{k}=(.*)$', conf, re.M)
    return m.group(1).strip() if m else None
if opt('repo1-type') != 's3':
    problems.append("pgbackrest repo1-type is not s3")
if opt('archive-async') != 'y':
    problems.append("archive-async is off — the S3 PUT would sit on the COMMIT path")
if re.search(r'^archive-push-queue-max=', conf, re.M):
    problems.append("archive-push-queue-max is SET. Past the limit pgBackRest drops the segment and "
                    "returns success, so the backup silently stops being recoverable. pgbackrest.conf "
                    "documents why it is unset; if this is deliberate the alerting has to change with it")
if re.search(r'repo1-s3-key\s*=', conf):
    problems.append("pgbackrest.conf contains an S3 key. Credentials come from the `backup-s3` "
                    "ExternalSecret through conf.d/, never from a committed file")

# The dump job and the archive have to agree about WHERE Wasabi is.
if os.path.exists(sys.argv[1]):
    for d in yaml.safe_load_all(open(sys.argv[1])):
        if d and d['kind'] == 'CronJob' and d['metadata']['name'] == 'pg-dump-wasabi':
            e = {v['name']: v.get('value') for v in
                 d['spec']['jobTemplate']['spec']['template']['spec']['containers'][0]['env']}
            if e.get('WASABI_ENDPOINT') != opt('repo1-s3-endpoint'):
                problems.append(f"the nightly dump uploads to {e.get('WASABI_ENDPOINT')} and pgBackRest "
                                f"archives to {opt('repo1-s3-endpoint')} — one of them is going somewhere "
                                "nobody restores from")
            if e.get('WASABI_REGION') != opt('repo1-s3-region'):
                problems.append("the dump and the archive disagree about the Wasabi region")
            if d['spec'].get('timeZone') != 'Asia/Colombo':
                problems.append("the dump CronJob has no Asia/Colombo timeZone — D-13 makes that the "
                                "platform's business day and a drifting schedule lands in the fee run")
            break
    else:
        problems.append("there is no pg-dump-wasabi CronJob — D7' §8's Backups row is "
                        "\"Wasabi nightly pg_dump (+ WAL archiving prod)\", both halves")

for b in problems:
    print(f"  \033[31m✗\033[0m {b}")
if not problems:
    print(f"  \033[32m✓\033[0m archive_mode on, archive_timeout {t}s (RPO ≤ {t}s vs §15's 300s), "
          "async push, no queue cap, no credential in the file, dump and archive agree")
sys.exit(1 if problems else 0)
PY
[ $? -eq 0 ] && pass=$((pass + 1)) || failures=$((failures + 1))

# -------------------------------------------------------------------------------------
head_ "5. the edge tells the gateway who the caller is (C132-02)"
# -------------------------------------------------------------------------------------
# Without this, `GatewayRateLimitMiddleware` buckets on the ingress controller's pod address and
# the whole internet shares one bucket — 30 logins a minute for 100,000 passengers, and C127-02's
# entire remediation inert. The same defect in a different spelling is C129-04.
if [ -f "$BUILD_DIR/production.yaml" ]; then
  python3 - "$BUILD_DIR/production.yaml" <<'PY'
import sys, yaml, re
problems = []
nets = []
for d in yaml.safe_load_all(open(sys.argv[1])):
    if d and d['kind'] == 'ConfigMap' and d['metadata']['name'] == 'service-endpoints':
        data = d.get('data', {})
        nets = [v for k, v in data.items() if k.startswith('Gateway__ForwardedHeaders__KnownNetworks__')]
        proxies = [v for k, v in data.items() if k.startswith('Gateway__ForwardedHeaders__KnownProxies__')]
        for p in proxies:
            if not re.match(r'^\d+\.\d+\.\d+\.\d+$', p):
                problems.append(f"KnownProxies has {p!r}, which is not an IP address. The gateway calls "
                                "IPAddress.TryParse and DISCARDS what does not parse, silently — that is "
                                "exactly how C129-04 happened on the replica")
        break
if not nets:
    problems.append("no Gateway__ForwardedHeaders__KnownNetworks__* in service-endpoints. Behind an "
                    "ingress every caller collapses into one rate-limit bucket")
values = open('infra/k8s/platform/ingress-nginx/values.production.yaml').read()
if 'use-forwarded-headers: "false"' not in values:
    problems.append("ingress-nginx does not set use-forwarded-headers: false — nginx would APPEND to a "
                    "client-supplied X-Forwarded-For and a client could pick its own bucket")
if 'externalTrafficPolicy: Local' not in values:
    problems.append("the ingress Service is not externalTrafficPolicy: Local — the node SNATs and the "
                    "client address never reaches nginx in the first place")
for b in problems:
    print(f"  \033[31m✗\033[0m {b}")
if not problems:
    print(f"  \033[32m✓\033[0m {len(nets)} trusted network(s), no un-parseable proxy, "
          "use-forwarded-headers false, externalTrafficPolicy Local")
sys.exit(1 if problems else 0)
PY
  [ $? -eq 0 ] && pass=$((pass + 1)) || failures=$((failures + 1))
else
  skip "§5 needs a rendered overlay"
fi

# -------------------------------------------------------------------------------------
head_ "6. ADD §10.2's scale-out triggers are alerts, not prose"
# -------------------------------------------------------------------------------------
python3 - <<'PY'
import sys, glob, yaml, os
RULES = 'infra/observability/prometheus/rules/alerts.capacity.yml'
required = {
    'add-emqx-node':          "EMQX CPU > 60% sustained, or > 8k clients/node",
    'add-position-processor': "Redpanda consumer lag > 5 s sustained",
    'add-fanout-replicas':    "Fanout-svc CPU > 60% or > 30k WS sessions/pod",
    'redis-cluster':          "Redis memory > 70% or ops/s > 50k",
    'add-read-replica':       "Postgres replication lag > 10 s, or replica CPU > 70%",
    'redpanda-5-brokers':     "Sustained > 50k vehicles or analytics workload",
}
doc = yaml.safe_load(open(RULES))
alerts = [r for g in doc['groups'] for r in g['rules'] if 'alert' in r]
seen = {r['labels'].get('capacity_trigger') for r in alerts if r.get('labels')}
problems = []
for trig, row in required.items():
    if trig not in seen:
        problems.append(f"ADD §10.2 \"{row}\" has no alert (no rule labelled capacity_trigger={trig})")

# every alert has to have somewhere to send the person it wakes
for r in alerts:
    url = (r.get('annotations') or {}).get('runbook_url')
    if not url:
        problems.append(f"{r['alert']} has no runbook_url")
        continue
    path = os.path.join('docs/runbooks', url.rsplit('/', 1)[-1])
    if not os.path.exists(path):
        problems.append(f"{r['alert']} points at {path}, which does not exist")
    else:
        first = open(path).read()
        if '## First action' not in first:
            problems.append(f"{path} does not open with a First action (docs/runbooks/README.md)")
    if not (r.get('labels') or {}).get('severity'):
        problems.append(f"{r['alert']} has no severity — Alertmanager cannot route it")
for b in problems:
    print(f"  \033[31m✗\033[0m {b}")
if not problems:
    print(f"  \033[32m✓\033[0m all 6 ADD §10.2 triggers are alerts; {len(alerts)} rules, "
          "each with a severity and a runbook that exists and opens with a First action")
sys.exit(1 if problems else 0)
PY
[ $? -eq 0 ] && pass=$((pass + 1)) || failures=$((failures + 1))

if command -v docker >/dev/null && docker image inspect prom/prometheus:v3.1.0 >/dev/null 2>&1; then
  if docker run --rm --entrypoint promtool -v "$PWD/$OBS/prometheus:/p" prom/prometheus:v3.1.0 \
       check rules /p/rules/alerts.capacity.yml >"$BUILD_DIR/promtool.txt" 2>&1; then
    ok "promtool: $(grep -o '[0-9]* rules found' "$BUILD_DIR/promtool.txt" | head -1)"
  else
    bad "promtool rejects alerts.capacity.yml:"; tail -5 "$BUILD_DIR/promtool.txt" | sed 's/^/      /' >&2
  fi
else
  skip "promtool not available locally (CI runs it)"
fi

# -------------------------------------------------------------------------------------
head_ "7. alert routing and escalation exist and hold no secret"
# -------------------------------------------------------------------------------------
python3 - <<'PY'
import sys, yaml, re
path = 'infra/observability/alertmanager/alertmanager.production.yml'
raw = open(path).read()
cfg = yaml.safe_load(raw)
problems = []
receivers = {r['name'] for r in cfg['receivers']}
for needed in ('safety-page', 'platform-page', 'capacity-ticket'):
    if needed not in receivers:
        problems.append(f"no `{needed}` receiver — docs/runbooks/oncall.md §1 describes three services")
for r in cfg['receivers']:
    for pd in r.get('pagerduty_configs', []):
        if 'routing_key' in pd:
            problems.append(f"receiver {r['name']} has an inline routing_key. It is a secret and comes "
                            "from Vault through ESO — `routing_key_file`, never a committed value")
        if not pd.get('routing_key_file'):
            problems.append(f"receiver {r['name']} has neither routing_key_file nor routing_key")
# SOS must not be able to wait behind anything
sos = [r for r in cfg['route'].get('routes', []) if 'slo = "sos-dispatch-latency"' in str(r.get('matchers'))]
if not sos:
    problems.append("no route for the SOS SLO")
elif sos[0].get('group_wait') != '0s' or cfg['route']['routes'][0] is not sos[0]:
    problems.append("the SOS route is not first with group_wait 0s — D-33's alert must not queue "
                    "behind another group's group_wait")
if not any('capacity_trigger' in str(r.get('matchers')) for r in cfg['route'].get('routes', [])):
    problems.append("capacity tickets are not routed separately — they would page somebody")
for b in problems:
    print(f"  \033[31m✗\033[0m {b}")
if not problems:
    print(f"  \033[32m✓\033[0m {len(receivers)} receivers, {len(cfg.get('inhibit_rules', []))} inhibitions, "
          "SOS first at group_wait 0s, every routing key from a file")
sys.exit(1 if problems else 0)
PY
[ $? -eq 0 ] && pass=$((pass + 1)) || failures=$((failures + 1))

for rb in postgres-failover redis-sentinel-failover dr-restore capacity-scale-out oncall; do
  if [ -f "docs/runbooks/$rb.md" ] && grep -q '^## First action' "docs/runbooks/$rb.md"; then
    ok "docs/runbooks/$rb.md"
  else
    bad "docs/runbooks/$rb.md is missing or does not open with a First action"
  fi
done

# -------------------------------------------------------------------------------------
head_ "8. the package a launch is actually run from"
# -------------------------------------------------------------------------------------
for f in "$DOCS/capacity-plan.md" "$DOCS/go-live-checklist.md" "$DOCS/readiness-report.md" \
         "infra/scripts/dr-rehearsal.sh" "$K8S/platform/ingress-nginx/values.production.yaml"; do
  [ -f "$f" ] && ok "$f" || bad "$f is missing"
done

# -------------------------------------------------------------------------------------
head_ "9. the printed verify command's kubectl clause, and the running cluster"
# -------------------------------------------------------------------------------------
if ! command -v kubectl >/dev/null; then
  skip "kubectl is not installed — the printed clause cannot run here"
elif ! kubectl version --request-timeout=5s >/dev/null 2>&1; then
  skip "no reachable cluster. \`kubectl apply --dry-run=client\` still needs one for discovery"
else
  if kubectl apply --dry-run=client -k "$K8S/overlays/production" >"$BUILD_DIR/dryrun.txt" 2>&1; then
    ok "kubectl apply --dry-run=client -k $K8S/overlays/production"
  else
    bad "the printed verify command's first clause fails:"; tail -10 "$BUILD_DIR/dryrun.txt" | sed 's/^/      /' >&2
  fi
  # If this happens to be the production cluster, the one drift no file can show: `patronictl
  # edit-config` writes the DCS ConfigMap and not the repository.
  if kubectl -n mageride get configmap mageride-pg-config >/dev/null 2>&1; then
    live="$(kubectl -n mageride get configmap mageride-pg-config -o jsonpath='{.metadata.annotations.config}' 2>/dev/null)"
    if [ -n "$live" ]; then
      python3 - "$live" <<'PY'
import json, sys, yaml
live = json.loads(sys.argv[1])
want = yaml.safe_load(open('infra/k8s/components/launch-topology/patroni.yml'))['bootstrap']['dcs']
diff = []
for k, v in (want.get('postgresql', {}).get('parameters') or {}).items():
    lv = (live.get('postgresql', {}).get('parameters') or {}).get(k)
    if lv is not None and str(lv) != str(v):
        diff.append(f"{k}: cluster={lv!r} repository={v!r}")
if diff:
    print("  \033[31m✗\033[0m the running Patroni configuration has drifted from patroni.yml "
          "(somebody ran `patronictl edit-config`):")
    for d in diff:
        print(f"        {d}")
    sys.exit(1)
print("  \033[32m✓\033[0m the running Patroni configuration matches patroni.yml")
PY
      [ $? -eq 0 ] && pass=$((pass + 1)) || failures=$((failures + 1))
    fi
  else
    skip "no Patroni cluster in this context — DCS drift not checked"
  fi
fi

# -------------------------------------------------------------------------------------
head_ "10. go-live blockers"
# -------------------------------------------------------------------------------------
# Parsed out of the checklist rather than duplicated here, so there is one list and it is the
# one a human signs.
mapfile -t blockers < <(python3 - <<'PY'
import re
text = open('docs/production/go-live-checklist.md').read()
sec = text.split('## A. Blockers', 1)[-1].split('## B.', 1)[0]
for line in sec.splitlines():
    if not line.startswith('|'):
        continue
    cells = [c.strip() for c in line.strip('|').split('|')]
    if len(cells) < 4 or cells[0] in ('#', '---'):
        continue
    if re.search(r'\*\*OPEN', cells[2] if len(cells) > 3 else '') or re.search(r'\*\*OPEN', ' '.join(cells)):
        num = cells[0]
        what = re.sub(r'\*\*|`', '', cells[1])[:96]
        owner = next((c for c in cells[3:] if c and 'OPEN' not in c), '?')
        print(f"{num}. {what}  [{owner}]")
PY
)
if [ "${#blockers[@]}" -eq 0 ]; then
  ok "no open blocker in docs/production/go-live-checklist.md §A"
else
  printf '  \033[33m•\033[0m %d go-live blockers are open:\n' "${#blockers[@]}"
  printf '        %s\n' "${blockers[@]}"
fi

# -------------------------------------------------------------------------------------
echo
echo "==============================================================================="
printf '%d passed, %d failed, %d skipped\n' "$pass" "$failures" "$skipped"
if [ "$failures" -ne 0 ]; then
  echo "the manifests or the package are broken — fix them here."
  exit 1
fi
if [ "${#blockers[@]}" -ne 0 ]; then
  echo
  echo "The readiness package is complete and correct. GO-LIVE IS BLOCKED on ${#blockers[@]} items"
  echo "that cannot be closed in this repository — see docs/production/go-live-checklist.md §A."
  echo "Exit 2 says so deliberately: this is not a passing verify and it is not a broken one."
  exit 2
fi
exit 0
