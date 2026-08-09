# Runbook — offer push latency (ADD §13.3 row 8)

**Alert:** `OfferPushLatencyBudgetBurning` · **Severity:** page
**Dashboard:** Grafana → `mageride-slo`

> ADD §13.3 row 8: `ride.requested` → driver device push received-ack, **p95 < 2 s, p99 < 4 s**,
> 99% monthly, 5% budget over 1 h.

**Not instrumented yet.** dispatch-svc does not record
`mageride_dispatch_offer_push_latency_milliseconds`, so this rule is armed and silent — see the C119
handoff. The rest of this page is what to do when it does fire, and what to look at meanwhile.

---

## First action

**Check the offer expiry rate**, which is the observable consequence and is available now:

```bash
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT reason_code, count(*)
      FROM rides.transitions
     WHERE to_state = 'Matching' AND ts > now() - interval '30 minutes'
       AND reason_code IN ('OFFER_EXPIRED','DRIVER_UNREACHABLE')
     GROUP BY reason_code;"
```

A rising `OFFER_EXPIRED` count is the symptom: the offer TTL is 15 s (D5' §3.5), so an offer that
spends 4 of those in flight arrives with a third of its window already gone and expires in the
driver's hand.

---

## Why it matters

The offer is time-boxed by the *ride*, not by the push. Every second of delivery latency is a second
the driver does not get to decide in, and an expired offer costs a reassignment round — so latency
here compounds into `RideStuckMatching`.

---

## Diagnose (once instrumented)

The path is `ride.requested` → outbox → Redpanda → dispatch-svc → candidate selection → FCM/APNs →
device ack. Four places it goes slow:

1. **The outbox.** `mageride:outbox` p95 over 50 ms puts the whole budget at risk before dispatch has
   seen anything — [outbox-lag.md](outbox-lag.md).
2. **Consumer lag on `ride.events`.** [consumer-lag.md](consumer-lag.md).
3. **Candidate selection.** The R-08 pool query against Redis plus `dispatch.candidate_scores`. A
   large or drifting `geo:live` makes this scan more than it should —
   [redis-evictions.md](redis-evictions.md) covers why that index grows.
4. **The push provider.** FCM high-priority is the transport. Check notification-svc's
   `http_client_request_duration_seconds` to the provider; this is the leg most often responsible and
   the one the platform controls least.

---

## Fix

- Provider slow → nothing to do but confirm and record; consider whether the 15 s TTL should be
  configured longer for that region, which is a product decision and not an on-call one.
- Dispatch saturated → scale dispatch-svc.
- Outbox or consumer lag → their own runbooks.

---

## Instrumenting it

The histogram should be recorded in dispatch-svc at the point the device ack arrives, against the
`ride.requested` event's own timestamp — the same shape C119 used for
`mageride_positions_e2e_latency_milliseconds` (both ends in one histogram, because adding two p95s
gives a number no SLO is written about). Name it
`mageride.dispatch.offer_push.latency` with unit `ms`; the recording rule
`mageride:offer_push:error_ratio5m` already reads
`mageride_dispatch_offer_push_latency_milliseconds_bucket{le="2000"}`.

---

## What not to do

- **Do not lengthen the offer TTL to stop expiries.** It hides the latency and makes every
  reassignment slower for the passenger.
- **Do not remove the expiry backstop.** `offer/expire` is bound to `offer_expires_at <= now()`
  evaluated by Postgres, so a sweeping node whose clock ran ahead cannot take an offer from a driver
  still inside the window. That guard is why a slow push is merely unfair and not incorrect.
