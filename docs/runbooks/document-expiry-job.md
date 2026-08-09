# Runbook — lapsed documents, vehicles still dispatchable (ADD §13.3.1 row 8, E-03)

**Alert:** `ExpiredDocumentsStillDispatching` · **Severity:** page
**Dashboard:** Grafana → `mageride-stuck-states`

> ADD §13.3.1: *"`documents.expires_at < now()` AND driver still `dispatch_active` — doc-expiry job
> not running."*

**Every one of these is a car that can still be offered a ride while its insurance, permit or
licence has lapsed.** That is a regulatory and safety exposure, not a data-quality issue.

---

## First action

**Name the vehicles**, so they can be suspended by hand if the job cannot be fixed quickly.

```bash
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT v.id AS vehicle_id, v.registration_number, v.owner_id,
           d.kind, d.expires_at, d.status, v.dispatch_state
      FROM registry.documents d
      JOIN registry.vehicles v ON v.id = d.vehicle_id
     WHERE d.expires_at IS NOT NULL
       AND d.expires_at < now()
       AND v.dispatch_state = 'ACTIVE'
     ORDER BY d.expires_at;"
```

---

## What is measured

§13.3.1 writes the row as "driver still `dispatch_active`". The column that says so is
**`registry.vehicles.dispatch_state`** (migration 0303), whose CHECK comment is literally "E-03
doc-expiry auto-suspend" and whose only writer is registry-svc's `DocumentExpiryWorker`. So the
gauge asks *"is that worker running"* of the thing that worker writes.

**Counted by vehicle, not by driver**, because a vehicle is what dispatch offers and what a
passenger gets into — registry-svc's own CLAUDE.md puts it plainly: suspension is per vehicle
"because that is where the column is". Four lapsed certificates on one car is one car to take off
the road. It also means AL-50 fleet-owned documents (`driver_id IS NULL`) are counted, which they
must be: E-03 suspends vehicles for those too.

Two predicates were tried before this one and both would have been quiet during the outage the
alert exists to catch. Matching on `registry.documents.status <> 'EXPIRED'` alone counts paperwork
rather than exposure. Joining `dispatch.driver_presence` makes the gauge follow driver shift
patterns — E-03 dying at 02:00 would be invisible until the morning — and drops fleet documents
entirely.

---

## Diagnose

1. **Is `DocumentExpiryWorker` running?** It is registry-svc's, sweeping `registry.documents` on
   `Registry:DocumentExpiryInterval`. It logs at start-up ("Document-expiry tracker sweeping
   registry.documents every …") and once per sweep that emits notices.

   ```bash
   docker compose -f infra/docker-compose.dev.yml logs app-services 2>&1 | grep -i "document-expiry"
   ```

   Nothing in the last interval → it died or was never started. Restart registry-svc.

2. **Is the sweep running and failing?** It catches, logs an error and retries on the next tick
   ("Document-expiry sweep failed; retrying on the next tick"). A repeated error is usually the
   notification hop, not the marking.

3. **Is it marking the document but not suspending the vehicle?** Two different writes in the same
   sweep. `registry.documents.status` moving to `EXPIRED` is bookkeeping;
   `registry.vehicles.dispatch_state = 'DISPATCH_SUSPENDED'` is what takes the car off the road, and
   it is the one the gauge reads — deliberately, because it is the one a passenger experiences.

   ```sql
   SELECT dispatch_state, count(*) FROM registry.vehicles GROUP BY dispatch_state;
   ```

4. **Is it one fleet?** Group the first-action query by `v.fleet_id`. A whole fleet arriving at once
   is a batch of certificates that were renewed together a year ago, not a worker failure.

---

## Fix

- **Job dead** → restart registry-svc. The sweep is idempotent and catches up.
- **Immediate exposure** → suspend the named vehicles through `admin-bff`. Do not wait for the job:
  the point of the alert is that somebody is being carried right now.
- **Systemic** → E-03's notices should have gone out before expiry (`registry.document_notices`, 0312).
  If drivers are reaching expiry with no warning, that is the upstream fix.

---

## What not to do

- **Do not `UPDATE registry.documents SET status = 'EXPIRED'` to clear the alert.** It would not
  clear it — the gauge reads `registry.vehicles.dispatch_state` — and if it did, it would mark the
  paperwork while leaving the car on the road, removing the only signal anybody had.
- **Do not `UPDATE registry.vehicles SET dispatch_state = …` either.** registry-svc owns the column
  and its write is one transaction with the `document.expired` event every other service reacts to.
  Suspend through `admin-bff`.
- **Do not widen the `for: 5m`.** Five minutes is already the time it takes to read this page.
