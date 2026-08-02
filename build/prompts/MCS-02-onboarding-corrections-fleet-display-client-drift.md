# MCS-02 — onboarding field corrections, fleet-assignment display, retired client operations

## Identity
This is a **micro-change-set**, not a manifest component, and this file is **hand-written**.

`build/tools/generate_build_plan.py` writes one `build/prompts/Cxxx.md` per entry in
`build/manifest.yaml` and deletes nothing, so this file survives a regeneration untouched — but it
is also not produced by it. **Do not add a component to `build/manifest.yaml` for this work and do
not re-run the generator**: re-running resets the Status column and the whole Session Handoffs log
in `build/progress.md`. Record this session as an ordinary Session Handoff entry instead.

Read `CLAUDE.md`, then `apps/driver-android/CLAUDE.md`, `shared/kmp/CLAUDE.md` and the stack file
for `backend/src/Registry.Api`.

**Run this after C069 and before C070.** Raised by the C069 handoff, gaps (a) and (b) plus the
pre-existing-failure note. Parts A and B each close a **wireframe deviation** that C069 recorded
and could not build; Part C restores a test that has silently stopped checking anything.

Three parts, independent of each other. **Any one of them can be dropped without breaking the
other two** — if the session runs short, do C first (it is the smallest and it protects everything
downstream), then A, then B.

---

## Part A — a driver cannot correct a doubtful extracted field

### The finding

SCR-DA-004a and SCR-DA-004b draw a **✎** on every extracted value (`2026-12-31 ✎`,
`RL-558231 ✎`), and BR-25.3 is built around the driver being able to edit one: *"any element
doubtful **or edited** → this step's status is Pending"*. The edit is unreachable three times over:

* `registry.yaml`'s `multipart/form-data` arm on `saveVehicleOnboardingStep` declares
  `registrationNumber`, `vehicleType`, `file`, `fileBack` and the two `…CapturedVia` parts, and
  **no `fields`**. The multipart arm is the only one an app can reach.
* The `application/json` arm *does* carry `OnboardingStepInput.fields`, but it needs a `fileId` —
  and **no response on this surface ever returns one**. `SaveOnboardingStepResponse` carries
  `stepStatus`, `onboardingStatus`, `status`, `nextStep`, `ocrJobId`. A client physically cannot
  learn the id of the document it just uploaded.
* Even with an id, `OnboardingService.SaveDocumentStepAsync` calls
  `RequireUploadAsync(…, command.FileId, "fileId", …)` **unconditionally** on every document step,
  so a save carrying only corrections is refused server-side.

Consequence: a doubtful insurance expiry goes to the Verification Officer and the driver — who can
read the certificate in their hand — has no way to type it in. That is the exact path AL-29 and
US-2.4a exist to provide, and C068 hit the identical wall on `licence_no` / `licence_expiry`
(**that finding is still open — close it here too**).

### Decide the shape first, and record the decision in the handoff

* **(A1) Named parts, one per accepted key** — `insuranceExpiry`, `revenueNo`, `revenueExpiry` on
  the step arm; `licenceNo`, `licenceExpiry` on `upsertDriverProfile`'s. **Recommended**: it is
  what MCS-01 chose for `nicNo` / `allowedVehicleTypes`, it keeps the contract self-describing and
  Spectral-checkable, and `DocumentFieldKeys.AcceptedFor` already enumerates exactly these keys per
  kind and side, so the server has the allow-list already.
* **(A2) One `fields` text part carrying a JSON object.** Fewer contract lines, but the shape stops
  being expressible in OpenAPI and `AcceptedFor`'s allow-list becomes a runtime string check.

Whichever you take, the **service change is the same and is the substantive half**: a document
step must accept a save with corrections and **no new file**, and re-judge the step against the
documents it already saved.

### Fences
- **A driver-supplied value is `source='manual'`, `verify_status='pending'`, and the client never
  says so** (AL-29, BR-25.2). registry-svc stamps it. A client that could claim `source='ai'` makes
  AL-29 advisory.
- **A correction must not silently discard the uploaded document.** `registry.onboarding_steps`
  records `documentIds` and the verdict is derived from those rows (C029 decision (4)); a
  fields-only save re-judges that same batch rather than starting a new one.
- **`RequireManualFields` keeps filtering by `AcceptedFor`.** A key that does not belong to the
  document kind is a `400`, not a stored field — otherwise a driver writes `reg_no_match=true`.
- **Editing a value does not clear the ⚑.** BR-25.3 makes a manual field pending *by design*; the
  chip stays up until an officer confirms. The driver may proceed either way.

### Deliverables
- `backend/contracts/registry.yaml` — corrections on the multipart arms of
  `saveVehicleOnboardingStep` **and** `upsertDriverProfile`
- `specs/D3_mageride_api_contracts.md` — both route-table rows updated, in the Δ style MCS-01 used
- `Registry.Api` — `SaveDocumentStepAsync` accepts a step save with no file when the step already
  has saved documents; `OnboardingEndpoints` binds the new parts
- `Registry.Api.Tests` — a doubtful field corrected without a re-upload lands `manual`/`pending`,
  the step stays `PENDING_REVIEW`, the previously-saved documents still back the verdict, and a
  fields-only save against a step with **no** documents is still a `400`
- `shared/kmp` — the two `upload…` client functions take the corrections
- `apps/driver-android` — the ✎ on `ExtractedFieldRow` wired up on SCR-DA-004a/004b (the row
  already has `editLabel` / `onEdit` parameters, unused since C068) and on SCR-DA-003a's licence
  rows; `VehicleOnboardingViewModel` sends them on the next save; tests for both

---

## Part B — SCR-DA-026 cannot render the temporarily-assigned caption

### The finding

The wireframe prints **"Lanka Fleet (Pvt) Ltd · until 30 Jun"** under a fleet-assigned vehicle
(US-13.9). `GET /v1/vehicles/mine` returns `VehicleSummary`, which carries neither. C069 renders
the type, the plate and a `FLEET` badge and omits both facts rather than faking them.

The two halves are **not** the same size, and this is the finding that matters:

* **`fleetName` is free.** `registry.fleets.name` is in registry-svc's own schema
  (D4' §2), joined through `registry.fleet_assignments.fleet_id`. No cross-service call, no
  migration — a column on a query this service already runs.
* **`assignedUntil` has nowhere to come from.** `registry.fleet_assignments` is
  `(id, fleet_id, vehicle_id, driver_id, assigned_at, revoked_at)`. There is **no expiry column**.
  US-13.9 says a temporary assignment **"auto-expires"** and D2' §SCR-DA-026 says the same
  ("assignment auto-expires") — so the requirement exists in three places and the schema can only
  express a manual revocation. **Nothing on the platform can auto-expire an assignment today.**

Treat that second bullet as the real deliverable: it is a D4' gap, not a display gap, and it is
also why no Fleet Portal screen can offer a fixed-term assignment.

### Fences
- **Do not put the fleet name on the vehicle.** It belongs to the assignment; a vehicle can be
  reassigned, and a denormalised copy on `registry.vehicles` would go stale silently.
- **An expired assignment must stop being go-live eligible**, not merely stop displaying. C069's
  `VehicleSummary.canGoLive` is what the driver app and (next) C070's go-online gate both read; if
  `expires_at` is added and nothing enforces it, the app will offer a vehicle dispatch will refuse.
- **`revoked_at` and `expires_at` are different facts** — one is an act, the other a schedule. The
  active-assignment index (`ux_fleet_assign_active`) is keyed on `revoked_at IS NULL`; decide
  deliberately whether it should also consider expiry, and say so in the handoff.

### Deliverables
- A migration adding `registry.fleet_assignments.expires_at` (nullable — an open-ended assignment
  is legitimate), plus whatever makes an expired assignment stop being active
- `specs/D4_mageride_data_model.md` + `specs/server_db_schema.md` — the column
- `backend/contracts/registry.yaml` — `VehicleSummary` gains `fleetName` and `assignedUntil`, both
  optional (a Mode C vehicle has neither)
- `specs/D3_mageride_api_contracts.md` — the `GET /v1/vehicles/mine` row
- `Registry.Api` + tests — the join, and an expired assignment excluded from `mine`
- `shared/kmp` — the two fields on `VehicleSummary`
- `apps/driver-android` — the caption on the assigned rows of `VehiclesScreen`, trilingual, with
  the date rendered through **`BusinessCalendar`** (D-38: Asia/Colombo, never the handset's zone)

---

## Part C — four retired operations are blinding the contract checks

### The finding

`ContractShapeTest` is the test that validates **every** DTO against **its contract schema** —
the check the KMP stack file calls strict enough that "an undeclared property is an error". It has
not validated anything for some time. It throws on its first row:

```
java.util.NoSuchElementException: Key bindOnepayMerchant is missing in the map.
```

Four operations are declared in `ApiOperations` / the typed clients and exist in **no contract**:

| Operation | Retired by |
|---|---|
| `bindOnepayMerchant` | AL-57 — D-11 retired; OnePay has one merchant account per merchant |
| `onepayPaymentWebhook` | AL-57 — `onepay` dropped as a ride method, replaced by `wallet` |
| `lankaqrPaymentConfirm` | AL-47 — the platform-merchant LankaQR ride rail became `scan_driver_qr` |
| `modeBOnepayWebhook` | AL-57 — `onepay` removed from Mode B subscription payments |

Because the lookup throws before any assertion runs, **three `ContractShapeTest` tests and three
`ApiOperationTableTest` tests never execute their check**. That is not a cosmetic failure: C069
found three registry DTOs that had drifted out of shape with `registry.yaml`
(`VehicleRegistration`'s four required-but-optional file ids, `RegisterVehicleResponse`'s
non-null `ocrJobId` and missing `nextStep`, `SaveOnboardingStepResponse`'s missing required
`status`) — a fresh vehicle's `201` **failed to deserialise** — and this suite was supposed to
catch exactly that, on the day the contract changed.

### Scope — deletion only

Remove the four operations from `ApiOperations`, from the typed client interfaces and
implementations, from any DTO that exists solely for them, and from
`DtoRoundTripIdentityTest`/`TypedClientTest`. They are unreachable from an app and their
`operationId`s resolve to nothing.

### Explicitly OUT of scope — and why

**64 in-scope contract operations have no typed client function**, across 13 contracts (wallet 14,
iam 12, support 7, registry 6, subscription 6, content 5, safety 5, ride 3, dispatch 2, and one
each in notification, transit, voip, trip-state). `ContractCoverageTest.EXPECTED_OPERATIONS` is
still `179` against a real in-scope count of **240**.

That is **a component, not a micro-change-set** — it is most of what C013 did, plus the DTOs each
operation needs — and it should get a manifest entry rather than be smuggled in here. Do not start
it. Do not "fix" the `179` constant to make a test pass; the constant is honest and the client is
behind.

**So the wave-1 gate is still red after this change set.** Expect `:shared:testDebugUnitTest` to
go from **13 failures to 6** — five `ContractCoverageTest` and one `ApiOperationTableTest` set
comparison, all of them the 64 missing operations and the stale count. If it does not land on 6,
read what the newly-executing shape checks found: **a shape mismatch they surface is a real
finding, not a regression you introduced.** Record any such finding in the handoff.

---

## Verify

```
dotnet test backend/src/Registry.Api.Tests
./gradlew :shared:testDebugUnitTest :shared:detekt :shared:ktlintCheck
./gradlew :apps:driver-android:testDebugUnitTest :apps:driver-android:assembleDebug
./gradlew :apps:driver-android:detekt :apps:driver-android:ktlintCheck
bash infra/scripts/migrate-verify.sh        # Part B only
```

Baseline to diff against, captured 2026-08-02 at the end of C069:

| Suite | Result |
|---|---|
| `:apps:driver-android:testDebugUnitTest` | **107 passed, 0 failed** |
| `:shared:testDebugUnitTest` | **817 tests, 13 failed** (the drift above) |
| `:shared` / `:apps:driver-android` detekt + ktlint | green |

**Diff the failure set, do not eyeball the count.**

## Definition of Done
- a driver corrects a doubtful insurance expiry on SCR-DA-004a **without re-photographing the
  document**, the value reaches `registry.document_fields` as `manual`/`pending`, and the step
  stays `PENDING_REVIEW` with the ⚑ up (Part A)
- the same is true of `licence_no` / `licence_expiry` on SCR-DA-003a — C068's open finding closed
- SCR-DA-026 prints the assigning fleet and the assignment expiry on a temporarily-assigned row,
  and an **expired** assignment is neither listed nor go-live eligible (Part B)
- `ContractShapeTest` and `ApiOperationTableTest` **execute their assertions** rather than throwing
  on a missing key (Part C)
- `:shared`'s failure set is **6 and is the 64-missing-operations set** — no new failure, and every
  one that remains is named in the handoff
- both C069 wireframe deviations are gone; `build/progress.md`'s C069 row updated to say so

## Two things to confirm rather than assume
- **Whether `registry.fleet_assignments` is populated by anything yet.** C069 could not exercise
  the assigned-vehicle group against real data — the group is derived from `mode != C` in
  `GET /v1/vehicles/mine`, and if fleet-svc does not yet write assignments, Part B's display work
  is untestable end to end and should say so rather than be marked DONE.
- **Whether `ContractShapeTest`, once it runs, is green.** It has not executed in some time. Budget
  for it finding real drift in contracts other than `registry.yaml`; that is the test doing its
  job, and each finding belongs in the handoff whether or not you fix it here.

## Handoff
Append to `build/progress.md`:
- Component: MCS-02 onboarding corrections, fleet-assignment display, retired client operations
- Status: DONE | PARTIAL (explain — and say which of the three parts landed)
- Notes: which correction shape you chose (A1/A2) and why; what `expires_at` now means for the
  active-assignment index; every finding `ContractShapeTest` surfaced once it started running;
  anything C070 needs before it builds the go-online gate
