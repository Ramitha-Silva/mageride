# observability-stack (C119) — Prometheus, Loki, Tempo, Grafana, Alertmanager, OTLP

Stack: Docker Compose. No application code. ADD §13 is normative for this component and wins over
this file and over the configuration.

**Verify:** `docker compose -f infra/observability/docker-compose.yml config && bash infra/scripts/verify-observability.sh`

## What this component is

The alerting the platform is specified against — ADD §13.3's SLOs, §13.3.1's stuck-state business
SLOs (R-20) and §13.4's seven runbook triggers — plus the dashboards and the OTLP plumbing that make
them readable. **49 alert rules, 30 recording rules, 11 dashboards, 24 runbooks.**

```
infra/observability/
  docker-compose.yml                 the stack; project `mageride`, network `mageride_mr`
  prometheus/prometheus.yml          scrape config, ADD §13.2 row by row
  prometheus/rules/
    recording.slo.yml                one SLI per §13.3 SLO, plus the human quantiles
    recording.stream.yml             consumer lag, computed (see below)
    alerts.slo.yml                   §13.3 burn-rate alerts
    alerts.business.yml              §13.3.1 stuck-state pages (R-20)
    alerts.infrastructure.yml        §13.4's seven bullets + the §13.2 planes
  prometheus/tests/*.test.yml        `promtool test rules` — the simulated stuck ride
  alertmanager/                      routing, inhibition, notification templates
  loki/ promtail/ tempo/             logs and traces
  otel-collector/config.yaml         the single OTLP door
  blackbox/blackbox.yml              ADD §13.1's synthetic probes
  grafana/provisioning/ dashboards/  datasources and 11 boards, file-provisioned, read-only
docs/runbooks/                       one per alert, each opening with a First action
infra/scripts/
  observability-up.sh / -down.sh     wrappers
  verify-observability.sh            the C119 definition of done, 100+ checks
```

## The decisions that are load-bearing

- **Tempo, not Jaeger.** ADD §13.1's MVP column says Jaeger and its own scale column says Tempo;
  D7' §12 says "Loki + Tempo (OTLP)" outright. Running Jaeger now would mean two trace stores, two
  query APIs and two Grafana datasources for the same spans.
- **`docker compose config` must pass standalone**, because that is half the verify command. So the
  file declares its own network (`mr` → `mageride_mr`, non-external) rather than requiring the dev
  stack to exist. Same project name as the dev stacks, so it is *more containers on one network* and
  never a second copy of anything. **Bring observability down before the platform** — compose will
  not remove a network that still has endpoints.
- **A target that is not running is a target that is DOWN, and that is the correct reading.**
  `prometheus.yml` lists both deployment shapes — one container per service (the shape the code is
  actually built in, `docker-compose.skeleton.yml`) and D7' §2.1's composition hosts
  (`app-services`, `hot-path`, `fanout`). Whichever is running, the other is legitimately down, which
  is why `TargetDown` is warning-only at 15 minutes. `ServiceDown` is the paging version and it is
  qualified by `max_over_time(up[6h]) > 0` — **page for a target that was serving and has stopped,
  never for a shape that was never deployed.** A job-name regex cannot express that, and the first
  version tried: both jobs are named `platform-*`, so the shape that was not running paged for ever,
  naming a service that was up (`fanout` and `fanout-svc` share a `service` label).
- **The service identity is a scrape label, not a metric label.** The OpenTelemetry Prometheus
  exporter puts `service_name` on `target_info` and on nothing else — pinned by
  `MageRide.Shared.Tests/Observability/PrometheusExpositionTests`. So every job stamps `service`
  itself and every rule and dashboard groups by it. A service added to a compose file and not to
  `prometheus.yml` is invisible, and `verify-observability.sh` compares the two for that reason.
- **Consumer lag is computed, not scraped.** Redpanda v24.2 publishes no lag metric: verified against
  the pinned `redpandadata/redpanda:v24.2.26`, `/public_metrics` carries
  `redpanda_kafka_consumer_group_committed_offset` and `redpanda_kafka_max_offset` and nothing named
  `..._lag_...`. `recording.stream.yml` does the join once so eight rules do not each get the vector
  matching wrong — and the failure mode of getting it wrong is a rule that matches nothing, silently.
- **Every latency SLO is recorded twice**: an `error_ratio` (the share of events outside the target,
  which is what a burn rate can be computed against) and a quantile (the number a human reads). A
  `histogram_quantile` cannot be burned against a budget.
- **The SOS alert has no smoothing.** §13.3: "any 5-min window > 5 s p99 pages on-call immediately."
  `for: 0s`, `group_wait: 0s`, its own Alertmanager route ahead of everything else. It is the only
  alert on the platform whose subject is a person in danger.
- **The stuck-state thresholds are inside the metric, and every gauge is published by the service
  that owns the table.** ride-svc's `StuckStateObserver` publishes `count(rides WHERE state=S AND
  age > T)` for six of §13.3.1's rows — its own formula — so the rules are `> 0` plus a `for:` taken
  from §13.3.1's own "Page" column (`+2m` on the two rows that say "after 12 min" / "after 7 min",
  `1m` on the rest for its own word "sustained"). The other two are fare-svc's `OverpaidGauge` and
  registry-svc's `ExpiredDocumentsGauge`, both on the kernel's `ScrapedGauges`. They were briefly in
  the analytics read model instead; that was wrong twice over — neither query actually spans two
  bounded contexts, and the read model's only host (admin-bff) is not a scrape target in the
  skeleton shape, so both pages would have been permanently silent there.
- **No `clamp_min` on any SLI denominator.** A clamped `0/0` records `1 - 0 = 1`, so an idle
  histogram reads as a 100% error rate and the 14× burn alert pages over a platform that is simply
  quiet. Unguarded, `0/0` is NaN and every comparison against it is false. `RedisMemoryHigh` fired
  permanently against the live dev stack on the same mistake before this was understood, which is
  why the verify runs the rules against a real stack and not only through `promtool`.
- **Grafana is provisioned from files and read-only in the UI** (`allowUiUpdates: false`). A
  dashboard edited in the browser lives in the container's volume and dies with it.
- **Alertmanager's receivers are webhooks with the URL from the environment.** A PagerDuty routing
  key is a secret, `.env*` is gitignored, and the production configuration is the commented block at
  the foot of `alertmanager.yml` (D7' §12/§13: Vault → External Secrets Operator).

## What the verify actually proves

`verify-observability.sh` runs in nine phases; the first six are static (no containers, a few
seconds) and are the ones that catch the failures nothing else would:

| Phase | Proves |
|---|---|
| 1 | every config file parses, under the pinned image's own `promtool` / `amtool` |
| 2 | every §13.3, §13.3.1 and §13.4 row has a rule, **and the thresholds are the spec's** |
| 3 | **a simulated stuck ride fires the correct alert within its window** — `promtool test rules` |
| 4 | every alert has a runbook that exists, and every runbook opens with a First action |
| 5 | every `mageride_*` series a rule names is one the C# actually declares |
| 6 | every app container in a compose file is scraped, and every §13.2 plane has a dashboard |
| 7–8 | the stack comes up, loads every rule, provisions every datasource and board, and the §13.2 golden signals are present |
| 9 | **OTLP end to end** — a real MageRide service's trace reaches Tempo through the collector |

Phase 5 is the one worth understanding. The metric inventory is **derived from the C# source at
verify time** — `MageRideDiagnostics`, `AdapterDiagnostics` and `GatewayDiagnostics` — applying the
same name mangling `PrometheusExpositionTests` pins. A rule written against a metric that does not
exist loads happily and evaluates to an empty vector for ever; nothing else in this repository would
notice.

The four SLOs ADD §13.3 defines that no service instruments yet are an explicit list in that phase,
and the list is self-cleaning: the check fails if an entry has since been implemented.

Env: `VERIFY_OBS_STATIC=1` (phases 1–6 only) · `VERIFY_OBS_KEEP=1` · `VERIFY_OBS_REUSE=1` ·
`VERIFY_OBS_NO_BUILD=1`.

## Known gaps (all in the C119 handoff)

- **Four §13.3 SLOs have rules and no data**: offer push (row 8), atomic accept (row 9), payment
  callback resolve (row 3), VoIP call setup (row 4). Each needs a histogram in the service that owns
  it; the recording rules already name the metric, and each runbook says where to record it.
- **No synthetic MQTT probe.** ADD §13.1's "Custom MQTT probe" would need a real signed device
  session and a SignalR client — a continuously-running e2e test, not a config file. The blackbox TCP
  probe covers listener reachability, and `mageride_positions_e2e_latency_milliseconds` measures the
  same journey over real traffic (which is strictly more informative except when there is none).
- **No HAProxy metrics.** Its Prometheus endpoint needs `http-request use-service
  prometheus-exporter` in `infra/deploy/haproxy.cfg`, which is C009's file, and §13.2 does not list
  the edge among the golden signals.
- **No PgBouncer exporter.** Pool saturation is visible from the Postgres side, which is what §13.2
  names.
- **ride-svc's `StuckStateObserver` predates `ScrapedGauges` and should adopt it.** It is the third
  copy of the scope/timeout/fail-to-zero scaffolding, and its `Dispose` has the same inert
  `_gauges.Clear()` the other two were fixed out of — clearing a local list unpublishes nothing,
  because the instruments belong to the static meter. Harmless in production (one host per process)
  and a real leak in an integration suite that builds several. C032's file; not changed here.
- **`pg_replication_lag_seconds` is 0 on the MVP's single Postgres.** The alert is guarded by
  `pg_replication_is_replica == 1` and becomes live with the first standby (C132).
