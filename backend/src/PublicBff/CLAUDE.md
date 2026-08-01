# public-bff (C066) — the no-login Passenger Web subview

Stack: .NET 10 Minimal API + Dapper over Npgsql + StackExchange.Redis. References
`MageRide.Shared` (C002). **No authentication scheme, no producer, no consumer, no outbox, no
command log** — see `PublicBffApplication` for why each is off.

**Verify:** `dotnet test backend/src/PublicBff.Tests -c Release`

`backend/contracts/public-bff.yaml` is normative for this surface and wins over this file and over
the code.

## What this service is

The six `SCR-WT` pages at `passenger.mageride.lk` (AL-44, AL-04), served to people with no MageRide
account: a package recipient, somebody else's passenger, and an unregistered rider being asked where
they are. **The `safety.trip_share_tokens` token in the path is the whole credential.**

| Endpoint | Scope | Screen | Spec |
|---|---|---|---|
| `GET /public/track/{token}` | any of the three | SCR-WT-001 → 002 / 003 / 004 | AL-44, US-25.2 |
| `GET /public/track/{token}/live` | any of the three | the map and the tracker | D6' I-29.1 |
| `POST /public/track/{token}/pickup/confirm` · `/decline` | `pickup_confirm` | SCR-WT-003 | AL-45, US-25.3 |
| `POST /public/track/{token}/sos` | `package_recipient` · `proxy_rider` | SCR-WT-004 | US-25.5, D-33 |
| `GET /public/track/{token}/receipt` | `package_recipient` · `proxy_rider` | SCR-WT-005 | US-25.6 |

| Table / key | Read | Written |
|---|---|---|
| `safety.trip_share_tokens` | the credential | **this service meters and burns**; notification-svc (C051) mints, safety-svc (C052) revokes |
| `rides.rides` · `rides.location_requests` · `rides.proof_artifacts` | every snapshot and receipt | **ride-svc** — read-only here |
| `registry.vehicles` · `registry.driver_profiles` · `iam.users` | the driver card | **registry-svc / iam-svc** — read-only here |
| `fares.ride_payments` | the receipt's figure | **fare-svc** — read-only here |
| `safety.sos_events` | — | **safety-svc**, over its internal plane |
| `veh:meta:{vehicleId}` (Redis) | the one position a page may draw | **position-processor-svc** |
| `package:delivery-code:{rideId}` (Redis) | SCR-WT-002's four digits | **notification-svc** (Δ C066) |
| `rate:public-track-{token,ip}:*` (Redis) | — | **this service** |

## The four fences, and how each is held structurally

- **No Bearer auth and no session.** There is no authentication scheme registered in this process at
  all — `UseAuthentication = false` — so a bearer could not be validated even if one arrived. What
  *is* registered is the kernel's authorization with its deny-by-default fallback, which is what
  makes the group's explicit `AllowAnonymous` a decision rather than a default.
  `PublicBffApplication.GuardTheSurface` refuses to start if any route asks for authorization, sits
  outside `/public/track`, or was mapped outside the group that carries the token gate; `FenceTests`
  asserts the same three against the running route table.
- **Responses are shaped strictly by token scope.** The scope is read off the row and dispatched on;
  no query parameter, no `Accept` negotiation and no request field selects a variant, so a
  `pickup_confirm` holder cannot obtain the package view by asking differently. **Each variant is a
  closed type with no field for what it may not carry**: `PackageSnapshotResponse` has nowhere to put
  the sender's number and `PublicFareResponse` has nowhere to put a payment instrument, so
  P-02/P-09's redaction survives a change to the projection. `SnapshotTests` asserts on the JSON
  rather than on the DTO — the half that matters is what is *absent*, and a test that deserialised
  into the DTO would say nothing about it.
- **A dead, expired or unknown token returns zero ride data.** The 404/410 is produced **before any
  ride row is read**: the gate looks the token up, meters it, and throws between that and the first
  read of `rides.rides`. `An_expired_token_answers_410_with_nothing_about_the_ride_in_the_body`
  asserts the body — the ride id, the driver's name, the plate, the state and the coordinates are all
  checked for absence, not the status code.
- **`POST /public/track/{token}/call` does not exist and cannot be re-added.** AL-48 removed the
  ride-scoped proxy-DID lease, the CPaaS bridge and the confirm-your-number step in full; the
  snapshot carries `driver.phone` and SCR-WT-002/004 dial it with a plain `tel:` link (US-26.3).
  Several pre-AL-48 spec lines still describe the lease, so the start-up guard refuses a route whose
  path contains `/call` **by name** — one string comparison standing between an earlier-dated
  document and a provider integration the platform deliberately does not have.

## Rules that are load-bearing

### The token gate

- **Both rate limits are applied before the token is looked up.** A token nobody ever issued costs a
  Redis round trip and no database work, which is what makes enumerating a 256-bit key space
  uninteresting. A value too short to have been minted is refused on shape before even that, so a
  probe cannot spend a real visitor's per-IP budget.
- **The per-IP bucket exists because a per-token limit is no limit against somebody holding a hundred
  harvested links.** Neither number is in a spec: D-34 gives 60/min per token and D3' asks for a
  per-IP companion without a figure. Both are held at the same values safety-svc holds them at,
  because the two surfaces are the same credential seen from two contracts and a page that polled
  harder than the share view would make the number depend on which endpoint was called.
- **The token is metered before the gate, not after.** The forensic value of `access_count` is
  precisely in the hits on a token that has already been revoked — somebody still holding a dead link
  is the pattern AL-44's metering exists to surface.
- **A `trip_view` token is refused as *unknown*.** D-34's share link is safety-svc's
  `GET /v1/trip-share/public/{token}`, with a different shape and a different redaction. Serving it
  here would answer a passenger's own link through a contract written for a package recipient, and
  saying "that belongs elsewhere" would make the route an oracle over which links are live.
- **The burn is this component's and the mint and the revoke are not.** notification-svc writes the
  row and its expiry; safety-svc closes every trip-scoped token when the trip ends. What is left is
  BR-29.1's single use, which happens when the token is *used* — and this is the only component a
  `pickup_confirm` token is ever presented to.

### What each scope may see

- **`senderNameMasked` carries a display name, and the field name is the misleading half.** The
  schema's own description says "the sender's display name only … the sender's phone number is never
  present in this scope", so the first name is what is emitted. There is no number on the type.
- **`driver.phone` is the real MSISDN, and that is AL-48 rather than an oversight.** The masking
  requirement was withdrawn in full (US-26.2/26.3); the page renders a `tel:` link the browser dials.
  `FenceTests` asserts the value equals the driver's own number and contains no mask characters,
  because "we removed the masking" and "we accidentally emit a masked value" look identical from a
  contract.
- **`cash_due` is the only thing a proxy rider is told about the money.** US-8.21's notice means the
  booker chose cash, so the fare is owed by whoever is in the car — which is the person reading the
  page. Every other method settles against the booker's instrument and **which** instrument is not
  said, because `PublicFareResponse` has no field that could say it (P-09).
- **A ride with no quote carries no fare block rather than a zero.** `Rs 0.00` on a tracking page
  reads as "this is free".
- **The `pickup_confirm` snapshot is the narrowest of the three and is meant to be.** A first name, a
  countdown and an optional pin. No ride, no driver, no vehicle, no position — a rider being asked
  for their live position is not owed an identity file on the person asking (P-02).
- **A stale position is omitted, never drawn.** The person watching is not in the vehicle and has no
  other way to tell that the marker stopped moving twenty minutes ago. The source is
  `veh:meta`, a hash holding exactly one fix — **reading a store that cannot answer "where has it
  been" is a stronger fence than remembering not to ask**, and it is the same one safety-svc's public
  view is built on (D-34 forbids historical replay).
- **The parcel's fourth step is derived from a departure.** ADD Appendix B.2 invariant 6 is literal —
  a package traverses the same eighteen states and adds none — so `PickedUp` and `InTransit` are one
  ride state and the fact that separates them is whether the vehicle has left the sender. With no
  position the answer is `PickedUp`: claiming the parcel is on its way with nothing to show for that
  would be a guess told as a fact.

### The live feed

- **The cursor describes what the client already knows; it indexes nothing.** There is no server-side
  buffer and there must not be — a replayable feed would be exactly the historical replay D-34
  forbids, reached through the back door. Resuming means "tell me what has changed since I knew
  this", answered from what is true now. Two consequences: a client disconnected for an hour resumes
  correctly and learns nothing about the hour, and **a replica that never saw the first connection
  resumes it as well as the one that served it**, so the stream survives a reconnect through a
  different pod with no shared state.
- **One diff function, two transports.** The failure mode of building SSE and the poll fallback
  separately is a page that behaves differently on a bad connection — which is the connection the
  fallback exists for. `PollAsync` is one evaluation of the diff and `StreamAsync` is the same
  evaluation on a timer.
- **The stream re-reads the token every tick, and that is the only thing that closes it.** A no-login
  page has no session to expire: without the re-read, a link revoked because the trip ended would keep
  feeding positions to whoever left the tab open.
- **A malformed cursor is answered, not refused.** A cursor mangled by a proxy is not something the
  page can act on, and the worst case of accepting it is one redundant frame.
- **A `pickup_confirm` feed can carry no position, as a branch that produces no such frame.**
  SCR-WT-003 is the screen on which nobody's location has been shared yet; a coordinate here would be
  the one this token was minted to *ask* for.

### The two writes, both of which are somebody else's

- **A BFF forwards; it does not become a second writer.** ride-svc built
  `/v1/internal/location-requests/{id}/confirm|decline` for this caller and says so at its own
  declaration; safety-svc's C052 handoff left the web SOS named rather than stubbed, naming
  public-bff as "the caller that does not exist yet". Writing either row here would give
  `rides.location_requests` two writers and put D-33's five-second SLO in two places.
- **The token is burned before the coordinate is forwarded.** That order is what makes BR-29.1's
  single use hold under a double tap — the loser of the burn never reaches ride-svc. It is also the
  safe order to fail in: a burn followed by an unreachable ride-svc costs the rider a retry through
  the app path, whereas the reverse leaves a live token on an answered request.
- **The decline carries no coordinate and no component on the path could add one.** The handler takes
  no body, the client sends no content, and ride-svc's statement has no `resolved_geo` in its `SET`
  list. P-02's fence is three properties of three components rather than one reviewer's care, and
  `A_declined_body_carrying_coordinates_still_stores_none` posts one anyway to prove it.
- **The 300 s deadline is the request row, and this service reads the same one ride-svc does.**
  `issued_at + ttl_seconds` is the durable fact (0609 (c)); a token whose own expiry is generous does
  not extend it, and a request already answered is over.
- **The booker's number is resolved inside safety-svc and this service never learns it.** public-bff
  sends a token and two coordinates and is told an id and an outcome. That is P-02/P-09 held by *where
  the column is read* rather than by a redaction step on the way out — and `RaisedWebSos` has no field
  that could carry a recipient.
- **`dispatchedAt` is nullable and `smsStatus` sits beside it**, the same micro-change-set C052 made
  on the app-side route. Without the status a caller cannot tell "the alert went out" from "the alert
  is on the admin console and nowhere else", and on this surface that is the difference between
  somebody having been told and nobody having been.
- **An unconfigured upstream is a 503 on a route that is still mapped and still gated.** A route that
  vanished with a setting is a route no fence test enumerates — and for the SOS the reason is
  sharper: an alert that goes nowhere must not answer 202, because it would look exactly like one
  that worked.

### Idempotency without a command log

- **Every write here is forwarded, so the key is forwarded too.** The caller's `Idempotency-Key`
  always wins. A BFF that minted a fresh key per call would defeat the dedupe of the two services it
  forwards to — and on the SOS route that means a double-tapped panic button sending two messages,
  which is the exact failure safety-svc's own file names.
- **When the page sends none, the key is derived from the business fact.** `pickup:{verb}:{token}` is
  stable for ever, because a location request can be answered exactly once and a retried tap should
  replay rather than read a refusal; `sos:{window}:{token}` is windowed, because a stable key would
  make a second genuine emergency twenty minutes later replay the first and send nobody anything.
  The token is hashed rather than embedded, so a key travelling to another service in a log line is
  not a credential.

### The receipt

- **Every value is derived and none is stored.** There is no receipt table and no `proof` column: the
  outcome is read off the ride's terminal, the settled payment attempt and the presence of a delivery
  photograph. A receipt reprinted a year later says the same thing as the one the recipient saw.
- **A dispute outranks the money and the money outranks the evidence.** P-14's terminal is the ride's
  own verdict and a receipt claiming a successful handover on a disputed delivery would contradict
  the ledger; a COD parcel that was both photographed and paid for reports the payment, because that
  is the question a receipt is opened to answer.
- **`Receiptable` is narrower than "terminal", deliberately.** D5' §6 has ten terminal states and six
  are cancellations and no-shows. A journey that did not happen has no receipt, and answering one
  with `proof: otp_verified` would record a handoff that never took place.
- **The settled attempt is chosen, not the last one.** D-10 makes a payment a chain of attempts, so a
  card that failed and fell back to cash has two rows and only one of them is what was paid.
- **The stored object pointer is never returned.** `rides.proof_artifacts.storage_url` is an `s3://`
  or `file://` key into D-36's bucket; the receipt carries a presigned URL or nothing at all. A
  deployment with no bucket presigns nothing and the field is absent, which is honest — the
  photograph genuinely is not reachable from a browser there.
- **No `driver.phone` on a receipt.** AL-48's `tel:` link exists so a recipient can reach a driver who
  is on the way to them. Once the parcel is delivered there is nothing to call about, and a receipt is
  a document that gets forwarded.

## Schema this service added

**None.** Every row this surface reads was landed by C004/C005/C037/C052, and the two it writes are
columns 0901 created for exactly this caller (`last_access_at`, `access_count`, `revoked_at`). The
one new *storage* is a Redis key, `package:delivery-code:{rideId}`, declared in the kernel's
`RedisKeys` and argued there.

## Spec gaps found, and what was done about each

| Gap | What was done |
|---|---|
| **`deliveryOtp` had no source.** ride-svc mints the plaintext at pickup and keeps only the digest ("in the clear for one hop", C037); notification-svc pushes it to a recipient with the app and deliberately leaves it out of the SMS for one without, because D6' I-23.3 has *this page* show it. Nothing was listening on the hop, so the unregistered recipient — the entire audience of SCR-WT-002 — could not learn their own code | notification-svc writes it to `RedisKeys.PackageDeliveryCode` in the same handler that mints the `package_recipient` token; this reads it back for the holder of that token, and only while the parcel is aboard. Redis rather than a column: the value expires with the delivery window whether or not anything clears it, reaches no backup, and a PDPA erasure has nothing to reach |
| **`startOtp` has no source and cannot have one.** `ride.yaml` and ride-svc's `RideContracts` both say a rider start OTP is "accepted and ignored in this build: no endpoint issues one", and `rides.rides`' two OTP columns are package-only | Omitted. Emitting four digits would mean inventing a code no driver could be asked to check |
| **`dropoff.addr` has no column.** `rides.rides` stores a `dropoff_geo` and no address — the drop-off is a pin the sender dropped | The object is omitted rather than filled with coordinates the schema has no field for |
| **`illegal-transition` is 400 and `public-bff.yaml` answers 409 on the receipt** | `receipt-not-ready` (409) coined in the kernel, `_shared.yaml` and the operation's `x-error-codes`. Moving the shared code's status would turn every one of ride-svc's illegal transitions into a 409; plain `conflict` says nothing, and the page needs to distinguish "come back when the trip ends" from any other conflict |
| **US-25.6's four `proof` values are a delivery's vocabulary applied to both kinds.** A proxy ride has no handoff artefact at all | `disputed` and `cod_collected` are genuine on a ride; `otp_verified` degenerates to "the journey ended the ordinary way", which the `state` beside it says precisely. Recorded rather than papered over |
| **The 202 on `sos` required `dispatchedAt`** | Nullable, with `smsStatus` beside it — the micro-change-set C052 already made on `POST /v1/sos`, applied to the second surface that raises one |

## Not here, and named rather than stubbed

- **Minting a token.** notification-svc's (C051), server-side and straight into an SMS. AL-44/AL-45
  are explicit that a token is never returned to a client, and there is no response shape in this
  assembly with a token-shaped field.
- **Revoking on trip end.** safety-svc's `POST /v1/internal/safety/trips/{tripId}/close`. The window
  is a fact about the trip rather than about who is looking at it.
- **D-34's `trip_view` page.** safety-svc's own public view, with its own shape.
- **Writing `safety.sos_events` or `rides.location_requests`.** Both forwarded; see above.
- **The admin live feed's consumer.** `sos.raised` is produced by safety-svc; `realtime/signalr-hub.md`
  still has no admin group and no `SosRaised` event. Unchanged by this component and still open
  (C041 / C065).
- **A SignalR client.** D6' I-29.1 describes this service as "subscribing to the same SignalR
  geocell/ride channels the apps use". What it actually needs is one vehicle's current position and
  one ride's state, and both are in stores this process already holds a connection to — a socket to
  fanout-svc would add a hop, a reconnect protocol and a second copy of the entitlement question for
  data it can read directly. Raised in the C066 handoff.
- **An ETA that follows a road.** ADD §7.6 puts routing (OSRM/Valhalla) in Phase 3. The estimate is a
  straight line with a detour factor, the same method and the same caveat as query-svc's
  `EtaEstimator` — deliberately duplicated rather than reached by a `ProjectReference` from a
  passenger-facing pod into another service. Promotion to the kernel is proposed in the handoff.
- **A Dockerfile.** `infra/docker-compose.dev.yml` already carries a `public-bff` cluster destination
  and `gateway-routes.json` forwards `/public/{**remainder}` to it.

## Configuration

Every knob is documented at its declaration in `PublicBffOptions`.

| Setting | Default | Where it comes from |
|---|---|---|
| `PerTokenPerMinute` | 60 | D-34, applied to the whole `/public/track` family by D3' Δ 2026-07-05 |
| `PerIpPerMinute` | 600 | **no spec gives a number** — ten tokens' worth, as safety-svc chose |
| `PositionMaxAge` | 2 min | **no spec** — US-7.17's staleness rule on the surface where a frozen marker misleads most |
| `PickupDepartureRadiusM` | 150 | **no spec** — the fact that separates `PickedUp` from `InTransit` |
| `EtaDetourFactor` · `EtaAssumedSpeedKph` · `MaxEta` | 1.35 · 22 kph · 90 min | US-7.11's method, stated; ADD §7.6 puts the real one in Phase 3 |
| `StreamMaxDuration` | 5 min | **no spec** — bounded so a revocation reaches somebody watching; the client reconnects with `?since` and loses nothing |
| `StreamPollInterval` | 2 s | inside D6' §5.1's 1–3 s band. A *read* interval: an unchanged position emits no frame |
| `StreamHeartbeat` | 15 s | **no spec** — a stationary vehicle produces no frames and an idle connection is reaped by intermediaries |
| `ProofPhotoUrlTtl` | 15 min | **no spec** — minted fresh on every receipt read |
| `SosDedupeWindow` | 30 s | **no spec** — the width of a double tap, for a page that sends no `Idempotency-Key` |
| `UpstreamTimeout` | 4 s | **no spec** — bounded by D-33's five seconds, not by D6' §8.3's 2 s |
| `TrustForwardedFor` | on | every request arrives through the C008 gateway (as iam-svc's and safety-svc's own flags) |
| `Ride:BaseUrl` + `:InternalApiKey` | — | AL-45's seam. **Unset ⇒ SCR-WT-003's Share and Decline answer 503** and an unregistered rider cannot answer at all |
| `Safety:BaseUrl` + `:InternalApiKey` | — | US-25.5's seam. **Unset ⇒ the web SOS answers 503.** No alert is recorded and nobody is SMSed |

`ConnectionStrings:Postgres` and `ConnectionStrings:Redis` are required through the kernel. There is
no `Jwt:*` section and there must not be; `Kafka:*`, `Outbox:*` and `CommandLog:*` are likewise
absent. `Storage:S3:*` is optional and only presigns the receipt's delivery photograph — this
service writes no bytes anywhere.
