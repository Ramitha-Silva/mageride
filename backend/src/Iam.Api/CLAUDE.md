# iam-svc (C020 ws-iam-minimal) — auth conventions

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/Iam.Api.Tests -c Release`

## What this slice is

The walking skeleton's identity: enough for a passenger and a driver to sign in and call an
authenticated endpoint. Everything here matches `backend/contracts/iam.yaml`, which wins over this
file and over the code.

| Endpoint | Spec |
|---|---|
| `POST /v1/auth/otp/request` · `/otp/resend` · `/otp/verify` | D3' iam-svc, D-32 |
| `POST /v1/auth/refresh` · `/logout` | D-29, US-1.7 |
| `GET /.well-known/jwks.json` | D-29, D-21 |

**Not here, on purpose.** Portal sign-in (`/v1/auth/google`, `/apple`, `/password`,
`/v1/admin/auth/login`), `/v1/auth/mqtt-token`, profile, saved addresses, nine-role RBAC and PDPA
are **C026/C027**. They are left unmapped rather than stubbed. **No MFA, ever (AL-37)** — the
endpoints AL-37 removed are listed at the top of `backend/contracts/iam.yaml`; do not re-add them.

## Rules that are load-bearing

- **One active session per `(user, app)`, not per user (AL-08).** A driver signing in must not end
  the same person's passenger session. The invariant is the C003 partial unique index
  `ux_sessions_active_app`, not application code — `SessionRepository.InsertAsync` is what would
  fail if a caller forgot to revoke first.
- **Refresh tokens rotate and are single-use (D-29).** The token is `mr1.{jti}.{hmac}`: opaque to
  the client, verifiable without a token column, because `iam.sessions` has none. Replaying a
  spent one revokes its **rotation family** (`family_id`, 0106) — never everything active for the
  `(user, app)`, which would let a stale handset log the live one out on a loop.
- **iam-svc validates its own tokens locally.** `Jwt:JwksUrl` is what *other* services fetch; this
  one holds the private half and resolves keys through `SigningKeyRing`
  (`IamServiceCollectionExtensions`). Never point its own bearer handler at its own HTTP endpoint.
- **D-32 fails closed.** If the Redis bucket is unreachable the OTP is refused (`503
  dependency-unavailable`). The gateway's coarse limiter fails *open*; this one guards an SMS bill.
- **The OTP is never at rest in the clear.** `iam.otp_attempts.otp_hash` is
  HMAC-SHA256(`{authId}:{code}`) under `Otp:PepperKey`, which is required outside Development.
- **Roles are not granted by opening an app.** A first sign-in creates the account with the role of
  the app it came from; an existing account is never escalated. Holding `driver` is what
  registry-svc onboarding grants (C029).

## Configuration

`Sms:Provider=dev` logs the OTP instead of sending it and is refused outside Development unless
`Sms:AllowDevSenderOutsideDevelopment=true`. `Sms:Provider=notifylk` fails at start-up until C026
lands the real gateway — better than a service that boots healthy and delivers nothing.

`Jwt:SigningKeyPem` and `Otp:PepperKey` are required outside Development and both are resolved
during `IamApplication.Build`, so a missing one is a failed deploy rather than a 500 on somebody's
sign-in. `Jwt:RefreshTokenKey` is optional but should be set: without it the refresh HMAC is
derived from the signing key, and the 90-day signing rotation (D7' §13) would log everybody out.

## Schema this service added

`db/migrations/0104`–`0106` close three gaps D4' leaves open; each file's header says why, and all
three are recorded as micro-change-sets in the C020 handoff in `build/progress.md`. `iam.devices`
now carries `device_key`, `iam.otp_attempts` carries `device_id`/`app`, `iam.sessions` carries
`family_id`, and `iam.command_log` exists at all.
