# End-to-end suites — `tests/E2E`

.NET 10 + xUnit v3 + Testcontainers. Four suites, one assembly:

| Category | Component | What it drives |
|---|---|---|
| `ModeC` | C120 | the on-demand ride: request → match → offer → accept → complete → pay |
| `ModeAB` | C121 | the Mode A/B journey, the hardware tracker plane and fleet operations |
| `ProxyPackage` | C122 | proxy booking, package delivery, and the six no-login `SCR-WT` web pages |
| `Money` | C123 | every path money takes: the daily fee, the wallet, the fare, the subscription |

**Verify (from the repository root):**

```
dotnet test tests/E2E -c Release --filter Category=ModeC          # 50 tests, ~2 m
dotnet test tests/E2E -c Release --filter Category=ModeAB         # 30 tests, ~2 m 35 s
dotnet test tests/E2E -c Release --filter Category=ProxyPackage   # 20 tests, ~45 s
dotnet test tests/E2E -c Release --filter Category=Money          # 36 tests, ~1 m
```

All four also run inside `dotnet test backend/MageRide.sln`, which is what CI executes — the project
is in the solution.

`backend/contracts/*.yaml` is normative; so is `specs/`. Where a suite and a service disagree, the
service is not automatically right — but neither is the test. Read the spec anchor.

## What "end to end" means here

**Services, running.** Every service on the path is started through its own `XApplication.Build` on a
real socket, talking to its neighbours over real HTTP, gRPC, Kafka and MQTT, against a real Postgres,
Redis, Redpanda and EMQX from the TestKit. C120 runs five; C121 runs nine; C122 runs ten.

**Every background worker on.** Outbox dispatchers, `ride.events` and `telemetry.*` consumers, R-04
and trip-state timer sweeps, EMQX subscriptions, the tracker listeners, the fan-out pumps. A scenario
acts and then **waits**. Nothing in this assembly calls `IDispatchService`, `ISessionService`,
`IModeBAccessService` or any other service type to move state along — if a scenario needed one, the
fence says it is not an E2E.

**Composed, not deployed** — the same choice C118 made and for the same reason.
`infra/docker-compose.dev.yml` cannot be brought up (`app-services`, `hot-path` and `fanout` have no
Dockerfile and `dev-up.sh full` refuses with the list), and the root `CLAUDE.md` forbids running the
~17–20 GB replica beside a build on this host. What is missing is the container boundary; the seams
— broker, socket, transaction, consumer group — are the real ones. When C124/C125 land the images
this gains a second transport and no assertion changes.

**One collection at a time.** `[assembly: CollectionBehavior(DisableTestParallelization = true)]` in
`ModeAbCollection.cs`: each fleet resets global state in its own containers at start-up (C120 flushes
Redis; C121 truncates the tracker and session planes), and two running at once would look like a
passenger losing an entitlement mid-scenario rather than like a race.

**Money is asserted on the ledger, never on a balance alone.** C123's fence — "after every scenario
the double-entry ledger must balance to zero" — is enforced by `MoneyScenario.RunAsync` rather than by
the scenarios, so a test added to that suite is covered by it without its author remembering. See
`MoneyFleet.AssertLedgerBalancedAsync` for what "balanced" means in three statements.

**One thing every stand-in in this assembly has in common.** `TrackerDevice` writes the bytes
firmware would write and `SmsGateway` answers the way Notify.lk answers — both stand in for the
*outside* of the platform, never for a part of it. Nothing in here stands between two MageRide
components, which is what the no-stub fence actually says.

## C120 — the Mode C ride (`Category=ModeC`)

**Five services:** ride-svc, reputation-svc, dispatch-svc, fare-svc, fanout-svc.

| File | What it proves |
|---|---|
| `Scenarios/HappyPathScenario.cs` | request → match → offer → accept → arrive → start → complete → pay, and both R-18 recovery reads |
| `Scenarios/CancellationMatrixScenario.cs` | one test case per cell of the §11.12 matrix, plus the D-05 cross-trip settlement and AL-16's three-cancel disable |
| `Scenarios/MatrixCoverage.cs` | the ratchet: every cell is driven or accounted for, and the one gap is asserted *as* a gap |
| `Scenarios/ConcurrentAcceptScenario.cs` | ADD §11.11 — N drivers, one offer, exactly one winner, 100 times |
| `Scenarios/OfflineGraceScenario.cs` | R-15/R-16 — the four windows, the flap, and the re-plan from the same instant |
| `Scenarios/DispatchTimeoutScenario.cs` | US-6A.11 — Matching rests, `ExpiredNoDriver` at 120 s |
| `Scenarios/DirectionalTravelScenario.cs` | DT-02/DT-08 filtering and both halves of DT-06 |

### The decisions

**One fleet for the whole assembly.** Every scenario class joins `ModeCCollection`, so `ModeCFleet`
starts the five services once. That is mostly about cost — but it is about correctness too:
dispatch-svc's `ride.events` consumer reads from the **earliest** offset by design ("a booking
committed while the service was down still has to be dispatched"), so a second fleet with a second
consumer group would replay every booking the first one made.

**Every ride gets its own square of Sri Lanka.** The candidate pool is global, so two rides at one
pickup share a pool and "the ride went to the driver I put online" stops being a property of the
scenario. `ModeCFleet.NextPlaces()` walks a 32 × 19 grid at 0.12° and **throws when it runs out**
rather than wrapping.

**The offer window is 60 s here, and D5' §3.5 says 15.** Widened so a §11.12 cell applied to a ride
in `Offered` is not racing the R-04 backstop on a loaded build host. The *mechanism* is what this
suite exercises; the **value** is pinned in dispatch-svc's own suite (C034 `OfferExpiryTests`).

**The matrix is a ratchet, not a list.** `MatrixCoverage` fails if a cell is neither driven nor
listed in `Unreachable` with a reason, **and fails if an entry in `Unreachable` becomes reachable and
is left there**.

## C121 — Mode A/B, the tracker plane and fleet operations (`Category=ModeAB`)

**Nine services:** trip-state-svc, tcp-adapter, provisioning-svc, fleet-svc, subscription-svc,
fanout-svc, mqtt-bridge-svc, position-processor-svc, persistence-writer-svc.

| File | What it proves |
|---|---|
| `Scenarios/ModeAJourneyScenario.cs` | Start/End Journey, US-5.3's idle timer, US-5.4's arrival fence and its unarmed first journey, US-5.10's grace in both directions, D-03's mutex, and R-01 said out loud |
| `Scenarios/TrackerPlaneScenario.cs` | all four protocol adapters carrying a real frame to Timescale, the documented GT06 acknowledgement, T-03's refusal, T-12's sub-second close, T-08's double quarantine, and T-04's last will ending a journey |
| `Scenarios/IgnitionScenario.cs` | AL-32 — the key starts and ends a journey, the dashboard overrides the device, an ACC-off does not override the dashboard, and an unapproved vehicle is declined |
| `Scenarios/FleetOperationsScenario.cs` | US-13.A7's gate, AL-50's document slots, US-13.2/13.9's assignment, the org-scoped map and analytics, and the cross-org read Postgres refuses |
| `Scenarios/ModeBEntitlementScenario.cs` | Epic 23 — request, grant, visibility on a real socket, unsubscribe, D-22's revocation push, and the rejoin |

### The decisions

**The tracker plane is driven by frames on sockets, never by synthetic MQTT** (the C121 fence).
`TrackerDevice` opens a real TCP or UDP socket on tcp-adapter's own listener and writes the bytes
firmware would write. **Every frame is assembled from D6' §4.1's layouts rather than by the codec
that is about to decode it** — `Gt06Codec.BuildFrame` was available and is deliberately not used,
because a device that encodes with the decoder's own arithmetic can only ever agree with it. What
they share is the *algorithm the format names*: `Wire.Crc16X25` for GT06 and `Wire.Xor8` for JT/T
808. `The_GT06_login_is_acknowledged_with_the_documented_frame` pins both sides against
`78 78 05 01 00 01 D9 DC 0D 0A`, the one independently attestable fixed point in any of the four.

**A drive steps 40 m every two seconds, and the step count comes from the distance.** That is
72 km/h, under every ADD §12.6 ceiling including a three-wheeler's 80. position-processor-svc refuses
a sample whose implied speed exceeds its type's ceiling, and **a refused sample never becomes the
position the next one is measured against** — so one over-long step poisons every step after it. The
symptom is not a failed assertion about speed; it is a session that never saw a single fix.

**Nothing that depends on where a vehicle is runs before `WaitForFixAsync`.** A frame crosses EMQX,
mqtt-bridge-svc, `telemetry.raw`, position-processor-svc, `telemetry.normalized` and trip-state-svc's
consumer before it becomes a column. A scenario that carried on as soon as *some* fix had landed
leaves the rest in flight, where they are applied to whatever session is live when they arrive —
which is how an inbound journey was found to have ended at its destination one second after it
started, on its predecessor's last four fixes.

**Bringing a deadline forward is a clock, not a state fix** — C120's rule, held here too. The three
`Age*Async` helpers move `last_movement_at`, `ended_at` or `offline_since`: the platform's own record
of *when* something happened, written by a real fix or a real last will. **Every one of them asserts
the window first**, read off the running service's `TripStateOptions` — US-5.3's 30 minutes, US-5.4's
100 metres, US-5.10's 5 minutes. What fires is the real sweep and what it does is `AutoEndAsync`.
Nothing else is written: no scenario UPDATEs a session's state, reason, actor or position.

**The sweep interval is 2 s here and the deployed value is a minute.** A cadence, not a window — it
decides how long a scenario waits for a worker that was going to fire anyway.

**A vehicle has to move between two journeys, and the deadhead is not a contrivance.** `end_geo` is
copied from the session's last position when it closes, so US-5.4's fence is always centred exactly
where the vehicle is standing the moment the previous journey ends. US-5.4 exists for the driver who
*forgot* to press End, and a tracker publishes whether or not the app holds a session (US-3.22) — so
the bus deadheads with nothing live, and the scenario drains those fixes through Timescale before
arming anything.

**One thing is written that no route in this fleet can do: `registry.vehicles.status = 'APPROVED'`
for a fleet vehicle.** See `ModeAbFleet.MarkVehicleApprovedAsync` and
`FleetOperationsScenario.Unreachable` — the AL-50 gate is asserted *refusing*, which is the half that
is reachable, and the gap is a ledger entry with a test that it is still a gap.

**A failure prints the vehicle.** `SessionJournal` renders the vehicle's registry row, every session
with its reason and actor, the domain log, the outbox with what was published, how much telemetry
landed, the tracker bindings and the Mode B grants. Every wait in `ModeAbFleet` appends it;
`AroundAsync` wraps each scenario body for the ordinary `Assert` failures.

## C122 — proxy booking, package delivery and the web subview (`Category=ProxyPackage`)

**Ten services:** ride-svc, iam-svc, dispatch-svc, reputation-svc, fare-svc, fanout-svc,
content-svc, notification-svc, public-bff, safety-svc. Three containers, not four — nothing on these
paths touches a broker, so EMQX is out of `ProxyPackageCollection` and the three MQTT switches are
off.

| File | What it proves |
|---|---|
| `Scenarios/ProxyBookingScenario.cs` | §11.15's registered branch end to end, the decline that stores nothing, the expiry and US-8.19's retained fallback, P-05's counterparty, and P-12's five-an-hour |
| `Scenarios/WebPickupConfirmScenario.cs` | AL-45 — SMS → SCR-WT-003 → the booker's pin, BR-29.1's single use, and a request whose deadline outranks its token's |
| `Scenarios/PackageDeliveryScenario.cs` | both P-07 gates, AL-21's two branches, the five-attempt lockout and its way out, and P-10's photograph |
| `Scenarios/CashOnDeliveryScenario.cs` | P-08's tap, P-14's 24-hour clock into `Disputed`, and the same clock correctly doing nothing to a parcel still in transit |
| `Scenarios/WebSubviewScenario.cs` | SCR-WT-002/004/005/006, the web SOS, and the two refusals that keep the scopes apart |

### The decisions

**iam-svc is in this fleet, and not for the reason the other two leave it out.** Their reason is that
a bearer is not what C120 and C121 are about, and that is still true here — the tokens are
`TestTokenIssuer`'s. What iam-svc is here *for* is P-03: `GET /v1/users/lookup` is the registration
oracle that decides between the FCM round-trip and AL-45's SMS, and `Ride:IamBaseUrl` unset makes the
whole `/v1/location-requests` family answer `503` by design. The branch every proxy scenario turns on
does not exist without it.

**The SMS gateway is a third party, not a component.** `SmsGateway` is a real socket speaking D6'
§7.3's Notify.lk REST shape, and it is the only way to reach SCR-WT-002 and SCR-WT-003 honestly:
AL-44/AL-45 make a share token mint-and-SMS — `MintedLink` has no token member and no contract in
notification-svc can carry one out — so a scenario that read `safety.trip_share_tokens` to open a
page would be asserting about a page no recipient could have reached. Every token in this suite comes
out of a message body the platform composed, addressed to the number it was composed for.

**This fleet resets nothing, and that is the decision C120's reset forces.** The three fleets are
never disposed, so a truncate performed by whichever collection xUnit runs second would pull the
floor out from under services that are still running. What C122 needs instead is that its rides never
share a candidate pool, and it takes that from the *same* static grid C120 walks —
`ModeCFleet.NextPlaces()`, one square per ride across the whole assembly — rather than from an empty
table.

**Two clocks are moved and both windows are asserted first.** `AgeLocationRequestAsync` moves
`issued_at`, because ADD §11.15's 300 s cannot be a `rides.timers` row at all (`ride_id` is `NOT
NULL` and the request is issued before the ride); `PullForwardRideTimerAsync` moves P-14's
`cod_uncollected`. Neither touches a state, a resolution or a geo, and what fires in both cases is
ride-svc's own sweep.

**A COD receipt carries no figure, and that is a ledger entry rather than a soft assertion.**
`SCR_WT_005_reports_the_payment_on_a_parcel_that_was_also_photographed` asserts that
`fares.ride_payments` never reaches `CashOnDeliveryCollected` and that the receipt therefore omits
`totalMinor` — and **fails the day it stops being true**, because ride-svc writes no payment row on
`cod-collected` (it says so at the line) and fare-svc has no consumer at all. See the C122 handoff.

**The absent half is asserted on the JSON text, never on a deserialised shape.** "The booker's number
is not in the counterparty field" is a much weaker claim than "the booker's number is not there at
all", and a closed DTO says nothing about a member it has no property for. `WebPage.Mentions` and the
raw-payload sweeps in `ProxyBookingScenario` are what P-02, P-05 and P-09 are actually held by.

## C123 — every path money takes (`Category=Money`)

**Seven services:** wallet-svc, subscription-svc, fare-svc, ride-svc, dispatch-svc, reputation-svc,
fleet-svc. Three containers — nothing on a money path touches a broker, so EMQX is out and
`Ride:VehicleStatusEnabled` / `Dispatch:LastWillEnabled` are off.

| File | What it proves |
|---|---|
| `Scenarios/DailyFeeScenario.cs` | D-13 — the free first trip, the Rs 100 charged before the second, the single flat charge, D-08's gate withholding an offer, the Colombo-day key in both directions, and the per-driver count |
| `Scenarios/WalletMoneyScenario.cs` | both AL-05 rails end to end, R-19's two idempotency guards, the unsigned and mis-valued callbacks, US-9.19's voucher, US-9.13's transfer, and the bank-transfer rail the database refuses |
| `Scenarios/RidePaymentScenario.cs` | D-10's three rails, AL-47's claim/confirm/dispute pair, E-10's tip, E-05's queue row, and the two AL-57 gaps |
| `Scenarios/ModeBSubscriptionPaymentScenario.cs` | Epic 23 — all five methods, the slip and the owner's confirm, US-23.6's cash, and §18b's pass-through asserted on the ledger *and* on `information_schema` |
| `Scenarios/MoneyLedgerCoverage.cs` | the ratchet: every `ck_journal_entries_kind` value is driven or accounted for, and each claim is proved by posting one |

### The decisions

**The fence runs in a `finally`, and the body's failure wins.** A scenario that fails halfway is
exactly when an unbalanced ledger is most likely — a debit posted and its credit lost is what a
half-finished money path looks like — so the balance check runs even after a failure. When both fail
the author's own assertion is reported with the fence's appended, because "the ledger does not
balance" would send them looking in the wrong place.

**Drivers start with an empty wallet, unlike C120's.** C120 seeds Rs 5,000 so the D-08 gate never
refuses a driver over a rule it did not come to assert; here the balance *is* the assertion. Every
rupee arrives through a rail the platform has — an OnePay or LankaQR top-up settled by a signed
callback, a bulk voucher, or another driver's transfer.

**The acquirer is a stand-in for a third party and decides nothing.** `AcquirerGateway` speaks D6'
§7.1's create-session shape and signs its callbacks with the deployment's own secret — C122's
`SmsGateway` for money. Whether a callback is a first delivery, a redelivery, a second transaction
for one session or an amount that disagrees with it is the *scenario's* choice, because those four
are four distinct R-19 behaviours and a gateway with opinions about them would be testing itself.

**A trip is an `ACCEPTED` row in `dispatch.offers`, so every trip is a real ride.** D5' §2.2 counts
"completed+accepted today for driver" and both readers of that number are running — dispatch-svc's
D-08 pre-dispatch gate and subscription-svc's charge. `AcceptedRideAsync` waits for dispatch-svc's
consumer to mark the accept, and `FinishTripAsync` waits for the driver's presence row to reach
`AVAILABLE` rather than for `released_at` — **the release is stamped before the R-10 reservation is
dropped**, so going on standby at the first signal has the fresh GEO entry removed by a release still
catching up, and the next ride rests in `Matching` for ever. Found exactly that way.

**Two writes no route on this platform can make, and both are ledger entries with a test.**
`OpenPassengerBalanceAsync` writes what a top-up callback would have written — one balanced entry,
both balances, the mirror and the history line, so the fence stays true of it — because AL-57's
passenger wallet has no funding route at all. `MoneyFleet.ChargeDailyFeeAsync` calls
`charge-before-trip` itself because ride-svc never does. Each has a named test that the gap is still
a gap.

**Four findings are asserted as gaps rather than worked around**, and each fails the day it is fixed:
the passenger wallet cannot be funded, `Overpaid` is unreachable since AL-57/AL-59 removed the ride
callbacks, E-05's reversal is refused by wallet-svc's own `kind` whitelist and answers `503` after
committing, and nothing calls the daily-fee charge. All four are in the C123 handoff with the owning
component.

## Rules for adding to any of the four suites

- **If a scenario needs a stub, it is not an E2E.** Drive the platform through the surface an app, a
  device or a peer service has: HTTP, a protocol frame on a socket, MQTT, SignalR, or an event on a
  topic. A stand-in for something *outside* the platform — a tracker, an SMS gateway — is a different
  thing and is allowed; it must speak the real wire format and must be named as what it stands for.
- **Assert the window before you shorten it.** A timer whose deadline is brought forward without the
  spec's window being checked first tests the sweep and nothing else.
- **A new ride needs a new square; a new vehicle needs a new place.** `ModeCFleet.NextPlaces()` and
  `ModeAbFleet.NextPlace()`; never reuse one.
- **Add the id to `ScenarioRides`/`ScenarioVehicles`/`ScenarioParties` the moment it exists**, before
  the first assertion about it. On C123 that means every id that will ever hold a
  `billing.accounts` row — a driver, a passenger, a fleet.
- **A found gap goes in a ledger with a reason and a test that it is still a gap** — never a softened
  assertion. `MatrixCoverage.Unreachable`,
  `DispatchTimeoutScenario.A_driver_who_comes_online_while_a_ride_waits_is_not_offered_it`,
  `FleetOperationsScenario.Unreachable`, C122's COD-receipt assertion and
  `MoneyLedgerCoverage.Accounted` are the five shapes that take.
- **Never assert money on a balance alone.** A balance is a materialised figure; the postings are the
  fact. Every money assertion in C123 names the entry's idempotency key, checks Σ legs = 0 and says
  who the counterparty was — because "the driver is Rs 100 poorer" is equally true of the platform
  being credited, of nobody being credited, and of the wrong entry having been keyed.
- **Assert what a payload must *not* carry on the text, not on the type.** A closed DTO is a good
  fence and a bad test: it has no property for the thing you are checking is absent. C122's
  `WebPage.Mentions` is the shape.
- **`MAGERIDE_TEST_LOGS=1`** keeps every service's console provider — on a fleet of nine or ten it is
  usually the fastest way to see which worker did what. **`MAGERIDE_E2E_RACE_RUNS`** shortens C120's
  hundred-race loop while working on something else; it is not a knob for CI.

## Not here, on purpose

- **ride-svc and dispatch-svc in the Mode A/B fleet, and trip-state-svc in the Mode C and
  proxy/package ones.** R-01 draws the line and no suite crosses it.
- **iam-svc, in C120 and C121** — a bearer is not what either component is about; `TestTokenIssuer`
  mints what iam-svc would sign. A real iam token crossing into a real service is
  `e2e/walking-skeleton`'s (C025). C122 runs iam-svc for one internal route and mints its bearers the
  same way.
- **ocr-svc** — needs Tesseract, an OpenCV native and a reachable Gemini. Its absence is *load
  bearing* rather than incidental: `Fleet:OcrBaseUrl` unset is what makes every AL-50 slot `pending`,
  which is what `FleetOperationsScenario` asserts about.
- **admin-bff** — the Verification Officer's screens are C062's. The suites stand in for its
  internal callers on the `/v1/internal/**` planes fleet-svc and ride-svc expose, which is what
  admin-bff would call anyway. C122's `package.otp_locked` is raised for an admin queue that is
  C065's; the event is asserted, the queue is not.
- **notification-svc, support-svc, wallet-svc, in C120 and C121** — nothing on a Mode C ride or a
  Mode A/B journey *requires* them. Where one would be called those fleets leave the address unset,
  which is a documented refusal rather than a stub that always agrees: `Fare:WalletBaseUrl` is unset
  so every C120 scenario pays cash, and `Fleet:ScheduleAlarmsEnabled` is off so no US-13.11 alarm
  rings into a socket nobody is listening on. **notification-svc is central to C122** for the
  opposite reason: the two no-app paths are the messages it sends, and **wallet-svc is central to
  C123** for the same reason — that fleet is the one that sets `Fare:WalletBaseUrl`.
- **notification-svc, in C123** — every money push on that surface is C051/C052's: US-9.9's
  low-balance warning, AL-47's five-minute nudge, §11.14's "Refund processed". Each producer emits
  the event or logs the intent with the numbers on it, and that is what a scenario asserts.
- **payout-svc and fleet-billing-svc, in C123** — AL-58's weekly sweep (C061) and the consolidated
  fleet invoice (C060) each own the run that raises the instruction beside the ledger entry, so
  driving the entry without the run would assert half of a movement. Both are named in
  `MoneyLedgerCoverage.Accounted` rather than left uncovered.
- **voip-svc** — AL-48 withdrew the masked leg and the web `/call` round-trip in full, so there is
  nothing on any of these paths for it to do; the driver card carries a real MSISDN and a browser
  dials it. public-bff refuses a route whose path contains `/call` at start-up, and that fence is
  `FenceTests`'.
- **The gateway (C008)** — each service is addressed directly. The edge's own routing, rate limits
  and `/v1/internal/**` refusal are `ApiGateway.Tests`'. C122's `WebSubview` still sends
  `X-Forwarded-For`, because `PublicBff:TrustForwardedFor` is on and the per-IP bucket would
  otherwise count the whole suite as one visitor.
