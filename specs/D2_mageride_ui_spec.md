# D2′ — MageRide Screen-by-Screen UI Specification (Passenger · Driver · Admin Portal · Fleet Portal)

> **🔄 Aligned to ADD v2.6 / URD v2.2 (ADD §1.8 AL-01…AL-16).** This pass: "Wallet Portal" web screens → **Admin Portal** (`admin.mageride.lk`, internal-role back-office, AL-02) + new **Fleet Portal** (`fleet.mageride.lk`, AL-03); driver **reseller** screens stay **in the Driver App** (capability, AL-01); **bank-transfer top-up removed** from all UIs (AL-05); **LankaQR = "Pay" deep-link button** (QR fallback, AL-15); canonical vehicle markers/pickers (`car`→`sedan`, +`truck`/`mini_truck`, AL-09); passenger **default payment method** + saved addresses settings (AL-14); **driver emergency contact** in profile (AL-13); `passenger.mageride.lk` = no-login proxy-ride web subview (AL-04).

> **Phase B deliverable (Prompt B2).** Transformed from the Namma Yatri Phase-A UI spec
> (`nammayatri-extraction/D2_ui_specification.md`) onto the MageRide stack per ADD v2.4 §18.2
> (KMP + **Jetpack Compose / Material 3** Android, **SwiftUI / HIG** iOS) and URD v1.3 §6 + epics.
> Cross-checked against D1′ (`mageride-specs/D1_mageride_user_flows.md`) for screen IDs / parent flows.
>
> **Stack delta:** NY = PrestoDOM (one PureScript view tree, Android-only host, Google Maps, Juspay,
> ₹/+91/Hindi). MageRide = **native Compose + native SwiftUI** over shared KMP state, **MapLibre GL
> Native + PMTiles**, **OnePay/LankaQR/Cash**, **Rs/+94/Si·Ta·En**. Every screen is tagged
> `[KEEP]`/`[ADAPT]`/`[REPLACE]`/`[NEW]` and carries **both an Android (`SCR-*A-###`) and an iOS
> (`SCR-*I-###`) variant**. **URD §6 is Android-only** → every iOS variant and all Admin/Fleet-Portal
> web screens are `[DERIVED]` from ADD §18.2 + URD epics. **Hard rules:** map UI = always `[REPLACE]`;
> payment UI = always `[REPLACE]`/`[NEW]`. All `[DELTA:INDIA]`/`[DELTA:JUSPAY]`/`[DELTA:PLATFORM]` and
> Phase-A `[UNVERIFIED]` items resolved (see §0.4).

---

## 0. UI Framework, Design System & Methodology (read first)

### 0.1 Framework
Unlike NY's single PrestoDOM view tree, MageRide renders **native per platform**: **Jetpack Compose +
Material 3** (Android) and **SwiftUI + HIG** (iOS), both driven by one **KMP state/reducer layer**
(`shared/commonMain/domain/*`, ADD §18.2). A screen = a Compose `@Composable` route inside a
`NavHost`/`Scaffold` **or** a SwiftUI `View` inside a `NavigationStack`. Maps are **MapLibre GL Native**
(Android/iOS SDK) over self-served **PMTiles on Cloudflare R2** — no Google Maps, no `JBridge`. The
ride/booking lifecycle is a **KMP state machine** (D1′ Appendix B.2), not a bottom-sheet `Stage` enum;
the Home overlay is hoisted Compose state / SwiftUI `@Observable`.

### 0.2 — MageRide Design Tokens (AUTHORITATIVE — single source of truth for Figma + Compose + SwiftUI)

**Brand & semantic colors** (light → dark variant). Compose = `MaterialTheme.colorScheme` roles;
SwiftUI = `Color` asset catalog (same hex, light/dark appearances).

| Role | Token | Light hex | Dark hex |
|---|---|---|---|
| Primary (MageRide orange) | `primary` | `#FF6D00` | `#FFB68A` |
| On-primary | `onPrimary` | `#FFFFFF` | `#4A2300` |
| Primary container | `primaryContainer` | `#FFE0CC` | `#6A3500` |
| On-primary container | `onPrimaryContainer` | `#2B1100` | `#FFDCC4` |
| Secondary (blue) | `secondary` | `#0061A4` | `#9FCAFF` |
| Secondary container | `secondaryContainer` | `#D1E4FF` | `#00497D` |
| Background | `background` | `#FFFFFF` | `#121316` |
| Surface | `surface` | `#F7F8FA` | `#1A1C1E` |
| Surface variant / card | `surfaceVariant` | `#ECEEF1` | `#2A2D31` |
| Outline / divider | `outline` | `#C7CBD1` | `#43474E` |
| Text primary | `onSurface` | `#1A1C1E` | `#E3E2E6` |
| Text secondary | `onSurfaceVariant` | `#44474B` | `#C3C7CF` |
| Text tertiary / hint | `outlineVariant` | `#74777C` | `#8D9199` |
| Success | `success` | `#2E9E4F` | `#7FD89A` |
| Warning / amber | `warning` | `#F5A300` | `#FFCF6B` |
| Error | `error` | `#D32F2F` | `#FFB4AB` |

**Vehicle-type marker legend** (MAP-03; map markers + tier cards):

| Vehicle | Token | Hex | Icon (Compose Material Symbol / SwiftUI SF Symbol) |
|---|---|---|---|
_Canonical vehicle types (AL-09; MAP-03): "car"→**sedan**; +truck/mini_truck (delivery)._
| Vehicle | Token | Hex | Icon (Compose Material Symbol / SwiftUI SF Symbol) |
| Bus (Mode A) | `vehBus` | `#2E9E4F` green | `directions_bus` / `bus.fill` |
| **Train (Mode A)** | `vehTrain` | `#E5331F` red | `train` / `tram.fill` (**rail icon, distinct**) |
| Motorbike | `vehMotorbike` | `#8E44CE` purple | `two_wheeler` / `bicycle` |
| Three-wheeler | `vehTuk` | `#F5C518` yellow | `electric_rickshaw` / `box.truck.fill`* |
| Flex | `vehFlex` | `#1ABC9C` teal | `directions_car` / `car.fill` |
| Sedan | `vehSedan` | `#1E6FE5` blue | `directions_car` / `car.fill` |
| Mini Van | `vehMiniVan` | `#EC4899` pink | `airport_shuttle` / `van.fill` |
| Van | `vehVan` | `#F57C00` orange | `airport_shuttle` / `van.fill` |
| Truck | `vehTruck` | `#8B5E3C` brown | `local_shipping` / `truck.box.fill` |
| Mini Truck | `vehMiniTruck` | `#808000` olive | `local_shipping` / `truck.box.fill` |
| Private (Mode B) | `vehPrivate` | `#8A8F98` grey | `local_shipping` / `shippingbox.fill` |

*custom asset where no SF Symbol exists.

**Mode badges:** Mode A = `#2E9E4F` green · Mode B = `#6B7280` grey · Mode C = `#FF6D00` orange.

**Typography** — Android: Material 3 type scale, font **Outfit** (display/headline) + **Inter** (body);
iOS: **SF Pro** with **Dynamic Type** (mapped roles). Sizes are the shared design contract:

| Role | Android M3 token | iOS Dynamic Type | px / pt | Weight |
|---|---|---|---|---|
| Display | `displaySmall` | `.largeTitle` | 32 | 700 |
| Headline | `headlineMedium` | `.title` | 22 | 700 |
| Title | `titleLarge` | `.title3` | 18 | 600 |
| Subtitle | `titleMedium` | `.headline` | 16 | 600 |
| Body | `bodyLarge` | `.body` | 16 | 400/500 |
| Body small | `bodyMedium` | `.callout` | 14 | 400 |
| Label / tag | `labelMedium` | `.caption` | 12 | 500 |
| Caption | `labelSmall` | `.caption2` | 11 | 400 |

**Spacing (4px base grid):** `4, 8, 12, 16, 24, 32, 48` → tokens `xxs/xs/sm/md/lg/xl/xxl`.
**Corner radius:** `sm 8` (buttons, chips) · `md 12` (fields, sheets-top) · `lg 16` (modals) ·
`card 24` (elevated cards, bottom sheets). **Elevation:** Android = M3 levels `0/1/3/6/8/12dp`
(`surfaceColorAtElevation`); iOS = subtle shadows (`radius 8, y 2, opacity 0.12`) + material blur.
**CTA token** (replaces NY `PrimaryButton`): height `56dp`, radius `sm 8`, `primary` bg, `onPrimary`
label `titleMedium`, optional 20dp leading/trailing icon, ripple/`.buttonStyle` press state, inline
lottie/`ProgressView` loader.

### 0.3 Map & marker patterns `[REPLACE]` (hard rule)
MapLibre GL Native style (light/dark PMTiles), `SymbolLayer` markers by `vehVeh*` color + heading
arrow (MAP-06), `ClusterLayer` when zoomed out (MAP-05), interpolated marker animation (MAP-04),
`LineLayer` trip polyline (MAP-08), accuracy circle (MAP-02), 100m geofence circle (MAP-10). Pins:
`pickup` green, `dropoff` red, `user` blue dot. Recenter FAB both apps. (NY `JBridge`/Google replaced.)

### 0.4 Resolved deltas (binding)
- `[DELTA:INDIA]` → **Rs** (was ₹), **+94** (was +91), **Si/Ta/En** (was Hindi/Kannada/Tamil/Telugu);
  no Aadhaar/UPI/metro/Yatri-Coins/Gullak/LMS UI (dropped, D1′).
- `[DELTA:JUSPAY]` → native **OnePay sheet / LankaQR link / Cash**; driver subscription screen replaced
  by **wallet + daily-fee card**.
- `[DELTA:PLATFORM]` → explicit per-screen Android(Compose)/iOS(SwiftUI) variants + Section C.
- Phase-A `[UNVERIFIED]` (10) resolved: **dark-theme tokens** now defined (§0.2); **spacing/elevation
  tokens** defined; marker rotation/cluster = MapLibre `SymbolLayer` `icon-rotate`/`ClusterLayer`;
  push-payload = D3 contract; **iOS host** = real SwiftUI target (Section C); RTL = N/A (Si/Ta/En all
  LTR); passenger no-show = N/A passenger-side (driver/back-end).

---

# SECTION A — PASSENGER APP (Compose: `SCR-PA-###` · SwiftUI: `SCR-PI-###`)

## Cluster: auth / onboarding

### SCR-PA-001 / SCR-PI-001 · `splash` — Boot & route · Parent: D1′ A.2 · **[KEEP]**
Centered MageRide logo on `primary` bg + indeterminate loader; KMP `auth` validates token → routes.
**Android:** `Box(contentAlignment=Center)` + `CircularProgressIndicator`; splash via `SplashScreen` API.
**iOS:** `ZStack` + `ProgressView`; `LaunchScreen.storyboard`. **States:** Loading only; Offline →
proceed to last screen with banner. **Anim:** logo fade/scale 300ms.

### SCR-PA-002 / SCR-PI-002 · `onboarding` — Carousel + language · **[ADAPT]** (NY welcome+language)
3-slide tutorial (US-1.2) + Si/Ta/En picker (US-1.3).
```
┌──────────────────────────┐
│        [skip]            │
│      ▢ illustration      │  pager slide (3)
│   Headline (22/.title)   │
│   Body (16/.body)        │
│        ● ○ ○             │  page indicator
│  ┌────────────────────┐  │
│  │  Get Started (CTA) │  │
│  └────────────────────┘  │
│  Language: [Si][Ta][En]  │  segmented
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Interaction |
|---|---|---|---|---|
| Pager | `HorizontalPager` | `TabView(.page)` | 3 slides | swipe |
| Indicator | `PagerIndicator` | `PageControl` | dots | — |
| Language | `SegmentedButton` | `Picker(.segmented)` | Si/Ta/En | select → persist locale |
| CTA | CTA button | `.borderedProminent` | "Get Started" | → login |

**States:** first-launch only. **Anim:** slide parallax; haptic `.selection` (iOS) on lang change.

### SCR-PA-003 / SCR-PI-003 · `login_phone` + `login_otp` — Phone & OTP · **[ADAPT]** (NY +91→+94, SMS gateway)
```
┌──────────────────────────┐
│ ‹ back                   │ TopAppBar / nav back
│ Enter mobile number (22) │
│ ┌──────────────────────┐ │
│ │ +94 │ 7XXXXXXXX      │ │ phone field
│ └──────────────────────┘ │
│ [inline hint]            │
│ ─ OTP mode ─             │
│ ▢ ▢ ▢ ▢ ▢ ▢  6 boxes    │ otp
│ Resend OTP (60s)         │ countdown
│ ┌──────────────────────┐ │
│ │   Continue (CTA)     │ │
│ └──────────────────────┘ │
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Source | Interaction |
|---|---|---|---|---|---|
| Country prefix | `Text` static | `Text` | `+94` | const | none |
| Phone field | `OutlinedTextField` | `TextField`.keyboardType(.phonePad) | 9-digit | local | validate |
| OTP boxes | `BasicTextField`×6 | `TextField`.oneTimeCode | digits | SMS auto-read | autofill |
| Resend | `TextButton`(timer) | `Button`(timer) | "Resend (NN s)" | KMP countdown | enabled@0 (US-1.10) |
| Continue | CTA | `.borderedProminent` | submit | computed | `auth` request/verify |

**States:** Loading → CTA inline loader ("Sending…/Verifying…"); Error → red hint `error` + Snackbar
(A) / `.alert` (I); rate-limited (D-32) → "Try again later". **Anim:** OTP auto-read fill; success
haptic `.notification(.success)` (iOS). **Android:** SMS Retriever auto-read. **iOS:** QuickType OTP.

### SCR-PA-004 / SCR-PI-004 · `profile_setup` — First profile · **[KEEP]** (NY drop gender/disability/referral)
Name field, photo picker, language, notification prefs (US-1.5). No referral/Aadhaar.
| Component | Compose | SwiftUI |
|---|---|---|
| Avatar | `AsyncImage`+`IconButton` | `PhotosPicker` |
| Name | `OutlinedTextField` | `TextField` |
| Language | `ExposedDropdownMenu` | `Picker` |
| Save | CTA | `.borderedProminent` |

**States:** Loading on save; Error inline. **Anim:** avatar crop sheet.

### SCR-PA-005 / SCR-PI-005 · `permission` — Location permission · **[ADAPT]** `[DELTA:PLATFORM]` resolved
Illustration + rationale + "Allow location" CTA → OS prompt. **Android:** `rememberLauncherFor
ActivityResult(RequestPermission)` (FINE + FOREGROUND_SERVICE_LOCATION). **iOS:** `CLLocationManager.
requestWhenInUseAuthorization()`. **States:** denied → "Open Settings" deep-link.

## Cluster: live map & booking (the home state machine)

### SCR-PA-010 / SCR-PI-010 · `live_map` — Live Map Home (PRIMARY) · Parent: D1′ A.2 · **[REPLACE]** (map hard rule)
Full-bleed MapLibre map + hoisted overlays. The booking/ride lifecycle swaps the bottom sheet content
by KMP ride state (idle → searching → finding → assigned → in-ride), but each is a distinct state of an
**explicit sheet**, not a `getMapHeight` hack.
```
┌──────────────────────────┐
│ [≡]  MageRide   [⦿filter]│ top icons + Mode filter FAB
│                          │
│      MAPLIBRE MAP        │ vehicle markers (legend colors),
│      ◦ buses ▲ tuks      │ heading arrows, clusters, user dot
│                ⊕ recenter│ FAB
│   ╭────────────────────╮ │ bottom sheet (drag handle)
│   │  Where to?     🔍  │ │ search entry
│   │  ★ Home   ★ Work   │ │ shortcuts (US-7.13)
│   │  Recent destinations│ │
│   ╰────────────────────╯ │
│ [Map] [Trips] [Support][≡]│ NavigationBar
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Data Source | Update |
|---|---|---|---|---|---|
| Map | MapLibre `MapView` | MapLibre `MLNMapView` | tiles+markers | PMTiles + SignalR | 2–8 s (US-7.3) |
| Top bar | `TopAppBar` | `.toolbar` | logo, menu | — | — |
| Mode filter | `FloatingActionButton` | `Button`+`.sheet` | ⦿ | local | → SCR-*-006 |
| Vehicle markers | `SymbolLayer` | `MLNSymbolStyleLayer` | A/B/C by color | SignalR geocell | live |
| Recenter | small FAB | `Button` SF `location.fill` | ⊕ | GPS | camera animate |
| Search sheet | `ModalBottomSheet` | `.sheet(.medium)` | "Where to?" | — | → SCR-*-008 |
| Shortcuts | `AssistChip` | `Label` | Home/Work | local | prefill |
| Bottom nav | `NavigationBar` | `TabView` | Map/Trips/Support/Menu | — | switch |

**Data:** nearby vehicles (SignalR, live); Mode B only if granted (D-23). **States:** Loading →
skeleton markers + shimmer shortcuts; Empty → "No {type} active nearby" banner (US-7.14); Error/
unserviceable → inline card; Offline → last-known markers dimmed + "Connection lost" banner (US-15.2,
15.6); Partial → markers stream in. **Tap A/B marker** → SCR-*-007 popup; **Mode C engaged hidden**
(US-7.16), **stale removed** (US-7.17). **Anim:** marker interpolation (MAP-04), sheet expand/collapse,
camera fit. **Scroll:** sheet scrolls, map fixed. **iOS** sheet detents `.medium/.large`; **Android**
drag-handle bottom sheet.

### SCR-PA-006 / SCR-PI-006 · `mode_filter` — Mode/type filter overlay · **[NEW]** (US-7.7)
Bottom sheet of toggles: **Mode A (Public — Bus, Train)**, **Mode B (Private)**, **Mode C (Standby)**,
plus per-type chips (bus/train/tuk/car/van/bike) with legend swatches.
```
╭──────────────────────────╮
│  ═ (drag handle)         │
│  Show on map             │
│  [✓ Mode A] green        │
│     ◦ Bus  ◦ Train(rail) │
│  [  Mode B] grey         │
│  [✓ Mode C] orange       │
│  ── Vehicle types ──     │
│  [bus][train][tuk][car]  │ filter chips w/ color dots
│  [van][bike]             │
│  ┌────────────────────┐  │
│  │  Apply             │  │
│  └────────────────────┘  │
╰──────────────────────────╯
```
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| Mode rows | `Switch`+`Text` | `Toggle` | A/B/C + badge color |
| Type chips | `FilterChip` | `Toggle`(.button) | trains filterable separately |
| Legend dot | `Canvas` circle | `Circle().fill` | marker color |
| Apply | CTA | `.borderedProminent` | re-query markers |

**States:** instant client-side filter. **Anim:** chip select scale + haptic `.impact(.light)`.

### SCR-PA-007 / SCR-PI-007 · `vehicle_popup` — Vehicle detail popup · **[ADAPT]** (NY tap-marker; **A/B only** US-7.4)
Small bottom card on tapping a Mode A/B marker: route/line, distance, **ETA** (US-7.11), vehicle reg,
driver name+photo (Mode A bus; **after-accept only for C**, US-7.12). Mode C engaged → no popup.
| Field | Type | Source | Update |
|---|---|---|---|
| Route/line | text | vehicle API | static |
| Distance/ETA | text | computed | live |
| Reg no | text | registry | static |
| Driver name/photo | text+avatar | registry | static |

**Compose:** `ModalBottomSheet` detent-low. **SwiftUI:** `.sheet(.height(220))`. **States:** stale →
"Last seen Ns ago".

### SCR-PA-008 / SCR-PI-008 · `search_location` — Location / route search · **[REPLACE]** (NY Google places → Nominatim/Photon; + public-route search per change619 #1)
Two-field origin/dest where the **drop field accepts a destination place _or_ a public route number** (e.g. `138`). Live predictions **blend matched public routes** (bus/train, US-7.x) **with** Nominatim/Photon geocoded places + "Select on map" + "Add address". Selecting a **public route** → SCR-PA-009 multimodal list (Track Route).
```
┌──────────────────────────┐
│ ‹  ● Pickup  ▸ search    │ origin field
│    ◆ Drop ▸ place / 138  │ dest = place OR route no.
│ ── predictions ──        │
│ 🚌 Route 138 · PUBLIC    │ public-route row (US-7.x)
│ 📍 Result · 1.2 km       │ LazyColumn / List rows
│ 📍 Result · 2.4 km       │
│ [ Select on map ]        │
│ [ + Add address ]        │
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Source | Update |
|---|---|---|---|---|
| Field×2 | `OutlinedTextField` | `TextField` | local | onChange (place or route no.) |
| Public-route row | `ListItem`+`AssistChip` PUBLIC | `Label`+badge | routes-svc (GTFS) | tap → SCR-*-009 |
| Predictions | `LazyColumn`+`ListItem` | `List` | Nominatim | debounced |
| Select-on-map | `TextButton` | `Button` | — | → map pin |

**States:** empty → recents/saved; Loading → shimmer rows; no-result → message; geocoder down → "Pick
on map"; **numeric/route-number entry → matched public routes ranked above places** (mixed result list).
**Anim:** list crossfade.

### SCR-PA-009 / SCR-PI-009 · `ride_booking` — Booking + multimodal options (PRIMARY) · **[REPLACE]** (map+payment; + public-route track per change619 #1)
Pickup/drop summary on map + a **multimodal options list**: **public routes** (bus/train, rendered as
`Bus Route 138 (Public) · ETA 15 mins`) alongside private **vehicle-tier cards** (upfront fare) + toggles
(For-Me/Someone, Person/Package) + payment chip. Selecting a **private tier** keeps **Book Now / Schedule**.
Selecting a **public route** zooms the map **out** to draw its **route polyline** (vehicle color), shows the
route **ETA**, and — if the rider is **off-route** — draws a **blue walking polyline** from current location to
the **closest halt** (with a "Walk N m to <halt>" hint); the primary CTA changes **Book Now → Track Route**
and no fare/payment is charged (public transport).
```
┌──────────────────────────┐
│  MAP — green route line  │
│  + blue walk-to-halt     │ off-route → halt nav (change619)
│ ╭──────────────────────╮ │
│ │ ● Pickup  ◆ Drop  ✎ │ │ editable
│ │ [For Me ▾][Person ▾] │ │ toggles (US-8.16 / 20.1)
│ │ ┌────┐ Bus 138 PUBLIC│ │ public route · ETA 15 min (US-7.x)
│ │ │ 🚌 │ ETA 15 mins   │ │
│ │ ⓘ Walk 250 m to halt │ │ blue polyline on map
│ │ ┌────┐ Tuk   Rs 740  │ │ ServiceTierCard (private)
│ │ ┌────┐ Sedan Rs 850  │ │
│ │ Payment: Cash ▾      │ │ (private tiers only) → SCR-*-016
│ │ ┌──────────────────┐ │ │ public → Track Route
│ │ │ Track Route /Book│ │ │ private → Book Now / Schedule
│ │ └──────────────────┘ │ │
│ ╰──────────────────────╯ │
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Source | Interaction |
|---|---|---|---|---|---|
| Public-route card | `Card`+`RadioButton` | `Button`+`Label` | route no., **ETA**, PUBLIC badge | routes-svc | select → map zoom-out + polyline |
| Route polyline | MapLibre `LineLayer` (veh color) | MapLibre line | route shape (GTFS) | routes-svc | drawn on select |
| Walk-to-halt polyline | MapLibre `LineLayer` **blue, dashed** | MapLibre line | current loc → closest halt | routing | off-route only |
| Tier card | `Card`+`RadioButton` | `Button`+`Label` | type, **Rs fare total** (US-8.4,8.9) | fare-svc | select |
| For-Me/Someone | `SegmentedButton` | `Picker` | proxy toggle | local | → SCR-*-010 |
| Person/Package | `SegmentedButton` | `Picker` | kind toggle | local | → SCR-*-012 |
| Payment chip | `AssistChip` | `Button` | Cash/LankaQR/OnePay (private only) | local | → SCR-*-016 |
| **Track Route** | CTA | `.borderedProminent` | track public route | — | → live map follows route + halt nav |
| Book Now | CTA | `.borderedProminent` | request ride (private) | — | `POST /rides/request` |
| Schedule | outlined | `.bordered` | future (private) | — | → SCR-*-013 |

**Data:** private tiers → upfront fare (1st-km+per-km+peak/night, **total only**), Source=fare-svc, on
pickup/drop change; public route → shape + halts + headway ETA, Source=routes-svc (GTFS), no fare. **States:**
Loading → "Estimating fare…" (private) / "Loading route…" (public); Error → retry; **booking disabled** (3
continuous cancels US-6A.10b) → blocked banner; public route **on-route** → no blue polyline (halt hint hidden).
**No-GTFS-coverage degradation (patch 2026-07-05 #3; safety net per AL-55/BR-32.4):** when `transit-svc`
returns no routes for the destination (genuine feed gap) **or** is unreachable, the public-routes
section is **hidden** and replaced by a muted empty-state row — *"Bus route info coming soon for this area"* —
while **live Mode A vehicles on the map and the private Mode C tiers render normally**; nothing blocks on GTFS
coverage (coverage is a data property of the active feed, not a launch gate).
**Anim:** tier expand, route draw, map zoom-out on public select.

### SCR-PA-010b / SCR-PI-010b · `proxy_details` — Proxy rider details · **[NEW]** (US-8.16–8.19)
Reveal when "For Someone Else": rider name + phone (or **contact picker**) + **3 pickup methods**:
(1) type & search, (2) map pin, (3) **Request Location** (FCM).
| Component | Compose | SwiftUI | Content | Interaction |
|---|---|---|---|---|
| Name/phone | `OutlinedTextField` | `TextField` | rider details | validate +94 |
| Contact pick | `Button`→Contacts | `.contactAccessButton`/picker | from phonebook | fill fields |
| Pickup method | 3× `SegmentedButton` | `Picker` | search/map/request | select |
| Request Location | CTA | `.borderedProminent` | FCM to rider | `POST /location-requests` (P-02) |

**States:** rider **unregistered** (P-03) → "Not a MageRide user — enter pickup manually" (US-8.19);
request **Pending** → spinner "Waiting for rider… (5:00)"; **Confirmed** → pin auto-fills; **Declined/
Expired** → fallback prompt. **Anim:** countdown ring.

### SCR-PA-011 / SCR-PI-011 · `confirm_pickup_rider` — Confirm pickup (Rider side) · **[NEW]** (US-8.18, P-02)
Shown to the **rider** on FCM `location_request`: their live GPS on a map with an adjustable pin +
booker name + Share/Decline.
```
┌──────────────────────────┐
│  [Booker] wants your     │
│  pickup location         │
│      MAPLIBRE MAP        │
│        ◎ (drag pin)      │
│  ┌─────────┐┌─────────┐  │
│  │ Decline ││ Share ▸ │  │
│  └─────────┘└─────────┘  │
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Interaction |
|---|---|---|---|
| Map+pin | MapLibre + draggable `Symbol` | MapLibre + drag gesture | adjust pickup |
| Share | CTA | `.borderedProminent` | `POST …/confirm {lat,lng}` |
| Decline | text/outlined | `.bordered` | `POST …/decline` |

**States:** 5-min TTL countdown banner; on expiry auto-dismiss. **Anim:** pin drop bounce; success
haptic. **Privacy:** declining never sends GPS (P-02).

### SCR-PA-012 / SCR-PI-012 · `package_booking` — Package booking · **[NEW]** (US-20.1,20.2,20.8; + size hint per change619 #2)
Size selector **S/M/L** (P-06) with a **helper hint + info icon directly below the size box** (weight +
vehicle guidance, updates per selection), item description, recipient name+phone, same 3 pickup methods,
payment incl. **COD**.
```
┌──────────────────────────┐
│ ‹ Send a package         │
│ Size:  ( S )( M )( L )   │ segmented (P-06)
│ ⓘ Up to 5 kg · backpack  │ helper hint (per size, change619)
│ Description [__________]  │
│ Recipient name [_______] │
│ Recipient +94 [________] │
│ Pickup: search/map/req   │
│ Payment: Cash/LankaQR/   │
│          OnePay/ COD     │ (US-20.8)
│ ┌──────────────────────┐ │
│ │  Get estimate / Book │ │
│ └──────────────────────┘ │
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| **Size S/M/L** | `SegmentedButton` | `Picker(.segmented)` | package size (P-06) |
| **Size hint** | `Row` `Icon(Info)` + `Text` | `Label(systemImage:"info.circle")` | weight/vehicle hint, swaps with size |
| Description | `OutlinedTextField` | `TextField` | item desc |
| Recipient | `OutlinedTextField`×2 | `TextField` | name/+94 |
| Payment incl COD | `RadioButton`s | `Picker` | LankaQR/OnePay/**COD** |
| Book | CTA | `.borderedProminent` | `POST /rides/request kind=package` |

**Size hint (per selection):** **S** → "Up to 5 kg · Fits in a backpack or motorbike box"; **M** → "Up to
20 kg · Fits in a Tuk Tuk or car trunk"; **L** → "Over 20 kg · Requires a van or truck". **States:** same
fare as Mode C (US-20.9); Loading estimate. **Anim:** size select scale, hint crossfade.

### SCR-PA-013 / SCR-PI-013 · `schedule_ride` — Schedule a ride · **[NEW]** (US-6A.4)
Date/time picker + summary + Confirm. **Android:** `DatePicker`+`TimePicker` (M3). **iOS:**
`DatePicker(.graphical)`. Reminders set (1h+15m, US-10.9). **States:** past time disabled.

### SCR-PA-014 / SCR-PI-014 · `finding_driver` — Finding driver overlay · **[REPLACE]** (NY finding-quotes)
Map + radar/pulse animation + "Finding a driver… (1:34)" + Cancel (free before accept). 2-min timeout
→ "No drivers available" (US-6A.11).
| Component | Compose | SwiftUI | Source |
|---|---|---|---|
| Pulse anim | Lottie/`Canvas` | Lottie/`TimelineView` | — |
| Timer | `Text` | `Text` | KMP 2-min |
| Cancel | outlined | `.bordered` | free (US-6A.9) |

**States:** Matching → pulse; NoDriver → empty-state + retry; cancelled → back to map. **Anim:** radar
sweep; transition to assigned card on accept.

### SCR-PA-015 / SCR-PI-015 · `ride_in_progress` — Active ride overlay · **[REPLACE]** (map+lifecycle)
Map with live driver marker + route, assigned-driver card (photo, name, reg, ETA, rating), Call/SOS/
Cancel, OTP-to-start display.
```
┌──────────────────────────┐
│   MAP: driver ▲ → pickup │ live (US-6A.12)
│ ╭──────────────────────╮ │
│ │ 👤 Driver · ★4.8     │ │ DriverInfoCard
│ │ Tuk · ABC-1234 · 3min│ │
│ │ Start OTP:  4 8 2 9  │ │ otpView
│ │ [📞 Call][⛨ SOS][✕] │ │ VoIP / SOS / cancel
│ ╰──────────────────────╯ │
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Source | Update |
|---|---|---|---|---|---|
| Driver card | `Card` | `GroupBox` | name,reg,ETA,★ | ride-svc (after accept US-7.12) | live |
| OTP display | `Text` mono | `Text`.monospaced | start OTP | ride-svc | static |
| Call | `IconButton` SF `phone.fill` | `Button` | VoIP | — | → SCR-*-028 |
| SOS | `IconButton` `sos` | `Button` | safety | — | → SCR-*-029 |
| Cancel | `IconButton` | `Button` | **Rs 50 after accept** (US-6A.10) | — | confirm dialog |

**States:** Accepted/DriverArrived/InProgress drive card content (Appendix B.2); cancel-after-accept →
Rs 50 confirm; Offline → driver marker frozen + banner. **Anim:** marker move, status pill transition,
arrival haptic.

### SCR-PA-016 / SCR-PI-016 · `payment_method` — Payment selection · **[REPLACE]** (payment hard rule, NY Juspay→native)
Sheet: **Cash (default) · LankaQR (no surcharge) · OnePay (+5%)**; for packages add **COD**.
```
╭──────────────────────────╮
│  ═  Payment method       │
│  ◉ Cash            Rs 500 │ default
│  ○ LankaQR        Rs 500 │ no surcharge (US-8.10)
│  ○ OnePay  +5%    Rs 525 │ surcharge shown (US-8.11)
│  [○ Cash on Delivery]    │ package only (US-20.8)
│  ┌────────────────────┐  │
│  │  Confirm           │  │
│  └────────────────────┘  │
╰──────────────────────────╯
```
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| Method rows | `RadioButton`+`Text` | `Picker`/`List` | Cash/LankaQR/OnePay/COD |
| Surcharge note | `Text` `warning` | `Text` | "+5% Rs 25" (US-8.11) |

**States:** OnePay shows recomputed total. **Anim:** total recompute count-up.

### SCR-PA-017 / SCR-PI-017 · `payment_pay` — Pay fare · **[REPLACE]** (NY Juspay PaymentPage → native)
Post-trip in-app pay. **LankaQR** → a **"Pay" button (deep link)** that opens the passenger's bank app
with amount + merchant ref pre-filled (AL-15, US-8.10a); **scannable QR shown only as a fallback** when no
compatible app is installed. **OnePay** → in-app payment sheet/redirect; on failure → retry or **switch to
Cash** (US-8.15) without losing history.
| Component | Compose | SwiftUI | Content | State machine |
|---|---|---|---|---|
| LankaQR Pay | `Button`(deep link) + QR fallback | `Button` + QR fallback | open bank app (AL-15) | §11.8 |
| OnePay sheet | `ModalBottomSheet`/Custom Tab | `.sheet`/`SFSafariVC` | card entry | Initiated→Pending→Succeeded/Failed |
| Retry / cash | `Button`s | `Button`s | fallback | →FellBackToCash |

**States:** Pending → "Awaiting confirmation (90s)"; Succeeded → check + receipt; Failed → retry/cash.
**Anim:** success checkmark, haptic success.

### SCR-PA-018 / SCR-PI-018 · `trip_summary` — Trip summary · **[KEEP]** (NY rider-ride-completed, drop tip/India charges)
Hero fare, route map snapshot, distance, fare breakdown, payment status, rating prompt.
| Field | Source | | Component A/I |
|---|---|---|---|
| Total fare (Rs) | fare-svc | | `Text` display |
| Distance | computed | | `Text` |
| Breakdown | fare-svc | | expandable `Card` / `DisclosureGroup` |
| Payment status | fare-svc | | status pill |
| Rate prompt | — | | → SCR-*-019 |

**States:** PaymentPending → "Pay now" CTA; Paid/CashSettled → receipt. **Anim:** hero fade-in.

### SCR-PA-019 / SCR-PI-019 · `rate_driver` — Rate driver · **[ADAPT]** (NY RatingCard, keep stars+comment, drop tip)
1–5 stars → reason chips → optional **text comment** (US-18.1). **Compose:** row of `Icon` stars +
`FilterChip` + `OutlinedTextField`. **SwiftUI:** star `Button`s + `TextField`. **States:** submit
loader. **Anim:** star tap scale + haptic.

### SCR-PA-020 / SCR-PI-020 · `package_track_sender` — Package tracking (Sender) · **[NEW]** (US-20.7, P-07)
Live driver position + **status bar** (Pickup Pending→Picked Up→In Transit→Delivered) + **Pickup OTP
display**.
```
┌──────────────────────────┐
│   MAP: driver ▲          │
│ Status: ●─●─○─○          │ stepper
│ Pickup OTP:  4 8 2 9     │ shown to sender (P-07)
│ 👤 Driver · Tuk · ★4.7   │
│ [📞 Call rider/driver]   │
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| Status stepper | custom `Row` | custom `HStack` | 4 states (US-20.7) |
| **Pickup OTP** | `Text` mono | `Text`.monospaced | 4-digit (P-07, US-20.4) |
| Map | MapLibre | MapLibre | live driver |

**States:** status updates via FCM. **Anim:** step fill.

### SCR-PA-021 / SCR-PI-021 · `package_track_recipient` — Package tracking (Recipient) · **[NEW]** (US-20.5, P-09)
Reached via FCM (registered) or **SMS web-share token** (unregistered). Live driver + status + **Delivery
OTP display** + driver details.
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| **Delivery OTP** | `Text` mono | `Text`.monospaced | 4-digit (P-07, US-20.5) |
| Status stepper | custom | custom | same 4 states |
| Driver card | `Card` | `GroupBox` | name, vehicle, ETA |

**States:** web-share fallback view for unregistered (no app chrome). **Anim:** step fill.

## Cluster: history / private transport / settings / support

### SCR-PA-022 / SCR-PI-022 · `trip_history` — Trip & schedule history · **[KEEP]** (US-8.7, 20.11)
Tabs: **Past · Scheduled · Packages**. List of cards (date, route, distance, fare, status).
**Compose:** `TabRow` + `LazyColumn` of `Card`; pagination. **SwiftUI:** `Picker(.segmented)` + `List`
+ `.refreshable`. **States:** empty → illustration; Loading → shimmer; infinite scroll. → SCR-*-023.

### SCR-PA-023 / SCR-PI-023 · `trip_details` — Trip details · **[KEEP]**
Map snapshot + fare breakdown + invoice/receipt + Report issue + Support. **States:** receipt download.

### SCR-PA-024 / SCR-PI-024 · `mode_b_request` — Private transport access request · **[NEW]** (US-4.5)
Enter **Vehicle ID** → send request. **Compose:** `OutlinedTextField` + CTA. **SwiftUI:** `Form` +
`TextField`. **States:** Pending/Accepted/Rejected chip (US-4.6). **Anim:** submit.

### SCR-PA-025 / SCR-PI-025 · `mode_b_manage` — My private subscriptions · **[NEW]** (US-NEW.1)
List of active Mode B grants + **Unsubscribe** per row (US-NEW.1; revocation push D-22). **Compose:**
`LazyColumn`+`ListItem`+`OutlinedButton`. **SwiftUI:** `List`+`.swipeActions`. **States:** first month
free badge (US-4.7).

### SCR-PA-026 / SCR-PI-026 · `saved_addresses` + `add_address` — Saved addresses · **[ADAPT]** (Epic 22, US-22.1/22.2; AL-14)
Save **Home & Work by selecting on the OSM map** (drop/drag pin, reverse-geocoded) + labelled addresses
("Save Address As", Address Line 1/2/3); edit/delete. **Compose:** `LazyColumn` + add `ModalBottomSheet`. **SwiftUI:** `List` + `.sheet`.

### SCR-PA-027 / SCR-PI-027 · `profile_settings` — Profile & settings · **[ADAPT]** (Epic 22)
User ID, name, photo, language, notification prefs (US-10.7), **Save Home & Work**, **Saved Addresses**,
**Default Payment Method** (Cash default / LankaQR / OnePay — US-22.4, AL-14), **Help & Support** (US-22.5),
logout (US-1.7), **delete account** (US-1.8 PDPA). **Compose:** `Scaffold`+`LazyColumn`. **SwiftUI:** `Form`+`Section`.
**States:** tapping the **profile card** opens **Edit profile (SCR-PA-027b)**; delete → confirm dialog.

### SCR-PA-027b / SCR-PI-027b · `edit_profile` — Edit profile · **[NEW]** (US-1.5, US-10.7, US-12.1; change619 #3)
Reached from the SCR-PA-027 profile card. Lets the user: (a) tap **avatar** → **take a new photo or upload
from gallery** (+ crop); (b) edit **Full name**; (c) change **app language** (English / සිංහල / தமிழ்);
(d) toggle **notification preferences** (US-10.7); (e) **add / edit SOS (emergency) contacts** — consumed by
SCR-PA-029 SOS (`POST /v1/sos`, which 400s `no-emergency-contact` when none set).
```
┌──────────────────────────┐
│ ‹ Edit profile     Save  │
│        ( 👤 📷 )         │ avatar → camera/gallery + crop
│        Take/upload       │
│ Full name [___________]  │ US-1.5
│ Language (En)(සි)(த)     │ SegmentedButton
│ Notifications & offers ▣ │ Switch (US-10.7)
│ ── SOS contacts ──       │
│ 👤 Amma · +94 77…    ✎   │ list row
│ [ + Add SOS contact ]    │ US-12.1
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Interaction |
|---|---|---|---|---|
| Avatar editor | `Box`+cam `IconButton` | `PhotosPicker` / `Menu` | photo | camera/gallery → crop → `PUT /v1/users/me {photoUrl}` |
| Full name | `OutlinedTextField` | `TextField` | name | validate → `PUT /v1/users/me {firstName}` |
| Language | `SegmentedButton` (En/Si/Ta) | `Picker(.segmented)` | locale | hot-swap → `{language}` |
| Notifications | `Switch` | `Toggle` | prefs (US-10.7) | `{notifPrefs}` |
| SOS contacts | `LazyColumn` + add row | `List` + add | emergency contacts | add/edit/delete (feeds `POST /v1/sos`) |

**States:** Loading on Save; inline validation (name; +94 phone for SOS); avatar upload progress; empty SOS
list → "Add a contact" prompt. Language change hot-swaps locale app-wide. **Anim:** avatar crop sheet;
segmented haptic.

### SCR-PA-028 / SCR-PI-028 · `voip_call` — In-app VoIP call · **[NEW]** (US-6A.16)
Full-screen call UI: avatar, name, timer, mute/speaker/end; **no phone number shown**. **Android:**
WebRTC + `ConnectionService`. **iOS:** **CallKit** `CXProvider` + WebRTC. **States:** Connecting/
Connected/Ended; fallback masked-SMS if VoIP fails (D-25). **Anim:** ripple; CallKit native UI (iOS).

### SCR-PA-029 / SCR-PI-029 · `sos` — SOS · **[ADAPT]** (NY NammaSafety, drop Aadhaar)
Large red SOS button → sends GPS + trip to emergency contact via SMS (US-12.1); confirm + countdown.
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| SOS button | big `Button` `error` | `Button`.tint(.red) | trigger |
| Contacts | `LazyColumn` | `List` | emergency contacts |

**States:** Active → pulsing red + "Sending…"; sent confirmation (SOS ≤5s D-33). **Anim:** pulse;
strong haptic `.notification(.error)`.

### SCR-PA-030 / SCR-PI-030 · `support` + `ticket_thread` — In-app support · **[ADAPT]** (NY help+chat)
FAQ accordion (US-16.1) + **Raise a ticket** (opens **modal sheet SCR-PA-030a**) + ticket list/thread (US-16.2).
**Compose:** `LazyColumn` expandable FAQ + chat `LazyColumn`. **SwiftUI:** `List`+`DisclosureGroup` +
chat. **States:** ticket status chip.

### SCR-PA-030a / SCR-PI-030a · `raise_ticket` — Raise a ticket (modal sheet) · **[NEW]** (US-16.2; change619 #4)
Modal bottom sheet launched from the SCR-PA-030 "Raise a ticket" button: **issue description** (multiline),
**dropdown to select a past Trip ID**, **attach a screenshot**, Submit.
```
┌──────────────────────────┐
│ ░░░ Support (scrim) ░░░   │
│ ╭──────────────────────╮ │ ModalBottomSheet
│ │ ▁ Raise a ticket     │ │
│ │ Issue description    │ │
│ │ [__________________] │ │ multiline
│ │ Related trip  ▾      │ │ past Trip ID dropdown
│ │ [ 📎 Attach screenshot]│ │
│ │ [   Submit ticket   ] │ │
│ ╰──────────────────────╯ │
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Interaction |
|---|---|---|---|---|
| Sheet | `ModalBottomSheet` | `.sheet(.presentationDetents([.medium]))` | container | dismiss / drag |
| Description | `OutlinedTextField` multiline | `TextField(axis:.vertical)` | issue text | required |
| Trip ID dropdown | `ExposedDropdownMenuBox` | `Picker` / `Menu` | past trips (`GET /v1/rides`) | optional attach |
| Attach screenshot | `Button`→`PhotoPicker` | `PhotosPicker` | image | upload → `screenshotFileId` |
| Submit | CTA | `.borderedProminent` | create ticket | `POST /v1/support/tickets {category,description,tripId?,screenshotFileId?}` |

**States:** Submit → Loading → new ticket prepended to "Your tickets" (US-16.2); validation if description
empty; attach upload progress; error → retry snackbar. **Anim:** sheet slide-up.

### SCR-PA-031 / SCR-PI-031 · `app_update` — App update prompt · **[NEW]** (US-17.1,17.2)
On gateway `426`: **mandatory** = non-dismissible dialog → Store; **soft** = dismissible banner.
**Compose:** `AlertDialog` (mandatory) / `Snackbar` (soft). **SwiftUI:** `.alert` / banner. **Anim:**
dialog scale-in.

### SCR-PA-032 / SCR-PI-032 · `offline_banner` — Offline state · **[ADAPT]** (resolves NY no-internet [UNVERIFIED])
Top banner "Connection lost — showing last known" (US-15.6); current screen preserved (not full
takeover). **Compose:** `Snackbar`/sticky `Surface`. **SwiftUI:** safe-area banner. Auto-clears on
reconnect < 5s (US-15.4).

---

# SECTION B — DRIVER APP (Compose: `SCR-DA-###` · SwiftUI: `SCR-DI-###`)

## Cluster: auth / onboarding / registration

### SCR-DA-001 / SCR-DI-001 · `splash` · **[KEEP]** — boot + driver-info route (same pattern as SCR-PA-001).

### SCR-DA-002 / SCR-DI-002 · `onboarding_lang_city` · **[ADAPT]** — language as **vertical boxes (Sinhala first & default)**, then Tamil, English; **operating-city radio list loaded from `config.operating_cities`** via `GET /config/cities` (admin-managed, active rows only) — selection persists to `iam.users.operating_city_code` (Change 6/22). First run only.

### SCR-DA-003 / SCR-DI-003 · `login_phone`+`login_otp` · **[ADAPT]** — +94 phone + SMS-gateway OTP,
**Phone-OTP only, no Google Sign-In** (US-11.5). Identical layout to SCR-PA-003.

### SCR-DA-003a / SCR-DI-003a · `profile_setup` — Driver profile setup (PRIMARY) · **[NEW]** (Change 6/22)
First profile right after OTP; **precedes Home, no vehicle required**. Avatar-with-camera (**photo
required**), **driver name** field, **driving license front + back** capture tiles + Gemini-extract card.
```
┌──────────────────────────┐
│      ( 📷 avatar )        │ profile photo · REQUIRED
│ Driver name: __________  │
│ Driving license          │
│ [📷 front] [📷 back]      │
│ ✦ AI: License no · Expiry│ Gemini Flash + redaction (US-2.4)
│ [   Save & continue   ]  │ → permissions → dashboard
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Source |
|---|---|---|---|---|
| Avatar+camera | `Box`+`IconButton` | `PhotosPicker` | **required** profile photo (US-2.12) | docs/registry |
| Name field | `TextField` | `TextField` | driver_profiles.display_name | registry-svc |
| License tiles | Camera capture ×2 | `Camera` ×2 | DL front/back → `registry.documents(kind='driving_license', vehicle-less)` | ocr-svc |
| Extract card | editable fields | `Form` | license no + expiry (US-2.10 editable) | Gemini Flash |
| Save | CTA | `.borderedProminent` | `PUT /v1/drivers/profile` → permissions |

**States:** photo missing → Save disabled; OCR low-confidence → editable manual note (US-2.10). **A vehicle is not required to reach Home.**

### SCR-DA-004 / SCR-DI-004 · `vehicle_onboard_step1` — Vehicle Onboarding · Step 1/4 (Mode C) · **[REPLACE]** (Change 6/22)
**Optional, in-app, Mode-C-only** wizard, entered from the My Vehicles empty-state popup (SCR-DA/DI-026a)
or the nav drawer. Step 1 captures **vehicle type + Registration No**. **No permit / GPS-tracker field**
(permit = Mode A → Fleet Portal). Steps 2–4 capture the docs; Submit runs Gemini Flash 3.0 auto-verify.
```
┌──────────────────────────┐
│ ●──○──○──○  Step 1/4 · C │ progress 25%
│ Vehicle type:  Sedan  ▾  │ canonical 10 (AL-09)
│ Registration No: ABC-1234│ unique active set (D-37)
│ ⓘ Mode A/B + permits =   │
│   Fleet Portal           │
│ [  Continue · Insurance ]│ → Step 2/4
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Source |
|---|---|---|---|---|
| Progress | `LinearProgressIndicator` | `ProgressView` | 1/4 … 4/4 | local |
| Type picker | `ExposedDropdownMenu` | `Picker` | motorbike/three_wheeler/flex/sedan/mini_van/van/truck/mini_truck (Mode C; **no bus/train**) | local |
| Reg No | `TextField` | `TextField` | unique in active set (D-37) | registry-svc |
| Continue | CTA | `.borderedProminent` | → Step 2/4 |

**States:** reg-no duplicate → inline error (409 D-37); back exits the wizard.

### SCR-DA-004a / SCR-DI-004a · `vehicle_onboard_insurance` — Step 2/4 · Insurance · **[NEW]** (Change 6/22)
Single capture tile (insurance card/paper) + **Done ✓** chip on upload + Gemini-extract card (**expiry date**).
| Component | Compose | SwiftUI | Content | Source |
|---|---|---|---|---|
| Capture tile | `Box`+`IconButton` Camera | `Camera`/`PhotosPicker` | insurance doc → `registry.documents(kind='insurance')` | ocr-svc |
| Done chip | status `Badge` | `.badge` | **Done ✓** on upload | local |
| Extract card | editable field | `Form` | **expiry date** (US-2.10 editable) | Gemini Flash 3.0 |

**States:** uploading → progress → **Done**; expiry extracted → Insurance **Verified** on SCR-DA/DI-006, else Pending.

### SCR-DA-004b / SCR-DI-004b · `vehicle_onboard_revenue` — Step 3/4 · Revenue licence · **[NEW]** (Change 6/22)
Same pattern; extract card shows **licence no + expiry date**. Mandatory per vehicle (US-2.20).
**States:** Done on upload; licence no + expiry extracted → Revenue **Verified** on SCR-DA/DI-006, else Pending.

### SCR-DA-004c / SCR-DI-004c · `vehicle_onboard_photos` — Step 4/4 · Vehicle photos · **[NEW]** (Change 6/22)
Two capture tiles (**front + back, number plate visible**) + **Done ✓** + **Submit for review** CTA.
| Component | Compose | SwiftUI | Content | Source |
|---|---|---|---|---|
| Photo tiles | Camera ×2 | `Camera` ×2 | front/back → `registry.documents` photos | ocr-svc |
| Submit | CTA | `.borderedProminent` | `POST /v1/vehicles` (Mode C) → SCR-DA/DI-006 | registry-svc |

**States:** both required; on Submit → Gemini Flash 3.0 analyses all 4 docs; plate OCR matches Reg No → photos **Verified**, else Pending.

### SCR-DA-006 / SCR-DI-006 · `vehicle_onboard_status` — Vehicle onboarding status · **[REPLACE]** (Change 6/22)
**4-document verdict** list (Vehicle details · Insurance · Revenue licence · Front & back photos), each
**Verified / Pending** from Gemini Flash 3.0 (`GET /v1/vehicles/{id}/onboarding-status`).
```
┌──────────────────────────┐
│ ⏳ Sedan · ABC-1234       │ vehicle under review
│ Vehicle details  Verified│
│ Insurance        Verified│
│ Revenue licence  Verified│
│ Front & back     Pending │ ← plate unreadable
│ ⚠ 1 pending → officer    │
└──────────────────────────┘
```
**States:** all 4 Verified → **vehicle status APPROVED automatically (no Verification Officer step)** → appears in My Vehicles (SCR-DA/DI-026); any Pending → Verification Officer queue (US-2.10); rejected → re-upload + reason (US-2.15). Approval via FCM/APNs (US-2.14).

### SCR-DA-007 / SCR-DI-007 · `permission` — Permissions · **[ADAPT]** `[DELTA:PLATFORM]`
Location **(Always/Background)**, notifications, battery-exempt, overlay. **Android:** ACCESS_BACKGROUND
_LOCATION + FOREGROUND_SERVICE_LOCATION + POST_NOTIFICATIONS + battery-optimization intent. **iOS:**
`requestAlwaysAuthorization` + notification auth. **States:** denied → Settings deep-link.

## Cluster: dashboard / go-online / lifecycle

### SCR-DA-010 / SCR-DI-010 · `dashboard` — Driver Dashboard Home (PRIMARY) · **[REPLACE]** (map+mode-aware)
Full-bleed MapLibre + **status header** (earnings, **daily-fee chip**, wallet, **Driver Level badge**)
+ mode-aware overlay (Mode A Start/End Journey · Mode C Standby toggle) + bottom nav.
```
┌──────────────────────────┐
│ [≡] L3● ★4.8  Rs 1,240   │ level badge + rating + wallet
│ Daily fee: PAID Rs100 ✓  │ daily-fee chip (US-9.7)
│      MAPLIBRE MAP        │ self + demand
│            ⊕ recenter    │
│ ╭──────────────────────╮ │
│ │  ◉ ONLINE (Mode C)   │ │ standby toggle (US-6A.1)
│ │  Live: 3W · ABC-1234  │ │ active vehicle reg (US-9.6, SCR-026)
│ │  First trip FREE today│ │ first-trip-free (US-9.1)
│ │  [⮕ Directional ▸]    │ │ → SCR-*-013
│ │  Today: 4 trips · Rs… │ │ stats
│ ╰──────────────────────╯ │
│ [Home][Jobs][Wallet][≡] │ NavigationBar
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Source | Update |
|---|---|---|---|---|---|
| Map | MapLibre | MapLibre | self+demand | GPS/SignalR | live |
| **Daily-fee chip** | `AssistChip` | `Label` | PAID/UNPAID, Rs rate (US-9.7) | wallet-svc | per day |
| **Level badge** | `Badge` "L3" | `Text`.badge | Driver Level (US-6A.14) | reputation | event |
| Wallet | `Text` | `Text` | balance (read-only US-9.7) | wallet-svc | event |
| Online toggle | `Switch`/big `Button` | `Toggle`/`Button` | Online/Offline | dispatch | tap |
| First-trip-free | `Text` `success` | `Text` | indicator (US-9.1) | computed | per day |
| **Live vehicle** | `AssistChip` | `Label` | active vehicle reg no selected in SCR-*-026 (US-9.6) | registry-svc | on switch |
| Directional entry | `Button` | `Button` | → SCR-*-013 | — | tap |
| Stats | `Row` | `HStack` | trips/earnings | earnings | live |

**States:** Offline → grey overlay + "Go online to receive rides"; **wallet insufficient for 2nd trip**
→ warning (US-9.1, low-balance < Rs 200 US-9.9); Loading → shimmer stats; Mode A vehicle → shows
**Start/End Journey** instead of standby (SCR-*-011). **Anim:** toggle color transition, ride-assigned
audio + haptic.

### SCR-DA-011 / SCR-DI-011 · `mode_a_session` — Start/End Journey (Mode A) · **[NEW]** (US-5.1–5.6)
Route selector + big **Start Journey** / **End Journey** + live session timer/distance + auto-end-at-
destination toggle (US-5.4).
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| Route select | `ExposedDropdownMenu` | `Picker` | bus route no |
| Start/End | big CTA (success/error) | `.borderedProminent` | session toggle |
| Timer/distance | `Text` | `Text` | live (US-5.6) |
| Auto-end | `Switch` | `Toggle` | 100m geofence (US-5.4) |

**States:** active session; idle>30min auto-end notice (US-5.3/5.9) + 5-min grace restart (US-5.10).
**No fee** (Mode A free). **Anim:** timer tick.

### SCR-DA-012 / SCR-DI-012 · `standby_toggle` — Standby (Mode C) · **[MERGED → SCR-DA/DI-010]** (US-6A.1, re-tagged 2026-07-05 item 8)
(Embedded in dashboard overlay; documented as a state — **do not build as a separate screen**.) Online/Offline + first-trip-free chip +
Directional entry chip showing active filter status. Going offline clears Directional (US-6A.19).

### SCR-DA-013 / SCR-DI-013 · `directional_travel` — Directional Travel filter · **[NEW]** (US-6A.17–23, DT-08)
Set destination (search / map pin / **Home**) + **remaining daily uses** + **max duration** + map
preview with **heading marker** + Set/Turn-Off + **persistent active banner**.
```
┌──────────────────────────┐
│ ‹ Directional Travel     │
│ Destination: [search/Home]│
│   MAP: ➤ heading marker  │
│ Uses left today: 1 of 2  │ (US-6A.18)
│ Max duration: 2h         │
│ ┌──────────────────────┐ │
│ │  Set Direction       │ │ consumes 1 use
│ └──────────────────────┘ │
│ ── when ACTIVE ──        │
│ ⮕ To: Nugegoda · 1:42 left│ persistent banner (US-6A.21)
│ Uses left: 1 · [Turn Off]│ (US-6A.19 still consumes use)
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Source |
|---|---|---|---|---|
| Destination | `OutlinedTextField`/map | `TextField`/map | dest/Home | dispatch-svc |
| **Uses left** | `Text` | `Text` | "1 of 2" (US-6A.18) | dispatch-svc |
| Heading marker | MapLibre `Symbol` | MapLibre | ➤ direction | computed |
| Set | CTA | `.borderedProminent` | `POST /standby/directional` (DT-01) |
| **Active banner** | sticky `Card` | sticky banner | dest+time+uses (US-6A.21, DT-08) | local |
| Turn Off | outlined | `.bordered` | consumes a use (US-6A.19) |

**States:** Inactive (set form) / Active (persistent banner); **pre-expiry reminder 10 min** push
(US-10.14, DT-08) → banner pulse; uses exhausted → Set disabled. **Anim:** banner countdown; heading
marker rotate.

### SCR-DA-014 / SCR-DI-014 · `incoming_request` — Incoming dispatch (PRIMARY) · **[REPLACE]** (NY RideAllocationModal, 15s)
Full-screen takeover with **15s countdown ring**, fare, pickup distance, vehicle cat, payment, source→
dest, **Accept/Reject**, and **badges**: "Third-party booking" (P-05), "Package" + size (US-20.3),
"Directional" (DT-08).
```
┌──────────────────────────┐
│        ◜15◝ countdown    │ ring
│ [🏷 Third-party booking]  │ proxy badge (P-05)
│ [📦 Package · M]          │ package badge+size (US-20.3, P-06)
│ [⮕ Directional]          │ directional badge (DT-08)
│ Rs 480 · Cash            │ fare + payment
│ ● Pickup · 1.2 km away   │
│ ◆ Drop · Nugegoda        │
│ ┌─────────┐┌───────────┐ │
│ │ Reject  ││  Accept ▸ │ │ 15s (US-6A.3)
│ └─────────┘└───────────┘ │
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Source |
|---|---|---|---|---|
| Countdown ring | `CircularProgressIndicator` | `Circle().trim` | 15s (US-6A.2/3) | local timer |
| **Proxy badge** | `Badge` | `Label` | "Third-party booking" (P-05) | offer payload |
| **Package badge** | `Badge` | `Label` | "Package · S/M/L" (US-20.3) | offer payload |
| **Directional badge** | `Badge` | `Label` | "Directional" (DT-08) | offer payload |
| Fare/payment | `Text` | `Text` | Rs + Cash/Card | offer |
| Accept | big CTA / slide | `.borderedProminent` | atomic accept (R-02) |
| Reject | outlined | `.bordered` | next driver |

**States:** counting-down (ring shrinks, last 5s pulse red); 2nd-trip → fee-deduction note (US-9.1);
expired → auto-dismiss; accepted → 409/410 "offer taken" → next. **Anim:** ring, slide-to-accept,
strong haptic on arrival. **Wakes app** via FCM-hi/APNs-silent (E-01).

### SCR-DA-015 / SCR-DI-015 · `ride_active` — Active ride/trip · **[REPLACE]** (NY RideActionModal)
Navigation map + source/dest + pickup/total distance + **Start (OTP)** / **End** CTAs + Call(rider)/
SOS. For packages → **Delivery Confirmation** sheet (SCR-*-016).
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| Start ride | big CTA `success` | `.borderedProminent` | enter rider/pickup OTP |
| End ride | big CTA `error` | `.borderedProminent`.red | complete |
| Call | `IconButton` `phone.fill` | `Button` | VoIP **rider** not booker (P-05) |
| SOS | `IconButton` | `Button` | driver SOS (US-12.8) |
| Navigate | `Button` map | `Button` | MapLibre nav |

**States:** Accepted→DriverArrived→InProgress→Completed; offline replay indicator (R-17). **Anim:**
status transitions; arrival haptic.

### SCR-DA-016 / SCR-DI-016 · `delivery_confirm` — Package fulfilment · **[NEW]** (US-20.4,20.6, P-07)
**Pickup OTP entry** (at pickup), **Delivery OTP entry** (at drop) **or photo proof** if recipient
absent, + **COD "Cash received"** (US-20.8, P-08).
```
╭──────────────────────────╮
│  ═ Confirm pickup        │
│  Enter Pickup OTP        │
│   ▢ ▢ ▢ ▢                │ (P-07, US-20.4)
│  [ Verify ]              │
│  ── at delivery ──       │
│  Enter Delivery OTP ▢▢▢▢ │ (US-20.6)
│   or [📷 Photo proof]     │ (recipient absent)
│  [💵 Cash received (COD)] │ (US-20.8)
╰──────────────────────────╯
```
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| **OTP entry** | `BasicTextField`×4 | `TextField`.oneTimeCode | 4-digit (P-07) |
| Verify | CTA | `.borderedProminent` | `POST …/pickup-otp` / `/delivery-otp` |
| **Photo proof** | `Button`→Camera | `Button`→`Camera` | proof artifact (P-10) |
| COD collect | `Button` | `Button` | "Cash received" (P-08) |

**States:** OTP mismatch (max 5 → admin queue, P-07); COD uncollected 24h → Disputed (P-14). **Anim:**
OTP box fill; success check + haptic.

## Cluster: jobs / level / earnings / wallet

### SCR-DA-017 / SCR-DI-017 · `job_board` — Job Board · **[NEW]** (US-6A.5)
**All future scheduled rides** within **30 km** — **post intent only** (no direct accept on the board). At **T-30 min** the system dispatches the ride to the closest intent-poster (by Driver Level) as a normal offer on the **dispatch screen (SCR-DA-014)**, where it is accepted. Level-gated (L1 excluded US-6A.8).
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| Job card | `Card` | `GroupBox` | time, route, fare, distance |
| Post intent | `Button` | `Button` | submit intent (only action on the board) |
| Intent posted | status `Badge` | `Text`.badge | "Intent posted ✓" after submit |

**States:** L1 → "Reach Level 2 to access Job Board" (US-6A.8); empty → "No jobs within 30 km";
shimmer loading; expired job → overlay. **No accept here** — acceptance happens on SCR-DA-014 at T-30 min. **Anim:** card expire fade.

### SCR-DA-018 / SCR-DI-018 · `scheduled_rides` — Scheduled rides · **[NEW]**
Accepted scheduled rides list; 30-min reminder (US-6A.15) push deep-links here. **Compose:**
`LazyColumn`. **SwiftUI:** `List`.

### SCR-DA-019 / SCR-DI-019 · `driver_level` — Driver Level & stats · **[NEW]** (US-6A.6,6A.14)
**Level badge (L1/L2/L3) + points progress** + acceptance rate + no-show history.
```
┌──────────────────────────┐
│      ◆ Level 3           │ badge
│  ▓▓▓▓▓░░ 510/500 pts → L4│ progress (US-6A.6)
│  Acceptance rate: 92%    │ (US-6A.14)
│  No-shows: 1             │
│  ⚠ 3 reports → delisting │ (US-6A.6)
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Source |
|---|---|---|---|---|
| Level badge | `Badge`/`Card` | `Text`.badge | L1/L2/L3 | reputation-svc |
| Points bar | `LinearProgressIndicator` | `ProgressView` | pts to next (100×5★=500=1lvl) | reputation |
| Stats | `Row` | `HStack` | accept rate, no-shows | reputation |

**States:** near-delisting warning (3 reports). **Anim:** progress fill.

### SCR-DA-020 / SCR-DI-020 · `earnings` — Earnings dashboard · **[ADAPT]** (NY driver-earnings, drop coins)
**Today / Week / Month** tabs: earnings, per-trip breakdown, payment-method stats, **daily fee
deducted** (US-9.22), fares received.
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| Period tabs | `TabRow` | `Picker(.segmented)` | today/week/month |
| Chart | `Canvas`/Vico | Swift Charts | earnings trend |
| Trip rows | `LazyColumn` | `List` | per-trip fare (US-8.8) |
| Fee summary | `Card` | `GroupBox` | daily fee paid (US-9.22) |

**States:** empty period; loading shimmer. **Anim:** chart draw.

### SCR-DA-021 / SCR-DI-021 · `wallet_fee` — Wallet & fee status (PRIMARY) · **[REPLACE]** (NY Juspay subscription → wallet)
**Wallet balance (read-only)** + **today's daily-fee status** (paid/unpaid, amount, vehicle rate,
first-trip-free) + **Top Up / Buy credit** + **Request credit** + **Transfer credit**.
```
┌──────────────────────────┐
│  Wallet                  │
│   Rs 1,240               │ balance (read-only US-9.7)
│  ── Daily fee ──         │
│  Vehicle: Three-wheeler  │
│  Rate: Rs 100/day        │ (US-9.7)
│  Today: PAID ✓ (1st free)│ first-trip-free (US-9.1)
│  ┌────────┐┌───────────┐ │
│  │Top Up/ ││  Request  │ │ (US-9.18 / US-9.10)
│  │ Buy    ││  credit   │ │
│  └────────┘└───────────┘ │
│  [Transfer credit]       │ (US-9.20/9.21)
│  [Payment history ›]     │
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content | Source | Update |
|---|---|---|---|---|---|
| Balance | `Text` display | `Text` | Rs (read-only) | wallet-svc | event |
| **Daily-fee card** | `Card` | `GroupBox` | rate, paid/unpaid, first-free (US-9.1/9.7) | wallet-svc | per day |
| Top Up / Buy credit | CTA | `.borderedProminent` | → SCR-*-022 | — | — |
| Request credit | outlined | `.bordered` | → SCR-*-023 (by Driver ID) | — | — |
| Transfer credit | `Button` | `Button` | → SCR-*-024 (requests + send by Driver ID) | — | — |

**States:** balance ≤ driver-set threshold (default Rs 200) → low-balance warning (US-9.9); below one day's
fee → "Top Up Required" banner (US-9A.7; balance may go negative on reversal/cancellation). **Anim:** balance count-up on credit.

### SCR-DA-022 / SCR-DI-022 · `wallet_topup` — Top Up Wallet · **[REPLACE]** (payment hard rule)
In-app **Card / OnePay / LankaQR** + **bulk credit vouchers** (Rs 1k/2k/3k/5k/10k; **per-tier purchase discount configured in DB**, applied at purchase → credited to wallet, e.g. pay 900 → 1,000). **No bank
transfer (AL-05); no web portal — everything in-app.**
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| Method | `RadioButton`s | `Picker` | Card / OnePay / LankaQR (US-9.18) |
| Amount | `OutlinedTextField` | `TextField` | Rs |
| Voucher tiles | `LazyRow` `Card` | `ScrollView` `HStack` | 1k–10k, DB-configured discount %, credited to wallet at purchase (US-9.19) |
| Pay | CTA | `.borderedProminent` | OnePay sheet / LankaQR Pay deep link (AL-15) |

**States:** Processing → spinner; Success → receipt + count-up; Failed → retry. **Anim:** success check.

### SCR-DA-023 / SCR-DI-023 · `request_credit` — Request credit (Driver ID) · **[NEW]** (US-9.10)
Enter another **driver's Driver ID** **or scan their QR** → request a credit transfer; that driver approves (**exact value, no commission**, US-9.13). No special reseller codes.
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| QR scanner | CameraX + ML Kit | `DataScannerViewController` | scan driver QR |
| Driver ID | `OutlinedTextField` | `TextField` | manual entry |
| Amount | `OutlinedTextField` | `TextField` | Rs |
| Request | CTA | `.borderedProminent` | `POST` credit-transfer request |

**States:** Requested → "Awaiting driver approval"; Approved → credit notification (US-9.17), **exact value** credited;
Rejected → notice. **Anim:** QR scan reticle; success haptic.

### SCR-DA-024 / SCR-DI-024 · `credit_transfer` — Credit transfer + requests · **[NEW]** (US-9.11,9.12,9.20,9.21)
Incoming credit-transfer **requests via push** (approve/reject) **and** send credit directly to another driver by **Driver ID** (**no commission**, exact value). **No voucher redeem** — bulk vouchers credit the wallet at purchase. **Compose:** request `Card`s + `OutlinedTextField` (Driver ID/amount). **SwiftUI:** `List` + `Form`.
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| Incoming requests | `Card` + approve/reject | `List` row + actions | requester name, vehicle, amount (FCM/APNs) |
| Driver ID | `OutlinedTextField` | `TextField` | recipient by Driver ID |
| Send credit | CTA | `.borderedProminent` | exact value, no commission |

**States:** incoming requests arrive via push; approve debits sender / credits requester; insufficient → blocked. **Anim:** ledger animation.

### SCR-DA-025 / SCR-DI-025 · `payment_history` — Payment/fee history · **[ADAPT]** (US-9A.6)
List of daily-fee deductions, top-ups, transfers (date, vehicle, amount, trips). **Compose:**
`LazyColumn`+filter. **SwiftUI:** `List`+`.searchable`. Statement download (US-9A.19). **States:**
date-range filter, empty.

## Cluster: vehicle mgmt / sharing / trackers / profile / support

### SCR-DA-026 / SCR-DI-026 · `vehicle_mgmt` — Vehicle management & switcher · **[ADAPT]** (US-2.8,2.16, US-9.6, US-13.9)
Top label **"Only one vehicle can be live at a time"**. **My vehicles** list + **active switcher** + add + deactivate
(US-2.16) + registration status, followed by a separate **"Temporarily assigned to me" (FLEET)** group listing fleet-assigned vehicles the driver can **select and go online** with without owning them (US-13.9).
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| Vehicle rows | `LazyColumn`+`RadioButton` | `List`+`Picker` | active selection (my vehicles) |
| Status chip | `AssistChip` | `Label` | Pending/Approved/Rejected (US-2.13) |
| Temp-assigned group | `LazyColumn` section | `List` section | fleet-assigned vehicles (assigning fleet + validity); select to go online (US-13.9) |
| Add | `FloatingActionButton` | `Button` | → **Vehicle Onboarding Step 1/4 (SCR-DA/DI-004)** |
| Deactivate | `OutlinedButton`/swipe | `.swipeActions` | remove (US-2.16) |

**States:** one-live lock ("Only one vehicle can be live at a time"); **temporarily assigned** fleet vehicles in a separate group, selectable to go online and auto-expiring (US-13.9); deactivate confirm.

#### SCR-DA-026a / SCR-DI-026a · empty state — onboard Mode C popup · **[NEW]** (Change 6/22)
When **My Vehicles is empty**, an `AlertDialog` (Android) / `.alert` (iOS) asks **"Onboard a Mode C
(Standby Vehicle)?"** — **Yes → SCR-DA/DI-004** (Step 1/4); **Not now** → empty list. Until a vehicle
(owned Mode C **APPROVED**, or shared / temporarily-assigned Mode A/B) appears, the driver **cannot go
online** (SCR-DA/DI-010, US-9.6).

### SCR-DA-027 / SCR-DI-027 · `tracker_pairing` — GPS tracker pairing · **[NEW]** (US-3.1,3.2, US-3.21–3.23, T-02/T-09)
Bind **IMEI** (enter / **scan device QR** / bind-code) to a vehicle; fleet **bulk-CSV** path noted as
**portal** (US-3.2). Includes a **hardware tracker behaviour** note: **Mode A / Mode B** tracker vehicles **auto start & end journeys on ignition** (ACC on/off — no mobile app needed, US-3.22/3.23); **Mode C** tracker GPS is **ingested only while the vehicle is Online** (US-3.21).
```
┌──────────────────────────┐
│ ‹ Pair GPS tracker       │
│ Vehicle: [Three-wheeler▾]│
│ IMEI [______________]    │
│ [▣ Scan device QR]        │
│ [ Enter bind code ]      │
│ ┌──────────────────────┐ │
│ │  Pair device         │ │
│ └──────────────────────┘ │
│ Fleet (5,000+)? Use the  │ (US-3.2 — portal)
│ Admin Portal CSV upload ›│
└──────────────────────────┘
```
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| IMEI field | `OutlinedTextField` | `TextField` | IMEI (US-3.1) |
| QR scan | CameraX+ML Kit | `DataScannerViewController` | device QR |
| Pair | CTA | `.borderedProminent` | provisioning-svc bind (T-02) |
| Fleet CSV link | `TextButton` | `Link` | → portal (US-3.2, T-09) |

**States:** Pairing → spinner; duplicate IMEI → quarantine notice (US-3.4, T-08); paired → cert-issued
confirmation. Tracker offline >15min → push (US-3.14). **Mode A/B tracker → ignition-driven auto start/end (no app); Mode C tracker → GPS accepted only when Online.** **Anim:** scan reticle.

### SCR-DA-028 / SCR-DI-028 · `sharing_mgmt` — Sharing management (Mode B) · **[ADAPT]** (US-4.1–4.4,4.7)
Share by **User ID** + set expiry + accept/reject incoming requests + current-grantee list — **both incoming requests and grantees show the passenger's name + mobile number** (and PAX ID).
| Component | Compose | SwiftUI | Content |
|---|---|---|---|
| Share form | `OutlinedTextField`+`DatePicker` | `Form`+`DatePicker` | User ID + expiry (US-4.2) |
| Requests | `LazyColumn` accept/reject | `List`+actions | incoming — passenger **name + mobile** + PAX ID (US-4.4) |
| Grantees | `LazyColumn` | `List` | who can track — **name + mobile** + PAX ID (US-4.7) |

**States:** request notification (US-10.2); auto-revoke expired (US-4.8). **Anim:** accept/reject swipe.

### SCR-DA-029 / SCR-DI-029 · `driver_profile` — Profile · **[KEEP]**
Personal/vehicle details + **overall rating + per-trip ratings** (US-18.3). **Compose:** `Scaffold`+
`LazyColumn`. **SwiftUI:** `Form`. → ride history, level, support.

### SCR-DA-030 / SCR-DI-030 · `ride_history` + `trip_details` + `rate_passenger` — History & rate · **[ADAPT]**
Completed/scheduled trips; trip detail shows **fare to driver** (US-8.8); **rate passenger** 1–5 stars
(US-18.2). **Compose:** `LazyColumn` + rating sheet. **SwiftUI:** `List` + `.sheet`.

### SCR-DA-031 / SCR-DI-031 · `voip_call` — VoIP (driver) · **[NEW]** (US-6A.16, P-05)
Same as passenger SCR-PA-028 but calls **rider not booker** for proxy (P-05). **iOS** CallKit.

### SCR-DA-032 / SCR-DI-032 · `sos` — SOS (driver) · **[NEW]** (US-12.8)
SOS during active trip → GPS+trip to emergency contact SMS. Same pattern as SCR-PA-029.

### SCR-DA-033 / SCR-DI-033 · `support` (+ `raise_ticket` modal SCR-DA-033a/SCR-DI-033a) — Support · **[ADAPT]** (US-16.2, US-9.23)
FAQ + raise/track ticket; **daily-fee refund request** (US-9.23, e.g. crash on Go Online). Tapping **"Raise a ticket"** opens a **modal bottom sheet** (`SCR-DA-033a` / `SCR-DI-033a`) containing an **Issue description** field, a **dropdown to select a past Trip ID**, and an **Attach screenshot** button → Submit (US-16.2). **Compose:** `ModalBottomSheet`. **SwiftUI:** `.sheet` (detent `.medium`).

### SCR-DA-034 / SCR-DI-034 · `notifications` — Alerts · **[KEEP]**
List of `NotificationCard`s (dispatch/fee/registration/sharing/directional) + detail. Shimmer loading.

### SCR-DA-035 / SCR-DI-035 · `no_internet` + `app_update` · **[ADAPT]**/**[NEW]**
Offline screen with **buffered-then-replayed GPS indicator** (R-17, US-15.1); app-update dialog (US-17.1/2).

---

# SECTION AP — ADMIN PORTAL (Responsive Web · `admin.mageride.lk` · `SCR-AP-###`) · all **[NEW]/[DERIVED]**

> **AL-02:** The old driver-facing "Wallet & Subscription Portal" is **removed** — drivers never use a web
> portal (all wallet/credit-transfer functions are in the Driver App). `admin.mageride.lk` is the **single
> back-office** for the six internal roles (Admin, Super Admin, Verification Officer, Support/CSR,
> Finance, Auditor), with **role-scoped menus** (deny-by-default RBAC, AL-06).

Next.js responsive web (`admin-portal`), styled exclusively with **Tailwind CSS** (shared `@mageride/tailwind-preset` carrying the §A tokens — AL-52). **Breakpoints:** mobile 375px / tablet 768px / desktop 1024px
(sidebar + content + detail). Si/Ta/En. **Auth: Password or Google Sign-In + MFA** (AL-07). No Phone-OTP/driver login.

### SCR-AP-001 · `admin_login` — Login · [DERIVED] (AL-07)
Email/password + **Google Sign-In** + **MFA (TOTP)** challenge for internal roles. Centered card (max-width 400px).

### SCR-AP-002 · `admin_home` — Role-scoped dashboard · [DERIVED] (AL-06)
Each internal user sees only the modules their role(s) permit (Verification Officer → onboarding queue only;
Auditor → read-only views, etc.). KPI cards + module nav.

### SCR-AP-003 · `verification_queue` — Onboarding/verification · [DERIVED] (US-2.9, US-13.A7)
AI-extracted driver docs / license / registration / **insurance** + **fleet-org approval** queue; approve/reject with reason.

### SCR-AP-004 · `moderation` — Moderation · [DERIVED] (US-12.6, US-14.3)
Suspend/ban driver or vehicle, vehicle-report review, temporary delisting.

### SCR-AP-005 · `support_tickets` — Support & disputes · [DERIVED] (US-14.13, US-16.3)
Ticket queue, read-only trip/user lookup, dispute investigation, refund **requests**.

### SCR-AP-006 · `finance` — Finance & reconciliation · [DERIVED] (US-9A.15, US-14.11)
All wallet transactions; **OnePay/LankaQR gateway settlement reconciliation** (no bank-transfer queue — AL-05);
**wallet reversals/adjustments & refunds**; payouts/settlements; **Credit transfers** tab = read-only review of driver→driver transfers (exact value, **no per-driver/per-transfer commission**, AL-01). The bulk-voucher commission % is set in Config (SCR-AP-007).

### SCR-AP-007 · `config` — Platform configuration · [DERIVED] (US-14.4/14.5/14.12)
Fare tariffs, daily-fee rates, **bulk-voucher commission % per voucher value** (the "Commission & vouchers" tab: a table of voucher value → commission % → driver-pays → wallet-credit → active; `billing.voucher_discount_tiers`, applied only at purchase, the reseller's margin — **no per-driver commission**), **canonical vehicle types** (AL-09), Driver Level params, feature flags.

### SCR-AP-008 · `rbac` — User & role management · [DERIVED] (Epic 21, AL-06) · **Super Admin only**
Provision internal users, assign roles, define permission sets, suspend/revoke accounts + sessions.

### SCR-AP-009 · `audit_logs` — Audit trail · [DERIVED] (US-19.3) · **Auditor read-only**
Immutable admin-action + permission-change log; analytics/reporting export.

---

# SECTION FP — FLEET PORTAL (Responsive Web · `fleet.mageride.lk` · `SCR-FP-###`) · all **[NEW]** (AL-03, Epic 13 Phase 1)

Next.js responsive web (`fleet-portal`), mobile-first, styled exclusively with **Tailwind CSS** (shared `@mageride/tailwind-preset` — AL-52). **Auth: Email+Password / Google / Apple** (AL-07).
Org-scoped **Owner/Manager/Viewer** sub-roles (US-13.A5). Org must be Verification-Officer-approved before non-read ops (US-13.A7).

### SCR-FP-001 · `fleet_login_signup` — Login / Sign-up · [NEW] (US-13.A2/A3)
Email+Password / Google / Apple; email verification + password reset; link/unlink identities.

### SCR-FP-002 · `fleet_org_setup` — Organisation setup · [NEW] (US-13.A5/A7)
Org profile + KYC (→ Verification-Officer gate), team-member invites (Manager/Viewer), language. Links to Bank & payout details (SCR-FP-002a).

### SCR-FP-002a · `fleet_bank_payout` — Bank & payout details · [NEW] (US-27.1/27.2, AL-49) · **Owner only**
Bank / branch / account number / account holder name; uploads: **latest bank statement or passbook first page** + **bank-app LankaQR code image**. Pending verification → Verified (Verification Officer, fleet-org queue SCR-AP-003). Gate: no **Service payment = Paid** vehicle and no Paid-subscription billing until Verified; passenger pay sheet consumes the verified details/QR.

### SCR-FP-003 · `fleet_dashboard` — Fleet dashboard · [NEW]
Fleet KPIs (online/stale/offline), today's trips, alerts, **fleet wallet balance + next monthly invoice**.

### SCR-FP-004 · `fleet_vehicle_onboarding` — Vehicle onboarding · [NEW] (US-13.1/13.6, US-27.3/27.4)
Single + **bulk CSV**; **named document slots — registration copy (CR book) · insurance certificate · revenue license · route permit (Mode A required)** — AI extraction with per-document Verified/Pending/Missing chips (AL-50); per-vehicle Pending/Approved/Rejected. Mode A/B only (**no Mode C**). Mode B: **Service payment — Free / Paid** (renamed from "Mode B classification", AL-51) + default monthly fare for Paid; Paid requires a Verified SCR-FP-002a payout profile.

### SCR-FP-005 · `fleet_drivers` — Driver assignment · [NEW] (US-13.2/13.8)
Assign/revoke drivers (by User ID / phone) to vehicles; assignment history.

### SCR-FP-006 · `fleet_trackers` — Tracker binding · [NEW] (US-13.12)
ST-901 IMEI/MAC binding + auto-session config; publish-cadence profile.

### SCR-FP-007 · `fleet_map` — Live fleet map · [NEW] (US-13.3)
Single org-scoped MapLibre map (row-level security), fleet-health overlay.

### SCR-FP-008 · `fleet_scheduling` — Scheduling & alarms · [NEW] (US-13.11)
Per-vehicle scheduled rides; not-started alarm config (rings in assigned-driver apps).

### SCR-FP-009 · `fleet_analytics` — Trip history & analytics · [NEW] (US-13.4)
Per-vehicle trips/distance/utilisation/idle; date filters; CSV/PDF export.

### SCR-FP-010 · `fleet_billing` — Billing & wallet · [NEW] (US-13.10/10b)
Monthly **per-Mode-B-vehicle** invoice (Mode A free); fleet wallet top-up (Card/OnePay/LankaQR — no bank transfer).

---

# SECTION C — PLATFORM DIFFERENCES (Compose vs SwiftUI) `[DELTA:PLATFORM]` resolved

Unlike NY (single PrestoDOM tree, Android-only host), MageRide is **native per platform** over shared
KMP state. Layout intent matches; primitives differ:

| Concern | Android (Jetpack Compose + Material 3) | iOS (SwiftUI + HIG) |
|---|---|---|
| Scaffolding | `Scaffold` + `TopAppBar` + `NavigationBar` | `NavigationStack` + `.toolbar` + `TabView` |
| Lists | `LazyColumn`/`LazyRow` + `ListItem` | `List`/`ScrollView` + `Form`/`Section` |
| Layout | `Column`/`Row`/`Box` | `VStack`/`HStack`/`ZStack` |
| Sheets | `ModalBottomSheet` (drag handle) | `.sheet` detents `.medium/.large` |
| Dialogs | `AlertDialog` | `.alert` / `.confirmationDialog` |
| Transient | `Snackbar` | inline banner / `.alert` |
| Type | M3 type scale (Outfit/Inter) | SF Pro + Dynamic Type |
| Icons | Material Symbols | SF Symbols |
| Color | `MaterialTheme.colorScheme` | `Color` asset (light/dark) |
| Back nav | system back + toolbar back | swipe-back gesture |
| Haptics | `HapticFeedback`/`Vibrator` | `.impact`/`.notification`/`.selection` |
| Context actions | long-press / FAB (bottom-end) | context menus (long press) |
| Map | MapLibre GL Native (Android SDK) | MapLibre GL Native (iOS SDK) |
| Background GPS | `ForegroundService` + `FusedLocationProvider` | `CLLocationManager` background mode |
| MQTT | HiveMQ Android (foreground svc) | CocoaMQTT (background task) |
| Secure store | Android Keystore | Keychain + Secure Enclave |
| Attestation | Play Integrity | App Attest |
| Push | FCM native | APNs via FCM |
| VoIP | WebRTC / ConnectionService | CallKit + WebRTC |
| QR scan | CameraX + ML Kit | VisionKit `DataScannerViewController` |
| Payment deep-link | Android Intent → portal | Universal Links → portal |
| App update | in-app update / Play redirect | App Store redirect |
| Accessibility | TalkBack + dynamic font (US-19.1/2) | VoiceOver + Dynamic Type |

**Status bar / safe-area** (resolves NY `[UNVERIFIED iOS host]`): Compose `WindowInsets`/`Scaffold`
padding; SwiftUI `.safeAreaInset`/`GeometryReader`. RTL = N/A (Si/Ta/En LTR). Dark mode both (§0.2).

---

## Traceability Addendum

| URD US-ID | URD Epic | D2′ Screen ID | Tag | ADD §/Item | Notes |
|---|---|---|---|---|---|
| US-1.1/1.10 | 1 | SCR-*-003 | [ADAPT] | §12.1, D-32 | +94, SMS OTP, 60s resend |
| US-1.2/1.3 | 1 | SCR-*-002 | [ADAPT] | §18.2 | carousel + Si/Ta/En |
| US-1.5 | 1 | SCR-PA-004/027/027b | [KEEP]/[NEW] | §6 iam | profile edit + edit-profile screen (change619) |
| US-1.7/1.8 | 1 | SCR-PA-027 | [KEEP] | E-06 | logout, delete (PDPA) |
| US-1.5/2.4/2.12 | 1/2 | SCR-DA-003a | [NEW] | D-36 | Profile Setup: name + **required** photo + DL (Gemini), precedes Home (Change 6/22) |
| US-2.1–2.5 | 2 | SCR-DA-004/004a/004b/004c | [REPLACE]/[NEW] | D-36 | Mode-C 4-step onboarding, Gemini Flash 3.0 auto-verify (Change 6/22) |
| Change 6/22 cities | 1 | SCR-DA-002 | [ADAPT] | D4 §17b | vertical lang (Sinhala first) + `config.operating_cities` |
| US-2.8/2.13/2.16 | 2 | SCR-DA-026/006 | [ADAPT] | §6 registry | switcher, status, deactivate |
| US-3.1/3.2/3.4/3.14 | 3 | SCR-DA-027 | [NEW] | T-02/08/09 | IMEI pair, fleet CSV |
| US-4.1–4.4,4.7,4.8 | 4 | SCR-DA-028 | [ADAPT] | D-22 | share grant + expiry |
| US-4.5/4.6 | 4 | SCR-PA-024 | [NEW] | D-23 | request by Vehicle ID |
| US-NEW.1 | 10 | SCR-PA-025 | [NEW] | §11.10 | unsubscribe Mode B |
| US-5.1–5.6,5.9,5.10 | 5 | SCR-DA-011 | [NEW] | Appendix B | Start/End Journey |
| US-6A.1 | 6A | SCR-DA-010/012 | [NEW] | R-08 | standby toggle |
| US-6A.2/6A.3 | 6A | SCR-DA-014 | [REPLACE] | R-02, §11.11 | 15s offer + badges |
| US-6A.4 | 6A | SCR-PA-013 | [NEW] | §6 dispatch | schedule ride |
| US-6A.5/6A.8/6A.15 | 6A | SCR-DA-017/018 | [NEW] | D-06 | Job Board, level gate |
| US-6A.6/6A.7/6A.14 | 6A | SCR-DA-019 | [NEW] | D-04 | Driver Level badge |
| US-6A.9/6A.10/6A.10b | 6A | SCR-PA-014/015/009 | [REPLACE] | §11.7/12 | Rs50, 3-cancel disable |
| US-6A.12/6A.13 | 6A | SCR-PA-015 | [REPLACE] | Appendix B.2 | live driver |
| US-6A.16 | 6A | SCR-PA-028/SCR-DA-031 | [NEW] | D-24/25, P-05 | VoIP (rider not booker) |
| US-6A.17–6A.23 | 6A | SCR-DA-013 | [NEW] | DT-01..08 | Directional Travel |
| US-7.1–7.4 | 7 | SCR-PA-010/007/009 | [REPLACE] | §3, MAP-03 | MapLibre, A/B popup; public-route polyline + walk-to-halt nav (change619) |
| US-7.7 | 7 | SCR-PA-006 | [NEW] | MAP-03 | mode/type filter, trains |
| US-7.11/7.12 | 7 | SCR-PA-007/015 | [ADAPT] | — | ETA / driver after accept |
| US-7.13 | 7 | SCR-PA-026 | [KEEP] | — | Home/Work shortcuts |
| US-7.14/7.16/7.17 | 7 | SCR-PA-010 | [REPLACE] | §7.5 | empty/engaged/stale states |
| US-8.2/8.4/8.9 | 8 | SCR-PA-008/009 | [REPLACE] | §6 fare | route/place search; upfront fare; public-route Track (change619) |
| US-8.7 | 8 | SCR-PA-022 | [KEEP] | — | trip history |
| US-8.8 | 8 | SCR-DA-030 | [ADAPT] | — | driver sees fare |
| US-8.10/8.11/8.15 | 8 | SCR-PA-016/017 | [REPLACE] | §11.8 | Cash/LankaQR/OnePay+5% |
| US-8.16–8.21 | 8 | SCR-PA-010b/011 | [NEW] | P-01..05,§11.15 | proxy + loc-request |
| US-9.1/9.4/9.7 | 9 | SCR-DA-021/010 | [REPLACE] | D-13 | daily-fee card, first-free |
| US-9.9 | 9 | SCR-DA-021 | [REPLACE] | — | low-balance warning |
| US-9.10/9.13/9.17 | 9 | SCR-DA-023/024 | [NEW] | §11.6 | driver-to-driver credit transfer (by Driver ID, exact value) |
| US-9.18/9.19/9.20/9.21 | 9 | SCR-DA-022/024 | [REPLACE]/[NEW] | §11.5 | top-up, vouchers |
| US-9.22/9.23 | 9 | SCR-DA-020/033 | [ADAPT]/[NEW] | US-14.11 | earnings, fee refund |
| US-9A.1–9A.19 | 9A | SCR-WP-001..011 | [NEW]/[DERIVED] | §11.5 | Wallet Portal |
| US-10.x | 10 | SCR-DA-034 / SCR-PA push | [KEEP] | DT-08 | alerts, directional reminder |
| US-12.1/12.5/12.8/12.10 | 12 | SCR-PA-027b/029/030b, SCR-DA-032 | [ADAPT]/[NEW] | D-33 | SOS contacts (change619), SOS, report/block |
| US-14.4/14.11/14.12/14.13 | 14 | SCR-WP-011 | [DERIVED] | §6 admin | admin panel |
| US-15.1/15.2/15.6 | 15 | SCR-DA-035, SCR-PA-032 | [ADAPT] | R-17 | offline replay, banner |
| US-16.1/16.2 | 16 | SCR-PA-030/030a, SCR-DA-033 | [ADAPT]/[NEW] | §6 support | FAQ + raise-ticket modal (change619) |
| US-17.1/17.2 | 17 | SCR-*-031/035 | [NEW] | D-31 | app update |
| US-18.1/18.2/18.3 | 18 | SCR-PA-019, SCR-DA-030/029 | [ADAPT]/[NEW] | — | ratings + comment |
| US-19.1/19.2/19.3 | 19 | Section C, SCR-WP-011 | [ADAPT] | §18.2 | a11y, audit log |
| US-20.1–20.11 | 20 | SCR-PA-012/020/021, SCR-DA-016 | [NEW] | P-06..10,§11.16 | package + OTP/COD |
| URD §6 Confirm Pickup (Rider) | 8 | SCR-PA-011 | [NEW] | P-02 | rider confirm screen |
| URD §6 VoIP Call | 6A | SCR-PA-028/SCR-DA-031 | [NEW] | D-24 | in-app call |

**Coverage:** every URD §6 screen (Passenger, Driver, Wallet Portal) → ≥1 row; every `[NEW]` screen →
≥1 row. iOS variants + Portal screens marked `[DERIVED]` per rules (URD §6 Android-only).

## Mandatory UI Coverage Check

| Item | Rendered where | ✅/❌ |
|---|---|---|
| **P-05** proxy "Third-party booking" badge on driver offer | SCR-DA-014 (badge) | ✅ |
| **P-07** pickup/delivery OTP entry + display | SCR-DA-016 (entry), SCR-PA-020/021 (display) | ✅ |
| **P-06** package size selector S/M/L + per-size weight/vehicle hint | SCR-PA-012 (`SegmentedButton`/`Picker` + info hint, change619) | ✅ |
| **DT-08** directional live state + remaining-uses + pre-expiry reminder | SCR-DA-013 (banner, uses, 10-min reminder) | ✅ |
| Daily-fee card | SCR-DA-021 + SCR-DA-010 chip | ✅ |
| Driver Level badge | SCR-DA-019 + SCR-DA-010 badge | ✅ |
| Mode badges (A green/B grey/C orange) | §0.2 tokens, SCR-PA-006 | ✅ |
| Vehicle-type marker legend | §0.2 legend table, SCR-PA-010 | ✅ |
| Design-token table (authoritative for Figma) | §0.2 | ✅ |

All in-scope items ✅ — **document NOT `[INCOMPLETE]`.**

---

## Verification & Caveats Summary

- Every screen carries both an Android (`SCR-*A`) and iOS (`SCR-*I`) variant with Compose/SwiftUI
  component names; Wallet Portal screens (`SCR-WP`) are responsive web with 375/768/1024 breakpoints.
- **All Phase-A `[UNVERIFIED]` (10) resolved** (§0.4): dark-theme + spacing + elevation tokens now
  defined; marker rotation/cluster = MapLibre layers; iOS host = real SwiftUI target; RTL N/A;
  passenger no-show N/A.
- **`[DELTA:INDIA]`** → Rs / +94 / Si·Ta·En; India-only screens dropped. **`[DELTA:JUSPAY]`** →
  OnePay/LankaQR/Cash + wallet (no Juspay PaymentPage). **`[DELTA:PLATFORM]`** → explicit per-screen
  Compose/SwiftUI split + Section C.
- **Hard rules honoured:** all map UI `[REPLACE]` (MapLibre + PMTiles); all payment UI `[REPLACE]`/
  `[NEW]` (OnePay/LankaQR/Cash/COD). No Google Maps, no Juspay anywhere.
- **Design tokens (§0.2) are authoritative** — Figma library and Compose/SwiftUI themes consume the
  same hex/scale/spacing/corner/elevation values.

---

## Δ Addendum — Discussion 2026-06-21 (UI/UX changes, items 1–18)

> Mirrors the updated wireframes (`wireframes/passenger_android.html` etc.). New screens get full SCR-IDs across all 4 mobile targets (PA/PI/DA/DI) + Fleet Portal (FP). Design tokens (§0.2) unchanged.

### Changed screens

| Screen | Change | Item |
|---|---|---|
| SCR-PA/PI-002 onboarding | Language selector → **vertical boxes, one per row, ordered Sinhala → Tamil → English** (default highlight Sinhala) | 1 |
| SCR-PA/PI-006 mode filter | Each vehicle-type chip shows a **small vehicle icon tinted with its AL-09 colour** (not a plain dot) | 2 |
| SCR-PA/PI-008 search | **Destination = geo-location only** (no route-number typing); predictions = geocoded places + saved/recent (no route rows) | 4 |
| SCR-PA/PI-009 booking | Mode A public buses (from GTFS) show **route number + description + Direct/Transit tag + PUBLIC label**, listing **all direct options**; Mode C private tiers show **price only** (no minutes/distance) | 3 |
| SCR-PA/PI-010b proxy pickup | Add **"Paste link"** method alongside Search / Map pin / Request (Google Maps URL → parse lat/lng → drop pin); tapping it opens the **paste sheet SCR-PA/PI-012a** | 5 |
| SCR-PA/PI-012 package | Pickup method = Search / Map / **Paste link** (Request removed); **Drop-off location** added with 4 options Search / Map / Paste link / Request; tapping **Paste link** opens the **paste sheet SCR-PA/PI-012a** | 6 |
| SCR-PA/PI-017 pay fare | Remove centred QR; add **"Scan driver's QR"** camera option (printed/on-screen/sticker) | 18 |
| SCR-PA/PI-021 package recipient | States: **driver-confirm-pickup** trigger; FCM push (app) opens this screen / SMS web-link (no app) | 11 |
| SCR-PA/PI-024 Mode B request | Opened by **tapping a Mode B marker** (Vehicle ID pre-filled) or the nav drawer; re-subscribe = request→accept | 8, 17 |
| SCR-PA/PI-025 subscriptions | Cards show **Paid (amount/mo + next-due) / Free**, **💳 Pay**, **🧾 history**, **compact ✕ unsubscribe icon**; muted-until-owner-deletes note | 16, 17 |
| SCR-PA/PI-027b edit profile | **Language selector removed** (kept in onboarding + Settings) | 10 |
| SCR-PA-007 / PI-007 popup | Title "vehicle popup (Mode A)"; Mode B markers route to SCR-PA-024 | 8 |
| SCR-FP-004 vehicle onboarding | Mode B vehicles require **Paid/Free classification + default monthly fare**; status table adds Classification column | 16b |
| SCR-DA/DI-028 sharing | **Per-vehicle** vehicle switcher; incoming Mode B subscription requests shown under the selected vehicle (multi-vehicle + temp-hired Mode A/B) | 12, 15 |

### New screens

| SCR-ID(s) | Screen | Key elements | Item |
|---|---|---|---|
| SCR-PA-012a / SCR-PI-012a | **Paste-link → pin** (ModalBottomSheet / `.sheet` detent) | clipboard **Paste** affordance (`ClipboardManager` / `UIPasteControl`) → states **Empty → Parsing ("Reading link…") → Resolved → Error**; full URLs parsed on-device, short `maps.app.goo.gl` resolved via `transit-svc /geo/parse-maps-link` (BR-23.4 / I-23.1, AL-20); **Resolved** shows pin preview + reverse-geocoded address (Nominatim) + lat/lng → **Use this location**; **Error** (unparseable / 3 s timeout) → "couldn't read that link — pick on map". Opened from every Paste-link entry (010b proxy pickup, 012 package pickup & drop-off) | 5, 6 |
| SCR-PA-025a / SCR-PI-025a | **Subscription payment** | amount + mode picker: LankaQR deep-link / LankaQR scan / OnePay (+5%) / Online transfer (attach screenshot) → routed to fleet owner | 16e |
| SCR-PA-025b / SCR-PI-025b | **Subscription payment history** | per-subscriber statement: month, date, method, amount, status (Paid / Pending verification / Paid-cash) | 16h |
| SCR-PA-026a / SCR-PI-026a | **Add address (ModalBottomSheet)** | opens after pin drop; **Address Line 1 / 2 / 3 + Label** ("Gym","Mum's House","Office"); Save | 7 |
| SCR-PA-033 / SCR-PI-033 | **Menu / nav drawer (passenger)** | Private transport → 024, My subscriptions → 025, Saved addresses → 026, Profile & settings → 027 (+ Help, Log out) | 9 |
| SCR-DA-036 / SCR-DI-036 | **Menu / nav drawer (driver)** | My Vehicles 026, **Vehicle Onboarding · Mode C 004**, GPS Tracker Pairing 027, Sharing Mgmt (Mode B) 028, Driver Profile 029, Ride History + Rate 030, Support + Fee Refund 033, Notifications 034 | 14 |
| SCR-FP-011 | **Mode B subscriptions & requests** (fleet) | per-vehicle: incoming request **Accept/Reject**; subscriber roster with **per-subscriber editable fare, billing cycle, this-month status, Mark received / Confirm transfer**, muted-until-deleted unsubscribed rows | 15, 16, 17 |
| SCR-FP-012 | **Subscriber payments ledger** (fleet) | per-subscriber per-vehicle ledger (LankaQR/OnePay/transfer/cash) + summary KPIs + CSV export | 16i |

> **No-login web (`passenger.mageride.lk`)**: recipient package-tracking page reached from the **driver-confirm-pickup** SMS (`/track?token=…`) already specified (AL-04/P-09); states updated to name the pickup-confirm trigger (item 11).

## Δ Addendum — Discussion 2026-06-25 (driver UI change pass, items 1–13)

> Per-screen driver UI changes for ADD v2.8 §1.10 (AL-28…AL-35) / URD v2.4. Screen numbers are SCR-DA-xxx (Android) / SCR-DI-xxx (iOS), valid for both; admin = SCR-AP-xxx. Wireframes updated in `driver_android.html`, `driver_ios.html`, `web_admin.html`.

| Screen | Change | Item |
|---|---|---|
| SCR-DA/DI-002 | **Feature-infographic carousel** (3 swipeable slides + paged dots) above the language & city selectors, mirroring SCR-PA-002 (`HorizontalPager` / paged `TabView`) | 1 |
| SCR-DA/DI-003a | Driving-licence card also shows **NIC no + Allowed vehicle types** (with Licence no + Expiry); unreadable fields are **typed by the driver** and shown with a **⚑ Admin-verify** flag (manual entry → Pending) | 2 |
| SCR-DA/DI-004a | Insurance step: a **doubtful (low-confidence) or edited** element sets the step **Pending** + ⚑ admin-verify note; "Save & continue" | 3 |
| SCR-DA/DI-004b | Revenue-licence step: same **doubtful/edited → Pending** + ⚑ admin-verify | 4 |
| SCR-DA/DI-004c | Vehicle-photos step: **plate ≠ registration-no → Pending**; each step saved on completion; resume opens the next step; My Vehicles shows **Incomplete/Approved** | 5 |
| SCR-DA/DI-006 | When all steps are complete & **Approved**, opening Vehicle Onboarding / ＋ in My Vehicles starts a **new vehicle at Step 1/4** | 6 |
| SCR-DA/DI-010 | Home map shows **only the driver's own active vehicle**; **top-left hamburger removed** (nav via Menu tab) | 7 |
| SCR-DA/DI-011 | **Mode A/B home dashboard**: only **Start Journey / End Journey**, vehicle type+number **below the route card**; **GPS-ignition auto-start** banner the dashboard can **override** | 8, 11 |
| SCR-DA/DI-016a | Delivery **sheet 1 — Review**: pickup/drop distances, payment method, **sender & recipient phone numbers each with a Call button** (mobile voice call); **Start** / **Cancel → re-dispatch to next driver** | 9 |
| SCR-DA/DI-016b | Delivery **sheet 2 — Pickup**: map pickup pin, **Call sender**, SOS, **Pickup OTP** → verify → sheet 3 | 9 |
| SCR-DA/DI-016c | Delivery **sheet 3 — Complete**: **Delivery OTP**, photo proof, sender+recipient call buttons, **"Delivery completed"** (replaces "Cash received (COD)") | 9 |
| SCR-DA/DI-023 | Request credit: **QR scan removed** — Driver-ID-only entry | 10 |
| SCR-DA/DI-027 | GPS tracker: once paired/assigned the **phone stops ingesting GPS** (device = single publisher); device-ON ignition **auto-starts the journey**; opening the app shows **"Journey started"** | 11 |
| SCR-DA/DI-028 | Mode B sharing: **"Showing sharing for … assigned by …" caption removed**; per-vehicle selector **full device width** | 12 |
| SCR-DA/DI-030 | Rate passenger opens in a **modal bottom sheet** (was an inline card) | 13 |
| SCR-AP-003 | Verification queue: per-field **Source (AI/Manual) + Status (Auto-verified/Pending)** with **Confirm / Edit & confirm** for doubtful/manual/plate-mismatch fields (incl. NIC + allowed types); **per-step Verified/Pending breakdown**; Approve unlocks only when all confirmed | 2–5 |

## Δ Addendum — Discussion 2026-06-28 (UX & admin directory, items 1–11)

> Per-screen changes for ADD v2.9 §1.11 (AL-36…AL-43) / URD v2.5 Epic 24 (US-24.1…US-24.11). Passenger = SCR-PA-xxx (Android) / SCR-PI-xxx (iOS); driver = SCR-DA/DI-xxx; admin = SCR-AP-xxx. Wireframes updated in `passenger_android.html`, `passenger_ios.html`, `driver_android.html`, `driver_ios.html`, `web_admin.html`.

| Screen | Change | Item / US |
|---|---|---|
| SCR-PA/PI-002 | Onboarding: **Get Started CTA pinned to the bottom** of the screen (full-width, below the language list after a flexible spacer) | 1 / US-24.1 |
| SCR-PA/PI-013 | Schedule ride: **mandatory "Where to?" destination picker** (same place-search/map-pick sheet as on-demand booking) + editable pickup (defaults to current location); **Confirm disabled until a destination is set** | 2 / US-24.2 |
| **SCR-PA/PI-015a** ★ NEW | **Call-type chooser** bottom sheet shown when 📞 Call is tapped (active ride, history, trip details): **Free call** (in-app VoIP, numbers hidden) vs **Normal call** (masked-number cellular); remembers last choice | 4 / US-24.3 |
| SCR-PA/PI-015 | Active ride: 📞 Call shows a **▾** affordance and opens **SCR-PA/PI-015a** instead of dialing immediately | 4 / US-24.3 |
| SCR-PA/PI-022 | Trip & schedule history: each **completed-trip card shows the driver's name + mobile number** with a **Call** action (opens 015a); number hidden for cancelled-before-assignment trips | 3 / US-24.4 |
| **SCR-DA/DI-005** ★ NEW | **Document capture (camera + draggable-corner crop):** live camera with an adjustable quad — drag four corner handles so the whole document fills the frame; auto edge-detect proposes the quad; Retake / Use photo; perspective-correct on confirm. Shared by every onboarding capture slot | 6 / US-24.6 |
| SCR-DA/DI-003a/004a/004b/004c | Each **📷 capture slot opens SCR-DA/DI-005**; labels read "Tap to capture"; cropped/de-skewed image is uploaded & sent to Gemini Flash | 6 / US-24.6 |
| SCR-AP-001 | Admin login: **MFA/TOTP block removed** — password or Google only, completes straight to dashboard | 5 / US-24.5 |
| SCR-AP-002 | Dashboard: **statistics period filter** (Today / This week / This month / Custom range) with vs-previous-period deltas + CSV export; period KPIs recompute, live cards stay real-time | 7 / US-24.7 |
| SCR-AP-003 | **Becomes the queues list** — three tabs/queues: **driving-licence pending · vehicle-registration pending · fleet-org approval**; search + status filter; row → detail | 8 / US-24.8 |
| **SCR-AP-003a** ★ NEW | Verification **detail**: attached-document **thumbnails grid** + AI-extracted fields (Confirm / Edit & confirm) + decision rail (per-step breakdown; Approve unlocks when all confirmed) | 8 / US-24.8 |
| **SCR-AP-003b** ★ NEW | **Full-size document viewer** (lightbox) opened by tapping any thumbnail — zoom / rotate / prev-next; signed-URL fetch; view is audited | 8 / US-24.8 |
| **SCR-AP-003c** ★ NEW | **Fleet-org approval detail**: org KYC fields + KYC-document thumbnails (→ 003b) + approve/reject with reason | 8 / US-24.8 |
| **SCR-AP-010 → 011** ★ NEW | **Passenger directory**: multi-criteria search (name / mobile / passenger ID / email) → detail with tabbed **Trips / Payments / Packages / Disputes** | 9 / US-24.9 |
| **SCR-AP-012 → 013** ★ NEW | **Driver directory**: multi-option search (name / mobile / driver ID / NIC / reg no / Level / status; verified by default) → detail with tabbed **Trips / Wallet ledger / Daily fee / Credit transfers / Reports** + linked vehicles | 10 / US-24.10 |
| **SCR-AP-014 → 015** ★ NEW | **Vehicle directory**: criteria search (reg no / vehicle ID / type / mode / owner mobile / fleet org / status) → detail with info + document thumbnails (→ 003b) + tabbed **Trips / Earnings / Daily fee / Reports** | 11 / US-24.11 |
| Admin sidebar | New **Directory** nav group (Passengers · Drivers · Vehicles) added across portal screens | 9–11 |

## Δ Addendum — Discussion 2026-07-05 (Passenger Web subview `SCR-WT`, items 1–8)

> Per-screen contracts for ADD v3.0 §1.12 (AL-44…AL-46) / URD v2.6 Epic 25 (US-25.1…US-25.8). The six `passenger.mageride.lk` pages (wireframed in `web_passenger.html`, previously spec'd only as AL-04/P-09 notes) get **screen IDs `SCR-WT-001…006`** and full states. Surface rules: no app chrome, no login — the **token is the credential** (scope-shaped payload, single ride/package, TTL-bounded); mobile-first responsive (375 px primary); all data via `public-bff /public/track/*`. Wireframes updated in `web_passenger.html` (IDs added); annotation hygiene in `passenger_android.html` / `passenger_ios.html` (item 8).

| Screen | Change | Item / US |
|---|---|---|
| **SCR-WT-001** ★ NEW | **Landing / token gate** — validates `?token=`: valid → route by scope (002 / 003 / 004); expired/invalid → 006; already-delivered → 005 receipt view. Loading spinner ≤1 s; no data rendered before validation | 1 / US-25.1 |
| **SCR-WT-002** ★ NEW | **Package track (recipient)** — live driver position (SSE/poll) + 4-step status (Pickup → Picked → In transit → Delivered) + **4-digit Delivery OTP** (US-20.5, P-07) + driver/vehicle card + **Call driver** (masked, → item 4). Delivered → auto-advance to 005 | 1–2 / US-25.1/25.2 |
| **SCR-WT-003** ★ NEW | **Confirm pickup (unregistered proxy rider)** — 5-min TTL countdown banner ("declining never sends your GPS"), adjustable map pin, **Share location** / **Decline**. Share → booker pin auto-fills; Decline/expiry → token dead + booker fallback (P-02) | 3 / US-25.3 |
| **SCR-WT-004** ★ NEW | **Ride track (proxy rider)** — driver name/photo, vehicle + reg, ETA, trip progress polyline, **Start OTP**, cash-due notice when booker chose Cash (US-8.21), **Call driver** (masked) + **SOS** (browser GPS → SMS). "Third-party booking" context chip | 1,4,5 / US-25.1/25.4/25.5 |
| **SCR-WT-005** ★ NEW | **Delivered / receipt** — outcome states: OTP-verified ✓ · photo-proof shown (recipient absent, P-10) · COD collected vs **Disputed** (>24 h uncollected, P-14); **Download receipt**; token closes after completion | 6 / US-25.6 |
| **SCR-WT-006** ★ NEW | **Expired / invalid link** — safe dead-end, zero ride data, app-download link. Reached from any closed/revoked/unknown token | 1 / US-25.1 |
| SCR-DA/DI-012 | Re-tagged **[MERGED → SCR-DA/DI-010]** — the standby Online/Offline toggle is a **dashboard state, not a separate screen** (matches wireframes; prevents a redundant build) | 8 / US-25.8 |
| Wireframe annotations | `US-8.7a` → **US-24.4** (history call action) in `passenger_android.html` / `passenger_ios.html`; splash `GET /rides/active` → **`GET /v1/rides/passenger/{id}/active`** (D3 path) | 8 / US-25.8 |

## Δ Addendum — Discussion 2026-07-05 #2 (driver-QR settlement & masking removal, items 1–6)

> Per-screen changes for ADD v3.1 §1.13 (AL-47…AL-48) / URD v2.7 Epic 26 (US-26.1…US-26.5). Wireframe annotations updated in `passenger_android.html` / `passenger_ios.html` / `driver_android.html` / `driver_ios.html` / `web_passenger.html`.

| Screen | Change | Item / US |
|---|---|---|
| SCR-PA/PI-017 | Pay fare: after scanning the driver's QR, new **"I've paid" CTA** (+ optional receipt-screenshot attach) → state chip **"Waiting for driver to confirm…"** → **Confirmed ✓** on `DriverConfirmedQR`. Unconfirmed >5 min → "Driver hasn't confirmed — get help" link → support ticket. OnePay path unchanged (gateway-verified) | 1 / US-26.1 |
| SCR-DA/DI-015 | Active ride: on QR-paid rides a **"QR payment received?" confirm sheet** (Confirm ✓ / Not received) at completion — Confirm → `DriverConfirmedQR`, earning posts; driver can also confirm proactively from the fare summary. "Not received" → fall back to cash or raise dispute | 1 / US-26.1 |
| SCR-PA/PI-015a | Call-type chooser retained, **copy changed**: **Free call** (in-app VoIP) · **Normal call** (**direct call to [name]'s number** — real MSISDN shown post-accept; masking removed). Remove "numbers hidden / masked" copy; add first-call tooltip "Your number is visible to the other party" (US-26.5) | 2, 5 / US-26.2/26.5 |
| SCR-PA/PI-028 + SCR-DA/DI-031 | VoIP call screen: failure state now offers **"Call normally instead?"** (direct dial) — masked-SMS fallback copy removed | 4 / US-26.4 |
| SCR-PA/PI-022 | Trip history: driver mobile on completed-trip cards is the **real number** (no masking); withheld-if-cancelled-pre-assignment rule unchanged | 2 / US-26.2 |
| SCR-WT-002 / SCR-WT-004 | Web subview: driver card shows the **phone number as a `tel:` link** ("Call driver"); no confirm-your-number step, no `/call` round-trip | 3 / US-26.3 |

## Δ Addendum — Discussion 2026-07-18 (Fleet Portal payout & vehicle-document detail, items 1–3)

> Per-screen changes for ADD v3.2 §1.14 (AL-49…AL-51) / URD v2.8 Epic 27 (US-27.1…US-27.4). Fleet Portal only. Wireframes updated in `web_fleet.html`.

| Screen | Change | Item / US |
|---|---|---|
| **SCR-FP-002a** ★ NEW | **Bank & payout details** (Owner-only sub-screen of Organisation setup) — Bank ▾ / Branch / Account number / Account holder name; uploads **latest bank statement *or* passbook first page** + **bank-app LankaQR code image**. Status chip Pending verification → Verified (Verification Officer, fleet-org queue SCR-AP-003) → Rejected + reason. Account-holder name must match org/owner KYC; any edit re-enters Pending. The passenger Mode B pay sheet (SCR-PA/PI-025a) renders the **verified** account details (online transfer) and QR image (LankaQR scan/deep link) | 1 / US-27.1/27.2 |
| SCR-FP-002 | Adds a **Bank & payout** link/nav entry → SCR-FP-002a | 1 / US-27.1 |
| SCR-FP-004 | **Named per-vehicle document slots** replace the generic dropzone: **Registration copy (CR book)** · **Insurance certificate** · **Revenue license** · **Route permit (Mode A — required)**, each with AI extraction + per-document **Verified / Pending / Missing** chip; status table gains a **Documents** column; vehicle cannot reach Approved with a required doc Missing/Pending. Bulk CSV creates vehicles in **Docs pending** (documents cannot ship in the CSV) | 2 / US-27.3 |
| SCR-FP-004 | **"Mode B classification" renamed → "Service payment"** — values unchanged (**Free / Paid**); status-table column header renamed; **Paid selectable only when the org payout profile (SCR-FP-002a) is Verified**. API path `/classification` and column `mode_b_billing` intentionally unchanged (AL-51) | 3 / US-27.4 |

## Δ Addendum — Discussion 2026-07-22 (web styling standard — Tailwind CSS)

> ADD v3.3 §1.15 (AL-52). Stack ruling only — **no new screens or states**. **Tailwind CSS** is the sole styling system for every MageRide web surface: Section AP (`admin-portal`), Section FP (`fleet-portal`), and the SCR-WT web-subview pages (Δ 2026-07-05). The §A design tokens — brand/semantic colors, vehicle-type tokens, Outfit (display/headline) + Inter (body) type scale, light/dark — are published once as a shared **`@mageride/tailwind-preset`** (`theme.extend` + `dark:` variant); the §AP/§FP breakpoints (375 / 768 / 1024) become the Tailwind `screens` config. Utility-first classes + headless primitives (Radix/Headless UI) only; **MUI, Bootstrap, styled-components and other runtime CSS-in-JS are excluded** (build-time PostCSS compilation, SSR-safe). Mobile sections (Compose/Material 3, SwiftUI/HIG) are unaffected.

| Surface | Change | AL |
|---|---|---|
| Section AP (`admin-portal`) | Intro line now mandates Tailwind CSS via the shared preset | AL-52 |
| Section FP (`fleet-portal`) | Intro line now mandates Tailwind CSS via the shared preset | AL-52 |
| SCR-WT-001…006 (`public-bff` pages) | Reuse the same Tailwind preset/pipeline for visual consistency (no separate CSS stack) | AL-52 |

## Δ Addendum — Discussion 2026-07-22 #2 (GTFS Dataset Manager `SCR-AP-016`, US-28.1…28.3)

> ADD v3.4 §1.16 (AL-54…AL-55) / URD v2.9 Epic 28. One new Admin Portal screen in the **Configuration** nav group ("Transit data"); Tailwind-styled per AL-52. **Premise change:** the full national GTFS file is available at launch — SCR-PA-009's no-coverage degradation note (patch 2026-07-05 #3) is retained as a **safety net**, no longer the expected launch state (AL-55).

### SCR-AP-016 · `gtfs_manager` — GTFS Dataset Manager · [NEW] (US-28.1…28.3) · **Admin, Super Admin**

Three stacked zones (desktop: side-by-side upload + preview above history; mobile 375px: stacked):

| Zone / element | Component | Data / API | Behaviour |
|---|---|---|---|
| Upload dropzone | Drag-and-drop + file picker; `.zip` only, ≤ 200 MB | `POST /admin/transit/gtfs/uploads` (multipart) → 202 `feedVersionId` | Progress bar during upload; sha256 duplicate → inline error "This exact file is already uploaded (version N)" |
| Status stepper | `Uploaded → Validating → Validated / Failed` chips | `GET /admin/transit/gtfs/uploads/{id}` (poll 2 s) | Failed → error-summary banner (first 5 errors) + **Download error report** button (`…/report`, JSON/CSV) |
| Preview card (Validated only) | Counts grid: agencies · routes · trips · stops · stop times · shapes; `feed_info` version; service date range; warnings list (collapsible) | same poll payload | **Activate** primary button → confirm dialog stating the currently-active version being replaced |
| Activation | Confirm → spinner "Swapping live dataset…" | `POST …/{id}/activate` (Idempotency-Key) | Success toast "Feed vN live — passenger route options updated"; failure leaves prior feed active (atomic, BR-32.2) |
| Version history table | Columns: feed version (`feed_info`) · file name · uploaded by/at · counts · status chip **Active / Archived / Validated / Failed** · actions | `GET /admin/transit/gtfs/versions` | Actions: **Re-activate** (Archived/Validated rows → rollback, same confirm dialog) · **View report** · **Download zip** (signed URL) |
| Empty state (pre-first-import) | Illustration + "No GTFS feed loaded — passenger route-matching is hidden until a feed is activated" | — | Day-0 state only (AL-55) |

**States:** uploading · validating · validated-preview · failed-report · activating · active-idle · empty. All mutations audited (D-35); RBAC deny-by-default — Verification/Support/Finance/Auditor see no Transit-data nav entry (Auditor may read history via audit log only).

| Screen | Change | Item / US |
|---|---|---|
| **SCR-AP-016** ★ NEW | GTFS Dataset Manager — full-feed upload / validate / preview / atomic activate / history + rollback (table above) | US-28.1/28.2/28.3 |
| SCR-PA/PI-009 | No-coverage degradation re-scoped to **safety net** (feed gap / expired window / pre-first-import), not launch state | AL-55 |
| Admin sidebar | **Configuration** group gains a **Transit data (GTFS)** entry → SCR-AP-016 | US-28.1 |

*End of D2′. 0 `[INCOMPLETE]` markers; all mandatory UI-coverage items ✅.*
