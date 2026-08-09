# Kubernetes manifest conventions (C124)

Read `infra/CLAUDE.md` first, then `README.md` in this directory for the layout and the reasoning.

## Stack
- Plain manifests + **Kustomize** (no Helm charts for MageRide's own workloads; Helm installs the
  cluster prerequisites only — ingress-nginx, cert-manager, ESO, ArgoCD).
- **ArgoCD** app-of-apps, one instance per cluster. `platform/argocd/README.md`.
- Three overlays for D7' §8's three substrates: `dev` (K3s single node), `staging` (DOKS),
  `production` (DOKS Singapore). The directory is `production` and not `prod` — C132's verify command
  names it.

## Rules
- **`service-catalog.yaml` is the source of truth.** Adding, removing or reshaping a workload is an
  edit there followed by `python3 infra/k8s/tools/generate_manifests.py`. Never hand-edit a file whose
  first line says GENERATED; `k8s-verify.sh` fails the build if you do, and the next regeneration
  would discard it silently.
- **No `kind: Secret` anywhere.** Credentials are `ExternalSecret` (staging/production) or
  `SealedSecret` (dev). Both fences are checked.
- **No custom resource under `base/` or `overlays/`.** They live in `platform/`, so that
  `kubectl apply --dry-run=client -k overlays/<env>` works on a cluster with no CRDs installed.
- **Every workload carries a sync wave** except the data plane, which uses the default 0. The order
  is what gates the migration — `platform/argocd/README.md` has the table, and changing a wave
  changes the gate.
- **RollingUpdate, maxUnavailable 0, maxSurge 1.** `Recreate` needs a ReadWriteOnce volume and a
  `why` in the catalog, or the fence check refuses it.
- **A comment says why, not what.** `replicas: 2` needs no comment; `replicas: 1` on
  provisioning-svc needs the paragraph the catalog gives it.

## Verify
```bash
bash infra/scripts/k8s-verify.sh                                  # 39 checks, no cluster
python3 infra/k8s/tools/generate_manifests.py --check             # drift only
kubectl kustomize infra/k8s/overlays/staging | less               # what the cluster receives
```
The printed C124 verify command's second clause (`kubectl apply --dry-run=client -k …`) needs a
reachable API server even though it says `client` — kubectl builds a RESTMapper from the server's
discovery document. `.github/workflows/k8s-validate.yml` runs it against a kind cluster on every pull
request; `k8s-verify.sh` runs it too when a cluster happens to be reachable and reports it skipped
when not.

## When you change a service
| Change | Where |
|---|---|
| a port, a probe, resources, replicas, an HPA range | `service-catalog.yaml`, then regenerate |
| a secret it reads | `service-catalog.yaml` → the service's `secrets:`, plus `aliases:` if shared |
| where it finds another service | `base/config/service-endpoints.yaml` |
| a platform-wide non-secret setting | `base/config/common-config.yaml` |
| anything that differs per environment | the overlay, never base |
| a new service | one catalog entry. CI's image matrix and the ExternalSecret follow. |

## What not to do
- **Do not add a CronJob for work a service already does in-process.** D7' §10 lists five, and waves
  2–3 implemented every one as a `BackgroundService`. A CronJob beside it runs the work twice — two
  sets of document-expiry notices, two daily-fee charges.
- **Do not put a `Password=` DSN in a ConfigMap.** It is then a password in `kubectl get cm -o yaml`.
  `common-secret` renders both DSNs from one Vault property with ESO's template engine.
- **Do not set `Outbox__*` or `CommandLog__Schema`.** Each service configures its own.
- **Do not use `commonLabels`.** It writes into the immutable Deployment selector.
- **Do not point an overlay at another environment's Vault mount.**
