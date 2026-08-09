# Runbook — payment callback resolve (ADD §13.3 row 3, §13.3.1 row 6)

**Alerts:** `PaymentCallbackResolveBudgetBurning` · `RideStuckPaymentPending`
**Severity:** page · **Dashboard:** Grafana → `mageride-money-safety`

---

## First action

**Count the rides that cannot be booked against.** This is the user-visible cost and it is what
decides urgency.

```bash
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT r.id, r.passenger_id, now() - r.updated_at AS pending_for,
           p.state AS payment_state, p.method, p.provider_transaction_id
      FROM rides.rides r
      LEFT JOIN LATERAL (
        SELECT state, method, provider_transaction_id FROM fares.ride_payments
         WHERE ride_id = r.id ORDER BY attempt_no DESC LIMIT 1) p ON true
     WHERE r.state = 'PaymentPending' AND now() - r.updated_at > interval '10 minutes'
     ORDER BY r.updated_at
     LIMIT 20;"
```

`PaymentPending` is **not** exempt from `ux_rides_open_passenger`, so every row here is a passenger
who cannot book their next ride.

---

## What is measured

- **`PaymentCallbackResolveBudgetBurning`** — §13.3 row 3: Initiated → terminal, p95 < 30 s,
  p99 < 90 s, 99% monthly, 2% budget over 1 h. *(fare-svc does not record this histogram yet — see
  the C119 handoff. The rule is armed and silent.)*
- **`RideStuckPaymentPending`** — §13.3.1 row 6: rides in `PaymentPending` for over 10 minutes. This
  one is live, and it is the downstream symptom of the same failure.

The relationship is the useful part: the latency SLO burning becomes the stuck-state page within ten
minutes, because a ride cannot leave `PaymentPending` until fare-svc settles it.

---

## Diagnose

1. **Is the provider calling back at all?** Look at the gateway's own dashboard and at the platform's
   webhook route:

   ```promql
   sum by (http_response_status_code) (rate(http_server_request_duration_seconds_count{http_route=~".*webhook.*"}[15m]))
   ```

   No traffic at all is an outbound problem (the provider cannot reach the edge); 4xx is signature
   verification failing — check the webhook secret, which D7' §13 rotates every 180 days.

2. **Is the callback arriving and not being applied?** `fares.ride_payments` moving to a terminal
   state while `rides.rides` stays `PaymentPending` means fare-svc is not calling ride-svc's
   `POST /v1/internal/rides/{id}/payment-settled`. R-05 has exactly one door, and if
   `Ride:InternalApiKey` is unset **the whole `/v1/internal/rides/**` family is not mapped at all** —
   every completed ride then stalls here.

   ```bash
   docker compose -f infra/docker-compose.dev.yml exec -T app-services env | grep -c 'Ride__InternalApiKey'
   ```

3. **Is it the outbox?** fare-svc announces the settlement through its outbox. A lagging outbox
   delays every settlement equally — [outbox-lag.md](outbox-lag.md).

4. **Cash rides.** `FellBackToCash` and `CashOnDeliveryCollected` are terminal without any gateway
   involved. A backlog of *cash* rides in `PaymentPending` is a different bug: the driver's tap is not
   reaching `POST /v1/rides/{id}/cod-collected`.

---

## Fix

- **Provider outage** → nothing to do but wait and communicate; the rides settle when the callbacks
  arrive. The alert is correct to keep firing.
- **Missing internal key** → set it and restart. The stalled rides settle as fare-svc retries.
- **A specific ride that must be moved** → `admin-bff`'s finance surface (SCR-AP-006), never SQL.

---

## What not to do

- **Do not `UPDATE rides.rides SET state = 'Paid'`.** ride-svc is the sole writer of `rides.state`
  (R-01); a direct write skips the transition row, the settlement event, the driver's earning and the
  R-04 timer retirement. The passenger can book again and nobody has been paid.
- **Do not replay provider callbacks blindly.** A second callback on a ride already settled in cash
  is exactly what produces `Overpaid` (R-19, ADD §11.14) — see
  [payments-overpaid.md](payments-overpaid.md). Delivery is at least once and the platform is
  idempotent on the header key only.
- **Do not cancel the rides.** They are completed journeys; the passenger travelled.
