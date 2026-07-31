# subscription-svc (C047 daily fee + C048 Mode B subscriptions)

Stack: .NET 10 Minimal API + Dapper over Npgsql + Confluent.Kafka. References `MageRide.Shared`
(C002). **No Redis**, and **Kafka and the outbox exist for Epic 23 and for nothing else** — see
"Rules that are load-bearing" for why each is absent or present.

**Verify:** `dotnet test backend/src/Subscription.Api.Tests -c Release`

`backend/contracts/subscription.yaml` is normative for this surface and wins over this file and
over the code.

## What this service is

**Two flows that share a name and nothing else.**

**C047 — the rule, not the money.** It decides *whether* a driver owes the daily platform fee and
*how much*; the movement is a call to wallet-svc's ledger seam. `billing.journal_postings` keeps
exactly one writer (D-09) and it is C046.

**C048 — the money, and none of it ours.** Epic 23's per-vehicle access requests, the subscriptions
an accept starts, and the monthly fare a passenger pays **to the fleet owner**. MageRide holds none
of it, takes no cut and writes no ledger entry for it (§18b). `billing.monthly_subscriptions` is the
platform's own per-Mode-B-vehicle charge *to* the fleet and is the ledgered one; the two never net
against each other, and the schema gives you no column tempting you to try.

| Endpoint | Auth | Spec |
|---|---|---|
| `GET /v1/fees/rates` | Bearer | US-14.4, ADD §19 — the seven tiers |
| `GET /v1/fees/{driverId}/today` | Bearer | US-9.1, US-9.7 |
| `GET /v1/fees/{driverId}/history` | Bearer | US-9A.6 |
| `POST /v1/fees/{driverId}/refund-requests` · `GET` | Bearer (driver) | **Δ C047** — US-9.23 |
| `POST /v1/internal/fees/{driverId}/charge-before-trip` | internal | D-08/D-13 — ride-svc's gate |
| `POST /v1/internal/fees/mode-b/run` | internal | **Δ C047** — AL-03 |
| `PUT /v1/admin/fees/rates` | finance · admin | US-14.4, SCR-AP-007 |
| `PUT`/`GET /v1/admin/voucher-discount-tiers` | finance · admin | US-9A.15, AL-01 |
| `GET /v1/admin/fees/mode-b/charges` | finance · admin | **Δ C047** — the C060 hand-off |
| `POST /v1/vouchers/purchase` · `/v1/transfers/driver` · `/v1/subscriptions/credit-transfer/**` | Bearer (driver) | forwarded to wallet-svc |

### Epic 23 (Δ C048) — `/v1/mode-b/**`

| Endpoint | Who | Spec |
|---|---|---|
| `POST`/`GET /v1/mode-b/{vehicleId}/access-requests` | passenger / manage | US-4.9, US-23.1, AL-23 |
| `POST /v1/mode-b/access-requests/{id}/accept` · `/reject` | manage | US-23.1, BR-23.7 |
| `GET /v1/mode-b/subscriptions/{passengerId}` | the passenger themselves | SCR-PA-025 |
| `POST /v1/mode-b/subscriptions/{id}/unsubscribe` | the passenger themselves | US-23.11, BR-23.11, D-22 |
| `POST /v1/mode-b/subscriptions/{id}/pay` | the passenger themselves | US-23.3, AL-49 |
| `GET /v1/mode-b/subscriptions/{id}/payments` | the passenger themselves | US-23.9, SCR-PA-025b |
| `POST /v1/mode-b/payments/{id}/transfer-slip` | the passenger themselves | US-23.4 |
| `POST /v1/mode-b/payments/{id}/confirm` | **own** | US-23.4, item 16f |
| `POST /v1/mode-b/pay/onepay/webhook` · `/lankaqr/confirm` | HMAC | R-19, D6' §7.1 |
| `GET /v1/mode-b/{vehicleId}/subscribers` | manage | US-23.12, item 16 |
| `DELETE /v1/mode-b/{vehicleId}/subscribers/{id}` | **own** | US-4.12, AL-25 |
| `PUT /v1/mode-b/{vehicleId}/subscribers/{id}/fare` | **own** | US-23.7 |
| `POST /v1/mode-b/{vehicleId}/subscribers/{id}/mark-cash` | **own** | US-23.6 |
| `GET /v1/mode-b/{vehicleId}/subscribers/{id}/payments` | **own** | US-23.10, SCR-FP-012 |
| `GET /v1/mode-b/files/{kind}/{id}` | the signature | **Δ C048** — AL-49's "signed URL" |

**manage** = the vehicle's owner, the org's Owner or Manager, or the driver it is assigned to
(US-23.1's "Owner/Manager … the same accept/reject is available to the assigned driver").
**own** = the vehicle's owner or the org's Owner, and nobody else — US-23.6 is explicit that "only
the fleet Owner can mark it received", and the fare override and the hard delete are the same hands.
Neither is ever a role claim: `fleet_owner` says somebody runs *a* fleet, not *this* vehicle, so
both are resolved against the vehicle by the query that fetched it.

| Table | Read | Written |
|---|---|---|
| `billing.plans` | every charge, the rates screen | admin PUT here |
| `billing.daily_fee_charges` | today, history | **this service only** |
| `billing.monthly_subscriptions` | the Mode B run and its view | **this service only** |
| `billing.voucher_discount_tiers` | the ladder | admin PUT here **and** wallet-svc's (C046) |
| `billing.journal_*` · `accounts` | — | **wallet-svc** — never touched here |
| `billing.fleet_invoices` | — | **fleet-billing-svc** (C060) |
| `dispatch.offers` | the Colombo-day trip count | **dispatch-svc** — read-only here |
| `registry.vehicles` · `driver_profiles` · `fleet_vehicles` · `fleets` · `fleet_assignments` · `fleet_payout_profiles` | type, mode, live vehicle, roster, authority, `payTo` | **registry-svc** / fleet-svc — read-only here |
| `iam.users` · `fleet_members` | the queue's names, the org sub-role | **iam-svc** — read-only here |
| `docs.uploads` | the owner's LankaQR image pointer | **the upload surface** — read-only here |
| `subscription.access_requests` · `grants` · `subscriptions` · `payments` | Epic 23 | **this service** (and registry-svc's two roster routes, argued below) |
| `support.tickets` | the driver's own claims | **support-svc** (C053) — one row written here, argued below |
| `subscription.command_log` · `outbox` | the kernel's replay, the D-22 event | this service |

## The C048 fences, and how each is held structurally

- **Access requests and grants are PER VEHICLE, never account-global (AL-23).** Held by shape:
  `IModeBAccessRepository` exposes no method that takes a fleet, an owner or an account, so there is
  no query in which an account-global grant could be written or read. A driver with three vehicles
  works three queues, and `ux_grant_active` is keyed `(vehicle_id, passenger_id)`.
- **Subscription money is a pass-through to the fleet owner. MageRide holds none of it and takes no
  cut.** `ModeBPaymentService` contains no call to wallet-svc, no journal entry, no account and no
  percentage — and migration 1202 gives `subscription.payments` **no column** that could hold a
  posting id or a commission, which `migrate-verify.sh` asserts. The absence is the mechanism.
- **`payTo` comes only from a VERIFIED payout profile (AL-49).** `ux_payout_profile_verified` makes
  "the verified row" singular; the table is versioned, so an owner's later edit lands as a *new*
  `pending_verification` row and collection continues against the last verified snapshot, never
  against an unverified edit. No profile — including a vehicle that belongs to no org at all — is
  `409 payout-profile-not-verified` on **every** method, because D5' §802 says a Paid subscription
  "cannot start billing" and that is a statement about collecting, not about one rail.
- **An unsubscribed row stays MUTED until the owner hard-deletes it (AL-25).** `ux_grant_active` is
  partial on `deleted_at IS NULL`, not on `status`, so the unsubscribed grant keeps the (vehicle,
  passenger) slot. That single index is what makes three requirements true at once: the roster keeps
  showing who left, the owner's `DELETE` is the only thing that frees the pair, and a rejoin *reuses*
  the row rather than colliding with it.

## The three C047 fences, and how each is held structurally

- **There is no per-trip fee and no commission. Ever.** The only money this service can move is one
  `daily_fee` debit per (driver, vehicle, Colombo day), and the only way it can move it is
  `POST /v1/internal/wallet/{driverId}/debit`, whose `kind` whitelist admits nothing else. A
  commission has no journal kind to be recorded under — AL-01 removed `reseller_commission` from
  `ck_journal_entries_kind`, so the database refuses one whoever asks.
- **The first trip of each Asia/Colombo day is always free.** `DailyFeeRule.Decide` answers the
  waiver *before* the rate, the balance or the existing row is consulted, which is US-9.1's "no
  wallet check" as control flow. The count is per **driver**, not per vehicle, so switching vehicles
  cannot buy a second free trip; and it excludes the ride being accepted, without which the first
  trip of every day arrives as `tripsToday = 1` and is charged.
- **Mode A pays nothing.** Held twice, independently. `PUT /v1/admin/fees/rates` refuses a non-zero
  rate for a Mode A type, so the number cannot be written; and `DailyFeeService.RateForAsync`
  returns zero for `mode = 'A'` whatever `billing.plans` says, so it cannot be read. A zero rate
  writes no journal entry and burns no idempotency key — nothing is charged, rather than zero being
  charged.

## Rules that are load-bearing

- **Debit first, record second.** The two systems are not in one transaction, so the order is the
  whole of the crash-safety argument. Debit-then-record leaves, at worst, money taken and no row —
  and the retry re-sends the same ledger key, gets `replayed: true` for the same entry, and writes
  the row it owed. Record-then-debit leaves a driver marked `PAID` who paid nothing, which no retry
  ever repairs because the row now says the day is settled.
- **Two guards make the charge single-shot, and they guard different things.**
  `billing.daily_fee_charges`'s composite primary key stops this service writing a second *row*;
  wallet-svc's UNIQUE `billing.journal_entries.idempotency_key` stops the *money* moving twice. The
  second is the load-bearing half, because two replicas can decide to charge at the same instant and
  nothing serialises the decision.
- **The ledger key is the business fact, spelled exactly.**
  `daily_fee:{driverId}:{vehicleId}:{feeDate}` — C005 decision 4, 1101's and 1107's headers, and
  `DailyFeeRule.LedgerKey`. It is a cross-service contract with a unit test that asserts the literal
  string, because a well-meaning reformat of it would silently start taking a second fee.
- **`already_charged` is `status = 'PAID'`, never "a row exists".** A `WAIVED_FIRST_TRIP` row means
  the driver has had their free trip on this vehicle today and still owes the day's fee; the row is
  upgraded in place on the second trip. Reading it as "a row exists" would make every driver's whole
  day free.
- **A paid day is final.** The upsert carries `WHERE status <> 'PAID'`, so a late redelivery of the
  first trip's waiver cannot overwrite a paid row with a zero and leave the money and the record
  disagreeing.
- **The trip count comes from `dispatch.offers`, and that is not a free choice.** dispatch-svc's
  D-08 pre-dispatch gate exists to predict *this* charge — it withholds an offer from a driver who
  could not pay for it — and it counts `status = 'ACCEPTED'` offers responded to on the Colombo day.
  Counting anything else here would make the gate mispredict its own subject: it would withhold an
  offer over a fee that would have been waived, or offer a trip whose accept then fails with a
  `402`. One number, read the same way in both services. A ride cancelled *after* an accept still
  counts — 0712 records the end of an accepted offer's liveness in `released_at` and leaves the
  status alone — which is what stops the free trip being farmed.
- **The Colombo day is converted to a UTC range, not the column to a local date.**
  `responded_at AT TIME ZONE 'Asia/Colombo' = …` is a function over the column and cannot use
  `ix_offers_driver_responded` (migration 0713); `BusinessCalendar.DayRange` selects the same rows
  and keeps the index. This query is on the accept path, inside D-08's budget.
- **A `402` from wallet-svc is not a failure to retry.** It is the D-08 gate's own answer arriving
  late, and it is carried through with the code the driver's app already branches on (US-9.1) rather
  than reshaped into a 503, which would look like an outage they could wait out instead of a balance
  they have to top up.
- **A vehicle type with no configured rate is refused, not guessed.** §20 seeds none for `truck` or
  `mini_truck` deliberately: a delivery vehicle cannot go online until Finance decides what it
  costs. Inventing a rate would bill a driver a number nobody chose.
- **Retry is on the ledger seam and not on the proxy.** The debit is idempotent by construction, so
  D6' §8.3's pipeline is safe there — with this service's own 2 s attempt timeout rather than the
  15 s default, because the whole sequence runs inside a 15 s offer window. The forwarded routes get
  no pipeline: a proxy must not invent retries its caller did not ask for.
- **The forwarder keys on seekability, not on `Content-Length`.** A chunked request carries no
  `Content-Length` — which is exactly what .NET's own `JsonContent` sends — so keying on the header
  drops the body of every call made by a .NET client and hands wallet-svc an empty object. What
  makes the stream seekable is the idempotency middleware's `EnableBuffering`.
- **The forwarded routes carry the caller's bearer, never a service credential.** wallet-svc scopes
  every one of those operations to the token's subject — a transfer that is not the caller's is a
  404 there — so forwarding the driver's own token keeps that check where it is and means this hop
  can grant nothing the driver did not already have.
- **A rate change reaches the next charge and never a past one.** There is no code path that
  revisits a `billing.daily_fee_charges` row: the charge path reads `billing.plans` at the moment it
  charges and records the amount it actually took. "No retro-billing" is a property of what is
  absent.
- **The admin PUTs upsert what they were sent and un-configure nothing.** A `PUT` that deleted the
  rows it was not given would let a Config screen rendering six of the eight tiers silently
  un-configure the other two — and an un-configured type cannot go online at all. A rung is
  withdrawn by sending it `active: false`, which is a decision rather than a consequence of what a
  form posted.
- **A vehicle type outside AL-09's vocabulary is refused.** `billing.plans.vehicle_type` is a bare
  `TEXT` primary key with no CHECK, so without the check in the endpoint an admin could configure a
  rate for `car` — a row that looks configured, matches no vehicle ever, and leaves the type it was
  meant for unable to go online. There is no `car`: it maps to `sedan`.
- **A `{driverId}` in the path is checked against the token in one place.** `SubjectScope.Require`
  admits the driver and the six back-office roles (Finance answers fee disputes from the Admin
  Portal); `SubjectScope.RequireSelf` admits only the driver, and the refund intake uses it — a
  back-office role that could raise a claim in a driver's name would put words in their mouth on a
  queue that ends in a wallet credit. A malformed id is `403`, not `400`: whatever it was, it was
  not the caller's.
- **The history cursor is the `(feeDate, vehicleId)` pair, not the date.** A driver who used two
  vehicles in one Colombo day has two rows sharing a date, and a date-only cursor would drop
  whichever straddled a page boundary — silently, and only for the drivers US-9.6 exists for.
- **Money columns are cast to `bigint` in the SQL.** `daily_fee_minor` and `amount_minor` are
  `INTEGER` in §10 while every contract types money as int64. Dapper's constructor binding matches
  parameter types *exactly*, so an `Int32` column against an `Int64` parameter does not fail to
  convert — it fails to materialise the record at all.
- **The Mode B runner is an interval, not a monthly alarm.** The run is an idempotent upsert, so
  re-running costs one statement and catches a vehicle approved on the 9th, a deployment that was
  rolling at midnight on the 1st, and a clock that moved. A monthly alarm gets exactly one attempt
  per month to be running, and the failure mode is a month nobody is billed for. Every replica runs
  it; `ON CONFLICT DO NOTHING` is the arbiter, so a lease would be a lock protecting an operation
  that is already idempotent.
- **"First month free" is anchored to the vehicle's registration month.** 1104 expects the FREE row
  to be "seeded per vehicle at registration", but registry-svc creates no billing row and knows
  nothing about money. Deriving it from `created_at` gets the same answer whenever billing is first
  switched on; anchoring it to "the first row this run creates" would hand a free month to every
  vehicle already on the platform the day the runner is deployed.
- **No Redis.** The D-08 balance cache belongs to dispatch-svc (reader) and wallet-svc (writer).
  This service never reads a balance — it asks wallet-svc to move money and is told `402` if there
  is not enough. A cache read here would be a third opinion about one number.
- **Kafka and the outbox exist for D-22 and for nothing else.** The daily fee still publishes
  nothing and must not: its event is `wallet.debited`, which wallet-svc emits inside the transaction
  that posts the money (R-13), and an event published from here would describe a movement this
  service did not make and could not roll back. The Mode B *platform* charge hand-off to C060 is a
  table it reads, not a message it waits for. What does need a broker is the unsubscribe — see the
  next three bullets. `Subscription:ModeBSubscriptionsEnabled=false` takes both back off, and the
  service is then exactly C047's.
- **The revocation is written inside the transaction that mutes the grant.** BR-23.11 gives D-22 a
  200 ms budget to reach the passenger's socket. Publishing *after* the commit leaves a passenger
  watching a vehicle they have left when the publish fails — the exact leak D-22 exists to close —
  and publishing *before* it revokes somebody whose unsubscribe then rolls back. So the grant, the
  cancellation and the `subscription.outbox` row commit together (R-13), and the same is true of the
  accept's `share.granted`.
- **The event goes on `registry.events`, keyed by vehicle, in registry-svc's exact envelope.**
  fanout-svc (C041) reads `vehicleId` and `passengerId` off the payload and the type off a Kafka
  header, and **skips any share event that names no passenger** — permanently, without stalling the
  partition. An envelope of our own invention would therefore be dropped silently, and only for the
  passengers this component exists to revoke. A topic of our own would be worse: two topics cannot
  order an accept against the unsubscribe before it, and because a rejoin *reuses* the grant row,
  keying by grant would not separate them either. Second producer, same topic, same partition key.
- **A rejoin reuses the grant and starts a new subscription, and both halves are forced.** The grant
  is reused because `ux_grant_active` holds the slot until the owner deletes it; the subscription is
  new because `ux_subscriptions_grant_live` admits one non-cancelled row per grant and the old one
  was cancelled. That is also why the passenger's card list filters on **both** the grant being live
  and the subscription not being cancelled — the grant alone would bring the ended subscription back
  beside the new one, one extra card per rejoin. (Found by `Rejoining_needs_a_fresh_request…`.)
- **The whole accept is idempotent without a second key.** The request decision is guarded on
  `status = 'pending'`, the grant is an upsert and the subscription is `ON CONFLICT DO NOTHING` onto
  the live-per-grant index. Two owners tapping Accept on two devices leave one grant, one
  subscription and one `share.granted`.
- **"Joined 5 June ⇒ due 6 July" is the example, and the example wins.** BR-23.9's prose says
  `join_date + 1 month` and its own worked example says the day after; D4' §18b, D5' and this
  component's DoD all print the example. It is also the only reading the money supports — the fare
  paid on the 5th buys the month up to and including the 5th of the next, so a due date on the 5th
  charges for a day already paid for.
- **The anniversary is re-derived from `join_day`, never advanced from the previous due date.** A
  subscriber who joined on the 31st has no anniversary in February; `AddMonths` clamps to the 28th,
  and advancing from *that* would move them to the 28th for ever. That is what the `join_day` column
  is for, and `ck_subscriptions_join_day` makes it non-optional for the cycle that needs it.
- **The due date moves only for the settlement that actually happened.** Every path — the owner's
  confirm, mark-cash, and both gateway callbacks — advances it from the row returned by a *guarded*
  `UPDATE`, so a redelivered callback finds the month already `paid`, gets no row, and does not
  advance a second time. A double advance is a free month, which is the one arithmetic error here
  that costs the fleet owner money.
- **The roster's "this month" is the nearest outstanding period, not `date_trunc(now())`.** A
  `join_anniversary` subscriber's *first* period is next month by construction, so an equality on
  the current month would show the owner "unpaid" against somebody who owes nothing yet. The
  subquery takes the earliest live payment from the current Colombo month forward, which reads as
  "paid" while the month it covers is current and drops out of view once it has passed.
- **Cash is not a method a passenger may choose.** `POST …/pay` refuses it outright: US-23.6 hands
  cash to a collector and only the owner may say it arrived, so a passenger who could record one
  would be marking their own month paid.
- **An unclassified Mode B vehicle is Free; a Paid one with no default fare is refused.**
  `registry.vehicles.mode_b_billing` is nullable and US-13.1b captures it at onboarding, so a NULL is
  a vehicle onboarded before the setting existed — reading it as Paid would start charging subscribers
  of a vehicle whose owner never named a price. The opposite mistake cannot be papered over:
  `ck_subscriptions_fare` refuses a Paid row with no fare, and inventing a number would bill a
  passenger an amount nobody chose.
- **AL-49 gates collecting, not joining.** A Paid vehicle whose org has no verified profile still
  accepts passengers; it just cannot take their money (`409` on `pay`). Blocking the *accept* would
  deny a child a seat on a school van over the owner's bank paperwork, and the classification gate
  that should have prevented the situation is fleet-svc's `PUT …/classification` (BR-31.1).
- **The signed document links carry the kind inside the signature.** Without it a link to a
  passenger's transfer slip could be re-pointed at a payout profile by editing one path segment, and
  both are somebody's private document. A bad signature and an expired one answer identically.
- **There is a command log, and it is for one route.** `POST /v1/fees/{driverId}/refund-requests`
  is the only surface here whose repetition would double an effect — a second identical claim on the
  Support queue, with no natural key to collide. The two internal fee routes opt out individually:
  their key is the Colombo day and the Colombo month, both stronger than a header, which dedupes
  identical *requests* rather than identical *days*.
- **Every switch-off is announced at start-up**, and here for wallet-svc's reason: each failure is
  silent from the outside. Trips are accepted, months roll over, nothing errors, and the platform's
  only revenue quietly does not arrive. `WarnAboutFeesThatCannotBeCollected` names each with the
  money that is not collected.

## The two cross-context reads Epic 23 makes, and the one shared schema

**`subscription.*` has a second writer, and it is registry-svc.** `GET /v1/vehicles/{id}/subscribers`
and `DELETE .../subscribers/{userId}` are on registry-svc in D3', and its C028 note says outright
that the roster "is held in `subscription.grants`" — so it reads the roster and performs the
passenger's own unsubscribe there. That predates this service. The two agree on the transition
(`status='unsubscribed'` + `unsubscribed_at`, the row stays MUTED) and on the event shape, so
whichever surface a client uses produces the same rows and the same `share.revoked`. **The
difference is that registry-svc knows nothing about `subscription.subscriptions`, so its unsubscribe
does not cancel the subscription.** Raised in the C048 handoff: the route should forward here, or be
retired, and D3' should say which.

**`registry.*` and `iam.*` are read and never written.** The vehicle, its Mode B classification, the
org's roster and sub-roles, the driver assignment and the verified payout profile all come from one
statement per request (`ModeBRegistryRepository.ReadVehicleAsync` computes the authority flags in the
same query as the vehicle, so the roster cannot be read against one answer and written against
another). The alternative is four synchronous hops to fleet-svc, which does not exist yet — the same
trade `IVehicleRepository`, ride-svc's `DriverSummaryRepository` and registry-svc's own
`SubscriptionRepository` already make in both directions.

## The one cross-context write, and why it is here

`support.tickets`, one row, category `daily_fee_refund`. Migration 1303's own table comment says the
table "also carries the driver daily-fee refund request (US-9.23) as an ordinary category" — the row
was designed for this caller. What makes it this service's to write is the validation: **only
subscription-svc can say whether the driver was in fact charged on the day they are disputing**, and
a ticket raised against a day with no charge is a queue item Finance has to close by hand.

The lifecycle is not ours. This writes one `OPEN` row and reads back the driver's own; it never sets
`status`, never writes `admin_response` and never resolves anything. support-svc (C053) owns the
queue and admin-bff (C065) owns the reversal that answers it. **When C053 lands this becomes a
forward to its ticket route and the SQL is deleted** — raised in the C047 handoff.

## Schema this service added

Three scripts, three micro-change-sets — two in the C047 handoff, one in C048's.

| Object | Why |
|---|---|
| `subscription.command_log` (1203) | R-14 needs a replay log per bounded context and D4' §5 prints DDL for `rides.command_log` only — the same gap C020, C021, C030, C033, C034, C045 and C046 each raised. `billing.command_log` is **not** reused: it is wallet-svc's, its primary key is the bare idempotency key, and two services sharing it would let a client's key collide across a service boundary and be served the wrong response |
| `ix_offers_driver_responded` (0713) | `tripsToday` is "this driver's ACCEPTED offers within a Colombo day" and 0702 indexes `dispatch.offers` only by `ride_id` and by the partial-unique live predicate — so both callers on the hot path (dispatch's gate, per candidate per round; this service's charge, per accept) were sequential scans over the platform's whole offer history. Partial on `ACCEPTED`, because DECLINED and EXPIRED are the bulk of the table |
| `subscription.outbox` (1204) | BR-23.11 puts the unsubscribe here and D-22 requires the revocation to be published inside the transaction that mutes the grant — and D6' §2.1 gives subscription-svc no topic, D4' §18b no table. The same gap 0309 opened for registry-svc, arriving from the other end of the same event |

`migrate-verify.sh` now expects **6** subscription tables, not 5, and carries a C048 section beside
C047's: the outbox's shape against registry's, the absent posting column on `subscription.payments`,
`ux_grant_active`'s partiality on `deleted_at`, and four CHECKs proved by rejection.

## Not here, and named rather than stubbed

- **The ledger entry.** wallet-svc's (C046, D-09). This service writes no posting, holds no account
  and computes no balance.
- **The fee reversal.** admin-bff's (C065, US-14.11) — `POST /v1/admin/drivers/wallet/{id}/reverse-fee`,
  a wallet credit of kind `adjustment`. This service raises the *request* and never the credit.
- **The consolidated fleet invoice.** fleet-billing-svc's (C060): `billing.fleet_invoices`, the fleet
  wallet, the per-vehicle breakdown, the dunning. This service raises the per-vehicle charges C060
  consolidates, and writes no invoice row.
- **Settlement of an individually-owned Mode B vehicle's charge.** Its `DUE` row is raised and there
  is no route in any spec that collects it: `billing.fleet_invoices` requires a `fleet_id` and the
  journal `kind` vocabulary has no value for a monthly platform fee. Named in the handoff.
- **The Fleet Portal's org-scoped proxy** over `/v1/mode-b/**` — `PUT /v1/fleets/{fleetId}/vehicles/
  {vehicleId}/classification`, `…/requests`, `…/subscribers`, `…/payments/{id}/confirm` — is
  **fleet-svc's** (C059). Both spellings resolve to the same rows and the same checks; the proxy adds
  the org scope, which is why no fleet id appears in a path here. `mode_b_billing` and
  `default_monthly_fare_minor` are set there and only read here, and BR-31.1's `409` on setting a
  vehicle Paid without a verified profile is that route's, not this one's.
- **The OnePay session for a Mode B subscription.** The callback half is complete and settles a
  session opened elsewhere; the *creation* half needs a per-org merchant binding that exists in no
  schema — `registry.driver_payouts` is per driver, for D-11 fare settlement — and opening one
  against the platform's merchant would route a passenger's fare into MageRide's account, which the
  fence forbids outright. `pay` with `method: onepay` therefore records the payment and returns no
  redirect, and says so in the log. LankaQR and online transfer are the working rails. Named in the
  C048 handoff.
- **Object storage for the LankaQR image and the transfer slip.** D-36's SSE-KMS bucket is C125's;
  until then the slip is a file on a configured path and the "signed URL" is an HMAC over a route
  this service serves. `ITransferSlipStore` is the seam, one method wide.
- **The subscription push notifications.** notification-svc's (C052): "your subscription is due", the
  owner's "a slip is waiting". This service moves the state and writes no `comms.*` row.
- **The credit-transfer and voucher arithmetic.** wallet-svc's. The D3'-spelled routes here forward
  the caller's bearer; the discount, the not-self rule, the `PENDING` claim and the account lock
  ordering all live in one place.
- **`audit.events`.** admin-bff's (C065, D-35), by the same split C045 uses. What this service
  contributes is the after-image — `billing.voucher_discount_tiers.updated_by`,
  `billing.plans.updated_at` — plus an information-level log naming the actor.
- **The Dockerfile.** `infra/docker-compose.dev.yml` already carries a `subscription-svc` cluster
  destination pointing at the combined `app-services` container.

## Configuration

Every knob is documented at its declaration in `SubscriptionOptions`. D7' §4.2 gives this service no
variables of its own, so unlike wallet-svc there is no unprefixed spelling to honour — but the two
wallet keys are read from `Wallet:*` as well, because that is what `.env.app.example` already ships
for the service on the other end of the seam and a co-located deployment should not have to set the
same secret twice under two names.

| Setting | Default | Where it comes from |
|---|---|---|
| `WalletBaseUrl` · `WalletInternalApiKey` | unset | **unset ⇒ no fee can be charged** (503) and the forwarded routes are unmapped. Also read as `Wallet:BaseUrl` / `Wallet:InternalApiKey` |
| `WalletTimeout` | 2 s | D6' §8.3's internal hop, inside the 15 s offer window |
| `FreeTripsPerDay` | 1 | US-9.1, verbatim. Anything else breaks the fence and is announced at start-up |
| `ModeBMonthlyFeeMinor` | 30 000 | URD §Daily Platform Fee Structure / ADD §19 (Rs 300) |
| `ModeBBillingEnabled` | on | **off ⇒ no Mode B vehicle is ever billed** and C060 has no lines |
| `ModeBBillingInterval` | 1 h | **no spec** — the run is idempotent, so frequent and cheap beats monthly and fragile |
| `MaxHistoryRows` | 2 000 | **no spec** — a bound, not a working limit |
| `InternalApiKey` | unset | **unset ⇒ `/v1/internal/fees/**` is not mapped.** These routes charge money |
| `ModeBSubscriptionsEnabled` | on | **off ⇒ `/v1/mode-b/**` is not mapped at all** — no requests, no roster, no payments, no unsubscribe — and Kafka and the outbox go off with it |
| `OnepayWebhookSecret` · `LankaQrWebhookSecret` | unset | **unset ⇒ that callback refuses every delivery.** No accept-unsigned mode: the money it would falsely settle is the fleet owner's |
| `FileLinkSigningKey` | unset | **unset ⇒ a key per process**: a link minted by one replica does not verify on another and the pay sheet's QR breaks |
| `FileLinkTtl` | 15 min | **no spec** — long enough to render and scan a pay sheet, short enough that a screenshotted link is dead |
| `SlipRoot` | *(temp dir)* | **not object storage** — D-36's bucket, when a client exists |
| `SlipMaxBytes` | 8 MiB | **no spec** — the same bound as `Ride:ProofPhotoMaxBytes`; the idempotency request buffer is raised to match |

`ConnectionStrings:Postgres` and `Jwt:*` are required. `CommandLog:*` defaults to `subscription` /
`command_log` with no aggregate-id column, and `Outbox:*` to `subscription` / `outbox` /
`subscription_outbox` / `registry.events` (both set in `SubscriptionApplication`, overridable).
`Kafka:BootstrapServers` is required **while Epic 23 is on** and unused otherwise. There is no
`ConnectionStrings:Redis` and there must not be — see `SubscriptionApplication` for why.
