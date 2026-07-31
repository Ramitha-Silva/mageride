# voip-svc (C055) — in-app "Free call", and nothing else

Stack: .NET 10 Minimal API + Dapper over Npgsql + Confluent.Kafka + LiveKit (SFU) and coturn (TURN).
References `MageRide.Shared` (C002). **No Redis, no outbox; Kafka in, nothing out.**

**Verify:** `dotnet test backend/src/Voip.Api.Tests -c Release`

`backend/contracts/voip.yaml` is normative for this surface and wins over this file and over the
code.

## What this service is

Three routes, one room per ride, and one decision: who may talk to whom.

| Endpoint | Auth | Spec |
|---|---|---|
| `POST /v1/voip/token` | Bearer (+ attestation at the edge) | D3' voip-svc, D-24, P-05 |
| `POST /v1/calls/start` | Bearer | D3' Δ 2026-07-05 #2 (AL-48) |
| `POST /v1/calls/{callId}/outcome` | Bearer | **Δ C055** — ADD §14/§16 had no way to see a failed call |

| Table | Read | Written |
|---|---|---|
| `rides.rides` | every request — four columns, and no phone number | **ride-svc** — read-only here |
| `comms.voip_sessions` | the teardown | **this service** — its first and only writer |
| `comms.call_log` | — | **this service** — its first and only writer |
| `comms.command_log` | the kernel's replay | notification-svc (C051) created it; shared |

## The three fences, and how each is held structurally

- **Number masking is withdrawn (AL-48), and it cannot come back by accident.**
  `MaskingWithdrawnTests` fails the suite if any type, member or parameter in this assembly is
  named after the removed stack (`masked`, `pstn`, `cpaas`, `did_pool`, `proxydid`, `smsrelay`, a
  CPaaS vendor) — because **three still-current documents describe masking** (D3' Δ 2026-06-28's
  `normal_masked` leg, D6' I-28.3's PSTN bridge, I-29.3's proxy-DID lease) and somebody
  implementing from the wrong section is a realistic way for it to return. AL-48 and D6' I-30.2 are
  later and win.
- **This service serves no phone number, and cannot name one.** The same test refuses `phone`,
  `msisdn`, `e164` and `dial_number` anywhere in the assembly, and the `rides.rides` projection
  selects four columns that are not `rider_phone_hash` or `recipient_phone`. "Normal call" is a
  client-side `tel:` dial of the number **ride-svc** carries on `GET /v1/rides/{id}` post-accept.
- **P-05: the room admits the driver and the RIDER. Never the booker.** Held by the projection, not
  by a branch: `RideParticipants.RiderIdentity` is `rider_id` on a proxy booking with **no fallback
  to `booker_id`**, and `PartyFor` returns nothing for anybody else. A proxy booker is answered
  `403` on both routes.

## Rules that are load-bearing

- **"Expiring at trip end" needs two mechanisms, because a token is a *join* credential.** LiveKit
  checks `exp` at connect and never again, so a short TTL alone would let a call that connected a
  minute before the ride ended run for as long as the two parties kept talking. What actually ends
  it is **the room being closed** when `ride.events` says the ride is over — which is also what
  makes an unexpired token in a handset's memory worthless. Minting is refused separately for a
  terminal ride. Both halves are asserted, the second against a real broker.
- **The teardown trigger is the ride's *state*, not the event name.** ride-svc publishes sixteen
  event types and ten of them are terminals; a consumer keyed on names would need all ten and would
  silently miss the eleventh. Every message on the topic carries the ride id as its key, so the
  handler re-reads `rides.rides.state` and asks the same question `CallService` asks when it refuses
  to mint. One rule, two callers.
- **The database row is closed whether or not LiveKit answered.** `ended_at` records that the *ride*
  ended, which is a fact about the ride; whether the SFU acknowledged is operational and is logged.
  The other order leaves a session open for ever every time LiveKit restarts.
- **`Completed` is not terminal, and that matters more here than almost anywhere.** The ride still
  owes a payment and the two people are standing next to each other — "my driver just left with my
  bag" is exactly the call this service carries, and a token refused at `Completed` would be refused
  in the ninety seconds it is most needed. ride-svc's own `RideStates` draws the same line.
- **One ride, one room, one open session.** `ux_voip_sessions_open_room` (migration 1311) is what
  makes that true rather than intended: the driver taps Call and the rider taps Call back, and
  without it each tap opens a rival session that the teardown would close only one of.
- **`direct_dial` starts nothing and is still logged.** There is no PSTN leg in this process. The
  client dialled the number ride-svc gave it and is telling us afterwards — best-effort by
  construction, which is why a missing row means nothing and a present one means only that somebody
  tapped the button. It is logged **even where LiveKit is absent**, because that is the deployment
  where the fallback rate matters most.
- **A parcel's sender or recipient cannot be reached in-app.** P-09 says they may have no account at
  all, so there is nobody to admit to a room; their Call button is a `tel:` link and always was.
  `free_voip` to those roles is `400`; `direct_dial` to them is fine.
- **A proxy ride whose rider never registered has no call at all.** P-03 keeps only a digest of that
  rider's number, so there is nobody to admit and — as ride-svc records from its own side — nobody
  to direct-dial either. The driver is refused rather than quietly connected to the booker. **AL-48
  and P-03 conflict in exactly this cell and P-03 wins.**
- **Before a driver accepts, there is nobody to call.** `400`, matching AL-48 withholding the
  counterparty's number until `Accepted` for the same reason. The apps only show the Call action
  post-accept, so this is a backstop.
- **A missing LiveKit is a `503`, not a `200` with an unusable token.** That is the VoIP-failure
  signal at its earliest and clearest point (ADD §14): the client puts up "Call normally instead?"
  and dials. A `200` would make an absent feature look like a flaky one.
- **The token grants microphone only, no data channel, and one room.** A camera track or a data
  channel would be a side channel through the platform's own media plane that nothing polices. The
  admin token used for teardown carries `roomAdmin` for one room and **no `roomJoin`** — it is a
  key to closing a conversation, never to hearing one.
- **The LiveKit token is minted by hand rather than through `JsonWebTokenHandler`.** Its authority
  lives in a nested object claim (`video`), and the claims-dictionary APIs stringify or flatten
  nested values depending on the path taken through them — producing a token that is accepted by
  nothing and fails at the SFU, minutes later, with a message about permissions. Sixty lines of
  Base64Url + HMAC is the whole format and is exactly testable.
- **An outcome is reported once, by the caller, and never overwritten.** A resumed app reporting
  again is `404`. Somebody else's call is also `404` — a call id is guessable and "that id exists"
  is itself something a stranger should not learn about two other people's conversation.
- **The ride is read directly from `rides.rides`, not fetched from ride-svc.** The platform's shape
  for a cross-context *read*, and the reason here is availability: an in-app call is what somebody
  reaches for when a driver cannot find them, and a hop through ride-svc would make a ride-svc
  outage into a calling outage on top of it. CLAUDE.md's outbox rule governs cross-service *state
  changes*; nothing read here is changed here.
- **Every switch-off is announced at start-up**, and here for its own reason: **a voip-svc with no
  LiveKit behind it looks exactly like one whose calls keep failing.** Every attempt answers `503`,
  every client shows the fallback, every user dials — and the feature reads as flaky rather than as
  absent.

## The media plane, and the one line of D6' §6 that shapes it

`infra/deploy/livekit/livekit.yaml` and `infra/deploy/coturn/turnserver.conf`, with both containers
declared `network_mode: host` in `infra/docker-compose.dev.yml`.

> "TURN media relay (coturn) on host UDP range (3478 + 50000–50100), **NOT via HAProxy/L7**
> (HAProxy cannot relay UDP)."

A TURN relay allocates a fresh UDP port per candidate and tells the peer to send to it; an L7 proxy
in front of that has nothing to proxy. **The failure mode of getting this wrong is not a startup
error — it is one-way audio, on the subset of calls whose ICE happened to need a relay.** What does
go through the proxy is the signalling websocket (7880) alone.

Two more things those files hold:

- **Recordings are off and there is no egress configuration at all** (ADD §6, PDPA). An egress that
  exists is an egress somebody can trigger; turning it on is a deliberate, audited change with a
  lawful basis behind it.
- **coturn denies loopback, link-local and every private range.** A TURN server with no denied peers
  is an open proxy into whatever network it sits in — an allocation pointed at `169.254.169.254`
  reads the cluster's metadata endpoint from the outside.

## Schema this service added

`db/migrations/1311__comms_voip_call_outcome.sql`, a micro-change-set recorded in the C055 handoff.
1302 landed both tables in their final post-AL-48 shape and neither had a writer until now.

| Object | Why |
|---|---|
| `ux_voip_sessions_open_room` | D3' gives a ride one room and **both** parties start a call into it; without this each tap opens a rival session and the teardown closes only one |
| `ck_call_log_outcome` | ADD §16's p95 call-setup SLO and ADD §14's fallback are both unmeasurable while a call that never connected looks like one that did. 1302 left the column free text with **no writer and no vocabulary**; `voip_failed` is the value the whole fallback hangs on |
| `ck_call_log_ended` | an outcome describes a call that finished. Without the pairing a row can claim `voip_failed` and still be open — exactly the row the SLO query counts |
| `ck_call_log_span` | 1302 guards the span on `voip_sessions` and leaves it off `call_log` |
| `ix_call_log_voip_failed` | the fallback-rate question the two CHECKs exist to answer, partial so the index is the size of the failures |

## Contract changes this component made

| Change | Why |
|---|---|
| `POST /v1/calls/{callId}/outcome` | **Δ C055.** ADD §14 documents a direct-dial fallback and ADD §16 a call-setup SLO; neither had a server-side artefact, and `comms.call_log.outcome` had no writer |

## Not here, and named rather than stubbed

- **The counterparty's phone number.** ride-svc's, on `GET /v1/rides/{id}` post-accept (AL-48). This
  service must never hold one, and a test enforces it.
- **The masked PSTN bridge, the proxy-DID lease and the D-25 masked-SMS relay.** Withdrawn by AL-48.
  Not deferred — removed.
- **`POST /public/track/{token}/call`.** Removed by AL-48; the web subview renders `driver.phone` as
  a plain `tel:` link (public-bff, C066).
- **Call recording.** Off by default (PDPA), and there is no egress configuration to turn on.
- **A `voip.*` topic.** This service publishes nothing: "a call started" is acted on by no service
  in this build, and the reader of the SLO is `comms.call_log`. D6' §2.1 gives voip-svc no topic.
- **The Dockerfile for voip-svc itself.** It is one of the 21 domain services inside `app-services`
  (D7' §2.1), which `infra/docker-compose.dev.yml` already routes `/v1/voip/**` to. The two
  containers this component *does* add are the media plane, which is not a MageRide service.
- **A LiveKit-side webhook.** LiveKit can post `room_finished`/`participant_left`, which would let
  `comms.voip_sessions.ended_at` reflect the SFU rather than the ride. No spec asks for it, and the
  ride ending is the fact this service is responsible for. Raised in the handoff.

## Configuration

Every knob is documented at its declaration in `VoipOptions` and in `infra/env/.env.app.example`.

| Setting | Default | Where it comes from |
|---|---|---|
| `LiveKit:WsUrl` · `ApiKey` · `ApiSecret` | unset | **unset ⇒ no call can be placed** — every attempt is `503` and every client falls back to a `tel:` dial |
| `LiveKit:ApiUrl` | unset | **unset ⇒ rooms are never torn down** and a call can outlive its ride (D6' §6) |
| `LiveKit:ApiTimeout` | 5 s | **no spec** — it runs on the consumer, where the uncommitted offset is the retry |
| `TokenTtl` | 5 min | **no spec** — D6' §6 says "expiring at trip end", which a join credential cannot know; see the rules above |
| `RoomTeardownEnabled` | on | **off ⇒ a call in progress outlives its ride**; only sensible with no broker |
| `ConsumerGroup` | `voip-svc` | D6' §2, "consumer group per service" |

`ConnectionStrings:Postgres` and `Jwt:*` are required; `Kafka:BootstrapServers` is required only
while `RoomTeardownEnabled` is on. `CommandLog:*` defaults to `comms` / `command_log` with no
aggregate-id column (set in `VoipApplication`, overridable). There is no `ConnectionStrings:Redis`
and no `Outbox:*`, and there must not be — see `VoipApplication` for why each is off.
