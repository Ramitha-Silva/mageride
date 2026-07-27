# api-gateway (C008) — edge conventions

Stack: .NET 10 Minimal API + **YARP 2.3** reverse proxy. References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/ApiGateway.Tests -c Release`

## What lives here, and what must not

The gateway owns exactly four edge concerns and nothing else:

| Concern | Where | Spec |
|---|---|---|
| Route/cluster table | `gateway-routes.json` | D6' §8.2 |
| Minimum-version gate → `426` | `Versioning/` | D-31, D3' §0 |
| Attestation enforcement → `401 attestation-failed` | `Attestation/` | D-30, ADD §12.6 |
| Per-route rate limiting, request-id / traceparent | `RateLimiting/`, `Http/` | D6' §8.2/§8.3 |

**No business logic.** No database, no Kafka, no domain types. If a decision needs to know what a
ride or a wallet is, it belongs in the owning service.

**Fences.**
- MQTT (8883) is TCP/SNI passthrough at HAProxy and never reaches this process (ADD §12.2).
- `/hubs/**` is proxied with a WebSocket upgrade and a long idle timeout, never buffered.
- `/v1/internal/**` is refused `404` at the edge — those routes are mTLS-only (D3' §0).
- The gateway does **not** authorize. AL-06 deny-by-default lives in the services, which are the
  only place the caller's role set and the target resource are both known. Every YARP route
  therefore carries `"AuthorizationPolicy": "anonymous"`, and the kernel's fallback policy is
  cleared in `GatewayServiceCollectionExtensions`.

## Adding a route

1. Add the entry to `gateway-routes.json` in the right **order tier** — 10 cross-service literal
   overrides, 20 `/v1/admin` sub-trees owned by a service other than admin-bff, 50 ordinary
   per-service prefixes, 90 the admin-bff catch-all. Lower `Order` wins.
2. Give it a `RateLimit` metadata value naming a policy that exists in `appsettings.json`; a route
   naming an unknown policy is logged as an error and left unlimited.
3. `RouteTableTests` drives **every** operation in `backend/contracts/*.yaml` through the running
   gateway and asserts the cluster that served it, so a new contract endpoint fails the build until
   it is routed.

## Adding an attestation-protected endpoint

`Gateway:Attestation:SensitiveOperations` must equal, exactly, the set of operations declaring the
`X-Attestation` parameter in `backend/contracts/*.yaml`. `AttestationEnforcementTests` asserts both
directions, so marking an operation sensitive in a contract fails this project's build until the
list is updated — and vice versa.

## Configuration

`appsettings.json` carries the platform defaults; `gateway-routes.json` carries the route table.
Everything layers from the environment in the usual way, e.g.
`ReverseProxy__Clusters__ride-svc__Destinations__primary__Address`. D7' §4.2 has **no api-gateway
row** — the variables this service reads are documented in the C008 handoff in
`build/progress.md` and need a micro-change-set into D7'.

Operational notes for C009 / C125:
- `Gateway__ForwardedHeaders__KnownProxies__0` must be HAProxy's address, or every caller collapses
  into one rate-limit bucket.
- `Gateway__StateStore=Redis` in any deployment with more than one gateway replica.
- HAProxy must **not** publish `/health/live`, `/health/ready` or `/metrics` to the internet; the
  shared kernel maps all three anonymously on every service, and on the public edge they are
  operational surface.
