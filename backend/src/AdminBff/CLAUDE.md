# admin-bff (C062 admin-bff-core + C063 admin-bff-verification) — the Admin Portal's back-office BFF

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002) and
`Analytics` (C061, a library — not a service). **No Redis, no consumer, no outbox, no command
log** — see `AdminBffApplication` for why each is off.

**Verify:** `dotnet test backend/src/AdminBff.Tests -c Release`

`backend/contracts/admin-bff.yaml` is normative for this surface and wins over this file and over
the code.

## What this service is

The single back-office front door for all six internal roles (AL-02, `admin.mageride.lk`). C062 is
its foundation and **C063 is the Verification Officer's whole surface**; C064 (directories) and C065
(finance/PDPA) map onto the same group in `AdminEndpoints` and inherit both fences without touching
either.

| Endpoint | URD §2.3 gate | Spec |
|---|---|---|
| `GET /v1/admin/session` | authenticated internal | **Δ C062** — URD §2.2, AL-37 |
| `GET /v1/admin/verification/queues/{driving-license,vehicle-registration,fleet-org}` | verification · read | AL-39, SCR-AP-003 |
| `GET /v1/admin/verification/{subjectId}` · `/org/{orgId}` | verification · read | AL-39, SCR-AP-003a/003c |
| `PUT /v1/admin/verification/{subjectId}/fields/{fieldKey}` | verification · write | AL-29, US-2.4a/2.10a |
| `POST /v1/admin/verification/{subjectId}/approve` · `/reject` | verification · write | US-2.9, US-2.15, AL-49 |
| `GET /v1/admin/documents/{docId}` | verification · read | AL-39, SCR-AP-003b, US-24.8 |
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
- **D-11's merchant bind is not performed and `merchantBound` is always `false`.** registry-svc's
  route requires a `merchantId` and nothing on this platform onboards one; claiming otherwise would
  say fare settlement has a payee. Logged at every approval, and raised in the C063 handoff.

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
- **The three directories (C064), finance and PDPA (C065).** Separate components on this project.
  `AdminMenu` already carries their nav entries, because the portal's shell must not depend on build
  order.
- **The object store itself (C063).** D-36's SSE-KMS bucket is C125's. This service mints the signed
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

`ConnectionStrings:Postgres`, `Jwt:*` and `Kafka:BootstrapServers` are required through the kernel.
There is no `ConnectionStrings:Redis` and no `Outbox:*`, and there must not be. The `Analytics:*`
section (C061) is read by this process, because this is the process that hosts the read model.

**Three of D7' §4.2's six admin-bff variables are not this service's.** `Login__MaxFailedAttempts`,
`Login__LockoutMinutes` and `Login__IpAllowList` belong to iam-svc, which owns every credential
path; `Rbac__DenyByDefault` is not a switch at all — deny-by-default is the kernel's fallback policy
plus a per-route gate, and nothing reads a flag. `Pdpa__DueDays` is C065's. Micro-change-set raised.
