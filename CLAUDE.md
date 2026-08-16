# MageRide — Global Conventions

## Stack
- Backend: .NET 10 **Minimal API**, C#, PostgreSQL **16** + PostGIS, TimescaleDB, Redis, Redpanda (Kafka), EMQX (MQTT)
- Data access: **Dapper over Npgsql** — hand-written parameterised SQL, repository per bounded context.
  **NO EF Core / DbContext / LINQ-to-SQL anywhere** (AL-53). Migrations: DbUp/Grate versioned `.sql` scripts.
- Mobile: Kotlin Multiplatform (KMP) shared module + native Android (Kotlin/Compose) / iOS (Swift/SwiftUI)
- Portals: Next.js, TypeScript, React + **Tailwind CSS — sole styling system (AL-52)**; shared
  `@mageride/tailwind-preset` carries the D2 §A tokens; no MUI/Bootstrap/styled-components/CSS-in-JS
- Infra: Docker Compose (dev) → **lightweight production replica** (single-VPS Docker Compose,
  Contabo EU — testing/CI/demos only, synthetic data, see `specs/lightweight-production-replica.md`)
  → **production: DigitalOcean Kubernetes (DOKS), Singapore** (hosting decision 2026-07-05)

## Build Host (read before running anything heavy)
- This repo is built ON the Contabo VPS (Ubuntu, 24 GB) — the SAME box that hosts the lightweight
  production replica. Keep the full replica stack DOWN during Waves 0–4; the ~17–20 GB replica and
  heavy builds do not fit together. Use the slim dev compose for per-component verification.
- iOS and KMP-iOS targets do NOT compile on this Linux host. iOS prompts generate code here;
  compile/verify runs on a Mac (or macOS CI runner). KMP verify on this host = common + Android
  targets only.

## Universal Rules
- **Specs are the single source of truth.** All specs live in `specs/`. If code contradicts a spec,
  the spec wins — file a micro-change-set if the spec needs updating.
- **Money as minor units.** All currency values stored and transmitted as integers (cents/paisa).
- **Trilingual resources.** All user-facing strings must support Si (Sinhala), Ta (Tamil), En (English).
  Use resource files, never hardcode strings.
- **ride-svc ≠ trip-state-svc.** ride-svc owns Mode C (on-demand). trip-state-svc owns Mode A/B
  (scheduled). Never cross this boundary.
- **Outbox pattern for all cross-service events.** No direct HTTP calls between services for state
  changes — use the transactional outbox (D6' §2.4).
- **Geocoding is query-svc's, and the key is `Query__NominatimBaseUrl`.** The self-hosted Nominatim is
  reached by `Query.Api/Geo/NominatimClient.cs` (registered as `IGeocoder`) behind
  `GET /v1/geo/search` and `GET /v1/geo/reverse`, configured by `QueryOptions` reading
  `Query:NominatimBaseUrl`.
  - **`Transit__NominatimBaseUrl` does not exist in the code. Never create it.** C125 wired it and the
    geocoder was deployed with nothing pointed at it: `/v1/geo/reverse` kept answering 503 and
    `/v1/geo/search` kept falling back to the caller's saved and recent places — which `QueryOptions`'
    own remarks describe as looking like a working search box with a thin index.
  - **transit-svc's `/v1/geo/parse-maps-link` is a different thing and touches no geocoder.** It
    resolves a short Google-Maps URL to a lat/lng (AL-20). It shares the `/v1/geo` prefix with
    query-svc's two routes, which is exactly how the mistake above was made.
  - **No Google Places fallback, ever** (D3' map hard rule, D-14). Unset means degrade and say so —
    `/v1/geo/reverse` answers 503 and search falls back to saved/recent, announced at start-up.
  - `infra/env/.env.app.example` sets `http://nominatim:8080/` — a CONTAINER hostname, right for a
    stack that has nominatim in it (DOKS).
  - **The lightweight replica must override it to `http://45.77.37.208:8080`** — a bare IP, no domain
    name, because that geocoder is on its own VPS and `nominatim` does not resolve from the replica.
    `infra/replica/nominatim/deploy-nominatim.sh` writes it into `.env.replica`.

## Build Manifest
- The build plan lives in `build/manifest.yaml` — **133 components, waves 0–6**.
- Each session works on ONE component only; read its prompt at `build/prompts/Cxxx.md`.
- After completing a component, append a 3-line handoff to `build/progress.md`.
- No wave N+1 work begins until all wave N verify commands pass.
- **`build/prompts/*.md`, `build/progress.md` and `build/screen_coverage.md` are GENERATED**
  from the manifest by `build/tools/generate_build_plan.py`. Change the manifest, then re-run
  the generator — never hand-edit a generated file. Re-running resets the Status column and the
  Session Handoffs log in `progress.md`, so only re-run it when the manifest itself changes.
- Known spec gaps and conflicts the planner resolved are recorded under
  **Planner findings** in `build/progress.md` — read them before touching `specs/`.

## Spec Anchors (how to reference)
- Format: `specs/D3_mageride_api_contracts.md#section-name`
- ADD requirements: `ADD: [R-01, R-02, ...]`
- Traceability: `specs/traceability_matrix.md`
