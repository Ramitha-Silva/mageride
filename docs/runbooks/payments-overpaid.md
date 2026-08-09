# Runbook — Overpaid payments backlog (ADD §13.3.1 row 7, R-19)

**Alert:** `PaymentsOverpaidBacklog` · **Severity:** page (after 1 h)
**Dashboard:** Grafana → `mageride-money-safety`

> ADD §13.3.1: *"`payment.state='Overpaid'` count > 0 for > 1 h — late-callback storm; check OnePay
> reconciliation."*

---

## First action

**List them, oldest first.** Each row is money the platform has taken twice and owes back.

```bash
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT p.id, p.ride_id, p.amount_minor, p.method, p.provider_transaction_id,
           p.created_at, now() - p.created_at AS waiting
      FROM fares.ride_payments p
     WHERE p.state = 'Overpaid'
     ORDER BY p.created_at
     LIMIT 50;"
```

That is the query behind migration 1008's `ix_ride_payments_overpaid`, which exists for this and for
the Finance refund queue (SCR-AP-006).

---

## What happened

ADD §11.14: a gateway callback arrived for a ride that had **already been settled in cash**. The
passenger paid the driver, the ride reached a cash terminal, and then OnePay confirmed a card
authorisation that the passenger also completed. The platform now holds the fare twice.

`Overpaid` is deliberately excluded from `AnalyticsVocabulary.SettledPaymentStates` — a refund is
owed, not revenue earned — so it does not inflate gross fare on the admin dashboard.

The gauge is published by the analytics read model (`BusinessSloObserver`, Δ C119) rather than by
fare-svc, because §13.3.1's last two rows each span two bounded contexts.

---

## Diagnose

1. **Storm or trickle?** One or two rows an hour is the normal race between a cash settlement and a
   slow callback — annoying, expected, and the queue drains as Finance works it. A step change is a
   *late-callback storm*: the provider replaying a backlog after their own outage.

   ```promql
   increase(mageride_payments_overpaid[1h])
   ```

2. **Is it one method?** Group by `method`. All `onepay` points at the provider; a mixture points at
   the platform's own settlement ordering.

3. **Is the queue being worked?** The alert is about *nobody deciding*, not about the state existing.
   Check whether Finance has the console: `admin-bff`'s refund queue (SCR-AP-006) reads exactly the
   rows above.

---

## Fix

**A refund is a Finance decision, not an on-call one.** The correct action for the person paged is:

1. Confirm the volume and whether it is still growing.
2. If growing: check OnePay's status and reconciliation; a provider replaying a backlog will stop on
   its own and the queue is then finite.
3. Escalate to Finance with the count and the oldest timestamp. They resolve each row through
   `admin-bff` (`RefundService`), which writes the refund and the audit entry.

If the storm is large enough to matter operationally, the mitigation is at the *gateway* end —
pause the webhook consumer so the replay is absorbed in order rather than concurrently. The rides are
already settled; nothing in the ride machine depends on these rows.

---

## What not to do

- **Do not resolve the rows in SQL.** A refund moves money. `admin-bff` is the only path that records
  who decided and why (D-35), and an appeal three months later is answered from that record.
- **Do not treat a rising Overpaid count as a payment outage.** It is the opposite: the money
  arrived, twice.
- **Do not silence for longer than the storm.** The one-hour `for:` already absorbs the normal race;
  anything that survives it is a decision nobody has taken.
