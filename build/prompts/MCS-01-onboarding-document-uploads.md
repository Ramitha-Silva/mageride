# MCS-01 — registry-svc onboarding document uploads

## Identity
This is a **micro-change-set**, not a manifest component, and this file is **hand-written**.

`build/tools/generate_build_plan.py` writes one `build/prompts/Cxxx.md` per entry in
`build/manifest.yaml` and deletes nothing, so this file survives a regeneration untouched — but it
is also not produced by it. **Do not add a component to `build/manifest.yaml` for this work and do
not re-run the generator**: re-running resets the Status column and the whole Session Handoffs log
in `build/progress.md`. Record this session as an ordinary Session Handoff entry instead.

Read `CLAUDE.md`, then the stack file for `backend/src/Registry.Api`.

**Run this after C068 and before C069.** Raised by the C068 handoff, gap (a). It blocks C068's
fifth DoD line and C069's second.

## The finding

`docs.uploads` has **no writer for any onboarding document**, driver or vehicle.

* `PUT /v1/drivers/profile` requires `profilePhotoFileId`, `licenseFrontFileId`,
  `licenseBackFileId` as already-uploaded ids, and `OnboardingService.RequireUploadAsync` rejects
  an id that is not on file **and not owned by the caller**.
* `PUT /v1/vehicles/{id}/onboarding/{step}` declares a `multipart/form-data` arm in
  `registry.yaml` that `OnboardingEndpoints.SaveStepAsync` **does not implement** — it binds
  `OnboardingStepBody? body` and reads `body?.FileId` / `body?.FileIdBack`. The KMP client
  (`RegistryApi.uploadVehicleOnboardingStep`) posts multipart to it today and would bind null.
* The only `INSERT INTO docs.uploads` in `Registry.Api` is `PayoutDocumentStore`, scoped to the
  three AL-58/AL-59 payout kinds.
* `ocr.yaml`'s header states the gap: *"Filling `docs.uploads` for onboarding is still unowned"*.
* `Registry.Api.Tests`' `RegistryHarness.SeedUploadAsync` seeds the row directly and its own
  summary says *"as the upload surface would. No service owns that table yet."*

Consequence: neither Profile Setup (SCR-DA-003a) nor the Mode-C wizard (SCR-DA-004a/b/c) can
complete against a real gateway, on either app.

## Spec Anchors (read these files)
- `specs/D3_mageride_api_contracts.md#registry-svc-route-table` — the `PUT /v1/drivers/profile`
  row (JSON body) and the `POST /v1/vehicles` note, which already reads
  *"Request (multipart or JSON w/ uploaded file IDs)"*
- `specs/architecture-design-document.md` — **AL-27** (profile setup precedes Home),
  **AL-29** (per-field source/verify), **AL-43** (drag-crop capture provenance), **D-36**
  (PII redaction, SSE-KMS, 90-day auto-delete)
- `specs/D4_mageride_data_model.md` — `docs.uploads`, including the `captured_via` CHECK domain
- `specs/D6_mageride_integration.md#7-5` — `registry-svc → ocr-svc`; ocr-svc fetches bytes from
  object storage by `storage_url` and never receives them in a request
- `backend/contracts/registry.yaml`, `backend/contracts/ocr.yaml`

## Scope
Give `docs.uploads` an owner for onboarding documents, so an app can send bytes.

**Decide the shape first, and record the decision in the handoff.** Two candidates:

* **(A) Multipart arms on the two routes that already reference uploads** —
  `PUT /v1/drivers/profile` gains one (mirroring the vehicle step's), and
  `PUT /v1/vehicles/{id}/onboarding/{step}` gains the *implementation* of the arm its contract
  already declares. **Recommended**: it adds no `operationId`, so the 180-operation count,
  `ApiOperations`, `ContractCoverageTest` and `ApiOperationTableTest` are untouched; it makes an
  existing KMP client function correct rather than adding a second way to do the same thing; and
  it keeps one request per user action, which matters on a Sri Lankan mobile network.
* **(B) A standalone `POST /v1/docs/uploads`** returning an id the two routes then reference.
  Adds an operation and a round trip per file, but is the only option if a document ever has to
  be uploaded before its owning record exists. Nothing in D1'/D2' asks for that today.

## Fences — do not cross these
- **`PayoutDocumentStore` is the pattern; do not invent a second storage path.** Bytes through the
  kernel's `IObjectStore`, row after bytes (an orphan object is swept by NFR-28; the other order
  leaves a record pointing at a file nobody can open), filesystem fallback when `Storage:*` is
  unset.
- **`captured_via` is not optional for an onboarding document.** AL-43 makes drag-crop capture
  versus gallery pick a **fraud signal**; `RegistryHarness` already writes `camera_dragcrop`. The
  client has to say which, and the column's CHECK domain is D4's. (This is the opposite of the
  payout store, whose remarks explain why it leaves the column NULL — do not copy that reasoning
  across.)
- **`RequireUploadAsync`'s ownership check stays.** Without it a driver can attach another
  driver's upload and have its extracted licence number verify against their own profile.
- **D-36 retention applies**: `auto_delete_at` per `RegistryOptions`, SSE-KMS, and nothing raw
  leaves the perimeter before the redaction pre-pass. ocr-svc still fetches by `storage_url`; do
  not start posting bytes to it.
- **No new document kinds.** `driving_license`, `insurance`, `revenue_license` and the vehicle
  photos already exist in `registry.documents`.
- Profile Setup remains **vehicle-less** (AL-27). The driver's three documents are owned by the
  driver, not by a vehicle.

## Deliverables
- `specs/D3_mageride_api_contracts.md` — the `PUT /v1/drivers/profile` row updated to name the
  multipart arm, matching how `POST /v1/vehicles` is already written
- `backend/contracts/registry.yaml` — the multipart arm on `upsertDriverProfile`; the
  `saveVehicleOnboardingStep` arm reviewed against what the implementation will actually accept
- `Registry.Api` — the endpoint(s) accepting the form (`.DisableAntiforgery()`, `HasFormContentType`
  guard, size limit → `413`), an onboarding document store beside `PayoutDocumentStore`, and
  `OnboardingService` taking either ids or freshly written uploads
- `Registry.Api.Tests` — integration coverage that a multipart Profile Setup and a multipart
  vehicle step both produce a `docs.uploads` row, the right `captured_via`, the right retention,
  and that a foreign upload id is still refused
- `shared/kmp` — `RegistryApi.uploadDriverProfile(...)` mirroring `uploadVehicleOnboardingStep`;
  no `ApiOperations` row if you took option (A)
- `apps/driver-android` — **delete the `DriverDocumentUploader` seam** (interface,
  `UnavailableDriverDocumentUploader`, `DocumentUploadUnavailableException`, the Koin binding,
  `error_upload_unavailable` in all three `strings.xml`), point `DriverProfileRepository.submit`
  at the multipart call, and update `ProfileSetupViewModelTest` (three tests reference it) and
  `RecordingDocumentUploader`
- `build/progress.md` — flip **C068** to DONE; record the change set as a Session Handoff entry

## Definition of Done
- a driver completes Profile Setup end to end against a running registry-svc, with no
  pre-seeded `docs.uploads` row, and reaches Home with no vehicle
- a Mode-C onboarding step accepts its image in the same request as its fields
- `captured_via` distinguishes a drag-crop capture from a gallery pick on every onboarding upload
- an upload id belonging to another driver is still refused
- `Registry.Api.Tests` green; `:shared`'s failure set is **no larger** than before the change
  (13 pre-existing contract-drift failures — diff it, do not eyeball it)
- `apps/driver-android` verify green with the seam removed

## Verify
```
dotnet test backend/src/Registry.Api.Tests
./gradlew :shared:testDebugUnitTest detekt ktlintCheck
./gradlew :apps:driver-android:testDebugUnitTest :apps:driver-android:assembleDebug
```

## Two things to confirm rather than assume
- Whether `Registry.Api.Tests` needs the slim dev compose on this host (Postgres, and MinIO if
  `Storage:*` is exercised). The root `CLAUDE.md` permits the slim compose for per-component
  verification; the full replica stays down.
- Whether `ContractCoverageTest`'s `ClientSourceIndex` copes with a third function sharing one
  `operationId`. `uploadVehicleOnboardingStep` is the existing precedent so it probably does — but
  that suite is **already red** on 13 pre-existing failures, and it would be easy to mistake a new
  failure for an old one. Capture the baseline before you start.

## Handoff
Append to `build/progress.md`:
- Component: MCS-01 registry-svc onboarding document uploads
- Status: DONE | PARTIAL (explain)
- Notes: which shape you chose and why; whether `saveVehicleOnboardingStep`'s contract arm changed;
  anything C069 needs to know before it builds SCR-DA-005
