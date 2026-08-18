#!/usr/bin/env bash
# =====================================================================================
# infra/replica/tiles/deploy-tiles.sh — build the tile host on a fresh Ubuntu VPS.
#
#   TILES_SSH_PASSWORD=... bash infra/replica/tiles/deploy-tiles.sh
#   ...                    bash infra/replica/tiles/deploy-tiles.sh --dry-run
#   ...                    bash infra/replica/tiles/deploy-tiles.sh --status
#   ...                    bash infra/replica/tiles/deploy-tiles.sh --refresh-archive
#
# The companion to `deploy-nominatim.sh`, and for the same reason: `tiles.mageride.lk` was
# built by hand and everything on it lived only on that box. If the VPS went, the geocoder
# came back with one command and the basemap came back from memory — including three fixes
# that are invisible until they bite (see WHAT IS NOT OBVIOUS below).
#
# Both services share ONE box today (45.77.37.208): Nominatim in Docker on 8080, the tiles as
# static files behind nginx on 443. They are independent — run either script alone.
#
# ------------------------------------------------------------------------------------
# WHAT A TILE HOST ACTUALLY IS HERE
# ------------------------------------------------------------------------------------
# There is NO TILE SERVER. `lk.pmtiles` is a single 167 MB archive and every client — the four
# native apps through MapLibre's PMTiles file source, the two portals and the viewer through
# `addProtocol('pmtiles', …)` — reads tiles out of it with HTTP RANGE REQUESTS. nginx serves a
# static file; that is the whole serving stack. What makes it work is `Accept-Ranges`, which
# nginx's static handler sets by itself, and CORS, which it does not.
#
# ------------------------------------------------------------------------------------
# WHAT IS NOT OBVIOUS, AND COST REAL TIME TO FIND
# ------------------------------------------------------------------------------------
#   1. ufw. A fresh box with ufw enabled and only 22 open answers nothing on 443 and the
#      failure looks like DNS. Step 7 opens 80 and 443 and leaves 8080's restriction alone.
#   2. `.mjs` has no entry in nginx's mime.types, so ES modules go out as
#      `application/octet-stream` and the browser REFUSES to execute them. The viewer is blank
#      and NOTHING in the network tab is red. Handled in the committed site config.
#   3. Nominatim 4.4 answers **406** to a bare `Accept: application/json`. The `/search` proxy
#      normalises the header to `*/*`. Also in the committed config.
# Every one of those is in `nginx/tiles.mageride.lk.conf`, which is why that file is committed
# rather than described.
#
# ------------------------------------------------------------------------------------
# THE FONT IS NOT INTER, WHATEVER THE STYLE SAYS
# ------------------------------------------------------------------------------------
# `fonts/Inter Regular` on the live box is a SYMLINK to `fonts/Noto Sans Regular`, so every
# label ever drawn on this map has been Noto Sans. Protomaps' asset repo ships no Inter at all
# (`Noto Sans Regular|Medium|Italic` and `Noto Sans Devanagari Regular v1` are the four stacks
# it has), so there is nothing to fetch under that name and nothing to commit either.
#
# Step 4 therefore installs Noto Sans Regular and RECREATES THE SYMLINK. The symlink is not
# tidiness — APKs and IPAs already in the field bundle a style whose `text-font` says
# `Inter Regular`, and those builds lose every label the moment the name stops resolving. New
# styles name `Noto Sans Regular` directly; the symlink is what keeps old installs working.
#
# ------------------------------------------------------------------------------------
# WHAT THIS SCRIPT DOES NOT DO
# ------------------------------------------------------------------------------------
# It does not publish the style — `infra/replica/publish-tile-style.sh` does, and step 9 calls
# it. It does not create DNS: `tiles.mageride.lk` must already resolve to the target before
# step 8 can pass an ACME http-01 challenge, and the script checks that rather than discovering
# it inside certbot.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")/../../.." || exit 2
REPO_ROOT="$PWD"
TILES_DIR="infra/replica/tiles"

HOST="${TILES_HOST:-45.77.37.208}"
PORT="${TILES_SSH_PORT:-22}"
USER="${TILES_SSH_USER:-root}"
REMOTE_DIR="${TILES_REMOTE_DIR:-/var/www/tiles}"
DOMAIN="${TILES_DOMAIN:-tiles.mageride.lk}"
ARCHIVE="${TILES_ARCHIVE:-lk.pmtiles}"
CERT_EMAIL="${TILES_CERT_EMAIL:-mageride75@gmail.com}"

# Sri Lanka, padded. Same box as the archive's own extent — `pmtiles extract` takes
# minlon,minlat,maxlon,maxlat and this is the value the live archive was cut with.
BBOX="${TILES_BBOX:-79.4,5.7,82.0,10.0}"
# The archive is z0-15 because Protomaps' planet build is. Raising this does not add detail.
MAXZOOM="${TILES_MAXZOOM:-15}"

# Protomaps publishes a daily planet build and keeps a limited window of them, so this is a
# DEFAULT and not a pin — an old date 404s and the script says so rather than writing a
# zero-byte archive. The live archive was cut from the 2026-08-15 build (planetiler 0.10.2,
# Protomaps Basemap v4.15.2), which its own metadata records.
BUILD_DATE="${TILES_BUILD_DATE:-}"

# Pinned: the glyph PBFs are a build output, and "whatever main holds today" is not a thing to
# rebuild a map from.
ASSETS_SHA="${TILES_ASSETS_SHA:-028c18f713baecad011301ff7a69acc39bcc2ae7}"
FONTSTACK="Noto Sans Regular"
LEGACY_FONTSTACK="Inter Regular"

DRY_RUN=0; STATUS_ONLY=0; REFRESH_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --dry-run)         DRY_RUN=1 ;;
    --status)          STATUS_ONLY=1 ;;
    --refresh-archive) REFRESH_ONLY=1 ;;
    *) printf 'unknown argument: %s\n' "$arg" >&2; exit 2 ;;
  esac
done

step() { printf '\n\033[1m▸ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
die()  { printf '  \033[31m✗\033[0m %s\n' "$*" >&2; exit 1; }
note() { printf '  \033[33m!\033[0m %s\n' "$*"; }

# --- how we reach the box ------------------------------------------------------------
# Key first, password only if a key does not work — the same order deploy-nominatim.sh and
# publish-tile-style.sh use. TILES_SSH_PASSWORD is never written to disk or echoed.
SSH_BASE=(ssh -o StrictHostKeyChecking=no -o ConnectTimeout=10 -p "$PORT")
auth="ssh key"
connect() {
  if "${SSH_BASE[@]}" -o BatchMode=yes "${USER}@${HOST}" true >/dev/null 2>&1; then
    remote()  { "${SSH_BASE[@]}" "${USER}@${HOST}" "$@"; }
    copy_in() { scp -q -o StrictHostKeyChecking=no -P "$PORT" -r "$1" "${USER}@${HOST}:$2"; }
    return
  fi
  [ -n "${TILES_SSH_PASSWORD:-}" ] || die "no ssh key works and TILES_SSH_PASSWORD is unset."
  command -v sshpass >/dev/null \
    || die "sshpass is not installed and no ssh key works.
      apt-get install -y sshpass, or ssh-copy-id ${USER}@${HOST} once."
  export SSHPASS="$TILES_SSH_PASSWORD"
  auth="password"
  remote()  { sshpass -e "${SSH_BASE[@]}" "${USER}@${HOST}" "$@"; }
  copy_in() { sshpass -e scp -q -o StrictHostKeyChecking=no -P "$PORT" -r "$1" "${USER}@${HOST}:$2"; }
}
connect

if [ "$DRY_RUN" = 1 ]; then
  step "dry run — what would be done on ${USER}@${HOST}"
  cat <<EOF
  1  reach ${HOST} (${auth})
  2  apt-get: nginx, certbot, python3-certbot-nginx, ufw, curl, tar
  3  install the pmtiles CLI, then
     pmtiles extract https://build.protomaps.com/<DATE>.pmtiles ${REMOTE_DIR}/${ARCHIVE} \\
       --bbox=${BBOX} --maxzoom=${MAXZOOM}
  4  glyphs: "${FONTSTACK}" from basemaps-assets@${ASSETS_SHA:0:7}
     plus the "${LEGACY_FONTSTACK}" symlink that already-shipped apps depend on
  5  ${TILES_DIR}/vendor/* and viewer.html -> ${REMOTE_DIR}
  6  nginx: sites-available/${DOMAIN} + conf.d/mageride-geocode.conf
  7  ufw: allow 80,443 (8080's existing restriction is left alone)
  8  certbot --nginx -d ${DOMAIN}   (requires ${DOMAIN} to resolve to ${HOST} first)
  9  bash infra/replica/publish-tile-style.sh, then verify
EOF
  exit 0
fi

# --- 1/9 -----------------------------------------------------------------------------
step "1/9  the target"
remote true >/dev/null 2>&1 || die "could not reach ${USER}@${HOST} (${auth})"
ok "reached ${HOST} via ${auth}"
os=$(remote '. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME"' 2>/dev/null)
[ -n "$os" ] && ok "$os"
free=$(remote "df -BG --output=avail / | tail -1 | tr -d ' G'" 2>/dev/null)
if [ -n "$free" ] && [ "$free" -lt 3 ] 2>/dev/null; then
  die "only ${free} GB free on / — the archive alone is ~170 MB and the extract needs room."
fi
[ -n "$free" ] && ok "${free} GB free on /"

if [ "$STATUS_ONLY" = 1 ]; then
  step "status"
  remote "ls -lh ${REMOTE_DIR}/ 2>/dev/null | tail -n +2" || note "${REMOTE_DIR} does not exist"
  remote "test -L '${REMOTE_DIR}/fonts/${LEGACY_FONTSTACK}' \
    && echo '  fonts: ${LEGACY_FONTSTACK} -> $(basename "${FONTSTACK}") symlink present' \
    || echo '  fonts: NO ${LEGACY_FONTSTACK} symlink — shipped apps will draw no labels'"
  remote "nginx -t 2>&1 | tail -2; ufw status 2>/dev/null | grep -E '^(80|443)' || true"
  for u in "/style.json" "/fonts/${FONTSTACK// /%20}/0-255.pbf" "/"; do
    printf '  %-40s %s (expect 200)\n' "$u" "$(curl -s -o /dev/null -w '%{http_code}' "https://${DOMAIN}${u}")"
  done
  printf '  %-40s %s (expect 206)\n' "/${ARCHIVE}" \
    "$(curl -s -o /dev/null -w '%{http_code}' -r 0-99 "https://${DOMAIN}/${ARCHIVE}")"
  exit 0
fi

# --- 2/9 -----------------------------------------------------------------------------
if [ "$REFRESH_ONLY" = 0 ]; then
  step "2/9  packages"
  remote "export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq >/dev/null 2>&1
    apt-get install -y -qq nginx certbot python3-certbot-nginx ufw curl tar ca-certificates \
      >/dev/null 2>&1" || die "apt-get failed"
  ok "nginx, certbot, ufw, curl, tar"
  remote "mkdir -p ${REMOTE_DIR}/fonts ${REMOTE_DIR}/vendor"
fi

# --- 3/9 -----------------------------------------------------------------------------
step "3/9  the pmtiles CLI and the Sri Lanka archive"
if remote "command -v pmtiles" >/dev/null 2>&1; then
  ok "pmtiles CLI already installed"
else
  # go-pmtiles ships static linux/amd64 tarballs on its releases; resolve the newest rather
  # than pinning, because the CLI is a tool and the ARCHIVE is what has to be reproducible.
  remote "set -e
    url=\$(curl -sL https://api.github.com/repos/protomaps/go-pmtiles/releases/latest \
      | grep -oE 'https://[^\"]*Linux_x86_64\.tar\.gz' | head -1)
    [ -n \"\$url\" ] || { echo 'could not resolve a go-pmtiles release'; exit 1; }
    curl -sL \"\$url\" -o /tmp/pmtiles.tgz
    tar xzf /tmp/pmtiles.tgz -C /usr/local/bin pmtiles
    chmod +x /usr/local/bin/pmtiles" || die "could not install the pmtiles CLI"
  ok "pmtiles CLI installed"
fi

if remote "test -s ${REMOTE_DIR}/${ARCHIVE}" && [ "$REFRESH_ONLY" = 0 ]; then
  sz=$(remote "du -h ${REMOTE_DIR}/${ARCHIVE} | cut -f1")
  ok "${ARCHIVE} already present (${sz}) — pass --refresh-archive to re-cut it"
else
  # Pick a build that exists. Protomaps keeps a window, so walk back from today.
  if [ -n "$BUILD_DATE" ]; then
    dates="$BUILD_DATE"
  else
    dates=$(for d in 1 2 3 4 5 6 7 8 9 10; do date -u -d "-${d} day" +%Y%m%d; done)
  fi
  build=""
  for d in $dates; do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' -I "https://build.protomaps.com/${d}.pmtiles")" = "200" ]; then
      build="$d"; break
    fi
  done
  [ -n "$build" ] || die "no Protomaps daily build answered 200 (tried: $(echo $dates | tr '\n' ' '))"
  ok "using the ${build} planet build"
  note "extracting over range requests — this reads only the Sri Lanka bbox, not the planet"
  remote "pmtiles extract https://build.protomaps.com/${build}.pmtiles ${REMOTE_DIR}/${ARCHIVE} \
    --bbox=${BBOX} --maxzoom=${MAXZOOM}" || die "pmtiles extract failed"
  # iOS builds ask for this name; it has been a symlink beside the archive since day one.
  remote "ln -sfn ${REMOTE_DIR}/${ARCHIVE} ${REMOTE_DIR}/sri-lanka.pmtiles"
  sz=$(remote "du -h ${REMOTE_DIR}/${ARCHIVE} | cut -f1")
  ok "${ARCHIVE} (${sz}) + sri-lanka.pmtiles symlink"
fi
[ "$REFRESH_ONLY" = 1 ] && { step "done"; ok "archive refreshed; the style is unchanged"; exit 0; }

# --- 4/9 -----------------------------------------------------------------------------
step "4/9  glyphs"
if remote "test -s '${REMOTE_DIR}/fonts/${FONTSTACK}/0-255.pbf'"; then
  ok "${FONTSTACK} already present"
else
  remote "set -e
    cd /tmp && rm -rf assets && mkdir assets
    curl -sL https://codeload.github.com/protomaps/basemaps-assets/tar.gz/${ASSETS_SHA} \
      | tar xz -C assets --strip-components=1
    mkdir -p '${REMOTE_DIR}/fonts'
    cp -r \"assets/fonts/${FONTSTACK}\" '${REMOTE_DIR}/fonts/'
    rm -rf assets" || die "could not install the glyph PBFs"
  n=$(remote "ls '${REMOTE_DIR}/fonts/${FONTSTACK}' | wc -l")
  ok "${FONTSTACK} — ${n} glyph ranges from basemaps-assets@${ASSETS_SHA:0:7}"
fi
# See THE FONT IS NOT INTER above. This symlink is what keeps already-shipped apps labelled.
remote "ln -sfn '${REMOTE_DIR}/fonts/${FONTSTACK}' '${REMOTE_DIR}/fonts/${LEGACY_FONTSTACK}'"
ok "${LEGACY_FONTSTACK} -> ${FONTSTACK} (shipped apps bundle a style naming the former)"

# --- 5/9 -----------------------------------------------------------------------------
step "5/9  the viewer and its vendored JS"
copy_in "${REPO_ROOT}/${TILES_DIR}/viewer.html" "${REMOTE_DIR}/viewer.html" \
  || die "could not copy viewer.html"
copy_in "${REPO_ROOT}/${TILES_DIR}/vendor/." "${REMOTE_DIR}/vendor/" \
  || die "could not copy the vendor directory"
remote "ln -sfn ${REMOTE_DIR}/viewer.html ${REMOTE_DIR}/index.html
        chown -R www-data:www-data ${REMOTE_DIR}"
ok "viewer.html + 6 vendored files, index.html symlinked"

# --- 6/9 -----------------------------------------------------------------------------
step "6/9  nginx"
copy_in "${REPO_ROOT}/${TILES_DIR}/nginx/mageride-geocode.conf" \
        "/etc/nginx/conf.d/mageride-geocode.conf" || die "could not copy the rate-limit zone"
# The committed config is HTTP-ONLY and certbot adds the TLS half in step 8 — that is the
# order certbot's nginx plugin is built for. A committed `listen 443 ssl` would point at a
# certificate that does not exist yet on a fresh box, and `nginx -t` would reject the file
# before certbot ever ran. Re-running this on a box that already has TLS is safe: the copy
# reverts the site to HTTP and step 8's `--keep-until-expiring` puts TLS straight back
# without re-issuing.
remote "cat > /etc/nginx/sites-available/${DOMAIN}" < "${REPO_ROOT}/${TILES_DIR}/nginx/tiles.mageride.lk.conf"
remote "set -e
  ln -sfn /etc/nginx/sites-available/${DOMAIN} /etc/nginx/sites-enabled/${DOMAIN}
  rm -f /etc/nginx/sites-enabled/default
  nginx -t" || die "nginx rejected the configuration"
remote "systemctl reload nginx || systemctl restart nginx"
ok "site enabled, rate-limit zone installed, nginx -t clean"

# --- 7/9 -----------------------------------------------------------------------------
step "7/9  firewall"
# ADD only. `ufw allow` is idempotent, and nothing here touches 8080 — if Nominatim shares
# this box its "allow from the replica, deny everyone" pair must survive untouched.
remote "ufw allow 22/tcp >/dev/null 2>&1
        ufw allow 80/tcp  comment '${DOMAIN} HTTP + ACME http-01' >/dev/null 2>&1
        ufw allow 443/tcp comment '${DOMAIN} HTTPS' >/dev/null 2>&1
        yes | ufw enable >/dev/null 2>&1 || true"
ok "$(remote "ufw status | grep -cE '^(80|443)/tcp' " 2>/dev/null) rules for 80/443; 8080 untouched"

# --- 8/9 -----------------------------------------------------------------------------
step "8/9  TLS"
resolved=$(getent hosts "$DOMAIN" 2>/dev/null | awk '{print $1}' | head -1)
if [ "$resolved" != "$HOST" ]; then
  note "${DOMAIN} resolves to '${resolved:-nothing}', not ${HOST} — SKIPPING certbot."
  note "the site is serving HTTP only. Point the A record at ${HOST}, wait for"
  note "propagation, then re-run this script to pick the certificate up."
else
  # `--keep-until-expiring` makes this idempotent: an existing valid certificate is reused
  # and only the nginx config is rewritten, so re-running the script cannot burn a rate limit.
  # `--redirect` is what restores the :80 -> :443 server block step 6 just replaced.
  remote "certbot --nginx -d ${DOMAIN} --non-interactive --agree-tos -m ${CERT_EMAIL} \
    --redirect --keep-until-expiring >/dev/null 2>&1" \
    || die "certbot failed — check DNS, and that 80 is reachable from the internet"
  remote "nginx -t >/dev/null 2>&1" || die "certbot left nginx in a state that does not parse"
  exp=$(remote "openssl x509 -enddate -noout -in /etc/letsencrypt/live/${DOMAIN}/fullchain.pem 2>/dev/null | cut -d= -f2")
  ok "TLS active${exp:+ — expires ${exp}}"
fi

# --- 9/9 -----------------------------------------------------------------------------
step "9/9  the style"
TILES_HOST="$HOST" TILES_SSH_PORT="$PORT" TILES_SSH_USER="$USER" \
  bash "${REPO_ROOT}/infra/replica/publish-tile-style.sh" >/dev/null 2>&1 \
  && ok "style published by publish-tile-style.sh" \
  || note "publish-tile-style.sh did not complete — run it directly to see why"

step "verifying"
fail=0
# The archive is probed WITH a Range header and everything else without one. Sending `-r` to
# all four would answer 206 everywhere and prove nothing — 206 only means something on the
# archive, where a 200 would mean ranges are not being served and every client would pull
# 167 MB to draw one tile.
for path in "/style.json" "/fonts/${FONTSTACK// /%20}/0-255.pbf" "/"; do
  code=$(curl -s -o /dev/null -w '%{http_code}' "https://${DOMAIN}${path}")
  if [ "$code" = 200 ]; then ok "${path} → 200"; else note "${path} → ${code}"; fail=1; fi
done
code=$(curl -s -o /dev/null -w '%{http_code}' -r 0-99 "https://${DOMAIN}/${ARCHIVE}")
if [ "$code" = 206 ]; then
  ok "${ARCHIVE} → 206 (range requests served)"
else
  note "${ARCHIVE} → ${code}, expected 206 — range requests are NOT working"; fail=1
fi

step "done"
[ "$fail" = 0 ] && ok "https://${DOMAIN} is serving" || note "some checks did not pass — see above"
cat <<EOF

  The archive is z0-${MAXZOOM}; MapLibre overzooms past it and gains no new geometry.
  Refresh the basemap later with:  bash ${TILES_DIR}/deploy-tiles.sh --refresh-archive
EOF
