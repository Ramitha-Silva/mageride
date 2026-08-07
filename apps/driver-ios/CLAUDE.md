# Driver iOS Conventions

- Swift + SwiftUI; consumes the KMP shared module as an XCFramework via SPM
- Parity-fenced to `apps/driver-android` — any behaviour difference beyond a D2' Section C
  platform delta needs a micro-change-set
- Screens map to D2' §B + the `specs/wireframes/driver_ios.html` wireframe (41 SCR-DI ids)
- Stays native (ADD §18.2): CLLocationManager background location, CocoaMQTT, Keychain +
  Secure Enclave device binding, App Attest, CallKit, MapLibre GL Native iOS
- Trilingual: `Localizable.strings` for si / ta / en, Dynamic Type respected
- Not a Gradle project — this is an Xcode project owned by C085; it is deliberately absent
  from `settings.gradle.kts`
- **iOS deployment target is 16.0** (C085's decision — see `Config/Shared.xcconfig` for the
  reasoning; no spec states one, which C000 recorded as a gap)
- **This Linux build host cannot compile iOS.** Generate code here; build and verify on macOS.

## Verify (macOS only)

```
./gradlew :shared:assembleXCFramework        # MUST run first — SPM resolves the artefact by path
xcodebuild -project apps/driver-ios/DriverApp.xcodeproj -scheme DriverApp \
  -destination 'platform=iOS Simulator,name=iPhone 15' test
```

The XCFramework is a **build output** consumed by a local Swift package
(`shared/swiftpm/MageRideShared`), so a tree that has never assembled it fails at package
resolution with "artifact not found" rather than at link time. That is the order
`.github/workflows/ci.yml`'s iOS leg already uses.

## The shell (C085) — what exists, and where a screen plugs in

```
apps/driver-ios/
├── Config/                      Shared/Debug/Release xcconfig — the gateway, MQTT, PMTiles
├── Tools/generate_xcodeproj.py  writes DriverApp.xcodeproj from the tree (see below)
├── DriverApp.xcodeproj/         the committed project — CI runs xcodebuild against it
├── DriverApp/
│   ├── Capture/                 C087 · SCR-DI-005 — the VisionKit scanner and the capture seam
│   ├── Vehicle/                 C087 · SCR-DI-004…004c, 006, 026/026a + their data layer
│   ├── Home/                    C088 · SCR-DI-010, 011, 013, 014 + the map, the offer inbox and
│   │                            the screen-level GNSS source
│   ├── Ride/                    C088 · SCR-DI-015 + its three sheets and its data layer
│   ├── Delivery/                C089 · SCR-DI-016a/b/c + the proof queue and its data layer
│   ├── Jobs/                    C090 · SCR-DI-017, 018 + the dispatch reads and the Colombo clock
│   ├── Level/                   C090 · SCR-DI-019
│   ├── Earnings/                C090 · SCR-DI-020 + its buckets and its chart
│   ├── Menu/                    C088 · SCR-DI-036 — AL-31's drawer, as a tab
│   ├── Onboarding/              C086 · SCR-DI-001, 002, 003, 003a, 007 + their data layer
│   ├── UI/                      the wireframe's shapes as views — fields, tiles, cards, pills
│   ├── DriverApp.swift          @main. One App, one shell.
│   ├── DriverAppDelegate.swift  the three callbacks SwiftUI has no equivalent for
│   ├── Info.plist               background modes, purpose strings, the MageRide config dict
│   ├── DriverApp.entitlements   App Attest, APNs, the payment Universal Link
│   ├── DI/                      DriverEnvironment (the only Info.plist reader) + DriverGraph
│   ├── Nav/                     DriverRoute (every destination), DriverTab, the navigator,
│   │                            DriverDestinations (the ONE route→view switch)
│   ├── Shell/                   DriverShell (TabView host), offline banner, update gate,
│   │                            connectivity, the three cross-cutting subscriptions
│   ├── Theme/                   D2' §0.2 — colour, type, spacing/radius/elevation, the CTA
│   ├── Map/                     MageRideMapView (MapLibre host), MapStyles, VehicleLayers
│   ├── Location/                PositionService (CLLocationManager) + CocoaMqttTransport
│   ├── Push/                    PushRouter (deep links), PushTokenProvider (APNs via FCM)
│   ├── Security/                DeviceBinding — App Attest and the Keychain, documented
│   └── Resources/               Assets.xcassets, en/si/ta .lproj, the two map styles
└── DriverAppTests/              theme, localisation, navigation, push, map, environment
```

## Cluster 2 (C087) — the Mode-C wizard, the scanner and My Vehicles

```
DriverApp/
├── Capture/     SCR-DI-005 — the VisionKit scanner, its camera grant and its imaging
└── Vehicle/     SCR-DI-004…004c, 006, 026/026a + their data layer and copy tables
```

- **The crop quadrilateral is VisionKit's, and that is what the wireframe asks for.**
  `driver_ios.html`'s own iOS clause on SCR-DI-005 is *"`VNDocumentCameraViewController` (native
  drag-corner crop); perspective transform applied on confirm"*, and D2' §SCR-DI-005's sequence —
  live camera → auto-proposed quad → drag four corners → Retake / Use photo → de-skewed image — is
  that controller's own. So `apps/driver-android`'s `CropQuad`, `DocumentEdgeDetector` and the warp
  in `DocumentImaging` (≈ 550 lines) have **no counterpart in this target**, and neither do the
  `capture_flash` / `capture_retake` / `capture_use_photo` strings: iOS draws those three buttons and
  localises them itself. `DocumentScannerScreen` is the wireframe's dark chrome around it, because
  the one thing VisionKit cannot say is **which** document it is scanning.
- **`CaptureSource.cameraDragCrop` is stamped in `DocumentScannerModel.onScanned` and nowhere
  else** (AL-43); `onPicked` stamps `.gallery`. A screen never chooses a provenance — it is what the
  Verification-Officer queue sorts on, and the only thing allowed to claim a scan happened is the
  code that performed one.
- **AL-30 lives in `ApiVehicleOnboardingRepository.resume()`, as a `ResumePoint`.** Re-opening the
  wizard opens the first non-verified step and never Step 1; when the current vehicle is approved the
  same call answers `.fresh` and the wizard starts a **new** vehicle. No screen decides this.
- **`POST /v1/vehicles` IS Step 1/4** (Δ C029). It carries the type and plate the `details` step
  stores, so a fresh vehicle comes back with one saved step and `nextStep = insurance`. Do not add a
  second call to save details on a vehicle that was just created.
- **`ActiveVehicleStore` is local, on purpose.** No operation on the platform sets a driver's active
  vehicle, and none is needed: the MQTT username **is** the vehicle id, so the broker learns the
  choice at CONNECT. C088 reads this store for the dashboard chip and the US-9.6 go-online gate;
  `VehiclesState.canGoOnline` and `VehicleSummary.canGoLive` are that rule written once.
- **`VehicleOnboardingSession` is to SCR-DI-006 what `DocumentCaptureCoordinator` is to
  SCR-DI-005** — the route carries no arguments, so the vehicle id goes through a process-wide
  holder instead.
- **`DriverNavigator.replaceTop(with:)` is `popUpTo(x) { inclusive = true }`.** Three moves use it —
  the wizard handing over to SCR-DI-006, and SCR-DI-006 handing on to My Vehicles or back into the
  wizard — and it is what stops a swipe back re-opening a step the driver has submitted.
- **SCR-DI-026 is a `List`, unlike every other screen in cluster 1 or 2.** Its cell's `Δ iOS` clause
  is *"`.swipeActions` deactivate"*, and that modifier exists only inside one. Android's `Remove` and
  `Documents` text buttons are the swipe here, and the wireframe's footnote announces it.
- **Swift has no key paths into tuples**, which is why `StepVerdictRow` is a named type where the
  Android twin uses a `Pair`: a `ForEach` needs a stable id, and indexing by position reorders a list
  the moment the data changes.
- Reusable UI added here: `StatusPill` + `StatusTone` (+ `.captured(_:)`), `ModeCBadge`,
  `StepProgress`, `VehicleTypeDot`, `MageRideScannerColor`, `MageRideControl.capturePanel` /
  `.statusAvatar` / `.statusDot` / `.illustrationIcon` / `.chipIcon` / `.shutter`, and `NoticeCard`'s
  `titleKey` went optional so an untitled `.card.fill` is the same control rather than a second one.

## Cluster 3 (C088) — the dashboard, the offer, the ride and the Menu tab

- **Home is ONE destination.** D2' merged SCR-DI-012 into SCR-DI-010 and makes SCR-DI-011 *the* home
  dashboard for a Mode A/B vehicle, so `HomeScreen` swaps only its sheet on
  `LiveVehicle.isScheduledMode`. The map, the header, the banners and the offer takeover are shared.
- **SCR-DI-014 is a `fullScreenCover` with `.interactiveDismissDisabled()`, not a route** — back is a
  swipe on this platform and fifteen seconds is not long enough to navigate. That is what `PushRouter`
  already meant by routing a `ride_offer` push to Home.
- **`OfferInbox` is the seam from APNs into `:shared`'s `OfferSession`**, and it is a process singleton
  because an offer arrives with no view anywhere. `DriverAppDelegate.deliver(_:)` hands **every** push
  to both it and `PushRouter`; either alone is a dashboard with nothing on it. The push carries
  `offerId`, `rideId`, `expiresAt` and a *rendered* fare and nothing else, so everything the badges
  need comes from one `GET /v1/rides/{rideId}` inside the window (`OfferModel.enrich`).
- **`OfferSlot` is why `OfferModel` is testable.** `OfferSession.state` is a `StateFlow` and
  `IosFlowWatcher`'s constructor is `internal`, so a test literally cannot build one — the protocol is
  the seam and `SharedOfferSlot` is the only thing that touches the watcher. `IosAppGraph.offerStates`
  is the watcher, added by C088 the way that type's own KDoc invites.
- **The countdown is local arithmetic, not `OfferSession.countdown()`** — a `Flow<Duration>` whose
  element is an inline value class. It is not a second rule: both derive what is left from `expiresAt`
  against the wall clock, and the *decision* rule (an offer past its deadline is never sent) stays in
  `OfferSession.accept()`.
- **`DriverLocationSource` is not `PositionService`.** The service owns the fixes that reach the broker
  and outlives every view; the source is a **screen's** `CLLocationManager` at ten-metre / nearest-ten-
  metres accuracy — the AL-31 own-vehicle marker, `GoOnlineRequest`'s position, SCR-DI-011's distance.
  It asks for no permission of its own. `PositionPublisher` is the matching seam that keeps the service
  out of a model.
- **Going online is two calls in one order**: `POST /v1/standby/online` then start publishing; going
  offline is stop publishing then `POST /v1/standby/offline`. A driver in the candidate pool who is not
  publishing is offered rides that cannot find them. `FakePositionPublisher` records the order.
- **A driver-QR ride is not over at `PaymentPending`.** AL-47's confirm settles the *payment*; the ride
  reaches `CashSettled` only once fare-svc's settlement travels the outbox. `ActiveRideState` carries
  `isQrAttested` and `isFinished` is sticky, because re-reading into that window would put the confirm
  sheet back up on a driver who had just answered it.
- **`ActiveRideState` holds `moved` beside `ride`, not folded into it.** `RideDetail` is a Kotlin data
  class and its `copy` reaches Swift as a twenty-two-argument `doCopy`; the state and the version are
  also the only two things that move between full reads.
- **`:shared` has `LiveHub`'s contract and no SignalR client**, so SCR-DI-015 polls
  `GET /v1/rides/{rideId}/state` — D3' §3.1's documented fallback. Delete that loop when a hub client
  lands.
- **CallKit is SCR-DI-031's (C093), and what is CallKit-aware *here* is the direct dial.** A `tel:` URL
  on iOS **places** the call, so dialling over one already up hangs it up — `SystemRideContact.dial`
  checks `CXCallObserver` and the refusal is copy. Android's `ACTION_DIAL` only opens the dialler, so
  it has nothing to check; this is a real Section C delta.
- **SCR-DI-036 is C088's** (`build/screen_coverage.md` is the authority; the C087 handoff says C093 and
  is wrong). It is a `List` where the Android twin is a `ModalDrawerSheet` — the cell's own `Δ iOS`
  clause — and its rows push onto the **Menu tab's** stack, which is what makes the system back button
  say `‹ Menu` on every screen that hangs off it.
- **`VehicleToken.wire` was camel case and is now `:shared`'s snake case** (the defect the C087 handoff
  asked C088 to fix before rendering a marker). Three of the ten types were drawn in the fallback grey.
- Reusable UI added here: `DashboardSheet`, `TopRoundedRectangle`, `OnlineToggle`, `DashboardBanner`,
  `CountdownRing`, `SolidBadge`, `VehicleChip`, `MetricCard`, `FlowRow` (a `Layout`, because SwiftUI
  has no wrapping stack), `MageRideOfferColor`, `MageRideCtaStyle.Emphasis.status(_:)` +
  `.mageCtaStatus(_:loading:)`, `UI/MoneyFormat.swift` (`Rs 1,240`, `1.2 km`, `01:12:40`, `1:42`, and
  `MageRideSymbols`), `UI/PackageLabels.swift`, and `MageRideControl.bigToggle` / `.countdownRing` /
  `.countdownStroke` / `.mapPreview` / `.avatarSmall` / `.rowIcon`.
- Four `:shared` `iosMain` helpers were added, each for the same reason `colomboBusinessDate` exists —
  a **defaulted Kotlin parameter does not survive the export**: `rideProjectionOf` /
  `rideProjectionCanSend`, `parseTimestampOrNull` / `timestampFromEpochMillis` / `timestampEpochMillis`,
  `colomboBusinessDateNow`, and `walletAlertFor`.

## Cluster 4 (C089) — the delivery

```
DriverApp/
└── Delivery/    SCR-DI-016a/b/c + the proof queue and ride-svc's package surface
```

- **A package ride is the SAME destination as a passenger ride.** R-01 keeps one aggregate and
  `PushRouter` resolves both `mageride://ride/{id}` and `mageride://package/{id}` to
  `DriverRoute.activeRide`, so the kind is not known until the ride has been read: `ActiveRideScreen`
  reads it and hands over to `DeliveryScreen` on `RideKind.package`. Both models are built by
  `HomeDestinationView` because a `@StateObject` cannot be introduced half way through a view's life;
  the delivery one costs nothing until its own screen calls `start()`. SCR-DI-015's poll and its GNSS
  subscription are **not** started for a package, so only one loop folds server states onto the ride.
- **Which of the three sheets is up is derived from the ride, never counted.** `package.picked_up` IS
  the `→ InProgress` move (D5' §11 skips `DriverArrived` for a parcel), so a driver whose app died
  between the two doors comes back to the right sheet from one read. Sheet 1's **Start delivery** is
  the one local step and sends nothing.
- **`PackageHandoff` (C015) is the five-attempt rule — do not count attempts in a screen.**
  `DeliveryModel` holds its `RideProjection` for the screen's whole life rather than re-seating it per
  read (which is what `ActiveRideModel` does), because re-seating throws the handoff away with it.
  `canSubmit` refuses a malformed code without spending an attempt; the **fifth** wrong code locks the
  gate, so there is no sixth request to make.
- **The four boxes are cleared on a TRANSITION, not on every fold** (Δ C089). The Android twin clears
  them in `DeliveryState.moved`, which its five-second poll also calls — so a courier typing the
  recipient's code there watches it vanish. `DeliveryState.advance(to:gates:)` compares the state
  first. Recorded as a defect found in C071.
- **The proof photograph completes the delivery** (Δ C037), so it is uploaded by the *"Delivery
  completed"* tap and not by the shutter. `ProofUploadQueue` is in memory and `mobile_db_schema.md`
  §3.6's durable table is deliberately unused here — read that class's doc before changing it.
- **`DocumentCaptureTarget.deliveryProof` is the first non-document use of SCR-DI-005.** A proof photo
  goes to `rides.proof_artifacts` and the contract declares no `…CapturedVia` part beside it, so
  AL-43's provenance stamp is dropped at the upload rather than filed against the
  Verification-Officer queue.
- **AL-33's fences:** *"Delivery completed"* replaces *"Cash received (COD)"* and nothing here calls
  `POST …/cod-collected`; both call buttons are a **direct PSTN dial** with no AL-48 chooser, and each
  names its own `CalleeRole` through `RideContact.startCall(rideId:calleeRole:type:)` — the kind-based
  overload cannot answer for a screen that can ring either end. The dial is still `CXCallObserver`-gated.
- **The three `Localizable.strings` files did not parse before this component, and now they do.**
  A `.strings` comment is a C comment and **does not nest**: `values*/strings.xml` inside one closes
  it on the `*/`, and everything after is garbage the parser refuses — so `NSDictionary(contentsOf:)`
  answered `nil` and every key in the app resolved to its own name. Seven occurrences, from C085
  onward. Write a path without the glob (`values…/strings.xml`). This is the mirror image of the trap
  `apps/driver-android/CLAUDE.md` records for KDoc, where block comments *do* nest.
- Reusable UI added here: none. Sheet 1's two distance tiles are C088's `MetricCard`, the party rows
  are C086's `GroupedList`, the boxes are C086's `OtpField`, and the sheets themselves are C088's
  `DashboardSheet` — which is the point of those five controls existing.

## Cluster 5 (C090) — the board, the level and the money

```
DriverApp/
├── Jobs/        SCR-DI-017, 018 + the dispatch reads and the Colombo clock
├── Level/       SCR-DI-019
└── Earnings/    SCR-DI-020 + its buckets and its Swift Charts trend
```

- **The Job Board is POST-INTENT ONLY, and dispatch-svc has no route that would let it be
  otherwise.** `GET /v1/rides/job-board` and `POST …/{id}/intent` are the whole surface; at T-30 min
  the booking becomes a ride and reaches the driver as an ordinary offer on SCR-DI-014. Anything on
  this screen that looked like an accept would be a second way to win a ride, racing the first.
- **T-30 is `jobBoardGoesLiveAtMillis(ride)` and both job screens read it.** `JobBoard.timeToGoLive`
  answers a `Duration` — an inline value class the export flattens to an opaque `Long` whose
  encoding is a packed nanos/millis pair with a tag bit, not a nanosecond count — so the *instant*
  crosses as epoch milliseconds and each screen does an ordinary comparison. SCR-DI-017's expiry and
  SCR-DI-018's *"reminder sent"* are the same moment (D5' §3.7 dispatches, §14.4 pushes), and
  `JobBoardModel.expiryFadeSeconds` is the only local number — it is the animation, not the rule.
- **`JobStanding.hasJobBoardAccess` is three-valued and the third value matters.** `true` opens the
  board, `false` is US-6A.8's gate, and **`nil` is "reputation did not answer"** — which must never
  render as the gate, because that tells a Level-3 driver they are Level 1. `JobBoardState` carries
  `isUnavailable` for exactly that.
- **`MageRideShared.DriverStanding` is NOT this app's `DriverStanding`.** C088 named the dashboard's
  whole status header `DriverStanding` (`Home/StandbyRepository.swift`); `:shared`'s is the level
  standing. Swift resolves an unqualified name to the local module and **nothing warns**, so every
  reference to the Kotlin type is spelled `MageRideShared.DriverStanding`.
- **Every clock on this cluster is Asia/Colombo, and Foundation's** — `ScheduleLabels.calendar` is a
  **Gregorian** `Calendar` (a non-Gregorian handset calendar answers a different day-of-month) on the
  zone `colomboZoneId()` supplies, the 24-hour clock is `en_US_POSIX` + a fixed `HH:mm` (a
  `.timeStyle = .short` follows the system's 12/24-hour switch, which is a setting), and only the
  `18 Jun` month name is locale data. Both suites are asserted at 19:00 UTC, already the next day in
  Colombo.
- **`now` and `positionWait` are injected separately on `JobBoardModel`, on purpose.** `now` is the
  T-30 rule and a test freezes it; `positionWait` is a GNSS timeout counted in **polls**. A budget
  measured off a frozen clock spins for the life of the process — a defect this component wrote and
  caught. Copy the split, not the first version.
- **Levels stop at 3.** The wireframe draws *"510 / 500 pts → Level 4"*; D5' §4.2 caps at
  `min(level + 1, 3)`. Layout is the wireframe's, the number is D5''s.
- **`DriverLevelState` carries no error field, and that is deliberate (Δ C090).** The Android twin's
  is unreachable — `JobsRepository.standing` swallows both failures by design, so nothing that calls
  it can throw. A failed read on SCR-DI-019 is an em-dash badge and *"Reading your level"*.
- **The earnings summary is query-svc's arithmetic and is printed as sent.** The per-trip rows feed
  the breakdown list and the chart and are never re-summed into a second total (R-05).
- Reusable UI added here: `MoneyFormat.radius(metres:)` (`30 km`, not `30.0 km` — a radius is a
  figure a spec fixed, not a measurement), `MageRideControl.levelBadge` / `.levelProgress` /
  `.earningsChart`, `LevelLabels`, and `Jobs/ScheduleLabels.swift` — **the Colombo clock and calendar
  every later screen that prints a time should use** rather than building a `DateFormatter` on the
  handset's zone.
- Three `:shared` `iosMain` helpers were added, each for the reason C088's four were — a defaulted
  Kotlin parameter does not survive the export: `jobBoardGoesLiveAtMillis` / `driverLevelRulesFor`,
  and `colomboZoneId` / `colomboStartOfDayMillis` / `colomboBusinessDateOf`.

**To add a screen (C091–C093):**

1. Its route is already in `Nav/DriverRoute.swift`. Use it; do not invent a case.
2. Replace its `placeholder(...)` line in `Nav/DriverDestinations.swift` with the real view. That
   file is the only `navigationDestination` in the app.
3. Put every user-facing string in **all three** `Resources/*.lproj/Localizable.strings` at once.
   `LocalizationTests` fails the build on a key that exists in one and not the others, on a
   translation left equal to its English, and on a format specifier dropped in translation.
4. Reach for `MageRideSpacing` / `MageRideRadius` / `MageRideColor` / `.mageFont(_:)` — never a raw
   number or hex. `ThemeTokenTests` is what keeps §0.2 true, and it reads the **compiled asset
   catalogue**, so a mistyped colour fails a build rather than a night shift.
5. The full-width orange bar in the wireframes is `.buttonStyle(.mageCta)`, not
   `.borderedProminent`.
6. **Re-run `python3 Tools/generate_xcodeproj.py`** and commit the `.pbxproj` with your files.

## The Xcode project is generated, and that is not the same as `build/`'s generated files

`.pbxproj` is the committed artefact — CI probes for it and `xcodebuild` reads it. It is also a file
where every source needs two 24-hex ids, a group membership and a build-phase entry, and the classic
failure is a file added to the group and forgotten in `Sources`: it compiles for whoever added it
and not in CI. So `Tools/generate_xcodeproj.py` derives it from the tree, with ids that are a hash
of the path, which makes the output identical on every machine and the diff show only real changes.

Hand-editing the `.pbxproj` is allowed (CLAUDE.md's "never hand-edit a generated file" rule is about
`build/prompts`, `build/progress.md` and `build/screen_coverage.md`). What you must not do is change
one and not the other.

## Where Kotlin ends and Swift begins

`:shared` is bigger on this platform than it is on Android, and the boundary is deliberate.

- **Swift cannot use Koin.** `Module.single`, `module { }` and `Koin.get` are all `inline` +
  `reified`, and an inline reified function is not exported to Objective-C at all. So the app passes
  **values** to `startIosGraph(config:)` and gets `IosAppGraph` back — typed properties over the same
  singletons `:shared` wires internally. `DriverGraph` holds it and constructs what is native.
- **Swift cannot collect a `Flow`.** `IosFlowWatcher<T>` is the adapter (a generic *class*, because a
  generic function's type parameter erases to `Any` in the export). Cancel every subscription.
- **The position pipeline is Kotlin** (`shared/kmp/src/iosMain/.../mqtt/IosPositionPipeline.kt`),
  where Android keeps it in the app module. Every collaborator — `AdaptiveRateEngine`,
  `PositionReplayQueue`, `GpsBuffer`, `PositionSample`, `Instant` — is on the Kotlin side of the
  bridge, and several cross it lossily (`Duration` is an inline value class the export flattens to
  an opaque `Long`; a nullable `Int` boxes to `KotlinInt?`; `copy` becomes a fifteen-argument
  `doCopy`). Swift owns the fix source and the socket; Kotlin owns the rules. A bonus that decided
  it: the file is type-checked by `:shared:compileKotlinIosArm64` **on the Linux host**.
- **`IosMqttPlan` computes the whole CONNECT** — client id, username, password, will, topics, QoS —
  so `CocoaMqttTransport` is a byte pipe that spells no topic of its own.
- **Nothing constructs `ApiConfig` / `AuthConfig` / `MqttConfig` in Swift.** They carry `Duration`
  fields and Kotlin default arguments do not survive the export, so a Swift call site would be
  passing unlabelled nanosecond counts for values the spec has already fixed. `IosAppConfig` takes
  primitives and builds them.

## Rules this target is built on

- **AL-31: no hamburger.** Navigation is the `TabView`'s **Menu** tab, and `Nav/DriverTab.swift` is
  the only place a top-level destination is declared. `NavigationShellTests` asserts there are
  exactly four and that Menu is one of them.
- **The route table is the Android one, path for path.** `NavigationShellTests` types out
  `DriverRoute.kt`'s paths and compares. A route added on Android goes here in the same commit.
- **No colour is a hex in Swift.** §0.2 says SwiftUI takes "the same hex, light/dark appearances" as
  a `Color` asset, so the palette is `Resources/Assets.xcassets` and the test reads it back.
- **Dynamic Type is not optional.** `.mageFont(_:)` is `@ScaledMetric` over the spec's point size,
  anchored to the text style §0.2 maps the role to. `.font(.title)` ignores the spec's sizes;
  `.font(.system(size:))` ignores the driver. Both are wrong.
- **Background GPS and MQTT stay native.** `PositionService` owns the authorisation, the fixes and
  the socket's lifetime; `:shared` supplies the cadence, the topics, the payload and the pacing.
- **`seq` must never rewind.** It comes from C018's `PersistentPositionSequencer` through
  `GpsBuffer`; a counter that restarted at zero makes `position-processor-svc` discard everything
  the app publishes while the app believes it is publishing (R-17/T-05).
- **The MQTT username is the vehicle id**, and the credential is the MQTT **session** JWT — never
  the API access token. EMQX validates it at CONNECT only, so a rotation means a reconnect.
- **A deep link is resolved, not trusted.** `PushRouter` maps a `mageride://…` URI onto a known
  `DriverRoute`; an unrecognised one opens nothing. Universal Links go through the same table.

## Section C deltas that are real, and are not bugs

Every one of these is D2' §C or a platform constraint, and each is called out at its call site.

| Concern | Android | iOS, here |
|---|---|---|
| Cadence | the service re-registers FusedLocation at the interval | `CLLocationManager` has no interval; the pipeline rejects an early fix |
| "Publishing" indicator | ongoing foreground-service notification | the system's blue location pill |
| Notification mute | per-channel, user-controllable | per-app only; the two categories buy interruption level, not a user control |
| Captive portal | `NET_CAPABILITY_VALIDATED` reads it as offline | `NWPath` has no equivalent; it reads as online |
| Update gate | `AlertDialog` swallowing `onDismissRequest` | an `.alert` with no cancel button — there is nothing to swallow |
| Elevation | M3's six tinted levels | one shadow, `radius 8 / y 2 / 0.12` (§0.2's own iOS row) |
| Satellite count | `Location` carries one | no public API; the field is absent, not zero |
| Offer takeover | a window-sized `Dialog` | `fullScreenCover` + `.interactiveDismissDisabled()` |
| Offer tone | `RingtoneManager`'s default notification tone | no equivalent API — the sound is the APNs payload's, allowed through in the foreground; the app adds the haptic and `kSystemSoundID_Vibrate` |
| Direct dial | `ACTION_DIAL` opens the dialler | `tel:` **places** the call, so `CXCallObserver` gates it |
| Menu (SCR-DI-036) | a `ModalDrawerSheet` behind a scrim | a `List` on the Menu tab's own stack |
| Delivery call button (SCR-DI-016a/c) | a 46×38 outlined `📞` icon button | the wireframe's own `textlink` — a green `📞 Call` in a grouped-list row |
| Earnings trend (SCR-DI-020) | bars hand-drawn from Compose primitives | **Swift Charts** — the cell's own `Δ iOS` clause; same spec, first-party axis and VoiceOver rotor |
| Earnings periods (SCR-DI-020) | a `TabRow` | a segmented `Picker` — `.tabbar2` is `UISegmentedControl` in the wireframe's own CSS |

## Things that will bite

- **The XCFramework must be assembled before `xcodebuild`.** See Verify.
- **`SWIFT_STRICT_CONCURRENCY` is `minimal`**, deliberately: this code was authored on a host that
  cannot compile it, and raising it before the first green build would bury C086 in diagnostics.
  Turning it up is C103's, and the actor annotations are already written.
- **`FirebaseApp.configure()` is guarded on `GoogleService-Info.plist` being present**, and it is
  not in this repository (C124 owns the Firebase project). Calling it without one raises an
  Objective-C exception, which Swift cannot catch — the app would terminate, not degrade. Push
  registration therefore answers `nil` on every build produced today, exactly as it does on Android.
- **App Attest does not exist on the simulator.** `DCAppAttestService.isSupported` is `false` there,
  so the DoD's `X-Attestation` line cannot be proven by a simulator run — and there is no
  registration endpoint yet either (the C014 gap). Both need a device and one contract addition.
- **No `H3Grid` is bound**, and for this app that is correct: AL-31's driver home map joins no
  geocell group, so nothing resolves one. **C094 must bind one** — `geoRealtimeModule` throws on
  resolution when the platform has none.
- **A localised `.strings` file is a variant group**, not three files. The generator builds them;
  adding a fourth language means a new `.lproj` directory and a re-run, nothing else.
- **`Bundle.main` is the TEST HOST when a test runs.** Every resource lookup goes through
  `Bundle(for: MageRideBundleToken.self)`; `Bundle.main` finds nothing and the failure looks like a
  missing asset rather than a missing bundle.
- **The tests run hosted** (`TEST_HOST` is the app), because they read the app's asset catalogue and
  its three `.lproj`. A standalone test bundle would find neither.
