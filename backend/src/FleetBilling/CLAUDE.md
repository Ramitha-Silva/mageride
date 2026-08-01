# fleet-billing-svc (C060) — the fleet wallet, and the consolidated monthly invoice

Stack: .NET 10 Minimal API + Dapper over Npgsql + Confluent.Kafka. References `MageRide.Shared`
(C002). **No Redis** — see `FleetBillingApplication` for why.

**Verify:** `dotnet test backend/src/FleetBilling.Tests -c Release`

`backend/contracts/fleet-billing.yaml` is normative for this surface and wins over this file and
over the code.

## What this service is

**The platform's charge TO a fleet operator, and the wallet it is paid from** (US-13.10,
US-13.10b, AL-03). subscription-svc (C047) raises one `billing.monthly_subscriptions` row per Mode B
vehicle per Colombo month; this service consolidates a fleet's rows into one invoice with a
per-vehicle breakdown, settles it against the fleet wallet, and dunes it when nobody pays.

**This is not C048's money.** `subscription.payments` is a passenger's fare **to the fleet owner** —
a pass-through MageRide never holds, never takes a cut of and never ledgers (§18b). The two share
the words "Mode B" and "monthly" and nothing else, and netting them would be the single most
expensive mistake available in this schema.

| Endpoint | Auth | Spec |
|---|---|---|
| `GET /v1/fleets/{fleetId}/billing` | owner, **approved org** | US-13.10 |
| `GET …/billing/{invoiceId}` | owner | **Δ C060** — the breakdown the story is about |
| `GET …/billing/{invoiceId}/export?format=csv\|pdf` | owner | **Δ C060** — SCR-FP-010's Download |
| `GET …/billing/{invoiceId}/receipt` | owner | **Δ C060** — US-13.10b |
| `POST …/billing/{invoiceId}/pay` | owner | **Δ C060** — settle now |
| `GET /v1/fleets/{fleetId}/wallet` | owner | **Δ C060** — the balance card and statement |
| `POST …/wallet/topup` | owner | US-13.10b |
| `GET …/wallet/topup/{topupId}` | owner | **Δ C060** — the 90 s poll |
| `POST /v1/fleet-billing/topup/onepay/webhook` · `…/lankaqr/confirm` | HMAC | **Δ C060** — D6' §7.1/§7.2 |
| `POST /v1/internal/fleet-billing/run` | internal | **Δ C060** — a month that was missed |

| Table | Read | Written |
|---|---|---|
| `billing.fleet_invoices` (1106) | the portal, the sweeps | **this service only** |
| `billing.fleet_invoice_lines` · `fleet_topups` · `fleet_outbox` · `fleet_command_log` (1108) | the breakdown, the sessions, the events, the replay | **this service only** |
| `billing.monthly_subscriptions` | the month's charges | **subscription-svc** raises; **this service** marks PAID — argued below |
| `billing.accounts` · `wallets` · `wallet_transactions` | the balance, the statement | **wallet-svc** — read-only here |
| `billing.journal_*` | — | **wallet-svc** — never touched here |
| `registry.fleets` · `fleet_vehicles` · `vehicles` | the roster, the plate, the mode | **fleet-svc** / registry-svc — read-only here |
| `iam.fleet_members` · `users` | the sub-role, the Owners to dun | **fleet-svc** / iam-svc — read-only here |

## The three fences, and how each is held structurally

- **Mode A vehicles are free and Mode C is never fleet-owned (AL-03).** Held by an absence: a line
  can only exist for a charge `billing.monthly_subscriptions` already carries, and 1104's only
  writer filters `v.mode = 'B'` — so there is no Mode A row to exclude. The `v.mode = 'B'` in this
  service's own line insert is a second, independent lock on the same door. Mode C is stopped by a
  stronger thing than either: `registry.fleet_vehicles.mode CHECK (mode IN ('A','B'))` means a fleet
  cannot own one at all. A Mode-A-only organisation gets a **FREE invoice with zero lines**, which is
  1106's own table comment ("the row is the evidence the run considered them") and the most direct
  statement of AL-03 an operator can be handed.
- **This is the platform charge, not the passenger pass-through (C048).** There is no code path
  from this assembly to `subscription.payments`, no repository that reads it and no column on
  anything here that could hold a passenger's fare. The two flows meet nowhere.
- **Postings use the same ledger with `owner_type='fleet'`; there is no parallel ledger.** Held by
  an absence again: **no `INSERT INTO billing.journal_`, no `UPDATE billing.accounts` and no
  `billing.wallets` write exists anywhere in this assembly.** Every movement is an HTTP call to
  wallet-svc's `/v1/internal/wallet/fleet/{fleetId}/debit` · `/credit`, which C060 added to C046's
  seam. `billing.journal_postings` keeps exactly one writer (D-09).

## Rules that are load-bearing

- **Generation is three set-based statements and no loop.** One insert per organisation, one per
  raised charge, one recompute of the totals — so a month with ten thousand vehicles costs three
  round trips. All three are upserts that add what is missing and restate nothing, which is what
  makes "re-running invoice generation for a month is idempotent" a property of the SQL rather than
  of a guard somebody has to remember. `ux_fleet_invoices_fleet_period`,
  `ux_fleet_invoice_lines_vehicle` and `ux_fleet_invoice_lines_charge` are the three arbiters.
- **The lines are a snapshot, not a join.** Deriving the breakdown live from
  `billing.monthly_subscriptions ⋈ registry.fleet_vehicles` would make a *settled* invoice's lines
  change under it — a vehicle that leaves the fleet next week would leave the month it was billed
  for, and Σ lines would stop equalling `total_minor`. The plate and the vehicle type are copied for
  the same reason: an operator re-reading last March must see what they were billed for.
- **One raised charge reaches one invoice, ever.** `ux_fleet_invoice_lines_charge` is UNIQUE on
  `monthly_subscription_id`, which is what stops a vehicle that changed organisation mid-month being
  billed to both. Without it the platform would collect twice and both invoices would look correct.
- **A settled invoice is never appended to.** A line added after settlement would break the
  invariant that Σ lines is the amount that was actually paid. A charge raised for a month whose
  invoice has already been settled is therefore left unconsolidated — and **the run says so**, with
  a count, because the alternative is the platform quietly not being paid for those vehicles.
- **Debit first, record second.** The ledger and this service's tables are not in one transaction,
  so the order is the whole of the crash-safety argument (C047's rule, larger amounts).
  Debit-then-record leaves, at worst, money taken and an invoice still DUE — and the retry re-sends
  the same `fleet_invoice:{invoiceId}` key, gets `replayed: true` for the same entry, moves nothing,
  and writes the row it owed. Record-then-debit leaves an organisation marked PAID that paid
  nothing, which no retry ever repairs.
- **Two guards make settlement single-shot, and they guard different things.**
  `billing.journal_entries.idempotency_key` is UNIQUE and stops the *money* moving twice;
  `UPDATE … WHERE status IN ('DUE','OVERDUE')` stops a second *row* change. The first is the
  load-bearing half, because two replicas can decide to settle at the same instant and nothing
  serialises the decision.
- **The ledger key is the business fact, spelled exactly.** `fleet_invoice:{invoiceId}` and
  `fleet_topup:{topupId}` — in `Domain/LedgerKeys`, in migration 1108's header, and in
  `LedgerKeyTests`, which asserts the literal strings. A well-meaning reformat would not fail a
  build and would not fail an integration test either; it would simply start charging twice.
- **A 402 is an outcome, not a failure.** An organisation whose wallet cannot cover the month is
  exactly what dunning exists for: the invoice stays open, the sweep counts it, and the next tick
  tries again after a top-up. That is what makes `FleetBilling:AutoSettle` safe to leave on.
- **A FREE invoice cannot post, three times over.** Its total is zero, so the entry would need a
  zero leg — which `LedgerService.PostAsync` refuses as "a movement that did not happen";
  `ck_fleet_invoices_free` forbids the column that would hold the result; and the settlement filter
  never selects it. `409 invoice-not-payable` rather than a bare conflict, because SCR-FP-010 draws
  a different thing for "already paid" and for "nothing to pay".
- **This service marks `billing.monthly_subscriptions` PAID, and that is a deliberate second
  writer.** C047 raises those rows as FREE or DUE and has no route that collects one; its own
  handoff hands the fleet half here. A row that stayed DUE for ever would leave `ix_monthly_subs_due`
  growing without bound and would tell the Fleet Portal that a vehicle on a settled invoice still
  owes for the month. `WHERE ms.status = 'DUE'` narrows it to exactly that transition — a FREE row is
  left alone, because a month that cost nothing was not paid for.
- **The runner is an interval, not a monthly alarm.** Every phase is idempotent, so re-running costs
  three statements and catches a vehicle approved on the 9th, a deployment that was rolling at
  midnight on the 1st, a replica whose clock moved and a wallet topped up an hour ago. A monthly
  alarm gets exactly one attempt per month to be running, and its failure mode is a month nobody is
  billed for. Every replica runs it and there is no lease: a lock would protect operations that are
  already idempotent, and would add a way for billing to stop entirely when its holder dies badly.
- **Three phases per tick, in this order.** Generate before settling, or the month just opened is
  settled empty; settle before dunning, or an invoice a fresh top-up already covers is announced
  overdue.
- **Dunning is two signals with two audiences and they are not one mechanism.** The Fleet Portal's
  is a *state* (`OVERDUE`, which SCR-FP-010 draws whenever an operator opens the screen) plus a
  `fleet.invoice_overdue` event; notification-svc's is a *push*, sent by a direct call to its
  internal plane. Routing the second through Kafka would mean notification-svc growing a
  `fleet.events` consumer for one message type, which is a second delivery path for something that
  already has one (C059 made the same call for the departure alarm).
- **The OVERDUE claim is what makes the notice exactly-once.** `UPDATE … WHERE status = 'DUE' …
  FOR UPDATE SKIP LOCKED RETURNING`, so every replica may sweep and each overdue invoice is
  announced by one of them. Without it an hourly sweep on three replicas would push an operator
  three times an hour about one bill.
- **`overdue_at` and `last_dunned_at` are two columns because they answer two questions.** "When
  this went overdue" is written once and never moved; "when we last said so" is what
  `FleetBilling:DunningInterval` reads to decide whether to say it again. One column would lose the
  first the moment a reminder went out.
- **The state change commits before anything is pushed.** A notification that failed to send must
  not roll back the record that an invoice went overdue — the operator's own screen reads that
  record, and an unsent push is still an overdue invoice.
- **No user-facing string is composed here (D-26).** The dunning call carries a `notificationType`
  and a bag of values; notification-svc resolves migration 1906's trilingual template and each
  recipient's own language. The one formatting decision that has to happen here is minor units →
  rupees, because `{{amount}}` lands inside a sentence — invariant culture, because a comma-decimal
  culture would render "Rs 3.000,00" into a Sinhala push.
- **Only the Owner is dunned.** US-13.A5 gives billing to the Owner and takes it from the Manager in
  the same sentence, so pushing a Manager about a bill they cannot pay is telling the wrong person.
- **Billing is the Owner's, on reads as well as writes.** Unlike fleet-svc — where the map and the
  analytics sit outside the role gate — **every** route here carries it, because there is no billing
  read a Manager is entitled to. And approval gates reading too: a PENDING organisation has no
  approved vehicles, so every route would answer an empty page, and an empty page is a worse answer
  than "your organisation is still being reviewed".
- **The token's `fleet_role` claim is not the authority; the membership row is.** iam-svc puts the
  caller's *most privileged* membership in the token (C027), so an Owner of fleet A arrives at fleet
  B's invoices carrying `fleet_role=owner`. The claim gets the request past deny-by-default
  authorization; `FleetBillingAccessFilter` decides what it may actually do. It has to be fleet-svc's
  rule exactly, or the two halves of the Fleet Portal would disagree about who may act — which is
  why the ladder is the kernel's `MageRideClaims.FleetRoles` and not a copy.
- **Two idempotency guards on a top-up callback, answering different questions.**
  `ux_fleet_topups_provider_txn` catches a redelivery of the same gateway transaction (R-19, and the
  first thing checked); the ledger's `fleet_topup:{topupId}` key catches two *different* callbacks
  for one session, which a provider retrying under a new transaction id produces. A redelivery
  answers `200` with the same body, because that is what stops a provider retrying for ever.
- **A callback whose amount disagrees with its session credits nothing.** Crediting what the
  callback says lets a misconfigured or spoofed provider set the balance; crediting what the session
  says credits money the organisation may not have paid. Both are wrong, so the session stays
  `Pending` and the mismatch is logged as the settlement exception D6' §7.2 routes to Finance.
- **The signature is verified over the raw bytes, before any parsing, and there is no unsigned
  mode.** This endpoint credits an organisation that owes the platform money, so a forged callback
  would settle an invoice for nothing.
- **A top-up session is stamped with the service's clock, not the database's `now()`.** D6' §7.1's
  90-second window is evaluated against `TimeProvider`, and a row stamped by a different clock than
  the one that measures it is a window whose width depends on the drift between two machines.
- **The session artefacts are returned and never stored.** A redirect URL, a session token and a QR
  payload are single-use, seconds-lived instruments of one gateway session; keeping them would put a
  payment instrument in a table the poll reads back long after it stopped working.
- **`availableMinor` is signed here where a driver's is floored at zero.** A fleet that owes more
  than it holds is exactly the state SCR-FP-010 has to draw, and flooring it would render "you can
  cover this" over a shortfall.
- **The balance comes from `billing.accounts`, the master.** `billing.wallets` is the mirror that
  exists for dispatch-svc's hot path; a billing screen reading it would show an operator a number
  that lags their own top-up.
- **Money is cast to `bigint` in the SQL.** `total_minor` and `amount_minor` are `INTEGER` in §10
  while every contract types money as int64, and Dapper's constructor binding matches parameter types
  exactly — an `Int32` column against an `Int64` parameter does not fail to convert, it fails to
  materialise the record at all.
- **`vehicleCount` is counted, never stored.** A column would be a second opinion about the same
  rows, right when the run wrote it and wrong the moment a line was added.
- **The export's TOTAL is Σ of the rows above it, computed from the lines rather than copied from
  the invoice.** If the two ever disagreed, the file an operator holds should show it rather than
  hide it behind a header.
- **Every switch-off is announced at start-up**, for wallet-svc's reason: each failure below is
  silent from the outside. Months roll over, operators run buses, nothing errors, and the platform's
  fleet revenue simply does not arrive — or arrives and is never collected, which looks identical on
  every screen.

## The PDF, and why it is written here

`Export/InvoicePdf.cs` emits a PDF 1.4 document using the three base-14 fonts every conforming
reader is required to have built in — nothing is embedded and nothing is rasterised. A renderer
(QuestPDF, iText, a headless browser) is a large dependency, a licence question and, in two of the
three cases, a native binary in every container, for a document with a title, six metadata lines and
a table. wallet-svc (C046) took the other branch for the driver statement and answers `415` with the
reason; the difference is that C060's deliverable names PDF export outright.

Two things follow, and both are stated rather than left as surprises:

- **The cross-reference table has to be exact.** A PDF is read backwards, so every object's byte
  offset is recorded as the bytes are written and `PdfAssert` parses the file back and checks each
  one lands on its own `N 0 obj`. An offset one byte out opens in some readers and not others.
- **Text outside Latin-1 becomes `?`.** The base-14 fonts cover Latin-1 and nothing else. Sri Lankan
  plates are Latin, so in practice only an organisation's *name* can be affected — and it is intact
  in the CSV, which is UTF-8 with a BOM and is the file an accounts department reconciles. An
  embedded font is the one thing a document renderer would buy; raised in the C060 handoff.

The table is set in Courier because base-14 Courier is metrically exact (every glyph 600/1000 em),
so the amount column is right-aligned by arithmetic rather than by shipping Helvetica's width tables.

## Schema this component added

`db/migrations/1108__billing_fleet_billing.sql` and the trilingual
`db/migrations/1906__seed_fleet_invoice_overdue.sql`. Each object is argued at its declaration; all
are micro-change-sets raised in the C060 handoff.

| Object | Why |
|---|---|
| `fleet_invoice` in `ck_journal_entries_kind` | `billing.fleet_invoices.journal_entry_id` has existed since 1106 and no journal kind a fleet's monthly fee could be posted under ever did, so the column could never be filled. `adjustment` was the alternative and is wrong: it is the Finance queue's correction kind (US-14.11), and netting the platform's largest recurring revenue line into it would make revenue and corrections one number for ever |
| `OVERDUE` in `ck_fleet_invoices_status` | `fleet.yaml`'s `FleetInvoice.status` enum has always been four values and 1106's CHECK admitted three, so the state C060's dunning is about could not be stored |
| `due_at` · `overdue_at` · `last_dunned_at` · `settled_at` · `updated_at` | the four instants SCR-FP-010 renders and the sweeps read. No spec pins a payment term, so the invoice records the one it was issued under rather than deriving it from a setting that can move |
| `ck_fleet_invoices_posting` · `ck_fleet_invoices_settled` | only a settled invoice may carry a posting, and a settled one must carry both halves. The pair is what makes `journal_entry_id IS NOT NULL` readable as "this was paid" |
| `billing.fleet_invoice_lines` | US-13.10 asks for "a per-vehicle line breakdown" and §10 gives the invoice a total and nothing else. Snapshotted, so a settled invoice cannot change under a roster edit |
| `billing.fleet_topups` | `billing.topups` (1107) is wallet-svc's and its `driver_id` is a driver, not an organisation. Same shape as 1107's own argument, one level up. AL-05 is a CHECK here too |
| `billing.fleet_outbox` | the dunning signal has a producer and two named consumers and no table. `billing.outbox` is wallet-svc's and drains to `wallet.events`; one table cannot serve two dispatchers publishing to two topics |
| `billing.fleet_command_log` | R-14 per bounded context — the **thirteenth**. Separate from `billing.command_log` (wallet-svc's): its primary key is the bare idempotency key, so two services sharing it would let one client's `Idempotency-Key` collide across a service boundary |
| `content.notification_templates` `fleet_invoice_overdue` ×3 (1906) | US-13.10's dunning notice has no D5' §14.4 row and no seeded key; this component is its producer, so both halves land together. `LOW_BALANCE` is not it — that is US-9.9's *driver* warning about the next trip |

`migrate-verify.sh` now expects **19** billing tables, not 15, and **24** notification template keys,
not 23; it carries a C060 section (the four tables, the two CHECK extensions, R-19's index, the
command log's key space, AL-05 by rejection, and the six constraints proved by rejection).

## Contract changes this component made

`fleet-billing.yaml` is new; `fleet.yaml`, `wallet.yaml` and `_shared.yaml` changed. All recorded in
the C060 handoff.

| Change | Why |
|---|---|
| `fleet-billing.yaml`, and `fleet.yaml` losing `GET …/billing`, `POST …/wallet/topup` and `FleetInvoice` | D3' lists both routes in the fleet-svc table and ADD §6 gives the fleet wallet and the monthly invoicing to fleet-billing-svc — the third instance of the split C007 made for the tracker-bulk route and C044 for `…/health`. The gateway resolves a cluster from the contract that declares an operation, so declaring one in `fleet.yaml` routes it to a service that does not implement it |
| 7 new operations in `fleet-billing.yaml` | the invoice detail (the breakdown the story is about), the CSV/PDF export, the receipt, the Pay verb, the wallet read, the top-up poll, and the internal run |
| `/v1/internal/wallet/fleet/{fleetId}/debit` · `/credit` · `/account` in `wallet.yaml` | the fleet side of C046's ledger seam. `/account` moves no money and exists because `billing.accounts` is created lazily by the first posting, which would leave a top-up session with no `account_id` to record until the organisation had already been invoiced once |
| `LedgerAccount` schema in `wallet.yaml` | what `/account` returns |
| `invoice-not-payable` in `_shared.yaml` | SCR-FP-010's Pay button has two refusals that draw differently, and `conflict` cannot tell them apart |

## Not here, and named rather than stubbed

- **The ledger entry.** wallet-svc's (C046, D-09). This service writes no posting, holds no account
  and computes no balance.
- **The per-vehicle charge.** subscription-svc's (C047). This service consolidates the rows that
  service raises and never raises one; it writes exactly one column on that table, and only the
  DUE → PAID transition.
- **The individually-owned Mode B vehicle's charge.** Its `DUE` row is raised by C047 and there is
  still no route in any spec that collects it: `billing.fleet_invoices` requires a `fleet_id`, so a
  vehicle that belongs to no organisation cannot be invoiced here. C047's handoff named it; it is
  named again.
- **A stranded charge's disposal.** A charge raised for a month whose invoice was already settled is
  left off it deliberately, and what to do with it is a Finance decision no spec makes. The run logs
  the count rather than inventing one.
- **The Fleet Portal's billing screen.** C115's (SCR-FP-010). This service serves the invoice, the
  breakdown, the documents and the wallet, and draws nothing.
- **The push itself.** notification-svc's (C051). This service calls its internal plane with a type
  and values, and composes no user-facing string (D-26).
- **`audit.events`.** admin-bff's (C065, D-35), by the same split C045, C047 and C058 use. What this
  service contributes is the durable after-image — the invoice, its lines and the journal entry —
  plus an information-level log naming what moved.
- **The OnePay status-poll reconciler** (D6' §7.1's "reconcile open orders by status poll"). It
  needs a live OnePay query API; `FleetBilling:TopupPendingWindow` is read (a Pending session's age
  is reported as `expired`) and nothing sweeps. A session that was paid and whose callback was lost
  is currently resolved by the provider's own retry — wallet-svc's position, unchanged.
- **Object storage for the exported documents.** They are rendered per request and streamed; there
  is no bucket (D-36 is C125's) and no signed link, because the caller is already authenticated as
  the organisation's Owner.
- **The Dockerfile.** `infra/docker-compose.dev.yml` carries a `fleet-billing-svc` cluster
  destination pointing at the combined `app-services` container, which is where D7' §2.1 puts it.

## Configuration

Every knob is documented at its declaration in `FleetBillingOptions` and in
`infra/env/.env.app.example`. **D7' §4.2 gives this service no variables** — it predates
fleet-billing-svc being split out of fleet-svc in ADD §6 — so the keys it shares with a neighbour are
also read under that neighbour's prefix (`Wallet:*`, `Onepay:*`, `LankaQr:*`, `ComBankIpg:*`,
`Notification:*`); a `FleetBilling:*` value wins where both are set.

| Setting | Default | Where it comes from |
|---|---|---|
| `WalletBaseUrl` · `WalletInternalApiKey` | unset | **unset ⇒ no invoice is ever settled and no top-up credited** (503). Also read as `Wallet:*` |
| `WalletTimeout` | 10 s | D6' §8.3's internal hop. Longer than subscription-svc's 2 s — no offer window here |
| `InvoicingEnabled` | on | **off ⇒ no fleet is ever invoiced**; the charges pile up and nothing consolidates them. ERROR |
| `RunInterval` | 1 h | **no spec** — the run is idempotent, so frequent and cheap beats monthly and fragile |
| `PaymentTerm` | 7 d | **no spec.** Copied onto the invoice at generation, so moving it never retro-dates one already issued |
| `AutoSettle` | on | **no spec** — US-13.10 reads as a standing arrangement, not a checkout. Off ⇒ only the Pay button ever settles |
| `RunBatchSize` | 200 | **no spec** — a bound, not a working limit |
| `ModeBMonthlyFeeMinor` | 30 000 | ADD §19 (Rs 300). **Read, never written** — the line carries the amount C047 raised. Must equal `Subscription:ModeBMonthlyFeeMinor` |
| `Onepay:ApiKey` · `BaseUrl` | unset | **unset ⇒ the card rail answers 503**; LankaQR unaffected (AL-05 leaves two rails, no bank-transfer fallback) |
| `Onepay:WebhookSecret` | unset | **unset ⇒ every OnePay callback is refused and nobody is credited.** No unsigned mode |
| `LankaQr:DeepLinkTemplate` | unset | AL-15's primary path. **Unset ⇒ that rail answers 503** |
| `LankaQr:PayloadTemplate` | unset | **no spec** — an EMVCo payload is the acquirer's; unset omits the QR fallback |
| `LankaQr:WebhookSecret` | unset | D7' §4.2 spells it `ComBankIpg__WebhookSecret` (D-12); both are read |
| `MinTopupMinor` / `MaxTopupMinor` | 3 000 / 100 000 000 | **no spec** — a tenth of one vehicle's month; ten times wallet-svc's driver ceiling |
| `TopupPendingWindow` | 90 s | D6' §7.1. **Read, never swept** — see "Not here" |
| `NotificationBaseUrl` · `NotificationInternalApiKey` | unset | **unset ⇒ an overdue invoice is recorded and nobody is told**; the portal still draws it |
| `NotificationTimeout` | 10 s | **no spec** |
| `DunningInterval` | 3 d | **no spec** — without it every operator with an unpaid bill would be pushed twenty-four times a day |
| `InternalApiKey` | unset | **unset ⇒ `/v1/internal/fleet-billing/**` is not mapped**: a missed month cannot be invoiced on demand |
| `MaxPageSize` | 50 | **no spec** — D3' §0 caps a page at 100 |

`ConnectionStrings:Postgres`, `Kafka:BootstrapServers` and `Jwt:*` are required. `Outbox:*` defaults
to `billing` / `fleet_outbox` / `billing_fleet_outbox` / `fleet.events` and `CommandLog:*` to
`billing` / `fleet_command_log` with no aggregate-id column (both set in `FleetBillingApplication`,
overridable). There is no `ConnectionStrings:Redis` and there must not be — see
`FleetBillingApplication` for why.
