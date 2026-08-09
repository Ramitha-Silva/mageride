# Runbook — rotate a secret (D7' §13, C124)

D7' §13's rotation schedule, as procedures:

| What | Every | Section | Blast radius if done wrong |
|---|---|---|---|
| JWT signing key (RS256) | 90 d | [§1](#1-the-jwt-signing-key-90-days-jwks-overlap) | **every logged-in user, instantly** |
| DB credentials | 90 d | [§2](#2-the-database-password-90-days) | every service |
| OnePay / IPG webhook secrets | 180 d | [§3](#3-a-payment-provider-webhook-secret-180-days) | payments stop settling, silently |
| MQTT session secret | 90 d | [§4](#4-the-mqtt-session-secret-90-days) | every vehicle's position stream |
| MQTT device certs / PSK (T-02) | 90 d | [§5](#5-the-device-ca-and-the-psk-signing-key-t-02-90-days) | the hardware tracker fleet |
| step-ca root | quarterly, offline | [§5](#5-the-device-ca-and-the-psk-signing-key-t-02-90-days) | the hardware tracker fleet |
| An internal API key | on demand | [§6](#6-an-internal-api-key) | one direction of one call |

---

## First action

**Whatever you are rotating, write the new value beside the old one before you retire anything.**
Every procedure below is add-then-restart-then-remove. The failure mode of a rotation is not "the new
value did not work" — it is a window where the two halves of the platform disagree about which value
is current, and nothing in a health check notices.

The mechanics are always the same three commands:

```bash
vault kv patch mageride-production/<path> <property>=<new value>

# ESO refreshes hourly; do not wait
kubectl -n mageride annotate externalsecret <name> force-sync=$(date +%s) --overwrite
kubectl -n mageride get secret <name> -o jsonpath='{.metadata.resourceVersion}'   # it changed

# a pod reads its env at start-up, so the value only lands on a restart
kubectl -n mageride rollout restart deploy/<service>
```

`infra/k8s/service-catalog.yaml`'s `aliases` table says which env var names resolve to the property
you are changing. **Read it before you rotate anything shared** — `internal-keys/wallet` is one value
under six names in six services, and restarting one of them is a rotation that half-happened.

---

## 1. The JWT signing key (90 days, JWKS overlap)

iam-svc signs every access token with RS256; every other service validates against
`http://iam-svc/.well-known/jwks.json`. **A straight swap invalidates every token in flight** — up to
30 minutes of them — so the retiring key stays published until they have all expired. That is what
`Jwt__RetiredSigningKeyPems__0` is for, and it is seeded empty at bootstrap so this procedure never
has to touch a manifest.

```bash
M=mageride-production

# 1. Generate. RSA 2048 minimum; 4096 if the extra ~1 ms per validation is acceptable.
openssl genrsa -out /tmp/jwt-new.pem 2048

# 2. Move the CURRENT key into the retired slot, and the new one into the active slot. One patch,
#    so there is no instant where neither is published.
CURRENT=$(vault kv get -mount=$M -field=Jwt__SigningKeyPem iam-svc)
vault kv patch $M/iam-svc \
  Jwt__SigningKeyPem=@/tmp/jwt-new.pem \
  Jwt__RetiredSigningKeyPems__0="$CURRENT"
shred -u /tmp/jwt-new.pem

# 3. Sync and restart iam-svc ONLY. It is the only holder; everything else reads the JWKS.
kubectl -n mageride annotate externalsecret iam-svc-secret force-sync=$(date +%s) --overwrite
kubectl -n mageride rollout restart deploy/iam-svc
kubectl -n mageride rollout status deploy/iam-svc --timeout=5m

# 4. Both keys are published
curl -sf https://api.mageride.lk/v1/internal/iam/.well-known/jwks.json | python3 -m json.tool | grep -c '"kid"'
#   -> 2

# 5. WAIT. The access-token TTL is 30 minutes (D-29). Sooner than that and you sign somebody out
#    mid-ride. Set a reminder; do not sit on it.
sleep 2100

# 6. Retire
vault kv patch $M/iam-svc Jwt__RetiredSigningKeyPems__0=""
kubectl -n mageride annotate externalsecret iam-svc-secret force-sync=$(date +%s) --overwrite
kubectl -n mageride rollout restart deploy/iam-svc
```

**Do not restart any other service during step 3-5.** They cache the JWKS for 15 minutes and refetch
on an unknown `kid`, so they pick the new key up on their own. Restarting them all is a
platform-wide rollout for no reason, in the one window where token validation is in flux.

Refresh tokens are separate: `Jwt__RefreshTokenKey` is an HMAC with no overlap mechanism, so rotating
it **signs everybody out**. Do it only deliberately, and announce it.

---

## 2. The database password (90 days)

One Vault property, three consumers: the Postgres container's own `POSTGRES_PASSWORD`, PgBouncer's
`DB_PASSWORD`, and the two DSNs `common-secret` renders for every service. They cannot be rotated
independently — that is why they read one property.

**The order matters and Postgres has to be told first**, because the container env only sets the
password on FIRST boot; on an existing volume it is ignored.

```bash
M=mageride-production
NEW=$(head -c 24 /dev/urandom | base64 | tr -d '=+/')

# 1. Change it IN the database
kubectl -n mageride exec statefulset/postgres -- \
  psql -U postgres -c "ALTER USER postgres PASSWORD '$NEW';"

# 2. Then in Vault
vault kv patch $M/common postgres_superuser_password="$NEW"

# 3. Sync both Secrets
for s in common-secret postgres-superuser; do
  kubectl -n mageride annotate externalsecret $s force-sync=$(date +%s) --overwrite
done

# 4. PgBouncer first — every service goes through it, and its pooled server connections are still
#    authenticated with the old password until it restarts.
kubectl -n mageride rollout restart deploy/pgbouncer
kubectl -n mageride rollout status deploy/pgbouncer --timeout=5m

# 5. Then everything else. maxUnavailable 0 means this is a rolling restart with no downtime, but it
#    is 34 workloads — expect ten minutes.
kubectl -n mageride rollout restart deployment,statefulset -l app.kubernetes.io/part-of=mageride
```

Between (1) and (4) a service that opens a NEW pooled connection is refused. Existing connections
survive, so the symptom is intermittent — do this in a quiet window, and watch
`pgbouncer_pools_server_login_retries` if the exporter is up.

D7' §13 says "DB creds 90 d (Vault dynamic)". Vault's dynamic database credentials would remove this
procedure entirely (a lease per pod, rotated automatically). It needs the database secrets engine
configured and a per-service role, and the superuser dependency in §2.2 of
[deploy.md](deploy.md) has to go first — migration 1804 needs CREATEROLE. **C132.**

---

## 3. A payment provider webhook secret (180 days)

`Onepay__WebhookSecret` and `ComBankIpg__WebhookSecret` are shared by fare-svc, wallet-svc and
fleet-billing-svc (`providers/onepay_webhook_secret`, `providers/combank_ipg_webhook_secret`).

**The provider changes it, not you.** The order is therefore the reverse of everything else here, and
the failure mode is the quiet one: a webhook whose signature no longer verifies is REJECTED, and the
payment it was reporting stays in `PaymentPending` until somebody notices the alert
(`PaymentCallbackLatency`, C119) or a driver complains.

```bash
# 1. In the provider's dashboard: generate the new secret. Do NOT retire the old one yet — most
#    providers sign with one secret only, so there is no overlap to arrange and this is a cutover.
# 2. Vault, then sync, then restart the three services TOGETHER.
vault kv patch mageride-production/providers onepay_webhook_secret='<new>'
for s in fare-svc-secret wallet-svc-secret fleet-billing-svc-secret; do
  kubectl -n mageride annotate externalsecret $s force-sync=$(date +%s) --overwrite
done
kubectl -n mageride rollout restart deploy/fare-svc deploy/wallet-svc deploy/fleet-billing-svc
# 3. In the provider's dashboard: activate the new secret. Between (2) and (3) inbound callbacks
#    fail signature verification — providers retry, so keep the window under their retry budget
#    (OnePay: 24 h, so minutes are safe).
# 4. Force a test callback and confirm it settles.
```

`Onepay__ApiKey` (outbound) has no such window: rotate Vault, restart, done — nothing verifies it but
OnePay.

---

## 4. The MQTT session secret (90 days)

**Read this before rotating it.** `Mqtt__SessionTokenSecret` is a shared HMAC held by EMQX and by
five services: iam-svc mints the mobile session token, and mqtt-bridge-svc, tcp-adapter, fanout-svc
and fleet-health-svc each mint their own service token. The broker validates all of them with the
same secret, so **there is no overlap mechanism and a partial rotation is a total MQTT outage** — no
device can publish and no bridge can subscribe.

D-21 specifies RS256 over a JWKS document for exactly this reason, and nothing implements it:
`MageRide.Shared.Mqtt.MqttSessionTokens` signs with `HmacSha256` and no service serves an MQTT JWKS.
Closing that is a code change (raised in the C124 handoff); until then this is the procedure.

```bash
M=mageride-production
NEW=$(head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n')
vault kv patch $M/common mqtt_session_token_secret="$NEW"

for s in common-secret emqx-auth; do
  kubectl -n mageride annotate externalsecret $s force-sync=$(date +%s) --overwrite
done

# The BROKER first. A token minted with the old secret is rejected the moment EMQX restarts, so the
# outage starts here — make it as short as possible by having the next command ready.
kubectl -n mageride rollout restart statefulset/emqx
kubectl -n mageride rollout status statefulset/emqx --timeout=5m

# Then every minter, immediately.
kubectl -n mageride rollout restart \
  deploy/iam-svc deploy/mqtt-bridge-svc deploy/fanout-svc deploy/fleet-health-svc \
  statefulset/tcp-adapter
```

Devices reconnect on their own (E-02 gives the session token a 4-hour minimum TTL and the clients
refresh), but **every vehicle reconnects at once** — R-09's reconnect storm. EMQX's
`max_conn_rate = 500/s` absorbs it; watch `EmqxDroppedMessages` and `EmqxAuthFailures` (C119) for
fifteen minutes afterwards. Do this at 03:00 Colombo.

---

## 5. The device CA and the PSK signing key (T-02, 90 days)

Two values, both generated by provisioning-svc onto its own volume, both copied into Vault (see
[deploy.md](deploy.md) §5), and both distributed from there.

### The PSK signing key

`provisioning-svc` rotates it on its own volume (`CredentialRotationWorker`, `Cred__RotationDays=90`).
**The copy into Vault has to follow, or tcp-adapter verifies against the retired key and every
credential minted after the rotation is rejected.**

```bash
POD=$(kubectl -n mageride get pod -l app=provisioning-svc -o name | head -1)
kubectl -n mageride exec "$POD" -- cat /var/step/secrets/psk_signing_key > /tmp/psk
vault kv patch mageride-production/provisioning-svc psk_signing_key=@/tmp/psk
shred -u /tmp/psk
kubectl -n mageride annotate externalsecret tcp-adapter-psk force-sync=$(date +%s) --overwrite
kubectl -n mageride rollout restart statefulset/tcp-adapter
```

### The CA chain (step-ca root, quarterly, offline)

**Write the chain containing BOTH intermediates before retiring either.** EMQX's 8883 listener uses
the chain as its `cacertfile`, and a device holding a certificate from the old intermediate fails its
handshake the moment that intermediate leaves the file. There are up to 100k devices at the §11
ceiling and no remote channel to their trust stores.

```bash
POD=$(kubectl -n mageride get pod -l app=provisioning-svc -o name | head -1)
kubectl -n mageride exec "$POD" -- cat /var/step/certs/ca_chain.crt > /tmp/chain.crt
# confirm the OLD intermediate is still in it before you go further
openssl storeutl -noout -text -certs /tmp/chain.crt | grep -c 'Subject:'
vault kv patch mageride-production/provisioning-svc ca_chain_crt=@/tmp/chain.crt
kubectl -n mageride annotate externalsecret emqx-device-ca force-sync=$(date +%s) --overwrite
kubectl -n mageride rollout restart statefulset/emqx
```

The root key itself is offline and is not in Vault. That is the point of an offline root.

---

## 6. An internal API key

The cheapest rotation in the platform, and the one to reach for if a key may have leaked. Every
internal key lives at `internal-keys/<owning service>` and every caller reads that one property —
`aliases` in `infra/k8s/service-catalog.yaml` says who.

```bash
# `internal-keys/wallet`, for example: wallet-svc validates it; fare-svc, subscription-svc,
# fleet-billing-svc, payout-svc and admin-bff all present it.
vault kv patch mageride-production/internal-keys wallet=$(head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n')

for s in wallet-svc fare-svc subscription-svc fleet-billing-svc payout-svc admin-bff; do
  kubectl -n mageride annotate externalsecret ${s}-secret force-sync=$(date +%s) --overwrite
done
kubectl -n mageride rollout restart deploy/wallet-svc deploy/fare-svc deploy/subscription-svc \
  deploy/fleet-billing-svc deploy/payout-svc deploy/admin-bff
```

**Restart the CALLERS and the VALIDATOR in one command.** In between, a caller with the old key gets
a 401 from the service with the new one. The window is one rolling restart; the alternative — a
second accepted key — does not exist in these services and adding one is a code change.

---

## Verifying a rotation

```bash
# ESO is happy
kubectl -n mageride get externalsecrets
#   every row: SecretSynced / True

# The Secret actually changed
kubectl -n mageride get secret <name> -o jsonpath='{.metadata.annotations}' | python3 -m json.tool

# Every pod restarted after the Secret changed — the step people skip
kubectl -n mageride get pods -o custom-columns=NAME:.metadata.name,START:.status.startTime
```

An `externalsecret` in `SecretSyncedError` names the property it could not read:

```bash
kubectl -n mageride describe externalsecret <name> | tail -20
```

Nine times out of ten it is a typo in the property name, or a `vault kv put` where a `vault kv patch`
was meant — **`put` replaces the whole secret**, so putting one property deletes the rest of them.

---

## What not to do

- **Never `kubectl create secret` or `kubectl edit secret`.** ESO overwrites it on the next refresh
  and `selfHeal` is on; the change lasts under an hour and leaves no record.
- **Never `vault kv put` when you mean `patch`.** It replaces the whole secret. Half the platform's
  ExternalSecrets then go `SecretSyncedError` at once.
- **Never rotate a shared credential and restart one consumer.** Read `aliases` first.
- **Never rotate the MQTT session secret during the day.** §4.
- **Never remove a retired JWT key before the access-token TTL has passed.** §1 step 5.
- **Never copy a production value into staging** to reproduce something. Staging's mount exists so
  that is unnecessary.
