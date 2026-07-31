# subscription-svc (C047) — the Namma-Yatri zero-commission fee model

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002).
**No Redis, no Kafka, no outbox** — see "Rules that are load-bearing" for why each is absent.

**Verify:** `dotnet test backend/src/Subscription.Api.Tests -c Release`

`backend/contracts/subscription.yaml` is normative for this surface and wins over this file and
over the code.

## What this service is

**The rule, not the money.** It decides *whether* a driver owes the daily platform fee and *how
much*; the movement is a call to wallet-svc's ledger seam. `billing.journal_postings` keeps exactly
one writer (D-09) and it is C046.

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

| Table | Read | Written |
|---|---|---|
| `billing.plans` | every charge, the rates screen | admin PUT here |
| `billing.daily_fee_charges` | today, history | **this service only** |
| `billing.monthly_subscriptions` | the Mode B run and its view | **this service only** |
| `billing.voucher_discount_tiers` | the ladder | admin PUT here **and** wallet-svc's (C046) |
| `billing.journal_*` · `accounts` | — | **wallet-svc** — never touched here |
| `billing.fleet_invoices` | — | **fleet-billing-svc** (C060) |
| `dispatch.offers` | the Colombo-day trip count | **dispatch-svc** — read-only here |
| `registry.vehicles` · `driver_profiles` · `fleet_vehicles` · `fleets` | type, mode, live vehicle, roster | **registry-svc** — read-only here |
| `support.tickets` | the driver's own claims | **support-svc** (C053) — one row written here, argued below |
| `subscription.command_log` | the kernel's replay | this service |

## The three fences, and how each is held structurally

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
- **No Kafka and no outbox, and there must not be.** The daily fee's event is `wallet.debited`,
  which wallet-svc emits inside the transaction that posts the money (R-13). An event published from
  here would describe a movement this service did not make and could not roll back. The Mode B
  hand-off to C060 is a table it reads, not a message it waits for.
- **There is a command log, and it is for one route.** `POST /v1/fees/{driverId}/refund-requests`
  is the only surface here whose repetition would double an effect — a second identical claim on the
  Support queue, with no natural key to collide. The two internal fee routes opt out individually:
  their key is the Colombo day and the Colombo month, both stronger than a header, which dedupes
  identical *requests* rather than identical *days*.
- **Every switch-off is announced at start-up**, and here for wallet-svc's reason: each failure is
  silent from the outside. Trips are accepted, months roll over, nothing errors, and the platform's
  only revenue quietly does not arrive. `WarnAboutFeesThatCannotBeCollected` names each with the
  money that is not collected.

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

Two scripts, two micro-change-sets in the C047 handoff.

| Object | Why |
|---|---|
| `subscription.command_log` (1203) | R-14 needs a replay log per bounded context and D4' §5 prints DDL for `rides.command_log` only — the same gap C020, C021, C030, C033, C034, C045 and C046 each raised. `billing.command_log` is **not** reused: it is wallet-svc's, its primary key is the bare idempotency key, and two services sharing it would let a client's key collide across a service boundary and be served the wrong response |
| `ix_offers_driver_responded` (0713) | `tripsToday` is "this driver's ACCEPTED offers within a Colombo day" and 0702 indexes `dispatch.offers` only by `ride_id` and by the partial-unique live predicate — so both callers on the hot path (dispatch's gate, per candidate per round; this service's charge, per accept) were sequential scans over the platform's whole offer history. Partial on `ACCEPTED`, because DECLINED and EXPIRED are the bulk of the table |

`migrate-verify.sh` now expects **5** subscription tables, not 4, and carries a C047 section: the
replay log's shape, the absent package-delivery rates, and the AL-03 split between the unledgered
per-vehicle charge and the ledgered fleet invoice.

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
- **Mode B passenger subscriptions** (`/v1/mode-b/**`, Epic 23). C048's, and a different flow: that
  money is a pass-through to the fleet owner MageRide never holds and never ledgers (§18b).
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

`ConnectionStrings:Postgres` and `Jwt:*` are required. `CommandLog:*` defaults to `subscription` /
`command_log` with no aggregate-id column (set in `SubscriptionApplication`, overridable). There is
no `ConnectionStrings:Redis`, no `Kafka:BootstrapServers` and no `Outbox:*`, and there must not be —
see `SubscriptionApplication` for why each is off.
