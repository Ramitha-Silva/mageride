# C0 — Build Planner Session

## Your Role
You are the build planner for MageRide. Your job is to produce FOUR deliverables:
1. `build/manifest.yaml` — the complete build manifest
2. `build/prompts/C001.md` through `build/prompts/C1xx.md` — all thin prompt files
3. `build/progress.md` — initialized with all components, status = pending
4. `build/screen_coverage.md` — the wireframe screen-coverage matrix (see "Screen Coverage
   Matrix" below) — every `SCR-*` ID in the wireframes mapped to exactly one component

## Inputs (read ALL of these before producing output)
- `specs/` — all D1'–D7' specs, B0 report, traceability matrix
- `specs/architecture-design-document.md` — ADD v3.5 (requirement IDs: R-xx, P-xx, AL-01…AL-56, E-xx)
- `specs/user-requirements-document.md` — URD v2.9 (Epics 1–28)
- `specs/server_db_schema.md` + `specs/mobile_db_schema.md` — canonical DDL mirrors of D4
- `specs/lightweight-production-replica.md` — the Contabo EU test-deployment container layout
- `specs/wireframes/` — all 7 HTML wireframe sets. These are the **team-reviewed and approved
  structural/functional baseline**: the screens that exist, what is on them, their states, and
  how they connect. Each screen block embeds its `SCR-*` ID in the markup. Ignore `index.html`
  and any non-HTML files in the folder when enumerating screens.
- `CLAUDE.md` — global conventions

## Manifest Requirements (per component entry)
Each entry in manifest.yaml must have:
- `id`: C001, C002, ... (sequential)
- `name`: component name (e.g., ride-svc, driver-android-auth-screens)
- `wave`: 0–6 (see wave definitions below)
- `depends_on`: list of component IDs that must be complete first
- `spec_anchors`: list of exact file paths + section references into specs/
- `scope`: what this component covers AND explicit exclusions/fences
- `definition_of_done`: checklist of acceptance criteria
- `verify_cmd`: shell command to validate (e.g., dotnet test, npm test, gradle test)
- `est_sessions`: estimated Claude sessions (usually 1, max 3 for complex components)
- `screens`: (UI components only — Waves 4a/4b/4c) the exact `SCR-*` IDs from the wireframes
  this component implements, plus the wireframe file they live in. Every wireframe screen ID
  must appear in exactly one component's `screens` list (see Screen Coverage Matrix below).

## Wave Definitions
- **Wave 0**: Repo scaffold, root configs, PostgreSQL DDL (split: core + telemetry/Timescale),
  Docker Compose dev, CI skeleton, D3'→OpenAPI conversion
- **Wave 1**: KMP shared module (models, API client, domain logic, auth, test kit)
- **Wave 2**: Core backend services (iam, registry, provisioning, trip-state, ride-svc,
  dispatch, reputation, position-processor, fanout, query, mqtt-bridge, persistence-writer,
  tcp-adapter, fleet-health)
- **Wave 3**: Business backend (subscription, wallet, fare, notification, safety, support,
  ocr, content, voip, Mode B subscriptions, **transit-svc** (GTFS routing + feed-version
  lifecycle, AL-18/AL-54), **fleet-svc**, **admin-bff**, **public-bff** (SCR-WT API, AL-44))
- **Wave 4a**: Android apps (Driver + Passenger, ~6-7 screen-group prompts each)
- **Wave 4b**: iOS apps (Driver + Passenger, parity-fenced to Android scope). The build host is
  Linux — mark every iOS `verify_cmd` as **"runs on macOS"** (code is generated on the VPS;
  compile/verify happens on a Mac or macOS CI runner)
- **Wave 4c**: Portals (Admin incl. SCR-AP-016 GTFS Dataset Manager, Fleet, Web Passenger
  SCR-WT pages) — NO Wallet Portal (removed by AL-02); all Tailwind CSS (AL-52)
- **Wave 5**: Integration (contract tests, E2E flows, CI/CD full, **deploy to the lightweight
  production replica** per `specs/lightweight-production-replica.md` + day-0 GTFS full-feed
  load via SCR-AP-016, AL-55)
- **Wave 6**: Hardening (security/ASVS, anti-spoof, load tests, chaos drills — VoIP-quality and
  tracker-RTT acceptance runs happen in the Singapore region, not on the EU replica)

## Walking Skeleton (MUST include)
Insert a vertical-slice milestone at approximately prompt C020–C025 (after Wave 1):
- Minimal iam + registry + dispatch(stub) + ride-svc(happy path) + fare(stub)
- One Android passenger book flow + one Android driver accept flow
- Wired through real EMQX/Redpanda/SignalR
- Deployed on Docker Compose
- One booked ride, end to end

## Screen Coverage Matrix (MUST produce — `build/screen_coverage.md`)
The wireframes are the team-approved baseline; no screen may be silently dropped. Build the
matrix BEFORE writing the Wave 4 prompts, in this order:

1. Enumerate the screen universe mechanically from the 7 wireframe HTML files (per-platform IDs):

       grep -hoE 'SCR-[A-Z]+-[0-9]+[a-z]?' \
         specs/wireframes/driver_android.html specs/wireframes/driver_ios.html \
         specs/wireframes/passenger_android.html specs/wireframes/passenger_ios.html \
         specs/wireframes/web_admin.html specs/wireframes/web_fleet.html \
         specs/wireframes/web_passenger.html | sort -u

2. Cross-check against the D2' per-screen tables and the URD §6 Screen Inventory. CAUTION:
   D2' addenda use combined IDs (`SCR-PA/PI-015a` = both platforms) — expand them before
   comparing; a per-platform regex scan of D2' under-reports coverage.
3. Write `build/screen_coverage.md` as a table: `| SCR ID | Wireframe file | Component ID | Notes |`.
   Every ID from step 1 gets a row; the Component ID must exist in `manifest.yaml` and list that
   screen in its `screens` field.
4. An ID you cannot map to a component (or that appears only as a cross-reference with no screen
   block) is a **spec gap** — report it in your final output; never drop it from the matrix.

## Thin Prompt File Format
Each `build/prompts/Cxxx.md` should be 30–60 lines and follow this template:

    # Cxxx — [Component Name]

    ## Identity
    You are building [component] for the MageRide platform.
    Read CLAUDE.md and [stack]/CLAUDE.md before starting.

    ## Spec Anchors (read these files)
    - [list of exact file paths + section anchors from manifest]

    ## Scope
    [What to build. What NOT to build. Explicit fences.]

    ## Screens (UI components only — baseline = wireframes)
    - [SCR-* IDs from the manifest `screens` field + the wireframe file they live in,
       e.g. "SCR-DA-004a-c — specs/wireframes/driver_android.html"]

    ## Deliverables
    - [list of files/folders to create]

    ## Definition of Done
    - [checklist from manifest]
    - (UI components) every screen listed under ## Screens is implemented to match the
      layout, controls, states, and navigation shown in its wireframe — the wireframes are
      the team-approved baseline; any deviation requires a micro-change-set first. D2' still
      governs design tokens, Tailwind styling rules, and trilingual string resources.

    ## Verify
    ```
    [verify_cmd from manifest]
    ```

    ## Handoff
    When complete, append to `build/progress.md`:
    - Component: Cxxx [name]
    - Status: DONE | PARTIAL (explain)
    - Notes: [any spec gaps found, decisions made]

## Critical Warnings to Encode
- **Wireframes are the team-approved structural/functional baseline for every screen** — every
  UI prompt must carry its wireframe file + `SCR-*` IDs; a screen that deviates from its
  wireframe without a micro-change-set is a bug. D2' remains authoritative for tokens, styling
  (Tailwind mapping), and trilingual resources.
- ride-svc ≠ trip-state-svc (R-01 fence) — in EVERY ride/trip prompt
- KMP must complete before ANY app prompt
- **Dapper over Npgsql only — any EF Core/DbContext reference in generated code is a bug (AL-53)**
- **Tailwind CSS is the sole styling system on every web surface (AL-52)** — reject MUI/Bootstrap/CSS-in-JS
- Fleet Portal (Epic 27, SCR-FP-002a/004, AL-49/50) must be included
- Web Passenger subview (SCR-WT + public-bff) must be included
- **GTFS Dataset Manager (Epic 28, SCR-AP-016, AL-54/55) + transit-svc feed-version lifecycle
  must be included — the full national feed loads day-0 before go-live**
- Driver-QR attestation (Epic 26) must be included; number masking is REMOVED (AL-48 — calls are
  direct-dial real numbers post-accept; VoIP = "Free call")
- Mode B passenger subscriptions (AL-23/24/25) must be included
- Driver onboarding restructure (AL-27) must be included

## progress.md Initial Format
Initialize with a table:

    | ID | Component | Wave | Status | Session Date | Notes |
    |----|-----------|------|--------|--------------|-------|
    | C001 | ... | 0 | PENDING | | |
    ...

## Output
1. Write `build/manifest.yaml`
2. Write all `build/prompts/C001.md` through `build/prompts/C1xx.md`
3. Write `build/progress.md` (initialized, all PENDING)
4. Write `build/screen_coverage.md` (every wireframe SCR-* ID mapped to a component)
5. Report: total component count, prompts per wave, screen-coverage totals (N wireframe IDs
   found / N mapped — must be equal), and any spec gaps or conflicts found (including
   unmappable screen IDs)
