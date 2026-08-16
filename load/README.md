# load — the C129 load and capacity suite

Load and capacity validation against ADD §3.2's non-functional goals and §16's sizing model, run
against the **lightweight production replica** on the Contabo VPS. The measured figures and what
they mean are in **[report.md](report.md)**; this file is how to run it and what it does and does
not cover.

```bash
bash infra/replica/deploy.sh          # the replica has to be up
bash load/configure.sh                # accounts, bearers, and the fleet's cell map
k6 run load/ingest.js --summary-export=load/out/ingest.json && k6 run load/dispatch.js
```

`bash load/run.sh` drives every profile with the server side sampled around each one.

## What is here

| File | What it is |
|---|---|
| `configure.sh` | provisions the synthetic accounts, signs them in, writes `env.json` (gitignored) |
| `probe.js` | one vehicle, one sample — the shortest reproduction of the whole chain |
| `warmup.js` | one orbit per vehicle, so `configure.sh` can read each vehicle's res-7 cell back |
| `ingest.js` | the ingest profiles, and the D-19 end-to-end SLO |
| `step.sh` | the rate sweep that finds where the chain stops keeping up |
| `dispatch.js` | concurrent ride requests and the offer-latency distribution |
| `fanout.js` | ADD §16.3's subscriber shape — sends per pod per second |
| `accept-race.sh` | ADD §11.11's single-winner accept, under contention, through the edge |
| `collect.sh` | what the *server* saw: counters, CPU, row counts, the DB-side distributions |
| `run.sh` | all of it, in order |
| `lib/` | MQTT-over-WebSocket, the CBOR codec, the session JWT, the SignalR client, the fleet |

## The profiles

| Profile | Shape | Why |
|---|---|---|
| `smoke` | 25 sessions × 4 msg/s = 100 msg/s, 30 s | proves the chain and the fixture |
| `sustained` | 750 × 4 = **3,000 msg/s**, 180 s | ADD §3.2's launch target |
| `burst` | 3,750 × 4 = **15,000 msg/s**, 60 s | ADD §3.2's launch burst |
| `fleet` | 3,000 × 0.12 = 360 msg/s, 120 s | §16.1's per-vehicle *cadence*, to price a connection |

## Two things this suite is careful about

**The message rate and the vehicle count are different loads, and the replica can only host one.**
ADD §3.2 states ingest as a rate (3,000 msg/s); §16.1 derives that rate from 10,000 vehicles at a
blended 0.12 msg/s each. Reaching the rate the way production does needs 25,000 concurrent MQTT
sessions, which is twelve times what the replica's 2 GB EMQX is sized for. So `sustained` and
`burst` publish the production *rate* from a smaller fleet, and `fleet` publishes the production
*cadence* from as many sessions as the box will hold. Every figure in `report.md` says which of the
two it came from.

**A profile the replica cannot carry must not exit 0.** k6's thresholds carry the definition of
done — `position_e2e_ms p(95)<5000, p(99)<8000` (D-19) and an acknowledged-message count within 5 %
of the target rate — and `run.sh` records a breach rather than softening it. `burst` is expected to
fail, and the failure is the deliverable.

One consequence of the manifest's verify command being chained with `&&`: while D-19 is missed
(`report.md` §1–2), `k6 run load/ingest.js` exits 99 and **`load/dispatch.js` never runs**. That is
the right order — there is nothing to learn about dispatch latency on a platform whose telemetry
plane is dropping nine samples in ten, and §4.3 explains why the two are not independent. Run the
halves separately while that is being fixed.

## What is NOT driven here, and what that costs

- **The hardware-tracker plane.** EMQX's 8883 listener is mutual TLS with `peer_cert_as_username`,
  and GT06/JT808/H02 are TCP frames at `tcp-adapter`; a WebSocket client can speak neither. T-10's
  "+100k trackers at 0.2 Hz" is therefore extrapolated from the mobile plane's measurements, not
  measured. `tests/E2E`'s `TrackerPlaneScenario` drives all four protocols correctly — at one frame
  at a time.
- **VoIP quality and tracker round-trip time.** C131's, in the Singapore region. EU numbers are not
  acceptance evidence for either (the C129 fence).
- **The portals.** `admin-portal` and `fleet-portal` are compose profiles that are not up; the
  Next.js surfaces are not part of any ADD §3.2 target.
- **A second EMQX, bridge or fanout replica.** The replica runs one of each by design, so E-08's
  shared-subscription scaling and D-40's per-pod fan-out arithmetic are validated *per pod* and
  multiplied, never measured at multi-pod scale.

## The load generator shares the box with the system under test

Eight vCPU total; the replica idles at ~0.8 and k6 takes what it needs. `collect.sh` samples
`docker stats` every five seconds so a run that was generator-bound rather than platform-bound is
visible rather than assumed. Where a figure could be either, `report.md` says so.

## Cleaning up

Everything this suite creates is greppable and synthetic:

```sql
-- accounts and vehicles
SELECT count(*) FROM iam.users WHERE phone LIKE '+9477003%';
SELECT count(*) FROM registry.vehicles WHERE registration_number LIKE 'WP-LT-%';
-- telemetry
SELECT count(*) FROM telemetry.positions WHERE vehicle_id::text LIKE '10ad10ad-%';
```

```bash
docker compose -f infra/replica/docker-compose.light-replica.yml exec redis \
  redis-cli --scan --pattern 'veh:meta:10ad10ad-*'
```

Nothing is deleted automatically. The replica is a testing stack whose data is synthetic by
construction (`infra/replica/seed.sql`), and a load run's rows are evidence until the next
`down.sh --volumes`.
