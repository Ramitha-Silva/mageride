#!/usr/bin/env bash
# =====================================================================================
# infra/replica/publish-tile-style.sh — publish the web MapLibre style to the tiles VPS.
#
#   TILES_SSH_PASSWORD=... bash infra/replica/publish-tile-style.sh
#   ...                    bash infra/replica/publish-tile-style.sh --dry-run
#   ...                    bash infra/replica/publish-tile-style.sh --verify
#
# ------------------------------------------------------------------------------------
# WHY A STYLE HAS TO BE PUBLISHED AT ALL
# ------------------------------------------------------------------------------------
# The four native apps BUNDLE their cartography — `res/raw/map_style_*.json` on Android,
# `Resources/MapStyle*.json` on iOS — and fetch only the archive and the glyphs. A browser
# cannot do that: `web-passenger` (SCR-WT) and `fleet-portal` (SCR-FP-007) are handed a
# style DOCUMENT URL (`WEB_PASSENGER_MAP_STYLE_URL` / `FLEET_PORTAL_MAP_STYLE_URL`, D-14's
# `tile-cdn`) and fetch it over HTTP. Unset, both draw markers on an empty canvas and say
# so — a missing basemap must not read as a missing driver.
#
# ------------------------------------------------------------------------------------
# WHY IT IS GENERATED AND NOT AUTHORED
# ------------------------------------------------------------------------------------
# One cartography, not two. The published style is the passenger app's style with the
# `__PMTILES_URL__` placeholder substituted — the same nine layers off the same archive —
# so a junction looks the same in the browser as it does in the app. Authoring a second
# style by hand is how the two quietly drift apart.
#
# The app styles are the source of truth. Change those, re-run this.
#
# ------------------------------------------------------------------------------------
# WHY `pmtiles://` KEEPS AN ABSOLUTE URL AFTER THE SCHEME
# ------------------------------------------------------------------------------------
# `portals/web-passenger/src/components/TrackMap.tsx` registers
# `addProtocol('pmtiles', new Protocol().tile)`, and the browser Protocol takes the archive
# URL after the scheme and resolves tiles itself with HTTP range requests. So the value is
# `pmtiles://https://<host>/lk.pmtiles`, not a bare path. There is no tile server on either
# side — the native apps do the same thing through MapLibre's own PMTiles file source.
#
# The `glyphs` URL is already absolute in the app style and is left untouched.
#
# ------------------------------------------------------------------------------------
# WHAT THIS SCRIPT DOES NOT DO
# ------------------------------------------------------------------------------------
# It does not produce `lk.pmtiles` and it does not install nginx, the certificate or the
# fonts. Those are the one-time build of the tile host:
#
#   pmtiles extract https://build.protomaps.com/<DATE>.pmtiles /var/www/tiles/lk.pmtiles \
#     --bbox=79.4,5.7,82.0,10.0
#
# with `sri-lanka.pmtiles` symlinked beside it for the iOS builds, which ask for that name.
# Refreshing the basemap is that command again; this script only republishes the style,
# which is cheap and safe to re-run.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")/../.." || exit 2
REPO_ROOT="$PWD"

HOST="${TILES_HOST:-45.77.37.208}"
PORT="${TILES_SSH_PORT:-22}"
USER="${TILES_SSH_USER:-root}"
REMOTE_DIR="${TILES_REMOTE_DIR:-/var/www/tiles}"
BASE_URL="${TILES_BASE_URL:-https://tiles.mageride.lk}"
ARCHIVE="${TILES_ARCHIVE:-lk.pmtiles}"

# The passenger app's styles. The driver app's are byte-identical by design (C076's note:
# "the two JSON files are byte-identical to the driver app's, which is what makes a junction
# look the same to both sides of a ride"), so either would do.
SRC_DIR="apps/passenger-android/src/main/res/raw"

dry_run=0
verify_only=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) dry_run=1 ;;
    --verify)  verify_only=1 ;;
    -h|--help) sed -n '2,50p' "$0"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

step() { printf '\n\033[1m▸ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
die()  { printf '  \033[31m✗\033[0m %s\n' "$*" >&2; exit 1; }
note() { printf '  \033[33m!\033[0m %s\n' "$*"; }

# --- how we reach the box ------------------------------------------------------------
# Key first, password second — same shape as deploy-nominatim.sh, so a box that has been
# ssh-copy-id'd never needs the variable. The password is read from the environment and is
# never written to disk or echoed.
SSH_BASE=(ssh -o StrictHostKeyChecking=no -o ConnectTimeout=10 -p "$PORT")

connect() {
  if "${SSH_BASE[@]}" -o BatchMode=yes "${USER}@${HOST}" true 2>/dev/null; then
    remote()  { "${SSH_BASE[@]}" "${USER}@${HOST}" "$@"; }
    copy_in() { scp -q -o StrictHostKeyChecking=no -P "$PORT" "$1" "${USER}@${HOST}:$2"; }
    auth="ssh key"
  elif [ -n "${TILES_SSH_PASSWORD:-}" ]; then
    command -v sshpass >/dev/null \
      || die "sshpass is not installed and no ssh key works.
      apt-get install -y sshpass, or ssh-copy-id ${USER}@${HOST} once."
    export SSHPASS="$TILES_SSH_PASSWORD"
    remote()  { sshpass -e "${SSH_BASE[@]}" "${USER}@${HOST}" "$@"; }
    copy_in() { sshpass -e scp -q -o StrictHostKeyChecking=no -P "$PORT" "$1" "${USER}@${HOST}:$2"; }
    auth="password"
  else
    die "cannot reach ${USER}@${HOST}: no ssh key works and TILES_SSH_PASSWORD is unset."
  fi
}

# --- verification, which is also the whole of --verify --------------------------------
# Three requests in the order a browser makes them: the style, then the archive it names,
# then the glyphs a label needs. The archive check is a RANGE request because that is what
# a PMTiles client issues — a 200 where a 206 belongs means range requests are not being
# served and the map will fail at the first tile even though the file is plainly there.
verify() {
  local style_url="${BASE_URL}/style.json" rc=0
  local body; body=$(curl -fsS --max-time 25 "$style_url" 2>/dev/null) \
    || { note "GET ${style_url} failed"; return 1; }

  local archive glyph
  archive=$(printf '%s' "$body" | python3 -c \
    'import json,sys; print(list(json.load(sys.stdin)["sources"].values())[0]["url"].replace("pmtiles://",""))' 2>/dev/null)
  glyph=$(printf '%s' "$body" | python3 -c \
    'import json,sys,urllib.parse
d=json.load(sys.stdin)
st=sorted({f for l in d["layers"] for f in (l.get("layout",{}).get("text-font") or [])})
print(d["glyphs"].replace("{fontstack}",urllib.parse.quote(st[0])).replace("{range}","0-255") if st else "")' 2>/dev/null)

  ok "style ${style_url} → 200"

  local code
  code=$(curl -s -o /dev/null -w '%{http_code}' -r 0-1023 --max-time 25 "$archive")
  [ "$code" = "206" ] && ok "archive ${archive} → 206 (range requests served)" \
                      || { note "archive ${archive} → ${code}, expected 206"; rc=1; }

  if [ -n "$glyph" ]; then
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 25 "$glyph")
    [ "$code" = "200" ] && ok "glyphs → 200" || { note "glyphs ${glyph} → ${code}"; rc=1; }
  fi
  return $rc
}

if [ "$verify_only" = 1 ]; then
  step "verifying the published style"
  verify || die "the published style does not resolve end to end"
  ok "published style resolves"
  exit 0
fi

# --- generate -------------------------------------------------------------------------
step "generating the style from ${SRC_DIR}"
command -v python3 >/dev/null || die "python3 is required to substitute and validate the style"

OUT_DIR=$(mktemp -d)
trap 'rm -rf "$OUT_DIR"' EXIT

PM_URL="${BASE_URL}/${ARCHIVE}" SRC="${REPO_ROOT}/${SRC_DIR}" OUT="$OUT_DIR" python3 - <<'PY' || die "could not generate the style"
import json, os, sys
src, out, pm = os.environ["SRC"], os.environ["OUT"], os.environ["PM_URL"]
for theme in ("light", "dark"):
    path = f"{src}/map_style_{theme}.json"
    raw = open(path, encoding="utf-8").read()
    if "__PMTILES_URL__" not in raw:
        sys.exit(f"{path} carries no __PMTILES_URL__ placeholder — has the app style changed shape?")
    doc = json.loads(raw.replace("__PMTILES_URL__", pm))   # parsing IS the validity check
    doc["name"] = f"MageRide {theme.capitalize()}"
    json.dump(doc, open(f"{out}/style-{theme}.json", "w", encoding="utf-8"), separators=(",", ":"))
    print(f"  style-{theme}.json  {len(doc['layers'])} layers  →  {list(doc['sources'].values())[0]['url']}")
PY

if [ "$dry_run" = 1 ]; then
  note "--dry-run: generated but not published. The documents are:"
  for f in "$OUT_DIR"/style-*.json; do printf '\n--- %s ---\n' "$(basename "$f")"; cat "$f"; printf '\n'; done
  exit 0
fi

# --- publish ---------------------------------------------------------------------------
step "publishing to ${USER}@${HOST}:${REMOTE_DIR}"
connect
remote true >/dev/null 2>&1 || die "could not reach ${USER}@${HOST} (${auth})"
ok "reached ${HOST} via ${auth}"

remote "test -d ${REMOTE_DIR}" \
  || die "${REMOTE_DIR} does not exist on ${HOST} — the tile host has not been built yet (see the header)."

for theme in light dark; do
  copy_in "$OUT_DIR/style-${theme}.json" "${REMOTE_DIR}/style-${theme}.json" \
    || die "could not copy style-${theme}.json"
done

# `style.json` is the name both portal variables point at; light is the default because a
# fleet operator's console and the public tracking page are both light-first.
remote "ln -sfn ${REMOTE_DIR}/style-light.json ${REMOTE_DIR}/style.json \
        && chown -h www-data:www-data ${REMOTE_DIR}/style*.json" \
  || note "published, but could not set the symlink or ownership — check nginx can read them"
ok "published style-light.json, style-dark.json and style.json"

step "verifying"
verify || die "published, but the style does not resolve end to end — check nginx and CORS"

printf '\n\033[32m▸ done\033[0m\n'
cat <<EOF
  ${BASE_URL}/style.json        (light, the default both portals point at)
  ${BASE_URL}/style-dark.json

  The portals read it from:
    WEB_PASSENGER_MAP_STYLE_URL   infra/k8s/base/config/portal-config.yaml
    FLEET_PORTAL_MAP_STYLE_URL    same, and infra/replica/docker-compose.light-replica.yml
EOF
