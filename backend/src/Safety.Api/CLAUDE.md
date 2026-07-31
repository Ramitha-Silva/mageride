# safety-svc (C052) — SOS, trip sharing, vehicle reports, driver blocking

Stack: .NET 10 Minimal API + Dapper over Npgsql + StackExchange.Redis + a **gRPC client** for
reputation-svc. References `MageRide.Shared` (C002). **An outbox, no consumers.**

**Verify:** `dotnet test backend/src/Safety.Api.Tests -c Release`

`backend/contracts/safety.yaml` is normative for this surface and wins over this file and over the
code.

## What this service is

The four things a passenger or driver reaches for when something is wrong: the panic button, the
link they send somebody so it can be watched, the complaint they file afterwards, and the driver
they never want to see again.

| Endpoint | Auth | Spec |
|---|---|---|
| `POST /v1/sos` | Bearer | D3' safety-svc, D-33, AL-13; `smsStatus` is **Δ C052** |
| `GET /v1/sos/{userId}/history` | Bearer | D3' route table |
| `POST` · `DELETE /v1/trip-share/{tripId}` | Bearer | D-34 |
| `GET /v1/trip-share/public/{token}` | **none** | D-34 — the token is the credential |
| `POST /v1/reports/vehicle` | Bearer | US-12.5 |
| `POST` · `DELETE /v1/drivers/{driverId}/block` | Bearer | US-12.10 |
| `GET /v1/internal/safety/reports/queue` · `POST .../{reportId}/resolve` | internal | **Δ C052** — the moderation pair admin-bff forwards |
| `POST /v1/internal/safety/trips/{tripId}/close` | internal | **Δ C052** — D-34's "trip end + 1 h" |
| `GET /v1/internal/safety/location-requests/{bookerId}` | internal | **Δ C052** — P-12's forensic read |

| Table | Read | Written |
|---|---|---|
| `safety.sos_events` | the history route | **this service** |
| `safety.trip_share_tokens` | the public view | **this service** issues `trip_view` and revokes every scope; notification-svc (C051) mints the other three |
| `safety.vehicle_reports` · `blocked_drivers` | the queue, the tally | **this service** |
| `safety.location_request_audit` | the P-12 read | **ride-svc** (C037) — read-only here |
| `safety.outbox` · `command_log` | the kernel | **this service** |
| `iam.users` | AL-13's contact, the driver's name | **iam-svc** — read-only here |
| `rides.rides` · `trips.sessions` | what a share token points at | **ride-svc / trip-state-svc** — read-only here |
| `registry.vehicles` | the plate on the share view | **registry-svc** — read-only here |
| `veh:meta:{vehicleId}` (Redis) | the one position a link may show | **position-processor-svc** |
| `comms.notifications` | — | **notification-svc** — reached over HTTP, never read directly |
| `reputation.counters` | — | **reputation-svc** — reached over gRPC |

## The three fences, and how each is held structurally

- **SOS is a p99 ≤ 5 s SLO from button tap to SMS dispatched.** Held in two places. The alert goes
  to notification-svc's **inline** dispatch rather than its queue, so the budget does not depend on
  how many ride offers happen to be in front of it — D-33's five seconds cannot be a property of a
  worker's drain rate. And the measurement is on the row: `ts` is the tap, `dispatched_at` is the
  gateway, and the interval between them survives the request, so the SLO is queryable after an
  incident rather than reconstructed from logs.
- **A share token is trip-scoped, TTL-bounded, revocable and rate-limited — and there is no replay.**
  The no-replay half is the structural one: the public view reads `veh:meta`, a Redis hash that
  holds **one** position and overwrites it. The alternative source, `telemetry.positions`, is the
  full track — a query against it with the wrong `LIMIT`, or a later "add a trail to the map"
  change, would turn a share link into the replay D-34 forbids and no reviewer would necessarily
  notice. Reading a store that cannot answer the question beats remembering not to ask it. The
  return type has no field for a track either.
- **A dead token returns zero ride data.** The `410` is produced **before the ride is read at all**:
  the revoked/expired check sits between the token lookup and the trip lookup, so there is no code
  path on which a dead token could carry a position. `A_revoked_token_answers_410_with_nothing_about
  _the_trip_in_the_body` asserts the body, not the status code.

## Rules that are load-bearing

- **The order is record, announce, dispatch.** The `safety.sos_events` row and its `sos.raised`
  outbox event commit first, so an operator sees the alert whether or not a gateway takes it — the
  case where a human being is most needed is exactly the one a "send first, record if it worked"
  ordering would drop. The dispatch is another service's transaction and runs after the commit.
- **There is no switch that can skip the outbox row.** Only its *publication* is optional, and that
  is the kernel's own `Outbox:DispatcherEnabled`. A `Safety:OutboxEnabled` flag existed for one
  commit and was removed for exactly this reason.
- **`{{name}}` is the raiser, not the contact.** `sos_alert` reads "{{name}} has raised an SOS";
  rendering the contact's own name there would tell them they had raised it themselves. The two are
  separate fields on the same read, and the test asserts the contact's name is *absent*.
- **There is always a link, and an SOS with no ride gets a `geo:` URI.** `sos_alert` interpolates
  `{{link}}` and notification-svc's renderer correctly refuses to render a template with a value
  missing — so an empty link is a *refused SOS*. An alert raised while walking to the car has no
  trip to track and still has a position, because the contract requires one; RFC 5870 is what every
  phone hands to its own map app, and it needs no platform surface and no network to be useful.
  This was a real defect, found by `A_double_tapped_panic_button_under_one_key_sends_one_message`.
- **AL-13 is a lookup, not a join.** The contact comes from the two denormalised columns on
  `iam.users` that iam-svc re-derives inside every mutation of `iam.emergency_contacts` — its own
  CLAUDE.md names this budget as the reason they exist.
- **A double-tapped panic button under one key sends one message.** R-14, and the one route where it
  matters most: the first thing somebody does when nothing appears to happen is press it again.
- **Re-issuing a share link replays it.** Two live links for one trip would mean the passenger
  revoking "the" link and leaving another one open, which is the failure D-34's revocability exists
  to prevent.
- **Revoking is narrower than closing.** `DELETE /v1/trip-share/{tripId}` revokes only `trip_view` —
  the scope this service issues. Cancelling a *package recipient's* link because the sender tapped
  "stop sharing my trip" would strand somebody waiting for a parcel. The trip-*end* hook closes
  every trip-scoped scope, because that window is a fact about the trip rather than about who minted
  the link.
- **`pickup_confirm` is never closed by a trip.** It names a location request (0901's
  `ck_trip_share_tokens_subject`), the round-trip happens before the ride exists, and its own 300 s
  TTL is what ends it.
- **The token is metered before the gate, not after.** The forensic value of `access_count` is in
  the hits on a token that has already been revoked — somebody still holding a dead link is the
  pattern AL-44's metering exists to surface.
- **Both rate limits are applied before the token is looked up.** A token nobody ever issued costs a
  Redis round trip and no database work, which is what makes enumerating the key space
  uninteresting. The per-IP limit exists because a per-token limit alone is no limit against
  somebody who has harvested a hundred links.
- **A stale position is omitted, never drawn.** The person watching a shared link is not in the
  vehicle and has no other way to tell that the marker stopped moving twenty minutes ago.
- **`Completed` is not a terminal state.** C004's note (b) and ride-svc's own rule: the ride passes
  through it to `PaymentPending` in one transaction. Treating it as terminal would close a share
  link while the passenger is still in the car.
- **The third confirmation and the count that makes it the third are one atomic fact.** Both happen
  inside the same transaction; two moderators confirming the second and third report at the same
  instant would otherwise both read two and nothing would delist.
- **A report is resolved once.** A guarded `UPDATE … WHERE status = 'PENDING'`, so two moderators
  produce one decision and the loser is told it was already resolved rather than overwriting who
  decided it. `404` and `409` are told apart by reading the row.
- **A report names the driver at report time.** `reputation.counters` is keyed by *user* and a
  vehicle has an owner, not a driver; re-deriving the driver at confirmation time would answer
  differently once the vehicle changed hands.
- **The reputation hop cannot fail the caller.** The report is durable before it runs, and a 500
  would make the passenger file a second complaint. reputation-svc dedupes on `report_id`, so a
  replay counts once.
- **A block emits no event.** dispatch-svc reads `safety.blocked_drivers` directly in its candidate
  query (C023's `CandidateRepository`), so the row *is* the mechanism — an event announcing it would
  have no consumer that could act any sooner than the next dispatch round.
- **Unblocking something that was never blocked is a `404`.** A client that thinks it cleared a block
  would show the driver as available when nothing changed.
- **Every switch-off is announced at start-up**, and it matters more here than anywhere: **an SOS
  that goes nowhere looks exactly like one that worked.** The button animates, the row is written,
  the response is a 200, and nobody's phone rings.

## Schema this service added

| Object | Why |
|---|---|
| `safety.outbox` (0905) | D3' lists "admin live-feed WS" as a side effect of `POST /v1/sos` and `realtime/signalr-hub.md` has **no admin group and no SOS event** — its §6 lists what is deliberately not on the hub and an SOS is not among them. CLAUDE.md's universal rule settles the shape; the ninth topic outside D6' §2.1 |
| `safety.command_log` (0905) | R-14 per bounded context — the **eleventh** time this micro-change-set has been raised. Matters most on the SOS |
| `sos_events.dispatched_at` (0905) | §8 gives the row a `ts` and D3' answers `dispatchedAt`; those are two instants and **the gap between them is the D-33 SLO**. With one column every SOS looks instantaneous |
| `ck_sos_events_sms_status` (0905) | the three outcomes this service's vocabulary has. The gateway columns beside it stay free text: D6' §7.3 names two gateway families and a deployment may swap either |
| `vehicle_reports.driver_id` (0905) | `reputation.v1.proto`'s `VehicleReport` requires one and §8 has no way to name it. A vehicle has an owner, not a driver |
| `vehicle_reports.resolved_at` / `_by` / `resolution_note` + `ck_vehicle_reports_resolution` | US-12.6's third confirmation delists, so *when* and *by whom* is the evidence behind an appeal. §8 has a status and no way to say who moved it |
| `ix_vreports_confirmed` (0905) | the tally is over CONFIRMED rows; `ix_vreports_vehicle` covers every report and scans the dismissed ones too |
| `ux_vreports_reporter_ride` (0905) | one complaint per passenger per trip — three taps must not be the three confirmations that delist a vehicle |
| `ix_trip_share_tokens_trip_scope` (0905) | 0901's index answers "every live token for this trip" (the revocation query); issuing asks the narrower per-scope question, because a package delivery legitimately carries two live tokens at once |

`migrate-verify.sh` now expects **7** safety tables, not 5, and carries a C052 section: the two new
tables, the SLO column, the driver a report counts against, and five rejection checks.

## Not here, and named rather than stubbed

- **The admin live feed's consumer.** `sos.raised` is produced; `realtime/signalr-hub.md` has no
  admin group to deliver it on and no `SosRaised` event. Adding both is a fanout-svc (C041) and
  admin-bff (C065) change, raised in the C052 handoff.
- **The admin-facing report queue and resolve routes.** `admin-bff.yaml` declares them; this service
  exposes the internal pair behind them, so the decision stays where `reputation.v1.proto` says it
  lives.
- **`safety.location_request_audit`'s writes.** ride-svc's (C037), inside the transaction that
  resolves each request — the only place they can be correct. A second writer here would
  double-count every outcome.
- **The web SOS (`source='web'`).** AL-44/US-25.5's alert from an SCR-WT page carries a share token
  instead of an account; the column, the CHECK and `RaiseSosCommand.ShareToken` all admit it, and
  public-bff (C057) is the caller that does not exist yet.
- **`admin_acked_at`.** US-12.11's acknowledgement is an admin action on an admin screen (C065). The
  column is read and never written here.
- **The block `reason`.** `safety.blocked_drivers` has no column for one, US-12.10 asks for none, and
  inventing one would put a passenger's free-text opinion of a named driver in the database for ever.
  Accepted by the contract, read by nobody.

## Configuration

Every knob is documented at its declaration in `SafetyOptions`.

| Setting | Default | Where it comes from |
|---|---|---|
| `InternalApiKey` | unset | **unset ⇒ `/v1/internal/safety/**` is not mapped** — no report can be confirmed and no trip-end revocation can run |
| `NotificationBaseUrl` · `NotificationInternalApiKey` | unset | **unset ⇒ no SOS is ever dispatched.** The alert is still recorded and announced |
| `NotificationTimeout` | 4 s | bounded by D-33's five seconds, not by D6' §8.3's 2 s — the alert is *delivered* on that call |
| `RequireEmergencyContact` | on | D3's `400 no-emergency-contact`. Off records the alert with `sms_status = NoContact` |
| `ShareBaseUrl` | `passenger.mageride.lk/track?token=` | unset ⇒ `POST /v1/trip-share` is refused and an SOS SMS carries a `geo:` URI instead |
| `ShareGrace` | 1 h | D-34's "trip + 1 h" |
| `ShareMaxLifetime` | 12 h | **no spec** — D-34 pins the end to trip end, which is unknown while the trip runs. The ceiling for a ride that never reaches a terminal state |
| `ShareTokenBytes` | 32 | **no spec** — 256 bits, the whole credential for an unauthenticated page |
| `PublicViewPerMinute` | 60 | D-34 |
| `PublicViewPerMinutePerIp` | 600 | **no spec gives a number** — D3' asks for per-token *and* per-IP; ten tokens' worth |
| `PositionMaxAge` | 2 min | **no spec** — the US-7.17 rule applied to the surface where a stale marker misleads most |
| `ReputationGrpcAddress` · `ReputationInternalKey` · `ReputationTimeout` | `reputation-svc:5005` · unset · 2 s | D3' reputation-svc, D6' §8.3 |
| `ReputationReportingEnabled` | on | **off ⇒ no vehicle is ever auto-delisted**; the queue fills and the third confirmation does nothing |
| `ReportDelistThreshold` | 3 | US-12.6 / D5' §4.2. Held here *and* in reputation-svc — same number, two subjects |
| `MaxPageSize` | 50 | **no spec** — a bound on the history and queue reads |

`ConnectionStrings:Postgres`, `ConnectionStrings:Redis` and `Jwt:*` are required.
`Kafka:BootstrapServers` is required only when `Outbox:DispatcherEnabled` is on. `CommandLog:*`
defaults to `safety` / `command_log` with no aggregate-id column and `Outbox:*` to `safety` /
`outbox` / `safety_outbox` / `safety.events` (set in `SafetyApplication`, overridable).
