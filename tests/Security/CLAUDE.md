# Security suite (C127) — `tests/Security`

.NET 10 + xUnit v3. Docker-free, network-free. References `tests/Contract`, which composes every
service with its own `XApplication.Build`.

**Verify:** `dotnet test tests/Security -c Release`. The other half of C127's verify command is
`bash security/run-asvs-checks.sh` — see [`security/README.md`](../../security/README.md) for where
the line between them is.

## What it asserts

| Layer | File | Question |
|---|---|---|
| RBAC probe | `Rbac/RbacProbeTests.cs` | is any of the 444 endpoints reachable with no credential requirement (AL-06) |
| Anonymous surface | `Rbac/AnonymousSurface.cs` | what authenticates the 147 that opt out of authentication |
| Internal-plane exposure | `Rbac/InternalPlaneExposureTests.cs` | is a shared-secret route addressable from the internet (C127-03) |
| Edge route table | `Rbac/GatewayRouteTable.cs` | what `gateway-routes.json` + `gateway-policy.json` actually publish |
| Bearer validation | `Tokens/BearerValidationTests.cs` | RS256-only, lifetime, issuer, skew — per service; algorithm confusion and `alg: none` driven |
| Redaction perimeter | `Pii/RedactionPerimeterTests.cs` | is `PerimeterGuardHandler` still on ocr-svc's Gemini client (D-36) |
| Evidence appendix | `Rbac/InventoryDump.cs` | writes the whole inventory for `security/asvs-l2-checklist.md` |

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

## Rules for adding to this suite

- **Assert the observed thing.** `BearerValidationTests` reads each service's final
  `JwtBearerOptions` off its own composed container rather than asserting the kernel once — a
  service that added a `Configure<JwtBearerOptions>` of its own would replace a decision nobody
  would look for again, and the options monitor applies every configurator in order.
- **Never soften an assertion to make it pass.** Add the finding to
  `security/remediation-backlog.md` with an owner and a date, or fix the platform. "Noted" is not a
  resolution (C127 fence).
- **A new anonymous endpoint needs an `AnonymousSurface` entry in the same change**, naming the
  credential — and if that credential is a shared secret, the path also needs a
  `Gateway:BlockedPathPrefixes` entry or `InternalPlaneExposureTests` fails.
- **Do not add a test that needs a socket, a container or a replica.** This project must run on a
  bare build agent in under ten seconds. Anything needing a deployment belongs in
  `security/checks/`, which skips loudly when there is nothing to ask.
