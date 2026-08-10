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
│   ├── Wallet/                  C091 · SCR-DI-021…025 + the top-up rails and the ledger
│   ├── Tracker/                 C092 · SCR-DI-027 + the device-QR scanner and the publisher gate
│   ├── Sharing/                 C092 · SCR-DI-028 — Mode B grants, per vehicle
│   ├── Profile/                 C092 · SCR-DI-029 + its three editors and the contact picker
│   ├── History/                 C092 · SCR-DI-030 + the rate-passenger sheet
│   ├── Comms/                   C093 · SCR-DI-031 + the WebRTC seam and the CallKit provider
│   ├── Safety/                  C093 · SCR-DI-032 — the driver SOS
│   ├── Support/                 C093 · SCR-DI-033 / 033a + the FAQ and the ticket sheets
│   ├── Notifications/           C093 · SCR-DI-034 — the local push inbox
│   ├── Menu/                    C088 · SCR-DI-036 — AL-31's drawer, as a tab
│   ├── Onboarding/              C086 · SCR-DI-001, 002, 003, 003a, 007 + their data layer
│   ├── UI/                      the wireframe's shapes as views — fields, tiles, cards, pills
│   ├── DriverApp.swift          @main. One App, one shell.
│   ├── DriverAppDelegate.swift  the four callbacks SwiftUI has no equivalent for
│   ├── Info.plist               background modes, purpose strings, the MageRide config dict
│   ├── DriverApp.entitlements   App Attest, APNs, the payment Universal Link
│   ├── DI/                      DriverEnvironment (the only Info.plist reader) + DriverGraph
│   ├── Nav/                     DriverRoute (every destination), DriverTab, the navigator,
│   │                            DriverDestinations (the ONE route→view switch)
│   ├── Shell/                   DriverShell (TabView host), offline banner, update gate,
│   │                            connectivity, the three cross-cutting subscriptions,
│   │                            C093's buffered-samples card
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

## Cluster 6 (C091) — the wallet, the top-up and the credit transfer

```
DriverApp/
└── Wallet/     SCR-DI-021…025 + the two top-up rails, the ledger and the driver-to-driver surface
```

- **A Kotlin `require` is a caught exception on Android and a TERMINATED PROCESS here**, and this
  cluster is the first to be exposed to one. An exception thrown out of a non-suspend, non-`@Throws`
  Kotlin function crosses as an uncaught Objective-C exception, which Swift cannot catch. Three
  `:shared` value types this cluster builds from **server data** carry `require`s in their
  constructors — `DailyFeeSchedule` (one rate per vehicle type), `VoucherCatalogue` (four rules on the
  tier table) and `CreditTransferIntent` (no self-transfer, positive amount) — so each is validated in
  Swift *first*: `ApiWalletRepository.readSchedule`, `ApiTopUpRepository.isWellFormed` and
  `CreditTransferModel.rejectionForSend`'s two guards. The Android twin's `launchGuarded` catches the
  identical failure and shows copy; here the same outcome has to be arranged. **Check any `:shared`
  constructor you call from Swift for a `require` before calling it with a server value.**
- **A voucher is a purchase, not a discounted top-up, and getting it wrong pays twice.** A tile goes to
  subscription-svc's `POST /v1/vouchers/purchase`; a `topUp(90000)` followed by a purchase credits
  Rs 900 on the webhook **and** Rs 1,000 on the purchase. wallet-svc's own `purchaseVoucherFromWallet`
  takes an already-settled `gatewayRef` and is the reconciliation entry point, not the buy button.
- **The balance is read and never computed** (D-09), and every *decision* asks `available` (net of
  D-05 debt) while the headline prints `balance` (US-9.7 calls it that). Two different questions.
- **D2' and D5' draw "Top Up Required" at two different lines and both are right** — §9.4 at a
  negative balance, §SCR-DI-021 below one day's fee. `WalletState` carries both and the screen ranks
  them. Read `WalletState.isBelowDayFee` before merging them.
- **The two rails leave the app by different doors, and only one of them can fail.** OnePay is an
  `SFSafariViewController` the *screen* presents — the cell's own `Δ iOS` clause — so `PaymentHandoff`
  covers the LankaQR bank-app link alone and the Android twin's *"no app could open the payment page"*
  does not exist here. The return leg is `PaymentReturn`: OnePay is sent `pay.mageride.lk` as its
  `returnUrl` and `SafariView` dismisses on a redirect onto that **host**, which is the `applinks:`
  domain the entitlements file already declares. The redirect is a shortcut; the driver's **Done** and
  a swipe both resolve the same session through the same poll.
- **SCR-DI-022 has ONE `.sheet` and three things to put in it.** A voucher purchase sets the checkout
  *and* the receipt in one breath (LankaQR sets the code *and* the receipt); on Android those stack as
  two dialogs, and SwiftUI presents one sheet per context and silently drops the rest. `TopUpSheet`
  ranks them — checkout, then code, then receipt.
- **`TopUpState` is this app's and `TopupState` is `:shared`'s**, one letter apart, and both are in
  scope in `TopUpModel`. The same pair exists on Android. Nothing warns.
- **The AL-34 fence is stronger here than on Android and the reason is the encoder.** AL-15's fallback
  is `CIQRCodeGenerator`, a first-party **writer**, where the Android twin added `com.google.zxing:core`
  — whose reader half C074 then used for SCR-DA-027. No decoder is linked into this target at all, so
  *"nothing in the wallet scans a code"* is a fact about the binary and not only about the imports.
  `WalletFenceTests` still asserts it, along with AL-05 in code **and in all three languages**.
- **`WalletFenceTests` reads the cluster's own source off disk through `#filePath`**, which works
  because a simulator shares the host's filesystem. It is the first test in this target to do that;
  everything else reads the built bundle.
- **US-9A.19's statement is written and shared in two steps here, where Android's exporter is one.**
  A chooser is an `Intent` an application context launches there; a share sheet is a
  `UIActivityViewController` a *view* presents. `StatementExporter` ends at a `URL` and `ActivityView`
  presents it — which also removes the "nothing can receive it" failure the Android seam reports.
- **SCR-DI-025's date range is two `DatePicker`s in the Colombo calendar**, which removes a trap: M3's
  range picker answers **UTC midnight** and `WalletHistoryScreen.kt` has to convert; putting
  `ScheduleLabels.calendar` and `.zone` in the environment makes the tapped day a Colombo day.
- Reusable UI added here: `UI/PlatformId` (the `Ulid` pattern, which SCR-DI-028/029 will want),
  `LabelledTextField`'s `prefix` / `keyboardType` / `autocapitalisation` plus the two combinations this
  app uses (`RupeeField`, `DriverIdField`), `OutlinedAction`, `MoneyFormat.percentOfBps`,
  `ActivityView` + `StatementFile`, and `SafariView` — the last two are the platform's two "hand this
  to something else" surfaces and C092/C093 should take them from here.
- One `:shared` `iosMain` helper was added, for the reason `IosCapturedDocument` exists rather than the
  defaulted-parameter reason the other seven do: `nsDataOf` (`IosBytes.kt`) copies a `ByteArray` with
  one `memcpy`, where `KotlinByteArray.get(index:)` is one Objective-C message per byte and a PDF
  statement is hundreds of kilobytes.

## Cluster 7 (C092) — the tracker, the sharing, the profile and the history

```
DriverApp/
├── Tracker/     SCR-DI-027 + the device-QR scanner and the publisher gate
├── Sharing/     SCR-DI-028 — Mode B grants and the request queue, per vehicle
├── Profile/     SCR-DI-029 + its three editors, the contact picker and the four-route destination view
└── History/     SCR-DI-030 + the rate-passenger sheet
```

- **Pairing a tracker stops this phone publishing for that vehicle, and the rule lives at the
  publisher seam.** `TrackerPositionPublisher` decorates `ServicePositionPublisher` and refuses
  `start(vehicleId:…)` for a vehicle in `TrackerBindingStore` (US-3.6, *"exactly one publisher at a
  time"*). A decorator rather than a check in a screen because **three** doors reach the position
  service — SCR-DI-010's go-online toggle, SCR-DI-011's Start Journey and US-5.10's Restart — and a
  rule written at one would be missing from the other two. `stop()` is **never** gated. `DriverGraph`
  has exactly one `PositionPublisher` binding: the decorator replaced the bare service publisher
  rather than being added beside it.
- **`TrackerBindingStore` is local because nothing on the app-facing surface answers the question.**
  Not a preference the way `ActiveVehicleStore` is: `POST /v1/vehicles/{id}/device` returns a
  `bindingId` and **nothing reads one back** — no registry read carries a device, and
  `GET /v1/trackers/{imei}` is provisioning-svc's, which `:shared` has no client for. Pair on one
  handset and the other still publishes; that is the honest limit of a device-local answer.
- **Δ iOS — the device QR is `DataScannerViewController`, and that is D2' §SCR-DI-027's own SwiftUI
  column.** Android put ZXing's reader half behind a CameraX `ImageAnalysis`; here VisionKit does
  barcodes, the highlight and the reticle with no dependency at all. **This is the first decoder
  linked into this target** — C091's *"no decoder is linked at all"* was true of the binary at that
  point, and `WalletFenceTests` still pins the half that matters (nothing in the **wallet** scans,
  AL-34). The grant is asked for **before** the sheet: `DataScannerViewController.isAvailable` is
  `false` without it, so presenting first would show a scanner that could not scan.
  `CameraAuthoriser.isCodeScannerSupported` is the seam; `TrackerImei.imeiIn` decides which fifteen
  digits in a payload are an IMEI and refuses a payload with two candidates.
- **SCR-DI-028's selector is a scope and re-reads on every change.** Both list endpoints take the
  vehicle in the path, so `SharingModel.select(vehicleId:)` **empties** the queue and the roster
  before fetching that vehicle's own — AL-35's *"never mixed across vehicles"* is a re-read, not a
  filter. Only Mode A/B vehicles are offered; a Mode C tuk has no subscribers. **Δ iOS:** the chip row
  is a segmented `Picker` (the cell's own clause), and because a segment holds text alone the type dot
  and the `FLEET` badge are drawn on the selected vehicle's identity row underneath — that row is not
  AL-35's removed caption box, and `TrackerFenceTests` reads all three languages to keep it that way.
- **Accept and reject are `.swipeActions`** — the cell's clause again — with `allowsFullSwipe: false`
  on the **admitting** edge: one gesture with no confirmation starts a subscription on somebody else's
  account, and a rejection is the recoverable one.
- **`ShareExpiry` has one time-zone hop where the Android twin has two.** M3's date picker answers
  **UTC midnight** and `ShareExpiry.kt` is mostly a warning about it; a SwiftUI `DatePicker` handed
  `ScheduleLabels.calendar` and `.zone` answers a Colombo day directly. What is left is the rule that
  matters: a grant lapses at the **end** of the chosen day, not its start (US-4.8).
- **SCR-DI-029 is where a driver reads their own platform id, and it is copyable.** C091's handoff
  asked for exactly that — *"if that screen does not print it verbatim and copyably, credit transfer
  has no way to be used"* — so the row carries a copy button and `.textSelection(.enabled)`. There is
  no `DRV-22011` (`UI/PlatformId`) and **no star average anywhere on the app-facing surface**, so the
  card prints the level and an em dash.
- **The emergency contact is replaced, never accumulated** (`EmergencyContact.isPrimary` is *"exactly
  one per account that has any"*, because D-33's SOS is p99 ≤ 5 s off a denormalised column).
  `ContactPickerView` is `CNContactPickerViewController`, which runs **out of process** and therefore
  needs **no `NSContactsUsageDescription` and no authorisation** — the mirror of the Android twin's
  `ACTION_PICK` avoiding `READ_CONTACTS`. Set `predicateForSelectionOfProperty`, or tapping a number
  inside a multi-number contact **places a call** instead of selecting it.
- **Log out navigates nothing.** `AuthSessionManager.logout()` clears the local session whatever the
  gateway answered and raises `RouteToLogin`, and `DriverShellModel` is the single subscriber; a
  second handler would reset the stacks twice. `DriverSessions.logOut()` is the seam C092 added.
- **Notification switches are grouped five ways and nothing safety-critical is offered.**
  `SOS_TRIGGERED`, `SOS_RESOLVED`, `RIDE_CANCELLED` and `SCHEDULE_NOT_STARTED` are absent because
  iam-svc drops a mute for one on the way in. An **absent** key reads as **on** (US-10.7 is opt-out),
  and the whole map is written back so a key this build has never heard of survives.
- **SCR-DI-030's list is query-svc's `GET /v1/trips/{driverId}`, not `GET /v1/rides/history`.**
  `TripSummary` has neither distance nor rating, so the screen reads one **detail per row** through a
  `TaskGroup` — and `TripDetail.rating` is joined on `rater_id = @UserId`, which makes it *"the stars
  I already left"* and is what stops a re-opened screen offering to rate a trip twice. A failed detail
  is dropped, not fatal.
- Reusable UI added here: `MageRideSymbols.starFilled` / `.starEmpty`, `RatingStars.text(_:)`, and
  `OutlinedAction` gained `symbolName` / `isEnabled` (SCR-DI-027 draws a glyph on Scan and draws
  **Bind code** disabled). `CameraAuthoriser` gained `isCodeScannerSupported`. No `:shared` helper was
  needed — this cluster crosses the bridge only for DTOs and two `IosInstantKt` conversions that
  already existed.

## Cluster 8 (C093) — the call, the alarm, support and the system states

```
DriverApp/
├── Comms/          SCR-DI-031 + the WebRTC seam and the CallKit provider
├── Safety/         SCR-DI-032 — the driver SOS
├── Support/        SCR-DI-033 / 033a + the FAQ, the ticket thread and the raise-ticket sheet
├── Notifications/  SCR-DI-034 — the local push inbox
└── Shell/BufferedSamplesCard.swift   SCR-DI-035's other half
```

- **The VoIP media client is NOT here, and it is a dependency wall rather than a decision** — the
  same wall C075 hit from the other side, reached differently. `livekit/client-sdk-swift` is a
  **remote** Swift package and this project's only package today is `shared/swiftpm/MageRideShared`,
  resolved by path; it also needs `NSMicrophoneUsageDescription`, which `Info.plist` keeps out until
  there is code behind it (the mirror of the Android manifest's missing `RECORD_AUDIO`); and neither
  can be verified on a host that cannot compile iOS. So `VoipEngine` is the seam, `AbsentVoipEngine`
  is what `DriverGraph` binds, and SCR-DI-031's **signalling half is real** while the media half
  reports `noMediaClient` — which is exactly the condition AL-48 legislates for. `CommsFenceTests`
  pins the absent purpose string, so the day the engine lands that assertion fails and asks for it.
- **CallKit is driven by the LINK, never by the tap** (Δ C093, and the whole reason the ordering is
  written down). `CallKitSession.startedConnecting` fires on `CallLink.connecting` and `connected()`
  on `.connected`; a failure calls `end(reason: .failed)` **before** *"Call normally instead?"* is
  offered. That last ordering is load-bearing: `SystemRideContact.dial` refuses while
  `CXCallObserver` sees a call (C088's guard, which was waiting for this class), so a reported call
  left up makes AL-48's fallback a button that silently does nothing. It also means a build with no
  media client reports **no call at all** rather than flashing one into the status bar and out again.
- **The reported handle is `.generic`, never `.phoneNumber`.** P-05 keeps the rider's number hidden
  on a free call, and a `.phoneNumber` handle is rendered on the lock screen *and written into the
  handset's own call history*.
- **`POST /v1/sos` has no positionless form**, so SCR-DI-032 waits for a fix before it arms and the
  disc reads `SOS` rather than a countdown until one arrives. BR-29.4 contemplates a positionless SOS
  for the *web* surface and the app-facing contract carries no equivalent — the C075 gap, carried
  forward. In practice it is milliseconds: `DriverLocationSource` emits the **last known** fix first,
  and SCR-DI-015 already disables its SOS button without one.
- **The three-second cancel window is not a spec number**, and it spends the D-33 budget. §14.3 fixes
  p99 ≤ 5 s for the *dispatch* and says nothing about a confirmation. `SosSmsStatus.failed` is **not**
  an error state: the alert is recorded and is on the admin live feed either way, so the screen stays
  dispatched and only the pill says which leg failed.
- **`RideContact` grew safety-svc rather than SCR-DI-032 growing a repository** — the alarm is raised
  from the same sheet as the call button, it is about the same ride, and `POST /v1/sos` is the only
  safety operation this app reaches. `triggerSos` is also the one method on that protocol that
  **throws**; every other member is best-effort.
- **The daily-fee refund is a `category`, not an endpoint** (US-9.23). It and *"Raise a ticket"* are
  one flow — SCR-DI-033a — posting the same `POST /v1/support/tickets`; `daily_fee_refund` is what
  derives `TicketQueue.finance`. The screenshot is a **separate upload** whose id the ticket links,
  and a failed upload never costs the driver their ticket.
- **`TicketDetail.description` collides with `NSObject.description` on the bridge**, so it is read
  through `IosTicketKt.ticketDescription`. That is the first *name*-collision helper in this target —
  the other eight `iosMain` helpers exist for defaulted parameters or for `memcpy` — and it is worth
  checking any `:shared` property called `description`, `hash`, `debugDescription` or `class` before
  reaching for it from Swift.
- **SCR-DI-034 is read from the device, not from the platform.** There is no *"list my
  notifications"* operation anywhere on the app-facing surface, so the list is `mobile_db_schema.md`
  §1.6 — which is also why it works with no connection. **Δ iOS:** `onMessageReceived` fires for every
  data message on Android; iOS hands a push to the app in three cases only — presented in the
  foreground, tapped, or `content-available` and the system chose to wake us. All three now reach
  `DriverAppDelegate.deliver(_:title:body:)`; a silent push the system declines to deliver is never
  seen, and no local inbox on this platform can do better.
- **`DriverDatabase` is the app's deferred answer to C018's un-bound database**, and it is an
  `actor`: opening is `suspend`, the `await` inside `get()` is a suspension point two callers could
  race, and every call on the handle is blocking. `PositionService` was moved onto it in the same
  change, so the position buffer, the alert inbox and SCR-DI-035's backlog count are three callers of
  **one** connection to one protected file.
- **`BufferedSampleCounter` reads the table, not `PositionService.bufferedCount`**, and the
  difference is a restart: that property is the live pipeline's and is zero when the service is not
  running — which is exactly the case the card exists for.
- **Swift has no key paths into tuples** (the C087 finding, and this cluster is where it bites four
  times). `ForEach(Array(x.enumerated()), id: \.element.id)` does not compile; key the collection
  itself and ask it whether a row is the last one.
- Reusable UI added here: `SearchField` (the wireframe's `.searchbar`, drawn rather than
  `.searchable` — that modifier belongs to a `List` and lives in the navigation bar, and this one is
  in the body), `MultilineTextField` (a `TextEditor` with a drawn placeholder, because SCR-DI-033a
  reserves three lines and `TextField(axis:)` cannot), `MoneyFormat.timer` (`00:42` — minutes and
  seconds, distinct from `clock`'s `01:12:40` and `countdown`'s `1:42`), `MageRideCallColor` +
  `MageRideSosColor` (the fourth and fifth palettes off §0.2's scheme, after the scanner and the
  offer takeover), and `MageRideControl.callAction` / `.callEnd` / `.avatarLarge` / `.sosButton` /
  `.sosHalo` / `.searchBar`. `avatarLarge` also replaced SCR-DI-003a's private `84`.
- Three `:shared` `iosMain` helpers were added: `fileUploadOf` (the `memcpy` reason
  `IosCapturedDocument` gives, for the two multipart parts that carry **no** AL-43 provenance),
  `IosNotificationInbox.kt`'s four §1.6 functions (the SQLDelight `Query<T>` types are from a
  dependency the framework does not `export`, and a view must not decode `data_json` per redraw), and
  `ticketDescription` (the `NSObject` name collision above).

**To add a screen:**

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
| Top-up method (SCR-DI-022) | three `FilterChip`s | a segmented `Picker` — `.seg` is `UISegmentedControl` in the same CSS |
| OnePay's hosted page | `ACTION_VIEW`, which can find no browser | `SFSafariViewController` in the app; the failure does not exist |
| LankaQR bank app | `ACTION_VIEW`; `canOpenURL`'s answer is hidden by package visibility | `open(…, universalLinksOnly:)`; `canOpenURL` is hidden by `LSApplicationQueriesSchemes` |
| AL-15's QR fallback | `com.google.zxing:core`, a third-party encoder | `CIQRCodeGenerator`, first-party — and no decoder is linked at all |
| Statement download (SCR-DI-025) | write + `ACTION_SEND` chooser, from a context | write, then a `UIActivityViewController` a **view** presents |
| History search (SCR-DI-025) | none — the Android screen has no search | `.searchable`, the cell's own `Δ iOS` clause |
| History date range | M3's range picker, which answers **UTC** midnight | two `DatePicker`s in the Colombo calendar |
| Device QR (SCR-DI-027) | CameraX + ZXing's reader half | **`DataScannerViewController`** — D2' §SCR-DI-027's own SwiftUI column; first-party, no dependency |
| Sharing selector (SCR-DI-028) | a row of full-width `VehicleChip`s | a segmented `Picker` + an identity row for what a segment cannot hold |
| Accept / reject (SCR-DI-028) | two text buttons in the row | `.swipeActions`, with no full swipe on the admitting edge |
| Share expiry (SCR-DI-028) | M3's picker answers **UTC** midnight, so two hops | one hop — the `DatePicker` is given the Colombo calendar |
| Contact picker (SCR-DI-029) | `ACTION_PICK` over `Phone.CONTENT_URI`, no `READ_CONTACTS` | `CNContactPickerViewController`, out of process, no usage-description key |
| Language change (SCR-DI-029) | `Activity.recreate()` re-inflates every resource | no `recreate()`; `DriverLocale` redirects the bundle and views rebuild |
| Profile editors (SCR-DI-029) | three `ModalBottomSheet`s | three `.sheet`s at `.medium` |
| Rate passenger (SCR-DI-030) | `ModalBottomSheet` | `.sheet` with detent `.medium` — the cell's own clause |
| Call UI (SCR-DI-031) | `ConnectionService`, unimplemented with the engine | **CallKit** — `CXProvider`, reported from the link; the audio session, the lock screen and `CXCallObserver` |
| VoIP fallback dial (SCR-DI-031) | a number handed back for a `LaunchedEffect` to `ACTION_DIAL` | placed here, through `RideContact.dial`'s `CXCallObserver` guard |
| Back on a takeover (SCR-DI-031/032) | a `BackHandler` that has to be disabled mid-request | nothing to disable — a `fullScreenCover` has no interactive dismissal |
| Support search (SCR-DI-033) | an `OutlinedTextField` | a drawn `SearchField` — the wireframe puts `.searchbar` in the **body**, not the navigation bar |
| Related trip (SCR-DI-033a) | `ExposedDropdownMenuBox` | a `.menu` `Picker` — D2' §SCR-DI-033a's own *"`Picker`"* |
| Ticket sheets (SCR-DI-033/033a) | three `ModalBottomSheet`s | three `.sheet`s at `.medium`, ranked through **one** `item:` binding |
| Screenshot picker (SCR-DI-033a) | `PickVisualMedia`, no `READ_MEDIA_IMAGES` | `PhotosPicker`, no `NSPhotoLibraryUsageDescription` |
| Alert shimmer (SCR-DI-034) | three hand-built placeholder rows | `.redacted(reason: .placeholder)` over four real rows — respects Reduce Motion for free |
| Filing a push (SCR-DI-034) | `onMessageReceived` fires for every data message | three doors only: foreground, tapped, or `content-available` and the system woke us |
| Buffered count (SCR-DI-035) | a counter over `gps_buffer` for the active vehicle | the same, on an `actor` — the Native SQLDelight driver is blocking |

## Things that will bite

- **The XCFramework must be assembled before `xcodebuild`.** See Verify.
- **`SWIFT_STRICT_CONCURRENCY` is `minimal`**, deliberately: this code was authored on a host that
  cannot compile it, and raising it before the first green build would bury C086 in diagnostics.
  Turning it up is C103's, and the actor annotations are already written.
- **`firebase-ios-sdk` is pinned to `upToNextMinorVersion` from `11.13.0`, and the bound matters.**
  It was `upToNextMajorVersion` from `11.0.0` with no committed `Package.resolved`, so every CI run
  resolved fresh — and the SDK adopted Swift 6 syntax mid-11.x, which `macos-14`'s Xcode 15.4 /
  Swift 5.10 cannot parse. The leg failed in `** ARCHIVE FAILED **` inside the SDK's own sources,
  with nothing in this repository at fault. **Two independent markers, and they arrived in different
  releases — checking only one is how a first attempt at this pin still failed:**
  `HeartbeatsPayload` takes access-level imports (`public import`) in **11.12.0**, and
  `FIRAllocatedUnfairLock` takes the `sending` parameter modifier in **11.14.0**. So **11.11.0 is the
  last release that compiles under Swift 5.10**, and that is the pin. Both boundaries were
  established by reading those two files at each tag (C124's CI repair), not guessed.
  **Two follow-ups, both for whoever has the Mac:** commit a `Package.resolved` so resolution stops
  being a moving target at all, and decide whether to move the leg to a runner with Xcode 16 — which
  is the only way past 11.13.x, and which will also raise every Swift 6 concurrency diagnostic that
  `SWIFT_STRICT_CONCURRENCY = minimal` is currently holding back. **Neither the pin nor the archive
  has been compiled: this host has no Xcode** (root `CLAUDE.md`).
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
