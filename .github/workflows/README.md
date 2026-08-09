# CI — how a component's `verify_cmd` becomes a job

Every component in `build/manifest.yaml` carries a `verify_cmd`. That command is the
component's definition of done, and it is the same command CI runs — a component is not
"green in CI" by some separate standard. This file says which job runs which shape of
command, and what to do when you add a component whose `verify_cmd` does not fit one.

`ci.yml` is the entry point only, and it still deploys nothing. Full CD — ArgoCD, promotion by image
SHA, rollback — **landed in C124** and is the six workflows in [Delivery](#delivery) below.

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
generator. C124 gave them two homes:

| `verify_cmd` | Job |
|---|---|
| `kubectl apply --dry-run=client -k infra/k8s/overlays/staging` (C124) | `k8s-validate.yml`, on every PR that touches `infra/k8s/` — against a kind cluster, because `--dry-run=client` still needs one for discovery |
| `kubectl apply --dry-run=client -k infra/k8s/overlays/production` (C132) | the same job, alongside staging and dev |
| `bash infra/replica/deploy.sh --dry-run` (C125) | `nightly.yml`, probing |
| `bash security/run-asvs-checks.sh` (C127/C128) | `nightly.yml`, probing |
| `k6 run load/…` (C129) | `nightly.yml`, probing |
| `bash chaos/run-drills.sh` (C130) | `nightly.yml`, probing |
| `bash acceptance/sg/run.sh` (C131) | `nightly.yml`, probing — and it warns that a GitHub runner is not in Singapore |

"Probing" means the job checks for its own script and skips with a `::notice::` when the component
that owns it has not landed — the same pattern the `android` leg uses for the wave-1 Gradle modules,
so `nightly.yml` needs no edit as waves 5 and 6 complete.

### Adding a component

1. If its `verify_cmd` is a `dotnet test`, a `./gradlew`, an `npm --prefix portals` or an
   `xcodebuild` command, **nothing to do** — the existing leg already runs it, provided the
   project is in `backend/MageRide.sln`, `settings.gradle.kts` or `portals/package.json`.
2. If it is a new script under `infra/scripts/`, add a job. Give it an explicit `runs-on`,
   a `timeout-minutes`, and a comment naming the component it verifies.
3. If it needs a deployed environment, add a probing job to `nightly.yml`.

---

## Delivery

Six workflows, none of which holds a cluster credential. The only write any of them makes is a commit
to `infra/k8s/overlays/<env>/images/`; ArgoCD, inside the cluster, is the only thing that talks to an
API server. Full walkthrough: `docs/runbooks/deploy.md`.

| Workflow | Trigger | What it does |
|---|---|---|
| `images.yml` | called by `cd.yml`; `workflow_dispatch` | 34 images: build, push `sha-<7>`, **cosign keyless sign the digest**, SBOM, build provenance. The matrix comes from `infra/k8s/service-catalog.yaml`, so a new service needs no edit here. |
| `deploy.yml` | called | migration gate → write the tag → commit → verify. Reusable, one environment per call. |
| `cd.yml` | `ci` completed on `main` | the automatic path: images → dev → staging. Refuses to start unless that CI run SUCCEEDED. |
| `promote.yml` | `workflow_dispatch` | staging → production. Requires the `production` environment's reviewer, and refuses a SHA staging is not running. |
| `rollback.yml` | `workflow_dispatch` | writes a previous tag. The same mechanism as a deploy, deliberately. `docs/runbooks/rollback.md`. |
| `k8s-validate.yml` | PRs touching `infra/k8s/` or a workflow | `k8s-verify.sh`, kubeconform, actionlint, and the printed C124 verify command against a kind cluster. |
| `nightly.yml` | 18:30 UTC (00:00 Asia/Colombo) | signature verification of what is deployed, ArgoCD drift, and the wave-5/6 suites above. |

### The migration gate

Twice, checking different things:

1. **`deploy.yml`, before the promotion commit exists.** `infra/scripts/migration-gate.sh` diffs the
   migrations against the SHA that environment is *currently running* (read from the overlay — the
   repository is the record of what is deployed), refuses a modified or deleted released script,
   refuses anything that is not expand-only, and applies the set twice to a throwaway
   `timescale/timescaledb-ha:pg16`. **A failure means no commit, so ArgoCD has nothing to sync and the
   previous version keeps serving.**
2. **ArgoCD sync wave 1, in the cluster.** The same image, the real database. A Job is Healthy only
   when it completes, and wave 2 is every service — so a failed migration leaves them all on the image
   they were already running. This half catches a hand-run `argocd app sync`.

Backward compatibility is not optional because the rollout is `maxUnavailable: 0, maxSurge: 1`: old
and new pods serve against one schema for the length of the rollout, which across 34 workloads is
minutes.

### Verifying an image signature

```bash
cosign verify ghcr.io/mageride/ride-svc:sha-1a2b3c4 \
  --certificate-identity-regexp '^https://github.com/Ramitha-Silva/mageride/' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com

cosign verify-attestation --type spdxjson ghcr.io/mageride/ride-svc:sha-1a2b3c4 \
  --certificate-identity-regexp '^https://github.com/Ramitha-Silva/mageride/' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

Keyless, so there is no key to store or rotate: the signature's certificate names the workflow, the
repository and the commit that built the image. `nightly.yml` runs this over every deployed image, and
it is the only check that would notice an image pushed by hand.

### Repository configuration the delivery path needs

| | | |
|---|---|---|
| environment | `dev`, `staging` | no protection |
| environment | `production` | **required reviewers** — this is the deploy gate |
| variable | `IMAGE_NAMESPACE` | `ghcr.io/<owner>` unless a `mageride` organisation exists |
| variable | `ARGOCD_SERVER` | optional; enables `argocd app wait` instead of a health probe |
| secret | `ARGOCD_AUTH_TOKEN` | with it |

`GITHUB_TOKEN` covers the rest: `packages: write` to push, `id-token: write` to sign, `contents:
write` to commit the promotion.

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
