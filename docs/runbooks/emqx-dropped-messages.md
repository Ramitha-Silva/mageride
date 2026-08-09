# Runbook — EMQX is dropping messages (ADD §13.2)

**Alert:** `EmqxMessagesDropped` · **Severity:** page
**Dashboard:** Grafana → `mageride-emqx`

---

## First action

**Find out which kind of drop**, because the four have different causes and only one of them is the
platform's fault.

```bash
curl -s http://127.0.0.1:18083/api/v5/prometheus/stats | \
  grep -E "^emqx_(messages_dropped|delivery_dropped)"
```

| Counter | Meaning | Whose problem |
|---|---|---|
| `emqx_messages_dropped_no_subscribers` | Published to a topic nobody is subscribed to | **The platform's** — mqtt-bridge is not subscribed, or its shared subscription is wrong |
| `emqx_delivery_dropped_queue_full` | A subscriber is too slow and its queue overflowed | The platform's — mqtt-bridge is not keeping up |
| `emqx_delivery_dropped_expired` | Message TTL elapsed before delivery | Usually a consequence of the above |
| `emqx_delivery_dropped_too_large` | Payload over the limit | A device sending something it should not |

---

## Why this pages

A dropped publish is invisible from **both** ends: the device got its QoS 1 PUBACK from the broker,
and nothing downstream ever saw the message, so no consumer-lag alert and no error rate moves. It is
a position that silently never happened.

---

## `no_subscribers` — the common one

mqtt-bridge subscribes with a shared subscription (`$share/posGroup/…`). Two ways it goes wrong:

1. **The bridge is down or not subscribed.**

   ```bash
   docker compose -f infra/docker-compose.dev.yml exec -T emqx emqx ctl subscriptions list | head
   ```

2. **`Emqx__SharedSub` interpolated to an empty string.** This is a real and recurring trap, called
   out in `infra/CLAUDE.md`: compose interpolates `env_file` values, so a literal `$` must be written
   `$$` or it silently becomes empty in the container. `slim-verify.sh` fails on any "variable is not
   set" warning for this reason.

   ```bash
   docker compose -f infra/docker-compose.dev.yml exec -T hot-path env | grep -i sharedsub
   ```

   An empty value means the bridge subscribed to nothing and every device publish is dropped with no
   subscribers — which is exactly this alert, at 100%.

---

## `queue_full` — the bridge is behind

The broker is producing faster than mqtt-bridge consumes. Check `mageride_mqtt_bridge_forwarded_total`
against EMQX's `messages_received` on the dashboard; the gap is the drop.

- Scale `hot-path` / `mqtt-bridge`. The shared subscription distributes across members, so replicas
  help directly.
- Check whether Redpanda is refusing produces — the bridge cannot drain if it cannot forward
  ([redpanda-partitions.md](redpanda-partitions.md)).
- Check T-05 replay throttling: a fleet draining a backlog is bounded to 20/s per device, and that
  waiting is intentional (`mageride_mqtt_bridge_replay_wait_milliseconds`), but it consumes broker
  queue while it happens.

---

## `too_large`

A device is sending oversized payloads. The canonical position sample is small; anything large is a
tracker firmware problem or a diagnostic dump on the wrong topic. Identify the client id from the
broker log and check `registry.vehicles` for the fleet.

---

## What not to do

- **Do not raise the queue limits to stop the drops.** That converts dropped messages into memory
  pressure on the broker, and an OOM-killed EMQX drops *everything*.
- **Do not disable the shared subscription** in favour of a plain one. Every bridge replica would
  then receive every message, and the platform would write each position to `telemetry.raw` once per
  replica.
- **Do not treat `no_subscribers` as harmless because "nobody wanted it".** On this platform every
  `veh/+/pos/live` publish has exactly one intended subscriber, and it is the bridge.
