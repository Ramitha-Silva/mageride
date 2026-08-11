# Runbook — operating the lightweight production replica

**Component:** C125 · **Stack:** `infra/replica/` · **Spec:** `specs/lightweight-production-replica.md`

> [!WARNING]
> **SYNTHETIC DATA ONLY. NEVER PRODUCTION DATA.**
> This stack exists for testing, CI and demos (root `CLAUDE.md`, D7' §8). One Postgres with no
> replica, one Redpanda at RF=1, one HAProxy with no VRRP peer, and a self-signed certificate on a
> publicly reachable edge. Restoring a production dump into it would put real riders' phone numbers
> and real drivers' documents on that box. `seed.sh` and `restore.sh` both check for the
> `replica.synthetic_marker` row and get loud when it is missing.

> [!IMPORTANT]
> **This box also hosts the build, and the two do not fit together.** ~16.6 GiB of replica plus a
> `dotnet build` does not fit in 24 GB. `guardrail.sh` refuses to start while a heavy build is
> running and `deploy.sh` calls it first — it is not advisory and there is no flag to skip it.

---

## 1. Bring it up

```bash
bash infra/replica/deploy.sh --dry-run   # every check, nothing built, nothing started
bash infra/replica/deploy.sh             # the real thing
bash infra/replica/smoke.sh              # the golden paths through the edge
```

`deploy.sh` runs five stages in an order that matters:

| Stage | What it does | Why it is before the next one |
|---|---|---|
| 1 guardrail | heavy build? budget? disk? | The OOM killer picks its victim by who asked last, not by who matters |
| 2 secrets | generates `.env.replica` if absent | Four compose values are `${VAR:?}` so the stack refuses to render with a default nobody changed |
| 3 certificates | device CA, then the edge pem | **EMQX reads `certs/ca_chain.crt` as its 8883 cacertfile at listener start** — it must exist before the container does |
| 4 validate | compose renders, both HAProxy configs pass `haproxy -c`, every mounted file exists | Cheaper to fail here than half-way through an `up` |
| 5 up | build, start, `--wait`, seed, live budget | — |

**First run takes ~15–25 minutes**, nearly all of it building `app-services` (23 pipelines) and
`hot-path`. Subsequent runs reuse the layer cache.

### Optional containers

```bash
docker compose -f infra/replica/docker-compose.light-replica.yml --profile portals up -d
docker compose -f infra/replica/docker-compose.light-replica.yml --profile voip    up -d
bash infra/replica/deploy.sh --with-monitoring     # C119's observability stack beside it
```

Monitoring is **not** duplicated in the replica compose: C119 owns that whole stack in
`infra/observability/`, and a second copy would be a second opinion about the same scrape config.
Its ~1.6 GB counts against the guardrail's optional total.

### What is reachable, and what deliberately is not

Only HAProxy publishes. Everything else is on the internal bridge network with no host port.

| Port | Purpose |
|---|---|
| 443 | HTTPS + WSS — REST API, SignalR (`/hubs/*`), `admin.` / `fleet.` / `s3.` vhosts |
| 8883 | MQTTS, L4 passthrough to EMQX |
| 8084 | MQTT over WSS for mobile clients that cannot open raw TCP |
| 5023 / 5024 / 5025 | GT06 / JT808 / H02 tracker sockets, passthrough to `tcp-adapter` |
| 5026/udp | NMEA — published by `tcp-adapter` directly, because **HAProxy has no UDP forwarder** |

Postgres, Redis, Redpanda, EMQX's dashboard and MinIO's console have **no** published port. Reaching
them means `docker compose exec`. Set `REPLICA_EDGE_BIND=127.0.0.1` in `.env.replica` to close even
the edge for a run that only has to satisfy the smoke suite.

The edge returns **404** for `/health/*`, `/metrics` and `/v1/internal/**`. That is not decoration:
`/health/ready` names every dependency a service probes, `/metrics` is the internal topology, and
this edge is on the public internet. 404 rather than 403 because a 403 confirms the path exists.

---

## 2. Bring it down

```bash
bash infra/replica/down.sh              # containers go, volumes and data survive
bash infra/replica/down.sh --volumes    # data too (asks you to type `discard`)
```

> [!CAUTION]
> **Never pass `--remove-orphans`.** It operates on the compose *project*. This project is
> `mageride-replica` and the three dev stacks share `mageride`, so the flag is one typo away from
> deleting a developer's Postgres. `infra/CLAUDE.md` records that incident shape for the dev stacks.

Before running a build, take the replica down. That is the whole reason `guardrail.sh` exists — and
it will refuse the next `deploy.sh` while your build is still running, which is the intended
direction of the interlock.

---

## 3. Back up

```bash
bash infra/replica/backup.sh                    # dump → object store → prune to the last 7
bash infra/replica/backup.sh --verify-restore   # ...and prove the dump restores
```

**A dump nobody has restored is not a backup.** `--verify-restore` restores into a database created
for the purpose, compares `iam.users` row counts against the source, and drops it. Without that flag
the script proves only that `pg_dump` exited 0 — which it also does for a dump of an empty database.

Nightly, from the host crontab:

```cron
15 2 * * * cd /root/mageride && bash infra/replica/backup.sh --verify-restore >> /var/log/mageride-backup.log 2>&1
```

The dumps land in the replica's **own** MinIO (`mageride-backups`). That is deliberate for a replica —
the data is synthetic and the point is to exercise the mechanism — and it means **a backup does not
survive the loss of this box**. A production backup goes off-host; this one does not.

Format is `pg_dump -Fc`, so `pg_restore` can be pointed at a single table for a partial recovery.
Plain SQL cannot do that.

---

## 4. Restore

```bash
bash infra/replica/restore.sh --list             # what is available
bash infra/replica/restore.sh                    # the newest dump
bash infra/replica/restore.sh mageride-2026...dump
```

The restore **drops the database**, so it asks you to type `restore` first, and it refuses `--yes`
outright when the synthetic marker is missing — because a database with no marker may not be a
replica. It stops the five services holding connections, terminates the rest, recreates the database
with `timescaledb`/`postgis`/`pgcrypto`, restores, and restarts the services.

`pg_restore` reports errors about extension ownership on a Timescale dump even when the data is
intact. **The row comparison decides, not the exit code** — that is why both scripts do one.

To test a dump *without* touching the live database, use `backup.sh --verify-restore` instead. It
restores into a throwaway database and never goes near the running one.

---

## 5. When something is wrong

**Start here.** The container that fails first is usually the one that explains everything after it.

```bash
docker compose -f infra/replica/docker-compose.light-replica.yml ps
docker compose -f infra/replica/docker-compose.light-replica.yml logs --tail 60 app-services
bash infra/replica/guardrail.sh --running       # live memory against the spec's budget
bash infra/replica/seed.sh --verify             # reference data present? marker present?
```

| Symptom | Cause | What to do |
|---|---|---|
| `app-services` restarts, log names a config key | An option validator wants a key the env templates do not supply. Container 7 drops the referenced projects' `appsettings.json` **by design** — 23 pipelines share one content root, so all configuration comes from the environment | Add the key to `infra/env/.env.app.example` and record the gap. The service names the key it wanted |
| Every route returns **502** | HAProxy reached no backend | Was the C125 gateway-precedence bug. Check `ReverseProxy__Clusters__*__Destinations__primary__Address` resolves — `Container7.cs` sets the 22 co-located ones in-process, the compose file sets the other three |
| `haproxy` exits: `cannot open the file` | The pem is not readable by uid 99, the `haproxy` user in the image | `chown 99:99 infra/deploy/certs/replica.pem`. `deploy.sh` does this; a hand-generated cert will not |
| `haproxy` exits: `No Private Key found in *.crt.key` | `crt <directory>` loads every file and looks for `<file>.key` beside each | Name the combined pem explicitly, as both configs now do |
| `provisioning-svc` fails: `Access to the path '/var/step/secrets' is denied` | `infra/deploy/device-ca` is bind-mounted at `/var/step`, and a bind mount carries the HOST directory's ownership — overriding whatever the image chowned | `chown -R 1654:1654 infra/deploy/device-ca`. `deploy.sh` does this; 1654 is `app` in both images and the uid the K8s manifests use |
| `emqx` healthy but no tracker connects | The device CA chain was absent when the listener started | `bash infra/scripts/ensure-device-ca.sh && docker compose ... restart emqx`. EMQX reads it **once**, at listener start |
| `hot-path` up but no telemetry persists | Was the alphabetical entry-point bug — the container ran one of its four services | `docker compose ... exec hot-path cat /app/.entrypoint` must say `MageRide.HotPath.dll` |
| OOM kills something | A build started while the replica was up | Take one of them down. This is what the guardrail exists to prevent, and it only guards the deploy |
| `migrate` exits 1 with `28P01: password authentication failed` | `.env.replica` was regenerated while the postgres volume still existed. `POSTGRES_PASSWORD` applies **only** at data-directory initialisation, so a new password never reaches an old volume | `bash infra/replica/down.sh --volumes`, then deploy. `deploy.sh` now probes for this and says so before it wastes the 120-second migrate wait |
| `seed.sh` refuses: "already holds N users" | No synthetic marker and pre-existing data | Correct behaviour. If it really is a throwaway, `down.sh --volumes` then deploy again |

### Where the numbers come from

`guardrail.sh` does not hardcode a budget: `budget.py` parses the resource table in
`specs/lightweight-production-replica.md` and compares every container's compose limit against its
row. Change a limit and the guardrail fails until the spec agrees, or the spec changes and the
guardrail follows. The core 11 come to **16.625 GiB**, which is the spec's own ~16.7 GB.

> [!NOTE]
> The C125 prompt's definition of done says "~18.9 GB". The spec's totals are 16.7 GB (core 11) and
> 19.7 GB (core + VoIP + both portals + monitoring). 18.9 matches neither, which is why the guardrail
> derives the figure instead of picking one.

---

## 6. Geocoding — Nominatim on its own VPS

**In scope and deployed.** `45.77.37.208` (Ubuntu 26.04, 7.2 GiB RAM, 4 cores, 8 GiB swap) runs
`mediagis/nominatim:4.4` against the Sri Lanka OSM extract, and **query-svc** reaches it through
`Query__NominatimBaseUrl` — the key overrides `env/.env.app.example`'s `http://nominatim:8080/`, which
names a container that does not exist on this stack.

```bash
NOMINATIM_SSH_PASSWORD=... bash infra/replica/nominatim/deploy-nominatim.sh --dry-run
NOMINATIM_SSH_PASSWORD=... bash infra/replica/nominatim/deploy-nominatim.sh
NOMINATIM_SSH_PASSWORD=... bash infra/replica/nominatim/deploy-nominatim.sh --status
NOMINATIM_SSH_PASSWORD=... bash infra/replica/nominatim/deploy-nominatim.sh --reimport   # discards the DB
```

Run it **from the replica box**. It installs Docker if absent, copies
`docker-compose.nominatim.yml` to `/opt/mageride-nominatim`, generates the geocoder's internal
Postgres password *on the target* (mode 600, never copied off it), starts the import, restricts 8080
to the replica's address, and writes `Query__NominatimBaseUrl` into `.env.replica`.

Prefer a key — `ssh-copy-id root@45.77.37.208` once — and the script uses it automatically and stops
needing `NOMINATIM_SSH_PASSWORD`.

### Why a separate box, and why it is not in the replica's budget

The spec is explicit: Nominatim wants 8 GB for the Sri Lanka extract, "which is a third of the 24 GB
budget… **Recommended for light replica: host Nominatim on a separate cheap VPS**". Co-locating it
would leave the eleven core containers about 8 GB and `guardrail.sh` would refuse the deploy.
`budget.py` lists `nominatim` under `ELSEWHERE`, so its 8 GB row is **not** counted against the
replica — that is deliberate, and the first version of that parser got it wrong and refused a deploy
that fits.

### The first boot is the whole job

`mediagis/nominatim` imports on first boot when the data volume is empty, then serves. The extract is
~137 MB and the import takes **tens of minutes**; the container stays `health: starting` throughout,
which is why `start_period` is 90 minutes. A shorter one makes compose kill and restart a healthy
import, which then never finishes.

The imported database is the expensive artefact. **`--reimport` is the only thing that should ever
discard that volume**, and it costs the whole import again.

### When it is wrong

| Symptom | Cause | What to do |
|---|---|---|
| `docker compose … ps` prints an interpolation error instead of a status | `NOMINATIM_PASSWORD` is `${VAR:?}`, so every compose subcommand needs `--env-file .env.nominatim` | use the script's `--status`, which passes it |
| container restarts repeatedly, import never completes | OOM during the import — its peak is far above steady state | the box needs swap (it has 8 GiB); check `NOMINATIM_SHARED_BUFFERS` and `MAINTENANCE_WORK_MEM` are not larger than the box |
| `/status` refuses the connection | the import has not finished. Refused-then-500-then-OK is the normal sequence | wait; follow the log with `--status` or the log command the script prints |
| `/v1/geo/reverse` still answers 503 | **query-svc** owns the geocoder and reads `Query__NominatimBaseUrl` at start-up. (`/v1/geo/parse-maps-link` is transit-svc's and touches no geocoder — same URL prefix, different service, and the trap that made the first version of `deploy-nominatim.sh` write a key nothing reads) | `docker compose -f infra/replica/docker-compose.light-replica.yml up -d --force-recreate app-services` |
| the geocoder answers strangers | the ufw rule did not apply | `ssh root@45.77.37.208 'ufw status'`; only the replica's address should reach 8080 |

`osm-pipeline` is **not** deployed. The spec makes it a weekly one-shot (diff → osm2pgsql →
tippecanoe → PMTiles → R2 sync) that "is NOT part of the always-on container set", so it belongs in a
cron entry. `--reimport` covers the geocoder half; the tile half needs an R2 bucket nobody has
provisioned.
