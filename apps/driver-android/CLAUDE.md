# Driver Android Conventions

- Kotlin, Jetpack Compose, Material 3
- Depends on shared/kmp — import DTOs, the API client, the domain state machines and the MQTT
  contracts from there. Nothing that exists in `:shared` may be reimplemented here.
- Screen groups map to D2' §B + the `specs/wireframes/driver_android.html` wireframe
- minSdk 26 — Android 8.0 (URD NFR-22); Gradle project path is `:apps:driver-android`
- Verify: `./gradlew :apps:driver-android:testDebugUnitTest :apps:driver-android:assembleDebug`
  (the Android SDK is installed on this build host at `/opt/android-sdk`; `local.properties` points
  AGP at it)

## The shell (C067) — what exists, and where a screen plugs in

C025's walking skeleton is **gone**. Its four files (`MainActivity`, `MainViewModel`,
`DriverMqtt`, `SkeletonClient`) were declared throwaway by their own component and were deleted
here; nothing in this module is throwaway any more.

```
lk.mageride.driver
├── DriverApplication.kt      Koin start, notification channels, Play Integrity warm-up
├── MainActivity.kt           the ONE Activity. Do not add a second.
├── di/                       DriverEnvironment (the only BuildConfig reader) + the app module
├── nav/                      DriverRoute (every destination), DriverTab, DriverNavHost, bottom bar
├── shell/                    DriverShell (Scaffold host), offline banner, update gate, connectivity
├── ui/theme/                 D2' §0.2 tokens — colour, type, spacing/radius/elevation/controls
├── ui/component/             MageRideCta + the cluster-1 controls (C068)
├── map/                      MageRideMap (MapLibre host), MapStyles, VehicleLayers (MAP-*)
├── location/                 PositionForegroundService, PositionPipeline, MQTT transport, journal
├── push/                     FCM service, PushRouter (deep links), channels, PushTokenProvider
├── capture/                  C069 · SCR-DA-005 — the scanner, its geometry and its coordinator
├── onboarding/               C068 · SCR-DA-001, 002, 003, 003a, 007 + their data layer
├── vehicle/                  C069 · SCR-DA-004…004c, 006, 026/026a + their data layer
├── home/                     C070 · SCR-DA-010, 011, 013, 014 + the map and the offer inbox
├── ride/                     C070 · SCR-DA-015 + its three sheets and its data layer
├── delivery/                 C071 · SCR-DA-016a/b/c + the proof queue and its data layer
├── jobs/                     C072 · SCR-DA-017, 018 + the dispatch reads and the Colombo labels
├── level/                    C072 · SCR-DA-019
├── earnings/                 C072 · SCR-DA-020 + its buckets and its chart
├── wallet/                   C073 · SCR-DA-021…025 + the top-up rails and the ledger
├── tracker/                  C074 · SCR-DA-027 + the device-QR scanner and the publisher gate
├── sharing/                  C074 · SCR-DA-028 — Mode B grants, per vehicle
├── profile/                  C074 · SCR-DA-029 + its three editors and the contact picker
├── history/                  C074 · SCR-DA-030 + the rate-passenger sheet
└── menu/                     C070 · SCR-DA-036 — AL-31's drawer, as a tab
```

**To add a screen (C070–C075):**

1. Its route is already in `nav/DriverRoute.kt`. Use it; do not invent a path.
2. Replace its `placeholder(...)` line in `nav/DriverNavHost.kt` with the real composable. That file
   is the only `NavHost` in the app.
3. Put every user-facing string in **all three** `res/values*/strings.xml` files at once.
   `StringResourceTest` fails the build on a key that exists in one and not the others, on a
   translation left equal to its English, and on a format placeholder dropped in translation.
4. Reach for `MageRideTheme.spacing` / `.radius` / `.elevation` / `.status` / `.vehicle` / `.mode`
   and `MaterialTheme.colorScheme` — never a raw `dp` or hex. `ThemeTokensTest` holds §0.2.
5. The full-width orange bar in the wireframes is `MageRideCta`, not a `Button`.

## Cluster 1 (C068) — what the next screen group can reuse

- **`OnboardingRouter.next(...)` is the only place that decides where a driver belongs.** Splash,
  the login screen after a verify and Profile Setup after a save all call it. If a new gate is ever
  added before Home it goes in that function, not in a screen.
- **`DocumentCaptureCoordinator` is the seam to SCR-DA-005** (C069's scanner). A screen calls
  `open(target)` and navigates to `DriverRoute.DocumentCapture`; the scanner reads `pending`, shows
  the right title and calls `deliver(image)`. The route carries no arguments, so this is the only
  way to say what a capture is for.
- **Images go up with the request that needs them, and there is no uploader seam.** MCS-01 landed
  the multipart arm of `PUT /v1/drivers/profile` and `PUT /v1/vehicles/{id}/onboarding/{step}`, so
  registry-svc writes `docs.uploads` and the row that references it in one call; the
  `DriverDocumentUploader` interface C068 left behind for a route that did not exist was deleted
  with it. Nothing on this surface mints a `docs.uploads` id for a client to hold.
- **`OnboardingPreferences`** holds the three answers given before there is a session (language,
  operating city, permissions-seen) and `OnboardingRepository.syncPreferences()` pushes the first
  two to `iam.users` on the first authenticated call.
- **Language is applied by `MainActivity.attachBaseContext`** through `DriverLocale.wrap`, and a
  change calls `recreate()`. Per-app locale (`LocaleManager`) is API 33+; the floor here is 26.
- Reusable UI: `SelectionBox`, `SectionLabel`, `PagerDots`, `IllustrationPanel`,
  `LabelledTextField`, `PhoneNumberField`, `OtpEntry`/`OtpProgress`, `CaptureTile`,
  `AdminVerifyChip`, `ExtractedFieldRow`, `NoticeCard`, and `ui/VehicleTypeLabels.kt`.
- **A proper noun is data, not copy.** The language endonyms (`සිංහල`), the `+94` prefix and the
  `7X XXX XXXX` mask are Kotlin constants, because three identical values in the three
  `strings.xml` files is exactly what `StringResourceTest` (correctly) fails on. City names come
  from `config.operating_cities` for the same reason.

## Cluster 2 (C069) — the Mode-C wizard and the scanner

- **`CropQuad` and `DocumentEdgeDetector` are pure Kotlin and are where SCR-DA-005's decisions
  live.** Everything that touches a `Bitmap` is in `DocumentImaging` and is untestable on this
  host, so the geometry (winding order, refusing a drag that folds the quad, the output size) and
  the edge-detect proposal are deliberately kept out of it. If you add behaviour to the scanner,
  add it there.
- **The scanner's frame is fixed at 4:3 and the viewfinder is `FIT_CENTER`.** The crop quad is
  normalised and applied to the *captured still*, so the preview and the capture have to be the
  same rectangle. A `FILL_CENTER` preview crops the sides away and every corner then means
  something different in the file that is uploaded.
- **`CaptureSource.CAMERA_DRAG_CROP` is stamped in `DocumentScannerViewModel.confirm()` and
  nowhere else** (AL-43). `readImage` stamps `GALLERY`. A screen never chooses a provenance — it
  is what the Verification-Officer queue sorts on.
- **AL-30 lives in `VehicleOnboardingRepository.resume()`, as a `ResumePoint`.** Re-opening the
  wizard opens the first non-verified step and never Step 1; when the current vehicle is approved
  the same call answers `Fresh` and the wizard starts a **new** vehicle. No screen decides this.
- **`POST /v1/vehicles` IS Step 1/4** (Δ C029). It carries the type and plate the `details` step
  stores, so a fresh vehicle comes back with one saved step and `nextStep = insurance`. Do not add
  a second call to save details on a vehicle that was just created.
- **`ActiveVehicleStore` is local, on purpose.** No operation on the platform sets a driver's
  active vehicle, and none is needed: the MQTT username **is** the vehicle id, so the broker learns
  the choice at CONNECT. C070 reads this store for the dashboard chip and the US-9.6 go-online gate.
- **`VehicleOnboardingSession` is to SCR-DA-006 what `DocumentCaptureCoordinator` is to
  SCR-DA-005** — the route carries no arguments, so the vehicle id is passed through a process-wide
  holder instead.
- Reusable UI added here: `StatusPill` + `StatusTone`, `ModeCBadge`, `CaptureTile(height = …)`,
  `VehicleColors.forType`, and `ScannerColors` in `ui/theme`.

## Cluster 3 (C070) — the dashboard, the offer and the ride

- **Home is ONE destination.** D2' merged SCR-DA-012 into SCR-DA-010 and makes SCR-DA-011 *the*
  home dashboard for a Mode A/B vehicle, so `HomeScreen` swaps only its sheet on
  `LiveVehicle.isScheduledMode`. The map, the header, the banners and the offer takeover are shared.
- **SCR-DA-014 is a window-sized `Dialog`, not a route** — back is disabled and fifteen seconds is
  not long enough to navigate. That is what `PushRouter` already meant by routing a `ride_offer`
  push to Home.
- **`OfferInbox` is the seam from FCM into `:shared`'s `OfferSession`**, and it is a process
  singleton because an offer arrives with no composition anywhere. The push carries `offerId`,
  `rideId`, `expiresAt` and a *rendered* fare and nothing else; everything the badges need comes
  from one `GET /v1/rides/{rideId}` inside the window, which also supplies the version the accept
  needs (R-14).
- **`DriverLocationSource` is not `PositionForegroundService`.** The service owns the fixes that
  reach the broker and outlives every composition; the source is a cold subscription for a *screen*
  — the AL-31 own-vehicle marker, `GoOnlineRequest`'s position, SCR-DA-011's distance.
  `PositionPublisher` is the matching seam that keeps a `Context` out of a view model.
- **Going online is two calls in one order**: `POST /v1/standby/online` then start publishing; going
  offline is stop publishing then `POST /v1/standby/offline`. A driver in the candidate pool who is
  not publishing is offered rides that cannot find them.
- **A driver-QR ride is not over at `PaymentPending`.** AL-47's confirm settles the *payment*; the
  ride reaches `CashSettled` only once fare-svc's settlement travels the outbox. `ActiveRideState`
  carries `qrAttested` and `finished` is sticky, because re-reading into that window used to put the
  confirm sheet back up on a driver who had just answered it.
- **`:shared` has `LiveHub`'s contract and no SignalR client**, so SCR-DA-015 polls
  `GET /v1/rides/{rideId}/state` — D3' §3.1's documented fallback. Delete that loop when a hub
  client lands.
- Reusable UI added here: `DashboardSheet`, `OnlineToggle`, `DashboardBanner`, `CountdownRing`,
  `SolidBadge`, `VehicleChip`, `StatusCta`, and `ui/MoneyFormat.kt` (`Rs 1,240`, `1.2 km`,
  `01:12:40`) — all integer arithmetic, because `Money` deliberately does not format itself.

## Cluster 4 (C071) — the delivery

- **A package ride is the SAME destination as a passenger ride.** R-01 keeps one aggregate and
  `PushRouter` resolves both `mageride://ride/{id}` and `mageride://package/{id}` to
  `DriverRoute.ActiveRide`, so the kind is not known until the ride has been read: `ActiveRideScreen`
  reads it and hands over to `DeliveryScreen` on `RideKind.PACKAGE`. SCR-DA-015's poll is switched
  off for a package (`ActiveRideState.isPollable`) so only one loop folds server states onto a ride.
- **Which of the three sheets is up is derived from the ride, never counted.** `package.picked_up`
  IS the `→ InProgress` move (D5' §11 skips `DriverArrived` for a parcel), so a driver whose app died
  between the two doors comes back to the right sheet from one read. Sheet 1's **Start delivery** is
  the one local step and sends nothing.
- **`PackageHandoff` (C015) is the five-attempt rule — do not count attempts in a screen.** The view
  model holds its `RideProjection` for the screen's whole life rather than re-seating it per read,
  because re-seating throws the handoff away with it. `canSubmit` refuses a malformed code without
  spending an attempt; the **fifth** wrong code locks the gate (which is also when ride-svc raises
  the admin-queue item), so there is no sixth request to make.
- **The proof photograph completes the delivery** (Δ C037), so it is uploaded by the *"Delivery
  completed"* tap and not by the shutter. `ProofUploadQueue` is in memory and `mobile_db_schema.md`
  §3.6's durable table is deliberately unused here — read that class's KDoc before changing it.
- **`DocumentCaptureTarget.DELIVERY_PROOF` is the first non-document use of SCR-DA-005.** A proof
  photo goes to `rides.proof_artifacts` and carries no `captured_via`, so AL-43's provenance stamp is
  dropped at the upload rather than filed against the Verification-Officer queue.
- **AL-33's fences:** *"Delivery completed"* replaces *"Cash received (COD)"* and nothing here calls
  `POST …/cod-collected`; both call buttons are a **direct PSTN dial** with no AL-48 chooser, and
  each logs its own `CalleeRole` through `RideContact.startCall(rideId, calleeRole, type)`.
- Reusable UI added here: `ui/PackageLabels.kt` (the size and payment-method label tables SCR-DA-014
  used to keep privately) and `ControlTokens.CallButtonWidth`/`.CallButtonHeight`.

## Cluster 5 (C072) — the board, the level and the money

- **The Job Board is POST-INTENT ONLY, and dispatch-svc has no route that would let it be
  otherwise.** `GET /v1/rides/job-board` and `POST …/{id}/intent` are the whole surface; at T-30 min
  the booking becomes a ride and reaches the driver as an ordinary offer on SCR-DA-014. Anything on
  this screen that looked like an accept would be a second way to win a ride, racing the first.
- **"Ranked by Driver Level" ranks DRIVERS, not rows.** D5' §3.7 picks *"the closest
  intent-submitting driver by Level"* at T-30; a device knows neither the other bidders nor their
  levels, which is why `:shared`'s `JobBoard` deliberately ships no ranking function. The board is
  ordered by pickup time, soonest first.
- **`JobStanding.hasJobBoardAccess` is three-valued and the third value matters.** `true` opens the
  board, `false` is US-6A.8's gate, and **`null` is "reputation did not answer"** — which must never
  render as the gate, because that tells a Level-3 driver they are Level 1. `JobBoardState` carries
  `isUnavailable` for exactly that.
- **Every clock on this cluster is Asia/Colombo** (D-38). `ScheduleLabels` and `EarningsBuckets`
  each resolve `ZoneId` from `:shared`'s `BusinessCalendar.ZONE` rather than naming the zone twice,
  and both are tested against `Fixtures.MIDNIGHT_EDGE`, which is already the next day in Colombo and
  is not in UTC.
- **The T-30 lead is `JobBoard.GO_LIVE_LEAD` on both screens.** SCR-DA-017's expiry and SCR-DA-018's
  *"reminder sent"* are the same instant (D5' §3.7 dispatches then; §14.4 pushes then), so neither
  screen keeps a threshold of its own. `JobBoardViewModel.EXPIRY_FADE` is the only local number, and
  it is the animation, not the rule.
- **The earnings summary is query-svc's arithmetic and is printed as sent.** `EarningsSummary` is
  the aggregate; the per-trip rows feed the breakdown list and the chart and are never re-summed
  into a second total. R-05 means an in-flight payment is on neither.
- **Levels stop at 3.** The wireframe draws *"510 / 500 pts → Level 4"*; D5' §4.2 caps at
  `min(level + 1, 3)`. The layout is the wireframe's and the number is D5''s — see
  `DriverLevelViewModel`'s KDoc before changing the points line.
- **Two view models take an injected `clock`** (`JobBoardViewModel`, `ScheduledRidesViewModel`).
  Their whole behaviour is a comparison against T-30, and a test that could only wait for real time
  would have to sleep for half an hour to assert the rule.
- Reusable UI added here: `StatusTone.INFO` (the wireframe's `pill-status.info`, resolved to
  `colorScheme.secondary` — the same role C070's info banner uses), `MoneyFormat.radius`, and
  `ControlTokens.LevelBadge` / `.LevelProgress` / `.ProgressGap` / `.EarningsChart`.
- **Three routes were added** (`ScheduledRides`, `DriverLevel`, `Earnings`) because the shell's
  table had only `Jobs`, and their entry points are SCR-DA-017's app bar, SCR-DA-010's `L3` badge
  and SCR-DA-010's *"Today: 4 trips · Rs 3,180"* line. The Menu stays at SCR-DA-036's **eight**
  rows — `MenuDestinationTest` pins that, and none of these three belongs there.

## Cluster 6 (C074) — the tracker, the sharing, the profile and the history

- **Pairing a tracker stops this phone publishing for that vehicle, and the rule lives at the
  publisher seam.** `TrackerPositionPublisher` decorates `AndroidPositionPublisher` and refuses
  `start(vehicleId)` for a vehicle in `TrackerBindingStore` (US-3.6, *"exactly one publisher at a
  time"*). It is a decorator rather than a check in a screen because **three** doors reach the
  position service — SCR-DA-010's go-online toggle, SCR-DA-011's Start Journey and US-5.10's
  Restart — and a rule written at one of them would be missing from the other two. `stop()` is
  never gated. The interface has exactly one binding, in `trackerAndProfileBindings`; C070's
  `dashboardBindings` no longer declares it.
- **`TrackerBindingStore` is local because nothing on the app-facing surface answers the question.**
  Not a preference the way `ActiveVehicleStore` is: `POST /v1/vehicles/{id}/device` returns a
  `bindingId` and **nothing reads one back** — no registry read carries a device, and
  `GET /v1/trackers/{imei}` is provisioning-svc's, which `:shared` has no client for. Pair on one
  handset and the other still publishes; that is the honest limit of a device-local answer and the
  reason the gap is worth closing server-side.
- **SCR-DA-027's device-QR scanner is a `Dialog`, and its decoder is ZXing.** A QR read is a short
  string that comes straight back to the view model underneath, so it needs neither a route nor a
  `DocumentCaptureCoordinator`. `com.google.zxing:core` was already a dependency (C073's LankaQR
  writer); its **reader** half decodes the `YUV_420_888` luminance plane straight off an
  `ImageAnalysis` frame, so there is no `Bitmap` per frame and no ML Kit model to download.
  `TrackerImei.imeiIn` is where "which fifteen digits in this payload" is decided, and it refuses a
  payload with two candidates rather than picking one.
- **SCR-DA-028 is scoped by its selector and re-reads on every change.** Both list endpoints take
  the vehicle in the path, so `SharingViewModel.selectVehicle` **empties** the queue and the roster
  before fetching that vehicle's own — AL-35's *"never mixed across vehicles"* is a re-read, not a
  filter. Only Mode A/B vehicles are offered; a Mode C tuk has no subscribers.
- **Sharing is two services on purpose.** registry-svc owns the entitlement (`…/share`,
  `…/subscribers`) and subscription-svc owns the request queue (`/v1/mode-b/…`), because accepting a
  request creates the grant **and** starts the subscription in one transaction. registry's
  `…/share/{grantId}/accept` is the *invited user's* half of an invitation and is the other
  direction. Revoking is by **user**, since `Subscriber` carries no `grantId`.
- **A new grant does not join the grantee list** (US-4.3b): visibility begins when the passenger
  accepts. The screen acknowledges the offer and leaves the roster alone.
- **`ShareExpiry` is two time-zone hops and both are easy to get wrong.** M3's date picker answers
  **UTC midnight** of the tapped day, and a grant should lapse at the **end** of that day in
  Colombo. Read its KDoc before touching it.
- **SCR-DA-029 is where a driver reads their own platform id** — C073's handoff named this screen,
  and `ui/PlatformId` is now the one place the `Ulid` pattern lives (`WalletInput` delegates to it).
  There is no `DRV-22011` and no `PAX-90431`.
- **The emergency contact is replaced, never accumulated.** `EmergencyContact.isPrimary` is *"exactly
  one per account that has any"* because D-33's SOS is p99 ≤ 5 s off a denormalised column, so
  `ProfileRepository.saveEmergencyContact` updates in place and only a driver with none creates one.
  The picker is `ACTION_PICK` over `Phone.CONTENT_URI`, which needs **no `READ_CONTACTS`** — the
  returned row carries its own read grant.
- **Notification switches are grouped, and nothing safety-critical is offered.** `SOS_TRIGGERED`,
  `SOS_RESOLVED`, `RIDE_CANCELLED` and `SCHEDULE_NOT_STARTED` cannot be muted; a switch the platform
  ignores is worse than no switch. An **absent** key reads as **on** — US-10.7 is opt-out. The whole
  map is sent back on a save, unknown keys included, because the event list grows without a contract
  change.
- **SCR-DA-030's list is query-svc's `GET /v1/trips/{driverId}`, not `GET /v1/rides/history`.** The
  first spans both planes and is driver-scoped in its own SQL; the second is Mode C only, carries a
  passenger-facing `driver` block (AL-36) and is unmapped (C048). `TripSummary` has neither distance
  nor rating, so the screen reads one **detail per row, concurrently** — and `TripDetail.rating` is
  joined on `rater_id = @UserId`, which makes it *"the stars I already left"* and is what stops a
  re-opened screen offering to rate a trip twice.
- Reusable UI added here: `ui/Symbols` (`·`, `—`, `★`, `☆` — `MoneyFormat.EMPTY` and
  `ScheduleLabels.UNKNOWN` now point at it), `ui/PlatformId`, `ui/vehicleLabel`, and `VehicleChip`
  gained `selected` / `onClick` / `badge` so the same chip is also SCR-DA-028's selector.
  `capture/cameraProvider` went `internal` so the QR viewfinder binds an `ImageAnalysis` through the
  same helper; `Language.endonym` / `.englishName` went `internal` so the profile's language sheet
  and SCR-DA-002 share one endonym table.

## Rules this module is built on

- **AL-31: no hamburger.** Navigation is the bottom-nav **Menu** tab, and `nav/DriverTab.kt` is the
  only place a top-level destination is declared. `NavigationShellTest` asserts there are exactly
  four tabs and that Menu is one of them.
- **No dynamic colour.** D2' §0.2 is the single source of truth shared with Figma, SwiftUI and the
  Tailwind preset; `dynamicLightColorScheme` would hand the app the wallpaper's palette.
- **`DriverEnvironment` is the only file that reads `BuildConfig`.** The gateway origin and the MQTT
  host are the two values a release build cannot afford to have wrong in two places.
- **Background GPS and MQTT stay native.** `PositionForegroundService` owns the socket, the fixes,
  the wake lock and the notification; `:shared` supplies the cadence (`AdaptiveRateEngine`), the
  topics (`MqttTopics`), the payload (`PositionCodec`) and the replay pacing
  (`PositionReplayQueue`). `PositionPipeline` is the seam between them and is where the R-17
  behaviour is tested, on the JVM, with no radio.
- **`seq` must never rewind.** It comes from C018's `PersistentPositionSequencer` through
  `GpsBufferJournal`; a counter that restarted at zero makes `position-processor-svc` discard
  everything the app publishes while the app believes it is publishing (R-17/T-05).
- **The MQTT username is the vehicle id**, and the credential is the MQTT **session** JWT — never
  the API access token. EMQX validates it at CONNECT only, so a rotation means a reconnect.
- **A deep link is resolved, not trusted.** `PushRouter` maps a `mageride://…` URI onto a known
  `DriverRoute`; an unrecognised one opens nothing.

## Things that will bite

- **`org.jetbrains.kotlin.android` is not applied.** AGP 9 has built-in Kotlin support and refuses
  the plugin outright.
- **The `google-services` plugin is not applied either**, and there is no `google-services.json`.
  firebase-messaging compiles and `DriverMessagingService` is registered, but **FCM does not deliver
  until C124 lands the Firebase project**. Nothing else about push is blocked by it.
- **MapLibre is the `-opengl` flavour** (`org.maplibre.gl:android-sdk-opengl`). The default
  `android-sdk` artifact requires Vulkan 1.0 in its manifest, which Play uses to filter devices —
  on the Android 8.0 floor that cuts off exactly the budget handsets this platform is for. Do not
  add `android-sdk-ktx`: it depends on the default artifact and fails `checkDuplicateClasses`.
- **Kotlin block comments nest.** A KDoc containing `values*/strings.xml` closes itself on the `*/`
  and the file stops parsing several declarations later. Same trap C014 hit.
- **detekt's `LongMethod` and `LongParameterList` carry `ignoreAnnotated: ['Composable']`**
  (C068, in `config/detekt/detekt.yml`). A screen function is the wireframe's layout tree and a
  component's parameter list is an M3 slot API; nothing non-composable is exempt.
- **The C019 test kit's top-level functions need the jar's `.kotlin_module`.** Without it
  `FakeApiBackend` imports and `backend.mageRideApi()` does not — Kotlin finds a package's
  file-facade classes through that file alone. Fixed in `shared/kmp/build.gradle.kts` by C068.
- **ktlint's `function-naming` needs the `@Composable` exemption**, which is set once in the repo
  root `.editorconfig`. detekt's config is `config/detekt/detekt.yml`, also shared.
- **`Modifier.menuAnchor()` with no argument is deprecated in Material3 1.4**, and the type is
  spelled **`ExposedDropdownMenuAnchorType`** there — `MenuAnchorType` is the 1.3 name and does not
  resolve. The Compose BOM `2026.06.01` pins material3 1.4.0.
- **detekt's `TooManyFunctions` has NO `@Composable` exemption** (only `LongMethod` and
  `LongParameterList` do), and the ceiling is 11 per file. A wireframe screen with more bodies than
  that gets split — C069 put its label table in `vehicle/VehicleOnboardingLabels.kt` and its CameraX
  plumbing in `capture/CameraXBinding.kt` for exactly this reason.
- **`MagicNumber` excludes `ui/theme` and nothing else.** A hex or a `dp` anywhere else is a build
  failure, which is the same rule as "never a raw dp or hex" above, enforced.
- **CameraX is pinned at 1.6.1** — the newest whose AAR metadata still says `minCompileSdk=36`.
  1.7.x is alpha and would raise the floor past this module's `compileSdk`.
- **`kotlin-test` resolves to no variant under AGP's built-in Kotlin.** Use `libs.kotlin.testjunit`
  (the `kotlin-test-junit` artifact) — the alias is deliberately not named `kotlin-test-junit`,
  because that would turn `kotlin-test` into a catalogue *group* and break `libs.kotlin.test.get()`
  in `shared/kmp/build.gradle.kts`.
- **Unit tests run with `isReturnDefaultValues = true`** and the working directory is the module
  directory, which is what lets `ManifestTest` and `StringResourceTest` read the real files.
- **iOS does not compile on this host** (root CLAUDE.md). Nothing here is iOS, but C085's SwiftUI
  shell mirrors these tokens and route names — keep them in step.
