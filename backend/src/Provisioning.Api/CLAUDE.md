# provisioning-svc (C030)

Stack: .NET 10 Minimal API + Dapper over Npgsql + StackExchange.Redis + Confluent.Kafka.
References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/Provisioning.Api.Tests -c Release`

## What this is

The hardware-tracker credential plane (T-02, T-03, T-08, T-09, T-12). It mints, binds, rotates
and revokes per-device credentials, owns the IMEI ↔ vehicle binding every inbound frame is
resolved through, and holds both records when two devices claim one IMEI. Everything here
matches `backend/contracts/provisioning.yaml`, which wins over this file and over the code.

| Endpoint | Spec |
|---|---|
| `POST /v1/trackers/bind` | D3' provisioning-svc, T-02, US-3.1 |
| `POST /v1/trackers/unbind` | **not in D3'** — D6' §4.3 (C030 micro-change-set) |
| `GET /v1/trackers/{imei}` | D3' route table, US-3.12 |
| `POST /v1/trackers/{imei}/switch-source` | D3' route table, US-3.6 |
| `DELETE /v1/trackers/{imei}` | D3' route table, US-3.8, T-12 |
| `POST /v1/fleets/{fleetId}/trackers/bulk` | D3' provisioning-svc, T-09, US-3.2 |
| `GET /v1/fleets/{fleetId}/trackers/bulk/{jobId}` | D3' route table |
| `GET /v1/fleets/{fleetId}/trackers/bulk/{jobId}/errors.csv` | **not in D3'** — the `errorReportUrl` it promises |
| `POST /v1/internal/trackers/{imei}/rotate` | D3' route table, US-3.5 |
| `GET /v1/internal/trackers/{imei}/validate` | D3' route table, T-01, T-03 |
| `POST /v1/internal/trackers/{imei}/quarantine` | **not in D3'** — T-08's adapter half |
| `GET /v1/internal/trackers/crl.pem` | **not in D3'** — T-12's MQTT half |

**Not here, on purpose.** Protocol decoding is **tcp-adapter (C043)** — this service only mints,
binds and revokes; the adapter calls `validate` on each connect and subscribes to the Redis
channel this service publishes on. Live health (`lastSeen`, `signal`, `battery`, `sats`) is
**fleet-health-svc (C044)**'s rollup: the columns are read here and written there, so a tracker
that is offline still answers `GET /v1/trackers/{imei}` with a stale `lastSeen`. The US-3.4
quarantine-resolution screen is **admin-bff (C062)**'s; this service feeds it
(`tracker.quarantined`, `state = QUARANTINED`) and would take its answer back through a route
C062 defines. `POST /v1/vehicles/{id}/device` stays on **registry-svc** — `/v1/vehicles/**` is
routed there at the gateway, so a second service answering it is not reachable; it becomes a thin
call into `POST /v1/trackers/bind` when C042 lands a mesh.

## Rules that are load-bearing

- **The certificate's CN is the authorisation boundary, not just an identifier.** A leaf is
  `CN={vehicleId}`; `infra/deploy/emqx/emqx.conf` gives the 8883 listener
  `peer_cert_as_username = cn`, and `acl.conf` writes every device rule as `veh/${username}/*`.
  So the credential *is* the topic grant, and a bound tracker is confined by exactly the rules a
  mobile client is confined by — **C030 added no ACL rule**. Nothing but `EmbeddedStepCa` may set
  a subject.
- **Rotation is not revocation, and conflating them bricks devices.** The replacement is minted
  14 days before expiry (`Cred:RotationLeadTime`) and the outgoing credential stays valid until
  its own `expires_at`. A tracker parked out of GSM coverage for a fortnight has to be able to
  come back and collect the new one; a sweep that revoked as it rotated would take exactly that
  population off the air. `prov.device_certs` therefore holds several live rows per binding, and
  `validate` accepts any of them.
- **Anti-clone is decidable at `bind`; at the adapter it is not.** Two claims on one live IMEI
  arrive at `bind` with two identities, so both are held there (T-08). At the adapter a clone
  presents a **copy** of the genuine credential — same serial — and what tells the two apart is
  two live sockets holding one identity, which is the adapter's state. So the adapter reports
  through `POST /v1/internal/trackers/{imei}/quarantine` and this service adjudicates.
  **`validate` deliberately does not treat two serials as a clone**: a rotation leaves two valid
  serials on one binding by design, and that rule would quarantine every device the cron renews.
- **The 24 h in T-08 is a window, and outside it the old binding is superseded rather than held.**
  An operator moving a tracker to another vehicle a week later has cloned nothing, and
  quarantining both would make them wait for an admin to undo a legitimate re-provision. Inside
  the window it fails closed — an IMEI is globally unique by construction, so a second claim on a
  live one is either a clone or a mis-keyed provisioning and both need a human.
- **The 409 is reported *after* the quarantine commits.** Both records have to be held before the
  caller is told the bind failed; a 409 that rolled the quarantine back would leave the incumbent
  publishing and the operator with nothing to escalate.
- **The challenger is materialised as a QUARANTINED binding with a real, held credential.** D6'
  §4.3 holds *both* and US-3.4's queue has to show an operator two rows to choose between.
  `credential_serial` is `NOT NULL`, so a binding pointing at a serial no certificate row carries
  would be a dangling reference in an audit trail — it is minted and revoked `certificate_hold`
  in the same transaction. `certificate_hold` is the one RFC 5280 reason a CA may lift, which is
  exactly what an admin resolution does.
- **Present in `imei:{imei}` means ACTIVE; there is no cached "revoked".** A reader that missed
  the cache and a reader that found a revoked entry must reach the same conclusion, and one
  representation of "not usable" — absence — is the only way to guarantee it.
- **Every Redis operation is best effort.** `prov.tracker_bindings` is the source of truth, so an
  outage costs latency on `validate` and the fast half of the revocation signal, never
  correctness, and it must never turn a bind into a 500. The durable half is the outbox row.
- **T-12 is two mechanisms because there are two transports.** TCP: the adapter re-validates
  through this service and force-closes on the `prov:tracker` pub/sub message. MQTT: a broker
  cannot be told to drop a session, so the serial goes on the CRL EMQX fetches from the
  distribution point in the certificate. Neither is derivable from the other, which is why
  `tracker.unbound` and `tracker.revoked` are both emitted.
- **A revoked binding revokes every credential on it at once**, not just the current one — the
  overlap that makes rotation safe would otherwise leave the outgoing certificate working.
- **A bind race is re-run, not reported.** `ux_tracker_imei_active` rejecting the insert means
  another request bound the IMEI in between, which is *precisely* the T-08 signal; the operation
  re-runs so the anti-clone rule sees the committed binding and fires deliberately.
- **Bulk validates atomically and executes per row.** The job and every parsed row commit
  together — a CSV never half-arrives — and the bindings behind them are minted one at a time
  afterwards, so a row that cannot be bound fails on its own and lands in the report.
- **A bulk row whose IMEI is already bound to the very vehicle it names is failed at validation.**
  Re-uploading last week's CSV is the most likely thing an operator will ever do here, and putting
  those rows through the bind path would hand every one to the anti-clone rule and quarantine a
  working fleet. A row naming a *different* vehicle for a live IMEI is left to the minter on
  purpose — that is a genuine second claim.
- **The bulk minter binds as the operator who submitted the job, not as an admin.** Re-checking
  fleet membership per row costs two indexed queries and means an operator whose access is revoked
  halfway through 5,000 rows stops binding where it happened.
- **No event payload carries credential material.** A rotation names the outgoing and incoming
  serials and stops there. The secret half goes to the caller that minted it, once, over TLS;
  putting it on a topic with a week's retention would undo that.
- **A service method must not be called `BindAsync`** — Minimal APIs treat any parameter type
  carrying one as custom-bound, so the route table fails to build at start-up. Hence
  `ITrackerService.BindTrackerAsync` (the same trap C028 hit with `IMerchantService`).
- **Ownership comes from the token's `sub`, never from the body**, and there are exactly two ways
  in: owning the vehicle, or running the fleet whose roster carries it (AL-03). `viewer` is
  excluded — bulk-binding 5,000 trackers is the largest write the Fleet Portal can make.
- **A malformed IMEI is 400 in a body and 404 in a path.** In a body the caller is being helped;
  on a path, "not well-formed" and "no such tracker" are the same answer and telling them apart
  confirms which of two IMEIs is real to somebody enumerating them.
- **The Luhn check digit is not enforced.** It would catch most typos, and the contract's
  `^\d{15}$` is the contract — D6' §4.1's grey-import GT06/JT808 units report IMEIs that fail
  Luhn, and refusing one leaves a working tracker unprovisionable with no override.

## The device PKI

`EmbeddedStepCa` keeps a two-tier ECDSA P-256 CA in **step-ca's own on-disk layout** under
`StepCa:RootKeyPath` — `certs/root_ca.crt`, `secrets/root_ca_key`, `certs/intermediate_ca.crt`,
`secrets/intermediate_ca_key`, plus `certs/ca_chain.crt` (EMQX's `cacertfile`) and
`secrets/psk_signing_key`. Matching the layout means swapping in a real step-ca is a
configuration change rather than a migration of key material. **`StepCa:Url` is refused at
start-up** rather than ignored: a deployment that set it believes its root key is in step-ca's
store and not on a Docker volume.

**The CA is generated outside this service.** `infra/scripts/dev-up.sh` writes it before the
stack comes up and both the `emqx` container and `app-services` mount the directory; this service
*loads* it. That ordering is forced: EMQX reads its `cacertfile` when the 8883 listener starts,
and a broker whose CA file is missing does not degrade — it refuses to boot. The same is true in
the test suite, where `EmqxFixture` generates it (`MageRide.TestKit.DeviceCa`).

**The root private key is on disk, unencrypted.** That is what an embedded CA with no operator at
start-up amounts to; mode 0600 under `secrets/` and a dedicated volume are the whole protection,
and D7' §13's answer (Vault) is C125's. Anyone holding that file can mint a credential for any
vehicle.

PSK credentials are `mrp1.{serial}.{expiry}.{secret}.{signature}`, the signature an HMAC over the
serial, the **IMEI** and the expiry. That makes them "signed PSK" (D6' §4.2) rather than merely
random: an adapter holding `secrets/psk_signing_key` rejects a forged, expired or replayed-to-
another-device token without a network call, and spends the round trip to `validate` on the one
question it cannot answer locally — whether the credential has since been revoked. Only a SHA-256
of the token is stored (`pem_or_token_hash`, "never the credential itself").

## Configuration

`Provisioning:InternalApiKey` **unset means `/v1/internal/trackers/**` is not mapped at all**.
The tcp-adapter then refuses every device, which is the safe direction to fail in, and completely
silent from the adapter's side — so it is said loudly at start-up. It must equal what C043 sends.
D3' §0 puts the internal family on mTLS and the gateway refuses the prefix at the edge (C008);
the shared secret is the interim until C042 lands a mesh.

`Provisioning:AntiCloneWindow` (default **24 h**) is D6' §4.3's window.
`Provisioning:ImeiCacheTtl` (default 24 h) is the cache backstop, not the mechanism — a revoke
deletes the key immediately, and the TTL bounds the damage from a revoke whose Redis write failed.

`Provisioning:RotationEnabled` / `RotationInterval` (1 h) / `RotationBatchSize` (200) size the
T-02 sweep; `Provisioning:BulkMintEnabled` / `BulkMintInterval` (2 s) / `BulkMintBatchSize` (50)
size the T-09 worker. Both are off in tests, which drive one pass directly.

`Provisioning:ErrorReportSigningKey` unset means a per-process key: correct for one instance,
wrong for several, because a link minted by replica A will not verify on replica B. The service
says so at start-up.

`Cred:RotationDays` (90) and `Cred:RotationLeadTime` (14 d) are the credential's life and its
overlap. `StepCa:CrlDistributionPoint` is **empty in dev on purpose** — EMQX refuses a certificate
whose CRL it cannot fetch and the broker starts before this service, so the CDP and
`enable_crl_check` are turned on together or not at all.

`Outbox:*` defaults to `prov` / `prov_outbox` / `provisioning.events`; `CommandLog:Schema`
defaults to `prov` with no aggregate-id column.

## Schema this service added

`db/migrations/0402`–`0405`; each file's header says why, and all are recorded as
micro-change-sets in the C030 handoff in `build/progress.md`.

| Migration | What | Why |
|---|---|---|
| 0402 | `prov.command_log` | D3' §0 mandates a per-service idempotency log; D4' prints one for rides only |
| 0403 | `prov.outbox` | `tracker.bound`/`tracker.unbound` had a producer and a consumer and no topic or table |
| 0404 | binding state audit, `prov.imei_sightings`, CRL reason, fleet FK | T-08 is a *time window* and nothing recorded when a state changed; `fleet_id` pointed at a legacy stub |
| 0405 | `prov.bulk_jobs`, `prov.bulk_job_rows` | D3' specifies the endpoint completely and D4' has no table for any of it |

`EventTopics.ProvisioningEvents` (`provisioning.events`, key vehicleId) is **not** one of D6'
§2.1's six topics; it is added to `infra/deploy/redpanda/bootstrap-topics.sh` and `slim-verify.sh`
alongside `registry.events`.

## Events on `provisioning.events`

`tracker.bound` · `tracker.unbound` · `tracker.revoked` · `tracker.quarantined` ·
`tracker.credential_rotated` · `tracker.source_switched`. Only `tracker.bound` and
`tracker.unbound` are named by a spec (D3' bind side effects; D6' §4.3's cache-invalidation pair)
and **none of the six has an envelope anywhere in D6' §2.2**, so the shapes in
`Trackers/TrackerEvents.cs` are provisioning-svc's and are raised as micro-change-sets in the C030
handoff.

The aggregate id is always the **vehicle**, matching the topic's partition key. Keying by binding
id would order events per binding, and the ordering that matters is per vehicle: a tracker moved
from vehicle A to vehicle B produces an unbind and a bind that a consumer must apply in that order
or it rebuilds the cache entry it has just dropped.
