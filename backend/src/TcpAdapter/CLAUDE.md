# tcp-adapter (C043) — hardware GPS tracker ingest

Stack: .NET 10 **Worker** (no HTTP surface) + MQTTnet 5 + StackExchange.Redis + Dapper over Npgsql.
References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/TcpAdapter.Tests -c Release`

## What this service is

Terminate a hardware tracker's socket on 5023-5026, authenticate its IMEI against provisioning-svc,
decode the binary or ASCII frame into the canonical `PositionSample`, and publish it into EMQX on
`veh/{vehicleId}/pos/live` (or `/pos/replay`) as the `svc-tcp-adapter` bridge user. Downlink commands
travel back: `veh/{vehicleId}/cmd` → a protocol-native command frame on the open socket. A half-closed
socket publishes the retained `status=offline` that emulates an MQTT last will.

Contracts: `backend/contracts/realtime/mqtt-topics.md` §2.1/§6/§7, D6' §4, ADD §7.7 and §11.4,
D7' §2.1's Container 9.

| Family | Transport | Port | Devices | Adapter |
|---|---|---|---|---|
| GT06 / GT06N | TCP, binary framed | 5023 | Concox GT06, TK103, ST-901 | `adapter-gt06` |
| JT/T 808 | TCP, binary framed, escaped | 5024 | Chinese / SL-import trackers | `adapter-jt808` |
| H02 / H02X | TCP, ASCII, delimited | 5025 | older bus trackers | `adapter-h02` |
| Generic NMEA | UDP | 5026 | low-cost asset trackers | `adapter-nmea-udp` |
| NMEA over MQTT | MQTT native | 8883 | Teltonika / Queclink | **none — direct to EMQX** |

## Fences

- **The canonical name is `tcp-adapter`.** D3' Part 1's `tracker-adapter-svc` is an alias only
  (D-DRIFT-2, planner finding 5).
- **MQTT-capable trackers never reach this service.** They connect to EMQX on 8883 with the same
  per-device credential and are confined by the same ACL. Nothing here is a fallback for them.
- **5023-5026 are TCP/UDP passthrough and never HTTP-routed.** `infra/deploy/haproxy.cfg` puts
  5023-5025 in `mode tcp` frontends with no inspection; 5026/UDP is published by the container itself,
  because HAProxy has no UDP forwarder. There is deliberately no path here by which a request could be
  routed anywhere.
- **No HTTP surface at all** (`mqtt-topics.md` §7). This is a `Microsoft.NET.Sdk.Worker` project with no
  Kestrel and no `AddMageRideDefaults` — that call configures the HTTP kernel, and there is no listener
  for a request to arrive on. D7' §5.1 gives the container a TCP-socket liveness probe instead.

## Rules that are load-bearing

- **Nothing is published before the vehicle is known.** The topic is derived from
  `prov.tracker_bindings` and there is no other authorisation on this path: a tracker cannot present a
  JWT, so EMQX's `verify_claims` + ACL enforcement — which is what confines an MQTT-native device — is
  simply unavailable here. The guarantee is instead structural: the only thing in this project that
  produces a topic is a `TrackerAuthorisation.VehicleId`.
- **One broker connection for every device on the pod.** The adapter is a `svc-` principal and
  `acl.conf` grants that prefix `veh/#`, so ten thousand sockets share one MQTT session. A session per
  device would multiply EMQX's connection count by the tracker population and buy no authorisation the
  `svc-` grant does not already give.
- **`seq` is the capture instant in milliseconds** (`TrackerSamples`). GT06's and JT/T 808's information
  serials are sixteen bits, wrap in hours and survive neither a device reboot nor a pod move — all of
  which `veh:seq:{vehicleId}` outlives. The GNSS instant is monotonic per vehicle, is *identical* for a
  sample sent live and the same sample re-sent from flash, and makes the R-17/T-05 backlog dedupe fall
  out of the comparison position-processor already makes.
  **The cost of that choice, which nothing stated until 2026-08-11: `seq` inherits the timestamp's
  resolution, and all four families stamp to the whole SECOND** — so every seq ends in `000`, two
  distinct fixes captured inside one second carry the same seq, and position-processor's watermark
  discards the later one as a replay (`Redis/LivePositionIndex.cs`, `>=`). One sample per vehicle per
  second is therefore a ceiling — and **it is lower than the rate limits the platform advertises**:
  AL-12's fastest scheduled cadence is 1 call/s (safe), but it is "bounded by the 5 msg/s/vehicle
  broker ceiling (§12.4)" and position-processor's D-17 line is 10 msg/s over 10 s. A tracker
  publishing anywhere between 2 and 5 msg/s is inside both limits and silently loses every fix but the
  first of each second. Giving seq real resolution means the frame counter this bullet rejects, so
  closing it is a spec question rather than a change here.
- **Two sources for an IMEI, in this order.** `imei:{imei}` first — present means ACTIVE (C030's rule;
  there is no cached "revoked"), so a hit is the whole answer and a fleet keeps publishing through a
  provisioning-svc restart. A miss goes to `GET /v1/internal/trackers/{imei}/validate`. A presented
  credential **always** goes to `validate`, because the cache holds one value per IMEI and cannot
  evaluate the anti-clone rule.
- **Unresolvable means refused.** Not "allowed pending confirmation". An adapter that admitted devices
  while it could not check them would publish for revoked trackers for the length of the outage, and
  C030 chose the other direction deliberately.
- **T-12 is a subscription, not a poll.** `RevocationWatcher` closes a matching socket on the
  `prov:tracker` message inside ADD §7.7.3's one second. `tracker.revoked` closes; so does
  `tracker.bound`, because an IMEI bound while a socket is open has moved to another vehicle and that
  socket is publishing under the old one. **A rotation does not** — "rotation is not revocation, and
  conflating them bricks devices" (C030): the replacement is minted fourteen days early precisely so a
  tracker out of coverage can come back and collect it.
- **T-08 is reported, not adjudicated.** Two live sockets holding one identity is a fact only this
  service can see, and both stay open: closing one would destroy the evidence and might well leave the
  clone publishing. provisioning-svc decides, and its answer arrives as a revocation.
- **T-11 is applied at ingest** (`ModeGate`). §7.7.7's "pings sent while offline are rejected and never
  reach the live map or dispatch" is a statement about where the sample stops. Mode C requires
  `veh:driver:{vehicleId}` — dispatch-svc's standby binding, which is exactly "the driver has gone
  online in the app"; Mode A and Mode B publish regardless (US-3.22/3.23). The availability hash's
  `state` is deliberately **not** consulted: a driver mid-ride is not `AVAILABLE` and is emphatically
  online.
- **A refused sample is counted, not logged.** A parked Mode C three-wheeler's tracker reports all
  night; a log line per ping would be the loudest thing in the deployment.
- **The retained presence pair is guarded by `SessionRegistry.IsCurrent`.** An `offline` from a session
  that has already been replaced would overwrite the replacement's `online`, and the value is retained
  — the vehicle would read dark until its next reconnect. Across pods this cannot be checked at all,
  which is one more reason stickiness is a deployment property.
- **A codec is pure and per session.** It reads no cache, resolves no vehicle and reaches no network,
  which is what lets the golden tests assert a captured frame with nothing running. Per session because
  the JT/T 808 decoder remembers which header shape the device used and what terminal number to address
  a reply to.
- **The five downlink commands are a closed set.** GT06's command payload is an opaque ASCII string, so
  a pass-through would turn any publisher on `veh/+/cmd` into a device-configuration channel. Not every
  command is expressible on every protocol; a codec answers null and the router counts it as
  unsupported rather than pretending to have sent it.
- **A device that cannot be framed is closed.** Three unidentified frames, or a full buffer with no
  frame in it, ends the socket — otherwise a device talking the wrong protocol at the wrong port holds
  a slot in the pod's budget for the idle timeout.

## The four protocols, and what each cost

Every decoder detail that could be silently wrong is argued at its declaration. The ones worth knowing
about from outside:

- **GT06's CRC is CRC-16/X-25** — reflected 0x8408, init 0xFFFF, final XOR 0xFFFF, over the length byte
  through the serial. The documented login acknowledgement `78 78 05 01 00 01 D9 DC 0D 0A` is the one
  independently attestable fixed point in the format and `WireTests` pins it. Plain CRC-CCITT over the
  same bytes gives a different digest, and a decoder using it rejects every genuine frame.
- **GT06 acknowledges login, status and alarm — never a location frame.** The protocol does not ask for
  one and some firmware drops the session on an unexpected reply.
- **JT/T 808 has two header shapes** told apart by properties bit 14, and **the 2013 shape's six-byte
  BCD terminal number cannot hold an IMEI** (twelve digits versus fifteen). Such a device decodes fine
  and authenticates never — see the findings below.
- **JT/T 808 timestamps are Beijing time** (§8.18). `Adapter:Jt808DeviceUtcOffset` is the knob for a
  re-flashed unit; getting it wrong shifts every fix eight hours, which T-07's clock gate then refuses.
- **H02 speed is in knots.** Read as km/h it understates a coach by 1.85×, which is inside every ADD
  §12.6 threshold and would therefore never be caught downstream.
- **H02's ACC bit is inverted** (bit 10 of the status word, active low), which is what makes `FFFFFBFF`
  mean ignition-on.
- **Generic NMEA carries no device identity**, so the framing this service accepts is stated in
  `NmeaCodec` and nowhere else — `IMEI:…;`, `#…#`, or a bare digit string ahead of the first `$`.

## Live or replay (T-05)

JT/T 808 says so itself: `0x0704` is the bulk upload a device sends after a coverage gap, and every fix
in one is routed to `pos/replay` whatever its age. The other three families carry no such bit, so a fix
older than `Adapter:ReplayAge` (60 s) is treated as backlog. Getting it wrong in the safe direction — a
live sample routed to the backlog — costs it the bridge's 20/s pacing and nothing else.

## Sticky by IMEI, and the per-pod budget

Four facts live in this process's memory and none is recoverable from another pod: the open socket a
downlink writes to, the T-08 duplicate detection, the T-04 presence pair, and the JT/T 808 session
state. So stickiness is a property of the deployment, enforced by the **L4 balancer** — HAProxy's
`stick-table` in front of 5023-5025, or `sessionAffinity: ClientIP` on DOKS. `Adapter:ShardCount` turns
on the pod's own check that the balancer agrees; a device that hashes elsewhere is **served anyway and
logged**, because refusing it would turn a misconfiguration into an outage. The hash is FNV-1a and is
pinned by a test: `string.GetHashCode()` is randomised per process, so every pod would disagree.

`Adapter:MaxSockets` (10 000, ADD §7.7.6) is per **pod**, not per listener — the constraint is file
descriptors and the 512 MB D7' §2.1 gives Container 9, and both are per process.

## Configuration

Every knob is argued at its declaration in `AdapterOptions` and mirrored in
`infra/env/.env.app.example`. The ones that are not obvious:

| Setting | Default | Where it comes from |
|---|---|---|
| `Ports` | `5023,5024,5025,5026` | D6' §4.1's family order, **positional**; a CSV because an `env_file` is a flat map |
| `MaxSockets` | 10 000 | ADD §7.7.6, per pod |
| `RevalidateInterval` | 5 min | ADD §7.7.3, and the backstop for a T-12 signal never delivered |
| `RevocationCloseBudget` | 1 s | ADD §7.7.3's "within 1 s" |
| `OfflineWindow` | 5 s | **no spec pins it** — the deadline the T-04 publish must land inside |
| `ReplayAge` | 60 s | **no spec gives a number** — argued at its declaration |
| `IdleTimeout` | 15 min | **no spec** — five missed GT06 heartbeats |
| `Jt808DeviceUtcOffset` | +08:00 | JT/T 808 §8.18 |
| `PublishWhenModeUnknown` | **on** | argued at its declaration: closed takes every Mode A bus off the map on a Postgres blip |
| `RequireCredential` | **off** | three of the four protocols have no field for one |
| `VehicleProfileTtl` | 10 min | matches position-processor's `VehicleMetaTtl` |

`Adapter:ProvisioningBaseUrl` and `Adapter:ProvisioningInternalApiKey` — **unset means every device
whose cache entry is absent is refused**, which is the safe direction and completely silent from the
device's side, so it is an error-level line at start-up. The key must equal provisioning-svc's
`Provisioning:InternalApiKey`.

`Adapter:PskKeyDirectory` is provisioning-svc's `StepCa:RootKeyPath`; unset means a presented PSK token
cannot be verified locally and the adapter falls back to `validate`, which answers revocation but not
forgery.

`Adapter:TripStateBaseUrl` unset means ACC transitions are decoded and not reported, so
tracker-equipped Mode A/B vehicles never auto-start or auto-end on ignition (AL-32).

`Mqtt:SessionTokenSecret` must equal EMQX's `EMQX_AUTHENTICATION__1__SECRET` or every CONNECT is
refused. `ConnectionStrings:Redis` and `ConnectionStrings:Postgres` are both required — see the
micro-change-set below for why there is a database here at all.

## Micro-change-sets and findings (all recorded in the C043 handoff)

1. **D7' §2.1 gives Container 9 no database, and this service needs one.** T-11 needs
   `registry.vehicles.mode` and the canonical sample needs `vehicleType`/`fleetId`; neither exists
   anywhere else. `veh:meta:{vehicleId}` is written by position-processor *from accepted samples*, so
   reading the mode from it to decide whether to accept a sample is circular. One read-only PK lookup
   per device connect, cached — the same read-only cross-context window provisioning-svc opens.
2. **D7' §2.2's `runtime:10.0-alpine` cannot be used.** `MageRide.Shared` carries
   `FrameworkReference Microsoft.AspNetCore.App`, and backend/CLAUDE.md requires every service to
   reference it. The Dockerfile uses `aspnet:10.0-alpine` and says why.
3. **JT/T 808-2013's terminal number cannot express an IMEI.** Twelve BCD digits against
   `provisioning.yaml`'s `^\d{15}$`. Those devices are refused with the reason named; resolving it
   needs 2019-capable firmware or an alias index in provisioning-svc, and inventing a mapping here
   would authenticate a device against a guess.
4. **D6' §4.1 calls H02 "pipe-delimited" and the wire is comma-delimited.** Both are accepted.
5. **Generic UDP-NMEA shares `source = 4` with NMEA-over-MQTT.** `ck_positions_source` allows 0…4 and
   D6' §4.1 lists five families for five codes; coining a sixth needs a migration for a distinction no
   consumer reads.
6. **ADD §7.7.3's "pre-shared bearer" is not expressible on three of the four protocols.** Only
   JT/T 808's `0x0102` has a field for it. Hence `RequireCredential` defaults off.
7. **The generic-NMEA framing is this component's**, because no spec gives one.
8. **H02's inverted ACC bit is not in any document here** — it is what field-tested decoders for the
   family do, and the reading that makes a running engine report ignition-on.
9. **`POST /v1/internal/sessions/ignition` had no caller.** C031 landed it saying "the tracker plane
   decodes ACC out of a GT06/JT808 frame (`tcp-adapter`, C043) and had nowhere to say so"; this service
   is that caller. Not in C043's deliverable list.

## Not here, and named rather than stubbed

- **The `seq` dedupe, the anti-spoof filter and the plausibility gates** — position-processor-svc
  (C039). This service publishes what a device said; refusing what cannot be true is that one's job,
  and it applies the same rules to a handset.
- **The Timescale write** — persistence-writer-svc (C040), which also denormalises `fleetId` at write
  time (`mqtt-topics.md` §6).
- **The LWT *consumers*** (R-15/T-04) — trip-state-svc's auto-end, dispatch-svc's grace,
  fleet-health-svc's rollup. This service publishes the message; none of the three is its concern.
- **Live device health** (`lastSeen`, `signal`, `battery`, `sats` on `prov.tracker_bindings`) —
  fleet-health-svc (C044). The GT06 status byte carries a voltage level and a GSM signal strength and
  this service decodes neither: `sys/diag/{vehicleId}` is where they belong and C044 is what reads
  them.
- **Credential minting, binding and revocation** — provisioning-svc (C030). This service consumes
  `validate`, reports a clone, and honours a revocation.
- **The T-08 *resolution* screen** — admin-bff (C062).
- **The `0x0704` fragment reassembly** for JT/T 808 messages that set the fragmentation bit. The only
  message large enough to need it is a media upload, which this service does not consume, and a
  half-body would decode to a plausible-looking fix.
