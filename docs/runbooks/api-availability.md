# Runbook — API and tracking-plane availability (ADD §13.3 rows 5, 6)

**Alerts:** `ApiAvailabilityBudgetBurning` · `TrackingPlaneAvailabilityBudgetBurning`
**Severity:** page · **Dashboard:** Grafana → `mageride-slo`, then `mageride-platform`

---

## First action

**Find the route, not the service.** The alert names a service; the fix almost always needs the
endpoint.

```promql
topk(10,
  sum by (service, http_route, http_response_status_code) (
    rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m])
  )
)
```

Paste that into Grafana → Explore → Prometheus. One route dominating is a bug; every route failing at
once is a dependency.

---

## What is measured

`5xx / total` over 6 h (and 1 h for the fast leg), per service.

**4xx is excluded on purpose.** Deny-by-default (AL-06) means a healthy platform answers a steady
stream of 401s, and a passenger booking twice legitimately gets a 409. Counting either would make the
platform's own correctness look like an outage.

| SLO | Objective | Budget | Pages at |
|---|---|---|---|
| API (registry, trips) | 99.9% monthly | 0.1% | 2% of budget in 6 h ≈ 2.43× ≈ **0.243%** 5xx |
| Tracking plane (`plane=position|edge`) | 99.5% monthly | 0.5% | 2% of budget in 6 h ≈ **1.215%** 5xx |

---

## Diagnose

1. **A dependency, not the service.** `/health/ready` pings Postgres, Redis and Kafka (D7' §5.1), so
   a service that is 5xx-ing while its readiness probe passes is failing on something narrower. Check
   `probe_success` on the same service — if the probe is also failing, go to
   [service-down.md](service-down.md).
2. **Which dependency.** In order of frequency: Postgres pool exhaustion
   ([postgres-saturation.md](postgres-saturation.md)), Redis
   ([redis-evictions.md](redis-evictions.md)), an upstream service over HTTP
   (`http_client_request_duration_seconds` by `service`), a third party (OnePay, Nominatim, FCM,
   LiveKit).
3. **A release.** Correlate with the deployment: `target_info{service_version=...}` changes at the
   moment a new build starts.
4. **Read the traces.** Grafana → Explore → Tempo, filter by `service.name` and `status=error`. A 5xx
   with a trace is usually one span deep.
5. **Read the problem details.** Every error is RFC 7807 with a stable `type` from the kernel's error
   registry, so Loki can count them:
   `{service="registry-svc"} | json | type != "" | line_format "{{.type}}"`.

---

## Fix

There is no generic fix — the alert is a pointer. But two are common enough to name:

- **Pool exhaustion under load** looks like 5xx on every route at once with normal upstream latency.
  Raise `Maximum Pool Size` on the service and check PgBouncer's `DEFAULT_POOL_SIZE` against
  Postgres's `max_connections`; the three have to be consistent.
- **A third party degrading** should be a 503 with a `dependency-unavailable` problem type, not a
  500. If it is arriving as a 500, that is a bug worth filing while you mitigate — the platform is
  meant to degrade visibly.

---

## What not to do

- **Do not count 4xx to "get a fuller picture" during the incident.** The SLI is defined; changing it
  mid-incident means the graph before and after are not comparable.
- **Do not restart the service reflexively.** If the cause is a dependency, the restart clears the
  connection pool and makes the first minute worse.
- **Do not treat a service with no traffic as healthy.** Zero requests is zero errors and a perfect
  ratio; that is what `ServiceDown` and the synthetic probes are for.
