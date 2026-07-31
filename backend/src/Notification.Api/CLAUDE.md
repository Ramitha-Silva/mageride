# notification-svc (C051) — every push and every SMS the platform sends

Stack: .NET 10 Minimal API + Dapper over Npgsql + StackExchange.Redis + Confluent.Kafka.
References `MageRide.Shared` (C002). **Four topics in, nothing out — no outbox.**

**Verify:** `dotnet test backend/src/Notification.Api.Tests -c Release`

`backend/contracts/notification.yaml` is normative for this surface and wins over this file and
over the code.

## What this service is

The end of every fan-out on the platform. Two transports — FCM HTTP v1 / APNs HTTP/2 (D6' §7.4)
and Notify.lk with a Dialog/Mobitel secondary (D6' §7.3) — one durable queue, and one rule: **no
user-facing string is composed here.** Every body is rendered from
`content.notification_templates` in the recipient's own language (D-26).

| Endpoint | Auth | Spec |
|---|---|---|
| `POST /v1/notify/register-token` | Bearer | D3' notification-svc, D-27 |
| `PUT /v1/notify/preferences` | Bearer | D3' notification-svc, US-10.7 |
| `POST /v1/notify/ack` | Bearer | **Δ C051** — E-01 is unimplementable without it |
| `POST /v1/internal/notify/send` | internal | D3' notification-svc; `notificationType`, `phones`, `audience` are **Δ C051** |

| Topic | What it produces |
|---|---|
| `dispatch.events` | `offer.created` → `RIDE_OFFER` (E-01); `directional.expiring`/`.cleared` (DT-08/DT-04) |
| `ride.events` | the lifecycle pushes, `ride.settled` → `PAYMENT_CONFIRMED`, `location.request.issued` (P-02/AL-45), `package.picked_up`/`.delivered` (AL-21) |
| `wallet.events` | `wallet.low_balance` → `LOW_BALANCE`/`TOP_UP_REQUIRED`; `wallet.debited` with `kind='daily_fee'` → `DAILY_FEE` |
| `registry.events` | `vehicle.approved`, `document.review_required` (US-2.14), `document.expiring`/`.expired` (E-03) |

| Table | Read | Written |
|---|---|---|
| `comms.notifications` | the two workers | **this service** — the queue, the log and the E-01 fence |
| `comms.notification_tokens` | every push | **this service** |
| `comms.command_log` | the kernel's replay | the same |
| `safety.trip_share_tokens` | — | **this service mints**; revocation and metering are safety-svc's (C052) |
| `iam.users` | language, phone, preferences | **this service writes `notif_prefs` and nothing else** — see below |
| `rides.location_requests` | `request_id` → `id`, for AL-45's token | **ride-svc** — read-only here |
| `content.notification_templates` | every render, over HTTP | **content-svc** (C045) |

## The three fences, and how each is held structurally

- **A share token is minted server-side and SMSed. It is never returned to a client (AL-44/AL-45).**
  Held by the type system rather than by care: `IShareTokenMinter` returns a `MintedLink` whose only
  public member is the URL, there is no response shape in this assembly with a token-shaped field,
  and `A_share_token_never_leaves_through_an_api` checks every response this service can produce
  against the value that was just minted.
- **P-12's 5/hour and 30/day per booker are hard limits.** Redis token buckets
  (`RateLimitPolicies.LocationRequestHourly`/`Daily`, declared by C002 for this component), spent
  **after** the dedupe claim so a redelivered `location.request.issued` costs nothing. The subject
  is the **booker**, not the rider being pinged — a rider asked by five different bookers has done
  nothing, and `The_limit_is_per_booker_not_per_rider` asserts it.
- **No masked-number relay.** AL-48 removed D-25; nothing here dials, bridges or leases a number,
  and `comms.call_log` is not touched by this service at all.

## Rules that are load-bearing

- **E-01's "exactly once" is two database facts, not one worker's care.** The claim is
  `UPDATE … WHERE status = 'Sent' AND acked_at IS NULL AND ack_deadline_at <= now RETURNING` — the
  move to `FellBackToSms` happens in the same statement that selects the row, so two replicas
  sweeping one instant produce one claimed row between them. The SMS is then enqueued under
  `fallback:{pushId}`, which `ux_notifications_dedupe` makes unique, so a worker that crashed
  between the claim and the insert still sends one. Two guards, because a second SMS costs money and
  interrupts a driver and none costs them a fare.
- **A late ack changes nothing.** `TryAckAsync` is bound to `Sent`, so a handset that woke on the
  fourth second finds the row already fallen back and gets a `404`. The driver receives both
  messages once, which is the honest outcome of a slow device: an ack cannot un-send an SMS.
- **The offer push is silent, and that is what makes the ack possible.** A message carrying an FCM
  `notification` member is delivered to the system tray rather than to a backgrounded app, so the
  ack that stops the fallback would never be sent. `RIDE_OFFER` and `location_request` carry `data`
  only, and neither has a template — the app draws SCR-DA-013 and the P-02 prompt from its own
  resources.
- **Every refusal happens at enqueue; delivery only asks "did the gateway take it".** Preferences
  (US-10.7), the P-12 buckets and "is there anybody to send to" are resolved once, so a retry cannot
  re-ask a question whose answer has changed. A driver who mutes a type mid-flight still gets the
  message that was already accepted, which is the behaviour a queue can actually promise.
- **A refused notification is a row, not a silence.** `Suppressed` costs one insert and answers the
  support question this service otherwise cannot: without it, *muted* and *lost* look identical from
  the outside.
- **The dedupe key carries the recipient, because one event is routinely two notifications.**
  `ride.accepted` tells the booker and the rider (P-05); a key without the recipient would make the
  second look like a redelivery of the first and one of two people would hear nothing.
- **D-33 is a property of the type, not of the caller.** `SOS_TRIGGERED` is the one `DualGateway`
  entry in the catalogue, so nothing else can buy two messages by asking. The parallel send resolves
  on `Task.WhenAny` and does not wait for the loser; the straggler's result is observed on a
  continuation so a faulted task cannot surface later as an unobserved exception.
- **A missing template value fails the notification; a gateway failure retries it.** Most of D6'
  I-29.2's SMS templates carry `{{link}}`, and their recipients are the people with no app to find
  another way in — "Track it here: " is worse than nothing and would go out silently. The renderer
  throws, the row is `Failed`, and nothing is sent. A gateway that refused is a different thing and
  goes back on D-27's backoff.
- **The language is resolved at enqueue and stored on the row.** Re-reading it at delivery time
  would let a preference change mid-retry produce two attempts in two languages.
- **A dead device token is deleted, not retried.** FCM `404 UNREGISTERED` and APNs `410
  Unregistered` mean the handle will never work again; leaving it would fan every future offer out
  to it (the same reason 1302 carries `ux_notif_tokens_token`).
- **`notif_prefs` is one column with two documented writers, and that is the lesser evil.** D3' puts
  `PUT /v1/notify/preferences` here and iam-svc's own CLAUDE.md says the route "writes the same
  column". A `comms.notification_preferences` of this service's own would make `GET /v1/users/me`
  report switches that gate nothing. Both services apply the same safety-critical exclusion list
  (`SOS_TRIGGERED`, `SOS_RESOLVED`, `RIDE_CANCELLED`) — a type in one list and not the other is a
  mute that appears to work and does not, and `The_unmutable_set_is_exactly_the_three_iam_svc_drops`
  is the test.
- **Preference keys are data, not property names.** `MageRideJson` sets
  `DictionaryKeyPolicy = CamelCase`, which answers a request that muted `LOW_BALANCE` with
  `loW_BALANCE`; a client that sends back what it was given then has a key matching no type.
  `LiteralKeyDictionaryConverter` is applied to both directions of the wire shape and the column is
  written by hand. This was a real defect, caught by
  `A_preference_key_survives_the_round_trip_verbatim`.
- **The catalogue is data, and it is compared with the spec.** Nothing chooses a channel, a priority
  or a template key in a branch — every one is looked up in `NotificationCatalogue`, and
  `The_catalogue_invents_nothing_beyond_the_declared_additions` fails **both ways**, so an invented
  type is as loud as a dropped §14.4 row.
- **The delivery worker takes a lease, not a lock.** Each pass pushes `next_attempt_at` out and then
  talks to the gateways with no transaction open; a worker that dies mid-send leaves rows that
  become due again. The lease is derived from the slowest transport rather than configured, so an
  operator cannot set it below the push timeout and hand one row to two replicas.
- **No resilience pipeline on the two push channels.** D6' §8.3's retry is for an idempotent internal
  hop; E-01's whole budget is three seconds, and a retry inside the HTTP client would spend the
  window the SMS fallback exists to rescue. Retrying is the queue's job, on D-27's schedule, where it
  is visible on a row.
- **`recipient_phone` is stored in the clear, and the retention sweep is why that is acceptable.** A
  durable queue cannot re-derive a delivery address, and AL-21's and AL-45's recipients have no
  account to re-read one from. `Notification:Retention` deletes the row — a PDPA control (E-06) as
  much as housekeeping, which is why it deletes rather than nulling the column.
- **Every switch-off is announced at start-up**, and the log transports are *refused* outside
  Development unless asked for by name: this service's SMS bodies carry share tokens, which are
  credentials for an unauthenticated page, and its push bodies carry package delivery OTPs.

## Schema this service added

| Object | Why |
|---|---|
| `comms.notifications` (1308) | **No spec declares an outbound notification table.** §11's `comms` is three tables and D5' §14.4 is a matrix in prose. D-27's backoff has to survive a restart and E-01's fallback has to be exactly once; both are properties of a row |
| `ux_notifications_dedupe` | the producer's claim — what turns at-least-once event delivery (D6' §2.3) into one message |
| `ix_notifications_ack_due` · `ix_notifications_due` | the two sweeps, partial so each index is the size of the work rather than of the history |
| `comms.command_log` (1308) | R-14 replay per bounded context — the **tenth** time this micro-change-set has been raised (iam 0104, registry 0307, dispatch 0710, reputation 0803, content 1307, fares 1005 …). D4' §5 prints DDL for `rides.command_log` only |
| `notification_tokens.device_id` + `ux_notif_tokens_device` (1308) | `notification.yaml` has carried `deviceId` since C013 and 1302 had nowhere to put it. A reinstall arrives with a new token and the same install, and without this the old handle survives to receive every future offer |
| `notification_tokens.last_seen_at` (1308) | FCM and APNs retire a token at ~270 days; a queue that keeps pushing to one never drains. `updated_at` cannot serve — it moves on any column change |
| 1904's eighteen template keys | the rest of D5' §14.4, seeded beside the code that resolves them (content-svc's rule). 1902 deliberately stopped at the four the specs name by string |

`migrate-verify.sh` now expects **5** comms tables, not 3, and **22** template keys, not 4; it
carries a C051 section covering the dedupe claim, the ack-sweep index and four rejection checks.

## Not here, and named rather than stubbed

- **`safety.location_request_audit`.** ride-svc (C037) already writes all four decisions inside the
  transaction that changes the state, which is the only place the row can be correct. A second row
  here would double-count the abuse signal P-12 exists to surface. C052's deliverable names the
  table too; the split is that ride-svc records *outcomes* and this service holds the *outbound*
  limit.
- **Token revocation and access metering.** safety-svc's (C052, AL-44). This service writes the row
  and its expiry; burning a `pickup_confirm` token on use is public-bff's, per BR-29.1.
- **The AL-47 driver-QR prompt, its +5 min nudge, US-8.15's refund notice and the P-14 COD
  reminder.** fare-svc's `QrNudgeSweeper` identifies who should be nudged and logs it because it has
  no queue to write to; it now has one, and wiring it is a fare-svc change. No type and no template
  key was invented for them — a key nobody resolves is what 1902 refused to create.
- **`fleet.events`.** C044 emits `fleet.health_alert` keyed by fleet, and resolving a fleet to the
  people who should hear about it is a read this service should not invent. Not in the C051
  deliverable list; raised in the handoff.
- **`SCHEDULED_REMINDER`'s producer.** The type, the template and the send path exist; the 30-minute
  driver and 1 h + 15 min passenger timers (US-6A.15, US-10.9) are dispatch-svc's, which calls
  nothing yet.
- **The unregistered proxy rider's link.** `ride.accepted` carries `riderId` and no number, and P-03
  stores an unregistered rider's MSISDN as a digest and nowhere else — so AL-44's `proxy_rider` SMS
  can only be addressed to a *registered* rider today. Raised in the handoff.
- **A DLQ.** D6' §2.3's `<topic>.dlq` is still unowned; an unparseable envelope is committed past and
  a handler failure stalls its partition, which is loud rather than lossy.

## Configuration

Every knob is documented at its declaration in `NotificationOptions` and in
`infra/env/.env.app.example`. `Sms:*` is bound from the **same section iam-svc binds**, with the same
property names, so one set of environment variables configures both — D7' §4.2 declares the keys
once, and a deployment where the OTP and the SOS went out under different sender masks would be one
where half the messages are unrecognisable.

| Setting | Default | Where it comes from |
|---|---|---|
| `InternalApiKey` | unset | **unset ⇒ `/v1/internal/notify/**` is not mapped** — no SOS, no receipt, no announcement |
| `ContentBaseUrl` · `ContentInternalApiKey` | unset | **unset ⇒ nothing with a body is ever sent** (D-26) |
| `TemplateCacheTtl` | 300 s | matches content-svc's `Cache:Ttl`, whose definition of done is written against it |
| `PushProvider` | `log` | **refused outside Development** unless `AllowLogTransportOutsideDevelopment` |
| `Fcm*` / `Apns*` | unset | D6' §7.4's two transports |
| `PushTimeout` | 5 s | **no spec** — bounded by E-01's three seconds |
| `PushFanoutBatchSize` | 25 | **no spec** — HTTP v1 has no multicast, so a "batch" is N concurrent sends |
| `TokenStaleAfter` | 180 d | **no spec** — shorter than FCM/APNs' ~270 d retirement |
| `OfferAckWindow` | 3 s | E-01 / D6' §7.4 |
| `OfferAckSweepInterval` | 1 s | R-04's "≤ 1 s after expiry", same shape as ride-svc's timers |
| `OfferSmsFallbackEnabled` · `OfferAckSweepEnabled` | on | off ⇒ a sleeping handset simply misses the offer |
| `DeliveryEnabled` · `DeliveryInterval` · `DeliveryBatchSize` | on · 250 ms · 50 | D-27's worker |
| `MaxAttempts` · `BackoffBase` · `BackoffMax` | 5 · 5 s · 5 min | **no spec** — D-27 says "exponential" and names no ceiling |
| `Retention` · `RetentionSweep*` | 30 d · on · 6 h | **no spec** — also the E-06 control over `recipient_phone` |
| `WebTrackBaseUrl` | `passenger.mageride.lk/track?token=` | **unset ⇒ the three link SMS are refused**, not sent broken |
| `ShareTokenBytes` | 32 | **no spec** — 256 bits, the whole credential for an unauthenticated page |
| `PackageRecipientTokenTtl` · `PickupConfirmTokenTtl` · `ProxyRiderTokenTtl` | 4 h · 300 s · 12 h | D6' I-23.3 / AL-45 / I-29.2's "TTL = trip completion", which is not a duration |
| `LocationRequestLimitsEnabled` | on | P-12; the second of two gates, ride-svc holds the first |
| `ConsumersEnabled` · `ConsumerGroup` | on · `notification-svc` | **off ⇒ nothing is consumed** and every handset stays silent |
| `MaxRecipientsPerSend` · `MaxBroadcastRecipients` | 1 000 · 50 000 | the contract's `maxItems`; the broadcast cap has **no spec** and truncation is logged |
| `Sms:Provider` | `dev` | **refused outside Development** unless `Sms:AllowDevSenderOutsideDevelopment` |
| `Sms:SecondaryGateway` | unset | **unset ⇒ D-33's SOS has one gateway** and the p99 has nothing behind it |
| `Sms:MaxAttemptsPerGateway` · `RequestTimeout` | 2 · 4 s | D6' §7.3's "Retry: 2 attempts"; the timeout is bounded by D-33's five seconds |

`ConnectionStrings:Postgres`, `ConnectionStrings:Redis` and `Jwt:*` are required.
`Kafka:BootstrapServers` is required **only when `ConsumersEnabled` is on** — this service never
publishes. `CommandLog:*` defaults to `comms` / `command_log` with no aggregate-id column (set in
`NotificationApplication`, overridable). There is no `Outbox:*`, and there must not be — see
`NotificationApplication` for why.
