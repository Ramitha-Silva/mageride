# MageRide

Multi-modal transport platform for Sri Lanka — public transport tracking (Mode A), private
vehicle sharing (Mode B) and on-demand standby rides plus package delivery (Mode C), across
four native mobile apps, three web surfaces and a .NET microservice backend.

> **Specs are the single source of truth.** Everything buildable is derived from `specs/`.
> If code contradicts a spec, the spec wins — file a micro-change-set instead of coding around it.
> Conventions live in `CLAUDE.md` at the repo root and in each source directory.

---

## Repo map

| Path | What lives here | Stack |
|------|-----------------|-------|
| `specs/` | The authoritative spec set — ADD v3.5, URD v2.9, D1'–D7', DB schemas, traceability matrix, walkthrough, and the approved wireframes | Markdown + HTML |
| `build/` | The build plan: `manifest.yaml`, generated per-component prompts, progress log, screen-coverage matrix | YAML + Markdown |
| `backend/` | .NET solution — one Minimal API project per service, plus the shared kernel and migrations | .NET 10, C# 14, Dapper over Npgsql |
| `db/` | DbUp versioned `.sql` migrations, applied by `backend/src/MageRide.Migrations` | PostgreSQL 16 + PostGIS + TimescaleDB |
| `shared/kmp/` | Kotlin Multiplatform module — DTOs, API client, domain logic, auth, local DB, test kit. Gradle path `:shared` | KMP (Android + iOS targets) |
| `apps/driver-android/` | Driver app. Gradle path `:apps:driver-android` | Kotlin, Jetpack Compose |
| `apps/passenger-android/` | Passenger app. Gradle path `:apps:passenger-android` | Kotlin, Jetpack Compose |
| `apps/driver-ios/` | Driver app — Xcode project, not a Gradle project | Swift, SwiftUI |
| `apps/passenger-ios/` | Passenger app — Xcode project, not a Gradle project | Swift, SwiftUI |
| `portals/tailwind-preset/` | `@mageride/tailwind-preset` — the D2' §A design tokens for every web surface | TypeScript |
| `portals/admin/` | Admin Portal · `admin.mageride.lk` | Next.js + Tailwind CSS |
| `portals/fleet/` | Fleet Portal · `fleet.mageride.lk` | Next.js + Tailwind CSS |
| `portals/web-passenger/` | Passenger web subview · `passenger.mageride.lk` (no login) | Next.js + Tailwind CSS |
| `infra/` | Docker Compose, Kubernetes manifests, env templates, deploy scripts | Docker, K8s |

Two directories named `build` exist and mean different things: the top-level `build/` is the
**MageRide build plan** and is tracked in git; every `<module>/build/` is **Gradle output** and is
ignored. The root Gradle project writes to `.gradle/root-build/` so it can never collide with the
build plan (see `build.gradle.kts`).

---

## Which component builds what

`build/manifest.yaml` is the plan of record: **132 components across waves 0–6**. Each component
has one generated prompt at `build/prompts/Cxxx.md` naming its scope, spec anchors, fences,
deliverables and definition of done.

| Wave | Components | What it delivers |
|------|-----------|------------------|
| 0 | C001–C010 (10) | Repo scaffold, shared kernel, the whole database schema, OpenAPI contracts, API gateway, dev compose, CI skeleton |
| 1 | C011–C019 (9) | The KMP shared module — models, API client, auth, domain logic, local DB, test kit |
| 2 | C020–C044 (25) | Walking skeleton (one booked ride end to end) then the core services |
| 3 | C045–C066 (22) | Business services — fares, billing, subscriptions, safety, support, BFFs |
| 4a | C067–C084 (18) | Both Android apps at full wireframe fidelity |
| 4b | C085–C102 (18) | Both iOS apps, parity-fenced to 4a (**built and verified on macOS**) |
| 4c | C103–C117 (15) | Tailwind preset and all three web surfaces |
| 5 | C118–C126 (9) | Contract tests, E2E, observability, replica deployment |
| 6 | C127–C132 (6) | Security, load, chaos and acceptance |

Rules that govern the plan:

- **One component per session.** Read its prompt, build it, append a 3-line handoff to
  `build/progress.md`.
- **No wave N+1 work begins until every wave N verify command passes.**
- `build/prompts/*.md`, `build/progress.md` and `build/screen_coverage.md` are **generated** by
  `build/tools/generate_build_plan.py`. Change `build/manifest.yaml`, then re-run the generator —
  never hand-edit a generated file. (The Status column and the Session Handoffs log in
  `progress.md` are the one exception: build sessions write those, and re-running resets them.)
- `build/screen_coverage.md` maps all **202 wireframe screen IDs** to the single component that
  owns each one. No screen may be silently dropped.
- Known spec gaps and conflicts the planner already resolved are recorded under
  **Planner findings** in `build/progress.md` — read them before touching `specs/`.

---

## Toolchain

Pinned by `global.json` (.NET), `gradle/wrapper/gradle-wrapper.properties` +
`gradle/libs.versions.toml` (Gradle side) and `portals/package.json` `engines` (Node).

| Tool | Version | Pinned in |
|------|---------|-----------|
| .NET SDK | 10.0.1xx | `global.json` |
| Gradle | 9.6.1 | `gradle/wrapper/gradle-wrapper.properties` (SHA-256 verified) |
| JDK | 17 | `gradle/libs.versions.toml` → `jvmToolchain` |
| Kotlin | 2.4.10 | `gradle/libs.versions.toml` |
| Android Gradle Plugin | 9.3.1 | `gradle/libs.versions.toml` |
| Android SDK | minSdk 26 (URD NFR-22) · compile/target 36 | `gradle/libs.versions.toml` |
| Node | 24 LTS (D7' Δ 2026-07-23) | `portals/package.json` |
| PostgreSQL | 16 + PostGIS + TimescaleDB | `infra/` |

---

## Getting started

```bash
# backend
dotnet build backend/MageRide.sln -c Release

# mobile / KMP  (needs an Android SDK; point at it with ANDROID_HOME or local.properties)
./gradlew :shared:testDebugUnitTest detekt ktlintCheck

# web portals
npm --prefix portals ci
npm --prefix portals run build
```

### Build-host caveats

- This repo is built on the **same Contabo VPS that hosts the lightweight production replica**.
  Keep the replica stack **down** during waves 0–4 — ~17–20 GB of replica plus a heavy build will
  not fit in 24 GB. Use the slim dev compose for per-component verification.
- **iOS is not verified on this Linux host.** `gradle.properties` turns on Kotlin/Native klib
  cross-compilation, so `./gradlew :shared:compileKotlinIosArm64` *type-checks* `src/iosMain`
  here — but linking a framework, `:shared:assembleXCFramework` and any `iosTest` still need
  macOS with Xcode. `assembleXCFramework` fails with that message rather than a linker error.
  A KMP verify on this host covers the common and Android targets only.
- **An Android SDK is required** from C011 onward — anything that applies the Android Gradle
  Plugin needs `platforms;android-36` and `build-tools;36.0.0`. Point Gradle at it with
  `ANDROID_HOME` or an untracked `local.properties` carrying `sdk.dir=…`. GitHub's
  `ubuntu-latest` runner ships one preinstalled, so CI needs no extra step.

---

## Environments

| Environment | Where | Purpose |
|-------------|-------|---------|
| Dev | Docker Compose (`infra/docker-compose.dev*.yml`) | Local development and per-component verification |
| Lightweight production replica | Single VPS, Docker Compose, Contabo EU | Testing / CI / demos on **synthetic data only** — see `specs/lightweight-production-replica.md` |
| Production | DigitalOcean Kubernetes (DOKS), Singapore | Hosting decision 2026-07-05 |
