# ADD §12.6 threat matrix — coverage (C127)

Every row of the ADD §12.6 security threat matrix, against a **tested control** or an **accepted
risk with an owner and a date**. C127's first definition-of-done item.

Twenty-six threats. Twenty-two have a control this review drove; four are partial and each says
exactly which half is missing and who owns it. Nothing is marked "mitigated by design" without a
test naming it — where the only evidence is a code comment, the row says so.

Column key: **Test** names the assertion that would fail if the control were removed.
`SEC` = `tests/Security`, `CHK` = `security/checks/`, other names are the owning service's own suite.

---

## The matrix

| # | Threat (ADD §12.6) | Control as built | Test | State |
|---|---|---|---|---|
| 1 | Spoofed GPS positions | Per-vehicle-type max speed (D-18), 1 km/s jump gate, accuracy > 200 m discarded, mTLS device identity | `HotPath.Tests` plausibility suite; **`SEC` `PlausibilityCorpusTests`** (Δ C128 — 35-track corpus, 0/828 honest samples refused, 0/158 attacks escaped) | **covered**, and **measured**; four attacks the gate cannot see are named in `anti-spoof-tuning.md` |
| 2 | Replay of MQTT messages | Monotonic timestamp per `vehicleId`; processor rejects older-than-last-seen | `HotPath.Tests` replay suite; **`SEC` corpus `replayed-track` family** (Δ C128) | **covered** — the clock gate is hardware-only by design; on the mobile plane `seq` is the control, and C128 measures which catches what |
| 3 | Publish above the per-vehicle ceiling | EMQX rule-engine 5 msg/s server-side; position-processor second line at 10 msg/s | **`SEC` `PublishCeilingTests` + `BrokerPolicyTests`** (Δ C128 — pacing measured; the ceiling asserted on *every* listener) | **covered** — the broker's limit is per **connection**, so several sessions under one credential beat it; that is what the two server-side lines are for, and C128 demonstrates it |
| 4 | Account takeover | Phone-OTP + D-32 bucket (60 s resend, 5/h, **fails closed**), durable failed-attempt lock-out, device binding, Play Integrity / App Attest | `Iam.Api.Tests` OTP + lock-out; `SEC` `BearerValidationTests`; `CHK` 20.1 | **covered** — no MFA is AL-37, argued |
| 5 | Tampered / cracked APK or IPA | YARP `AttestationMiddleware`, 22 sensitive operations, fails closed on an unconfigured verifier | `ApiGateway.Tests/AttestationEnforcementTests`; `CHK` 20.1 | **partial** — see C127-05 |
| 6 | Outdated client app | `X-App-Version` against a per-platform floor → `426` | `ApiGateway.Tests/VersionGateTests`; `CHK` 30.4 (**driven live**) | **covered** |
| 7 | Passenger sees a private vehicle | Sharing grants server-side; `fanout-svc` validates entitlement at SignalR group join, Redis `share:{userId}` pub/sub-invalidated | `Fanout.Api.Tests` group-join suite | **covered** |
| 8 | MQTT broker DoS via publish flood | EMQX per-client publish limit + QoS inflight cap, 1 KB max payload | **`SEC` `PublishCeilingTests`** (Δ C128 — 40 publishes on one connection are paced, not refused) | **covered** |
| 9 | Tracker IMEI cloning | IMEI bound to first provisioned cert; two sockets in 24 h force-closed and quarantined | `Provisioning.Api.Tests`, `TcpAdapter.Tests`; **`SEC` `ImeiCloneTests`** (Δ C128 — both detection paths, the 24 h boundary from both sides, timing) | **covered** |
| 10 | **Insider DB access** | Postgres RLS on fleet-scoped tables; Vault ephemeral creds; audit log | `CHK` 40.1–40.4 | **PARTIAL — C127-01 open** |
| 11 | Daily-fee bypass | Idempotent charge keyed `(driverId, vehicleId, fee_date)`, enforced before second-trip dispatch; first trip free | `Subscription.Api.Tests` fee suite | **covered** |
| 12 | Driver online on two vehicles | Redis `lock:driver:{id}` SETNX + Postgres partial unique index on `trips.sessions` | `TripState.Api.Tests` | **covered** |
| 13 | Geo data scraping | Per-user QPS limit at the edge (`geo` bucket, 120/min) + anomaly detection | `CHK` 30 (rate limiter observed enforcing) | **covered** — was **NOT** before C127-02 |
| 14 | PII in the LLM (OCR) | D-36 pre-pass: OpenCV face blur + Tesseract-boxed ID masking, before any Gemini call; `PerimeterGuardHandler` on the wire | `SEC` `RedactionPerimeterTests`; `Ocr.Api.Tests` redaction suite | **covered** |
| 15 | Trip-share link abuse | Token bound to `tripId`, expires `trip_end + 1 h`, revocable, no historical replay, 60 req/min per token and per IP, metered | `Safety.Api.Tests` share suite; `PublicBff.Tests`; `CHK` 30 (429 observed) | **covered** — the 60/min half was **NOT** before C127-02 |
| 16 | Wallet balance manipulation | All mutations server-side via wallet-svc; double-entry ledger with balanced postings; `Idempotency-Key`; audit trail | `Wallet.Api.Tests` ledger suite; `SEC` RBAC probe (every wallet route gated) | **covered** |
| 17 | Fraudulent top-up (fake receipt) | Bank transfers held pending until IPG webhook reconciles or an admin approves within 4 h | `Wallet.Api.Tests` top-up suite | **covered** |
| 18 | Credit-transfer / voucher abuse | Transfers move exact value, no commission; discount tiers server-side and admin-configured; double-entry + idempotency + audit | `Subscription.Api.Tests` voucher suite | **covered** |
| 19 | Admin / Fleet Portal XSS + CSRF | CSP headers, CSRF tokens, input sanitisation, HttpOnly cookies | portal suites | **partial** — portal-side, not re-driven by C127; see below |
| 20 | Privileged admin misuse | `admin-bff` interceptor writes `audit.events` for every mutation; the service refuses to start if a mutating route is outside the audited group | `AdminBff.Tests` audit suite; `SEC` `RbacProbeTests` (every `/v1/admin/**` route names a matrix cell) | **covered** — immutability is C127-01 |
| 21 | Ride-farming / collusion (E-07) | reputation-svc pair-frequency detector, device-binding and IP/ASN clustering, `fraud.suspected`; the auto-suspend is an admin decision, never the detector's | `Reputation.Api.Tests` detector suite; **`SEC` `RideFarmingTests`** (Δ C128 — 39-pair population: recall 100 %, `repeat_pair` precision 67 %, correlated with the device cross-check 100 %) | **covered**, precision **PARTIAL — C128-02 open** |
| 22 | Concurrent ride double-acceptance | Conditional `UPDATE … WHERE state IN … AND version=:v` + partial unique index + Redis Lua reservation | `Ride.Api.Tests` concurrency suite | **covered** |
| 23 | Replay of a mutating ride command | Mandatory `Idempotency-Key`; `rides.command_log(idempotency_key UNIQUE)` replays the stored response | kernel `IdempotencyMiddleware` suite; contract conventions | **covered** |
| 24 | Late payment callback after cash fallback | Provider transaction id UNIQUE; `payment.overpaid` reconciliation queue + refund workflow | `Fare.Api.Tests`, `AdminBff.Tests` refund queue | **covered** |
| 25 | PDPA erasure / export non-compliance (E-06) | `pdpa.requests` workflow, 30 d SLA, statutory hold list, soft-anonymise, audit never touched | `AdminBff.Tests` PDPA suite; `SEC` `RbacProbeTests` (the subject family is the only un-gated one, and named) | **covered** — see the note below |
| 26 | Driving on an expired licence (E-03) | `registry.documents.expires_at` nightly scan; auto-suspends dispatch; no offer from a non-compliant driver | `Registry.Api.Tests` expiry suite | **covered** |

---

## The four that are not fully covered, and what is owed

### Row 10 — insider DB access · **the one open HIGH**

RLS is on nine fleet-scoped tables and **not** on the tables that hold the most PII: `iam.users`
(phone, email, emergency contact), `registry.driver_profiles` (NIC). ADD §12.6 says "row-level
security on PII tables".

Two separate gaps, and the second is the urgent one:

1. **Coverage.** `iam.users` has no policy. It is defensible — there is no tenant key to scope it
   by, since a passenger's row belongs to the platform rather than to an organisation, and the
   control that does exist is `admin-bff`'s: every clear MSISDN emitted has a `PII_READ` audit row
   behind it, and unmasking needs `account-management · Write` held unscoped. **Recorded here rather
   than closed**: a per-analyst RLS policy would need a role model the platform does not have.
2. **Effect.** The nine policies that *do* exist were **inert** — see **C127-01**. Migration 2001
   sets `FORCE ROW LEVEL SECURITY` and creates the roles; the cutover is C133's and is due at
   go-live. `security/checks/40-database-privileges.sh` fails until it is done.

Vault dynamic credentials (the row's third clause) are C133's and are named in the runbook §5.

### Row 5 — attestation · partial

Enforced by code and **`Disabled` on the replica**. Accepted (C127-05): neither app exists before
Wave 4a/4b. The control that matters — `Enforce` is the default and no manifest overrides it — is
asserted by `CHK` 20.1. C127-02 is what made the operation *set* non-empty in a deployed image;
before it, `Enforce` would have enforced on nothing.

### Row 19 — portal XSS / CSRF · partial, and deliberately out of scope

The Next.js portals' CSP, CSRF tokens and cookie flags were **not** re-driven by this review. C127's
deliverables name RBAC, mTLS, attestation, tokens, PII and secrets; the browser surface belongs to
the portal components and to a DAST pass that needs a deployed portal, which the replica does not
serve. **Owner: C133, before go-live.** Named rather than quietly counted as covered.

### Row 25 — PDPA · covered, with a live-fire caveat worth reading

The workflow is tested. What C127 adds is the observation that `POST /v1/pdpa/erasure` and
`/export` admit **any authenticated caller** by design — correctly, since URD §2.3 has no cell for a
data subject's own rights and gating on the account-management row would refuse every subject their
own data. The handler scopes by `sub` and answers 404 rather than 403 for a request that is not
yours, so the status route is not an oracle over live erasure ids.

The caveat: these routes are trivially reachable and **do real work**. C118's first live sweep filed
two genuine 30-day obligations against its own operator account by sending an unauthenticated-looking
POST with a bearer. That is written up in `tests/Contract/Live/LiveRequestPlan.cs` and is why no
check in `security/checks/` sends a POST to `/v1/pdpa/**`.

---

## What a reader should not conclude from this table

**"Covered" means a control exists and a named test fails without it.** It does not mean the control
was penetration-tested, and this review ran no exploit against a live target beyond the probes in
`security/checks/30-edge-exposure.sh` — every one of which is a read or a request the platform
refuses before any handler.

**Three rows depend on infrastructure this deployment does not have.** Rows 1, 3 and 8 are enforced
by EMQX and by the hardware plane; the replica's EMQX runs the deployed `emqx.conf` and the TestKit
fixture asserts against that same file, which is the strongest statement available without a
hardware tracker on the bench.

**Δ C128 closed the part of that C127 deferred, and opened one row it could not.** The anti-spoof
hardening pass drove rows 1, 2, 3, 8, 9 and 21 against a real broker and a real database rather than
against a file: a 35-track adversarial corpus through the deployed D-18/T-07 thresholds, a
cross-vehicle publish refused on all **three** listeners rather than the one earlier suites dialled,
a cloned IMEI held at the 24 h boundary from both sides, and E-07's precision measured against a
population shaped like a Sri Lankan ride-hailing month. `security/anti-spoof-tuning.md` is the
write-up.

**What it found is row 9's other half.** T-12 says a revoked credential stops authenticating within
60 s "on both MQTT and TCP paths". The TCP path meets it. The MQTT path **does not exist in any
deployed configuration** — `enable_crl_check` is commented out in `infra/deploy/emqx/emqx.conf`, so a
revoked tracker certificate still completes the mutual-TLS handshake and still publishes. Measured
rather than inferred, recorded as **C128-01**, and blocked on a fleet-wide credential re-mint before
it can be switched on. Owner C133, before go-live.
