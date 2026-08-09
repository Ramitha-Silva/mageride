# `infra/k8s` — the Kubernetes manifests (C124)

D7' §5's manifest set, for the three substrates of D7' §8. Nothing here is applied by hand except two
ArgoCD objects per cluster; everything else is a commit.

```
service-catalog.yaml            the ONE list. 31 backend workloads, 3 portals, the migrator,
                                every credential and where it comes from.
tools/generate_manifests.py     -> base/services/*, base/portals/*, platform/external-secrets/base/*,
                                   overlays/*/images/*   (and --matrix, which CI's image build reads)
tools/set_image_tag.py          reads and writes one environment's promoted tag. The only thing a
                                deploy changes.
tools/check_fences.py           the C124 fences, against a rendered overlay.

base/                           the platform, at the spec's shape, with no environment in it
  namespace.yaml                Namespace (restricted PSA) + the one ServiceAccount
  config/                       common-config (§4.1), service-endpoints, portal-config
  data/                         Postgres, PgBouncer, Redis, Redpanda, EMQX  (+ emqx/, redpanda/ conf)
  services/                     31 generated workloads
  portals/                      3 generated Next.js surfaces
  ingress/                      api. / api./hubs / admin. fleet. passenger.
  jobs/                         the migration gate; the suspended osm-pipeline CronJob

overlays/dev|staging|production one per D7' §8 substrate. `production`, not `prod` — C132's verify
                                command names the directory.

platform/                       cluster prerequisites and custom resources
  argocd/                       AppProject, app-of-apps, per-env Applications  (README.md)
  external-secrets/             Vault -> Secret, staging + production
  sealed-secrets/               the K3s/MVP path (README.md), dev
```

**Verify:** `bash infra/scripts/k8s-verify.sh` — 39 checks, no cluster needed.
**Deploy:** you don't. `docs/runbooks/deploy.md`.

---

## Why a generator

Thirty-four images share one shape, and the parts that differ — a port, a probe, a secret list, a
replica count — are exactly the parts worth reading side by side. Written by hand, the difference
between two services would be invisible in a diff and drift between them undetectable. Here it is one
catalog entry per service, `--check` makes drift a red build, and the CI image matrix and the
ExternalSecret set come from the same list as the manifests — so **adding a service is one catalog
entry and never a workflow edit**, and a Deployment whose image nothing builds cannot exist.

Same convention as `build/tools/generate_build_plan.py`: the output is not edited. Every generated
file says so on its first line.

---

## Why per-service and not the composed `app-services` container

D7' §2.1's canonical layout puts 21 domain services in one container. That is the LIGHTWEIGHT
REPLICA's layout — one VPS, C125 — and D7' §2 says the opposite of production ("All services 2 GB / 1
vCPU in production pods"), while D7' §5 prints a Deployment *per stateless service*.

The repository settles it: every service under `backend/src` is its own `Microsoft.NET.Sdk.Web` host
with its own `Program.cs`, and the composed `app-services` / `hot-path` / `fanout` images have no
project and no Dockerfile at all (the C118 and C025 handoffs both record this). So the per-service
split is the only shape that builds, and it is the one D7' §5 describes.

### The capacity consequence, which is a real spec tension

D7' §5's template requests `cpu: 500m, memory: 1Gi` per service. Thirty-four workloads plus the data
plane at those numbers want roughly **20 vCPU and 40 GB of requests**. D7' §8's P1–P2 substrate is
**3 nodes × 4 vCPU / 8 GB = 12 vCPU / 24 GB**. The two do not fit, and neither number is wrong —
§2.1 avoids the problem by co-locating everything, which is what a single VPS has to do.

How it is handled here, visibly rather than quietly:

- **base** carries §5's template verbatim. It is the spec's manifest and it should be readable as such.
- **overlays/dev** drops *requests* to 50m/128Mi and leaves every limit alone. Requests are a
  scheduling claim, not a ceiling — that is what makes forty pods fit on one 6-vCPU box while nothing
  is allowed to take more than it was.
- **overlays/production** raises Postgres and Redpanda, and leaves the application tier at the
  template. **That still over-subscribes a 3-node pool**, so the launch node pool has to be bigger
  than §8's row, or the request figures have to come down with measurements behind them.

That is a capacity decision with money attached, and it is **C132's** — its second deliverable is
"capacity plan for 10k vehicles / 100k passengers with the documented scale-out triggers". The
arithmetic above is the input.

---

## What is deliberately not here

| | Why |
|---|---|
| **Patroni, Redis Sentinel, HAProxy+Keepalived** | D7' §8's launch topology. Each needs an operator or a failover controller, plus a restore procedure and a drill. **C132.** The data plane here is single-instance and the production overlay says so out loud. |
| **LiveKit + coturn** | Host UDP (D6' §6) — a media plane cannot be a Deployment behind a Service. C132 ("LiveKit+coturn pinned SGP"). |
| **Nominatim** | A separate 8 GB VPS (D-14), not a cluster workload. |
| **MinIO** | Production storage is Cloudflare R2 (D7' §8) — an endpoint and a credential. The dev overlay adds a single-node MinIO because a dev cluster has no R2 bucket. |
| **An OTLP collector / Prometheus / Grafana** | C119 built the observability stack for the compose project. `Otel__Endpoint` is empty in every overlay until one exists in-cluster, because an endpoint pointed at nothing makes every exporter retry on a timeout. **For tcp-adapter that is its only telemetry path** — it has no `/metrics` to scrape. Raised in the C124 handoff. |
| **NetworkPolicies** | Worth having and not in C124's deliverables. A default-deny in this namespace touches all 34 workloads and the data plane; it belongs with C127's ASVS review. |
| **`document-expiry`, `credential-rotation`, `daily-fee-reset`, `pdpa-fulfillment`, `gtfs-import` CronJobs** | D7' §10 lists them, and waves 2–3 implemented every one as an IN-PROCESS worker. Adding the CronJobs would run the work twice. See the header of `base/jobs/osm-pipeline.yaml`. |

---

## The three fences, and where each is enforced

**RollingUpdate, maxUnavailable 0, maxSurge 1.** In every generated Deployment; asserted by
`tools/check_fences.py` against the rendered overlay, so an overlay patch cannot undo it. The one
exception is `provisioning-svc`, which is `Recreate` at one replica because its ReadWriteOnce CA
volume makes maxUnavailable 0 a deadlock — the catalog says so and the fence check requires a PVC
before it allows `Recreate`.

**Secrets from Vault via ESO, never from a repository file.** No `kind: Secret` exists anywhere under
`infra/k8s/` and the verify fails if one appears. `k8s-verify.sh` §6 checks the harder direction too:
every credential in `infra/env/*.example` is either delivered by an ExternalSecret or listed under
`unwiredSecrets:` in the catalog with the reason. A forgotten internal key does not fail a build — it
fails one direction of one call with a 401, in production, weeks later.

**Migrations gated pre-deploy, and backward-compatible.** Two places. Before the promotion commit
exists: `infra/scripts/migration-gate.sh` (the expand/contract rules — a `RollingUpdate` with
maxUnavailable 0 means old and new pods share one schema for the length of the rollout). In the
cluster: ArgoCD sync wave 1, where a failed Job leaves wave 2 unapplied. `platform/argocd/README.md`
has the wave table.

---

## Things that will bite

- **`runAsUser` is numeric and must stay that way.** Every image ends on `USER app` or `USER node` — a
  *name* — and kubelet refuses a pod with `runAsNonRoot: true` whose image user it cannot prove is
  non-root. 1654 is `APP_UID` in the .NET 10 images; 1000 is `node`.
- **`commonLabels` is not used, and must not be.** It writes into `spec.selector.matchLabels`, which
  is immutable on a Deployment: adding a label later would need every workload deleted and recreated.
  Use `labels[].includeSelectors: false`.
- **No custom resource lives under `base/` or `overlays/`.** `kubectl apply --dry-run=client` needs a
  REST mapping for every kind, so one `ExternalSecret` in that tree would make the C124 verify depend
  on whether ESO happened to be installed. That is why `platform/` is separate.
- **`Outbox__*` and `CommandLog__Schema` are absent from every ConfigMap on purpose.** Each service
  configures its own in `Application.Build`; a shared value would have dispatch-svc publishing
  `offer.created` onto `ride.events`. `base/config/service-endpoints.yaml` explains it.
- **`sha-0000000` is unpullable on purpose.** An overlay that was never promoted must fail to pull
  rather than quietly run whatever `latest` happens to be.
- **A StatefulSet's `volumeClaimTemplates[].storageClassName` is immutable after the first apply.**
  The overlays set it explicitly; changing it later is a delete-and-restore, not an edit.
- **`tcp-adapter` is a StatefulSet with no volume.** Stable identity, not storage — which is why the
  overlays' storage-class patch selects on `app.kubernetes.io/component=data` rather than on kind.
