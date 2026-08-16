# Sealed Secrets — the K3s / MVP path (D7' §13)

D7' §13 gives two mechanisms and says which goes where:

> **At rest:** HashiCorp Vault … K8s **External Secrets Operator** syncs Vault → K8s `Secret`;
> **sealed-secrets at K3s/MVP**.

So:

| Environment | Mechanism | Where |
|---|---|---|
| `dev` (K3s single node) | **sealed-secrets** | this directory |
| `staging` (DOKS) | Vault + External Secrets Operator | `../external-secrets/overlays/staging` |
| `production` (DOKS Singapore) | Vault + External Secrets Operator | `../external-secrets/overlays/production` |

The reason for the split is operational, not ideological. ESO needs a reachable Vault, a
Kubernetes auth mount bound to the cluster, and a policy per environment; a single-node K3s
cluster that exists to prove the manifests should not need a Vault server to come up.
Sealed-secrets needs one controller and a keypair the cluster generates itself.

## The invariant

**A `SealedSecret` is safe to commit. A `Secret` is not.** A SealedSecret is asymmetric
ciphertext that only the controller's private key in the target cluster can open — not even
the person who sealed it can read it back. That is what makes this GitOps-compatible at all,
and it is why the intermediate plaintext never touches the working tree: `seal.sh` pipes it.

`infra/scripts/k8s-verify.sh` fails the build if a `kind: Secret` with a `data:` or
`stringData:` block appears anywhere under `infra/k8s/`, whatever the file is called.

## Install the controller (once per cluster)

```bash
kubectl apply -f https://github.com/bitnami-labs/sealed-secrets/releases/download/v0.27.3/controller.yaml
kubeseal --fetch-cert > /tmp/mageride-dev-sealing.pem     # the PUBLIC half; not a secret
```

Keep a backup of the controller's **private** key somewhere a person can reach without the
cluster — losing it means every SealedSecret in git is unopenable and every credential has to
be re-sealed:

```bash
kubectl -n kube-system get secret -l sealedsecrets.bitnami.com/sealed-secrets-key -o yaml \
  > sealing-key-backup.yaml         # then straight into a password manager, never into git
```

## Seal the platform's secrets

`seal.sh` reads the same credential list the ESO manifests use — it walks
`infra/k8s/service-catalog.yaml`, so the set cannot drift from what the Deployments
`envFrom` — and writes one SealedSecret per Kubernetes Secret into `dev/`.

```bash
# From a Vault you can already read (the same values staging would get):
VAULT_ADDR=https://vault.mageride.lk VAULT_MOUNT=mageride-dev \
  bash infra/k8s/platform/sealed-secrets/seal.sh --from-vault --cert /tmp/mageride-dev-sealing.pem

# Or, with no Vault at all, generating fresh random values for everything that is only ever
# read by MageRide itself (internal keys, HMAC peppers, signing keys) and prompting for the
# ones an external party issues (OnePay, FCM, Gemini …):
bash infra/k8s/platform/sealed-secrets/seal.sh --generate --cert /tmp/mageride-dev-sealing.pem
```

Then commit `dev/*.yaml` and let ArgoCD sync it. Rotating one credential is one re-seal of one
file and one commit, which is the whole appeal of this mechanism at MVP scale.

## What `--generate` will and will not invent

It generates the credentials whose only requirement is that both sides agree — every
`internal-keys/*` value, the HMAC peppers, the file-link signing keys, the Erlang cookie, the
Postgres password, the MQTT session secret. Those are exactly the ones the `aliases` table in
the catalog says must be shared, and generating them centrally is how they stay shared.

It cannot invent, and will prompt for (or leave empty, with a warning per key):

- **`providers/*`** — a real third party issued them: OnePay, LankaQR/ComBank IPG, Fit SMS,
  FCM, APNs, Gemini, LiveKit, Play Integrity. An empty value here means the corresponding
  feature is unavailable in the dev cluster, which is the honest state and, for a dev cluster,
  usually the right one. Every service that takes one is written to degrade rather than fail
  (payout-svc rests instructions at PENDING with no bank rail, voip-svc answers "VoIP
  unavailable", ocr-svc extracts on-prem and sends nothing to Gemini).
- **`provisioning-svc/psk_signing_key`** and **`provisioning-svc/ca_chain_crt`** — the platform
  generates these itself on provisioning-svc's first boot. They are copied INTO the secret
  store afterwards; `docs/runbooks/deploy.md` §5 is that step, and it is the same in every
  environment.
