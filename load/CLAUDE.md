# load/ — conventions for the capacity suite (C129)

Stack: **stock k6** (no xk6 extension build), bash, python3 for shaping JSON. Talks to the
lightweight production replica through its edge and to nothing else. No .NET, no service code.

**Verify:** `k6 run load/ingest.js --summary-export=load/out/ingest.json && k6 run load/dispatch.js`
(after `bash load/configure.sh`; the replica has to be up).

`README.md` is how to run it, `report.md` is what it measured. This file is why it is built the
way it is.

## Rules that are load-bearing

- **Stock k6, and the protocols are written here.** k6 speaks HTTP, WebSocket and gRPC; the
  platform's ingest plane is MQTT and its realtime plane is SignalR. Both are implemented in
  `lib/` against the wire formats, because the alternative — `xk6 build --with …` — makes the
  manifest's verify command depend on a bespoke binary that a stock k6 would fail on with a
  message about a JavaScript module rather than about a missing toolchain. `lib/mqtt.js` is
  MQTT 3.1.1 over the 8084 WSS listener; `lib/signalr.js` is the JSON hub protocol with
  negotiation skipped, which is what the real client does under `skipNegotiation`.

- **The CBOR encoder is not a convenience.** `PositionSampleCodec` accepts JSON on the way in, so
  a JSON publisher would work and be half the code — and 2.4× the bytes. This suite exists partly
  to measure bandwidth and retention against ADD §16.1's own arithmetic, so it publishes what a
  driver app publishes. `lib/cbor.js` is the encode half of the deployed codec, field names copied
  from its constants.

- **Vehicle ids are derived, never allocated.** `vehicleId(n)` is
  `10ad10ad-0000-4000-8000-{n:012d}` — a pure function, so a k6 VU, `configure.sh` and a psql
  query afterwards name the same vehicle without sharing a file, and everything the suite leaves
  behind is greppable. None of them is in `registry.vehicles` and none needs to be:
  `telemetry.positions` has no FK to it (migration 1801 says why), position-processor-svc keys on
  the vehicleId EMQX authenticated, and fanout-svc has no database.

- **The res-7 cell is read back from the platform, never computed here.** A subscriber has to join
  the cells its vehicles publish into, and H3 is not implemented in this suite: an independent
  implementation would be asserting that two H3 libraries agree, and the failure mode is an empty
  map rather than an error (R-06's superseded "res-8 + ring(1)" figure is still in circulation).
  `configure.sh` publishes one orbit and reads `veh:meta:{vehicleId}`'s `cell` field back.

- **The fleet moves in a closed orbit, and the step size is not arbitrary.** position-processor-svc
  judges implied speed over `max(actual gap, MinStepInterval=1 s)`, so a 10 m step reads as
  36 km/h whatever the publish rate — under every ADD §12.6 ceiling. A refused sample does not
  become the position the next one is measured against, so one over-long step poisons the rest of
  the track. The orbit closes after 120 steps (~191 m radius), which keeps a vehicle inside the
  res-7 cell a subscriber joined for the whole run.

- **The measurement window excludes the ramp.** Publishing starts as each socket connects;
  only what happens after the ramp counts toward a rate claim, so a half-connected fleet can never
  be reported as having carried a target.

- **Publishing is catch-up scheduled, not `setInterval(publish, 1000/rate)`.** k6's timers are not
  drift-corrected and the callback cost compounds: the first version asked for 100 msg/s and
  delivered 89, which would have been read as the platform failing to keep up. The number of
  samples owed is derived from elapsed time, so a shortfall shows up where it belongs — as
  backpressure skips.

- **`seq` is strictly increasing per vehicle even inside one millisecond.** It is the R-17/T-05
  replay watermark and `seq <= watermark` is discarded outright, so a catch-up tick emitting two
  samples with one timestamp would have the second counted `replayed` — which reads as the
  platform losing data.

- **A failing threshold is a result.** `ingest.js` carries D-19's `p(95)<5000, p(99)<8000` and an
  acknowledged-count floor at 95 % of the target rate; `run.sh` records a breach and carries on.
  Nothing here is ever softened to make a scaled-down run report a production target as met — that
  is the component's first fence.

- **The client's view is never the only view.** k6 measures what a subscriber experienced;
  `collect.sh` reads what the platform did, from the services' own Prometheus endpoints, EMQX's
  `broker metrics`, `docker stats` and psql. C129's central finding is exactly a case where the
  two disagree: every publish is PUBACKed while nine in ten samples are discarded inside the
  broker.

- **Nothing here holds a credential in a committed file.** `env.json` carries EMQX's shared MQTT
  secret and a dozen live 30-minute bearers; it is written at 0600 by `configure.sh` and is
  gitignored. The bearers are obtained through `POST /v1/auth/otp/request` + `verify`, reading the
  code from the dev SMS sender's log line — never minted, because iam-svc's RS256 key is not
  something a load suite may hold.

- **`configure.sh` refuses to run anywhere but the replica**, with `infra/replica/seed.sh`'s own
  three checks: the compose project is `mageride-replica`, `replica.synthetic_marker` exists, and
  every row it writes is in the `+9477003xxxx` / `WP-LT-xxxx` block.

## Traps this suite has already fallen into

- **`docker compose logs --since` costs 95 seconds here.** The json-file driver has no index and
  `--since` rescans the file; `app-services`' log was 2.2 GB after a day and a half because the
  replica's compose file sets no `logging:` options. Use `docker logs --tail N`, which seeks from
  the end and costs 70 ms. The unbounded log itself is a finding in `report.md`.

- **`python3 - "$args" <<'PY'` already uses stdin for the program text.** A
  `printf … | python3 - <<PY` pipeline is silently discarded. Pass data as arguments.

- **The gateway's `auth` policy is 30 requests a minute per caller address, and on this deployment
  every caller has the same address.** Provisioning 24 accounts is 48 auth calls; `api_paced`
  waits out the 429s. The cause is a finding in `report.md`.

- **A re-run collides with the previous run's `veh:meta`.** The vehicle restarts at orbit step 0
  while the hash still holds the position it had seconds ago, and the implied speed over that gap
  can exceed the type's ceiling — so the first second or two of a run shows `positions.dropped
  {reason=implausible}`. It self-corrects as the allowed radius grows with the gap, and the
  measurement window starts after the ramp, but a run's drop counts should be read with it in
  mind.

## Not here, on purpose

- **The tracker plane** (8883 mutual TLS, and GT06/JT808/H02 TCP frames) — a WebSocket client can
  speak neither, so T-10 is extrapolated rather than measured. `tests/E2E`'s `TrackerPlaneScenario`
  drives all four protocols, one frame at a time.
- **VoIP and tracker RTT** — C131's, in the Singapore region.
- **Any change to a service, spec, contract or migration.** This component measures; the two
  defects it found are recorded with an owner in `report.md` and in
  `security/`-style form in the C129 handoff, not fixed here. The one exception is operational:
  `fanout-svc` was recreated to pick up C126's corrected `Jwt__JwksUrl`, because without it no
  subscriber can connect at all and the D-19 SLO is unmeasurable.
