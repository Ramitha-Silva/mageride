# Runbook — part of the observability stack is down

**Alert:** `ObservabilityComponentDown` · **Severity:** page
**Dashboard:** Grafana → `mageride-observability-self`

> A silent alerting pipeline reads as a healthy platform. That is the worst failure mode
> observability has, and it is the reason this alert exists.

---

## First action

```bash
docker compose -f infra/observability/docker-compose.yml ps
docker compose -f infra/observability/docker-compose.yml logs --tail 60 <component>
```

**If Prometheus itself is the component that is down, you will not have received this alert.** The
only signal is Grafana showing no data. Check that first when something feels too quiet.

---

## What is lost, per component

| Component | While it is down |
|---|---|
| **Prometheus** | Everything. No rules evaluate, no alert fires, no dashboard has data. Metrics for the outage are lost — there is no buffering upstream. |
| **Alertmanager** | Every rule still evaluates and every one fires **into nothing**. Prometheus's own `ALERTS` series still shows them, so the Grafana alert tables are the fallback view. |
| **otel-collector** | Traces stop. Logs pushed over OTLP stop (Promtail's container-stdout path keeps working). **tcp-adapter's metrics stop entirely** — it has no HTTP surface to scrape and this is its only path. |
| **Tempo** | Traces are dropped at the collector after its retry budget. Span metrics and the service graph stop being remote-written. |
| **Loki** | Logs are dropped. Container stdout still exists in the Docker log driver, so `docker logs` remains. |
| **Grafana** | Only the view. Prometheus, Alertmanager and the rules are unaffected — an alert still pages. |

---

## Diagnose

1. **OOM.** Every component is capped (`mem_limit`) so that slim + observability fits beside a build
   on the 24 GB box. Prometheus at 1 g is the tightest, and head series is what drives it — check
   `prometheus_tsdb_head_series` on the self dashboard before raising the cap, because a cardinality
   explosion is the more likely cause than growth.
2. **Disk.** `prometheusdata`, `lokidata` and `tempodata` are named volumes on the same disk as the
   platform's Postgres. Retention here is deliberately short (15 d metrics, 7 d logs and traces) for
   that reason.

   ```bash
   docker system df -v | grep -E "prometheusdata|lokidata|tempodata|grafanadata"
   ```
3. **A bad config after an edit.** Prometheus refuses to start on an invalid rule file. Validate
   before restarting:

   ```bash
   bash infra/scripts/verify-observability.sh
   ```
4. **The collector has no shell and no healthcheck** — by design; it is the binary and nothing else.
   `up{job="otel-collector"}` from outside is its health signal, which is a better test anyway.

---

## Fix

- Restart the component. All state that matters is on a volume.
- Config error → fix and `curl -X POST http://127.0.0.1:9090/-/reload` (Prometheus is started with
  `--web.enable-lifecycle`), which avoids losing the head block to a restart.
- Cardinality → find it:

  ```promql
  topk(10, count by (__name__)({__name__=~".+"}))
  ```

---

## What not to do

- **Do not `down --volumes` to "start clean".** That discards the metric history of the incident you
  are in.
- **Do not disable rule groups to make Prometheus start.** Fix the rule; `promtool check rules` names
  the line.
- **Do not run the observability stack and the full lightweight production replica together on this
  box.** The root CLAUDE.md is explicit: the ~17–20 GB replica and a heavy build do not fit
  together, and observability adds ~1.6 GB on top.
