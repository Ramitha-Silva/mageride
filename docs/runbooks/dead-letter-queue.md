# Runbook — messages arriving on a dead-letter topic (D6' §2.3)

**Alert:** `DeadLetterTopicReceiving` · **Severity:** ticket
**Dashboard:** Grafana → `mageride-stream`

---

## First action

**Read one.** A DLQ message carries the payload that could not be handled, and one is usually enough
to name the bug.

```bash
docker compose -f infra/docker-compose.dev.yml exec -T redpanda \
  rpk topic consume <topic>.dlq --brokers redpanda:9092 -n 3 --offset start
```

---

## Why this is a ticket and not a page

A dead letter is a message **no consumer could handle**. Nothing retries it, and no other alert
covers it — the producer's write succeeded, so from every other angle the platform looks fine. It is
not urgent (nothing is blocked; the consumer moved on) and it is not ignorable (something the
platform produced is being silently discarded).

The exception is `telemetry.normalized.dlq`. C039 refuses everything implausible upstream, so a row
Postgres still rejects means **a producer has changed shape** — and
`mageride_telemetry_rows_dead_lettered_total` is documented as "should be zero; any non-zero reading
is worth a page". Treat volume there as more urgent than the ticket implies.

---

## Diagnose

1. **Which topic, and therefore which consumer.** The DLQs mirror the D6' §2.1 topics.

   | DLQ | Producer of the dead letter | Usual cause |
   |---|---|---|
   | `telemetry.normalized.dlq` | persistence-writer-svc | Postgres refused the row on its own merits — a schema/contract drift, or a value C039 should have caught |
   | `ride.events.dlq` | a ride.events consumer | An event type or payload shape the consumer does not recognise and cannot skip |
   | `dispatch.events.dlq` | dispatch consumers | As above |
   | `audit.events.dlq` | admin-bff | As above |

2. **Is it one message replayed, or many distinct ones?** Same key repeatedly is a poison message the
   consumer keeps re-reading; many distinct keys is a contract change.

3. **Check the envelope against D6' §2.2.** The contract test suite (C118) validates event envelopes;
   if a producer has drifted, that is where the fix belongs.

4. **Correlate with a deployment.** `target_info{service_version=…}` changing at the moment the DLQ
   started receiving names the release.

---

## Fix

- **A producer bug** → fix and redeploy. The dead letters are then replayable.
- **Replay**, once the consumer can handle them:

  ```bash
  docker compose -f infra/docker-compose.dev.yml exec -T redpanda sh -c \
    'rpk topic consume <topic>.dlq --brokers redpanda:9092 --offset start -f "%v\n" | \
     rpk topic produce <topic> --brokers redpanda:9092'
  ```

  **Check the key preservation before doing this in anger** — the command above does not carry keys,
  and everything on these topics is keyed by `vehicleId` or `rideId` so ordering per aggregate
  depends on it. For anything more than a handful, write the replay with keys.

- **A message that genuinely cannot be handled** (a test payload, a corrupt frame) → record it in the
  incident and let retention expire it.

---

## What not to do

- **Do not replay `telemetry.normalized.dlq` without fixing the cause.** The rows were refused by the
  system of record; replaying them produces the same rejection and another dead letter.
- **Do not replay without keys.** A `ride.completed` landing on a different partition from its
  `ride.accepted` can be consumed out of order, and dispatch-svc will release a driver that was never
  assigned.
- **Do not delete the DLQ topic to clear the alert.** It is the only copy.
