# Runbook — WebSocket connection failures (ADD §13.3 row 7)

**Alert:** `WebSocketConnectFailureRateHigh` · **Severity:** page
**Dashboard:** Grafana → `mageride-fanout`

> ADD §13.3: WebSocket connection success rate 99%; alert on **5% failure over 5 min**.

---

## First action

**Split the failures by status code.** 401 and 503 are completely different incidents.

```promql
sum by (http_response_status_code) (
  rate(http_server_request_duration_seconds_count{http_route=~"/hubs.*"}[5m])
)
```

| Code | Cause |
|---|---|
| 401 | Token rejected — JWKS, clock skew, or the token not reaching the hub |
| 404 | The gateway route is wrong or fanout-svc's cluster address is stale |
| 502 / 503 | fanout-svc is down or refusing |
| 500 | The hub threw — read the trace |

---

## How bad this is

Degraded, not dark. `signalr-hub.md` §1.1 makes `GET /v1/nearby` the snapshot and resync path, so a
passenger who cannot hold a socket still gets a map — it just stops moving between polls. Drivers
lose live offers, which matters more.

---

## Diagnose

### 401 — the credential

The hub takes the ordinary 30-minute API access token (D-29) in the **`access_token` query
parameter**. That is SignalR's convention and unavoidable: a browser `WebSocket` cannot set an
`Authorization` header. It is scoped to `/hubs/live`, because anywhere else a token in a URL is a
token in a proxy log.

It is **never** the MQTT session JWT (E-02). A client sending the wrong one gets a uniform 401.

1. **JWKS reachable?** Every service validates RS256 against iam-svc's JWKS.
   `Jwt__JwksUrl` must resolve *from fanout-svc's container* — in the skeleton stack that is
   `http://iam-svc:5000/.well-known/jwks.json`, not the `app-services` path `.env.common.example`
   ships.
2. **Key rotation without overlap.** D7' §13 rotates the JWT signing key every 90 days with JWKS
   overlap. No overlap invalidates every live token at once, and the rate is 100% rather than 5%.
3. **Clock skew** on the fanout host — tokens fail as they age in.

### 502/503 — the path

`/hubs/{**remainder}` is proxied by the gateway to fanout-svc with **HTTP/1.1 pinned and a 30-minute
activity timeout** (C008). Both matter: HTTP/2 breaks the WebSocket upgrade, and a shorter timeout
disconnects idle passengers on a schedule, which shows here as a steady reconnect rate rather than a
spike.

```bash
docker compose -f infra/docker-compose.dev.yml exec -T api-gateway env | grep -i fanout
```

### A reconnect storm

Flat `kestrel_active_connections` with a high `kestrel_connection_duration_seconds_count` rate is
clients cycling. Usually a proxy or load-balancer idle timeout shorter than the hub's.

---

## Fix

- **JWKS** → restore reachability; tokens validate again with no client change.
- **Gateway route** → correct the cluster address and restart the gateway.
- **fanout-svc saturated** → scale it. There is no SignalR backplane, so replicas do not multiply each
  other's work; each covers its own connections.

---

## What not to do

- **Do not accept the MQTT session JWT at the hub** to "make clients work". E-02 keeps the two
  credentials separate on purpose: the MQTT token lives four hours or more so a failed refresh in
  poor coverage does not stop a ride's position stream, and it must not become a long-lived API
  credential.
- **Do not widen the query-parameter token hook past `/hubs/live`.**
- **Do not add a SignalR backplane to spread load.** Every replica would re-broadcast every batch and
  a passenger would receive one copy per replica.
