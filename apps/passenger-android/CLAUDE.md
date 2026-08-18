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
├── history/                  C081 · SCR-PA-020…023 — package tracking for both parties, the
│                             three-tab history, trip details, and PackageOtps
├── subscription/             C082 · SCR-PA-024/025/025a/025b — Mode B access, the cards and the
│                             owner-paid rails
├── settings/                 C083 · SCR-PA-026/026a/027/027b — addresses, settings, the drawer
│                             header, AddressBook, SosContacts, PaymentPreference
├── comms/                    C084 · SCR-PA-028 + the WebRTC seam
├── safety/                   C084 · SCR-PA-029 — the passenger SOS and D-34's share link
├── support/                  C084 · SCR-PA-030/030a — the FAQ accordion, tickets and the sheet
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

**To add or change a screen (every SCR-PA id now has one — C084 took the last three):**

1. Its route is already in `nav/PassengerRoute.kt`. Use it; do not invent a path.
2. Register it in `nav/PassengerNavHost.kt`, the only `NavHost` in the app. There is no
   `placeholder(...)` helper any more, and re-adding one would be a way to register a route with no
   screen behind it.
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
- **SCR-PA-010 owns the cell tick, and it is the only caller of `PassengerLiveMap.refreshCells()`**
  (Δ C096). ADD §7.4 step 6 applies the first boundary crossing immediately and then **holds** the
  next for thirty seconds; a held crossing is applied by the next call into `GeoCellSubscription`,
  which on a fix-driven path is the next fix. A passenger who steps over a cell edge and then stops
  walking produces none, so the crossing never lands and they keep the nineteen cells around where
  they *were*. `LiveMapViewModel.tickCells()` re-evaluates every `CELL_TICK` (15 s — half the
  window). C076's handoff asked C078 for this loop and C078 did not write it; C096 found the same
  hole from the iOS side and both apps now tick.
- **SCR-PA-032 is a state of SCR-PA-010, not a screen.** `LiveMapState.stale` (anything but
  `LiveStatus.Connected`) fades the marker layers through `MageRideMap(dimmed = …)`; nothing is
  erased, because a passenger who has lost signal still wants to know where the bus was (US-15.2).
  `EmptyReason` has three values rather than a boolean so US-7.14 can tell an outage from a filter
  the passenger set from a genuinely quiet area.

## Cluster 3 (C079) — the booking flow

- **`BookingDraft.begin` defaults the pickup to `LastKnownFix`, and that is a defect fix** (Δ C097).
  `begin` takes an *optional* pickup and all three production call sites omitted it, so the draft had
  none — and `RideBookingViewModel.refresh()` returns early on exactly that, which meant **SCR-PA-009
  loaded neither list: no bus routes, no tiers, nothing to book.** `RideBookingViewModelTest` did not
  catch it because its own setup passed a pickup nothing in the app passed. The default lives inside
  the draft rather than at the call sites so a fourth one cannot reintroduce it, and
  `a_booking_begun_the_way_the_app_begins_one_has_a_pickup` fails without it.
- **`LastKnownFix` is written by the fix source, not by a screen.**
  `RecordingPassengerLocationSource` decorates `PassengerLocationSource` and records every fix that
  passes, so the last known position is the last one *anybody* saw and no screen holds a second
  collector open on the fused provider. Read it for a default pickup, a geocoder bias or a picker's
  opening camera; **do not add a second collector to get one**.
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
- **`RideHandOff` is what carries a finished ride to its money** (Δ C098). `ActiveRideScreen`'s KDoc
  said the ride-state change did it and **nothing implemented it** — no screen, no `NavHost` arm and
  no push built `PaymentMethod(rideId)`, so `Completed` left the passenger on a finished trip and
  SCR-PA-016/017/018/019 were reachable only from the receipt they come *after*. Found while
  building the iOS twin; both apps carry the same three-valued type.
- **`PaymentSelection` is how the chosen rail reaches SCR-PA-017** (Δ C098). The NavHost was
  *discarding* `onConfirmed`'s argument while `PayFareViewModel.method` defaulted to
  `SCAN_DRIVER_QR` and `setMethod` had no production caller — so a passenger who chose **Cash** or
  **Wallet** landed on a screen that had already posted a driver-QR payment. The rail is a
  constructor parameter now, because the initiation happens in `init`.

## Cluster 5 (C081) — packages and history

- **SCR-PA-020 and SCR-PA-021 are one view model**, because they are one ride. The party is decided
  from the ride (booker vs recipient), never from the URI — `mageride://package/{rideId}` is the
  same link for both.
- **A recipient never signed in.** They arrive from an FCM deep link, or with no app from an SMS
  onto the SCR-WT web page (P-09, AL-45). Nothing on that screen is reachable by logging in.
- **`PackageOtps` is a `single` and is process-lifetime.** Neither handover code can be read back
  from the platform, so it is written by C079's booking and by `PassengerMessagingService` on
  arrival. A cold start has nothing, and the screen says so.
- **A history card renders `mobileMasked` and never dials it.** `PhoneMasked`'s KDoc forbids parsing
  one back; the Call fetches `RideDetail.counterpartyPhone` (AL-48). A cancelled-before-assignment
  trip offers neither, checked in the card and again in the view model.
- **`HistoryRepository` deliberately reads two services.** The Past tab is ride-svc's history (state
  + driver); SCR-PA-023's detail is query-svc's (polyline + filtered distance, spanning all modes).

## Cluster 7 (C083) — settings, addresses and the drawer's header

- **SCR-PA-026's Home and Work are the `isHome`/`isWork` flags, never a label convention.** The
  label is free text a passenger types in their own language, so matching `"Home"` against it would
  make a Sinhala *"නිවස"* not a Home. The two rows are drawn **always**, set or not, and the ✎ on
  either is the wireframe's only *"save Home & Work by pin"* control — which is why SCR-PA-026a can
  keep to AL-26's four fields and ask nothing about shortcuts.
- **`AddressBook` is the only door onto `iam.saved_addresses`, and the one seam in this app that
  spans two services on purpose.** AL-14's *"OSM-pin + reverse-geocode"* is one gesture.
  `describe()` answers `null` rather than throwing: a geocoder that cannot name a coordinate has not
  stopped a passenger saving it, so the lookup is a **pre-fill and never a gate**.
- **`PassengerProfileRepository.update` has no `language` parameter, and that is AL-26 made
  structural.** Everything Settings and Edit Profile save goes through it; the language has its own
  route (`saveLanguage`) reached only from SCR-PA-027. A screen cannot send a language it has no
  parameter for, however it is later edited.
- **A language change writes the device first and re-creates the Activity.** `PassengerLocale.wrap`
  runs in `attachBaseContext`, so nothing else can change what is on screen; `SettingsState.relaunch`
  is the ask. The server write is second and is allowed to fail — `languagePendingSync` is left set
  for C077's next authenticated pass.
- **`PaymentPreference` is where *"pre-selected at booking/checkout"* lives** (US-22.4).
  `BookingDraft` re-reads it on **every fresh draft**, which is what makes the DoD's *"the next
  booking"* true rather than *"every booking after a restart"*; SCR-PA-016 seeds `chosen` from the
  same value. It is device-local because `DefaultPaymentMethod` is still `[cash, lankaqr, onepay]`
  and AL-57's replacement rail (`wallet`) has no value in it — see the contract gaps below.
- **`PassengerIdentity` is a `single` because the *shell* reads it.** SCR-PA-033's header is drawn
  above every screen; SCR-PA-027 and SCR-PA-027b hand their profile over rather than leaving the
  drawer to fetch one each time it opens, and `PassengerShell` clears it on every `RouteToLogin`.
- **Nothing in this app sets `EmergencyContact.isPrimary`.** iam-svc promotes the first contact onto
  `iam.users.emergency_contact_name/phone` for D-33's five-second SOS budget and re-promotes on a
  delete — which is why a removal **re-reads** the list rather than dropping the row locally.
  C084's SCR-PA-029 reads the same `SosContacts` seam; an empty list is what makes `POST /v1/sos`
  answer `400 no-emergency-contact`, and SCR-PA-027b says so where the list is empty.
- **`settings/SettingsRows.kt` is this cluster's row vocabulary** — `SettingsTopBar` (the `‹ Title`
  with an optional trailing slot) and `SettingsRow` (the wireframe's `.listrow`, with the right-hand
  side as a slot: a chevron, a value, a switch or an ✎). Three screens draw the same row.

## Cluster 8 (C084) — the call, the alarm, support and the version gate

- **`AbsentVoipEngine` is what the graph binds, and that is a dependency wall rather than a
  decision.** D6' §6 names LiveKit and D2' §SCR-PA-028 says *"WebRTC + `ConnectionService`"*, but
  `io.livekit:livekit-android` depends on `com.github.davidliu:audioswitch`, published **only on
  JitPack** — and this repo resolves from a content-filtered `google()` plus `mavenCentral()` with
  `FAIL_ON_PROJECT_REPOS`. Widening that is an edit to `settings.gradle.kts`, which is C001's. So
  SCR-PA-028's **signalling half is real** (`POST /v1/calls/start` mints the room and writes
  `comms.call_log`) while the media half reports `NO_MEDIA_CLIENT` — exactly the condition AL-48
  legislates for, so the screen offers *"Call normally instead?"* and the passenger reaches the
  driver. Landing the real engine is **one binding**, plus `RECORD_AUDIO` / `MODIFY_AUDIO_SETTINGS`
  in the manifest (absent on purpose — `ManifestTest` asserts that a permission with no code behind
  it is not declared). The driver app hit the same wall at C075.
- **SCR-PA-015's `⛨ SOS` NAVIGATES now; it does not act** (Δ C084). `ActiveRideViewModel.triggerSos`
  is gone: SCR-PA-029 owns the confirm, the countdown, the contact list, the dispatched state and
  D-34's link, and `POST /v1/sos` having **one** caller is what stops one emergency arriving on the
  operator's live feed as two events. `RideRepository.triggerSos`/`.shareTrip` are unchanged and are
  now called from `SosViewModel`. The same rule holds for the call: `POST /v1/calls/start` for a free
  call is made by SCR-PA-028 and by nothing else — SCR-PA-015a records the *choice*.
- **The three-second cancel window is not a spec number**, and it spends the D-33 budget. §14.3
  fixes p99 ≤ 5 s for the *dispatch* and says nothing about a confirmation; three is what is left of
  that urgency once a mis-tap on the largest control on screen has to be recoverable. A deliberate
  tap sends immediately. `SosSmsStatus.FAILED` is **not** an error state and does not colour like
  one: the alert is recorded and on the admin live feed either way.
- **D-34's share link is minted after the alarm and is allowed to fail.** Putting
  `POST /v1/trip-share/{id}` in front of `POST /v1/sos` would spend the five-second budget on a URL;
  an alarm that went out with no link to hand on is still an alarm that went out.
- **Only the primary emergency contact wears the `Sent` pill.** iam-svc promotes exactly one onto
  `iam.users.emergency_contact_name/phone` because a join does not fit the SLO, so one contact is
  texted. The whole list is drawn (D2' §SCR-PA-029 says `LazyColumn`) and the rest carry no status —
  showing `Sent` against three names when one was texted would be a fan-out the platform does not do.
- **This app passes `?lang=` on the FAQ, and the driver app deliberately does not.** `SupportApi`'s
  KDoc argues for `null` — let the server use the profile — and that is right where the profile *is*
  the answer. Here AL-26 makes language a **device-first** choice that the server write is allowed to
  lag (`languagePendingSync`), and `PassengerLocale.wrap` is what everything else on screen is drawn
  in. So the FAQ is asked for in the language the app is *drawing* in; `null` still means "use the
  profile's" before SCR-PA-002 has been answered.
- **SCR-PA-030's FAQ is an accordion, one row open at a time.** D2' says *"FAQ accordion"* and the
  wireframe draws a `＋` per row, so the body is fetched into state beside the id that is open — not
  into a sheet. The ticket **thread** is a `ModalBottomSheet` for the opposite reason: the baseline
  draws no frame for it, and a route the wireframes have no picture of would be a deviation.
- **There is one ticket category on this side.** `daily_fee_refund` is the *driver's* fee (US-9.23),
  so no passenger-facing category routes to the Finance queue and SCR-PA-030 has no quick action —
  every ticket it raises is `general` and therefore Support's (US-14.13). A ticket another service
  raised (fare-svc's AL-47 driver-QR dispute) still renders, from its own key.
- **The screenshot is read to bytes at the pick and uploaded by Submit.** The system photo picker's
  grant dies with the pick and does not survive a process death, so a `Uri` parked in state would be
  a permission failure at the one moment it must not fail — and a failed upload never costs the
  passenger their ticket, because what they wrote is the part support acts on.
- **`RoutePlaceholder` is gone.** C084 took the last three destinations, so every route in
  `PassengerNavHost` now registers a real screen; the two `placeholder(…)` helpers and the
  `route_placeholder_*` strings went with it.
- Reusable UI added here: `MageRideCtaTonal` (the wireframe's `cta tonal`), `DateFormat.timer`
  (`01:24`), and `ControlTokens.CallAction`/`.CallEnd`/`.SosButton`/`.SosHalo`/`.DialogIcon`.
  `support/SupportScreen.StatusPill` is internal so the sheets draw the same chip as the list.

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
- **A missing H3 engine degrades the plane; it does not kill the app** —
  `PassengerLiveMap.geocells`. The graph hands this class a `SupervisorJob() + Dispatchers.Default`
  scope, and a supervisor job stops a failed child cancelling its siblings; it does **not** stop the
  failure reaching the thread's default uncaught handler. Every `H3Grid` call here is inside a
  `scope.launch`, so a device with no native library was a `FATAL EXCEPTION`, not a caught failure.
  The latch is set once and never retried — a missing `.so` does not turn up later, so a retry per
  fix would be one crash per second rather than one. What is lost is the nineteen cells and every
  vehicle with them; the socket, the ride and package events, booking and the passenger's own dot
  all keep working. An emulator is the ordinary way to reach it — see "Things that will bite".

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
- **Neither package OTP has a read.** The pickup code is returned once on
  `POST /v1/rides/request`; the delivery code arrives only in the `package_picked_up` FCM payload.
  `RideDetail` has fields for neither and §2.3's `rides` projection has no columns.
- **No passenger-facing read lists their own scheduled rides.** `dispatch.yaml` has the driver's
  list and a cancel-by-id; SCR-PA-022's Scheduled tab renders empty until one exists.
- **`RideHistoryRow` carries no `kind`**, so the Packages tab splits on `CashOnDeliveryCollected` —
  a package paid another way looks like a passenger ride.
- **§2.3's `rides.payment_method` CHECK still lists `lankaqr`/`onepay`**, the same AL-57/AL-59
  staleness as `ride.yaml`'s booking enum.
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
- **`iam.yaml`'s `DefaultPaymentMethod` predates AL-57/AL-59** — still `[cash, lankaqr, onepay]`,
  and C003's `CHECK` with it. Both surviving preference-shaped rails cannot be expressed: `wallet`
  has no value at all, and the contract's own text excludes the driver QR from a *stored* preference.
  So SCR-PA-027 offers Cash and Wallet, writes the account only for Cash, and a **wallet default is
  device-local** until the enum gains it (Δ C083).
- **No contract carries a human-readable passenger number.** SCR-PA-027's card and SCR-PA-033's
  header both print `PAX-90431` in the wireframe; `UserProfile` has `userId` and nothing else, so
  the ULID is what is drawn. Inventing a `PAX-` prefix would be the client minting an identifier.
- **There is no avatar upload route anywhere in the contract set.** `photoUrl` is a *URL* and the
  whole upload surface is `POST /v1/support/screenshots`, the Mode B transfer slip and the driver's
  documents. SCR-PA-027b draws the 📷 badge and *"Take photo or upload"* **disabled**, the same call
  C077 made on SCR-PA-004.
- **`mobile_db_schema.md` §2.1's on-device `saved_addresses` has no writer**, here or on the driver
  side (§1.3's `emergency_contacts` likewise). Both screens read the server, which is where US-22.6
  puts the list anyway (the eager-fetch set is a `GET /v1/me/bootstrap` concern). A mirror with no
  outbox between it and the API would be two writers for one list.
- **`POST /v1/sos` has no positionless form.** `TriggerSosRequest.lat`/`.lng` are required, so
  SCR-PA-029 cannot arm until the handset has answered once. BR-29.4 contemplates exactly this case
  for the *web* surface — *"geolocation denied → SOS still fires with the last known driver-reported
  position"* — and the app-facing contract carries no equivalent. In practice this is milliseconds:
  `PassengerLocationSource` emits the last known fix before it registers for updates.
- **No contract mints a ticket number.** SCR-PA-030's card prints `#TK-4521` and there is no `TK-`
  series — a `support.tickets` id is a ULID — so the card leads with the category, the same call
  C083 made about `PAX-90431` and C074 about `DRV-22011`. The wireframe's *"Attached trip
  PAX-90431-0617"* line is drawn without the invented number.

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
- **`com.uber:h3`'s native library does not survive an APK on its own, and the failure is a process
  kill rather than a missing class.** The jar carries its natives as ordinary RESOURCES —
  `android-arm64/libh3-java.so`, `windows-x64/libh3-java.dll` and eleven more — and
  `H3Core.newInstance()` unpacks whichever matches the running ABI at runtime. That works on the
  desktop JVM every test in this module runs on, and can never work inside an APK: AGP's
  java-resource merger drops every `*.so`, and the native-lib merger only recognises
  `lib/<abi>/*.so`, which `android-arm64/…` is not. So the APK shipped 1.5 MB of macOS and Windows
  binaries and **not** the one file Android needed, and the first `grid.cellAt()` after a location
  grant died on a `Dispatchers.Default` worker. `extractH3Natives` in the build script repackages
  the `.so` into a real jniLibs tree and `H3JavaGrid` loads it with `newSystemInstance()`; the
  unpack path stays as the fallback so the JVM target is unaffected. **h3 4.4.0 ships `android-arm64`
  and `android-arm` and nothing else** — there is no x86 or x86_64 native, so an EMULATOR has no H3
  whatever that task does. See the live-plane note above for what the app does about it.
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
- **A write that publishes nothing on success cannot be awaited on state at all** (Δ C084). Several
  view models set the new value **before** launching the request and touch state again only on
  failure — `SettingsViewModel.chooseDefaultPayment` is the case — so `await { it.x == chosen }`
  returns while the call is still in flight and the assertion after it fails on a loaded host.
  `MainDispatcher.kt`'s `FakeApiBackend.awaitCall(operationId)` is the counterpart to `await` for
  those; `SettingsViewModelTest` was fixed onto it.
- **Unit tests run with `isReturnDefaultValues = true`** and the working directory is the module
  directory, which is what lets `ManifestTest` and `StringResourceTest` read the real files.
- **iOS does not compile on this host** (root CLAUDE.md). C094's SwiftUI shell mirrors these tokens
  and route names — keep them in step.
