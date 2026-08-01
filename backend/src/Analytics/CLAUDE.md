# analytics-read-model (C061) — the admin dashboard's statistics filter

Stack: .NET 10 class library + Dapper over Npgsql. References `MageRide.Shared` (C002). **No Redis,
no Kafka, no MQTT, no HTTP surface.**

**Verify:** `dotnet test backend/src/Analytics.Tests -c Release`

## What this component is, and why it is a library

The read model behind `GET /v1/admin/dashboard/stats` and `stats.csv` (AL-38, US-24.7,
SCR-AP-002): daily rollups in Asia/Colombo, period aggregation with vs-previous-period deltas, the
real-time live block, and the CSV export feed.

**It is a library admin-bff (C062) references — not a service on a socket.** That is the one
structural decision of the component, and it is made on three things:

- **D3' gives the surface to admin-bff.** `getAdminDashboardStats` and `exportAdminDashboardStats`
  are operations of `backend/contracts/admin-bff.yaml`, and D6' §I-28.5 says that endpoint
  "aggregates `analytics.daily_metrics`" — not that it calls anything.
- **ADD §6's service table has no `analytics-svc` row.** The name appears only in AL-38's
  affected-areas column, beside `admin-bff`.
- **The build planner already recorded it.** `analytics-read-model` is in the list of components
  with **no `.yaml`**, because "none has an HTTP contract in D3'" (`build/progress.md`, C007
  finding (j)).

So there is no `analytics.yaml`, no container, no gateway route and no `MapAnalyticsEndpoints`.
C062 calls `AddMageRideAnalytics(configuration)` and maps its own contracted routes onto
`IDashboardStatsService`. An HTTP hop would have put a network boundary between admin-bff and a read
model living in the database it already holds a connection to.

| Seam | Used by | For |
|---|---|---|
| `AddMageRideAnalytics(configuration)` | admin-bff (C062) | everything, plus the rollup job |
| `AddMageRideAnalyticsQueriesOnly(configuration)` | a read-only replica, and this suite | the same without the timer |
| `IDashboardStatsService.GetAsync` | `GET /v1/admin/dashboard/stats` | `{period, range, kpis, deltaVsPrev, live}` |
| `IDashboardStatsService.ExportCsvAsync` | `GET /v1/admin/dashboard/stats.csv` | the download, byte for byte |
| `IDashboardStatsService.DaysAsync` | diagnostics | the materialised days behind a period |
| `IAnalyticsRollupService.RunDayAsync` · `RunRangeAsync` | an operator route, if C062 wants one | rebuild a day or a window |

| Table | Read | Written |
|---|---|---|
| `analytics.daily_metrics` (1405) | the period query | **this component only** |
| `rides.transitions` (0602) | which trips completed, and when | ride-svc |
| `fares.ride_payments` (1002) | which fares were collected | fare-svc |
| `iam.user_roles` (0101) | new riders, new drivers | iam-svc |
| `billing.daily_fee_charges` (1103) | daily-fee revenue | subscription-svc |
| `dispatch.driver_presence` (0701) | online drivers | dispatch-svc |
| `registry.documents` · `document_fields` · `onboarding_steps` · `fleets` | the three verification queues | registry-svc / fleet-svc |
| `support.tickets` (1303) | open tickets | support-svc |

## The two fences, and how each is held structurally

- **Derived, never authoritative — this component writes one table.** Held by the SQL and *proved*
  by `FenceTests`, which reflects over every SQL constant in the assembly and asserts that every
  data-modifying keyword targets `analytics.`, that the keyword count equals the schema-qualified
  target count (so an unqualified write cannot slip past), and that there is exactly one writing
  statement. The SQL is held in `const string` fields precisely so this is possible. Nothing here
  reads a replica DSN either — when one exists it belongs in the kernel's
  `INpgsqlConnectionFactory`, which is the only way this component reaches Postgres.
- **Asia/Colombo decides the day, not UTC (D-38).** Held by an absence: there is no `AT TIME ZONE`,
  no `date_trunc`, no `::date` and no `now()` anywhere in this component's SQL — `FenceTests`
  asserts all four. Every boundary is computed once by `MageRide.Shared.Time.BusinessCalendar` and
  passed in as a half-open UTC pair, so there is one implementation of the rule and it is the one
  that knows Sri Lanka changed offset in 1996 and again in 2006.

## Rules that are load-bearing

- **Completed trips come from the transition, not from the ride's state.** A ride never *rests* in
  `Completed` — ride-svc moves it to `PaymentPending` inside the same transaction (C022's
  `RideService.CompleteAsync`) — so `WHERE state = 'Completed'` would count nothing. Counting the
  terminal states instead would be worse: `Paid` / `CashSettled` are reached when the *money*
  settles, which can be the next day, and the cancel and no-show terminals are trips that did not
  happen. `rides.transitions` is append-only with one row per move, so the row where
  `to_state = 'Completed'` **is** the trip's end and its `ts` is when it ended.
- **Gross fare is one payment per completed ride, in R-05's terminal set.** A retry chain is several
  `fares.ride_payments` rows for one fare (D-10), so summing the table would bill the dashboard for
  every attempt; `DISTINCT ON (ride_id) … ORDER BY attempt_no DESC` takes the attempt that
  collected. The four states are exactly fare-svc's `RidePaymentStates.Terminal`, and
  `VocabularyTests` compares the two lists — which is the whole reason `Analytics.Tests` references
  `Fare.Api`. `Disputed` is a terminal of the ride and not of the money; `Overpaid` means a refund
  is owed, not that revenue doubled.
- **Gross fare is attributed to the day the trip ended, not the day the money arrived.** So
  "completed trips" and "gross fare" describe the same set of trips, which is the only way the two
  cards on one screen can be read together. The cost is that a metric day is not closed when the day
  ends — hence the lookback window, below.
- **New riders and new drivers are counted from the `iam.user_roles` grant.** `iam.users.role` is
  the account's *primary* role and it moves: a passenger who later signs up to drive would silently
  leave a past day's figure the next time that day was recomputed. iam-svc inserts grants
  `ON CONFLICT DO NOTHING` ("an idempotent retry is not a new decision", C026), so `granted_at` is
  written once and a past day's count never moves under the rollup.
- **Daily-fee revenue matches `fee_date` directly.** That column is already an Asia/Colombo `DATE`
  written by subscription-svc under D-13. Re-deriving the day from `charged_at` would disagree with
  the owning service for every fee charged near midnight — the read model would be *more* precise
  and *less* correct.
- **A day with no activity is materialised as a zero row.** Without it, "no row" would mean both
  "nothing happened" and "not rolled up yet", and a period sum could not tell a quiet Sunday from a
  job that has been down since Friday. This is also why the pass is one statement per day rather
  than one statement grouped by day: a grouped rebuild only writes the days that had activity.
- **The pass recomputes a window, not a day.** `RollupLookbackDays` defaults to 3 because a metric
  day is not closed at midnight: a cash fare confirmed the next morning, a driver-QR attestation
  claimed overnight (AL-47) or a late gateway callback (R-19) each change a figure after the fact.
  A day older than the window is rebuilt on demand through `RunRangeAsync`.
- **Idempotency is the primary key, not a guard.** One `INSERT … ON CONFLICT (metric_date) DO
  UPDATE`, with the five figures recomputed from the sources every pass. There is deliberately no
  delete-then-insert, which would leave a window where the dashboard reported zero for a day that
  had numbers.
- **`metric_date_tz_at` is written once and never moved.** Migration 1405 defines it as the instant
  the day was *first* rolled up — the D-38 audit companion for the business date — while
  `refreshed_at` is the last recompute. The `DO UPDATE` list omits it; including it would collapse
  two columns into one and lose the first answer.
- **The job is an interval and every replica runs it, with no lease.** Every pass is idempotent, so
  a lock would protect an operation that does not need protecting and would add a way for the
  dashboard to stop updating entirely when the lock holder dies badly. fleet-billing-svc's runner
  (C060) is written under the same rule. A pass that throws is logged and retried next tick, because
  an unhandled exception would end the `BackgroundService` for the process's lifetime — and a frozen
  dashboard looks exactly like a quiet week.
- **The live block is read at request time and is never rolled up.** D6' §I-28.5: it "bypasses the
  period filter". These are facts about *this instant*, and a materialised copy of any of them is a
  different question with a similar name — filter the dashboard to last March and the three cards
  must not change. Asserted directly in `LiveCountersTests`.
- **Online drivers is presence *and* freshness.** `dispatch.driver_presence` is the durable half of
  a state whose live half is a Redis hash with a 60 s TTL (R-08), so a driver whose app was killed
  leaves the row saying `AVAILABLE` for ever and the card would only ever go up. The cutoff is
  computed from `TimeProvider` and passed as a parameter rather than being `now()` in the SQL, so
  one clock decides and a test can state where the boundary falls. `ON_RIDE` and `OFFERED` count: a
  driver carrying a passenger is online.
- **Pending verifications is the sum of AL-39's three queues, counted by subject.** Driving licence,
  vehicle registration, fleet-org approval — which is what SCR-AP-003 is after the split, and the
  card on SCR-AP-002 links there. A licence with four doubtful fields is **one** driver to review;
  counting fields would tell an officer their queue was four times as deep as it is. **C063's own
  queue endpoints must use these three predicates**, or the card and the screen it opens disagree,
  which is worse than either being wrong on its own.
- **Open tickets counts `IN_PROGRESS` too**, and is expressed as "not `RESOLVED`" so a fourth status
  added to `support.tickets` later is counted rather than silently dropped. "Open" on an operations
  dashboard is the work outstanding, and a ticket an agent has picked up is still outstanding.
- **The previous period is arithmetic, not calendar.** `admin-bff.yaml#DashboardDeltas` says
  "percentage change against the immediately preceding period of the same length", so it is
  `[from − N, from − 1]`. A 31-day custom range starting mid-July compares against the 31 days
  before it — *not* "the same dates last month", which would have been 30 days and would have
  compared 31 days of trips against 30. A range spanning a month boundary is therefore a subtraction
  rather than a special case.
- **A delta with a zero base is `null`, not `0` and not `100`.** Growth from nothing has no
  percentage; every property of `DashboardDeltas` is optional in the contract precisely so the field
  can be absent. Both ends zero *is* 0 %. In the CSV it is an empty cell.
- **"Today", "this week" and "this month" are calendar-anchored and end today**, because that is
  what SCR-AP-002 labels them: on the 5th, "This month" is five days and not thirty-one. The
  alternative reading — a rolling 7 or 30 days — would make "This month" include days of the
  previous one.
- **An invalid query is a 400 naming the parameter**, never a silently substituted default. A
  `custom` range missing its dates that quietly answered for today would put the wrong number under
  the right heading, and the operator would have no way to tell.
- **The CSV and the JSON are rendered from one `DashboardStats`.** There is no second query and no
  second period resolution, so "exactly the figures the endpoint returns for the same query" is
  structural. The file states both ranges in its preamble, because percentages with no stated
  comparison window are unfalsifiable once the request that produced them is gone. Money stays in
  integer minor units under the contract's own `…Minor` names; invariant culture throughout, because
  a comma-decimal culture would split one number across two columns of a comma-separated file.
- **No user-facing string is composed here (D-26).** Nothing in this component is rendered to a
  passenger or a driver, and the admin surface is a back-office console whose labels are C104/C105's.
  The CSV's field names are contract identifiers, not copy.
- **This component enforces no authorization.** AL-06's deny-by-default and D-35's audit interceptor
  belong to admin-bff, where the caller's effective role set is known. This assembly answers
  questions; it does not decide who may ask them.

## Schema this component added

**None.** `analytics.daily_metrics` was landed by C005 as `db/migrations/1405__analytics_daily_metrics.sql`
with exactly the five figures AL-38 names, the `LKR` currency column and the D-38 `metric_date_tz_at`
companion. `migrate-verify.sh` is unchanged.

## Contract changes this component made

**None.** `admin-bff.yaml` already carries `getAdminDashboardStats`, `exportAdminDashboardStats` and
the `DashboardKpis` / `DashboardDeltas` / `DashboardLive` schemas (C007). The records in
`Domain/AnalyticsRecords.cs` are those schemas' field names one-for-one, so C062 serialises them
directly rather than reshaping.

## Not here, and named rather than stubbed

- **The endpoints.** admin-bff's (C062). See "why it is a library".
- **The RBAC gate and the audit event.** admin-bff's (AL-06, D-35).
- **`GET /v1/admin/dashboard`** — the *unfiltered* landing view (US-14.6). It is the same
  `DashboardKpis` + `DashboardLive` pair with no period, so C062 can serve it from
  `GetAsync("today", …)` or from a period of its choosing; this component takes no view on which,
  because no spec states one.
- **A read replica.** The fence says "derived from events/read replicas" and there is no replica DSN
  on this platform yet. When there is, it belongs in the kernel's `INpgsqlConnectionFactory` — which
  is the only way this component reaches Postgres, so nothing here changes.
- **An event-sourced rollup.** The fence's other half ("derived from events") would mean consuming
  `ride.events`, `wallet.events` and the rest into a projection. Recomputing from the source tables
  was taken instead because it makes the day's figures *re-derivable* rather than accumulated: a
  projection that missed a message is wrong until somebody replays the topic, whereas a recompute is
  correct on the next pass. It also makes the R-19 late-callback and AL-47 overnight-attestation
  cases ordinary rather than special.
- **Per-metric drill-down, heatmaps, cohorts, forecasting.** ADD §10 puts operator analytics on
  ClickHouse in Phase 3. This component is five figures a day and three live counts.

## Configuration

Every knob is documented at its declaration in `AnalyticsOptions` and in
`infra/env/.env.app.example` (under the `analytics-read-model` heading, beside admin-bff's, because
that is the process that hosts it). **D7' §4.2 gives this component no variables** — it predates
AL-38 — so every default below is argued rather than cited.

| Setting | Default | Where it comes from |
|---|---|---|
| `RollupEnabled` | on | **off ⇒ every period on the dashboard freezes** at whatever was last materialised; the live cards keep moving, which is what makes it hard to notice. Logged at ERROR |
| `RollupInterval` | 15 min | **no spec** — the coarsest staleness an operator watching a launch day would not notice. Also the window the "completes within its window" test measures against |
| `RollupLookbackDays` | 3 | **no spec** — wide enough for a fare that settles the next morning, narrow enough that a pass stays five small aggregates |
| `MaxBackfillDays` | 400 | **no spec** — a bound, not a working limit: a year plus slack, and a refusal for a typo |
| `WeekStartsOn` | Monday | **spec gap (C061)** — D2 SCR-AP-002 and US-24.7 say "This week" and no document says which day that is. ISO 8601, as a setting because the answer is a local convention |
| `MaxRangeDays` | 366 | **no spec** — a year and a day, so "the last 12 months" and "this year" both fit |
| `PresenceFreshness` | 2 min | **no spec** — two missed R-08 heartbeats (that Redis hash has a 60 s TTL) |

`ConnectionStrings:Postgres` is required, through the kernel. There is no `ConnectionStrings:Redis`
and there must not be: nothing here is on a hot path, and a cached KPI would be a second opinion
about a number the rollup is already a derived copy of.
