# Security remediation backlog

The OWASP ASVS L2 review's findings (C127) and the anti-spoof hardening pass's (C128), 2026-08-12.
**Every entry is fixed or explicitly risk-accepted with an owner and a date. "Noted" is not a
resolution** (C127 fence).

Severity is CVSS-shaped but argued rather than scored: what an attacker gets, and what they need to
get it. Where a finding is severe in production and not on the replica, both are stated — the only
deployment that exists today is the synthetic-data replica, and saying "HIGH" without that
qualification would be as misleading as saying "LOW".

| # | Finding | Severity | State | Owner | Date |
|---|---|---|---|---|---|
| C128-01 | A revoked tracker certificate still authenticates to EMQX — no deployed broker checks the CRL | **HIGH** (prod) / LOW (replica) | **open**; blocked on a fleet re-mint | C133 | due at go-live |
| C128-02 | E-07 raises three uncorrelated flags; the correlation is what carries the precision | MEDIUM | **open** | admin-bff (C061) | 2026-08-12 |
| C128-03 | ADD §12.6 prices `flex` above `sedan` in the anti-spoof table | LOW | micro-change-set | spec owner | 2026-08-12 |
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

---

## C128-01 — a revoked tracker certificate still authenticates to EMQX · **HIGH** · open

**What was found.** `RevocationPropagationTests.A_revoked_tracker_certificate_still_completes_the_mutual_tls_handshake`
mints a device certificate from the CA the broker trusts, connects on 8883, decommissions the
tracker, and connects again. **The second handshake succeeds, and the device goes on publishing
positions for its vehicle.** Measured against the deployed broker policy — `EmqxFixture` bind-mounts
`infra/deploy/emqx/emqx.conf` and `acl.conf`, the same two files the replica mounts.

Every platform-side half of T-12 works. The binding goes `REVOKED`; `validate` answers no, which
closes the TCP path within its budget; the serial reaches the CRL provisioning-svc publishes at
`/v1/internal/trackers/crl.pem`, well inside 60 s. What is missing is the broker reading it:
`enable_crl_check` and `crl_cache.refresh_interval` are **commented out** in `emqx.conf`.

**Why nothing caught it.** Two suites each proved their own half and neither owned the join.
`Provisioning.Api.Tests.RevocationTests` asserts the serial is on "the CRL the broker fetches", and
`HotPath.Tests.EmqxAuthTests` asserts the broker's ACL — but nothing asked whether the broker
fetches anything, and the file that decides says so only inside a comment. The same shape as C127-03:
a convention standing in for a declaration.

**Severity.** HIGH on production. A decommissioned, sold, stolen or quarantined tracker keeps
publishing under its vehicle's topic until its certificate expires — up to the 90-day T-02 rotation
period — and the positions it writes are indistinguishable from the real vehicle's. LOW as it stands,
because no hardware fleet exists and the only deployment is the synthetic-data replica.

**Why it cannot simply be switched on**, and this is the whole reason it is open rather than fixed.
EMQX locates a CRL through the **CRL distribution point extension in the peer certificate**.
`EmbeddedStepCa.Issue` writes that extension only when `StepCa:CrlDistributionPoint` is configured
(`EmbeddedStepCa.cs`, the `if (!string.IsNullOrWhiteSpace(...))` around the CDP builder), and **no
environment sets it** — not `.env.app.example`, not the replica, not the k8s overlays. So every
certificate the platform has ever minted carries no distribution point, and a broker with
`enable_crl_check = true` refuses a certificate whose CRL it cannot locate. Turning the check on
first does not tighten the tracker plane; it takes the whole of it off the air. `emqx.conf`'s own
comment warns about the ordering and gives the wrong reason for it — it describes a start-up race
with provisioning-svc, which is real but secondary.

**The order it has to happen in.**

1. Set `StepCa__CrlDistributionPoint` to an address the broker can reach — for the replica, the
   internal base URL app-services serves `/v1/internal/trackers/crl.der` on; for DOKS, the
   provisioning-svc Service address. It must be reachable **from the broker's network namespace**,
   not from the edge.
2. Re-mint every device credential so the extension is present. `CredentialRotationWorker` already
   does this on the T-02 90-day schedule; a fleet-wide rotation is `Provisioning:RotationEnabled`
   with the window brought forward, and every device must have **collected** its replacement before
   step 3 — the overlap is what stops a rotation from being a revocation.
3. Uncomment `ssl_options.enable_crl_check = true` and `crl_cache.refresh_interval = 60s` in
   `infra/deploy/emqx/emqx.conf`. The refresh interval is what puts the number on T-12's 60 s for
   the MQTT path.
4. Delete this entry, and invert the two assertions that pin the current state:
   `BrokerPolicyTests.The_broker_does_not_yet_check_the_revocation_list_and_that_is_recorded` and
   `RevocationPropagationTests.A_revoked_tracker_certificate_still_completes_the_mutual_tls_handshake`.
   Both name this finding in their failure messages.

**Owner: C133, before the first production tracker is provisioned.** It is a go-live blocker on the
hardware plane specifically — C133's fence already gates go-live on no open high findings — and it is
*not* a blocker for the mobile plane, where the MQTT session JWT's own TTL bounds the exposure
(`disconnect_after_expire = true`, `max(active-ride + 2h, 4h)`).

**Interim compensating controls, in force today.** The TCP path is unaffected and is the one the
adapters use. A revoked device's `imei:{imei}` cache entry is deleted and `validate` refuses it, so
anything that resolves through provisioning-svc stops. A quarantined or revoked vehicle is not
dispatchable (T-11). And the positions a revoked tracker publishes still pass through the D-18/T-07
gate, so it cannot teleport — it can only lie plausibly, which is C128's documented `slow-walk` gap.

---

## C128-02 — E-07 raises three uncorrelated flags · MEDIUM · open

**What was found.** Against a 39-pair synthetic population shaped like a Sri Lankan ride-hailing
month, `repeat_pair` at the deployed threshold of 8 rides / 30 days has **67 % precision**: nine
flags, of which three are honest commuters. Correlating it with the `shared_device` cross-check on
the same population names **exactly the six farming pairs and nothing else** — 100 %.

**Why raising the threshold is not the fix.** A farming pair rides *less* than a commuter. A
passenger keeping one three-wheeler driver on call — twice a day on weekdays — is 34 completed rides
with one counterparty in thirty days, which is over any threshold that would still catch farming at
12–27. No value of `PairRideThreshold` separates them; raising it drops both.

ADD §12.6 already says the right thing — the detector "cross-checks device-binding hashes and IP/ASN
clustering" — and `CollusionDetector` already computes all three signals in one pass. What it does
not do is correlate them: it writes `repeat_pair`, `shared_device` and `network_cluster` as three
independent rows, so `GET /v1/admin/reputation/flags` is three queues rather than one ranked one.

**Severity.** MEDIUM, and it is a *precision* finding rather than an exposure — nothing is missed
(recall is 100 % and asserted), and nothing is blocked, because the fence holds: a flag is a review
signal and `reputation.block_states` never moves. What it costs is reviewer attention, and a queue
where two of every three items is a loyal customer is a queue that stops being read.

**Accepted as open, admin-bff (C061), 2026-08-12.** Not changed here: the information is all present
in `reputation.fraud_flags` and what is missing is a ranking on the admin surface, which C128's own
fence puts out of scope — anti-collusion output is a review signal, and the review surface is not
this component's to redesign. `Reputation__Collusion__PairRideThreshold` is left at **8**
deliberately, so the corroborating signal is present to correlate against; tightening it would remove
the very rows the correlation needs.

The measurement is `RideFarmingTests.The_deployed_thresholds_catch_every_farming_pair_in_a_realistic_population`
and it asserts the correlation isolates exactly the farming pairs — so if the correlation ever stops
working, this finding's premise fails loudly rather than quietly.

---

## C128-03 — ADD §12.6 prices `flex` above `sedan` · LOW · micro-change-set

ADD §12.6's anti-spoof table gives `flex` a **200 km/h** ceiling — the highest in the table, above
`sedan`'s 180 — for what D5' §1's enumeration lists as a passenger tier between `three_wheeler` and
`sedan`. Nothing on a Sri Lankan road legally approaches it; the expressway limit is 100 km/h.

Two consequences. The `flex` tier's per-type ceiling is effectively no ceiling, and because
`DefaultMaxSpeedKph` is deliberately set to the most permissive value *in* the table, the three
registry types §12.6 omits (`truck`, `mini_truck`, `train`) inherit the same 200. The 1 km/s jump
backstop still applies to all four, which is why this is LOW rather than MEDIUM.

**Raised as a micro-change-set, not changed.** The corpus measures what is deployed, and inventing a
number for a tier the spec priced would be exactly the thing `DefaultMaxSpeedKph`'s own remarks warn
against. `honest-flex-expressway-and-town` drives the tier at 105 km/h so the ceiling is exercised
and the finding stays visible.
