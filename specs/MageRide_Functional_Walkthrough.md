# MageRide — Functional Walkthrough Document
**Version: aligned to ADD v2.6 / URD v2.2**
*Audience: business owners, QA testers, investors, support staff — not developers.*
*Last generated: 21 June 2026 · Driver change pass: 27 June 2026 — onboarding feature infographics (SCR-DA-002); NIC no + allowed-vehicle-types read from the driving licence, with admin verification of any driver-typed or doubtful field (SCR-DA-003a / SCR-AP-003); per-step onboarding save/resume and My-Vehicles **Incomplete / Approved** status (004a–c, 006, 026); own-vehicle-only home map with the top-left hamburger removed (010); Mode A/B **Start/End-Journey** home dashboard with GPS-ignition auto-start and dashboard override (011, 027); three delivery bottom sheets with **Delivery completed** replacing "Cash received (COD)" (016a/b/c); QR scan removed from request-credit (023); sharing caption box removed / full-width vehicle selector (028); rate-passenger bottom sheet (030).*

---

## 1. Platform Overview

**What MageRide is.** MageRide is a Sri Lankan transport and delivery platform. It connects everyday passengers with vehicles for hire, lets people track public and private transport live on a map, and moves parcels from sender to recipient. Everything is priced in Sri Lankan Rupees (Rs), uses Sri Lankan mobile numbers (+94), and is available in **Sinhala, Tamil and English**.

Think of it as three things in one:
1. A **live map** of buses, trains, school vans, office buses, and private hire vehicles moving in real time.
2. A **ride-hailing service** — book a tuk, car, or van to come to you (like an app-based taxi).
3. A **delivery service** — send a package and have a driver carry it across town.

**The three transport modes.** Every vehicle on MageRide belongs to one of three "modes":

| Mode | Plain meaning | Who runs it | Does the passenger pay per trip? |
|---|---|---|---|
| **Mode A — Public** | Public buses and trains anyone can watch on the map | Bus/rail operators | No — it is free public transport; MageRide only shows where it is |
| **Mode B — Private** | A specific private vehicle you are allowed to follow — a school van, an office staff bus | Fleet owners / vehicle owners | Sometimes — either **Free** (e.g. company staff bus) or **Paid** (a fixed monthly subscription) |
| **Mode C — On-demand** | A driver you hail right now for a ride or a delivery | Individual drivers | Yes — a fare per trip, shown upfront before you book |

**The five user types.**

| User type | Who they are |
|---|---|
| **Passenger** | A member of the public who tracks transport, books rides, and sends packages |
| **Driver** | An individual who owns or is assigned a vehicle and takes ride/delivery jobs |
| **Fleet Owner** | A company or person who owns many vehicles (school vans, office buses) and manages them centrally |
| **Reseller driver** | *Not a separate account.* It is simply an ordinary driver who has bought bulk wallet credit and passes some on to other drivers. There is no special "reseller" login |
| **Admin staff** | MageRide's own internal back-office team — split into specialised roles (see below) |

The Admin staff group is itself made up of several **internal roles**, each seeing only what their job needs:
- **Super Admin** — creates internal accounts and sets permissions
- **Verification Officer** — checks driver and fleet documents and approves them
- **Support / CSR Agent** — handles passenger and driver complaints and tickets
- **Finance Officer** — reconciles payments and processes refunds
- **Auditor** — read-only access to the tamper-proof history of admin actions

**The four surfaces** (the four "apps" people use):

| Surface | Web address / form | Used by |
|---|---|---|
| **Passenger App** | Android + iOS phone app | Passengers |
| **Driver App** | Android + iOS phone app | Drivers (including reseller drivers and fleet-assigned drivers) |
| **Admin Portal** | `admin.mageride.lk` (website) | The six internal Admin roles |
| **Fleet Portal** | `fleet.mageride.lk` (website) | Fleet owners and their managers |

**What `passenger.mageride.lk` is.** This is a special **no-login web page**. It is *not* an app and you never sign in. It is opened only by people who receive a text-message (SMS) link in two situations:
- A **package recipient** who does not have the app taps the link to watch their parcel arrive and see the delivery code.
- A **person being picked up on someone else's booking** taps the link to share their exact pickup location, when they don't have the app.

---

## 2. How to Read This Document

**Screen ID notation (SCR-XX-###).** Every screen in MageRide has a short code so testers and stakeholders can refer to the exact same screen. Read it left to right:

- `SCR-PA-010` = **SCR** (screen) · **PA** (Passenger Android) · **010** (screen number).

The two-letter middle part tells you which surface and platform:

| Code | Means |
|---|---|
| **SCR-PA-###** | Passenger app — **A**ndroid version |
| **SCR-PI-###** | Passenger app — **i**OS (iPhone) version |
| **SCR-DA-###** | Driver app — **A**ndroid version |
| **SCR-DI-###** | Driver app — **i**OS version |
| **SCR-AP-###** | Admin Portal web screen |
| **SCR-FP-###** | Fleet Portal web screen |
| **passenger.mageride.lk** | The no-login web page for unregistered people |

**Android vs iOS variants.** MageRide builds the Android and iPhone apps separately, so each screen exists in two near-identical versions — for example `SCR-PA-010` (Android) and `SCR-PI-010` (iPhone) are the **same screen**, just the Android and iPhone build of it. They look and behave the same except for small platform habits (the way menus, call screens, or pop-ups appear). Where the difference matters, this document flags it:
> 📱 **Android:** [behaviour] 🍎 **iOS:** [behaviour]

To keep things readable, when a screen behaves identically on both we write it once as, e.g., "the Live Map screen (SCR-PA/PI-010)".

**What "edge case" means here.** A normal walkthrough assumes everything goes right. An **edge case** is "what happens when it doesn't" — the passenger loses signal, the driver's wallet is empty, a code is entered wrong, a payment fails, a recipient isn't home. For each scenario we list the realistic things that can go wrong and **what appears on the screen** when they do, plus what the user should do about it.

**A note on language.** This document deliberately avoids technical terms. Where the specifications mention background machinery (servers, databases, message queues, security tokens), we describe only **what the user sees and experiences**.

---

═══════════════════════════════════════════════
## SECTION A — PASSENGER APP (SCR-PA / SCR-PI)
═══════════════════════════════════════════════

### Scenario 1: New passenger signs up and verifies their phone number
**Platform:** Passenger App
**Who:** Passenger (brand new)
**Goal:** Create an account and prove the phone number belongs to them.
**Preconditions:** The app is freshly installed; the person has a working Sri Lankan mobile number.
**Screens involved:** SCR-PA/PI-001 (Splash), SCR-PA/PI-002 (Onboarding + language), SCR-PA/PI-003 (Phone & code), SCR-PA/PI-004 (First profile), SCR-PA/PI-005 (Location permission)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Splash (SCR-PA/PI-001) | The MageRide logo on an orange background with a loading spinner | Nothing — waits a moment | Because there is no saved login, the app moves to the welcome carousel |
| 2 | Onboarding (SCR-PA/PI-002) | Three swipeable intro slides and a language chooser shown as **vertical boxes, Sinhala on top, then Tamil, then English** (Sinhala highlighted by default) | Swipes through the slides, taps a language, taps **Get Started** | The app remembers the language and opens the phone-number screen |
| 3 | Phone entry (SCR-PA/PI-003) | A field pre-filled with **+94** and space for a 9-digit number | Types their mobile number, taps **Continue** | The system sends a 6-digit secure code by text message; the screen shows six empty boxes |
| 4 | Code entry (SCR-PA/PI-003) | Six boxes and a "Resend code (60s)" countdown | Types the 6-digit code (the phone often fills it in automatically) | If correct, the account is created and the app moves on; if this is a returning number it goes straight to the map |
| 5 | First profile (SCR-PA/PI-004) | Fields for name and a photo, plus language and notification preferences | Enters their name, optionally adds a photo, taps **Save** | Profile saved |
| 6 | Location permission (SCR-PA/PI-005) | An explanation of why location is needed and an **Allow location** button | Taps Allow, then accepts the phone's own permission pop-up | The Live Map opens, centred on the user |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Wrong code typed | The boxes turn red with an inline error message | Re-type the code carefully, or wait for the countdown and resend |
| Code never arrives | The "Resend code" link becomes tappable after 60 seconds | Tap **Resend**; check the number was entered correctly |
| Too many code requests | "Try again later" message (the system limits codes to 5 per hour) | Wait, then try again |
| Location permission denied | The app shows an **"Open Settings"** link | Open phone settings and turn location on for MageRide |

⚠️ **SPEC GAP:** The specs describe a "first profile" screen (SCR-PA-004) **and** a richer profile-edit screen (SCR-PA-027b), but do not state whether skipping the name at step 5 is allowed or blocks the user. QA should confirm whether name is mandatory at first sign-up.

---

### Scenario 2: Setting up the profile — name, photo, saved addresses, default payment
**Platform:** Passenger App
**Who:** Passenger
**Goal:** Complete their profile, save Home and Work, and choose a default way to pay.
**Preconditions:** The passenger is signed in.
**Screens involved:** SCR-PA/PI-033 (Menu drawer), SCR-PA/PI-027 (Profile & settings), SCR-PA/PI-027b (Edit profile), SCR-PA/PI-026 (Saved addresses), SCR-PA/PI-026a (Add address sheet)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Menu drawer (SCR-PA/PI-033) | A side menu with Private transport, My subscriptions, Saved addresses, Profile & settings, Help, Log out | Taps **Profile & settings** | Opens the settings list |
| 2 | Profile & settings (SCR-PA/PI-027) | Their User ID, name, photo, language, notification preferences, Save Home & Work, Saved Addresses, Default Payment Method, Help & Support, logout and delete-account options | Taps the **profile card** | Opens Edit profile |
| 3 | Edit profile (SCR-PA/PI-027b) | Avatar (tap to take or upload a photo), full-name field, notification toggle, and an **SOS (emergency) contacts** list | Updates name/photo, adds an emergency contact, taps **Save** | Profile updated. *(Language is changed in Settings/onboarding, not here.)* |
| 4 | Saved addresses (SCR-PA/PI-026) | A list with **Home** and **Work** shortcuts plus any labelled addresses | Taps to add Home | Opens the map to drop a pin |
| 5 | Add address (SCR-PA/PI-026a) | After dropping the pin: **Address Line 1 / 2 / 3** and a free-text **Label** ("Gym", "Mum's House") | Fills the lines, taps **Save** | The address is saved and appears as a one-tap shortcut when booking |
| 6 | Profile & settings (SCR-PA/PI-027) | A **Default Payment Method** option — Cash (default), LankaQR, or OnePay | Picks their preferred default | Future bookings pre-select this method |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Photo upload fails | An upload progress indicator that errors out | Retry the upload; check connection |
| Emergency contact number invalid | Inline validation error (must be a +94 number) | Re-enter a valid Sri Lankan number |
| No emergency contact set later used for SOS | SOS will refuse with a "no emergency contact" message | Add at least one SOS contact here first |

⚠️ **SPEC GAP:** Saved-address structure (3 lines + label) is well defined, but the specs do not state a **maximum number** of saved/labelled addresses, nor whether duplicate labels are blocked.

---

### Scenario 3: Finding nearby vehicles on the live map and filtering by type
**Platform:** Passenger App
**Who:** Passenger
**Goal:** See what transport is moving nearby and narrow it to the type they care about.
**Preconditions:** Signed in; location permission granted.
**Screens involved:** SCR-PA/PI-010 (Live Map Home), SCR-PA/PI-006 (Mode/type filter), SCR-PA/PI-007 (Vehicle popup)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Live Map (SCR-PA/PI-010) | A full-screen map with coloured moving markers — green buses, red trains, yellow tuks, etc. — each pointing in its direction of travel | Looks at the map | Markers refresh every few seconds as vehicles move |
| 2 | Live Map (SCR-PA/PI-010) | A **filter button** (top-right) and a bottom "Where to?" panel with Home/Work shortcuts | Taps the filter button | Opens the filter sheet |
| 3 | Mode/type filter (SCR-PA/PI-006) | Toggles for **Mode A (Public — Bus, Train)**, **Mode B (Private)**, **Mode C (Standby)** plus per-type chips each showing a small **colour-tinted vehicle icon** | Turns off what they don't want, taps **Apply** | The map instantly redraws showing only the chosen types |
| 4 | Live Map (SCR-PA/PI-010) | The filtered markers | Taps a bus or private-vehicle marker | A detail popup slides up |
| 5 | Vehicle popup (SCR-PA/PI-007) | For a **public (Mode A)** vehicle: route/line, distance, **ETA**, registration, and driver name/photo | Reads the details | Can close and tap another marker |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Nothing of that type nearby | "No [type] active nearby" banner | Widen the filter or check later |
| A vehicle goes stale (stopped reporting) | Marker shows "Last seen N seconds ago", then disappears | Nothing — the map self-cleans |
| Connection lost | "Connection lost — showing last known" banner; markers dim | Wait; the map auto-recovers when signal returns |
| Tapping a **Mode B** (private) marker | It does **not** show a popup — it opens the access-request screen instead | See Scenario 12 |
| Tapping a busy/engaged Mode C vehicle | No popup — on-demand vehicles that are already on a job are hidden | Nothing |

*(No spec gap identified.)*

---

### Scenario 4: Booking a regular on-demand ride (tuk / sedan / van) for yourself
**Platform:** Passenger App
**Who:** Passenger
**Goal:** Hail a Mode C vehicle to come now and take them somewhere.
**Preconditions:** Signed in; location on; for-me booking (rider = booker).
**Screens involved:** SCR-PA/PI-010 (Live Map), SCR-PA/PI-008 (Location search), SCR-PA/PI-009 (Booking + options), SCR-PA/PI-016 (Payment method), SCR-PA/PI-014 (Finding driver), SCR-PA/PI-015 (Ride in progress)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Live Map (SCR-PA/PI-010) | The "Where to?" panel with Home/Work and recent destinations | Taps "Where to?" | Opens search |
| 2 | Location search (SCR-PA/PI-008) | Pickup and drop fields; the drop accepts a **place/address only**; predictions are geocoded places plus saved/recent | Types or picks the destination | Returns to the booking screen with the route shown |
| 3 | Booking (SCR-PA/PI-009) | The map with pickup/drop, **vehicle-tier cards** each showing the **total fare only** (Tuk Rs 740, Sedan Rs 850…), toggles for **For Me / For Someone** and **Person / Package**, and a payment chip | Selects a tier (e.g. Tuk), confirms **For Me / Person** | Fare confirmed for that tier |
| 4 | Payment method (SCR-PA/PI-016) | **Cash (default)**, **LankaQR (no surcharge)**, **OnePay (+5%)**; the total updates per method | Picks a method, taps **Confirm** | Returns to booking with the method set |
| 5 | Booking (SCR-PA/PI-009) | A **Book Now** button | Taps **Book Now** | The search for a driver begins |
| 6 | Finding driver (SCR-PA/PI-014) | A radar/pulse animation and "Finding a driver… (countdown)" with a free **Cancel** | Waits | When a driver accepts, the screen switches to the active ride |
| 7 | Ride in progress (SCR-PA/PI-015) | Live driver marker moving toward pickup, driver card (photo, name, vehicle, rating, ETA), and a **Start code** to give the driver | Watches the driver approach | Driver arrives, the trip starts after the code is verified |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| No driver accepts within 2 minutes | "No drivers available" message; the request auto-cancels | Try again shortly or pick a different tier |
| Passenger blocked from booking | A blocked banner on the booking screen (after 3 cancellations in a row after a driver had accepted) | Complete the steps to re-enable booking (see Scenario 10) |
| OnePay chosen | The total visibly recalculates with a +5% line | Accept or switch to Cash/LankaQR |
| Cancel before a driver accepts | Returns to the map, **no charge** | Re-book any time |

*(No spec gap identified.)*

---

### Scenario 5: Booking a ride for someone else (Proxy booking)
**Platform:** Passenger App
**Who:** Passenger (the "booker"), arranging a ride for another person (the "rider")
**Goal:** Book a Mode C ride that picks up a different person and confirm that person's exact pickup point.
**Preconditions:** Signed in; the booker knows the rider's name and phone number.
**Screens involved:** SCR-PA/PI-009 (Booking), SCR-PA/PI-010b (Proxy details), SCR-PA/PI-011 (Confirm pickup — rider side), SCR-PA/PI-014 (Finding driver), SCR-PA/PI-015 (Ride in progress)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Booking (SCR-PA/PI-009) | The **For Me / For Someone** toggle | Switches to **For Someone** | Reveals the proxy details panel |
| 2 | Proxy details (SCR-PA/PI-010b) | Rider name and phone (or pick from contacts), and pickup methods: type & search, drop a map pin, **Paste link** (a Google Maps link), or **Request Location** | Enters the rider's details and chooses how to set pickup | If "Request Location", the rider gets an alert; if "Paste link", the **paste sheet (SCR-PA/PI-012a)** opens |
| 2a | Paste-link sheet (SCR-PA/PI-012a) | A **Paste** button + the pasted Google Maps link; once read, a **pin preview**, the reverse-geocoded address and lat/lng, and **Use this location** | Taps **Paste** then **Use this location** | The pin is committed to the pickup; an unreadable link offers **Pick on map** instead |
| 3 | Confirm pickup — rider side (SCR-PA/PI-011) | *(On the rider's phone, if they have the app)* "[Booker] wants your pickup location", a map with a draggable pin, **Share** / **Decline** | Rider adjusts the pin and taps **Share** | The confirmed pickup auto-fills on the booker's screen |
| 4 | Proxy details (SCR-PA/PI-010b) | "Waiting for rider… (5:00)" countdown, then the confirmed pin | Sees the pin fill in | Proceeds to book |
| 5 | Finding driver → Ride in progress (SCR-PA/PI-014 → 015) | Same as a normal ride; the driver's offer is tagged **"Third-party booking"** | Tracks the ride | The driver calls/contacts the **rider**, not the booker |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Rider is not a MageRide user | "Not a MageRide user — enter pickup manually" | Set the pickup by search or map pin instead |
| Rider declines the location request | A fallback prompt to set pickup manually | Enter pickup yourself |
| Request expires (5 minutes) | The request auto-dismisses | Re-send or set pickup manually |
| Too many location requests sent | The system limits requests (5/hour, 30/day per booker) | Wait before requesting again |
| Who pays? | If **Cash**, the **rider** pays the driver; if **LankaQR/OnePay**, the **booker** is charged | Choose the method accordingly |

*(No spec gap identified.)*

---

### Scenario 6: Sending a package delivery
**Platform:** Passenger App
**Who:** Passenger (the sender)
**Goal:** Send a parcel from a pickup point to a recipient, choosing size and how to pay.
**Preconditions:** Signed in.
**Screens involved:** SCR-PA/PI-009 (Booking), SCR-PA/PI-012 (Package booking), SCR-PA/PI-016 (Payment method)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Booking (SCR-PA/PI-009) | The **Person / Package** toggle | Switches to **Package** | Opens package booking |
| 2 | Package booking (SCR-PA/PI-012) | A size selector **S / M / L** with a helper hint and info icon directly below | Picks a size | The hint updates: **S** "Up to 5 kg · backpack/motorbike box"; **M** "Up to 20 kg · tuk or car trunk"; **L** "Over 20 kg · van or truck" |
| 3 | Package booking (SCR-PA/PI-012) | Item description, recipient name and **+94** phone, **pickup** (Search / Map / Paste link) and **drop-off** (Search / Map / Paste link / **Request** from recipient) | Fills in the details | Pickup and drop-off set; choosing **Paste link** for either opens the paste sheet |
| 3a | Paste-link sheet (SCR-PA/PI-012a) | A **Paste** button + the pasted Google Maps link (e.g. copied from WhatsApp); once read, a **pin preview**, the reverse-geocoded address and lat/lng, and **Use this drop-off** | Taps **Paste** then **Use this drop-off** | The drop-off pin is committed; an unreadable link offers **Pick on map** instead |
| 4 | Package booking (SCR-PA/PI-012) | Payment options: Cash, LankaQR, OnePay, and **Cash on Delivery (COD)** | Chooses a payment method | Method set |
| 5 | Payment / Book | "Get estimate / Book" — the fare uses the **same tariff as a normal ride** | Taps **Book** | A driver is dispatched the same way as a ride |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Chosen size too big for available vehicles | The driver still sees the size and may decline; another suitable driver is found | Wait, or pick a size/vehicle that fits |
| Paste-link can't be read | "Couldn't read that link — pick on map" | Drop the pin manually |
| Recipient phone invalid | Inline validation (must be +94) | Re-enter the number |

⚠️ **SPEC GAP:** The size hint gives weight/vehicle guidance, but there is **no stated maximum weight or dimension** that hard-blocks a booking — a sender could pick "L" for an oversized item. QA should confirm whether the platform enforces any absolute upper limit.

---

### Scenario 7: Tracking a package you have sent (sender view)
**Platform:** Passenger App
**Who:** Passenger (the sender)
**Goal:** Watch the parcel travel and hold the pickup code the driver needs.
**Preconditions:** A package booking has been made and a driver assigned.
**Screens involved:** SCR-PA/PI-020 (Package tracking — sender), SCR-PA/PI-028 (VoIP call)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Package tracking — sender (SCR-PA/PI-020) | A live map with the driver moving, a 4-step status bar (**Pickup Pending → Picked Up → In Transit → Delivered**), and a **Pickup code** to give the driver | Watches the driver approach | The driver arrives at pickup |
| 2 | Package tracking — sender (SCR-PA/PI-020) | The **Pickup code** clearly shown | Reads the code to the driver (or shows it) | When the driver enters it correctly, the status moves to **Picked Up** |
| 3 | Package tracking — sender (SCR-PA/PI-020) | A **Call** button | Taps to call the driver if needed | An in-app call connects with the number hidden on both sides |
| 4 | Package tracking — sender (SCR-PA/PI-020) | Status advances to **In Transit** then **Delivered** | Watches | Tracking completes when the recipient's delivery code is verified |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Driver enters wrong pickup code repeatedly | After 5 wrong attempts the pickup is locked and sent to support | Confirm the code on this screen; contact support if needed |
| Connection drops | Status updates pause, then catch up | Wait for signal to return |

*(No spec gap identified.)*

---

### Scenario 8: Receiving a package — with the app and without the app
**Platform:** Passenger App **and** Passenger Web (no-login)
**Who:** Unregistered Recipient (or a registered recipient)
**Goal:** Track the incoming parcel and hand over the delivery code to complete it.
**Preconditions:** A package is out for delivery; the driver has confirmed pickup.
**Screens involved:** SCR-PA/PI-021 (Package tracking — recipient), passenger.mageride.lk (no-login web track page)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | *(Trigger)* | **When the driver confirms pickup**, the recipient is notified | — | Branch depends on whether they have the app |
| 2a | Recipient tracking (SCR-PA/PI-021) | 📱 **App:** A phone alert "📦 Package on the way — [Driver] · ETA NN min" opens a live map, status bar, and the **Delivery code** | Taps the alert, watches the driver | Hands the code to the driver on arrival |
| 2b | passenger.mageride.lk | 🌐 **Web (no app):** A text message with a link opens a stripped-down web page — map, status, and the **Delivery code**, no login | Opens the link, watches | Hands the code to the driver on arrival |
| 3 | Either | The **Delivery code** shown clearly | Gives the code to the driver | Driver enters it; status becomes **Delivered** and the trip completes |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Recipient is not at the address | The driver can complete with a **photo proof** instead of the code | Arrange re-delivery / collect from driver |
| Delivery is **Cash on Delivery** | The recipient pays the driver cash; the driver completes the drop with **Delivery completed** (the COD cash is reconciled separately) | Have the cash ready |
| Web link expired | The page no longer loads the trip | Ask the sender to re-share / contact support |

> 🌐 **Web vs 📱 App:** The web page is intentionally bare (no menus, no account) — it shows only the map, status, and delivery code. The app version adds notifications and the full driver card.

*(No spec gap identified.)*

---

### Scenario 9: Booking a scheduled future ride
**Platform:** Passenger App
**Who:** Passenger
**Goal:** Book a ride for a specific future date and time rather than now.
**Preconditions:** Signed in.
**Screens involved:** SCR-PA/PI-009 (Booking), SCR-PA/PI-013 (Schedule a ride), SCR-PA/PI-022 (Trip history — Scheduled tab)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Booking (SCR-PA/PI-009) | After choosing a private tier, a **Schedule** button beside **Book Now** | Taps **Schedule** | Opens the date/time picker |
| 2 | Schedule a ride (SCR-PA/PI-013) | A date and time picker with a summary; past times are disabled | Picks a future date/time, taps **Confirm** | The scheduled ride is saved |
| 3 | Trip history — Scheduled (SCR-PA/PI-022) | The booking appears under the **Scheduled** tab | Can review or cancel it later | Reminders are set automatically (1 hour and 15 minutes before) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Picks a time in the past | That time is greyed out / disabled | Choose a valid future time |
| No driver available at dispatch time | Handled like a live ride — if none accept, the passenger is notified | Re-book or try a different time |

⚠️ **SPEC GAP:** The passenger schedule screen does not state how far in advance a ride may be scheduled (e.g. days vs weeks) or whether scheduled rides can be edited (vs cancel-and-rebook). QA should confirm the allowed scheduling window.

---

### Scenario 10: Cancelling a ride after a driver is assigned — the Rs 50 penalty
**Platform:** Passenger App
**Who:** Passenger
**Goal:** Cancel a ride that already has a driver, and understand the penalty.
**Preconditions:** A driver has **accepted** the ride (it is past the "Finding driver" stage).
**Screens involved:** SCR-PA/PI-015 (Ride in progress), SCR-PA/PI-009 (Booking — blocked state)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Ride in progress (SCR-PA/PI-015) | The driver card with a **Cancel (✕)** button | Taps Cancel | A confirmation dialog appears |
| 2 | Ride in progress (SCR-PA/PI-015) | A confirm dialog warning of a **Rs 50** charge | Confirms the cancellation | The ride is cancelled and a Rs 50 penalty is recorded against the account |
| 3 | *(Later)* Booking (SCR-PA/PI-009) | The next time they take a trip, the Rs 50 is **added to that trip's fare** | Pays the next fare as normal | The Rs 50 is settled (it goes to a driver, not kept as a fee) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Cancelling **before** a driver accepts | No charge at all | Cancel freely at the "Finding driver" stage |
| Cancelling **after** the trip has started | The **full fare** applies, not Rs 50 | Avoid cancelling mid-trip |
| Three post-acceptance cancellations in a row | Booking is **disabled** — a blocked banner appears on the booking screen | Clear the outstanding Rs 50 (pay it on a completed ride) and wait the cooldown, or contact support to be reinstated. The counter resets to zero after any completed ride |

⚠️ **SPEC GAP:** The penalty is settled by being added to the *next* trip, but the specs do not describe what the passenger sees if they **never take another trip** (the Rs 50 stays outstanding indefinitely). QA should confirm the passenger-facing display of a lingering outstanding balance.

---

### Scenario 11: Tracking a public bus or train (Mode A) on the live map
**Platform:** Passenger App
**Who:** Passenger
**Goal:** Follow a public bus or train and see its arrival time — no booking, no payment.
**Preconditions:** Signed in; the bus/train operator is on MageRide.
**Screens involved:** SCR-PA/PI-010 (Live Map), SCR-PA/PI-008 (Location/route entry), SCR-PA/PI-009 (Track route), SCR-PA/PI-007 (Vehicle popup)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Live Map (SCR-PA/PI-010) | Green bus and red train markers moving | Either taps a marker or searches a destination | Two ways in (popup or route list) |
| 2 | Vehicle popup (SCR-PA/PI-007) | For a tapped public vehicle: route/line, distance, **ETA**, registration, driver name/photo | Reads arrival info | Can close |
| 3 | Booking screen (SCR-PA/PI-009) | When a destination is chosen, **all direct public routes** are listed — each with route number, description, a **Direct/Transit** tag and a **PUBLIC** label — alongside private tiers | Selects a **public route** | The map zooms out and draws the route line in the vehicle's colour |
| 4 | Booking screen (SCR-PA/PI-009) | The route line, the route **ETA**, and — if the rider is off-route — a **blue dashed walking line** to the nearest halt with "Walk N m to [halt]" | Taps **Track Route** | The live map follows that route; **no fare, no payment** |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Rider already on the route | No blue walking line is drawn | Just board and track |
| No direct route exists | **Transit** options (with transfers) are listed below the direct ones | Choose a transit option |
| Bus stops reporting | Its marker shows "last seen" then drops off | Track another vehicle on the route |

> Note: Mode A is **free public transport** — MageRide only shows position and ETA; there is never a fare or a "Book" button, only **Track Route**.

⚠️ **SPEC GAP:** Public-route arrival times rely on the operator's vehicles actually broadcasting. The specs do not describe what the passenger sees for a scheduled route where **no vehicle is currently reporting** (e.g. a timetable-only fallback). QA should confirm the empty-route experience.

---

### Scenario 12: Requesting access to a private Mode B vehicle (school van, office bus)
**Platform:** Passenger App
**Who:** Passenger
**Goal:** Ask the owner of a specific private vehicle for permission to track it.
**Preconditions:** Signed in; the passenger knows the vehicle (or can see its marker).
**Screens involved:** SCR-PA/PI-010 (Live Map), SCR-PA/PI-024 (Private transport access request)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Live Map (SCR-PA/PI-010) | A grey **Mode B (Private)** marker on the map | Taps the marker | Opens the access-request screen with the Vehicle ID **pre-filled** |
| 2 | Access request (SCR-PA/PI-024) | A Vehicle ID field (pre-filled) and a **Send request** button | Taps Send (or types a Vehicle ID manually and sends) | The request goes to the vehicle's owner/driver |
| 3 | Access request (SCR-PA/PI-024) | A status chip: **Pending / Accepted / Rejected** | Waits for the owner to respond | Once **Accepted**, the vehicle becomes trackable on the map |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Owner rejects the request | The chip shows **Rejected** | Contact the operator directly / re-request later |
| Vehicle ID typed wrong | No matching vehicle | Re-check the ID, or tap the marker instead to pre-fill |
| Vehicle is **Paid** | After acceptance, a monthly fare applies (first month free) | Proceed to subscription payment (Scenario 14/15) |

⚠️ **SPEC GAP:** The access-request screen shows Pending/Accepted/Rejected but does not state a **request expiry** — what the passenger sees if the owner never responds. QA should confirm whether requests time out.

---

### Scenario 13: Tracking an approved Mode B vehicle on the map
**Platform:** Passenger App
**Who:** Passenger (subscriber)
**Goal:** Watch the private vehicle they have been granted access to.
**Preconditions:** The owner has **Accepted** the access request.
**Screens involved:** SCR-PA/PI-010 (Live Map), SCR-PA/PI-007/024 (Vehicle detail)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Live Map (SCR-PA/PI-010) | The granted Mode B vehicle now appears as a trackable marker (it did not before approval) | Taps it | Shows its position and movement |
| 2 | Live Map (SCR-PA/PI-010) | The vehicle moving in real time (e.g. the school van approaching) | Watches its progress and arrival | Tracks until it arrives |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Vehicle off / outside operating hours | The marker may not broadcast (private vehicles publish on a cadence set by the owner) | Track during the vehicle's active hours |
| Access revoked by owner | The passenger **loses visibility** of the vehicle (a revocation alert may appear) | Re-request access if needed |

⚠️ **SPEC GAP:** The specs describe owner-set publish cadence (active vs off-hours) but the **passenger-side** view does not clearly state what is shown when a granted vehicle is in its "off-hours" silent window (no marker vs a "currently offline" note).

---

### Scenario 14: Managing Mode B subscriptions — viewing and unsubscribing
**Platform:** Passenger App
**Who:** Passenger (subscriber)
**Goal:** See their private-vehicle subscriptions and stop one.
**Preconditions:** At least one active Mode B grant.
**Screens involved:** SCR-PA/PI-033 (Menu), SCR-PA/PI-025 (My subscriptions), SCR-PA/PI-025b (Payment history)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Menu (SCR-PA/PI-033) | A **My subscriptions** entry | Taps it | Opens the subscriptions list |
| 2 | My subscriptions (SCR-PA/PI-025) | One card per subscription showing **Paid (amount/month + next-due date) or Free**, a **💳 Pay** button, a **🧾 history** button, and a small **✕ unsubscribe** icon | Reviews a card | Can pay, view history, or unsubscribe |
| 3 | My subscriptions (SCR-PA/PI-025) | The compact **✕ unsubscribe** icon | Taps unsubscribe and confirms | The passenger loses visibility of that vehicle almost immediately |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Subscription is **Free** | No Pay button or amount — just tracking | Nothing to pay |
| After unsubscribing, the card lingers on the owner's side | The passenger no longer sees the vehicle, but the owner keeps the record **muted** until they delete it | To rejoin, **request access again** and wait for the owner to accept |
| Next-due date passes unpaid | The card shows the amount/next-due; status may move to overdue | Pay via the 💳 Pay button |

*(No spec gap identified.)*

---

### Scenario 15: Paying for a ride — Cash, LankaQR, OnePay (+5% surcharge)
**Platform:** Passenger App
**Who:** Passenger
**Goal:** Pay the fare at the end of a trip by the chosen method.
**Preconditions:** A trip has completed; payment is due.
**Screens involved:** SCR-PA/PI-016 (Payment method), SCR-PA/PI-017 (Pay fare), SCR-PA/PI-018 (Trip summary)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Trip summary (SCR-PA/PI-018) | The fare total and a **Pay now** prompt (if not cash-settled at the car) | Taps Pay now | Opens the pay screen |
| 2 | Payment method (SCR-PA/PI-016) | **Cash Rs 500**, **LankaQR Rs 500 (no surcharge)**, **OnePay +5% Rs 525** | Picks a method | Total reflects the choice |
| 3a | Pay fare (SCR-PA/PI-017) | 💵 **Cash:** the driver collects cash directly | Hands over cash | Marked settled |
| 3b | Pay fare (SCR-PA/PI-017) | 📲 **LankaQR:** a **"Pay" button** opens the passenger's bank app with amount and reference pre-filled; **or** a **"Scan driver's QR"** camera option | Pays in the bank app or the driver's QR, then taps **"I've paid"** (optional receipt screenshot) | The driver confirms **"QR payment received"** → success tick (**DriverConfirmedQR**, Scenario 107 — no automatic bank confirmation exists for the driver's own QR) |
| 3c | Pay fare (SCR-PA/PI-017) | 💳 **OnePay:** an in-app payment sheet | Completes the card payment | "Awaiting confirmation (90s)" then success |
| 4 | Trip summary (SCR-PA/PI-018) | A success tick and a receipt | Done | Receipt available in history |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| OnePay payment fails | "Failed — retry / switch to Cash" without losing the trip | Retry or pay cash |
| Payment stuck "pending" | "Awaiting confirmation (90s)" | Wait; if it times out, retry or pay cash |
| LankaQR with no compatible bank app | A scannable QR is shown as a fallback | Scan it from another device/bank app |
| Payment confirmed late, after cash already paid | Counts as an overpayment; routed to admin for a refund | Contact support if a refund is owed |

*(No spec gap identified.)*

---

### Scenario 16: Viewing trip history (past rides, scheduled rides, packages)
**Platform:** Passenger App
**Who:** Passenger
**Goal:** Browse their past and upcoming activity.
**Preconditions:** Signed in.
**Screens involved:** SCR-PA/PI-022 (Trip & schedule history)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Trip history (SCR-PA/PI-022) | Three tabs: **Past · Scheduled · Packages**, each a list of cards (date, route, distance, fare, status) | Switches between tabs | The list updates per tab |
| 2 | Trip history (SCR-PA/PI-022) | Cards load as they scroll | Scrolls down | More history loads automatically |
| 3 | Trip history (SCR-PA/PI-022) | A chosen card | Taps a card | Opens trip details (Scenario 17) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| No history yet | An empty-state illustration | Take a trip first |
| Still loading | Shimmer placeholders | Wait briefly |

*(No spec gap identified.)*

---

### Scenario 17: Viewing trip details and downloading a receipt
**Platform:** Passenger App
**Who:** Passenger
**Goal:** See full details of one trip and get a receipt.
**Preconditions:** At least one past trip.
**Screens involved:** SCR-PA/PI-022 (History), SCR-PA/PI-023 (Trip details)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Trip details (SCR-PA/PI-023) | A map snapshot of the route, the fare breakdown, payment status, plus **Report issue** and **Support** | Reviews the trip | Can act on it |
| 2 | Trip details (SCR-PA/PI-023) | A receipt / invoice download option | Taps to download the receipt | The receipt is saved/shared |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Something was wrong with the trip | A **Report issue** link | Raise a ticket attached to this trip (Scenario 19) |
| Receipt won't download | A failure message | Retry; check connection/storage |

*(No spec gap identified.)*

---

### Scenario 18: Rating a driver after a completed trip
**Platform:** Passenger App
**Who:** Passenger
**Goal:** Leave a star rating and optional comment for the driver.
**Preconditions:** A trip has just completed.
**Screens involved:** SCR-PA/PI-018 (Trip summary), SCR-PA/PI-019 (Rate driver)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Trip summary (SCR-PA/PI-018) | A rating prompt after the trip | Taps to rate | Opens the rating screen |
| 2 | Rate driver (SCR-PA/PI-019) | 1–5 stars, reason chips, and an optional **comment** box | Taps stars, optionally adds reasons/comment | Submits |
| 3 | Rate driver (SCR-PA/PI-019) | A brief submit confirmation | Done | The rating feeds the driver's level |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Submit fails | A loading state that errors | Retry |
| Skips rating | The prompt can be dismissed | Optional — no penalty |

> Note: 4★ and 5★ ratings build the driver's level; ratings of 2★ or below do **not** add level points.

*(No spec gap identified.)*

---

### Scenario 19: Raising a support ticket — with and without a trip attached
**Platform:** Passenger App
**Who:** Passenger
**Goal:** Report a problem, optionally linked to a specific trip, with a screenshot.
**Preconditions:** Signed in.
**Screens involved:** SCR-PA/PI-030 (Support + FAQ), SCR-PA/PI-030a (Raise a ticket sheet)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Support (SCR-PA/PI-030) | An FAQ accordion, a **Raise a ticket** button, and a list of existing tickets | Taps **Raise a ticket** | Opens a slide-up ticket sheet |
| 2 | Raise a ticket (SCR-PA/PI-030a) | An **Issue description** box, a **Related trip** dropdown (past trips), and an **Attach screenshot** button | Describes the issue; optionally picks a trip and attaches a screenshot | Fills the form |
| 3 | Raise a ticket (SCR-PA/PI-030a) | A **Submit ticket** button | Taps Submit | The new ticket is added to the top of "Your tickets" with a status chip |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Description left blank | A validation prompt | Enter a description |
| No trip selected | That's fine — the trip dropdown is optional | Submit without a trip |
| Screenshot upload fails | An error snackbar | Retry the attachment |

*(No spec gap identified.)*

---

### Scenario 20: Using SOS as a passenger during an active ride
**Platform:** Passenger App
**Who:** Passenger
**Goal:** Send an emergency alert with their location during a ride.
**Preconditions:** An active ride; at least one SOS contact set.
**Screens involved:** SCR-PA/PI-015 (Ride in progress), SCR-PA/PI-029 (SOS)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Ride in progress (SCR-PA/PI-015) | An **SOS** button on the ride card | Taps SOS | Opens the SOS screen |
| 2 | SOS (SCR-PA/PI-029) | A large red SOS button, emergency contacts, and a confirm/countdown | Confirms | A text with their **location and trip details** is sent to their emergency contact(s) within seconds |
| 3 | SOS (SCR-PA/PI-029) | A pulsing red "Sending…" then a sent confirmation | Sees confirmation | Alert delivered; MageRide's safety team is also notified |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| No emergency contact set | SOS refuses with a "no emergency contact" message | Add an SOS contact in Edit profile first |
| Pressed by accident | A confirm/countdown allows cancelling | Cancel during the countdown |

*(No spec gap identified.)*

---

### Scenario 21: App offline behaviour — what the passenger sees
**Platform:** Passenger App
**Who:** Passenger
**Goal:** Understand what the app shows when the connection drops.
**Preconditions:** Signed in; loses connectivity.
**Screens involved:** SCR-PA/PI-010 (Live Map), SCR-PA/PI-032 (Offline banner)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Offline banner (SCR-PA/PI-032) | A top banner: **"Connection lost — showing last known"** — the current screen stays visible, not blanked out | Keeps using what's visible | The app preserves the screen |
| 2 | Live Map (SCR-PA/PI-010) | Vehicle markers are **dimmed** at their last-known positions | Waits | The map does not pretend vehicles are still moving |
| 3 | Offline banner (SCR-PA/PI-032) | The banner clears automatically when signal returns (within a few seconds) | — | Live data resumes |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Mid-ride loss of signal | The driver marker freezes with a banner | Wait — it resumes when reconnected |
| Long outage | The banner persists | Move to better coverage |

*(No spec gap identified.)*

---

### Scenario 22: Mandatory app update — blocking dialog vs soft banner
**Platform:** Passenger App
**Who:** Passenger
**Goal:** Understand the difference between a forced update and an optional one.
**Preconditions:** A new app version has been released.
**Screens involved:** SCR-PA/PI-031 (App update prompt)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1a | App update — mandatory (SCR-PA/PI-031) | A **non-dismissible** dialog that blocks the app and points to the app store | Must tap to go update | Cannot use the app until updated |
| 1b | App update — soft (SCR-PA/PI-031) | A **dismissible banner** suggesting an update | Can dismiss and keep using the app | App keeps working; can update later |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Mandatory update but store unavailable | The blocking dialog stays | Retry the store; the app stays locked until updated |
| Soft banner ignored repeatedly | The banner may reappear | Update at convenience |

> 📱 **Android:** mandatory = a full-screen blocking dialog; soft = a snackbar. 🍎 **iOS:** mandatory = a blocking alert; soft = a banner. Both send the user to their respective app store.

*(No spec gap identified.)*

---

═══════════════════════════════════════════════
## SECTION B — DRIVER APP (SCR-DA / SCR-DI)
═══════════════════════════════════════════════

### Scenario 23: New driver registration — phone code, profile setup, reach Home; then optional Mode-C vehicle onboarding (auto-verified)
**Platform:** Driver App
**Who:** Driver (brand new)
**Goal:** Set up the driver profile to reach Home, then optionally onboard a Mode-C standby vehicle that is auto-verified.
**Preconditions:** Fresh install; the driver has their licence; (for vehicle onboarding) their vehicle's insurance, revenue licence and the vehicle to photograph.
**Screens involved:** SCR-DA/DI-001 (Splash), SCR-DA/DI-002 (Language/city), SCR-DA/DI-003 (Phone & code), **SCR-DA/DI-003a (Profile setup)**, SCR-DA/DI-007 (Permissions), SCR-DA/DI-010 (Home), **SCR-DA/DI-026a (No-vehicle popup)**, **SCR-DA/DI-004 → 004a/004b/004c (Vehicle onboarding, 4 steps)**, SCR-DA/DI-006 (Vehicle onboarding status)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Language/city (SCR-DA/DI-002) | A **3-slide feature-infographic carousel at the top** (auto-advancing / swipeable, like the passenger onboarding) introducing the core features — vehicle onboarding, 15-second dispatch, Directional Travel, in-app wallet & daily fee; below it, language as **vertical boxes — Sinhala first & default**, then Tamil, English; plus an operating-city list **loaded from the database** (admin-managed launch cities) | Swipes the slides, picks language and city | Moves to login |
| 2 | Phone & code (SCR-DA/DI-003) | A +94 phone field then a 6-box code (drivers sign in by **phone code only — no Google login**) | Enters number and the texted code | Account created → Profile setup |
| 3 | Profile setup (SCR-DA/DI-003a) | A **required profile photo**, a **driver name** field, and **driving-licence front + back** capture; an auto-read card shows the fields **the AI (Gemini Flash) reads from the licence** — **Licence no, Expiry, NIC no, and Allowed vehicle types** | Adds photo + name, photographs the licence; **if a field is unclear in the scan, types it in** | Profile saved; **any field the driver typed in is flagged for Admin / Verification-Officer verification (⚑)**; **no vehicle needed** → permissions |
| 4 | Permissions (SCR-DA/DI-007) | Location/notification prompts | Grants them | **Home (SCR-DA/DI-010) opens** — the driver has reached the app with no vehicle |
| 5 | Home / My Vehicles (SCR-DA/DI-026a) | Because there are **no vehicles**, a popup asks **"Onboard a Mode C (Standby Vehicle)?"** | Taps **Yes** (or opens it later from the menu) | Opens the 4-step vehicle onboarding |
| 6 | Vehicle onboarding · Step 1/4 (SCR-DA/DI-004) | **Vehicle type + Registration No** (no permit / tracker field — those are Fleet-Portal/Mode-A) | Enters them | → Step 2/4 |
| 7 | Steps 2–4 (SCR-DA/DI-004a/004b/004c) | Capture **Insurance**, **Revenue licence**, then **front & back photos** (plate visible); each shows **Done** + an AI auto-read card (insurance expiry; revenue-licence no + expiry; the plate matched against the Registration No) | Photographs each and confirms/edits the read fields | Each step is **saved on completion**; on Step 4, taps **Submit for review**. **Any doubtful (low-confidence) or driver-edited field sets that step Pending; a plate that doesn't match the Registration No sets the photos step Pending — all flagged (⚑) for admin verification** |
| 8 | Vehicle onboarding status (SCR-DA/DI-006) | A **4-document** list — vehicle details, insurance, revenue licence, photos — each **Verified** or **Pending** | Waits | When **all four are Verified the vehicle is auto-approved** (no officer) and shows **Approved** in My Vehicles; any Pending goes to a Verification Officer. A vehicle with **at least one step saved but not all four** shows **Incomplete** in My Vehicles, and re-opening onboarding **resumes at its next incomplete step** (not Step 1) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| No profile photo | **Save & continue** is disabled (photo is required) | Add a profile photo |
| A document can't be auto-read (e.g. blurry plate) | That document shows **Pending**; the vehicle waits on a Verification Officer | Re-photograph in good light; the officer can verify manually |
| A document is rejected | Shows **Rejected** with a reason and a re-upload button | Re-photograph and resubmit |
| The licence scan is unclear, so the driver typed in the NIC or allowed vehicle types | Those typed fields are **flagged for admin verification (⚑)** and the driver/vehicle waits on a Verification Officer | Nothing — the officer confirms (or edits) the typed values in the Admin Portal |
| An onboarding step's reading is **doubtful**, or the plate **doesn't match** the Registration No | That step shows **Pending** and is sent to a Verification Officer | Re-photograph clearly; the officer can confirm or correct it |
| Driver closed the app part-way through onboarding | The vehicle shows **Incomplete** in My Vehicles; re-opening onboarding **resumes at the next incomplete step** | Continue from where they left off |
| Driver tries to go Online with no approved vehicle | The Go Online toggle is disabled with a prompt | Onboard a Mode C vehicle, or get assigned/shared a Mode A/B vehicle |
| Driver wants a bus/permit vehicle | The Driver App onboards **Mode C only** | Register Mode A/B vehicles + permits in the **Fleet Portal** |

> **Note:** Insurance and revenue-licence **expiry** auto-suspends dispatch via a back-office rule (E-03); renewal re-enables it.

> **Note:** Once a vehicle's four steps are all complete and it is **Approved**, the onboarding wizard is "done": the next time **Vehicle Onboarding** is opened (from the menu) or **＋** is tapped in My Vehicles, it starts a **fresh Step 1/4 for a NEW vehicle** (a part-onboarded vehicle instead resumes at its next incomplete step).

---

### Scenario 24: Going online (standby toggle) and waiting for a ride request
**Platform:** Driver App
**Who:** Driver
**Goal:** Become available so the system can offer them rides.
**Preconditions:** Approved driver with at least one approved vehicle; wallet able to cover the daily fee for a 2nd trip.
**Screens involved:** SCR-DA/DI-010 (Dashboard), SCR-DA/DI-011 (Mode A/B home dashboard), SCR-DA/DI-012 (Standby toggle)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Dashboard (SCR-DA/DI-010) | A full-screen map showing **only the driver's own active vehicle** (other drivers' vehicles are never drawn here), a header with **Level badge, rating, wallet**, a **Daily fee** chip, the active vehicle, and a big **ONLINE** toggle. **There is no top-left hamburger** — navigation is via the bottom **Menu** tab | Flips the toggle to **ONLINE (Mode C)** | The driver enters the available pool |
| 2 | Dashboard (SCR-DA/DI-010) | "First trip FREE today" indicator and today's trip/earnings stats | Waits for offers | The system can now send ride offers |
| 3 | Dashboard (SCR-DA/DI-010) | A live map of self and demand | Stays online | A ride offer can arrive at any time (Scenario 25) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Offline | A grey overlay: "Go online to receive rides" | Toggle online |
| Wallet too low for a 2nd trip | A low-balance warning (below Rs 200) / "Top Up Required" banner | Top up the wallet (Scenario 43) |
| Active vehicle is a **Mode A** (public bus/train) or **Mode B** (private) vehicle | The **Mode A/B home dashboard (SCR-DA/DI-011)** loads instead of the standby map — it carries only **Start Journey** (green) and **End Journey** (red), with the **vehicle type & number shown below the route card** | Use the journey controls (no daily fee on Mode A; Mode B is a monthly fee) |

> **Note — Mode A/B home dashboard & GPS device (SCR-DA/DI-011):** When the active vehicle is a public-transport bus (Mode A) or a private vehicle (Mode B), SCR-DA/DI-011 *is* the home screen. If a **paired GPS device is ingesting** (the journey was auto-started when the vehicle's **ignition** came on), the dashboard already shows the journey **started** and offers **End Journey** — opening the app shows "Journey started" automatically. When the **ignition is off** the device stops publishing and the green **Start Journey** button returns. The **dashboard can override the device** — the driver may manually Start or End the journey here regardless of the device's state.

*(No spec gap identified.)*

---

### Scenario 25: Receiving a ride offer — the 15-second accept window (Mode C)
**Platform:** Driver App
**Who:** Driver
**Goal:** Accept a ride within the countdown.
**Preconditions:** Online and available.
**Screens involved:** SCR-DA/DI-014 (Incoming dispatch)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Incoming dispatch (SCR-DA/DI-014) | A full-screen takeover with a **15-second countdown ring**, the fare, pickup distance, vehicle category, payment type, pickup→drop, and any badges (Third-party booking / Package + size / Directional) | Reads the offer | The ring counts down |
| 2 | Incoming dispatch (SCR-DA/DI-014) | **Accept** and **Reject** buttons; the last 5 seconds pulse red | Taps (or slides) **Accept** | The driver is assigned; the daily fee is handled (free on the first trip of the day) |
| 3 | (Transition) | The ride becomes active | Proceeds to navigate | Moves to the active-ride screen (Scenario 26) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Countdown expires | The offer auto-dismisses and passes to another driver | Wait for the next offer |
| Another driver wins it first | "Offer taken" then the next offer | Nothing — it's automatic |
| 2nd trip of the day | A daily-fee deduction note appears | Accept knowing the fee is taken from the wallet |
| App was asleep | The offer wakes the phone with sound and vibration | Respond within 15 seconds |

*(No spec gap identified.)*

---

### Scenario 26: Navigating to pickup, arriving, and starting the ride with the passenger code
**Platform:** Driver App
**Who:** Driver
**Goal:** Reach the passenger and start the trip securely.
**Preconditions:** A ride has been accepted.
**Screens involved:** SCR-DA/DI-015 (Active ride)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Active ride (SCR-DA/DI-015) | A navigation map to the pickup, source/drop, distances, and a **Start (code)** button; plus **Call** and **SOS** | Drives to the pickup | The status becomes "Driver arrived" when within the pickup area |
| 2 | Active ride (SCR-DA/DI-015) | A field to enter the **passenger's start code** | Asks the passenger for the code and enters it | If correct, the trip starts (**In Progress**) |
| 3 | Active ride (SCR-DA/DI-015) | The route to the destination with live progress | Drives to the destination | Reaches the drop, ready to complete |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Wrong start code | "Incorrect code" error | Re-ask the passenger and re-enter |
| Passenger not there | After 5 minutes (+2 reminder texts) the passenger is a no-show (Rs 100 to them; driver compensated) | Wait the grace period, then mark no-show |
| Need to call | The **Call** button connects without revealing either number | Use the in-app call |

*(No spec gap identified.)*

---

### Scenario 27: Completing a ride — cash vs LankaQR vs OnePay
**Platform:** Driver App
**Who:** Driver
**Goal:** End the trip and settle payment.
**Preconditions:** The trip is In Progress.
**Screens involved:** SCR-DA/DI-015 (Active ride), SCR-DA/DI-020 (Earnings)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Active ride (SCR-DA/DI-015) | An **End** button at the destination | Taps **End** | The fare is finalised; payment is collected by the passenger's chosen method |
| 2a | Active ride (SCR-DA/DI-015) | 💵 **Cash:** collect the fare amount | Takes the cash | Settled |
| 2b | Active ride (SCR-DA/DI-015) | 📲 **LankaQR:** the passenger pays via their bank app or scans the driver's QR, then taps "I've paid" | Taps **"QR payment received" → Confirm** | Settled as **DriverConfirmedQR**; earning posts (Scenario 107) |
| 2c | Active ride (SCR-DA/DI-015) | 💳 **OnePay:** the passenger pays in-app (+5%) | Waits for confirmation | Settled once confirmed |
| 3 | Earnings (SCR-DA/DI-020) | The completed trip's fare appears in earnings | Reviews | The driver's earning is posted only once payment is final |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Passenger's OnePay fails | The passenger can retry or switch to cash | Accept cash if they fall back |
| Payment pending a while | Earning posts only when payment finalises | Wait for confirmation |

*(No spec gap identified.)*

---

### Scenario 28: Receiving and declining a ride offer
**Platform:** Driver App
**Who:** Driver
**Goal:** Turn down an offer without penalty (within reason).
**Preconditions:** Online; an offer arrives.
**Screens involved:** SCR-DA/DI-014 (Incoming dispatch)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Incoming dispatch (SCR-DA/DI-014) | The offer with **Reject** | Taps **Reject** | The offer passes immediately to the next eligible driver |
| 2 | Dashboard (SCR-DA/DI-010) | Back to the available state | Stays online | Eligible for the next offer |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Repeated declines | The driver's **acceptance rate** (shown on the Level screen) drops | Accept more offers to keep the rate up |
| Package size they can't carry | They may reject; pattern rejections are downweighted, not penalised | Reject if genuinely unsuitable |

⚠️ **SPEC GAP:** A low acceptance rate clearly affects the driver's level/score, but the specs do not state a **threshold** at which declines actively reduce dispatch priority versus merely informing the level points. QA should confirm the exact acceptance-rate consequence.

---

### Scenario 29: Receiving a package delivery request — the three delivery bottom sheets (review, pickup OTP, complete)
**Platform:** Driver App
**Who:** Driver
**Goal:** Review, pick up and deliver a parcel through the three delivery bottom sheets and the two security codes.
**Preconditions:** Online; a package offer arrives.
**Screens involved:** SCR-DA/DI-014 (Incoming dispatch), SCR-DA/DI-016a (Review & start · sheet 1/3), SCR-DA/DI-016b (Pickup & OTP · sheet 2/3), SCR-DA/DI-016c (Complete · sheet 3/3)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Incoming dispatch (SCR-DA/DI-014) | The offer with a **"Package · S/M/L"** badge | Accepts | Opens the first delivery bottom sheet |
| 2 | Review & start — sheet 1/3 (SCR-DA/DI-016a) | The **pickup & drop distances**, the **payment method**, and the **sender and recipient phone numbers, each with a Call button** (a tap places a real mobile-phone voice call to that party) | Reviews; if satisfied taps **Start delivery** | Proceeds to the pickup sheet. **If not satisfied, taps Cancel** → the request is re-dispatched to the **next eligible driver** |
| 3 | Pickup & OTP — sheet 2/3 (SCR-DA/DI-016b) | The **pickup location on the map**, a **Call sender** button, **SOS**, and a **Pickup OTP** entry | Drives to the sender, asks for the pickup OTP and enters it | On a correct OTP the parcel is **Picked Up**, the recipient is notified, and the third sheet appears |
| 4 | Complete — sheet 3/3 (SCR-DA/DI-016c) | The recipient's **Delivery OTP** entry, a **Photo proof** option, **both the sender & recipient numbers, each with a Call button**, and a **Delivery completed** button (this replaces the old "Cash received (COD)" button) | Asks the recipient for the delivery OTP and enters it (or uses photo proof if absent), then taps **Delivery completed** | Delivery confirmed; trip **Completed**; both parties get a confirmation |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Driver not satisfied at review | **Cancel** on sheet 1/3 re-dispatches the job to the next eligible driver | Cancel freely before starting |
| Wrong OTP (either end) | "Incorrect code"; after 5 wrong attempts it locks and goes to support | Re-confirm the code |
| Recipient absent | A **Photo proof** option stands in for the delivery OTP on sheet 3/3 | Take a clear proof photo to complete |
| COD parcel | There is **no "Cash received" button** — the driver taps **Delivery completed**; any COD/uncollected-cash is reconciled separately (unconfirmed 24h → Disputed) | Collect the cash, then complete the delivery |

*(No spec gap identified.)*

---

### Scenario 30: Photo proof of delivery when the recipient is unavailable
**Platform:** Driver App
**Who:** Driver
**Goal:** Complete a delivery when no one can give the delivery code.
**Preconditions:** A package is at the drop-off; recipient absent.
**Screens involved:** SCR-DA/DI-016c (Complete · delivery sheet 3/3)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Complete — sheet 3/3 (SCR-DA/DI-016c) | A **📷 Photo proof** button beside the Delivery OTP field | Taps Photo proof | Opens the camera |
| 2 | Complete — sheet 3/3 (SCR-DA/DI-016c) | The camera | Photographs the parcel where it was left | The photo (with location) is attached as proof |
| 3 | Complete — sheet 3/3 (SCR-DA/DI-016c) | A success confirmation and the **Delivery completed** button | Taps **Delivery completed** | The delivery is completed with proof instead of a code |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Photo unclear | The proof may be disputed later | Take a clear, well-lit photo |
| COD parcel and no one to pay | Cannot collect cash | Do not leave a COD parcel; contact support/return |

⚠️ **SPEC GAP:** Photo-proof completion is allowed for absent recipients, but the specs do not state whether photo proof is **blocked for COD** parcels (where cash must be collected). QA should confirm the COD-plus-absent-recipient handling.

---

### Scenario 31: Cash on Delivery (COD) — collecting cash and completing the delivery
**Platform:** Driver App
**Who:** Driver
**Goal:** Collect the COD cash and complete the delivery (there is no longer a separate "Cash received" button).
**Preconditions:** A COD package is being delivered.
**Screens involved:** SCR-DA/DI-016c (Complete · delivery sheet 3/3)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Complete — sheet 3/3 (SCR-DA/DI-016c) | The offer/review showed the payment method as **Cash on Delivery (COD)**; this sheet shows the Delivery OTP and a **Delivery completed** button (the old "Cash received (COD)" button has been **removed**) | Collects the cash from the recipient | Has the cash in hand |
| 2 | Complete — sheet 3/3 (SCR-DA/DI-016c) | The **Delivery completed** button | Enters the delivery OTP (or photo proof) and taps **Delivery completed** | The delivery is **Completed**; the COD/uncollected-cash is **reconciled separately** and the driver's earning posts once payment is final |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Recipient won't/can't pay | The driver should not hand over the parcel or complete | Do not complete; contact support |
| COD cash left unreconciled 24 hours | The delivery is flagged as **Disputed** and sent to admin | Collect the cash at delivery |

*(No spec gap identified.)*

---

### Scenario 32: Using the Directional Travel filter — destination and daily limit
**Platform:** Driver App
**Who:** Driver
**Goal:** Only receive offers that head roughly toward where the driver is going.
**Preconditions:** Online; the driver has Directional uses left today.
**Screens involved:** SCR-DA/DI-013 (Directional Travel)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Directional Travel (SCR-DA/DI-013) | A destination entry (search / map pin / **Home**), **Uses left today (e.g. 1 of 2)**, a **Max duration** (default 2h), and a map preview with a heading marker | Sets a destination | Ready to activate |
| 2 | Directional Travel (SCR-DA/DI-013) | A **Set Direction** button | Taps Set Direction | Consumes one use; the filter becomes active |
| 3 | Directional Travel (SCR-DA/DI-013) | A persistent banner: "To: Nugegoda · 1:42 left · Uses left: 1 · Turn Off" | Drives; receives only offers heading that way | Offers are filtered toward the destination |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Filter about to expire | A reminder push 10 minutes before; the banner pulses | Re-set if still travelling that way (uses another use) |
| Turning it off early | "Turn Off" **still consumes the use** (to prevent gaming) | Only turn off when genuinely done |
| Out of uses | **Set Direction** is disabled | Wait until tomorrow (default 2/day) |
| Goes offline | The filter clears automatically | Re-set after going online again |
| No directional rides arrive | No penalty | Keep driving normally |

*(No spec gap identified.)*

---

### Scenario 33: Viewing the Job Board and posting intent for a scheduled ride
**Platform:** Driver App
**Who:** Driver (Level 2 or above)
**Goal:** Express interest in an upcoming scheduled ride.
**Preconditions:** Online; driver level is **2+** (Level 1 is excluded from the Job Board).
**Screens involved:** SCR-DA/DI-017 (Job Board)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Job Board (SCR-DA/DI-017) | A list of **future scheduled rides within 30 km** — each card with time, route, fare, distance | Browses jobs | Sees what's coming up |
| 2 | Job Board (SCR-DA/DI-017) | A **Post intent** button on a card (the only action available — no direct accept here) | Taps Post intent | The card shows **"Intent posted ✓"** |
| 3 | Job Board (SCR-DA/DI-017) | The posted intent | Waits | At 30 minutes before the ride, the system offers it to the closest intent-poster (Scenario 34) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Driver is Level 1 | "Reach Level 2 to access Job Board" | Build level by completing rides with good ratings |
| No jobs nearby | "No jobs within 30 km" | Check later |
| A job expires | The card fades out | Pick another |

*(No spec gap identified.)*

---

### Scenario 34: Receiving a dispatch offer at 30 minutes before a scheduled ride
**Platform:** Driver App
**Who:** Driver who posted intent
**Goal:** Be offered the scheduled ride at the right time.
**Preconditions:** The driver posted intent and is closest by level/distance.
**Screens involved:** SCR-DA/DI-014 (Incoming dispatch), SCR-DA/DI-018 (Scheduled rides)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | (30 min before) | A scheduled reminder/offer arrives | — | The ride is dispatched as a normal offer |
| 2 | Incoming dispatch (SCR-DA/DI-014) | The same 15-second offer screen as a live ride | Accepts | The scheduled ride becomes the driver's active job |
| 3 | Scheduled rides (SCR-DA/DI-018) | The accepted scheduled ride listed | Prepares to drive | Proceeds at the scheduled time |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Driver doesn't accept in time | The offer cascades to the next intent-poster | Watch for the offer near the time |
| No-show on an accepted scheduled ride | The driver's **level drops by one** | Honour accepted scheduled rides |

*(No spec gap identified.)*

---

### Scenario 35: Accepting and completing a pre-dispatched scheduled ride
**Platform:** Driver App
**Who:** Driver
**Goal:** Carry out a scheduled ride end to end.
**Preconditions:** The scheduled ride was accepted at the 30-minute mark.
**Screens involved:** SCR-DA/DI-018 (Scheduled rides), SCR-DA/DI-015 (Active ride)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Scheduled rides (SCR-DA/DI-018) | The accepted ride with its time | Heads to the pickup at the scheduled time | The ride proceeds like any active ride |
| 2 | Active ride (SCR-DA/DI-015) | Navigation, the **Start (code)**, then **End** | Picks up (code), drives, ends | Trip completes and payment settles |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Passenger cancels before pickup | The ride is cancelled (passenger penalty rules apply) | Move on to other jobs |
| Daily fee | The scheduled ride counts toward the day's trips for the daily fee | Ensure wallet covers the fee |

*(No spec gap identified.)*

---

### Scenario 36: The Driver Level system — Level 1 restrictions and progressing to Level 2+
**Platform:** Driver App
**Who:** Driver
**Goal:** Understand levels, how to climb, and what Level 1 cannot do.
**Preconditions:** An active driver (everyone starts at Level 3).
**Screens involved:** SCR-DA/DI-019 (Driver Level & stats), SCR-DA/DI-010 (Dashboard badge)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Driver Level (SCR-DA/DI-019) | A **Level badge (L1/L2/L3)**, a points progress bar, acceptance rate, and no-show history | Reviews their standing | Understands progress to the next level |
| 2 | Driver Level (SCR-DA/DI-019) | Points fill from good ratings (5★ = 5 points, 4★ = 4 points; **500 points = +1 level**) | Keeps earning good ratings | Levels up at 500 points |
| 3 | Dashboard (SCR-DA/DI-010) | The level badge in the header | Sees it everywhere | Higher level improves dispatch ranking and unlocks the Job Board |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| 3 passenger reports | Level drops by one and a temporary delisting | Improve service; appeal via admin if needed |
| Dropping to Level 1 | Loses Job Board / scheduled-ride access (but can still do immediate Mode C — **not** a permanent ban) | Rebuild rating to climb back |
| Near delisting | A "3 reports → delisting" warning | Address the cause of reports |

*(No spec gap identified.)*

---

### Scenario 37: Managing multiple registered vehicles and switching the active one
**Platform:** Driver App
**Who:** Driver
**Goal:** Keep several vehicles and choose which one is live.
**Preconditions:** The driver has more than one approved vehicle.
**Screens involved:** SCR-DA/DI-026 (Vehicle management & switcher)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Vehicle management (SCR-DA/DI-026) | A label **"Only one vehicle can be live at a time"**, a **My vehicles** list with an active selector — each vehicle showing **Approved** (all 4 onboarding steps complete & verified) or **Incomplete · Step N of 4** (with a **Resume** link) — plus a separate **"Temporarily assigned to me (FLEET)"** group | Reviews their vehicles | Sees which is active and which are still incomplete |
| 2 | Vehicle management (SCR-DA/DI-026) | Radio selectors for the active vehicle | Selects a different vehicle as active | That vehicle becomes the live one (the dashboard chip updates) |
| 3 | Dashboard (SCR-DA/DI-010) | The active vehicle's registration in the header chip | Goes online | Operates as that vehicle |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Trying to run two at once | Blocked — "Only one vehicle can be live at a time" | Switch instead |
| A vehicle still **Incomplete** | Shows **Incomplete · Step N of 4** with **Resume**; only **Approved** Mode C vehicles can go live | Resume onboarding from the next step |
| Tapping **＋** to add a vehicle | If the current vehicle is finished/Approved, ＋ opens a **fresh Step 1/4 for a NEW vehicle**; an Incomplete vehicle instead resumes its next step | Add a new vehicle, or resume the incomplete one |
| Fleet-assigned vehicle | Appears in the **Temporarily assigned** group, selectable to go online; auto-expires | Use within the validity window (Scenario 52) |

*(No spec gap identified.)*

---

### Scenario 38: Registering a new vehicle — document upload and approval wait
**Platform:** Driver App
**Who:** Driver
**Goal:** Add another Mode-C vehicle to their account.
**Preconditions:** An existing approved driver.
**Screens involved:** SCR-DA/DI-026 (Vehicle management), SCR-DA/DI-004 → 004a/004b/004c (Vehicle onboarding, 4 steps), SCR-DA/DI-006 (Vehicle onboarding status)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Vehicle management (SCR-DA/DI-026) | An **Add (＋)** button. Because the existing vehicle is already finished/Approved, ＋ opens a **fresh Step 1/4 for a NEW vehicle** | Taps Add | Opens the Mode-C onboarding wizard at Step 1/4 |
| 2 | Vehicle onboarding (SCR-DA/DI-004 → 004c) | The 4 steps: vehicle type + Registration No, insurance, revenue licence, front/back photos — **each saved on completion** | Photographs each; confirms or edits the auto-read fields | Submits for review (Gemini Flash 3.0 auto-verify) |
| 3 | Vehicle onboarding status (SCR-DA/DI-006) | The new vehicle's **4-document** Verified/Pending list | Waits | **All Verified → auto-approved** (shows **Approved** in My Vehicles) and selectable as active; until then it stays **Incomplete**; any Pending field → Verification Officer |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| A document can't be auto-read | That document shows **Pending** (officer review) | Re-photograph clearly |
| A field reads as **doubtful**, the driver **edited** a field, or the plate **doesn't match** the Registration No | That step shows **Pending** and goes to a Verification Officer | Re-photograph clearly; the officer can confirm or correct it |
| A document rejected | The relevant row shows Rejected + reason | Re-upload |
| Mode A/B vehicle | The Driver App onboards **Mode C only** | Use the Fleet Portal for Mode A/B + permits |

*(No spec gap identified.)*

---

### Scenario 39: Deactivating a vehicle
**Platform:** Driver App
**Who:** Driver
**Goal:** Remove a vehicle they no longer use.
**Preconditions:** The driver has a vehicle to deactivate (not currently live).
**Screens involved:** SCR-DA/DI-026 (Vehicle management)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Vehicle management (SCR-DA/DI-026) | A **Deactivate** action (button or swipe) on a vehicle row | Taps/swipes Deactivate | A confirm prompt appears |
| 2 | Vehicle management (SCR-DA/DI-026) | A confirmation dialog | Confirms | The vehicle is deactivated and removed from the active choices |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Trying to deactivate the live vehicle | Should switch active first | Make another vehicle active, then deactivate |

⚠️ **SPEC GAP:** The specs allow deactivation but do not state whether a deactivated vehicle can be **reactivated** without re-uploading documents, or whether deactivation is permanent. QA should confirm reactivation behaviour.

---

### Scenario 40: Pairing a GPS hardware tracker (IMEI / QR / bind code)
**Platform:** Driver App
**Who:** Driver
**Goal:** Link a physical GPS tracker device to a vehicle.
**Preconditions:** The driver has a tracker device and a vehicle.
**Screens involved:** SCR-DA/DI-027 (GPS tracker pairing)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Tracker pairing (SCR-DA/DI-027) | A vehicle picker, an **IMEI** field, a **Scan device QR** option, and an **Enter bind code** option | Chooses the vehicle and enters the IMEI (or scans the QR / types the bind code) | Identifies the device |
| 2 | Tracker pairing (SCR-DA/DI-027) | A **Pair device** button | Taps Pair | The device is bound to the vehicle |
| 3 | Tracker pairing (SCR-DA/DI-027) | A note on behaviour: once a device is **paired/assigned, the phone no longer ingests GPS** for that vehicle — the **device becomes the single publisher**. **Mode A/B** tracker vehicles **auto start/end journeys on ignition** (no app needed) and the **journey start is updated automatically** — opening the app shows the dashboard already at **"Journey started"**; **Mode C** tracker GPS is used **only while online** | Reads the note | Understands the tracker is now the sole publisher and how it behaves per mode |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Same IMEI already in use elsewhere | A quarantine notice (both devices held for admin review) | Contact support to resolve |
| Tracker goes offline >15 min | A phone alert | Check the device power/connection |
| Large fleet (5,000+ devices) | A note to use the **Admin/Fleet Portal CSV upload** instead | Use the portal bulk path |

> **Note:** After pairing, GPS comes **only from the device**, not the phone. For Mode A/B the journey **auto-starts on ignition** and **auto-ends** when the ignition is off; the driver's dashboard (SCR-DA/DI-011) reflects this automatically and can still **override** it by starting/ending the journey manually.

*(No spec gap identified.)*

---

### Scenario 41: Managing Mode B sharing grants — granting, accepting requests, revoking
**Platform:** Driver App
**Who:** Driver (or owner-operator of a Mode B vehicle)
**Goal:** Control who can track their private vehicle.
**Preconditions:** The driver operates a Mode B vehicle.
**Screens involved:** SCR-DA/DI-028 (Sharing management)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Sharing management (SCR-DA/DI-028) | A **full-device-width per-vehicle selector at the top** (the old "Showing sharing for … temporarily assigned by …" caption box has been **removed** — the selected chip already shows the active vehicle); for the selected vehicle, a share-by-User-ID form with an expiry, incoming **requests**, and the current **grantees** list — each showing the passenger's **name + mobile number** | Reviews requests for that vehicle | Sees who wants access |
| 2 | Sharing management (SCR-DA/DI-028) | **Accept / Reject** on each incoming request | Accepts a request | The passenger gains tracking; their subscription starts |
| 3 | Sharing management (SCR-DA/DI-028) | The grantees list with revoke controls | Revokes a grantee if needed | That passenger loses visibility (a revocation alert is sent) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Multiple vehicles | Each vehicle's requests appear **only under that vehicle** | Switch the per-vehicle selector |
| Grant expiry reached | Access auto-revokes | Re-grant if still needed |
| Unsubscribed passenger | Stays **muted** on the list until deleted | Delete to clear, or leave muted |

*(No spec gap identified.)*

---

### Scenario 42: Viewing wallet balance and the daily-fee deduction logic
**Platform:** Driver App
**Who:** Driver
**Goal:** Understand the wallet and how the daily fee is charged.
**Preconditions:** An active driver.
**Screens involved:** SCR-DA/DI-021 (Wallet & fee status)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Wallet & fee (SCR-DA/DI-021) | A **read-only balance**, the vehicle's **daily-fee rate** (e.g. Three-wheeler Rs 100/day), and today's status (**PAID ✓** / first-trip-free) | Reviews the daily-fee card | Understands what's owed today |
| 2 | Wallet & fee (SCR-DA/DI-021) | "Today: PAID ✓ (1st free)" | Notes the rule: first trip of the day is **free**; the flat fee is taken before the **2nd** trip | Knows the fee is charged once per day, no matter how many trips |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Balance below Rs 200 | A low-balance warning | Top up soon |
| Balance below one day's fee | "Top Up Required" banner; can't take a 2nd trip until topped up | Top up (Scenario 43) |
| Balance goes negative (e.g. a reversal) | The balance can show negative | Top up to restore |
| Mode A vehicle | **No daily fee** (public transport is free) | Nothing |

*(No spec gap identified.)*

---

### Scenario 43: Topping up the wallet — OnePay and LankaQR (no bank transfer)
**Platform:** Driver App
**Who:** Driver
**Goal:** Add money to the wallet, optionally buying discounted bulk credit.
**Preconditions:** An active driver.
**Screens involved:** SCR-DA/DI-021 (Wallet), SCR-DA/DI-022 (Top Up Wallet)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Wallet (SCR-DA/DI-021) | A **Top Up / Buy credit** button | Taps it | Opens the top-up screen |
| 2 | Top Up (SCR-DA/DI-022) | **Card / OnePay / LankaQR** methods and an amount field — **no bank transfer** | Picks a method and amount | Ready to pay |
| 3 | Top Up (SCR-DA/DI-022) | **Bulk credit vouchers** (Rs 1k/2k/3k/5k/10k) with a per-tier discount (e.g. pay Rs 900 → get Rs 1,000) | Optionally buys a voucher | The wallet is credited with the face value at purchase |
| 4 | Top Up (SCR-DA/DI-022) | Processing → success | Confirms payment | Balance increases (count-up animation) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Payment fails | A retry prompt | Retry or change method |
| Expects bank transfer | Not available | Use OnePay/LankaQR/card |

*(No spec gap identified.)*

---

### Scenario 44: Requesting credit from another (reseller-capable) driver
**Platform:** Driver App
**Who:** Driver (requester)
**Goal:** Get wallet credit from a driver who holds bulk credit.
**Preconditions:** The requester knows the other driver's Driver ID.
**Screens involved:** SCR-DA/DI-021 (Wallet), SCR-DA/DI-023 (Request credit)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Wallet (SCR-DA/DI-021) | A **Request credit** button | Taps it | Opens the request screen |
| 2 | Request credit (SCR-DA/DI-023) | A **Driver ID** field and an amount — **no QR scan** (QR scanning has been removed), no special reseller codes | Types the other driver's Driver ID and amount | Sends the request |
| 3 | Request credit (SCR-DA/DI-023) | "Awaiting driver approval" | Waits | When the other driver approves, the **exact amount** is credited (no commission); the requester gets a notification |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Request rejected | A rejection notice | Ask another driver or top up directly |
| Wrong Driver ID | No matching driver | Re-check and re-enter the Driver ID |

*(No spec gap identified.)*

---

### Scenario 45: A reseller driver approving or rejecting an incoming credit request
**Platform:** Driver App
**Who:** Reseller driver (a driver holding bulk credit)
**Goal:** Respond to another driver's credit request.
**Preconditions:** The driver has wallet credit and receives a request.
**Screens involved:** SCR-DA/DI-024 (Credit transfer + requests)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Credit transfer (SCR-DA/DI-024) | An incoming request card (requester name, vehicle, amount), delivered by phone alert | Reviews the request | Decides |
| 2 | Credit transfer (SCR-DA/DI-024) | **Approve / Reject** | Taps Approve | The exact amount is debited from the sender and credited to the requester (no commission) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Insufficient balance | The transfer is blocked | Top up first or reject |
| Don't recognise the requester | Reject | Verify the Driver ID out-of-band |

> Note: a "reseller" is **not** a special account — it is just a driver who bought bulk credit and shares it. There is **no per-transfer commission**; their margin comes only from the voucher discount at purchase.

*(No spec gap identified.)*

---

### Scenario 46: Reseller driver — purchasing bulk vouchers at a tiered discount
**Platform:** Driver App
**Who:** Reseller driver
**Goal:** Buy bulk credit cheaply to later pass on.
**Preconditions:** An active driver with a payment method.
**Screens involved:** SCR-DA/DI-022 (Top Up Wallet)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Top Up (SCR-DA/DI-022) | **Bulk credit voucher** tiles (Rs 1k–10k) each showing its discount | Picks a tier (e.g. Rs 10k) | Sees the discounted price (e.g. pay Rs 9,000) |
| 2 | Top Up (SCR-DA/DI-022) | The pay step | Pays via OnePay/LankaQR/card | The **full face value** is credited to the wallet at purchase |
| 3 | Wallet (SCR-DA/DI-021) | A higher balance | Later transfers credit to other drivers at face value | The discount (margin) was captured at purchase |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Discount differs by tier | Each voucher value has its own discount % (set by Admin) | Choose the best-value tier |
| Payment fails | Retry prompt | Retry/change method |

*(No spec gap identified.)*

---

### Scenario 47: Reseller driver — viewing a commission report
**Platform:** Driver App
**Who:** Reseller driver
**Goal:** See the margin earned from bulk-credit activity.
**Preconditions:** The driver has bought/transferred bulk credit.
**Screens involved:** SCR-DA/DI-025 (Payment/fee history)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Payment history (SCR-DA/DI-025) | A list of top-ups, voucher purchases, and credit transfers (date, amount) | Filters by date range | Reviews their activity |
| 2 | Payment history (SCR-DA/DI-025) | Voucher purchases showing the discount captured | Reviews the margin | Understands their effective "commission" (the voucher discount) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| No transfers yet | An empty/filtered list | Buy/transfer first |

⚠️ **SPEC GAP:** A dedicated "commission report" is implied by the prompt, but the specs only describe a **payment/fee history** (SCR-DA-025) and bulk-voucher discounts captured at purchase — there is **no distinct reseller commission report screen**. QA/product should confirm whether a separate margin-summary view is required, or whether payment history suffices.

---

### Scenario 48: Viewing the earnings summary and payment/fee history
**Platform:** Driver App
**Who:** Driver
**Goal:** Review earnings and the history of fees and payments.
**Preconditions:** An active driver with trips.
**Screens involved:** SCR-DA/DI-020 (Earnings), SCR-DA/DI-025 (Payment/fee history)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Earnings (SCR-DA/DI-020) | **Today / Week / Month** tabs with earnings, a per-trip breakdown, payment-method stats, and the **daily fee deducted** | Switches periods | Reviews totals and trends |
| 2 | Payment history (SCR-DA/DI-025) | Daily-fee deductions, top-ups, transfers (date, vehicle, amount, trips), with a statement download | Filters and downloads a statement | Gets a record for their own books |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Empty period | An empty-state | Pick a period with activity |
| Statement won't download | A failure | Retry; check storage |

*(No spec gap identified.)*

---

### Scenario 49: Using SOS as a driver during an active ride
**Platform:** Driver App
**Who:** Driver
**Goal:** Send an emergency alert during a trip.
**Preconditions:** An active trip; an emergency contact set.
**Screens involved:** SCR-DA/DI-015 (Active ride), SCR-DA/DI-032 (SOS)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Active ride (SCR-DA/DI-015) | An **SOS** button | Taps SOS | Opens the SOS screen |
| 2 | SOS (SCR-DA/DI-032) | A large red SOS with a confirm/countdown | Confirms | A text with the driver's **location and trip** goes to their emergency contact within seconds; MageRide's safety feed is alerted |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| No emergency contact set | SOS refuses | Add a contact in the profile first |
| Accidental press | Cancel during countdown | Cancel |

*(No spec gap identified.)*

---

### Scenario 50: In-app call to the passenger — number hidden from both sides
**Platform:** Driver App
**Who:** Driver
**Goal:** Contact the passenger without exchanging phone numbers.
**Preconditions:** An active ride.
**Screens involved:** SCR-DA/DI-031 (In-app call)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Active ride (SCR-DA/DI-015) | A **Call** button | Taps Call | An in-app call screen opens |
| 2 | In-app call (SCR-DA/DI-031) | A full-screen call: name, timer, mute/speaker/end — **no phone number shown** | Talks, then ends the call | The call ends; neither party saw the other's number |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| In-app call fails | The app offers "Call normally instead?" — a direct dial to the other party's number (US-26.4) | Take the direct-dial option |
| Proxy (third-party) booking | The call connects to the **rider**, not the booker | Contact the actual rider |

> 🍎 **iOS:** the call appears in the native iPhone call interface (CallKit). 📱 **Android:** the call uses the in-app calling screen.

*(No spec gap identified.)*

---

### Scenario 51: Driver offline — GPS buffering and reconnect behaviour
**Platform:** Driver App
**Who:** Driver
**Goal:** Keep trip tracking accurate through a signal drop.
**Preconditions:** On a trip; loses connectivity.
**Screens involved:** SCR-DA/DI-035 (No internet / app update)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | No internet (SCR-DA/DI-035) | An offline indicator showing that GPS is being **buffered** locally | Keeps driving | Location points are stored on the phone |
| 2 | No internet (SCR-DA/DI-035) | A "buffered-then-replayed" indicator when signal returns | Continues | The stored points are sent in order; duplicates are ignored, so the route stays accurate |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Long offline stretch | Buffering continues | Keep driving; it catches up on reconnect |
| Offline beyond the grace window | The system may treat the driver as having dropped (per-state grace: 60s after accept, 120s after arrive, 5 min in-progress) | Reconnect promptly |

*(No spec gap identified.)*

---

### Scenario 52: An assigned fleet vehicle appearing in the driver app (temporary assignment)
**Platform:** Driver App
**Who:** Assigned Driver (driving a fleet owner's vehicle)
**Goal:** Operate a vehicle owned by a fleet, without owning it.
**Preconditions:** A fleet owner has assigned this driver to a vehicle.
**Screens involved:** SCR-DA/DI-026 (Vehicle management)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Vehicle management (SCR-DA/DI-026) | A **"Temporarily assigned to me (FLEET)"** group listing the fleet vehicle, the assigning fleet, and its validity | Selects the fleet vehicle | It becomes selectable as the active vehicle |
| 2 | Vehicle management (SCR-DA/DI-026) | The fleet vehicle as active | Goes online with it | Operates as that vehicle without owning it |
| 3 | (Later) | The assignment **auto-expires** at the end of validity | — | The vehicle leaves the driver's list automatically |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Assignment revoked by the fleet | The vehicle is removed; an active session is affected | Stop using it; contact the fleet |
| Assignment expired | The vehicle disappears from the list | Ask the fleet to re-assign if needed |

⚠️ **SPEC GAP:** The driver app notes that revoking an assignment affects an active session, but the **exact on-screen behaviour mid-trip** (e.g. can the driver finish the current trip, or is it cut off?) is not specified. This is also raised in Scenario 72. QA should confirm.

---

═══════════════════════════════════════════════
## SECTION C — PASSENGER WEB (passenger.mageride.lk)
═══════════════════════════════════════════════

### Scenario 53: An unregistered package recipient tracks a delivery via an SMS link (no app, no login)
**Platform:** Passenger Web (no-login)
**Who:** Unregistered Recipient
**Goal:** Watch the incoming parcel and present the delivery code, without installing anything.
**Preconditions:** Someone sent them a package; the driver has confirmed pickup; the recipient does not have the app.
**Screens involved:** passenger.mageride.lk (no-login tracking page)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | (Trigger) | A text message arrives **when the driver confirms pickup**, containing a tracking link | Taps the link | The phone's browser opens the no-login page |
| 2 | passenger.mageride.lk | A stripped-down page: a live map with the driver moving, a status bar, and the **Delivery code** — no menus, no sign-in | Watches the driver approach | Tracks in real time |
| 3 | passenger.mageride.lk | The **Delivery code** clearly displayed | Reads the code to the driver on arrival | The driver enters it; the delivery completes |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Recipient not home | The driver may complete with **photo proof** instead | Arrange collection / re-delivery |
| Link expired | The page no longer loads the trip | Ask the sender to re-share or contact support |
| COD delivery | Pay the driver cash on arrival | Have cash ready |

> 🌐 **Web:** This page has no account, no history, and no extra features — only this one parcel's map, status, and code. 📱 **App:** A registered recipient gets the same information inside the app with a phone alert and the full driver card.

⚠️ **SPEC GAP:** The link is time-limited (scoped to the trip plus a short grace), but the page does not describe a way for a recipient to **request a fresh link** themselves if it expires before delivery — they must rely on the sender or support.

---

### Scenario 54: Proxy ride location confirmation — a rider shares GPS via a web link when they don't have the app
**Platform:** Passenger Web (no-login)
**Who:** Unregistered Recipient (the rider being picked up)
**Goal:** Share their exact pickup location for a ride booked by someone else.
**Preconditions:** A booker made a proxy booking and requested the rider's location; the rider has no app.
**Screens involved:** passenger.mageride.lk (no-login pickup-confirm subview), SCR-PA/PI-011 (the app equivalent)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | (Trigger) | A text message: "[Booker] wants your pickup location" with a link | Taps the link | The no-login page opens |
| 2 | passenger.mageride.lk | A map centred on the rider's location with an adjustable pin, and **Share** / **Decline** | Drags the pin to the exact spot, taps **Share** | The confirmed location is sent back to the booker's app |
| 3 | (Booker side) | The booker's screen auto-fills the pickup pin | — | The booker can complete the booking |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Rider taps **Decline** | The page closes; **no location is ever sent** | The booker sets pickup manually |
| Request expires (5 minutes) | The page auto-dismisses | The booker re-requests or enters pickup manually |
| Rider denies browser location | The pin defaults to a rough area | Drag the pin to the correct spot manually |

> 🌐 **Web vs 📱 App:** A rider **with** the app sees this as an in-app screen (SCR-PA/PI-011); a rider **without** the app gets the same map-and-pin experience on the no-login web page.

*(No spec gap identified.)*

---

═══════════════════════════════════════════════
## SECTION D — ADMIN PORTAL (SCR-AP, admin.mageride.lk)
═══════════════════════════════════════════════

> The Admin Portal is a website (`admin.mageride.lk`) used only by MageRide's internal staff. Each of the six internal roles sees only the parts of the portal their job allows.

### Scenario 55: Admin login — email/password or Google Sign-In (no MFA step)
**Platform:** Admin Portal
**Who:** Any internal role (Verification Officer, Support Agent, Finance Officer, Super Admin, Auditor, Admin)
**Goal:** Sign in securely to the back-office.
**Preconditions:** The staff member has an internal account.
**Screens involved:** SCR-AP-001 (Login)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Login (SCR-AP-001) | A centred login card: email + password **or** a **Google Sign-In** button | Enters credentials or uses Google | Identity checked |
| 2 | Dashboard (SCR-AP-002) | Their role-scoped dashboard — **no second-step security code** | — | Access granted straight away |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Wrong password | An error on the login card | Re-enter or reset |
| Too many failed attempts | The account is temporarily locked out | Wait for the lock-out window, or ask a Super Admin to clear it |
| Signing in from outside the office IP allow-list (if enabled for that account) | Sign-in refused | Sign in from an allowed network, or ask a Super Admin to update the allow-list |
| Wrong portal | Drivers/passengers cannot log in here at all | Use the relevant app instead |

> **Change note (2026-06-28, AL-37 / US-24.5):** the 6-digit authenticator (MFA/TOTP) step was
> **removed** from this screen — sign-in completes straight to the dashboard. The earlier ⚠️ SPEC GAP
> about a lost-authenticator recovery path is therefore closed: there is no authenticator to lose.
> Failed-attempt lock-out and the optional IP allow-list are the compensating controls. See §"Admin
> Portal — login" later in this document and D2 SCR-AP-001.

---

### Scenario 56: Role-scoped dashboard — what each role sees on login
**Platform:** Admin Portal
**Who:** Verification Officer / Support Agent / Finance Officer / Super Admin / Auditor / Admin
**Goal:** Land on a dashboard showing only the modules that role is allowed.
**Preconditions:** Logged in.
**Screens involved:** SCR-AP-002 (Role-scoped dashboard)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Dashboard (SCR-AP-002) | KPI cards plus a navigation menu **filtered to their role** | Reviews their tasks | Each role sees a different menu |
| 2 | Dashboard (SCR-AP-002) | Role-specific view (examples below) | Opens their module | Works within their permissions |

**What each role sees (deny-by-default — nothing is shown unless the role permits it):**
| Role | Lands on / can access |
|---|---|
| **Verification Officer** | The onboarding/verification queue only |
| **Support / CSR Agent** | Support tickets, read-only trip/user lookup, refund requests |
| **Finance Officer** | Wallet transactions, gateway reconciliation, reversals/refunds |
| **Auditor** | Read-only views and the audit trail |
| **Super Admin** | User & role management plus broad access |
| **Admin** | Moderation, configuration, and general operations |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Tries to open a module outside their role | The module isn't in their menu (hidden by default) | Request access from the Super Admin |

*(No spec gap identified.)*

---

### Scenario 57: Verification Officer — confirming flagged onboarding fields and approving/rejecting
**Platform:** Admin Portal
**Who:** Verification Officer
**Goal:** Confirm the fields that were flagged during onboarding, then approve or reject the driver/vehicle.
**Preconditions:** A driver/vehicle reached the queue because at least one field was flagged.
**Screens involved:** SCR-AP-003 (Verification queue)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Verification queue (SCR-AP-003) | The driver's documents and an **AI-extracted fields table** (licence no, expiry, **NIC no**, **allowed vehicle types**, vehicle type, **reg-no ↔ plate**, insurance & revenue-licence numbers/expiries), each row showing its **Source** (AI confidence, or **Manual** = driver-typed) and **Status** (Auto-verified or **Pending**) | Opens the driver | Sees which fields are auto-verified and which need a decision |
| 2 | Verification queue (SCR-AP-003) | The **Pending** rows, each with **Confirm** and **Edit & confirm** actions. A field is Pending for one of **three reasons**: it was **driver-entered (Manual)** — e.g. NIC no or allowed vehicle types typed in because the licence scan was unclear; it was a **doubtful (low-confidence) AI** read — e.g. an insurance/revenue-licence expiry; or the **plate doesn't match the registration no** | Confirms each Pending field (or edits, then confirms) | Each confirm/edit is written to the audit log; a right-rail shows the **per-step Verified/Pending breakdown** (profile-licence · insurance · revenue licence · vehicle photos) |
| 3 | Verification queue (SCR-AP-003) | **Confirm all & approve** (enabled **only once every Pending field is confirmed**) / **Reject (with reason)** | Approves once all fields are confirmed, or rejects with a reason | On approval the driver/vehicle becomes **Approved** (+ OnePay merchant onboarding + FCM); the driver gets a phone alert. If rejected, the reason is shown so they can re-upload |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| A flagged field is still Pending | **Confirm all & approve** stays disabled | Confirm (or edit & confirm) every Pending field first |
| A driver-typed value is wrong | The row is editable | Use **Edit & confirm** to correct it, or reject with a reason |
| Plate genuinely doesn't match the reg no | The reg-no↔plate row shows **Pending · mismatch** | Reject with the mismatch reason (or confirm if it was a legitimate read error) |
| Document expired / illegible | Flagged | Reject with the expiry or clarity reason |

*(No spec gap identified.)*

---

### Scenario 58: Verification Officer — approving a fleet organisation's KYC
**Platform:** Admin Portal
**Who:** Verification Officer
**Goal:** Approve a fleet company's identity documents so they can operate.
**Preconditions:** A fleet owner has submitted organisation KYC via the Fleet Portal.
**Screens involved:** SCR-AP-003 (Verification queue — fleet-org approval)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Verification queue (SCR-AP-003) | A separate **fleet-organisation approval** queue alongside driver onboarding | Opens a pending fleet org | Reviews the company's KYC documents |
| 2 | Verification queue (SCR-AP-003) | The org's documents and details | Verifies them | Decides |
| 3 | Verification queue (SCR-AP-003) | **Approve / Reject with reason** | Approves | The fleet can now perform non-read operations (onboard vehicles, assign drivers) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Org not yet approved tries to operate | The fleet portal blocks non-read actions | Approve the org first |
| Incomplete KYC | Missing documents flagged | Reject asking for the missing items |

*(No spec gap identified.)*

---

### Scenario 59: Admin — suspending or banning a driver, reviewing vehicle reports
**Platform:** Admin Portal
**Who:** Admin
**Goal:** Take moderation action against a problem driver or vehicle.
**Preconditions:** A driver/vehicle has been reported.
**Screens involved:** SCR-AP-004 (Moderation)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Moderation (SCR-AP-004) | A list of reports and the affected drivers/vehicles | Opens a case | Reviews the report and history |
| 2 | Moderation (SCR-AP-004) | Actions: **suspend**, **ban**, **temporary delisting**, or review a vehicle report | Chooses an action | The driver/vehicle status changes (they may be removed from dispatch) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Driver already at delisting threshold | Their record shows the report count | Apply the appropriate action |
| Appeal raised | The case may need review | Reinstate if justified |

*(No spec gap identified.)*

---

### Scenario 60: Support/CSR — handling a ticket, looking up a trip, requesting a refund
**Platform:** Admin Portal
**Who:** Support / CSR Agent
**Goal:** Resolve a passenger or driver complaint and, if warranted, request a refund.
**Preconditions:** A support ticket exists.
**Screens involved:** SCR-AP-005 (Support & disputes)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Support & disputes (SCR-AP-005) | A ticket queue | Opens a ticket | Reads the issue (and any attached trip/screenshot) |
| 2 | Support & disputes (SCR-AP-005) | A **read-only** trip/user lookup | Looks up the trip details | Investigates what happened |
| 3 | Support & disputes (SCR-AP-005) | A **refund request** action | Raises a refund **request** | The request goes to Finance to action (CSR cannot move money directly) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Needs payment data they can't change | Trip lookup is read-only | Hand off money movements to Finance |
| Dispute needs investigation | A dispute case | Gather evidence; escalate |

> Note: The CSR **requests** a refund; the **Finance** role actually processes it (Scenario 61). This separation is deliberate.

*(No spec gap identified.)*

---

### Scenario 61: Finance — wallet transactions, gateway reconciliation, reversals and adjustments
**Platform:** Admin Portal
**Who:** Finance Officer
**Goal:** Reconcile payment-provider settlements and process wallet reversals/refunds.
**Preconditions:** Logged in as Finance.
**Screens involved:** SCR-AP-006 (Finance & reconciliation)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Finance (SCR-AP-006) | All wallet transactions and a **reconciliation** view for **OnePay/LankaQR gateway settlements** (there is no bank-transfer queue) | Reconciles the provider settlements against records | Discrepancies surfaced |
| 2 | Finance (SCR-AP-006) | **Wallet reversals / adjustments / refunds** tools | Processes a refund (e.g. from a CSR request or an overpayment) | Money is reversed via the payment provider and the wallet ledger updates |
| 3 | Finance (SCR-AP-006) | Payouts/settlements | Reviews settlements | Records stay balanced |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Late payment after a cash trip (overpayment) | The trip is flagged **Overpaid** in the refund queue | Process the refund |
| Settlement mismatch | A discrepancy in reconciliation | Investigate with the provider |

*(No spec gap identified.)*

---

### Scenario 62: Finance — reviewing driver-to-driver credit transfers (read-only)
**Platform:** Admin Portal
**Who:** Finance Officer
**Goal:** Audit credit transfers between drivers (without altering them).
**Preconditions:** Driver-to-driver transfers have occurred.
**Screens involved:** SCR-AP-006 (Finance — Credit transfers tab)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Finance (SCR-AP-006) | A **Credit transfers** tab listing driver→driver transfers (sender, recipient, amount) — **read-only** | Reviews transfers | Confirms each moved the **exact value** with **no per-transfer commission** |
| 2 | Finance (SCR-AP-006) | The transfer records | Notes that the only "margin" is the bulk-voucher discount (set in Config, Scenario 63) | Nothing to edit here |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Suspicious transfer pattern | Visible in the list | Flag for moderation/fraud review |
| Wants to reverse a transfer | This tab is read-only | Use the reversals tool (Scenario 61) if a reversal is warranted |

*(No spec gap identified.)*

---

### Scenario 63: Admin — configuring platform settings (tariffs, fees, voucher tiers, vehicle types, levels)
**Platform:** Admin Portal
**Who:** Admin
**Goal:** Adjust the platform's core business numbers.
**Preconditions:** Logged in with config rights.
**Screens involved:** SCR-AP-007 (Platform configuration)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Config (SCR-AP-007) | Tabs for **fare tariffs**, **daily-fee rates**, **bulk-voucher commission % per voucher value**, **canonical vehicle types**, **Driver Level parameters**, and feature flags | Opens a tab (e.g. fare tariffs) | Edits the values |
| 2 | Config (SCR-AP-007) | The **Commission & vouchers** table: voucher value → commission % → driver-pays → wallet-credit → active | Sets the discount per voucher denomination | The discount applies to new voucher purchases |
| 3 | Config (SCR-AP-007) | Daily-fee tiers (e.g. Three-wheeler Rs 100/day) and peak/night windows | Adjusts as needed | New trips use the updated values |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Misconfigured tariff | Affects live fares | Double-check before saving |
| Vehicle-type change | Must stay within the canonical set | Use the defined types only |

*(No spec gap identified.)*

---

### Scenario 64: Super Admin — provisioning an internal user and assigning roles
**Platform:** Admin Portal
**Who:** Super Admin
**Goal:** Create a back-office account and give it the right role(s).
**Preconditions:** Logged in as Super Admin.
**Screens involved:** SCR-AP-008 (User & role management)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | RBAC (SCR-AP-008) | A list of internal users and a **provision user** action | Creates a new user | Enters their details |
| 2 | RBAC (SCR-AP-008) | A role assignment control (Verification Officer, Support, Finance, Auditor, etc.) | Assigns role(s) | The new user can sign in with exactly those permissions |
| 3 | RBAC (SCR-AP-008) | Suspend/revoke controls | Can later suspend accounts or end their sessions | Access is controlled centrally |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Over-granting roles | The user gets more access than needed | Assign least-privilege roles |
| Departing staff | Their account remains | Suspend/revoke and end sessions |

*(No spec gap identified.)*

---

### Scenario 65: Super Admin — creating or adjusting a custom permission set
**Platform:** Admin Portal
**Who:** Super Admin
**Goal:** Define a tailored set of permissions beyond the standard roles.
**Preconditions:** Logged in as Super Admin.
**Screens involved:** SCR-AP-008 (User & role management)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | RBAC (SCR-AP-008) | Permission-set definitions | Creates/edits a permission set | Defines which modules/actions it allows |
| 2 | RBAC (SCR-AP-008) | The permission set ready to assign | Assigns it to a user | That user gets the custom access |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Permission set too broad | Risk of over-access | Keep sets narrow and purposeful |
| Change affects live users | Active sessions may need to refresh | Communicate changes |

⚠️ **SPEC GAP:** The UI spec lists "define permission sets" under SCR-AP-008 but does not detail the **granularity** of custom permissions (per-module, per-action, per-record). QA/product should confirm how fine-grained custom sets can be.

---

### Scenario 66: Auditor — viewing the tamper-proof audit trail and exporting reports
**Platform:** Admin Portal
**Who:** Auditor
**Goal:** Inspect the immutable record of admin actions and export it.
**Preconditions:** Logged in as Auditor (read-only).
**Screens involved:** SCR-AP-009 (Audit trail)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Audit trail (SCR-AP-009) | An immutable log of admin actions and permission changes (who did what, when) — **read-only** | Browses/filters the log | Reviews actions |
| 2 | Audit trail (SCR-AP-009) | An export/report option | Exports a report | Gets a record for compliance/analytics |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Tries to edit a log entry | Not possible — the log is immutable | Auditors can only read/export |
| Large date range | Export may take time | Narrow the range if needed |

*(No spec gap identified.)*

---

═══════════════════════════════════════════════
## SECTION E — FLEET PORTAL (SCR-FP, fleet.mageride.lk)
═══════════════════════════════════════════════

> The Fleet Portal is a website (`fleet.mageride.lk`) for fleet owners and their managers. A fleet manages **Mode A and Mode B** vehicles (public buses and private vehicles) — **not** on-demand Mode C. Owners can invite team members with **Owner / Manager / Viewer** sub-roles.

### Scenario 67: Fleet owner sign-up — email/password, Google, or Apple Sign-In
**Platform:** Fleet Portal
**Who:** Fleet Owner
**Goal:** Create a fleet account.
**Preconditions:** A web browser.
**Screens involved:** SCR-FP-001 (Login / Sign-up)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Login / Sign-up (SCR-FP-001) | Sign-up options: **Email + Password**, **Google**, or **Apple** | Chooses a method and signs up | Account created |
| 2 | Login / Sign-up (SCR-FP-001) | Email verification / password reset options | Verifies email | Can sign in |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Email not verified | Limited access until verified | Complete email verification |
| Forgot password | A reset option | Reset via email |
| Wants to link Google/Apple later | Link/unlink identities option | Manage in account settings |

*(No spec gap identified.)*

---

### Scenario 68: Setting up the fleet organisation — KYC documents, waiting for approval
**Platform:** Fleet Portal
**Who:** Fleet Owner
**Goal:** Register the company and pass identity checks before operating.
**Preconditions:** Signed up.
**Screens involved:** SCR-FP-002 (Organisation setup)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Org setup (SCR-FP-002) | An organisation profile form plus **KYC document** upload | Enters company details and uploads KYC | Submits for review |
| 2 | Org setup (SCR-FP-002) | A **pending approval** state (a Verification Officer must approve — see Scenario 58) | Waits | Non-read operations stay blocked until approved |
| 3 | Org setup (SCR-FP-002) | Team-member invites (Manager / Viewer) and language | Invites team members | Team can join with their sub-role |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Tries to onboard vehicles before approval | Non-read actions blocked | Wait for org approval |
| KYC rejected | A reason from the Verification Officer | Re-submit corrected documents |

*(No spec gap identified.)*

---

### Scenario 69: Onboarding a single vehicle — document upload, automatic reading, status
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** Add one vehicle to the fleet.
**Preconditions:** The organisation is approved.
**Screens involved:** SCR-FP-004 (Vehicle onboarding)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Vehicle onboarding (SCR-FP-004) | **Named document slots — registration copy (CR book), insurance certificate, revenue license, route permit (Mode A)** — with automatic data reading *(detailed by Scenario 111)* | Uploads each document into its slot | Fields auto-read; each slot shows Verified / Pending / Missing |
| 2 | Vehicle onboarding (SCR-FP-004) | For a **Mode B** vehicle: a required **Service payment — Paid or Free** *(renamed from "classification", Scenario 112)* and a **default monthly fare** | Sets Service payment and fare | Captured (Mode A vehicles skip this — public transport is free) |
| 3 | Vehicle onboarding (SCR-FP-004) | A per-vehicle status: **Pending / Approved / Rejected** (with Service payment + Documents columns) | Waits for approval | Approved only once all required documents are verified; then the vehicle can operate |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Mode C attempted | Not allowed for fleets (Mode A/B only) | Onboard as Mode A or B |
| Document rejected | Status shows Rejected | Re-upload |

*(No spec gap identified.)*

---

### Scenario 70: Bulk onboarding vehicles via CSV — validation and error report
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** Add many vehicles at once from a spreadsheet.
**Preconditions:** The organisation is approved.
**Screens involved:** SCR-FP-004 (Vehicle onboarding — bulk CSV)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Vehicle onboarding (SCR-FP-004) | A **bulk CSV** upload option | Uploads a CSV of vehicles | The file is validated |
| 2 | Vehicle onboarding (SCR-FP-004) | A validation result; rows with problems are flagged | Downloads the **error report** | Sees exactly which rows failed and why |
| 3 | Vehicle onboarding (SCR-FP-004) | The valid vehicles created (Pending/Approved per row) | Fixes and re-uploads the failed rows | The fleet is populated |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Some rows invalid | An error report listing the bad rows | Correct and re-upload only those |
| Very large file | Validation runs in stages | Wait for the result |

*(No spec gap identified.)*

---

### Scenario 71: Assigning a driver to a fleet vehicle (by phone / User ID)
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** Let a driver operate a fleet vehicle.
**Preconditions:** An approved vehicle and a driver to assign.
**Screens involved:** SCR-FP-005 (Driver assignment)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Driver assignment (SCR-FP-005) | An assignment tool: assign a driver by **User ID** or **phone** to a vehicle | Enters the driver's ID/phone and assigns | The vehicle appears in that driver's app under "Temporarily assigned" (Scenario 52) |
| 2 | Driver assignment (SCR-FP-005) | An assignment history | Reviews who has been assigned | Tracks assignments over time |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Driver not found | No matching user | Re-check the ID/phone |
| Driver already assigned elsewhere | Visible in assignment status | Resolve before reassigning |

*(No spec gap identified.)*

---

### Scenario 72: Revoking a driver assignment — effect on an active session
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** Remove a driver from a vehicle.
**Preconditions:** A driver is currently assigned.
**Screens involved:** SCR-FP-005 (Driver assignment)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Driver assignment (SCR-FP-005) | The assigned driver with a **revoke** control | Revokes the assignment | The vehicle is removed from the driver's app |
| 2 | (Driver side) | The driver's "Temporarily assigned" entry disappears | — | The driver can no longer go online with that vehicle |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Driver is mid-session when revoked | The active session is affected | See the spec gap below |

⚠️ **SPEC GAP:** Both the Fleet Portal and the Driver App note that revoking affects an **active session**, but neither specifies the **exact on-screen outcome** if revoked mid-trip (graceful finish vs immediate cut-off). This is the same gap flagged in Scenario 52 and should be resolved before launch.

---

### Scenario 73: Binding an ST-901 GPS tracker to a vehicle (IMEI/MAC)
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** Link a hardware tracker to a fleet vehicle.
**Preconditions:** A vehicle and a tracker device.
**Screens involved:** SCR-FP-006 (Tracker binding)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Tracker binding (SCR-FP-006) | An **IMEI / MAC** binding form | Enters the device ID and binds it to a vehicle | The tracker is linked |
| 2 | Tracker binding (SCR-FP-006) | An **auto-session config** option | Enables automatic start/end on ignition (for Mode A/B) | The vehicle reports without needing the driver app |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Duplicate device ID | A conflict/quarantine notice | Resolve with support |
| Tracker offline | No position reported | Check device power/signal |

*(No spec gap identified.)*

---

### Scenario 74: Configuring tracker publish cadence (active hours vs off-hours)
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** Control how often a tracker reports during and outside operating hours.
**Preconditions:** A bound tracker.
**Screens involved:** SCR-FP-006 (Tracker binding — publish cadence)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Tracker binding (SCR-FP-006) | A **publish-cadence profile** with active-hours and off-hours settings | Sets frequent updates during active hours and sparse during off-hours | Saves the profile |
| 2 | Tracker binding (SCR-FP-006) | The cadence applied | Reviews | The tracker reports accordingly, balancing freshness and cost |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Off-hours too sparse | Passengers may see fewer updates | Tune the cadence |
| Active-hours too frequent | More data/cost | Balance as needed |

*(No spec gap identified.)*

---

### Scenario 75: Viewing the live fleet map — all vehicles with health and status
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager / Viewer
**Goal:** See every fleet vehicle on one map with its status.
**Preconditions:** Vehicles are onboarded and (ideally) reporting.
**Screens involved:** SCR-FP-007 (Live fleet map)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Live fleet map (SCR-FP-007) | A single map scoped to **this organisation's** vehicles, with a **fleet-health overlay** showing **online / stale / offline** status | Scans the map | Sees the whole fleet at a glance |
| 2 | Live fleet map (SCR-FP-007) | Per-vehicle status colours | Clicks a vehicle | Sees its detail |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| A vehicle shows **stale** | It reported recently but not just now | Check the device/driver |
| A vehicle shows **offline** | Not reporting | Investigate power/signal |
| Viewer sub-role | Can view but not change | Read-only as designed |

*(No spec gap identified.)*

---

### Scenario 76: Adding and managing scheduled journeys per vehicle
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** Define regular journeys for each vehicle.
**Preconditions:** Approved vehicles.
**Screens involved:** SCR-FP-008 (Scheduling & alarms)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Scheduling (SCR-FP-008) | A per-vehicle scheduled-journeys list | Adds a scheduled journey (route, time) | The schedule is saved |
| 2 | Scheduling (SCR-FP-008) | A **not-started alarm** configuration | Sets an alarm for journeys that don't start on time | The alarm will ring in the assigned driver's app and alert the owner |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Overlapping schedules | Possible conflicts | Adjust times |
| No driver assigned | The schedule has no one to run it | Assign a driver (Scenario 71) |

*(No spec gap identified.)*

---

### Scenario 77: Schedule-not-started alarm — what the driver and the owner see
**Platform:** Fleet Portal **and** Driver App
**Who:** Fleet Owner (alerted) and Assigned Driver (reminded)
**Goal:** Catch a journey that hasn't started on time.
**Preconditions:** A scheduled journey with a not-started alarm; the start time passes without the journey starting.
**Screens involved:** SCR-FP-008 (Scheduling & alarms), SCR-DA/DI-011 (Mode A session) / SCR-DA/DI-018 (Scheduled rides)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | (Trigger) | The scheduled start time passes with no journey started | — | The alarm fires |
| 2 | Driver App (SCR-DA/DI-011/018) | 📱 **Driver:** an alarm/reminder to start the journey | Starts the journey | The alarm clears |
| 3 | Fleet Portal (SCR-FP-008) | 🌐 **Owner:** an alert that the journey hasn't started | Follows up with the driver | The owner can intervene |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Driver offline | The owner still sees the alert | Contact the driver |
| Journey legitimately delayed | Both see the not-started state | Owner decides whether to act |

*(No spec gap identified.)*

---

### Scenario 78: Viewing per-vehicle trip history and analytics — export to CSV/PDF
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager / Viewer
**Goal:** Review utilisation per vehicle and export it.
**Preconditions:** Vehicles have trip activity.
**Screens involved:** SCR-FP-009 (Trip history & analytics)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Analytics (SCR-FP-009) | Per-vehicle **trips / distance / utilisation / idle**, with date filters | Picks a vehicle and date range | Sees the metrics |
| 2 | Analytics (SCR-FP-009) | **CSV / PDF export** | Exports a report | Gets a file for their records |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| No activity in range | Empty metrics | Widen the date range |
| Export fails | A failure message | Retry |

*(No spec gap identified.)*

---

### Scenario 79: Fleet billing — monthly per-Mode-B-vehicle invoice and fleet wallet top-up
**Platform:** Fleet Portal
**Who:** Fleet Owner
**Goal:** Pay MageRide's monthly charge per private vehicle and keep the fleet wallet funded.
**Preconditions:** The fleet has Mode B vehicles.
**Screens involved:** SCR-FP-003 (Dashboard), SCR-FP-010 (Billing & wallet)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Dashboard (SCR-FP-003) | The **fleet wallet balance** and the **next monthly invoice** | Reviews upcoming charges | Knows what's due |
| 2 | Billing & wallet (SCR-FP-010) | A monthly invoice charged **per Mode B vehicle** (Mode A is free) | Reviews the invoice | Sees the per-vehicle breakdown |
| 3 | Billing & wallet (SCR-FP-010) | Top-up via **Card / OnePay / LankaQR** (no bank transfer) | Tops up the fleet wallet | The invoice is covered |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Mode A vehicles in the fleet | They are **not** charged | Nothing — only Mode B is billed |
| Top-up fails | A retry prompt | Retry / change method |
| Expects bank transfer | Not available | Use card/OnePay/LankaQR |

*(No spec gap identified.)*

---

### Scenario 80: Managing Mode B passenger subscriptions — incoming access requests, approve/reject
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** Decide who can track each private vehicle.
**Preconditions:** Passengers have requested access to a Mode B vehicle.
**Screens involved:** SCR-FP-011 (Mode B subscriptions & requests)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Subscriptions & requests (SCR-FP-011) | Per-vehicle incoming **access requests** with **Accept / Reject** | Selects a vehicle, reviews its requests | Sees who wants to subscribe |
| 2 | Subscriptions & requests (SCR-FP-011) | **Accept** grants tracking and starts the subscription; **Reject** dismisses | Accepts a request | The passenger can now track the vehicle |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Requests for multiple vehicles | Each vehicle's requests appear **under that vehicle only** | Switch the per-vehicle view |
| Re-subscribe after unsubscribe | Treated as a fresh request | Accept again to restore |

*(No spec gap identified.)*

---

### Scenario 81: Setting the Service payment (Paid/Free) per vehicle *(label renamed by Scenario 112)*
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** Decide whether a private vehicle charges its subscribers.
**Preconditions:** A Mode B vehicle (set at onboarding, editable here).
**Screens involved:** SCR-FP-004 (Vehicle onboarding) / SCR-FP-011 (Subscriptions)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Vehicle onboarding (SCR-FP-004) | A **Service payment — Paid / Free** setting for the Mode B vehicle | Chooses **Free** (e.g. company staff bus) or **Paid** (Paid needs a Verified bank & payout profile — Scenario 110) | Determines whether payment UI appears |
| 2 | (Result) | **Free:** no fare, no payment screens for subscribers; **Paid:** a default monthly fare is collected | Confirms | Subscribers see the appropriate experience |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Free vehicle | Subscribers never see a charge | Nothing |
| Switching Paid↔Free later | Affects existing subscribers | Communicate the change |

⚠️ **SPEC GAP:** The specs define Paid/Free at onboarding but do not describe what happens to **existing subscribers** if a vehicle is switched from Paid to Free (or back) — e.g. mid-cycle proration or refunds. QA/product should confirm.

---

### Scenario 82: Setting per-subscriber monthly fare amounts
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** Charge different subscribers different amounts on the same vehicle.
**Preconditions:** A Paid Mode B vehicle with subscribers.
**Screens involved:** SCR-FP-011 (Mode B subscriptions & requests)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Subscriptions (SCR-FP-011) | A subscriber roster with an **editable fare** per subscriber and a **billing cycle** | Overrides the monthly fare for a specific subscriber | That subscriber is billed the custom amount |
| 2 | Subscriptions (SCR-FP-011) | A billing-cycle choice: **1st-of-month** or **join-anniversary** | Sets the cycle | The next-due date is computed accordingly (e.g. joined 5 Jun → next due 6 Jul) |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Different amounts per subscriber | Allowed — each can differ | Set per subscriber |
| Anniversary cycle | Next-due rolls monthly from the join date | Both owner and subscriber see the date |

*(No spec gap identified.)*

---

### Scenario 83: Monitoring subscriber payment status — paid, pending, overdue
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** See who has paid and who hasn't.
**Preconditions:** Paid subscribers exist.
**Screens involved:** SCR-FP-011 (Mode B subscriptions & requests)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Subscriptions (SCR-FP-011) | Each subscriber's **this-month status**: Paid / Pending verification / Paid-cash / overdue | Scans the roster | Identifies non-payers |
| 2 | Subscriptions (SCR-FP-011) | Action buttons (**Mark received / Confirm transfer**) | Follows up where needed | Updates status as payments come in |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Subscriber overdue | An overdue status | Remind / follow up |
| Payment pending verification | A "pending" status (e.g. a transfer slip awaiting confirmation) | Verify it (Scenario 85) |

*(No spec gap identified.)*

---

### Scenario 84: Recording a cash payment received from a subscriber
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** Mark a subscriber as paid when they hand over cash.
**Preconditions:** A Paid subscriber pays in cash (to a collector).
**Screens involved:** SCR-FP-011 (Mode B subscriptions & requests)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Subscriptions (SCR-FP-011) | The subscriber with a **Mark received** action | Confirms the cash was received | The subscriber's status flips to **Paid** |
| 2 | (Subscriber side) | The subscriber's card shows **Paid** | — | The payment is logged in the ledger |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Only the **owner** can mark cash received | A collector cannot self-confirm | Owner marks it in the portal |
| Marked in error | Status changes incorrectly | Correct via the ledger / support |

*(No spec gap identified.)*

---

### Scenario 85: Verifying a bank-transfer screenshot uploaded by a subscriber
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** Confirm an online-transfer payment from its slip.
**Preconditions:** A subscriber paid by online transfer and uploaded a slip screenshot.
**Screens involved:** SCR-FP-011 (Mode B subscriptions & requests)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Subscriptions (SCR-FP-011) | A subscriber in **pending verification** with the uploaded transfer **slip screenshot** | Opens and checks the slip against the expected amount | Verifies it |
| 2 | Subscriptions (SCR-FP-011) | A **Confirm transfer** action | Confirms | The status flips to **Paid** and is logged |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Slip doesn't match the amount | A mismatch | Don't confirm; contact the subscriber |
| No slip attached | Cannot verify | Ask the subscriber to upload one |

> Note: Subscription payments route **to the fleet owner** (pass-through), not to MageRide. The owner is responsible for confirming transfers and cash.

*(No spec gap identified.)*

---

### Scenario 86: Viewing the full subscriber payment ledger with export
**Platform:** Fleet Portal
**Who:** Fleet Owner / Manager
**Goal:** See a complete payment record per subscriber/vehicle and export it.
**Preconditions:** Subscriber payments exist.
**Screens involved:** SCR-FP-012 (Subscriber payments ledger)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Payments ledger (SCR-FP-012) | A per-subscriber, per-vehicle ledger across all methods (LankaQR / OnePay / transfer / cash) plus summary KPIs | Reviews the ledger | Sees the full payment picture |
| 2 | Payments ledger (SCR-FP-012) | A **CSV export** | Exports the ledger | Gets a record for accounting |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Reconciling cash vs digital | The ledger separates methods | Use the method columns |
| Export fails | A failure | Retry |

*(No spec gap identified.)*

---

═══════════════════════════════════════════════
## SECTION F — CROSS-PLATFORM SCENARIOS
═══════════════════════════════════════════════

> These scenarios follow a single real-world event across **two or more surfaces at once**, showing how the passenger, driver, and back-office experiences line up.

### Scenario 87: End-to-end Mode C ride — from passenger booking to driver completing and payment settling
**Platform:** Cross-platform (Passenger App + Driver App)
**Who:** Passenger and Driver
**Goal:** Walk a complete on-demand ride from both sides.
**Preconditions:** A signed-in passenger and an online, eligible driver nearby.
**Screens involved:** SCR-PA/PI-009/014/015/016/017/018/019 (passenger), SCR-DA/DI-010/014/015/020 (driver)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Passenger · Booking (SCR-PA/PI-009) | Tier cards with the upfront fare | Picks a tier and **Book Now** | The search for a driver starts |
| 2 | Passenger · Finding driver (SCR-PA/PI-014) | "Finding a driver…" | Waits | The system offers the ride to the best driver |
| 3 | Driver · Incoming dispatch (SCR-DA/DI-014) | A 15-second offer | **Accepts** | Both sides switch to the active ride; first trip of the day is free, otherwise the daily fee is taken |
| 4 | Passenger · Ride in progress (SCR-PA/PI-015) | The driver approaching + a **Start code** | Gives the code to the driver | — |
| 5 | Driver · Active ride (SCR-DA/DI-015) | Navigation + a code field | Enters the code, drives, taps **End** | The fare finalises |
| 6 | Passenger · Pay (SCR-PA/PI-016/017) | Cash / LankaQR / OnePay | Pays by the chosen method | Payment settles; a late callback after cash would be refunded |
| 7 | Both · Summary & rate (SCR-PA/PI-018/019, SCR-DA/DI-020) | Trip summary + rating; the driver's earnings update | Both rate each other | The driver's earning posts once payment is final; ratings feed driver level |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| No driver accepts in 2 minutes | Passenger sees "No drivers available" | Re-book |
| Passenger cancels after accept | Rs 50 penalty (added to next trip) | See Scenario 10 |
| Payment fails | Retry or fall back to cash | See Scenario 15/27 |

*(No spec gap identified.)*

---

### Scenario 88: End-to-end package delivery — sender books, driver picks up, recipient receives
**Platform:** Cross-platform (Passenger App sender + Driver App + recipient App/Web)
**Who:** Sender, Driver, and Recipient (registered or unregistered)
**Goal:** Follow a parcel through all three views.
**Preconditions:** A sender with a parcel; an online driver; a recipient.
**Screens involved:** SCR-PA/PI-012/020 (sender), SCR-DA/DI-014/016a/016b/016c (driver), SCR-PA/PI-021 or passenger.mageride.lk (recipient)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Sender · Package booking (SCR-PA/PI-012) | Size S/M/L, pickup, drop-off, payment (incl. COD) | Books the delivery | A driver is dispatched |
| 2 | Driver · Incoming dispatch (SCR-DA/DI-014) | A "Package · S/M/L" offer | Accepts, drives to pickup | — |
| 3 | Sender · Tracking (SCR-PA/PI-020) | A live map + the **Pickup code** | Gives the pickup code to the driver | — |
| 4 | Driver · Pickup sheet 2/3 (SCR-DA/DI-016b) | The pickup-OTP field | Enters the code → **Picked Up** | The recipient is now notified |
| 5 | Recipient · App/Web (SCR-PA/PI-021 or passenger.mageride.lk) | A phone alert (app) or SMS link (no app) → live tracking + the **Delivery code** | Watches the driver arrive | — |
| 6 | Driver · Complete sheet 3/3 (SCR-DA/DI-016c) | The delivery-OTP field (or **Photo proof** if absent) and a **Delivery completed** button — there is **no "Cash received" button**; COD is reconciled separately | Enters the delivery OTP → taps **Delivery completed** → **Delivered** | The trip completes; COD/earnings settle |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Recipient absent | Driver completes with photo proof | Arrange re-delivery |
| Wrong code 5× (either end) | Locks and goes to support | Confirm the codes |
| COD unpaid 24h | Flagged as Disputed | Confirm cash at delivery |

*(No spec gap identified.)*

---

### Scenario 89: End-to-end scheduled ride — booking, job board, intent, 30-minute dispatch, completion
**Platform:** Cross-platform (Passenger App + Driver App)
**Who:** Passenger and Driver(s)
**Goal:** Follow a future booking from creation to completion.
**Preconditions:** A passenger schedules a future ride; Level 2+ drivers are around.
**Screens involved:** SCR-PA/PI-013/022 (passenger), SCR-DA/DI-017/014/018/015 (driver)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Passenger · Schedule (SCR-PA/PI-013) | A date/time picker | Books a future ride | It appears in the Scheduled tab; reminders are set |
| 2 | Driver · Job Board (SCR-DA/DI-017) | The future ride within 30 km | **Posts intent** | "Intent posted ✓" |
| 3 | (30 min before) Driver · Incoming dispatch (SCR-DA/DI-014) | A standard 15-second offer to the closest intent-poster | **Accepts** | The ride becomes the driver's active job |
| 4 | Driver · Scheduled rides → Active ride (SCR-DA/DI-018/015) | The accepted ride, then navigation | Picks up (code), drives, ends | The trip completes |
| 5 | Passenger · History (SCR-PA/PI-022) | The completed scheduled ride | Reviews/rates | Done |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| No driver posts intent | Handled like a live dispatch at the time; if none, passenger notified | Re-book |
| Driver no-show on accepted scheduled ride | Driver's level drops one | Honour the booking |
| Level 1 driver | Cannot see the Job Board | Build level first |

*(No spec gap identified.)*

---

### Scenario 90: End-to-end driver onboarding — register, admin verifies, first ride
**Platform:** Cross-platform (Driver App + Admin Portal)
**Who:** Driver and Verification Officer
**Goal:** Take a new driver from sign-up through profile setup and Mode-C vehicle onboarding to their first live ride.
**Preconditions:** A new driver with licence + a Mode-C vehicle's documents; a Verification Officer available for any Pending document.
**Screens involved:** SCR-DA/DI-003/003a/004→004c/006/010/014 (driver), SCR-AP-003 (admin)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Driver · Profile setup (SCR-DA/DI-003a) | Required photo, name, driving-licence capture (the AI reads licence no, expiry, **NIC no** and **allowed vehicle types**; any unclear field is typed in and flagged) | Completes profile | Reaches Home with **no vehicle** |
| 2 | Driver · Vehicle onboarding (SCR-DA/DI-004 → 004c) | The 4-step Mode-C wizard (type+reg, insurance, revenue licence, front/back photos) | Captures all 4, submits for review | **Gemini Flash 3.0** auto-verifies each document |
| 3 | Driver · Onboarding status (SCR-DA/DI-006) | A 4-document Verified/Pending list | Sees the result | **All Verified → vehicle auto-approved** (no officer); any **Pending** → step 4 |
| 4 | Admin · Verification queue (SCR-AP-003) | Only the **Pending/flagged** field(s) — driver-entered (Manual), doubtful AI, or plate↔reg-no mismatch — each with **Confirm / Edit & confirm** | **Confirms every flagged field, then Confirm all & approve** (or rejects with reason) | The driver gets a phone alert; vehicle approved |
| 5 | Driver · Dashboard (SCR-DA/DI-010) | The online toggle (now enabled — a vehicle is available) | Goes **online** | Enters the dispatch pool (starts at Level 3) |
| 6 | Driver · Incoming dispatch (SCR-DA/DI-014) | A first offer (first trip of the day is free) | Accepts and completes | The driver is live on the platform |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Document rejected | The status shows the reason | Re-upload and resubmit |
| Mandatory doc (insurance/revenue licence) missing | Cannot submit | Provide it first |

*(No spec gap identified.)*

---

### Scenario 91: End-to-end fleet onboarding — fleet registers, admin approves, vehicle onboarded, driver assigned, passenger subscribes and tracks
**Platform:** Cross-platform (Fleet Portal + Admin Portal + Driver App + Passenger App)
**Who:** Fleet Owner, Verification Officer, Assigned Driver, Passenger
**Goal:** Stand up a fleet vehicle and get a passenger tracking it.
**Preconditions:** A new fleet owner with company documents and at least one vehicle.
**Screens involved:** SCR-FP-001/002/004/005/011 (fleet), SCR-AP-003 (admin), SCR-DA/DI-026 (driver), SCR-PA/PI-024/025/010 (passenger)

**Step-by-step walkthrough:**
| Step | Screen (ID) | What the user sees | What the user does | What happens next |
|---|---|---|---|---|
| 1 | Fleet · Sign-up + Org setup (SCR-FP-001/002) | Sign-up and the org KYC form | Registers and submits org KYC | Pending approval |
| 2 | Admin · Verification queue (SCR-AP-003) | The fleet org's KYC | **Approves** the organisation | The fleet can now operate |
| 3 | Fleet · Vehicle onboarding (SCR-FP-004) | The vehicle form with the Service payment (Paid/Free) setting + default fare | Onboards a Mode B vehicle | The vehicle is approved |
| 4 | Fleet · Driver assignment (SCR-FP-005) | The assignment tool | Assigns a driver by phone/User ID | The vehicle appears in that driver's app |
| 5 | Driver · Vehicle management (SCR-DA/DI-026) | The vehicle under "Temporarily assigned" | Selects it and goes online | The vehicle starts reporting its position |
| 6 | Passenger · Access request (SCR-PA/PI-024) | The Mode B marker → request screen | Requests access | The request reaches the fleet |
| 7 | Fleet · Subscriptions (SCR-FP-011) | The incoming request | **Accepts** (and sets the fare if Paid) | The passenger's subscription starts |
| 8 | Passenger · Live Map / Subscriptions (SCR-PA/PI-010/025) | The vehicle now trackable; the subscription card | Tracks the vehicle; pays if Paid | The passenger follows the vehicle live |

**What can go wrong (edge cases):**
| Situation | What the user sees | What they should do |
|---|---|---|
| Org not approved | The fleet can't onboard vehicles | Wait for admin approval |
| Paid vehicle, unpaid subscriber | Status shows pending/overdue | Owner follows up on payment |
| Assignment revoked | Driver loses the vehicle | Re-assign as needed (see Scenario 72 gap) |

*(No spec gap identified.)*

---

## SECTION G — 2026-06-28 CHANGE SET (UX & ADMIN DIRECTORY)
═══════════════════════════════════════════════

> Eleven refinements from the 2026-06-28 review (URD Epic 24 / ADD §1.11 AL-36…AL-43). Scenarios 92–102 below describe only what changed; all other behaviour is as in Sections A–F.

### Scenario 92: A new passenger reaches "Get Started" at the bottom of onboarding
**Platform:** Passenger App · **Who:** Brand-new passenger · **Goal:** Start using the app. *(item 1 / US-24.1)*
**Screens:** SCR-PA/PI-002 (Onboarding).
On the welcome screen the three intro slides and the **Sinhala-first language list** sit in the middle, and the **Get Started button is now pinned to the bottom of the screen** as a full-width button below the language choices. The user picks a language and taps **Get Started** without having to reach past the language list. Nothing else about onboarding changes.

### Scenario 93: A passenger schedules a ride and chooses where to go
**Platform:** Passenger App · **Who:** Passenger · **Goal:** Book a ride for later, to a chosen destination. *(item 2 / US-24.2)*
**Screens:** SCR-PA/PI-013 (Schedule ride), destination picker.
| Step | Screen | What the user sees | What they do | What happens next |
|---|---|---|---|---|
| 1 | Schedule ride | A **"Where to?"** block at the top: pickup (defaults to current location) + **"Select destination…"** | Taps **Select destination** | The place-search / map-pick sheet opens (same one used for normal booking) |
| 2 | Destination picker | Search box + map | Picks the place to go | The destination fills in; pickup can be edited too |
| 3 | Schedule ride | Date, time, reminders, **Confirm** | Sets date & time, taps **Confirm schedule** | The ride is scheduled to that destination; reminders set 1 h & 15 min before |

**What can go wrong:** No destination chosen → **Confirm stays disabled** with a hint to pick a destination. Past time → that time is greyed out.

### Scenario 94: A passenger chooses how to call the driver — free or normal
**Platform:** Passenger App · **Who:** Passenger on an active ride · **Goal:** Phone the driver. *(item 4 / US-24.3)*
**Screens:** SCR-PA/PI-015 (Ride in progress) → SCR-PA/PI-015a (Call-type chooser) → SCR-PA/PI-028 (VoIP) or phone dialer.
Tapping **📞 Call** no longer dials immediately — it opens a small sheet with two choices: **Free call** ("In-app, no charge") and **Normal call** ("Uses your minutes"). **Free call** starts an in-app internet call; if it fails the app offers **"Call normally instead?"** — a direct dial. **Normal call** places an ordinary phone call **directly to the other party's real number** (shown only after a driver accepts; the masking requirement was removed on 2026-07-05 — Scenario 108, US-26.2). A first-call tooltip notes that your number is visible to the other party. The app remembers the last choice.

### Scenario 95: A passenger calls the driver after the trip (lost item)
**Platform:** Passenger App · **Who:** Passenger · **Goal:** Reach the driver about something left behind. *(item 3 / US-24.4)*
**Screens:** SCR-PA/PI-022 (Trip history).
Each completed trip card now shows the **driver's name and mobile number** with a **Call** link. Tapping **Call** opens the same **call-type chooser** (Scenario 94). For a ride that was cancelled before a driver was assigned, no number is shown.

### Scenario 96: An officer signs in to the Admin Portal — no authenticator step
**Platform:** Admin Portal · **Who:** Internal staff · **Goal:** Sign in. *(item 5 / US-24.5)*
**Screens:** SCR-AP-001 (Login).
The login screen offers **email + password** or **Sign in with Google**. After credentials are accepted the user lands **straight on the dashboard** — there is **no 6-digit MFA/authenticator step** any more. Accounts are protected by failed-attempt lock-out and an optional office-IP allow-list instead.

### Scenario 97: An admin filters the dashboard statistics by period
**Platform:** Admin Portal · **Who:** Admin · **Goal:** See figures for a chosen period. *(item 7 / US-24.7)*
**Screens:** SCR-AP-002 (Dashboard).
A filter bar lets the admin choose **Today / This week / This month / Custom range**. The KPI cards (completed trips, gross fare, new riders/drivers, daily-fee revenue) **recompute for that period** and show a **vs-previous-period** arrow. Real-time cards (online drivers now, pending verifications, open tickets) ignore the filter. **Export** downloads the filtered figures as CSV.

### Scenario 98: A verification officer works the split queues and opens a document full-size
**Platform:** Admin Portal · **Who:** Verification Officer · **Goal:** Review and approve onboarding. *(item 8 / US-24.8)*
**Screens:** SCR-AP-003 (Queues) → SCR-AP-003a (Detail) / SCR-AP-003c (Fleet-org) → SCR-AP-003b (Viewer).
| Step | Screen | What the user sees | What they do | What happens next |
|---|---|---|---|---|
| 1 | Queues | Three tabs: **Driving-licence pending · Vehicle-registration pending · Fleet-org approval** | Picks a queue, taps a row | The detail screen opens for that entry |
| 2 | Detail | A grid of **attached-document thumbnails** + AI-extracted fields with **Confirm / Edit** + a decision rail | Taps a thumbnail | It opens **full-size** in a viewer |
| 3 | Viewer | The document large, with **zoom / rotate / prev-next** | Reviews, closes | Back to the detail; each open is audit-logged |
| 4 | Detail | Confirms each flagged field | Taps **Confirm all & approve** | Approve only unlocks once **every** pending field is confirmed; the driver/vehicle/org becomes Approved |

Fleet-org rows open the **fleet-org approval detail** (SCR-AP-003c) with KYC-document thumbnails that open the same viewer.

### Scenario 99: An agent looks up a passenger and their transactions
**Platform:** Admin Portal · **Who:** Support / Admin · **Goal:** Find a passenger and review their activity. *(item 9 / US-24.9)*
**Screens:** SCR-AP-010 (Search) → SCR-AP-011 (Detail).
The agent searches by **name, mobile, passenger ID, or email**. Opening a result shows the **profile** plus tabbed **Trips / Payments / Packages / Disputes**. It is read-only — a refund can be escalated to Finance but not posted here. Opening the detail records a privacy-access audit entry; sensitive fields appear only for permitted roles.

### Scenario 100: An agent reviews a verified driver and their transactions
**Platform:** Admin Portal · **Who:** Support / Admin / Finance · **Goal:** Inspect a driver. *(item 10 / US-24.10)*
**Screens:** SCR-AP-012 (Search) → SCR-AP-013 (Detail).
Search options include **name, mobile, driver ID, NIC, vehicle reg no, Driver Level, and status** (verified by default). The detail shows profile/wallet/level + linked vehicles and tabbed **Trips / Wallet ledger / Daily fee / Credit transfers / Reports**. Vehicle chips jump to the vehicle detail. Wallet reversals stay Finance-only.

### Scenario 101: An agent searches a registered vehicle and its transactions
**Platform:** Admin Portal · **Who:** Support / Admin / Finance · **Goal:** Inspect a vehicle. *(item 11 / US-24.11)*
**Screens:** SCR-AP-014 (Search) → SCR-AP-015 (Detail).
Search by **registration no, vehicle ID, type, mode, owner mobile, fleet org, or status**. The detail shows registration / insurance / revenue-licence / tracker info, a **document-thumbnail grid** (thumbnails open the full-size viewer), and tabbed **Trips / Earnings / Daily fee / Reports**.

### Scenario 102: A driver captures a document by fitting it in the frame
**Platform:** Driver App · **Who:** Driver onboarding a vehicle · **Goal:** Capture a clear licence/insurance/photo. *(item 6 / US-24.6)*
**Screens:** SCR-DA/DI-003a / 004a / 004b / 004c → SCR-DA/DI-005 (Document capture).
Every **📷 Tap to capture** slot opens a **camera document-scanner**. The driver sees a live camera with an **adjustable frame whose four corners can be dragged** so the whole document fills the frame (the app also auto-detects the edges as a starting point). After **Use photo**, the image is straightened and cropped, then uploaded and read by Gemini Flash — clearer captures mean fewer fields get flagged for admin verification. Gallery pick is still allowed as a fallback.

**What can go wrong:** Camera permission denied → an "Allow camera" prompt; low light → a flash toggle; blurry capture → **Retake**.

*(No spec gap identified — all eleven items map to URD Epic 24 / ADD §1.11.)*

---

## SECTION H — 2026-07-05 CHANGE SET (PASSENGER WEB SUBVIEW CONTRACTS)
═══════════════════════════════════════════════

> Eight items from the 2026-07-05 spec-vs-wireframe audit (URD Epic 25 / ADD §1.12 AL-44…AL-46). The `passenger.mageride.lk` pages walked in Section C now carry screen IDs **SCR-WT-001…006** and full contracts. Scenarios 103–106 describe only what is new or newly formalized; Scenarios 53–54 remain valid.

### Scenario 103: A proxy rider without the app tracks the whole ride from one SMS link
**Platform:** Passenger Web (no-login) · **Who:** Unregistered proxy rider · **Goal:** Track the ride, show the Start code, reach the driver. *(items 1–2, 4 / US-25.1/25.2/25.4)*
**Screens:** SCR-WT-001 (Landing) → SCR-WT-004 (Ride track).
When the driver accepts, the rider's phone gets a text with a tracking link. Opening it shows a brief check (SCR-WT-001), then the ride page (SCR-WT-004): the driver's name and photo, vehicle and plate, a live map with ETA, the **Start code** to read to the driver, and — if the booker chose Cash — a clear "you pay the driver Rs X in cash" note. **Call driver** is a plain tap-to-call link with the driver's number (US-26.3 — masking removed; the number appears only while the tracking link is valid).
**What can go wrong:** Link expired or ride already finished → a safe "This link has expired" page (SCR-WT-006) with nothing but an app-download button; connection drops → the page falls back to periodic refresh instead of the live stream.

### Scenario 104: A rider without the app shares their pickup spot from the browser
**Platform:** Passenger Web (no-login) · **Who:** Unregistered proxy rider · **Goal:** Share the exact pickup pin the booker asked for. *(item 3 / US-25.3)*
**Screens:** SCR-WT-001 → SCR-WT-003 (Confirm pickup).
The booker requested the rider's location, but the rider isn't a MageRide user. Previously the booker had to guess with a map pin; now the rider gets an SMS link too. The page shows a map pin they can drag, a **5-minute countdown**, and two buttons: **Share location** (sends the pin to the booker's booking form) and **Decline** — declining sends **no location at all**. If the rider ignores it, the link simply dies at 5 minutes and the booker places the pin manually, as before.

### Scenario 105: A web tracker presses SOS
**Platform:** Passenger Web (no-login) · **Who:** Proxy rider mid-trip · **Goal:** Raise an alarm without the app. *(item 5 / US-25.5)*
**Screens:** SCR-WT-004 (Ride track → SOS).
The **SOS** button asks the browser for the rider's location and fires an SMS (two gateways in parallel, within ~5 seconds) to the **booker** — the person who arranged the ride — and lights up the admin live feed. If the browser refuses the location prompt, SOS still fires using the vehicle's last reported position.
**What can go wrong:** SMS gateway down → the second gateway carries it; repeated presses → rate-limited, first alarm stands.

### Scenario 106: The delivery ends and everyone can prove it
**Platform:** Passenger Web (no-login) · **Who:** Package recipient · **Goal:** See the outcome and keep a receipt. *(item 6 / US-25.6)*
**Screens:** SCR-WT-002 (Package track) → SCR-WT-005 (Delivered).
On hand-over the page flips to the outcome view: **Delivered ✓** (code verified), or a **photo of where the parcel was left** if the recipient was absent, or the cash status — **collected**, or **Disputed** if cash stays uncollected past 24 hours. A **Download receipt** button works while the link is valid (link closes ~1 hour after delivery); after that, the receipt lives in the sender's app history.

*(Hygiene, item 8: SCR-DA/DI-012 is a dashboard state, not a screen — re-tagged [MERGED → 010]; stale wireframe story/endpoint references corrected. No user-visible behaviour change.)*

---

## SECTION I — 2026-07-05 CHANGE SET #2 (DRIVER-QR SETTLEMENT & MASKING REMOVAL)
═══════════════════════════════════════════════

> Two decisions from the technical-feasibility review (URD Epic 26 / ADD §1.13 AL-47…AL-48). Scenarios 107–109 describe only what changed; earlier scenarios (15, 87, 94, 103) are updated in place where they described the old behaviour.

### Scenario 107: A passenger pays the driver's QR and both sides confirm it
**Platform:** Passenger + Driver Apps · **Who:** Matched pair at drop-off · **Goal:** Settle a QR payment that the platform cannot see. *(item 1 / US-26.1)*
**Screens:** SCR-PA/PI-017 (Pay fare) → SCR-DA/DI-015 (QR payment received sheet).
The passenger scans the **driver's own bank QR** and pays in their bank app. Because that money moves bank-to-bank, MageRide receives no confirmation — so the passenger taps **"I've paid"** (optionally attaching a screenshot of the bank receipt), and the driver gets a **"QR payment received?"** prompt. The driver taps **Confirm**, the payment closes as **DriverConfirmedQR**, and the earning posts exactly as a cash trip would. A driver can also confirm on their own if the passenger has already left.
**What can go wrong:** Passenger claims but driver doesn't confirm → the driver is nudged after 5 minutes; still unresolved → **Get help** opens a support ticket that lands in the Finance dispute queue (the screenshot is the evidence). Driver says money never arrived → they pick **Not received** and either take cash or raise the same dispute.

### Scenario 108: Calls now use real numbers
**Platform:** Passenger + Driver Apps + Passenger Web · **Who:** Any matched pair · **Goal:** Reach the other party. *(items 2–3, 5 / US-26.2/26.3/26.5)*
**Screens:** SCR-PA/PI-015a (chooser), SCR-WT-002/004 (web tap-to-call).
The **number-masking requirement is removed.** The call chooser still offers **Free call** (in-app internet call) and **Normal call** — but Normal call now simply **dials the other party's real number**, which becomes visible only once a driver accepts the ride (and is never shown for rides cancelled before a driver was assigned). On proxy bookings the driver sees the **rider's** number, never the booker's. On the web tracking pages, **Call driver** is a plain tap-to-call link. A first-call tooltip and the sign-up terms disclose that matched parties can see each other's numbers.

### Scenario 109: An internet call fails mid-connect
**Platform:** Passenger or Driver App · **Who:** Caller on poor data · **Goal:** Still reach the other party. *(item 4 / US-26.4)*
**Screens:** SCR-PA/PI-028 / SCR-DA/DI-031 (VoIP call).
A Free (internet) call that cannot connect no longer falls back to a masked text relay — the app simply offers **"Call normally instead?"**, which places a direct call to the same number.

---

## SECTION J — 2026-07-18 CHANGE SET (FLEET PAYOUT & VEHICLE-DOCUMENT DETAIL)
═══════════════════════════════════════════════

> Three Fleet Portal corrections from the 2026-07-18 review (URD Epic 27 / ADD §1.14 AL-49…AL-51). Scenarios 110–112 describe only what changed; earlier scenarios (69, 81, 91) are updated in place where they described the old behaviour.

### Scenario 110: The fleet owner registers bank & payout details
**Platform:** Fleet Portal + Admin Portal · **Who:** Fleet Owner, then Verification Officer · **Goal:** Give Mode B subscription money a verified destination. *(item 1 / US-27.1/27.2)*
**Screens:** SCR-FP-002a (Bank & payout details) → SCR-AP-003 (fleet-org verification queue).
From Organisation setup the Owner opens **Bank & payout details** and enters the **bank, branch, account number and account holder name**, then uploads two documents: a **copy of the latest bank statement (or the first page of the passbook)** as proof of account ownership, and the **LankaQR code image generated by their bank app**. The profile goes to **Pending verification**; a Verification Officer checks that the account-holder name matches the organisation's KYC name and approves it. Once **Verified**, passengers paying a Mode B subscription see exactly this: the owner's **LankaQR code** when paying by QR scan / deep link, and the **verified account details** when paying by online transfer. Only now can a vehicle be set **Service payment = Paid**.
**What can go wrong:** Name mismatch → Rejected with a reason; the Owner corrects and resubmits. Owner edits any field later → the profile re-enters Pending, but paying subscribers keep seeing the **last verified** details until the new ones are approved. No verified profile → the portal blocks Paid vehicles with "Verify your bank & payout details first."

### Scenario 111: Onboarding a vehicle with the full document set
**Platform:** Fleet Portal · **Who:** Fleet Owner / Manager · **Goal:** Onboard a vehicle with the paperwork a Sri Lankan operator actually holds. *(item 2 / US-27.3)*
**Screens:** SCR-FP-004 (Vehicle onboarding).
The vehicle form now has **four named document slots** instead of one generic upload: **registration copy (CR book)**, **insurance certificate**, **revenue license**, and — for **Mode A** vehicles — the **route permit**. Each upload is read automatically (registration number matched against the plate, insurance and revenue-license expiry dates, permit number and route) and shows its own **Verified / Pending / Missing** chip. The vehicle reaches **Approved** only when every required document is verified — for Mode A that includes the route permit; Mode B/CSV-imported vehicles start as **Docs pending** until their slots are completed.
**What can go wrong:** A document fails extraction or a field is doubtful → that slot goes Pending and lands with the Verification Officer. A required document is missing → the row shows exactly which slot (e.g. "CR copy missing") and the vehicle stays unapproved. An approved vehicle's insurance/revenue license/permit expires → dispatch is auto-suspended (existing expiry rule).

### Scenario 112: "Mode B classification" becomes "Service payment"
**Platform:** Fleet Portal · **Who:** Fleet Owner / Manager · **Goal:** Understand the Paid/Free setting at a glance. *(item 3 / US-27.4)*
**Screens:** SCR-FP-004 (Vehicle onboarding).
The setting formerly labelled "Mode B classification" is now simply **"Service payment" — Free or Paid**: does this private service charge its subscribers? A staff/office transport picks **Free** (no fares, no payment screens); anything else picks **Paid** with a default monthly fare. Nothing else changes — the values, the per-subscriber fare overrides (SCR-FP-011) and the underlying system names are untouched; this is a naming fix.

---

═══════════════════════════════════════════════
## SECTION K — 2026-07-22 CHANGE SET #2 (GTFS DATASET MANAGER)
═══════════════════════════════════════════════

> The full national GTFS file is available from day one (URD Epic 28 / ADD §1.16 AL-54…AL-55), and the admin gets a real screen to load it: **SCR-AP-016**. Scenarios 113–114 describe the new interface; the booking-screen behaviour passengers see (Scenario coverage of SCR-PA-009) is unchanged except that "no route coverage" becomes a rare safety-net state instead of the expected launch condition.

### Scenario 113: Loading the full GTFS feed before launch
**Platform:** Admin Portal · **Who:** Admin / Super Admin · **Goal:** Make the complete national bus-route dataset live so passengers see public-transport options from day one. *(US-28.1/28.2)*
**Screens:** SCR-AP-016 (GTFS Dataset Manager).
From the **Configuration** menu the admin opens **Transit data (GTFS)** and drags the full GTFS zip into the upload box. The screen walks through **Uploaded → Validating → Validated**: the system checks that all required files are present, every reference lines up, and every stop actually falls inside Sri Lanka. A **preview card** then shows what the feed contains — how many agencies, routes, trips, stops and shapes, the feed version, and the service date range — plus any warnings. The admin presses **Activate**, confirms, and the live dataset is swapped **atomically**: within a minute the passenger booking screen serves route options from the new feed, and at no point could anyone see a half-loaded dataset. The history table now shows this version as **Active**.
**What can go wrong:** Validation fails → the feed never goes near the live data; the admin downloads a **row-level error report** (file, row, problem), fixes the feed, and re-uploads. Uploading the same file twice → refused with a pointer to the existing version. Activation error mid-swap → the transaction rolls back and the previous feed stays live.

### Scenario 114: Rolling back a bad feed
**Platform:** Admin Portal · **Who:** Admin / Super Admin · **Goal:** Recover quickly when an activated feed turns out to be wrong. *(US-28.3)*
**Screens:** SCR-AP-016 (GTFS Dataset Manager — version history).
A refreshed feed went live, but route 138's stops are wrong on the booking screen. In the **version history** the admin finds the previous feed (now **Archived**), presses **Re-activate**, and confirms. The rollback uses the same atomic swap as activation — the prior dataset is rebuilt from its stored original zip and swapped in whole. Every version ever uploaded stays listed with its status, uploader, and counts; its original zip can be downloaded for at least 12 months, so the broken feed can be pulled apart offline while passengers ride on the restored one.
**What can go wrong:** Only previously **validated** versions can be re-activated — a **Failed** upload is permanently barred from going live; it exists only for its error report.

---

# APPENDIX A — Screen Index

> Every screen ID across all surfaces. Mobile screens exist in both an Android (`SCR-PA`/`SCR-DA`) and iOS (`SCR-PI`/`SCR-DI`) variant; the table lists them together by their shared number.

## Passenger App (SCR-PA-### Android / SCR-PI-### iOS)

| Screen ID | Platform | Friendly Name | Section(s) it appears in |
|---|---|---|---|
| SCR-PA/PI-001 | Passenger App | Splash / boot | A (1) |
| SCR-PA/PI-002 | Passenger App | Onboarding + language | A (1) |
| SCR-PA/PI-003 | Passenger App | Phone & verification code | A (1) |
| SCR-PA/PI-004 | Passenger App | First profile | A (1) |
| SCR-PA/PI-005 | Passenger App | Location permission | A (1) |
| SCR-PA/PI-006 | Passenger App | Mode/type filter | A (3) |
| SCR-PA/PI-007 | Passenger App | Vehicle popup (Mode A) | A (3, 11, 13) |
| SCR-PA/PI-008 | Passenger App | Location search | A (4, 11) |
| SCR-PA/PI-009 | Passenger App | Booking + multimodal options | A (4, 5, 6, 9, 10, 11); F (87) |
| SCR-PA/PI-010 | Passenger App | Live Map Home | A (3, 4, 11, 12, 13, 21); F (91) |
| SCR-PA/PI-010b | Passenger App | Proxy rider details | A (5) |
| SCR-PA/PI-011 | Passenger App | Confirm pickup (rider side) | A (5); C (54) |
| SCR-PA/PI-012 | Passenger App | Package booking | A (6); F (88) |
| SCR-PA/PI-013 | Passenger App | Schedule a ride (**+ destination picker**) | A (9); F (89); G (93) |
| SCR-PA/PI-014 | Passenger App | Finding driver | A (4, 5); F (87) |
| SCR-PA/PI-015 | Passenger App | Ride in progress | A (4, 5, 10, 20); F (87); G (94) |
| SCR-PA/PI-015a | Passenger App | **Call-type chooser (Free VoIP / Normal direct dial — masking removed)** | G (94); I (108) |
| SCR-PA/PI-016 | Passenger App | Payment method | A (4, 6, 15); F (87) |
| SCR-PA/PI-017 | Passenger App | Pay fare | A (15); F (87) |
| SCR-PA/PI-018 | Passenger App | Trip summary | A (15, 18); F (87) |
| SCR-PA/PI-019 | Passenger App | Rate driver | A (18); F (87) |
| SCR-PA/PI-020 | Passenger App | Package tracking (sender) | A (7); F (88) |
| SCR-PA/PI-021 | Passenger App | Package tracking (recipient) | A (8); F (88) |
| SCR-PA/PI-022 | Passenger App | Trip & schedule history (**+ driver mobile + Call**) | A (9, 16, 17); F (89); G (95) |
| SCR-PA/PI-023 | Passenger App | Trip details | A (17) |
| SCR-PA/PI-024 | Passenger App | Mode B access request | A (12); F (91) |
| SCR-PA/PI-025 | Passenger App | My subscriptions | A (14); F (91) |
| SCR-PA/PI-025a | Passenger App | Subscription payment | A (14, 15) |
| SCR-PA/PI-025b | Passenger App | Subscription payment history | A (14) |
| SCR-PA/PI-026 | Passenger App | Saved addresses | A (2) |
| SCR-PA/PI-026a | Passenger App | Add address (sheet) | A (2) |
| SCR-PA/PI-027 | Passenger App | Profile & settings | A (2) |
| SCR-PA/PI-027b | Passenger App | Edit profile | A (2) |
| SCR-PA/PI-028 | Passenger App | In-app call (VoIP) | A (7) |
| SCR-PA/PI-029 | Passenger App | SOS | A (20) |
| SCR-PA/PI-030 | Passenger App | Support + FAQ | A (19) |
| SCR-PA/PI-030a | Passenger App | Raise a ticket (sheet) | A (19) |
| SCR-PA/PI-031 | Passenger App | App update prompt | A (22) |
| SCR-PA/PI-032 | Passenger App | Offline banner | A (21) |
| SCR-PA/PI-033 | Passenger App | Menu / nav drawer | A (2, 14) |

## Driver App (SCR-DA-### Android / SCR-DI-### iOS)

| Screen ID | Platform | Friendly Name | Section(s) it appears in |
|---|---|---|---|
| SCR-DA/DI-001 | Driver App | Splash / boot | B (23) |
| SCR-DA/DI-002 | Driver App | Onboarding language/city (feature infographics · vertical lang · DB cities) | B (23) |
| SCR-DA/DI-003 | Driver App | Phone & verification code | B (23); F (90) |
| SCR-DA/DI-003a | Driver App | Profile setup (name · photo · licence → no/expiry/NIC/allowed types) | B (23); F (90) |
| SCR-DA/DI-004 | Driver App | Vehicle onboarding · Step 1/4 (Mode C) | B (23, 38); F (90) |
| SCR-DA/DI-004a | Driver App | Vehicle onboarding · Step 2/4 (Insurance) | B (23, 38); F (90); G (102) |
| SCR-DA/DI-004b | Driver App | Vehicle onboarding · Step 3/4 (Revenue licence) | B (23, 38); F (90); G (102) |
| SCR-DA/DI-004c | Driver App | Vehicle onboarding · Step 4/4 (Front & back photos) | B (23, 38); F (90); G (102) |
| SCR-DA/DI-005 | Driver App | **Document capture (camera + draggable-corner crop)** | B (23, 38); G (102) |
| SCR-DA/DI-006 | Driver App | Vehicle onboarding status (4-doc, auto-verify) | B (23, 38); F (90) |
| SCR-DA/DI-007 | Driver App | Permissions | B (23) |
| SCR-DA/DI-010 | Driver App | Driver Dashboard Home (own vehicle only · no hamburger) | B (24, 25, 36); F (87, 90) |
| SCR-DA/DI-011 | Driver App | Mode A/B home dashboard — Start/End Journey (GPS-ignition auto-start) | B (24); E (77) |
| SCR-DA/DI-012 | Driver App | Standby toggle (Mode C) — **[MERGED → 010]**, dashboard state, not a screen | B (24) |
| SCR-DA/DI-013 | Driver App | Directional Travel filter | B (32) |
| SCR-DA/DI-014 | Driver App | Incoming dispatch | B (25, 28, 29, 34); F (87, 89, 90) |
| SCR-DA/DI-015 | Driver App | Active ride/trip | B (26, 27, 29, 35, 49, 50); F (87, 88) |
| SCR-DA/DI-016a | Driver App | Package delivery · Review & start (sheet 1/3) | B (29); F (88) |
| SCR-DA/DI-016b | Driver App | Package delivery · Pickup & OTP (sheet 2/3) | B (29); F (88) |
| SCR-DA/DI-016c | Driver App | Package delivery · Complete (sheet 3/3 · Delivery completed) | B (29, 30, 31); F (88) |
| SCR-DA/DI-017 | Driver App | Job Board | B (33); F (89) |
| SCR-DA/DI-018 | Driver App | Scheduled rides | B (34, 35); E (77); F (89) |
| SCR-DA/DI-019 | Driver App | Driver Level & stats | B (36) |
| SCR-DA/DI-020 | Driver App | Earnings dashboard | B (27, 48); F (87) |
| SCR-DA/DI-021 | Driver App | Wallet & fee status | B (42, 43, 44, 46) |
| SCR-DA/DI-022 | Driver App | Top Up Wallet | B (43, 46) |
| SCR-DA/DI-023 | Driver App | Request credit (Driver ID · no QR) | B (44) |
| SCR-DA/DI-024 | Driver App | Credit transfer + requests | B (45) |
| SCR-DA/DI-025 | Driver App | Payment/fee history | B (47, 48) |
| SCR-DA/DI-026 | Driver App | Vehicle management & switcher | B (37, 38, 39, 52); F (91) |
| SCR-DA/DI-026a | Driver App | No-vehicle → onboard Mode C popup | B (23) |
| SCR-DA/DI-027 | Driver App | GPS tracker pairing (device = sole publisher · auto journey-start) | B (40) |
| SCR-DA/DI-028 | Driver App | Sharing management (Mode B · full-width selector) | B (41) |
| SCR-DA/DI-029 | Driver App | Driver profile | (referenced via menu) |
| SCR-DA/DI-030 | Driver App | Ride history + rate passenger (rate via bottom sheet) | (referenced) |
| SCR-DA/DI-031 | Driver App | In-app call (VoIP) | B (50) |
| SCR-DA/DI-032 | Driver App | SOS (driver) | B (49) |
| SCR-DA/DI-033 | Driver App | Support + fee refund | (referenced via menu) |
| SCR-DA/DI-033a | Driver App | Raise a ticket (sheet) | (referenced) |
| SCR-DA/DI-034 | Driver App | Notifications | (referenced via menu) |
| SCR-DA/DI-035 | Driver App | No internet / app update | B (51) |
| SCR-DA/DI-036 | Driver App | Menu / nav drawer | (navigation) |

## Admin Portal (SCR-AP-###, admin.mageride.lk)

| Screen ID | Platform | Friendly Name | Section(s) it appears in |
|---|---|---|---|
| SCR-AP-001 | Admin Portal | Login (password / Google — **no MFA**, 2026-06-28) | D (55); G (96) |
| SCR-AP-002 | Admin Portal | Role-scoped dashboard (**+ stats period filter**) | D (56); G (97) |
| SCR-AP-003 | Admin Portal | Verification **queues list** (licence / vehicle-reg / fleet-org) | D (57, 58); F (90, 91); G (98) |
| SCR-AP-003a | Admin Portal | Verification **detail** (+ document thumbnails) | G (98) |
| SCR-AP-003b | Admin Portal | **Full-size document viewer** (lightbox) | G (98) |
| SCR-AP-003c | Admin Portal | Fleet-org approval detail | G (98) |
| SCR-AP-004 | Admin Portal | Moderation | D (59) |
| SCR-AP-005 | Admin Portal | Support & disputes | D (60) |
| SCR-AP-006 | Admin Portal | Finance & reconciliation | D (61, 62) |
| SCR-AP-007 | Admin Portal | Platform configuration | D (63) |
| SCR-AP-008 | Admin Portal | User & role management (RBAC) | D (64, 65) |
| SCR-AP-009 | Admin Portal | Audit trail | D (66) |
| SCR-AP-010 | Admin Portal | **Passenger directory — search** | G (99) |
| SCR-AP-011 | Admin Portal | **Passenger detail + transactions** | G (99) |
| SCR-AP-012 | Admin Portal | **Driver directory — search** | G (100) |
| SCR-AP-013 | Admin Portal | **Driver detail + transactions** | G (100) |
| SCR-AP-014 | Admin Portal | **Vehicle directory — search** | G (101) |
| SCR-AP-015 | Admin Portal | **Vehicle detail + transactions** | G (101) |
| SCR-AP-016 | Admin Portal | **GTFS Dataset Manager (full-feed upload · validate · activate · rollback)** | K (113, 114) |

## Fleet Portal (SCR-FP-###, fleet.mageride.lk)

| Screen ID | Platform | Friendly Name | Section(s) it appears in |
|---|---|---|---|
| SCR-FP-001 | Fleet Portal | Login / Sign-up | E (67); F (91) |
| SCR-FP-002 | Fleet Portal | Organisation setup (KYC) | E (68); F (91); J (110) |
| SCR-FP-002a | Fleet Portal | **Bank & payout details (bank/branch/account · statement-or-passbook upload · bank-app LankaQR)** | J (110) |
| SCR-FP-003 | Fleet Portal | Fleet dashboard | E (79) |
| SCR-FP-004 | Fleet Portal | Vehicle onboarding (**document slots · Service payment**) | E (69, 70, 81); F (91); J (111, 112) |
| SCR-FP-005 | Fleet Portal | Driver assignment | E (71, 72); F (91) |
| SCR-FP-006 | Fleet Portal | Tracker binding | E (73, 74) |
| SCR-FP-007 | Fleet Portal | Live fleet map | E (75) |
| SCR-FP-008 | Fleet Portal | Scheduling & alarms | E (76, 77) |
| SCR-FP-009 | Fleet Portal | Trip history & analytics | E (78) |
| SCR-FP-010 | Fleet Portal | Billing & wallet | E (79) |
| SCR-FP-011 | Fleet Portal | Mode B subscriptions & requests | E (80, 82, 83, 84, 85); F (91) |
| SCR-FP-012 | Fleet Portal | Subscriber payments ledger | E (86) |

## Passenger Web (SCR-WT-###, passenger.mageride.lk, no-login)

| Screen ID | Platform | Friendly Name | Section(s) it appears in |
|---|---|---|---|
| SCR-WT-001 | Passenger Web | Landing / token gate | H (103, 104) |
| SCR-WT-002 | Passenger Web | Package recipient tracking (no login) | C (53); F (88); H (106) |
| SCR-WT-003 | Passenger Web | Proxy pickup-confirm (no login) | C (54); H (104) |
| SCR-WT-004 | Passenger Web | Ride track (proxy rider · Start OTP · tap-to-call · SOS) | H (103, 105); I (108) |
| SCR-WT-005 | Passenger Web | Delivered / receipt (photo-proof · COD · Disputed) | H (106) |
| SCR-WT-006 | Passenger Web | Expired / invalid link | H (103) |

---

# APPENDIX B — Glossary

Plain-English definitions of the key terms used throughout this document.

| Term | Plain-English meaning |
|---|---|
| **Mode A** | Public transport — buses and trains anyone can watch live on the map. Always free; MageRide only shows position and arrival time. |
| **Mode B** | A specific private vehicle (school van, office bus) that you must be granted access to before you can track it. May be **Free** or a **Paid** monthly subscription. |
| **Mode C** | On-demand hire — a driver you book right now for a ride or a delivery, paying a per-trip fare shown upfront. |
| **Job Board** | A list of upcoming **scheduled** rides (within 30 km) that drivers can express interest in. Drivers "post intent" here; they cannot accept directly — the actual offer comes at 30 minutes before the ride. |
| **Driver Level** | A driver's standing (Level 1–3). Everyone starts at Level 3. Good ratings (4★/5★) earn points (500 points = one level up); reports and no-shows pull it down. Higher level = better dispatch priority and Job Board access. Level 1 loses the Job Board but can still take immediate rides. |
| **Reseller** | Not a special account — just an ordinary driver who buys bulk wallet credit and passes some to other drivers. Their only "margin" is the discount on bulk vouchers; there is **no** per-transfer commission. |
| **Dispatch Offer** | The ride request shown to a driver, with a **15-second** countdown to Accept or Reject. |
| **Directional Travel Filter** | A driver setting that only lets through ride offers heading roughly toward where the driver is going. Limited to a few uses per day; turning it off still uses one. |
| **Fleet Wallet** | A fleet owner's prepaid balance used to pay MageRide's monthly per-vehicle charges. Topped up by card/OnePay/LankaQR (no bank transfer). |
| **Pickup OTP / Pickup code** | A short code the **sender** holds for a package; the driver must enter it to confirm they have collected the parcel. |
| **Delivery OTP / Delivery code** | A short code the **recipient** holds for a package; the driver must enter it to confirm successful delivery. |
| **Start OTP / Start code** | A short code the **passenger** holds for a ride; the driver must enter it to start the trip, proving they have the right passenger. |
| **Proxy Booking** | Booking a ride for someone else (the "rider"), where the booker and the rider are different people. The rider can confirm their own pickup location via the app or a web link. |
| **Bind Code** | An alternative way to pair a GPS tracker device to a vehicle (instead of typing the device's IMEI number or scanning its QR). |
| **Fleet Owner** | A company or individual who owns multiple vehicles and manages them centrally through the Fleet Portal. |
| **Assigned Driver** | A driver who has been temporarily assigned a fleet owner's vehicle and can operate it without owning it. The assignment auto-expires. |
| **Verification Officer** | An internal MageRide staff role that reviews and approves driver documents and fleet organisation identity checks. |
| **LankaQR** | A Sri Lankan QR-based payment method. In MageRide it opens the passenger's bank app with the amount pre-filled, or lets them scan the driver's QR. **No surcharge.** |
| **OnePay** | An in-app card/wallet payment method. Carries a **+5% surcharge**. |
| **COD (Cash on Delivery)** | A package-payment option where the recipient pays the driver cash on arrival. The driver completes the drop with the **Delivery completed** button (there is no separate "Cash received" button); the COD cash is reconciled separately and, if left unreconciled for 24 hours, is flagged **Disputed**. |
| **Anniversary Billing** | A subscription billing cycle that renews on the **same date each month as the join date** (e.g. joined 5 June → next due 6 July), as opposed to billing everyone on the 1st of the month. |

---

*End of MageRide Functional Walkthrough Document — 114 scenarios across Passenger App, Driver App, Passenger Web, Admin Portal, and Fleet Portal (incl. Section G, the 2026-06-28 change set, scenarios 92–102; Section H, the 2026-07-05 Passenger-Web-subview set, scenarios 103–106; Section I, the 2026-07-05 driver-QR-settlement & masking-removal set, scenarios 107–109; Section J, the 2026-07-18 fleet-payout & vehicle-document set, scenarios 110–112; and Section K, the 2026-07-22 GTFS-Dataset-Manager set, scenarios 113–114), plus a full screen index and glossary.*


