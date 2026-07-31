# fleet-svc (C058) — the fleet organisation, its sub-roles, and the payout profile Mode B money depends on

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002).
**No Redis, no Kafka and no outbox** — see `FleetApplication` for why each is off.

**Verify:** `dotnet test backend/src/Fleet.Api.Tests -c Release`

`backend/contracts/fleet.yaml` is normative for this surface and wins over this file and over the
code.

## What this component is, and what it is not

C058 is the **identity** half of the Fleet Portal: who the organisation is, who may act for it, and
where its money goes. C059 (`fleet-svc-fleet-ops`) is the **operations** half — vehicle onboarding,
named document slots, assignment, tracker binding, scheduling, the map, analytics, geofences and
the Mode B subscription proxies — and C060 the billing.

| Endpoint | Auth | Spec |
|---|---|---|
| `POST /v1/fleets` | Bearer `fleet_owner` | US-13.A7 |
| `GET /v1/fleets/{fleetId}` | any sub-role | D3' fleet-svc route table |
| `POST /v1/fleets/{fleetId}/members` | owner | US-13.A5 |
| `GET /v1/fleets/{fleetId}/members` | any sub-role | **Δ C058** — provisioning had no read-back |
| `GET` · `PUT /v1/fleets/{fleetId}/payout-profile` | owner | AL-49, SCR-FP-002a |
| `POST /v1/fleets/{fleetId}/payout-profile/documents` | owner | AL-49 |
| `PUT /v1/fleets/{id}/vehicles/{vid}/classification` | owner/manager, **approved org** | AL-24 item 16b, BR-31.1 |
| `GET /v1/internal/fleets/queue` · `/{fleetId}` | internal | **Δ C058** — AL-39's fleet-org queue |
| `POST /v1/internal/fleets/{fleetId}/approve` · `/reject` | internal | **Δ C058** — AL-39, AL-49 |

| Table | Read | Written |
|---|---|---|
| `registry.fleets` | every route | **this service** |
| `iam.fleet_members` | every route | **this service** — iam-svc reads it for the claim (C027) |
| `iam.users` / `iam.user_roles` | the team view | **iam-svc**; this service only *provisions* a sub-user |
| `registry.fleet_payout_profiles` | this service, subscription-svc (C050 `payTo`) | **this service** |
| `registry.vehicles` | through `registry.fleet_vehicles_fleet` only | **registry-svc**, + `mode_b_billing` here |
| `registry.fleet_vehicles` | the roster view | **C059** |
| `docs.uploads` | the officer's queue detail | **this service**, for the three AL-49 kinds only |
| `registry.fleet_command_log` | the kernel | **this service** |

## The three fences, and how each is held structurally

- **A fleet operates Mode A and/or Mode B — never Mode C (AL-03).** Held in the database:
  `registry.fleet_vehicles.mode CHECK (mode IN ('A','B'))` (migration 0306). `FleetModes` names the
  two and has no third constant, and `/classification` refuses anything that is not Mode B outright
  — `mode_b_billing` is NULL for Mode A and C by design (AL-24), and a bus has no subscribers.
- **An unapproved org onboards nothing (US-13.A7).** Held by the *route group*, not by each handler:
  `FleetEndpoints` builds `/v1/fleets/{fleetId}/vehicles` and `/v1/fleets/{fleetId}/assignments`
  with `.RequireApprovedFleet()`, so **a route C059 adds to either is gated the moment it is
  mapped**. `Every_vehicle_and_assignment_route_is_gated` walks the endpoint data source and fails
  for the *next* component's mistake, which is the point. **C059: map your routes on those groups.**
- **A cross-org read is a security bug.** Held in Postgres — migration 1806's RESTRICTIVE policies
  over the five org-owned tables, entered through `SET LOCAL ROLE mageride_fleet_reader`. The
  repositories' own `WHERE fleet_id = @FleetId` is the second lock, never the only one, and
  `RowLevelSecurityTests` asserts the first one by connecting as a real non-superuser login with no
  fleet-svc code in the path.

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

## Schema this component added

`db/migrations/0313__registry_fleet_org.sql` and `db/migrations/1806__fleet_org_rls.sql`. Each
object is argued at its declaration; three are micro-change-sets raised in the C058 handoff.

| Object | Why |
|---|---|
| `registry.fleets.contact_phone` / `contact_email` / `address` | `POST /v1/fleets` **requires** `contactPhone` and returns two more, and US-13.A7's KYC is what the officer reads. §2 has `name` and `business_reg` and nothing else. Not on `iam.users`: the contact is the organisation's, and an owner changing their own number must not rewrite approved KYC |
| `ux_fleets_business_reg_active` | two organisations claiming one business registration is the KYC failure the queue exists to catch; the live set only, D-37's shape, so a REJECTED application frees the number |
| `ck_payout_profile_status` + `superseded` | §26 makes the table versioned **and** admits one verified row per org; when an officer approves an edit the incumbent has to leave `verified` and no printed status could carry it |
| `registry.fleet_command_log` | R-14 per bounded context — the **twelfth** time. Separate from `registry.command_log` (0307): the two services share a schema but not a key space |
| `registry.current_fleet_id()` | the scoping predicate, in one place, fail-closed — the two-argument `current_setting` returns NULL rather than raising |
| RLS + two policies × five tables | the fence. RESTRICTIVE and role-targeted so the twenty services that read these tables platform-wide are untouched |
| `registry.fleet_vehicles_fleet` · `iam.fleet_members_fleet` · `trips.sessions_fleet` | the joins the fleet needs into tables it must never hold. The base tables are never granted, so a forgotten `WHERE` cannot become a platform-wide read |

`migrate-verify.sh` gained a C058 section (25 checks, 391 total) and now expects **16** registry
tables. Two platform-rule queries there were narrowed to `BASE TABLE`: a trigger cannot be attached
to a view and a CHECK cannot be declared on one, and the `_fleet` views project their base tables'
`updated_at` and `default_monthly_fare_minor` by design.

## Contract changes this component made

`fleet.yaml` and `_shared.yaml`, all recorded in the C058 handoff:

| Change | Why |
|---|---|
| `GET /v1/fleets/{fleetId}/members` | members could be provisioned and never read back; SCR-FP-002's team list had no source |
| `/v1/internal/fleets/**` (4 routes) | AL-39 states the officer's routes on admin-bff, which is a BFF and holds no fleet tables — its own text has approving an org set `payout_profiles.status='verified'`, a write on `registry.fleet_payout_profiles` |
| `FleetStatus` lost `SUSPENDED` | the CHECK admits three values and nothing in D3'/D5'/URD Epic 13 suspends an *organisation* |
| `PayoutProfileStatus` gained `superseded` | see the schema table above |
| seven org error codes | `fleet.yaml` declared 403/404/409 with only kernel codes to carry them, and the portal renders a different screen for each |
| `FleetMember`, `FleetOrgQueueRow`, `PayoutDocumentRef`, `FleetVerificationDecision` | the shapes the four new operations return |

## Not here, and named rather than stubbed

- **Vehicles, documents, assignment, trackers, scheduling, map, analytics, geofences, billing, the
  Mode B proxies.** C059 and C060. The two gated groups are mapped and empty of C059's routes on
  purpose — see the fences above.
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

`ConnectionStrings:Postgres` and `Jwt:*` are required. `CommandLog:*` defaults to `registry` /
`fleet_command_log` with no aggregate-id column (set in `FleetApplication`, overridable). There is
no `ConnectionStrings:Redis`, no `Kafka:BootstrapServers` and no `Outbox:*`, and there must not be —
see `FleetApplication` for why each is off.
