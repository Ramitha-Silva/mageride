# Contract test suite (C118) — `tests/Contract`

.NET 10 + xUnit v3 + YamlDotNet. npm-free, Docker-free. References **every service that owns a
document in `backend/contracts/`** and composes each one with its own `XApplication.Build`.

**Verify:** `dotnet test tests/Contract -c Release` (from the repository root). Also runs inside
`dotnet test backend/MageRide.sln`, which is what CI executes — the project is in the solution.

`backend/contracts/*.yaml` is normative and wins over this file, over the services, and over
`specs/`. C007's rule, quoted in full because it is this suite's whole premise: *"If a service and a
contract disagree, the contract wins — fix the service, or file a micro-change-set against
`specs/D3_mageride_api_contracts.md` and update both."*

## What it asserts

| Layer | File | Question |
|---|---|---|
| Contract model | `Model/ContractSet.cs`, `ContractOperation.cs`, `ContractSchema.cs` | 26 documents, `$ref`s resolved across files, 382 operations |
| Conventions | `Conventions/ConventionTests.cs` | `Idempotency-Key` on POST · problem+json on every error · LKR integer minor units · cursor pagination · `/v1` prefix · explicit `security` |
| Error registry | `Conventions/ErrorRegistryTests.cs` | `_shared.yaml#ErrorCode` **==** `MageRideErrors` (the promise `.spectral.yaml` makes and cannot keep) |
| Route conformance | `Runtime/RouteTableTests.cs` | every operation is mapped by a running service, and every route a service maps is in a contract |
| Drift | `Runtime/DriftTests.cs` | the validator has teeth: eleven deliberately drifted payloads and paths, each of which must fail |
| Recorded drift | `Runtime/RouteDrift.cs` | the twelve real mismatches found on landing, ratcheted so the list cannot grow |
| **Live conformance** (Δ C126) | `Live/LiveConformanceTests.cs` | every operation driven over HTTPS against a **deployed** replica: no 5xx, a status the platform may answer, 2xx bodies schema-valid, the internal plane refused, the JWKS published |
| **Live drift** (Δ C126) | `Live/LiveDrift.cs` | the five findings the first live run produced, ratcheted the same way — a fixed entry fails the suite until it is deleted |

## The decisions

### Services are composed, not deployed

`infra/docker-compose.dev.yml` is what C118's brief names, and **it cannot be brought up**:
`app-services`, `hot-path` and `fanout` have no Dockerfile (the file says so itself, and
`dev-up.sh full` refuses with the list). A suite that required it would not run at all today, and one
that ran against a subset stack would silently stop covering whatever was left out.

So each service is composed by **its own composition root** — the same `XApplication.Build` its
`Program.cs` calls — which is a stronger guarantee than a container in the one direction that
matters: the route table read here is the route table that image serves, endpoint filters and
authorization metadata included, and it is read for *all twenty-four* rather than for the ones that
happen to have an image. When the Dockerfiles land (C124/C125), the sweep in `Runtime/` gains a
second transport; nothing about the assertions changes.

### The route table is read before start, and that is deliberate

A minimal-API route exists the moment `Build` returns. Reading `((IEndpointRouteBuilder)app).DataSources`
needs no database, no broker and no socket — so the structural half of "every operation exists on a
running service" is exact, fast and cannot flake. It is also the only way to get the **reverse**
direction: you cannot send a request to an endpoint you do not know exists, so a route with no
contract is invisible to any request-driven sweep.

### Every optional upstream is configured, because a route can be conditional

Several services map a proxy route only when its upstream is set — fleet-svc's Epic 23 Mode B family
is behind `if (!string.IsNullOrWhiteSpace(settings.SubscriptionBaseUrl))`. A suite that left those
unset would read a table with ten operations missing and report the platform as drifted. `BlackHole`
is an address nothing listens on: present enough to map the route, dead enough that nothing calls it.

**`Fleet:ProvisioningBaseUrl` is the one exception, and `RouteDrift` says why** — setting it makes
fleet-svc throw while composing. That is a real defect, not a configuration choice.

### The validator is written here rather than taken from a package

`SchemaValidator` implements the keywords `backend/contracts/` actually uses, and
`No_schema_uses_a_keyword_the_response_validator_ignores` fails the build the day a document grows
one it does not. A general JSON Schema 2020-12 validator would accept every keyword in the draft and
tell you nothing about which of them are enforced. It also lets a failure read like a platform
review — *"`fare.totalMinor` is 480.5; currency crosses the wire as integer minor units"* — instead
of *"instance failed keyword `type`"*.

### Findings are ratcheted, never suppressed

`RouteDrift` is a ledger with a reason per entry. The suite fails if the list **grows** and fails if
an entry is **fixed and left there**, so it shrinks when the platform does the work. Every entry is
reproduced in the C118 handoff with the component that owns the fix.

## Rules for adding to this suite

- **A new contract document needs a `ServiceCatalog` entry in the same change**, or an entry in
  `NotComposed` with a reason. `The_catalog_covers_every_contract_document` fails otherwise — a
  coverage report whose denominator quietly shrinks is not a coverage report.
- **Per-service settings are transcribed from that service's own harness**, which is the only place
  the required-options set is written down. Do not guess: a service that cannot compose cannot have
  its route table read, and the failure looks like drift.
- **Never soften an assertion to make it pass.** Add the finding to `RouteDrift` with a sentence
  saying what is wrong and who owns it, or fix the service.
- **`ContractSchema` carries two documents** — the one a node was *written* in and the one it
  *resolved* in. Use `ResolvedDocument` for children. Getting this wrong makes every cross-file
  schema resolve empty, and an empty schema passes every assertion silently.

## Not here yet — the PARTIAL half

Named rather than absent; the C118 handoff carries the plan for each.

1. **The response sweep — LANDED for a DEPLOYED target (Δ C126), still open in-process.**
   `Live/` drives every operation over HTTPS against a running replica and validates 2xx bodies
   against the declared schema: `bash infra/replica/contract-live-verify.sh`. It skips wholesale
   without `MAGERIDE_LIVE_EDGE`, so this project's own verify is unchanged.

   What it proves that composition cannot: TLS terminates, the gateway's cluster addresses resolve,
   the version gate lets a real client through, and **a bearer can be validated at all** — which is
   how C126 found `Jwt__JwksUrl` pointing at a path the gateway refuses, a 500 on every
   authenticated request in both compose stacks that every in-process assertion had stayed green
   through.

   What is still missing: the same sweep over the **in-process** fleet (`ServiceFleet` on port 0
   over TestKit containers), which is the only way to cover it in CI where no replica exists. And
   the live transport asserts *shape*, never behaviour — it must not change state, so it cannot
   construct the business state a 2xx body needs. Most operations therefore answer a refusal, and
   the schema half only fires where a 2xx comes back unaided.

   **Read `Live/LiveRequestPlan.cs` before pointing it anywhere you care about.** Its first run
   filed two real PDPA obligations against its own operator account and rewrote two configuration
   rows; both holes are closed and both are written up there, because the next hole will be found
   the same way.
2. **Redpanda event-schema conformance** for `telemetry.raw`/`normalized`, `ride.events`,
   `dispatch.events`, `trip.events`, `audit.events` (D6' §2.2).
3. **KMP client contract tests against the live stack.** `e2e/walking-skeleton` is the pattern and
   `infra/docker-compose.skeleton.yml` the only buildable stack.
4. **The `docker-compose.dev.yml` CI leg**, which is blocked on the three missing Dockerfiles.
