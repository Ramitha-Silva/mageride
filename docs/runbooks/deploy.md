# Runbook — deploy MageRide (C124)

Two parts: **bootstrapping a cluster** (once, per environment) and **the deploy path** (every
commit, automatic). If something has gone wrong with a deploy that already happened, you probably
want [rollback.md](rollback.md).

---

## First action

There isn't one. **A deploy needs no action.** A commit merged to `main` with green CI builds and
signs 34 images, gates the migrations, promotes to dev and then staging, and ArgoCD applies it. The
only manual step in the whole path is the approval on production:

```bash
gh workflow run promote.yml -f sha=<the sha staging is running> -f reason="<release name>"
# then approve the `production` environment in the run's page
```

What is running where, from the repository:

```bash
for e in dev staging production; do
  printf '%-11s %s\n' "$e" "$(python3 infra/k8s/tools/set_image_tag.py "$e" --print)"
done
```

---

## 1. The path a commit takes

```
merge to main
  → ci.yml            build, test, contracts, migrations, compose        (C010)
  → cd.yml            waits for ci to be green
      → images.yml    34 images: build, push, cosign sign, SBOM, provenance
      → deploy.yml    dev:      migration gate → write tag → commit
      → deploy.yml    staging:  migration gate → write tag → commit
  → ArgoCD            notices the commit, applies the overlay wave by wave
promote.yml (approval) → deploy.yml production: the same three steps
```

The pipeline holds **no cluster credentials**. Its only write is a commit to
`infra/k8s/overlays/<env>/images/`; ArgoCD, inside the cluster, is the only thing that talks to an
API server. A compromised runner can propose a deploy and cannot perform one.

**The migration gate is what makes a bad schema change safe.** It runs before the promotion commit
exists, so a failure means there is nothing new to sync and the previous version keeps serving; and
it runs again in-cluster at ArgoCD sync wave 1, where a failed Job leaves wave 2 — every service —
unapplied. `infra/k8s/platform/argocd/README.md` has the wave table.

---

## 2. Bootstrapping a cluster (once per environment)

Everything below is done once. After it, the cluster's contents are whatever `main` says they are.

### 2.1 Prerequisites in the cluster

```bash
# ingress-nginx (staging/production; K3s ships Traefik and the dev overlay expects it)
helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx
helm install ingress-nginx ingress-nginx/ingress-nginx -n ingress-nginx --create-namespace

# cert-manager, for the Ingress certificates. The ClusterIssuers (`letsencrypt-staging-dns`,
# `letsencrypt-prod-dns`) are DNS-01 — the portals and the API are on subdomains and HTTP-01 would
# need each of them reachable before its certificate exists.
helm install cert-manager jetstack/cert-manager -n cert-manager --create-namespace \
  --set crds.enabled=true

# metrics-server, or every HPA reports <unknown>/70% for ever. DOKS ships it; K3s does not, which
# is why the dev overlay deletes the HPAs instead.
kubectl top nodes    # confirms it is there

# External Secrets Operator (staging/production)
helm install external-secrets external-secrets/external-secrets \
  -n external-secrets --create-namespace

# sealed-secrets (dev only) — see infra/k8s/platform/sealed-secrets/README.md
kubectl apply -f https://github.com/bitnami-labs/sealed-secrets/releases/download/v0.27.3/controller.yaml

# ArgoCD
helm install argocd argo/argo-cd -n argocd --create-namespace
```

### 2.2 Vault (staging/production)

One KV v2 mount per environment, and one Kubernetes auth mount per cluster. The per-environment
mount is what lets every ExternalSecret be identical across environments — `remoteRef.key: ride-svc`
resolves inside whichever mount the local `ClusterSecretStore` points at, so no environment name
appears in any manifest and no file can be copied into the wrong environment.

```bash
vault secrets enable -path=mageride-staging kv-v2
vault auth enable -path=kubernetes-staging kubernetes
vault write auth/kubernetes-staging/config kubernetes_host="https://$KUBERNETES_HOST"

vault policy write mageride-staging - <<'EOF'
path "mageride-staging/data/*"     { capabilities = ["read"] }
path "mageride-staging/metadata/*" { capabilities = ["read", "list"] }
EOF

vault write auth/kubernetes-staging/role/mageride-staging \
  bound_service_account_names=external-secrets \
  bound_service_account_namespaces=external-secrets \
  policy=mageride-staging ttl=1h
```

Seed the credentials. The paths and property names are the `aliases` and `commonSecrets` tables in
`infra/k8s/service-catalog.yaml`; `infra/scripts/k8s-verify.sh` §6 fails the build if a credential
the platform needs is not wired, so that file is the authoritative list.

```bash
M=mageride-staging
rand() { head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n'; }

# One value per service that VALIDATES an internal key. Every caller reads the same property.
vault kv put $M/internal-keys \
  iam=$(rand)      registry=$(rand)   provisioning=$(rand) trip-state=$(rand) \
  ride=$(rand)     dispatch=$(rand)   reputation=$(rand)   fare=$(rand) \
  wallet=$(rand)   subscription=$(rand) notification=$(rand) safety=$(rand) \
  support=$(rand)  content=$(rand)    ocr=$(rand)          fleet=$(rand) \
  fleet-billing=$(rand) payout=$(rand) query=$(rand)

# Platform-wide. `postgres_superuser_password` is the one common-secret renders both DSNs from.
vault kv put $M/common \
  postgres_superuser_password=$(rand) \
  s3_access_key=<from Cloudflare R2> s3_secret_key=<from Cloudflare R2> \
  mqtt_session_token_secret=$(rand) \
  emqx_erlang_cookie=$(rand) \
  ghcr_username=<a github user> ghcr_read_token=<a packages:read PAT>

# Third parties. Each one is issued by somebody else; an empty value means that feature is
# unavailable and the service says so at start-up rather than failing.
vault kv put $M/providers \
  onepay_api_key=... onepay_webhook_secret=... \
  lankaqr_merchant_id=... combank_ipg_webhook_secret=... \
  notify_lk_user_id=... notify_lk_api_key=... sms_secondary_api_key= \
  fcm_service_account_json=@fcm.json apns_p8_key=@apns.p8 apns_key_id=... apns_team_id=... \
  livekit_api_key=... livekit_api_secret=... gemini_api_key=... bank_api_key= \
  play_integrity_service_account_json=@play.json app_attest_app_id=...

# Per-service, for the credentials only that service holds.
vault kv put $M/iam-svc \
  Jwt__SigningKeyPem=@jwt-signing-key.pem \
  Jwt__RetiredSigningKeyPems__0="" \
  Jwt__RefreshTokenKey=$(rand) Auth__PhoneHashKey=$(rand) Oidc__Google__ClientSecret=...
vault kv put $M/ride-svc            Otp__PepperKey=$(rand)
vault kv put $M/fare-svc            Fare__EstimateTokenKey=$(rand)
vault kv put $M/provisioning-svc    Provisioning__ErrorReportSigningKey=$(rand) \
                                    psk_signing_key="" ca_chain_crt=""     # see §5
vault kv put $M/subscription-svc    Subscription__LankaQrWebhookSecret=... \
                                    Subscription__FileLinkSigningKey=$(rand)
vault kv put $M/support-svc         Support__FileLinkSigningKey=$(rand)
vault kv put $M/transit-svc         Transit__Gtfs__DownloadSigningKey=$(rand)
vault kv put $M/fleet-svc           Fleet__ErrorReportSigningKey=$(rand)
vault kv put $M/admin-bff           AdminBff__Documents__SigningKey=$(rand)
vault kv put $M/emqx                tls_crt=@emqx.crt tls_key=@emqx.key
```

> **`Jwt__RetiredSigningKeyPems__0` is seeded EMPTY on purpose.** It is the 90-day JWKS overlap slot
> and the rotation procedure writes it. Wiring it now means a rotation never has to edit a manifest —
> and a rotation that needs a deploy is a rotation that gets skipped.

### 2.3 ArgoCD

```bash
kubectl apply -f infra/k8s/platform/argocd/project.yaml
kubectl apply -f infra/k8s/platform/argocd/app-of-apps/staging.yaml
```

That is the last `kubectl apply` anybody runs against this cluster.

### 2.4 GitHub

| | | |
|---|---|---|
| environment | `dev`, `staging` | no protection |
| environment | `production` | **required reviewers** — this is the deploy gate |
| variable | `IMAGE_NAMESPACE` | `ghcr.io/<owner>` unless a `mageride` org exists |
| variable | `ARGOCD_SERVER` | optional; enables the authoritative post-deploy check |
| secret | `ARGOCD_AUTH_TOKEN` | with it |

---

## 3. The first deploy into a fresh cluster

Expect this order, and expect the middle step to look wrong for a few minutes.

1. `mageride-secrets` syncs. Every ExternalSecret goes `SecretSynced`. If one does not,
   `kubectl -n mageride describe externalsecret <name>` names the Vault property it could not read.
2. `mageride-<env>` starts at wave −2 and works up. **At wave 0 the data plane comes up and EMQX
   will not start**: its 8883 listener needs the device CA chain, which does not exist yet. That is
   §5 and it is expected.
3. Wave 1: the `migrate` Job applies all 138 scripts to an empty database. Two to three minutes.
4. Wave 2: 34 workloads. `fleet-svc` will CrashLoopBackOff — see §6.
5. Wave 3: the Ingresses. cert-manager issues; DNS has to already point at the load balancer.

---

## 4. Every environment's first promotion

Base carries `sha-0000000`, which is unpullable on purpose — an overlay that was never promoted must
fail to pull rather than quietly run whatever `latest` happens to be. Until the first promotion every
pod is `ImagePullBackOff`, and that is correct.

```bash
gh workflow run cd.yml -f sha=$(git rev-parse main)
```

---

## 5. The two bootstrap copies (T-02)

provisioning-svc generates its own CA material on first boot, onto its own ReadWriteOnce volume.
Two other workloads need parts of it and cannot share that volume — EMQX reads the chain when its
listener starts, and tcp-adapter is a different pod on possibly a different node. So two values are
copied out once, into Vault, and ESO projects them.

```bash
POD=$(kubectl -n mageride get pod -l app=provisioning-svc -o name | head -1)

# (a) the issuing chain — a PUBLIC certificate; Vault is distribution here, not secrecy
kubectl -n mageride exec "$POD" -- cat /var/step/certs/ca_chain.crt > /tmp/ca_chain.crt
vault kv patch mageride-staging/provisioning-svc ca_chain_crt=@/tmp/ca_chain.crt

# (b) the PSK signing key — SECRET; a holder can mint a tracker credential as well as verify one
kubectl -n mageride exec "$POD" -- cat /var/step/secrets/psk_signing_key > /tmp/psk
vault kv patch mageride-staging/provisioning-svc psk_signing_key=@/tmp/psk
shred -u /tmp/psk

# Pick them up without waiting for the refresh interval
for s in emqx-device-ca tcp-adapter-psk; do
  kubectl -n mageride annotate externalsecret $s force-sync=$(date +%s) --overwrite
done
kubectl -n mageride rollout restart statefulset/emqx statefulset/tcp-adapter
```

Between provisioning-svc's first boot and (b), **tcp-adapter runs with `CanVerify == false`** and
serves the tracker protocols that carry no credential at all. `PskCredentials` was written to allow
that; it is a documented degraded state, not a crash. GT06 and NMEA devices ingest; JT/T 808 devices
that present a credential do not.

Both values are on T-02's 90-day rotation — [secret-rotation.md](secret-rotation.md) §5.

---

## 6. Known first-deploy failures

**`fleet-svc` CrashLoopBackOff with `InvalidOperationException: BindAsync method found on
ITrackerBindingService with incorrect format`.** Recorded by C118 as drift finding (1) and reproduced
by these manifests on purpose: `service-endpoints` sets `Fleet__ProvisioningBaseUrl`, which makes
fleet-svc map `POST /v1/fleets/{fleetId}/trackers/bind`, and that handler takes
`ITrackerBindingService` without `[FromServices]` — minimal APIs read it as a custom parameter binder
and refuse. **Any deployment that configures the provisioning upstream, which production must, has a
fleet-svc that does not boot.** The fix is one attribute in `Fleet.Api`. Unsetting the URL hides the
defect and silently drops ten contract operations, which is why it is not unset here.

**Everything reports `Otel__Endpoint` empty.** There is no OTLP collector in these manifests; C119
built the observability stack for the compose project. For tcp-adapter that is the only telemetry
path it has, because that process has no `/metrics` to scrape. Raised in the C124 handoff.

**`osm-pipeline` is a suspended CronJob.** No component builds that image (D7' §10 names it, nothing
produces it). Suspended so it cannot become a weekly page.

---

## What not to do

- **Never `kubectl apply` a manifest into a cluster.** ArgoCD's `selfHeal` reverts it within three
  minutes and the repository stops describing what is running, which breaks the rollback path.
- **Never `kubectl create secret`.** Put the value in Vault; ESO delivers it. A hand-created Secret
  is invisible, survives no cluster rebuild, and diverges the environments.
- **Never point staging's `ClusterSecretStore` at production's mount** "to test against the real
  thing". That mount holds the OnePay merchant key and the RS256 signing key for every token in the
  platform.
- **Never promote a SHA that staging is not running.** `promote.yml` refuses it; the override exists
  for an incident hotfix and says so in the commit message.
