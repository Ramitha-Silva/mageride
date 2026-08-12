#!/usr/bin/env bash
# =====================================================================================
# 10 — no secrets in the repository (ADD §12.4, D7' §13, ASVS V14.3 / V6.4)
#
# "No secrets in environment variables, Docker files, or Git repositories" (ADD §12.4). Two halves:
# nothing secret is TRACKED, and the ignore rules that keep it that way are still in place.
#
# The scan is over `git ls-files`, not over the working tree — an untracked `.env.replica` full of
# real values is exactly what is supposed to happen, and a scan that flagged it would be trained
# away within a week.
# =====================================================================================

# shellcheck shell=bash

step "10. secrets are not in the repository (ADD §12.4, D7' §13)"

# -------------------------------------------------------------------------------------
# 10.1 — the ignore rules
# -------------------------------------------------------------------------------------
missing_rules=()
for rule in '.env' '.env.*' '*.pem' '*.key'; do
  grep -Fxq "$rule" .gitignore || missing_rules+=("$rule")
done

if [ ${#missing_rules[@]} -eq 0 ]; then
  ok ".gitignore still excludes .env, .env.*, *.pem and *.key"
else
  bad ".gitignore no longer excludes: ${missing_rules[*]} — a real key is one 'git add .' away"
fi

grep -Fxq '!.env.*.example' .gitignore \
  && ok "the .env.*.example templates stay tracked (D7' §4), which is the point of the exception" \
  || warn "'!.env.*.example' is gone from .gitignore; D7' §4's templates may have stopped being tracked"

# -------------------------------------------------------------------------------------
# 10.2 — tracked files that should never be tracked
# -------------------------------------------------------------------------------------
# `.example` is excluded on purpose and is the whole point of the exception in .gitignore:
# D7' §4's templates are tracked, carry only placeholders, and 10.3 is what checks that.
tracked_material=$(git ls-files \
  | grep -Ev '\.example$' \
  | grep -Ei '(^|/)\.env($|\.)|\.pem$|\.key$|\.p12$|\.pfx$|\.jks$|(^|/)id_(rsa|ed25519)$' \
  || true)

if [ -z "$tracked_material" ]; then
  ok "no key, certificate or .env file is tracked"
else
  bad "key material is tracked in git:"
  while IFS= read -r file; do note "$file"; done <<<"$tracked_material"
fi

# -------------------------------------------------------------------------------------
# 10.3 — a filled-in placeholder in a template
#
# Every secret in `.env.app.example` and `.env.common.example` is `CHANGEME_…`. Somebody who ran
# the deployment against the template rather than a copy would commit the real value in its place,
# and the diff would look like an ordinary configuration change.
# -------------------------------------------------------------------------------------
filled=0
for template in infra/env/.env.*.example; do
  [ -f "$template" ] || continue

  while IFS= read -r line; do
    key="${line%%=*}"
    value="${line#*=}"

    # Only the keys the templates themselves mark secret. A non-secret setting with a real value
    # is what a template is FOR.
    case "$key" in
      *Secret*|*SECRET*|*Password*|*PASSWORD*|*ApiKey*|*API_KEY*|*Key|*KEY|*Token*|*TOKEN*|*Pem*|*PEM*) ;;
      *) continue ;;
    esac

    # Structural, not entropy-based: a template's secret is a placeholder, an empty string, or a
    # variable reference. Anything else is a value somebody pasted.
    #
    # The templates spell a placeholder four ways and all four are deliberate — `CHANGEME_x`,
    # `x-change-me`, `mageride-dev-x` and `mageride_dev`. A check that only knew the first would
    # report five honest rows as findings and be switched off within a week, which is worse than
    # not having it.
    case "$value" in
      ''|'${'*|'<'*|'"'*) continue ;;
    esac

    printf '%s' "$value" | grep -qiE 'change|placeholder|example|sample|(^|[-_])dev([-_]|$)' && continue

    # A settings NAME rather than a value — several rows point at where the real one lives.
    case "$value" in
      *__*|*:*) continue ;;
    esac

    [ "${#value}" -ge 12 ] || continue

    filled=$((filled+1))
    note "$template: $key looks like a real value, not a placeholder"
  done < <(grep -E '^[A-Za-z][A-Za-z0-9_]*=' "$template" || true)
done

if [ "$filled" -eq 0 ]; then
  ok "every secret in infra/env/*.example is still a placeholder"
else
  bad "$filled secret(s) in the env templates carry what looks like a real value"
fi

# -------------------------------------------------------------------------------------
# 10.4 — External Secrets Operator, which is what D7' §13 says holds the real ones
# -------------------------------------------------------------------------------------
eso_count=$(git ls-files 'infra/k8s/platform/external-secrets/base/*.yaml' | wc -l)

if [ "$eso_count" -ge 20 ]; then
  ok "${eso_count} ExternalSecret manifests sync Vault → K8s (D7' §13)"
else
  bad "only ${eso_count} ExternalSecret manifest(s); D7' §13 puts every service's secrets in Vault"
fi

if [ -f docs/runbooks/secret-rotation.md ]; then
  ok "the rotation procedure exists (docs/runbooks/secret-rotation.md)"

  rotation_gaps=()
  for what in 'JWT signing key' 'DB credentials' 'MQTT' 'webhook secret'; do
    grep -qi "$what" docs/runbooks/secret-rotation.md || rotation_gaps+=("$what")
  done

  if [ ${#rotation_gaps[@]} -eq 0 ]; then
    ok "it covers every rotation D7' §13 schedules"
  else
    bad "the rotation runbook does not cover: ${rotation_gaps[*]}"
  fi
else
  bad "no docs/runbooks/secret-rotation.md — D7' §13 schedules five rotations and names no procedure"
fi
