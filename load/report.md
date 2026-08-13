# C129 — load and capacity report

**Status: PARTIAL.** Ingest, the D-19 latency SLO and the database write path were measured and
are reported below. Fan-out at ADD §16.3's subscriber scale, the offer-latency distribution and the
atomic-accept race could not be measured, because all three sit downstream of a defect this run
found in the ingest chain — §1. Their profiles are written, committed and unblocked by that fix.

**Target:** the lightweight production replica (`mageride-replica`), Contabo VPS, 8 vCPU / 24 GB,
2026-08-13. Synthetic data only. The generator runs on the same box as the system under test.

**What this measured:** ADD §3.2's non-functional goals and §16's sizing model, driven through the
replica's own edge with `load/` (stock k6, `bash load/run.sh`). Raw output is in `load/out/`
(gitignored); everything below is transcribed from it.

---

## The headline

**The ingest chain carries ~10 messages per second. ADD §3.2's launch target is 3,000 msg/s
sustained. Everything above the ceiling is discarded inside EMQX, and every publisher is
acknowledged anyway.**

That is not a sizing shortfall to be extrapolated away. It is a defect in the hot path, it is
reproducible from a cold start, and it is invisible to every client, every contract test and every
existing verify command on this repository.

---

## 1. Ingest — EMQX → mqtt-bridge → telemetry.raw → position-processor → Redis

### 1.1 The rate sweep (`load/step.sh`, 30 s a step, 4 msg/s per session)

| Offered | Sessions | Published (EMQX accepted) | Delivered to the bridge | Forwarded to Redpanda | Indexed in Redis | Dropped `queue_full` | **Carried** |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 20 msg/s | 5 | 647 | 365 | 283 | 199 | 55 | **30.8 %** |
| 40 msg/s | 10 | 1,277 | 356 | 339 | 297 | 836 | **23.3 %** |
| 80 msg/s | 20 | 2,565 | 420 | 411 | 397 | 2,152 | **15.5 %** |
| 160 msg/s | 40 | 5,105 | 367 | 389 | 290 | 4,523 | **5.7 %** |

**The delivered column is flat.** However fast the fleet publishes, EMQX hands mqtt-bridge-svc
about 360–420 messages per 30 s — **12–14 msg/s** — and everything else is counted
`delivery.dropped.queue_full`. A clean-slate repeat after restarting `hot-path` (empty queues, a
fresh session) gave the same answer: 3,000 messages published at 100 msg/s, **329 forwarded**,
2,048 dropped, 796 still queued.

### 1.2 It is not EMQX, and it is not Redpanda

| Question | Test | Answer |
|---|---|---|
| Can EMQX route at rate? | a second `svc-` subscriber on `veh/+/pos/live` at **QoS 0** (`load/lib/probe-subscriber.js`) | **2,971 of 3,057 received — 97.2 % at 100 msg/s** |
| Can Redpanda absorb the writes? | `redpanda_kafka_request_latency_seconds` over 13,693 produce requests | **16.9 ms mean**; `rpk` wrote 2,000 messages in 2.5 s including process start |
| Is anything CPU-bound? | `docker stats` through the sweep | hot-path ≤ 76 %, redpanda ≤ 63 %, emqx ≤ 134 % of one core — nothing pinned |

### 1.3 The mechanism

```
emqx.conf sets neither, so both are EMQX 5.8 defaults:
    mqtt.max_inflight    = 32       unacknowledged QoS-1 messages per session
    mqtt.max_mqueue_len  = 1000     queued beyond that; the rest are DISCARDED
    mqtt.retry_interval  = infinity never redelivered
```

mqtt-bridge-svc subscribes at **QoS 1** with `AutoAcknowledge = false` and PUBACKs only after its
Redpanda delivery report — which is correct and deliberate (`HotPath.MqttBridge/CLAUDE.md`:
"Acknowledgement follows the produce, and nothing else", because acking earlier would make
EMQX → Redpanda at-most-once). Its throughput is therefore `max_inflight ÷ acknowledge-latency`.

EMQX's own session view, taken during a run:

```
Client(mageride-bridge-…-live, username=svc-mqtt-bridge, subscriptions=1,
       inflight=32, delivered_msgs=349, enqueued_msgs=796, dropped_msgs=2048)
```

**`inflight=32` — the window is full and stays full.** At a measured 8–13 msg/s that is
**2.5–4 s per acknowledgement**, against a produce that Redpanda completes in 16.9 ms. The
remaining ~99 % of that interval is inside the bridge and is not explained by anything C129 can
see from outside the process; it is handed to C038's owner below.

### 1.4 Why nothing has caught this

- **The publisher is acknowledged.** EMQX PUBACKs the device before it decides whether it can
  deliver. The k6 client, the driver app and `mqtt_puback` all report 100 % success while 9 in 10
  samples are dropped: `achieved 99.5 msg/s (99.5 % of target), 0 broker errors`.
- **The existing suites drive one frame at a time.** `tests/E2E`'s `TrackerPlaneScenario` and
  `HotPath.Tests` assert that *a* sample reaches Timescale; `MqttBridgeRateTests` measured "4.9/s
  on a single socket". Nothing before this component published faster than the ceiling.
- **The contract sweep cannot see it.** `POST` a position and it is accepted; the loss is on a
  path with no HTTP surface.

### 1.5 The signal, and what to do

**Alarm on `delivery.dropped.queue_full` from EMQX. A non-zero value is unrecoverable telemetry
loss and there is no other symptom.** It is not currently scraped — `infra/observability`'s targets
do not include EMQX's Prometheus endpoint.

Ordered by what each buys:

1. **Find the 2.5–4 s inside `TelemetryForwarder.CompleteAsync`.** Everything else is a workaround.
   The produce is 17 ms; the ack path is 150× that.
2. **Raise `mqtt.max_inflight` for the bridge's zone.** The ceiling is linear in it: 32 → 512 is
   16× throughput at the same latency, and QoS-1 ordering is preserved per topic regardless
   (`telemetry.raw` ordering comes from the partition key, not from the MQTT window).
3. **Set `retry_interval` to a finite value.** With `infinity`, an acknowledgement lost for any
   reason costs that slot permanently and the ceiling decays.
4. **Alarm on it.** `max_mqueue_len` is doing exactly what it is configured to do; the problem is
   that nobody is told.

---

## 2. End-to-end position latency (D-19) — **not met, by 7×**

| | Measured | ADD §3.2 / D-19 |
|---|---|---|
| p95, device GNSS instant → frame at a subscribed passenger | **33,603 ms** | < 5,000 ms |
| p99 | **35,859 ms** | < 8,000 ms |
| observations | 174 | — |

Measured at **100 msg/s**, one thirtieth of the launch target, from a real `/hubs/live` subscriber
holding real res-7 geocell memberships. An earlier run of the same profile gave 36,558 / 36,903 ms
over 111 observations — the figure is reproducible and is not a transient. `k6 run load/ingest.js
-e PROFILE=smoke` exits **99** on the `position_e2e_ms` threshold, which is the definition of done
failing as it should rather than being softened.

This is a **consequence of §1**, not an independent finding: samples queue in EMQX's mqueue behind
a 10 msg/s drain, so the delay is the backlog, and it grows without bound for as long as the
offered rate exceeds the ceiling. The platform's own `mageride.positions.ingest.latency` agrees —
839,338 s over 7,280 samples, a **115 s mean**.

The path itself is sound: with the chain drained, a frame reaches a subscriber correctly and the
correlation rate is 100 % (111 frames matched, 0 unmatched). **The 2 s `Fanout:BatchInterval` and
the pumps are not the problem** — they contribute at most 2 s of the 36.5.

---

## 3. Fan-out (ADD §16.3)

**The counter an operator would scale on is not the quantity the model is written in.**

§16.3 prices fan-out in *SignalR sends per pod per second* — one message delivered to one
subscriber — and derives pod count by dividing by D-40's 10–25k sends/pod/s.
`CellStreamPump` calls `Clients.Group(cell).SendAsync("VehiclePositions", visible)` **once per cell
per tick** and then adds `visible.Count` to `mageride.fanout.frames`. That counter is therefore
**vehicle frames per group send, independent of how many subscribers are in the group**: a cell with
one subscriber and a cell with a thousand read identically. Dividing it by 10,000 sizes nothing.

`load/fanout.js` measures the §16.3 unit the only way left — by counting the WebSocket messages
that actually arrived at subscribers — and `load/collect.sh` records the platform counter beside it
so the two can be compared.

**The subscriber-scale run (20 cells × 30 subscribers, §16.3's own shape) was not completed on this
box**: it needs the ingest chain to carry ~5 vehicles per cell at ≥ 0.5 Hz, which is above the
10 msg/s ceiling of §1. It is unblocked the moment §1 is fixed, and the profile is written and
committed.

What *was* measured, at the smoke rate: fan-out delivered every frame it was given, with
100 % correlation and no unmatched frames.

---

## 4. Dispatch

### 4.1 A dispatch plane that dispatched nothing

`Dispatch__RideServiceBaseUrl` was `http://dispatch-needs-ride-svc:8080` — the deliberate
placeholder from `infra/env/.env.app.example`, never overridden for the replica, and **NXDOMAIN**.
dispatch-svc could not reach ride-svc to place an offer, so its `ride.events` consumer never
committed an offset:

```
GROUP dispatch-svc   STATE Stable   MEMBERS 1   TOTAL-LAG 8
ride.events  0  CURRENT-OFFSET -   LOG-END-OFFSET 1   LAG 1
ride.events  1  CURRENT-OFFSET -   LOG-END-OFFSET 4   LAG 4
ride.events  2  CURRENT-OFFSET -   LOG-END-OFFSET 3   LAG 3
```

`reputation-svc`, in the same container reading the same topic, was at lag 0 — so this was not the
broker, the topic or the container.

**Every Mode C ride booked on this replica sat in `Requested` for ever.** `POST /v1/rides/request`
answers 202 and `GET /v1/rides/{id}/state` answers 200, which is why the wave-5 contract sweep is
green over a dispatch plane that dispatches nothing.

**Fixed** in `infra/replica/docker-compose.light-replica.yml` (`Dispatch__RideServiceBaseUrl:
http://127.0.0.1:5106`, ride-svc's loopback port in the co-located container). After recreating
`app-services` the consumer drained to lag 0 and the eight stranded rides moved `Requested` →
`Matching` immediately.

### 4.2 The edge rate limiter is one bucket for the whole platform

`Gateway__ForwardedHeaders__KnownProxies__0=haproxy` — a **hostname**, and
`ConfigureForwardedHeaders` does `IPAddress.TryParse(proxy, …)`, which rejects it silently. The
known-proxy list is therefore empty, `X-Forwarded-For` is ignored (HAProxy sets it —
`option forwardfor` is in the config), and `GatewayRateLimitMiddleware.Subject` keys every bucket on
`route|HAProxy's address`.

Proven rather than inferred: **40 requests carrying 40 distinct `X-Forwarded-For` values, 39
refused with 429.**

The consequence is a platform-wide ceiling per route, not a per-caller one:

| Policy | Routes | Ceiling **for every caller on the platform, combined** |
|---|---|---|
| `auth` | `/v1/auth/**` | 30 requests/min |
| `write` | `/v1/rides/**` (**all methods**), `/v1/fare/**`, `/v1/standby/**` | 120 requests/min |
| `default` | most reads | 300 requests/min |

At 120/min the entire Mode C surface — booking, reading a ride's state, cancelling and every fare
estimate — shares **2 requests per second**. A ride costs about five of them, so the edge caps the
platform at **~0.4 rides/s**, or ~35k rides/day. ADD §16.4's launch figure is 10k trips/day, so this
is not breached at launch — but the margin is 3.5×, it is invisible until it bites, and `auth` at
30/min is 43k sign-ins/day against a 100k-passenger target.

The code's own comment states the failure mode exactly: *"Must be HAProxy's address, or
X-Forwarded-For is ignored and every caller collapses into one rate-limit bucket (C008 handoff)."*
The value set is not an address.

**Not fixed here** — the correct value is a deployment decision (a container IP that moves on every
recreate, versus a `KnownNetworks` CIDR that widens proxy trust), and it belongs with C008/C125.

### 4.3 What was measured, and what is still blocked

With C129-03 fixed, three (passenger, driver) pairs — each in its own 13 km square of the E2E grid,
sized to sit under §4.2's ceiling — over 150 s:

| | Measured | Against |
|---|---|---|
| `POST /v1/rides/request` | **145 ms median, 202 ms p95** | no documented budget |
| `rides.outbox` → dispatched (E-09) | **116 ms median, 676 ms p95, 937 ms max** (56 rows) | E-09's "offer push median < 50 ms" — **2.3× over** |
| Rides requested | 6 | — |
| Rides reaching `Offered` | **0** | — |
| Edge 429s / API errors | **0 / 0** | the profile stayed under §4.2 |
| D-05 penalties raised against a load passenger | **0** | correct: a pre-acceptance cancel raises none |

**The ride reaches `Matching` and no candidate is ever found.** `dispatch.candidate_scores` took
**0 rows** — and R-11 records a row for *every candidate considered, eligible or not* — so the
candidate set was empty before any gate ran, rather than every candidate being refused.

The pool itself works: `POST /v1/standby/online` writes an `AVAILABLE` `dispatch.driver_presence`
row, `driver:availability:{driverId}`, `veh:driver:{vehicleId}` and the
`geo:drivers:available:{type}:{res5cell}` membership, all verified directly.

What does not survive is **freshness**. `CandidateRepository` gates on
`last_seen_at >= now() - MaxPositionAge` (D5' §3.2's `2×expectedInterval`), and after go-online the
only thing that advances `last_seen_at` is `PresenceRepository`'s update **driven from
`telemetry.normalized`** — i.e. from the plane §1 shows dropping ~90 % of samples. A driver goes
online, is a candidate for up to `Dispatch:PresenceTtl` (60 s), and then falls out of the pool
because their positions are being discarded inside EMQX.

**So the dispatch DoD item is blocked behind C129-01, not independent of it.** The three drivers
published at 1 Hz throughout; 335 telemetry rows landed from ~600 offered over the run, and not
reliably enough to keep a presence row inside a 60 s window.

The offer-latency distribution, the accept race and `load/accept-race.sh` are therefore **not
reported as measured**. They are unblocked by C129-01 and the profiles are committed and ready.

---

## 5. Database write load (ADD §16.4)

**§16.4 models the wrong write path.** It prices "Position sampling: 10k vehicles × 1 write/min =
~167 WPS (trivial)" — which is `trips.position_samples`, the 1/min operational downsample, and that
table takes a row only for a Mode A/B vehicle on an **ACTIVE** tracking session. Every normalised
sample, from every vehicle, is also `COPY`-ed into the `telemetry.positions` hypertable by
persistence-writer-svc. At the launch target that is **3,000 rows/s, not 167** — an 18× difference
in the line §16.4 calls trivial. ADD §9.5's own "40k rows/s" figure is the one that describes this
path, and §16.4 does not reference it.

Measured: every sample that survived §1 was written. `mageride.telemetry.rows_written` tracked
`positions.processed` exactly (4,523 = 4,523), with **0 dead-lettered and 0 flush failures**, and
`telemetry.positions` grew by exactly the indexed count. The Timescale batch writer was never the
constraint — it never saw more than ~10 rows/s to write.

`trips.position_samples` took **0** rows throughout, correctly: every load vehicle is Mode C and has
no tracking session.

---

## 6. Other findings

### 6.1 The wire payload is ~2× ADD §3.4 A3's assumption

A3 assumes "≈ 80–120 bytes on the wire (CBOR/Protobuf); ~250 bytes JSON". The landed
`PositionSampleCodec` encodes a full sample as **227 bytes of CBOR** (measured by `load/probe.js`),
because `vehicleId` is a 36-character UUID *string* and `sampleTs` a 28-character ISO-8601 *string*
— 64 bytes of text before a key is written.

Everything §16.1 derives from A3 doubles: 0.12 MB/s → **0.27 MB/s** steady at launch, ~10 GB/day
raw → **~23 GB/day**, and 70 GB of 7-day retention → **~160 GB**. On a single-VPS replica with a
290 GB disk shared with Postgres, MinIO and the container logs, that is a sizing question rather
than an accounting one.

### 6.2 Container logs are unbounded, and are mostly SQL

`app-services`' json-file log was **2.2 GB after 1.4 days** — ~1.6 GB/day at idle. The replica's
compose file sets no `logging:` options at all, so nothing rotates. The content is almost entirely
`info: Npgsql.Command` — every statement echoed at Information level (306 of the last 400 lines).

Two consequences, both measured here: the disk fills without a ceiling, and
`docker compose logs --since` takes **95 seconds** on that file (the json-file driver has no index),
which is slow enough to have broken this suite's own account provisioning until it was changed to
`docker logs --tail`.

### 6.3 `fanout-svc` could not validate any bearer

Found on the suite's first run: every authenticated `/hubs/live` connection answered **500**, from
`JwksConfigurationManager.FetchAsync` → 404. The container was still carrying the pre-C126
`Jwt__JwksUrl=http://app-services:5000/v1/internal/iam/.well-known/jwks.json`; C126 corrected
`.env.common.example` on 2026-08-11 15:04 UTC but only `app-services` was recreated afterwards.
`fanout` and `tcp-adapter` were created at 08:23 UTC that day and kept the old value.

**Fixed operationally** (`docker compose up -d --no-deps --force-recreate fanout tcp-adapter`).
`load/configure.sh` now asserts the value before a run, because the realtime plane is otherwise
entirely down while every contract operation stays green — `/hubs/**` is routed by HAProxy straight
to fanout and is not one of the 382 operations the wave-5 sweep drives.

### 6.4 An offer id has no read path

`POST /v1/rides/{rideId}/offer/{driverId}/accept` requires `offerId`, and the accept matches it
against `rides.rides.current_offer_id`. The id reaches a driver in the FCM `RIDE_OFFER` payload and
nowhere else: `GET /v1/rides/{id}/state` returns `{state, version, offerExpiresAt}`, `RideDetail`
carries a `driver` block only from `Accepted` onward, and `dispatch.yaml` has no driver-side offer
read. **A driver whose push was lost cannot recover a live offer**, and `GET
/v1/rides/driver/{driverId}/active` — documented as "driver-side resume" — resumes an accepted ride
but not an offer. `load/accept-race.sh` reads the id from `dispatch.offers`, standing in for the
push.

### 6.5 The only documented budget on the dispatch path is an alarm threshold

E-09 gives "offer push median < 50 ms" for the outbox hop alone; ADD §13.3.1's stuck-state table
gives `Matching` 60 s and `Offered` 20 s. Neither is a latency *target* for request → offer, so this
report measures against §13.3.1 and says so. The outbox hop itself was measured at **209 ms median,
673 ms p95** (39 rows) — over E-09's 50 ms, on a box carrying a load run.

---

## 7. Measured directly, extrapolated, or not measured

| ADD target | Status | Figure |
|---|---|---|
| Ingest 3,000 msg/s sustained | **measured — not met** | ceiling ~10 msg/s, 0.3 % of target |
| Ingest 15,000 msg/s burst | **not attempted** | pointless below a 10 msg/s ceiling; the profile is committed |
| Position latency p95 < 5 s / p99 < 8 s | **measured — not met** | 36.6 s / 36.9 s at 100 msg/s |
| Fan-out sends/pod/s (§16.3) | **not measured** | blocked by the ingest ceiling; instrument gap recorded in §3 |
| DB write load (§16.4) | **measured — model corrected** | writer never saturated; §16.4 understates by 18× |
| Offer dispatch median | **not measured** | the plane was dead (C129-03, fixed); it now reaches `Matching` and finds no candidate — blocked behind C129-01, §4.3 |
| Outbox hop (E-09, < 50 ms median) | **measured — not met** | 116 ms median, 676 ms p95 |
| Atomic-accept single winner (§11.11) | **not measured** | needs an offer to race; `load/accept-race.sh` is committed |
| Concurrent vehicle publishers 10,000 | **not measured** | the replica's EMQX is sized for ~2,000 sessions; needs the `fleet` profile once §1 is fixed |
| Concurrent passenger sockets 100,000 | **not measured** | one fanout container; §16.3's per-pod arithmetic is the extrapolation path |
| VoIP concurrency / tracker RTT | **out of scope** | C131, in the Singapore region |
| Hardware-tracker plane (T-10) | **not driven** | 8883 is mutual TLS and GT06/JT808/H02 are TCP frames; see `load/README.md` |

**No production target is reported as met by this run.**

---

## 8. The first scaling bottleneck

**mqtt-bridge-svc's acknowledgement of QoS-1 deliveries from EMQX**, at ~10 msg/s.

- **The signal that would trigger it:** EMQX's `delivery.dropped.queue_full`, non-zero. Nothing else
  changes: publishers are acknowledged, no error is logged, no HTTP status moves, and CPU stays low.
- **The order of what is behind it**, once that ceiling lifts: at the launch profile the next
  constraints in line are the `telemetry.positions` write path at 3,000 rows/s (§5), then fan-out at
  §16.3's ~10.8k sends/s against D-40's 10k/pod/s lower bound — i.e. the second fanout pod. Neither
  has been reached.
- **The edge rate limiter (§4.2) is the first bottleneck on the *control* plane**, at 2 writes/s
  platform-wide, and it is independent of the ingest one.
- **It is not only the map that stops.** The dispatch candidate pool is refreshed from
  `telemetry.normalized` (§4.3), so the same ceiling takes drivers out of the pool 60 s after they
  go online. A platform losing 90 % of its telemetry does not degrade to a stale map — it stops
  dispatching rides, with no error anywhere.

---

## 9. Findings, with owners

| id | Severity | What | Owner |
|---|---|---|---|
| **C129-01** | **HIGH** | Ingest carries ~10 msg/s against a 3,000 msg/s target; the excess is discarded by EMQX as `queue_full` while every publisher is acknowledged. Root cause is a 2.5–4 s acknowledgement in the bridge against a 17 ms produce. | C038 mqtt-bridge-svc; `emqx.conf` (C009) for `max_inflight` / `retry_interval` |
| **C129-02** | **HIGH** | D-19's position SLO missed by 7× at 1/30th of the target rate — a consequence of C129-01 | C038 (closes with C129-01) |
| **C129-13** | **HIGH** | The dispatch candidate pool starves behind C129-01: after go-online only `telemetry.normalized` advances `dispatch.driver_presence.last_seen_at`, so a driver falls out of the pool on D5' §3.2's freshness gate within `Dispatch:PresenceTtl` and no ride is ever offered. **The on-demand ride plane depends on the telemetry plane and fails silently with it** | C038 (closes with C129-01); C034 for whether standby alone should keep a driver dispatchable |
| **C129-03** | **HIGH** | `Dispatch__RideServiceBaseUrl` was an NXDOMAIN placeholder: no Mode C ride was ever dispatched on the replica. **Fixed** in the replica compose file | C125 (fixed); C009 for the `.env.app.example` placeholder |
| **C129-04** | **HIGH** | The gateway's rate limiter buckets every caller on the platform together, because `KnownProxies` is a hostname that `IPAddress.TryParse` rejects. 2 writes/s platform-wide | C008 / C125 |
| **C129-05** | MEDIUM | `fanout-svc` and `tcp-adapter` were running a pre-C126 `Jwt__JwksUrl`; the realtime plane answered 500 to every authenticated connection. **Fixed** operationally, and asserted by `load/configure.sh` | C125 |
| **C129-06** | MEDIUM | `mageride.fanout.frames` counts frames per *group send*, not per-subscriber sends, so it cannot be compared with ADD §16.3's pod arithmetic | C041 / C119 |
| **C129-07** | MEDIUM | Container logs are unbounded and dominated by `Npgsql.Command` at Information level — 1.6 GB/day per container at idle, nothing rotates | C125 |
| **C129-08** | MEDIUM | ADD §16.4 models only the 1/min operational downsample and omits the hypertable write path, understating the launch write load by ~18× | spec (ADD §16.4) |
| **C129-09** | LOW | ADD §3.4 A3's 80–120 byte wire payload is 227 bytes as landed; every bandwidth and retention figure derived from it roughly doubles | spec (ADD §3.4, §16.1) |
| **C129-10** | LOW | An offer id is delivered only by push and has no read path; a driver whose push is lost cannot accept | D3' ride/dispatch contracts |
| **C129-11** | LOW | No documented latency budget exists for request → offer; only §13.3.1's stuck-state alarm thresholds | spec (ADD §13.3.1 / E-09) |
| **C129-12** | LOW | EMQX exposes no Prometheus target in `infra/observability`, so `delivery.dropped.queue_full` — C129-01's only signal — is unscrapable | C119 |

---

## 10. Reproducing

```bash
bash infra/replica/deploy.sh
bash load/configure.sh                     # accounts, bearers, the fleet's cell map
bash load/step.sh --rates 20,40,80,160     # §1.1
k6 run load/lib/probe-subscriber.js        # §1.2, the QoS-0 control
bash load/run.sh                           # every profile, with the server side sampled
```

EMQX's own view of the ceiling, which is the fastest confirmation of all:

```bash
docker compose -f infra/replica/docker-compose.light-replica.yml exec emqx \
  /opt/emqx/bin/emqx ctl broker metrics | grep -E 'qos1|queue_full'
```
