# chaos — the C130 drill suite

Break the lightweight production replica on purpose, one documented failure at a time, and write
down what actually happened. Every drill states its blast radius and its rollback before it injects
anything; the rollback is armed first and runs on any exit path.

What the drills found is in **[report.md](report.md)**. This file is how to run them.

```bash
bash infra/replica/deploy.sh                                       # the replica has to be up
bash chaos/configure.sh                                            # accounts + bearers (30 min)
bash chaos/run-drills.sh --env replica --report chaos/out/report.md
```

## The drills

| | Drill | What it breaks | Spec |
|---|---|---|---|
| 10 | `redis-flush` | `FLUSHALL` while an offer is live | R-04, ADD §14.1, §15 |
| 11 | `redis-loss` | Redis stopped — unreachable, not empty | ADD §14.1 Redis row, ADD §12 `limited_live` |
| 20 | `postgres-loss` | the system of record stopped | ADD §14.1 Postgres row |
| 30 | `redpanda-loss` | the event backbone stopped | ADD §14.1 stream-lag row, D6' §2.4 |
| 40 | `emqx-loss` | the MQTT broker stopped | ADD §14.1 EMQX row, R-09 |
| 50 | `outbox-stall` | ride-svc's drain loop wedged, nothing else touched | E-09, §13.4 bullet 6 |
| 60 | `reconnect-storm` | N sessions arriving at once | R-09 / ADD §7.5.3 |
| 61 | `replay-flood` | every vehicle emptying its buffer at once | R-09 / R-17, ADD §7.5.1 |
| 62 | `mass-lwt` | a fleet losing coverage — sockets dropped without DISCONNECT | R-15, R-16, T-04 |
| 63 | `network-partition` | app-services severed from the network, process alive | no ADD row |
| 70 | `wallet-degraded` | dispatch with the balance unknowable — and **one ride ridden to completion**, which is what makes the second booking a *second* trip | D-08, US-9.1 |
| 90 | `dr-restore` | the whole database, dropped and rebuilt from a dump | ADD §15 |

```bash
bash chaos/run-drills.sh --env replica --list             # the registry
bash chaos/run-drills.sh --env replica --only 10,50 …     # a subset
bash chaos/run-drills.sh --env replica --skip 20 …        # all but one
bash chaos/run-drills.sh --env replica --no-dr …          # everything except drill 90
```

`--env replica` is required and is the only accepted value. This suite runs `FLUSHALL`,
`docker stop postgres`, `docker network disconnect` and `DROP DATABASE`; the only environment where
that is free is the one whose data is synthetic by construction. Production is DOKS in Singapore and
is reached by no code path in this directory.

## What the exit code means

| | |
|---|---|
| **0** | every drill ran, every fault was rolled back, the stack is healthy. **Findings may exist** — they are the deliverable, and the summary and the report name them. |
| **1** | a drill could not run, an assertion about the drill's own mechanics failed, or the stack did not come back inside a drill's recovery budget. |
| **2** | bad usage, or this is not the replica. |

It is deliberately not a verdict on the platform. A drill that proves a documented degradation never
happens has succeeded at its job — `chaos/report.md` is where that is read, and `▲ HIGH` findings are
printed again at the end of every run so a green exit code cannot hide one.

## Two files called report.md

- **`chaos/report.md`** is committed: the transcribed findings, what they mean, and who owns each.
- **`chaos/out/report.md`** is one run's machine-written record and is gitignored, along with the k6
  summaries beside it. It is what the manifest's verify command writes.

## Timing

A full run is about **20 minutes**, most of it in drill 90 (a `pg_dump`, a `DROP DATABASE`, a
`pg_restore` and every service restarting) and drill 40 (EMQX takes over a minute to report healthy
after a restart, and mqtt-bridge-svc longer still to re-subscribe). `--no-dr` brings it to about
eight. The bearers live 30 minutes, so a full run needs `configure.sh` to have been run recently —
`run-drills.sh` refuses to start on an expired one rather than reporting a platform that refuses
everything under fault.

## What a drill needs to exist

`chaos/configure.sh` provisions three (passenger, driver) pairs in the **`+9477 004 xxxx`** block
with plates **`WP-CH-xxxx`**, one APPROVED Mode C three-wheeler and a funded wallet each, an
emergency contact so `POST /v1/sos` gets past AL-13's guard, and their bearers — obtained through
`POST /v1/auth/otp/request` + `verify` exactly as the apps do, reading the code out of the dev SMS
sender's log line. It writes `chaos/env.json` at 0600 (gitignored: it carries EMQX's shared MQTT
secret, live bearers and the opaque refresh tokens drill 10 probes with).

It refuses to run anywhere but the replica, with `infra/replica/seed.sh`'s own three checks: the
compose project is `mageride-replica`, `replica.synthetic_marker` exists, and every row it writes is
in that phone/plate block.

Everything it leaves behind is greppable:

```sql
SELECT count(*) FROM iam.users WHERE phone LIKE '+9477004%';
SELECT count(*) FROM registry.vehicles WHERE registration_number LIKE 'WP-CH-%';
SELECT count(*) FROM telemetry.positions WHERE vehicle_id::text LIKE 'c0a0c0a0-%';
```

## Cleaning up after a run that was interrupted

The rollback trap covers Ctrl-C and any abort, but a killed terminal or a reboot can leave a fault
in place. In order of likelihood:

```bash
# A fixture passenger or driver wedged on a ride the platform cannot settle (report.md §3.1).
# Every later booking by that passenger is 409, and the driver's next accept is a 500.
docker compose -f infra/replica/docker-compose.light-replica.yml exec -T postgres \
  psql -U mageride -d mageride -c \
  "UPDATE rides.rides r SET state = 'Completed' FROM iam.users u
    WHERE u.id = r.passenger_id AND u.phone LIKE '+9477004%' AND r.state = 'PaymentPending';"

docker compose -f infra/replica/docker-compose.light-replica.yml ps            # anything stopped?
docker compose -f infra/replica/docker-compose.light-replica.yml start redis postgres redpanda emqx
docker network connect mageride-replica_internal $(docker compose -f infra/replica/docker-compose.light-replica.yml ps -q app-services)
# a held outbox drain lock — it also expires on its own with the psql session that holds it
docker compose -f infra/replica/docker-compose.light-replica.yml exec -T postgres \
  psql -U mageride -d mageride -c \
  "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE query LIKE '%pg_sleep%';"
bash infra/replica/smoke.sh
```

## What is not drilled here, and what that costs

- **Patroni promotion, a second EMQX node, a Redpanda quorum loss.** ADD §14's MVP column says
  "Single + daily backup", "Single node (accepted risk)" and "Single Redpanda broker (RF=1)"; the
  replica's compose file calls itself "a single-point-of-failure stack by design". Drills 20, 30 and
  40 measure the half of each §14.1 row a single-node stack can answer, and say which half they
  could not.
- **The per-ASN reconnect guardrail (R-09).** Every connection on this box has one source address —
  the same deployment property that makes the gateway's per-caller rate limit a per-platform one
  (load/report.md). An ASN limit cannot be observed where there is one ASN.
- **The documented rate limits themselves.** The storm generator reaches ~38 connections/s against
  `max_conn_rate = "500/s"`; each session is a TLS handshake plus a WebSocket upgrade plus a
  CONNECT, driven from the same eight vCPU as the broker. Where a drill could not reach a limit it
  says so rather than reporting the limit as untested-and-fine.
- **The tracker plane's protocols.** GT06/JT808/H02 framing is `tests/E2E`'s `TrackerPlaneScenario`;
  drill 20 probes only that the listener still accepts a connection.
- **Anything above the database in the §15 table** — Redis RDB, Redpanda tiered storage, Vault
  snapshots, etcd, Terraform. None of the four exists on this deployment; drill 90 covers the
  PostgreSQL row, which is the one with an RPO and an RTO on it.
