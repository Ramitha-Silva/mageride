# registry-svc (C021 ws-registry-minimal) — vehicle conventions

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/Registry.Api.Tests -c Release`

## What this slice is

The walking skeleton's vehicle identity: enough for a driver to have one approved Mode C
vehicle that the dispatcher can offer a ride to. Everything here matches
`backend/contracts/registry.yaml`, which wins over this file and over the code — except
`select-live`, which the contract does not have (see below).

| Endpoint | Spec |
|---|---|
| `POST /v1/vehicles` | D3' registry-svc, AL-09, D-37 |
| `GET /v1/vehicles/mine` | D3' route table, US-2.8 |
| `POST /v1/vehicles/{id}/select-live` | **not in D3'** — US-9.6/US-9.7 (C021 micro-change-set) |
| `POST /v1/dev/vehicles/{id}/approve` | dev seed path only; **not a contract route** |

**Not here, on purpose.** Document upload, Gemini OCR, the four-step onboarding machine
(`PUT /v1/vehicles/{id}/onboarding/{step}`, AL-29/AL-30), `PUT /v1/drivers/profile`, status
polling, deactivate, the E-03 expiry job, Mode B sharing and subscribers, IMEI binding and the
OnePay merchant bind (D-11) are **C028/C029**. They are left unmapped rather than stubbed. The
Verification-Officer queue is skipped entirely.

## Rules that are load-bearing

- **AL-09's set is exact, and `car` is refused.** AL-09 maps `car → sedan` as a one-time data
  migration, not an input alias — rewriting it silently would hide an un-updated client until a
  fare tariff or a map marker disagreed. `bus` and `train` are real types but Mode A, so they
  are `403 mode-not-allowed`, not `400`. `VehicleTypes` must stay identical to the
  `registry.vehicles.vehicle_type` CHECK in `db/migrations/0303`.
- **Registration numbers are canonicalised, not just validated.** `wp qa-1234` and `WP-QA-1234`
  both become `WP-QA-1234`. `ux_vehicles_regno_active` (D-37) is a unique index over the stored
  text, so without this the rule is bypassed by retyping the plate. A character a plate cannot
  contain is refused rather than stripped — deleting it would let two different plates collide.
- **One selected vehicle per driver (US-9.6).** The selection is
  `registry.driver_profiles.active_vehicle_id` (migration 0308) and the invariant is that
  table's primary key, not application code. Ownership is the composite FK to
  `registry.vehicles(id, owner_id)`; **APPROVED-ness is not expressible as a constraint** and is
  enforced here. C029 must clear the selection when a selected vehicle is DEACTIVATED or
  REJECTED.
- **`owner_id` comes from the token's `sub`, never from the body.** There is no field to supply
  somebody else's id in, and `VehicleRegistrationTests` asserts a body that tries anyway is
  ignored.
- **Every route requires the `driver` role.** Opening the Driver App does not grant it (C020
  decision 4): a passenger who signs in there carries `app=driver, role=passenger` and is
  refused. Granting `driver` on approval is C029's.
- **The dev approve endpoint is not approval.** Real approval is AL-30's auto-approve once all
  four steps are VERIFIED, gated by AL-10's mandatory insurance document. This one checks
  neither, and it is **not mapped at all** unless `Registry:DevApprovalEnabled` resolves true
  (Development by default, plus whatever the replica sets).

## Configuration

`Registry:DevApprovalEnabled` unset means Development only. The lightweight production replica
runs synthetic data under the Production environment name and sets it to `true` explicitly; the
service logs a warning at start-up whenever it is on outside Development.

`Jwt:Issuer` must match what iam-svc signs with or every request is a 401. registry-svc holds no
signing key — it resolves iam-svc's public half through `Jwt:JwksUrl` like every other consumer.

## Schema this service added

`db/migrations/0307`–`0308`; each file's header says why, and both are recorded as
micro-change-sets in the C021 handoff in `build/progress.md`. `registry.command_log` exists at
all, and `registry.driver_profiles` carries `active_vehicle_id` /
`active_vehicle_selected_at`.

## The seed

`db/seed/skeleton.sql` (applied by `bash infra/scripts/seed-skeleton.sh`) creates the skeleton
driver `00000000-0000-4000-8000-00000000d001` on `+94770000001` with one APPROVED Mode C
three-wheeler `00000000-0000-4000-8000-00000000c001`, plate `WP-QA-0001`, already selected.
It lives outside `db/migrations/` deliberately — DbUp applies that directory to production, and
this file invents an account and waves a vehicle past AL-10.
