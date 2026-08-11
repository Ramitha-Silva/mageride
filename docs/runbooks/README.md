# MageRide runbooks

Every alert in `infra/observability/prometheus/rules/` carries a `runbook_url` pointing at a file in
this directory, and `infra/scripts/verify-observability.sh` **fails if that file does not exist**. A
page can never arrive with nothing to do behind it.

## How these are written

Each one opens with **First action** — a single thing to do inside the first minute, before any
diagnosis. That is deliberate: the person reading this has been woken up, and the most expensive
minute of an incident is the one spent deciding where to start.

Then: what the alert actually measures, how to confirm it, the likely causes in the order they are
worth checking, and — the section that is easiest to skip and hardest to recover from — **what not to
do**.

## Conventions

- **Never `psql` a fix into a live table.** Every manual state change goes through `admin-bff`, which
  writes an audit row (D-35). ADD §13.4 says this outright for stuck rides and it is true for all of
  them.
- **`docker compose -f infra/docker-compose.dev.yml …`** in dev and on the replica;
  `kubectl -n mageride …` in production (DOKS, Singapore).
- Dashboards are at `http://127.0.0.1:3000` in dev — the UID is given in each runbook.
- Traces: Grafana → Explore → Tempo. A log line's `TraceId` is a link; a latency spike on a panel has
  an exemplar behind it.

## Index

### ADD §13.3 — SLO burn (latency and availability)

| Runbook | Alerts |
|---|---|
| [position-e2e-latency.md](position-e2e-latency.md) | `PositionE2ELatencyBudgetBurning`, `…Slowly`, `PositionE2ELatencyP99Breached`, `PositionPipelineSilent`, `MqttBridgeFailing` |
| [sos-dispatch-latency.md](sos-dispatch-latency.md) | `SosDispatchLatencyBreached`, `SosDispatchFailing` |
| [api-availability.md](api-availability.md) | `ApiAvailabilityBudgetBurning`, `TrackingPlaneAvailabilityBudgetBurning` |
| [service-down.md](service-down.md) | `ServiceDown`, `TargetDown`, `SyntheticProbeFailing`, `SyntheticProbeSlow` |
| [websocket-connect-failures.md](websocket-connect-failures.md) | `WebSocketConnectFailureRateHigh` |
| [payment-callback-latency.md](payment-callback-latency.md) | `PaymentCallbackResolveBudgetBurning`, `RideStuckPaymentPending` |
| [offer-push-latency.md](offer-push-latency.md) | `OfferPushLatencyBudgetBurning` |
| [accept-resolution-latency.md](accept-resolution-latency.md) | `AcceptResolutionLatencyBudgetBurning` |
| [voip-call-setup.md](voip-call-setup.md) | `VoipCallSetupSlow` |

### ADD §13.3.1 — stuck-state business SLOs (R-20)

| Runbook | Alerts |
|---|---|
| [ride-stuck.md](ride-stuck.md) | `RideStuckMatching`, `RideStuckOffered`, `RideStuckAccepted`, `RideStuckDriverArrived`, `RideStuckInProgress`, `StuckStateMetricsMissing`, `RideStuckDetectionRateHigh` |
| [payments-overpaid.md](payments-overpaid.md) | `PaymentsOverpaidBacklog` |
| [document-expiry-job.md](document-expiry-job.md) | `ExpiredDocumentsStillDispatching` |

### ADD §13.4 — infrastructure

| Runbook | Alerts |
|---|---|
| [consumer-lag.md](consumer-lag.md) | `ConsumerLagSecondsHigh`, `ConsumerLagMessagesWarning`, `ConsumerLagMessagesCritical` |
| [emqx-auth-failures.md](emqx-auth-failures.md) | `EmqxAuthFailureRateHigh` |
| [emqx-dropped-messages.md](emqx-dropped-messages.md) | `EmqxMessagesDropped` |
| [redis-evictions.md](redis-evictions.md) | `RedisEvictions`, `RedisMemoryHigh`, `RedisDown` |
| [postgres-replication-lag.md](postgres-replication-lag.md) | `PostgresReplicationLag` |
| [postgres-saturation.md](postgres-saturation.md) | `PostgresConnectionSaturation`, `PostgresLongRunningQuery`, `PostgresDown` |
| [ride-timer-backlog.md](ride-timer-backlog.md) | `RideTimerBacklogHigh` |
| [outbox-lag.md](outbox-lag.md) | `OutboxDispatchLagHigh`, `OutboxPublishFailing` |
| [redpanda-partitions.md](redpanda-partitions.md) | `RedpandaPartitionsUnavailable`, `RedpandaUnderReplicatedPartitions` |
| [dead-letter-queue.md](dead-letter-queue.md) | `DeadLetterTopicReceiving` |
| [fanout-visibility.md](fanout-visibility.md) | `FanoutVisibilityFilterInert` |
| [observability-down.md](observability-down.md) | `ObservabilityComponentDown` |

### Delivery (C124) — not alert-driven

No alert points at these three. They are here because every runbook above can end in one of them,
and because `verify-observability.sh` only requires that an alert's `runbook_url` exists — it does not
forbid a runbook with no alert.

| Runbook | When |
|---|---|
| [deploy.md](deploy.md) | bootstrapping a cluster, and understanding the path a commit takes. Nothing needs doing for a normal deploy. |
| [rollback.md](rollback.md) | the answer to a page is "the release we just shipped". **Never `kubectl rollout undo` — ArgoCD's selfHeal reverts it.** |
| [secret-rotation.md](secret-rotation.md) | D7' §13's schedule, as procedures. The JWT key needs a 30-minute overlap; the MQTT session secret has none and is a brief total outage. |
| [replica-operations.md](replica-operations.md) | the lightweight production replica (C125): bring up, bring down, back up, restore, and the eight failures it actually produces. **Synthetic data only**, and the box it runs on is the box that builds the repository. |
