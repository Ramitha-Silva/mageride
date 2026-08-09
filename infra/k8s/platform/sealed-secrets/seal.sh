#!/usr/bin/env bash
# =====================================================================================
# Seal the platform's credentials for the dev / K3s cluster (D7' §13's sealed-secrets path).
#
#   bash infra/k8s/platform/sealed-secrets/seal.sh --generate   --cert <public.pem>
#   bash infra/k8s/platform/sealed-secrets/seal.sh --from-vault --cert <public.pem>
#
# Writes one SealedSecret per Kubernetes Secret into platform/sealed-secrets/dev/, which is
# safe to commit: the ciphertext can only be opened by the controller's private key inside the
# target cluster. See README.md for the invariant and for what --generate will not invent.
#
# THE SECRET LIST COMES FROM infra/k8s/service-catalog.yaml, not from this script. Every
# Deployment's `envFrom` and every ExternalSecret is generated from that file, so reading it
# here is what keeps the sealed set from drifting away from what the pods actually mount — the
# failure mode being a pod stuck in CreateContainerConfigError on a Secret nobody sealed.
#
# PLAINTEXT NEVER TOUCHES DISK. Every value is piped into `kubeseal` on stdin through a process
# substitution; there is no temporary file to forget to delete.
# =====================================================================================
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
OUT="$REPO/infra/k8s/platform/sealed-secrets/dev"
NAMESPACE="mageride"
MODE=""
CERT=""

usage() {
  sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

while [ $# -gt 0 ]; do
  case "$1" in
    --generate)  MODE=generate; shift ;;
    --from-vault) MODE=vault; shift ;;
    --cert)      CERT="${2:?--cert needs a path}"; shift 2 ;;
    -h|--help)   usage; exit 0 ;;
    *) echo "error: unrecognised argument '$1'" >&2; usage >&2; exit 2 ;;
  esac
done

[ -n "$MODE" ] || { echo "error: pass --generate or --from-vault" >&2; exit 2; }
[ -n "$CERT" ] || { echo "error: pass --cert <public.pem> (kubeseal --fetch-cert)" >&2; exit 2; }
[ -f "$CERT" ] || { echo "error: no such certificate: $CERT" >&2; exit 1; }

command -v kubeseal >/dev/null || { echo "error: kubeseal is not on PATH (see README.md)" >&2; exit 1; }
command -v python3 >/dev/null || { echo "error: python3 is required to read the catalog" >&2; exit 1; }
if [ "$MODE" = vault ]; then
  command -v vault >/dev/null || { echo "error: the vault CLI is required for --from-vault" >&2; exit 1; }
  : "${VAULT_MOUNT:?set VAULT_MOUNT (e.g. mageride-dev)}"
fi

mkdir -p "$OUT"

# --- the credential list, from the catalog -------------------------------------------
# Prints one line per (kubernetes secret, key, vault path, vault property, kind) where kind is
# `shared` (MageRide generates it) or `provider` (a third party issued it, so --generate cannot
# invent one).
catalog_credentials() {
  python3 - "$REPO/infra/k8s/service-catalog.yaml" <<'PY'
import sys, yaml
cat = yaml.safe_load(open(sys.argv[1], encoding="utf-8"))
aliases = cat.get("aliases") or {}
rows = []
for svc in cat["services"]:
    for key in svc.get("secrets") or []:
        a = aliases.get(key)
        path = a["path"] if a else svc["name"]
        prop = (a.get("property", key) if a else key)
        kind = "provider" if path == "providers" else "shared"
        rows.append((f"{svc['name']}-secret", key, path, prop, kind))
# The hand-written ones. Kept here rather than parsed out of the ExternalSecret YAML so that a
# missing entry is a visible omission in one list instead of a silent gap.
rows += [
    ("common-secret", "pgPassword", "common", "postgres_superuser_password", "shared"),
    ("common-secret", "s3AccessKey", "common", "s3_access_key", "shared"),
    ("common-secret", "s3SecretKey", "common", "s3_secret_key", "shared"),
    ("common-secret", "mqttSessionTokenSecret", "common", "mqtt_session_token_secret", "shared"),
    ("postgres-superuser", "password", "common", "postgres_superuser_password", "shared"),
    ("emqx-auth", "session_token_secret", "common", "mqtt_session_token_secret", "shared"),
    ("emqx-auth", "erlang_cookie", "common", "emqx_erlang_cookie", "shared"),
    ("minio-kms", "kms_secret_key", "common", "minio_kms_secret_key", "shared"),
]
for r in rows:
    print("\t".join(r))
PY
}

# --- value resolution -----------------------------------------------------------------
# Cached per (path, property) so one credential under six env-var names is ONE value. That is
# the same invariant the `aliases` table enforces for ESO, and it is the reason this script
# resolves by Vault coordinates rather than by env-var name.
declare -A VALUES

value_for() {
  local path="$1" prop="$2" kind="$3" cache="$1/$2"
  if [ -n "${VALUES[$cache]:-}" ]; then printf '%s' "${VALUES[$cache]}"; return; fi

  local v=""
  if [ "$MODE" = vault ]; then
    v="$(vault kv get -mount="$VAULT_MOUNT" -field="$prop" "$path" 2>/dev/null || true)"
    if [ -z "$v" ]; then
      echo "  warning: $VAULT_MOUNT/$path#$prop is not set in Vault — sealing an empty value" >&2
    fi
  elif [ "$kind" = provider ]; then
    # A third party issued it. Prompting one by one for seventeen provider credentials is worse
    # than sealing them empty and letting each service announce its own degraded state at
    # start-up, which every one of them is written to do.
    echo "  note: $path#$prop is a provider credential — sealed EMPTY (see README.md)" >&2
  else
    # 32 bytes of urandom, base64url. Long enough for every options validator that checks a
    # key's length, and URL-safe so a value that ends up in a signed link is not re-encoded.
    v="$(head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n')"
  fi
  VALUES[$cache]="$v"
  printf '%s' "$v"
}

# --- seal -----------------------------------------------------------------------------
# One kubeseal invocation per Secret, fed a plain Secret on stdin that never exists as a file.
secrets="$(catalog_credentials | cut -f1 | sort -u)"
count=0

for secret in $secrets; do
  echo "sealing $secret"
  {
    printf 'apiVersion: v1\nkind: Secret\nmetadata:\n  name: %s\n  namespace: %s\nstringData:\n' \
      "$secret" "$NAMESPACE"
    while IFS=$'\t' read -r s key path prop kind; do
      [ "$s" = "$secret" ] || continue
      v="$(value_for "$path" "$prop" "$kind")"
      # Block scalar, so a value containing ':' or '#' (a DSN, a PEM) survives.
      printf '  %s: |-\n    %s\n' "$key" "$v"
    done < <(catalog_credentials)
  } | kubeseal --cert "$CERT" --format yaml --namespace "$NAMESPACE" > "$OUT/$secret.yaml"
  count=$((count + 1))
done

# --- the kustomization, regenerated so a new Secret is never left unreferenced ---------
{
  echo "# GENERATED by infra/k8s/platform/sealed-secrets/seal.sh — do not hand-edit."
  echo "#"
  echo "# SealedSecret ciphertext, safe to commit: only the sealed-secrets controller's private"
  echo "# key inside the dev cluster can open it. Re-run seal.sh to rotate."
  echo "---"
  echo "apiVersion: kustomize.config.k8s.io/v1beta1"
  echo "kind: Kustomization"
  echo "namespace: $NAMESPACE"
  echo "resources:"
  for secret in $secrets; do echo "  - $secret.yaml"; done
} > "$OUT/kustomization.yaml"

echo
echo "sealed $count secret(s) into ${OUT#"$REPO"/}"
echo "commit them; ArgoCD's dev Application syncs this directory."
