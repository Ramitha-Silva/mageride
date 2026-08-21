# MCS-06 — ＋ in My Vehicles starts a new vehicle, and Resume continues one

## Identity

This is a **micro-change-set**, not a manifest component, and this file is **hand-written**.

`build/tools/generate_build_plan.py` writes one `build/prompts/Cxxx.md` per entry in
`build/manifest.yaml` and deletes nothing, so this file survives a regeneration untouched — but it
is also not produced by it. **Do not add a component to `build/manifest.yaml` for this work and do
not re-run the generator**: re-running resets the Status column and the whole Session Handoffs log
in `build/progress.md`. Record the session as an ordinary Session Handoff entry instead.

Raised from a handset defect report, 2026-08-21. **The work is done** — this file records the
requirement change that the code now implements, because the code was correct against US-2.27 as
written and the spec is the thing that moved.

---

## The finding

> *"in my vehicles screen, when plus + button is tapped new vehicle onboarding has to start from
> step 1 of 4, but it start from 2 of 4 which is insurance screen."*

Reproduced by reading, not by guessing. `VehiclesScreen`'s ＋ and a row's *Resume ›* both called
`onAddVehicle()`, which navigates to `DriverRoute.VehicleOnboarding` — a route with **no
arguments**. Neither said which vehicle it meant, so the wizard asked
`VehicleOnboardingRepository.resume()`, which searches:

```kotlin
val incomplete = myVehicles().firstOrNull {
    it.isOnboardable && it.onboardingStatus == OnboardingStatus.INCOMPLETE
} ?: return ResumePoint.Fresh
```

Find an unfinished Mode-C vehicle → open it at the server's `nextStep`.

**And `POST /v1/vehicles` IS Step 1/4** (Δ C029): the request carries exactly the type and plate the
`details` step stores, so a vehicle that got past the first screen comes back with
`nextStep = insurance`. Every vehicle abandoned after the first screen therefore leaves the wizard
pointing at **Step 2 of 4 · Insurance** — which makes the reported symptom the *default* outcome of
tapping ＋ with anything unfinished, not an edge case.

**The code was right and the requirement was wrong.** US-2.27 said, verbatim:

> Once the current vehicle's 4 steps are complete and it is **Approved** … **Vehicle Onboarding is
> opened (nav drawer) or ＋ is tapped in My Vehicles, it starts a fresh Step 1/4 for a NEW
> vehicle**. A vehicle that is still **Incomplete** instead resumes at its next incomplete step.

So ＋ resuming an Incomplete vehicle was the specified behaviour, faithfully built. What the story
did not reckon with is that it leaves the driver **no way to add a second vehicle** while a first is
unfinished, and gives them no clue which vehicle the wizard is even about: the header shows a plate
they did not type on a step they did not choose.

## The decision

**＋ means add.** It starts a fresh Step 1/4 unconditionally. Continuing is the row's own
*Resume ›*, one tap away on the same screen, and SCR-DA/DI-006's *Continue*.

The **nav-drawer** entry keeps the old behaviour, and that is deliberate rather than an oversight:
it names no vehicle and is reached from a menu rather than from a list, so "take me back to wherever
my onboarding is" remains the only sensible reading of it. AL-30's *resume at the first non-verified
step, never Step 1* is unchanged for that door.

## What changed

**Spec**

* `specs/user-requirements-document.md` — US-2.27 rewritten to split ＋ from Resume, naming this
  change set and the symptom.
* `specs/D4_mageride_data_model.md` — the `onboarding_status` comment on `registry.vehicles`
  matched to it.
* `specs/D3_mageride_api_contracts.md` is **untouched**. Its line — *"When the vehicle is already
  `approved`, a fresh `POST /v1/vehicles` starts a NEW vehicle at Step 1/4"* — is about the server
  and stays true; nothing on the app-facing surface changes.

**`apps/driver-android`** — no contract, no migration, no server work.

* `VehicleOnboardingSession` gains a **one-shot `WizardIntent`** (`NewVehicle` / `Continue(id)`)
  beside the `vehicleId` it already carried for SCR-DA-006. The route carries no arguments, which is
  the same reason that class exists at all; the intent is consumed once, when the wizard's view
  model is constructed, so a retry after a failed read reuses it rather than falling back to a
  search.
* `VehicleOnboardingRepository.resume(vehicleId)` joins the searching `resume()`. **This also fixes
  a second defect nobody had reported yet**: with two unfinished vehicles, *Resume ›* on the second
  row opened the first, because a search takes whatever `GET /v1/vehicles/mine` returns first.
* `VehiclesScreen` ＋ and SCR-DA-026a's *"Yes, onboard ›"* → `startNewVehicle()`; a row's
  *Resume ›* → `resumeOnboarding(row)`; SCR-DA-006's *Continue* → `continueOnboarding()`.

## What a later session should know

* **The three doors now mean three different things**, and the wizard is told which rather than
  deducing it. If a fourth entry point is ever added, it has to say what it means — a navigation to
  `DriverRoute.VehicleOnboarding` with nothing seated is read as the nav-drawer case.
* **iOS is unchanged.** `apps/driver-ios` mirrors these screens and has the same ambiguity; it was
  not touched, because that host cannot compile Swift. Worth folding into the next Mac session.
* There is still **no UI affordance that says which vehicle the wizard is about** once it is open —
  the header shows the plate, but a driver who arrived from the nav drawer has no way to tell that
  it resumed rather than started fresh. Out of scope here; worth a wireframe note.
