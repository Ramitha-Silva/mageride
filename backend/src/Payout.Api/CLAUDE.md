# payout-svc (C133) — the weekly driver payout run

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002).
**No Redis, no Kafka, no outbox, no command log** — see `PayoutApplication` for why each is off.

**Verify:** `dotnet test backend/src/Payout.Api.Tests -c Release`

`backend/contracts/payout.yaml` is normative for this surface and wins over this file and over the
code.

## What this service is

**The discharge of the custody AL-57 created, and nothing else.** A card-paid ride moves passenger
wallet → driver wallet, so a driver's balance is money MageRide *owes*; AL-05 had deleted the only
outward bank rail. This is that rail. It prices nothing, settles no ride, and never decides who may
be paid — that is the Verification Officer, through the AL-39 queue.

| Endpoint | Auth | Spec |
|---|---|---|
| `GET /v1/drivers/payouts` | Bearer (driver) | AL-58, SCR-DA-022a |
| `GET /v1/admin/payouts` · `/batches` | finance · read | AL-58, SCR-AP-006 |
| `POST /v1/admin/payouts/batches` | finance · write | AL-58 — run out of band |
| `POST /v1/internal/payouts/{id}/result` | internal | AL-58 — the bank adapter reporting back |

| Table | Read | Written |
|---|---|---|
| `billing.payout_batches` · `billing.payouts` | Finance, the driver's history | **this service** |
| `billing.accounts` | who has a balance to sweep | **wallet-svc** — read-only here |
| `registry.driver_payout_profiles` | who may be paid at all | **registry-svc** — read-only here |
| `billing.journal_*` | — | **wallet-svc** (D-09) — never touched here |

## The fences, and how each is held structurally

- **Weekly FULL sweep — no minimum, no holdback.** Whatever the balance is on run day leaves in
  full. `Payout:RetainMinor` defaults to `0` and exists only because of one named interaction
  (D5' §8.1): the D-08 daily fee is charged from the **second** trip of a Colombo day, and cash and
  driver-QR fares never credit the wallet — so a cash-earning driver is swept to zero and refused
  their second trip until they top up. Setting it to one daily fee is the remedy, and it is a
  setting rather than a redesign precisely so the decision stays reversible.
- **A driver with no `verified` payout profile is never swept, and the join is the mechanism.**
  `EligibleDriversAsync` inner-joins `registry.driver_payout_profiles` on `status = 'verified'`, so
  such a driver cannot be selected — rather than being filtered out somewhere that could stop
  filtering. Their balance is retained, never lost.
- **This service writes no `billing.journal_postings` row.** wallet-svc (C046) is the only writer of
  the ledger on this platform (D-09); every movement goes through its
  `/v1/internal/wallet/driver-payout` seam, exactly as fleet-billing-svc does for an invoice.
- **The bank adapter is one outbound port and no provider is chosen.** ADD §1.18 makes origination
  via LankaPay/CEFTS a sponsor-bank and CBSL question — a go-live gate, not an engineering task.
  Unconfigured, the run still selects, still debits and still records: instructions rest at
  `PENDING` so **the liability is visible before a rail exists**. Refusing to sweep until a bank is
  wired would hide the debt in a growing wallet balance instead.

## Rules that are load-bearing

- **The debit and the instruction cannot be one transaction, so they are made recoverable instead.**
  They live in two services. The payout id is a deterministic function of `(batchId, driverId)` and
  wallet-svc composes its ledger key from the id — so re-running a batch regenerates the same id,
  replays the same debit (a no-op answering `replayed: true`), and completes the insert that did not
  happen. A random id would make an orphaned debit unfindable and the driver's money with it.
  `PayoutIds` carries the argument; `ux_payouts_batch_driver` stops the completing insert becoming a
  second one.
- **The order is debit-then-record, and it has to be.** `billing.payouts.journal_entry_id` is
  `NOT NULL` — the schema refuses to hold half the pair — so there is no row to write until the
  money has moved. The failure that order admits is an orphaned debit, which the derived id makes
  findable; the other order would admit an instruction with no money behind it, which nothing could
  find.
- **`FAILED` reverses under a second ledger key, not a second kind.**
  `driver_payout_reversal:{payoutId}`. Sharing the debit's key would make the reversal a *replay* of
  the debit and restore nothing — a driver whose bank transfer bounced would silently lose the week.
  Two guards, because this is somebody's money: the status moves first under a guarded `UPDATE` (so
  a redelivered result finds the row terminal and does nothing), and the ledger key would catch a
  second reversal anyway.
- **A reversal that fails is the one state where a driver is out of pocket, and it is logged at
  ERROR by name.** The row says `FAILED` and the money has not come back. Nothing retries it
  automatically — a second automated attempt at moving money nobody has reconciled is how one
  mistake becomes two — so it needs Finance.
- **An interval, not a weekly alarm.** The sweep is idempotent on the Colombo business date
  (`run_date` UNIQUE), so re-asking costs one indexed read and catches everything an alarm would
  miss: a deployment rolling at midnight on Sunday, a replica whose clock moved, a run that died
  halfway. A weekly alarm gets exactly one chance per week to be running, and its failure mode is a
  week nobody is paid.
- **Every replica runs it and there is no lease.** Concurrency is resolved by two unique indexes
  rather than by a lock, and a lock would introduce a way for payouts to stop entirely when its
  holder dies badly.
- **A second sweep of one Colombo date is a `409`, not a quiet second pass.** The run pays a
  driver's *whole* balance, so running it again the same day would raise an empty instruction for
  everybody it had just emptied. The scheduled runner treats an already-swept day as done; Finance
  asking for it explicitly is told.
- **Money leaving the platform is Finance's, not an ordinary admin's.** URD §2.3's Finance row.
  An Admin who may configure a tariff has no business releasing a week's payouts.
- **The account number is masked on every read.** `****6543`. An operator reconciling a bank
  statement needs to recognise the account, not to be handed it.

## Not here, and named rather than stubbed

- **The bank itself.** No provider is chosen (ADD §1.18). `IBankOrigination` is one interface with
  one HTTP implementation and a documented unconfigured behaviour; nothing in the run depends on
  which provider eventually sits behind it.
- **A retry route.** `payout.yaml` declared `POST /v1/admin/payouts/{id}/retry` and implementing
  AL-58 showed it to be incoherent: a `FAILED` instruction has *already* had its debit reversed, so
  there is nothing left to re-submit and the next weekly run sweeps the restored balance. Where an
  operator needs to pay somebody before Sunday, `POST /v1/admin/payouts/batches` is that capability.
  Removed from the contract; recorded in the C133 handoff.
- **The "your payout is on its way" notification.** notification-svc's (C051), and it has no
  template — migration 1904 seeds none. Named as absent rather than invented, which is why this
  service has no outbox: there is no consumer to publish to.
- **The Finance exception queue's screen.** admin-bff's (C065). This service exposes
  `GET /v1/admin/payouts?status=` and the partial index `ix_payouts_open` that makes it cheap.
- **The Dockerfile.** `infra/docker-compose.dev.yml` carries a combined `app-services` container,
  which is where D7' §2.1 puts a service of this size.

## Configuration

Every knob is documented at its declaration in `PayoutOptions` and in `infra/env/.env.app.example`.
**Every one of them fails silently and looks like normal operation from the outside** — a wallet
balance that keeps growing looks like a busy week — so `PayoutApplication.Announce` names each with
the money that does not move.

| Setting | Default | Where it comes from |
|---|---|---|
| `Enabled` | on | **off ⇒ NO driver is ever swept** and every balance grows without bound. ERROR |
| `RunDay` | Sunday | **no spec** — a week that closes on it puts earnings in the account at the start of the next |
| `PollInterval` | 15 min | **no spec** — an interval, not an alarm; see above |
| `RetainMinor` | 0 | **the decision**: full sweep, no holdback. The knob is D5' §8.1's remedy if it strands cash-earning drivers |
| `BatchSize` | 5 000 | **no spec** — a bound, not a working limit |
| `WalletBaseUrl` · `WalletInternalApiKey` | unset | **unset ⇒ no sweep can debit anything.** The run still reports what it would have moved. ERROR |
| `BankBaseUrl` · `BankApiKey` | unset | **unset ⇒ instructions rest at PENDING** — the designed state, not a fault. ERROR, because the liability is real |
| `BankTimeout` | 30 s | **no spec** — a bank is not a pod |
| `InternalApiKey` | unset | **unset ⇒ `POST /v1/internal/payouts/{id}/result` is NOT mapped** and every instruction stays SUBMITTED for ever |

`ConnectionStrings:Postgres` and `Jwt:*` are required. There is no `ConnectionStrings:Redis`, no
`Kafka:BootstrapServers` and no `Outbox:*`, and there must not be.
