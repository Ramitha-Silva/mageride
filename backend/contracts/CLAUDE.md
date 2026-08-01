# API Contracts

**Stack:** OpenAPI 3.1 (YAML) + Spectral lint. No code here — contracts only.

D3' is the source of truth for the API; these files are its machine-checkable form. C012/C013
(KMP client) are generated from them and C118 (contract tests) checks the running services
against them. If a service and a contract disagree, the contract wins — fix the service, or file
a micro-change-set against `specs/D3_mageride_api_contracts.md` and update both.

## Layout

| File | Contents |
|---|---|
| `_shared.yaml` | Problem+json, the `ErrorCode` registry, pagination, money, geo, the edge headers, security schemes, reusable error responses. Every service file `$ref`s into it. |
| `{service}.yaml` | One document per service — 23 of them. `fleet-health.yaml` (C044) and `fleet-billing.yaml` (C060) are the two whose paths live under another service's prefix; each file's header says why. |
| `proto/*.proto` | The gRPC IDL, for the internal surfaces OpenAPI cannot express. `reputation.v1.proto` (C033) is the D-04 block-status / driver-level service; the owning `.yaml` reproduces it under `x-grpc-service` and points here. Server and callers compile the same file — a copied proto is how two services start disagreeing. Not linted by Spectral (the glob is `*.yaml`); `dotnet build` is what checks it. |
| `realtime/signalr-hub.md` | `/hubs/live` methods, events, groups, entitlement (D3 §3.1, D6 §5). |
| `realtime/mqtt-topics.md` | EMQX topic tree, payloads, ACL, rate limits, replay (D3 §3.2, D6 §3/§4). |
| `.spectral.yaml` | Lint rules. |

## Verify

```bash
npx --yes @stoplight/spectral-cli lint 'backend/contracts/*.yaml' --ruleset backend/contracts/.spectral.yaml
```

Must exit 0 with **zero errors**. The one `unrecognized-format` **warning** is the verify glob
picking up `.spectral.yaml` itself, which is not an OpenAPI document; it cannot be suppressed from
the ruleset and is below the CLI's fail severity.

## Rules the lint enforces

1. **Kebab error codes.** Every operation declares `x-error-codes: [...]`, each entry lower-case
   kebab. The registry is `_shared.yaml#/components/schemas/ErrorCode`, which mirrors
   `MageRide.Shared.Errors.MageRideErrors` (C002) one-for-one.
2. **`Idempotency-Key` required on POST.** The only escape is `x-idempotency-exempt: <reason>`,
   used by the six HMAC-signed payment-provider callbacks, which dedupe on
   `provider_transaction_id` (R-19) because an external gateway cannot send our header.
3. **LKR integer minor units.** A `*Minor` field must be `type: integer, format: int64`; a
   `currency` field must be `const: LKR`; a bare `amount`/`fare`/`price`/… must not be a `number`.
4. **`/v1` path prefix** — or `/public`, the AL-44 token-scoped family, which is versioned by
   share-token scope rather than by a path segment.

Plus the structural rules the generated clients need: `operationId` present, unique and camelCase;
one tag per operation; explicit `security` on every operation (`security: []` for the deliberately
public ones — deny-by-default is not something a contract may leave to a default); 4xx/5xx served
as `application/problem+json`.

## Conventions when editing

- **Adding an error code** means editing three places in the same change: this directory's
  `ErrorCode` enum, `MageRideErrors` in the kernel, and the owning operation's `x-error-codes`.
- **A superseded endpoint is deleted, not deprecated** — but a comment naming the AL that removed
  it stays in the file header, so nobody re-adds it from an earlier-dated spec line. The removals
  live at the top of `iam.yaml`, `voip.yaml`, `wallet.yaml`, `admin-bff.yaml` and
  `public-bff.yaml`.
- **Where a D3 Δ addendum supersedes an earlier line, the later addendum wins.** Several
  pre-AL-48 sections still describe masked calling and several pre-AL-37 lines still describe MFA;
  they are superseded in the same document.
- **Path parameter names are consistent per service** (`vehicleId`, `rideId`, `driverId`, …). Two
  paths that differ only in the *name* of a template parameter are an ambiguous OpenAPI document,
  not two endpoints.
