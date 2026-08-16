#!/usr/bin/env bash
# =====================================================================================
# The C124 definition of done, offline (C124).
#
#   bash infra/scripts/k8s-verify.sh
#
# The component's printed verify command is:
#
#   python3 -c "import yaml,glob;[yaml.safe_load(open(f)) for f in
#     glob.glob('.github/workflows/*.yml')+glob.glob('infra/k8s/**/*.yaml',recursive=True)]" \
#     && kubectl apply --dry-run=client -k infra/k8s/overlays/staging
#
# BOTH HALVES OF IT NEED A CORRECTION, and this script runs the corrected form.
#
# `yaml.safe_load` parses ONE document and raises ComposerError on a second. 43 of the 107 YAML
# files here are multi-document, because a Kubernetes manifest is conventionally a Deployment, its
# Service, its HPA and its PDB in one file separated by `---`. So the printed clause cannot pass over
# a manifest directory whatever the manifests contain; the only way to satisfy it literally would be
# one resource per file — about 200 files — which makes them worse to read in an incident for no
# gain. `safe_load_all` is the one-word fix and it is what runs below and in CI. Raised as a
# micro-change-set for the manifest's verify_cmd in the C124 handoff.
#
# The second half NEEDS A REACHABLE API SERVER even though it says `--dry-run=client`: kubectl
# builds a RESTMapper from the server's discovery document to turn a `kind` into a resource, so
# without a cluster it fails with "connection refused" before it reads a single manifest. On a
# build host with no cluster that clause cannot run, so `.github/workflows/k8s-validate.yml`
# stands up a kind cluster on every pull request and runs it verbatim, and THIS script is the
# superset that needs nothing but python3:
#
#   * both halves of the printed command, with the kubectl clause executed when a cluster is
#     reachable and reported as skipped when it is not;
#   * `kubectl kustomize` (or a `kustomize` binary) builds all three overlays and the platform
#     tree — the same rendering the cluster would receive;
#   * kubeconform against the real Kubernetes JSON schemas, when it is installed;
#   * and the FENCES, which is the part no generic tool checks.
#
# THE FENCES, from build/prompts/C124.md:
#   - "Rollout is RollingUpdate with maxUnavailable 0, maxSurge 1. No big-bang deploys."
#   - "Secrets come from Vault via External Secrets Operator — never from repository files."
#   - "DB migrations are gated pre-deploy" (the gate's own manifest is asserted here; the DDL
#     rules are infra/scripts/migration-gate.sh).
# plus the platform rules these manifests have to keep: MQTT never through the Ingress, every
# container non-root with probes and limits, no image on a moving tag, and every credential in
# infra/env/*.example either wired to Vault or explicitly listed as not wired.
# =====================================================================================
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO"

pass=0
failures=0
skipped=0

ok()   { pass=$((pass + 1)); printf '  \033[32m✓\033[0m %s\n' "$*"; }
bad()  { failures=$((failures + 1)); printf '  \033[31m✗\033[0m %s\n' "$*" >&2; }
skip() { skipped=$((skipped + 1)); printf '  \033[33m•\033[0m %s\n' "$*"; }
head_() { printf '\n\033[1m%s\033[0m\n' "$*"; }

command -v python3 >/dev/null || { echo "error: python3 is required" >&2; exit 1; }
python3 -c 'import yaml' 2>/dev/null || {
  echo "error: PyYAML is required (pip install pyyaml)" >&2
  exit 1
}

# `kubectl kustomize` has kustomize built in (v5.x), so one binary covers both. A standalone
# `kustomize` is accepted as a fallback because a build host may have it and not kubectl.
KUSTOMIZE=""
if command -v kubectl >/dev/null; then KUSTOMIZE="kubectl kustomize"
elif command -v kustomize >/dev/null; then KUSTOMIZE="kustomize build"
fi

BUILD_DIR="$(mktemp -d)"
trap 'rm -rf "$BUILD_DIR"' EXIT

echo "=== MageRide Kubernetes manifests — C124 verify ================================"

# -------------------------------------------------------------------------------------
head_ "1. every YAML file parses (the first half of the printed verify command)"
# -------------------------------------------------------------------------------------
if python3 -c "import yaml,glob;[list(yaml.safe_load_all(open(f))) for f in glob.glob('.github/workflows/*.yml')+glob.glob('infra/k8s/**/*.yaml',recursive=True)]"; then
  count=$(python3 -c "import glob;print(len(glob.glob('.github/workflows/*.yml')+glob.glob('infra/k8s/**/*.yaml',recursive=True)))")
  ok "$count files parse"
else
  bad "a YAML file does not parse"
fi

# -------------------------------------------------------------------------------------
head_ "2. the generated manifests match the catalog"
# -------------------------------------------------------------------------------------
# Thirty-one workloads, three portals, twenty-six ExternalSecrets and three image lists are
# generated from infra/k8s/service-catalog.yaml. A hand-edit is silent otherwise: the next
# regeneration reverts it and the reason it was made is gone.
if python3 infra/k8s/tools/generate_manifests.py --check >/dev/null 2>&1; then
  ok "generated manifests are current"
else
  bad "generated manifests are stale — run: python3 infra/k8s/tools/generate_manifests.py"
  python3 infra/k8s/tools/generate_manifests.py --check 2>&1 | sed 's/^/      /' >&2 || true
fi

# The two files copied verbatim from the dev stack. `cmp`, not "looks similar": the D6' §3.1 topic
# ACL and the D6' §2.1 topic registry do not depend on the substrate, so a second authored copy
# would be a second thing to get wrong — silently, in a way that only shows up as a device that
# cannot publish or a topic with one partition.
for pair in \
  "infra/deploy/emqx/acl.conf:infra/k8s/base/data/emqx/acl.conf" \
  "infra/deploy/redpanda/bootstrap-topics.sh:infra/k8s/base/data/redpanda/bootstrap-topics.sh"; do
  src="${pair%%:*}"; dst="${pair##*:}"
  if cmp -s "$src" "$dst"; then
    ok "$(basename "$dst") is identical to the dev stack's"
  else
    bad "$dst has drifted from $src — copy it: cp $src $dst"
  fi
done

# -------------------------------------------------------------------------------------
head_ "3. every overlay and the platform tree render"
# -------------------------------------------------------------------------------------
TARGETS=(
  "infra/k8s/base"
  "infra/k8s/overlays/dev"
  "infra/k8s/overlays/staging"
  "infra/k8s/overlays/production"
  "infra/k8s/platform/external-secrets/base"
  "infra/k8s/platform/external-secrets/overlays/staging"
  "infra/k8s/platform/external-secrets/overlays/production"
  "infra/k8s/platform/sealed-secrets/dev"
)
if [ -z "$KUSTOMIZE" ]; then
  skip "no kubectl or kustomize on PATH — nothing below can be rendered"
else
  for target in "${TARGETS[@]}"; do
    out="$BUILD_DIR/$(echo "$target" | tr '/' '_').yaml"
    if $KUSTOMIZE "$target" > "$out" 2>"$out.err"; then
      n=$(grep -c '^kind:' "$out" || true)
      ok "$target — $n resources"
    else
      bad "$target does not build:"
      sed 's/^/      /' "$out.err" >&2
    fi
  done
fi

# -------------------------------------------------------------------------------------
head_ "4. schema validation (kubeconform)"
# -------------------------------------------------------------------------------------
# What catches a misspelled field. `readinesProbe` is valid YAML, is silently DROPPED by the API
# server's field pruning, and gives you a workload with no readiness gate — which looks exactly
# like a healthy deploy until the first rollout takes traffic to a pod that is not ready.
if ! command -v kubeconform >/dev/null; then
  skip "kubeconform is not installed — field names are unchecked (CI installs it)"
elif [ -z "$KUSTOMIZE" ]; then
  skip "kubeconform needs a rendered manifest"
else
  # A cache directory, because kubeconform fetches the schemas over HTTPS and this loop asks for
  # the same ones three times.
  CACHE="${KUBECONFORM_CACHE:-$BUILD_DIR/schemas}"
  mkdir -p "$CACHE"
  for env in dev staging production; do
    out="$BUILD_DIR/infra_k8s_overlays_$env.yaml"
    [ -s "$out" ] || continue
    kubeconform -strict -summary -ignore-missing-schemas -cache "$CACHE" "$out" >"$out.conform" 2>&1
    invalid=$(grep -oE 'Invalid: [0-9]+' "$out.conform" | grep -oE '[0-9]+' || echo 0)
    errors=$(grep -oE 'Errors: [0-9]+' "$out.conform" | grep -oE '[0-9]+' || echo 0)
    if [ "${invalid:-0}" -gt 0 ]; then
      bad "overlays/$env has a schema error:"
      sed 's/^/      /' "$out.conform" >&2
    elif [ "${errors:-0}" -gt 0 ]; then
      # A schema kubeconform could not FETCH is a network failure, not a manifest failure — and
      # failing the verify on `connection reset by peer` teaches people to ignore it. The
      # distinction is in kubeconform's own summary: `Invalid` counts manifests, `Errors` counts
      # everything else.
      skip "overlays/$env — a schema could not be retrieved (network), $invalid invalid of the rest:"
      grep -E 'failed parsing schema|Summary' "$out.conform" | sed 's/^/      /'
    else
      ok "overlays/$env is schema-valid"
    fi
  done
fi

# -------------------------------------------------------------------------------------
head_ "5. the fences"
# -------------------------------------------------------------------------------------
# Against the RENDERED overlay, not the source files: a fence has to hold in what the cluster
# actually receives, and an overlay patch can undo anything base says.
if [ -z "$KUSTOMIZE" ]; then
  skip "the fence checks need a rendered overlay"
else
  for env in dev staging production; do
    rendered="$BUILD_DIR/infra_k8s_overlays_$env.yaml"
    [ -s "$rendered" ] || { skip "overlays/$env did not render — fences unchecked"; continue; }
    if python3 infra/k8s/tools/check_fences.py "$env" "$rendered" > "$BUILD_DIR/fences-$env.txt" 2>&1; then
      sed 's/^/  /' "$BUILD_DIR/fences-$env.txt"
      # One ✓ per fence, counted from the checker's own output so the tally matches what it says.
      pass=$((pass + $(grep -c '✓' "$BUILD_DIR/fences-$env.txt")))
    else
      sed 's/^/  /' "$BUILD_DIR/fences-$env.txt" >&2
      # The exit status is what fails the run — grep -c on the output would count 0 if the
      # pattern ever stopped matching, and a verify script that silently stops failing is worse
      # than no verify script.
      failures=$((failures + 1))
    fi
  done
fi

# -------------------------------------------------------------------------------------
head_ "6. every credential is accounted for"
# -------------------------------------------------------------------------------------
# The check that closes "no secret value exists in the repository" from the other direction: not
# "is there a secret in git" but "is every secret the platform HAS wired to Vault". A forgotten
# internal API key does not fail a build — it fails one direction of one call with a 401, in
# production, weeks later.
python3 - <<'PYEOF'
import re, sys, pathlib, yaml

REPO = pathlib.Path(".").resolve()
CATALOG = yaml.safe_load((REPO / "infra/k8s/service-catalog.yaml").read_text(encoding="utf-8"))

SECRET_SHAPED = re.compile(
    r"(ApiKey|ApiSecret|InternalKey|SigningKey|SigningKeyPem|WebhookSecret|ServiceAccountJson"
    r"|P8Key|PepperKey|HashKey|ClientSecret|SecretKey|TokenKey|RefreshTokenKey|MerchantId"
    r"|KeyId|TeamId|SessionTokenSecret|BankApiKey|AppId)$"
)

declared = set()
for f in ("infra/env/.env.common.example", "infra/env/.env.app.example"):
    for line in (REPO / f).read_text(encoding="utf-8").splitlines():
        m = re.match(r"^([A-Za-z][A-Za-z0-9_]*__[A-Za-z0-9_]+)=", line)
        if m and SECRET_SHAPED.search(m.group(1)):
            declared.add(m.group(1))

# Everything any ExternalSecret delivers, whether as a fetched key or a rendered template key.
wired = set()
for path in (REPO / "infra/k8s/platform/external-secrets/base").glob("*.yaml"):
    for doc in yaml.safe_load_all(path.read_text(encoding="utf-8")):
        if not doc or doc.get("kind") != "ExternalSecret":
            continue
        for entry in doc["spec"].get("data", []):
            wired.add(entry["secretKey"])
        template = doc["spec"].get("target", {}).get("template", {})
        wired.update((template.get("data") or {}).keys())

unwired = set(CATALOG.get("unwiredSecrets") or {})
granted = {k for svc in CATALOG["services"] for k in (svc.get("secrets") or [])}

missing = sorted(declared - wired - unwired)
if missing:
    print("✗ credentials in infra/env/*.example that no ExternalSecret delivers and that")
    print("  service-catalog.yaml does not list under `unwiredSecrets`:")
    for k in missing:
        print(f"      {k}")
    print("  Add it to a service's `secrets:` (with an `aliases:` entry if it is shared), or")
    print("  list it under `unwiredSecrets:` with the reason it is deliberately absent.")
    sys.exit(1)

# The reverse: a service is granted a key nothing declares. Usually a typo, occasionally a key
# the env template is missing — which is a finding about the template, so it is a warning.
undeclared = sorted(granted - declared)
print(f"✓ {len(declared)} credentials in the env templates: {len(declared - unwired)} wired to Vault, "
      f"{len(unwired)} explicitly not wired")
if undeclared:
    print(f"  — {len(undeclared)} granted key(s) are not in infra/env/*.example (a gap in the")
    print("    template, raised in the C124 handoff): " + ", ".join(undeclared))

# No portal may mount a Secret: a portal is a browser bundle, so anything it can read a user can.
for path in (REPO / "infra/k8s/base/portals").glob("*.yaml"):
    for doc in yaml.safe_load_all(path.read_text(encoding="utf-8")):
        if not doc or doc.get("kind") != "Deployment":
            continue
        spec = doc["spec"]["template"]["spec"]
        for c in spec.get("containers", []):
            for src in c.get("envFrom", []):
                if "secretRef" in src:
                    print(f"✗ portal {doc['metadata']['name']} mounts Secret "
                          f"{src['secretRef']['name']} — a portal is a browser bundle")
                    sys.exit(1)
            for e in c.get("env", []):
                if "secretKeyRef" in (e.get("valueFrom") or {}):
                    print(f"✗ portal {doc['metadata']['name']} reads a Secret into {e['name']}")
                    sys.exit(1)
print("✓ no portal mounts a Secret")
PYEOF
case $? in
  0) pass=$((pass + 2)) ;;
  *) failures=$((failures + 1)) ;;
esac

# -------------------------------------------------------------------------------------
head_ "7. no secret value in the repository's k8s tree"
# -------------------------------------------------------------------------------------
# Belt and braces with fence (2), and it looks at the FILES rather than the rendered output — a
# `kind: Secret` in a directory no kustomization references would not appear in a build.
found=0
while IFS= read -r file; do
  if python3 - "$file" <<'PYEOF'
import sys, yaml
for doc in yaml.safe_load_all(open(sys.argv[1], encoding="utf-8")):
    if doc and doc.get("kind") == "Secret" and (doc.get("data") or doc.get("stringData")):
        sys.exit(1)
sys.exit(0)
PYEOF
  then :; else
    bad "$file contains a Secret with a value. Use an ExternalSecret, or kubeseal it."
    found=1
  fi
done < <(find infra/k8s -name '*.yaml' -not -path '*/sealed-secrets/*')
[ "$found" = 0 ] && ok "no Secret carries a value anywhere under infra/k8s/"

# A SealedSecret is asymmetric ciphertext and IS safe to commit; a plain Secret next to one is
# the mistake that mechanism invites.
if compgen -G "infra/k8s/platform/sealed-secrets/*/[!k]*.yaml" >/dev/null; then
  bads=$(grep -l '^kind: Secret' infra/k8s/platform/sealed-secrets/*/*.yaml 2>/dev/null || true)
  if [ -n "$bads" ]; then
    bad "a plain Secret is committed beside the SealedSecrets: $bads"
  else
    ok "the sealed-secrets directory holds only SealedSecrets"
  fi
fi

# -------------------------------------------------------------------------------------
head_ "8. the image namespace is one value and not two"
# -------------------------------------------------------------------------------------
# The manifests carry `<registry>/<service>` rendered from the catalog; the workflows push to
# `${IMAGE_NAMESPACE}/<service>`. Those are two spellings of one fact living in two trees that no
# generator connects — C124 made the workflow side a repository variable and left the manifest side
# a literal, so setting the variable alone moves the push and not the pull.
#
# Nothing about that failure is visible in CI. Every image builds, every image pushes, `ci` and `cd`
# are green, the promotion commit lands, and the first symptom is ImagePullBackOff in a cluster —
# after the deploy has been reported as successful.
catalog_ns=$(sed -n 's#^registry: *ghcr\.io/##p' infra/k8s/service-catalog.yaml | head -1)
if [ -z "$catalog_ns" ]; then
  bad "could not read \`registry: ghcr.io/<namespace>\` from infra/k8s/service-catalog.yaml"
else
  ok "the catalog renders images into 'ghcr.io/$catalog_ns'"
  for wf in images promote nightly; do
    f=".github/workflows/$wf.yml"
    [ -f "$f" ] || continue
    # `IMAGE_NAMESPACE: ${{ vars.IMAGE_NAMESPACE || 'x' }}` — the fallback is what a fresh clone
    # runs with, so it is the value that has to agree, not whatever a variable happens to hold.
    wf_ns=$(sed -n "s/.*IMAGE_NAMESPACE *|| *'\([^']*\)'.*/\1/p" "$f" | head -1)
    if [ -z "$wf_ns" ]; then
      skip "$f names no IMAGE_NAMESPACE fallback"
    elif [ "$wf_ns" = "$catalog_ns" ]; then
      ok "$wf.yml falls back to the same namespace"
    else
      bad "$wf.yml pushes to 'ghcr.io/$wf_ns' but the manifests pull 'ghcr.io/$catalog_ns' —
      the images land in one namespace and the Deployments name the other"
    fi
  done

  # Not every manifest under base/ comes from the generator: base/jobs/ is authored by hand, so
  # `--check` passes straight over it and the namespace there is a literal nobody rewrites. Both
  # job manifests were missed by the Δ 2026-08-10 change for exactly that reason, and `migrate` is
  # the one image whose absence stops a deploy before it starts. Scan the tree, not the catalog.
  strays=$(grep -rn 'image: *ghcr\.io/' infra/k8s --include=*.yaml \
    | grep -v "image: *ghcr\.io/$catalog_ns/" || true)
  if [ -n "$strays" ]; then
    bad "manifests name an image outside 'ghcr.io/$catalog_ns' — nothing pushes these:"
    printf '%s\n' "$strays" | sed 's/^/      /' >&2
  else
    ok "every image: in infra/k8s/ is in 'ghcr.io/$catalog_ns', authored files included"
  fi
fi

# -------------------------------------------------------------------------------------
head_ "9. the printed verify command's kubectl clause"
# -------------------------------------------------------------------------------------
if ! command -v kubectl >/dev/null; then
  skip "kubectl is not installed — the printed clause cannot run here"
elif ! kubectl version --request-timeout=5s >/dev/null 2>&1; then
  skip "no reachable cluster. \`kubectl apply --dry-run=client\` still needs one for discovery;
    .github/workflows/k8s-validate.yml runs it against a kind cluster on every pull request"
else
  for env in staging dev production; do
    if kubectl apply --dry-run=client -k "infra/k8s/overlays/$env" >"$BUILD_DIR/dryrun-$env.txt" 2>&1; then
      ok "kubectl apply --dry-run=client -k infra/k8s/overlays/$env"
    else
      bad "kubectl apply --dry-run=client -k infra/k8s/overlays/$env:"
      tail -20 "$BUILD_DIR/dryrun-$env.txt" | sed 's/^/      /' >&2
    fi
  done
fi

# -------------------------------------------------------------------------------------
echo
echo "==============================================================================="
printf '%d passed, %d failed, %d skipped\n' "$pass" "$failures" "$skipped"
if [ "$failures" -ne 0 ]; then
  exit 1
fi
exit 0
