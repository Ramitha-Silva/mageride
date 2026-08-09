# ArgoCD — app-of-apps, sync policies and the ordering that gates a migration

D7' §7: *"`RollingUpdate` (maxUnavailable 0, maxSurge 1) via ArgoCD; dev→staging→prod promotion
by image SHA tag + ArgoCD app-of-apps; DB migrations gated pre-deploy."* This directory is the
second and third clauses; the first is in every generated Deployment.

## The shape

```
project.yaml                     AppProject `mageride` — the blast radius. Applied by hand, once.
app-of-apps/<env>.yaml           the root Application for one cluster. Applied by hand, once.
applications/<env>/secrets.yaml  wave 0 — ExternalSecrets (staging/production) or SealedSecrets (dev)
applications/<env>/platform.yaml wave 1 — every workload, from infra/k8s/overlays/<env>
```

Two `kubectl apply`s per cluster and nothing else, for the life of the cluster:

```bash
kubectl apply -f infra/k8s/platform/argocd/project.yaml
kubectl apply -f infra/k8s/platform/argocd/app-of-apps/staging.yaml
```

**One ArgoCD per cluster.** Each root Application reads only its own environment's directory and
targets `https://kubernetes.default.svc`. A single instance managing all three would hold admin
credentials for production, which puts production one Git-path compromise away from the dev
pipeline.

## Sync waves, and why the numbers matter

Two levels of ordering, both by `argocd.argoproj.io/sync-wave`.

**Between Applications** (set on the child Applications, honoured by the root):

| Wave | Application | Why here |
|---|---|---|
| 0 | `mageride-secrets` | A Deployment whose `envFrom` Secret does not exist holds in `CreateContainerConfigError`. Credentials converge first or the first deploy looks like a stuck rollout. |
| 1 | `mageride-<env>` | Everything else. |

**Inside the platform Application** (set on the resources themselves):

| Wave | Resources | Why here |
|---|---|---|
| −2 | Namespace, ServiceAccount | Everything references them. The namespace also carries the Pod Security Admission labels, which is why `CreateNamespace=false`. |
| −1 | `common-config`, `service-endpoints`, `portal-config` | Mounted by every pod in wave 2. |
| 0 | Postgres, PgBouncer, Redis, Redpanda, EMQX | 0 is kustomize/ArgoCD's default, so the data plane carries no annotation at all. |
| 1 | **`migrate` Job**, `redpanda-topics` Job | The gate. See below. |
| 2 | 31 backend workloads + 3 portals, with Services, HPAs, PDBs | |
| 3 | the three Ingresses, the suspended `osm-pipeline` CronJob | An Ingress that resolved before its backend had endpoints answers 503 to real traffic. |

### The migration gate is the wave ordering

ArgoCD applies one wave at a time and **waits for the wave to be Healthy before starting the
next**. A `Job` is Healthy only once it has completed successfully. So:

> a failed migration leaves wave 2 unapplied, and every service and portal keeps running the
> image it was already running.

That is C124's definition-of-done sentence, and it holds for a hand-run `argocd app sync` as
much as for the pipeline — there is no step anybody can skip.

Both Jobs carry `sync-options: Replace=true,Force=true`, because a Job's `spec.template` is
immutable and a new image SHA cannot be patched into one: without it, the sync fails on the gate
itself rather than on the migration, which is a much more confusing red. The consequence is that
the migration Job runs on **every** sync. That is the gate working — DbUp records each script in
`public.schema_versions`, a no-op pass takes seconds, and `infra/scripts/migrate-verify.sh`
exists to prove the second pass applies nothing.

**Why not a `PreSync` hook.** A PreSync hook runs before *everything*, including the namespace
and Postgres. On a first sync there is no database to migrate and no `common-secret` to read the
DSN from, so the gate would fail on a healthy cluster — the worst kind of gate, one that cries
wolf on the day it is installed.

## Sync policies

| | dev | staging | production |
|---|---|---|---|
| `automated` | yes | yes | **yes** |
| `prune` / `selfHeal` | yes | yes | yes |
| `allowEmpty` | secrets only | no | no |
| what a human decides | nothing | nothing | **the promotion** |

Production is automated, and the gate is on changing the desired state rather than on applying
it: only `.github/workflows/promote.yml` writes `overlays/production/images/`, and that job runs
in the GitHub `production` environment, which requires a reviewer's approval before it starts.

That trade buys two things a manual sync gives up:

- **`selfHeal` reverts a `kubectl edit`.** With manual sync, an incident fix applied by hand
  persists invisibly until somebody syncs — and the next deploy then applies a change nobody
  reviewed alongside the one they did.
- **A rollback is one commit.** `rollback.yml` writes the previous SHA and ArgoCD applies it. A
  manual-sync production needs either a cluster credential in the pipeline or a human at a
  terminal during the incident.

### `ignoreDifferences`: the HPA owns `replicas`

Every generated Deployment declares `replicas` — it must, a Deployment has the field — and
every scalable one also has an HPA. The moment the HPA scales, git and the cluster disagree, and
with `selfHeal: true` ArgoCD scales it back while the HPA scales it up again, for ever.
`ignoreDifferences` on `/spec/replicas` is what lets both be correct: git sets the initial size,
the autoscaler owns it afterwards.

The secrets Applications ignore `/status` for the same class of reason: the operator writes it,
so diffing it leaves every Application permanently OutOfSync.

## Operating notes

- **`argocd app diff mageride-staging` before a promotion** shows exactly what the tag change
  will do. On a normal promotion it is 34 image lines and nothing else — that is what makes the
  promotion commit reviewable, and it is why the image list is a separate kustomize Component.
- **`argocd app history mageride-production`** is the rollback candidate list; the runbook
  (`docs/runbooks/rollback.md`) prefers a git-side rollback so the cluster and the repository
  never disagree about what production is running.
- **An OutOfSync `mageride-secrets` with a `SecretSyncedError`** is Vault, not Kubernetes: the
  property is missing, or the ClusterSecretStore's role lost its policy. `kubectl -n mageride
  describe externalsecret <name>` names the property it could not read.
