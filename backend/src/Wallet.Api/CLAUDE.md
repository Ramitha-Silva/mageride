# wallet-svc (C046) — the driver wallet on a balanced double-entry ledger

Stack: .NET 10 Minimal API + Dapper over Npgsql + StackExchange.Redis + Confluent.Kafka. References
`MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/Wallet.Api.Tests -c Release`

`backend/contracts/wallet.yaml` is normative for this surface and wins over this file and over the
code.

## What this service is

The **only writer of `billing.journal_postings`** on this platform. Everything else here is a reason
to write one.

| Endpoint | Auth | Spec |
|---|---|---|
| `GET /v1/wallet/{userId}` · `/transactions` | Bearer | US-9.7, US-9A.19 |
| `GET /v1/wallet/{driverId}/transfers` | Bearer (driver) | US-9A.11 |
| `POST /v1/wallet/topup/onepay` · `/lankaqr` | Bearer + attestation | US-9.18, D6' §7.1/§7.2, AL-15 |
| `POST /v1/wallet/topup/onepay/webhook` · `/lankaqr/confirm` | HMAC | D6' §7.1/§7.2, R-19 |
| `GET /v1/wallet/topup/{topupId}` | Bearer | **Δ C046** — the 90 s window a client polls |
| `POST /v1/wallet/voucher/purchase` | Bearer (driver) | **Δ C046** — US-9.19 |
| `GET /v1/wallet/voucher/discount-tiers` | Bearer | ADD Appendix C |
| `POST /v1/wallet/credit-transfer/initiate` | Bearer (driver) | US-9A.12 |
| `POST /v1/wallet/credit-transfer/request` · `/{id}/approve` · `/{id}/reject` · `GET /pending` | Bearer (driver) | **Δ C046** — US-9.10/9.12/9.13, US-9A.10 |
| `GET`/`PUT /v1/wallet/admin/voucher-discount-tiers` | admin · finance | US-9A.15 |
| `POST /v1/internal/wallet/{driverId}/debit` · `/credit` | internal | **Δ C046** — the ledger seam |
| `POST /v1/internal/wallet/fleet/{fleetId}/debit` · `/credit` · `/account` | internal | **Δ C060** — the same seam for an `owner_type='fleet'` wallet (AL-03) |

| Table | Read | Written |
|---|---|---|
| `billing.accounts` · `journal_entries` · `journal_postings` | balances, replays | **this service only** |
| `billing.wallets` · `wallet_transactions` | history, dispatch's gate | this service (projections) |
| `billing.topups` (1107) | the poll | this service |
| `billing.voucher_discount_tiers` | the ladder | admin PUT here **and** subscription-svc's own admin route (C047) |
| `billing.voucher_purchases` · `credit_transfers` | history | this service |
| `billing.outbox` · `command_log` (1107) | the kernel's dispatcher / replay | this service |
| `dispatch.cancellation_penalties` | `outstandingDebtMinor` | **dispatch-svc** — read-only here |
| `wallet:bal:{driverId}` (Redis) | **dispatch-svc** (D-08) | this service, write-through |

## The three fences, and how each is held structurally

- **Bank transfer is not a top-up method (AL-05).** There is no route, no `method` value, no receipt
  column and no reconciliation queue. `ck_topups_method` (1107) admits `onepay` and `lankaqr`, so the
  database refuses the row — a `migrate-verify.sh` check asserts exactly that. The contract's header
  lists the removed endpoints under "do not re-add".
- **"Reseller" is not a role, an account or a capability (AL-01).** One wallet per driver
  (`ux_accounts_owner`), `owner_type` has no `reseller`, and a transfer posts two legs of equal and
  opposite value. **A fee leg cannot be recorded**: `ck_journal_entries_kind` has no value for one,
  because AL-01 removed `reseller_commission` from the vocabulary. The margin is the *purchase*
  discount and nothing else.
- **A credit request names a Driver ID (AL-34).** SCR-DA/DI-023's QR path was removed; no request body
  on this surface has a field for a scanned payload.

## Rules that are load-bearing

- **One method writes every entry.** `LedgerService.PostAsync` claims the idempotency key, locks the
  accounts, checks the arithmetic, writes the postings, updates `billing.accounts` +
  `billing.wallets` + `billing.wallet_transactions`, queues the outbox row, commits, and *then* writes
  the D-08 cache. Five things that have to happen together; five call sites would eventually be four.
- **Σ legs = 0 is checked twice.** `trg_balanced` (1101) is the guarantee — it binds a psql session and
  every future service — but it fires at COMMIT and surfaces as a 500. Checking in code first turns a
  caller's arithmetic bug into a diagnosable failure rather than an `internal-error` on somebody's
  wallet.
- **The idempotency key is the business fact, never a random value.** `topup:{topupId}`,
  `voucher_purchase:{purchaseId}`, `driver_transfer:{transferId}` — recorded in
  `Domain/JournalKinds.LedgerKeys` and in 1107's header, beside the three spellings 1101 already
  fixes. `INSERT … ON CONFLICT (idempotency_key) DO NOTHING RETURNING id` is the whole mechanism: the
  loser of a race reads what the winner wrote and reports `replayed`.
- **Accounts are locked in id order.** A transfer touches two wallets; two simultaneous transfers
  between the same pair in opposite directions would deadlock if each locked its own sender first.
  `ORDER BY id … FOR UPDATE` makes that impossible rather than rare.
- **A driver's wallet may not go negative.** §10 leaves it to the application ("driver non-negativity
  in app") and nothing else enforces it, so `402 insufficient-wallet` does. The platform and suspense
  accounts are exempt — the platform side of every credit is negative by construction, which is what
  double entry means.
- **The wallet is credited on the callback and never on the initiate.** A gateway session that was
  accepted has moved no money; treating it as a credit is how a balance grows by abandoning a payment
  page.
- **Two idempotency guards on a callback, answering different questions.**
  `ux_topups_provider_txn` catches a redelivery of the same gateway transaction (R-19, and the first
  thing checked); the ledger's `topup:{topupId}` key catches two *different* callbacks for one
  session, which a provider retrying under a new transaction id produces. A redelivery answers `200`
  with the same body, because that is what stops a provider retrying for ever.
- **A callback whose amount disagrees with its session credits nothing.** Crediting what the callback
  says lets a misconfigured or spoofed provider set the balance; crediting what the session says
  credits money the driver may not have paid. Both are wrong, so the session stays `Pending` and the
  mismatch is logged as the settlement exception D6' §7.2 routes to Finance.
- **The signature is verified over the raw bytes, before any parsing**, and there is no unsigned mode.
  A wallet-credit endpoint that trusts an unsigned body is a free-money endpoint. The verifier is
  `MageRide.Shared.Payments.WebhookSignature` — in the kernel because six callbacks across four
  services share the scheme `_shared.yaml` declares once, and four copies is four chances for one of
  them to compare with `==`.
- **The discount reduces the price and never the credit.** `credited_minor = denomination_minor` is a
  database constraint (C005); the entry moves the *face value* both ways and `paid_minor` records what
  was charged. A denomination that is not an active tier is refused rather than interpolated — the rate
  is configured per voucher value, and interpolating one invents a number somebody is paid.
- **The discount is truncated so the price rounds up.** Integer arithmetic throughout; the fraction of
  a minor unit stays with the platform rather than being handed to the buyer, which is the direction an
  unattended rounding error should fall.
- **A transfer's balance is checked at approval, not at request.** What the holder can afford when they
  answer is the only figure that matters. `DIRECT` sends are approved on creation — the sender is the
  one acting — and `REQUESTED` ones wait; one table, one status column, two directions.
- **The `PENDING` predicate is the claim.** `UPDATE … WHERE status = 'PENDING'` inside the ledger's
  transaction, so two taps on Approve post one entry and the second reports a conflict. Throwing from
  the claim rolls the postings back with it, which is what keeps the money and the row's status
  identical.
- **A transfer that is not the caller's is a 404, not a 403.** The house rule: telling them apart makes
  the endpoint an oracle over other drivers' credit requests. The `409` after that is different — by
  then the caller has proved the row is theirs.
- **The `kind` whitelist is the boundary of the internal plane.** A caller may post the kinds a spec
  names for it and nothing else — notably not `topup`, `voucher_purchase` or `driver_transfer`, which
  have their own endpoints here carrying arithmetic and provider dedupe the seam would bypass.
- **The fleet pair is a second route family and not a second ledger (Δ C060, AL-03).** Everything
  downstream of the route — the lock ordering, the Σ = 0 check, the `billing.wallets` mirror, the
  history line, the outbox row — is `LedgerService.PostAsync`, unchanged; the only thing
  fleet-billing-svc needs that the driver routes cannot give it is an account resolved by *fleet*
  id. The path is `/fleet/{fleetId}/…` rather than an `ownerType` field on the body, because a body
  field that picks whose wallet is debited is one typo away from taking a month's fleet invoice out
  of a driver's balance. **`topup` is admitted on the fleet credit route and refused on the driver
  one**: the driver rails are here and the seam would bypass them, while the fleet rails are
  fleet-billing-svc's (ADD §6) and carry the same two guards over `billing.fleet_topups` (1108).
  `POST …/fleet/{fleetId}/account` is the one route on this plane that moves no money — the account
  is created lazily by the first posting, which would otherwise leave a fleet top-up session with no
  `account_id` to record until the organisation had already been invoiced once.
- **The low-balance edge is driver-only (Δ C060).** US-9.9 is a driver's warning about the next
  trip; a `LOW_BALANCE` at an organisation would resolve to no recipient and would mean the wrong
  thing if it did. A fleet's dunning is fleet-billing-svc's OVERDUE signal.
- **The internal routes are idempotency-exempt because the *body* carries the ledger key.** A second,
  header-based guard over the same money would be weaker and would need its own table.
- **The cache write-through is after COMMIT, and a failure degrades to a delete.** Writing inside the
  transaction would publish a balance a rollback then un-did, and dispatch would gate a second trip on
  money that does not exist for up to 5 s. On a Redis failure the key is dropped instead, which sends
  the gate to `billing.wallets` — updated in the same transaction. If that fails too the TTL bounds it.
- **The low-balance event is edge-triggered** (US-9.9). The balance *before* the posting is known
  inside the transaction, so a driver already below the threshold who spends again is not notified
  twice. Level-triggered, every debit of a low wallet would be a push and the warning would be the
  noise a driver mutes. `severity` carries D5' §9.4's second clause (below zero → "Top Up Required"),
  because only a client draws a banner.
- **The balance a driver reads comes from `billing.accounts`, the master.** `billing.wallets` is the
  mirror that exists for dispatch-svc's hot path; a wallet screen reading the mirror would show a
  driver a number that lags their own top-up.
- **`availableMinor` is net of accrued debt, read from `dispatch.cancellation_penalties`.** A
  read-only cross-context read, for the same reason iam-svc's bootstrap makes several: the alternative
  is a synchronous call to dispatch-svc on the wallet screen. For a driver it is nearly always zero
  (§11.12 answers a driver cancellation with reputation, not money) — the column is there because
  `availableMinor` is what the fee gate checks and a gross figure would overstate it the day a debt
  exists.
- **Every switch-off is announced at start-up**, and here it matters more than anywhere: each one is
  silent from the inside. A driver pays at a gateway and is never credited; a fleet's daily fee is
  never charged. `WarnAboutMoneyThatCannotMove` names each with the money that does not arrive.

## Schema this service added

`db/migrations/1107__billing_topups_outbox.sql`. Three objects, three micro-change-sets in the C046
handoff.

| Object | Why |
|---|---|
| `billing.topups` | D3' returns `{topupId, state, redirectUrl}` from the initiate and D6' §7.1 has the webhook arrive later carrying `{orderId, providerTransactionId}` — so the id has to survive between two requests and §10 prints no table. `fares.ride_payments` cannot be borrowed: its `ride_id` is NOT NULL |
| `billing.outbox` | `wallet.debited`/`wallet.credited` have a named producer (ADD §6) and named consumers (dispatch's D-08 cache, ride-svc) and no topic or table — the same shape C028/C030/C033/C044 each raised |
| `billing.command_log` | R-14, fifth bounded context; D4' §5 prints `rides.command_log` only |

`migrate-verify.sh` now expects **15** billing tables, not 12, and carries a C046 section: the R-19
index, the two posting/settlement constraints, and the AL-05 rejection.

## Contract changes this component made

`wallet.yaml`, all recorded in the C046 handoff. The header lists them; the two that are decisions
rather than additions:

| Change | Why |
|---|---|
| the voucher purchase and the whole request/approve/reject flow moved **into** this file | C007 left them out because D3' prints them under `/v1/vouchers` and `/v1/subscriptions/credit-transfer`. `billing.*` has one writer, and ADD §11.6 draws subscription-svc calling **wallet-svc** for the balance check and the movement. subscription-svc keeps its route table and forwards the bearer here (C047/C048) |
| `404` on the internal routes instead of `401` | the internal plane is unmappable rather than unauthorized, which is what the gateway does for `/v1/internal/**` |

## Not here, and named rather than stubbed

- **The daily fee itself.** subscription-svc's (C047, D-13). This service exposes the debit it needs
  and holds no fee logic — no rate table read, no Colombo-day arithmetic, no first-trip rule.
- **The PDF statement.** `wallet.yaml` declares `application/pdf` on the transactions route and it
  answers `415` with the reason: a PDF needs a renderer and a document template no spec provides, and
  a PDF-shaped CSV would be worse than saying so. CSV is implemented.
- **The OnePay status-poll reconciler** (D6' §7.1's "reconcile open orders by status poll"). It needs a
  live OnePay query API; `Wallet:TopupPendingWindow` is read (a Pending session's age is reported) and
  nothing sweeps. Named in the handoff — a session that was paid and whose callback was lost is
  currently resolved by the provider's own retry.
- **Gateway settlement reconciliation** (D6' §7.2's ComBank IPG exceptions → the Finance queue). The
  exception is *logged* here with the numbers; the queue is admin-bff's (C065).
- **The push that tells a driver their balance is low.** notification-svc's (C051). This service emits
  `wallet.low_balance` with the numbers and a `notificationType`, and no rendered text (D-26).
- **Mode B subscription money.** subscription-svc's (C048) — a pass-through to the fleet owner that
  MageRide never holds, so it writes no entry here.
- **The fleet invoice, its per-vehicle breakdown, the fleet top-up session and the dunning.**
  fleet-billing-svc's (C060). This service holds the fleet's *account* and posts its entries; it
  owns no `billing.fleet_*` table and knows nothing about what a month costs.
- **The Dockerfile.** `infra/docker-compose.dev.yml` already carries a `wallet-svc` cluster destination
  pointing at the combined `app-services` container.

## Configuration

Every knob is documented at its declaration in `WalletOptions` and in `infra/env/.env.app.example`.
D7' §4.2's four variables are honoured **as it spells them** (`Onepay__ApiKey`,
`LankaQr__MerchantId`, `ComBankIpg__WebhookSecret`, `LowBalance__ThresholdMinor`); a `Wallet:*` value
wins where both are set.

| Setting | Default | Where it comes from |
|---|---|---|
| `LowBalanceThresholdMinor` | 20 000 | D7' §4.2 / D5' §9.4 (Rs 200). Edge-triggered |
| `BalanceCacheTtl` | 5 s | D-08 / D5' §9.2. **Must equal `Dispatch:WalletCacheTtl`** |
| `BalanceCacheEnabled` | on | off ⇒ a top-up is invisible to the D-08 gate for its own TTL |
| `Onepay:ApiKey` · `BaseUrl` | unset | **unset ⇒ the card rail answers 503** |
| `Onepay:WebhookSecret` | unset | **unset ⇒ every OnePay callback is refused and nobody is credited** |
| `LankaQr:DeepLinkTemplate` | unset | AL-15's primary path. **Unset ⇒ that rail answers 503** |
| `LankaQr:PayloadTemplate` | unset | **no spec** — an EMVCo payload is the acquirer's; unset omits the QR fallback |
| `ComBankIpg:WebhookSecret` | unset | D7' §4.2, the LankaQR confirm secret (D-12). Same rule as OnePay's |
| `MinTopupMinor` / `MaxTopupMinor` | 5 000 / 10 000 000 | **no spec** — below the cheapest daily fee; ten times the largest voucher |
| `MaxTransferMinor` | 10 000 000 | **no spec** — a transfer cannot move more than a top-up may put in |
| `TopupPendingWindow` | 90 s | D6' §7.1. **Read, never swept** — see "Not here" |
| `MaxStatementRows` | 10 000 | **no spec** — a year of a busy driver's wallet |
| `InternalApiKey` | unset | **unset ⇒ `/v1/internal/wallet/**` is not mapped.** These routes move money |

`ConnectionStrings:Postgres`, `ConnectionStrings:Redis`, `Kafka:BootstrapServers` and `Jwt:*` are
required. `Outbox:*` defaults to `billing` / `billing_outbox` / `wallet.events` and `CommandLog:*` to
`billing` / `command_log` with no aggregate-id column (both set in `WalletApplication`, overridable).
