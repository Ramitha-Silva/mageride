# admin-bff (C062 core + C063 verification + C064 directories + C065 finance & PDPA) — the Admin Portal's back-office BFF

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002) and
`Analytics` (C061, a library — not a service). **No Redis, no consumer, no outbox, no command
log** — see `AdminBffApplication` for why each is off.

**Verify:** `dotnet test backend/src/AdminBff.Tests -c Release`

`backend/contracts/admin-bff.yaml` is normative for this surface and wins over this file and over
the code.

## What this service is

The single back-office front door for all six internal roles (AL-02, `admin.mageride.lk`). C062 is
its foundation, **C063 is the Verification Officer's whole surface**, **C064 is the three
directories** and **C065 is finance, the three queues and E-06's data rights**. Each maps onto the
same group in `AdminEndpoints` and inherits both fences without touching either — except that C065
adds **one** second prefix, `/v1/pdpa`, which the start-up guard names explicitly (see AL-02 below).

It also serves the **pdpa-svc surface** D3' heads "pdpa-svc (via admin-bff)": three data-subject
routes and three operator ones.

| Endpoint | URD §2.3 gate | Spec |
|---|---|---|
| `GET /v1/admin/session` | authenticated internal | **Δ C062** — URD §2.2, AL-37 |
| `GET /v1/admin/verification/queues/{driving-license,vehicle-registration,fleet-org}` | verification · read | AL-39, SCR-AP-003 |
| `GET /v1/admin/verification/{subjectId}` · `/org/{orgId}` | verification · read | AL-39, SCR-AP-003a/003c |
| `PUT /v1/admin/verification/{subjectId}/fields/{fieldKey}` | verification · write | AL-29, US-2.4a/2.10a |
| `POST /v1/admin/verification/{subjectId}/approve` · `/reject` | verification · write | US-2.9, US-2.15, AL-49 |
| `GET /v1/admin/verification/queues/driver-payout` | verification · read | **Δ AL-58/AL-59** — SCR-AP-003 tab 4 |
| `GET /v1/admin/verification/payout/{driverId}` | verification · read | **Δ AL-58/AL-59** |
| `POST /v1/admin/verification/payout/{driverId}/approve` · `/reject` | verification · write | **Δ AL-58** |
| `GET /v1/admin/documents/{docId}` | verification · read | AL-39, SCR-AP-003b, US-24.8 |
| `GET /v1/admin/passengers` · `/{passengerId}` | support · read **platform-wide** | **C064** — AL-40, SCR-AP-010/011 |
| `GET /v1/admin/drivers` · `/{driverId}` | driver-wallet · read **platform-wide** | **C064** — AL-41, SCR-AP-012/013 |
| `GET /v1/admin/vehicles` · `/{vehicleId}` | fleet-monitoring · read **platform-wide** | **C064** — AL-42, SCR-AP-014/015 |
| `GET /v1/admin/dashboard` · `/stats` · `/stats.csv` | analytics · read | US-14.6, AL-38, US-24.7 |
| `POST /v1/admin/vehicles/{id}/suspend` · `/drivers/{id}/suspend` | moderation · write **platform-wide** | US-14.3 |
| `GET /v1/admin/reports/queue` | moderation · read | ADD Appendix C |
| `POST /v1/admin/reports/{id}/resolve` | moderation · write **platform-wide** | US-12.6 |
| `GET /v1/admin/support/tickets` | support · read | US-16.3 |
| `POST /v1/admin/support/tickets/{id}/resolve` | support · write **platform-wide** | US-16.3 |
| `PUT /v1/admin/fares/tariffs` | platform-pricing · configure | US-14.4 |
| `POST /v1/admin/config/cities` · `PATCH .../{code}` | platform-settings · configure | AL-27 |
| `GET · PUT /v1/admin/config/feature-flags[/{key}]` | platform-settings · configure | **Δ C062** — US-14.12 |
| `POST /v1/admin/trains` · `PUT` · `DELETE .../{id}` | platform-settings · configure | US-2.17/2.18 |
| `POST /v1/admin/announcements` | announcements · write | US-14.8, D-26 |
| `GET /v1/admin/finance/reconciliation` · `/exceptions` | finance · read | **C065** — D6' §7.2, SCR-AP-006 |
| `GET /v1/admin/finance/transactions[.csv\|.pdf]` | finance · read | **C065** — US-9A.15 |
| `GET · POST /v1/admin/finance/refunds` | refunds · read / write | **C065** — E-05, ADD §11.14 |
| `POST /v1/admin/drivers/wallet/{driverId}/reverse-fee` | driver-wallet-adjustments · write | **C065** — US-14.11 |
| `GET /v1/admin/documents/expiring` | verification · read | **C065** — E-03, ADD §6 |
| `GET /v1/admin/fraud/queue` | moderation · read | **C065** — E-07, ADD §6 |
| `GET /v1/admin/pdpa/queue` | account-management · read **platform-wide** | **C065** — E-06 |
| `POST /v1/admin/pdpa/{id}/fulfill` · `/reject` | account-management · write **platform-wide** | **C065** — E-06 |
| `POST /v1/pdpa/export` · `/erasure` · `GET /v1/pdpa/{id}` | **authenticated data subject** | **C065** — E-06, US-1.8 |
| `GET /v1/admin/audit-log` | audit-trail · read | US-19.3, D-35 |
| `/v1/admin/transit/gtfs/{**path}` | platform-settings · configure | AL-54, SCR-AP-016 |

**Δ C062** marks the three routes D3' does not carry; each is argued in `admin-bff.yaml` and
recorded as a micro-change-set in the C062 handoff in `build/progress.md`.

## The four fences, and how each is held structurally

- **AL-06 — deny-by-default on every route.** Every endpoint names a URD §2.3 (feature area,
  capability) pair through the kernel's `RequireFeature`; nothing falls through to the
  authenticated-user fallback. `RbacMatrixTests` reads each route's own
  `FeaturePermissionRequirement` off the built endpoint, evaluates it through the same matrix the
  service enforces, and drives all six internal roles at the running socket — so the expectation
  *is* the spec rather than a second transcription of it, and a route whose gate drifts fails.
  A companion test enumerates the route table and fails on any route with no matrix gate and on
  any route no probe covers, so C063/C064/C065 cannot add one that escapes.
- **D-35 — every mutation writes an `audit.events` row.** Held in three places at once: the
  service **refuses to start** if a mutating endpoint sits outside the audited group or declares no
  action (`AdminBffApplication.GuardTheSurface`); a mutating request that finishes 2xx with nothing
  recorded **throws**, which is a 500 and a failed test, not a silent gap; and there is no setting
  that switches the interceptor off, because a fence with an off switch is a default.
  **Δ C063: the success window is 2xx *and* 3xx**, because AL-39's document viewer answers `302`
  with the signed URL and its `DOC_VIEW` row is written on the way out of exactly that response —
  treating a redirect as a failure would make the one audited read on this surface the one that
  records nothing.
- **AL-37 — no MFA, ever.** There is no auth route here at all: sign-in is iam-svc's
  `POST /v1/admin/auth/login` (the gateway sends `/v1/admin/auth/**` there at Order 20).
  `No_login_path_asks_for_a_second_factor` asserts the *absence* against the running route table,
  and `GET /v1/admin/session` answers `mfaRequired: false` explicitly because D3' §0 and D7' §4.2
  still carry the pre-AL-37 wording and a portal built from those would wait for a challenge.
- **AL-02 — nothing driver-facing or passenger-facing.** For an API that means every route is under
  `/v1/admin`, asserted by the same start-up guard rather than trusted.
  **Δ C065: `/v1/pdpa` is the one other prefix, and it is named in the guard rather than admitted by
  a looser rule.** D3' heads that family "pdpa-svc (via admin-bff) — data rights (`/v1/pdpa`)" and
  marks its three routes Bearer; `gateway-routes.json` already sends the prefix to this cluster; and
  iam-svc's `DELETE /v1/users/me` answers `202` with `Location: /v1/pdpa/{requestId}`, a URL a
  passenger's app follows. AL-02 forbids a driver-facing or passenger-facing **console** — three
  routes by which a person exercises a statutory right over their own record are not one. A *fourth*
  prefix still fails to start.

## Rules that are load-bearing

### Who writes what

- **A BFF forwards; it does not become a second writer.** The rule, stated once: *if the owning
  service exposes a route for the operation, admin-bff forwards to it; if no service does,
  admin-bff owns it.* safety-svc (C052) and support-svc (C053) each built a `/v1/internal/**` seam
  **for this BFF** and say so in their own files, and content-svc (C054) already exposes
  `POST /v1/admin/content/broadcasts`; writing those rows here would give
  `safety.vehicle_reports` two writers and put US-12.6's three-confirmations-delist rule in two
  places. What is left over — the two suspensions, the rate card, the cities, the flags, the trains
  — has no route anywhere and is written here.
- **CLAUDE.md's outbox rule is not violated by that.** "No direct HTTP calls between services for
  state changes" is about a *service* reacting to another service's state. This is a back-office
  front door relaying a human's command, and the components that built the seams designed it that
  way.
- **Two credentials, because there are two kinds of callee.** An `/v1/internal/**` plane takes the
  shared key and is told who the human was in the body — it has no bearer to check. content-svc and
  transit-svc expose role-gated `/v1/admin/**` routes, so the caller's own bearer is forwarded.
  Sending the shared key to a role-gated route would be a bypass of a check that exists.
- **An unconfigured upstream is a 503 on a route that is still mapped and still gated.** A route
  that disappeared when a setting was absent is a route neither the RBAC matrix test nor the D-35
  guard enumerates.
- **A suspension is five tables in one transaction, and that is the bounded exception.** A vehicle
  marked un-dispatchable while its tracking session is still live is still on the passenger's map,
  so `registry.vehicles`, `trips.sessions`, `dispatch.driver_presence`, `iam.users` and
  `iam.sessions` move together. No service owns all five and registry-svc's internal plane has no
  suspend route.

### The audit interceptor

- **The handler records the fact; the interceptor decides it was recorded.** A handler cannot know
  whether it is on a mutating route and an interceptor cannot know what changed — so the handler
  supplies the entity and the before/after images, the interceptor supplies the actor, the address
  and the request, and an empty context on a mutating 2xx is an exception.
- **The row commits with the decision.** Handlers call `IAdminAuditContext.FlushAsync(unitOfWork,…)`
  just before `CommitAsync`, so the change and its audit row commit together or not at all — the
  rule reputation-svc and transit-svc are written under. What the interceptor writes afterwards is
  whatever a route with no transaction left behind.
- **Only successes are audited.** A 4xx changed nothing, and a row saying an admin suspended a
  vehicle they were not allowed to suspend would be a false entry in an immutable log.
- **The topic publish is best-effort and the row is not.** Postgres holds the log D-35 is about and
  `GET /v1/admin/audit-log` reads; `audit.events` on Redpanda is D6' §2.1's cold-storage sink. A
  broker that is down must not roll back a suspension that has already committed.
- **`before`/`after` and `detail` are different facts and are stored apart.** The first pair is what
  the handler knows about the **entity**; `detail` is what the interceptor knows about the
  **request** (method, path, the caller's whole role union, the idempotency key). One column
  holding both makes "what changed" unreadable.
- **`actor_role` is recorded, never joined.** `iam.user_roles` is mutable, and an auditor asking
  who could do this in March must get March's answer. When several roles are held the union is
  sorted and the first is stored, so the column is deterministic; the whole set is in `detail`.
- **Two rows for one forwarded action is the right failure.** This one records "an admin called this
  route"; the owning service's records "the state went from X to Y". Only the second survives the
  route being renamed, and only the first survives the row being purged.

### RBAC

- **`RequireFeature` names a row; `RequirePlatformWideFeature` also demands it unscoped.** URD §2.3's
  ◐ is a fence — "allowed, and you must bound it" — and the Moderation row's qualifiers are
  `at onboarding` (Verification Officer) and `temp on reports` (Support CSR). Neither describes a
  permanent platform-wide suspension, which is the only moderation action this component offers, so
  the queue stays open to both and the ban button does not. Cells whose ◐ describes a *subset of
  settings* rather than a subset of records (Admin on Platform config) keep plain `RequireFeature`:
  the subset there is the endpoint set itself.
- **Scope is per capability and the union is additive.** Somebody who is both a CSR and an Admin
  holds Moderation · Write platform-wide from the Admin column and may suspend. Asserted by name.
- **The menu is a projection of the matrix, not a second list of roles.** URD §2.2 requires the UI to
  be "rendered from the same permission model the API enforces"; each nav item names an (area,
  capability) pair and the same evaluator filters it. It is nav, not authorization — hiding an item
  hides nothing, and AL-06 says so in as many words (US-21.1).
- **Six nav items are answered by other services, and the manifest says which.** The Configuration
  group has to contain the GTFS manager (transit-svc), the daily-fee rates and voucher tiers
  (subscription-svc), the Driver-Level parameters (dispatch-svc) and RBAC provisioning (iam-svc),
  because each is owned by the service that owns the table it writes. The console is one console;
  the writers are not one writer.

### Verification (C063)

- **A queue is "has a field still `pending`", and that *is* AL-27's fence.** The officer sees only
  submissions with a live question on them because an auto-verified document produces no
  `registry.document_fields` row in that state — not because something filters them out. The
  partial index `ix_document_fields_pending` (migration 0305) exists to be this query's index, and
  the same count is US-2.10a's approval gate, so the queue and the gate cannot disagree.
- **The `status` on a queue row is the subject's, not the queue's.** Every member is by construction
  awaiting review, so repeating that would say nothing; what the officer needs is whether this is a
  renewal on an already-approved applicant or a resubmission after a refusal — which is what
  SCR-AP-003's status filter filters on.
- **One route family, three subjects, and the owner of each decision decides it.** The AL-30
  recompute is registry-svc's — it built `/v1/internal/vehicles/{id}/onboarding/recompute` for this
  caller and says so. AL-50's fleet-vehicle gate and AL-49's organisation approval are fleet-svc's;
  its whole `/v1/internal/fleets/**` plane exists for this BFF. What is written here is what nobody
  exposes a route for: `registry.vehicles.rejection_reason` (registry-svc's file leaves it to this
  component by name), `registry.driver_profiles.verified_at` and its new `rejection_reason`, and
  `registry.document_fields`' confirmations.
- **The confirmation is one transaction and the recompute follows it.** The field, the audit row and
  the commit go together; only then is registry-svc asked to re-derive, because it has to read what
  this transaction wrote. An unreachable registry-svc is a `503` on a request whose field is already
  confirmed — the retry is idempotent both halves, and silently leaving a vehicle at
  `pending_review` is the exact gap that route was built to close.
- **A field key is decided on every row that carries it.** A licence is two documents and the
  officer confirms "the NIC number", not "the NIC number as read off the back". Deciding one row
  would leave the subject unapprovable for a field the officer believes they cleared.
- **An edit reaches an unflagged field; a bare confirm does not.** AL-29's premise is that OCR can be
  confidently wrong, so correcting an `auto_verified` value is a legitimate act — while confirming
  one nobody flagged is a decision with no question in front of it, and re-confirming on a double
  click must change nothing. An edited value becomes `manual` with no confidence, which is what
  `ck_document_fields_manual_confidence` demands and what stops an invented score reading as
  evidence.
- **Withdrawing a rejection is its own audited fact.** registry-svc declines to auto-approve a
  REJECTED vehicle — "a decision that four green steps do not overturn" — so approving a
  resubmission has to reopen it first. `VERIFICATION_REOPENED` keeps the trail honest when the
  approval that follows is then refused by AL-10, and the reopen can answer `409` because PENDING is
  inside `ux_vehicles_regno_active` and the plate may have been claimed (D-37).
- **Every document fetch goes through the audited route, and that is how AL-39's two halves hold
  together.** It asks for short-lived signed object-storage URLs *and* a `DOC_VIEW` row per read; a
  pre-signed bucket URL in `thumbUrl` would give the first and silently drop the second. So
  `thumbUrl`/`fullUrl` are `/v1/admin/documents/{docId}?variant=…`, and *that* route mints the
  signed URL and `302`s to it. One view is one row — opening a grid of four thumbnails records four,
  because each is a look at somebody's licence.
- **The rail speaks the vocabulary of the surface that produced the subject.** A Mode C vehicle's
  steps are AL-30's four saved rows and are authoritative; a fleet vehicle has none, so its rail is
  AL-50's named slots derived by registry-svc's own rule (verified iff no field of it is pending); a
  driver is the one synthetic `profile` step, because a decision rail with nothing in it reads as
  "nothing to check".
- **Δ AL-57: D-11 is retired outright and `merchantBound` is permanently `false`.** OnePay supports
  one merchant account per merchant, so the per-driver sub-account the bind assumed never existed;
  `POST /v1/internal/vehicles/{id}/merchant` is gone (C028) and the field is kept only so a portal
  built against the earlier contract still parses. It is marked `deprecated` in `admin-bff.yaml`.

### The driver's bank & payout profile (Δ AL-58 / AL-59)

- **A driver id names two subjects, and they are decided separately.** `/verification/{driverId}/…`
  is their **identity** — a licence and an NIC. `/verification/payout/{driverId}/…` is where their
  money goes. Sharing one verdict route, which is what the ADD's "the existing AL-39 queue, whose
  subject-agnostic routes already take a driver id" (§1.18 AL-58) reads as, would make one button
  decide two unrelated questions — and a refusal aimed at an illegible bank statement would refuse
  the driver's licence and stop them driving. The queue *family* is reused; the verdict could not
  be. **Micro-change-set raised.**
- **This queue's predicate is the profile's own status, and it has to be.** The other two tabs are
  "has a flagged `registry.document_fields` row", which is AL-27's fence as a query — but nothing
  extracts fields from a bank statement, exactly as for a fleet's. The partial index
  `ix_driver_payout_pending` (migration 0316) exists to serve this query and its comment names it.
  **Without this tab the gap was total**: a driver approved in March who changes banks in September
  has nothing pending on their licence, appeared in no queue at all, and payout-svc skipped them
  for ever while their wallet accrued.
- **The decision is forwarded; the identity verdict is not.** Both are registry tables, and the
  difference is what the write *is*. `driver_profiles.verified_at` is one column with no invariant.
  Approving a payout profile is BR-31.1's versioning transition — supersede the incumbent, **then**
  verify the replacement, one transaction, that order, because `ux_driver_payout_verified` admits
  exactly one verified row — and that invariant belongs to the service that owns the table, whose
  repository already holds its other half. fleet-svc made the same call for the identical table.
  `Upstreams:Registry` was already configured for the AL-30 recompute, so this costs no new setting.
- **`PAYOUT_PROFILE_APPROVED` on `driver_payout_profile`, not `VERIFICATION_APPROVED` on `driver`.**
  "I checked this statement against this account number and authorised money to be sent there" is a
  different claim from "I checked a photograph of a licence", and an auditor asking who approved
  paying this driver must not have to infer it from a decision about a photograph.
- **Approve is idempotent; Reject is not.** With nothing pending, Approve returns the current
  version unchanged — a second press must not rewrite `verified_at` or `verified_by`. Reject on an
  already-decided version is a `409`, because writing a refusal onto an approved one would stop a
  driver's payouts by a mis-click.
- **A rejection never disturbs the incumbent.** A mismatched account-holder name is a reason to
  refuse the *edit*, not a reason to stop paying somebody their wages — so a driver who mistypes on
  Friday is still paid on Sunday.
- **`approvable` means "there is a version awaiting a decision", not "the evidence is sufficient".**
  Whether a statement really shows this account is the judgement the officer is there to make; a
  rule that refused Approve without an upload would be this service overruling them.
- **The evidence needed nothing new.** A payout document is a `docs.uploads` row with no
  `registry.documents` row — exactly like AL-49's — and `FindDocumentSql`'s second branch already
  resolved it, so the audited lightbox and its `DOC_VIEW` row work here as they stand.

### The three directories (C064)

- **Each directory is gated on the URD §2.3 row whose cells are *exactly* the role list D3' prints
  for the route, with ◐ fenced by `RequirePlatformWideFeature`.** That is what makes the two
  documents agree instead of one overruling the other — and it is worth stating because the obvious
  row is the wrong one twice. Passengers → **Support** (not Passenger, whose PAX cell is ✅: gating an
  operator console on it would let any passenger list every passenger; and URD §2.4 gives the CSR
  "trip/user read-only lookup", which is this screen). Drivers → **Driver wallet & credit transfers**
  (not Driver-app, which gives Finance ➖ — and BR-28.8 names Finance, because half of SCR-AP-013 is
  the wallet ledger, the daily fee and the credit transfers). Vehicles → **Fleet live map &
  per-vehicle analytics** (not Fleet-operations, which is the Fleet Portal's write surface and also
  gives Finance ➖). `AdminMenu`'s three entries carry the same pairs and the same flag, because a
  nav item gated on anything else promises a screen the API refuses.
- **Two rows, two questions: one opens the directory, the other unmasks it.** Reaching the screen is
  the row above, held as Read. Seeing a mobile, an email or an NIC in the clear is
  **`account-management` · Write, held unscoped** — the row about people *as accounts*, whose two ◐
  qualifiers say who is bounded (`◐ verification` for the officer, `◐ on tickets` for the CSR) and
  therefore who sees the mask. So a Support CSR may open every record and read nobody's number, which
  is what BR-28.8's "PII fields render only for roles whose RBAC grant permits them" means once you
  ask *which* grant.
- **A list is masked for everybody, including the roles that may unmask a detail.** `admin-bff.yaml`
  says the clear number requires the audited detail read, and that is what makes the audit claim
  complete: every clear MSISDN this surface has emitted has a `PII_READ` row behind it. There is no
  response anywhere that carries the clear value beside the masked one — the portal is given one
  string, decided server-side.
- **One detail open is exactly one `PII_READ` row, and the row records whether anything was
  revealed.** Every permitted role can open every record, so the actor alone does not answer the
  question a privacy investigation asks. `piiRevealed` does, and it is a fact by then rather than an
  inference. A 404 records nothing: there was nobody to look at.
- **The vehicle detail writes one too, and that is a deliberate reading.** `server_db_schema.md` §23
  introduces `PII_READ` as "passenger/driver directory detail opened" and D3' marks only those two;
  URD §2.3's privacy clause requires a read-access entry for "all passenger/driver/**vehicle**
  directory lookups", and a vehicle resolves to a named owner. A second action would split one
  auditor question across two filters. **Micro-change-set raised.**
- **The page is chosen before anything is counted.** Each search filters and orders on its keyset
  index under a `LIMIT`, and only then joins the per-row facts (trip counts, plates, the owning
  organisation) by `LATERAL` — so the aggregates run at most `limit + 1` times whatever the size of
  the platform. That is what makes the DoD's 500 ms p95 a property of the query shape rather than of
  the current row count, and it is the thing to preserve: counting first and paging afterwards is
  the same answer at a cost that grows with the table.
- **`iam.users.role` decides which directory an account is in — deliberately not the choice C061
  made.** That component counts new riders out of `iam.user_roles` because a historical count must
  not move when somebody later signs up to drive. This one answers "which directory does this
  account live in", and an account lives in one: the union would put every driver who has ever
  booked a ride into the passenger directory a CSR is searching.
- **A Trips tab is a union of `rides.rides` and `trips.sessions`, on every surface that has one.** A
  Mode C driver's journeys are rides and a fleet driver's are sessions; a directory that showed only
  one would render an empty tab for every bus on the platform. **ride-svc ≠ trip-state-svc is about
  who writes the row**, not about what an operator may read back — and this reads both and writes
  neither.
- **A driver's status is derived and suspension outranks verification.** There is no status column:
  verified is `driver_profiles.verified_at`, suspended is `iam.users.is_blocked`, pending is neither.
  A driver approved in March and blocked in July is suspended, because that is the later fact and the
  one the row was opened to find.
- **Earnings are derived per vehicle, not read from `fares.driver_earnings`.** That rollup is keyed
  `(driver_id, earn_date)`, so a vehicle two drivers shared on one day appears in it twice under
  neither of them. The tab sums the settled payment of each of the vehicle's rides — one row per ride
  over D-10's retry chain — bucketed by the Colombo business date. The settled-state list is
  **C061's `AnalyticsVocabulary.SettledPaymentStates`, used rather than copied**: this process
  already hosts that read model, and a second literal would be the copy nobody notices drifting.
- **The document grid mints C063's audited links.** `thumbUrl`/`fullUrl` point at
  `GET /v1/admin/documents/{docId}`, never at the bucket — a directory that handed out pre-signed
  URLs would be a second door onto the same documents with no `DOC_VIEW` row behind it.
- **Read-only is asserted against the route table.** `No_directory_route_accepts_a_write` enumerates
  every route under the three prefixes and allows exactly the two C062 suspensions, so a later
  component cannot hang a write off a directory path without the suite failing.

### Finance and data rights (C065)

- **This surface moves money and writes none of it.** `FinanceRepository` has no `INSERT` and no
  `UPDATE` — asserted as a class, and `Only_two_finance_routes_mutate_and_both_forward` asserts it
  against the running route table. The reversal is posted by **wallet-svc**, the only writer of
  `billing.journal_postings`, whose own file names admin-bff as the caller entitled to
  `kind='adjustment'`; the refund is raised by **fare-svc**, which owns `fares.refunds`, the
  balanced entry and the gateway reverse call; a fraud flag is resolved by **reputation-svc**. What
  this component contributes is the RBAC gate, the queue the decision is made from, and the D-35 row
  saying a human made it.
- **Four URD §2.3 rows on one screen, and the obvious single row is wrong three times.**
  Reconciliation and the transactions report are **Finance** (CSR and Verification Officer ➖ — a CSR
  investigating a ticket has the passenger directory and no business reading the platform's
  settlement position). The reversal is **Driver wallet adjustments / reversals**, the one row that
  exists for this button and whose cells are ✅ for exactly Super Admin and Finance — so C065's fence
  ("Finance/Super-Admin only") is not a role list written into a route, it is what the matrix already
  says, and there is no ◐ in the row to fence. Refunds are **Refunds**, whose CSR cell
  `◐ raise/recommend` opens the queue and withholds the button without any platform-wide fence,
  because `PermissionCell.Parse` already trades Write for Raise. **Admin holds ✅ on refunds and 👁 on
  reversals**, and the difference is deliberate: giving a passenger back their own fare is not the
  same authority as putting credit into a driver's wallet.
- **The two review queues are gated where they belong, not where they were built.** ADD §6 lists the
  refund, document-expiry and fraud-review queues together on admin-bff's row, so all three are here
  — but an expiring insurance certificate is a document review (**Verification**, and E-03's expiry
  is what AL-10's gate turns on) and a confirmed E-07 signal leads to a suspension
  (**Moderation**). Gating either on Finance would hand the screen to the one role that has no use
  for it and take it from the two that do.
- **The reversal's ledger key is the business fact, so one charge is reversed once, ever.**
  `adjustment:fee_reversal:{driverId}:{vehicleId}:{feeDate}` collides on
  `billing.journal_entries.idempotency_key`, so a double click answers `replayed: true` with the
  original entry — which is why this route needs no command log. The bound is the right one: a
  second correction on the same day is an adjustment somebody has to argue for rather than press
  twice. **The audit row is written either way**, because what D-35 records is that an operator
  performed the action, not that the ledger happened to be in a state where it had an effect.
- **The refund queue is a union, and the second half is the point.** §11.14's late callback writes
  `fares.ride_payments.state = 'Overpaid'` *and* a `fares.refunds` row — but a payment that reached
  Overpaid with no refund row is exactly the R-19 failure the queue exists to catch, and a list of
  raised refunds would hide it. The Overpaid half excludes payments a refund already covers, so a
  normally-handled callback appears once.
- **The exception queue's four classes are derived, never stored.** wallet-svc refuses a callback
  whose amount disagrees with its session, logs the numbers and leaves the session `Pending` — there
  is no exception column, and adding one would give this component a write on another service's
  table. So `amount-mismatch` / `settled-not-posted` / `unsettled` / `gateway-failed` are a `CASE`
  over the state, the ledger and the clock, and a session that resolves itself leaves the queue with
  nobody having to close it. A lost callback and a refused mismatch are **both** `unsettled` because
  the schema cannot tell them apart, and the operator is told that rather than shown a guess.
- **`AdminBff:Finance:SettlementGracePeriod` is deliberately not D6' §7.1's 90 seconds.** That window
  is how long a client polls before falling back; a session still open a minute later is somebody
  typing a card number. An hour later it is a lost callback. Using the gateway's own number would
  fill Finance's queue with people who are still paying.
- **The transactions report reads the journal, not `billing.wallet_transactions`.** The projection is
  one row per *account leg*, so a driver-to-driver transfer appears twice and a report that summed it
  would double the platform's transfer volume. One entry, two parties named by the sign of their leg
  — one projection for all four kinds rather than four `CASE` arms to revisit when a kind is added.
- **The JSON, the CSV and the PDF are one query.** "The export matches the screen" is structural, not
  a coincidence two queries share — C061's CSV is written under the same rule. Both files carry a
  preamble naming the window, the timezone and the row count, because a figure with no stated window
  is unfalsifiable once the request that produced it is gone.
- **The PDF is written by hand and that is cheaper than it sounds.** A table in one of the base-14
  fonts every conforming reader must have needs no embedded font and no library on
  `Directory.Packages.props` — and the byte offsets are *measured* rather than computed, because a
  file whose xref is one byte out opens in a forgiving reader and fails in a strict one, which is the
  worst way for it to be wrong. **It must not be extended to anything trilingual**: WinAnsi has no
  Sinhala or Tamil glyphs, and faking them is worse than the 415 wallet-svc answers (D-26).
- **An erasure is a soft anonymisation, and the hold list has two kinds of entry.** A *blocking* hold
  (an in-flight ride, an open dispute, an unsettled payment, money still in the wallet) is a live
  operation that anonymising would break; it answers 409 and lifts on its own. A *retention* hold
  (the ledger, the audit trail) is a record a statute requires be kept and is what turns `Fulfilled`
  into `FulfilledHold`. Treating them alike would either refuse every erasure for ever — every
  account has a ledger — or anonymise somebody who is in a car right now.
- **`audit.events` is never touched and the fulfilment writes to it.** A right-to-erasure that
  deleted the record of the erasure would leave the platform unable to prove it complied. The
  retention is *declared* on the request rather than being silent, because a subject is entitled to
  know what was kept and on what basis.
- **`phone` becomes NULL and `email` becomes a per-account `.invalid` address, and neither is
  cosmetic.** `ck_users_credential` requires one of the two, so both cannot be cleared; both are
  UNIQUE, so a shared placeholder would let the first erasure block every one after it. RFC 2606
  reserves `.invalid` for a value that must exist and must not resolve, and no credential row
  survives, so it cannot be signed in with.
- **`iam.users.anonymised_at` is what makes C064's `deleted` producible.** That handoff recorded a
  `PassengerRow.status` value nothing could produce and left the enum for this component; migration
  0110 is the column and the passenger directory now derives it. Deliberately not `is_blocked`:
  blocking is a moderation decision an admin can undo, and an erasure is neither.
- **The data subject's own POST writes an `audit.events` row**, which nothing else on this surface
  does. A statutory clock that starts when somebody presses a button in the Passenger App needs the
  press in the same immutable log the fulfilment lands in, or the platform can prove it acted and
  cannot prove when it was asked. `actor_role` is `passenger` or `driver` on exactly these rows.
- **A request that is not yours is a 404.** Telling a 403 from a 404 would make the status route an
  oracle over whether a given id is somebody's live erasure request — wallet-svc's house rule for
  credit transfers, and it matters more here.
- **A decided request is a 409, not an idempotent no-op.** Fulfilling an already-rejected erasure
  would anonymise an account whose owner was told their request was refused; rejecting an
  already-fulfilled one would record a refusal of something that has already happened.
- **The archive is written before the transaction opens.** Assembling fourteen datasets and pushing a
  ZIP to a bucket inside a transaction would hold a Postgres write transaction across a network round
  trip. The failure mode of this order is an orphaned object the bucket's lifecycle rule sweeps; the
  other order's is a lock on `pdpa.requests` for as long as the bucket is slow.
- **An export archive is `ephemeral`, and that is a decision.** Any non-null `Retention` puts the
  object under the prefix D-36's one lifecycle rule matches. It is emphatically not `Retained`, which
  is for objects the platform keeps *serving* — a copy of everything held about one person must not
  become a permanent second copy.

### Configuration surfaces

- **A tariff version is inserted, never updated, and never backdated.** D-10 makes a published rate
  permanent — a completed ride must stay reconcilable against the rate that priced it — so
  `effectiveFrom` in the past is a 400 rather than a repricing of quotes already given.
- **A peak window may end before it starts.** The night window wraps midnight and migration 1001
  declines to constrain the ordering for exactly that reason; nothing here may "fix" it.
- **Null windows leaves them alone; an empty list clears them.** Two different intents, and a PUT
  that treated them the same would drop the night window every time somebody changed a per-km rate.
- **A city needs all three languages.** `GET /v1/config/cities` serves the row to a first-run screen
  in whichever language the handset is set to; two names is a city that renders blank for some
  passengers (D-26).
- **A train is a Mode A `registry.vehicles` row, and admin-only is enforced by absence.** AL-09 puts
  `train` in the canonical enum and the Driver App's and Fleet Portal's own enums exclude it, so the
  only path that can insert one is `TrainRepository`. Retirement is `status = 'DEACTIVATED'`, never a
  delete: historical trips still resolve, and D-37's live-set index releases the number for a
  successor.
- **The registering admin owns the train row.** `registry.vehicles.owner_id` is `NOT NULL` and
  references a real account; a synthetic "platform" user would be an account with credentials nobody
  holds and a row every directory in C064 would special-case.
- **Suspension is `dispatch_state`, not `status`.** `DISPATCH_SUSPENDED` is E-03's "do not offer
  rides to it" and is what dispatch-svc's candidate query excludes; `DEACTIVATED` is the end of a
  registration, which is what US-12.6's third confirmed report reaches — and safety-svc reaches it.
  Retiring the registration instead would burn the plate under D-37 and make reinstatement a
  re-registration.

## Promoted into the kernel

Three moves, each because a second copy is how two components start disagreeing.

| Moved | From | Why |
|---|---|---|
| `PermissionMatrix`, `PermissionModel`, `PermissionEvaluator`, `FeatureAuthorization` | `Iam.Api/Rbac/` | URD §2.3 is a rule **two services must agree on**. `PermissionMatrixTests` moved with it and still parses §2.3 out of the URD, so the kernel proves the table and each service proves its endpoints. `IPolicyEvaluator` was renamed `IPermissionEvaluator` on the way — the framework has an `IPolicyEvaluator` of its own in the same problem domain. `FleetMembership` became `MageRide.Shared.Auth.FleetScope`. |
| `IAuditEventWriter` / `AuditEventWriter` | `Reputation.Api` + `Transit.Api` | **The C057 handoff asked the third caller to do this by name.** `audit.events` is shared and append-only; both services now use the kernel's writer and keep only their own action vocabulary. |
| `TimeOnlyTypeHandler` | new | Dapper has no `TimeOnly` mapping, exactly as it had none for `DateOnly`. `fares.peak_windows` is the first `TIME` column anything writes. |

**One kernel behaviour changed:** `PermissionCell.Parse("✅")` now yields
`Read | Write | Configure`. The legend reads "✅ Full (create/edit/execute)" against "⚙ Configure
(settings only)" — Full is the broader authority in the same area, and changing a setting is
editing one. Without it a Super Admin (✅ on both Platform-config rows) is refused a screen the
Admin beside them (⚙) may use, which URD §2.4 rules out in as many words.

## Schema this service added

| Migration | Object | Why |
|---|---|---|
| `0202` | `config.feature_flags` | URD §2.3 gives feature flags a whole matrix row and US-14.12 an Admin Portal Config surface; **no spec prints the DDL**. The other three configuration surfaces the deliverable names already have tables and owners — `fares.tariffs` (1001), `billing.plans` (1103), `dispatch.level_config` (0713) — and this is the fourth |
| `1312` | `audit.events.event_id` · `.actor_role` · `.ip` · `.detail` | §15 prints eight columns and the D-35 interceptor records four more. Each is named by a document — D6' §2.2's envelope id, `admin-bff.yaml#AuditEvent`'s `actorRole`/`ip`, the deliverable's "before/after, ip" — and 1305 has nowhere to put any of them. `ip` is `TEXT` and not `INET`: an audit trail that refuses a value it cannot parse records less than one that writes down what it was handed |
| `0315` | `registry.driver_profiles.rejection_reason` | **C063.** AL-39's family is subject-agnostic and US-2.15 makes a rejection reason mandatory and shown to the applicant. Two of the three subjects already have a column — `registry.vehicles` (0303) and `registry.fleets` (0301) — and the driver did not, so an officer's refusal of a licence could be recorded in `audit.events` and never shown to the driver it was about. No companion timestamp and no `status`: the state is derived (`verified_at` ⇒ APPROVED, else this ⇒ REJECTED, else PENDING), exactly as `onboarding_status` is |
| `0109`, `0317`, `0507`, `0610`, `1110` | five directory read-path indexes | **C064.** No new table and no new column: three directories over other services' tables need ordering keys and two reverse lookups nobody had needed before. `iam.users(role, created_at DESC, id DESC)` is both people-directories' keyset; `registry.vehicles(created_at DESC, id DESC)` is the vehicle directory's — 0303's three indexes all *find* a vehicle and none orders by registration date. `trips.sessions(driver_id, started_at DESC)` because `ux_sessions_active_driver` is partial on ACTIVE and holds at most one row, which is the opposite set; `rides.rides(accepted_vehicle_id, …)` because 0601 indexes a ride by *person* and SCR-AP-014/015 asks by vehicle; `billing.daily_fee_charges(vehicle_id, fee_date DESC)` because the `(driver_id, vehicle_id, fee_date)` PK cannot serve a vehicle-first read as a prefix |
| `0110` | `iam.users.anonymised_at` | **C065.** E-06's erasure is a *soft* anonymisation — the row survives so every ride, posting and audit event referencing it still resolves — and nothing in §1 recorded that it happened. Two consequences: `admin-bff.yaml`'s `PassengerRow.status` carried a `deleted` value nothing could produce (the C064 handoff records exactly that and leaves the enum for this component), and a second erasure request would re-anonymise an account and report it as fresh work. Not a `status` column and not `is_blocked`: blocking is a moderation decision an admin can undo |
| `1008`, `1111` | two finance read-path indexes | **C065.** No new table and no new column. `fares.ride_payments(created_at) WHERE state='Overpaid'` because 1002's three indexes start from a ride, a QR claim or the retry chain and none can answer "everything currently Overpaid, oldest first", which is the refund queue's second half; `billing.topups(method, created_at DESC)` because 1107's three all start from one session and SCR-AP-006 starts from a **rail and a day** |
| `1314` | `pdpa.requests.decided_by` · `.decision_reason` + `ix_pdpa_requests_decided` | **C065.** §16 gives the table a `Rejected` status and D3' a `/reject` route whose body the contract types as a **required** reason — with no column to put it in. `hold_reason` is not it: `ck_pdpa_requests_hold` ties that column to `FulfilledHold`, the opposite outcome, and one field could not carry both without the SLA queue losing the difference between a partial fulfilment and a denial. `decided_by` is the same gap on the other axis — `audit.events` records who called the route, and neither the subject's status read nor the queue can join it |
| `1409` | `registry.vehicles.default_route_id` | `TrainInput.routeId` had nowhere to live. **Not** the same question as `trips.sessions.route_id`: that is the line a *journey* ran, this is the line the vehicle is *registered for* (US-2.17), and nothing derives one from the other. In the 14xx range because the FK points at `spatial.routes`, which 1401 creates |

## Not here, and named rather than stubbed

- **Sign-in.** iam-svc's `POST /v1/admin/auth/login`, and the failed-attempt lock-out and IP
  allow-list with it (AL-07, C026). A second credential path in the BFF would be a second place a
  password could be checked and a second place the lock-out could be forgotten.
- **RBAC user/role provisioning.** iam-svc's `/v1/admin/rbac/**` (C027), routed there by the gateway
  at Order 20. This service contributes the Access nav group, which is what makes it one console.
- **Daily-fee rates, bulk-voucher tiers, Driver-Level parameters.** subscription-svc's
  `/v1/admin/fees/rates` and `/v1/admin/voucher-discount-tiers`, dispatch-svc's
  `/v1/admin/drivers/level-config`. Same shape, same reason.
- **Every movement of money C065's screens decide (C065).** wallet-svc posts the reversal, fare-svc
  raises the refund and its balanced entry and calls the gateway, reputation-svc resolves a fraud
  flag, payout-svc runs the weekly sweep. This surface reads, gates and audits.
- **The images behind an export archive (C065).** `documents.json` lists what is on file — kind,
  dates, status — and the scans themselves are not enclosed: an archive assembled in one request
  cannot carry a folder of photographs, and a PDPA download that timed out would be a fulfilment that
  did not happen. The ZIP's own `README` says they are available through support. Named in the C065
  handoff as the one thing the export names rather than includes.
- **A sweeper for expired export archives (C065).** The bucket's own lifecycle rule is the deadline
  (the object is written under `ephemeral/`), which is D-36's whole design — a second deleter in this
  service would be a process that has to be right about somebody else's retention policy.
- **Anything that erases a *driver's* operational record (C065).** An erasure anonymises the identity
  on `iam.users` and `registry.driver_profiles` and leaves vehicles, documents, rides and the ledger
  where they are: they are a fleet's, a passenger's and a statute's records as much as the driver's.
  What stops the account being used is the session revocation, not a cascade.
- **Two of the three `rides` columns the C032 handoff named for this component (C065).**
  `rides.rides.rider_phone_hash` **is** cleared, where the subject is the rider rather than the
  booker who typed somebody else's number. The other two are deliberately not:
  `rides.rides.recipient_phone` is a **third party's** number on a delivery the subject booked — it
  is the recipient's data, not the subject's, and `ck_rides_package_recipient` makes it `NOT NULL`
  for a package ride, so clearing it would break the record of a delivery that happened.
  `rides.proof_artifacts.storage_url` points at bytes in the D-36 bucket under NFR-28's own
  `ephemeral/` lifecycle rule, which already deletes them on a deadline this service does not own.
  Both are recorded in the C065 handoff rather than done quietly.
- **Δ D-36 is wired (C063).** With `Storage:S3:*` configured, `GET /v1/admin/documents/{docId}`
  records its `DOC_VIEW` row and 302s to **the bucket's own presigned GET** — a SigV4 signature the
  storage provider verifies, with the TTL enforced by the provider and no MageRide process carrying
  the bytes. The HMAC-over-a-pointer path below is the fallback for a deployment with no bucket, and
  only that. This service still writes no bytes; it holds the store only to presign a read.
- **The object store itself (superseded).** This service mints the signed
  URL and redirects to it; it never holds a byte, which is what keeps an unredacted document on the
  far side of the perimeter. `AdminBff:Documents:PublicBaseUrl` is what makes the redirect
  resolvable, and its absence is an ERROR at start-up rather than an invented host.
- **The OnePay merchant onboarding D-11 needs (C063).** registry-svc owns the bind and requires a
  `merchantId`; no component on this build map produces one. `merchantBound` answers `false` and
  fare-svc answers `402 merchant-not-onboarded` until one does.
- **A command log.** Every mutation this service owns is idempotent by shape — a suspension is an
  upsert of a state, a tariff publish keys on `(vehicle_type, effective_from)`, a feature flag is an
  upsert — and the forwarded ones carry the caller's `Idempotency-Key` to services that own their
  own. A fourteenth instance of D4' §5's gap would guard operations that cannot double-apply. The
  two `create` paths (city, train) answer a well-defined 409 on a replay rather than the original
  201; recorded in the handoff as the cost.
- **An outbox.** The durable record is the `audit.events` row; the topic is a sink. An outbox would
  add a table and a dispatcher to guarantee delivery of a copy.
- **A Dockerfile.** `infra/docker-compose.dev.yml` already carries an `admin-bff` cluster
  destination pointing at the combined `app-services` container.

## Configuration

Every knob is documented at its declaration in `AdminBffOptions` and in `infra/env/.env.app.example`.

| Setting | Default | Where it comes from |
|---|---|---|
| `Audit:Topic` | `audit.events` | D7' §4.2 `Audit__Topic`, D6' §2.1 |
| `Audit:PublishToTopic` | on | **off ⇒ the D6' §2.1 sink receives nothing.** D-35 is unaffected — the row is the log |
| `Audit:TrustForwardedFor` | on | every request arrives through the C008 gateway (as iam-svc's own flag) |
| `AuditLogDefaultWindow` | 30 d | **no spec** — the table is append-only and unbounded |
| `Upstreams:{Safety,Support}:BaseUrl` + `:InternalApiKey` | — | C052/C053's `/v1/internal/**` planes. Unset ⇒ 503 on those routes |
| `Upstreams:{Content,Transit}:BaseUrl` | — | role-gated `/v1/admin/**`; the caller's bearer is forwarded, no key |
| `Upstreams:Registry:BaseUrl` + `:InternalApiKey` | — | **C063.** AL-30's recompute. Unset ⇒ a confirmed field never reaches registry-svc and a Mode C vehicle can never be approved |
| `Upstreams:Fleet:BaseUrl` + `:InternalApiKey` | — | **C063.** The fleet-org queue, AL-49's payout approval, AL-50's gate. Unset ⇒ no organisation can be approved, so nothing can go Paid |
| `Upstreams:*:Timeout` | 5 min | **no spec** — bounded by the 200 MB GTFS upload (BR-32.1), not by the queue reads |
| `Documents:PublicBaseUrl` | — | **C063, no spec.** Where the D-36 store is reachable from a browser. Unset ⇒ the stored pointer is redirected to unchanged; the DOC_VIEW row is unaffected. ERROR at start-up |
| `Documents:SigningKey` | *(per process)* | **C063, no spec.** Unset ⇒ a URL minted by one replica does not verify on another. Warned |
| `Documents:UrlTtl` | 5 min | **C063, no spec** beyond AL-39's "short-lived" |
| `Upstreams:Wallet:BaseUrl` + `:InternalApiKey` | — | **C065.** The ledger seam US-14.11 posts through. Unset ⇒ no driver can be given back a fee they were wrongly charged |
| `Upstreams:Fare:BaseUrl` | — | **C065.** E-05's refund execution — role-gated, so the caller's bearer is forwarded and there is no key. Unset ⇒ the queue still reads and only the decision 503s |
| `Pdpa:DueDays` | 30 | **C065.** D7' §4.2's `Pdpa__DueDays`, finally landing. The deadline itself is `pdpa.requests.due_by`'s column default (1306); this is what the 202 reports before the row is read back |
| `Pdpa:ArtifactUrlTtl` | 15 min | **C065, no spec.** An export archive is a copy of everything held about one person; the link is minted fresh on every status read |
| `Pdpa:MaxRowsPerDataset` | 10 000 | **C065, no spec.** The archive is assembled in memory. Truncation is recorded in the ZIP's manifest, never silent |
| `Finance:SettlementGracePeriod` | 1 h | **C065, no spec.** Deliberately not D6' §7.1's 90 s — that window is how long a client polls, and a session open a minute later is somebody typing a card number |

`ConnectionStrings:Postgres`, `Jwt:*` and `Kafka:BootstrapServers` are required through the kernel.
There is no `ConnectionStrings:Redis` and no `Outbox:*`, and there must not be. The `Analytics:*`
section (C061) is read by this process, because this is the process that hosts the read model.

**Three of D7' §4.2's six admin-bff variables are not this service's.** `Login__MaxFailedAttempts`,
`Login__LockoutMinutes` and `Login__IpAllowList` belong to iam-svc, which owns every credential
path; `Rbac__DenyByDefault` is not a switch at all — deny-by-default is the kernel's fallback policy
plus a per-route gate, and nothing reads a flag. Micro-change-set raised. **Δ C065: the sixth,
`Pdpa__DueDays`, now lands here** — on `AdminBff:Pdpa:DueDays`, and it reports rather than decides
the deadline, which migration 1306's column default owns.
