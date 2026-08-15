# Go-live checklist — MageRide production, DOKS Singapore

C132 · prepared 2026-08-13, updated 2026-08-14 · **status: NOT READY.** Fifteen items open, four
of them HIGH security findings that C132's own fence gates go-live on. **Item 5 — the ingest
ceiling that made the platform not work — was closed on 2026-08-14**, and item 5a is what is left
of it: a capacity gap rather than a defect. Item 5 stays in the table, struck through, because a
blocker that disappears is a blocker nobody can check.

---

## How this document works

**Every item is MET, or WAIVED by a named owner with a date and a reason.** "In progress" is not a
state this checklist has — an item that is in progress is not met, and the launch either waits or
somebody puts their name against proceeding without it. That is C127's rule ("'Noted' is not a
resolution") applied to a launch.

The two fences from `build/prompts/C132.md`, verbatim:

> Production is DOKS Singapore (hosting decision 2026-07-05). The Contabo EU box stays a test
> replica.
>
> **Go-live is gated on the day-0 GTFS feed being active (C126) and on no open high/critical
> security findings.**

`bash infra/k8s/verify-readiness.sh` checks the mechanical half of this file and fails while a
blocker is open. It cannot check the human half — §D is signatures.

### A note on ownership that has to be read before the table

`security/remediation-backlog.md` assigns both open HIGH findings to **"C133"**, "due at go-live",
and says "C133's own fence already gates go-live on no open high findings". **C133 is
`payout-svc`, a wave-3 backend service that shipped weeks ago.** It has no fence about go-live,
no deployment surface, and nobody working on it. The component whose fence that sentence describes
is **C132 — this one.**

So the two most important remediations on the platform were assigned to a component that cannot
perform them, in a way that reads as ownership. They are re-owned below, to the roles that can act
(the deployment owner and the tracker-plane owner), and the backlog needs the same correction.
Recorded as **C132-08**.

---

## A. Blockers

### A1 — Security: no open HIGH or CRITICAL finding

| # | Finding | Sev | State | Owner | Gate |
|---|---|---|---|---|---|
| 1 | **C127-01** — services connect to Postgres as a superuser; `audit.events` is mutable by the credential that appends to it and all nine RLS policies are inert | HIGH | **OPEN** — mechanism landed (migration 2001), deployment cutover not done | deployment owner (was "C133", see above) | **before the first production write** |
| 2 | **C128-01** — a revoked tracker certificate still authenticates to EMQX; no deployed broker checks the CRL | HIGH | **OPEN** — blocked on a fleet-wide re-mint, in the documented 3-step order | tracker-plane owner (was "C133") | **before the first production tracker is provisioned** |
| 3 | **C131-01** — the SFU never tells any client the relay exists; `turn.enabled: false`, no `rtc.turn_servers`, and voip-svc's token carries no ICE servers. LiveKit falls back to Google's public STUN | HIGH | **OPEN** — `infra/sg/livekit.sg.yaml` declares it; the deployed `infra/deploy/livekit/livekit.yaml` and voip-svc are unchanged | C132 → voip-svc owner | before VoIP is enabled for real users |
| 4 | **C131-02** — 101 relay ports is 50 concurrent relayed calls against D-24's 500; demonstrated, the 51st is refused `508` | HIGH | **OPEN** — `infra/sg/turnserver.sg.conf` widens the range (a recorded deviation from D6' §6); not deployed | C132 → media-host owner | before VoIP is enabled for real users |

**Items 1 and 2 are the fence.** Neither can be waived by an engineer: they are a mutable audit
log and a tracker that keeps publishing after it has been revoked. Items 3 and 4 can be waived
**only by shipping without in-app voice** — the app's documented fallback is a direct cellular
dial (AL-48), so that is a real option and it is the recommendation if the media host is not
ready.

Every other C127/C128 finding is FIXED or risk-accepted with an owner and a date; see
`security/remediation-backlog.md`. Nothing there needs to move for launch.

### A2 — The platform carries the launch load

| # | Item | State | Owner | Gate |
|---|---|---|---|---|
| 5 | ~~The ingest chain carries ~10 msg/s against a 1,200 msg/s launch target~~ | **FIXED 2026-08-14.** The cause was `messages_rate = "5/s"` on EMQX's in-cluster 1883 listener — D-17's per-vehicle ceiling applied to the connection carrying the whole fleet's shared subscription. **240 msg/s now carried with zero drops** on the replica, up from ~10. `MqttBridgeThroughputTests` is the regression test | C132 | met |
| 5a | **1,200 msg/s sustained is not yet demonstrated.** `mqtt.max_inflight` is now 512 (2026-08-15) and the same run went from 7,189 dropped to **0**; the platform carries everything this box can offer at 490-550 msg/s with no loss. What remains is only the third lever: **k6 on the same 8 vCPU as the replica cannot offer 1,200 msg/s**, so it cannot be measured here | **OPEN** — blocked on a generator off the box, not on the platform | infrastructure owner | measure on staging before go-live; this is measurement capacity, not a defect |
| 6 | **End-to-end position latency at the launch rate** — the 33.6 s p95 was the backlog behind #5's cap and has not been re-measured since it was lifted | **OPEN** | C132 → infrastructure owner | re-measure with the subscriber half (`LOAD_WATCH=1`) before go-live |
| 7 | `delivery.dropped.queue_full` is not scraped. It is the ONLY symptom of an ingest ceiling, and #5 proved it can be non-zero for weeks with nothing else showing | **OPEN** — EMQX's Prometheus endpoint is not a target in the DOKS cluster | C132/C119 | with #12 |

### A3 — Operability

| # | Item | State | Owner | Gate |
|---|---|---|---|---|
| 8 | **A DOKS cluster exists** — staging and production, Singapore, from these manifests | **OPEN — neither cluster exists.** The launch topology is proven on a 3-node Kubernetes cluster on the build host (`readiness-report.md` §2), not on DOKS | infrastructure owner | before anything else in §B |
| 9 | **DR restore rehearsed against the real Wasabi repository**, production-sized, timed | **OPEN** — rehearsed against MinIO at 7.7 MB: 122 s, correct point-in-time cut. That number does not extrapolate (`dr-restore.md` §6) | C132 → infrastructure owner | before go-live |
| 10 | **Vault has every secret the manifests read**, including the three C132 added: `postgres_replication_password`, `postgres_rewind_password`, `wasabi_access_key`/`wasabi_secret_key` | **OPEN** | infrastructure owner | before the first sync |
| 11 | **The day-0 GTFS feed is loaded and active** (C126, AL-55) | **OPEN** — the pipeline is built, rehearsed and reversible; the national feed is an externally provided file that has not been obtained | operations owner | **fence** |
| 12 | **Monitoring exists in the production cluster.** Prometheus, Alertmanager, and the exporters the alerts read (`postgres_exporter` with C132's `queries.yaml`, `redis_exporter` against :26379, EMQX's endpoint, kube-state-metrics) | **OPEN — C132-05.** 66 alert rules and a production Alertmanager routing config exist and nothing loads them; C119 built the stack for the compose project | C132 → C119 owner | **hard blocker.** Every runbook in this package assumes a page arrives |
| 13 | **`Otel__Endpoint` is set.** It is `""` in every overlay. For `tcp-adapter` that is its ONLY telemetry path — it has no `/metrics` to scrape | **OPEN** | with #12 | before the tracker plane carries real devices |
| 14 | On-call rota staffed, PagerDuty services created, routing keys in Vault | **OPEN** | operations owner | before go-live |
| 15 | Status page live on infrastructure that shares nothing with DOKS/Wasabi/R2 (`oncall.md` §5) | **OPEN** | operations owner | before go-live |

### A4 — Met

| # | Item | Evidence |
|---|---|---|
| 16 | The launch topology is Patroni 1P+2R + PgBouncer and a 3-node Redis Sentinel group, and both fail over | `readiness-report.md` §2: leader killed, `Service/postgres` repointed in **6 s**; the old primary rejoined by `pg_rewind` on the new timeline. `SENTINEL FAILOVER` promoted a new Redis primary in **6 s** and the .NET client followed with no restart |
| 17 | WAL archiving to object storage, with a bounded RPO | `archive_timeout: 60` — worst case one minute of writes, against ADD §15's five. Proven end to end by `infra/scripts/dr-rehearsal.sh` |
| 18 | A point-in-time restore works and honours its target | 1,000 rows from before T restored, 0 rows from after T |
| 19 | The nightly `pg_dump` to Wasabi verifies its own upload | `pg-dump-wasabi.yaml`: `pg_restore --list` floor, then a HEAD to compare byte counts |
| 20 | ADD §10.2's six scale-out triggers are alerts, not prose | `alerts.capacity.yml`, 17 rules, `promtool check rules` clean |
| 21 | Every rate-limit bucket is per caller on DOKS | C132-02 fixed: `Gateway__ForwardedHeaders__KnownNetworks__*` + `use-forwarded-headers: false` |
| 22 | The production data plane can be admitted by its own namespace | C132-01 fixed: `postgres-0` was rejected by `restricted` PSA and no pod was ever created |
| 23 | Migrations are gated pre-deploy and backward-compatible | ArgoCD sync wave 1 + `infra/scripts/migration-gate.sh` (C124) |
| 24 | Rollback is one revert, and `kubectl` cannot be used to work around it | `docs/runbooks/rollback.md` (C124); ArgoCD `selfHeal` |

---

## B. The cutover

Nothing here starts until every §A blocker is met or waived.

| T | Step | Verify |
|---|---|---|
| **T-14d** | Create the staging DOKS cluster from these manifests. Bootstrap ArgoCD (two `kubectl apply`s), install ingress-nginx with `values.production.yaml`, cert-manager, ESO | `bash infra/scripts/k8s-verify.sh`; the app-of-apps goes Healthy |
| **T-14d** | Run the smoke suite against staging, end to end: a Mode C ride, a Mode A journey, a package delivery | `infra/replica/smoke.sh` pointed at the staging edge |
| **T-10d** | Kill the staging Postgres leader in business hours. Kill a Redis pod. Drain a staging node | a failover inside 30 s, and `PatroniClusterHasNoLeader` actually pages somebody |
| **T-7d** | Create the production cluster. **Do not promote any image yet** | `sha-0000000` is unpullable on purpose — an un-promoted overlay must fail to pull |
| **T-7d** | Vault: every secret, then let ESO sync | no `ExternalSecret` in `SecretSyncError` |
| **T-5d** | DNS for `api.`, `admin.`, `fleet.`, `passenger.` at low TTL (300 s), cert-manager issues | `letsencrypt-prod-dns` certificates Ready |
| **T-5d** | Take the first base backup and **rehearse the restore against the real repository** (blocker #9) | `pgbackrest --stanza=mageride info`; record the RTO |
| **T-3d** | Load the day-0 GTFS feed through SCR-AP-016 (blocker #11) | `bash infra/replica/gtfs-day0-verify.sh` shape, against production |
| **T-2d** | Promote the release SHA to production. ArgoCD syncs behind the environment approval | wave 1 migration Job green before wave 2 starts |
| **T-1d** | Final `bash infra/k8s/verify-readiness.sh`. On-call staffed. Status page up | exit 0 |
| **T-0** | Open the apps. Watch `queue_full`, position e2e latency, and the SOS SLO | |
| **T+2h** | **The rollback decision point** — §C | |

---

## C. The rollback decision point

**T+2 hours after the first real passenger, one named person decides: continue, or roll back.**
It is a scheduled decision with criteria agreed in advance, not a reaction. The reason to fix the
moment now is that at T+2h everyone involved will be tired, invested, and reluctant — which is
exactly when a launch that should stop does not.

### Roll back if ANY of these is true at T+2h

| Signal | Threshold |
|---|---|
| position e2e latency p95 | above 5 s (D-19) for 30 continuous minutes |
| `delivery.dropped.queue_full` | non-zero at all — telemetry is being lost silently |
| SOS dispatch p99 | above 5 s in any 5-minute window (D-33), even once |
| any money invariant | one unbalanced ledger entry, one double-charge, one payout that does not reconcile |
| ride completion rate | below 80 % of the staging baseline |
| Patroni | more than one unplanned failover |
| any HIGH security finding | newly discovered in production |

### What rollback means, and what it cannot undo

```bash
# 1. stop the world reaching it
#    take the apps out of the store rollout / put the portals behind maintenance
# 2. revert the promotion commit — the ONLY supported mechanism
git revert <promotion-sha> && git push        # ArgoCD syncs the previous SHA
# NEVER kubectl rollout undo: selfHeal reverts it within three minutes
#    (docs/runbooks/rollback.md)
```

**Data written during the two hours is not rolled back with the code.** A restore to T-0 would
discard real rides, real payments and real wallet movements from real people. So the decision at
T+2h is about *stopping*, not about *unwinding*: the platform goes back to the previous release
or to closed, and whatever was written stays written and is reconciled by hand through admin-bff
so it lands in `audit.events` (D-35).

**A schema migration cannot be reverted by reverting the commit.** The gate requires every
migration to be expand/contract and backward-compatible (`infra/scripts/migration-gate.sh`), so
the previous image runs against the new schema — that is what makes the revert above safe. If a
migration in the launch release is NOT backward-compatible, this whole section is void and the
launch must not proceed; check it at T-2d.

---

## D. Sign-off

No signature is valid before every §A item is met or carries a waiver in the same table.

| Role | Signs for | Name | Date |
|---|---|---|---|
| Engineering lead | §A2 (the platform carries the load), §B, and the §C criteria | | |
| Security owner | §A1 — every HIGH finding closed or waived in writing | | |
| Infrastructure owner | §A3 items 8, 9, 10, 12, 13 | | |
| Operations owner | §A3 items 11, 14, 15 and the status page | | |
| Project owner | the launch itself, and any waiver of a HIGH finding | | |

**Waivers**, one line each, appended here — finding, who waived it, the date, the compensating
control, and the date it will be revisited. A waiver with no revisit date is a decision to never
fix it, and should say so.

*(none)*
