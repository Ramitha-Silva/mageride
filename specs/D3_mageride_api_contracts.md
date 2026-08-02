# D3′ — MageRide API Contracts (.NET 10 LTS)

> **🔄 Aligned to ADD v2.6 / URD v2.2 (ADD §1.8 AL-01…AL-16).** This pass: JWT `role` claim = **nine canonical roles** + `fleet_role`, deny-by-default RBAC (AL-06); **auth by surface** — apps Phone OTP only, Admin Portal Password/Google (**no MFA — AL-37 supersedes the original "+MFA" wording**), Fleet Portal Email/Google/Apple (AL-07); `app` claim + **single-active-device per app** (AL-08); **reseller endpoints are Driver-App APIs** (capability, not role/portal — AL-01); **bank-transfer top-up endpoints removed** (AL-05); LankaQR top-up returns a **Pay deep link** (AL-15); **canonical `vehicleType` enum** (car→sedan, +truck/mini_truck — AL-09); **fleet-svc → Phase 1** with full Fleet Portal route table (AL-03); `wallet-portal` references → **Admin Portal** (`admin-bff`, AL-02); passenger settings endpoints (AL-14).

> **Phase B deliverable (Prompt B3).** Transformed from the Namma Yatri Phase-A API extraction
> (`nammayatri-extraction/D3_api_contracts.md`) onto **MageRide .NET 10 LTS minimal-API** microservices
> per ADD v2.4 §6 + Appendix C + §12, canonical service list `lightweight-production-replica.md`,
> endpoint intent from URD v1.3 §4.
>
> **Stack delta:** NY = Haskell + Servant type-level routing + EulerHS + opaque long-lived token +
> Beckn/ONDC + Juspay + Kafka/external-LTS + ₹/+91. MageRide = **.NET 10 LTS minimal API** + **JWT
> RS256 (30-min access + opaque rotating refresh, D-29)** + **RFC 7807 Problem Details** + **MQTT
> (EMQX) ingest + SignalR fan-out** + **OnePay/LankaQR/Cash** + **Rs (integer minor units)/+94/Si·Ta·En**.
> Every mapped endpoint tagged `[KEEP]`/`[ADAPT]`/`[REPLACE]`; MageRide-only = `[NEW]` + full spec.
> **Hard rules:** payment endpoints always `[REPLACE]`/`[NEW]`; map/tile endpoints always `[REPLACE]`.
> All `[DELTA:HASKELL]` resolved to .NET, `[DELTA:INDIA]`/`[DELTA:JUSPAY]` to SL/native, all Phase-A
> `[UNVERIFIED]` resolved (see Verification).

---

## 0. MageRide .NET 10 Conventions (apply to every endpoint)

- **Routing:** .NET 10 LTS minimal-API route groups per service (`app.MapGroup("/...")`); REST + JSON
  (`System.Text.Json`, camelCase). gRPC where noted (`reputation-svc`, `query-svc` internal).
- **Data access:** **Dapper** (micro-ORM) over **Npgsql** — hand-written **parameterised SQL**, a
  **repository per bounded context**, no EF Core / `DbContext` / LINQ-to-SQL and **no ORM change
  tracking**. PostGIS / Timescale queries are written as raw SQL. Schema changes ship as versioned
  **SQL migration scripts** (DbUp/Grate), never `dotnet ef`. Multi-statement writes use explicit
  `NpgsqlTransaction`; idempotent commands key on the `Idempotency-Key` table (D-29).
- **Auth (AL-06/07/08):** `Authorization: Bearer <JWT>` — **RS256, 30-min access** (JWKS-verifiable) + **opaque
  rotating refresh** in `iam.sessions`+Redis `refresh:{jti}` (single-use; **single-active-device per app** —
  a new-device login revokes only that app's prior session, US-1.12). **Sign-in by surface:** Passenger/Driver
  apps = **Phone OTP only**; Admin Portal (`admin.mageride.lk`) = **Password or Google Sign-In — no MFA/TOTP
  second factor** (~~Password or Google + MFA~~ removed by **AL-37**; compensating controls = failed-attempt
  lock-out, session binding, optional IP allow-list — see the Δ Addendum 2026-06-28); Fleet Portal
  (`fleet.mageride.lk`) = **Email+Password / Google / Apple**. Claims: `sub` (userId), `role ∈ {passenger,
  driver, fleet_owner, admin, super_admin, verification_officer, support_csr, finance_officer, auditor}`
  (effective perms = union of `iam.user_roles`, **deny-by-default RBAC**), `fleet_role?` ∈ {owner,manager,viewer},
  `device_id`, `app ∈ {passenger,driver}`, attestation claim. **MQTT session JWT is separate** (TTL =
  max(ride+2h, 4h), bound `(vehicleId,deviceId,rideId?)` — E-02). Service-to-service = **mTLS**
  (Linkerd/SPIFFE), routes prefixed `/internal`.
- **Attestation (D-30):** YARP gateway middleware validates **Play Integrity** (Android) /
  **App Attest** (iOS) header `X-Attestation` on sensitive mutations (auth, payments, ride accept,
  wallet, SOS). Failure → `401` `attestation-failed`.
- **Min-version gate (D-31):** gateway reads `X-App-Version` (+ `X-Platform: android|ios`); below
  floor → **`426 Upgrade Required`** with body `{updateUrl, latestVersion, isMandatory}`.
- **Errors:** **RFC 7807** — `Content-Type: application/problem+json`:
  ```json
  { "type":"https://mageride.lk/errors/{code}", "title":"...", "status":400,
    "detail":"...", "instance":"/path", "traceId":"00-..." }
  ```
  `{code}` = stable kebab key (e.g. `invalid-otp`, `offer-expired`). 400 validation/state · 401 auth ·
  403 forbidden · 404 not-found · 409 conflict (optimistic-concurrency/atomic-accept) · 410 gone
  (expired offer) · 423 locked (OTP attempts) · 426 upgrade · 429 rate-limited.
- **Pagination:** cursor-based — `{ "items":[...], "cursor":"opaque|null", "hasMore":bool }`; query
  `?cursor=&limit=` (default 20, max 100).
- **Timestamps:** ISO 8601 UTC (`DateTimeOffset`). Business dates `Asia/Colombo` (D-13).
- **Money:** **integer minor units** (Rs × 100; `long amountMinor`, `currency:"LKR"`).
- **Idempotency:** `Idempotency-Key` header (ULID/UUID ≤128) **required on all POST mutations**;
  duplicates replay the original response from a per-service command log (R-14, R-18). All ride-svc
  responses carry `version` for optimistic concurrency.
- **Versioning:** path-stable `/v1` per service behind gateway; behaviour adapts via `X-App-Version`
  (replaces NY `/v2`·`/ui` split + `x-bundle/client/config-version` headers, `[DELTA:HASKELL]`).

---

# PART 1 — SERVICE MAP (Namma Yatri → MageRide)

| Namma Yatri Service | MageRide Service(s) | Transformation |
|---|---|---|
| `rider-app` (BAP) auth/profile | **iam-svc** | `[ADAPT]` opaque token → RS256 JWT + refresh; +94 SMS-gateway OTP; no Beckn/Aadhaar; **apps = Phone OTP only; Google/Apple/Password on the Admin & Fleet portals only** (AL-07) |
| `rider-app` search→confirm (Beckn) | **ride-svc** + **fare-svc** + **dispatch-svc** | `[REPLACE]` Beckn `search/init/confirm` round-trip → direct `POST /rides/request` (idempotent) → server dispatch; no ONDC gateway |
| `driver-app` ride lifecycle (`Ride.hs`) | **ride-svc** (Mode C) + **trip-state-svc** (A/B) | `[REPLACE]`/`[ADAPT]` split: Mode C ride aggregate (R-01) vs Mode A/B tracking sessions |
| `driver-app` onboarding/KYC | **registry-svc** + **ocr-svc** | `[ADAPT]` DL/RC + Gemini OCR; **drop** Aadhaar/PAN/GST/UPI; **driver payout profile** — bank + own LankaQR, Verification-Officer approved (AL-58/AL-59; D-11's OnePay merchant onboarding retired by AL-57). **Change 6/22:** split into driver-identity Profile Setup (name/photo/DL) + optional **Mode-C 4-step vehicle onboarding** with **Gemini Flash 3.0 auto-verify → auto-approve** (Mode A/B + permits = Fleet Portal) |
| `Allocator` worker (FCM offer) | **dispatch-svc** | `[REPLACE]` candidate scoring + 15s Redis offer + Job Board + Driver Level + Directional |
| `driver-app` Plans/fees (Juspay) | **subscription-svc** + **wallet-svc** | `[REPLACE]` Juspay mandate → daily-fee + wallet + vouchers + reseller capability (top-up via **OnePay/LankaQR only — no bank transfer**, AL-05) |
| rider/driver Payment (Juspay) | **fare-svc** + **wallet-svc** | `[REPLACE]` Juspay SDK → OnePay/LankaQR/Cash payment state machine |
| Rating (`Rating.hs`) | **trip-state-svc** + **ride-svc** | `[KEEP]` 1–5 stars + text; passenger↔driver |
| SOS (`Sos.hs`) | **safety-svc** | `[ADAPT]` SOS → SMS to emergency contact; + report/block driver; trip-share token (D-34) |
| Maps/Route proxy (`Maps.hs`) | **query-svc** + **nominatim-svc** + **tile-cdn** | `[REPLACE]` Google → MapLibre/PMTiles + self-hosted geocoder |
| live driver loc (`Ride.hs` driver/location) | **query-svc** + **fanout-svc** (SignalR) | `[REPLACE]` HTTP poll/external LTS → SignalR geocell groups + MQTT ingest |
| TriggerFCM / Notifications | **notification-svc** + **content-svc** | `[ADAPT]` FCM+APNs HTTP v1 batch + Si/Ta/En templates (D-26, D-27) |
| Dashboards (BFF, RBAC) | **admin-bff** (Admin Portal `admin.mageride.lk`) + **fleet-svc** (Fleet Portal `fleet.mageride.lk`) | `[ADAPT]` single consolidated back-office for all six internal roles (AL-02); `wallet-portal` **removed**; audit interceptor (D-35), PDPA, train admin, nine-role RBAC (~~+ MFA~~ **no MFA — AL-37**) |
| Kafka LocationUpdate / LTS | **mqtt-broker** + **mqtt-bridge** + **position-processor** + **persistence-writer** | `[REPLACE]` Kafka→Redpanda; external LTS → in-repo MQTT pipeline |
| Cancellation reasons / dues | **ride-svc** + **fare-svc** | `[ADAPT]` Rs 50 cross-trip settlement (D-05) |
| *(none)* | **dispatch-svc Directional** | `[NEW]` Directional Travel (DT-01..08) |
| *(none)* | **reputation-svc** | `[NEW]` block_status/driver_level gRPC, anti-collusion (D-04, E-07) |
| *(none)* | **provisioning-svc** + **tcp-adapter** (a.k.a. tracker-adapter-svc) + **fleet-health-svc** | `[NEW]` hardware tracker plane (T-01,T-02,T-09) |
| *(none)* | **voip-svc** | `[NEW]` LiveKit signalling tokens (D-24/25) |
| *(none)* | **support-svc** | `[NEW]` FAQ + tickets (Epic 16) |
| *(none)* | **fleet-svc** (Fleet Portal) | `[NEW]` **Phase 1** fleet org (AL-03): verification-gated onboarding, Mode A/B vehicles, driver assignment, scheduling, fleet map/analytics, monthly per-Mode-B-vehicle billing |
| *(none)* | **version-check** (gateway) | `[NEW]` X-App-Version → 426 (D-31) |
| *(none)* | **pdpa-svc** (via admin-bff) | `[NEW]` export/erasure (E-06) |

---

# PART 2 — ENDPOINT CATALOG (by MageRide service)

## iam-svc — auth, profile, token (`/v1/auth`, `/v1/users`)

### iam-svc — POST /v1/auth/otp/request   [ADAPT] (NY `POST /v2/auth`)
Purpose: start login; send OTP via SMS gateway (Notify.lk). **Auth:** none (public) + attestation.
Request body:
```jsonc
{ "phone": "string  // +94 E.164, required, regex ^\\+947\\d{8}$",
  "deviceId": "string  // device binding, required",
  "fcmToken": "string?", "role": "enum passenger|driver  // apps only; 'reseller' is not a role or capability (AL-01)" }
```
Response 200: `{ "authId":"ulid", "attemptsRemaining":int, "cooldownSeconds":60, "isBlocked":bool }`
Errors: |400 `invalid-phone`|bad +94| · |429 `otp-rate-limited`|>5/h or <60s resend (D-32)| · |403 `user-blocked`|
Side Effects: mint+send OTP; create `iam.auth_attempts`. Idempotent: no. Rate Limit: Redis token-bucket 60s/5h.

### iam-svc — POST /v1/auth/otp/verify   [ADAPT] (NY `POST /v2/auth/{authId}/verify`)
Purpose: verify OTP, issue tokens. **Auth:** none + attestation. Idempotency-Key required.
Request:
```jsonc
{ "authId":"ulid // required", "otp":"string // 6 digits, required",
  "deviceId":"string // required, must match request" }
```
Response 200:
```jsonc
{ "accessToken":"jwt // RS256, exp 30m", "refreshToken":"opaque",
  "expiresIn":1800, "user": { "userId":"ulid","phone":"+94...","firstName":"string?",
  "role":"passenger","language":"si|ta|en?" }, "isNewUser":bool }
```
Errors: |401 `invalid-otp`| · |400 `otp-expired`| · |404 `auth-not-found`| · |409 `device-mismatch`|
Side Effects: persist `iam.sessions` (refresh jti) + Redis `refresh:{jti}`; **revoke prior device
sessions** (US-1.11); bind device. Idempotent: yes (replay token). Rate Limit: yes.

### iam-svc — route table (rest)   [ADAPT]/[NEW]
| Verb · Path | Auth | Tag | Body → Resp | Notes |
|---|---|---|---|---|
| POST `/v1/auth/otp/resend` | none | [ADAPT] | `{authId}` → `{attemptsRemaining,cooldownSeconds}` | 60s cooldown |
| POST `/v1/auth/refresh` | refresh tok | [NEW] | `{refreshToken}` → `{accessToken,refreshToken,expiresIn}` | rotate jti (D-29) |
| POST `/v1/auth/logout` | Bearer | [ADAPT] | `{}` → 204 | revoke refresh (US-1.7) |
| POST `/v1/auth/google` | Google idToken | [NEW] | `{idToken}` → tokens | **Admin & Fleet portals only** (AL-07); apps are Phone OTP only |
| POST `/v1/auth/apple` | Apple idToken | [NEW] | `{idToken}` → tokens | **Fleet Portal only** (AL-07) |
| POST `/v1/auth/password` | email+password | [NEW] | → tokens (**no MFA challenge** — AL-37) | **Admin Portal (Password) + Fleet Portal (Email+Password)** (AL-07) |
| POST `/v1/auth/mqtt-token` | Bearer | [NEW] | `{vehicleId,deviceId,rideId?}` → `{mqttJwt,expiresIn}` | E-02 long TTL |
| GET `/v1/users/me` | Bearer | [KEEP] | → `UserProfile` | profile |
| PUT `/v1/users/me` | Bearer | [KEEP] | `{firstName,photoUrl,language,notifPrefs}` → `UserProfile` | US-1.5 |
| GET `/v1/users/lookup?phone=` | mTLS internal | [NEW] | → `{registered:bool, userId?}` | proxy P-03 |
| DELETE `/v1/users/me` | Bearer | [NEW] | → 202 `{requestId}` | account delete → pdpa (US-1.8) |

## content-svc — public reference data (`/v1/config`)   [NEW] (Change 6/22)

### content-svc — GET /v1/config/cities   [NEW]
Purpose: launch-city list for the first-run language/city screen (SCR-DA/DI-002). **Auth:** none (public), read-only.
Response 200:
```jsonc
{ "cities": [ { "code":"colombo", "nameEn":"Colombo", "nameSi":"කොළඹ", "nameTa":"கொழும்பு",
  "centroid":{"lat":6.9271,"lng":79.8612}, "sortOrder":0 } ] }   // active rows only, ordered by sortOrder
```
Notes: backed by `config.operating_cities` (D4 §17b); admin-managed in the Admin Portal
(`POST/PATCH /v1/admin/config/cities`, admin-bff, audited D-35). **Cacheable** (ETag / `Cache-Control`, see D6 §7) —
launching a new city needs no app release. The chosen `code` persists on `iam.users.operating_city_code`.

## registry-svc — vehicles, sharing, device binding, driver payout profile (`/v1/vehicles`)

### registry-svc — POST /v1/vehicles   [ADAPT] (NY `register/dl`+`register/rc`; Change 6/22 = Mode-C 4-step auto-verify)
Purpose: onboard the driver's own **Mode C standby vehicle** (SCR-DA/DI-004 → 004a/b/c) with its 4 documents;
queues Gemini Flash 3.0 extraction and **auto-approves** on success. **Auth:** Bearer (driver) + attestation.
Idempotency-Key required. **Driver identity (name/photo/driving-license) is captured separately at Profile Setup
(`PUT /v1/drivers/profile`).** **Mode A/B vehicles + permits are onboarded in the Fleet Portal (fleet-svc / SCR-FP-004), not here.**
Request (multipart or JSON w/ uploaded file IDs):
```jsonc
{ "registrationNumber":"string // required, unique in active set (D-37) — Step 1/4",
  "vehicleType":"enum motorbike|three_wheeler|flex|sedan|mini_van|van|truck|mini_truck  // canonical, car→sedan (AL-09); Step 1/4. (bus/train = Mode A, not driver-app)",
  "mode":"enum C  // driver app onboards Mode C only (A/B → Fleet Portal)",
  "insuranceFileId":"ulid // Step 2/4 — insurance card/paper",
  "revenueLicenseFileId":"ulid // Step 3/4 — vehicle revenue licence",
  "vehiclePhotoFrontFileId":"ulid // Step 4/4 — number plate visible",
  "vehiclePhotoBackFileId":"ulid // Step 4/4 — number plate visible",
  "driverName":"string? // defaults from registry.driver_profiles (Profile Setup)",
  "driverPhotoFileId":"ulid? // defaults from profile" }
```
Response 201:
```jsonc
{ "vehicleId":"ulid", "status":"PENDING", "ocrJobId":"ulid", "registrationNumber":"string",
  "verification": { "vehicleDetails":"VERIFIED|PENDING_REVIEW", "insurance":"VERIFIED|PENDING_REVIEW",
    "revenueLicense":"VERIFIED|PENDING_REVIEW", "photos":"VERIFIED|PENDING_REVIEW" },
  "onboardingStatus":"incomplete|approved", "createdAt":"iso8601" }
```
Errors: |409 `registration-exists`|active dup (D-37)| · |400 `invalid-vehicle-type`| · |403 `mode-not-allowed`| (only Mode C via driver app; A/B/train → Fleet Portal / admin-bff)
Side Effects: emits `vehicle.registered`; `ocr-svc` extracts each doc (Gemini Flash 3.0 + redaction D-36) →
per-doc verdict: insurance(expiry) · revenue(no+expiry) · photos(plate matches `registrationNumber`) · vehicleDetails(entered).
A step is **`PENDING_REVIEW`** when any of its fields is **doubtful (low confidence) or driver-entered (manual)**, or — photos — **plate OCR ≠ reg-no** (AL-29/AL-30).
When **all four = VERIFIED**, registry-svc auto-sets `status=APPROVED` & `onboardingStatus=approved` (**no Verification Officer step**, user decision 6/22)
→ vehicle appears in My Vehicles as **Approved**; any `PENDING_REVIEW` field/step → Verification Officer queue (US-2.10/2.10a) and the vehicle stays **Incomplete**. Idempotent: yes. 

### registry-svc — route table   [ADAPT]/[NEW]
| Verb · Path | Auth | Tag | Resp | Notes |
|---|---|---|---|---|
| PUT `/v1/drivers/profile` | Bearer (driver) | [ADAPT] | **multipart or JSON w/ uploaded file IDs** (Δ MCS-01) — `{driverName, profilePhotoFileId, licenseFrontFileId, licenseBackFileId, nicNo?, allowedVehicleTypes?[]}` → `{driverId, status, fields:[{key,value,source,verifyStatus}]}` | **Profile Setup** (SCR-DA/DI-003a): writes registry.driver_profiles + registry.documents(kind=driving_license, vehicle-less); queues DL OCR which extracts **licenceNo, expiry, nicNo, allowedVehicleTypes** (AL-29). Any **driver-supplied** `nicNo`/`allowedVehicleTypes` (unclear scan) is stored `source='manual'` + `verifyStatus='pending'` → Verification-Officer queue (US-2.4a). **Profile photo required.** Precedes Home; no vehicle needed (Change 6/22). **Δ MCS-01: the multipart arm** carries `photo`, `licenseFront`, `licenseBack` and a per-part `…CapturedVia` (AL-43, `camera_dragcrop|gallery`) → `docs.uploads`, so the three ids the JSON arm needs can be minted; before it, no route on the platform created a `docs.uploads` row for an onboarding document and this screen could not be completed |
| GET `/v1/vehicles/{id}` | Bearer | [KEEP] | `VehicleDetail` (+ driver, status) | — |
| GET `/v1/vehicles/{id}/status` | Bearer | [ADAPT] | `{status, rejectionReason?}` | US-2.13/2.15 |
| GET `/v1/vehicles/{id}/onboarding-status` | Bearer | [ADAPT] | `{status, onboardingStatus: incomplete\|approved, nextStep: details\|insurance\|revenue\|photos\|null, steps:{details,insurance,revenue,photos: VERIFIED\|PENDING_REVIEW\|PENDING_INPUT}, fields:[{key,value,source,confidence,verifyStatus}]}` | **SCR-DA/DI-006** per-step verdicts + per-field source/verify; doubtful/manual/plate↔reg-mismatch → `PENDING_REVIEW`; all VERIFIED → auto-APPROVED & `onboardingStatus=approved`; `nextStep` drives **resume** (AL-30, Change 6/22) |
| PUT `/v1/vehicles/{id}/onboarding/{step}` | Bearer (driver) | [NEW] | step ∈ {details,insurance,revenue,photos}; body = step fields/file ids → `{stepStatus: VERIFIED\|PENDING_REVIEW, onboardingStatus, nextStep}` | **Saves one onboarding step** (SCR-DA/DI-004→004c); each save persists `registry.onboarding_steps`; resume opens `nextStep`. **Δ MCS-01: the multipart arm is implemented** — `file`/`fileBack` plus a per-part `…CapturedVia` land in `docs.uploads` in the same request as the step's fields (it was declared from the start and bound JSON only). Plate OCR ≠ reg-no (photos) or doubtful/manual field → step `PENDING_REVIEW` (AL-30, US-2.26/2.27). When the vehicle is already `approved`, a fresh `POST /v1/vehicles` starts a NEW vehicle at Step 1/4 |
| GET `/v1/vehicles/mine` | Bearer | [NEW] | `{items:[VehicleSummary]}` | multi-vehicle (US-2.8) |
| POST `/v1/vehicles/{id}/deactivate` | Bearer | [NEW] | 204 | US-2.16; removes from map |
| PUT `/v1/vehicles/{id}/driver-profile` | Bearer | [ADAPT] | `{photoUrl,name}` → 200 | US-2.12 |
| POST `/v1/vehicles/{id}/share` | Bearer | [ADAPT] | `{userId, expiresAt?}` → `{grantId}` | Mode B (US-4.1/4.2) |
| POST `/v1/vehicles/{id}/share/{grantId}/accept` | Bearer | [NEW] | 200 | sharee accepts (US-4.3b) |
| DELETE `/v1/vehicles/{id}/share/{grantId}` | Bearer | [ADAPT] | 204 | revoke → `share.revoked` (D-22) |
| GET `/v1/vehicles/{id}/subscribers` | Bearer | [NEW] | `{items:[...]}` | grantees (US-4.7) |
| DELETE `/v1/vehicles/{id}/subscribers/{userId}` | Bearer | [NEW] | 204 | Mode B unsubscribe (US-NEW.1) |
| POST `/v1/share-requests` | Bearer (passenger) | [NEW] | `{vehicleId}` → `{requestId,status}` | request Mode B access (US-4.5) |
| POST `/v1/vehicles/{id}/device` | Bearer | [NEW] | `{imei}` → `{bindingId}` | bind IMEI (US-3.1, → provisioning) |
| _(removed)_ | — | — | **`POST /v1/internal/vehicles/{id}/merchant` deleted — D-11 retired (AL-57): OnePay has one merchant account per merchant, so a per-driver bind never existed** |
| GET · PUT `/v1/drivers/payout-profile` | Bearer (driver) | [NEW] | bank/branch/account no/holder name + **the driver's own bank-app LankaQR image**; any edit re-enters `pending_verification` (**AL-58/AL-59**, mirrors `PUT /v1/fleets/{id}/payout-profile`) |
| POST `/v1/drivers/payout-profile/documents` | Bearer (driver) | [NEW] | multipart; kind: `bank_statement` \| `passbook_first_page` \| `lankaqr_code` → `docs.uploads` (**AL-58**) |

## provisioning-svc — tracker credentials & binding (`/v1/trackers`, `/v1/fleets`)   all [NEW] (T-02, T-09)

### provisioning-svc — POST /v1/trackers/bind   [NEW] (T-02)
Purpose: bind an IMEI to a vehicle and mint per-device credential. **Auth:** Bearer (owner) /
admin-bind-code + attestation. Idempotency-Key required.
Request:
```jsonc
{ "imei":"string // 15-digit, required", "vehicleId":"ulid // required",
  "method":"enum manual|qr|admin_code", "bindCode":"string?",
  "credentialType":"enum x509|psk // x509 for MQTT-capable, psk for legacy TCP" }
```
Response 201:
```jsonc
{ "bindingId":"ulid", "imei":"string", "vehicleId":"ulid",
  "credentialSerial":"string", "credential": { "type":"x509",
  "clientCertPem":"string?", "pskToken":"string?" }, "rotatesAt":"iso8601 // +90d" }
```
Errors: |409 `imei-duplicate`|anti-clone quarantine, both held (T-08, US-3.4)| · |404 `vehicle-not-found`| · |403 `not-owner`|
Side Effects: write `prov.tracker_bindings`; Redis `imei:{imei}→vehicleId`; emit `tracker.bound`.
Idempotent: yes.

### provisioning-svc — POST /v1/fleets/{fleetId}/trackers/bulk   [NEW] (T-09, US-3.2)
Purpose: bulk-bind up to **5,000 IMEIs** via CSV (fleet operator, Admin Portal). **Auth:** Bearer
(fleet-admin) + attestation. Idempotency-Key required.
Request: `multipart/form-data` — `file: text/csv` (rows `imei,registrationNumber`).
Response 202:
```jsonc
{ "jobId":"ulid", "totalRows":int, "status":"PROCESSING",
  "errorReportUrl":"string? // available when done" }
```
Errors: |400 `csv-invalid`| · |413 `too-many-rows`|>5000| · |429 `bulk-in-progress`|
Side Effects: SAGA validates rows; materialises bindings; queues credential-mint jobs; per-row error
report (NFR-43: 5k in ≤5 min). Idempotent: yes (jobId).

### provisioning-svc — route table   [NEW]
| Verb · Path | Auth | Resp | Notes |
|---|---|---|---|
| GET `/v1/trackers/{imei}` | Bearer/admin | `{binding,lastSeen,signal,battery,sats}` | US-3.12 |
| POST `/v1/trackers/{imei}/switch-source` | Bearer | `{source:mobile\|hardware}` | single publisher (US-3.6) |
| DELETE `/v1/trackers/{imei}` | admin | 204 | decommission, revoke ≤60s (US-3.8, T-12) |
| POST `/v1/internal/trackers/{imei}/rotate` | mTLS | new credential | 90d cron (US-3.5) |
| GET `/v1/internal/trackers/{imei}/validate` | mTLS (adapter) | `{valid,vehicleId}` | tracker-adapter calls per connect (T-01) |
| GET `/v1/fleets/{id}/trackers/bulk/{jobId}` | Bearer | `{status,errorReportUrl}` | poll bulk job |

**tcp-adapter interface (T-01)** (a.k.a. tracker-adapter-svc) — *not HTTP-public*; raw TCP/UDP per protocol family
(`adapter-gt06`/`adapter-jt808`/`adapter-h02`/`adapter-nmea`). Validates IMEI via
`GET /v1/internal/trackers/{imei}/validate`, decodes binary → canonical `PositionSample`, publishes to
EMQX `veh/{vehicleId}/pos/live` (or `/pos/replay`). LWT emulation on socket half-close → `status=offline`.
Downlink commands consumed from `veh/{vehicleId}/cmd` (US-3.17). See Part 3 / D6′ for frame detail.

## trip-state-svc — Mode A/B tracking sessions (`/v1/sessions`)   [ADAPT] (NY driver ride lifecycle, trimmed)
> Scope: **Mode A/B tracking only**; Mode C lifecycle is in `ride-svc`.

### trip-state-svc — POST /v1/sessions/start   [ADAPT]
Purpose: driver starts a Mode A (journey) / Mode B session. **Auth:** Bearer (driver). Idempotency-Key.
Request:
```jsonc
{ "vehicleId":"ulid", "mode":"enum A|B", "routeId":"ulid? // Mode A bus route",
  "autoEndAtDestination":bool }
```
Response 201: `{ "sessionId":"ulid", "state":"ACTIVE", "startedAt":"iso8601" }`
Errors: |409 `driver-already-live`|active-session mutex (D-03, US-9.6)| · |403 `vehicle-not-approved`|
Side Effects: Redis `lock:driver:{driverId}` SETNX + Postgres unique partial index; start MQTT publish
expectation; idle-30min + geofence-100m timers (US-5.3/5.4). **No fee (Mode A free).** Idempotent: yes.

### trip-state-svc — route table   [ADAPT]/[NEW]
| Verb · Path | Auth | Tag | Notes |
|---|---|---|---|
| POST `/v1/sessions/{id}/end` | Bearer | [KEEP] | end journey (US-5.2) |
| POST `/v1/sessions/{id}/restart` | Bearer | [NEW] | 5-min grace restart (US-5.10) |
| GET `/v1/sessions/{vehicleId}/active` | Bearer | [KEEP] | current session state (resume) |
| POST `/v1/sessions/{id}/rating` | Bearer (passenger) | [KEEP] | 1–5 + text (US-18.1) |
| POST `/v1/sessions/{id}/driver-rating` | Bearer (driver) | [NEW] | driver rates passenger (US-18.2) |
| POST `/v1/internal/sessions/{id}/auto-end` | mTLS | [NEW] | timer-fired idle/geofence end (US-5.9) |

## ride-svc — Mode C Ride Aggregate (SOLE writer; `/v1/rides`)   [NEW]/[REPLACE] (R-01)
All mutations require `Idempotency-Key`; all responses carry `version`.

### ride-svc — POST /v1/rides/request   [REPLACE] (NY Beckn search→confirm) (R-18, R-01)
Purpose: passenger/booker requests a Mode C ride (passenger | proxy | package). Idempotent on
`(passengerId, clientRequestId)`. **Auth:** Bearer (passenger) + attestation.
Request:
```jsonc
{ "clientRequestId":"ulid // required, idempotency partner (R-18)",
  "kind":"enum passenger|proxy|package // default passenger",
  "pickup": { "lat":double, "lng":double, "address":"string?" },
  "dropoff": { "lat":double, "lng":double, "address":"string?" },
  "vehicleType":"enum motorbike|three_wheeler|flex|sedan|mini_van|van  // Mode-C passenger ride set; +truck|mini_truck for package delivery (AL-09)",
  "fareEstimateToken":"string // from fare-svc, required",
  "paymentMethod":"enum cash|lankaqr|onepay|cod // cod=package only",
  "scheduledAt":"iso8601? // null=immediate",
  // proxy (P-01):
  "isProxy":"bool?", "riderName":"string?", "riderPhone":"+94...?",
  // package (P-06):
  "packageSize":"enum S|M|L?", "packageDescription":"string?",
  "recipientName":"string?", "recipientPhone":"+94...?" }
```
Response 202:
```jsonc
{ "rideId":"ulid", "state":"Requested", "version":1,
  "pickupOtp":"string? // 4-digit, package only, shown once to sender (P-07)",
  "estimatedFare": { "amountMinor":48000, "currency":"LKR", "surchargeMinor":0 } }
```
Errors: |409 `active-ride-exists`|rider already has non-terminal ride| · |403 `booking-disabled`|3 continuous cancels (US-6A.10b)| · |400 `invalid-fare-token`| · |402 `payment-method-invalid`|
Side Effects: INSERT `rides.rides` + `command_log`; outbox `ride.requested` → dispatch-svc; for proxy
registered-rider lookup (iam-svc); package → generate+hash 2 OTPs. Idempotent: yes (dual key).

### ride-svc — POST /v1/rides/{rideId}/offer/{driverId}/accept   [REPLACE] (R-02, §11.11)
Purpose: driver accepts an offer — **atomic single-winner**. **Auth:** Bearer (driver) + attestation.
Idempotency-Key + `version` echo.
Request: `{ "offerId":"ulid", "version":int }`
Response 200: `{ "rideId":"ulid", "state":"Accepted", "version":2, "ride":RideDetail }`
Errors: |409 `offer-already-accepted`|another driver won| · |410 `offer-expired`|past 15s TTL| · |402 `insufficient-wallet`|2nd-trip fee unmet (US-9.1)|
Side Effects: conditional `UPDATE rides … WHERE state∈('Matching','Offered') AND offer_expires_at>now()
AND version=:v`; row_count=1 winner; outbox `ride.accepted`; subscription-svc fee charge (2nd+ trip);
notify passenger FCM (US-6A.13). Idempotent: yes.

### ride-svc — POST /v1/rides/{rideId}/cancel   [ADAPT] (NY cancel; resolve cross-trip Rs50)
Purpose: rider/driver cancels; effect per matrix §11.12. **Auth:** Bearer. Idempotency-Key.
Request: `{ "reason":"enum RIDER_CHANGED_MIND|DRIVER_TOO_FAR|EMERGENCY|OTHER", "version":int }`
Response 200: `{ "rideId":"ulid", "state":"CancelledByRiderAfterAccept", "penalty": {"amountMinor":5000,"settledOn":"next-trip"}, "version":3 }`
Errors: |409 `version-conflict`| · |400 `illegal-transition`|
Side Effects: terminal transition; **Rs 50 penalty** accrued cross-trip (D-05, §11.7) if after accept;
3rd continuous → `booking-disabled` (US-6A.10b); driver-side → reputation hit; outbox `ride.cancelled`,
`cancellation.penalty.accrued`. Idempotent: yes.

### ride-svc — POST /v1/location-requests   [NEW] (P-02, P-13, §11.15)
Purpose: booker requests rider's live GPS as pickup. **Auth:** Bearer (booker) + attestation.
Idempotency-Key. Rate-limited (P-12).
Request: `{ "riderPhone":"+94...", "rideDraftId":"ulid?" }`
Response 202: `{ "requestId":"ulid", "state":"Pending|RiderNotRegistered", "ttl":300 }`
Errors: |429 `loc-request-rate-limited`|5/h, 30/d (P-12)| · |400 `invalid-phone`|
Side Effects: iam-svc lookup (P-03); if registered → FCM data-message to rider (notification-svc),
durable 5-min expiry timer (Quartz), outbox `location.request.issued`; booker subscribes SignalR group
`booker:{bookerId}:loc-req:{requestId}` (P-13). Idempotent: yes.

### ride-svc — package OTP & location-request sub-routes   [NEW]
| Verb · Path | Auth | Tag | Resp | Notes |
|---|---|---|---|---|
| POST `/v1/rides/{id}/offer/{driverId}/decline` | Bearer | [REPLACE] | 200 → Matching | re-offer next |
| POST `/v1/rides/{id}/arrive` | Bearer | [ADAPT] | 200 → DriverArrived | geofence/manual |
| POST `/v1/rides/{id}/start` | Bearer | [ADAPT] | `{state:InProgress}` | rider OTP (passenger) |
| POST `/v1/rides/{id}/complete` | Bearer | [ADAPT] | `{state:PaymentPending,fare}` | → fare-svc |
| POST `/v1/rides/{id}/dispute` | Bearer | [NEW] | `{ticketId}` | post-payment (E-05) |
| GET `/v1/rides/{id}` | Bearer | [KEEP] | `RideDetail` | full state |
| GET `/v1/rides/{id}/state` | Bearer | [NEW] | `{state,version,offerExpiresAt?}` | lightweight |
| GET `/v1/rides/passenger/{id}/active` | Bearer | [NEW] | `RideDetail?` | client recovery (R-18) |
| GET `/v1/rides/driver/{id}/active` | Bearer | [NEW] | `RideDetail?` | resume |
| POST `/v1/location-requests/{id}/confirm` | Bearer (rider) | [NEW] | 200 | `{lat,lng,accuracy}` (P-02) |
| POST `/v1/location-requests/{id}/decline` | Bearer (rider) | [NEW] | 200 | P-02 |
| POST `/v1/rides/{id}/package/pickup-otp` | Bearer (driver) | [NEW] | 200\|400\|423 | `{otp}`, max 5 (P-07) |
| POST `/v1/rides/{id}/package/delivery-otp` | Bearer (driver) | [NEW] | 200\|400\|423 | `{otp}` (P-07) |
| POST `/v1/rides/{id}/package/proof-photo` | Bearer (driver) | [NEW] | `{artifactId}` | multipart (P-10) |
| POST `/v1/rides/{id}/cod-collected` | Bearer (driver) | [NEW] | `{state:CashOnDeliveryCollected}` | P-08 |
| POST `/v1/internal/rides/{id}/system-cancel` | mTLS | [NEW] | — | LWT/grace (R-15,16) |
| POST `/v1/internal/rides/{id}/payment-settled` | mTLS | [NEW] | — | fare-svc terminal (R-05) |

## dispatch-svc — standby, directional, Job Board, level (`/v1/standby`, `/v1/rides/job-board`)

### dispatch-svc — POST /v1/standby/directional   [NEW] (DT-01, DT-03)
Purpose: Mode C driver sets a Directional Travel filter. **Auth:** Bearer (driver). Idempotency-Key.
Request: `{ "destination": {"lat":double,"lng":double}, "label":"string? // e.g. Home" }`
Response 201: `{ "filterId":"ulid", "expiresAt":"iso8601", "usesRemaining":int, "maxDurationSec":7200 }`
Errors: |409 `directional-limit-reached`|daily uses exhausted (default 2, US-6A.18)| · |403 `not-online`|
Side Effects: Redis `driver:directional:{driverId}` (PX=TTL) + Postgres `dispatch.directional_filters`
(`use_count++` atomic, Asia/Colombo); durable Quartz expiry timer; emits nothing until clear.
Idempotent: yes.

### dispatch-svc — GET /v1/standby/directional   [NEW] (DT-08)
Purpose: live filter state for driver UI. **Auth:** Bearer (driver).
Response 200: `{ "active":bool, "destination":{...}?, "expiresAt":"iso8601?", "timeRemainingSec":int, "usesRemaining":int }`
Side Effects: none (read). The **10-min pre-expiry reminder** (US-10.14) is pushed by notification-svc
on the Quartz pre-expiry trigger; `directional.cleared` emitted on expiry/offline/manual (DT-04, DT-08).

### dispatch-svc — route table   [NEW]
| Verb · Path | Auth | Resp | Notes |
|---|---|---|---|
| POST `/v1/standby/online` | Bearer | `{state:online}` | register presence (US-6A.1) |
| POST `/v1/standby/offline` | Bearer | 200 | clears directional (DT-04) |
| DELETE `/v1/standby/directional` | Bearer | 200 | early off, **still consumes a use** (US-6A.19) |
| GET `/v1/rides/job-board?lat=&lng=&radius=30km` | Bearer | `{items:[ScheduledRide],cursor}` | ST_DWithin (D-06, US-6A.5) |
| POST `/v1/rides/job-board/{rideId}/intent` | Bearer | 200 | post intent (US-6A.5) |
| GET `/v1/rides/scheduled/{driverId}` | Bearer | `{items}` | upcoming (US-6A.15) |
| GET `/v1/drivers/{id}/level` | Bearer | `{level:1-3}` | L1 = no Job Board (US-6A.8) |
| GET `/v1/drivers/{id}/stats` | Bearer | `{acceptanceRate,noShows,points}` | US-6A.14 |
| POST `/v1/internal/drivers/{id}/no-show` | mTLS | — | scheduler → level−1 (US-6A.7) |
| PUT `/v1/admin/dispatch/directional-config` | admin | 200 | θ_max,detour_max,progress_min,uses,duration (DT-02) |
| PUT `/v1/admin/drivers/level-config` | admin | 200 | level params (US-14.12) |

## reputation-svc — counters & gRPC (`reputation.v1` gRPC + `/v1/...` admin)   [NEW] (D-04, E-07)
gRPC service `Reputation` (mTLS internal):
```protobuf
service Reputation {
  rpc GetBlockStatus(DriverRef) returns (BlockStatus);   // OK|WARN|BOOKING_DISABLED|DELISTED
  rpc GetDriverLevel(DriverRef) returns (Level);         // 1..3 + points
  rpc ReportCancellation(CancellationEvent) returns (Ack);
  rpc ReportNoShow(NoShowEvent) returns (Ack);
  rpc ReportVehicle(VehicleReport) returns (Ack);
}
```
`dispatch-svc` calls `GetBlockStatus`/`GetDriverLevel` as a hard gate before candidate scoring.
| Verb · Path | Auth | Notes |
|---|---|---|
| GET `/v1/admin/reputation/flags` | admin | anti-collusion `fraud.suspected` (E-07) |
| POST `/v1/admin/drivers/{id}/level/restore` | admin | appeal restore (US-6A.8) |

## fare-svc — estimate, calculate, pay (`/v1/fare`)   [REPLACE] (payment hard rule; NY Juspay)

### fare-svc — GET /v1/fare/estimate   [ADAPT] (NY fareCalculator)
Purpose: upfront total fare estimate (US-8.9). **Auth:** Bearer.
Query: `fromLat,fromLng,toLat,toLng` (required), `vehicleType` (required), `kind=passenger|package`.
Response 200:
```jsonc
{ "fareEstimateToken":"string // opaque, pass to /rides/request",
  "amountMinor":48000, "currency":"LKR",
  "breakdown": { "firstKmMinor":10000, "perKmMinor":8000, "distanceKm":4.8,
    "peakSurchargePct":20, "nightSurchargePct":0 } }   // total only shown in UI (US-8.4)
```
Errors: |400 `unserviceable-area`| · |422 `route-unavailable`|

### fare-svc — POST /v1/fare/pay   [REPLACE] (NY Juspay → wallet / driver-QR / cash) (D-10, AL-57/AL-59)
Purpose: initiate in-app payment. **Auth:** Bearer (payer) + attestation. Idempotency-Key.

> **Δ AL-57/AL-59 — no ride fare is charged to a platform merchant account.** `onepay` and the
> platform-merchant `lankaqr` are **removed** as ride methods: OnePay has one merchant account per
> merchant, so a card fare could only ever land in MageRide's own account, and `lankaqr` pointed at
> `LankaQr__MerchantId` — the platform's — while crediting the driver nothing but a read-model row.
> Card acceptance is preserved one step earlier: the passenger **tops up their wallet** by card
> (`POST /v1/wallet/topup/onepay`, where MageRide *is* the payee) and pays with **`wallet`**.
Request:
```jsonc
{ "rideId":"ulid", "method":"enum cash|wallet|scan_driver_qr|cod",
  "tipMinor":"long? // E-10" }
```
Response 200:
```jsonc
{ "paymentId":"ulid", "state":"Initiated|Succeeded",   // `wallet` is TERMINAL on the spot — no Pending
  "method":"wallet", "amountMinor":50000, "surchargeMinor":0,   // no surcharge on any surviving rail
  "walletBalanceAfterMinor":125000 }                            // `wallet` only
```
Errors: |409 `payment-already-settled`| · |402 `insufficient-wallet`|the passenger's balance is short; cash and driver-QR remain offered (AL-57)|
Side Effects: payment state machine `Initiated→Pending→Succeeded/Failed/Retried/FellBackToCash`
(§11.8, D-10); driver earning posts only on terminal (R-05); proxy payer routing (P-04). Idempotent: yes.

### fare-svc — route table   [REPLACE]/[NEW]
| Verb · Path | Auth | Tag | Notes |
|---|---|---|---|
| POST `/v1/fare/calculate` | mTLS internal | [ADAPT] | final fare on complete (E-04 Kalman) |
| GET `/v1/fare/pay/{paymentId}/status` | Bearer | [NEW] | poll state (US-8.15) |
| POST `/v1/fare/pay/{paymentId}/fallback-cash` | Bearer | [NEW] | switch to cash mid-fail (US-8.15) |
| POST `/v1/fare/pay/onepay/webhook` | HMAC sig | [REPLACE] | OnePay confirm; idempotent on `provider_transaction_id` (R-19) |
| POST `/v1/fare/pay/lankaqr/confirm` | HMAC sig | [REPLACE] | LankaQR confirm |
| POST `/v1/admin/fare/refund` | admin | [NEW] | partial/full reversal (E-05) |

## subscription-svc — daily fee, vouchers, credit transfer (`/v1/fees`, `/v1/subscriptions`)   [REPLACE] (NY Juspay plans)
> **AL-01:** the credit-transfer endpoints below are **Driver-App APIs**, not portal APIs. "Reseller" is **not a role/account/capability** — any driver holding credit can transfer it by **Driver ID**; transfers move the **exact value, no commission**.

### subscription-svc — GET /v1/fees/{driverId}/today   [NEW] (US-9.1, US-9.7)
Purpose: today's daily-fee status. **Auth:** Bearer (driver).
Response 200:
```jsonc
{ "vehicleType":"three_wheeler", "dailyRateMinor":10000,
  "status":"PAID|UNPAID", "deductedMinor":10000, "tripsToday":3,
  "firstTripFree":true, "feeDate":"2026-06-12 // Asia/Colombo" }
```

### subscription-svc — route table   [REPLACE]/[NEW]
| Verb · Path | Auth | Tag | Notes |
|---|---|---|---|
| GET `/v1/fees/rates` | Bearer | [NEW] | 7-tier rates (Bus Free…Van 300) |
| POST `/v1/internal/fees/{driverId}/charge-before-trip` | mTLS | [NEW] | idempotent 2nd-trip deduct, key driverId+vehicleId+date (D-08/D-13) |
| GET `/v1/fees/{driverId}/history?from=&to=` | Bearer | [NEW] | deduction history (US-9A.6), cursor |
| POST `/v1/subscriptions/credit-transfer/request` | Bearer (driver) | [ADAPT] | `{holderDriverId,amountMinor}` — request a credit transfer from another driver by **Driver ID only** (SCR-DA/DI-023 **QR-scan path removed**, AL-34) (US-9.10, Driver App, AL-01) |
| POST `/v1/subscriptions/credit-transfer/{id}/approve` | Bearer (holding driver) | [NEW] | debit sender **exact amount**, credit recipient **exact amount** — **no commission** (US-9.13) |
| POST `/v1/subscriptions/credit-transfer/{id}/reject` | Bearer (holding driver) | [NEW] | US-9.12 |
| GET `/v1/subscriptions/credit-transfer/pending` | Bearer (holding driver) | [NEW] | incoming requests (US-9A.10) |
| POST `/v1/vouchers/purchase` | Bearer | [NEW] | bulk voucher Rs1k–10k; per-tier DB discount applied at purchase, credits buyer wallet (US-9.19) |
| POST `/v1/transfers/driver` | Bearer | [NEW] | driver→driver direct send by Driver ID, **exact value, no commission** (US-9.20/9.21) |
| PUT `/v1/admin/fees/rates` | admin | [NEW] | configure rates (US-14.4, Admin Portal) |
| PUT `/v1/admin/voucher-discount-tiers` | admin | [NEW] | set bulk-voucher commission % **per voucher value** (denomination) (US-9A.15, Admin Portal Config, AL-01) |

## wallet-svc — balance, top-up, ledger (`/v1/wallet`)   [REPLACE] (payment hard rule)

### wallet-svc — POST /v1/wallet/topup/onepay   [REPLACE]/[NEW] (US-9.18)
Purpose: in-app card/OnePay top-up. **Auth:** Bearer (driver) + attestation. Idempotency-Key.
Request: `{ "amountMinor":100000, "returnUrl":"string?" }`
Response 200: `{ "topupId":"ulid", "state":"Pending", "redirectUrl":"string", "sessionToken":"string?" }`
Errors: |400 `invalid-amount`| · |402 `gateway-error`|
Side Effects: OnePay initiate; on webhook → balanced double-entry journal credit (D-09); emits
`wallet.credited` (invalidates dispatch balance cache D-08). Idempotent: yes.

### wallet-svc — route table   [REPLACE]/[NEW]
| Verb · Path | Auth | Tag | Notes |
|---|---|---|---|
| GET `/v1/wallet/{userId}` | Bearer | [NEW] | balance + summary (read-only US-9.7) |
| GET `/v1/wallet/{userId}/transactions` | Bearer | [NEW] | cursor, PDF/CSV (US-9A.19) |
| POST `/v1/wallet/topup/lankaqr` | Bearer | [REPLACE] | returns **"Pay" deep link to bank app** (QR fallback only, AL-15) (US-9.18) |
| POST `/v1/wallet/topup/onepay/webhook` | HMAC | [REPLACE] | credit wallet |
| POST `/v1/wallet/topup/lankaqr/confirm` | HMAC | [REPLACE] | credit wallet |
| GET `/v1/wallet/{driverId}/transfers` | Bearer (driver) | [NEW] | credit-transfer history, sent & received (US-9A.11, Driver App) |
| POST `/v1/wallet/credit-transfer/initiate` | Bearer (driver) | [NEW] | driver proactively sends credit by Driver ID — exact value, no commission (US-9A.12, Driver App) |
| GET `/v1/wallet/admin/voucher-discount-tiers` | admin | [NEW] | list bulk-voucher commission % per voucher value + usage (US-9A.15, Admin Portal) |
| PUT `/v1/wallet/admin/voucher-discount-tiers` | admin | [NEW] | set bulk-voucher commission % per voucher value (denomination) (Admin Portal) |
| _(removed)_ | — | — | **`/wallet/topup/bank-transfer` + `/wallet/admin/*bank-transfer*` deleted — bank transfer is not a top-up method (AL-05)** |

> **Δ AL-57 — the wallet is now the passenger's too.** `billing.accounts.owner_type` gains
> `passenger`, and every route above is reachable by a passenger for their own account: a card
> top-up (`/topup/onepay`) is how card acceptance survives the retirement of the `onepay` ride rail,
> and `POST /v1/fare/pay {method:"wallet"}` spends it. **A passenger top-up and a driver top-up are
> the same route and the same ledger movement** — what differs is only which account the credit
> leg lands on, so there is no second top-up surface to keep in step. The driver's wallet is
> unchanged and is now also the **fare accumulation account**: fares credit it, daily fees debit it,
> and the AL-58 payout run sweeps it.

### payout-svc — the weekly driver payout run   [NEW] (AL-58)
Purpose: discharge the liability an AL-57 wallet fare creates. **Weekly full sweep — no minimum,
no holdback:** whatever the driver's balance is on run day is paid out in full.

| Verb · Path | Auth | Tag | Notes |
|---|---|---|---|
| GET `/v1/drivers/payouts` | Bearer (driver) | [NEW] | this driver's payout history — amount, status, when (SCR-DA-022a) |
| GET `/v1/admin/payouts` | finance · admin | [NEW] | every instruction, filterable by batch / status / driver (SCR-AP-006) |
| GET `/v1/admin/payouts/batches` | finance · admin | [NEW] | run history: date, instruction count, total |
| POST `/v1/admin/payouts/batches` | finance | [NEW] | run the sweep now, out of band. Idempotent on `run_date` — a second call for a date already swept is `409` |
| POST `/v1/admin/payouts/{payoutId}/retry` | finance | [NEW] | re-submit a `FAILED` instruction; the reversal has already restored the balance |
| POST `/v1/internal/payouts/{payoutId}/result` | mTLS | [NEW] | the bank origination adapter reporting `PAID` \| `FAILED`; idempotent on `provider_reference` (R-19's shape) |

Side Effects: the wallet debit (`driver_payout` journal kind) and the `billing.payouts` row commit
**together** — an instruction with no debit pays twice on retry, a debit with no instruction loses
the driver's money. `FAILED` reverses the debit under the same idempotency-key discipline (§0), so
the balance is restored exactly once and the next run picks it up.
Errors: |409 `payout-batch-exists`| · |409 `payout-not-failed`| · |422 `payout-profile-not-verified`|

> **A driver with no `verified` payout profile accrues and is never paid out** — the balance is
> retained, never lost, and they surface on the Finance exception queue. **Origination is one
> outbound port and no provider is chosen**: unconfigured, the run still records what is owed and
> announces the gap at start-up, so the liability is visible before a rail exists.
> ⚠ CEFTS/LankaPay origination needs a sponsor bank and CBSL authorisation — ADD §1.18.

## query-svc — nearby, trips, earnings (`/v1/nearby`, `/v1/trips`, `/v1/earnings`)   [REPLACE] (map hard rule)

### query-svc — GET /v1/nearby   [REPLACE] (NY Google/LTS → MapLibre + Redis GEO)
Purpose: nearby vehicles for live map. **Auth:** Bearer (passenger).
Query: `lat,lng` (required), `radius` (m, default 3000), `types=[bus,train,three_wheeler,...]`,
`modes=[A,B,C]`.
Response 200:
```jsonc
{ "vehicles": [ { "vehicleId":"ulid", "type":"bus", "mode":"A",
  "lat":double, "lng":double, "heading":int, "speed":double,
  "driverName":"string? // after accept only for C (US-7.12)",
  "etaSeconds":int?, "registrationNumber":"string?" } ],
  "asOf":"iso8601" }
```
Side Effects: none. **Visibility rules:** excludes Mode C on active hire (US-7.16) + stale/offline
(US-7.17); Mode B only if entitled (D-23). Update: live via SignalR (this is snapshot/resync).

### query-svc — route table   [REPLACE]/[ADAPT]
| Verb · Path | Auth | Tag | Notes |
|---|---|---|---|
| GET `/v1/trips/{userId}` | Bearer | [KEEP] | history, cursor (US-8.7) |
| GET `/v1/trips/{userId}/{tripId}` | Bearer | [KEEP] | detail + polyline |
| GET `/v1/earnings/{driverId}?period=today\|week\|month` | Bearer | [ADAPT] | earnings dashboard (US-9.22) |
| GET `/v1/earnings/{driverId}/sessions` | Bearer | [ADAPT] | per-session breakdown |
| GET `/v1/transport-options?toLat=&toLng=` | Bearer | [NEW] | dest options incl. **trains** (US-7.15) |
| GET `/v1/routes/{routeNumber}/buses` | Bearer | [NEW] | active buses on route (US-7.9) |

**tile-cdn / nominatim-svc** `[REPLACE]` (map hard rule): tiles served by **Cloudflare R2 + Worker**
(`GET https://tiles.mageride.lk/sl.pmtiles` range-byte; signed offline bundles MAP-09) — **not an app
API**. Geocoding: `GET /v1/geo/search?q=` (forward) / `GET /v1/geo/reverse?lat=&lng=` (reverse) →
nominatim-svc. NY Google Maps/places fully replaced.

## safety-svc — SOS, trip-share, report, block (`/v1/sos`, `/v1/trip-share`)   [ADAPT]/[NEW]

### safety-svc — POST /v1/sos   [ADAPT] (NY Sos.hs; drop Aadhaar) (D-33)
Purpose: passenger/driver SOS → SMS to emergency contact. **Auth:** Bearer + attestation. Idempotency-Key.
Request: `{ "rideId":"ulid?", "lat":double, "lng":double, "role":"passenger|driver" }`
Response 200: `{ "sosId":"ulid", "dispatchedAt":"iso8601" }`
Errors: |400 `no-emergency-contact`|
Side Effects: SMS via primary+secondary gateway **parallel** (p99 ≤5s, D-33); admin live-feed WS;
log `safety.sos_events` (US-12.11). Idempotent: yes.

### safety-svc — route table   [ADAPT]/[NEW]
| Verb · Path | Auth | Tag | Notes |
|---|---|---|---|
| GET `/v1/sos/{userId}/history` | Bearer | [NEW] | past SOS |
| POST `/v1/trip-share/{tripId}` | Bearer | [NEW] | issue share token, trip+1h, 60/min, revocable (D-34) |
| GET `/v1/trip-share/public/{token}` | none | [NEW] | public live view (no replay, D-34) |
| DELETE `/v1/trip-share/{tripId}` | Bearer | [NEW] | revoke |
| POST `/v1/reports/vehicle` | Bearer | [ADAPT] | report (US-12.5) → reputation |
| POST `/v1/drivers/{id}/block` | Bearer | [NEW] | block driver (US-12.10) |
| DELETE `/v1/drivers/{id}/block` | Bearer | [NEW] | unblock |

## support-svc — FAQ & tickets (`/v1/support`)   [NEW] (Epic 16)
| Verb · Path | Auth | Tag | Notes |
|---|---|---|---|
| GET `/v1/support/faq?lang=si\|ta\|en&category=` | Bearer | [NEW] | FAQ list (US-16.1) |
| GET `/v1/support/faq/{articleId}` | Bearer | [NEW] | article |
| POST `/v1/support/tickets` | Bearer | [NEW] | `{category,description,tripId?,screenshotFileId?}` (US-16.2) |
| GET `/v1/support/tickets/{userId}` | Bearer | [NEW] | user tickets, cursor |
| GET `/v1/support/tickets/{userId}/{ticketId}` | Bearer | [NEW] | detail + admin response |

## content-svc — localised templates (`/v1/content`)   [NEW] (D-26)
| Verb · Path | Auth | Notes |
|---|---|---|
| GET `/v1/content/templates/{key}?lang=si\|ta\|en` | mTLS internal | notification template render (Si/Ta/En) |
| GET `/v1/content/broadcasts?lang=` | Bearer | active in-app announcements (US-14.8) |
| PUT `/v1/admin/content/{key}` | admin | versioned template edit (approval workflow) |

## voip-svc — call signalling (`/v1/voip`)   [NEW] (D-24/25)
### voip-svc — POST /v1/voip/token   [NEW]
Purpose: mint LiveKit signalling token scoped to a ride. **Auth:** Bearer + attestation.
Request: `{ "rideId":"ulid" }`
Response 200: `{ "roomName":"ride_{id}", "token":"jwt // LiveKit, expires at trip end", "wsUrl":"wss://...", "callee":"rider|driver" }`
Side Effects: token binds driver↔**rider** (not booker, P-05); on VoIP fail the client prompts **"Call
normally instead?"** → direct `tel:` dial of the counterparty number from `GET /v1/rides/{id}`
(~~masked-number SMS relay (D-25)~~ removed — **AL-48**). Errors: |403 `not-ride-participant`| · |409 `ride-terminal`|

## notification-svc — push (`/v1/notify`; mostly internal)   [ADAPT] (NY TriggerFCM)
| Verb · Path | Auth | Tag | Notes |
|---|---|---|---|
| POST `/v1/notify/register-token` | Bearer | [ADAPT] | register FCM/APNs token |
| PUT `/v1/notify/preferences` | Bearer | [NEW] | per-type prefs (US-10.7) |
| POST `/v1/internal/notify/send` | mTLS | [ADAPT] | FCM HTTP v1 batch / APNs HTTP/2 (D-27); dispatch offers high-priority (E-01) |

## fleet-svc (**Phase 1**, AL-03) — Fleet Portal `fleet.mageride.lk` (`/v1/fleets`)   [NEW]
**Auth:** Bearer (Fleet Owner / org-scoped `fleet_role` owner|manager|viewer; Email+Password/Google/Apple). Org must be APPROVED (Verification Officer) before non-read ops.
| Verb · Path | Auth | Notes |
|---|---|---|
| POST `/v1/fleets` | Bearer (fleet_owner) | register org → status PENDING (verification-gated, US-13.A7) |
| POST `/v1/fleets/{id}/members` | owner | provision Manager/Viewer sub-users (US-13.A5) |
| POST `/v1/fleets/{id}/vehicles` · DELETE `.../{vehicleId}` | owner/manager | fleet vehicles (Mode A/B only — **no Mode C**) |
| POST `/v1/fleets/{id}/vehicles/bulk` | owner/manager | bulk CSV onboarding (US-13.1) |
| POST `/v1/fleets/{id}/assignments` · DELETE | owner/manager | driver↔vehicle assign/revoke (US-13.2/13.8) |
| POST `/v1/fleets/{id}/trackers/bind` | owner/manager | ST-901 bind + auto-session (US-13.12) |
| POST `/v1/fleets/{id}/schedules` | owner/manager | per-vehicle scheduled rides + not-started alarm (US-13.11) |
| GET `/v1/fleets/{id}/map` | any sub-role | scoped live positions (row-level security, US-3.24/13.3) |
| GET `/v1/fleets/{id}/health` | any sub-role | tracker health rollup (US-3.13, fleet-health-svc) |
| GET `/v1/fleets/{id}/analytics` | any sub-role | per-vehicle trip/usage analytics (US-13.4) |
| GET `/v1/fleets/{id}/billing` · POST `/wallet/topup` | owner | monthly per-Mode-B-vehicle invoice + fleet-wallet top-up (US-13.10/10b) |
| GET `/v1/fleets/{id}/alerts` | any sub-role | route-deviation / geofence (**Phase 3**, US-13.5) |

## admin-bff — operator console (`/v1/admin`)   [ADAPT] (NY dashboards; + audit D-35)
Every mutation passes an **audit interceptor** → `audit.events` (D-35, US-19.3).
| Verb · Path | Auth | Tag | Notes |
|---|---|---|---|
| GET `/v1/admin/dashboard` | admin | [ADAPT] | platform metrics (US-14.6) |
| POST `/v1/admin/vehicles/{id}/approve` · `/reject` | admin / verification_officer | [ADAPT] | + reason (US-2.9/2.15); **no merchant bind — D-11 retired (AL-57)**. **Approve blocked while any field `verifyStatus='pending'`** (US-2.10a) |
| GET `/v1/admin/onboarding/queue` | verification_officer | [NEW] | flagged drivers/vehicles with per-field `{key,value,source,confidence,verifyStatus}` + per-step status (SCR-AP-003, US-2.10a) |
| POST `/v1/admin/onboarding/{vehicleOrDriverId}/fields/{fieldKey}/confirm` | verification_officer | [NEW] | confirm a flagged field as-is → `verifyStatus='confirmed'` (audited); NIC/allowed-types/insurance/revenue/reg-mismatch (US-2.4a/2.10a) |
| PATCH `/v1/admin/onboarding/{vehicleOrDriverId}/fields/{fieldKey}` | verification_officer | [NEW] | edit & confirm `{value}` → `verifyStatus='confirmed'` (audited). When no field remains pending the step → VERIFIED and the vehicle may be approved (US-2.10a) |
| POST `/v1/admin/vehicles/{id}/suspend` · `/admin/drivers/{id}/suspend` | admin | [ADAPT] | US-14.3 |
| POST `/v1/admin/trains` · PUT/DELETE `.../{id}` | admin | [NEW] | **train admin-only** Mode A (US-2.17/2.18) |
| PUT `/v1/admin/fares/tariffs` | admin | [REPLACE] | Mode C tariffs, peak/night (US-14.4) |
| POST `/v1/admin/announcements` | admin | [ADAPT] | broadcast (US-14.8) |
| POST `/v1/admin/drivers/wallet/{id}/reverse-fee` | admin | [NEW] | fee reversal (US-14.11) |
| GET `/v1/admin/support/tickets` · POST `.../{id}/resolve` | admin | [NEW] | ticket queue (US-16.3) |
| GET `/v1/admin/audit-log` | admin | [NEW] | audit (US-19.3) |
| POST `/v1/admin/reports/{id}/resolve` | admin | [ADAPT] | 3 confirmed → delist (US-12.6) |

## pdpa-svc (via admin-bff) — data rights (`/v1/pdpa`)   [NEW] (E-06)
| Verb · Path | Auth | Notes |
|---|---|---|
| POST `/v1/pdpa/export` | Bearer | 202 `{requestId,dueBy}` — 30d (US-1.8) |
| POST `/v1/pdpa/erasure` | Bearer | 202 `{requestId,dueBy,holdReasons[]}` |
| GET `/v1/pdpa/{requestId}` | Bearer | status + signed download |
| POST `/v1/admin/pdpa/{id}/fulfill` · `/reject` | admin | fulfilment |

## version-check — gateway gate (`/v1/version`)   [NEW] (D-31)
### version-check — GET /v1/version/check   [NEW]
Query: `platform=android|ios` (required), `current=<semver>` (required).
Response 200: `{ "updateRequired":bool, "latestVersion":"semver", "updateUrl":"string", "isMandatory":bool }`
Note: also enforced **transparently** by gateway middleware reading `X-App-Version` → **`426 Upgrade
Required`** on any below-floor request (US-17.1/17.2).

---

# PART 3 — REAL-TIME ENDPOINTS

## 3.1 SignalR hub — `/hubs/live` (passenger fan-out; resolves NY "no WebSocket")
Auth: access JWT in `access_token` query (SignalR convention). Backplane: Redis (MVP) / Redpanda (scale).

**Client→Server (C→S):**
| Method | Args | Effect |
|---|---|---|
| `JoinGeocells(cells:string[])` | H3 res-7 cell IDs (self + ring(2), R-06) | subscribe to live vehicle frames; Mode B entitlement checked (D-23) |
| `LeaveGeocells(cells:string[])` | — | unsubscribe |
| `SubscribeRide(rideId)` | ulid | live driver position for own ride (US-6A.12) |
| `SubscribeLocRequest(requestId)` | ulid | booker awaits rider confirm (P-13) |

**Server→Client (S→C):**
| Event | Payload | When |
|---|---|---|
| `VehiclePositions` | `[{vehicleId,lat,lng,heading,speed,type,mode}]` | per-cell batch, 2–8s (US-7.3) |
| `VehicleRemoved` | `{vehicleId,reason:stale\|offline\|engaged}` | US-7.16/7.17 |
| `RideStateChanged` | `{rideId,state,version,driver?,etaSeconds?}` | ride aggregate transition (Appendix B.2) |
| `DriverPosition` | `{rideId,lat,lng,heading}` | assigned-ride live (US-6A.12) |
| `LocationRequestResolved` | `{requestId,state:Confirmed\|Declined\|Expired,geo?}` | proxy round-trip (P-02,P-13) |
| `ShareRevoked` | `{vehicleId}` | Mode B unsubscribe → `RemoveFromGroupAsync` (D-22) |
| `PackageStatus` | `{rideId,status:PickedUp\|InTransit\|Delivered}` | US-20.7 |

Push events (FCM/APNs, not socket) for backgrounded apps: `RIDE_OFFER` (high-priority/silent E-01),
`DRIVER_ASSIGNED`, `DRIVER_ARRIVED`, `RIDE_CANCELLED`, `PAYMENT_CONFIRMED`, `SCHEDULED_REMINDER`,
`DIRECTIONAL_EXPIRING` (10-min, DT-08/US-10.14), `LOW_BALANCE`, `location_request`, `package_*`, `SOS_*`.

## 3.2 MQTT topics (EMQX) referenced by APIs — full schema in D6′
Device ingest (replaces NY Kafka/external-LTS). Per-device JWT/X.509 auth; ACL = PUB own
`veh/{vehicleId}/*` only; per-vehicle **5 msg/s** ceiling (D-17).
| Topic | Dir | Notes |
|---|---|---|
| `veh/{vehicleId}/pos/live` | device→broker | live GPS, QoS1 (adaptive cadence US-5.5) |
| `veh/{vehicleId}/pos/replay` | device→broker | offline backlog, monotonic `seq`, rate-limited (R-17, US-15.1) |
| `veh/{vehicleId}/status` | broker (LWT) | `online`/`offline` → dispatch-svc, trip-state-svc, fleet-health (R-15, T-04) |
| `veh/{vehicleId}/cmd` | broker→device | server cadence hints / tracker downlink (R-07, US-3.17) |

## 3.3 No MQTT-as-realtime-out / no Beckn (resolved)
Resolves NY `[UNVERIFIED]`: passenger realtime-out = **SignalR** (not FCM-poll); device ingest =
**MQTT** (real, replacing NY's Kafka+external LTS); **no Beckn/ONDC gateway** (direct ride-svc); **APNs
present** (E-01), unlike NY (FCM/gRPC only).

---

## Traceability Addendum

| URD US-ID | URD Epic | D3′ section/endpoint | Tag | ADD §/Item | Notes |
|---|---|---|---|---|---|
| US-1.1/1.10/1.11 | 1 | iam POST /auth/otp/* , /refresh | [ADAPT] | §6 iam, D-29/32 | +94 OTP, JWT, device revoke |
| US-1.5/1.7/1.8 | 1 | iam /users/me, DELETE /users/me | [KEEP]/[NEW] | E-06 | profile, logout, delete |
| US-2.1–2.5,2.12 | 2 | registry POST /vehicles | [ADAPT] | D-36/37 | reg + OCR + photo |
| US-2.8/2.13/2.16 | 2 | registry /vehicles/* | [ADAPT]/[NEW] | §6 registry | multi/status/deactivate |
| US-2.17/2.18 | 2 | admin-bff POST /admin/trains | [NEW] | §6 admin | train admin-only |
| US-3.1/3.2/3.5/3.6/3.8 | 3 | provisioning /trackers/* , /fleets/bulk | [NEW] | T-02/08/09 | tracker plane |
| US-3.9/3.10 | 3 | tcp-adapter (Part 3 MQTT) | [NEW] | T-01, R-17 | ingest + replay |
| US-4.1–4.5,4.7,NEW.1 | 4/10 | registry /share* , /share-requests | [ADAPT]/[NEW] | D-22/23 | Mode B share/unsub |
| US-5.1–5.4,5.10 | 5 | trip-state /sessions/* | [ADAPT]/[NEW] | §6 trip-state | Mode A journey |
| US-6A.1 | 6A | dispatch /standby/online | [NEW] | R-08 | standby |
| US-6A.2/6A.3 | 6A | ride accept (15s atomic) | [REPLACE] | R-02, §11.11 | single-winner |
| US-6A.4/6A.5/6A.8 | 6A | dispatch /rides/scheduled, /job-board | [NEW] | D-06 | Job Board, level |
| US-6A.6/6A.7/6A.14 | 6A | dispatch /drivers/{id}/level,stats; reputation gRPC | [NEW] | D-04 | Driver Level |
| US-6A.9/6A.10/6A.10b | 6A | ride POST /rides/{id}/cancel | [ADAPT] | D-05, §11.7/12 | Rs50, 3-cancel disable |
| US-6A.16 | 6A | voip POST /voip/token | [NEW] | D-24/25, P-05 | VoIP rider not booker |
| US-6A.17–6A.23 | 6A | dispatch /standby/directional (GET/POST/DELETE) | [NEW] | DT-01..08 | Directional |
| US-7.1–7.4,7.16,7.17 | 7 | query GET /nearby ; SignalR /hubs/live | [REPLACE] | §6 query/fanout | map, visibility |
| US-7.7/7.9/7.15 | 7 | query /nearby?types, /routes, /transport-options | [REPLACE]/[NEW] | — | filter, trains |
| US-8.2/8.4/8.9 | 8 | fare GET /fare/estimate ; ride POST /rides/request | [REPLACE] | §6 fare | upfront estimate |
| US-8.7/8.8 | 8 | query /trips, /earnings | [KEEP]/[ADAPT] | — | history, driver fare |
| US-8.10/8.11/8.15 | 8 | fare POST /fare/pay , /status, /fallback-cash | [REPLACE] | D-10, §11.8 | OnePay/LankaQR/Cash |
| US-8.16–8.21 | 8 | ride /rides/request(proxy), /location-requests/* | [NEW] | P-01..05, §11.15 | proxy + loc-request |
| US-9.1/9.4/9.6/9.7 | 9 | subscription /fees/* | [REPLACE]/[NEW] | D-08/13 | daily fee, first-free |
| US-9.9 | 9 | notification (LOW_BALANCE) | [NEW] | §6 notif | push |
| US-9.10–9.17 | 9 | subscription credit-transfer/* (Driver App, AL-01) | [NEW] | §11.6, D-09 | driver-to-driver, exact value, no commission |
| US-9.18/9.19/9.20/9.21 | 9 | wallet /topup/* (OnePay/LankaQR only) ; subscription /vouchers,/transfers | [REPLACE]/[NEW] | §11.5, AL-05 | top-up (no bank transfer), vouchers |
| US-9.22/9.23 | 9 | query /earnings ; support /tickets | [ADAPT]/[NEW] | US-14.11 | summary, fee refund |
| US-9A.1–9A.19 | 9A | wallet/subscription (in-app); admin /voucher-discount-tiers | [ADAPT] | AL-01/05 | **in-app, no bank transfer; no per-transfer commission** |
| US-13.* | 13 | fleet-svc /v1/fleets/* (Fleet Portal, Phase 1) | [NEW] | AL-03 | org, vehicles, assign, schedule, map, billing |
| US-22.* | 22 | iam /profile (saved addresses, default payment) | [NEW] | AL-14 | passenger settings |
| US-10.x | 10 | notification /notify/* | [ADAPT] | DT-08, E-01 | push, reminders |
| US-12.1/12.5/12.8/12.10/12.11 | 12 | safety /sos, /reports, /drivers/{id}/block | [ADAPT]/[NEW] | D-33/34 | SOS, report, block |
| US-14.4/14.8/14.11/14.12/14.13 | 14 | admin-bff /admin/* | [ADAPT]/[NEW] | D-35 | config, audit |
| US-15.1 | 15 | tracker-adapter /pos/replay (MQTT) | [NEW] | R-17 | offline replay |
| US-16.1/16.2/16.3 | 16 | support /faq, /tickets ; admin /support | [NEW] | §6 support | FAQ + tickets |
| US-17.1/17.2 | 17 | version-check /version/check + 426 gate | [NEW] | D-31 | app update |
| US-18.1/18.2 | 18 | trip-state /sessions/{id}/rating,/driver-rating ; ride | [KEEP]/[NEW] | — | ratings |
| US-19.3 | 19 | admin-bff /admin/audit-log | [NEW] | D-35 | audit |
| US-20.1–20.11 | 20 | ride /rides/request(package), /package/* , cod-collected | [NEW] | P-06..08,§11.16 | package OTP/COD |

**Coverage:** every ADD §6 service → ≥1 endpoint row above; every URD P0 story needing an API → ≥1 row.

## Mandatory ADD Critique-Item Coverage (D3′ scope)

| Item | Where | ✅/❌ |
|---|---|---|
| **D-04** reputation-svc gRPC (block_status, driver_level) | reputation-svc `Reputation` proto | ✅ |
| **D-06** Job Board ST_DWithin | dispatch `GET /rides/job-board?radius=30km` | ✅ |
| **D-08** wallet 5s-TTL cache + degraded rule | subscription `charge-before-trip` + ride accept `insufficient-wallet` | ✅ |
| **D-10** payment state machine in API | fare `POST /fare/pay` (Initiated→…→FellBackToCash) | ✅ |
| ~~**D-11** OnePay merchant onboarding~~ **RETIRED (AL-57)** | no per-driver OnePay merchant exists; replaced by `GET·PUT /v1/drivers/payout-profile` (AL-58) + the weekly payout run | ✅ |
| **D-24** voip-svc tokens | voip `POST /voip/token` | ✅ |
| **D-26** content-svc localised templates | content-svc `/v1/content/*` | ✅ |
| **D-29** 30-min RS256 access + refresh | §0 conventions; iam `/auth/refresh` | ✅ |
| **D-30** attestation middleware | §0 conventions; `X-Attestation` on sensitive routes | ✅ |
| **D-31** X-App-Version → 426 | version-check + gateway gate | ✅ |
| **D-34** trip-share token scoping | safety `/trip-share/*` (trip+1h, 60/min, revocable) | ✅ |
| **D-35** admin-bff audit interceptor | admin-bff (every mutation → audit.events) | ✅ |
| **R-01** ride-svc endpoints | ride-svc full section | ✅ |
| **R-14** idempotent ride commands | §0 Idempotency-Key on all ride mutations | ✅ |
| **R-18** idempotent POST /rides/request | ride-svc request (clientRequestId dual key) | ✅ |
| **T-01** tcp-adapter interface | tcp-adapter interface note + MQTT topics | ✅ |
| **T-02** provisioning mint/bind | provisioning `POST /trackers/bind` | ✅ |
| **T-09** bulk tracker CSV | provisioning `POST /fleets/{id}/trackers/bulk` | ✅ |
| **DT-01** POST /standby/directional | dispatch directional section | ✅ |
| **DT-08** pre-expiry reminder trigger | dispatch `GET /standby/directional` + DIRECTIONAL_EXPIRING push | ✅ |

All in-scope items ✅ — **document NOT `[INCOMPLETE]`.**

---

## Verification & Caveats Summary

- Endpoints grouped by MageRide service; full request/response JSON for trip-critical/`[NEW]` endpoints,
  compact route tables (verb·path·auth·tag) for the remainder, per the prompt's "mapped → tag,
  MageRide-only → NEW + full spec" rule.
- **`[DELTA:HASKELL]` resolved:** Servant type-routes → .NET 10 minimal-API groups; opaque token → RS256
  JWT + opaque rotating refresh (D-29); error records → RFC 7807; EulerHS flows → service handlers;
  `/v2`·`/ui` + version headers → `/v1` + `X-App-Version` gate.
- **`[DELTA:INDIA]` resolved:** ₹→Rs (integer minor units), +91→+94, Hindi/Kannada/Tamil/Telugu→Si/Ta/En;
  no Aadhaar/PAN/GST/UPI/Beckn/ONDC endpoints. **`[DELTA:JUSPAY]` resolved:** all payment/payout →
  OnePay/LankaQR/Cash/COD with persisted state machine (D-10) and double-entry ledger (D-09).
- **Hard rules honoured:** payment endpoints `[REPLACE]`/`[NEW]` (fare-svc, wallet-svc, subscription-svc
  — no Juspay); map/tile endpoints `[REPLACE]` (query `/nearby`, tile-cdn PMTiles, nominatim — no Google).
- **Phase-A `[UNVERIFIED]` (7) resolved:** error envelope = RFC 7807; retry = idempotency-key replay;
  WebSocket = SignalR `/hubs/live` exists; FCM payloads = §3.1 event table; LTS = in-repo MQTT pipeline
  (not external); MQTT = real EMQX topics (§3.2); APNs = present (E-01).
- Real-time: SignalR hub methods (C→S/S→C) + MQTT topics enumerated; full frame/QoS detail deferred to
  D6′ as scoped.

---

## Δ Addendum — Discussion 2026-06-21 (API contracts, ADD v2.7 §1.9)

All endpoints follow the existing conventions (RS256 JWT with `role`/`fleet_role` claims, deny-by-default RBAC, RFC 7807 errors, `Idempotency-Key` on writes).

### transit-svc (NEW — AL-18; items 3, 4, 5, 6)
```
GET  /transit/options?fromLat&fromLng&toLat&toLng   # ALL direct GTFS routes (route_no, headsign/desc, shape) + transit options
GET  /transit/routes/{routeId}                      # route detail + shape polyline + nearest halts
GET  /geo/parse-maps-link?url=                       # resolve a Google Maps URL (incl. short links) → {lat,lng} for "Paste link"
POST /admin/transit/gtfs-import                      # admin: import/refresh GTFS dataset  [admin]
```

### fare-svc (item 18, AL-22)
```
POST /fare/pay/scan-driver-qr   # complete fare by scanning the driver's QR (printed/on-screen/sticker); body: {rideId, qrPayload}
```
`POST /fare/pay` `method` enum is **`cash | wallet | scan_driver_qr | cod`** (AL-57/AL-59: `onepay` and platform-merchant `lankaqr` removed).

### subscription-svc — Mode B subscriptions, requests & payments (Epic 23; items 8,15,16,17)
```
POST   /mode-b/{vehicleId}/access-requests                  # passenger requests access (Vehicle ID pre-filled from marker)   [passenger]
GET    /mode-b/{vehicleId}/access-requests                  # per-vehicle pending requests (name, mobile, PAX id)            [driver/owner]
POST   /mode-b/access-requests/{id}/accept                  # accept → grant + start subscription                            [driver/owner]
POST   /mode-b/access-requests/{id}/reject                  # reject                                                          [driver/owner]
GET    /mode-b/subscriptions/{passengerId}                  # passenger subscriptions (paid/free, fare, next_due, status)    [passenger]
POST   /mode-b/subscriptions/{id}/unsubscribe              # unsubscribe → revocation push; loses visibility (item 17)      [passenger]
DELETE /mode-b/{vehicleId}/subscribers/{subId}            # OWNER deletes a muted/unsubscribed subscriber (hard-delete)    [owner]
PUT    /mode-b/{vehicleId}/subscribers/{subId}/fare        # set/override per-subscriber monthly fare (item 16f)            [owner]
GET    /mode-b/{vehicleId}/subscribers                      # roster (fare, cycle, this-month status, muted flag)            [owner/driver]
POST   /mode-b/subscriptions/{id}/pay                      # init payment: lankaqr_deeplink|lankaqr_scan|online_transfer|cash  [passenger]
                                                           #   AL-59: `onepay` REMOVED — payTo is the fleet OWNER's account (AL-49) and OnePay would land it in MageRide's
POST   /mode-b/payments/{paymentId}/transfer-slip          # upload transfer screenshot → pending_verification (item 16e)   [passenger]
POST   /mode-b/payments/{paymentId}/confirm                # OWNER confirms transfer slip → paid (item 16f)                 [owner]
POST   /mode-b/{vehicleId}/subscribers/{subId}/mark-cash   # OWNER marks cash received → paid (item 16f)                    [owner]
POST   /mode-b/pay/onepay/webhook                          # OnePay confirmation (subscription)
POST   /mode-b/pay/lankaqr/confirm                         # LankaQR confirmation (subscription)
GET    /mode-b/subscriptions/{id}/payments                 # passenger payment history (SCR-PA-025b, item 16h)              [passenger]
GET    /mode-b/{vehicleId}/subscribers/{subId}/payments    # owner per-subscriber ledger (SCR-FP-012, item 16i)            [owner]
```

### fleet-svc (Fleet Portal proxies, RLS-scoped to org; items 15,16,17)
```
PUT    /fleets/{fleetId}/vehicles/{vehicleId}/classification          # set Mode B Paid/Free + default monthly fare (item 16b)
GET    /fleets/{fleetId}/vehicles/{vehicleId}/requests                # incoming requests (item 15)
POST   /fleets/{fleetId}/vehicles/{vehicleId}/requests/{id}/accept|reject
GET    /fleets/{fleetId}/vehicles/{vehicleId}/subscribers             # roster (item 16)
PUT    /fleets/{fleetId}/vehicles/{vehicleId}/subscribers/{subId}/fare
POST   /fleets/{fleetId}/vehicles/{vehicleId}/subscribers/{subId}/mark-cash
POST   /fleets/{fleetId}/payments/{paymentId}/confirm
DELETE /fleets/{fleetId}/vehicles/{vehicleId}/subscribers/{subId}     # delete muted/unsubscribed (item 17)
GET    /fleets/{fleetId}/vehicles/{vehicleId}/subscribers/{subId}/payments
```

### iam-svc (items 7, 1, 10)
```
GET    /me/saved-addresses                 # list saved addresses
POST   /me/saved-addresses                 # body: {label, line1, line2?, line3?, lat, lng, isHome?, isWork?} (item 7)
PUT    /me/saved-addresses/{id}            # edit
DELETE /me/saved-addresses/{id}            # delete
PUT    /me/prefs/language                  # {language: si|ta|en} (onboarding + Settings only; item 1/10)
```

## Δ Addendum — Discussion 2026-06-28 (API contracts, ADD v2.9 §1.11)

> New/changed endpoints for ADD v2.9 §1.11 (AL-36…AL-43) / URD v2.5 Epic 24. All `/admin/*` reads are RBAC-gated (deny-by-default) and write a read-access audit event; PII fields are role-masked. Money is integer minor units.

### iam-svc — Admin login MFA removed (item 5, AL-37)
```
POST /admin/auth/login            # {email,password} | Google OIDC code → {accessToken, refreshToken}
                                  #   NO mfaChallenge step; 2FA/TOTP enrolment + /admin/auth/mfa/verify REMOVED (US-24.5)
                                  #   compensating: failed-attempt lockout + optional IP allow-list on internal roles
```

### admin-bff — Dashboard statistics filter (item 7, AL-38)
```
GET /admin/dashboard/stats?period={today|week|month|custom}&from=YYYY-MM-DD&to=YYYY-MM-DD   [admin, scoped]
    → { period, range:{from,to},
        kpis:{ completedTrips, grossFareMinor, newRiders, newDrivers, dailyFeeRevenueMinor },
        deltaVsPrev:{ completedTripsPct, grossFarePct, ... },
        live:{ onlineDrivers, pendingVerifications, openTickets } }   # Asia/Colombo; live block real-time (US-24.7)
GET /admin/dashboard/stats.csv?...        # CSV export of the filtered figures
```

### admin-bff — Verification split + document viewer (item 8, AL-39)
```
GET /admin/verification/queues/driving-license          [verification] → [{driverId,name,submittedAt,flaggedFields[],status}]
GET /admin/verification/queues/vehicle-registration     [verification] → [{vehicleId,regNo,ownerDriverId,flaggedFields[],status}]
GET /admin/verification/queues/fleet-org                [verification] → [{orgId,name,kycStatus,vehicleCount,status}]
GET /admin/verification/{driverId|vehicleId}            [verification]
    → { subject, fields:[{key,value,source,confidence,verifyStatus}],
        documents:[{docId, kind, thumbUrl, fullUrl, capturedVia}] }    # signed URLs (US-24.8)
GET /admin/verification/org/{orgId}                     [verification] → { kyc{...}, documents:[...] }
GET /admin/documents/{docId}                            [verification|admin|support]
    → 302 signed object-storage URL; emits DOC_VIEW audit event (US-24.8)
#  Confirm / Edit&confirm / Approve / Reject endpoints unchanged (PUT /admin/verification/{id}/fields/{key}, /approve, /reject)
```

### admin-bff — Passenger directory (item 9, AL-40)
```
GET /admin/passengers?name=&mobile=&id=&email=&page=        [support|admin|auditor]
    → [{passengerId,name,mobileMasked,trips,joinedAt,status}]
GET /admin/passengers/{id}                                  [support|admin|auditor]   # emits PII_READ audit
    → { profile{mobile,email,joinedAt,rating,defaultPay,sosContacts},
        trips:[...], payments:[...], packages:[...], disputes:[...] }   # tabbed read-models (US-24.9)
```

### admin-bff — Driver directory (item 10, AL-41)
```
GET /admin/drivers?name=&mobile=&id=&nic=&regNo=&level=&status=verified&page=   [support|admin|finance|auditor]
    → [{driverId,name,mobileMasked,vehicles[],level,trips,status}]
GET /admin/drivers/{id}                                                          # emits PII_READ audit
    → { profile{mobile,nic,joinedAt,rating,walletMinor,level,points}, vehicles:[...],
        trips:[...], walletLedger:[...], dailyFee:[...], creditTransfers:[...], reports:[...] }  # (US-24.10)
#  Reversals remain Finance-only via existing POST /admin/drivers/wallet/{id}/reverse-fee
```

### admin-bff — Vehicle directory (item 11, AL-42)
```
GET /admin/vehicles?regNo=&id=&type=&mode=&ownerMobile=&fleetOrg=&status=&page=   [support|admin|finance|auditor]
    → [{vehicleId,type,mode,owner,regNo,trips,status}]
GET /admin/vehicles/{id}
    → { info{type,regNo,mode,owner,insuranceExpiry,revenueLicenceExpiry,tracker},
        documents:[{docId,kind,thumbUrl,fullUrl}],            # → /admin/documents/{docId} viewer
        trips:[...], earnings:[...], dailyFee:[...], reports:[...] }   # (US-24.11)
```

### dispatch-svc / passenger client (items 1,2,3,4, AL-36)

> Scheduled rides are owned by **`dispatch-svc`** over **`dispatch.scheduled_rides`** (ADD §9.1, D4' §6,
> `server_db_schema.md` §6). There is no `scheduling-svc` and no `scheduling` schema — the earlier
> heading was a naming slip, corrected 2026-07-26.

```
POST /v1/rides/schedule           # now REQUIRES destLat/destLng (the "location to go"); pickup defaults to current GPS,
                                  #   editable; 400 if destination missing (US-24.2)
GET  /v1/rides/history            # each completed trip row now returns driver {name, mobileMasked, callTypesAvailable[]}
                                  #   for the post-trip Call action (US-24.4)
POST /v1/calls/start              # {rideId, calleeRole, callType: free_voip}
                                  #   free_voip → WebRTC/CallKit session (US-24.3)
                                  #   ⚠ SUPERSEDED BY AL-48 (Δ 2026-07-05 #2): the `normal_masked` value and the masked
                                  #   PSTN bridge are REMOVED. "Normal call" is a client-side tel: dial of the
                                  #   counterparty MSISDN returned post-accept in GET /v1/rides/{id} — no server
                                  #   round-trip. See "Calling — masking removed (items 2–4, AL-48)" below.
```
> Driver onboarding image uploads (item 6, AL-43) use the **same upload contract** — the camera drag-crop scanner (SCR-DA/DI-005) only changes the client capture/crop; the perspective-corrected image is posted to the existing `PUT /v1/vehicles/{id}/onboarding/{step}` file field, improving OCR confidence.

## Δ Addendum — Discussion 2026-07-05 (Passenger Web subview `public-bff`, ADD v3.0 §1.12)

> New endpoint family for ADD v3.0 §1.12 (AL-44…AL-46) / URD v2.6 Epic 25. Serves the six `SCR-WT` pages at `passenger.mageride.lk`. **No Bearer auth — the `trip_share_tokens` token IS the credential** (single ride/package, scope-shaped response, TTL-bounded, D-34/P-09). Rate-limited per token **and** per IP (Redis token-bucket); every hit updates `last_access_at`/`access_count`. Errors uniform across the family: |404 `token-unknown`| · |410 `token-expired-or-revoked`| · |429 `rate-limited`|. No PII beyond the scope's need (P-02/P-09): recipient scope never sees the sender's number in clear; proxy scope never sees the booker's payment instruments.

### public-bff — token-scoped web tracking (`/public/track`)   [NEW] (AL-44, US-25.2)
```
GET  /public/track/{token}                       # snapshot, shaped by token scope:
     # scope=package_recipient → { kind:"package", status: PickupPending|PickedUp|InTransit|Delivered,
     #     driver:{name,photo,vehicleType,regNo}, position:{lat,lng,ts}, deliveryOtp:"1234",
     #     dropoff:{addr}, senderNameMasked }                                   (US-20.5, P-07)
     # scope=proxy_rider       → { kind:"ride", state, driver:{name,photo,vehicleType,regNo},
     #     etaMin, startOtp:"5678", route:{polyline}, fare:{totalMinor, paidBy:booker|cash_due} }   (US-8.21/8.22)
     # scope=pickup_confirm    → { kind:"pickup_confirm", bookerFirstName, suggestedPin:{lat,lng},
     #     expiresAt, ttlRemainingSec }                                          (P-02)
GET  /public/track/{token}/live                  # SSE stream: position + status events; poll fallback ?since=cursor
POST /public/track/{token}/pickup/confirm        # {lat,lng,accuracy} → 200; resolves rides.location_requests
                                                 #   (scope=pickup_confirm only; idempotent; TTL 300 s)   (US-25.3)
POST /public/track/{token}/pickup/decline        # 200; NO coordinates accepted or stored (P-02)
#  ⚠ POST /public/track/{token}/call — REMOVED BY AL-48 (Δ 2026-07-05 #2). The ride-scoped proxy-DID
#  lease is gone; the snapshot above carries driver.phone and SCR-WT-002/004 render it as a plain
#  tel: link (US-26.3). No endpoint, no DID pool, no confirm-your-number step.
POST /public/track/{token}/sos                   # {lat,lng,accuracy?} → 202; dual-gateway SMS to booker +
                                                 #   admin live feed; safety.sos_events(source='web')   (US-25.5, D-33)
GET  /public/track/{token}/receipt               # terminal state only → receipt (HTML + PDF); includes
                                                 #   proof: otp_verified|photo_proof|cod_collected|disputed   (US-25.6)
```
> **Token minting** stays server-side (`notification-svc`): package pickup-confirm → `package_recipient` (TTL = delivery + 1 h); proxy accept → `proxy_rider` (TTL = trip completion); `RiderNotRegistered` location request → `pickup_confirm` (TTL 300 s, bound to `location_request_id`) — mint-and-SMS, never returned to a client. The app-side `POST /v1/location-requests/{id}/confirm|decline` (Bearer) contracts are unchanged; the `/public/...` pair is the no-app path for the same state machine (AL-45).

## Δ Addendum — Discussion 2026-07-05 #2 (driver-QR settlement & masking removal, ADD v3.1 §1.13)

> New/changed endpoints for ADD v3.1 §1.13 (AL-47…AL-48) / URD v2.7 Epic 26.

### fare-svc — driver-QR attestation settlement (item 1, AL-47)
```
POST /v1/fare/pay/driver-qr/claim      # Bearer (passenger) {rideId, receiptArtifactId?} → 202
                                       #   state → QrClaimedByPassenger; driver gets "QR payment received?" push;
                                       #   nudge re-push at +5 min if unconfirmed (US-26.1)
POST /v1/fare/pay/driver-qr/confirm    # Bearer (driver) {rideId} → 200; state → DriverConfirmedQR (TERMINAL);
                                       #   earning posts (R-05). Valid with or without a prior passenger claim.
POST /v1/fare/pay/driver-qr/dispute    # Bearer (either party) {rideId, note?} → 201 ticket; routes Support → Finance.
                                       #   No wallet movement — zero-commission; evidence = claim screenshot if attached.
#  POST /fare/pay/scan-driver-qr (AL-22) still records method='scan_driver_qr'; it now leads to the claim/confirm
#  pair above instead of a webhook wait. Gateway-verified `Succeeded` remains OnePay-only (D-10).
```

### Calling — masking removed (items 2–4, AL-48)
```
POST /v1/calls/start                   # [ADAPT] callType now `free_voip` ONLY (LiveKit session). The former
                                       #   `normal_masked` bridge is REMOVED — "Normal call" is a client-side
                                       #   tel: dial of the counterparty MSISDN (no server round-trip).
GET  /v1/rides/{id}                    # [ADAPT] post-accept RideDetail adds counterpartyPhone (E.164):
                                       #   passenger sees driver.phone; driver sees rider.phone (P-05 — never booker).
                                       #   Omitted until Accepted and for cancelled-before-assignment rides.
GET  /public/track/{token}             # [ADAPT] scope package_recipient|proxy_rider snapshot adds driver.phone
                                       #   for the tel: link (US-26.3).
DELETE POST /public/track/{token}/call # REMOVED (AL-48) — no proxy-DID lease, no confirm-your-number step.
```
> `comms.call_log` shrinks to client-logged taps: `call_type ∈ {free_voip, direct_dial}` (best-effort; a tel: dial cannot be server-verified). The `share_token` column added for web calls is dropped (web SOS keeps its own `share_token` on `safety.sos_events`).

## Δ Addendum — Discussion 2026-07-18 (Fleet Portal payout & vehicle-document detail, items 1–3)

> New/changed endpoints for ADD v3.2 §1.14 (AL-49…AL-51) / URD v2.8 Epic 27. Conventions unchanged (RS256 JWT `fleet_role` claims, deny-by-default RBAC, RFC 7807, `Idempotency-Key` on writes).

### fleet-svc — org bank & payout profile (item 1, AL-49; SCR-FP-002a)
```
GET    /fleets/{fleetId}/payout-profile                # {bank, branch, accountNo, accountHolderName, lankaqrDocId?, proofDocId?, status, rejectionReason?}   [owner]
PUT    /fleets/{fleetId}/payout-profile                # upsert bank/branch/accountNo/accountHolderName → status='pending_verification' (any edit re-triggers)  [owner]
POST   /fleets/{fleetId}/payout-profile/documents      # multipart; kind: bank_statement | passbook_first_page | lankaqr_code → docs.uploads                    [owner]
```
- Verification: rides the existing fleet-org queue — `GET /admin/verification/org/{orgId}` response `documents[]` now includes payout docs; officer Approve/Reject sets `payout_profiles.status`.
- **Gate (BR-31.1):** `PUT /fleets/{id}/vehicles/{vid}/classification {mode_b_billing:'paid'}` → **409 RFC 7807 `payout-profile-not-verified`** while the org profile is not `verified`.

### subscription-svc — passenger pay sheet consumes the verified payout profile (item 1, AL-49)
```
POST /mode-b/subscriptions/{id}/pay        # response now carries payTo:
   → { method, amountMinor, payTo: { lankaqrImageUrl?,                      # lankaqr_scan / lankaqr_deeplink (signed URL of the owner's bank-app QR)
                                     bank?, branch?, accountNo?, accountHolderName? } }   # online_transfer
```
- `payTo` is served **only from a `verified` payout profile**; Paid subscriptions of an org whose profile falls back to `pending_verification` keep collecting against the last verified snapshot (versioned row) — never unverified edits.

### fleet-svc — per-vehicle document slots (item 2, AL-50; SCR-FP-004)
```
GET    /fleets/{fleetId}/vehicles/{vehicleId}/documents        # [{docId, kind, status: verified|pending|missing, expiresAt?, fields[]}]    [owner|manager]
POST   /fleets/{fleetId}/vehicles/{vehicleId}/documents        # multipart; kind: registration_copy→'registration' | insurance | revenue_license | route_permit→'permit'; queues ocr-svc extraction  [owner|manager]
```
- Required set: `registration` + `insurance` + `revenue_license` (all modes) **+ `permit` (Mode A only)** — registry-svc blocks `status='APPROVED'` until every required doc is verified (extends AL-10).
- Bulk CSV (`POST /fleets/{fleetId}/vehicles/bulk`) rows are created `docs_pending`; documents arrive per vehicle via the endpoint above.

### Naming note (item 3, AL-51)
UI label is now **"Service payment" (Free / Paid)**; `PUT …/classification` and `mode_b_billing` are **intentionally unchanged** (API stability — no client/DB migration for a label).

## Δ Addendum — Discussion 2026-07-22 #2 (GTFS Dataset Manager, ADD v3.4 §1.16)

> AL-54: versioned GTFS feed lifecycle for **SCR-AP-016** (`transit-svc`, admin-scoped, deny-by-default RBAC — Admin/Super Admin; every mutation audited). Dapper over Npgsql per D3 conventions; import runs outside the request path (queued job); activation swap is one `NpgsqlTransaction`.

```
POST /admin/transit/gtfs/uploads                       # multipart zip ≤200 MB → 202 {feedVersionId}; 409 duplicate sha256; 413 too large  [admin]
GET  /admin/transit/gtfs/uploads/{feedVersionId}       # {status: uploaded|validating|validated|failed, counts{}, feedInfoVersion,
                                                       #  serviceStart, serviceEnd, warnings[], errorSummary[≤5]}  [admin]
GET  /admin/transit/gtfs/uploads/{feedVersionId}/report  # full row-level report {errors:[{file,row,code,message}],warnings:[…]}; ?format=csv  [admin]
POST /admin/transit/gtfs/uploads/{feedVersionId}/activate  # Idempotency-Key; 200 on atomic swap; 409 not-validated / already-active  [admin]
GET  /admin/transit/gtfs/versions                      # history [{feedVersionId, feedInfoVersion, fileName, uploadedBy, uploadedAt,
                                                       #  counts{}, status: active|archived|validated|failed, activatedAt}]  [admin]
GET  /admin/transit/gtfs/versions/{feedVersionId}/download  # 302 signed URL, original zip  [admin]
POST /admin/transit/gtfs-import                        # [SUPERSEDED → uploads + activate, AL-54] retained as internal import step
```

Side effects of `activate`: load `transit_staging.gtfs_*` from the stored zip → single-transaction table swap → `NOTIFY transit_feed_activated` → `transit-svc` reloads route/stop caches (≤ 60 s); prior `active` row → `archived`. **Rollback = activate on an archived validated version** (same endpoint, same guarantees). `GET /transit/options` contract unchanged.

*End of D3′. 0 `[INCOMPLETE]` markers; all in-scope ADD critique items ✅.*
