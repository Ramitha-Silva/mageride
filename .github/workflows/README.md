# CI — how a component's `verify_cmd` becomes a job

Every component in `build/manifest.yaml` carries a `verify_cmd`. That command is the
component's definition of done, and it is the same command CI runs — a component is not
"green in CI" by some separate standard. This file says which job runs which shape of
command, and what to do when you add a component whose `verify_cmd` does not fit one.

`ci.yml` is the entry point only. Full CD — ArgoCD, promotion by image SHA, rollback — is
**C124** (D7' §7's `deploy` job), and nothing here deploys anything.

## Jobs

| Job | Runner | Runs | Components |
|---|---|---|---|
| `build (backend)` | `ubuntu-latest` | `dotnet build` + `dotnet test` over `backend/MageRide.sln`, then the container templates | C001 C002 C008 C010, and every `dotnet test …` component from wave 2 on |
| `build (android)` | `ubuntu-latest` | `./gradlew` — `projects` today, the wave-1 and wave-4a commands as those modules land | C001, C011–C019, C067–C084 |
| `build (ios)` | **`macos-14`** | `:shared:assembleXCFramework` then `xcodebuild` | C085–C102 |
| `build (portal)` | `ubuntu-latest` | `npm --prefix portals ci && … run lint && … run build` | C001, C103–C117 |
| `contracts` | `ubuntu-latest` | `spectral lint backend/contracts/*.yaml` | C007 |
| `migrations` | `ubuntu-latest` | `infra/scripts/migrate-verify.sh` — apply, re-apply, re-apply without the journal | C003–C006 |
| `compose` | `ubuntu-latest` | `infra/scripts/slim-verify.sh` | C009 |

## Mapping a `verify_cmd`

The 135 `verify_cmd` entries in the manifest come in seven shapes. Six already have a home:

| `verify_cmd` starts with | Count | Job | Notes |
|---|---|---|---|
| `dotnet test …` / `dotnet build …` | 55 | `build (backend)` | The job runs the **solution**, so a new test project needs no workflow edit — add it to `backend/MageRide.sln` and it is covered |
| `./gradlew …` | 28 | `build (android)` | The step probes `./gradlew -q projects` and runs the wave-1 / wave-4a commands only once those modules exist, so it grows with the repository |
| `runs on macOS — xcodebuild …` | 18 | `build (ios)` | **Never** on Linux — see the fence below |
| `npm --prefix portals …` | 15 | `build (portal)` | The root script fans out with `--workspaces --if-present` |
| `bash infra/scripts/migrate-verify.sh` | 4 | `migrations` | |
| `docker compose -f infra/docker-compose.dev.slim.yml …` | 1 | `compose` | Covered by `slim-verify.sh`, which is a superset |
| `npx … spectral-cli lint …` | 1 | `contracts` | |

The seventh shape is **wave 5 and 6**: `bash infra/replica/deploy.sh`, `bash chaos/run-drills.sh`,
`k6 run load/…`, `kubectl apply --dry-run -k infra/k8s/…`, `bash acceptance/sg/run.sh`. None of
these belong on a PR runner — they need the deployed replica, a Singapore region, or a load
generator. They are **C124's** to schedule, not `ci.yml`'s.

### Adding a component

1. If its `verify_cmd` is a `dotnet test`, a `./gradlew`, an `npm --prefix portals` or an
   `xcodebuild` command, **nothing to do** — the existing leg already runs it, provided the
   project is in `backend/MageRide.sln`, `settings.gradle.kts` or `portals/package.json`.
2. If it is a new script under `infra/scripts/`, add a job. Give it an explicit `runs-on`,
   a `timeout-minutes`, and a comment naming the component it verifies.
3. If it needs a deployed environment, it is C124's, not this file's.

## Rules this workflow enforces

- **The iOS leg is pinned to `macos-14` as a literal.** D7' §7 writes it as
  `runs-on: ${{ matrix.target == 'ios' && 'macos-14' || 'ubuntu-latest' }}`; here every
  matrix leg carries its own literal `runner`, and the leg's first step fails the job if
  `RUNNER_OS` is not `macOS`. iOS and KMP-iOS targets do not compile on Linux at all, so a
  leg that silently landed on Ubuntu would skip its way to green.
- **Migrations are validated by applying versioned `.sql` twice, never by `dotnet ef`**
  (AL-53, D7' §1). `migrate-verify.sh` fails if the second apply executes anything, and a
  grep step fails the job if `Microsoft.EntityFrameworkCore`, `DbContext` or `dotnet ef`
  appears anywhere under `backend/`.
- **Integration tests may not silently skip on CI.** The TestKit fixtures fall back to
  `Assert.Skip` when Docker is unreachable so a developer without a daemon can still run
  the unit tests; the backend job sets `MAGERIDE_REQUIRE_CONTAINERS=1`, which turns that
  fallback into a hard failure. A runner with broken Docker is a red build, not a green one
  that tested nothing.
- **Container images run as a non-root user and carry a healthcheck.** The backend job
  builds both templates and asserts `Config.User == app` and a non-null `Healthcheck`.

## Container templates

`infra/docker/` holds the three shapes from D7' §2.1/§2.2. All three take the repository
root as build context, because restore needs `global.json` and `backend/Directory.*.props`
(or, for a portal, the workspace lockfile) from above the project directory.

| File | Base | For | Selected by |
|---|---|---|---|
| `Dockerfile.service` | `aspnet:10.0-alpine` | anything serving HTTP — `api-gateway`, `app-services`, and each domain service when production splits them out | `--build-arg SERVICE=<project dir>` |
| `Dockerfile.worker` | `runtime:10.0-alpine` | `tcp-adapter` and the `hot-path` consumers — no ASP.NET, so no `/health/*`; D7' §5.1 probes a TCP socket instead | `--build-arg SERVICE=… [--build-arg HEALTH_PORT=5023\|none]` |
| `Dockerfile.portal` | `node:24-alpine` | the three Next.js surfaces (Node 20 is EOL — Δ 2026-07-23) | `--build-arg PORTAL=<admin\|fleet\|web-passenger> --build-arg PORT=…` |

The entry assembly is resolved at build time from the published `*.runtimeconfig.json`
rather than assumed to be `${SERVICE}.dll` — `ApiGateway` publishes `MageRide.ApiGateway.dll`.

## Running a job locally

Every job is a script you can run yourself; that is the point of keeping the logic in
`infra/scripts/` rather than in YAML.

```bash
dotnet build backend/MageRide.sln -c Release && dotnet test backend/MageRide.sln -c Release
bash infra/scripts/migrate-verify.sh          # the migrations job
bash infra/scripts/slim-verify.sh             # the compose job
npx --yes @stoplight/spectral-cli lint 'backend/contracts/*.yaml' --ruleset backend/contracts/.spectral.yaml
docker build -f infra/docker/Dockerfile.service --build-arg SERVICE=ApiGateway -t mageride/api-gateway:dev .
```

`migrate-verify.sh` and `slim-verify.sh` both start containers and remove them on exit.
Keep the lightweight production replica **down** while they run — this box hosts both
(root `CLAUDE.md`, "Build Host").

## Linting this workflow

`ci.yml` is checked two ways, and both are cheap enough to run before pushing:

```bash
python3 -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml'))"   # the C010 verify
actionlint .github/workflows/ci.yml                                          # expressions, contexts, shell
```
