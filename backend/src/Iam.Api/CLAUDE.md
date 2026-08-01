# iam-svc (C020 ws-iam-minimal + C026 iam-svc-auth + C027 iam-svc-profile-rbac)

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/Iam.Api.Tests -c Release`

## What this is

Two halves that share one account. **Authentication** (C026): every sign-in surface AL-07 lists,
the token model, device binding, and the MQTT session JWT E-02 decouples from the API token.
**The identity data plane** (C027): profile, preferences, saved addresses, emergency contacts,
the eager-fetch login payload, the nine-role deny-by-default RBAC and the PDPA erasure request.
Everything here matches `backend/contracts/iam.yaml`, which wins over this file and over the code.

| Endpoint | Surface | Spec |
|---|---|---|
| `POST /v1/auth/otp/request` · `/otp/resend` · `/otp/verify` | Passenger + Driver apps | D3' iam-svc, D-32 |
| `POST /v1/auth/password` | Admin + Fleet portals | AL-07, AL-37 |
| `POST /v1/auth/google` | Admin + Fleet portals | AL-07 |
| `POST /v1/auth/apple` | Fleet Portal only | AL-07 |
| `POST /v1/admin/auth/login` | Admin Portal (password *or* Google code) | Δ 2026-06-28 item 5 |
| `POST /v1/auth/refresh` · `/logout` | all | D-29, US-1.7 |
| `POST /v1/auth/mqtt-token` | Driver app | E-02, D-21 |
| `GET /.well-known/jwks.json` | infrastructure | D-29, D-21 |
| `GET` · `PUT /v1/users/me` | all | US-1.5, AL-06 |
| `DELETE /v1/users/me` | apps | US-1.8, E-06 |
| `GET /v1/users/lookup` | ride-svc (mTLS) | P-03 |
| `GET · POST · PUT · DELETE /v1/me/saved-addresses` | Passenger app | AL-14, AL-26, US-22.x |
| `GET · POST · PUT · DELETE /v1/me/emergency-contacts` | both apps | **Δ C027** — AL-13 |
| `PUT /v1/me/prefs/language` | onboarding + Settings | AL-26, D-26 |
| `PUT /v1/me/prefs/payment-method` | Passenger Settings | **Δ C027** — AL-14, US-22.4 |
| `PUT /v1/me/prefs/operating-city` | first-run city screen | **Δ C027** — AL-27, US-1.3a |
| `GET /v1/me/bootstrap` | both apps, on login | **Δ C027** — AL-14, US-1.14/1.15 |
| `GET /v1/me/permissions` | both portals | **Δ C027** — URD §2.2, AL-06 |
| `GET /v1/admin/rbac/matrix` · `/roles` · `/users/{id}` · role grant/revoke | Admin Portal | **Δ C027** — URD §2.3 |

**Δ C027** marks the eight routes D3' does not carry; each is argued in `iam.yaml` and recorded as
a micro-change-set in the C027 handoff in `build/progress.md`.

**Not here, on purpose.** PDPA *fulfilment* — the export ZIP, the anonymisation, the statutory
hold list — is admin-bff's (C065); `DELETE /v1/users/me` writes a `pdpa.requests` row and touches
nothing else. Driver identity (name, photo, licence) is `PUT /v1/drivers/profile` in registry-svc
(C029). Notification *delivery* preferences also have a notification-svc route (C061) over the
same `iam.users.notif_prefs` column.

**No MFA, ever (AL-37).** The endpoints AL-37 removed are listed at the top of
`backend/contracts/iam.yaml`; do not re-add them. Both password routes answer with a token pair or
an error — there is no code path that can return a challenge, and there is no `iam.user_mfa`.
D3' §0 and D7' §4.2 still carry pre-AL-37 wording; AL-37 is later and wins (planner finding 3).

## Rules that are load-bearing

- **One active session per `(user, surface)` (AL-08, 0107).** `iam.sessions.app` is
  `passenger | driver | admin | fleet`, and the C003 partial unique index `ux_sessions_active_app`
  is the invariant — not application code. For the apps that is AL-08's single active device; for
  the portals it is the **session binding** AL-37 keeps as a compensating control. A driver signing
  in does not end the same person's passenger session, and neither ends their Fleet Portal one.
- **Refresh tokens rotate and are single-use (D-29).** The token is `mr1.{jti}.{hmac}`: opaque to
  the client, verifiable without a token column, because `iam.sessions` has none. Replaying a
  spent one revokes its **rotation family** (`family_id`, 0106) — never everything active for the
  `(user, app)`, which would let a stale handset log the live one out on a loop.
- **A rotation re-reads the account.** A role granted since the sign-in (C029's driver grant)
  reaches the token within one refresh rather than at next sign-in.
- **One claim set for every surface.** `sub`, `role` (repeated for the union, AL-06),
  `fleet_role` + `fleet_id` when there is a fleet membership, `device_id`, `app`, `jti`. Portal and
  app sign-ins differ in the *values*, never in the shape — nine services and EMQX read one shape.
- **No portal sign-in creates an account.** Internal roles are provisioned by a Super Admin
  (AL-06), fleet users by their owner (AL-03). An unknown email or an unlinked Google subject is
  `403`, not a first sign-in. Only phone-OTP verify creates accounts.
- **Federated identity binds on the provider's `sub`, never on the email.** An address can be
  changed at the provider and re-asserted by somebody else; an **unverified** asserted address
  never matches an existing account at all.
- **The OIDC audience is not optional.** An ID token minted for another OAuth client is a valid
  Google token. `Oidc:{Provider}:ClientIds` has no default and an empty list refuses everything.
- **iam-svc validates its own tokens locally.** `Jwt:JwksUrl` is what *other* services fetch; this
  one holds the private half and resolves keys through `SigningKeyRing`
  (`IamServiceCollectionExtensions`). Never point its own bearer handler at its own HTTP endpoint.
- **A key rotation is an overlap, not a switch (D7' §13).** `Jwt:SigningKeyPem` signs;
  `Jwt:RetiredSigningKeyPems` stays published and accepted for at least one access-token lifetime
  plus D-21's 15-minute cache window, then is deleted. Set `Jwt:RefreshTokenKey`, or the rotation
  invalidates every live refresh token.
- **D-32 fails closed.** If the Redis bucket is unreachable the OTP is refused (`503
  dependency-unavailable`). The gateway's coarse limiter fails *open*; this one guards an SMS bill.
- **A gateway outage is a 503, not a 500.** Notify.lk primary → Dialog/Mobitel secondary
  (D6' §7.3); both refusing answers `dependency-unavailable`, which a client can act on.
- **The OTP is never at rest in the clear.** `iam.otp_attempts.otp_hash` is
  HMAC-SHA256(`{authId}:{code}`) under `Otp:PepperKey`, which is required outside Development.
- **The lock-out counter is durable, not cached.** `iam.user_credentials.failed_attempts` /
  `.locked_until`. A Redis flush must not hand an attacker a clean slate on every internal account
  at once. The lock gates the **password** path only — locking Google sign-in from failed password
  guesses would be a denial of service against an admin who never uses one.
- **The MQTT credential inherits the session's device binding.** `deviceId` must equal the
  `device_id` claim, or a stolen access token could mint a publishing credential for another
  handset — the one thing AL-08 exists to prevent.
- **Roles are not granted by opening an app.** A first sign-in creates the account with the role of
  the app it came from; an existing account is never escalated. Holding `driver` is what
  registry-svc onboarding grants (C029).

### The RBAC model (C027)

- **URD §2.3 is compiled in, not configured — and since C062 it is the kernel's, not this
  service's.** `MageRide.Shared.Auth.PermissionMatrix` is the 21×9 table transcribed cell for cell;
  `PermissionMatrixTests` (now in `MageRide.Shared.Tests`) **parses §2.3 out of
  `specs/user-requirements-document.md`** and compares all 189 cells, so a slip there or a change
  here fails the build. It moved because admin-bff enforces the same matrix on the same nine roles
  and a second copy is how two services start disagreeing; what stayed is `RoleAdminService` and
  `iam.user_roles`, which is the writable half. `IPolicyEvaluator` was renamed
  `IPermissionEvaluator` on the way (ASP.NET Core has an `IPolicyEvaluator` of its own) and
  `Domain.FleetMembership` became `MageRide.Shared.Auth.FleetScope`. It is read-only on purpose:
  the principal who would edit it is the principal it constrains, so a writable matrix is one
  `UPDATE` away from a Super Admin granting themselves what §2.3 forbids.
- **Deny-by-default has no fall-through.** `PermissionMatrix.Cell` answers ➖ for a pair it does
  not hold and `FeatureAuthorizationHandler` never succeeds a requirement whose feature area is
  not one of the twenty-one. An endpoint that names an area nobody transcribed answers **403**,
  not "authenticated is good enough".
- **Effective permissions are the union of every role held** (URD §2.1), and the union is
  strictly additive over capabilities. `RequireFeature(area, capability)` is preferred over
  `RequireMageRideRole` wherever §2.3 has a row: naming `super_admin` at a call site duplicates a
  decision the spec already made and drifts the day the spec adds a role to the cell.
- **`ownScope` is a fence, not an answer, and it is per capability.** iam-svc knows the caller's
  roles; it cannot know whether ride 7 is theirs. A service that sees it has been told "allowed,
  and you must bound it", and `qualifier` names how (`own`, `own org`, `financial`, …). It is
  tracked per verb because a caller who is both an Admin (👁 platform-wide) and a Fleet Owner
  (◐ own org) reads every fleet and writes only their own — one flag for the whole row would be
  wrong in one direction or the other.
- **The fleet sub-role narrows the `fleet_owner` column and nothing else** (URD §2.1): Manager
  loses billing, Viewer is read-only. A Viewer who is also a Support CSR keeps every CSR cell at
  full strength.
- **An Admin is refused role management.** URD §2.3's RBAC row gives `admin` ➖ and §2.4 spells it
  out. It is the most surprising cell in the matrix, so `RbacEndpointTests` asserts it by name.
- **A role grant reaches its holder at the next refresh, not instantly.** C026's rotation re-reads
  the principal; revoking the live session instead would sign out an admin who was granted an
  *extra* role.

### The profile data plane (C027)

- **`iam.saved_addresses` keeps both spellings of Home and Work, and that is the answer to C003
  note (c).** `label` and `is_home`/`is_work` are not redundant: only the booleans can express
  "at most one Home" as an index (`uq_saved_home`), and only the label gives D2 SCR-PA-026's
  "Save Address As" somewhere to go — and `iam.yaml` requires both. The service reconciles them
  (a `home` label sets the flag and vice versa) and refuses the one combination that cannot be
  honoured rather than guessing.
- **The primary emergency contact is denormalised on purpose.** D-33 budgets five seconds for the
  whole SOS fan-out, so safety-svc reads `iam.users.emergency_contact_name`/`_phone` and never
  joins. Every mutation re-derives the primary (oldest row) and rewrites those columns **in the
  same transaction**, so the two copies can never be observed disagreeing. Deleting the last
  contact clears them, which puts `POST /v1/sos` back to `400 no-emergency-contact`.
- **`GET /v1/me/bootstrap` is one connection and nothing unbounded** (NFR-51). It reads across
  bounded-context lines — `rides.rides`, `trips.sessions`, `fares.driver_earnings`,
  `config.operating_cities` — for the same reason `PublisherRepository` does: four synchronous
  HTTP calls would make a login fail whenever any of four services is redeploying, on the one
  request a user cannot proceed without. Read-only; the outbox rule is about state changes. Trip
  history, earnings breakdowns and receipts are lazy-fetched (US-1.16) and must never be added.
- **`DELETE /v1/users/me` records and does not act.** Erasure may be rejected or held
  (`FulfilledHold`), so the account, its columns and its live session are left exactly as they
  were; a second request while one is open is a `409`, because two 30-day clocks against one
  obligation leave whichever C065 does not fulfil permanently overdue.
- **`GET /v1/users/lookup` authenticates itself.** It is a registration oracle and it is **not**
  under the `/v1/internal/**` prefix the gateway refuses — the `iam-users` route forwards it from
  the public internet. `Auth:InternalApiKey` unset means the route is not mapped at all.
- **Notification-type keys are data, not property names.** `MageRideJson`'s camelCase
  dictionary-key policy would rewrite `SCHEDULED_REMINDER` as `sCHEDULED_REMINDER` once, silently;
  `LiteralKeyDictionaryConverter` is applied to the column and to the wire so it cannot.
  Safety-critical types (`SOS_*`, `RIDE_CANCELLED`) are dropped rather than stored (US-10.7).
- **Language is accepted on two routes and the AL-26 fence is a UI one.** AL-26 removed the
  picker from Edit-profile and kept it in onboarding and Settings — a rule about screens, which
  the server cannot see. `iam.yaml` lists `language` on `PUT /v1/users/me`, so it is honoured
  there; `PUT /v1/me/prefs/language` is the route the two allowed screens use. D2 SCR-PA/PI-027b
  still draws the control on Edit profile and is the earlier document (C027 handoff).

## Configuration

`Sms:Provider=dev` logs the OTP instead of sending it and is refused outside Development unless
`Sms:AllowDevSenderOutsideDevelopment=true`. `Sms:Provider=notifylk` requires
`Sms:NotifyLkUserId` + `Sms:NotifyLkApiKey`; `Sms:SecondaryGateway` (optional) adds the D6' §7.3
fallback.

Four things are resolved during `IamApplication.Build`, so a missing one is a failed deploy rather
than a 500 on somebody's sign-in: `Jwt:SigningKeyPem`, `Otp:PepperKey`, the embedded SMS templates,
and `Mqtt:SessionTokenSecret` — which must equal EMQX's `EMQX_AUTHENTICATION__1__SECRET` or the
broker refuses every CONNECT.

`Auth:InternalRoleIpAllowList` is off while empty (the ADD calls it optional). `Auth:TrustForwardedFor`
is on because every request arrives through the C008 gateway.

Two more are resolved during `IamApplication.Build` for the same reason as the other four:
`Auth:PhoneHashKey` (required outside Development — an unkeyed digest of a `+947XXXXXXXX` number
is a 10^8 offline search, and it is **not rotatable in place**, since a new key partitions
`iam.phone_lookups` rather than re-keying it) and `Auth:InternalApiKey`, whose absence unmaps
`GET /v1/users/lookup` entirely.

## Schema this service added

`db/migrations/0104`–`0108` close five gaps D4' leaves open; each file's header says why, and all
five are recorded as micro-change-sets in the C020, C026 and C027 handoffs in `build/progress.md`.
`iam.devices` carries `device_key` and admits `web`; `iam.otp_attempts` carries
`device_id`/`app`/`fcm_token`; `iam.sessions` carries `family_id` and admits `admin`/`fleet`;
`iam.command_log`, `iam.user_credentials`, `iam.federated_identities` and `iam.phone_lookups`
exist at all.

`iam.user_prefs` does **not** exist, despite ADD §9.1 and D4' Δ 2026-06-21 naming it: no
`CREATE TABLE` appears in any spec and both runnable DDL sources put `language` and
`default_payment_method` on `iam.users` (C003 note (d)). C027 kept that decision; the three
preference routes write columns.
