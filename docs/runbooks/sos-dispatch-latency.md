# Runbook — SOS dispatch (D-33, ADD §13.3)

**Alerts:** `SosDispatchLatencyBreached` · `SosDispatchFailing`
**Severity:** page, immediately · **Dashboard:** Grafana → `mageride-money-safety`

> This is the only alert on the platform whose subject is a person in danger. §13.3 gives it no burn
> rate and no smoothing: **any 5-minute window over 5 s p99 pages on-call immediately**, and
> Alertmanager routes it ahead of everything else with `group_wait: 0s`.

---

## First action

**Find out whether the alerts are arriving at all.** Latency is the lesser failure; an SOS that
reached no gateway is the greater one, and the two alerts can fire independently.

```bash
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT id, ts, source, sms_status, primary_gateway, secondary_gateway,
           dispatched_at, dispatched_at - ts AS took
      FROM safety.sos_events
     WHERE ts > now() - interval '30 minutes'
     ORDER BY ts DESC;"
```

`sms_status` is one of `Dispatched`, `Failed`, `NoContact`. Any row with `dispatched_at IS NULL` in
the last half hour is an alert that has not been handed to anybody — **treat that as an active
incident with a person at the other end of it**, and get the contact number from the row so somebody
can be called manually while the platform is fixed.

---

## What is measured

`safety.sos_events.ts` is the button tap; `dispatched_at` is the moment the message was handed to the
primary gateway. Two columns, deliberately, so the interval survives the request — safety-svc's own
fence. C119 added `mageride_sos_dispatch_latency_milliseconds` recording the *same* interval where
Prometheus can watch it, because a column cannot page anybody during an incident.

The SLO is computed over `outcome="dispatched"` alone. An alert that failed at the gateway in 200 ms
is a worse outcome than one that took six seconds and arrived, and averaging them together would let
a rising failure rate pull the latency graph *down*.

---

## `SosDispatchLatencyBreached` — the alert arrived, late

### Likely causes, in order

1. **notification-svc's inline path is slow.** D-33's five seconds cannot be a property of a worker's
   drain rate, which is why safety-svc calls notification-svc's **inline** dispatch and not its
   queue. Check notification-svc's own latency on `mageride-platform` (filter `service` to it) and
   its upstream gateway calls (`http_client_request_duration_seconds`).
2. **The SMS gateway is slow.** D6' §7.3 names two families and D-33 hands the message to both at
   once, resolving on whichever answers — so this is the slower of the two dragging the pair. The
   `primary_gateway` / `secondary_gateway` columns record which was tried and which delivered.
3. **`Safety:NotificationTimeout` is too generous.** It defaults to 4 s, bounded by D-33's five and
   deliberately *not* by D6' §8.3's 2 s, because the alert is delivered on that call. A value above
   5 s makes the SLO unachievable by configuration.
4. **The share link mint is on the path.** `TryMintSosLinkAsync` runs before the send; if
   `Safety:ShareBaseUrl` points at something slow it is inside the budget. A failed mint falls back
   to a `geo:` URI and costs nothing.

### Fix

- A slow gateway: fail over by swapping the provider order in notification-svc's configuration. Both
  are tried anyway, so this changes which one the budget waits on.
- notification-svc saturated: scale it. The inline path shares the process with the queued one.

---

## `SosDispatchFailing` — the alert reached nobody

Any non-zero share pages. Look at the `outcome` label:

| `outcome` | What happened | What to do |
|---|---|---|
| `failed` | Both gateways refused or timed out | The gateway credentials or the provider. Check notification-svc's logs for the provider error; the SOS row's `sms_status` is `Failed`. |
| `no_contact` | AL-13's emergency contact was missing | `Safety:RequireEmergencyContact` is off. With it on, the user is refused at the API with `400 no-emergency-contact` *before* a row exists, which is the correct behaviour — they can be sent to the "add a contact" screen while the alert still matters. |

**A configuration that makes this silent.** safety-svc's `NotificationBaseUrl` /
`NotificationInternalApiKey` unset means **no SOS is ever dispatched** — the alert is still recorded
and still announced on the admin live feed, and nothing else happens. The service says so at
start-up; search Loki for the start-up warning.

```bash
docker compose -f infra/docker-compose.dev.yml logs app-services 2>&1 | grep -i "sos\|notification" | head -20
```

---

## Confirm the fix

Raise a test SOS against a staging contact and watch the row:

```bash
# The interval the SLO is written about, straight from the system of record.
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT percentile_disc(0.99) WITHIN GROUP (ORDER BY dispatched_at - ts) AS p99
      FROM safety.sos_events
     WHERE dispatched_at IS NOT NULL AND ts > now() - interval '1 hour';"
```

This and `mageride:sos_dispatch:p99` must agree. If they do not, the histogram and the column have
diverged and that is its own bug.

---

## What not to do

- **Do not silence this alert to stop the noise.** It fires on a single bad window because the event
  is a panic button; there is no volume of them that makes one less important.
- **Do not move the SOS onto notification-svc's queue** to "smooth" the latency. The inline call is
  the fence: the budget must not depend on how many ride offers happen to be in front of it.
- **Do not raise `Safety:NotificationTimeout` above 5 s.** That does not fix the SLO, it makes it
  unmeetable while hiding the breach.
- **Do not assume a green latency panel means the button works.** `SosDispatchFailing` and
  `SosDispatchLatencyBreached` are separate for exactly this reason — an SOS that goes nowhere looks
  exactly like one that worked: the button animates, the row is written, the response is a 200.
