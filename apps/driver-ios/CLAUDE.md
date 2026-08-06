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

**To add a screen (C088–C093):**

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
