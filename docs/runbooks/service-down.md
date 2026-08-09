# Runbook — a service is down or unreachable

**Alerts:** `ServiceDown` · `TargetDown` · `SyntheticProbeFailing` · `SyntheticProbeSlow`
**Severity:** page (`TargetDown` and `SyntheticProbeSlow`: ticket)
**Dashboard:** Grafana → `mageride-platform`, `mageride-observability-self`

---

## First action

```bash
docker compose -f infra/docker-compose.dev.yml ps
docker compose -f infra/docker-compose.dev.yml logs --tail 100 <service>
# production
kubectl -n mageride get pods -o wide
kubectl -n mageride logs deployment/<service> --tail 100 --previous
```

`--previous` is the important flag: a container in a crash loop has already replaced the logs that
explain why.

---

## Which alert you have

| Alert | Means | Urgency |
|---|---|---|
| `ServiceDown` | A `platform-*` scrape target has not answered for 2 min | Page |
| `TargetDown` | *Any* target, down for 15 min | Ticket — see below |
| `SyntheticProbeFailing` | Blackbox cannot reach the endpoint at all | Page |
| `SyntheticProbeSlow` | Readiness probe taking over 2 s | Ticket |

**`TargetDown` is deliberately slow and warning-only.** `prometheus.yml` lists *both* deployment
shapes — one container per service (`platform-services`, the shape the code is actually built in) and
the D7' §2.1 composition hosts (`platform-composed`: `app-services`, `hot-path`, `fanout`). Whichever
is running, the other's targets are legitimately down. `ServiceDown` is the paging version.

---

## Why the synthetic probe matters separately

An availability SLO computed from traffic cannot distinguish "no errors" from "no traffic", and at
03:00 in Colombo it is mostly the second. `probe_success` is what says the platform is reachable when
nobody happens to be asking.

A probe that fails while `/metrics` is still being scraped is informative: the process is alive and
`/health/ready` is refusing, which means a dependency (D7' §5.1 pings Postgres, Redis and Kafka).

---

## Diagnose

1. **Crash loop.** `Restarts` climbing in `kubectl get pods`, or `docker ps` showing a recent
   `Up 12 seconds` on a service that has been deployed for hours. Read `--previous`.
2. **Failed start-up validation.** Several services refuse to start rather than half-work, and each
   says which key is missing:
   - iam-svc without `Mqtt__SessionTokenSecret`
   - ride-svc without `Ride:PhoneHashKey` / `Ride:OtpPepper` outside Development
   - any service with a malformed `Jwt__SigningKeyPem`
3. **Readiness never satisfied.** `/health/ready` is failing on a dependency; `/health/live` will
   still answer. Check the dependency's own alert.
4. **OOM.** `dotnet_process_memory_working_set_bytes` climbing to the container's `mem_limit` before
   each restart. The compose files cap every container.
5. **It was never scraped.** A brand-new service that has never appeared: it is missing from
   `infra/observability/prometheus/prometheus.yml`. That is a real defect —
   `verify-observability.sh` checks the compose files against the scrape config for exactly this.

---

## Fix

- Restart, having read the logs first.
- If the cause is configuration, fix it in `infra/env/.env.*` (dev) or the Secret/ConfigMap
  (production) — not with `docker exec`, which is lost on the next restart.
- If the service is one of the composition hosts and the other shape is running, silence and correct
  the scrape config instead.

---

## What not to do

- **Do not restart in a loop hoping it settles.** A service that refuses to start is telling you
  something in its first ten log lines.
- **Do not remove the down target from `prometheus.yml` to clear the alert.** That is the one change
  that makes the next real outage invisible.
- **Do not scale to zero to stop the paging.** Use a silence, with an expiry.
