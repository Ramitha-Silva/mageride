# fleet-svc (C058 + C059) — the whole Fleet Portal: the organisation, and the operations on top of it

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002).
**No Redis, no Kafka and no outbox** — see `FleetApplication` for why each is off.

**Verify:** `dotnet test backend/src/Fleet.Api.Tests -c Release`

`backend/contracts/fleet.yaml` is normative for this surface and wins over this file and over the
code.

## What this component is, and what it is not

C058 is the **identity** half of the Fleet Portal: who the organisation is, who may act for it, and
where its money goes. **C059 is the operations half** — Mode A/B vehicle onboarding with AL-50's
named document slots, time-bounded driver assignment, tracker binding, scheduling with the
US-13.11 not-started alarm, the live map, analytics, geofences and the Epic 23 subscription
proxies. C060 is the billing.

| Endpoint | Auth | Spec |
|---|---|---|
| `POST /v1/fleets` | Bearer `fleet_owner` | US-13.A7 |
| `GET /v1/fleets/{fleetId}` | any sub-role | D3' fleet-svc route table |
| `POST /v1/fleets/{fleetId}/members` | owner | US-13.A5 |
| `GET /v1/fleets/{fleetId}/members` | any sub-role | **Δ C058** — provisioning had no read-back |
| `GET` · `PUT /v1/fleets/{fleetId}/payout-profile` | owner | AL-49, SCR-FP-002a |
| `POST /v1/fleets/{fleetId}/payout-profile/documents` | owner | AL-49 |
| `POST` · `GET /v1/fleets/{id}/vehicles` | manager · viewer, **approved org** | US-13.1; the GET is **Δ C059** |
| `DELETE /v1/fleets/{id}/vehicles/{vid}` | manager | US-13.7 |
| `POST /v1/fleets/{id}/vehicles/bulk` · `GET /bulk/{jobId}` · `GET …/errors.csv` | manager · viewer · **the signature** | US-13.1; the last two are **Δ C059** |
| `GET` · `POST /v1/fleets/{id}/vehicles/{vid}/documents` | viewer · manager | AL-50, SCR-FP-004 |
| `PUT /v1/fleets/{id}/vehicles/{vid}/classification` | manager | AL-24 item 16b, BR-31.1 |
| `POST` · `GET /v1/fleets/{id}/assignments` · `DELETE /{id}` | manager · viewer · manager | US-13.2/13.8; the GET is **Δ C059** |
| `POST /v1/fleets/{id}/trackers/bind` | manager | US-13.12 — forwarded to provisioning-svc |
| `POST` · `GET /v1/fleets/{id}/schedules` | manager · viewer | US-13.11; the GET is **Δ C059** |
| `GET /v1/fleets/{id}/map` · `/analytics` · `/alerts` | viewer, **not approval-gated** | US-13.3/13.4/13.5 |
| `PUT` · `GET /v1/fleets/{id}/geofences` | manager · viewer | US-13.5; the GET is **Δ C059** |
| `…/vehicles/{vid}/requests`, `/subscribers`, `…/payments/{id}/confirm` | manager · owner | Epic 23 — proxied to subscription-svc |
| `GET /v1/internal/fleets/queue` · `/{fleetId}` | internal | **Δ C058** — AL-39's fleet-org queue |
| `POST /v1/internal/fleets/{fleetId}/approve` · `/reject` | internal | **Δ C058** — AL-39, AL-49 |
| `GET` · `POST /v1/internal/fleets/{id}/vehicles/{vid}[/approve\|/reject]` | internal | **Δ C059** — AL-50's gate |

| Table | Read | Written |
|---|---|---|
| `registry.fleets` | every route | **this service** |
| `iam.fleet_members` | every route | **this service** — iam-svc reads it for the claim (C027) |
| `iam.users` / `iam.user_roles` | the team view, the assignment's driver | **iam-svc**; this service only *provisions* a sub-user |
| `registry.fleet_payout_profiles` | this service, subscription-svc (C050 `payTo`) | **this service** |
| `registry.vehicles` | through `registry.fleet_vehicles_fleet` only | **this service** and registry-svc — the two do not overlap, see below |
| `registry.fleet_vehicles` | the roster view | **this service** |
| `registry.fleet_assignments` | `…_fleet`, and `driver_eligible_vehicles` platform-wide | **this service** |
| `registry.documents` / `document_fields` | the slots, the officer's queue | **this service** for fleet rows; registry-svc for driver rows (XOR) |
| `registry.fleet_schedules` · `fleet_bulk_jobs` · `fleet_bulk_job_rows` | the portal, the sweep | **this service** |
| `spatial.geofences` | the org's own | **this service**, for `fleet_id IS NOT NULL` only |
| `telemetry.positions` · `trips.sessions` | `_fleet` views only | **the hot path** / **trip-state-svc** |
| `docs.uploads` | the officer's queue detail | **this service**, for the AL-49 and AL-50 kinds |
| `registry.fleet_command_log` | the kernel | **this service** |
| `subscription.*` | — | **subscription-svc** — proxied, never touched |

## The four fences, and how each is held structurally

- **A fleet operates Mode A and/or Mode B — never Mode C (AL-03).** Held in the database:
  `registry.fleet_vehicles.mode CHECK (mode IN ('A','B'))` (migration 0306). `FleetModes` names the
  two and has no third constant, and `/classification` refuses anything that is not Mode B outright
  — `mode_b_billing` is NULL for Mode A and C by design (AL-24), and a bus has no subscribers.
- **An unapproved org onboards nothing (US-13.A7).** Held by the *route group*, not by each handler:
  `FleetEndpoints` builds `/v1/fleets/{fleetId}/vehicles` and `/v1/fleets/{fleetId}/assignments`
  with `.RequireApprovedFleet()`, so **a route C059 adds to either is gated the moment it is
  mapped**. `Every_vehicle_and_assignment_route_is_gated` walks the endpoint data source and fails
  for the *next* component's mistake, which is the point. **C059: map your routes on those groups.**
- **A cross-org read is a security bug.** Held in Postgres — migrations 1806 and 1807's RESTRICTIVE
  policies over the nine org-owned relations, entered through `SET LOCAL ROLE
  mageride_fleet_reader`. The repositories' own `WHERE fleet_id = @FleetId` is the second lock,
  never the only one, and `RowLevelSecurityTests` asserts the first one by connecting as a real
  non-superuser login with no fleet-svc code in the path.
- **A required AL-50 document slot holds a vehicle out of APPROVED (C059).** Registration,
  insurance and revenue licence for every mode, plus the route permit for Mode A (US-27.3). Held in
  `VehicleApprovalService`, which re-derives every slot from `registry.documents` **inside the
  transaction that writes the status** — so a permit that lapsed while the officer's queue item sat
  there stops the approval. There is no `docs_status` column, deliberately: it would be a copy of
  this answer, made earlier.

## Rules that are load-bearing

- **The token's `fleet_role` claim is not the authority; the membership row is.** A person may
  belong to several organisations and iam-svc puts *one* pair in the token — the most privileged
  (C027 `FleetMembershipAsync`). So `FleetAccessFilter` reads `iam.fleet_members` for the org in the
  **path** on every request, and the claim does nothing but get past deny-by-default authorization.
  An Owner of fleet A arriving at fleet B is `403 not-fleet-member`; a Viewer with a token that says
  `owner` is `403 fleet-role-insufficient`.
- **404 on the organisation, 403 on everything inside it.** A fleet id is a UUID nobody guesses, so
  "no such organisation" leaks nothing. Inside one, a non-member must not be able to tell an id that
  exists from one that does not — which is why the membership check comes second and every later
  refusal is a 403.
- **Reading is not gated on approval; onboarding is.** A PENDING org's owner has to be able to see
  that it is pending, and — deliberately — to edit the payout profile, because the payout documents
  are part of what the Verification Officer reads *before* approving (AL-49). Gating that would mean
  approving an organisation before seeing the evidence you approve it on.
- **The organisation and its owner's seat commit together.** A `registry.fleets` row whose registrant
  has no `iam.fleet_members` seat is an organisation nobody can open, including the person who just
  created it.
- **A sub-user holds the canonical `fleet_owner` role.** URD §2.1 makes Owner/Manager/Viewer "an
  org-scoped sub-model of the Fleet Owner role" and C027's `PolicyEvaluator` narrows the
  `fleet_owner` column and only that one — a Viewer with no canonical role would be narrowed from an
  empty cell and hold nothing. An existing driver or passenger keeps their primary `iam.users.role`;
  what is added is the `iam.user_roles` grant, because AL-06 makes permissions the **union**.
- **No credential is set when a member is provisioned, and nobody is told.** AL-07's three sign-in
  methods are iam-svc's (C026). The *invitation* is missing platform-wide — no fleet-org template
  exists in `content.notification_templates` (migration 1904) — so the owner passes the address on
  out of band and the service logs it once per new account.
- **A second Owner cannot be provisioned here.** US-13.A5 gives the Fleet Owner "Manager and Viewer";
  a second Owner is a change of who the organisation belongs to, and `registry.fleets.owner_id` —
  which nothing on this route rewrites — says it is not this route's to make.
- **Provisioning the same person twice is `409`, not a silent promotion.** `ON CONFLICT DO NOTHING`,
  because changing somebody's seat is a decision with its own audit story.
- **The payout table is a version history, and an edit never redirects money.** BR-31.1 in two
  halves. An edit to a `verified` profile **inserts** a new pending row and leaves the incumbent
  verified and collecting, so subscription-svc's `payTo` keeps rendering the account an officer
  approved. An edit to a profile that is still *pending* updates in place — nothing is collecting
  against it and a version marks a verification decision, not a keystroke.
- **A document is an edit too.** BR-31.1 says "any edit", and replacing the bank statement behind a
  verified profile is exactly the change an officer would want to see again. So an upload against a
  verified profile forks a new pending version, carrying the other slot forward.
- **`bank_statement` and `passbook_first_page` share one column.** BR-31.1 asks for one *or* the
  other; §26 gives them `proof_upload_id` and the LankaQR image `lankaqr_upload_id`. Uploading a
  passbook after a statement replaces it, which is what somebody correcting a blurred photograph
  expects.
- **The officer's approval supersedes before it verifies.** `ux_payout_profile_verified` admits one
  verified row per org, so the other order fails on the index. Said out loud on the index's own
  comment (migration 0313) rather than left to a 23505.
- **A rejection never disturbs the incumbent.** A mismatched account-holder name is a reason to
  refuse the *edit*, not a reason to stop an organisation collecting against approved details.
- **Approve is not once-only.** An APPROVED org whose owner edited a verified profile is back on the
  queue; approving again decides the new version and leaves the org APPROVED. With nothing pending
  it re-stamps nothing — a second Approve must not rewrite `verified_at` and who decided it.
- **Paid needs a verified profile; Free never does.** BR-31.1's `409 payout-profile-not-verified`.
  The gate and the write are one transaction, or an officer rejecting between them leaves a vehicle
  Paid against an account nobody approved. An office shuttle collects nothing, so Free is ungated.
- **The classification write scopes its transaction without dropping to the read role.** The fleet
  reader holds `SELECT` only and cannot carry the `UPDATE`, but the transaction still sets
  `app.fleet_id` — otherwise `registry.fleet_vehicles_fleet` matches nothing and an owner is told
  their own vehicle does not exist. `FleetScope.ApplyFleetIdAsync` is that half on its own.
- **Every scoped statement is `SET LOCAL`.** Transaction-local, so the next transaction on the same
  server connection under PgBouncer inherits neither the role nor the GUC.
  `The_scope_does_not_leak_to_the_next_transaction` asserts it on a pooled connection.
- **The fare is validated against the column, not the contract.** `fleet.yaml` types
  `defaultMonthlyFareMinor` as `int64`; `registry.vehicles.default_monthly_fare_minor` is `INTEGER`.
  The narrower of the two is what a row can hold, so a wider number is a `400` here rather than a
  `22003` from Postgres about integer range.
- **The fare is nulled when a vehicle goes Free.** "Free, Rs 2,500" is not a state SCR-FP-004 can
  render, and a stale default is a number subscription-svc could pick up on a switch back.
- **The bytes are written before the `docs.uploads` row.** A crash between them leaves an orphan
  file, which NFR-28's deadline sweeps; the other order leaves a profile pointing at a document the
  officer is told exists and cannot open.
- **The upload is streamed and counted, not measured by `Content-Length`.** A ceiling enforced
  against a length the client declared is not a ceiling.
- **`captured_via` is left NULL on a payout document.** AL-43's provenance is about onboarding
  *photographs*, where a gallery pick is a fraud signal; a bank statement is exported from a banking
  app, and recording `gallery` would put a fraud signal on every payout profile on the platform.
- **Every switch-off is announced at start-up**, and here for its own reason: **all four fail
  silently and look like normal operation.** An organisation nobody can approve looks exactly like
  one nobody has got to yet; an ungated one looks approved; an unscoped read returns rows, just too
  many of them.

## Rules C059 added

- **A fleet vehicle is PENDING until a person decides, and there is no auto-approval here.**
  AL-30's auto-approve is the Mode C wizard's, gated on four steps ocr-svc can settle by itself;
  AL-50 puts a Mode A vehicle's route permit — a legal document — in front of a Verification
  Officer. registry-svc's onboarding route refuses a Mode A/B vehicle outright, so the gate lives
  on this service's internal plane rather than where AL-50's sentence puts it (see the handoff).
- **The two writers of `registry.vehicles` do not overlap.** registry-svc owns a driver's own Mode
  C registration and the four-step wizard's documents, keyed by `driver_id`; this service owns a
  Mode A/B fleet vehicle, its roster row, its Service-payment pair and its documents, keyed by
  `fleet_id`. `ck_documents_owner` is an **XOR**, so the database keeps the two apart rather than
  a convention doing it.
- **The plate is canonicalised, not merely validated — and it must match registry-svc exactly.**
  D-37's uniqueness is a unique index over the *stored text*, so two writers storing one plate
  differently bypass it: `wp qa-1234` from the Fleet Portal and `WP-QA-1234` from the Driver App
  would be two rows for one bus. `FleetRegistrationNumbers` is character-for-character
  registry-svc's, and `FleetRegistrationNumberTests` pins the canonical form so a divergence fails
  a build rather than a plate lookup. **It belongs in the kernel** — raised in the handoff.
- **`docsStatus` is derived and never stored.** `fleet.yaml` describes `docs_pending` as "the state
  a bulk-CSV row starts in", which sounds like a column; a column would be a second opinion about
  the same documents, and a slot an officer verified would leave the vehicle reading `docs_pending`
  until something remembered to rewrite it.
- **An expired document is `pending`, not `verified`.** US-27.3 keeps expiry and approval separate
  — expiry auto-suspends *dispatch* under E-03 — but a certificate that has already lapsed cannot
  be the evidence a vehicle is approved on, or an operator could upload last year's cover.
- **A slot with no fields at all is `pending`.** An ocr-svc that was unreachable writes the
  required keys with null values and `pending`, so "the permit expiry could not be read" is a row
  the officer can fill rather than an absence they have to notice — and the belt-and-braces clause
  catches a document inserted by some other path, which must not read as verified.
- **US-13.9's "auto-expires" is a predicate, not a sweep.** `registry.driver_eligible_vehicles`
  (migration 0314) evaluates the assignment's window at read time, so the driver's app stops
  offering the vehicle the instant `expires_at` passes with the row untouched and nobody pressing
  anything. A sweep would be a second mechanism that could lag, fail or be switched off, and the
  driver would keep the bus for as long as it did.
- **`valid_from` is not `assigned_at` renamed.** `assigned_at` is when the row was written — what
  an assignment history orders by; `valid_from` is when the driver may start driving, which a
  temporary hire routinely puts in the future. A relief driver booked on Monday for Thursday's
  shift must not be able to take the bus out on Monday.
- **The overlap rule is an exclusion constraint, not a check.** `ex_fleet_assign_overlap` refuses
  two open assignments of one driver to one vehicle whose windows overlap; consecutive windows are
  legal and are how a relief driver is re-hired next month. A `SELECT` then an `INSERT` loses the
  race between two managers assigning at once, and 0306's unique index could not tell an expired
  row from a live one.
- **Removing a vehicle revokes its drivers in the same transaction, and in that order.** After the
  roster row is deleted the vehicle is no longer this org's and the assignment update's own
  `fleet_id` predicate would match nothing — leaving drivers holding a vehicle that had left the
  fleet, which is the leak US-13.7's "immediately" is about.
- **A bulk row that fails costs only itself.** Postgres aborts a transaction on a constraint
  violation, so each row runs inside its own `SAVEPOINT`; without one the first duplicate plate
  would take 4,999 good rows with it and the operator would be told nothing imported. `COMPLETED`
  with failures is a partial import with a report — `FAILED` is reserved for a job that could not
  be processed at all, because a client branching on the status must not discard the good rows.
- **The bulk error report speaks the API's vocabulary.** Every failed row carries the same kebab
  code the single-vehicle `POST` would have raised, so the CSV an operator downloads and the 409
  they would have seen say the same thing.
- **The report link is anonymous and that is the point.** The HMAC in the query string *is* the
  credential, which is what lets the portal hand it to a browser download; the signature covers the
  fleet as well as the job, and the read behind it goes through `registry.fleet_bulk_job_rows_fleet`
  anyway. A bad or expired link is `404`, not `403` — it has not proved the job exists.
- **The alarm sweep is two statements, in this order.** Departures that were made are recorded
  before anything decides to ring: PostgreSQL gives two data-modifying CTEs one snapshot with no
  ordering between them, so a single statement could mark the same row twice. The claim
  (`UPDATE … WHERE status = 'SCHEDULED' … RETURNING`) is what makes the alarm exactly-once across
  replicas, and `alarm_raised_at` survives the status moving on.
- **The MISSED state commits before the push is attempted.** A notification that failed to send
  must not roll back the record that a departure was missed — the operator's own screen reads that
  record. The push is best effort and is not re-queued: ringing a driver about a departure that is
  by then an hour old is worse than not ringing.
- **The alarm goes to whoever was driving at the booked time**, not to whoever is assigned now. An
  alarm raised at 06:20 about the 06:10 belongs to the 06:10's driver, and a shift that changed in
  between must not redirect it.
- **`SCHEDULE_NOT_STARTED` is not `SCHEDULED_REMINDER`.** The second is dispatch-svc's courtesy
  *before* a booking (US-6A.15/US-10.9); this is an exception *after* a departure that should
  already have happened, and sharing a type would tell a late driver their ride is upcoming. The
  type, the trilingual template (migration 1905) and this producer landed together, which is the
  rule 1902's header states.
- **Reading is not approval-gated; writing is.** US-13.A7 disables "onboarding and assignment", not
  monitoring, so the map, the analytics and the alerts sit outside the gate — a PENDING
  organisation waits days for an officer and must still be able to watch the vehicles it already
  runs. The writes on the same group carry `.RequireApprovedFleet()` individually.
- **Analytics distance is great-circle, not road distance.** Nothing in this build map-matches a
  completed journey, so the number is the sum of hops between consecutive telemetry samples — an
  under-estimate on a winding road and an over-estimate on a jittery fix. `earningsMinor` is
  **absent rather than zero**: a fleet's Mode A/B vehicles take no fares on this platform, and zero
  would be a claim that the operator earned nothing.
- **The tracker bind and the Mode B proxies forward the caller's own bearer.** provisioning-svc and
  subscription-svc each resolve what the caller may do *against the vehicle*, so forwarding the
  operator's token keeps that check where it is and means these hops can grant nothing the operator
  did not already have. In particular subscription-svc's owner-only rules — mark cash received,
  override a fare, delete a subscriber, confirm a slip — stay owner-only without this service
  restating them.
- **A hop with nowhere to go leaves its routes unmapped.** No `Fleet:ProvisioningBaseUrl` means
  `POST …/trackers/bind` is a 404 rather than a bind that silently does nothing, and no
  `Fleet:SubscriptionBaseUrl` means the Epic 23 proxies are absent rather than a screen of zeroes.
  Both are announced as errors at start-up.
- **Route-deviation and geofence alerting is Phase 3 and is not built.** The CRUD is, and
  `GET /alerts` answers an empty page so the portal can render its empty state without a later
  breaking change — empty by construction, not by filtering.

## Schema this component added

**C058:** `db/migrations/0313__registry_fleet_org.sql` and `db/migrations/1806__fleet_org_rls.sql`.
**C059:** `0314__registry_fleet_ops.sql`, `1408__spatial_fleet_geofences.sql`,
`1807__fleet_ops_rls.sql` and the trilingual `1905__seed_fleet_schedule_alarm.sql`. Each object is
argued at its declaration; all are micro-change-sets raised in the two handoffs.

| Object | Why |
|---|---|
| `registry.fleets.contact_phone` / `contact_email` / `address` | `POST /v1/fleets` **requires** `contactPhone` and returns two more, and US-13.A7's KYC is what the officer reads. §2 has `name` and `business_reg` and nothing else. Not on `iam.users`: the contact is the organisation's, and an owner changing their own number must not rewrite approved KYC |
| `ux_fleets_business_reg_active` | two organisations claiming one business registration is the KYC failure the queue exists to catch; the live set only, D-37's shape, so a REJECTED application frees the number |
| `ck_payout_profile_status` + `superseded` | §26 makes the table versioned **and** admits one verified row per org; when an officer approves an edit the incumbent has to leave `verified` and no printed status could carry it |
| `registry.fleet_command_log` | R-14 per bounded context — the **twelfth** time. Separate from `registry.command_log` (0307): the two services share a schema but not a key space |
| `registry.current_fleet_id()` | the scoping predicate, in one place, fail-closed — the two-argument `current_setting` returns NULL rather than raising |
| RLS + two policies × five tables | the fence. RESTRICTIVE and role-targeted so the twenty services that read these tables platform-wide are untouched |
| `registry.fleet_vehicles_fleet` · `iam.fleet_members_fleet` · `trips.sessions_fleet` | the joins the fleet needs into tables it must never hold. The base tables are never granted, so a forgotten `WHERE` cannot become a platform-wide read |
| `fleet_assignments.valid_from` / `expires_at` + `ex_fleet_assign_overlap` (0314) | the gap 0310's own header named as C059's to close: US-13.9 says an assignment "auto-expires" and the table had `revoked_at` and nothing else, so the only way one could end was a human ending it |
| `registry.fleet_schedules` (0314) | US-13.11's departures have no table anywhere. **Not** `dispatch.scheduled_rides`, which is a passenger's Mode C advance booking — AL-03 forbids a fleet Mode C vehicle and a bus leaving the depot has no passenger and no pickup point |
| `registry.fleet_bulk_jobs` · `fleet_bulk_job_rows` (0314) | `fleet.yaml` specifies the bulk endpoint completely and D4' has no table for any of it — the same gap 0405 raised for bulk trackers. The failed rows are stored, which is what makes the report survive a restart |
| `spatial.geofences.fleet_id` (1408) | §17's table has no owner, and a `PUT` is a replace: one operator's upload would have deleted every other operator's fences |
| RLS + two policies × four more relations, and three more `_fleet` views (1807) | the same fence over what C059 added. `registry.documents` is granted with a policy rather than hidden behind a view because it carries `fleet_id` — and `ck_documents_owner`'s XOR is what keeps a driver's own licence invisible to every fleet |
| `content.notification_templates` `schedule_not_started` ×3 (1905) | US-13.11's ringing alarm has no D5' §14.4 row and no seeded key; this component is its producer, so both halves land together |

`migrate-verify.sh` gained a C058 section (25 checks, 391 total) and now expects **16** registry
tables. Two platform-rule queries there were narrowed to `BASE TABLE`: a trigger cannot be attached
to a view and a CHECK cannot be declared on one, and the `_fleet` views project their base tables'
`updated_at` and `default_monthly_fare_minor` by design.

## Contract changes these components made

`fleet.yaml` and `_shared.yaml`, all recorded in the two handoffs.

**Δ C059:** `GET /v1/fleets/{id}/vehicles` (the roster SCR-FP-004 renders and nothing could read),
`GET …/vehicles/bulk/{jobId}` and `…/errors.csv` (the poll and the `errorReportUrl` the 202
promises), `GET …/assignments`, `GET …/schedules`, `GET …/geofences` (three writes whose screens
had no read), `/v1/internal/fleets/{id}/vehicles/**` (AL-50's approval gate, which the spec places
on a service that structurally cannot hold it), `driverPhone` on the assignment body (US-13.2
assigns "by User ID / phone"), the fields `Assignment`, `FleetSchedule` and `VehicleDocumentSlot`
need for their screens, and two error codes — `driver-not-found` and `documents-incomplete`.

**Δ C058:**

| Change | Why |
|---|---|
| `GET /v1/fleets/{fleetId}/members` | members could be provisioned and never read back; SCR-FP-002's team list had no source |
| `/v1/internal/fleets/**` (4 routes) | AL-39 states the officer's routes on admin-bff, which is a BFF and holds no fleet tables — its own text has approving an org set `payout_profiles.status='verified'`, a write on `registry.fleet_payout_profiles` |
| `FleetStatus` lost `SUSPENDED` | the CHECK admits three values and nothing in D3'/D5'/URD Epic 13 suspends an *organisation* |
| `PayoutProfileStatus` gained `superseded` | see the schema table above |
| seven org error codes | `fleet.yaml` declared 403/404/409 with only kernel codes to carry them, and the portal renders a different screen for each |
| `FleetMember`, `FleetOrgQueueRow`, `PayoutDocumentRef`, `FleetVerificationDecision` | the shapes the four new operations return |

## Not here, and named rather than stubbed

- **Billing.** C060's: `billing.fleet_invoices`, the fleet wallet, the per-vehicle breakdown and
  the dunning. `GET /v1/fleets/{id}/billing` and `POST …/wallet/topup` are unmapped here.
- **Route-deviation and geofence *alerting*.** Phase 3 (US-13.5). The polygons are stored and
  `GET /alerts` answers an empty page; there is no producer, no table and no consumer, and a stub
  that invented one would be worse than an honest emptiness.
- **The Mode B subscription surface itself.** subscription-svc's (C048). These routes are a proxy
  that adds the org scope and forwards the caller's bearer; re-implementing the roster or the
  payment ledger here would give Epic 23 two writers of `subscription.payments` that could disagree
  about a month's due date.
- **The credential an ST-901 binding mints.** provisioning-svc's (C030) — its CA's private key is
  on that service's volume and nowhere else. `autoStartSession` is accepted and is **not armed**:
  AL-32/T-11 make tracker-driven journey auto-start a property of the ingest path, and
  `prov.tracker_bindings` has no column for it. Logged when a caller asks for `false`.
- **`registry.vehicles.approved_by`.** There is no such column: the Verification Officer's identity
  on a *vehicle* decision is validated here and recorded by admin-bff in `audit.events` (D-35),
  which is where the organisation's decision is recorded too.
- **Signed thumbnail and full-document URLs on a slot.** `fleet.yaml` offers `thumbUrl` and
  `fullUrl`; this service holds no signing key and no object-storage client (C125), and a
  `file://` path on the wire would be a storage layout no browser can follow. admin-bff mints them,
  as it does for the payout documents (US-24.8).
- **The Verification Officer's screen.** The Admin Portal's (C107) behind admin-bff (C062), which is
  RBAC-gated deny-by-default and writes `audit.events` for every mutation (D-35). This service holds
  the decision and never sees the officer's bearer.
- **`audit.events`.** admin-bff's, for the reason above. A second audit row here would double-count
  every approval and leave the two copies to disagree.
- **The "your organisation has been approved" notification.** notification-svc's (C051) — and it has
  no template: migration 1904 seeds none for a fleet org, and D6' §2.1 gives fleet-svc no topic
  (`fleet.events` is fleet-health-svc's, C044). That is why the outbox is named as *absent* rather
  than merely missing; it would be its first real consumer.
- **`rides.rides_fleet`.** R-01 and `registry.fleet_vehicles.mode CHECK` mean a fleet vehicle can
  never appear in `rides.rides`, so the fleet-scoped journey view is over `trips.sessions`. A
  `rides.rides_fleet` would be empty by construction and would tell a future reader that fleets have
  Mode C rides.
- **The Dockerfile.** `infra/docker-compose.dev.yml` already carries a `fleet-svc` cluster
  destination pointing at the combined `app-services` container, which is where D7' §2.1 puts it.

## Configuration

Every knob is documented at its declaration in `FleetOptions` and in `infra/env/.env.app.example`.

| Setting | Default | Where it comes from |
|---|---|---|
| `RlsEnabled` | `true` | D7' §4.2. **false ⇒ a cross-org read is prevented by application SQL alone** — the escape hatch is a login role that has not been granted `mageride_fleet_reader`. Logged as an ERROR |
| `VerificationGate` | `true` | D7' §4.2, US-13.A7. **false ⇒ an unapproved org can onboard vehicles and assign drivers.** ERROR |
| `InternalApiKey` | unset | **unset ⇒ `/v1/internal/fleets/**` is not mapped**: no organisation can ever be approved, so nothing on the platform can go Paid. ERROR |
| `DocumentRoot` | *(temp dir)* | **not object storage** — D-36's bucket, when a client exists (C125) |
| `DocumentMaxBytes` | 8 MiB | **no spec** — the same bound as `Support:ScreenshotMaxBytes`; the idempotency request buffer is raised to match |
| `DocumentRetention` | 90 d | NFR-28. Written to `docs.uploads.auto_delete_at`; the sweeper is not this service's |
| `MaxPageSize` | 50 | **no spec** — D3' §0 caps a page at 100; the queue logs when it bites |
| `MaxMembersPerFleet` | 200 | **no spec** — a backstop on an unbounded provisioning route whose sub-users need no verification |
| `OcrBaseUrl` · `OcrInternalApiKey` | unset | **unset ⇒ no vehicle document is ever read (AL-50)**: uploads are stored, every chip stays `pending`, and every vehicle is held out of APPROVED. ERROR |
| `OcrConfidenceThreshold` | 0.80 | **no spec** — and it must equal `Registry:OcrConfidenceThreshold`, or one licence is doubtful in the Driver App and certain in the Fleet Portal |
| `ProvisioningBaseUrl` | unset | **unset ⇒ `POST …/trackers/bind` is not mapped** (US-13.12). The safe direction: the alternative is believing a tracker is armed. ERROR |
| `SubscriptionBaseUrl` | unset | **unset ⇒ the Epic 23 proxies are not mapped** (SCR-FP-011/012). ERROR |
| `ProxyTimeout` | 10 s | D6' §8.3's internal hop. No retry on a proxy — a retried `accept` is a second grant |
| `ScheduleAlarmsEnabled` | `true` | **false ⇒ no not-started alarm ever rings** and a missed departure stays SCHEDULED for ever. ERROR |
| `NotificationBaseUrl` · `NotificationInternalApiKey` | unset | **unset ⇒ the departure is recorded MISSED and no driver app rings** (US-13.11b). ERROR |
| `ScheduleAlarmInterval` · `BatchSize` | 30 s · 100 | **no spec** — 30 s bounds lateness well inside the smallest offset the contract admits |
| `ScheduleEarlyStartGrace` | 30 min | **no spec** — a bus that pulls out eight minutes early made its departure |
| `BulkMaxRows` · `BulkUploadMaxBytes` | 5 000 · 2 MiB | T-09's ceiling, which `ck_fleet_bulk_jobs` repeats; the byte bound refuses a non-CSV at the pipe |
| `ErrorReportSigningKey` · `Ttl` | unset · 24 h | **unset ⇒ a key per process**: a link minted by one replica 404s on another |
| `MapStaleAfter` | 15 min | **no spec** — US-7.16's judgement applied to the fleet map |
| `MaxAnalyticsDays` | 92 | **no spec** — bounds a window function over every telemetry sample in the range |
| `MaxGeofences` · `MaxGeofenceVertices` | 100 · 1 000 | **no spec** — backstops on a route that replaces a whole set |

`ConnectionStrings:Postgres` and `Jwt:*` are required. `CommandLog:*` defaults to `registry` /
`fleet_command_log` with no aggregate-id column (set in `FleetApplication`, overridable). There is
no `ConnectionStrings:Redis`, no `Kafka:BootstrapServers` and no `Outbox:*`, and there must not be —
see `FleetApplication` for why each is off. **Every switch above is announced at start-up**, and
C059's four for C058's reason: each fails silently and looks like normal operation — an unread
document looks like one nobody has got to, an unmapped bind looks like a routing problem, and an
alarm nobody hears looks like a departure that was made.
