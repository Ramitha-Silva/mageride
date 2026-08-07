# Passenger iOS Conventions

- Swift + SwiftUI; consumes the KMP shared module as an XCFramework via SPM
- Parity-fenced to `apps/passenger-android` — any behaviour difference beyond a D2' Section C
  platform delta needs a micro-change-set
- Screens map to D2' §A + the `specs/wireframes/passenger_ios.html` wireframe (41 SCR-PI ids)
- Stays native (ADD §18.2): SignalR Swift client, MapLibre GL Native iOS, Core Location,
  Keychain + Secure Enclave, App Attest, APNs via FCM
- Map subscribes by geocell — H3 res-7 + `ring(2)` = 19 cells with 30 s hysteresis, never
  per-vehicle (R-06)
- Trilingual: `Localizable.strings` for si / ta / en, Dynamic Type respected
- Not a Gradle project — this is an Xcode project owned by C094; it is deliberately absent from
  `settings.gradle.kts`
- **iOS deployment target is 16.0**, the same floor `apps/driver-ios` sets (C085's decision; no spec
  states one, which C000 recorded as a gap and C094 restated)
- **This Linux build host cannot compile iOS.** Generate code here; build and verify on macOS.

## Verify (macOS only)

```
./gradlew :shared:assembleXCFramework        # MUST run first — SPM resolves the artefact by path
xcodebuild -project apps/passenger-ios/PassengerApp.xcodeproj -scheme PassengerApp \
  -destination 'platform=iOS Simulator,name=iPhone 15' test
```

The XCFramework is a **build output** consumed by a local Swift package
(`shared/swiftpm/MageRideShared`), so a tree that has never assembled it fails at package resolution
with "artifact not found" rather than at link time. `.github/workflows/ci.yml`'s iOS leg already uses
that order and already probes for this project's `.pbxproj`, so no CI change was needed.

## The shell (C094) — what exists, and where a screen plugs in

```
apps/passenger-ios/
├── Config/                      Shared/Debug/Release xcconfig — the gateway and PMTiles
├── Tools/generate_xcodeproj.py  a shim over shared/tools/generate_xcodeproj.py (see below)
├── PassengerApp.xcodeproj/      the committed project — CI runs xcodebuild against it
├── PassengerApp/
│   ├── PassengerApp.swift          @main. One App, one shell.
│   ├── PassengerAppDelegate.swift  the four callbacks SwiftUI has no equivalent for
│   ├── Info.plist                  one background mode, one purpose string, the MageRide dict
│   ├── PassengerApp.entitlements   App Attest + APNs. No associated domains — see the file.
│   ├── DI/          PassengerEnvironment (the only Info.plist reader), PassengerGraph,
│   │                PassengerDatabase (C018's deferred open)
│   ├── Geo/         SharedH3Grid — R-06's engine, the binding C017 and C085 left to C094
│   ├── Onboarding/  C095 · SCR-PI-001…005 + the router, the OTP rules and the error table
│   ├── Nav/         PassengerRoute (32 destinations), PassengerTab, the navigator,
│   │                PassengerMenuDestination (SCR-PI-033's rows),
│   │                PassengerDestinations (the ONE route→view switch)
│   ├── Shell/       PassengerShell (TabView host), offline banner, update gate, connectivity,
│   │                AppPreferences, PassengerLocale
│   ├── Live/        the SignalR plane — transport, AnyJSON, subscriptions, inbox, vehicle store
│   ├── Map/         MageRideMap (the whole §0.3 layer stack), MapStyles, VehicleLayers,
│   │                MapPalette, MarkerInterpolator (MAP-04), EncodedPolyline
│   ├── Location/    PassengerLocationSource — the fix the 19 cells are computed from
│   ├── Push/        PushRouter (deep links), PushTokenProvider (APNs via FCM)
│   ├── Security/    DeviceBinding — App Attest and the Keychain, documented
│   ├── Theme/       D2' §0.2 — colour, type, spacing/radius/elevation, the CTA, the legend
│   ├── UI/          Localisation (the one door a string is resolved through) + the wireframe's
│   │                shapes as views — fields, rows, the carousel's panel and dots
│   └── Resources/   Assets.xcassets, en/si/ta .lproj, the two map styles
└── PassengerAppTests/           theme, localisation, navigation, push, environment, H3, live, map
```

**To add a screen:**

1. Its route is already in `Nav/PassengerRoute.swift`. Use it; do not invent a case.
2. Replace its `placeholder(...)` line in `Nav/PassengerDestinations.swift` with the real view. That
   file is the only `navigationDestination` in the app, and a second one would fork the back stack
   the way a second `NavHost` does. Give a cluster **one arm and one `…DestinationView`** — `body` is
   an implicit `@ViewBuilder` and thirty-two inline screens is a type nobody wants to infer.
3. Put every user-facing string in **all three** `Resources/*.lproj/Localizable.strings` at once.
   `LocalizationTests` fails the build on a key that exists in one and not the others, on a
   translation left equal to its English, on a format specifier dropped in translation, **and on a
   key nothing references**.
4. Reach for `MageRideSpacing` / `MageRideRadius` / `MageRideColor` / `.mageFont(_:)` — never a raw
   number or hex. `ThemeTokenTests` reads the **compiled asset catalogue**, so a mistyped colour
   fails a build rather than a night shift.
5. The full-width orange bar in the wireframes is `.buttonStyle(.mageCta)`, not `.borderedProminent`.
6. **Re-run `python3 apps/passenger-ios/Tools/generate_xcodeproj.py`** and commit the `.pbxproj`.

## Cluster 1 (C095) — what the next screen group can reuse

- **`OnboardingRouter.next(...)` is the only place that decides where a passenger belongs.** The
  splash, the login screen after a verify and Profile Setup after a save all call it. If a new gate
  is ever added before the map it goes in that function, not in a screen.
- **`UI/` is now populated**, and a screen uses it rather than raw SwiftUI: `LabelledTextField`,
  `PhoneNumberField`, `OtpField`, `TextLink`, `CountdownLink`, `FormErrorText`, `LabelledDivider`,
  `ProfileAvatar`, `SectionLabel`, `GroupedList`, `GroupedRow`, `SelectionRow`, `IllustrationPanel`
  and `PageDots`. Append yours rather than putting a number or a shape at a call site.
- **`PassengerProfileRepository` is `iam.users` as an app uses it** — `GET`/`PUT /v1/users/me`.
  C101's SCR-PI-027 reads the same pair; reuse it rather than opening a second seam. Its `update`
  has **no language parameter**, which is AL-26 made structural.
- **`OnboardingErrors` is the FIRST-RUN code table only.** Every arm is a code `iam.yaml` declares on
  the operations SCR-PI-003 and SCR-PI-004 reach. Add your own table for your contracts; one `switch`
  over the platform is a function nobody can check. **Use `OnboardingErrors.kotlinCause(of:)`** — a
  Kotlin exception does not cross the bridge as itself, and a `catch let error as MageRideError`
  never matches without it.
- **A proper noun is data, not copy.** The language endonyms (`සිංහල`), the `+94` prefix and the
  `7X XXX XXXX` mask are Swift constants (`LanguageDisplay`, `PhoneNumber`), because three identical
  values in the three `.strings` files is exactly what `LocalizationTests` fails on.
- **Language is applied by `PassengerLocale.apply(_:)` and takes effect immediately.** There is no
  `recreate()` on this platform and none is needed — the bundle is re-pointed and the next view
  resolves against it. A test that calls it must reset it in `tearDown`; it is process-wide.
- **Implement a Swift protocol, never a Kotlin one with `suspend` methods.** `PassengerSessions`,
  `OnboardingRepository`, `PassengerProfileRepository`, `ActiveRideLookup` and `LocationPermission`
  all exist for that reason, and each is why its screen is assertable with no gateway.

## The Xcode project is generated, and the generator is shared

`.pbxproj` is the committed artefact — CI probes for it and `xcodebuild` reads it. It is also a file
where every source needs two 24-hex ids, a group membership and a build-phase entry, and the classic
failure is a file added to the group and forgotten in `Sources`: it compiles for whoever added it and
not in CI. So `shared/tools/generate_xcodeproj.py` derives it from the tree, with ids that are a hash
of the path, which makes the output identical on every machine and the diff show only real changes.

**One generator, two apps** (Δ C094 — the C085 handoff asked for this). What differs between them is
data, not logic: `Tools/generate_xcodeproj.py` here and in `apps/driver-ios` are two-line shims that
describe their target name, bundle id and Swift packages. Regenerating the driver project after the
promotion produced a byte-identical `.pbxproj`, which is how the change was checked on a host that
cannot open either.

Hand-editing the `.pbxproj` is allowed (CLAUDE.md's "never hand-edit a generated file" rule is about
`build/prompts`, `build/progress.md` and `build/screen_coverage.md`). What you must not do is change
one and not the other.

## Where Kotlin ends and Swift begins

`:shared` is bigger on this platform than it is on Android, and the boundary is deliberate.

- **Swift cannot use Koin.** `Module.single`, `module { }` and `Koin.get` are all `inline` +
  `reified`, and an inline reified function is not exported to Objective-C at all. So the app passes
  **values** to `startIosGraphWithH3(config:h3Grid:)` and gets `IosAppGraph` back — typed properties
  over the same singletons `:shared` wires internally. `PassengerGraph` holds it and constructs what
  is native.
- **Swift cannot collect a `Flow`.** `IosFlowWatcher<T>` is the adapter. Cancel every subscription.
- **A `kotlin.time.Duration` must never be read as a number.** The export flattens an inline value
  class to an opaque `Long` whose encoding is a packed nanos/millis pair with a tag bit.
  `IosLiveHub` and `IosReconnectBackoff` are the two doors a duration crosses on this surface;
  `IosGeoCells.passengerCellSubscription` exists so the 30 s hysteresis never has to.
- **Swift cannot call a reified decoder.** `MageRideJson.decodeFromString<T>()` is `inline` +
  `reified`, so `IosLiveHubPayloads` has one function per hub event. Every one answers `nil` rather
  than throwing, because an exception out of a non-suspend Kotlin function crosses as an uncatchable
  Objective-C exception (the C091 finding).
- **Swift implements two Kotlin interfaces and no more**: `H3Grid` (`SharedH3Grid`) and nothing else.
  A Kotlin interface with `suspend` methods is deliberately **not** implemented from Swift — see
  `NearbySnapshots`, which is why the live plane takes a one-method app protocol rather than
  `QueryApi`.
- **A Kotlin default argument does not survive the export**, so every parameter is passed at every
  call site. That is why `ApiNearbySnapshots` exists rather than a call to `getNearbyVehicles` in
  the inbox.

## Rules this target is built on

- **SCR-PI-033 is a Menu TAB, not a drawer** (Δ Section C, and the wireframe's own `Δ iOS` clause is
  *"`List` with `NavigationLink` rows"*). Nothing here hosts a drawer, no screen has a `≡`, and
  `PassengerTab.menu` carries a route where the Android enum's carries `null`. The Android drawer is
  not an AL-31 violation and this is not a repeal of it — AL-31 is a rule about the *driver*
  dashboard.
- **The map is a widget, not a seam.** `MageRideMap(vehicles:userPosition:pins:routePolyline:…)`
  already does MAP-01..08 and MAP-10; a screen passes frames and never a rendering position. This is
  the opposite of `apps/driver-ios`, where `MageRideMapView` hands out a bare `MLNStyle` — AL-31
  means that map only ever draws one marker, and this one draws every mode in a cell at once.
- **The passenger view is 19 cells and the client never subscribes to a vehicle.** `HubSubscriptions`
  is the only place group membership changes; `signalr-hub.md` §2.1 says `vehicle:{vehicleId}` groups
  are *"joined by the server, never asked for"*. There is no `SubscribeVehicle` method, and
  `PassengerLiveMapTests` pins the four that exist.
- **Recovery is an ORDER, not a set** (D6' §5.4): rejoin the groups, *then* `GET /v1/nearby`. A
  client that snapshots first loses every frame published between the two calls.
- **This app has no MQTT client and never will.** D3' §3.3: device position *ingest* is MQTT and is
  the driver's; passenger realtime-*out* is SignalR. There is no broker host in the Info.plist, no
  background-location mode and no publisher — `PassengerEnvironmentTests` asserts all three.
- **No colour is a hex in Swift.** §0.2 says SwiftUI takes "the same hex, light/dark appearances" as
  a `Color` asset, so the palette is `Resources/Assets.xcassets` and the test reads it back.
- **Dynamic Type is not optional.** `.mageFont(_:)` is `@ScaledMetric` over the spec's point size,
  anchored to the text style §0.2 maps the role to. `.font(.title)` ignores the spec's sizes;
  `.font(.system(size:))` ignores the passenger. Both are wrong.
- **A deep link is resolved, not trusted.** `PushRouter` maps a `mageride://…` URI onto a known
  `PassengerRoute`; an unrecognised one opens nothing. `mageride://wallet` and
  `mageride://documents` are the **driver's** and deliberately resolve to nothing here.
- **P-02's location request carries no deeplink at all.** It is a silent data message —
  `{kind:'location_request', requestId, bookerName, ttl:300}` — so `PushRouter` builds SCR-PI-011's
  route from `data.requestId`. Do not invent a `mageride://pickup-confirm` host.
- **`PassengerDatabase` is the app's deferred answer to C018's un-bound database.** Opening it is
  `suspend`, so it is opened by the first caller behind an actor and shared. Six §2 tables and eight
  screen groups: do not call `openPassenger()` anywhere else.

## Section C deltas that are real, and are not bugs

Every one of these is D2' §C, a `passenger_ios.html` cell's own `Δ iOS` clause, or a platform
constraint, and each is called out at its call site.

| Concern | Android | iOS, here |
|---|---|---|
| SCR-PA/PI-033 | `ModalNavigationDrawer` behind a scrim, opened from a `≡` | a **Menu tab** over a `List` — the cell's own clause |
| Language change (002/004) | `Activity.recreate()`, so the model tracks whether it *changed* | the bundle is re-pointed and the next view resolves; applying the same one twice is free |
| Login errors (003) | inline under the field | an `.alert` — the cell's own clause; the attempts counter stays inline |
| OTP auto-fill (003) | SMS Retriever, unwireable without a signing certificate | `.oneTimeCode` QuickType — no hash, no signing config |
| Location re-prompt (005) | the dialog stops after **two** refusals | it stops after **one**; the CTA becomes *Open Settings* either way |
| Reduced-accuracy grant (005) | Android 12+ COARSE counts as granted | iOS 14+ approximate counts as granted — same reasoning, ~3 km map |
| Offline banner (032) | a `Snackbar`-adjacent inline banner in the `Scaffold` | `.safeAreaInset` — the cell's own clause; an overlay would cover a full-bleed map |
| Update gate (031) | mandatory `AlertDialog` · soft `Snackbar` | mandatory `.alert` with one action · soft inline banner with ✕ |
| Language change | `Activity.recreate()` re-inflates every resource | no `recreate()`; `PassengerLocale` redirects the bundle and views rebuild |
| Location cadence | a ten-second interval on the fused provider | `CLLocationManager` has no interval — a 250 m `distanceFilter` instead |
| Notification mute | per-channel, user-controllable | per-app only; the two categories buy interruption level, not a user control |
| Captive portal | `NET_CAPABILITY_VALIDATED` reads it as offline | `NWPath` has no equivalent; it reads as online |
| Elevation | M3's six tinted levels | one shadow, `radius 8 / y 2 / 0.12` (§0.2's own iOS row) |
| Filing a push | `onMessageReceived` fires for every data message | three doors only: foreground, tapped, or `content-available` and the system woke us |
| Hub payload binding | Gson's `JsonElement` — the identity binding | `AnyJSON`, hand-written, because `Decodable` has no equivalent |
| Polyline decode | none in the SDK without the `-ktx` artifact | none in the SDK without Turf — the same twenty lines, ported |

## Things that will bite

- **The XCFramework must be assembled before `xcodebuild`.** See Verify.
- **Three files have never been compiled by anybody and are the first to read on macOS**:
  `Live/LiveHubTransport.swift` (SignalR-Client-Swift), `Map/MageRideMap.swift` and
  `Map/VehicleLayers.swift` (MapLibre's `NSExpression` builders). They are written against the
  documented APIs on a host that cannot build for iOS, exactly as C085's three were.
- **`SWIFT_STRICT_CONCURRENCY` is `minimal`**, deliberately, for C085's reason. The three `actor`s
  here (`HubSubscriptions`, `LiveHubInbox`, `PassengerDatabase`) each hold a non-`Sendable` Kotlin
  collaborator, which is what raising it will surface first.
- **`FirebaseApp.configure()` is guarded on `GoogleService-Info.plist` being present**, and it is not
  in this repository (C124 owns the Firebase project). Calling it without one raises an Objective-C
  exception, which Swift cannot catch. Push registration therefore answers `nil` on every build
  produced today, exactly as it does on Android.
- **App Attest does not exist on the simulator.** `DCAppAttestService.isSupported` is `false` there,
  and there is no registration endpoint yet either (C014's gap (b), restated by C085 as gap (c)).
- **A Kotlin exception is not a Swift `Error` you can pattern-match.** Kotlin/Native wraps it in an
  `NSError` under `userInfo["KotlinException"]`. `OnboardingErrors.kotlinCause(of:)` is the unwrap;
  without it every failure in the app resolves to the generic message.
- **A localised `.strings` file is a variant group**, not three files. The generator builds them.
- **A `.strings` comment is a C comment and does NOT nest.** Writing `values*` followed by a slash
  inside one closes it early, `NSDictionary(contentsOf:)` answers `nil`, and every key in the app
  resolves to its own name. C089 found seven of those on the driver side.
- **`Bundle.main` is the TEST HOST when a test runs.** Every resource lookup goes through
  `MageRideColor.bundle`; `Bundle.main` finds nothing and the failure looks like a missing asset
  rather than a missing bundle. The exception is `PassengerEnvironment`, which reads the *app's* own
  Info dictionary and is the only file allowed to.
- **The tests run hosted** (`TEST_HOST` is the app), because they read the asset catalogue, the three
  `.lproj` and the Info.plist. A standalone test bundle would find none of them.
- **`shared/swiftpm/MageRideH3/Sources/CH3` is vendored third-party C.** Do not reformat it, do not
  lint it, and do not edit it — see `VENDOR.md` for the tag, the commit and how to upgrade.
