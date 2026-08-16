# OWASP ASVS L2 — MageRide (C127)

Reviewed 2026-08-12 against the implemented platform and the deployed lightweight replica
(Contabo EU, synthetic data). ASVS 4.0.3 chapter numbering.

**Verify:** `bash security/run-asvs-checks.sh && dotnet test tests/Security -c Release`

**How to read the evidence column.** Every row names something that fails if the control is removed
— a test, a check, or a live observation with the status code it produced. Where the only evidence
is a code comment, the row says **argued, untested** and the item is in
`security/remediation-backlog.md`. An ASVS checklist whose evidence column says "yes" is a document
about somebody's confidence; this one is about what breaks.

Scope note: **L2, and the browser half is L2-incomplete.** V5 (validation/encoding) and the portal
side of V3 were not re-driven here — see V5 below and threat-matrix row 19. Everything else is
covered or has a named owner.

---

## Summary

| Chapter | Items reviewed | Pass | Partial | Findings |
|---|---|---|---|---|
| V1 Architecture | 8 | 7 | 1 | C127-03 |
| V2 Authentication | 11 | 11 | — | — |
| V3 Session management | 8 | 8 | — | — |
| V4 Access control | 9 | 8 | 1 | C127-09 (accepted) |
| V5 Validation & encoding | 5 | 3 | 2 | portal DAST → C133 |
| V6 Cryptography | 6 | 6 | — | — |
| V7 Logging & error handling | 7 | 5 | 2 | **C127-01** |
| V8 Data protection | 8 | 7 | 1 | **C127-01** |
| V9 Communications | 5 | 5 | — | — |
| V10 Malicious code | 3 | 3 | — | — |
| V11 Business logic | 6 | 6 | — | — |
| V12 Files & resources | 5 | 5 | — | — |
| V13 API & web service | 8 | 7 | 1 | C127-08 (accepted) |
| V14 Configuration | 9 | 7 | 2 | **C127-02**, C127-04 |

Two HIGH findings. **C127-02 is fixed and verified live.** **C127-01 is fixed in the repository and
open at deployment**, owned by C133 and due at go-live.

---

## V1 — Architecture, design and threat modelling

| Item | Requirement | Evidence |
|---|---|---|
| 1.1.4 | Trust boundaries documented | ADD §12, `infra/CLAUDE.md`; the edge/service/mesh split is `Gateway:BlockedPathPrefixes` + `AnonymousSurface.InternalPrefix` |
| 1.2.1 | Unique low-privilege OS accounts per component | `Dockerfile.service` runs `app:app`; `runAsNonRoot: true`, `runAsUser: 1000` in every k8s Deployment |
| 1.2.2 | Component-to-component authentication | Shared internal key today, **not** mesh mTLS. `AnonymousSurface` lists all 46 internal routes; **C042** owns the SPIFFE identity. Argued, tested at the key level |
| 1.4.1 | Access control enforced at a trusted layer | `SEC RbacProbeTests.No_endpoint_is_reachable_with_no_credential_requirement` over all 444 endpoints |
| 1.4.4 | A single, well-vetted access-control mechanism | `MageRide.Shared.Auth` — one `PermissionMatrix`, one evaluator, one fallback policy. `SEC RbacProbeTests.Every_service_registers_the_deny_by_default_fallback_policy` (23 of 24; `ocr` is a named exception with its own asserted compensating property) |
| 1.5.2 | No serialisation to untrusted clients | `System.Text.Json` with explicit contracts; no BinaryFormatter anywhere |
| 1.14.6 | No unsupported/insecure client-side tech | Kotlin/Compose + SwiftUI + Next.js; no WebView bridge |
| **1.9.1** | **Encrypted communication between components** | **PARTIAL — C127-03.** Three mTLS-declared operations were published to the internet; fixed and verified 404 at the edge. Mesh mTLS itself is C042 |

---

## V2 — Authentication

| Item | Requirement | Evidence |
|---|---|---|
| 2.1.x | Password policy for portal accounts | `Iam.Api/Auth/PasswordHasher` — PBKDF2, `Auth:PasswordIterations` validated 100 000–5 000 000 |
| 2.2.1 | Anti-automation on credential paths | D-32 Redis token bucket, 60 s resend, 5/h, **fails closed** on an unreachable Redis (deliberately unlike the gateway's, which fails open — this one guards an SMS bill). `Iam.Api.Tests` |
| 2.2.3 | Notification on credential change | notification-svc templates; `Notification.Api.Tests` |
| 2.3.1 | OTP secrets not at rest in the clear | `iam.otp_attempts.otp_hash` = HMAC-SHA256(`{authId}:{code}`) under `Otp:PepperKey`, required outside Development |
| 2.5.x | Credential recovery does not reveal the credential | OTP re-issue only; no password is ever transmitted |
| 2.7.2 | OTP delivered out of band, time-limited | SMS via Fit SMS + a secondary gateway (AL-60, D6' §7.3); D-33's 5 s p99 |
| 2.8.x | MFA | **AL-37 removed it deliberately.** Compensating controls: durable failed-attempt lock-out on `iam.user_credentials` (not a Redis counter — a cache flush must not reset every internal account at once), session binding, optional `Auth:InternalRoleIpAllowList`. `Iam.Api.Tests` lock-out suite |
| 2.10.1 | Service accounts do not use default credentials | `.env.*.example` placeholders only; `CHK` 10.3 |
| 2.10.4 | Secrets stored with protection | Vault + ESO (D7' §13); `CHK` 10.4 |
| — | Federated identity binds on `sub`, not email | `Iam.Api/Auth/OidcTokenVerifier`; `Oidc:{Provider}:ClientIds` has no default and an empty list refuses everything |
| — | Device binding | Android Keystore / iOS Secure Enclave; the MQTT credential inherits the session's `device_id` |

---

## V3 — Session management

| Item | Requirement | Evidence |
|---|---|---|
| 3.2.1 | New session token on authentication | `AccessTokenIssuer`; `Iam.Api.Tests` |
| 3.2.3 | Tokens not exposed in URLs or logs | Bearer header only. The share token *is* in a path — AL-44's deliberate design, scoped and metered (V13 below) |
| 3.3.1 | Logout invalidates the session | `DEL refresh:{jti}` + `UPDATE … revoked_at` |
| 3.3.2 | Re-authentication period | 30 min access / 30 d refresh (D-29) |
| 3.5.2 | Static API secrets are not used for user sessions | The internal key never authenticates a user; `SEC AnonymousSurface` records the credential for each of the 147 anonymous endpoints |
| 3.5.3 | Stateless tokens use a strong signature | RS256 only. `SEC BearerValidationTests.The_service_accepts_RS256_and_nothing_else` asserts the exact set on **all 22** bearer-validating services |
| — | Algorithm confusion | `SEC BearerValidationTests` forges a `super_admin` token with the published RSA modulus as an HMAC secret and asserts it is refused; `alg: none` likewise. Live: `CHK` 30.6 → 401 |
| — | Refresh reuse detection | Single-use `mr1.{jti}.{hmac}`; replaying a spent token revokes its **rotation family** (`family_id`, migration 0106), never everything for the `(user, app)` — a stale handset must not log the live one out on a loop. `Iam.Api.Tests` |

---

## V4 — Access control

| Item | Requirement | Evidence |
|---|---|---|
| 4.1.1 | Access controls enforced server-side | `SEC RbacProbeTests` over 444 endpoints; `CHK` 30.3 drives ten privileged routes at the live edge → 401 each |
| 4.1.2 | User/data attributes not manipulable | Roles come from the token's repeated `role` claims, re-read from the account on every refresh; `iam.user_roles` is the only writable half |
| 4.1.3 | Least privilege | 63 endpoints name a URD §2.3 (area, capability) pair; 93 name a role or fleet sub-role. `SEC` reports the pair off the composed `FeaturePermissionRequirement`, so the evidence is the policy the server applies |
| 4.1.5 | Fail securely | `PermissionMatrix.Cell` answers ➖ for a pair it does not hold; an endpoint naming an untranscribed area answers 403 rather than falling through. `SEC RbacProbeTests.Every_feature_policy_names_an_area_the_matrix_actually_has` |
| 4.2.1 | Protection against IDOR | Ownership checks in handlers against `sub`; a resource that is not yours is 404, not 403 (the house rule wallet-svc states and PDPA follows) |
| 4.2.2 | CSRF protection on state-changing operations | Bearer-authenticated APIs (no ambient cookie); portals use CSRF tokens |
| 4.3.1 | Admin interfaces use appropriate MFA | **AL-37 removed MFA** — see V2.8 |
| 4.3.2 | No directory browsing / unintended file exposure | Signed-link routes only; `SEC AnonymousSurface` lists all five and names the HMAC key |
| **4.3.3** | **Additional authorisation for high-value transactions** | **PARTIAL — C127-09, accepted.** 141 endpoints admit any authenticated caller. Argued per class in the backlog; the two surfaces where that is never right (`/v1/admin/**` and the fleet sub-role family) are asserted exhaustively |

---

## V5 — Validation, sanitisation and encoding

| Item | Requirement | Evidence |
|---|---|---|
| 5.1.x | Input validation | Minimal-API typed binding + `ValidateDataAnnotations` on every options class; contract-level schema validation in `tests/Contract` |
| 5.3.4 | SQL injection prevented | **Dapper with hand-written parameterised SQL, no EF Core (AL-53).** Structurally: `Npgsql` parameters are never string-concatenated in a repository |
| 5.3.8 | LDAP / OS command injection | No LDAP. One child process (`tesseract`) invoked with an argument array, never a shell string |
| **5.3.3** | **Output encoding / XSS** | **NOT RE-DRIVEN.** React escapes by default and the portals set a CSP; a DAST pass against a deployed portal is **C133's**, before go-live |
| **5.2.x** | **Sanitisation of untrusted HTML** | **NOT RE-DRIVEN.** Same owner. Content-svc stores template bodies with `{{placeholder}}` substitution, not HTML |

---

## V6 — Cryptography at rest

| Item | Requirement | Evidence |
|---|---|---|
| 6.2.1 | No custom crypto | `System.Security.Cryptography` and `Microsoft.IdentityModel` throughout |
| 6.2.2 | Approved algorithms | RS256 (API), HS256 (MQTT session, matching EMQX's `hmac-based`), SHA-256, PBKDF2, HMAC-SHA256 |
| 6.2.3 | Random values are cryptographically secure | `RandomNumberGenerator`; `Ulids` for identifiers |
| 6.2.8 | Constant-time comparison of secrets | `CryptographicOperations.FixedTimeEquals` in every `InternalKeyFilter` and every webhook signature check |
| 6.3.2 | GUIDs are not used as secrets | Share tokens are minted random values in `safety.trip_share_tokens`, not GUIDs |
| 6.4.1 | Secret management | Vault + ESO; `CHK` 10.4; rotation procedures in `docs/runbooks/secret-rotation.md` |

---

## V7 — Error handling and logging

| Item | Requirement | Evidence |
|---|---|---|
| 7.1.1 | No credentials or PII in logs | `PII_READ`/`DOC_VIEW` audit rows record *that* a value was revealed, never the value |
| 7.1.3 | Security-relevant events logged | `audit.events` for every admin mutation (D-35), plus the interceptor's `detail` (method, path, role union, idempotency key, IP) |
| 7.2.1 | Access-control decisions logged | `AttestationRejections` / `AttestationAudited` metrics; `BlockedPathMiddleware` logs each refusal at Warning |
| 7.4.1 | Generic error messages | RFC 7807 problem+json everywhere; `tests/Contract` `Conventions` asserts it on every declared error |
| 7.4.3 | No stack traces to clients | `ProblemDetailsExceptionHandler` |
| **7.3.1** | **Log protection from tampering** | **FAIL → C127-01.** `audit.events` accepted `UPDATE`/`DELETE`/`TRUNCATE` from the connecting role. Migration 2001 lands the mechanism; the cutover is C133's. `CHK` 40.2 |
| **7.3.3** | **Logs protected from unauthorised access** | **PARTIAL — C127-01.** `mageride_readonly` exists and grants `SELECT` only; until the cutover every service holds full access |

---

## V8 — Data protection

| Item | Requirement | Evidence |
|---|---|---|
| 8.1.1 | Sensitive data not cached by the client | `Cache-Control: no-store` on authenticated responses |
| 8.2.2 | No sensitive data in client storage | KMP shared module stores tokens in Keystore / Keychain |
| 8.3.1 | Sensitive data sent in the body, not the query string | The five signed links carry an **HMAC**, never the protected value |
| 8.3.4 | Collected PII is documented | E-06 export enumerates fourteen datasets; `pdpa.requests` records what was retained and why |
| 8.3.7 | PII protected with approved algorithms | Phone hashed under `Auth:PhoneHashKey` (`iam.phone_lookups` — *"to correlate repeats, never to recover a number"*); documents in an SSE bucket, presigned reads |
| — | **D-36 pre-LLM redaction** | OpenCV face blur + Tesseract-boxed ID masking before any Gemini call. Two fences: a type only `RedactionPipeline` can construct, and `PerimeterGuardHandler` on the wire. `SEC RedactionPerimeterTests` asserts the handler is **still on the composed client**, which is the half that fails silently |
| — | Signed-URL access to raw documents | `AdminBff` 302s to the bucket's presigned GET; the process never holds a byte. One view = one `DOC_VIEW` row |
| **8.1.4** | **Detect and alert on abnormal data retrieval** | **PARTIAL — C127-01.** RLS is the database-side control and was inert. Directory reads are audited (`PII_READ`), which is detection after the fact; no alert threshold exists. Recorded in the backlog under row 10 |

---

## V9 — Communications

| Item | Requirement | Evidence |
|---|---|---|
| 9.1.1 | TLS for all client connectivity | HAProxy terminates HTTPS/WSS; the replica's certificate is self-signed by design and every probe says so |
| 9.1.2 | Only strong ciphers/versions | TLS 1.3 (ADD §12.2) |
| 9.1.3 | Old TLS versions disabled | HAProxy config |
| 9.2.1 | Connections to backends use trusted certificates | Mesh mTLS is C042; today the internal plane is network-isolated plus a shared key |
| — | **No plaintext MQTT published** | `CHK` 20.4 — asserts no compose host port and no LoadBalancer/NodePort maps 1883. EMQX still *binds* it for in-cluster clients; 8883 (mTLS) and 8084 (WSS+JWT) are the only two an edge publishes |

---

## V10 — Malicious code

| Item | Requirement | Evidence |
|---|---|---|
| 10.2.1 | No unauthorised data exfiltration | `PerimeterGuardHandler` refuses an outbound image the redactor never produced, and logs it at Critical. `SEC RedactionPerimeterTests` |
| 10.3.2 | Integrity of deployed code | Images from GHCR by digest; `Play Integrity` / `App Attest` for clients |
| 10.3.3 | Dependency management | Central package management (`Directory.Packages.props`); a version added there or the build fails NU1008 |

---

## V11 — Business logic

| Item | Requirement | Evidence |
|---|---|---|
| 11.1.1 | Sequential processing | Ride saga state machine; `rides.transitions` is an immutable audit with no UPDATE path |
| 11.1.2 | Business-limit enforcement | Fare ceilings, wallet balance checks, D-17 message rates |
| 11.1.3 | Anti-automation | Eight edge rate-limit buckets (20–600/min) **plus** D-32's OTP bucket. Live-verified after C127-02 |
| 11.1.4 | Excessive-use detection | reputation-svc pair-frequency detector (E-07) |
| 11.1.5 | Business-logic-flaw controls | Double-entry ledger; conditional `UPDATE … version=:v`; Redis Lua reservation |
| 11.1.8 | Alerting on automated attack | `AttestationRejections`, rate-limit rejection metrics, `emqx-auth-failures` runbook |

---

## V12 — Files and resources

| Item | Requirement | Evidence |
|---|---|---|
| 12.1.1 | Upload size limits | `Ocr:Storage:MaxBytes` 16 MiB, `Support:ScreenshotMaxBytes` |
| 12.3.1 | User-supplied filenames not used in paths | Object keys built from minted ids — the kernel rule: *"build the key from ids you minted, never from a client filename"* |
| 12.3.4 | File-type validation | Content sniffing, not the extension |
| 12.4.2 | Uploads scanned | OCR pipeline decodes every image; a file that is not an image fails to decode |
| 12.5.1 | No serving of files with executable extensions | Object storage with presigned GETs; no static file middleware on any service |

---

## V13 — API and web service

| Item | Requirement | Evidence |
|---|---|---|
| 13.1.1 | Same encoding for all API components | `MageRideJson`, one options instance |
| 13.1.3 | API URLs do not expose sensitive information | Ids are ULIDs/UUIDs |
| 13.1.4 | Authorisation decisions at both URI and resource level | Endpoint policy + handler ownership check |
| 13.2.1 | Only permitted HTTP methods | Minimal-API verb mapping; `tests/Contract` asserts both directions against the contracts |
| 13.2.3 | CSRF protection for JSON APIs | Bearer only; no ambient credential |
| 13.2.5 | Message-payload schema validation | Contract-driven; `tests/Contract` `SchemaValidator` |
| 13.4.1 | Query-allowlist / depth limits for GraphQL | n/a — no GraphQL |
| **13.1.5** | **Requests with unexpected content types rejected** | **PARTIAL — C127-08, accepted.** Twelve `/v1/internal` operations declare no `security` block; the prefix and the key filter protect them, and C042 rewrites all twelve |

### The share-token surface (D-34, AL-44) — reviewed as its own item

The token in the path is the whole credential on seven routes. Scoped to one trip; expires at
`trip_end + 1 h`; revocable; no historical replay; **60 req/min per token and per IP**; metered on
`trip_share_tokens`; scope-gated (`proxy_rider`, `pickup_confirm`) so a live token does not imply a
confirm right. `public-bff` registers **no authentication scheme at all**, so the token is
structurally the only credential it can accept, and the service refuses to start if any route on it
is not under the token gate. The 60/min half was **not in force** before C127-02 and is now
live-verified (67 × 404 then 3 × 429).

---

## V14 — Configuration

| Item | Requirement | Evidence |
|---|---|---|
| 14.1.1 | Build and deploy are repeatable | Docker Compose → DOKS; `infra/replica/deploy.sh` |
| 14.1.3 | Build pipeline warns on out-of-date components | `Directory.Packages.props`, Dependabot |
| 14.2.1 | All components are up to date | .NET 10, YARP 2.3, Npgsql 10 |
| 14.3.2 | Debug modes disabled in production | `EnvironmentName` from the deployment; ephemeral JWT keys are refused outside Development |
| 14.3.3 | No version fingerprinting | No `Server` header exposed by HAProxy |
| 14.4.1 | Content-type on every response | `application/json` / `application/problem+json` |
| 14.5.1 | Unused HTTP methods rejected | Route table is explicit |
| **14.1.4** | **Deployed configuration matches the reviewed configuration** | **FAIL → C127-02, FIXED.** The entire `Gateway` policy section was absent from the co-located image: no rate limit on 70 routes, nothing marked D-30 sensitive. Now `gateway-policy.json`, loaded `optional: false` so a gateway without it refuses to start |
| **14.2.6** | **No secrets in the source repository** | **PASS, with C127-04 FIXED.** `*.key` was missing from `.gitignore`. `CHK` 10.1–10.4: ignore rules, tracked key material, filled-in placeholders, 33 ESO manifests, the rotation runbook's coverage |

---

## Evidence appendix — the endpoint inventory

`MAGERIDE_SECURITY_DUMP=<path> dotnet test tests/Security -c Release --filter InventoryDump`
writes every endpoint with its guard and the permission it names, read off the composed pipeline.

At sign-off, **444 endpoints across 24 services**:

| Guard | Count | What it means |
|---|---|---|
| Feature | 63 | names a URD §2.3 (area, capability) pair |
| Role | 93 | names a canonical role or a fleet sub-role |
| AuthenticatedOnly | 141 | kernel fallback + an ownership check in the handler (C127-09) |
| Anonymous | 147 | reviewed compensating credential — see `AnonymousSurface` |
| **Open** | **0** | **nothing is reachable with no credential requirement** |

Of the 147 anonymous, every one has a named compensating credential in
`tests/Security/Rbac/AnonymousSurface.cs`, and the probe fails on any route that does not:

| Class | Count | Compensating credential |
|---|---|---|
| `/v1/internal/**` | 55 | edge-refused **and** internal-key filter answering 404 |
| kernel health probes | 48 | none, and correctly none — the edge does not publish them (`CHK` 30.1) |
| gRPC methods | 12 | server interceptor; the port is not fronted by the edge |
| pre-credential auth | 8 | there cannot be one yet — D-32 bucket, lock-out, D-30 attestation |
| share-token routes | 7 | the token (D-34, AL-44): trip-scoped, expiring, revocable, metered, 60/min |
| payment webhooks | 6 | provider HMAC over the body; provider transaction id UNIQUE |
| signed links | 5 | HMAC in the query string — the presigned-URL shape |
| internal-key, outside the prefix | 3 | internal-key filter **+ edge-refused as of C127-03** |
| genuinely public reads | 2 | none needed — `/v1/config/cities`, the onboarding carousel |
| JWKS | 1 | none — public by construction (RFC 7517) |
