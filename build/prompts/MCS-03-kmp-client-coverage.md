# MCS-03 — close the KMP typed-client's contract coverage

## Identity
This is a **micro-change-set**, not a manifest component, and this file is **hand-written**.

`build/tools/generate_build_plan.py` writes one `build/prompts/Cxxx.md` per entry in
`build/manifest.yaml` and deletes nothing, so this file survives a regeneration untouched — but it
is also not produced by it. **Do not add a component to `build/manifest.yaml` for this work and do
not re-run the generator**: re-running resets the Status column and the whole Session Handoffs log
in `build/progress.md`. Record this session as an ordinary Session Handoff entry instead.

Read `CLAUDE.md`, then `shared/kmp/CLAUDE.md` — particularly its **Model layer** and **API layer**
sections, which are the rules this change set is entirely an application of.

**Run this after MCS-02 and before C070.** It is the last thing standing between the tree and a
green wave-1 gate.

## The finding

`ContractCoverageTest` is the wave-1 gate's own check that **every operation in the sixteen
app-facing contracts has a typed client function**. It is red on **65 operations**, and the
constants it measures against are stale in the same direction:

| | |
|---|---|
| In-scope contract operations | **241** |
| `ApiOperations` rows / client functions | **176** |
| `ContractCoverageTest.EXPECTED_OPERATIONS` | **179** — the C025 figure |
| `ContractCoverageTest.EXPECTED_ATTESTED` | **20**, against a real **23** |

This is drift accumulated by every service component since C013: a contract gained an operation and
the client did not follow. It is not one team's mistake — the gate that should have caught each one
has been red since C067 arrived, so every component since has had a red suite as its baseline and
no way to tell its own breakage from the inherited kind.

**Why now, and not "later".** C070–C075 are the driver app's remaining six screen groups and they
consume dispatch, ride, wallet, support and subscription. Every one of those contracts is on the
list. A screen group that finds its operation missing has to either stop or invent a client
function inline, and the second is how `:shared` stops being the single API surface.

## The 65, by contract

wallet **14** · iam **12** · support **7** · registry **6** · subscription **6** · content **6** ·
safety **5** · ride **3** · dispatch **2** · trip-state **1** · transit **1** · voip **1** ·
notification **1**

About **29 of the 65 are `/v1/internal/**`** — mTLS, service-to-service, unreachable from an app.
They are still in scope: `shared/kmp/CLAUDE.md` is explicit that the kit covers *"all 176
operations, including the mTLS and webhook ones… they exist so no contract is half-covered"*, and
`FakeApiBackend` can only stub an operation the table knows about.

Regenerate the exact list rather than trusting this one — the diff script is in the MCS-03 handoff.

## Scope

For each of the 65:

1. **DTOs** in `data/models/{service}/`, following the Model-layer rules: the contract is the
   shape; `allOf` flattened; required → non-null, optional → nullable with `= null`; money as
   `Long` minor units; enums matching the DB CHECK domain with an explicit `@SerialName` and a
   `wire` property where the spelling is not upper camel case.
2. **A client function** on the existing `{Service}Api` interface and its `Ktor{Service}Api`
   implementation, going through `apiGet`/`apiPost`/`apiPostExempt`/`apiPut`/`apiDelete` — never
   `HttpClient` directly.
3. **An `ApiOperations` row**, with the contract's verb, path, success status and the client's own
   request/response serializers.
4. **Round-trip coverage** in the `DtoRoundTrip*Test` file for its service, populating **every**
   field — that is what makes `ContractShapeTest` check the shape.

Then update `EXPECTED_OPERATIONS` and `EXPECTED_ATTESTED` to the real figures, with the reasoning
in the comment the way C025 left it.

## Fences — do not cross these

- **The contract is the shape, and `ContractShapeTest` is the judge.** It runs now (MCS-02
  unblinded it) and it is strict: an undeclared property is an error, because it means the DTO has
  a field the contract does not. Do not "tidy" a payload into a nicer shape.
- **`X-Attestation` is the contract's, not a preference.** An operation whose YAML carries the
  `XAttestation` parameter passes `attested = true`; one that does not, does not. Three wallet
  operations on this list are attested and that is why `EXPECTED_ATTESTED` moves.
- **`apiPostExempt` is for `x-idempotency-exempt` operations only** — the provider callbacks that
  dedupe on `provider_transaction_id` (R-19). `chargeDailyFeeBeforeTrip` is currently called with
  `apiPost` and its contract says exempt; that is a real one-line bug on the list.
- **Never render `title`/`detail`/`message` from a `ProblemDetails`** (D-26). Errors are
  `MageRideError` keyed on the kebab `code`.
- **Money is `Long` minor units, never `Double`.** A `…Minor` field stays flat and the DTO
  implements `MoneyHolder`.
- **Portal-only contracts stay out** — `admin-bff`, `fleet`, `provisioning`, `public-bff` and
  `reputation` are Next.js surfaces and are not modelled here. The sixteen in `CONTRACTS` are the
  scope; do not widen it.
- **An internal/mTLS operation says so in its KDoc.** It exists for coverage and no app calls it.
  Copy the phrasing the existing ones use.
- **Do not weaken a test to reach green.** If a contract turns out to be internally inconsistent —
  AL-57's half-application is already one such case — record it in the handoff and leave the test
  honest. A green suite bought by a lowered assertion is worth less than a red one that is telling
  the truth.

## Deliverables
- 65 operations covered end to end: DTOs, client functions, `ApiOperations` rows, round-trip tests
- `ContractCoverageTest`'s two constants corrected, with their reasoning
- `build/progress.md` — a Session Handoff entry naming every contract finding the work surfaced

## Definition of Done
- `./gradlew :shared:testDebugUnitTest detekt ktlintCheck` — **green, 0 failures.** That is the
  wave-1 gate and the point of the exercise.
- `:apps:driver-android:testDebugUnitTest` + `assembleDebug` still green, and passenger-android and
  the e2e harness still compile — `:shared` is their API surface and a changed DTO reaches them.
- No `@Suppress` and no lowered assertion added to reach any of the above.

## Verify
```
./gradlew :shared:testDebugUnitTest :shared:detekt :shared:ktlintCheck
./gradlew :apps:driver-android:testDebugUnitTest :apps:driver-android:assembleDebug
./gradlew :apps:passenger-android:compileDebugKotlin :e2e:walking-skeleton:compileKotlin
```

Baseline to diff against, captured 2026-08-02 at the end of MCS-02:

| Suite | Result |
|---|---|
| `:shared:testDebugUnitTest` | **817 tests, 6 failed** (5 `ContractCoverageTest`, 1 `ApiOperationTableTest`) |
| `:apps:driver-android:testDebugUnitTest` | **110 passed, 0 failed** |

## Two things to confirm rather than assume
- **Whether every one of the 65 has a coherent contract.** MCS-02 found `listFaqArticles` declared
  in two contracts and AL-57 retired at settlement but not at booking. Expect one or two more; each
  is a finding for the handoff, not a thing to paper over.
- **Whether `ApiOperations` should still be hand-maintained.** It is described as derived
  ("regenerate rather than hand-extend") and there is no generator in the tree. Adding 65 rows by
  hand is the moment to say so plainly in the handoff, whichever way it is done here.
