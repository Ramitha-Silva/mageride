#!/usr/bin/env bash
# =====================================================================================
# infra/replica/nominatim/deploy-nominatim.sh — deploy the self-hosted geocoder onto its own VPS.
#
#   NOMINATIM_SSH_PASSWORD=... bash infra/replica/nominatim/deploy-nominatim.sh
#   ...                        bash infra/replica/nominatim/deploy-nominatim.sh --dry-run
#   ...                        bash infra/replica/nominatim/deploy-nominatim.sh --reimport
#   ...                        bash infra/replica/nominatim/deploy-nominatim.sh --status
#
# Run FROM the replica box. It installs Docker if absent, copies the compose file, generates the
# geocoder's internal Postgres password, starts the import, restricts port 8080 to this box, and
# writes `Query__NominatimBaseUrl` into the replica's .env.replica.
#
# ------------------------------------------------------------------------------------
# WHY THIS IS A SEPARATE BOX AT ALL
# ------------------------------------------------------------------------------------
# The spec is explicit: Nominatim wants 8 GB for the Sri Lanka extract, "which is a third of the 24 GB
# budget… Recommended for light replica: host Nominatim on a separate cheap VPS." Co-locating it would
# leave the eleven core containers ~8 GB, and `infra/replica/guardrail.sh` would refuse the deploy.
#
# ------------------------------------------------------------------------------------
# CREDENTIALS
# ------------------------------------------------------------------------------------
# The SSH password comes from NOMINATIM_SSH_PASSWORD in the environment and is never written to disk
# or to a log. The geocoder's own Postgres password is generated here and stored only in
# `.env.nominatim` ON THE TARGET, mode 600 — nothing about it needs to exist in this repository.
#
# Prefer a key: `ssh-copy-id root@<host>` once, and this script uses it automatically and stops
# needing the variable.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
NOMINATIM_DIR="$PWD"
cd ../../.. || exit 2
REPO_ROOT="$PWD"

HOST="${NOMINATIM_HOST:-45.77.37.208}"
PORT="${NOMINATIM_SSH_PORT:-22}"
USER="${NOMINATIM_SSH_USER:-root}"
REMOTE_DIR="/opt/mageride-nominatim"
REPLICA_ENV="infra/replica/.env.replica"

dry_run=0
reimport=0
status_only=0
for arg in "$@"; do
  case "$arg" in
    --dry-run)  dry_run=1 ;;
    --reimport) reimport=1 ;;
    --status)   status_only=1 ;;
    -h|--help)  sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

step() { printf '\n\033[1m▸ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
die()  { printf '  \033[31m✗\033[0m %s\n' "$*" >&2; exit 1; }
note() { printf '  \033[33m!\033[0m %s\n' "$*"; }

# --- how we reach the box ------------------------------------------------------------
# A key if one works, the password otherwise. Trying the key first means a box that has been
# ssh-copy-id'd never needs the variable again.
SSH_BASE=(ssh -o StrictHostKeyChecking=no -o ConnectTimeout=10 -p "$PORT")

if "${SSH_BASE[@]}" -o BatchMode=yes "${USER}@${HOST}" true 2>/dev/null; then
  remote() { "${SSH_BASE[@]}" "${USER}@${HOST}" "$@"; }
  copy_in() { scp -q -o StrictHostKeyChecking=no -P "$PORT" "$1" "${USER}@${HOST}:$2"; }
  auth="ssh key"
elif [ -n "${NOMINATIM_SSH_PASSWORD:-}" ]; then
  command -v sshpass >/dev/null || die "sshpass is not installed and no ssh key works.
      apt-get install -y sshpass, or ssh-copy-id ${USER}@${HOST} once."
  export SSHPASS="$NOMINATIM_SSH_PASSWORD"
  remote() { sshpass -e "${SSH_BASE[@]}" "${USER}@${HOST}" "$@"; }
  copy_in() { sshpass -e scp -q -o StrictHostKeyChecking=no -P "$PORT" "$1" "${USER}@${HOST}:$2"; }
  auth="password from NOMINATIM_SSH_PASSWORD"
else
  die "cannot reach ${USER}@${HOST}: no ssh key works and NOMINATIM_SSH_PASSWORD is unset."
fi

# =====================================================================================
step "1/7  the target"
# =====================================================================================
facts=$(remote '. /etc/os-release; printf "%s|%s|%s|%s|%s" "$PRETTY_NAME" "$(free -m | awk "/^Mem:/{print \$2}")" "$(nproc)" "$(free -m | awk "/^Swap:/{print \$2}")" "$(df -BG --output=avail / | tail -1 | tr -dc 0-9)"' 2>&1) \
  || die "could not reach ${USER}@${HOST} ($auth)"

IFS='|' read -r os mem_mb cores swap_mb disk_gb <<<"$facts"
ok "${HOST}: ${os}, ${mem_mb} MiB RAM, ${cores} cores, ${swap_mb} MiB swap, ${disk_gb} GiB free (via ${auth})"

# The import's peak is well above steady state. Swap is what stops an OOM kill turning into an
# infinite restart-and-reimport loop, which is the failure mode that wastes a whole afternoon.
[ "${swap_mb:-0}" -ge 2048 ] || note "only ${swap_mb} MiB of swap. The import's peak exceeds its
      steady state and an OOM kill restarts the import from scratch. 4-8 GiB is worth adding."
[ "${disk_gb:-0}" -ge 20 ] || die "only ${disk_gb} GiB free; the import needs room for the extract,
      the flatnode file and the database"

if [ "$status_only" = 1 ]; then
  step "status"
  # --env-file, because the compose file marks NOMINATIM_PASSWORD `${VAR:?}` and `ps` fails on
  # interpolation without it — printing an error instead of the status it was asked for.
  remote "cd ${REMOTE_DIR} 2>/dev/null && docker compose -f docker-compose.nominatim.yml --env-file .env.nominatim ps" 2>&1 | sed 's/^/  /'
  echo
  probe=$(remote "curl -sS --max-time 10 http://127.0.0.1:8080/status 2>&1 | head -c 80" 2>&1)
  echo "  /status: ${probe:-<no answer>}"
  exit 0
fi

# =====================================================================================
step "2/7  docker"
# =====================================================================================
if remote 'command -v docker >/dev/null' 2>/dev/null; then
  ok "docker present: $(remote 'docker --version' 2>&1)"
else
  if [ "$dry_run" = 1 ]; then
    note "docker is absent and would be installed"
  else
    ok "installing docker (this box had none)"
    remote 'bash -s' <<'REMOTE_INSTALL' 2>&1 | tail -2 | sed 's/^/      /'
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq ca-certificates curl gnupg >/dev/null
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
codename=$(. /etc/os-release && echo "$VERSION_CODENAME")
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${codename} stable" > /etc/apt/sources.list.d/docker.list
# A brand-new Ubuntu release often has no Docker CE suite yet. The distro packages are adequate for
# one container and are the honest fallback rather than pinning a codename that is not this box's.
if apt-get update -qq 2>/dev/null && apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-compose-plugin >/dev/null 2>&1; then
  echo "docker-ce from the Docker repo"
else
  rm -f /etc/apt/sources.list.d/docker.list
  apt-get update -qq
  apt-get install -y -qq docker.io docker-compose-v2 >/dev/null
  echo "docker.io from the Ubuntu repo (no Docker CE suite for ${codename})"
fi
systemctl enable --now docker >/dev/null 2>&1 || true
docker --version
REMOTE_INSTALL
    remote 'command -v docker >/dev/null' 2>/dev/null || die "docker did not install"
    ok "docker installed"
  fi
fi

# =====================================================================================
step "3/7  the compose file and the geocoder's own password"
# =====================================================================================
if [ "$dry_run" = 1 ]; then
  note "would copy docker-compose.nominatim.yml to ${REMOTE_DIR} and generate .env.nominatim"
else
  remote "mkdir -p ${REMOTE_DIR}" || die "could not create ${REMOTE_DIR}"
  copy_in "${NOMINATIM_DIR}/docker-compose.nominatim.yml" "${REMOTE_DIR}/docker-compose.nominatim.yml" \
    || die "could not copy the compose file"
  ok "compose file in ${REMOTE_DIR}"

  # Generated on the TARGET and never leaving it. Nothing in this repository needs the geocoder's
  # internal Postgres password — it is reachable only from inside that container.
  remote "test -f ${REMOTE_DIR}/.env.nominatim" 2>/dev/null && ok ".env.nominatim exists — left alone" || {
    remote "umask 077; printf 'NOMINATIM_PASSWORD=%s\n' \"\$(openssl rand -base64 24 | tr -d '\n=+/')\" > ${REMOTE_DIR}/.env.nominatim" \
      || die "could not write .env.nominatim"
    ok "generated ${REMOTE_DIR}/.env.nominatim (mode 600, never copied off the box)"
  }
fi

# =====================================================================================
step "4/7  the import"
# =====================================================================================
if [ "$dry_run" = 1 ]; then
  note "would start the container; a first import of the Sri Lanka extract takes tens of minutes"
elif [ "$reimport" = 1 ]; then
  note "REIMPORT: discarding the imported database and starting again. This is the only thing that
      should ever delete that volume, and it costs the whole import time."
  remote "cd ${REMOTE_DIR} && docker compose -f docker-compose.nominatim.yml --env-file .env.nominatim down --volumes" >/dev/null 2>&1
  remote "cd ${REMOTE_DIR} && docker compose -f docker-compose.nominatim.yml --env-file .env.nominatim up -d" >/dev/null 2>&1 \
    || die "could not start the container"
  ok "import restarted"
else
  already=$(remote "cd ${REMOTE_DIR} && docker compose -f docker-compose.nominatim.yml --env-file .env.nominatim ps --services --filter status=running 2>/dev/null | tr -d '\n'" 2>&1)
  if [ "$already" = "nominatim" ]; then
    ok "already running — not touching the imported database"
  else
    remote "cd ${REMOTE_DIR} && docker compose -f docker-compose.nominatim.yml --env-file .env.nominatim up -d" >/dev/null 2>&1 \
      || die "could not start the container"
    ok "started. The FIRST boot imports the extract and takes tens of minutes; the container stays
      'starting' until /status answers OK, which is deliberate."
  fi
fi

# =====================================================================================
step "5/7  restrict 8080 to the replica"
# =====================================================================================
# An open geocoder is a free service somebody else will find, and the abuse is indistinguishable from
# traffic. Only the replica needs to reach it.
replica_ip="${REPLICA_PUBLIC_IP_FOR_NOMINATIM:-$(curl -sS --max-time 10 https://api.ipify.org 2>/dev/null || echo '')}"

if [ -z "$replica_ip" ]; then
  note "could not determine this box's public address, so 8080 is left as the compose file binds it.
      Set REPLICA_PUBLIC_IP_FOR_NOMINATIM and run again to close it."
elif [ "$dry_run" = 1 ]; then
  note "would allow ${replica_ip} to 8080 and drop the rest"
else
  remote "bash -s" <<REMOTE_FW >/dev/null 2>&1
set -e
if command -v ufw >/dev/null; then
  ufw --force enable >/dev/null 2>&1 || true
  ufw allow 22/tcp >/dev/null 2>&1 || true
  ufw allow from ${replica_ip} to any port 8080 proto tcp >/dev/null 2>&1 || true
  ufw deny 8080/tcp >/dev/null 2>&1 || true
fi
REMOTE_FW
  if remote "command -v ufw >/dev/null && ufw status | grep -q '${replica_ip}'" 2>/dev/null; then
    ok "8080 allowed from ${replica_ip}, denied elsewhere (ufw); 22 kept open"
  else
    note "could not confirm a firewall rule for 8080. Check by hand — an open geocoder gets used."
  fi
fi

# =====================================================================================
step "6/7  point the replica at it"
# =====================================================================================
# QUERY, not TRANSIT. Nominatim is query-svc's — Query.Api/Geo/NominatimClient.cs behind
# GET /v1/geo/search and /v1/geo/reverse, keyed `Query:NominatimBaseUrl`. transit-svc's
# /v1/geo/parse-maps-link resolves a short Google-Maps URL to a lat/lng and touches no geocoder at
# all; the two live under the same /v1/geo prefix and are different services, which is how the first
# version of this script came to write a key nothing reads.
#
# Trailing slash to match env/.env.app.example's own form.
base_url="http://${HOST}:8080/"

if [ "$dry_run" = 1 ]; then
  note "would set Query__NominatimBaseUrl=${base_url} in ${REPLICA_ENV}"
elif [ ! -f "$REPLICA_ENV" ]; then
  note "${REPLICA_ENV} does not exist yet. After infra/replica/deploy.sh has run, add:
      Query__NominatimBaseUrl=${base_url}"
elif grep -q '^Query__NominatimBaseUrl=' "$REPLICA_ENV"; then
  sed -i "s|^Query__NominatimBaseUrl=.*|Query__NominatimBaseUrl=${base_url}|" "$REPLICA_ENV"
  ok "updated Query__NominatimBaseUrl in ${REPLICA_ENV}"
else
  {
    echo
    echo "# --- generated by deploy-nominatim.sh: the self-hosted geocoder on its own VPS ---"
    echo "Query__NominatimBaseUrl=${base_url}"
  } >> "$REPLICA_ENV"
  ok "added Query__NominatimBaseUrl=${base_url} to ${REPLICA_ENV}"
fi

note "transit-svc reads this at start-up, so app-services needs a restart to pick it up:
      docker compose -f infra/replica/docker-compose.light-replica.yml up -d --force-recreate app-services"

# =====================================================================================
step "7/7  where the import has got to"
# =====================================================================================
if [ "$dry_run" = 1 ]; then
  printf '\n\033[1m✓ dry run complete — nothing was installed, copied or started.\033[0m\n'
  exit 0
fi

state=$(remote "cd ${REMOTE_DIR} && docker compose -f docker-compose.nominatim.yml --env-file .env.nominatim ps --format '{{.Service}} {{.Health}}' 2>/dev/null" 2>&1)
echo "  ${state:-<no container>}"
probe=$(remote "curl -sS --max-time 10 http://127.0.0.1:8080/status 2>&1 | head -c 60" 2>&1)
echo "  /status: ${probe:-<not answering yet>}"

printf '\n\033[1m▸ the import runs unattended.\033[0m Follow it with:\n'
echo "  bash infra/replica/nominatim/deploy-nominatim.sh --status"
# --env-file is not optional in that command: the compose file marks NOMINATIM_PASSWORD `${VAR:?}`,
# so `docker compose logs` without it fails on interpolation before it prints a single line.
echo "  ssh ${USER}@${HOST} 'cd ${REMOTE_DIR} && docker compose -f docker-compose.nominatim.yml --env-file .env.nominatim logs -f --tail 30'"
