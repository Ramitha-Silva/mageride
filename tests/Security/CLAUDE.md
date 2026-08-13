# Security suite (C127, C128) — `tests/Security`

.NET 10 + xUnit v3. References `tests/Contract`, which composes every service with its own
`XApplication.Build`, and `HotPath.PositionProcessor` for the D-18/T-07 gate.

**Verify:**
- C127 — `dotnet test tests/Security -c Release`, plus `bash security/run-asvs-checks.sh`; see
  [`security/README.md`](../../security/README.md) for where the line between them is.
- C128 — `dotnet test tests/Security -c Release --filter Category=AntiSpoof`, written up in
  [`security/anti-spoof-tuning.md`](../../security/anti-spoof-tuning.md).

## What it asserts

### C127 — the ASVS L2 review. Docker-free, network-free.

| Layer | File | Question |
|---|---|---|
| RBAC probe | `Rbac/RbacProbeTests.cs` | is any of the 444 endpoints reachable with no credential requirement (AL-06) |
| Anonymous surface | `Rbac/AnonymousSurface.cs` | what authenticates the 147 that opt out of authentication |
| Internal-plane exposure | `Rbac/InternalPlaneExposureTests.cs` | is a shared-secret route addressable from the internet (C127-03) |
| Edge route table | `Rbac/GatewayRouteTable.cs` | what `gateway-routes.json` + `gateway-policy.json` actually publish |
| Bearer validation | `Tokens/BearerValidationTests.cs` | RS256-only, lifetime, issuer, skew — per service; algorithm confusion and `alg: none` driven |
| Redaction perimeter | `Pii/RedactionPerimeterTests.cs` | is `PerimeterGuardHandler` still on ocr-svc's Gemini client (D-36) |
| Evidence appendix | `Rbac/InventoryDump.cs` | writes the whole inventory for `security/asvs-l2-checklist.md` |

### C128 — `Category=AntiSpoof`. The first three need nothing; the rest need Docker.

| Layer | File | Question |
|---|---|---|
| Position corpus | `AntiSpoof/PlausibilityCorpusTests.cs` | what the D-18/T-07 gate does to 35 labelled tracks — measured FP and escape rates |
| Threshold fences | `AntiSpoof/ThresholdConfigurationTests.cs` | is every threshold deployable, is it ADD §12.6's, and does changing it change the verdicts |
| Broker policy | `AntiSpoof/Mqtt/BrokerPolicyTests.cs` | what the deployed `emqx.conf` / `acl.conf` say about the listeners nobody dialled |
| ACL matrix | `AntiSpoof/Mqtt/CrossVehiclePublishTests.cs` | is a cross-vehicle publish refused on **all three** listeners (JWT, WSS, mTLS) |
| Publish ceiling | `AntiSpoof/Mqtt/PublishCeilingTests.cs` | does the broker enforce D-17's 5 msg/s, and where does the per-connection limit stop |
| IMEI cloning | `AntiSpoof/Trackers/ImeiCloneTests.cs` | are both devices held, inside the documented 24 h window, on both detection paths (T-08) |
| Revocation | `AntiSpoof/Trackers/RevocationPropagationTests.cs` | does revocation bite within 60 s on the TCP path, and on the MQTT one (T-12) |
| Anti-collusion | `AntiSpoof/Collusion/RideFarmingTests.cs` | E-07's recall and precision on a realistic population, and that nothing is auto-blocked |

## The decisions

### It reads the composed pipeline, not the source

A grep for `RequireFeature` finds call sites. It does not find an endpoint mapped in a loop, one
whose *group* carries the policy, or one where a later `RequireAuthorization` replaced an earlier
decision. ASP.NET resolves all of that into endpoint metadata, and metadata is what the
authorization middleware reads at request time — so reading it here asks the same question the
server asks, in the same order. `RequireAuthorization(policy => …)` puts the **built** policy in
metadata alongside the `IAuthorizeData`, which is what lets a feature requirement be reported as
`feature:driver-wallet:Write` instead of an opaque generated policy id.

### The anonymous surface is a list somebody signed, not a rule

An `IEndpointFilter` is compiled into the request delegate and leaves **no endpoint metadata**. So
no amount of reflection can tell an `AllowAnonymous` route guarded by `InternalKeyFilter` from one
guarded by nothing; anything automatic would have to trust every anonymous route or fail on all of
them. `AnonymousSurface.Reviewed` is the review, held as data: an entry means somebody read the
handler and wrote down what a caller has to present.

It is a **ratchet in both directions** (C118's `RouteDrift` idiom). A new anonymous endpoint fails
until it is added with a reason, and an entry whose route no longer exists fails too — a stale
exemption is one the next endpoint on that path inherits.

### It reuses `tests/Contract`'s `ServiceCatalog` rather than copying it

That file is four hundred lines of per-service start-up settings transcribed from twenty-four
integration harnesses. A second copy drifts the first time a service gains a required option, and a
security probe that silently stopped composing a service would report it as having **no endpoints at
all** — the exact failure a coverage denominator exists to prevent. C118 exposes the types with an
`InternalsVisibleTo`, and `The_probe_covers_every_service_in_the_fleet` asserts the denominator
before anything is asserted about it.

### The exceptions are named, and each one asserts its compensating control

Two services and one route family opt out of the platform's own rules, all three legitimately. The
suite does not excuse them — it asserts the property that makes each defensible:

- **`ocr` clears the deny-by-default fallback policy.** It registers no authentication scheme (its
  plane carries no token), so `RequireAuthenticatedUser` could never be satisfied and every
  *unmatched* path would answer 500 instead of 404. `A_service_that_cleared_the_fallback_serves_nothing_but_operational_and_internal_routes`
  is the price: nothing but health probes and the key-gated internal plane may be mapped there.
- **`public-bff` registers no bearer handler at all** (AL-44 makes the share token the only
  credential). Named in `BearerValidationTests.NoBearerHandler` so the theory's denominator cannot
  shrink by accident.
- **`/v1/pdpa/**` is authenticated and not feature-gated.** URD §2.3 has no cell for a data
  subject's own rights — the account-management row gives PAX and DRV ➖ because it is about
  operating on *other people's* accounts, and gating an own-account right on it would refuse every
  subject their own data. `The_only_back_office_routes_outside_the_matrix_are_the_data_subjects_own_rights`
  asserts the **boundary**: exactly those three routes, so the exception cannot spread.

### `AuthenticatedOnlyLedger` is a ratchet, not a target

141 endpoints rely on the kernel fallback plus an ownership check in the handler. That is the right
control for most of them: URD §2.3 says which *roles* may read rides, and only the handler knows
whether ride 7 is yours. Demanding a feature policy there would move the check to a layer that does
not know the answer. What must not happen is the number growing quietly, so it is pinned — and
`Tolerance` is small, because tightening five endpoints is worth recording and losing a service is
worth failing on.

### Δ C128 — the container rule, and why it now has one exception

C127's rule was **"do not add a test that needs a socket, a container or a replica"**, and it was
right for what C127 asserts: route tables, bearer options and gateway policy are properties of
composed code, and a probe over them runs on a bare agent in under ten seconds.

Three of C128's four definition-of-done items are not properties of composed code. *"A cross-vehicle
publish attempt is refused by EMQX"*, *"a cloned IMEI quarantines both devices"* and *"revocation
takes effect within 60 s"* are claims about a broker and a database doing something, and a version of
them that ran without either would be asserting the test double. The C128-01 finding is the
demonstration: every in-process assertion about revocation was green, and a real broker accepted a
revoked certificate.

So the exception is **scoped to `Category=AntiSpoof`, and inside it to the classes that say so**:

- The corpus, the threshold fences and `BrokerPolicyTests` need **nothing** — no socket, no
  container — and are deliberately the larger half. `dotnet test --filter Category=AntiSpoof` still
  answers the headline false-positive number on a bare agent.
- `AntiSpoofCollection` carries the EMQX, Postgres and Redis fixtures for the rest, and every test in
  it opens with `Assert.SkipWhen(!fixture.IsAvailable, fixture.SkipReason)` — the platform's existing
  idiom. Without Docker they skip loudly; they never fail for its absence and never pass by
  pretending.
- **The C127 classes stay docker-free.** Nothing outside `AntiSpoof/` may take a fixture.

`security/checks/` is still where a test that needs a *deployment* goes. The line is: a throwaway
container the suite starts and owns is in scope; the running replica is not.

## Rules for adding to this suite

- **Assert the observed thing.** `BearerValidationTests` reads each service's final
  `JwtBearerOptions` off its own composed container rather than asserting the kernel once — a
  service that added a `Configure<JwtBearerOptions>` of its own would replace a decision nobody
  would look for again, and the options monitor applies every configurator in order. C128's
  equivalent: the corpus binds `infra/env/.env.app.example` rather than
  `new PositionProcessorOptions()`, because the class initialisers are the one place tuning is not
  supposed to happen.
- **Never soften an assertion to make it pass.** Add the finding to
  `security/remediation-backlog.md` with an owner and a date, or fix the platform. "Noted" is not a
  resolution (C127 fence).
- **A new anonymous endpoint needs an `AnonymousSurface` entry in the same change**, naming the
  credential — and if that credential is a shared secret, the path also needs a
  `Gateway:BlockedPathPrefixes` entry or `InternalPlaneExposureTests` fails.
- **A gap the platform cannot close is a ratchet, not a comment.** C128's `knownGap` tracks and the
  two assertions that pin C128-01 all assert the *current, wrong* state, so closing the gap fails the
  suite and asks for the ledger entry to be deleted. Same idiom as `LiveDrift` (C118) and
  `AnonymousSurface`. A finding recorded only in a document outlives its fix.
- **A measurement that cannot fail is not a measurement.** A corpus reporting zero false positives
  proves nothing on its own; `ThresholdConfigurationTests` mistunes each threshold in turn and
  asserts the corpus notices. Any new corpus needs the same.
