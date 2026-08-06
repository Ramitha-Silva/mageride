# Passenger Android Conventions

- Kotlin, Jetpack Compose, Material 3; minSdk 26 — Android 8.0 (URD NFR-22)
- Depends on shared/kmp — import DTOs, the API client, the domain state machines and the SignalR
  contract from there. Nothing that exists in `:shared` may be reimplemented here.
- Screen groups map to D2' §A + the `specs/wireframes/passenger_android.html` wireframe (41 SCR-PA ids)
- MapLibre GL Native over PMTiles; live vehicles arrive over SignalR by geocell —
  H3 res-7 + ring(2) = 19 cells with 30 s hysteresis, never a per-vehicle subscription (R-06)
- Trilingual: every user-facing string comes from `values/`, `values-si/`, `values-ta/` — no literals
- Gradle project path is `:apps:passenger-android`; versions come from `gradle/libs.versions.toml`
- Verify: `./gradlew :apps:passenger-android:testDebugUnitTest :apps:passenger-android:assembleDebug`
  (the Android SDK is installed on this build host at `/opt/android-sdk`; `local.properties` points
  AGP at it)

## The shell (C076) — what exists, and where a screen plugs in

C025's walking skeleton is **gone**. Its three files (`MainViewModel`, `PassengerLiveMap`,
`SkeletonClient`) were declared throwaway by their own component and were deleted here; nothing in
this module is throwaway any more.

```
lk.mageride.passenger
├── PassengerApplication.kt   Koin start, notification channels, Play Integrity warm-up
├── MainActivity.kt           the ONE Activity. Do not add a second.
├── di/                       PassengerEnvironment (the only BuildConfig reader), the app module,
│                             PassengerDatabase (C018's deferred open)
├── nav/                      PassengerRoute (every destination), PassengerTab, the bottom bar,
│                             SCR-PA-033's drawer table + sheet, PassengerNavHost
├── shell/                    PassengerShell (drawer + Scaffold host), offline banner, update gate,
│                             connectivity, AppPreferences + PassengerLocale
├── onboarding/               C077 · SCR-PA-001…005 + the router, the OTP rules and the error table
├── home/                     C078 · SCR-PA-006/007/008/010/032 — the map, the filter, the popup,
│                             the destination field, and RecentPlaces (§2.2's only writer)
├── booking/                  C079 · SCR-PA-009/010b/011/012/012a/013 — the multimodal list, the
│                             proxy round trip, the parcel, the paste sheet, the schedule, and
│                             BookingDraft (the one booking six screens edit)
├── ride/                     C080 · SCR-PA-014…019 — finding, the active ride, the call chooser,
│                             the payment rails, AL-47's attestation, the receipt and the rating
├── ui/component/             MageRideCta + the cluster-1 controls (C077)
├── ui/theme/                 D2' §0.2 tokens — colour, type, spacing/radius/elevation/controls
├── map/                      MageRideMap (the whole §0.3 layer stack), MapStyles, VehicleLayers,
│                             MapPalette, MarkerInterpolator (MAP-04)
├── live/                     the SignalR plane — transport, subscriptions, inbox, vehicle store
├── location/                 PassengerLocationSource — the fix the 19 cells are computed from
└── push/                     FCM service, PushRouter (deep links), channels, token provider
```

## Cluster 1 (C077) — what the next screen group can reuse

- **`OnboardingRouter.next(...)` is the only place that decides where a passenger belongs.** The
  splash, the login screen after a verify and Profile Setup after a save all call it. If a new gate
  is ever added before the map it goes in that function, not in a screen.
- **`ui/component/` is now populated**, and a screen uses it rather than raw M3: `MageRideCta` (the
  §0.2 CTA token — every full-width orange bar in the wireframes), `MageRideTextLink` (the
  `textlink`), `SelectionBox`, `PagerDots`, `IllustrationPanel`, `SectionLabel`, `InlineError`,
  `LabelledTextField`, `PhoneNumberField` and `OtpEntry`.
- **`PassengerProfileRepository` is `iam.users` as an app uses it** — `GET`/`PUT /v1/users/me`.
  C083's SCR-PA-027 reads the same pair; reuse it rather than opening a second seam.
- **`OnboardingErrors` is the FIRST-RUN code table only.** Every arm is a code `iam.yaml` declares
  on the three operations SCR-PA-003 and SCR-PA-004 reach. Add your own table for your contracts;
  one `when` over the platform is a function nobody can check.
- **A proper noun is data, not copy.** The language endonyms (`සිංහල`), the `+94` prefix and the
  `7X XXX XXXX` mask are Kotlin constants (`LanguageNames.kt`, `PhoneNumber.kt`), because three
  identical values in the three `strings.xml` files is exactly what `StringResourceTest` fails on.
- **Language is applied by `MainActivity.attachBaseContext`** through `PassengerLocale.wrap`, and a
  change calls `recreate()`. Per-app locale (`LocaleManager`) is API 33+; the floor here is 26.
  A first run counts as a change — there is no stored language, so the app is drawing in the
  *handset's* locale, which is usually not the Sinhala default AL-26 pre-selects.

**To add a screen (every remaining SCR-PA id is a standing placeholder):**

1. Its route is already in `nav/PassengerRoute.kt`. Use it; do not invent a path.
2. Replace its `placeholder(...)` line in `nav/PassengerNavHost.kt` with the real composable. That
   file is the only `NavHost` in the app.
3. Put every user-facing string in **all three** `res/values*/strings.xml` files at once.
   `StringResourceTest` fails the build on a key that exists in one and not the others, on a
   translation left equal to its English, and on a format placeholder dropped in translation.
4. Reach for `MageRideTheme.spacing` / `.radius` / `.elevation` / `.status` / `.vehicle` / `.mode`
   and `MaterialTheme.colorScheme` — never a raw `dp` or hex. `ThemeTokensTest` holds §0.2.
5. A screen with a `≡` in its app bar calls `LocalDrawerControl.current()`. The drawer itself is
   the shell's; a screen never hosts one.

## Cluster 2 (C078) — the map screen group

- **`MapFilter` is a value, not a service.** SCR-PA-006's answer lives in view-model state and is
  re-applied to *every* batch. The filter is deliberately **not** folded into the live plane: the
  plane holds what the *platform* says is visible (entitlement, freshness, engagement) and the
  filter holds what the *passenger* asked to see. Folding them would make a Mode B vehicle the
  passenger has no grant for indistinguishable from one they simply switched off.
- **A tap is routed by mode in the view model** (`LiveMapViewModel.onMarkerTapped` → `MarkerTap`),
  not by the sheet. Mode A opens SCR-PA-007, Mode B hands SCR-PA-024 the vehicle id (AL-23/US-4.6),
  Mode C and a tap on a departed marker do nothing (US-7.4). A Mode B vehicle cannot reach the
  popup composable by any path.
- **`VehicleLabels` is D2' §0.2's vehicle table as a screen needs it** — display name, Material
  Symbol, and the **same** `VehicleColors.Legend` colour the map marker is tinted with. A second
  table would be the second copy of §0.2 that MAP-03 exists to prevent.
- **`RecentPlaces` is the only door onto `mobile_db_schema.md` §2.2's `place_recents`**, and
  SCR-PA-008 is its **writer** — the table is "recent / searched locations", so choosing a
  prediction records one, whether or not a ride follows. Local-only: no `dirty`, no `synced_at`, no
  outbox. It has no change feed, so a screen showing recents re-reads on resume.
- **SCR-PA-032 is a state of SCR-PA-010, not a screen.** `LiveMapState.stale` (anything but
  `LiveStatus.Connected`) fades the marker layers through `MageRideMap(dimmed = …)`; nothing is
  erased, because a passenger who has lost signal still wants to know where the bus was (US-15.2).
  `EmptyReason` has three values rather than a boolean so US-7.14 can tell an outage from a filter
  the passenger set from a genuinely quiet area.

## Cluster 3 (C079) — the booking flow

- **`BookingDraft` is a `single` and it is where a booking lives.** Six screens edit one — a
  destination on SCR-PA-008, a tier on SCR-PA-009, a rider on SCR-PA-010b, a parcel on SCR-PA-012,
  a time on SCR-PA-013 and a payment method on C080's SCR-PA-016. Do not thread booking fields
  through nav arguments; `BookingDraft.clear()` is what stops one outliving its ride.
- **`CaptureTarget` is how one picker serves five callers.** SCR-PA-008 is reached from the home
  sheet, the booking screen's edit row, the proxy pickup, both package ends and the schedule
  destination, and it cannot tell which. Whoever opens it calls `draft.expect(…)`; the NavHost's
  `onPlaceChosen` calls `draft.capture(place)`, and a `false` return means *"nobody was waiting —
  this is a new booking"*.
- **`BookingRepository` is one door onto six services** (transit, fare, ride, dispatch, query, iam).
  A seventh operation from one of them goes there, not into a second client beside it.
- **`MapsLink` parses on the device and only short links reach the server** (AL-20). Precedence is
  `!3d!4d` → `q=` → `ll=` → `@`, because a `/maps/place/…` URL carries both a place pin and a
  viewport centre and they are routinely a hundred metres apart.
- **`TierQuote` has three fields and a test pins that** — AL-19/BR-23.3 says a Mode C tier shows the
  upfront price and nothing else before a driver is matched, and the type is the enforcement.
- **A decline sends no coordinates** (P-02). `declineLocationRequest` takes an id and has no
  parameter for a point; the assertion is on what the repository was handed.

## Cluster 4 (C080) — the active ride and its money

- **`PaymentRails` is the only place a payment method becomes a control**, and it contains neither
  `onepay` nor platform-`lankaqr` — AL-57 and AL-59 retired both as ride rails. **No surviving rail
  carries a surcharge**, so nothing in this app can render one; `PaymentRailsTest` pins the lists.
- **`BookingDraft.paymentMethod` is the SETTLEMENT-time `PaymentMethod`**, not `ride.yaml`'s
  booking-time enum. `PaymentRails.bookingValueOf` maps it at `POST /v1/rides/request` — see the
  contract-gaps section below for what that mapping loses.
- **AL-47 is a conversation, not a callback.** The passenger pays into the driver's own bank, so no
  webhook reaches fare-svc: `claimPaid()` → poll → `DriverConfirmedQR`. `QrClaimedByPassenger` is
  **not** settled and must never be shown as Confirmed.
- **There is no masked call path and no masking copy** (AL-48). A "Normal call" is `ACTION_DIAL` on
  `RideDetail.counterpartyPhone`, which the contract only carries from `Accepted` onward.
  `ACTION_CALL` is deliberately not used — it needs `CALL_PHONE` and dials without showing the
  number. US-26.5's notice is shown once, and only before a direct dial.
- **The Rs 50 cancellation fee is stated before the tap.** D-05 settles it on the *next* trip, so
  the confirm dialog is the only moment a passenger can be told.
- **`rideScoped(pattern, arg) { rideId -> … }`** registers a ride-scoped destination; every C080
  view model takes its id from the route, which is why none of them is a Koin `viewModel { }`.

## The live plane — read this before touching `live/`

- **The passenger view is 19 cells and the client never subscribes to a vehicle.** R-06 is res-7 +
  `ring(2)`; `HubSubscriptions` is the only place group membership changes, and `signalr-hub.md`
  §2.1 says `vehicle:{vehicleId}` groups are *"joined by the server, never asked for"* — a Mode B
  vehicle is visible because fanout-svc checked the `share:{userId}` entitlement at join (D-23).
  There is no `SubscribeVehicle` method to call. `PassengerLiveMapTest` pins the four methods.
- **The SignalR Java client has NO `withAutomaticReconnect()`.** Unlike the JavaScript and .NET
  clients, `HttpHubConnectionBuilder` offers only `withServerTimeout`, `withKeepAliveInterval` and
  `onClosed`. R-09's jittered exponential reconnect is therefore ours: `PassengerLiveMap.supervise`
  runs it over `:shared`'s `ReconnectBackoff`, and the first retry lands inside 1.25 s, which is
  what makes SCR-PA-032's *"auto-clears on reconnect < 5 s"* true.
- **Recovery is an ORDER, not a set** (D6' §5.4): rejoin the groups, *then* `GET /v1/nearby`. A
  client that snapshots first loses every frame published between the two calls — exactly the ones
  that moved while it was away. `LiveHubRecovery.plan` is C017's and is followed verbatim.
- **Payloads are decoded by `MageRideJson`, never by Gson.** The hub protocol is Gson and Gson
  binds an enum by its Kotlin `name()`; C012's enums carry `@SerialName` wire spellings that differ
  (`three_wheeler`, not `THREE_WHEELER`). The transport binds each argument as a
  `com.google.gson.JsonElement` — the identity binding, which cannot be wrong — and hands the text
  up. That is why `com.google.code.gson` is an explicit dependency: signalr declares it at
  **runtime** scope, so the type is not otherwise on the compile classpath.
- **A vehicle leaves the map for exactly four reasons** — `VehicleRemoved` (stale/offline/engaged),
  `ShareRevoked` (D-22), a cell the client left, or a resync that replaced the set. See
  `LiveVehicleStore`. Batches carry only what moved, so absence never means removal.
- **`LiveHubTransport` is the seam.** Every rule above is asserted on the JVM against
  `FakeLiveHubTransport`, with no server and no network — the same split C067 made between
  `PositionPipeline` and the MQTT client.

## Rules this module is built on

- **The passenger app HAS a hamburger, and that is not an AL-31 violation.** AL-31 is a rule about
  the *driver* dashboard. SCR-PA-033 says the drawer opens *"from the ≡ menu / 'Menu' tab"*, and
  the wireframe draws the `≡` in every cluster-2 app bar. `PassengerTab.MenuTab` therefore carries
  **no route** — it opens the drawer over whatever is on screen.
- **This app has no MQTT client and never will.** D3' §3.3: device position *ingest* is MQTT and is
  the driver's; passenger realtime-*out* is SignalR. There is no broker host in `BuildConfig`, no
  foreground service, and no background-location permission — `ManifestTest` asserts their absence.
- **No dynamic colour.** D2' §0.2 is the single source of truth shared with Figma, SwiftUI and the
  Tailwind preset. It would also break MAP-03: the vehicle legend is a fixed eleven-colour identity,
  and a wallpaper-derived scheme could put the app's own accent inside it.
- **`PassengerEnvironment` is the only file that reads `BuildConfig`.** The gateway origin is the
  one value a release build cannot afford to have wrong in two places — and the hub rides it, so
  there is one origin rather than two.
- **A deep link is resolved, not trusted.** `PushRouter` maps a `mageride://…` URI onto a known
  `PassengerRoute`; an unrecognised one opens nothing. `mageride://wallet` and
  `mageride://documents` are the **driver's** links and deliberately resolve to nothing here.
- **P-02's location request carries no deeplink at all.** It is a silent data message —
  `{kind:'location_request', requestId, bookerName, ttl:300}` — so `PushRouter` builds
  SCR-PA-011's route from `data.requestId`. Do not invent a `mageride://pickup-confirm` host.
- **`PassengerDatabase` is the app's deferred answer to C018's un-bound database.** Opening it is
  `suspend` (the SQLCipher key comes out of the Keystore), so it is opened by the first caller
  behind a `Mutex` and shared. Six §2 tables and eight screen groups: do not call `openPassenger()`
  anywhere else.

## Contract gaps this app is living with

- **No route number exists for a vehicle anywhere.** Neither `VehicleFrame` (socket) nor
  `NearbyVehicle` (snapshot) carries one, so SCR-PA-007 shows the vehicle type where the wireframe
  shows *"Route 138 — Pettah → Maharagama"*. A `query.yaml` change, not an app change.
- **`VehicleFrame` carries no timestamp**, so SCR-PA-007's *"last seen Ns ago"* cannot be drawn —
  the client knows when it *received* a frame, not when the sample was taken, and the difference is
  exactly the lag the label is for.
- **There is no `GET /v1/vehicles/{id}`.** The popup's ETA, driver and plate come from
  `GET /v1/nearby` matched by id, centred on the **passenger** — `etaSeconds` is defined as seconds
  to the querying passenger, so a lookup centred on the vehicle would answer roughly zero.
- **No contract POSTs a Mode C ride rating.** `ride.yaml` declares no rating operation; trip-state's
  is scoped to a *session*, and using it for a ride would cross R-01. SCR-PA-019 queues to
  `ratings_pending` — which has no columns for the stars or the comment. C074 found the same gap
  from the driver side.
- **`ride.yaml`'s booking-time payment enum predates AL-57/AL-59** and still carries `lankaqr` and
  `onepay`. There is no booking-time value for "wallet", so a booking says `cash` and SCR-PA-016
  asks again.
- **The SCR-PA-016/017 wireframes predate the payment-custody change set** and still draw OnePay
  +5 %. The built screens follow AL-57/AL-59; both wireframes need a micro-change-set.
- **AL-17 beats D2' §SCR-PA-008.** That section still says the drop field accepts a route number and
  that predictions blend routes with places. The wireframe and AL-17 say geo-only, and geo-only is
  what is built. **US-7.9 therefore has no screen in this app**, though `getBusesOnRoute()` exists.
- **No headway or frequency exists on any transit shape**, so SCR-PA-009's *"every ~10 min"* cannot
  be drawn — and D2' §SCR-PA-009's *"ETA 15 mins"* for a public card is equally underivable, since
  `TransitOption.totalDurationSec` is a duration and not an arrival time.
- **There is no walking-routing service.** The blue walk-to-halt line is a straight line from the
  passenger to the nearest halt: honest about the distance, not about the path.
- **No SCR-PA id exists for a map picker.** The wireframe offers Map / Map pin / "Select on map" as
  *methods* and draws no screen for any; `booking/MapPickSheet` is a modal for that reason.
- **D2' §SCR-PA-010b and §SCR-PA-012 predate AL-20** and still list three capture methods. The
  wireframe's four (and the pickup/drop-off asymmetry) win; both tables need a micro-change-set.

## Things that will bite

- **`org.jetbrains.kotlin.android` is not applied.** AGP 9 has built-in Kotlin support and refuses
  the plugin outright.
- **The `google-services` plugin is not applied either**, and there is no `google-services.json`.
  firebase-messaging compiles and `PassengerMessagingService` is registered, but **FCM does not
  deliver until C124 lands the Firebase project**. Nothing else about push is blocked by it.
- **MapLibre is the `-opengl` flavour** (`org.maplibre.gl:android-sdk-opengl`). The default
  `android-sdk` artifact requires Vulkan 1.0 in its manifest, which Play uses to filter devices —
  on the Android 8.0 floor that cuts off exactly the budget handsets this platform is for. Do not
  add `android-sdk-ktx`: it depends on the default artifact and fails `checkDuplicateClasses`.
- **MAP-02 and MAP-10 are metres and MapLibre's `circleRadius` is pixels.** `MageRideMap` rescales
  both circle layers on `addOnCameraIdleListener` through
  `Projection.getMetersPerPixelAtLatitude`. A radius set once is wrong at every other zoom.
- **`TestScope.backgroundScope` does NOT run under `advanceUntilIdle()`.** kotlinx-coroutines
  deliberately stopped draining background work there, so a test that drives a supervision loop
  must build its own `CoroutineScope(StandardTestDispatcher(testScheduler) + Job())` and cancel it.
  `PassengerLiveMapTest` does, and says why.
- **`Channel(CONFLATED)` refuses an explicit `onBufferOverflow`** — conflation already implies
  `DROP_OLDEST`, and passing both throws `IllegalArgumentException` at construction.
- **Kotlin block comments nest.** A KDoc containing `values*/strings.xml` closes itself on the `*/`
  and the file stops parsing several declarations later. Same trap C014 and C067 hit.
- **detekt's `LongMethod` and `LongParameterList` carry `ignoreAnnotated: ['Composable']`** (in
  `config/detekt/detekt.yml`), but `TooManyFunctions` has **no** exemption and the ceiling is 11
  per class. `LongParameterList` triggers at **7** parameters, not 8 — that is what split
  `PassengerLiveMap` into `HubSubscriptions` + `LiveHubInbox` and produced `MapPalette`.
- **`MagicNumber` excludes `ui/theme` and nothing else.** A hex or a `dp` anywhere else is a build
  failure, which is the same rule as "never a raw dp or hex" above, enforced.
- **`kotlin-test` resolves to no variant under AGP's built-in Kotlin.** Use `libs.kotlin.testjunit`.
- **Any test that asserts the OTP resend cooldown must stub `requestOtp`.** `AuthSessionManager`
  computes `resendAllowedAt` off the **real** clock, and the fixture generator answers `15` for any
  field whose name contains "second" — so a fifteen-second stall on a loaded host expires the
  cooldown and fails the assertion. `LoginViewModelTest` pins it explicitly (Δ C079).
- **The view-model test harness is `lk.mageride.passenger.MainDispatcher`** (root test package, not
  `onboarding`). `own(model)` gives a view model a lifetime; anything with a `while (…) { delay(…) }`
  in it must be owned or it wakes inside the next class's `resetMain()`.
- **A `StateFlow` predicate of "something is on screen" is usually wrong on SCR-PA-008.** The
  predictions list is never empty — the recents and saved addresses fill it before a lookup goes
  out — so `await { predictions.isNotEmpty() }` passes on the *previous* state.
- **Unit tests run with `isReturnDefaultValues = true`** and the working directory is the module
  directory, which is what lets `ManifestTest` and `StringResourceTest` read the real files.
- **iOS does not compile on this host** (root CLAUDE.md). C094's SwiftUI shell mirrors these tokens
  and route names — keep them in step.
