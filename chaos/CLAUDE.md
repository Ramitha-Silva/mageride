# chaos/ — conventions for the drill suite (C130)

Stack: **bash**, `docker compose`, `psql`, `redis-cli`, `rpk`, `emqx ctl`, and **stock k6** for the
client-side drills. Talks to the lightweight production replica and to nothing else. No .NET, no
service code.

**Verify:** `bash chaos/run-drills.sh --env replica --report chaos/out/report.md`
(after `bash chaos/configure.sh`; the replica has to be up).

`README.md` is how to run it, `report.md` is what it found. This file is why it is built this way.

## Rules that are load-bearing

- **The rollback is armed before the fault, always.** `arm_rollback` pushes an undo onto a stack
  and one trap runs the whole stack in reverse on *any* exit path — a failed assertion, a Ctrl-C,
  a `set -e` abort, the terminal going away. A recovery step written as the last line of a script
  recovers nothing when the line above it fails, and a `docker stop postgres` that is never undone
  is not a drill. It follows that **every rollback must be idempotent**: `drill_end` runs it on the
  happy path too and the trap may run it again.

- **`drill_end` does not merely undo — it waits and times.** "We broke it and it came back" is a
  claim about a duration. Every drill reports how long recovery took and fails if it did not fit
  its own budget, which is per drill because a `FLUSHALL` and a `docker stop postgres` do not come
  back on the same timescale.

- **A finding is the deliverable; a failed assertion is a bug in the drill.** `finding HIGH|MED|LOW`
  records what the drill saw that the documented behaviour does not describe. `bad` records that
  the drill itself could not establish what it set out to. The exit code reflects the second, never
  the first — see README.md's "What the exit code means", and the fence in `build/prompts/C130.md`:
  *a drill that cannot be recovered from within RTO is a finding, not a footnote*.

- **Every drill takes a control.** An offer expiry measured only under a flush is a number nobody
  can read. Drill 10 expires one offer untouched before it expires one with the keyspace destroyed;
  drill 50 measures the outbox hop before it stalls the dispatcher; the SOS baseline is taken in the
  pre-flight before any fault exists. **This is not optional and it has already saved the report
  three times**: the first version of drill 10 probed a refresh token that a previous run had
  already spent and raised a HIGH finding about a platform that had done nothing wrong, and drill 70
  twice concluded that D-08 had failed when the fixture, not the platform, was what had refused.
  The rule that falls out of it: **before recording a finding, check the thing that would make the
  observation innocent.**

- **`--env replica` is a fence, not a default.** There is no other accepted value. This suite runs
  `FLUSHALL`, `docker stop postgres`, `docker network disconnect` and `DROP DATABASE`; the only
  environment where that is free is the one whose data is synthetic by construction. The
  `replica.synthetic_marker` check is made again at start-up and a third time immediately before
  the DR drill's drop — an hour may have passed, and the database answering on this socket is not
  necessarily the one that was there then.

- **Probes go through the edge.** HAProxy on 443 with the self-signed certificate, for `smoke.sh`'s
  reason: talking to `app-services:5000` would skip TLS termination, the forwarded headers, the
  `/health` and `/v1/internal` denials and the vhost routing, which is most of what the edge exists
  to do. The exceptions are deliberate and named where they occur — `metrics_of` scrapes from
  *inside* the network because no service but the edge publishes a port.

- **The fixture is put back between drills, not only by each drill's own rollback.**
  `ux_rides_open_passenger` allows one non-terminal ride per passenger, so a leaked ride is the
  *next* drill's booking refusal — a fault it did not inject and would report as if it had. The
  rollback is also the thing most likely to have been interrupted, so `run-drills.sh` clears the
  fixture after every drill regardless.

- **Nothing here holds a credential in a committed file.** `chaos/env.json` carries EMQX's shared
  MQTT secret, live 30-minute bearers and the opaque refresh tokens drill 10 probes with; it is
  written at 0600 by `configure.sh` and is gitignored. Bearers come through
  `POST /v1/auth/otp/request` + `verify`, reading the code from the dev SMS sender's log — never
  minted, because iam-svc's RS256 key is not something a chaos suite may hold.

- **Its own accounts, not C129's.** `+9477 004 xxxx` and plates `WP-CH-xxxx`, against the load
  suite's `+9477 003 xxxx` / `WP-LT-xxxx`. `env.json` is written whole by each suite's
  `configure.sh`, so sharing one would mean a chaos run silently invalidating the capacity suite's
  fixture.

- **C129's k6 libraries are imported, not copied.** `chaos/k6/*.js` reaches across to
  `../../load/lib/{mqtt,jwt,cbor}.js` — the platform's k6-side wire implementations, and a second
  copy would drift from the deployed codec. **Only `lib/config.js` is chaos's own**, because k6
  resolves `open()` against the calling module's directory and C129's would read `load/env.json`.
  Two things were added to `load/lib/mqtt.js` for this component and both are additive: a `will`
  option on CONNECT and `abort()`, which drops the socket *without* a DISCONNECT. MQTT 3.1.1 §3.14
  makes that the whole difference between a driver closing their app and a driver losing coverage,
  and drill 62 exists to measure the second.

## Traps this suite has already fallen into

- **`psql_q "…"`'s argument is a double-quoted bash string, so a backtick pair inside it is command
  substitution.** A SQL comment naming `` `iam.users.emergency_contact_name` `` the way every other
  comment in this repository does became an attempt to run it. It happened twice; the second time
  it truncated the provisioning transaction so the accounts were created and their emergency
  contacts were not, and the symptom was a 400 from `/v1/sos` three drills later. The provisioning
  block now goes through `psql_stdin <<SQL`, a quoted heredoc, which has no such layer.

- **`wait_for` runs its argument as a command in the current shell, so `bash -c '…'` does not see
  these functions.** A predicate written that way silently never succeeds and the drill reports a
  timeout. Every predicate is a function in `lib/fixture.sh` for this reason.

- **`curl -w '%{http_code}' || echo 000` prints `000000` on a timeout** — curl already writes `000`
  when there was no response *and* exits non-zero, so both halves fire. Every
  `case "$code" in 000)` branch then misses, and drill 63 read a hung edge as "answered 000000"
  instead of raising its finding. `edge_code` captures once and normalises.

- **Column names are not what they look like.** `dispatch.offers` has `sent_at`, not `created_at`;
  `dispatch.candidate_scores` has `evaluated_at`; `rides.outbox` has `dispatched_at`. A `psql`
  error inside `$( )` becomes a *string* that compares unequal to everything and passes straight
  into an arithmetic test.

- **`GET /v1/rides/history` answers 404 with the platform healthy.** It is deliberately unmapped —
  `RideEndpoints`' own remarks: "Left unmapped rather than stubbed … `GET /history` (AL-36, C048).
  A stubbed route is worse than an absent one." Drill 20 used it as its "trip history unavailable"
  probe and passed for the wrong reason; the probe is now query-svc's `GET /v1/trips/{userId}`.

- **Cancelling a ride needs `{reason, version}`,** `reason` from the SCREAMING_SNAKE enum
  `[RIDER_CHANGED_MIND, DRIVER_TOO_FAR, EMERGENCY, OTHER]` and `version` the one the client last
  saw (`VersionedCommand`). `{"reason":"other"}` is answered `400 validation-failed` on both
  counts, silently, and every fixture ride stays open.

- **"No offer was placed" is not evidence about dispatch.** It is also what
  `ux_rides_open_passenger`, `ux_offers_driver_live` and a stale presence row look like. Drill 70
  raised a HIGH finding about D-08's first-trip-free rule for a booking that had been answered
  `409 active-ride-exists`, and a second one about R-11's audit for a driver whose
  `dispatch.driver_presence` row said `OFFLINE`. `make_live_offer` now sets `FIXTURE_FAILURE` to
  `booking` or `no-offer` so a caller can tell them apart, and every "the platform refused" branch
  checks the state that would have made the refusal legitimate before it concludes anything.

- **Go online BEFORE booking.** The candidate build runs within a second of the booking, so a
  driver brought back to standby afterwards is not in the pool when it matters — the round finds
  nobody, writes no `candidate_scores` row, and the drill reads a correct empty audit as a missing
  one.

- **`tripsToday` counts ACCEPTED offers, not offers sent.** `DailyFeeRepository` filters
  `status = 'ACCEPTED' AND responded_at::date = today`, so a drill that places one offer and asks
  for a second is still asking for a *first* trip. Reaching D-08's second-trip branch means
  actually accepting — and the accept needs the offer id out of `dispatch.offers`, standing in for
  the FCM push, because `dispatch.yaml` has no driver-side offer read.

- **A generator must not be able to manufacture the failure it is looking for.** The flood drill's
  control session used to close its socket on the heels of its last publish; five in-flight PUBACKs
  never landed and the drill reported "a replay flood drowns live samples" — R-09 failing — from its
  own shutdown. `replay-flood.js` now stops publishing, drains for six seconds, and only then
  closes. Any client-side drill that counts acknowledgements needs the same gap.

- **A completed cash ride wedges its passenger and its driver on this deployment**, permanently
  (report.md §3.1). Drills that ride one to completion must pick a *free* passenger
  (`free_passenger`) rather than a fixed index, and check the driver is not still attached to one —
  otherwise the accept is answered `500` and the drill reports it as a D-08 failure.

- **The k6 client-side drills cannot reach the limits they are aimed at on this box.** The storm
  generator reaches ~35 connections/s against `max_conn_rate = "500/s"`; each session is a TLS
  handshake plus a WebSocket upgrade plus a CONNECT, driven from the same eight vCPU as the broker.
  Where a drill could not reach a documented limit it says so in the report rather than reporting
  the limit as untested-and-fine.

## Not here, on purpose

- **Patroni failover, a second EMQX node, a Redpanda quorum loss.** ADD §14's MVP column says
  "Single + daily backup", "Single node (accepted risk)" and "Single Redpanda broker (RF=1)", and
  the replica's compose file opens by calling itself "a single-point-of-failure stack by design".
  Drills 20, 30 and 40 measure the half of each §14.1 row that a single-node stack *can* answer —
  what still serves, what is refused, and how long recovery actually takes — and say plainly which
  half they could not.
- **The tracker plane's protocols.** GT06/JT808/H02 framing is `tests/E2E`'s `TrackerPlaneScenario`;
  drill 20 probes only that the listener still accepts a connection.
- **Any change to a service, spec, contract or migration.** This component breaks things and writes
  down what happened. Every finding is recorded with an owner in `report.md`; none is fixed here.
