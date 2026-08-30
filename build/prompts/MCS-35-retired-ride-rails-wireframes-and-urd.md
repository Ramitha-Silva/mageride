# MCS-35 — the retired card rails are still drawn on the wireframes and still stated in URD Epic 8

## Identity

This is a **micro-change-set**, not a manifest component, and this file is **hand-written**.

`build/tools/generate_build_plan.py` writes one `build/prompts/Cxxx.md` per entry in
`build/manifest.yaml` and deletes nothing, so this file survives a regeneration untouched — but it
is also not produced by it. **This change set adds no component to `build/manifest.yaml` and the
generator must not be re-run for it**: re-running resets the Status column and the whole Session
Handoffs log in `build/progress.md`. Record the work as an ordinary Session Handoff entry.

*(The one exception is decision **D4** below. If a passenger wallet screen is given its own
`SCR-PA-*` id, `build/screen_coverage.md` moves from 202 / 202 to 203 / 203 and that id needs an
owning component — which **is** a manifest change, and it would follow
`build/prompts/C134-www/S02-manifest-c134-regenerate.md`'s backup → regenerate → restore →
one-row-diff procedure. Do not fold it in casually.)*

Raised 2026-08-28 from C134 · S08 (the passenger guide's fare chapter), and **outstanding since
2026-08-06** — three component handoffs asked for it by name before this one. **The work is not
done.** This file is the record and the instruction; the edits are a session's work.

---

## The finding

**ADD v3.6's payment-custody change set — §1.18 AL-57…AL-59, 2026-08-01 — retired both card rails
as ride methods**, and the rest of the spec set never caught up. The rule the three changes reduce
to is stated once in the ADD itself:

> *"OnePay and the platform's own LankaQR merchant are used only where **MageRide is the payee**.
> Where the payee is a driver or a fleet owner, the rail is the payee's own bank instrument, and the
> platform never touches the money — except on the `wallet` rail, where it takes custody
> deliberately and discharges it through AL-58's payout run."*
> — `specs/architecture-design-document.md` §1.18

**Four artefacts already carry the new answer**, which is what makes the rest a contradiction rather
than an omission:

* **D2 · SCR-PA-016**, marked `Δ AL-57/AL-59`: *"Cash (default) · Wallet (prepaid balance, no
  surcharge …) · Driver QR (scan the driver's own bank LankaQR, no surcharge) … **OnePay is not a
  ride method***".
* **D3** — `POST /fare/pay`'s method enum is **`cash | wallet | scan_driver_qr | cod`**, and
  `POST /mode-b/subscriptions/{id}/pay` is `lankaqr_deeplink | lankaqr_scan | online_transfer |
  cash`.
* **ADD §6 `fare-svc`** — the same four, *"because no ride fare may be charged to a platform
  merchant account"*.
* **The built app.** `apps/passenger-android` implements exactly those rails and says why in its own
  words: *"`PaymentRails` is the only place a payment method becomes a control, and it contains
  neither `onepay` nor platform-`lankaqr` … **No surviving rail carries a surcharge**, so nothing in
  this app can render one."*

**Three completed components asked for this change set and it was never filed.**

> *"…`ride.yaml`'s booking-time payment enum never caught up with AL-57/AL-59, and the SCR-PA-016/017
> wireframes still draw OnePay +5%"* — **C080** handoff, 2026-08-06
>
> *"the pay sheet has no OnePay row and no `+5 %` anywhere … **Cash** takes the vacant row (D2′ §16e);
> the wireframe needs a micro-change-set"* — **C082** handoff, 2026-08-06
>
> *"the wireframe's Cash/LankaQR/OnePay row needs a micro-change-set"* — **C083** handoff, 2026-08-06

**What made it urgent is that the wireframes are now published.** C134 renders
`specs/wireframes/*.html` into the images on `www.mageride.lk` (MCS-34 D10). Two of them are
committed today under `portals/www/public/screens/`:

* `pa-016-payment-method` draws a live **"OnePay +5% Rs 43 · Rs 893"** row.
* `pa-025a-subscription-payment` draws **"OnePay · cards / wallets · +5%"**.

Both are faithful renderings of the **approved** wireframes — the wireframes are what changed
underneath them. Until they are re-rendered, a public marketing site shows a member of the public a
surcharge the platform does not charge, on a rail it does not offer, next to guide copy (S08,
chapters 6 and 8) that correctly says there is none.

## The decision

**No ride fare and no Mode B subscription is charged to a platform merchant account, and the specs
say so everywhere or nowhere.** Concretely:

| Where | Methods, after this change set |
|---|---|
| **Ride fare** (Mode C, passenger or package) | **Cash** (default) · **Wallet** (prepaid balance topped up by card; no surcharge) · **Driver QR** (the driver's own bank LankaQR, settled by AL-47 attestation; no surcharge) · **COD** (packages only) |
| **Mode B subscription** | **LankaQR deep link** · **LankaQR scan** (the owner's own QR) · **Online transfer** (slip attached; pending until the owner confirms) · **Cash** (owner marks received) |
| **Wallet top-up, daily platform fee, bulk vouchers, fleet wallet** | **OnePay / LankaQR unchanged** — MageRide is the payee, and this is where OnePay's processing cost is recovered |

**There is therefore no surcharge on any ride payment method.** The +5% existed to recover OnePay's
fee on the fare; with the rail moved one step earlier, the fee is recovered at top-up and the fare a
passenger agrees to is the fare they pay by every route. **`lankaqr_deeplink` survives for Mode B
and not for rides** — for a subscription the payee is the fleet owner's verified account (AL-49);
for a ride it pointed at `LankaQr__MerchantId`, the platform's own.

**Nothing in this change set touches the legitimate uses**, and the deltas below are written to be
greppable so a future sweep can tell the two apart.

## What must change

### Scope A — `specs/wireframes/*.html` (the approved baseline)

Every edit is **row-for-row**: no frame gains or loses a control, so the `.phone` box stays
**320 × 680** and C134's capture geometry (S05, measured) is undisturbed.

| File · line | Frame | Change |
|---|---|---|
| `passenger_android.html` 584, 589 | **SCR-PA-016** payment method | `LankaQR · no surcharge` → **Wallet** (balance + *"Top up"* when short); `OnePay +5% Rs 893` → **Driver QR** (*"scan the driver's code · no surcharge"*). Cash stays first and default. States line drops US-8.11's recompute and keeps COD |
| `passenger_android.html` 864, 872 | **SCR-PA-025a** subscription payment | Delete the **OnePay** row; **Cash** takes the vacant row (D2′ §16e, C082's finding). Deep link, scan, transfer, cash |
| `passenger_android.html` 890 | **SCR-PA-025b** payment history | Method list drops OnePay |
| `passenger_android.html` 956 | **SCR-PA-027** profile & settings | Default payment `Cash / LankaQR / OnePay` → **Cash / Wallet**. The driver QR is deliberately **not** a stored preference — `iam.yaml` says in its own text that it is *"a settlement choice made during a ride"* (it needs a driver, a QR image and an amount, none of which exist in Settings) |
| `passenger_android.html` 157 | file header | *"Cash/LankaQR/OnePay"* → the surviving three |
| `passenger_ios.html` 470, 476 · 681, 689 · 705 · 758 | **SCR-PI-016 / 025a / 025b / 027** | The identical four edits. The iOS file must not be left behind — `iosTwin()` in C134's registry assumes the two files agree |
| `web_fleet.html` 386 | **SCR-FP-012** subscriber ledger | Method list drops OnePay (AL-59: subscriber money never reaches a MageRide account) |
| `web_admin.html` 237 | **SCR-AP-003** verification | *"Approval → vehicle/driver APPROVED + OnePay merchant …"* — **D-11 was retired by AL-57**; there is no per-driver OnePay merchant onboarding. Replace with AL-58's **driver payout profile** approval, which is what that queue actually gates now |

**Untouched in the same files, on purpose:** `driver_android.html` 515 and `driver_ios.html` 495
(wallet top-up — Card / OnePay / LankaQR, MageRide is the payee), `web_fleet.html` 318/323 (fleet
wallet top-up), `web_admin.html` 349–361 and 567 (gateway settlement reconciliation and a voucher
top-up row).

### Scope B — `specs/user-requirements-document.md`

Epic 8 first, because it is the one the user named and the one every other line quotes:

| Line | Story | Change |
|---|---|---|
| 434 | **US-8.10** | *"pay the fare in-app via LankaQR (no surcharge) or OnePay (+5%). Cash is default."* → **Cash (default), the passenger wallet, or scanning the driver's QR — none of the three carries a surcharge.** |
| 435 | **US-8.10a** | The platform-merchant LankaQR deep link is **retired for rides**. Either delete the story or re-scope it to Mode B subscriptions, where the deep link survives against the *owner's* account (see **D2** below) |
| 436 | **US-8.10b** | **Unchanged and now the primary in-app rail.** It already describes the driver's own QR |
| 437 | **US-8.11** | The +5% ride surcharge is **retired**. Replace with the rule: OnePay collects only where MageRide is the payee, and its processing cost is recovered on the **top-up** (Epic 9A), never on a fare |
| 438 | **US-8.12** | Keep, but re-scope: a `wallet` fare is terminal on the spot with no gateway leg, and a driver-QR fare notifies the driver to **confirm** rather than to observe (AL-47) |
| 439 | **US-8.15** | Re-scope the failure path. There is no gateway leg to fail on `wallet`; what survives is an **insufficient balance** and an unconfirmed driver-QR claim, both falling back to cash without losing trip history (`fare-svc` keeps `FellBackToCash`) |
| 445 | **US-8.21** | Proxy-booking payer routing has **no answer under the new rails** — see **D1** |
| 465–468 | Passenger Ride Payment Methods table | Three rows → **four**: Cash, Wallet, Driver QR, COD (packages). Surcharge column reads **None** throughout |
| 470 | The Rs 500 example | Now says nothing — every method totals Rs 500. Replace with the one-line statement that no ride method carries a surcharge, and leave gateway fees to Epic 9/9A where they belong |
| 472 | **UI label standard (C-11)** | *"Cash / LankaQR / Card, where Card = OnePay"* is the platform-wide label contract for **both** apps and must be restated — see **D3** |
| 474 | Zero-Commission Model | Its last clause routes OnePay settlement to the driver's merchant account. That account does not exist (D-11 retired); the driver is paid by **AL-58's payout run** |

**The same claim, in five other epics.** Each is the identical defect and moving Epic 8 alone would
leave the document contradicting itself:

* **710 · US-13.15** and **1192** (fleet screen table) — subscriber payment history drops OnePay.
* **806 · US-20.8** and **816–819** (package payment table) — sender pays by wallet or driver QR, or
  the recipient pays COD. "Three methods" becomes the four in the table above.
* **855 · US-22.4** and **1114** (screen table) — Default Payment Method is **Cash / Wallet**.
* **871 · US-23.3** and **882** (money-handling note) and **1112** (screen table) — Mode B drops
  OnePay; the note's *"MageRide facilitates LankaQR/OnePay routing"* becomes LankaQR only.
* **1107** (screen table, Pay fare) — the LankaQR deep link goes; the driver-QR camera stays.

### Scope C — the same claim in D2, the contracts and the migrations

**Recommended for this change set** (spec text, no code):

* `specs/D2_mageride_ui_spec.md` — **line 483's ASCII sketch contradicts line 477's own corrected
  paragraph six lines above it**, which is the sharpest instance in the set. Also 348 (payment chip),
  415/427 (package methods), 492/495/500/505 (SCR-PA-016/017 component tables), 584 (default
  payment), **1303** (traceability row still reading *"Cash/LankaQR/OnePay+5%"*), 1401 (SCR-FP-012).
* `specs/mobile_db_schema.md` §2.3 — `rides.payment_method` CHECK still lists `lankaqr`/`onepay`.

**Recommended as a separate change set**, because it carries code, a migration and a contract
version, and because the booking-time enum is a distinct question from the settlement-time one:

* `backend/contracts/ride.yaml` 1505 — booking enum `[cash, lankaqr, onepay, cod]` has **no
  `wallet`**, so a booking cannot express the rail the passenger chose and SCR-PA-016 has to ask
  again after the trip. `dispatch.yaml` 834 and `admin-bff.yaml` 3015 carry the same three.
* `backend/contracts/iam.yaml` 768/1246/1312/1314 — `DefaultPaymentMethod` is `[cash, lankaqr,
  onepay]`, so US-22.4's wallet preference is **device-local** in the built app (Δ C083).
* `backend/contracts/fleet.yaml` 2300 — Mode B methods still include `onepay`, while
  `subscription.yaml` 1845 has already removed it. **Two contracts, one flow, two answers.**
* `db/migrations/0601__rides_rides.sql` 59, `0101__iam_users_roles.sql` 21,
  `1202__subscription_subscriptions_payments.sql` 68 — the CHECK constraints behind the three above.
* **Already correct, do not "fix":** `fare.yaml` 591, `subscription.yaml` 1845, `wallet.yaml`,
  `1007__fares_wallet_payment_method.sql` (which added `wallet` to `fares.ride_payments`),
  `1010__registry_retire_d11_merchant.sql`.

### Scope D — the published images (C134)

`portals/www/public/screens/pa-016-payment-method.*` and `pa-025a-subscription-payment.*` are
generated, committed artefacts. Once Scope A lands, re-run **`npm --prefix portals run
screens:refresh --workspace @mageride/www`** (it needs `npx playwright-core install chromium` once
on the host) and commit the result in the same change, per `portals/www/CLAUDE.md`: the refresh is
the only sanctioned way that directory changes, and the ≤ 12 MB budget gate runs inside
`npm run build`.

**Until that happens**, C134 · S08 has fenced the two frames out of the guide chapters that would
otherwise publish them beside contradicting copy (`chapters: []` on SCR-PA-025a; SCR-PA-016 reduced
to `passenger/paying`). **They still render on `/screens`**, so the fence is a mitigation and not a
fix.

## Decisions needed

None of these can be answered from the specs, and each changes what gets written.

| # | Question | Why it cannot be inferred |
|---|---|---|
| **D1** | **Who pays a proxy booking now?** US-8.21's rule was *Cash ⇒ the rider pays the driver; LankaQR/OnePay ⇒ the booker is charged.* `wallet` maps cleanly (the booker's balance), but **`scan_driver_qr` has no answer**: whoever is standing at the drop-off is the one holding the phone that scans, and that is the rider, not the booker. Options: (a) driver QR behaves like cash — the rider settles; (b) driver QR is unavailable on a proxy booking; (c) the booker chooses and the app tells the rider. |
| **D2** | **Does US-8.10a survive, and where?** Delete it, or re-scope it to Mode B subscriptions where `lankaqr_deeplink` is live against the owner's account. Deleting loses the "falls back to a scannable QR when no bank app is installed" behaviour, which Mode B still needs. |
| **D3** | **What are C-11's three labels now?** The old standard was *Cash / LankaQR / Card*. **Cash / Wallet / Driver QR** is the obvious successor, but C-11 binds the **driver's** incoming-request card too — the driver has to read the method the passenger chose — so this is a two-app label change, not a passenger-app one. |
| **D4** | **Does the passenger wallet get its own screen id?** AL-57 gave passengers a prepaid balance and a top-up, and D2 line 1019 discharges the requirement with *"Δ AL-57: the same screen serves the passenger"* — pointing at **SCR-DA-022**, a **driver** screen in the **driver** app's wireframe file. A passenger cannot open a `SCR-DA-*` screen. Options: (a) a new `SCR-PA-*` id, a frame in `passenger_android.html` + `passenger_ios.html`, a coverage row and an owning component (**202 → 203**, manifest change); (b) state explicitly that the passenger's top-up is a sheet on SCR-PA-016 with no id of its own. **The built app has already had to answer this**, and C134's guide chapter 8 tells readers they can pay from a wallet balance that no wireframe draws. |
| **D5** | **Is Scope C's contract half in or out?** In = one change set, code and migrations included, longer. Out = MCS-36, and MCS-35 stays a documentation correction that leaves `ride.yaml` still unable to express `wallet`. |

## What a later session should know

* **The URD is not simply "older" than the ADD.** URD v2.9 (2026-07-22) predates the payment-custody
  change set (2026-08-01) and was never re-issued for it; D2 and D3 were partially re-issued, which
  is why D2 now disagrees with itself six lines apart. When this change set lands, **the URD's
  version header should record it** the way §1.18 does in the ADD, so the next reader can date the
  two documents against each other rather than guessing.
* **The wireframes are the approved structural baseline and editing one is not a small act.** C134's
  capture pipeline asserts that polish must not move a control by more than 1 px; that gate is about
  *rendering*, and it will not notice a deliberate content change. What protects the baseline is
  review, so a wireframe edit should be reviewed as a spec change and not as a typo fix.
* **`build/screen_coverage.md` is generated** and stays 202 / 202 unless **D4** says otherwise. C134
  claims no `SCR-*` id and this change set claims none either.
* **Three grep lines separate the legitimate from the retired**, and a future sweep should use them
  rather than searching for "OnePay":
  * a **ride** fare or a **Mode B subscription** with `onepay` or platform `lankaqr` → wrong;
  * a **top-up**, **daily fee**, **voucher** or **reconciliation** with `onepay` → right;
  * a **driver's own** QR (`scan_driver_qr`) or a **fleet owner's** QR (`lankaqr_scan`) → right, and
    note that neither produces a gateway webhook, which is why both settle by attestation or by an
    owner marking it received.
* **C134 · S08 recorded the same finding from the other end** (`build/progress.md`, 2026-08-28) and
  wrote the guide against the current specs. If this change set is ever reversed, chapters 6 and 8
  of the passenger guide are the copy that has to move with it.
