# C127 — remediation backlog

The OWASP ASVS L2 review's findings, 2026-08-12. **Every entry is fixed or explicitly risk-accepted
with an owner and a date. "Noted" is not a resolution** (C127 fence).

Severity is CVSS-shaped but argued rather than scored: what an attacker gets, and what they need to
get it. Where a finding is severe in production and not on the replica, both are stated — the only
deployment that exists today is the synthetic-data replica, and saying "HIGH" without that
qualification would be as misleading as saying "LOW".

| # | Finding | Severity | State | Owner | Date |
|---|---|---|---|---|---|
| C127-01 | Services connect to Postgres as a superuser: `audit.events` is mutable and every RLS policy is inert | **HIGH** | mechanism landed; **deployment cutover open** | C133 | cutover due at go-live |
| C127-02 | The co-located edge shipped with no rate limit on any route and nothing marked D-30 sensitive | **HIGH** | **FIXED** and verified live | C127 | 2026-08-12 |
| C127-03 | Three mTLS-only operations were published to the public internet | MEDIUM | **FIXED** and verified live | C127 | 2026-08-12 |
| C127-04 | `*.key` was absent from `.gitignore` | LOW | **FIXED** | C127 | 2026-08-12 |
| C127-05 | The replica runs attestation `Disabled` rather than C125's stated `Audit` | LOW | **risk-accepted** | project owner | 2026-08-12 |
| C127-06 | `Content:InternalApiKey` unset leaves the template render open rather than unmapped | LOW | **risk-accepted** | C042 | 2026-08-12 |
| C127-07 | One database role for twenty-two services | MEDIUM | **risk-accepted**, deferred | C133 | reviewed 2026-08-12 |
| C127-08 | Twelve `/v1/internal` operations declare no `security` block | LOW | **risk-accepted** | C042 | 2026-08-12 |
| C127-09 | 141 endpoints admit any authenticated caller | INFO | **risk-accepted**, ratcheted | C127 | 2026-08-12 |

---

## C127-01 — the platform connects to Postgres as a superuser · **HIGH** · open at deployment

**What was found.** On the replica the application connects as `mageride`, which is `usesuper = t`
and owns every table. Two consequences, both observed rather than inferred:

- `has_table_privilege('audit.events','DELETE')` answered `true`, and the server **accepted** a
  `DELETE` (issued inside a transaction and rolled back). D-35's immutable admin log is writable by
  the same credential that appends to it, so it cannot evidence anything it is the only record of —
  which is its entire purpose.
- `row_security_active()` answered **false** for all nine policy-bearing tables (`registry.fleets`,
  `registry.fleet_vehicles`, `registry.fleet_assignments`, `registry.fleet_payout_profiles`,
  `registry.fleet_schedules`, `registry.fleet_bulk_jobs`, `registry.documents`,
  `iam.fleet_members`, `spatial.geofences`). Every fleet-scoping policy migrations 1806/1807 ship
  was doing nothing at all.

**Why nothing caught it.** Both migrations are correct and both policies are correct. This is a
property of the *credential a deployment hands the services*, and it is invisible from inside the
process: no log line, no exception, and every in-process test passes because none of them connects
as a production role. Migration 1305's own comment predicted it in as many words.

**Severity.** HIGH on production; **LOW as it stands**, because the only deployment that exists is
the replica and its data is synthetic (`REPLICA_SYNTHETIC_MARKER`). It is a **go-live blocker**
rather than a live exposure, and C133's own fence already gates go-live on no open high findings.

**What landed.** `db/migrations/2001__least_privilege_roles.sql` creates `mageride_app`,
`mageride_migrate` and `mageride_readonly`; grants DML on the twenty-two business schemas;
grants `SELECT, INSERT` and **only** those on `audit.events`; and sets `FORCE ROW LEVEL SECURITY` on
every policy-bearing table. Applied twice to the replica (re-runnable) and verified: `mageride_app`
can insert into the `telemetry.positions` hypertable and into `audit.events`, cannot update or
delete an audit row, and **has RLS applied to it** — `row_security_active('registry.fleets')` is
`true` under `SET ROLE mageride_app`.

**What is open.** The cutover — one `Username=` in the connection string, plus PgBouncer's own
client auth. `docs/runbooks/database-roles.md` §3 is the procedure and
`security/checks/40-database-privileges.sh` is what fails until it is done, so the half-finished
state is loud rather than silent. **Owner: C133. Due: at go-live, before the first production
write.** Deliberately not performed here: it changes a deployment's credential and PgBouncer's
`userlist`, which is C125/C133's surface, and a security review that broke the replica between
sessions would have cost more than it bought.

---

## C127-02 — the co-located edge ran with no rate limit and an empty attestation policy · **HIGH** · FIXED

**What was found.** The gateway's whole `Gateway` configuration section was **absent** from the
deployed `app-services` image, so every value fell back to its compiled default:

| Setting | Intended | What the deployment had |
|---|---|---|
| `Attestation:SensitiveOperations` | 22 operations (auth, payments, wallet, ride accept, SOS) | **`[]` — nothing marked sensitive** |
| `RateLimits:Policies` | 8 buckets, 20–600/min | **`{}` — no limit on any of 70 routes** |
| `BlockedPathPrefixes` | `/v1/internal` + the three C127-03 paths | `["/v1/internal"]` |

Observed directly: the container logged
`Route admin-bff names rate-limit policy 'admin', which is not configured; applying no limit` for
every route on start-up, and 70 consecutive reads of a share-token page all returned 200/404 with no
`429`.

**Why it matters beyond the replica.** `Gateway:Attestation:Mode` defaults to `Enforce`, so a
deployment that sets nothing believes it is enforcing D-30. With an empty operation set it enforces
on **zero** operations, and the metric that would show it (`AttestationRejections`) stays at zero —
which reads exactly like "no attacker has tried". The missing rate limits defeat four ADD §12.6 rows
outright: geo-data scraping, trip-share link abuse, the OTP/auth bucket and MQTT-adjacent SOS
flooding.

**Root cause.** `AppServices` (C125) co-locates the edge and twenty-two services in one process.
One content root cannot hold twenty-three files called `appsettings.json`, so its
`DropCoLocatedAppSettings` target removes every referenced project's — including api-gateway's,
which is not a service configuration but the edge's own policy. The identical collision had already
been found once, in the *test* project, and patched there by
`GatewaySettingsWinTheOutputDirectory` (Δ C126) — which made it less likely anybody would look at
the deployment.

**Fix.** The `Gateway` section moved to `backend/src/ApiGateway/gateway-policy.json`, loaded
explicitly by `GatewayApplication.Build` with `optional: false` and pinned to the output directory
by `ApiGateway.csproj` — the same two lines `gateway-routes.json` has always had, and a filename
nothing else ships. A gateway that cannot find its policy now refuses to start rather than falling
back to a permissive default.

**Verified live.** Rebuilt and redeployed `app-services`: zero `policy not configured` log lines,
and 70 reads of `/public/track/{dead token}` produced 67 × 404 then **3 × 429** — the
`public-track` bucket (60/min) enforcing.

---

## C127-03 — three mTLS-only operations were published to the public internet · MEDIUM · FIXED

**What was found.** `backend/contracts/` marks 49 operations `security: [{ mtls: [] }]`. Forty-six
carry the `/v1/internal` prefix and were refused at the edge. Three do not, and were routed to the
internet by an ordinary per-service rule:

| Operation | Route | What it does |
|---|---|---|
| `calculateFinalFare` | `POST /v1/fare/calculate` | prices a completed ride |
| `renderNotificationTemplate` | `GET /v1/content/templates/{key}` | the D-26 render path |
| `lookupUserByPhone` | `GET /v1/users/lookup` | the P-03 registration oracle |

Each failed closed on its own shared-secret filter, so nothing was exploitable. What was missing is
the second, independent control that makes a shared secret tolerable on the other forty-six — and on
the template render the filter degrades to **open** when its key is unset (C127-06).

**Root cause.** Both the gateway's blocked-path list and both test suites keyed on the `/v1/internal`
path prefix, which is a *convention*. The contract's `security` block is the *declaration*, and it
is what the platform means. iam-svc's own CLAUDE.md had flagged the lookup route in passing since
C027; nothing acted on it because nothing joined the two facts.

**Fix.** All three paths added to `Gateway:BlockedPathPrefixes` (matching is `StartsWithSegments`,
so `/v1/users/lookup` does not block `/v1/users/me`). Every caller of all three uses a direct
service address, never the gateway, so refusing them at the edge costs nothing.

Three regression tests, at three layers, and each fails on the day a *fourth* such route is written:

- `tests/Security/Rbac/InternalPlaneExposureTests` — joins the endpoint inventory to the gateway's
  route table.
- `ApiGateway.Tests/RouteTableTests.Every_operation_the_contract_puts_on_the_mtls_plane_is_refused_at_the_edge`
  — drives all 58 through a running gateway; `ContractOperation.IsInternalPlane` now reads the
  declaration *or* the prefix, because neither signal alone is complete.
- `security/checks/20-configuration.sh` §20.3 — derives the expected block list from the contracts.

**Verified live.** After redeploy, `/v1/users/lookup` and `/v1/content/templates/*` answer 404 at
the edge; `/v1/users/me` and `/v1/config/cities` are unaffected.

---

## C127-04 — `*.key` was absent from `.gitignore` · LOW · FIXED

`.gitignore` excluded `*.pem`, `*.p12`, `*.pfx`, `*.jks` and `*.keystore` — and not `*.key`. The
rotation runbook's own first command is `openssl genrsa -out /tmp/jwt-new.pem`, but the identical
command with a `.key` extension is the more common spelling and would have produced a file
`git add .` staged. Nothing was ever committed (`git ls-files` is clean). One line, plus
`security/checks/10-repository-secrets.sh` §10.1, which now fails if any of the five rules is
removed.

---

## C127-05 — attestation is `Disabled` on the replica, not `Audit` · LOW · risk-accepted

`.env.app.example` sets `Gateway__Attestation__Mode=Disabled` and its comment says *"C125 sets
Audit, then Enforce per platform as each app ships."* C125 did not; the replica runs `Disabled`.

**Accepted, by the project owner, 2026-08-12.** Neither mobile app exists before Wave 4a/4b, so
`Audit` on the replica would log a `missing-header` verdict for every synthetic request and produce
a metric with no signal in it. The control that matters is that **the default is `Enforce`** and no
manifest overrides it, so a production deployment enforces unless somebody deliberately turns it
off — asserted by `security/checks/20-configuration.sh` §20.1.

**Owed:** C133 sets `Audit` on the first deployment that has a real client, and `Enforce` per
platform as each app ships. `PlatformModes` exists for exactly that staged rollout.

---

## C127-06 — an unset `Content:InternalApiKey` leaves the template render open · LOW · risk-accepted

Every other internal family on the platform is **unmapped** without its key. `content-svc`'s
template render is not: `InternalKeyFilter` treats a null key as "no check", and
`ContentApplication` announces it loudly at start-up. The service argues the trade in its own file —
unmapping it stops notification-svc rendering anything, and the failure would surface there rather
than here, so the availability cost lands on the wrong service.

**Accepted, C042, 2026-08-12.** The reasoning holds and a template body is not a secret. Two things
reduce it further as of C127: the route is no longer reachable from the public edge (C127-03), and
the key **is** set on the replica. C042's SPIFFE peer identity removes the shared secret and this
degradation with it.

---

## C127-07 — one database role for twenty-two services · MEDIUM · risk-accepted, deferred

`mageride_app` (C127-01) is a single role every service holds, so a compromised content-svc can read
`iam.users`. Splitting the matrix per bounded context is the correct end state and is a large piece
of work: twenty-two roles, a grant matrix per schema, and a connection string per service.

**Accepted and deferred, C133, reviewed 2026-08-12.** The step that matters most is the first one —
not being a superuser — and it is C127-01. Doing both at once would make a cutover that has to be
right about twenty-two grant sets before anything works, which is how a security improvement gets
reverted. Revisit after the C127-01 cutover has been stable for one release.

---

## C127-08 — twelve `/v1/internal` operations declare no `security` block · LOW · risk-accepted

`.spectral.yaml` requires an explicit `security` on every operation *"because deny-by-default is not
something a contract may leave to a default"*, and twelve operations under `/v1/internal` write
none — ride-svc's saga family, provisioning-svc's CRL routes, reputation-svc's observation sink.
They are protected: the prefix is blocked at the edge and each carries an internal-key filter.

**Accepted, C042, 2026-08-12.** A contract-hygiene gap rather than an exposure, and C042 rewrites
every one of these declarations when the shared secret becomes a mesh identity. Until then
`ContractOperation.IsInternalPlane` reads *either* signal, so the omission cannot become an
exposure the way C127-03 did.

---

## C127-09 — 141 endpoints admit any authenticated caller · INFO · risk-accepted, ratcheted

Of 444 endpoints, 63 name a URD §2.3 (feature area, capability) pair, 93 name a role or fleet
sub-role, 147 are anonymous with a reviewed compensating credential, and **141 rely on the kernel's
deny-by-default fallback alone** — an authenticated caller, any role.

**That is the right control for most of them and the wrong question to ask of the rest.** URD §2.3
answers "may this role do this kind of thing"; it cannot answer "is this row yours", and most of the
app surface is the second question — `GET /v1/rides/{rideId}`, `PUT /v1/me/saved-addresses/{id}`,
`GET /v1/wallet/{userId}`. For those the control is an ownership check against the `sub` claim
inside the handler, which the owning service's own suite drives. Requiring a feature policy would
move the check to a layer that does not know the answer.

**Accepted, C127, 2026-08-12, and ratcheted.** The two surfaces where "any authenticated caller" is
never right are asserted separately and exhaustively: every `/v1/admin/**` route on admin-bff names
a matrix cell or a role, and the only un-gated family on that service is the three `/v1/pdpa` data
subject routes (scoped by `sub`, 404 for a request that is not yours). The count itself is pinned by
`AuthenticatedOnlyLedger`, so it fails if it grows.
