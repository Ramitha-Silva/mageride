# Runbook — the failures C130 drilled, and what to do when one is real

**Not alert-driven.** Each section below is a failure the chaos suite injects on purpose
(`chaos/run-drills.sh`), with the measured behaviour of *this* platform beside ADD §14.1's
documented behaviour. When one of them happens for real, this is the page: **detection signal**
(what you will actually see first) and **first action** (the one thing to do before diagnosing).

Where an alert already covers a failure its runbook is linked; those runbooks own the diagnosis and
this one owns the drill's measurement. Where no alert covers it — and three of these have none —
that is said outright, because "how would we know" is the first question in the incident review.

Numbers are from one run of `chaos/run-drills.sh --env replica` against the lightweight production
replica (single VPS, 8 vCPU / 24 GB, all eleven containers on one bridge network). Production is
DOKS in Singapore and will differ; the *shapes* will not.

> **Every drill in this file has been run.** What it found — including six HIGH findings that
> change how this platform should be operated — is in `chaos/report.md`. Read that before treating
> any "documented behaviour" column here as a description of what happens.

---

## 10 · Redis keyspace lost (`FLUSHALL`, or a restart with no AOF)

**Detection signal.** Nothing pages. `mageride_query_nearby_limited_live_total` does **not** move —
Redis is up, only its contents are gone — and `GET /v1/nearby` answers
`200 {limitedLive:false, vehicles:[]}`. The first visible symptom is a live map that is empty in a
city that is not, and drivers reporting they get no offers.

**First action.** **Do not restart anything.** Confirm the keyspace is genuinely empty before
concluding it, because the same symptom is produced by a broken ingest chain:

```bash
docker compose -f infra/replica/docker-compose.light-replica.yml exec -T redis redis-cli DBSIZE
docker compose -f infra/replica/docker-compose.light-replica.yml exec -T redis redis-cli ZCARD geo:live
```

Then bring drivers back into the pool. The R-08 candidate index is rebuilt only by the **next**
go-online or the next position sample, so every driver already online is invisible to dispatch until
they move — measured at **1.4 s** for the first driver to reappear after going online again, and
indefinite for one who does not.

**What survives.** Measured: in-flight offer expiries all did. R-04's `rides.timers` backstop fired
**760 ms** after the deadline with the keyspace destroyed (**305 ms** with Redis intact), the ride
returned to `Matching` and the offer row was settled `EXPIRED`. Fare quotes and session refreshes
were unaffected — iam-svc falls back to `iam.sessions` when `refresh:{jti}` is gone.

**Related:** [redis-evictions.md](redis-evictions.md) · [ride-timer-backlog.md](ride-timer-backlog.md)

---

## 11 · Redis unreachable (the server is gone)

**Detection signal.** `RedisDown` fires. `mageride_query_nearby_limited_live_total` climbs and
`GET /v1/nearby` answers `200 {limitedLive:true}` — this is ADD §14.1's documented degradation and
it held.

**First action.** Start Redis. Everything else waits on it:

```bash
docker compose -f infra/replica/docker-compose.light-replica.yml start redis
```

Measured recovery: container healthy in **6.6 s**, and `limitedLive` cleared itself **196 ms** later
without any service restart.

**What is refused, and is not in §14.1.** `POST /v1/rides/request` is refused outright, and
**`POST /v1/sos` answers 503** — the one request with a person on the other end of it. §14.1's Redis
row promises a stale live map and says nothing about either. If an SOS is raised during a Redis
outage it does not reach the platform at all; there is no queue and no retry.

**Related:** [redis-evictions.md](redis-evictions.md) · [sos-dispatch-latency.md](sos-dispatch-latency.md)

---

## 20 · Postgres primary down

**Detection signal.** `PostgresDown`. Registration and every history read answer 500 within
**~200 ms** — fast, which matters: a slow refusal is how a database outage becomes an
every-request outage.

**First action.** Start Postgres and let the pools recover on their own. Do **not** restart the
application containers — measured, the platform served a full steady state **815 ms** after Postgres
reported healthy, and restarting app-services costs a 180-second `start_period` on top.

```bash
docker compose -f infra/replica/docker-compose.light-replica.yml start postgres
```

**What keeps working.** ADD §14.1's "Tracking continues" held: `GET /v1/nearby` answered 200 from
Redis throughout, and the GT06 listener kept accepting connections. The tracking plane genuinely
does not need the database.

**The 30 seconds in §14.1 is Patroni's, and there is no Patroni here.** §14's MVP column says
"Single + daily backup". Measured end-to-end recovery on this stack: **~10–12 s** for a clean
`docker compose start`, and unbounded for anything that needs the volume rebuilt — see drill 90.

**Related:** [postgres-saturation.md](postgres-saturation.md) · [service-down.md](service-down.md)

---

## 30 · Event backbone down (Redpanda)

**Detection signal.** **Not for 15 seconds, and then only if you are watching the right counter.**
`mageride_outbox_publish_failures_total` cannot move until librdkafka gives up on the message —
`Kafka:MessageTimeoutMs` is 15 s and `KafkaEventPublisher` awaits each delivery in turn — so
`OutboxPublishFailing` has a 15-second floor. `OutboxDispatchLagHigh` is a p95 over a histogram that
only takes an observation when a row *is* dispatched, so a stopped backbone makes it go quiet rather
than tall. The reliable signal is the undispatched row count, and nothing exports it:

```sql
SELECT count(*) FROM rides.outbox WHERE dispatched_at IS NULL;
```

**First action.** Start the broker. Do not touch the outbox tables — the rows are the recovery.

```bash
docker compose -f infra/replica/docker-compose.light-replica.yml start redpanda
```

**What is guaranteed, and was measured.** Bookings still commit (**541 ms**, HTTP 202) and their
`ride.requested` waits in `rides.outbox`; the ride sits in `Requested` and is not lost. On recovery
the outbox drained **842 ms** after the broker was healthy and the held ride reached `Offered`
**105 ms** later. Nothing was lost. **Broker healthy took 22 s.**

**What a passenger sees, and cannot be told.** ADD §14.1 promises the position payload carries a
`data_age` field so the app can show "updating…". It does not exist on any surface — neither
`NearbyResponse` nor the SignalR `VehicleFrame` carries a sample timestamp, and `asOf` is when
query-svc answered. Under lag the map shows stale markers with a current clock.

**Related:** [outbox-lag.md](outbox-lag.md) · [consumer-lag.md](consumer-lag.md) · [redpanda-partitions.md](redpanda-partitions.md)

---

## 40 · MQTT broker down (EMQX)

**Detection signal.** `EmqxAuthFailureRateHigh` will not fire — there is nothing to authenticate
against. Position ingest stops; `mageride_mqtt_bridge_forwarded_total` goes flat. HAProxy keeps
**accepting** TCP on 8084 for as long as its health check takes to notice, so a driver app gets an
established socket and no CONNACK rather than a connection failure.

**First action.** Start EMQX, then **watch for the bridge's subscription to come back** — the
container being healthy is not the same as telemetry flowing:

```bash
docker compose -f infra/replica/docker-compose.light-replica.yml start emqx
docker compose -f infra/replica/docker-compose.light-replica.yml exec -T emqx \
  /opt/emqx/bin/emqx ctl clients list | grep svc-mqtt-bridge
```

**Measured, and this is the number to plan around.** EMQX reported healthy **58–81 s** after start
(its healthcheck has a 60-second `start_period`) and a device could open a socket **25–43 ms** after
that. **mqtt-bridge-svc's subscription is the long pole and it is not bounded by anything you can
see:** 71 s after a clean `docker compose restart emqx`, and **more than four minutes** after a
stop/start cycle where the broker had been away ~100 s. `MqttBridgeOptions.ReconnectDelayMin/Max`
are 1 s and 60 s with exponential backoff, and the attempt counter does not reset when the broker
becomes reachable — a failed attempt made while EMQX is still starting pushes the next one a full
minute out. For that whole window every container is healthy, the broker PUBACKs every publish, and
nothing reaches `telemetry.raw`.

If it has not come back after two minutes, restarting hot-path is faster than waiting out the
backoff — `docker compose … restart hot-path` — and the drill's own record shows the subscription
returning immediately when the container is recycled.

**What is unaffected.** The booking plane. MQTT is never routed through the API gateway
(infra/CLAUDE.md's fence) and the drill confirmed a ride could still be requested and offered.

**Related:** [emqx-dropped-messages.md](emqx-dropped-messages.md) · [position-e2e-latency.md](position-e2e-latency.md)

---

## 50 · Outbox dispatcher wedged (nothing else is wrong)

**Detection signal.** **There is none.** Measured with ride-svc's drain lock held by an outside
session: `/health/ready` answers 200, every container is healthy, no log line is written,
`mageride_outbox_publish_failures` does not move (nothing threw — the drain returns zero rows
because it lost the leader election), and `mageride_outbox_dispatch_latency` goes quiet rather than
tall. Every ride booked meanwhile sits in `Requested` and is offered to nobody.

The one thing that does move is R-20's stuck-state observer, and only after ADD §13.3.1's **60 s**
budget for a pre-match state.

**First action.** Look for a competing holder of the drain's advisory lock before restarting
anything — a restart clears it, and also destroys the evidence:

```bash
docker compose -f infra/replica/docker-compose.light-replica.yml exec -T postgres \
  psql -U mageride -d mageride -c "
    SELECT l.pid, a.application_name, a.state, a.query, now() - a.state_change AS held_for
      FROM pg_locks l JOIN pg_stat_activity a USING (pid)
     WHERE l.locktype = 'advisory' AND l.granted;"
```

The key for `rides.outbox` is `686202424421738511` (FNV-1a of the schema-qualified table name, as
`OutboxDispatcher.AdvisoryLockKey` computes it).

**Measured recovery.** The outbox drained **5.6 s** after the lock was released and the frozen ride
reached `Offered` **214 ms** after that. The event was late, not lost — but a stall longer than
US-6A.11's **120 s** deadline costs every ride booked inside it, and the passenger is told
`ExpiredNoDriver`, which is what they are also told when the city really is empty.

**Related:** [outbox-lag.md](outbox-lag.md) · [ride-stuck.md](ride-stuck.md)

---

## 60–61 · Reconnect storm and replay flood

**Detection signal.** `EmqxMessagesDropped` (`delivery.dropped.queue_full`), and CONNACK latency —
which nothing measures server-side. Measured: connection setup went from **404 ms** at rest to a
**5.4 s median / 8.4 s p95** during a storm of only ~38 connections/s. A driver app whose reconnect
timeout is under ten seconds gives up and re-queues, which is how a storm sustains itself.

**First action.** Nothing, for the first minute. Both are self-limiting by design and both held:

- a storm of **1,200 of 1,200** sessions, 0 refused, cost the incumbent publisher **zero**
  acknowledgements;
- a **132–137 samples/s** flood on `veh/+/pos/replay` cost the live lane **zero** acknowledgements
  across four runs — but the ack-latency tail reached **4.3 s** (median 22–46 ms). Delivery is
  protected; timeliness is not, and D-19's 5 s p95 has little headroom left at that rate.

If `delivery.dropped.queue_full` is climbing, that is [emqx-dropped-messages.md](emqx-dropped-messages.md)
and load/report.md's ceiling, not the storm.

**What the drill could not reach.** `max_conn_rate = "500/s"` was never approached — the generator
shares the box with the broker. The per-ASN guardrail cannot be observed here at all: every
connection has one source address.

---

## 62 · Mass driver offline (a region loses coverage)

**Detection signal.** EMQX's connected-client count falls. Nothing else — and on this deployment,
nothing at all happens next.

**Measured, broker half:** every one of 150 sockets dropped without a DISCONNECT produced a retained
`offline` on `veh/{vehicleId}/status`, median **918 ms** after the socket died. EMQX does its part
exactly as R-15 and T-04 describe.

**Measured, platform half: nothing consumed them.** `Dispatch__LastWillEnabled` is unset — it
appears in no environment file, no compose file and no Kubernetes manifest — so `VehicleStatusWorker`
never subscribes and no `offer_release_grace` timer is armed. dispatch-svc says so at start-up.

**First action.** Set it. Until then, a driver who loses coverage mid-offer holds it for the full
15 s instead of the 5 s grace, their Directional Travel filter (DT-04) is never cleared, and T-04's
stalled-tracker path has no signal:

```bash
# .env.replica, and the equivalent in infra/k8s/overlays/*/
Dispatch__LastWillEnabled=true
docker compose -f infra/replica/docker-compose.light-replica.yml up -d --no-deps app-services
```

Then confirm with `emqx ctl clients list | grep svc-dispatch`.

---

## 63 · Network partition (a service is isolated but alive)

**Detection signal.** The container is **healthy** and answers its own `/health/ready` with 200 —
measured, with every socket to Postgres, Redis, Redpanda, EMQX and MinIO black-holed. Docker's health
state does not change; on DOKS the readiness probe would keep the pod in the Service's endpoints.
The edge is what tells you, and **what it tells you is not consistent**: measured over three runs,
`GET /v1/.well-known/jwks.json` answered `503 in 47 ms` twice and produced **no response inside
8 s** once. Do not wait for a 5xx to confirm a partition — check the container's networks.

**First action.** Confirm it is a partition and not a crash — the two need opposite responses:

```bash
docker inspect $(docker compose -f infra/replica/docker-compose.light-replica.yml ps -q app-services) \
  --format '{{json .NetworkSettings.Networks}}'
docker compose -f infra/replica/docker-compose.light-replica.yml logs --tail 20 app-services
```

An empty `Networks` map is a partition — reconnect rather than restart, so the process keeps its
state:

```bash
docker network connect --alias app-services mageride-replica_internal <container>
```

Measured: the platform served again **1.5 s** after the network was reattached, with no restart. If
the container comes back on a *different* address and the edge does not follow it, restart HAProxy —
`haproxy.replica.cfg` declares `resolvers docker` for exactly this, and it is the failure to check
for first.

**What is unaffected.** The ingest plane. MQTT is a separate listener into a separate container.

---

## 70 · Wallet balance unknowable (D-08)

**Detection signal.** Drivers reporting they get no rides after their first of the day, with no
error anywhere. `dispatch.candidate_scores.breakdown->>'rejectedBy'` is the only place it is
recorded.

**First action.** Read the audit before believing a dispatch bug:

```sql
SELECT s.evaluated_at, s.breakdown->>'rejectedBy' AS gate, s.breakdown
  FROM dispatch.candidate_scores s
 WHERE s.driver_id = '<driver>'
 ORDER BY s.evaluated_at DESC LIMIT 10;
```

**Measured: D-08 held on both halves.** A driver with `balance_minor = 0` and no `wallet:bal` cache
was offered their **first** trip of the Colombo day *and rode it to completion* — so US-9.1's
`402 insufficient-wallet` at the accept gate correctly did not apply — and was dropped from the
candidate set on the **second**, 840 ms after booking, with `rejectedBy = wallet_daily_fee`.
`tripsToday` counts `dispatch.offers` rows that reached **`ACCEPTED`**, not offers sent, which is
why the first-trip rule survives a billing outage.

**Two defects live on this path and you will meet them before D-08 does.** A completed cash ride
cannot be settled here (`POST /v1/fare/pay` answers `404 — no computed fare yet`) so it sits in
`PaymentPending`, which `ux_rides_open_passenger` does not exempt — that passenger and that driver
are both out of service until somebody intervenes. And the driver's next accept is then answered
**`500` with a stack trace** rather than the documented 409, because
`ux_rides_driver_busy`'s violation is uncaught. Both are in `chaos/report.md` §3.

---

## 90 · Disaster recovery — restore from backup

**Detection signal.** You are here because the database is gone. There is no signal to wait for.

**First action.** Read `chaos/report.md`'s DR section before running anything. The measured RPO of
this deployment is **one backup interval**, not ADD §15's five minutes: there is no pgBackRest and no
WAL archive, `backup.sh` takes a `pg_dump -Fc` and `restore.sh` puts one back. On the runbook's
nightly `15 2 * * *` schedule that is up to **24 hours** of writes, and no improvement in restore
speed changes it.

Then:

```bash
bash infra/replica/restore.sh --list          # what is available
bash infra/replica/restore.sh                 # newest; asks before it drops anything
bash infra/replica/smoke.sh                   # and prove it came back
```

**Measured RTO: 1 m 11 s and 1 m 25 s** over two runs, taken end to end from the drop to a passenger
being able to book again — not from `pg_restore` exiting. It breaks down as a **13 s** backup, a
**43–57 s** `restore.sh` (stop five containers, drop and recreate, reinstall three extensions,
`pg_restore`), **25 s** for every container to report healthy again and **3–4 s** to the first good
request. Against ADD §15's 30 minutes that is comfortable — on a **1.5 MB** dump of a 13,000-row
telemetry table. It does not extrapolate: production's telemetry hypertable is the whole of ADD
§16.4's write load and `pg_restore` is linear in it.

**`restore.sh` could not restore at all until C130 fixed it.** `psql -c "DROP DATABASE …; CREATE
DATABASE …;"` sends both statements as one query, which the server wraps in a transaction, and
`DROP DATABASE` cannot run in one. It died there having already stopped five containers — the
platform down with the database intact. `backup.sh --verify-restore` never caught it because it
restores into a *fresh* scratch database and only ever runs `CREATE DATABASE` on its own.

**Related:** [replica-operations.md](replica-operations.md) §4
