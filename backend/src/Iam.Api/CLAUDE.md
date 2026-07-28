# iam-svc — auth conventions (C020 ws-iam-minimal + C026 iam-svc-auth)

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/Iam.Api.Tests -c Release`

## What this is

iam-svc's **authentication** half: every sign-in surface AL-07 lists, the full token model, device
binding, and the MQTT session JWT E-02 decouples from the API token. Everything here matches
`backend/contracts/iam.yaml`, which wins over this file and over the code.

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

**Not here, on purpose.** Profile (`/v1/users/me`), saved addresses, the language preference,
`/v1/users/lookup` and PDPA are **C027**. They are left unmapped rather than stubbed.

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

## Schema this service added

`db/migrations/0104`–`0107` close four gaps D4' leaves open; each file's header says why, and all
four are recorded as micro-change-sets in the C020 and C026 handoffs in `build/progress.md`.
`iam.devices` carries `device_key` and admits `web`; `iam.otp_attempts` carries
`device_id`/`app`/`fcm_token`; `iam.sessions` carries `family_id` and admits `admin`/`fleet`;
`iam.command_log`, `iam.user_credentials` and `iam.federated_identities` exist at all.
