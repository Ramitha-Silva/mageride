#!/usr/bin/env bash
# =====================================================================================
# Δ MCS-10 — bring the replica to a CI-built tag, without building anything on this box.
#
#   REPLICA_TAG=sha-1a2b3c4 bash infra/replica/pull-deploy.sh
#
# --- How this differs from deploy.sh, and why both exist ------------------------------
# `deploy.sh` is the FIRST-RUN and the FROM-SOURCE path: it generates secrets, issues
# certificates, renders and validates the compose file, then BUILDS the seven images here
# and starts them. On this box a full build is 10-20 minutes and competes for RAM with the
# stack it is about to replace — the root CLAUDE.md is explicit that the ~17-20 GB replica
# and a heavy build do not fit together.
#
# This script is the STEADY-STATE path, and it is what cd.yml calls. The images were already
# built by `replica-images.yml` on a GitHub runner; here we only move the checkout to the
# matching commit, pull, and cycle the containers. That is ~2 minutes and no build pressure.
#
# It deliberately does NOT regenerate secrets or certificates. Those are deploy.sh's, they
# already exist on a box that has been deployed once, and a CD job that could rewrite
# `.env.replica` unattended is a CD job that can lock everyone out of the database.
#
# PREREQUISITE: this box must have been through `deploy.sh` at least once.
# =====================================================================================
set -euo pipefail

REPO_ROOT="${REPO_ROOT:-/root/mageride}"
COMPOSE="$REPO_ROOT/infra/replica/docker-compose.light-replica.yml"
ENV_FILE="$REPO_ROOT/infra/replica/.env.replica"

: "${REPLICA_TAG:?set REPLICA_TAG to the sha-<7> tag to deploy}"
export REPLICA_REGISTRY="${REPLICA_REGISTRY:-ghcr.io/ramitha-silva}"
export REPLICA_TAG

# Which optional profiles move with the deploy. `portals` by DEFAULT, and that is a
# deliberate difference from deploy.sh, which passes no profile at all: that is why
# admin-portal and fleet-portal on this box were five days older than the backend they
# talk to. An immutable sha-<7> tag is only worth having if everything wears the same
# one. Set REPLICA_PROFILES="" to leave the optional services alone.
REPLICA_PROFILES="${REPLICA_PROFILES-portals}"
profile_args=()
for p in $REPLICA_PROFILES; do profile_args+=(--profile "$p"); done

bold() { printf '\n\033[1m▸ %s\033[0m\n' "$1"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
note() { printf '  \033[33m!\033[0m %s\n' "$1"; }
die()  { printf '  \033[31m✗ %s\033[0m\n' "$1" >&2; exit 1; }

cd "$REPO_ROOT"

# -------------------------------------------------------------------------------------
bold "1/6  preflight"
[ -f "$ENV_FILE" ] || die "$ENV_FILE is missing — run infra/replica/deploy.sh on this box first"
[ -f "$COMPOSE" ]  || die "$COMPOSE is missing"
docker info >/dev/null 2>&1 || die "docker is not running"
ok "the box has been deployed before (.env.replica present)"

# The commit whose compose file, env layer and MIGRATIONS match the images being pulled.
# Without this the box would pull new images and run them against the old schema.
#
# Δ MCS-12 — cd.yml does this ITSELF, in the ssh command, before calling this script, and then
# leaves REPLICA_COMMIT unset. It has to: a script that moves the checkout cannot be the thing
# that produces itself, and on the first run the box was still on a commit that predated this
# file — `bash: …/pull-deploy.sh: No such file or directory`, exit 127, before a line of this
# ran. The variable stays for a HAND-driven deploy, where the checkout is wherever the operator
# left it and naming the commit is the only way to be sure.
COMMIT="${REPLICA_COMMIT:-}"
if [ -n "$COMMIT" ]; then
  git fetch --quiet origin || die "git fetch failed"
  git checkout --quiet --detach "$COMMIT" || die "no such commit: $COMMIT"
  ok "checkout moved to $COMMIT"
else
  note "REPLICA_COMMIT unset — deploying images at $REPLICA_TAG against the checkout as it stands"
fi

# -------------------------------------------------------------------------------------
bold "2/6  configuration"
# The same resolution deploy.sh does, but ADVISORY here. Failing closed is right for a human
# at a terminal who can fill a value in; a CD job that goes red on the 62-key backlog this
# repo already carries would be red on every merge for a reason unrelated to the merge.
# The count is printed so a REGRESSION is still visible in the log.
env_layer=()
for candidate in \
  "$REPO_ROOT/infra/env/.env.common.example" \
  "$REPO_ROOT/infra/env/.env.common" \
  "$REPO_ROOT/infra/env/.env.app.example" \
  "$REPO_ROOT/infra/env/.env.app" \
  "$ENV_FILE"; do
  [ -f "$candidate" ] && env_layer+=("$candidate")
done

placeholders=$(
  awk '
    /^[A-Za-z_][A-Za-z0-9_]*=/ {
      key = $0; sub(/=.*/, "", key)
      val = $0; sub(/^[^=]*=/, "", val)
      value[key] = val
    }
    END { for (k in value) if (value[k] ~ /CHANGEME/) print k }
  ' "${env_layer[@]}" | sort
)
count=$([ -z "$placeholders" ] && echo 0 || printf '%s\n' "$placeholders" | wc -l | tr -d ' ')

# The one that decides whether a driving licence can be read at all (MCS-07). Unlike the
# rest of the backlog this key has no counterpart to match, so a placeholder here is a
# guaranteed silent failure — every extraction rejected by Google, every field blank on
# SCR-DA-003a. It is worth failing a deploy over.
if printf '%s\n' "$placeholders" | grep -qx 'Ocr__Gemini__ApiKey'; then
  die "Ocr__Gemini__ApiKey is still a CHANGEME placeholder — document extraction would return
  nothing and every licence field would come back blank (MCS-07). Set it in $ENV_FILE."
fi
ok "Ocr__Gemini__ApiKey resolves to a real value"
[ "$count" -gt 0 ] && note "$count other key(s) still resolve to CHANGEME (known backlog; see deploy.sh)"

set -a
# shellcheck disable=SC1090
. "$ENV_FILE"
set +a

# -------------------------------------------------------------------------------------
bold "3/6  pull $REPLICA_REGISTRY/*:$REPLICA_TAG"

# GHCR packages are PRIVATE by default even when the repository is public — an anonymous
# `docker pull` of one answers `unauthorized`, which is what this box did before this step
# existed. So a credential is required, and the one cd.yml sends is its own GITHUB_TOKEN:
# it carries `packages: read`, it is scoped to this repository, and it dies with the job.
# Nothing long-lived is stored on the box.
#
# Read from STDIN, not from the command line: an `ssh host "GHCR_TOKEN=... bash ..."` puts
# the token in the remote sshd's process arguments, where `ps` can see it.
if [ -z "${GHCR_TOKEN:-}" ] && [ ! -t 0 ]; then
  read -r GHCR_TOKEN || true
fi

if [ -n "${GHCR_TOKEN:-}" ]; then
  printf '%s' "$GHCR_TOKEN" \
    | docker login "${REPLICA_REGISTRY%%/*}" -u "${GHCR_USER:-x-access-token}" --password-stdin >/dev/null \
    || die "docker login to ${REPLICA_REGISTRY%%/*} failed"
  # Logged out on the way out however this script ends, so a token does not outlive the run
  # in ~/.docker/config.json.
  trap 'docker logout "${REPLICA_REGISTRY%%/*}" >/dev/null 2>&1 || true' EXIT
  ok "authenticated to ${REPLICA_REGISTRY%%/*}"
else
  note "no GHCR_TOKEN — relying on whatever credentials this box already has"
fi

docker compose -f "$COMPOSE" "${profile_args[@]}" pull --quiet \
  || die "pull failed — does $REPLICA_TAG exist in $REPLICA_REGISTRY? (replica-images.yml builds it)"
ok "all seven images present locally at $REPLICA_TAG"

# -------------------------------------------------------------------------------------
bold "4/6  up"
# --no-build is the whole point: if a tag is missing we want the pull above to have failed
# loudly, not a 20-minute build to start silently on the box serving api.mageride.lk.
docker compose -f "$COMPOSE" "${profile_args[@]}" up -d --no-build --wait --wait-timeout 600 \
  || {
    printf '\n--- container state ---\n'
    docker compose -f "$COMPOSE" ps
    printf '\n--- app-services, last 40 ---\n'
    docker compose -f "$COMPOSE" logs --tail 40 app-services 2>&1 | tail -40
    die "containers did not become healthy within 600s"
  }
ok "every container healthy"

# -------------------------------------------------------------------------------------
bold "5/6  what actually landed"
running=$(docker inspect --format '{{.Config.Image}}' mageride-replica-app-services-1 2>/dev/null || echo unknown)
echo "  app-services image: $running"
case "$running" in
  *":$REPLICA_TAG") ok "running the tag that was asked for" ;;
  *) die "app-services is running '$running', not $REPLICA_TAG" ;;
esac

# -------------------------------------------------------------------------------------
bold "6/6  smoke"
bash "$REPO_ROOT/infra/replica/smoke.sh" || die "smoke failed — the replica is up but not correct"

printf '\n\033[1m✓ replica is at %s.\033[0m\n' "$REPLICA_TAG"
