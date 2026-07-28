# fare-svc (C022) — **the walking-skeleton STUB**

Stack: .NET 10 Minimal API. References `MageRide.Shared` (C002). No database, no cache, no broker.

**Verify:** `dotnet test backend/src/Ride.Api.Tests -c Release`
(this project has no test suite of its own — `FareStubTests` drives it end to end into
`POST /v1/rides/request`, which is the only crossing worth asserting.)

## Read this before changing anything

**This is a stub. C049 and C050 replace it.** It exists so the walking skeleton can quote a price
and so `POST /v1/rides/request` has a `fareEstimateToken` to accept. Every stubbed decision is
marked `STUB (C049)` or `STUB (C049/C050)` in the source next to the spec line it stands in for:

| Stubbed here | What it should be |
|---|---|
| `FareTariff` — a hard-coded table | `fares.tariffs` (migration 1001), admin-editable through `PUT /v1/admin/fares/tariffs` (US-14.4), with `effective_from` so a rate change never re-prices a quoted ride |
| straight-line haversine distance | OSRM/Valhalla route distance (D5' §1.2) — every quote here is low by the road detour |
| no peak or night surcharge, ever | `fares.peak_windows`, evaluated in Asia/Colombo, stacking additively on the base (D5' §1.1, D-38) |
| a Sri Lanka bounding box | `config.operating_cities` (migration 0201) — per-city service polygons |
| `truck` / `mini_truck` rates | Epic 20. **The two rows here are invented**: D5' §1.1 prints no delivery rates and the contract still lets a caller ask for them, so refusing would break the contract and inventing is the lesser evil. Recorded in the C022 handoff. |

The other fourteen operations in `backend/contracts/fare.yaml` — `POST /v1/fare/calculate`, the
payment state machine, the OnePay and LankaQR callbacks, driver-QR settlement (AL-47) and the
Finance refund routes — are **left unmapped rather than stubbed**. A stubbed payment endpoint is
worse than an absent one: it answers 200 to a client that then believes money moved.

## The one thing here that is not a stub

`fareEstimateToken` is the real contract between fare-svc and ride-svc, and its codec lives in
`MageRide.Shared.Fares` because both sides use it. Format:
`mrf1.<base64url(claims)>.<base64url(hmac-sha256)>`, keyed by `Fare:EstimateTokenKey`.

- **Both services must be configured with the same key**, or every booking is a
  `400 invalid-fare-token`.
- There is deliberately **no default key**. A token signed with a well-known key is one a client
  can mint for itself, and naming your own fare is exactly what the token exists to stop — the
  codec throws at start-up rather than run without one.
- The claims bind the tier and the trip; ride-svc rejects a token issued for another
  `vehicleType` or `kind`. C049 keeps the format and replaces what fills it.

## Money

Integer minor units throughout (`amountMinor`, `currency: LKR`). D5' §1.3: compute in minor units,
one `round` where a product is taken, away from zero — a passenger reading "Rs 480" should not
need to know which way 0.5 fell. **US-8.4 shows the total only**; the `breakdown` is for support
and receipts.
