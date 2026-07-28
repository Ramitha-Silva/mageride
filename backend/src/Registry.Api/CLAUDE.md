# registry-svc (C021 ws-registry-minimal + C028 registry-svc-vehicles + C029 registry-svc-onboarding)

Stack: .NET 10 Minimal API + Dapper over Npgsql + StackExchange.Redis + Confluent.Kafka.
References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/Registry.Api.Tests -c Release`

## What this is

Vehicle identity and lifecycle: registration, the one-live-publisher rule, deactivation, Mode B
sharing, the OnePay merchant binding fare settlement needs, driver-identity Profile Setup, the
four-step Mode-C onboarding machine and the E-03 document-expiry tracker. Everything here matches
`backend/contracts/registry.yaml`, which wins over this file and over the code — except
`select-live`, which the contract does not have (see below).

| Endpoint | Spec |
|---|---|
| `PUT /v1/drivers/profile` | D3' route table, AL-27, AL-29 (SCR-DA/DI-003a) |
| `POST /v1/vehicles` | D3' registry-svc, AL-09, D-37 |
| `GET /v1/vehicles/mine` | D3' route table, US-2.8, **US-13.9** |
| `GET /v1/vehicles/{id}` · `/status` | D3' route table, US-2.13/2.15 |
| `PUT /v1/vehicles/{id}/onboarding/{step}` | AL-30, US-2.26/2.27 (SCR-DA/DI-004→004c) |
| `GET /v1/vehicles/{id}/onboarding-status` | AL-30 (SCR-DA/DI-006) |
| `POST /v1/vehicles/{id}/deactivate` | US-2.16, D-37 |
| `PUT /v1/vehicles/{id}/driver-profile` | US-2.12 |
| `POST /v1/vehicles/{id}/select-live` | **not in D3'** — US-9.6/US-9.7 (C021 micro-change-set) |
| `POST /v1/vehicles/{id}/share` · `/share/{grantId}/accept` · `DELETE /share/{grantId}` | US-4.1/4.2/4.3b, D-22 |
| `GET /v1/vehicles/{id}/subscribers` · `DELETE /subscribers/{userId}` | US-4.7, US-NEW.1 |
| `POST /v1/share-requests` | US-4.5 |
| `POST /v1/internal/vehicles/{id}/merchant` | D-11 |
| `POST /v1/internal/vehicles/{id}/onboarding/recompute` | **not in D3'** — AL-30 (C029 micro-change-set) |
| `POST /v1/dev/vehicles/{id}/approve` | dev seed path only; **not a contract route** |

**Not here, on purpose.** Gemini extraction, the PII redaction pre-pass and the Tesseract fallback
are **C054**, behind `IDocumentExtractionClient`. The upload surface that fills `docs.uploads` is
not this service's either — registry-svc resolves a file id and never sees the bytes, which is
what keeps an unredacted image on the far side of the D-36 perimeter. The Verification-Officer
queue screens are **C062**; this service feeds them (`document.review_required`,
`registry.document_fields.verify_status='pending'`) and takes their answer back through the
internal recompute route. The rejection path (`registry.vehicles.rejection_reason`, US-2.15) is
C062's too — nothing here writes the column. `POST /v1/vehicles/{id}/device` is a thin wrapper over
provisioning-svc's `POST /v1/trackers/bind` (T-02) and belongs with the service that mints the
credential — **C030**. Mode A/B vehicle onboarding, route permits and writing
`registry.fleet_assignments` are the Fleet Portal's (**C059**); this service only *reads*
assignments. All are left unmapped rather than stubbed.

## Rules that are load-bearing

- **AL-09's set is exact, and `car` is refused.** AL-09 maps `car → sedan` as a one-time data
  migration, not an input alias — rewriting it silently would hide an un-updated client until a
  fare tariff or a map marker disagreed. `bus` and `train` are real types but Mode A, so they
  are `403 mode-not-allowed`, not `400`: **the Driver App is the wrong surface, not the value.**
  `VehicleTypes` must stay identical to the `registry.vehicles.vehicle_type` CHECK in
  `db/migrations/0303`.
- **Registration numbers are canonicalised, not just validated.** `wp qa-1234` and `WP-QA-1234`
  both become `WP-QA-1234`. `ux_vehicles_regno_active` (D-37) is a unique index over the stored
  text, so without this the rule is bypassed by retyping the plate. A character a plate cannot
  contain is refused rather than stripped — deleting it would let two different plates collide.
- **`registry.driver_eligible_vehicles` is the one answer to "which vehicles may this driver
  operate"** (migration 0310). registry-svc's `select-live`, dispatch-svc's standby gate and
  trip-state-svc's session start all read it, so the three cannot derive the rule differently.
  It carries `source` (`owned` | `assigned`), the raw status columns, and one computed
  `is_go_live_eligible` (APPROVED + not E-03 suspended). **Consumers read the raw columns when
  they need their own error mapping** — dispatch answers `vehicle-not-approved` where a
  pre-filtered read would have collapsed it into `vehicle-not-found`.
- **Ownership stopped being the selection rule when US-13.9 landed.** 0308 made "a driver may
  only select a vehicle they own" a composite foreign key; **0311 relaxed it**, because an
  assigned non-owner may go online with a fleet vehicle and the composite key rejected exactly
  that. The invariant was restated, not dropped: entitlement spans two tables and is enforced
  here against the projection, and the database still refuses a selection that names no vehicle.
  A missing entitlement is **404, not 403** — the projection is driver-scoped, so "not yours" and
  "does not exist" are the same query result and telling them apart would leak a stranger's plate.
- **One selected vehicle per driver (US-9.6), and the release is the acquire.** The selection is
  one column on `registry.driver_profiles`, whose primary key is the driver, so selecting a
  second releases the first in a single `UPDATE` — there is no window in which two are set. Ten
  concurrent selections leave exactly one (`GoLiveEligibilityTests`).
- **`lock:driver:{driverId}` is a published fact, not a lock** (D-03). Despite the ADD's
  `lock:` prefix, Postgres holds the invariant; Redis is how the dispatch and tracking planes
  learn which vehicle it is. Written **after COMMIT** and **best effort** — an unreachable Redis
  costs a cache, not a driver's shift.
- **Deactivation is a cascade, in one transaction** (US-2.16). The status change, the revocation
  of every live share on the vehicle, the outbox row per grantee and the clearing of the driver's
  selection commit together. A vehicle off the map while a grant still says otherwise is exactly
  the leak D-22 is about. DEACTIVATED is outside `ux_vehicles_regno_active`'s predicate, so this
  also frees the plate (D-37).
- **`share.revoked` goes through the outbox and names the passenger.** A revoke that committed
  and then failed to publish leaves somebody watching a vehicle they lost access to (R-13). The
  payload carries `passengerId`, which **D6' §5.1's `{vehicleId}` does not** — §5.2 in the same
  document requires a *directed* `RemoveFromGroupAsync`, and a vehicle id alone leaves fanout
  removing everybody or querying this service on the hot path. The aggregate id is the vehicle,
  because it is the partition key and keying by grant would let a later `share.granted` overtake
  the revoke that preceded it.
- **A grant confers nothing until it is accepted** (US-4.3b). `POST /share` publishes no event;
  `share.granted` is written when the grantee accepts. Only the named grantee may accept, and
  that is a predicate in the `UPDATE`, not a check afterwards.
- **A share is visibility; an assignment is operation.** US-4.1 shares "tracking access" with any
  driver-app user (`registry.shares`); the right to *drive* a fleet vehicle is
  `registry.fleet_assignments` (US-13.9). Two tables, two screens, and conflating them would let
  a passenger take a bus live.
- **`DELETE /subscribers/{userId}` is the passenger's own unsubscribe, and only theirs**
  (US-NEW.1). The owner's removal keeps the row MUTED until they delete it (US-4.12) and is
  `DELETE /v1/mode-b/{vehicleId}/subscribers/{subId}` in subscription.yaml — a different verb on
  a different service, so an owner reaching this route is `403` rather than silently performing
  the wrong one.
- **Every vehicle route requires the `driver` role; the three counterparty routes do not.**
  Opening the Driver App does not grant the role (C020 decision 4). Accepting a grant,
  unsubscribing and requesting access are things a *passenger* does, so those demand
  authentication and check ownership or grantee identity instead — which is stronger than a role.
- **`owner_id` comes from the token's `sub`, never from the body.**
- **The dev approve endpoint is not approval.** Real approval is AL-30's auto-approve once all
  four steps are VERIFIED, gated by AL-10's mandatory insurance document. This one checks
  neither, and it is **not mapped at all** unless `Registry:DevApprovalEnabled` resolves true.
- **A service method must not be called `BindAsync`.** Minimal APIs treat any parameter type
  carrying one as custom-bound, so a handler taking that service as a dependency fails to build
  the route table at start-up. `IMerchantService.BindMerchantAsync` is named for that reason.
- **Registration saves Step 1/4.** `POST /v1/vehicles` carries the type and registration number,
  which *is* the `details` step, and D5' §14.1a verifies that step on entry ("entered"). So a
  fresh vehicle already has one saved step, reads Incomplete on My Vehicles and resumes at
  `insurance` — BR-25.4 working from the first screen rather than from the second. D3' marks the
  four document ids **required** on that body while AL-30 in the same specification has the wizard
  "create a NEW vehicle at Step 1/4"; they are optional here and honoured when sent.
- **Every verdict is one rule, applied to fields.** A step is `pending_review` iff any of *its*
  fields is `pending`, and nothing else. The plate mismatch, the low-confidence read, the
  driver-typed correction and the field that failed to extract all arrive as a pending field, so
  there is one clause rather than four that can drift. A required key extraction did not return is
  still written — null value, `source='ai'`, `pending` — so the officer queue shows a row to fill
  rather than an absence to notice.
- **The driver's own typing on Step 1 is not a manual field.** D5' §14.1a marks vehicle details
  "(entered)". Treating the type and plate as `source='manual'` would be consistent with AL-29 and
  would make AL-30's auto-approve unreachable for every vehicle ever onboarded.
- **A step's verdict is derived from the documents that step saved**, recorded as `documentIds` in
  `registry.onboarding_steps.fields`. That is what makes a re-upload supersede cleanly: the failed
  attempt stays in the audit trail without holding the step down forever.
- **`onboarding_status` comes back down; `status` does not.** Both move up together at
  auto-approval. When a verified step stops being verified — a renewal whose scan was blurry, an
  edited plate — the vehicle reads Incomplete on My Vehicles and stays APPROVED, because the
  certificate the driver is carrying has not lapsed. E-03 is what takes them off the road when one
  actually does, and un-approving would also overturn a Verification Officer's decision.
- **Editing the registration number re-judges the photos.** `reg_no_match` is recomputed against
  the stored `plate_text`; without it a vehicle could be approved with front and back photos of a
  different plate, which is the one thing Step 4 exists to rule out.
- **AL-10 is checked at the gate, not inferred from the steps.** Approval re-reads the current
  insurance and revenue-licence documents and refuses on a missing, expired or expiry-less one, so
  a step that verified weeks ago cannot approve a vehicle whose cover has since lapsed.
- **"Current document" is the newest saved *batch* per (owner, kind), not the newest row.** A step
  can save two documents of one kind — the licence's two sides, the vehicle's two photos — and
  they are equally current. Both are inserted in one transaction, so `DEFAULT now()` (the
  transaction timestamp) gives them the same `created_at` and makes the batch exactly expressible.
- **E-03 suspends vehicles, because that is where the column is.** ADD §6 says expiry "flips
  driver to `DISPATCH_SUSPENDED`" and `dispatch_state` lives on `registry.vehicles`: a per-vehicle
  document suspends its own vehicle, and a vehicle-less driving licence suspends every vehicle
  that driver owns. The release is strict — both mandatory documents current and unexpired with a
  real expiry, and no lapsed identity document.
- **The extraction call happens outside the transaction.** ocr-svc is a network hop; holding a
  Postgres transaction open across it would put another service's latency on this one's connection
  pool. Nothing is written until every document has come back.
- **An extractor that is down must not stop a driver saving a step.** `ExtractAsync` throwing is
  caught and treated as `DocumentExtraction.Unavailable`; the step saves, lands `pending_review`
  and a Verification Officer takes it — which is what D5' §14.1a does with a document that failed
  to extract. `UnconfiguredDocumentExtractionClient` is the same answer for a deployment with no
  ocr-svc at all, and it says so once per document in the log.

## Configuration

`Registry:DevApprovalEnabled` unset means Development only. The lightweight production replica
runs synthetic data under the Production environment name and sets it to `true` explicitly; the
service logs a warning at start-up whenever it is on outside Development.

`Registry:OcrConfidenceThreshold` (default **0.80**) is the confidence at or above which an
ocr-svc field is `auto_verified` rather than sent to an officer. **No spec pins the number** —
AL-29, BR-25.2 and D6' §7.5 all say "below threshold" and none of them says what it is, the same
situation as `Dispatch:SearchRadiusM`. Bounded at 0.5 so it cannot be turned into "trust
everything". A field with **no** confidence is treated exactly like a low one.

`Registry:DocumentExpiryEnabled` (default **on**) gates the E-03 sweep;
`Registry:DocumentExpiryInterval` (default 1 h) and `Registry:DocumentExpiryBatchSize` (500) size
it. E-03 says "nightly" and the interval is hourly on purpose: `registry.document_notices` makes
every extra pass free, and a restart or a deployment window cannot cost a night.

`Registry:InternalApiKey` **unset means `/v1/internal/vehicles/**` is not mapped at all** — a
deployment that forgets it gets 404s rather than an unauthenticated write to
`registry.driver_payouts`, and the missing binding then surfaces as `402 merchant-not-onboarded`
at `POST /v1/fare/pay` (D-11). It must equal what fare-svc (C046) sends. D3' §0 puts the internal
family on mTLS and the gateway refuses the prefix at the edge (C008); the shared secret is the
interim until C042 lands a mesh.

`Outbox:*` defaults to `registry` / `registry_outbox` / `registry.events` (set in
`RegistryApplication`, overridable). `CommandLog:Schema` defaults to `registry`.

Redis is **on** from C028 (`lock:driver:{driverId}`), but every use is best-effort, so a Redis
outage degrades coordination rather than refusing requests.

`Jwt:Issuer` must match what iam-svc signs with or every request is a 401. registry-svc holds no
signing key — it resolves iam-svc's public half through `Jwt:JwksUrl` like every other consumer.

## Schema this service added

`db/migrations/0307`–`0311`; each file's header says why, and all are recorded as
micro-change-sets in the C021 and C028 handoffs in `build/progress.md`.

| Migration | What | Why |
|---|---|---|
| 0307 | `registry.command_log` | D3' §0 mandates a per-service idempotency log; D4' prints one for rides only |
| 0308 | `driver_profiles.active_vehicle_id` | US-9.6/9.7 need the selection stored and no spec stores it |
| 0309 | `registry.outbox` | `share.revoked` (D-22) had a producer and a consumer and no topic or table |
| 0310 | `registry.driver_eligible_vehicles` | US-13.9's entitlement spans three tables and every consumer would re-derive it |
| 0311 | relaxes 0308's composite FK | US-13.9 admits non-owners, which the composite key rejected |
| 0312 | `registry.document_notices` | E-03 names four notices per document and `documents.status` can remember one |

`EventTopics.RegistryEvents` (`registry.events`, key vehicleId) is **not** one of D6' §2.1's six
topics; it is added to `infra/deploy/redpanda/bootstrap-topics.sh` and `slim-verify.sh` alongside
them.

## Events on `registry.events`

`share.granted` · `share.revoked` · `vehicle.deactivated` (C028) and, from C029,
`vehicle.registered` · `vehicle.approved` · `document.review_required` · `document.expiring` ·
`document.expired` · `vehicle.dispatch_resumed`. Only `vehicle.registered` is named by a spec
(D3' `POST /v1/vehicles` side effects) and only `document.expiring`/`document.expired` are named
by E-03; **none of the six has an envelope anywhere in D6' §2.2**, so the shapes in
`Onboarding/OnboardingEvents.cs` are registry-svc's and are raised as micro-change-sets in the
C029 handoff.

The aggregate id is the **vehicle**, matching the topic's partition key. Two events have no
vehicle to key by and use the **driver** instead: `document.review_required` for Profile Setup,
whose pending fields belong to a person rather than a vehicle, and `document.expiring` for a
driving licence lapsing before the driver has onboarded anything. Ordering per driver is the right
guarantee for both.

## The seed

`db/seed/skeleton.sql` (applied by `bash infra/scripts/seed-skeleton.sh`) creates the skeleton
driver `00000000-0000-4000-8000-00000000d001` on `+94770000001` with one APPROVED Mode C
three-wheeler `00000000-0000-4000-8000-00000000c001`, plate `WP-QA-0001`, already selected.
It lives outside `db/migrations/` deliberately — DbUp applies that directory to production, and
this file invents an account and waves a vehicle past AL-10.
