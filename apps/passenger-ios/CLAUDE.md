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
│   ├── Home/        C096 · SCR-PI-006/007/008/010/032 — the map, the filter, the popup, the
│   │                destination field, MapFilter, VehicleLabels, RecentPlaces (§2.2's only door)
│   ├── Booking/     C097 · SCR-PI-009/010b/011/012/012a/013 — the multimodal list, the proxy
│   │                round trip, the parcel, the paste sheet, the schedule, and BookingDraft
│   │                (the one booking six screens edit)
│   ├── Ride/        C098 · SCR-PI-014/015/015a/016/017/018/019 — finding, the active ride, the
│   │                call chooser, the rails, AL-47's attestation, the receipt and the rating
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

## Cluster 2 (C096) — the live map, the filter, the popup and the search

- **`MapFilter` is a value, not a service, and it holds ``ModeToken``/``VehicleToken`` rather than
  the wire enums.** SCR-PI-006's answer lives in model state and is re-applied to *every* batch. It
  is deliberately **not** folded into the live plane: the plane holds what the *platform* says is
  visible (entitlement, freshness, engagement) and the filter holds what the *passenger* asked to
  see. Folding them would make a Mode B vehicle with no grant indistinguishable from one that was
  simply switched off.
- **A tap is routed by mode in the model** (`LiveMapModel.onMarkerTapped` → `MarkerTap`), not by a
  sheet. Mode A opens SCR-PI-007, Mode B hands SCR-PI-024 the vehicle id (AL-23/US-4.6), Mode C, a
  marker with no mode and a marker the filter has hidden all do nothing (US-7.4). A Mode B vehicle
  cannot reach `VehiclePopup` by any path.
- **`VehicleLabels` is the bridge, not a second legend.** `VehicleToken` (Theme) already owns §0.2's
  colour and SF Symbol and stays free of `MageRideShared`; this file adds the trilingual name key,
  the `VehicleType`/`ServiceMode` → token mapping and the eight chip types. A second colour table
  here would be the copy MAP-03 exists to prevent.
- **`RecentPlaces` is the only door onto §2.2's `place_recents`, and SCR-PI-008 is its writer** —
  the table is *"recent / searched locations"*, so choosing a prediction records one whether or not
  a ride follows. Local-only: no `dirty`, no `synced_at`, no outbox. It has no change feed, so the
  map re-reads on `.onAppear`.
- **The SQL is `:shared`'s, not this app's.** `IosPlaceRecentsKt` reads and writes §2.2 in
  `GeocodedPlace`, for the reason `IosNotificationInbox.kt` gives on the driver side: a
  `Query<Place_recents>` comes from a dependency the framework does not `export`, `last_used_at` is
  a `kotlin.time.Instant`, and the row id is a *derived* value whose rule belongs beside the insert.
- **`GET /v1/nearby` has ONE seam in this app.** `graph.nearby` is the same `NearbySnapshots` the
  live plane's D6' §5.4 resync uses, and SCR-PI-007's ETA/driver/plate go through it too — the popup
  asks for a radius around the **passenger**, because `etaSeconds` is *"seconds to the querying
  passenger"* and a lookup centred on the bus answers roughly zero.
- **SCR-PI-032 is a state of SCR-PI-010, not a screen.** `LiveMapState.stale` (anything but
  `.connected`) fades the marker layers through `MageRideMap(dimmed:)`; nothing is erased, because a
  passenger who has lost signal still wants to know where the bus was (US-15.2). `EmptyReason` has
  four values rather than a boolean so US-7.14 can tell an outage from a filter from a quiet area.
- **The map runs a 15 s tick, and the Android twin does not.** ADD §7.4 step 6 *holds* a boundary
  crossing for thirty seconds, and this platform's `CLLocationManager` has a 250 m `distanceFilter`
  — so a passenger who crosses a cell edge and then stands still produces no further fixes and the
  held crossing never lands. `PassengerLiveMap.refreshCells()` exists for exactly that and its own
  KDoc says *"SCR-PI-010's own tick calls this"*. C078 never wired one either — **both apps tick
  now**, `LiveMapViewModel.tickCells()` on the Android side at the same 15 s (Δ C096).
- **Never construct a boxed Kotlin primitive from Swift in this cluster.** `KotlinInt(int:)` and
  `KotlinInt(value:)` are both in this repository, in two apps neither host has compiled, and only
  one of them is right. Reading one is settled (`int32Value`); *building* one is avoided —
  `IosGeoSearchKt.searchPlacesNear` takes a `GeoPoint?` so the three optional primitives on
  `searchPlaces` never cross, and every fixture passes `nil`.
- **Three contract gaps this cluster draws around**, all restated from C078 and none of them an app
  change: **no route number exists for a vehicle** anywhere (`VehicleFrame` and `NearbyVehicle` both
  lack one), so SCR-PI-007's headline is the vehicle *type* where the cell writes *"Route 138"*;
  **`VehicleFrame` carries no sample timestamp**, so the cell's `seen 6s ago` pill is not drawn;
  and **there is no `GET /v1/vehicles/{id}`**, so the popup matches `GET /v1/nearby` by id.
- **AL-17 beats D2' §SCR-*-008, and the fence is structural.** That section still says the drop field
  accepts a route number and that predictions blend routes with places; the cell, AL-17 and this
  component's prompt say geo-only. `PassengerPlaces` has **no route lookup on it at all**, so
  `getBusesOnRoute` is unreachable from SCR-PI-008 without adding a protocol method. **US-7.9 has no
  screen on either platform** as a result.
- Reusable UI added here (`UI/MapControls.swift`): `SheetGrabber`, `TopRoundedRectangle`,
  `SearchBarButton`, `PlaceChip`, `MetricTile`, `MapNotice`, `MapOverlayButton`, `OutlinedAction`,
  plus `MageRideSymbols.separator` and `MapFormat` (`350 m`, `2.4 km`, `2 min`). Tokens added:
  `grabberWidth/Height`, `searchBar`, `outlinedAction`, `chipSwatch`, `chipIcon`,
  `filterChipMinimum`, `routeDot`, `vehiclePopupHeight`.

## Cluster 3 (C097) — the booking flow

- **`BookingDraft` is one object for the process and it is where a booking lives.** Six screens edit
  one — a destination on SCR-PI-008, a tier on SCR-PI-009, a rider on SCR-PI-010b, a parcel on
  SCR-PI-012, a time on SCR-PI-013 and a payment rail on C098's SCR-PI-016. Do not thread booking
  fields through a `NavigationPath`; `BookingDraft.clear()` is what stops one outliving its ride.
- **`CaptureTarget` is how one picker serves five callers.** SCR-PI-008 is reached from the home
  sheet, the booking screen's edit row, the proxy pickup, both package ends and the schedule
  destination, and it cannot tell which. Whoever opens it calls `draft.expect(…)` — **at the
  navigation site, in `BookingDestinationView`**, so the call that opens the picker and the call
  that says why are two adjacent lines. `capture(_:)` answering `false` means *"nobody was waiting —
  this is a new booking"*.
- **`BookingRepository` is one door onto six services** (transit, fare, ride, dispatch, query, iam).
  A seventh operation from one of them goes there, not into a second client beside it.
- **`TierQuote` has three fields and a test pins the list** — AL-19/BR-23.3 says a Mode C tier shows
  the upfront price and nothing else before a driver is matched, and the type is the enforcement.
- **AL-18: a public route is tracked, never booked.** Selecting one drops the tier from the draft,
  **removes** the payment chip (not disables it) and changes the CTA to *"Track route"*.
- **AL-55 degrades to one muted row.** transit-svc unreachable and transit-svc with no feed are the
  same row, and neither touches the private tiers — *"nothing blocks on GTFS coverage"*.
- **`MapsLink` parses on the device and only short links reach the server** (AL-20). Precedence is
  `!3d!4d` → `q=` → `ll=` → `@`, because a `/maps/place/…` URL carries both a place pin and a
  viewport and they are routinely a hundred metres apart.
- **A decline sends no coordinates** (P-02). `declineLocationRequest` takes an id and has no
  parameter for a point; `ConfirmPickupModelTests` asserts it on what the repository was handed.
- **`PaymentRails` lives in `Booking/` because cluster 3 needs it first.** C098 owns SCR-PI-016 and
  should **extend** it — `preferable`, `caption(_:)`, `storedValueOf(_:)`, `fromStored(_:)` — rather
  than open a second one.
- **`LastKnownFix` is how a screen gets a position without subscribing for one**, and
  `RecordingLocationSource` — which the graph wraps `CoreLocationPassengerSource` in — is what writes
  it. `PassengerLocationSource` is *cold* and the blue status-bar pill stays lit for as long as
  anything is subscribed, so recording happens at the seam rather than in one of five subscribers.
  Three readers: SCR-PI-008's geocoder bias, a booking's default pickup, and the map picker's opening
  camera. **Do not add a second subscriber to get a position.**
- **`BookingDraft.begin` defaults the pickup to that fix, and it is a defect fix rather than a
  convenience** (Δ C097). `begin` takes an *optional* pickup and every production call site on the
  Android side omitted it, so the draft had none — and `RideBookingViewModel.refresh()` returns early
  on exactly that, which meant SCR-PA-009 loaded neither list. The default lives inside the draft on
  both platforms so a fourth call site cannot reintroduce it.
- **`PackageOtps` is written here and read by C099.** P-07's pickup code exists in exactly one
  response and no read returns it; the type that catches it sits next to the screen that catches it.
- Reusable UI added here (`UI/BookingControls.swift`): `StatusPill`, `SolidBadge`, `InfoBanner`,
  `FormattedBanner`, `TierCard`, `RouteFieldRow`, plus `UI/MoneyFormat.swift` (`Rs 740`) and
  `Booking/BookingRows.swift`'s `PublicSection`, `TierRow`, `PaymentChipRow`, `JourneySummaryCard`,
  `LocationMethodPicker`, `CapturedPlaceRow`, `LoadingRow`, `MutedRow`. Tokens: `tierIcon`,
  `selectionRing`, `pinPreview`, `pasteSheetHeight`, `bookingMapHeight`.
- **Four contract gaps this cluster draws around**, all C079's and none an app change: **no headway
  or frequency** on any transit shape, so the cell's *"every ~10 min"* cannot be drawn; **no
  walking-routing service**, so the blue leg is a straight line — honest about the distance, not
  about the path; **no SCR-PI id for a map picker**, so `MapPickSheet` is a sheet; and
  **SCR-PI-012's drop-off *Request* has no wired round trip** — the chip selects and the fence
  holds, but asking a *recipient* needs P-02's machinery generalised out of `ProxyRiderModel`.

## Cluster 4 (C098) — the ride, the call, the money and the rating

- **A ride moves forwards, so four of its six moves are a `replaceTop`.** SCR-PI-014 is replaced by
  SCR-PI-015 on acceptance, SCR-PI-015 by SCR-PI-016 on `Completed`, SCR-PI-017 by SCR-PI-018 on
  settlement. Pushing would leave an edge-swipe back into *"finding a driver"* for a ride that has
  one.
- **`RideHandOff` is the defect this cluster closes.** Both apps *documented* the ride handing over
  to the payment screen and **neither implemented it**: no screen, no `NavHost` arm and no push
  built `PaymentMethod(rideId)`, so a `Completed` ride left the passenger on a finished trip and the
  whole of D-10 (SCR-PI-016/017/018/019) was reachable only from the receipt it comes *after*.
  `apps/passenger-android` was fixed in the same session.
- **`PaymentSelection` is the second one.** `PassengerRoute.payFare(rideId:)` cannot carry a rail —
  the table is diffed against Kotlin's — and the Android NavHost was *discarding* the confirmed
  method, so choosing **Cash** landed on a driver-QR scan. A process-lifetime holder, like
  ``PackageOtps``, written by SCR-PI-016 and read at the navigation site.
- **`PaymentRails` is the only place a payment method becomes a control**, and it contains neither
  `onepay` nor platform-`lankaqr` (AL-57/AL-59). **No surviving rail carries a surcharge**, so
  nothing in this app can render one; `PaymentRailsTests` pins the lists, the labels and the
  captions. C098 added `preferable`, `captionKey(_:)`, `storedValueOf(_:)` and `fromStored(_:)` to
  it, which is what C101's SCR-PI-027 row reads.
- **AL-47 is a conversation, not a callback.** The passenger pays into the driver's own bank, so no
  webhook reaches fare-svc: `claimPaid()` → poll → `DriverConfirmedQR`. `QrClaimedByPassenger` is
  **not** settled and must never be shown as Confirmed. `PayFareState.isConfirmed` reads
  `PaymentState.isTerminal` rather than a hand-written list.
- **There is no masked call path and no masking copy** (AL-48). *"Normal call"* is `RideContact.dial`
  on `RideDetail.counterpartyPhone`, which the contract carries only from `Accepted` onward.
  US-26.5's notice is shown **once**, and only before a direct dial.
- **The Rs 50 is named before the tap.** D-05 settles it on the *next* trip, so the confirm is the
  only moment a passenger can be told; `ActiveRideModel.cancellationPenaltyMinor` is a local
  constant because `CancelRideResponse.penalty` arrives *after* the cancel.
- **`⛨ SOS` navigates and does not act**, and there is **no share-trip control** — D-34's link is
  SCR-PI-029's, and neither wireframe draws a fourth button on SCR-PI-015 (the Android screen has
  one; see the C098 handoff).
- **The QR scanner is VisionKit's `DataScannerViewController`** — the cell's own `Δ iOS` clause, and
  the first decoder linked into this target. The **camera grant is asked for before the sheet is
  presented**, because `isAvailable` is `false` without it. A refusal is not an error state: AL-15's
  bank-app link and AL-47's claim both still work.
- **Nothing renders a MageRide QR** (AL-22) and nothing shows a start OTP: `ride.yaml`'s `pickupOtp`
  is *"package bookings only"* and ride-svc's own contracts say a rider start OTP is *"accepted and
  ignored in this build"*. ``StartCodeCard`` draws the card and says so.
- Reusable UI added here (`UI/RideControls.swift`): `ScrimmedSheet` (a destination the wireframe
  draws as a sheet over a scrimmed map), `RadarPulse` (`TimelineView`, the cell's own clause),
  `DriverIdentityRow`, `StartCodeCard`, `StarRating`, `KeyValueRow`, `AmountHeadline`; plus
  `MoneyFormat.pending`, `OutlinedAction`'s `tint`, and `MageRideControl.radar` / `.otpBox` /
  `.star` / `.scanPanel` / `.scanPanelDash` / `.receiptMapHeight`.
- One `:shared` `iosMain` helper was added — `IosRatingsPending.kt` — for `IosPlaceRecents.kt`'s
  reason: `created_at` is a `kotlin.time.Instant`, and §1.11's two CHECK spellings belong beside the
  schema that has to accept them.

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
| Mode filter entry (010) | the app bar's trailing `⦿` icon | a FAB on the map — this cell draws no app bar at all |
| Home sheet (010) | a drag-handle `BottomSheetScaffold` | a **drawn** panel above the tab bar, not a `.sheet` — see `LiveMapScreen` |
| Mode / type filter (006) | `ModalBottomSheet` + `Switch` + `FilterChip` | `.sheet(.medium/.large)` + `Toggle` + `Toggle(.button)`, with the cell's `.impact(.light)` |
| Vehicle popup (007) | `ModalBottomSheet` | `.sheet(.height(220))` — the cell's own clause |
| Held cell crossing (010) | a 15 s tick, added by Δ C096 — C078 shipped without one | a 15 s tick; it matters more here, because `distanceFilter` means a stationary passenger emits nothing at all |
| Recent row subtitle (010) | the address line | the **distance**, which is what both wireframes draw |
| Booking toggles (009) | four `AssistChip`s | two segmented `Picker`s — the cell's own `seg` |
| Booking back button (009) | an `IconButton` over the map | the same, because the cell draws no navigation bar |
| Capture methods (010b/012) | a scrolling row of `FilterChip`s | a segmented `Picker` — `UISegmentedControl` in the cell's CSS |
| Paste affordance (012a) | a `ClipboardManager` read behind a button | `PasteButton`, so the app never touches the pasteboard until asked |
| Short-link timeout (012a) | `withTimeout(3.seconds)` | a two-task race, because Swift has no `withTimeout` |
| Date and time (013) | an M3 date picker **and** a time picker | one `DatePicker(.graphical)` — the cell's own clause |
| Contacts row (010b) | not built | not built either, and deliberately: adding one on this side alone is a parity break |
| Radar sweep (014) | an `InfiniteTransition` over a `Canvas` | `TimelineView(.animation)` — the cell's own clause; the ring is a function of the clock, so nothing is left running |
| Cancel confirm (015) | an `AlertDialog` | `.confirmationDialog` — the cell's own clause; the Rs 50 is in the message either way |
| Call chooser (015a) | a `ModalBottomSheet` | a `.sheet` at `.medium` |
| Direct dial (015a) | `ACTION_DIAL` opens the dialler | a `tel:` URL **places** the call, so it goes through `RideContact` |
| Driver QR (017) | CameraX + ZXing's reader half in a `Dialog` | **`DataScannerViewController`** in a `.sheet` — the cell's own `Δ iOS` clause; first-party, no dependency |
| Bank-app link (017) | `ACTION_VIEW`; `canOpenURL`'s answer is hidden by package visibility | `open(_:options:)`; `canOpenURL` is hidden by `LSApplicationQueriesSchemes` — so it opens and reports |
| Fare breakdown (018) | four rows, from a field nothing assigns | a `DisclosureGroup` — the cell's own clause — with the total and an honest note |
| Share trip (015) | a fourth outlined button neither wireframe draws | absent; D-34's link is SCR-PI-029's |

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
