# ride-svc (C022 ws-ride-svc-happy-path) — Mode C ride aggregate

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/Ride.Api.Tests -c Release`

## What this slice is

The happy path of the Mode C ride, and only that: `Requested → Matching → Offered → Accepted →
DriverArrived → InProgress → Completed → PaymentPending`. Everything here matches
`backend/contracts/ride.yaml`, which wins over this file and over the code.

| Endpoint | Spec |
|---|---|
| `POST /v1/rides/request` | D3' ride-svc, R-18 |
| `GET /v1/rides/{id}` · `/state` | D3' route table |
| `GET /v1/rides/passenger/{id}/active` · `/driver/{id}/active` | D3' route table, R-18 |
| `POST /v1/rides/{id}/offer/{driverId}/accept` · `/decline` | ADD §11.11, §11.12 |
| `POST /v1/rides/{id}/arrive` · `/start` · `/complete` | D3' route table |
| `POST /v1/internal/rides/{id}/matching` · `/offer` | **Δ C022** — see below |

**Not here, on purpose.** The §11.12 cancellation and no-show matrix, `POST /cancel`,
`/internal/{id}/system-cancel` and the P-02 location-request family are **C032**. Package
delivery and its two OTP gates are **C037**. The durable Quartz offer-expiry backstop (R-04) is
**C037** — `offer_expires_at` is already authoritative, but nothing fires on it yet, so a ride
whose offer lapses sits in `Offered` until dispatch re-offers. `/dispute`,
`/internal/{id}/payment-settled` and the payment terminals are **C049/C050**. `GET /v1/rides/history`
is **C048**. All are left unmapped rather than stubbed: a stubbed `cancel` answers 200 to a
passenger and leaves a driver en route.

## Rules that are load-bearing

- **ride-svc is the sole writer of `rides.state`** (R-01, D5' §6). ADD §11.11's diagram draws
  dispatch-svc updating the row itself; §11.12 in the same document says sole-writer, and
  sole-writer wins — two services issuing conditional updates against one aggregate is the race
  R-02 exists to remove. The two moves dispatch drives are therefore commands on
  `/v1/internal/rides/**`, and `dispatch.offers` / `dispatch.candidate_scores` stay dispatch's.
- **The accept is one conditional UPDATE and nothing else** (`RideRepository.AcceptAsync`). No
  advisory lock, no pre-flight `SELECT`, no application-side ordering — the database picks the
  winner. There is deliberately **no `offered_driver_id` predicate** on it: adding one turns a
  concurrent double-accept into two 403s and moves the guarantee out of the database. The
  `CASE WHEN offered_driver_id = :driverId` on `accepted_vehicle_id` is what keeps a spoofed
  winner from recording somebody else's vehicle. `ConcurrentAcceptTests` races two drivers, then
  ten.
- **Every mutation is one transaction with three writes**: the `UPDATE`, the `rides.transitions`
  audit row (ADD Appendix B.2 invariant 4) and the `rides.outbox` row (R-13). Nothing publishes
  to Redpanda directly — the kernel's LISTEN/NOTIFY dispatcher drains after COMMIT (E-09).
- **`Completed` is not terminal.** `ux_rides_open_passenger` exempts it (C004 note (b)); the
  state machine does not — the ride still owes a payment. `RideStates.Terminal` answers the
  domain's question, not the index's, and `complete` moves through `Completed` to
  `PaymentPending` inside one transaction so the ride never rests there.
- **Idempotency is two independent keys.** The header is the kernel's (`rides.command_log`,
  R-14); `(passenger_id, client_request_id)` is `ux_rides_idem` (R-18) and survives a client that
  regenerated its header key. `CreateAsync`'s `ON CONFLICT` names `ux_rides_idem` specifically, so
  `ux_rides_open_passenger` still raises — the two mean opposite things to the caller.
- **`clientRequestId` may be a ULID.** ADD §11.13 has the apps generate them; the column is
  `UUID`. `Ulids.TryParse` decodes Crockford base32 to the same 128 bits, so a correct client is
  not a 400.
- **A booking without a valid `fareEstimateToken` is refused** (`400 invalid-fare-token`), and the
  token's tier must match the requested one. The codec is `MageRide.Shared.Fares` because
  fare-svc signs and ride-svc verifies; both read `Fare:EstimateTokenKey`.

## Configuration

`Ride:InternalApiKey` **unset means `/v1/internal/rides/**` is not mapped at all** — a deployment
that forgets it gets 404s, not an open door, and dispatch-svc places no offers. D3' §0 puts the
internal family on service-to-service mTLS and the gateway already refuses the prefix at the edge
(C008); the shared secret is the interim until C042 lands a mesh, and `ride.yaml`'s `internalKey`
scheme is deleted with it.

`Ride:OfferTtl` (default 15 s, D5' §3.5) is used when dispatch sends no `ttlSeconds`. The deadline
is always stamped from **this** service's clock: it is ride-svc's `offer_expires_at > now()` that
decides an accept.

`Fare:EstimateTokenKey` must be the same value fare-svc signs with, or every booking is a 400.

`Jwt:Issuer` must match what iam-svc signs with or every request is a 401. ride-svc holds no
signing key — it resolves iam-svc's public half through `Jwt:JwksUrl`.

Redis is **off** (`UseRedis = false`): the `lock:driver-offer:{driverId}` fast path in ADD §11.11
is dispatch-svc's reservation (D5' §3.6), and the authoritative accept here is pure Postgres, so a
Redis dependency would be a readiness probe that can fail while every route still works.

## Schema this service added

`db/migrations/0608` — `offered_driver_id`, `offered_vehicle_id`, `fare_estimate_minor`,
`fare_surcharge_minor` and `currency` on `rides.rides`. The file header says why; both are recorded
as micro-change-sets in the C022 handoff in `build/progress.md`.
