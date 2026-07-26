# User Requirements Document (URD) v2.9
# MageRide — Nationwide Real-Time Vehicle Tracking & Ride Platform

> **Status:** For Review
> **Date:** July 22, 2026 (v2.9 — **Discussion 2026-07-22 change set #2: GTFS Dataset Manager** (Epic 28 / ADD v3.4 §1.16 AL-54…AL-55): **a full national GTFS file is available at the beginning** — launch is not gated on corridor-first feed acquisition (the standalone acquisition plan was re-scoped to refresh/maintenance, then retired 2026-07-23 — feeds are externally provided, ADD AL-56); new Admin Portal screen **SCR-AP-016 GTFS Dataset Manager** (Configuration group, Admin/Super Admin): **upload the full GTFS zip** with server-side validation and a downloadable row-level error report (US-28.1); **preview** (counts, feed version, service window) and **atomic activation** — staging load → transactional swap → `transit-svc` cache reload, so passenger route-matching (US-8.2b) serves the new feed immediately (US-28.2); **version history with one-click rollback** to a previously validated feed and original-zip download (US-28.3); new `transit.gtfs_feed_versions` + `/admin/transit/gtfs/*` endpoints, raw `gtfs-import` superseded. v2.8 — **Discussion 2026-07-18 change set: Fleet Portal payout & vehicle-document detail** (Epic 27 / ADD v3.2 §1.14 AL-49…AL-51): **fleet bank & payout profile** — new Owner-only screen **SCR-FP-002a** capturing bank / branch / account number / account holder name plus uploads of the **latest bank statement or passbook first page** and the **bank-app-generated LankaQR code image**, Verification-Officer-approved; the verified profile is what Mode B subscription payments route to (passenger pay sheet shows the owner's LankaQR + transfer details), and **Service payment = Paid requires a Verified profile** (US-27.1/27.2); **SCR-FP-004 vehicle onboarding detailed** with named per-vehicle document slots — **registration copy (CR book), insurance certificate, revenue license, route permit (Mode A required)** — AI-extracted with per-document status, gating vehicle approval (US-27.3); the Mode B **Paid/Free "classification" is renamed "Service payment"** — UI/docs label only, API & DB names unchanged (US-27.4). v2.7 — **Discussion 2026-07-05 change set #2: driver-QR settlement & masking removal** (Epic 26 / ADD v3.1 §1.13 AL-47…AL-48): **driver-QR fare payments settle by attestation** — passenger "I've paid" claim + driver "QR payment received" confirm → terminal **DriverConfirmedQR**; disputes route to Support/Finance (no gateway callback exists for payments into the driver's own bank QR) (US-26.1); **the number-masking requirement is withdrawn** — "Normal call" is a **direct cellular call to the counterparty's real number**, revealed only after driver acceptance (P-05 proxy routing retained: driver sees the rider's number, never the booker's), the call-type chooser keeps **Free (in-app VoIP) / Normal (direct dial)** (US-26.2); the **web subview shows the driver's number as a `tel:` link** — `POST /public/track/{token}/call` + proxy-DID lease removed (US-26.3); **VoIP-failure fallback = direct-dial prompt** (masked-SMS relay D-25 removed) (US-26.4); ToS/PDPA consent copy for number visibility (US-26.5). Supersedes the masking clauses of US-6A.16/16a, US-8.22, US-11.9, US-24.3, US-25.4. v2.6 — **Discussion 2026-07-05 Passenger Web subview contract pass** (8 changes, Epic 25 / ADD v3.0 §1.12 AL-44…AL-46): the `passenger.mageride.lk` no-login subview is formalized into buildable contracts — **screen IDs SCR-WT-001…006** for the six wireframed pages (US-25.1); **public token-authenticated tracking API** `/public/track/{token}` + live feed (US-25.2); **unregistered proxy rider confirms pickup via SMS web link** within the 5-min TTL, extending US-8.19's fallback (US-25.3); **web masked call** via ride-scoped proxy DID, refining US-11.9's masked-VoIP wording (US-25.4); **web SOS** → dual-gateway SMS to the booker (US-25.5); **delivered/receipt page** with OTP-verified / photo-proof / COD / Disputed outcomes (US-25.6); **token scopes package_recipient / proxy_rider / pickup_confirm** with per-scope TTLs, burning & metering (US-25.7); spec hygiene — SCR-DA/DI-012 re-tagged [MERGED → 010], stale wireframe annotations corrected (US-25.8). v2.5 — **Discussion 2026-06-28 change set** (Epic 24, 11 changes — the version-header bump was omitted at the time; content shipped in Epic 24/§6). v2.4 — **Discussion 2026-06-25 driver change pass** (13 changes): driver onboarding **feature-infographic carousel** on the language/city screen (US-1.2a); the driving-licence scan also extracts **NIC no + allowed vehicle types** (shown with licence no + expiry), and on an unclear scan the driver types the value, which is **flagged for admin/Verification-Officer verification** (US-2.4a); any onboarding element that is **doubtful (low confidence), driver-entered (manual), or a plate↔registration-no mismatch** sets that step **Pending** for officer **Confirm / Edit & confirm** before approval (US-2.10a); onboarding **steps are saved individually and resume at the next incomplete step**, and a vehicle shows in **My Vehicles** as **Incomplete** (≥1 step) or **Approved** (all steps) (US-2.26); once a vehicle is fully onboarded & **Approved**, opening Vehicle Onboarding / tapping ＋ starts a **fresh Step 1/4 for a NEW vehicle** (US-2.27); the **driver home map shows only the driver's own active vehicle** and the top-left hamburger is removed (US-7.18); when the active vehicle is **Mode A or Mode B the Start/End-Journey screen is the home dashboard** (only Start/End buttons; vehicle type & number below the route card) and a **GPS device started by ignition auto-starts the journey, which the dashboard can override** (US-5.11); **package delivery is a three-stage bottom-sheet flow** — review (distances, payment, sender/recipient call buttons, Start/Cancel→re-dispatch) → pickup OTP → delivery OTP + **"Delivery completed"** (replacing "Cash received (COD)") (US-20.12/20.13); **QR scanning removed from the driver credit-request flow** (US-9.10); the Mode B sharing screen drops the "Showing sharing for …" caption and shows a **full-width vehicle selector**; **rate-passenger opens in a bottom sheet** (US-18.2). v2.3 — **Discussion 2026-06-21 change pass** (17 changes): vertical language boxes Sinhala-first (US-1.3) & language removed from Edit-profile (US-1.5); coloured vehicle-type icons; **geo-location-only search** (US-8.2a) with **GTFS direct public-bus routes** (US-8.2b) and **Mode C price-only** tiers (US-8.2c); **Paste-link** location input (US-8.2d); package **drop-off capture** (US-20.2); **add-address ModalBottomSheet** with Address Line 1/2/3 + Label (US-22.2); **Mode B marker → access request** (US-4.9); **menu nav drawers** (US-22.7, driver); **recipient FCM/SMS web-tracking** on pickup confirm (US-20.5); **per-vehicle sharing/requests** (US-4.10); **QR-scan pay** (US-8.10b); **unsubscribe + muted-until-deleted** (US-4.11/4.12); new **Epic 23 — Mode B Subscription Payments & Requests** (US-23.x) + fleet stories (US-13.13–13.16, US-13.1b). v2.2 — **Conflict-resolution pass** (resolves C-01…C-15 + O-1…O-4 from `conflicting-requirements.md`): auth scoping clarified (apps = Phone OTP; web portals = Email/Password/Google/Apple); Passenger Web Portal is a no-login **subview of the Passenger App** (four surfaces); **canonical vehicle-type enumeration** applied to fee/tariff/map/registration/dispatch (Car→Sedan; +Flex/Mini Van tariffs; +Truck/Mini Truck for delivery); US-1.11 merged into US-1.12; near-pickup 1 s burst cadence defined; cancellation penalty clarified; **Bank Transfer removed** as a top-up method; fleet billing = monthly per Mode B vehicle (Mode A free, Mode C non-fleet); Fleet sub-roles (Owner/Manager/Viewer); insurance mandatory for **all** modes; VoIP promoted to P0; single-active-device is **per app**. v2.1 — Insurance + driver emergency contact + Epic 22. v2.0 — Passenger Web Portal. v1.4–v1.9 — see history)
> **Audience:** Product, Engineering, Design, Investors

---

## 1. Product Vision

A mobile-first platform called **MageRide**, consisting of two mobile applications (Android & iOS)—a **MageRide Passenger App** and a **MageRide Driver App**—plus two web applications: the **Admin Portal** (`admin.mageride.lk`, back-office) and the **Fleet Portal** (`fleet.mageride.lk`). MageRide enables passengers across Sri Lanka to see nearby public buses, track private transport vehicles (school buses, book hires), and book on-demand rides on a live map. The platform operates on a **zero-commission model** that mirrors **Namma Yatri (India)** — drivers keep **100% of passenger fares**. For Mode C (Standby On-Demand) drivers, the **first trip of the day is always free**; from the 2nd trip, a flat **daily platform fee** (vehicle-type dependent: **Motorbike Rs 50**, **Three-wheeler Rs 100**, **Flex Rs 150**, **Sedan Rs 200**, **Mini Van Rs 250**, **Van Rs 300**) is auto-deducted from their wallet. Mode A (Public Transport) buses pay **no daily fee**. Mode B (Private Transport) vehicles pay a **monthly charge of approximately Rs 300**. **No per-trip fee, no commission.** **Passengers use MageRide completely free** — no subscription or premium tier. Drivers top up their wallet **entirely in the app** using credit/debit card, OnePay, or LankaQR — drivers never access the web portal. MageRide operates on **free and open-source mapping** to eliminate per-user map SDK licensing costs.

---

## 1.A. Service Modes

The platform supports three distinct modes of service:
* **Mode A (Public Transport):** Public **buses and trains** sharing their live location along fixed routes. **Free to track** — no daily platform fee. **Trains are registered by Admin only** (not via the Driver App); buses may be registered by the bus owner/driver. Passengers can filter trains separately, see them in a distinct colour/icon on the map, and have trains appear among transport options when entering a destination.
* **Mode B (Private Transport):** Private vehicles (school buses, book hires, etc.) visible only to subscribed users who have been granted access. Monthly charge of approximately Rs 300. First month free for subscribers.
* **Mode C (Standby On-Demand):** Motorbikes, three-wheelers, and cars/vans (Flex, Sedan, Mini Van, Van) on standby to be dispatched for point-to-point passenger requests; **Truck / Mini Truck** are additionally available for package delivery (Epic 20). Higher cost, premium service. Daily platform fee applies (first trip free). Standby drivers may optionally set a **Directional Travel** (Destination Filter) so that — for a limited period and a limited number of times per day — they only receive hires heading in their chosen direction (e.g., towards home at the end of a shift).

---

## 1.B. Canonical Vehicle Types

> This single enumeration is **authoritative** and is applied consistently across vehicle registration (Epic 2), the daily-fee table (Epic 9), the fare-tariff table (Epic 8), map markers (MAP-03), and dispatch `vehicle category` (Epic 6A). **"Car" is not used — it is represented as "Sedan".**

| Group | Vehicle Types | Used For |
|---|---|---|
| **Mode C — Passenger ride** | **Motorbike, Three-wheeler, Flex, Sedan, Mini Van, Van** | Standby on-demand passenger rides (fare + daily fee) |
| **Mode C — Package delivery (additional)** | **Truck, Mini Truck** (plus all passenger-ride types) | Package delivery (Epic 20) |
| **Mode A / Mode B — Transport** | **Bus, Train** (Mode A public); private Mode B vehicles use the same ride types above (e.g., school bus, van) | Public transport (A) and private transport (B) |

> **Note:** Trains are Mode A and **admin-registered only** (§1.A). All rates and markers below reference these exact type names.

---

## 2. User Roles & Access Control

### 2.1 Platform Roles

MageRide defines **nine canonical roles**. Three are **end-user roles** (self-serve via the apps/Fleet Portal); the **other six are internal/back-office roles** that all log in to the **Admin Portal** (`admin.mageride.lk`, see Epic 14) with **role-scoped menus and least-privilege defaults**. Every user is assigned one or more roles and may perform only the actions permitted to those role(s) per the **Feature Permission Matrix (§2.3)**.

| # | Role | Description | Primary Surface | Auth |
|---|---|---|---|---|
| 1 | **Driver** | Operates one or more vehicles across **Mode A** (public), **Mode B** (private), and **Mode C** (standby on-demand): registers vehicles, runs sessions, accepts dispatches/packages, sets Directional Travel, manages own wallet, may **resell bulk-purchased wallet credit** to other drivers, and may accept a Fleet Owner's vehicle assignments. Fee treatment differs by mode (Mode A free, Mode B ~Rs 300/month, Mode C daily fee). | MageRide Driver App (Android & iOS) | Phone OTP (SMS) |
| 2 | **Passenger** | Tracks Mode A vehicles, books Mode C rides/packages, tracks granted Mode B vehicles. **Fully free — no subscription.** | MageRide Passenger App (Android & iOS) | Phone OTP (SMS) |
| 3 | **Fleet Owner** | Owns/operates a fleet organisation of **Mode A (public) and/or Mode B (private)** vehicles: onboards vehicles, assigns/revokes drivers, binds hardware trackers, schedules rides, views the fleet map & per-vehicle analytics, and pays a **monthly fee per Mode B vehicle** from the fleet wallet (Mode A vehicles are free). **Mode C is not a fleet option** — Mode C daily fees are always paid from the individual driver's wallet. May provision **Manager / Viewer** sub-users within the org. Scoped to **own organisation only** (row-level security). | Fleet Portal (`fleet.mageride.lk`) | Email + Password / Google / Apple |
| 4 | **Admin** | Day-to-day platform administration: moderation, tariff/feature configuration, end-user account management, analytics, announcements. **Cannot** manage internal roles/permissions or other admin accounts (that is Super Admin). | Admin Portal (`admin.mageride.lk`) | Password or Google Sign-In |
| 5 | **Super Admin** | Highest privilege. All Admin powers **plus user & role management, permission & feature-flag configuration, internal-account provisioning, and system-level settings**. | Admin Portal (`admin.mageride.lk`) | Password or Google Sign-In |
| 6 | **Driver Onboarding / Verification Officer** | Reviews driver documents, driving licenses, vehicle registration, and background checks; **approves or rejects** registrations with a recorded reason. No financial or system-config access. | Admin Portal (`admin.mageride.lk`) | Password or Google Sign-In |
| 7 | **Support Agent / CSR** | Handles passenger and driver complaints, trip disputes, and refund **requests**; read-only trip/user lookup; actions support tickets; can raise refunds and apply limited temporary actions (e.g., block on reports). | Admin Portal (`admin.mageride.lk`) | Password or Google Sign-In |
| 8 | **Finance / Payments Officer** | Manages **payouts, commissions, settlements, payment-gateway reconciliation (OnePay/LankaQR), wallet adjustments/reversals**, and fee and voucher-discount configuration. | Admin Portal (`admin.mageride.lk`) | Password or Google Sign-In |
| 9 | **Auditor** | **Read-only** access to logs, transactions, audit trails, and reports for compliance. **Cannot mutate any data.** | Admin Portal (`admin.mageride.lk`) | Password or Google Sign-In |

> **Service modes are not roles.** Mode A / Mode B / Mode C (see §1.A) are operating modes of the single **Driver** role; one driver may operate in several modes with different vehicles. A **fleet-assigned driver** is simply a Driver who accepted a Fleet Owner's vehicle assignment — not a distinct role.

> **Reseller is not a separate role.** "Reseller" is simply any **driver** who has **purchased bulk credit cheaply** (at the tiered bulk-voucher purchase discount) and then **transfers wallet credit to other drivers**. There is no standalone reseller account, login, portal, or "enable" step, and **no per-transfer commission** — every driver-to-driver transfer moves the **exact value**. The reselling driver's margin comes entirely from the **purchase discount** on bulk credit vouchers (configured per tier in the database). All credit-transfer functions are performed in the Driver App under the driver's existing identity (Phone OTP).

> **Roles are composable.** A user may hold more than one role (e.g., a Driver who is also a Fleet Owner; a Super Admin who also performs Finance tasks). Effective permissions are the **union** of the user's roles, always bounded by the Feature Permission Matrix. **Internal roles (4–9) are provisioned only by a Super Admin** (~~and require MFA~~ — **no second factor**, US-24.5).

> **Fleet team sub-roles (org-scoped).** Within the **Fleet Owner** role, a Fleet Owner can provision **Manager** and **Viewer** sub-users for their **own organisation only** (the Owner is the org's primary account). These three sub-roles — **Owner / Manager / Viewer** — are an **org-scoped sub-model of the Fleet Owner role**, not part of the nine canonical roles: Owner = full org control + billing; Manager = onboarding, assignment, scheduling, monitoring (no billing/owner changes); Viewer = read-only fleet map & analytics. The "internal roles provisioned only by Super Admin" rule applies to roles **4–9 only**; fleet sub-users are provisioned by the Fleet Owner and are always scoped to that organisation.

### 2.2 Admin Portal & Authentication

**All six internal roles log in to a single back-office web application — the Admin Portal at `admin.mageride.lk`** — where all MageRide back-office functions are performed (moderation, onboarding/verification, support, finance/wallet reconciliation, configuration, RBAC, audit, reporting). **Authentication is by Password or Google Sign-In only — there is no OTP / TOTP / authenticator second-factor step** (the previously-shown MFA challenge was removed per the 2026-06-28 change set, US-24.5; sign-in completes straight to the dashboard). Account is protected by failed-attempt lock-out and an optional IP allow-list rather than a second factor. Each role sees only the menus and records its permissions allow; the UI is rendered from the same permission model the API enforces server-side (**deny-by-default**; every privileged endpoint checks the caller's role grants). Internal accounts follow least-privilege, and all of their mutating actions are written to the **immutable admin-action audit log** (US-19.3 / Epic 21) visible to Auditors and Super Admins.

> **Note:** The platform has **four surfaces**: the **MageRide Passenger App**, **MageRide Driver App**, the **Fleet Portal** (`fleet.mageride.lk`), and the **Admin Portal** (`admin.mageride.lk`). `passenger.mageride.lk` is **not a separate surface** — it is a **no-login web subview of the Passenger App**, opened only via a tokenised SMS link when a driver accepts a proxy ride (US-8.22). Only **Driver, Passenger, and Fleet Owner** are end-user roles (apps / Fleet Portal); **every other role logs in to the Admin Portal**. Drivers top up wallets and perform reseller transfers **in-app**; dispute resolution and finance reconciliation are handled by Finance/Admin in the Admin Portal. **MageRide operates on a zero-commission model** — passengers pay fares directly to drivers; the platform charges Mode C drivers a flat daily fee (first trip free) and Mode B drivers a monthly fee. Mode A (public buses) pay no fee.

### 2.3 Role-Based Access Control — Feature Permission Matrix

> Every feature area is gated by this matrix. A user's effective rights are the union of their roles, bounded by these grants; the API enforces the same matrix server-side (deny-by-default).

**Legend:** **✅ Full** (create/edit/execute) · **⚙ Configure** (settings only) · **👁 Read-only** · **◐ Own-scope / limited** · **➖ No access**
**Columns:** DRV = Driver · PAX = Passenger · FLT = Fleet Owner · ADM = Admin · S.ADM = Super Admin · VER = Verification Officer · CSR = Support/CSR · FIN = Finance Officer · AUD = Auditor

| Feature Area | DRV | PAX | FLT | ADM | S.ADM | VER | CSR | FIN | AUD |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| **Passenger** — book/track/rate rides & packages, SOS, trip history | ➖ | ✅ | ➖ | 👁 | 👁 | ➖ | 👁 | ➖ | 👁 |
| **Driver app** — register vehicle, sessions, accept dispatch, Directional Travel | ◐ own | ➖ | ➖ | 👁 | 👁 | 👁 | 👁 | ➖ | 👁 |
| **Driver wallet & credit transfers** — top-up, bulk vouchers, initiate/approve driver-to-driver credit transfers (**Driver App only**) | ◐ own | ➖ | ➖ | 👁 | 👁 | ➖ | 👁 | 👁 | 👁 |
| **Driver wallet adjustments / reversals** (back-office) | ➖ | ➖ | ➖ | 👁 | ✅ | ➖ | ➖ | ✅ | 👁 |
| **Fleet** — org & vehicle onboarding, driver assignment, scheduling | ◐ assigned | ➖ | ◐ own org | 👁 | ✅ | 👁 | 👁 | ➖ | 👁 |
| **Fleet** — live fleet map, per-vehicle analytics | ➖ | ➖ | ◐ own org | 👁 | 👁 | ➖ | 👁 | 👁 | 👁 |
| **Fleet billing** — monthly per-Mode-B-vehicle invoice, fleet wallet | ➖ | ➖ | ◐ own org | 👁 | ✅ | ➖ | 👁 | ✅ | 👁 |
| **Hardware trackers** — bind to vehicle, bulk onboard, fleet health | ◐ own | ➖ | ◐ own org | 👁 | ✅ | ➖ | ➖ | ➖ | 👁 |
| **Hardware trackers** — decommission / revoke credentials | ➖ | ➖ | ◐ own org | ✅ | ✅ | ➖ | ➖ | ➖ | 👁 |
| **Onboarding/Verification** — review docs, approve/reject registrations, background checks | ➖ | ➖ | ➖ | ✅ | ✅ | ✅ | 👁 | ➖ | 👁 |
| **Support** — tickets, trip disputes, block/unblock, temporary delisting | raise | raise | ◐ own org | ✅ | ✅ | ➖ | ✅ | ◐ financial | 👁 |
| **Refunds** — raise / approve / execute | raise | raise | ➖ | ✅ | ✅ | ➖ | ◐ raise/recommend | ✅ approve/execute | 👁 |
| **Moderation** — suspend/ban driver or vehicle, review reports | ➖ | report | ➖ | ✅ | ✅ | ◐ at onboarding | ◐ temp on reports | ➖ | 👁 |
| **Finance** — payouts, settlements, reconciliation, wallet reversals/adjustments | ➖ | ➖ | ➖ | 👁 | ✅ | ➖ | ➖ | ✅ | 👁 |
| **Platform config** — fare tariffs, daily-fee rates, bulk-voucher discount tiers, subscription pricing | ➖ | ➖ | ➖ | ⚙ | ✅ | ➖ | ➖ | ⚙ rates | 👁 |
| **Platform config** — Driver Level params, feature flags, system settings | ➖ | ➖ | ➖ | ◐ subset | ✅ | ➖ | ➖ | ➖ | 👁 |
| **User & role management (RBAC)** — provision internal users, assign roles, define permissions | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | 👁 |
| **End-user account management** — KYC status, deactivate/restore users | ➖ | ➖ | ➖ | ✅ | ✅ | ◐ verification | ◐ on tickets | ➖ | 👁 |
| **Analytics & reporting** — platform-wide dashboards | ➖ | ➖ | ◐ own org | ✅ | ✅ | ➖ | 👁 | ◐ financial | 👁 |
| **Audit logs & admin-action trail** | ➖ | ➖ | ➖ | 👁 | 👁 | ➖ | ➖ | 👁 | ✅ read |
| **Announcements / broadcast** | ➖ | ➖ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | 👁 |

> "raise" / "report" = the end user can submit a request or report from their app, but cannot adjudicate it. "◐ own" / "◐ own org" = limited to records the user owns or that belong to their organisation.

### 2.4 Allowed-Feature Summary by Role

- **Driver** — own vehicle registration & status; session start/end (Mode A/B/C); accept/reject dispatches, scheduled jobs, packages; Directional Travel; own wallet (top-up, bulk vouchers, daily-fee status); driver-to-driver credit transfers (request/approve/send by Driver ID, transfer history); accept fleet vehicle assignments; raise support tickets/refund requests; in-app VoIP; SOS.
- **Passenger** — live map & filters; book/schedule Mode C rides & packages (incl. proxy); track granted Mode B & public Mode A; fares & in-app payment; rate trips; trip/package history; SOS; block driver; report vehicle; raise support tickets; unsubscribe Mode B.
- **Fleet Owner** — organisation & team-member management (own org), incl. provisioning **Manager / Viewer** sub-users; vehicle onboarding (single/bulk) & lifecycle for **Mode A and/or Mode B**; driver assignment/revocation; tracker binding & fleet health; scheduling & alarms; live fleet map & per-vehicle analytics (own org); **monthly per-Mode-B-vehicle billing** & fleet wallet (Mode A free; **no Mode C**).
- **Admin** — moderation (suspend/ban, reports); approve/reject registrations; tariff/fee/Driver-Level configuration; end-user account management; support oversight; platform analytics; announcements; view finance & audit data. **No** RBAC/role management.
- **Super Admin** — everything Admin can do **plus** RBAC (provision internal users, assign roles, define permissions), feature flags, system settings, and elevated finance/config actions.
- **Verification Officer** — onboarding queue: review AI-extracted documents, licenses, vehicle registration & background checks; approve/reject with reason; view (read-only) related driver/vehicle records.
- **Support Agent / CSR** — support ticket queue; trip/user read-only lookup; investigate disputes; raise/recommend refunds; limited temporary actions (block on reports); respond to passengers/drivers.
- **Finance / Payments Officer** — payouts, commissions, settlements, payment-gateway (OnePay/LankaQR) reconciliation; wallet reversals/adjustments; approve/execute refunds; configure fee/commission/voucher rates; financial reporting.
- **Auditor** — read-only access to all logs, transactions, audit trails, and reports; export for compliance; no write access anywhere.

---

## 3. Map Strategy — Zero Google Maps Cost

> **IMPORTANT:** The application **must not** use Google Maps SDK to avoid per-session/per-load billing. All mapping uses free and open-source alternatives. The public OSM tile servers (tile.openstreetmap.org) **prohibit commercial/heavy use** and will block the app — tiles must be self-served.

### 3.1 Chosen Map Stack

| Component | Technology | Cost |
|---|---|---|
| **Map data** | OpenStreetMap (OSM) — open license | **Free** |
| **Android map renderer** | Maplibre GL Native (Android SDK) | **Free** (BSD license, fork of Mapbox GL pre-proprietary) |
| **Web map renderer** (admin) | Maplibre GL JS | **Free** |
| **Tile format** | PMTiles (Protomaps) — single static file containing Sri Lanka vector tiles (~2–5 GB) | **Free** to generate |
| **Tile storage** | Cloudflare R2 (S3-compatible object storage) | **$0 egress fees**, ~$0.015/GB storage |
| **Tile CDN** | Cloudflare CDN (free tier — unlimited bandwidth) | **Free** |
| **Geocoding** (address → lat/lng) | Nominatim (self-hosted) or Photon | **Free** |
| **Reverse geocoding** (lat/lng → address) | Nominatim (self-hosted) | **Free** |
| **Routing / ETA** (Phase 3) | OSRM or Valhalla (self-hosted) | **Free** |
| **Map style** | Custom OSM-based style (OpenMapTiles schema) | **Free** |

### 3.2 Tile Serving Architecture

```
OpenStreetMap Data (free)
        │
        ▼  generate once, update weekly
┌─────────────────────┐
│ Sri Lanka PMTiles    │  (~2–5 GB single static file)
│ (vector tiles)       │
└────────┬────────────┘
         │  upload
         ▼
┌─────────────────────┐     Cache HIT (~99% of requests)
│ Cloudflare R2       │◄────────────────────────────┐
│ (object storage)    │                              │
│ $0 egress fees      │     ┌────────────────────┐   │
└────────┬────────────┘     │ Cloudflare CDN     │───┘
         │                  │ (free tier)         │
         └─────────────────►│ Edge-cached tiles   │
                            └────────┬───────────┘
                                     │
                                     ▼
                            Android / Web apps
                            (Maplibre GL renderer)
```

> **Why PMTiles + Cloudflare R2?** PMTiles is a single static file — no tile server process to run or maintain. Cloudflare R2 has **zero egress fees** (unlike S3 which charges per GB transferred). Cloudflare's free CDN tier provides **unlimited bandwidth**. This combination makes tile serving nearly free even at 1M+ users.

### 3.3 Map Cost Estimates vs Google Maps

| Scale | PMTiles + Cloudflare R2 | Google Maps SDK |
|---|---|---|
| Dev (10–20 users) | **~$0/mo** | ~$0 (within free tier) |
| Launch (10k–100k users) | **~$5–20/mo** | ~$500–2,000/mo |
| Scale (1M users) | **~$20–50/mo** | **~$7,000–14,000/mo** |

> **Cost saving at 1M users: ~99.5%** compared to Google Maps SDK.

### 3.4 Map Feature Requirements

| ID | Requirement |
|---|---|
| MAP-01 | Display OpenStreetMap-based vector tiles with custom style (dark mode + light mode) |
| MAP-02 | Show user's current location with accuracy circle |
| MAP-03 | Display vehicles as color-coded animated markers by **canonical vehicle type (§1.B)**: bus=green, **train=red with rail icon**, motorbike=purple, three-wheeler=yellow, Flex=teal, Sedan=blue, Mini Van=pink, Van=orange, Truck=brown, Mini Truck=olive, private=grey |
| MAP-04 | Smooth marker animation between position updates (interpolation) |
| MAP-05 | Cluster vehicle markers when zoomed out to prevent clutter |
| MAP-06 | Show vehicle direction arrow (heading indicator) on marker |
| MAP-07 | Tap vehicle marker to see details popup (for Mode A public transport — buses & trains — and Mode B private transport only) |
| MAP-08 | Draw trip polyline on map for active journeys |
| MAP-09 | Offline map tile caching for frequently used areas |
| MAP-10 | Geofence visualization (100m circle for auto-end zones) |

---

## 4. User Stories by Epic

### Epic 1: User Registration & Onboarding

| ID | Story | Priority |
|---|---|---|
| US-1.1 | As a new user, I can register using my **mobile number + OTP** (sent via local SMS gateway) | P0 |
| US-1.2 | As a new user, I can see a brief onboarding tutorial (3 slides) explaining app features on first launch | P2 |
| US-1.2a | As a new **driver**, the language/city onboarding screen (SCR-DA-002 / SCR-DI-002) shows a **feature-infographic carousel (3 auto-advancing / swipeable slides with paged dots)** at the top — mirroring passenger onboarding (SCR-PA-002) — so I grasp the core driver features (vehicle onboarding, 15 s dispatch, Directional Travel, in-app wallet & daily fee) before choosing language & city. *(2026-06-25 item 1)* | P2 |
| US-1.3 | As a user, I can set my preferred language during onboarding. The language options are presented as **vertical selectable boxes, one per row, ordered Sinhala (first) → Tamil → English** (default highlight = Sinhala). *(Discussion item 1)* | P1 |
| US-1.3a | As a user, I choose my **operating city** during onboarding from a list of **launch cities that are stored in the backend database** (`config.operating_cities`) and **managed by an admin in the Admin Portal** — not hard-coded in the app. The app loads the active cities on first run (`GET /config/cities`); adding/activating a new launch city needs no app release. The selection is saved with my profile (`iam.users.operating_city_code`) and seeds the map centroid. *(Discussion item 1)* | P1 |
| US-1.5 | As a user, I can edit my profile (name, photo, notification preferences). **Language is no longer part of the Edit-profile screen** — language is set during onboarding (SCR-PA-002) and from Profile & settings (SCR-PA-027). *(Discussion item 10)* | P1 |
| US-1.7 | As a user, I can log out of my account from the app settings | P0 |
| US-1.8 | As a user, I can delete my account and all associated personal data from within the app (PDPA / Google Play compliance) | P1 |
| US-1.9 | As a returning user, my session persists across app restarts — I do not need to re-authenticate unless I explicitly log out or my token expires | P0 |
| US-1.10 | As a user, if my OTP is not received, I can request a new OTP after a 60-second cooldown | P0 |
| US-1.12 | **Single active device (per app).** Each app account (Passenger App, or Driver App) may have **only one active device session at a time** (Android or Apple). The **same person may run the Driver App and the Passenger App simultaneously** — the one-device rule is enforced **per app**, not per person. Logging in to a given app on a **new device immediately revokes that app's previous device session**: the platform **invalidates the prior device's access and refresh tokens server-side** (Redis + PostgreSQL), so the old device's next API call or silent refresh is rejected, and an **FCM/APNs force-logout push** signals the old device to clear local session and return to the login screen — typically within seconds. *(Token mechanics: on each device login the backend issues a new 30-minute access token + refresh token stored in Redis + PostgreSQL; the client silently refreshes on expiry. Issuing the new device's token revokes the prior device's tokens for that app.)* | P0 |
| US-1.13 | As a user whose previous device was revoked, that old device shows a clear message (e.g., *"You're signed out because your account was used on another device"*) and **cannot access any authenticated screen or cached personal data** until re-authentication. | P0 |
| US-1.14 | **Active-trip continuity on device switch (critical).** If a **driver switches phones mid-trip** (or a passenger mid-ride), the new device, on login, **immediately restores the active/ongoing trip state** (trip ID, role, pickup/drop-off, live status, navigation, fare meter, session timer) via the eager-fetch set (US-1.15) so the trip continues seamlessly; the old device simultaneously loses access (US-1.12). | P0 |
| US-1.15 | **Eager fetch on login — essential data only.** On successful login the app fetches a small, bounded payload: **(1)** user profile, **(2)** saved addresses, **(3)** payment-method metadata, **(4)** any **active/ongoing trip** (critical — see US-1.14), **(5)** for drivers: current **shift/online status** and **today's earnings summary**, **(6)** app config & feature flags. No large or unbounded lists are loaded at login. | P0 |
| US-1.16 | **Lazy fetch per screen — large/unbounded data.** Large data is fetched only when its screen is opened: **trip history** is **paginated** (page 1 on opening the History screen, more on scroll); **earnings breakdowns** by week/month are fetched when that screen opens; **receipts/invoices** are fetched **on tap**. This keeps login fast and minimises data usage (ties to NFR-32, NFR-03). | P1 |

> **Cost Decision & Auth Scope:** Firebase Phone/OTP auth is **avoided** due to **$0.27 per SMS in Sri Lanka** (~Rs 90) with no free allowance. Instead, OTP is delivered via a **local SMS gateway** (e.g., Notify.lk at ~Rs 0.50–1.50/SMS), reducing auth cost by ~95%. **The Passenger App and Driver App use Phone OTP only** (no email/password) — a phone number is required for driver contact and notifications. **Email + Password / Google Sign-In / Apple Sign-In apply only to the web portals**: the **Fleet Portal** (`fleet.mageride.lk`) uses Email+Password / Google / Apple; the **Admin Portal** (`admin.mageride.lk`) uses Password or Google Sign-In (§2).

> **Data Population Strategy:** To keep login fast and data usage low, the apps split data loading into **(a) eager fetch on login** — only the small, essential set needed to make the app usable immediately (profile, saved addresses, payment-method metadata, **active/ongoing trip**, driver shift/online status & today's earnings summary, app config/feature flags); and **(b) lazy fetch per screen** — the large, unbounded data loaded on demand (trip history paginated on the History screen, earnings breakdowns by period on the Earnings screen, receipts/invoices on tap). The **active-trip object is always part of the eager set** so a mid-trip device switch restores trip state instantly.

---

### Epic 2: Vehicle Registration

| ID | Story | Priority |
|---|---|---|
| US-2.1 | As a public bus owner/driver, I can register my bus by providing {bus route number, owner mobile, bus registration number, permit paper photo, **vehicle insurance certificate**, **revenue license**} | P0 |
| US-2.2 | The app extracts permit data from the uploaded photo using AI (Gemini Flash) and stores structured data in the database | P0 |
| US-2.3 | As a Mode C vehicle owner (**Motorbike, Three-wheeler, Flex, Sedan, Mini Van, Van** — and **Truck / Mini Truck** for delivery; §1.B), I can register my vehicle by providing {vehicle registration number, **canonical vehicle type**, owner mobile, vehicle registration document, **vehicle insurance certificate**, **revenue license**, driving license both sides, profile picture, vehicle photo} | P0 |
| US-2.4 | The app extracts ID data from the uploaded photo using AI and stores structured data | P0 |
| US-2.4a | At **driver Profile Setup** (SCR-DA-003a / SCR-DI-003a), the **driving-licence scan** (Gemini Flash) extracts and displays — alongside **Licence no** and **Expiry** — the **NIC number** and the **allowed vehicle types** (licence classes). **If any element is unreadable due to image clarity, the driver can type it in**; any **driver-entered** element is **flagged for admin / Verification-Officer verification** (SCR-AP-003) and stored with `source ∈ {ai, manual}` + `verify_status` before it is trusted. *(2026-06-25 item 2)* | P0 |
| US-2.5 | As a private transporter, I can register my vehicle by providing {vehicle registration number, vehicle type, owner mobile, driving license both sides, registration document photo, **vehicle insurance certificate**, **revenue license**, profile picture, vehicle photo} | P0 |
| US-2.6 | Upon successful registration, the system generates a unique Vehicle ID for the vehicle | P0 |
| US-2.7 | A vehicle can only be registered to **one mobile phone** at a time | P0 |
| US-2.8 | As a vehicle owner, I can register multiple vehicles under my account | P1 |
| US-2.9 | As an admin, I can review and approve/reject vehicle registrations with AI-extracted data flagged for manual review | P1 |
| US-2.10 | If AI extraction confidence is below threshold, the registration is queued for manual review with extracted fields editable by admin | P1 |
| US-2.10a | During **Mode-C in-app onboarding**, any data element that is **doubtful (AI confidence below threshold)**, **driver-entered (manual)**, or — for the vehicle-photos step — a **plate ↔ registration-no mismatch**, sets **that step's status to Pending** and is **flagged for a Verification Officer to Confirm or Edit & confirm** in the Admin Portal (SCR-AP-003, per-field Source/Status + per-step breakdown). A step whose fields are all auto-verified and in-confidence is **auto-Verified**; **the vehicle is not Approved until every Pending element is confirmed**; all confirm/edit actions are audited. *(2026-06-25 items 2,3,4,5)* | P0 |
| US-2.11 | As a vehicle owner, I can upload updated permit/ID documents when they expire | P2 |
| US-2.12 | As a driver, I must provide my **profile photo** and **full name** during vehicle registration, which are displayed to passengers | P0 |
| US-2.13 | As a driver, I can view the current status of my vehicle registration (Pending / Approved / Rejected) from the Driver App, with the **"Under review" section showing a per-document status breakdown**. For **Mode-C in-app onboarding (US-2.22/2.23)** this is the **4-document** view — **vehicle details · insurance · revenue licence · front & back photos** — each Verified/Pending (driving license + profile photo are captured once at Profile Setup, US-2.21). | P0 |
| US-2.14 | As a driver, I receive a push notification and in-app message when my vehicle registration is approved or rejected by the admin | P0 |
| US-2.15 | As an admin, when rejecting a vehicle registration I must provide a rejection reason which is displayed to the driver | P1 |
| US-2.16 | As a driver, I can deactivate or remove a vehicle from my account (e.g., sold vehicle), which removes it from the map immediately | P1 |
| US-2.17 | As an **admin**, I can register a **train** (Mode A) from the Admin Portal by providing {train/service number, route/line, operator}; **train registration is admin-only and is not available in the Driver App** | P0 |
| US-2.18 | As an **admin**, I can edit, deactivate, or remove a registered train from the Admin Portal | P1 |
| US-2.19 | The **vehicle insurance certificate** is a **mandatory document for all modes (A/B/C)**; it is uploaded at registration, AI-extracts policy/insurer/expiry, is admin-verified, and **insurance expiry auto-suspends dispatch** until renewed (AL-10). Admin-registered trains are exempt (line-level cover) | P0 |
| US-2.20 | The **revenue license** (vehicle revenue licence) is a **mandatory document** for every vehicle in all modes; it is uploaded at registration, AI-extracts its expiry, is admin-verified, and shown alongside other document statuses on the registration hub and "Under review" screen. Like insurance, **revenue-license expiry auto-suspends dispatch** until renewed (mirrors AL-10) | P0 |
| US-2.19 | **Vehicle insurance is mandatory for all vehicle onboarding (Mode A, Mode B, and Mode C)**: the owner uploads a valid **insurance certificate**, the app **AI-extracts the policy number, insurer, and expiry date** (admin review on low confidence), and registration cannot be approved without it. The platform stores the **insurance expiry** and flags/blocks vehicles whose insurance has expired until a renewed certificate is uploaded (reuses the US-2.11 document-renewal flow). *(Admin-registered trains, US-2.17, are exempt where insurance is carried at the operator/line level.)* | P0 |

> **Discussion item 2 (6/22) — onboarding split into driver-identity and Mode-C vehicle onboarding.** Driver registration is now two phases: a **mandatory driver-identity Profile Setup** that precedes the Home screen, and an **optional, in-app, Mode-C-only vehicle onboarding** that is auto-verified by Gemini Flash 3.0. **Mode A/B vehicle registration and permits are handled in the Fleet Portal, not the Driver App.** US-2.1/2.3/2.5 above describe the *documents required*; US-2.21–2.25 describe the *Driver-App flow*.

| US-2.21 | As a new driver, immediately after phone-OTP I complete a **Profile Setup** screen — **driver name**, a **mandatory profile picture** (shown to passengers), and my **driving license (front and back)** — before reaching the Home dashboard. **No vehicle is required to reach Home**; profile-setup data is captured once per driver (driving license is stored vehicle-less). *(Discussion item 2)* | P0 |
| US-2.22 | As a driver, I can **onboard my own Mode C (Standby) vehicle in-app** via a **4-step wizard** — **Step 1** vehicle type + Registration No, **Step 2** insurance, **Step 3** revenue license, **Step 4** front & back photos (number plate visible) — each document showing **Done** on upload. The wizard is launched from the **My Vehicles empty-state popup** (if I have no vehicles) or the **nav-drawer → Vehicle Onboarding**. *(Discussion item 2)* | P0 |
| US-2.23 | As a driver, when I submit the 4 steps the documents are **analysed by Gemini Flash 3.0** and **auto-verified**: insurance is *Verified* if the **expiry date** is extracted; revenue license if its **number and expiry date** are extracted; front/back photos if the **plate matches my entered Reg No**; otherwise that document is *Pending*. **When all four are Verified the vehicle is auto-approved with no Verification Officer step** and appears in My Vehicles; any *Pending* document routes the vehicle to the Verification Officer queue. *(Discussion item 2)* | P0 |
| US-2.24 | The **vehicle registration document / permit and GPS-tracker fields are removed from the Driver-App vehicle onboarding** — permits are required for **Mode A** transport and are handled in the **Fleet Portal**; the Driver App onboards **Mode C** vehicles only. *(Discussion item 2)* | P0 |
| US-2.25 | As a driver, I **cannot switch Online until at least one vehicle is available to me** — an **owned Mode C** vehicle that is **APPROVED** in My Vehicles, or a **shared / temporarily-assigned Mode A or Mode B** vehicle. With no available vehicle the Go Online toggle is disabled with a prompt to onboard a Mode C vehicle or await an assignment. *(Discussion item 2)* | P0 |
| US-2.26 | Mode-C onboarding **saves each step on completion**: the next time the wizard is opened it **resumes at the next incomplete step** (not Step 1). A vehicle with **≥1 saved step** appears in **My Vehicles (SCR-DA-026 / SCR-DI-026)** as **Incomplete** (showing the next step, with a **Resume** action); a vehicle whose **all 4 steps are complete & Verified** appears as **Approved**. **Only an Approved Mode-C vehicle can be selected to go live.** *(2026-06-25 item 5)* | P0 |
| US-2.27 | Once the current vehicle's 4 steps are complete and it is **Approved**, the onboarding wizard is "finished": the next time **Vehicle Onboarding is opened (nav drawer) or ＋ is tapped in My Vehicles, it starts a fresh Step 1/4 for a NEW vehicle**. A vehicle that is still **Incomplete** instead resumes at its next incomplete step. *(2026-06-25 item 6)* | P0 |

---

### Epic 3: Hardware GPS Tracker Support

> **Promoted from Phase 2 to Phase 1.** Supports both **Mode C private ride-hailing vehicles** (individual driver cabs, three-wheelers, motorbikes) and **Mode A public passenger transport** (state-owned / private operator buses, fleet-managed school buses, intercity coaches). Where a behaviour differs between an individual driver and a fleet operator, the difference is called out explicitly in the story.

#### 3.A. Functional Requirements — Device Onboarding & Binding

| ID | Story | Priority |
|---|---|---|
| US-3.1 | As an **individual vehicle owner**, I can bind an external GPS tracker (ST-901, ST-902, GT06, TK103, JT/T 808-class) to my already-registered vehicle by entering the **IMEI number** in the Driver App, scanning the QR code on the device, or accepting an admin-issued bind code | P0 |
| US-3.2 | As a **fleet operator** (state-owned bus authority, private bus company, school transport operator), I can bulk-register up to **5,000 trackers per CSV upload** in the Admin Portal, each row pairing an IMEI to a vehicle registration number; the system validates each row and produces a downloadable error report for rejected lines | P0 |
| US-3.3 | The TCP/UDP ingest adapter validates an incoming device by IMEI, resolves it to a `vehicleId` (Redis cache → Postgres fallback), and rejects any unprovisioned IMEI with a logged security event | P0 |
| US-3.4 | The system detects and rejects **duplicate IMEI registrations** (anti-cloning); the second binding attempt is queued for admin review and both devices are temporarily quarantined until resolved | P0 |
| US-3.5 | Every tracker is issued a **per-device credential** (X.509 client certificate for MQTT-capable trackers, or a per-device API token / shared secret bound to IMEI for legacy GT06 / JT808 trackers) at provisioning; credentials are short-lived and rotated by the platform on a scheduled interval | P0 |
| US-3.6 | As an individual owner, I can switch the tracking source between **mobile app** and **hardware tracker** at any time; only one source publishes position for a given vehicle at a time, and the system enforces this as a single active publisher rule | P0 |
| US-3.7 | As a fleet operator, I can assign one or more trackers to a vehicle (primary + redundant), and the system uses the primary unless it goes offline for more than 60 seconds, at which point the redundant tracker is promoted | P1 |
| US-3.8 | As an admin, I can **decommission** a tracker (lost, stolen, faulty), which revokes its credentials within 60 seconds and prevents any further ingest from that IMEI | P0 |

#### 3.B. Functional Requirements — Real-Time Ingest, Replay & Health

| ID | Story | Priority |
|---|---|---|
| US-3.9 | The platform accepts position pings from hardware trackers over **TCP** (binary GT06 / JT/T 808 protocol family), **UDP** (low-overhead variants), and **MQTT** (modern trackers); the adapter normalises every payload into a single internal position event before publishing to the event stream | P0 |
| US-3.10 | When a tracker has been offline (out of GSM coverage), it batches GPS samples locally and **bulk-replays** them on reconnect; the platform accepts the backlog on a separate ingest channel that is rate-limited and lower priority than live samples | P0 |
| US-3.11 | The platform deduplicates and orders the replayed backlog using the device's monotonic sample sequence number, discarding any sample whose `seq` has already been persisted | P0 |
| US-3.12 | As an admin, I can view per-tracker **last-seen, signal strength, satellite count, and battery voltage** (when reported by the protocol) from the Admin Portal | P1 |
| US-3.13 | As a fleet operator, I can view a **fleet health dashboard** showing the count and percentage of trackers in states `Online / Stale (no ping > 5 min) / Offline (no ping > 30 min) / Decommissioned` across my fleet | P1 |
| US-3.14 | As an individual owner, I receive a push notification when my tracker has been offline for more than **15 minutes** during a session that was supposed to be active | P1 |
| US-3.15 | The platform applies the same plausibility checks (per-vehicle-type max speed, max jump distance, GPS accuracy threshold) to hardware-tracker positions as to mobile positions; failed samples are dropped and counted toward a per-device fraud-score | P0 |
| US-3.16 | As a fleet operator, I can subscribe (via the Admin Portal) to **device-down alerts** delivered by email/SMS when N % of my fleet goes offline within a 5-minute window (early-warning for SIM-provider outage or back-office outage) | P2 |

#### 3.C. Functional Requirements — Commands, Configuration & Lifecycle

| ID | Story | Priority |
|---|---|---|
| US-3.17 | The platform can send **downlink commands** to MQTT-capable trackers (request immediate position, change publish cadence, reboot, set geofence) on a per-device command topic; non-MQTT trackers receive the equivalent via the protocol's native command frame through the TCP adapter | P1 |
| US-3.18 | Fleet operators can configure **per-vehicle publish cadence profiles** (e.g., "school-bus active hours = 10 s, off-hours = 60 s") and apply them in bulk; individual drivers use the default profile for their vehicle type | P1 |
| US-3.19 | The platform retains the **last-known position** of every tracker (online or offline) for use in trip auto-end, dispatch eligibility, and admin search | P0 |
| US-3.20 | When a tracker's binding is removed or the vehicle is deactivated, the tracker's credentials are revoked, its data continues to be retained per retention policy, and any in-flight session is force-ended | P0 |

#### 3.D. Functional Requirements — Mode-Specific Behaviour

| ID | Story | Priority |
|---|---|---|
| US-3.21 | **Mode C (private ride-hailing).** A hardware-tracker-equipped Mode C vehicle is dispatch-eligible only when its tracker has been online within the last 30 seconds **and** the driver app is logged in (driver-presence is still required for accept/cancel UX); position for dispatch decisions is taken from the tracker, not the phone. **Tracker GPS for a Mode C vehicle is ingested only while the vehicle is Online** (the driver has gone online in the app) — pings sent while offline are not accepted onto the live map or dispatch | P0 |
| US-3.22 | **Mode A (public passenger transport).** A hardware-tracker-equipped bus does not require a driver app session for its position to be published on the passenger live map; the tracker is the authoritative source of position. For tracker-installed Mode A vehicles, the **journey starts and ends automatically on vehicle ignition** (ACC on → session start; ACC off / idle timeout → session end) — **the mobile app is not needed**. The driver app, if used, is only for optional session metadata | P0 |
| US-3.23 | **Mode B (private transport / school buses / book-hires).** Sharing-grant entitlement (Epic 4) applies identically to tracker-sourced positions; passengers with valid grants see the bus on the map regardless of whether the source is mobile or hardware. For tracker-installed Mode B vehicles, the **journey starts and ends automatically on vehicle ignition** (ACC on/off) — **the mobile app is not needed** | P0 |
| US-3.24 | **Fleet aggregation.** Fleet operators see all their hardware-tracker vehicles on a single **Fleet Portal** map (Epic 13); positions are scoped to the operator's organisation by row-level security | P1 (Phase 1 with Epic 13) |

#### 3.E. Functional Requirements — Security & Anti-Spoofing

| ID | Story | Priority |
|---|---|---|
| US-3.25 | All hardware-tracker traffic is encrypted in transit (TLS 1.2+ for MQTT, DTLS or per-payload signature for TCP/UDP where the protocol allows; otherwise the device is restricted to a network-segregated ingest VLAN and PII-free payloads) | P0 |
| US-3.26 | The platform records every credential-validation failure, rate-limit breach, and IMEI-mismatch in an immutable audit log, surfaced in the Admin Portal | P0 |
| US-3.27 | If a tracker exceeds its configured publish rate or fails plausibility checks repeatedly within a window, it is auto-quarantined (no further ingest accepted) and an admin alert is raised | P0 |

---

### Epic 4: Vehicle Sharing & Access Control (Mode B — Private Transport)

| ID | Story | Priority |
|---|---|---|
| US-4.1 | As a public transport driver/owner, I can share tracking access for my vehicle to any driver app user by entering their User ID | P0 |
| US-4.2 | When sharing a public transport vehicle, I can set an expiry date/time for the share | P0 |
| US-4.3 | As a public vehicle driver, I can cancel any active sharing grant at any time | P0 |
| US-4.3b | As a sharee, I need to accept the incoming sharing request to allow sharing the vehicle GPS data | P0 |
| US-4.4 | As a private transporter, I can see incoming access requests — each showing the **requesting passenger's name and mobile number** (plus their PAX ID) so I can identify the requester — and accept or reject them | P0 |
| US-4.5 | As a passenger, I can request access from a private transporter by entering their Vehicle ID | P0 |
| US-4.6 | Once a private transporter accepts my request, I see their vehicle on the map within my subscribed geocells | P0 |
| US-4.7 | As a vehicle owner, I can see a list of all users who currently have access to track my vehicle, **each shown with their name and mobile number** (plus PAX ID). *(Private vehicle subscribers — first month is free.)* | P1 |
| US-4.8 | For public buses, the system automatically revokes expired sharing grants | P0 |
| US-4.9 | As a passenger, tapping a **Mode B (private) vehicle marker** on the live map opens the **Mode B access-request screen** (SCR-PA-024) with the Vehicle ID pre-filled — Mode B markers do **not** open the A-style vehicle popup. *(Discussion item 8)* | P0 |
| US-4.10 | As a driver/owner, **sharing & incoming subscription requests are scoped per vehicle**. A driver may have **more than one vehicle** registered, or be **temporarily hired with a Mode A/Mode B vehicle assigned**; in every case incoming private Mode B subscription requests appear **under the particular vehicle** they target (driver app SCR-DA-028, fleet portal SCR-FP-011). *(Discussion item 12 & 15)* | P0 |
| US-4.11 | As a passenger, I can **unsubscribe** from a Mode B vehicle from "My subscriptions" (compact unsubscribe icon button). Once unsubscribed I can **no longer see the vehicle**; to subscribe again I must send a new request that the driver/owner accepts. *(Discussion item 17)* | P0 |
| US-4.12 | When a passenger unsubscribes, the subscriber row remains visible but **muted** in the fleet portal **until the fleet owner deletes it**; the owner deleting the row removes it permanently. *(Discussion item 17)* | P0 |

> **Private Transport (Mode B):** Private vehicle sharing (book hires, school buses, etc.) does not incur daily charges. A monthly platform charge of approximately **Rs 300** may apply (first month free). Separately, the **passenger-facing Mode B fare is set by the fleet/owner** — each Mode B vehicle is classified **Paid** or **Free** (office transports may be Free); Paid vehicles collect a monthly fare per subscriber. See **Epic 23 — Mode B Subscription Payments**.

---

### Epic 5: Driver Session Management — Bus (Mode A)

| ID | Story | Priority |
|---|---|---|
| US-5.1 | As a bus driver, I can start a journey session by tapping "Start Journey" in the app | P0 |
| US-5.2 | As a bus driver, I can end the journey session by tapping "End Journey" | P0 |
| US-5.3 | If the driver forgets to end the session, the system auto-ends it after **30 minutes of idle** (no movement detected) | P0 |
| US-5.4 | As a bus driver, I can enable **auto-end at destination** — the session ends automatically when the vehicle enters a 100m radius of the previous journey's end position | P0 |
| US-5.5 | During an active **Mode A (bus)** session, the app publishes GPS position via MQTT at adaptive rates: **Moving** — 1 call every 4 seconds; **Stationary (GPS idle)** — 1 call every 10 seconds. *(Mode C standby/idle and near-pickup/near-drop cadences are defined in US-6A.24.)* | P0 |
| US-5.6 | As a bus driver, I can see my current session duration and distance traveled in real-time | P1 |
| US-5.9 | As a bus driver, if the system auto-ends my session due to idle, I receive a push notification informing me with the reason | P0 |
| US-5.10 | As a bus driver, I can restart a session that was auto-ended within a 5-minute grace period. No daily fee charge is involved for bus drivers. | P1 |
| US-5.11 | When my **active vehicle is a public-transport bus (Mode A) or a private-transport vehicle (Mode B)**, the **Start/End-Journey screen is my home dashboard** (SCR-DA-011 / SCR-DI-011) — instead of the Mode C standby map — carrying only **Start Journey** and **End Journey** actions, with the **vehicle type & number shown below the route card**. *(2026-06-25 item 8)* | P0 |
| US-5.12 | If a **paired GPS device began ingesting on vehicle ignition**, the journey is **already started** and the dashboard shows it **started** (offering End Journey); when **ignition is OFF** the device stops publishing and the green **Start Journey** button returns. The **dashboard can override the device** — the driver may manually Start or End the journey from the dashboard regardless of the device state (ties to US-3.6 single active publisher, US-3.22/3.23 ignition auto-sessions). *(2026-06-25 items 8, 11)* | P0 |

---

### Epic 6: Driver Session Management — Ad-hoc

> 🟡 **Entire epic deferred to Phase 2.**

---

### Epic 6A: Driver Dispatch & Scheduling (Mode C: Standby On-Demand)

| ID | Story | Priority |
|---|---|---|
| US-6A.1 | As a standby driver, I can toggle my status to "Online" to receive point-to-point ride requests | P0 |
| US-6A.2 | When a passenger requests a ride, the system sends a private request to the nearest available driver based on **distance**, **Driver Rating/Level**, and **vehicle category**. The driver is shown: Pickup Point (address or pin on map), Distance to Pickup (e.g., "1.2 km away"), Vehicle Category (canonical type, e.g., "Three-wheeler", "Sedan" — §1.B), and **Payment Method (Cash / LankaQR / Card)** — the **actual** method the passenger selected is shown ("Card" = OnePay). If the driver does not accept within **15 seconds**, the request is automatically sent to the next eligible driver, and so on. | P0 |
| US-6A.3 | As a standby driver, I have 15 seconds to accept or reject an incoming dispatch | P0 |
| US-6A.4 | As a passenger, I can **schedule a Mode C (Standby On-Demand) ride in advance** (e.g., for an airport transfer tomorrow) | P0 |
| US-6A.5 | As a standby driver, I can view **all future scheduled rides** on a **Job Board** visible to all drivers within a 30 km radius and **post my intent** for any of them. The Job Board is for **posting intent only — there is no direct accept on the board**. When a job goes live **30 minutes prior to start**, the system dispatches it to the closest intent-poster (by Driver Level) as a normal ride **offer on the dispatch screen (SCR-DA-014)**, where the driver accepts or rejects. If two intent-posters are in nearby (≤8) cells, the higher-level driver is rung first. | P0 |
| US-6A.6 | **Driver Level System**: Every standby driver starts at **Level 3**. If a driver accepts a scheduled job 30 minutes prior and does not appear, their level drops by 1. Level-up formula: every 100 five-star ratings = 500 points = 1 level up (e.g., 50×5-star + 65×4-star = 510 points = 1 level up). Ratings of 2 stars or below are not counted. **3 passenger reports** will drop a driver's level by 1 and trigger a **temporary delisting**. | P0 |
| US-6A.7 | If a driver accepts a scheduled ride but fails to appear, their Driver Level drops by 1. | P0 |
| US-6A.8 | If a driver's level drops to **Level 1**, that driver is **no longer able to accept scheduled rides from the Job Board**. *(Not a permanent ban — the driver can still operate in other modes but loses scheduled ride privileges.)* | P0 |
| US-6A.9 | As a passenger, I can cancel a Mode C ride request **before a driver accepts it, without any penalty**. **If I cancel after the driver accepts**, my passenger app **outstanding balance is debited Rs 50** per cancellation. This **Rs 50 is added to the fare of my next ride**; once that next ride is completed and paid, the **driver who served that next ride has Rs 50 deducted from their wallet, which is credited to the originally-affected driver** (the one whose accepted ride I cancelled). | P0 |
| US-6A.10 | As a passenger, I can cancel a Mode C ride after a driver has accepted, with a clear cancellation policy shown — a **post-acceptance** cancellation incurs Rs 50, added to my next ride (US-6A.9). | P0 |
| US-6A.10b | **Three continuous post-acceptance cancellations disable booking.** Only **cancellations made after a driver has accepted** count; pre-acceptance cancellations never count (US-6A.9). The counter is **consecutive** — it **resets to zero on any successfully completed ride**. On the 3rd consecutive post-acceptance cancellation, the passenger's **vehicle booking is disabled** in the app. **Re-enable path:** the passenger must **clear any outstanding Rs 50 balance**; access is restored after a configurable cooldown or once an admin/CSR reinstates it. | P0 |
| US-6A.11 | If no driver accepts a Mode C dispatch within a defined timeout (e.g., 2 minutes), the passenger is notified and the request is automatically cancelled | P0 |
| US-6A.12 | As a passenger, when a Mode C driver has accepted my request, I can see the driver's live location on the map as they travel to my pickup point | P0 |
| US-6A.13 | As a passenger, I receive a push notification when a driver accepts my Mode C ride, including driver name, photo, vehicle type and ETA | P0 |
| US-6A.14 | As a standby driver (Mode C), I can view my acceptance rate and no-show history from my profile to track my Driver Level standing | P1 |
| US-6A.15 | As a standby driver (Mode C), I receive a push notification 30 minutes before a scheduled ride so I can prepare on time | P0 |
| US-6A.16 | As a passenger, I can contact my assigned Mode C driver via an **in-app VoIP call** (like PickMe) — both driver and passenger call over the internet through the app. **No personal phone numbers are exposed** to either party. *(Promoted to P0 — US-8.20, US-8.22, US-11.9. ⚠ Masking clause superseded by US-26.2: VoIP remains the Free-call option, but masking is no longer a requirement.)* | P0 |
| US-6A.17 | As a standby driver (Mode C), I can set a **Directional Travel** (Destination Filter) by choosing a destination (search an address, drop a map pin, or pick "Home") while online. When set, I only receive dispatch offers for hires whose direction of travel aligns with my chosen direction — i.e., the trip moves me **towards** my destination, the pickup is roughly **en route** (within an admin-configurable detour limit, default 2 km), and the drop-off leaves me **closer** to my destination than the pickup. This mirrors the **PickMe driver "Directional Travel" feature**. | P0 |
| US-6A.18 | As a standby driver, I can set Directional Travel only a **limited number of times per day** (admin-configurable, default **2 per day**), and each activation has a **maximum duration** (admin-configurable, default **2 hours**), after which it auto-clears and I return to receiving all eligible hires. I can see my remaining uses for the day. | P0 |
| US-6A.19 | As a standby driver, I can **cancel / turn off Directional Travel** at any time before it expires; turning it off mid-period **still consumes one of my daily uses** (to prevent gaming the limit). Going fully Offline also clears any active Directional Travel. | P0 |
| US-6A.20 | As a standby driver, once I **accept a hire** matched under Directional Travel, the filter remains active for the rest of its period so I can chain further hires in the same direction; it does not auto-clear on a single matched trip (it clears only on expiry, manual cancel, or going offline). *(Admin-configurable: "clear on first matched trip" vs "keep until expiry".)* | P1 |
| US-6A.21 | As a standby driver, while Directional Travel is active, my Driver App clearly shows a **persistent indicator** — my chosen destination, a directional/heading marker on the map, time remaining, and remaining daily uses — so I always know the filter is limiting my incoming hires. | P0 |
| US-6A.22 | As a standby driver, Directional Travel applies to **both passenger rides and package deliveries** (Mode C) and is evaluated **in addition to** the normal nearest-driver, Driver Level, vehicle-category, and wallet-balance dispatch rules — it never overrides safety/eligibility gates; it only narrows which eligible hires reach me. | P0 |
| US-6A.23 | As a standby driver, if **no directional hires** arrive during my active period, I am not penalised in any way (no Driver Level impact, no acceptance-rate impact) — Directional Travel is purely a convenience filter. | P1 |
| US-6A.24 | **Mode C GPS publish cadence.** A Mode C driver app publishes GPS via MQTT at adaptive rates: **Idle standby (online, no active hire)** — 1 call every 60 seconds; **En route to pickup / active trip — moving** — 1 call every 4 seconds; **stationary** — 1 call every 10 seconds; **Near-pickup / near-drop burst** — **1 call per second** while the vehicle is within an admin-configurable radius (default **150 m**) of the pickup or drop-off point (or in the final-approach window), to give the passenger precise live positioning. The 1 s burst auto-ends when the vehicle leaves the radius / completes the stop. This is the cadence the broker rate-limit in US-12.4 accounts for. | P0 |

> **Directional Travel (Destination Filter):** A Mode C convenience that lets a standby driver receive only hires heading in a chosen direction, for a limited time and a limited number of times per day. It **filters** which eligible ride offers reach the driver; it does **not** change fares, the zero-commission model, the daily platform fee, or any dispatch eligibility/safety rule. Direction match is computed server-side from the bearing of the driver→destination vector versus the ride's pickup→drop-off vector, an en-route pickup-detour limit, and a "must make progress towards destination" constraint — all admin-configurable.

---

### Epic 7: Live Map & Passenger Experience

| ID | Story | Priority |
|---|---|---|
| US-7.1 | As a passenger, when I open the app I see all public vehicles (**buses and trains**) and my privately shared vehicles (Mode B) within my nearby geocells on the map | P0 |
| US-7.2 | Each vehicle type is shown in a **different color** on the map (**trains use a distinct colour and rail icon**) | P0 |
| US-7.3 | Vehicle markers update position in real-time with **2–8 seconds latency** and smooth animation | P0 |
| US-7.4 | As a passenger, I can tap a vehicle marker to see a popup with vehicle details. **This info popup is only available for private shared vehicles (Mode B) and public transport (Mode A — buses & trains).** Standby on-demand vehicles do not show info when tapped. | P0 |
| US-7.6 | The map automatically re-centers on my location and updates the nearby vehicle list as I move | P1 |
| US-7.7 | As a passenger, I can **filter** the map by vehicle type (show only buses, **only trains**, only three-wheelers, etc.) | P1 |
| US-7.8 | As a passenger, I can see the estimated **direction of travel** (heading arrow) on each vehicle | P1 |
| US-7.9 | As a passenger, I can **search for a bus route number** and see all active buses on that route | P1 |
| US-7.11 | As a passenger, I can see the estimated time of arrival (ETA) of a vehicle. **This is valid only for the accepted vehicle.** However, buses (Mode A) can also display ETA when selected on the map. | P1 |
| US-7.12 | As a passenger, I can view the driver's name, profile photo, and vehicle registration number **after the driver has accepted the ride/tour**. | P0 |
| US-7.13 | As a passenger, I can set **"Home" and "Work" location shortcuts by selecting the location on the OSM map** (drop/drag a pin, with reverse-geocoded address shown) to quickly center the map or set as pickup/drop-off. Managed in **Settings** (see Epic 22). | P1 |
| US-7.14 | As a passenger, I receive an in-app message when no vehicles of my selected type are active in my area, instead of seeing an empty map with no context | P1 |
| US-7.15 | As a passenger, when I enter a destination I want to travel to, the app presents **trains alongside other transport options** (buses and on-demand vehicles) that serve that route | P1 |
| US-7.16 | As a passenger, **Mode C standby vehicles that are currently engaged on a hire** (motorbike, three-wheeler, Flex, Sedan, Mini Van, Van on an active trip) are **not shown on the public map** — only the booking passenger sees the assigned vehicle | P0 |
| US-7.17 | Any vehicle that is **not ingesting live locations** (GPS turned off or app gone offline / stale beyond the freshness window) is **removed from the map** until it resumes sending live positions | P0 |
| US-7.18 | The **driver home-screen map (SCR-DA-010 / SCR-DI-010) shows only the driver's OWN active vehicle** — other drivers' active vehicles are **never shown on the driver home map**. The **top-left hamburger menu button is removed** from the driver dashboard; navigation is via the bottom-nav **Menu** tab (SCR-DA-036 / SCR-DI-036). *(2026-06-25 item 7)* | P0 |

---

### Epic 8: Passenger Trip, Booking & Fare Calculation

| ID | Story | Priority |
|---|---|---|
| US-8.2 | As a passenger booking a Mode C (Standby On-Demand) vehicle, I enter pickup and drop-off to dispatch a driver | P0 |
| US-8.2a | **Destination is a geo-location only.** In location search the passenger **cannot type a route number** — they always enter a **place / address** (geocoded). The system then computes and shows the **relevant public buses + private options** to reach that destination on the booking screen. *(Discussion item 4)* | P0 |
| US-8.2b | On the booking/results screen, **Mode A public-transport options are derived from the GTFS feed** and show the **route number, route description (origin → destination + corridor), a Direct / Transit indicator, and a PUBLIC label**. The system lists **all direct options from GTFS** for the destination (transit options shown below direct). *(Discussion item 3)* | P0 |
| US-8.2c | On the booking/results screen, **Mode C private options cannot display "minutes away" or "distance"** (no driver matched yet) — they show the **upfront price only**. *(Discussion item 3)* | P0 |
| US-8.2d | **Paste-link location input.** Wherever a location is captured (proxy pickup, package pickup & drop-off), the passenger can choose **"Paste link"**: paste a **Google Maps link** (e.g., copied from WhatsApp); the app **parses the lat/lng from the URL and drops the pin automatically**. *(Discussion items 5 & 6)* | P0 |
| US-8.4 | The system calculates Mode C (Standby On-Demand) fare using a complex tariff: 1st KM charge + per-km rate + peak/night surcharges. The **pre-booking estimate shows a single total** (tariff components are **not** itemised); the **post-trip summary may show a breakdown**, including any payment-method surcharge (e.g., OnePay +5%). | P0 |
| US-8.6 | As a passenger, I can rate the trip (1–5 stars) after the trip ends | P2 |
| US-8.7 | As a passenger, I can view all my past trips with date, route, distance, and fare | P1 |
| US-8.8 | As a driver, I can see the fare calculated for each passenger on trip completion | P1 |
| US-8.9 | As a passenger, before booking/boarding, I can see an **upfront fare estimate** | P0 |
| US-8.10 | As a passenger, I can pay the fare in-app via **LankaQR** (no surcharge) or **OnePay** (+5% surcharge). Cash is default. | P0 |
| US-8.10a | When a passenger selects **LankaQR**, the app shows a **"Pay" button backed by a LankaQR deep link** (not a scannable QR code). Tapping **Pay** opens the passenger's installed LankaQR-compatible banking/wallet app directly with the amount and merchant reference pre-filled, so the passenger only confirms in their bank app. If no compatible app is installed (deep link cannot resolve), the app falls back to displaying the scannable QR code. | P0 |
| US-8.10b | On the **Pay fare** screen the passenger can **scan the driver's QR code** (printed, displayed on the driver's device, or a window sticker) to complete payment. The app **no longer renders a MageRide QR in the centre of the pay screen** — instead it offers a **"Scan driver's QR"** camera option (alongside the bank-app deep link). *(Discussion item 18)* | P0 |
| US-8.11 | When a passenger pays via **OnePay**, the displayed fare includes a 5% processing surcharge | P0 |
| US-8.12 | As a standby driver, I receive a **payment confirmation notification** when a passenger pays in-app | P1 |
| US-8.15 | As a passenger, if a LankaQR or OnePay payment fails mid-trip, I am notified immediately and can retry or switch to cash without losing trip history | P0 |
| US-8.16 | As a passenger, I can book a Mode C ride **for someone else** (proxy booking) by selecting "For Someone Else", then entering their phone number and name (or picking from contacts) | P1 |
| US-8.17 | When booking for someone else, I can set the pickup location using three methods: **(1)** type and search for a location name/address, **(2)** select a point directly on the map, or **(3)** tap "Request Location" to send an **in-app FCM push notification** to the rider's MageRide Passenger App requesting their live GPS position | P1 |
| US-8.18 | When a location request is sent (method 3), the rider sees a prompt in their MageRide app: *"[Booker name] wants your pickup location for a ride"* with **Share Location** and **Decline** buttons. If the rider taps Share, their GPS is captured, they can adjust the pin on a map, and tap Confirm. The confirmed location is sent back to the booker's app in real-time and auto-populated as the pickup point | P1 |
| US-8.19 | The FCM location request expires after **5 minutes** (configurable). If the rider does not respond, declines, or is not a registered MageRide user, the booker is notified and can fall back to manual location entry (method 1 or 2) | P1 |
| US-8.20 | When booking for someone else, the driver is shown the **actual rider's name** and informed this is a third-party booking. The driver can contact the rider via in-app VoIP (no phone numbers exposed) | P1 |
| US-8.21 | Payment for a proxy booking is charged to the **booker's** selected payment method (LankaQR or OnePay). If the booker selects "Cash", the actual rider pays the driver directly and is notified of this responsibility via FCM push | P1 |
| US-8.22 | When a **proxy (booked-for-someone-else) ride is accepted by a driver**, the platform generates an **SMS to the actual rider's phone** containing a **secure web link to the Passenger Web Portal (`passenger.mageride.lk`)**. Opening the link lets the rider complete the **rest of the ride journey on the web** — live driver-to-pickup tracking, ETA, driver name/photo/vehicle & registration, trip progress, contact driver *(now tap-to-call `tel:` link — superseded by US-26.3)*, and SOS — **without needing the MageRide app installed**. The link is **ride-scoped, tokenised, and expires** after the trip completes (or after a configurable TTL) | P1 |

### Fare Tariff Structure (Configurable by Admin)

**Mode C (Standby On-Demand - Premium Rates)** — uses the canonical vehicle types (§1.B)
| Vehicle Type | 1st KM Charge (Rs) | Per-km Rate (Rs) | Peak Hour Surcharge | Night Surcharge |
|---|---|---|---|---|
| Motorbike | 80 | 60 | +20% | +15% |
| Three-wheeler | 100 | 80 | +20% | +15% |
| Flex | 130 | 90 | +20% | +15% |
| Sedan | 150 | 100 | +20% | +15% |
| Mini Van | 150 | 110 | +20% | +15% |
| Van | 150 | 120 | +20% | +15% |

> **Note:** Peak Hours (e.g., 7-9 AM, 5-7 PM) and Night Time (e.g., 10 PM - 5 AM) times and multiplier rates are configurable by the Admin. **Truck / Mini Truck** (package delivery, Epic 20) use admin-configured delivery rates of the same structure. All values are admin-configurable.

### Passenger Ride Payment Methods

| Method | Gateway | Surcharge | Notes |
|---|---|---|---|
| **Cash** | — | None | Default payment method; driver collects directly |
| **LankaQR** | Direct bank transfer | **None** | App shows a **"Pay" button (LankaQR deep link)** — not a QR code; tapping it opens the passenger's LankaQR-compatible banking app with amount + merchant reference pre-filled. Scannable QR shown only as a fallback when no compatible app is installed |
| **OnePay** | OnePay payment link | **+5% on fare** | Visa, Mastercard, AMEX, mobile wallets; surcharge covers processing fees |

> **Example:** For a Rs 500 fare — Cash = Rs 500, LankaQR = Rs 500, OnePay = Rs 525 (Rs 500 + Rs 25 surcharge).

> **UI label standard (C-11):** The three methods are surfaced consistently in both passenger and driver UIs as **Cash / LankaQR / Card**, where **"Card" = OnePay** (the gateway). The driver's incoming-request card always shows the **actual** method the passenger chose.

> **Zero-Commission Model:** MageRide takes **zero commission** on ride fares. Passengers pay drivers directly. The fare displayed is exactly what the driver receives. MageRide's revenue comes from the daily platform fee paid by drivers (see Epic 9). For OnePay payments, the payment gateway settles directly to the driver's registered merchant account; MageRide never holds ride fares.

---

### Epic 9: Daily Platform Fee & Billing

> **IMPORTANT — Namma Yatri-style Zero-Commission Model:** MageRide replicates the **Namma Yatri (India)** payment methodology in the Sri Lankan context. Drivers keep **100% of all passenger fares**. Mode C (Standby On-Demand) drivers pay a flat **daily platform fee** (vehicle-type dependent) — the **first trip of the day is always free**, and the fee is auto-deducted from their wallet before the 2nd trip. Mode A (Public Transport) buses pay **no fee**. Mode B (Private Transport) vehicles pay a **monthly charge of ~Rs 300**. **There is no per-trip fee, no commission.** If a driver does not go online on a given day, **no fee is charged**. Drivers top up their wallet **entirely in the Driver App** (credit/debit card, OnePay, or LankaQR) — drivers never access the web portal. **Passengers pay nothing to MageRide** — the Passenger App is fully free with no subscriptions or premium tiers.

| ID | Story | Priority |
|---|---|---|
| US-9.1 | As a driver, the **first trip of the day is always free** — no wallet deduction. To accept a **2nd trip request**, the driver must maintain sufficient wallet balance. This entire daily-fee flow is **managed within the MageRide Driver App (Android & iOS)**. If wallet balance is insufficient, an in-app notification informs the driver that the passenger request was missed due to insufficient balance. If sufficient, the fee is auto-deducted. When accepting the 1st trip, if wallet balance is insufficient for the 2nd trip, a warning notification is given. **Daily fee rates by vehicle type**: Public Bus = Free, Motorbike = Rs 50, Three-wheeler = Rs 100, Flex = Rs 150, Sedan = Rs 200, Mini Van = Rs 250, Van = Rs 300. | P0 |
| US-9.4 | The daily fee is a **single flat charge per day** regardless of how many trips I complete — there is no per-trip fee, no commission | P0 |
| US-9.6 | The daily fee rate is determined by registered vehicle type. If a driver has registered multiple vehicles, **only one vehicle can go live at a time**. The driver pays the daily payment per vehicle. The driver can accept trips from one vehicle at a time and is not charged additionally for the same vehicle on the same day. | P0 |
| US-9.7 | As a driver, I can view my **wallet balance**, **today's platform fee status** (paid/unpaid, amount deducted today), **vehicle-specific daily rate**, and the **registration number of the vehicle currently live/online** (the single active vehicle selected in vehicle management, US-9.6) in the MageRide Driver App dashboard | P0 |
| US-9.9 | The system sends a **low-balance push notification** when wallet balance **≤ a threshold** (platform default Rs 200). The **driver can set their own preferred low-balance alert threshold** in the Driver App; the alert fires when balance ≤ the configured value | P0 |
| US-9.10 | As a driver, I can request a **credit transfer** by entering the **Driver ID** of another driver who holds wallet credit, directly in the MageRide Driver App — **QR scanning is not required (removed)** and there are **no special "reseller" codes** (SCR-DA-023 / SCR-DI-023). *(2026-06-25 item 10)* | P0 |
| US-9.11 | As a driver, I receive a **push notification** in the Driver App when another driver requests a credit transfer from me, showing the requesting driver's name, vehicle, and requested amount | P0 |
| US-9.12 | As a driver, I can **approve or reject** another driver's credit-transfer request **entirely within the MageRide Driver App (Android & iOS)** or via push-notification action — no web portal is involved | P0 |
| US-9.13 | When a driver approves a credit transfer, the system debits the **exact transferred amount** from the approver's wallet and credits the **same exact amount** to the requesting driver's wallet — **no commission is deducted on the transfer** (the reselling driver's margin, if any, came from the bulk-voucher purchase discount) | P0 |
| US-9.14 | As a driver, I can view my **wallet balance** and a **transaction history** of all credit transfers (sent and received) **in the MageRide Driver App** | P0 |
| US-9.17 | As a driver, I receive a **confirmation notification** once my wallet is topped up via a credit transfer from another driver | P0 |
| US-9.18 | As a driver, I can **top up my wallet directly in the app** using credit/debit card, OnePay, or LankaQR without leaving the app | P0 |
| US-9.19 | As a driver, I can **purchase a bulk credit voucher** in the app in denominations of **Rs 1,000 / Rs 2,000 / Rs 3,000 / Rs 5,000 / Rs 10,000**; each denomination carries a **purchase discount whose percentage is configured per tier in the database** (variable/admin-configurable; larger denominations typically earn a higher discount). The discount applies **only at purchase** — e.g., at a 10% rate the driver pays **Rs 900** and **Rs 1,000** is credited to the wallet | P0 |
| US-9.20 | A purchased bulk credit voucher **credits the driver's own wallet directly at purchase** (no separate redeem step or voucher code to enter). The driver can then **transfer credit (or part of their wallet balance) to another driver** by Driver ID | P0 |
| US-9.21 | When a driver transfers wallet balance/credit to another driver, **no commission or discount is applied** — the **exact transfer amount is debited from the sender and credited to the recipient**; both sides are recorded in the ledger | P0 |
| US-9.22 | As a driver, I can view a monthly summary of daily fees paid, total trips completed, and total fares received in the Driver App to understand my net platform cost | P1 |
| US-9.23 | As a driver, I can **raise a support ticket** from the Driver App to request a refund for a daily fee charged in error (e.g., app crash on Go Online) or report other issues | P1 |

> **Wallet System:** Drivers top up their wallet **directly in the app** (credit/debit card, OnePay, LankaQR) or by purchasing **bulk credit vouchers** in denominations of **Rs 1,000 / Rs 2,000 / Rs 3,000 / Rs 5,000 / Rs 10,000**, each carrying a **purchase discount configured per tier in the database** (variable/admin-configurable; larger denominations earn a higher discount). The discount applies **only at the time of purchase**, and the voucher **credits the buyer's own wallet immediately** (no redeem code). A driver can then **transfer credit to another driver** by Driver ID. **Driver-to-driver transfers carry no commission or discount** — the exact value is debited from the sender and credited to the recipient. "Reseller" is simply a driver who bought bulk credit cheaply and resells it at face value; the margin is the purchase discount, **not** a per-transfer commission.

### Daily Platform Fee Structure (Namma Yatri Methodology)

| Vehicle Type | Daily Fee | How It Works |
|---|---|---|
| **Public Transport Bus** (Mode A) | **Free** | No daily platform fee for public transport buses. |
| **Motorbike** (Mode C) | **Rs 50/day** | First trip free. Fee deducted before 2nd trip. Unlimited trips after. No charge on off days. |
| **Three-wheeler** (Mode C) | **Rs 100/day** | First trip free. Fee deducted before 2nd trip. Unlimited trips after. No charge on off days. |
| **Flex** (Mode C) | **Rs 150/day** | First trip free. Fee deducted before 2nd trip. Unlimited trips after. No charge on off days. |
| **Sedan** (Mode C) | **Rs 200/day** | First trip free. Fee deducted before 2nd trip. Unlimited trips after. No charge on off days. |
| **Mini Van** (Mode C) | **Rs 250/day** | First trip free. Fee deducted before 2nd trip. Unlimited trips after. No charge on off days. |
| **Van** (Mode C) | **Rs 300/day** | First trip free. Fee deducted before 2nd trip. Unlimited trips after. No charge on off days. |
| **Private Transport** (Mode B) | **~Rs 300/month** | Monthly subscription. No daily fee. First month free. |

> **Mode Key:** Mode A = Public Transport · Mode B = Private Transport · Mode C = Standby On-Demand

> **Methodology:** This is the **exact Namma Yatri model** adapted for Sri Lanka — pure daily subscription for Mode C, **no per-trip fees, no commission**. Rates differ by vehicle type to reflect each segment's earning capacity. Drivers pay only on days they actually work. All rates admin-configurable.

> **Passengers:** Always free. No subscriptions, no premium tiers, no booking fees from MageRide.

---

### Epic 9A: In-App Driver Wallet + Admin Portal Reconciliation

> **Rationale:** **Drivers never access the web portal.** Every driver-facing wallet function — login/identity, balance, top-up (card/OnePay/LankaQR + bulk credit vouchers), daily-fee status and history, payment confirmations, statements, and **driver-to-driver credit transfers** — is delivered **entirely within the MageRide Driver App (Android & iOS)**. Google Play permits in-app collection of driver platform fees via external payment methods (credit/debit card, LankaQR, OnePay) for off-platform/physical service fees, so no browser hand-off is required. The wallet/finance back-office (payment-gateway reconciliation, dispute resolution, reporting) lives in the **Admin Portal** (`admin.mageride.lk`), used by **internal roles** (Finance/Payments Officer, Admin, Super Admin, Auditor — see §2); it has **no driver-facing login or screens**. Wallet balance is used to pay daily platform fees automatically.

| ID | Story | Priority |
|---|---|---|
| US-9A.1 | The **Admin Portal** (`admin.mageride.lk`) is the **back-office web application** where all MageRide back-office functions run; its wallet/finance module is used **only by internal roles** (Finance/Payments Officer, Admin, Super Admin, Auditor) for payment-gateway reconciliation, dispute resolution, and reporting. Authentication is **Password or Google Sign-In**. **It has no driver-facing login or screens** — all driver wallet functions are in the Driver App | P0 |
| US-9A.2 | As a driver, I **authenticate only in the MageRide Driver App** (Phone OTP) and access **all** wallet functions there; **there is no driver login to the web portal** | P0 |
| US-9A.3 | As a driver, I can **view my wallet balance, daily fee history, and full transaction history entirely in the Driver App** (Android & iOS) | P0 |
| US-9A.4 | As a driver, I can **top up my wallet entirely in the Driver App** using **(1) Credit/Debit Card** (via OnePay — Visa, Mastercard, AMEX), **(2) OnePay wallet**, or **(3) LankaQR** (no surcharge). All three credit the wallet instantly in-app; no portal visit and no bank-transfer/receipt flow | P0 |
| US-9A.5 | As a driver, I can **view my vehicle-specific daily rate** and today's fee status **in the Driver App** | P0 |
| US-9A.6 | As a driver, I can **view daily fee deduction history** — date, vehicle, amount charged, trips taken that day — **in the Driver App** | P0 |
| US-9A.7 | If wallet balance is critically low (**below one day's fee** for the driver's vehicle type), the **Driver App** shows a prominent **"Top Up Required"** banner with the amount needed to resume service. *(The balance **can go negative** in certain circumstances — e.g., an admin wallet reversal/adjustment or a post-acceptance cancellation debit — in which case the banner shows the amount needed to return to a serviceable balance.)* | P0 |
| US-9A.8 | As a driver, I can access **credit-transfer functions** (request, approve, send) **within the MageRide Driver App (Android & iOS)** using my existing driver identity — there is **no separate "reseller" enablement, login, or web portal** | P0 |
| US-9A.9 | As a driver, I can **top up my wallet in the Driver App** using Credit/Debit Card, OnePay, or LankaQR — no portal access | P0 |
| US-9A.10 | As a driver, I can **view and manage pending credit-transfer requests** from other drivers — approve or reject each request — **entirely within the Driver App** | P0 |
| US-9A.11 | As a driver, I can **view my credit-transfer history** (transfers sent and received, with amounts and dates, date-range filters) **in the Driver App** — there is **no per-transfer commission** to report | P0 |
| US-9A.12 | As a driver, I can **initiate a credit transfer** proactively from the Driver App by entering another driver's **Driver ID** and transfer amount — the recipient's wallet is credited the **exact amount** immediately | P1 |
| US-9A.13 | The **Driver App** displays a **payment confirmation** after each successful wallet top-up, with a transaction reference number and receipt download/share option | P0 |
| US-9A.14 | The Admin Portal supports **multi-language** (Sinhala, Tamil, English) for internal users, matching the app's language support | P1 |
| US-9A.15 | As an admin, I can access an **admin panel** within the portal to view all wallet transactions, **configure the bulk-voucher purchase-discount tiers** (per-tier discount % and denominations), reconcile payment-gateway settlements, and resolve disputes | P1 |
| US-9A.17 | As a driver, after a successful in-app top-up I receive an **in-app confirmation** with a transaction reference number (and an optional email/SMS receipt) | P1 |
| US-9A.18 | As a driver, I can set a **minimum balance alert threshold** **in the Driver App** so I receive a notification before my wallet runs too low (e.g., to keep servicing other drivers' credit requests) | P1 |
| US-9A.19 | As a driver, I can download a PDF or CSV statement of my wallet transaction history for a selected date range **from the Driver App** | P1 |

### Wallet Top-Up Methods

| Method | Channel | Gateway | Surcharge | Notes |
|---|---|---|---|---|
| **Credit/Debit Card** | **In-app** (Driver App) | OnePay payment gateway | OnePay processing fee | Visa, Mastercard, AMEX; instant wallet credit on successful payment |
| **OnePay** | **In-app** | OnePay | OnePay processing fee | OnePay wallet / cards; instant wallet credit |
| **LankaQR** | **In-app** | Direct bank transfer via QR | **None** | Payment via any LankaQR-compatible banking app; instant or near-instant wallet credit |

### Bulk Credit Vouchers (In-App)

| Denomination | Indicative Purchase Discount | Notes |
|---|---|---|
| **Rs 1,000** | lowest tier | Smallest voucher; small or no discount |
| **Rs 2,000** | low tier | |
| **Rs 3,000** | mid tier | |
| **Rs 5,000** | high tier | |
| **Rs 10,000** | highest tier | Largest voucher earns the highest discount % |

> **Bulk Voucher Model:** Drivers buy bulk credit vouchers in-app at a **purchase discount configured per tier in the database** (variable/admin-configurable; larger denominations → higher discount %). The discount applies **only on purchase**, and the voucher **credits the buyer's own wallet immediately** (no redeem code). The buyer can later **transfer credit to another driver** by Driver ID. **All driver-to-driver transfers carry no commission or discount** — the exact amount is debited from the sender and credited to the recipient. A driver who buys bulk credit cheaply and resells it at face value keeps the **purchase discount** as margin; there is **no per-transfer commission**.

### Driver-to-Driver Credit Transfer Flow

| Method | Channel | Value Transferred | Notes |
|---|---|---|---|
| **Credit Transfer** | Both drivers act **in the MageRide Driver App (Android & iOS)** — the requester enters the holder's **Driver ID** (QR scanning removed, 2026-06-25 item 10) and requests; the credit-holding driver approves, or proactively initiates a send by Driver ID | **Exact value — no commission** | The exact transfer amount is debited from the sender's wallet and credited to the recipient's wallet; both sides recorded in the double-entry ledger |

> **Credit Transfer Example:** A driver requests a Rs 1,000 transfer from another driver (by Driver ID). The holding driver approves in the Driver App. Their wallet is reduced by **Rs 1,000** and the requesting driver's wallet is credited **Rs 1,000** — **no commission**. The holding driver's profit (if any) came earlier, from buying that credit at a bulk-voucher discount (e.g., paying Rs 900 for Rs 1,000 of credit).

> **Note:** **"Reseller" is not a separate role, account, or enabled capability** — it is simply any **driver who has purchased bulk credit** and transfers it to other drivers in the Driver App. Such a driver maintains the same prepaid wallet balance used for their own platform fees, topped up **entirely in the Driver App** (card/OnePay/LankaQR + bulk vouchers). The **bulk-voucher purchase-discount tiers** are configured by Admin in the database (per-tier %).

> **App ↔ Back-office Integration:** The MageRide Driver App handles **all** driver wallet functions **in-app** — top-ups (credit/debit card, OnePay, LankaQR + bulk vouchers), **driver-to-driver credit transfers** (request/approve/send by Driver ID), balance, daily-fee status, history, and statements. **Drivers never open the web portal.** The **Admin Portal** (`admin.mageride.lk`) is **back-office only**, used by internal roles for **payment-gateway reconciliation**, **bulk-voucher discount-tier configuration**, dispute resolution, and reporting.

---

### Epic 10: Notifications

| ID | Story | Priority |
|---|---|---|
| US-10.2 | As a private transporter, I receive a notification when a passenger requests access to track me, **showing the requesting passenger's name and mobile number** | P0 |
| US-10.3 | As a passenger with a sharing grant, I receive a notification when the shared vehicle starts a session | P1 |
| US-10.4 | As a driver, I receive a notification when my wallet balance is low (< Rs 200) | P0 |
| US-10.5 | As a driver, I receive a notification when the system auto-ends my session (idle/geofence) | P0 |
| US-10.7 | As a user, I can configure which notifications I want to receive | P1 |
| US-10.8 | As a driver, I receive a push notification when a scheduled Mode C ride is cancelled by the passenger, with sufficient notice to adjust plans | P0 |
| US-10.9 | As a passenger, I receive a push notification reminding me of an upcoming scheduled Mode C ride (1 hour before and 15 minutes before) | P0 |
| US-10.10 | When a proxy ride is accepted by a driver, the system notifies the actual rider with the driver's name, photo, vehicle type, registration number, and ETA — via **FCM push** if they are a registered app user, **and always via SMS containing a secure web link to the Passenger Web Portal (`passenger.mageride.lk`)** to continue tracking the ride (US-8.22) | P1 |
| US-10.11 | If the actual rider **does not have the MageRide app**, the **SMS web link to `passenger.mageride.lk`** lets them track and complete the ride in the browser (no app or login required); the booker is also informed that the rider was sent the web link | P1 |
| US-10.12 | When a package is picked up by the driver (pickup OTP verified), the recipient receives an **FCM push notification** with driver details, ETA, live tracking, and their **Delivery OTP** | P2 |
| US-10.13 | When a package is delivered (delivery OTP verified or photo proof taken), both sender and recipient receive an **FCM push notification** confirming delivery with timestamp and fare summary | P2 |
| US-10.14 | As a standby driver (Mode C), I receive a push/in-app notification when my **Directional Travel** period is about to expire (e.g., 10 minutes before) and when it has **auto-cleared**, so I know I have returned to receiving all eligible hires | P1 |
| US-NEW.1 | As a passenger, I can **unsubscribe from a private transport** (Mode B) service at any time from the Passenger App | P1 |

---

### Epic 11: Application Architecture Updates

| ID | Story | Priority |
|---|---|---|
| US-11.1 | The platform has **four surfaces**: MageRide Passenger App (Android & IOS), MageRide Driver App (Android & IOS), the **Admin Portal** (`admin.mageride.lk`, back-office web application for all internal roles), and the **Fleet Portal** (`fleet.mageride.lk`, responsive web application for fleet owners). `passenger.mageride.lk` is **not a separate surface** — it is a **no-login web subview of the Passenger App** (US-11.9), opened only via a tokenised SMS link when a driver accepts a proxy ride (US-8.22) | P0 |
| US-11.2 | The MageRide Passenger App contains mapping, dispatch, scheduling, and tracking features. **Fully free for all passengers.** | P0 |
| US-11.3 | The MageRide Driver App contains vehicle registration, session management, trip acceptance, **in-app wallet top-up** (card/OnePay/LankaQR + bulk credit vouchers), **wallet/fee status display**, and **driver-to-driver credit transfers** (request/approve/send credit by Driver ID, transfer history, statements, alerts) | P0 |
| US-11.4 | The **Admin Portal** (`admin.mageride.lk`) is the back-office web application performing **all MageRide back-office functions** (moderation, onboarding/verification, support, finance & payment-gateway reconciliation, configuration, RBAC, audit, reporting); routine driver top-ups and **all driver-to-driver credit-transfer functions happen in the Driver App** | P0 |
| US-11.5 | The **Driver App sign-in method is Phone OTP only**, and **all driver wallet functions live in the Driver App** — drivers never log in to the web portal. **All roles other than Driver, Passenger, and Fleet Owner log in to the Admin Portal** (`admin.mageride.lk`) via **Password or Google Sign-In** (§2) | P0 |
| US-11.6 | The Admin Portal is hosted on MageRide's domain at **`admin.mageride.lk`** and optimized for **responsive design**; it exposes **no driver-facing pages** and is accessible only to the six internal roles | P0 |
| US-11.7 | The **Fleet Portal** is hosted on MageRide's domain at **`fleet.mageride.lk`**, optimized for **responsive (mobile-first) design**, and authenticates fleet operators via **Email + Password, Google Sign-In, or Apple Sign-In** | P0 |
| US-11.8 | All four surfaces share the same backend platform and identity service; the Fleet Portal's email/Google/Apple identities, the Driver/Passenger Phone-OTP identities, and the Admin Portal's password/Google identities resolve to a single unified account model | P0 |
| US-11.9 | The **Passenger Web subview** (`passenger.mageride.lk`) is a **link-accessed, no-login web view of the Passenger App** (not a separate surface): a proxy-booking rider opens it via a **secure, ride-scoped, expiring token embedded in the SMS link** (US-8.22) — **no login or app install required**. It provides the rest-of-ride experience (live tracking, ETA, driver/vehicle details, driver contact *(tap-to-call — superseded by US-26.3)*, SOS, trip summary) for that single ride only | P1 |

---

### Epic 12: Safety & Trust

| ID | Story | Priority |
|---|---|---|
| US-12.1 | As a passenger, I can tap an **SOS button** that sends my current location + trip details to a pre-configured emergency contact via SMS | P1 |
| US-12.4 | The system rate-limits device publish frequency to prevent abuse — hard broker ceiling **5 msg/s per vehicle** (accommodates the 1 s near-pickup/near-drop cadence + retries; ~0.12 Hz blended steady-state), with misbehaving devices throttled and flagged | P0 |
| US-12.5 | As a passenger, I can **report a vehicle** (inappropriate behavior, wrong route info, etc.) | P1 |
| US-12.6 | Reported vehicles are flagged for admin review; 3+ reports trigger temporary delisting | P2 |
| US-12.7 | Driver identity is verified via uploaded ID document (AI extraction + admin review for flagged cases) | P0 |
| US-12.8 | As a driver, I can tap an **SOS button** in the Driver App during an active trip, which sends my GPS location and trip details to a pre-configured emergency contact via SMS | P1 |
| US-12.9 | As a driver, I can **add and manage an emergency contact in my Driver App profile** (name + phone number; pick from contacts or enter manually). This contact is the recipient of my SOS alerts (US-12.8). I can edit or remove it at any time | P1 |
| US-12.10 | As a passenger, I can **block a specific driver** so their vehicle no longer appears in my map view and they cannot be dispatched to me | P2 |
| US-12.11 | The platform logs all SOS events (passenger and driver) with timestamp, GPS location, and trip context for admin review and potential law-enforcement support | P0 |

---

### Epic 13: Fleet Operator Features (Fleet Portal — Phase 1)

> **Role:** Stories in this epic use "fleet operator"; the corresponding access role is **Fleet Owner** (§2.1). The two terms are interchangeable in this document.

> 🟢 **Promoted to Phase 1.** All fleet operator features are delivered through the **Fleet Portal**, a responsive web application hosted on the MageRide domain at **`fleet.mageride.lk`**. The Fleet Portal is a **separate web property** from the Admin Portal (`admin.mageride.lk`) — it is purpose-built for organisations operating multiple vehicles (state/private bus companies, school-transport operators, book-hire fleets). Fleet Operators do **not** use the Driver App to manage their fleet; drivers they assign continue to use the MageRide Driver App.

> **Authentication:** The Fleet Portal supports three sign-in methods — **Email + Password**, **Google Sign-In**, and **Apple Sign-In**. (This differs from the Driver App, which is Phone OTP only, and the Admin Portal, which is Password or Google Sign-In.) All three methods resolve to the same Fleet Owner identity and organisation.

#### 13.A. Fleet Portal Access & Authentication

| ID | Story | Priority | Phase |
|---|---|---|---|
| US-13.A1 | As a fleet operator, I can access the Fleet Portal at **`fleet.mageride.lk`** from any desktop or mobile browser (responsive, mobile-first) | P0 | **Phase 1** |
| US-13.A2 | As a fleet operator, I can **sign up and sign in** using **Email + Password**, **Google Sign-In**, or **Apple Sign-In**; all methods map to the same operator account | P0 | **Phase 1** |
| US-13.A3 | As a fleet operator signing in with Email + Password, I can **verify my email**, **reset a forgotten password**, and the platform enforces password strength and rate-limited login attempts | P0 | **Phase 1** |
| US-13.A4 | As a fleet operator, I can **link/unlink** Google and Apple identities to my existing email account so I can use any method interchangeably | P1 | **Phase 1** |
| US-13.A5 | As a Fleet **Owner**, I can **provision team members (sub-users) for the Manager and Viewer roles** within my organisation; each member authenticates with their own Email+Password / Google / Apple credentials. **Owner / Manager / Viewer** are **org-scoped fleet sub-roles** (not part of the nine canonical roles, §2.1): Owner = full org control + billing; Manager = onboarding/assignment/scheduling/monitoring (no billing/owner changes); Viewer = read-only fleet map & analytics. The "internal roles provisioned only by Super Admin" rule does **not** apply to fleet sub-users (they are provisioned by the Fleet Owner, scoped to that org). | P1 | **Phase 1** |
| US-13.A6 | The Fleet Portal supports **multi-language** (Sinhala, Tamil, English) matching the rest of the platform | P1 | **Phase 1** |
| US-13.A7 | A **Fleet organisation must be verified/approved before it can onboard vehicles or assign drivers**: on sign-up the Fleet Owner submits organisation KYC (business name/registration, contact, authorised-person ID), which a **Verification Officer reviews and approves/rejects** in the Admin Portal (same approval pattern as driver/vehicle onboarding, Epic 2). Until approved, the org is in a **Pending** state with onboarding/assignment disabled. | P0 | **Phase 1** |

#### 13.B. Organisation & Vehicle Onboarding

| ID | Story | Priority | Phase |
|---|---|---|---|
| US-13.1 | As a fleet operator, I can **register an organisation** and **add multiple vehicles** under it (single entry or **bulk CSV upload**, reusing the Epic 3 bulk-onboarding validation and downloadable error report) | P1 | **Phase 1** |
| US-13.1b | When adding a **Mode B vehicle**, I must set a **Service payment — Paid or Free** *(⚠ label renamed from "classification" by US-27.4)* (office/staff transports may be **Free**; others are **Paid** with a default monthly fare). The setting (and default fare for Paid) is captured at onboarding (single & bulk CSV) and drives the passenger-facing subscription fare; **Paid requires a Verified bank & payout profile (US-27.2)**. *(Discussion item 16a/16b — see Epic 23)* | P1 | **Phase 1** |
| US-13.2 | As a fleet operator, I can **assign drivers to vehicles and revoke assignments**; an assignment links an existing MageRide Driver App user (by User ID / phone) to one or more fleet vehicles | P1 | **Phase 1** |
| US-13.6 | As a fleet operator, every vehicle I onboard goes through the **same admin approval workflow** (Epic 2) — AI document extraction, status of Pending / Approved / Rejected visible per vehicle in the Fleet Portal | P1 | **Phase 1** |
| US-13.7 | As a fleet operator, I can **deactivate or remove** a vehicle from my organisation, which immediately removes it from the fleet and passenger maps and force-ends any in-flight session | P1 | **Phase 1** |

#### 13.C. Driver Assignment Behaviour

| ID | Story | Priority | Phase |
|---|---|---|---|
| US-13.9 | As an **assigned driver (non-owner)**, I can see and start sessions on vehicles assigned to me by the fleet operator in the MageRide Driver App, even though I did not register those vehicles. These appear in a **separate "Temporarily assigned to me" group** in vehicle management (showing the assigning fleet and validity); the driver can **select one and go online** with it (subject to the one-vehicle-live rule), and the assignment **auto-expires** | P1 | **Phase 1** |
| US-13.8 | As a fleet operator, when I revoke a driver's assignment, the driver immediately loses the ability to start new sessions on that vehicle; any active session is allowed to complete or is force-ended per operator policy | P1 | **Phase 1** |

#### 13.D. Live Fleet Map, Trips & Analytics

| ID | Story | Priority | Phase |
|---|---|---|---|
| US-13.3 | As a fleet operator, I can **view all my vehicles on a single live map** in the Fleet Portal; positions are scoped to my organisation by **row-level security** and sourced from mobile or hardware trackers transparently (Epic 3 / US-3.24) | P1 | **Phase 1** |
| US-13.4 | As a fleet operator, I can **view trip history and usage analytics per vehicle** (trips, distance, active hours, utilisation, idle time) with date-range filters and CSV/PDF export | P1 | **Phase 1** |

#### 13.E. Scheduling & Schedule Alarms

| ID | Story | Priority | Phase |
|---|---|---|---|
| US-13.11 | As a fleet operator, I can **add/change scheduled rides per vehicle**; if a journey has **not started by its scheduled time**, I am alerted by a **ringing alarm in the Android and iOS Driver Apps** (assigned driver) and a Fleet Portal notification | P1 | **Phase 1** |
| US-13.11b | As an assigned driver, I receive the **schedule-start alarm** on my Driver App (Android & iOS) with the vehicle, route, and scheduled time, and can acknowledge/start the journey directly from the alarm | P1 | **Phase 1** |

#### 13.F. Hardware Tracker (ST-901) Binding & Auto Sessions

| ID | Story | Priority | Phase |
|---|---|---|---|
| US-13.12 | As a fleet operator, I can **assign an ST-901 MAC address (IMEI) to a vehicle**; for ST-901-bound vehicles, **journey start/end is handled automatically** by ST-901 GPS ingestion (no manual Start/End in the Driver App required), reusing the Epic 3 ingest, plausibility, and single-active-publisher rules | P1 | **Phase 1** |

#### 13.G. Fleet Billing

> **Fleet billing model (C-08):** A fleet operates **Mode A and/or Mode B** vehicles only. **Mode A vehicles are always free.** **Mode B vehicles are charged a monthly fee per vehicle.** **Mode C is not a fleet option** — Mode C daily fees are always paid from the **individual driver's wallet** in the Driver App (Epic 9), never from a fleet wallet.

| ID | Story | Priority | Phase |
|---|---|---|---|
| US-13.10 | As a fleet operator, I pay a **monthly fee per Mode B vehicle** from my **fleet wallet**; **Mode A fleet vehicles are free**. Charges are presented as a **single consolidated monthly invoice with a per-vehicle line breakdown** (so the operator pays once, but the amount is the sum of per-Mode-B-vehicle monthly fees). **Mode C is not available to fleets.** | P1 | **Phase 1** |
| US-13.10b | As a fleet operator, I can **top up my fleet wallet** in the Fleet Portal via **Credit/Debit Card, OnePay, or LankaQR**, and receive a downloadable receipt/invoice | P1 | **Phase 1** |

#### 13.I. Mode B Passenger Subscriptions, Requests & Payments

> Per-vehicle management of private (Mode B) passenger subscribers in the Fleet Portal — see **Epic 23** for the full cross-surface stories (fleet portal + passenger app + driver app). Summary stories below are Fleet-Portal-scoped.

| ID | Story | Priority | Phase |
|---|---|---|---|
| US-13.13 | As a fleet Owner/Manager, I can **accept or reject incoming passenger subscription requests for each Mode B vehicle** in my fleet (per-vehicle request queue), mirroring the Driver App accept/reject. *(Discussion item 15)* | P1 | **Phase 1** |
| US-13.14 | As a fleet Owner, for **Paid** Mode B vehicles I can **manage subscriber payments**: see each subscriber's monthly fare, **set/override the monthly amount per subscriber** (subscribers may pay different amounts), see this-month payment status, **mark cash payments as received**, and **confirm online-transfer slips** — all per vehicle. *(Discussion item 16)* | P1 | **Phase 1** |
| US-13.15 | As a fleet Owner, I can **monitor full payment history per subscriber per vehicle** (LankaQR / OnePay / online transfer / cash) with export. *(Discussion item 16i)* | P1 | **Phase 1** |
| US-13.16 | When a passenger **unsubscribes**, the subscriber row stays **muted** in the Fleet Portal until I **delete** it; until deletion the slot is retained for reference. *(Discussion item 17)* | P1 | **Phase 1** |

#### 13.H. Geofence & Route-Deviation Alerts

| ID | Story | Priority | Phase |
|---|---|---|---|
| US-13.5 | As a fleet operator, I receive **alerts when a vehicle deviates from its expected route or enters/exits a geofence**, delivered as Fleet Portal notifications and optional email/SMS | P3 | Phase 3 |

---

### Epic 14: Admin & Moderation

> **Role mapping (§2):** "admin" below means **any internal user whose role permits the action** per the Feature Permission Matrix — not necessarily the generic Admin role. Specifically: registration approval/rejection (US-14.2) is performed by the **Verification Officer** (and Admin/Super Admin); support tickets & disputes (US-14.13) by **Support/CSR**; wallet reversals, settlements & fee/tariff rates (US-14.11, US-14.4, US-14.5) by **Finance Officer** (and Super Admin); Driver Level / feature config (US-14.12) by **Admin/Super Admin**; analytics (US-14.6) is read-available to relevant roles; **Auditor** has read-only access throughout.

| ID | Story | Priority |
|---|---|---|
| US-14.1 | As an admin, I can view all registered vehicles, users, and active sessions on a web dashboard | P1 |
| US-14.2 | As an admin, I can approve/reject vehicle registrations with AI-extracted data shown for verification | P1 |
| US-14.3 | As an admin, I can suspend/ban drivers or vehicles | P1 |
| US-14.4 | As an admin, I can configure fare tariffs per vehicle type | P1 |
| US-14.5 | As an admin, I can configure subscription plan pricing | P1 |
| US-14.6 | As an admin, I can view platform-wide analytics (active vehicles, concurrent passengers, revenue) | P2 |
| US-14.7 | As an admin, I can review vehicle reports and take action | P2 |
| US-14.8 | As an admin, I can broadcast platform announcements to all users or specific segments | P3 |
| US-14.11 | As an admin, I can manually trigger a wallet deduction reversal (credit back) for a driver who was charged a daily fee in error | P1 |
| US-14.12 | As an admin, I can configure the Driver Level system parameters (starting level, penalty thresholds, ban rules) without a code deployment | P1 |
| US-14.13 | As an admin, I can view a support ticket queue and resolve disputes raised by drivers and passengers from within the admin portal | P1 |

---

### Epic 15: Offline & Low-connectivity Handling

| ID | Story | Priority |
|---|---|---|
| US-15.1 | As a driver, if I lose internet connectivity, the app queues GPS positions locally and publishes them when reconnected (MQTT QoS1 + persistent session) | P0 |
| US-15.2 | As a passenger, if I lose connectivity, the map shows last-known vehicle positions with a "connection lost" indicator | P0 |
| US-15.3 | The app caches map tiles for the user's frequently visited area for offline viewing | P1 |
| US-15.4 | The app gracefully reconnects WebSocket sessions after connectivity restoration without user intervention | P0 |
| US-15.6 | As a passenger, the app shows a banner when the device is offline, clearly distinguishing between "no vehicles nearby" and "no connectivity" | P0 |

---

### Epic 16 ★ NEW: In-App Support

| ID | Story | Priority |
|---|---|---|
| US-16.1 | As a passenger or driver, I can access an in-app FAQ section covering common questions (wallet top-up, daily fee, vehicle registration, ride booking) without needing to contact support | P1 |
| US-16.2 | As a passenger or driver, I can raise a support ticket from within the app via a **modal bottom sheet** containing an **issue description** field, a **dropdown to select a past Trip ID**, and a **button to attach a screenshot**, and then track its status | P1 |
| US-16.3 | As an admin, I can respond to support tickets and mark them as resolved from the admin portal, with the resolution visible to the submitting user | P1 |

---

### Epic 17 ★ NEW: App Update / Versioning

| ID | Story | Priority |
|---|---|---|
| US-17.1 | When a critical app update is released, the app shows a mandatory upgrade prompt and prevents use until updated, to ensure API compatibility | P0 |
| US-17.2 | For non-critical updates, the app shows a soft "Update Available" banner that can be dismissed, without blocking access | P1 |

---

### Epic 18 ★ NEW: Ratings & Reviews

| ID | Story | Priority |
|---|---|---|
| US-18.1 | As a passenger, after a completed Mode C or Mode B trip I can rate the driver (1–5 stars) and optionally leave a text comment | P1 |
| US-18.2 | As a driver, after a completed Mode C trip I can rate the passenger (1–5 stars, optional comment) — the **rating opens in a bottom sheet** from the ride-history row (SCR-DA-030 / SCR-DI-030), not as an inline card — to maintain platform trust. *(2026-06-25 item 13)* | P2 |
| US-18.3 | As a driver, I can view my overall rating and per-trip ratings from the Driver App profile screen | P1 |
| US-18.4 | As an admin, I can view aggregated driver rating data and flag drivers with consistently low ratings for review | P2 |

---

### Epic 19 ★ NEW: Accessibility & Compliance

| ID | Story | Priority |
|---|---|---|
| US-19.1 | As a user, all app screens support TalkBack (Android screen reader) for core flows: registration, map, booking, and trip summary | P1 |
| US-19.2 | As a user, I can increase the text size in system settings and the app layout adapts without content being cut off | P1 |
| US-19.3 | The platform maintains an audit log of all admin actions (vehicle approvals/rejections, wallet adjustments, user bans) with timestamp and admin ID for compliance | P0 |

---

### Epic 20 ★ NEW: Package Delivery (Mode C Extension)

> **Rationale:** Package delivery reuses the same Mode C dispatch infrastructure (nearest-driver matching, 15 s accept window, daily platform fee) and the same proxy booking workflow (booker ≠ person at pickup). The same fare tariff as passenger rides applies. Package delivery is **not** restricted by vehicle type — drivers see package details (size, contents) on the incoming request and can accept or reject accordingly.

| ID | Story | Priority |
|---|---|---|
| US-20.1 | As a passenger (sender), I can request a **package delivery** by selecting "Package" instead of "Person" on the Mode C booking screen | P2 |
| US-20.2 | When booking a package delivery, I enter: **pickup location** via **Search / Map / Paste link** (the "Request" option is removed for pickup), **drop-off location** via **Search / Map / Paste link / Request** (Request asks the recipient to share their drop-off), recipient's phone number and name, **package size** (Small / Medium / Large), and a **brief item description**. *(Discussion item 6 — both pickup & drop-off captured; Paste link parses a Google Maps URL)* | P2 |
| US-20.3 | The system dispatches the nearest available driver using the same Mode C dispatch logic. The driver's incoming request screen shows a **"Package Delivery" badge**, package size, item description, pickup/drop-off locations, distance, and payment method. The driver can **accept or reject** based on the package details | P2 |
| US-20.4 | At pickup, the system generates a **4-digit Pickup OTP** shown to the sender. The driver must enter this OTP in their app to confirm package collection. The delivery status changes to "Picked Up" | P2 |
| US-20.5 | **When the driver confirms pickup**, the recipient is notified: **(a) if the recipient has the MageRide app** (number registered) → an **FCM push** *"📦 Package on the way — [Driver] · ETA NN min"*; opening it opens the **recipient tracking screen (SCR-PA-021)**. **(b) if the recipient does NOT have the app** → an **SMS** *"Your package is on the way. Track here: passenger.mageride.lk/track?token=xxxxx"*; tapping it opens a **lightweight no-login web tracking page** (`passenger.mageride.lk`) with the same map, status and **4-digit Delivery OTP** — no app, no login. *(Discussion item 11)* | P2 |
| US-20.6 | At delivery, the driver enters the **Delivery OTP** (provided by the recipient) to confirm handover. If the recipient is unavailable, the driver can take a **photo proof of delivery** as an alternative confirmation method | P2 |
| US-20.7 | Both the sender and recipient can track the package delivery in real-time on the map with status updates: **Pickup Pending → Picked Up → In Transit → Delivered** | P2 |
| US-20.8 | Payment for package delivery supports three methods: **(1)** Sender pays via **LankaQR** (no surcharge), **(2)** Sender pays via **OnePay** (+5% surcharge), or **(3)** **Cash on Delivery (COD)** — recipient pays driver cash upon receiving the package. Sender selects the payment method at booking time | P2 |
| US-20.9 | Package delivery uses the **same fare tariff** as Mode C passenger rides (1st KM charge + per-km rate + peak/night surcharges). No separate delivery tariff | P2 |
| US-20.10 | The daily platform fee structure for package delivery trips is the **same** as passenger Mode C rides — first trip free, fee deducted before 2nd trip. Package deliveries and passenger rides are **counted together** for daily fee purposes | P2 |
| US-20.11 | As a passenger (sender), I can view my past package deliveries with date, route, distance, fare, recipient, and delivery status in trip history | P2 |
| US-20.12 | The driver's **delivery flow is a three-stage bottom-sheet sequence** (SCR-DA-016a/b/c · SCR-DI-016a/b/c): **(1) Review & start** — shows the pickup & drop **distances**, the **payment method**, and the **sender & recipient phone numbers each with a Call button** (a tap places a **mobile-phone voice call** to that party); the driver taps **Start delivery** if satisfied, or **Cancel** to release the job, which **re-dispatches it to the next eligible driver**. **(2) Pickup** — the map shows the pickup location and the sheet offers **Call sender**, **SOS**, and the **Pickup OTP** entry; verifying the pickup OTP advances to stage 3. **(3) Complete** — **Delivery OTP** entry, **Photo proof**, and **both sender & recipient numbers each with a Call button**. *(2026-06-25 item 9)* | P2 |
| US-20.13 | At delivery hand-over, the old **"Cash received (COD)"** action is replaced by a **"Delivery completed"** button — on tap the ride moves to **Completed** and both sender and recipient receive a confirmation. Any COD / cash reconciliation is handled separately (uncollected COD > 24 h → Disputed, P-14). *(2026-06-25 item 9)* | P2 |

> **Package Delivery Payment Methods**
>
> | Method | Who Pays | How It Works |
> |---|---|---|
> | **LankaQR** | Sender | Sender receives LankaQR payment link after delivery is confirmed. No surcharge. |
> | **OnePay** | Sender | Auto-charged to sender's card (+5% surcharge). |
> | **Cash on Delivery (COD)** | Recipient | Recipient pays driver cash upon receiving the package. Sender selects "COD" at booking; recipient is notified via FCM push. |

---

### Epic 21 ★ NEW: Role-Based Access Control (RBAC) & Internal Console

> **Rationale:** The platform must enforce the nine-role model (§2) consistently across every surface. Permissions are **deny-by-default** and enforced **server-side** on every privileged endpoint, with the Admin Portal rendering only the menus a role permits. This epic covers role/permission management and the **Admin Portal** (`admin.mageride.lk`) used by the six internal roles (Admin, Super Admin, Verification Officer, Support/CSR, Finance, Auditor).

| ID | Story | Priority |
|---|---|---|
| US-21.1 | The platform implements **role-based access control** with the nine canonical roles in §2.1; every privileged API enforces the **Feature Permission Matrix (§2.3) server-side**, deny-by-default, independent of what the UI shows | P0 |
| US-21.2 | As a **Super Admin**, I can **provision internal user accounts** and **assign one or more roles** (Admin, Super Admin, Verification Officer, Support/CSR, Finance, Auditor) to each | P0 |
| US-21.3 | As a **Super Admin**, I can **create custom permission sets / adjust role-to-feature grants** without a code deployment, and changes take effect for new sessions immediately | P1 |
| US-21.4 | A user holding **multiple roles** receives the **union** of those roles' permissions, always bounded by the matrix; the system resolves effective permissions at login and on role change | P0 |
| US-21.5 | All **internal roles (4–9) log in to the Admin Portal (`admin.mageride.lk`) via Password or Google Sign-In**; end-user roles (Driver, Passenger via Phone OTP; Fleet Owner via Email/Google/Apple) follow their respective auth methods. **No MFA/TOTP second factor** — ~~optional MFA (TOTP) may be enabled per internal account~~ removed by US-24.5; internal accounts are protected by failed-attempt lock-out + an optional IP allow-list | P0 |
| US-21.6 | The **Admin Portal renders role-scoped menus** — each internal user sees only the modules and records their role(s) permit (e.g., a Verification Officer sees only the onboarding queue; an Auditor sees read-only views) | P0 |
| US-21.7 | As an **Auditor**, I have **read-only** access to logs, transactions, audit trails, and reports, and **cannot perform any mutating action** anywhere in the Admin Portal | P0 |
| US-21.8 | Every **mutating action by an internal user** is written to an **immutable audit log** with actor identity, role, timestamp, target entity, and before/after values (ties to US-19.3) | P0 |
| US-21.9 | As a **Super Admin**, I can **suspend, reactivate, or revoke** an internal account and force-terminate its active sessions within 60 seconds | P0 |
| US-21.10 | As a **Verification Officer**, I can access **only** the driver/vehicle onboarding queue (review documents, licenses, registration, background checks; approve/reject with reason) and cannot access finance, configuration, or RBAC | P0 |
| US-21.11 | As a **Finance / Payments Officer**, I can access payouts, settlements, reconciliation, wallet reversals/adjustments, refund approval/execution, and fee and voucher-discount configuration — but not RBAC, moderation, or onboarding approval | P0 |
| US-21.12 | As a **Support Agent / CSR**, I can access the support ticket queue, read-only trip/user lookup, dispute investigation, refund requests, and limited temporary actions (block on reports) — but cannot configure tariffs, manage finance settlements, or manage roles | P0 |
| US-21.13 | As a **Super Admin**, I can **impersonate (view-as) a role** in a sandboxed/read-only mode to verify what that role can see, for support and QA, with the impersonation recorded in the audit log | P2 |
| US-21.14 | Permission changes, role assignments, and internal-account lifecycle events are **themselves audited** and visible to Auditors and Super Admins | P0 |

---

### Epic 22 ★ NEW: Passenger App Settings

> Available in the **MageRide Passenger App (Android & iOS)** under **Settings**. Locations are picked on the **OSM map** (Maplibre) with reverse-geocoding via Nominatim/Photon (per §3).

| ID | Story | Priority |
|---|---|---|
| US-22.1 | As a passenger, in **Settings** I can **save Home and Work by selecting the location on the map** (drop/drag a pin); the reverse-geocoded address is shown and stored, and Home/Work appear as quick shortcuts in booking and map centering (ties to US-7.13) | P1 |
| US-22.2 | As a passenger, I can **save additional addresses** by selecting a pin on the OSM map. After dragging/dropping the pin, tapping **＋ Add address** opens a **ModalBottomSheet** (separate view, SCR-PA-026a) that captures: **Address Line 1** (main street/building), **Address Line 2** (area/suburb), **Address Line 3** (city/district), and a free-text **Label** (e.g., "Gym", "Mum's House", "Office"). Saved addresses are selectable as pickup/drop-off during booking. *(Discussion item 7)* | P1 |
| US-22.3 | As a passenger, I can **edit or delete** any saved address (including Home and Work) | P1 |
| US-22.4 | As a passenger, I can set my **Default Payment Method** in Settings — **Cash (default)**, **LankaQR**, or **OnePay** — which is pre-selected at booking/checkout (and still changeable per trip). OnePay shows the +5% surcharge note; LankaQR uses the Pay-button deep link (US-8.10a) | P1 |
| US-22.5 | As a passenger, I can open **Help & Support** from Settings — the in-app FAQ and raise/track support tickets (ties to Epic 16) | P1 |
| US-22.6 | Saved addresses and default payment method are **tied to the passenger account** and restored on the device per the eager/lazy data-population strategy (saved addresses + payment-method metadata are part of the eager-fetch set, US-1.15) | P1 |
| US-22.7 | As a passenger, I can open a **menu / navigation drawer** (from the ≡ menu / "Menu" tab) that links to: **Private transport** (→ SCR-PA-024 Mode B access request), **My subscriptions** (→ SCR-PA-025), **Saved addresses** (→ SCR-PA-026), and **Profile & settings** (→ SCR-PA-027). *(Discussion item 9)* | P1 |
| US-22.8 | As a passenger, on a **Mode B subscription card** (My subscriptions, SCR-PA-025) I can: see whether the vehicle is **Paid** (amount/mo + next-due) or **Free**; tap **💳 Pay** (Paid only) to open the **subscription payment screen** (SCR-PA-025a); tap **🧾** to view my **payment history** (SCR-PA-025b); and tap a **compact unsubscribe icon button**. *(Discussion items 16 & 17 — see Epic 23)* | P1 |

---

### Epic 23 ★ NEW: Mode B Subscription Payments & Requests (Discussion 2026-06-21)

> **Rationale:** Private (Mode B) fleets — office transport, school vans, staff shuttles — need to **collect monthly fees from subscribed passengers** and to **gate who can track each vehicle**. A vehicle is classified **Paid** or **Free**; Paid vehicles collect a per-subscriber monthly fare. Passengers pay **electronically in-app** (LankaQR / OnePay / online transfer) **to the fleet owner**, or in **cash** which the owner marks received. This epic spans the **Fleet Portal** (SCR-FP-011/012), the **Passenger App** (SCR-PA-025/025a/025b) and the **Driver App** (SCR-DA-028). Covers discussion items **15, 16, 17**.

| ID | Story | Priority |
|---|---|---|
| US-23.1 | As a fleet Owner/Manager, each **Mode B vehicle has its own queue of incoming passenger subscription requests**, and I can **accept or reject** each. The same accept/reject is available to the assigned driver in the Driver App (SCR-DA-028, per-vehicle). Accepting grants tracking access and starts the subscription. *(item 15)* | P1 |
| US-23.2 | Each Mode B vehicle is classified **Paid** or **Free** at onboarding (US-13.1b). **Free** = office/staff transport (no fee, no payment UI). **Paid** = monthly fare collected per subscriber. *(item 16a/16b)* | P1 |
| US-23.3 | As a passenger, I can **pay my subscription electronically in-app** to the fleet owner via **LankaQR (deep link)**, **LankaQR (scan QR)**, **OnePay**, or **online bank transfer**. Tapping **💳 Pay** on the subscription card (SCR-PA-025) opens the **payment screen** (SCR-PA-025a). *(item 16c/16e)* | P1 |
| US-23.4 | When paying by **online transfer**, the passenger must **attach a screenshot** of the transfer slip; the payment is recorded as **Pending verification** until the fleet owner confirms it in the portal. *(item 16e)* | P1 |
| US-23.5 | Subscriber payments **appear in the Fleet Portal under the corresponding vehicle** (per-subscriber and per-vehicle), routed to the **fleet owner** (pass-through; not platform revenue). *(item 16d)* | P1 |
| US-23.6 | As a passenger paying **cash**, I hand it to whoever collects for the fleet; **only the fleet Owner can mark it received** in the web portal. Once marked received, the passenger's subscription card (SCR-PA-025) shows **Paid** and the payment appears in the passenger's payment history. *(item 16f)* | P1 |
| US-23.7 | As a fleet Owner, I can **set/update the monthly payment amount per subscriber** — each subscriber may pay a different amount. *(item 16f-monthly)* | P1 |
| US-23.8 | A subscription's **billing cycle** may run **from the 1st of each month** or from the **anniversary of the subscriber's join day** (e.g., subscribed on 5 June → next payment due 6 July). The cycle and next-due date are shown to both the subscriber and the owner. *(item 16g)* | P1 |
| US-23.9 | As a subscriber, I can **view my payment history** in the app (SCR-PA-025b) — month, date, method, amount, and status (Paid / Pending verification). *(item 16h)* | P1 |
| US-23.10 | As a fleet Owner, I can **monitor the full payment history per subscriber for each vehicle** (SCR-FP-012), with summary KPIs (collected / pending verify / cash due) and export. *(item 16i)* | P1 |
| US-23.11 | As a passenger, I can **unsubscribe** from a vehicle via a **compact unsubscribe icon button** (SCR-PA-025). After unsubscribing I **can no longer see the vehicle**; to rejoin I must request again and the driver/owner must accept. *(item 17)* | P1 |
| US-23.12 | An **unsubscribed passenger remains visible but muted in the Fleet Portal until the owner deletes** that subscriber from the corresponding vehicle. *(item 17)* | P1 |

> **Money handling:** Mode B subscription payments are **between the passenger and the fleet owner** (pass-through). MageRide facilitates LankaQR/OnePay routing to the fleet owner's merchant account and records online-transfer/cash confirmations; these are **not** platform revenue and are shown read-only to Admin Finance for dispute support only.

### Epic 24 ★ NEW: 2026-06-28 Change Set (UX & Admin Directory)

> **Rationale:** A focused review on 2026-06-28 produced eleven UX/admin refinements across the Passenger App, Driver App and Admin Portal. They are grouped here for traceability; each story also updates its home epic and screen. Covers change-file items **1–11**.

**a) Passenger App**

| ID | Story | Priority |
|---|---|---|
| US-24.1 | As a new user on the onboarding screen (SCR-PA-002 / SCR-PI-002), the **Get Started button is pinned to the bottom of the screen** (full-width primary CTA below the language list), so the primary action is always within thumb reach. *(item 1; refines US-1.2)* | P2 |
| US-24.2 | As a passenger scheduling a ride (SCR-PA-013 / SCR-PI-013), I must **select the destination ("the location to go")** — a mandatory destination picker (same place-search / map-pick as on-demand booking) plus an editable pickup that defaults to my current location. **Confirm is disabled until a destination is set.** *(item 2; refines US-10.1 → US-10.1a)* | P1 |
| US-24.3 | As a passenger, when I tap **Call** during an active ride (SCR-PA-015 / SCR-PI-015), and in trip history (SCR-PA-022) and trip details, I am first shown a **call-type chooser (SCR-PA-015a)** offering **Free call** (in-app VoIP, both numbers hidden) or **Normal call**. *(item 4; refines US-6A.16 → US-6A.16a. ⚠ Masked-number clause superseded by US-26.2 — Normal call is now a direct dial of the real number, post-accept.)* | P1 |
| US-24.4 | As a passenger viewing **trip & schedule history** (SCR-PA-022 / SCR-PI-022), each completed trip card **shows the driver's name and mobile number** with a **Call** action (opens the call-type chooser), so I can reach the driver after a trip (e.g. for a lost item). Number is hidden for trips cancelled before driver assignment. *(item 3; refines US-8.7 → US-8.7a)* | P2 |

**b) Driver App**

| ID | Story | Priority |
|---|---|---|
| US-24.6 | As a driver capturing onboarding documents — profile driving licence front/back (SCR-DA-003a), insurance (SCR-DA-004a), revenue licence (SCR-DA-004b) and vehicle front/back photos (SCR-DA-004c) — every **📷 capture slot opens a camera document-scanner (SCR-DA-005) with an adjustable crop frame: I drag the four corner handles so the whole document fits the full frame** before it is saved. Auto edge-detect proposes a quad; manual drag overrides. The cropped, de-skewed image is what gets uploaded and sent to Gemini Flash, improving OCR confidence and reducing admin-verify flags. *(item 6; refines US-2.4 → US-2.4b)* | P1 |

**c) Admin Portal**

| ID | Story | Priority |
|---|---|---|
| US-24.5 | As internal staff signing in to the Admin Portal (SCR-AP-001), there is **no OTP / MFA / authenticator step** — I sign in with **password or Google only** and land directly on the dashboard. *(item 5; refines §2.2 / AL-07)* | P1 |
| US-24.7 | As an admin on the dashboard (SCR-AP-002), I can **filter the displayed statistics by Today / This week / This month / a custom date range** (Asia/Colombo). Period KPIs (completed trips, gross fare, new riders/drivers, daily-fee revenue) recompute against the selected range and show a **vs-previous-period** delta; live cards stay real-time; figures are exportable as CSV. *(item 7; refines Epic 14)* | P1 |
| US-24.8 | As a Verification Officer, the onboarding/verification screen is **split into a queues list (SCR-AP-003) and a detail screen (SCR-AP-003a)**. The list has **three separate queues — driving-licence pending, vehicle-registration pending, and fleet-org approval**. Selecting an entry opens its detail with an **attached-document thumbnails grid**; **tapping any thumbnail opens it in a full-size viewer (SCR-AP-003b)** with zoom/rotate/paging. Fleet-org entries open a dedicated approval detail (SCR-AP-003c). *(item 8; refines US-2.9)* | P1 |
| US-24.9 | As Support/Admin, I can **search for a passenger by multiple criteria** (name, mobile, passenger ID, email) on a passenger directory (SCR-AP-010) and open a **passenger detail (SCR-AP-011)** showing profile plus tabbed **Trips / Payments / Packages / Disputes**. PII is role-gated and every lookup is audited. *(item 9; new in Epic 14)* | P1 |
| US-24.10 | As Support/Admin/Finance, I can **search verified drivers by multiple options** (name, mobile, driver ID, NIC no, vehicle reg no, Driver Level, status) on a driver directory (SCR-AP-012) and open a **driver detail (SCR-AP-013)** showing profile/wallet/level + linked vehicles and tabbed **Trips / Wallet ledger / Daily fee / Credit transfers / Reports**. Reversals remain Finance-only; all views audited. *(item 10; new in Epic 14)* | P1 |
| US-24.11 | As Support/Admin/Finance, I can **search registered vehicles by different criteria** (registration no, vehicle ID, type, mode, owner mobile, fleet org, status) on a vehicle directory (SCR-AP-014) and open a **vehicle detail (SCR-AP-015)** showing registration/insurance/revenue-licence/tracker info, a document-thumbnail grid (→ full-size viewer), and tabbed **Trips / Earnings / Daily fee / Reports**. *(item 11; new in Epic 14)* | P1 |

> **Privacy/audit:** All passenger/driver/vehicle directory lookups and every full-size document view in the Admin Portal write a **read-access audit entry** (actor, target, timestamp); PII fields (mobile, email, NIC) render only for roles whose RBAC grant permits them.

### Epic 25 ★ NEW: 2026-07-05 Change Set (Passenger Web Subview Contracts & Spec Hygiene)

> **Rationale:** A spec-vs-wireframe audit on 2026-07-05 found the `passenger.mageride.lk` no-login subview (US-8.22 / US-11.9 / US-20.5) wireframed as six pages but not implementable from spec — no screen IDs, no public API contracts, and a contradiction on the unregistered-rider pickup-confirm path. This epic formalizes the existing requirements into contracts; it adds **no new product surface** (US-11.1's four-surface rule is unchanged). Covers change-file items **1–8**.

**a) Passenger Web subview (`passenger.mageride.lk` → screens `SCR-WT-001…006`)**

| ID | Story | Priority |
|---|---|---|
| US-25.1 | As a link-recipient (package recipient or proxy rider), the six web pages carry **screen IDs SCR-WT-001…006** — landing/token gate (001), package track (002), confirm pickup (003), ride track (004), delivered/receipt (005), expired link (006) — each with defined states, so the surface is buildable and traceable like every other surface. *(item 1; formalizes US-8.22/11.9/20.5)* | P1 |
| US-25.2 | As a token-holder opening a tracking link, the page is served by a **public, token-authenticated API** (`/public/track/{token}` + live feed): the token is the only credential, the response is shaped to the token's scope, and an expired/invalid token yields a safe dead-end with zero ride data. *(item 2)* | P1 |
| US-25.3 | As an **unregistered proxy rider** asked for my pickup location, I receive an **SMS web link** and can share (or decline) an adjustable map pin **in the browser within the 5-minute TTL** — feeding the same location-request state machine as the in-app confirm; declining never sends my GPS. The booker's map-pin/search fallback remains for decline/expiry. *(item 3; extends US-8.19)* | P1 |
| US-25.4 | As a web tracker (recipient or proxy rider), I can **call the driver from the browser** (`tel:`) — no app, mic permission, or VoIP stack required. *(item 4. ⚠ Proxy-DID/masking clause superseded by US-26.3 — the link now dials the driver's real number.)* | P1 |
| US-25.5 | As a proxy rider tracking on the web, I have an **SOS action** that sends my browser location via dual-gateway SMS (≤5 s) to the **booker** and raises the admin live feed. *(item 5; extends US-12.1 to the web subview)* | P1 |
| US-25.6 | As a package recipient/sender party on the web, after delivery I see the **outcome page** — OTP-verified, photo-proof (recipient absent), COD collected, or Disputed (>24 h uncollected) — and can **download the receipt** while the link is valid. *(item 6; completes US-20.5)* | P2 |
| US-25.7 | As the platform, share tokens support the scopes **package_recipient / proxy_rider / pickup_confirm** with per-scope TTLs, single-use burning (pickup_confirm), per-token + per-IP rate limits, and access metering. *(item 7; hardens US-11.9's token rule)* | P1 |

**b) Spec hygiene**

| ID | Story | Priority |
|---|---|---|
| US-25.8 | As an implementer reading the specs, **SCR-DA/DI-012 is re-tagged [MERGED → SCR-DA/DI-010]** (the standby toggle is a dashboard state, not a screen), and stale wireframe annotations are corrected (US-8.7a → US-24.4; splash resume endpoint → the per-role D3 paths), so no redundant screen gets built and cross-references resolve. *(item 8)* | P2 |

### Epic 26 ★ NEW: 2026-07-05 Change Set #2 (Driver-QR Settlement & Number-Masking Removal)

> **Rationale:** Two product decisions closing feasibility conditions C2/C3 (`technical_feasibility.md`). **(a)** Payments scanned into the **driver's own bank-issued LankaQR** produce **no gateway callback** to the platform, so `Paid` cannot be gateway-verified — settlement becomes **attestation-based**, like cash. **(b)** The **number-masking requirement is withdrawn**: matched parties may see each other's real numbers once a driver accepts; this removes the Sri Lanka provider gap (no proxy-DID/CPaaS product with +94 numbers) instead of engineering around it. VoIP remains as the free in-app call option. **Supersedes the masking clauses of US-6A.16/US-6A.16a, US-8.22, US-11.9, US-24.3 and US-25.4, and the masked-SMS relay fallback (ADD D-25).** Covers change items **1–6**.

**a) Payments**

| ID | Story | Priority |
|---|---|---|
| US-26.1 | As a passenger who paid by **scanning the driver's QR**, I tap **"I've paid"** (optional receipt-screenshot attach as dispute evidence); the driver gets a **"QR payment received?"** confirm and on **Confirm** the payment reaches the terminal state **DriverConfirmedQR** — the ride settles like cash (driver earning posts, R-05). The driver may also confirm **without** a passenger claim. If the passenger claims and the driver does not confirm, the app nudges the driver; unresolved claims route to **Support → Finance dispute** (no money moves — zero-commission). Gateway-verified `Succeeded` remains **OnePay-only**. *(item 1; refines US-8.15 / AL-22 / D-10)* | P1 |

**b) Calling (masking removed)**

| ID | Story | Priority |
|---|---|---|
| US-26.2 | As a passenger or driver on a matched ride, **Normal call places a direct cellular call to the counterparty's real mobile number**. Numbers are **revealed only after driver acceptance** and are **withheld for rides cancelled before assignment** (existing US-24.4 rule); for proxy bookings the driver sees **the rider's number, never the booker's** (P-05). The **call-type chooser (SCR-PA/PI-015a) is retained**: **Free call** (in-app VoIP) / **Normal call** (direct dial). *(item 2; supersedes the masked-number clause of US-24.3 / US-6A.16a)* | P1 |
| US-26.3 | As a web tracker (package recipient SCR-WT-002 / proxy rider SCR-WT-004), the page shows the **driver's number as a tap-to-call `tel:` link** in the driver card. `POST /public/track/{token}/call` and the proxy-DID lease are **removed**. *(item 3; supersedes US-25.4)* | P2 |
| US-26.4 | As a caller whose **VoIP call fails** (poor data), the app offers **"Call normally instead?"** — a direct dial to the same number. The **masked-SMS relay fallback (D-25) is removed**. *(item 4)* | P2 |
| US-26.5 | As a user, the sign-up ToS and the first-call tooltip disclose that **matched parties see each other's mobile numbers for ride coordination** (PDPA transparency); blocking a driver (US-12.10) still prevents future matching. *(item 5)* | P2 |

### Epic 27 ★ NEW: 2026-07-18 Change Set (Fleet Portal Payout & Vehicle-Document Detail)

> **Rationale:** Three gaps found reviewing the Fleet Portal against the Mode B money flow and Sri Lankan vehicle-compliance paperwork. **(a)** Mode B subscription payments are pass-through to the fleet owner (Epic 23), yet the portal never captured **where** the money goes — no bank details, no proof of account ownership, and no home for the **bank-app LankaQR code** the passenger pay sheet must display. **(b)** SCR-FP-004 accepted "insurance + registration docs" as one generic upload — too thin to gate approval on the four documents a Sri Lankan operator actually holds (CR, insurance, revenue license, and the **route permit** that Mode A passenger transport legally requires). **(c)** The label "Mode B classification" confused owners — it is simply whether the service is paid: renamed **"Service payment" (Free / Paid)**. Covers change items **1–3**.

**a) Bank & payout details (SCR-FP-002a)**

| ID | Story | Priority |
|---|---|---|
| US-27.1 | As a fleet **Owner**, I can record my organisation's **bank & payout details** in a new **Bank & payout details** screen (SCR-FP-002a, Owner-only): **bank, branch, account number, account holder name**, plus document uploads — a **copy of the latest bank statement *or* the first page of the passbook**, and the **LankaQR code image generated by my bank app**. A **Verification Officer** reviews the profile in the existing fleet-org queue (SCR-AP-003); the **account-holder name must match the org/owner KYC name**. Any edit re-enters Pending verification. *(item 1)* | P1 |
| US-27.2 | Mode B subscription payments route to the **verified** payout profile: the passenger pay sheet (SCR-PA-025a) shows my **LankaQR code image** (scan / deep link) and my **verified account details** (online transfer). A vehicle **cannot be set Service payment = Paid**, and Paid subscriptions cannot start billing, until my payout profile is **Verified**; payers always see the last verified snapshot, never unverified edits. *(item 1; extends US-23.5 / BR-23.10 — pass-through unchanged, MageRide holds no subscriber money)* | P1 |

**b) Vehicle onboarding document detail (SCR-FP-004)**

| ID | Story | Priority |
|---|---|---|
| US-27.3 | As a fleet operator onboarding a vehicle, SCR-FP-004 gives me **named document slots** instead of one generic upload: **registration copy (CR book)**, **insurance certificate**, **revenue license** — required for **all** vehicles — and **route permit**, required for **Mode A** vehicles. Each upload is AI-extracted (reg-no ↔ plate match, expiry dates, permit no/route) with a per-document **Verified / Pending / Missing** status; the vehicle **cannot be Approved while a required document is Missing or Pending**, and expiry of any required document auto-suspends dispatch (existing E-03 rule). Bulk-CSV rows are created "Docs pending" and completed per vehicle. *(item 2; details US-13.1/13.6, extends AL-10)* | P1 |

**c) Naming**

| ID | Story | Priority |
|---|---|---|
| US-27.4 | The Mode B **Paid/Free** setting is labelled **"Service payment"** throughout the Fleet Portal (SCR-FP-004 form field and status-table column; formerly "Mode B classification"). Values remain **Free / Paid**; the API path (`/classification`) and DB column (`mode_b_billing`) are intentionally unchanged. *(item 3; renames the label of US-13.1b / BR-23.8)* | P2 |

### Epic 28 ★ NEW: 2026-07-22 Change Set #2 (GTFS Dataset Manager)

> **Rationale:** Mode A route-matching (US-8.2b) is fed by an admin-managed GTFS dataset, but its only input was a raw API endpoint with no screen, no validation feedback, and no way to see or undo what is live. The premise has also changed: **a full national GTFS file will be available at the beginning**, so day-0 operations need a proper interface — upload the complete feed, verify it, make it live, and roll back if a bad feed slips through. Corridor-first acquisition is re-scoped to how the feed is *refreshed*, not how launch is gated *(the standalone acquisition plan was subsequently retired on 2026-07-23 — the feed, launch and refreshes alike, is externally provided and enters via SCR-AP-016; ADD AL-56)*. New Admin Portal screen **SCR-AP-016** (Configuration group; Admin + Super Admin only; all mutations audited).

| ID | Story | Priority |
|---|---|---|
| US-28.1 | As an **Admin**, I can open the **GTFS Dataset Manager (SCR-AP-016)** and **upload the full GTFS zip** (drag-and-drop or file picker, up to 200 MB). The system validates it server-side — required files present (`agency`, `routes`, `trips`, `stops`, `stop_times`, `calendar`/`calendar_dates`), referential integrity, duplicate IDs, stops within Sri Lanka, service-window sanity — showing progress **Uploaded → Validating → Validated / Failed**; on failure I can **download a row-level error report** (file, row, error) to fix the feed and re-upload. A duplicate of an already-uploaded file is detected and refused. | P0 |
| US-28.2 | Once a feed is **Validated**, I see a **preview** — per-file counts (agencies, routes, trips, stops, stop times, shapes), the feed version from `feed_info`, the service date range, and any warnings — and can **Activate** it after a confirmation step. Activation is **atomic**: the live dataset is swapped in a single transaction and `transit-svc` reloads within a minute, so passenger booking screens (US-8.2b) serve the new routes immediately with no partial state. Exactly **one feed version is active** at any time. | P0 |
| US-28.3 | I can see the **version history** — every uploaded feed with its status (Active / Archived / Validated / Failed), who uploaded it and when, and its counts — **download the original zip** of any version, and **roll back** by re-activating a previously validated (archived) version in one click, with the same atomic-swap guarantee. | P1 |

---

## 5. Non-Functional Requirements

### 5.1 Performance

| ID | Requirement | Target |
|---|---|---|
| NFR-01 | End-to-end position latency (device → passenger screen) | p95 2–8 seconds |
| NFR-02 | Map rendering frame rate | ≥ 30 FPS on mid-range Android (Snapdragon 600 series) |
| NFR-03 | App cold start time | < 3 seconds to interactive map |
| NFR-04 | API response time (CRUD operations) | p95 < 500 ms |
| NFR-05 | WebSocket reconnection time | < 5 seconds |

### 5.2 Scalability

| ID | Requirement | Target |
|---|---|---|
| NFR-06 | Concurrent vehicles (initial launch) | 10,000 |
| NFR-07 | Concurrent passenger sessions (initial launch) | 100,000 |
| NFR-08 | Scale-out ceiling | 100,000 vehicles / 1,000,000 passengers |
| NFR-09 | Scale by adding nodes, not re-architecting | Mandatory |

### 5.3 Availability & Reliability

| ID | Requirement | Target |
|---|---|---|
| NFR-10 | Tracking plane availability | 99.5% monthly |
| NFR-11 | API availability | 99.9% monthly |
| NFR-12 | RPO (operational data) | 5 minutes |
| NFR-13 | RTO (full recovery) | 30 minutes |

### 5.4 Security

| ID | Requirement |
|---|---|
| NFR-14 | All external connections encrypted (TLS 1.3) |
| NFR-15 | Per-device MQTT authentication (JWT or X.509) |
| NFR-16 | Devices can only publish to their own vehicle topic |
| NFR-17 | GPS position plausibility validation on all ingest |
| NFR-18 | PII (ID documents, permit photos) encrypted at rest |
| NFR-19 | No secrets in source code, APK, or environment variables |
| NFR-20 | Certificate pinning on mobile app |
| NFR-45 | **Role-based access control enforced server-side** (deny-by-default) on every privileged endpoint per the §2.3 Feature Permission Matrix; UI gating is never the sole control |
| NFR-46 | All internal/back-office roles authenticate to the **Admin Portal (`admin.mageride.lk`) via Password or Google Sign-In**, with **no MFA/TOTP second factor** (~~optional MFA (TOTP) enforced per account / globally by a Super Admin~~ — removed by US-24.5). Compensating controls: **failed-attempt lock-out**, session binding, and an **optional IP allow-list** per internal account |
| NFR-47 | **Least-privilege** internal accounts; internal roles provisioned only by Super Admin; revocation/session-termination enforced within 60 s |
| NFR-48 | **Auditor role is strictly read-only** at the API layer — no write/mutate capability under any code path |
| NFR-49 | Every internal mutating action and every permission/role change is written to an **immutable, tamper-evident audit log** retained per compliance policy |
| NFR-50 | **Single active device session** per passenger/driver account: a new-device login **revokes the previous device's access & refresh tokens server-side** (Redis + PostgreSQL) and force-logs-out the old device (token rejection + FCM/APNs push) within **≤ 5 s p95**; the old device cannot access authenticated screens or cached personal data thereafter |
| NFR-51 | **Login payload is bounded** — eager fetch returns only the essential set (US-1.15); large/unbounded data (trip history, earnings breakdowns, receipts) is lazy-fetched per screen (US-1.16). Active-trip state is always restored on login for trip continuity |
| NFR-52 | The **Passenger Web Portal** SMS link uses a **single-ride-scoped, expiring, unguessable token** (no standing credentials); it grants access only to that one ride's tracking data, is revoked on trip completion / TTL expiry, and is served over TLS |

### 5.5 Usability & Accessibility

| ID | Requirement |
|---|---|
| NFR-21 | Multi-language support: Sinhala, Tamil, English |
| NFR-22 | Minimum Android version: Android 8.0 (API 26) |
| NFR-23 | Dark mode and light mode support |
| NFR-24 | Accessible color contrast ratios (WCAG AA) |
| NFR-25 | Screen reader compatible for core navigation flows |

### 5.6 Data & Privacy

| ID | Requirement |
|---|---|
| NFR-26 | Position history retained 12 months, archived to cold storage after |
| NFR-27 | User can request deletion of their account and associated data |
| NFR-28 | AI-processed documents (ID/permit) raw images deleted after 90 days |
| NFR-29 | No user data sent to third-party LLMs without privacy-stripped preprocessing |

### 5.7 Battery & Data Usage

| ID | Requirement |
|---|---|
| NFR-30 | Adaptive GPS publish rate: **1 call/4s moving → 1 call/10s stationary → 1 call/60s idle standby**, plus a **1 call/second near-pickup/near-drop burst** (Mode C, within an admin-configurable radius — default 150 m — of the pickup or drop-off; US-6A.24). The 1 s burst is bounded by the US-12.4 broker ceiling of 5 msg/s/vehicle |
| NFR-31 | Driver mode battery consumption < 10%/hour on typical device |
| NFR-32 | Passenger mode data usage < 5 MB/hour |
| NFR-33 | MQTT binary payloads (CBOR/Protobuf, ~100 bytes) over JSON (~250 bytes) where feasible |

### 5.8 Hardware GPS Tracker (Telematics)

| ID | Requirement | Target |
|---|---|---|
| NFR-34 | Concurrent hardware trackers supported | **100,000 active devices** (scale-out ceiling); 10,000 at launch |
| NFR-35 | Tracker → passenger screen end-to-end latency | p95 < 5 s, p99 < 8 s (same SLO as mobile) |
| NFR-36 | Tracker ingest throughput | Sustained **30,000 msg/s**, burst 90,000 msg/s (blended hardware + mobile) |
| NFR-37 | Replay (offline backlog) ingest | Up to 20 backlog samples/s/device, lower priority than live, never starves live samples |
| NFR-38 | Per-device credential rotation | Maximum credential TTL 90 days; rotation is non-disruptive |
| NFR-39 | TCP/UDP adapter availability | 99.9% monthly per protocol family |
| NFR-40 | Position retention (hot, queryable) | 30 days at full resolution; 12 months downsampled; archived to cold storage after |
| NFR-41 | Plausibility check rejection rate | < 0.5% of valid samples falsely rejected |
| NFR-42 | Adapter horizontal scale | Add nodes without re-IPing devices (DNS-based routing or anycast); zero-downtime deploy |
| NFR-43 | Fleet bulk-onboarding throughput | 5,000 IMEIs validated and provisioned per CSV upload within 5 minutes |
| NFR-44 | Decommission propagation | Revoked credentials enforced platform-wide within 60 s |

---

## 6. Screen Inventory (Android App)

### MageRide Passenger App

| Screen | Key Elements |
|---|---|
| **Splash / Onboarding** | MageRide logo, 3-slide tutorial, **language selection as vertical boxes — Sinhala (first) / Tamil / English**; **Get Started CTA pinned to the bottom** of the screen below the language list (2026-06-28 item 1 / US-24.1) (item 1) |
| **Login** | Phone number + OTP input |
| **Live Map** (home) | OSM map, vehicle markers, current location, filter by Mode A (Public Transport) / Mode B (Private Transport) / Mode C (Standby On-Demand). Type-filter chips show a **small vehicle icon tinted with its AL-09 colour** (item 2). Tapping a **Mode B marker → Private Transport Request** (item 8) |
| **Ride Booking (Mode C — Standby On-Demand)** | **Destination is a geo-location only** (no route-number typing, item 4). Results list **Mode A public buses from GTFS** with route number + description + Direct/Transit + PUBLIC label (item 3); **Mode C private tiers show price only — no minutes/distance** (item 3). **"For Me" / "For Someone Else"** and **"Person" / "Package"** toggles; proxy pickup methods = type & search, map pin, **Paste link** (Google Maps URL), Request (item 5) |
| **Confirm Pickup Location (Rider)** ★ NEW | Shown when rider receives FCM location request from a booker. Displays rider's live GPS on map with adjustable pin, booker's name, "Confirm" and "Decline" buttons |
| **Package Booking** ★ NEW | Package details form: size (Small / Medium / Large), item description, recipient phone + name. **Pickup location**: Search / Map / Paste link; **Drop-off location**: Search / Map / Paste link / Request (item 6). Payment method selection including COD option |
| **Package Tracking (Sender)** ★ NEW | Live map showing driver position + package status bar (Pickup Pending → Picked Up → In Transit → Delivered). Pickup OTP display |
| **Package Tracking (Recipient)** ★ NEW | Live map showing driver position + package status. Delivery OTP display. Driver details. Reached via **FCM push** (app) or **SMS web-tracking link** (no app, item 11) |
| **Vehicle Detail Popup** | For **Mode A (Public Transport)**: route, distance, ETA, driver info. **Mode B markers open the Private Transport Request screen instead** (item 8) |
| **Active Trip** | Map with polyline, trip timer, distance, ETA, "End Journey" / SOS button. **Tapping Call opens the call-type chooser** (Free in-app VoIP vs Normal direct cellular call, SCR-PA-015a / US-24.3 as amended by US-26.2) |
| **Call-type Chooser** (SCR-PA-015a) ★ NEW | Bottom sheet shown when **Call** is tapped (active trip, history, trip details): **Free call** (in-app VoIP) or **Normal call** (**direct dial to the counterparty's real number** — masking removed, US-26.2). Remembers last choice |
| **Schedule Ride** (SCR-PA-013) | **Mandatory destination picker ("select the location to go")** + editable pickup (defaults to current location); date + time; reminders; Confirm disabled until a destination is set (US-24.2) |
| **Trip Summary** | Route on map, distance, fare breakdown, payment method selection, rating prompt |
| **Payment (Pay fare)** | Fare total; **"Scan driver's QR" camera option** (printed/displayed/sticker) — no MageRide QR rendered in the centre (item 18); plus **LankaQR "Pay" deep-link** / OnePay; payment confirmation |
| **Trip/Schedule History** | Past trips, upcoming scheduled rides, and past package deliveries. **Each completed-trip card shows the driver's name + mobile number with a Call action** (opens the call-type chooser) so the rider can reach the driver after a trip, e.g. for a lost item (US-24.4) |
| **Menu / Nav Drawer** ★ NEW | Links to Private transport (Mode B request), My subscriptions, Saved addresses, Profile & settings (item 9) |
| **Private Transport Request** | Enter / pre-filled Vehicle ID to request Mode B access (opened from a Mode B marker or the nav drawer) |
| **My Private Subscriptions** ★ NEW | Per-vehicle cards showing **Paid (amount/mo + next-due) or Free**, **💳 Pay → payment screen**, **🧾 → payment history**, and a **compact unsubscribe icon** (items 16, 17) |
| **Subscription Payment** ★ NEW | Choose LankaQR deep link / LankaQR scan / OnePay / online transfer (attach screenshot); routed to the fleet owner (item 16e) |
| **Subscription Payment History** ★ NEW | Per-subscriber statement: month, date, method, amount, status (Paid / Pending verification) (item 16h) |
| **Profile & Settings** | User ID, language, notification preferences; **Save Home & Work (select on OSM map)**; **Saved Addresses**; **Default Payment Method** (Cash default / LankaQR / OnePay); **Help & Support** |
| **Edit Profile** | Name, photo, notification preferences, SOS contacts. **No language selector here** (item 10) |
| **Saved Addresses** ★ NEW | List of saved addresses (Home, Work, custom labels); add via OSM map pin → **ModalBottomSheet** capturing Address Line 1/2/3 + **Label** (item 7); edit/delete |
| **In-App Support** | FAQ section, raise support ticket, track ticket status |

### MageRide Driver App

| Screen | Key Elements |
|---|---|
| **Driver Dashboard** | Active status, today's earnings, today's platform fee (paid/amount, vehicle-specific rate), wallet balance, Driver Level indicator, vehicle list |
| **Daily Fee Status** | Vehicle-specific daily rate (Bus Free / Motorbike Rs 50 / Three-wheeler Rs 100 / Flex Rs 150 / Sedan Rs 200 / Mini Van Rs 250 / Van Rs 300); today's fee status (paid/unpaid); first trip free indicator |
| **Mode A/B Home — Start/End Journey** | **Home dashboard when the active vehicle is Mode A (bus) or Mode B (private)** (SCR-DA-011): route card with vehicle type & number below it; only **Start Journey** / **End Journey** buttons; auto-start banner when a **GPS device started on ignition** (dashboard can override) (item 8, 11) |
| **Standby Toggle (Mode C — Standby On-Demand)** | Go Online/Offline switch for dispatch (triggers daily fee on 2nd trip activation). **"Directional Travel"** entry point with status chip (active destination, time remaining, remaining daily uses) |
| **Directional Travel (Mode C)** ★ NEW | Set destination (address search / map pin / "Home"), shows remaining daily uses and max duration; map preview with directional/heading marker; "Set Direction" and "Turn Off" actions; persistent active-state banner showing destination, time remaining, and remaining uses |
| **Incoming Request (Mode C)** | Passenger pickup/drop-off, distance to pickup, vehicle category, payment method, estimated fare, Accept/Reject timer (15s). **For proxy bookings:** shows actual rider's name + "Third-party booking" badge. **For package deliveries:** shows "Package Delivery" badge, package size, item description. **When Directional Travel is active:** a small "Directional" badge indicates the hire matched the driver's set direction |
| **Delivery (3 bottom sheets)** ★ NEW | **(1) Review & start** (SCR-DA-016a): pickup/drop distances, payment method, sender & recipient numbers each with a **Call** button (mobile voice call), **Start delivery** / **Cancel→re-dispatch**. **(2) Pickup** (SCR-DA-016b): map pickup pin, **Call sender**, SOS, **Pickup OTP**. **(3) Complete** (SCR-DA-016c): **Delivery OTP**, photo proof, sender & recipient call buttons, **"Delivery completed"** (replaces "Cash received (COD)") (item 9) |
| **Job Board (Mode C)** | All future scheduled rides within 30 km radius — **post intent only** (no direct accept; the ride is offered at T-30 min on the dispatch screen) |
| **Scheduled Rides** | List of upcoming scheduled rides available to accept |
| **Active Session/Trip** | Map, navigation to passenger, session timer, live earnings, SOS |
| **Earnings Dashboard** | Today's earnings, per-trip breakdown, payment method stats, daily fee deducted |
| **Vehicle Management** | Registered vehicles with per-vehicle status **Incomplete** (≥1 onboarding step saved → Resume next step) / **Approved** (all 4 steps Verified; only these go live); ＋ / add-vehicle starts a **new** onboarding when the current vehicle is Approved (items 5, 6) |
| **Register Vehicle** | Reg no, type, mobile, driver photo, vehicle photo, doc upload (license, registration, **vehicle insurance certificate**, **revenue license**), AI extraction (incl. licence **NIC no + allowed vehicle types**, insurance policy no./insurer/expiry, revenue-licence expiry); **doubtful / manual / plate-mismatch fields → step Pending → admin verify** (items 2–5). **Every 📷 capture slot opens the camera document-scanner (SCR-DA-005) with a draggable-corner crop frame** so the whole document fills the frame before upload (US-24.6) |
| **Document Capture (camera + drag-crop)** (SCR-DA-005) ★ NEW | Shared camera document-scanner reached from every onboarding capture slot (SCR-DA-003a/004a/004b/004c). Live camera with an **adjustable crop quadrilateral — drag the four corner handles** to fit the full document; auto edge-detect proposes the quad; Retake / Use photo; perspective-corrected image is uploaded and sent to Gemini Flash (US-24.6) |
| **Sharing Management (Mode B)** | **Per-vehicle**, with a **full-device-width vehicle selector** at the top (the old "Showing sharing for … temporarily assigned …" caption box is **removed**, 2026-06-25 item 12): active Mode B shares + **incoming subscription requests accept/reject scoped to the selected vehicle**; supports multi-vehicle drivers and temporarily-hired Mode A/B vehicles (items 12, 15) |
| **Menu / Nav Drawer (Driver)** ★ NEW | Links to My Vehicles, GPS Tracker Pairing, Sharing Management (Mode B), Driver Profile, Ride History + Rate Passenger, Support + Fee Refund, Notifications / Alerts (item 14) |
| **Wallet & Fee Status** | Wallet balance, today's fee status (paid/unpaid, amount, vehicle-specific rate), full transaction & daily-fee history, in-app "Top Up Wallet" (card/OnePay/LankaQR + bulk credit vouchers), payment-confirmation/receipt download, PDF/CSV statement export, driver-set low-balance alert threshold, request a credit transfer (enter another driver's **Driver ID**; QR scanning removed). **No web portal — everything in-app.** |
| **Credit Transfer** ★ | Available to any driver holding wallet credit: incoming credit-transfer requests (push) with approve/reject, and **send credit directly by Driver ID** — exact value, no commission |
| **Pending Credit Requests** ★ | List of incoming driver credit-transfer requests — requesting driver name, vehicle, amount, approve/reject actions |
| **Send Credit** ★ | Enter recipient driver's **Driver ID**, enter amount, confirm — shows exact value transferred (no commission) |
| **Transfer History** ★ | Credit transfers sent & received, per-transaction breakdown, date-range filter, PDF/CSV export |
| **Profile & Settings (Driver)** | Name, profile photo, language, notification preferences, **emergency contact (name + phone, pick from contacts or enter manually)** used for SOS alerts, log out |
| **VoIP Call** | In-app voice call with passenger/driver — no phone numbers exposed |
| **In-App Support** | FAQ, raise support ticket, track ticket status |

### Admin Portal (Web) — `admin.mageride.lk` (back-office, all internal roles)

> **No driver-facing screens.** Drivers never log in here. The Admin Portal performs **all MageRide back-office functions** and is accessed only by the six internal roles via **Password or Google Sign-In** (**no OTP / MFA step**, US-24.5), with **role-scoped menus** — each role sees only what §2.3 permits.

| Screen / Module | Key Elements | Primary Role(s) |
|---|---|---|
| **Login** (SCR-AP-001) | Password or Google Sign-In; **no OTP / MFA / authenticator step** — sign-in completes straight to the dashboard (US-24.5). No driver/Phone-OTP login | All internal |
| **Dashboard** (SCR-AP-002) | Role-scoped KPIs + alerts; **statistics filter by Today / This week / This month / custom date range** with vs-previous-period deltas and CSV export (US-24.7) | All internal (scoped) |
| **Verification — Queues** (SCR-AP-003) | **Split list screen** with three queues: **driving-licence pending · vehicle-registration pending · fleet-org approval**; search + status filter; row → detail (US-24.8) | Verification Officer, Admin, Super Admin |
| **Verification — Detail** (SCR-AP-003a) | Selected entry: **attached-document thumbnails grid** + AI-extracted fields with **Confirm / Edit & confirm** for **doubtful, driver-entered, or plate↔reg-no-mismatch** elements (incl. licence **NIC no + allowed vehicle types**); **per-step Verified/Pending breakdown**; Approve unlocks only when all confirmed (US-2.4a/2.10a/US-24.8) | Verification Officer, Admin, Super Admin |
| **Document Viewer** (SCR-AP-003b) | **Full-size document window** opened by tapping any thumbnail — zoom / rotate / prev-next paging; signed-URL fetch; PII view audited (US-24.8) | Verification Officer, Admin, Support |
| **Fleet-org Approval Detail** (SCR-AP-003c) | Org KYC fields + **KYC-document thumbnails** (→ full-size viewer); approve/reject with reason (US-13.A7/US-24.8) | Verification Officer, Admin, Super Admin |
| **Passenger Directory** (SCR-AP-010) → **Detail** (SCR-AP-011) | **Search a passenger** by name / mobile / passenger ID / email; detail = profile + tabbed **Trips / Payments / Packages / Disputes** (US-24.9) | Support/CSR, Admin, Auditor |
| **Driver Directory** (SCR-AP-012) → **Detail** (SCR-AP-013) | **Search verified drivers** by name / mobile / driver ID / NIC / vehicle reg no / Driver Level / status; detail = profile/wallet/level + linked vehicles + tabbed **Trips / Wallet ledger / Daily fee / Credit transfers / Reports** (US-24.10) | Support/CSR, Admin, Finance, Auditor |
| **Vehicle Directory** (SCR-AP-014) → **Detail** (SCR-AP-015) | **Search registered vehicles** by reg no / vehicle ID / type / mode / owner mobile / fleet org / status; detail = registration/insurance/revenue-licence/tracker info + document thumbnails (→ full-size viewer) + tabbed **Trips / Earnings / Daily fee / Reports** (US-24.11) | Support/CSR, Admin, Finance, Auditor |
| **GTFS Dataset Manager** (SCR-AP-016) | **Upload the full GTFS zip** → validation with downloadable row-level error report → preview (counts, feed version, service window) → **atomic Activate**; version history with **one-click rollback** + original-zip download (US-28.1…28.3) | Admin, Super Admin |
| **Moderation** | Suspend/ban drivers & vehicles, review reports, delisting | Admin, Super Admin |
| **Support Tickets & Disputes** | Ticket queue, read-only trip/user lookup, dispute investigation, refund requests, block-on-reports | Support/CSR, Admin, Super Admin |
| **Payment Reconciliation** | Payment-gateway (OnePay/LankaQR) settlement reconciliation, exceptions, and manual adjustments | Finance, Admin, Super Admin |
| **Transactions (All)** | All wallet top-ups, daily fee deductions, driver-to-driver credit transfers — date filter, receipt download, PDF/CSV export | Finance, Admin, Auditor |
| **Payouts & Settlements** | Driver/fleet settlements, reconciliation, wallet reversals/adjustments | Finance, Super Admin |
| **Configuration** | Fare tariffs, daily-fee rates, bulk-voucher discount tiers, Driver-Level params, subscription pricing, feature flags | Admin, Finance (rates), Super Admin |
| **Voucher Discount Config** | Configure bulk-voucher purchase-discount tiers (per-tier %, denominations); review driver-to-driver credit transfers | Admin, Super Admin |
| **User & Role Management (RBAC)** | Provision internal users, assign roles, define permission sets, suspend/revoke accounts | Super Admin |
| **Audit Logs** | Immutable admin-action & permission-change trail (read-only) | Auditor, Super Admin |
| **Analytics & Reporting** | Platform-wide dashboards; settlement/financial reports; export | Admin, Super Admin, Finance (financial), Auditor (read) |
| **Announcements** | Broadcast to all users or segments | Admin, Super Admin |

### Fleet Portal (Web) — `fleet.mageride.lk`

| Screen | Key Elements |
|---|---|
| **Login / Sign-Up** | **Email + Password**, **Google Sign-In**, and **Apple Sign-In** buttons; email verification, forgot-password / reset flow; link/unlink Google & Apple identities |
| **Organisation Setup** | Organisation profile (name, type, contact), team-member invites with roles (Owner / Manager / Viewer), language selection; links to Bank & Payout Details |
| **Bank & Payout Details (SCR-FP-002a)** ★ NEW | Owner-only: **bank, branch, account number, account holder name**; uploads — **latest bank statement or passbook first page** + **bank-app LankaQR code image**; Pending verification → Verified (Verification Officer) → Rejected + reason; verified profile feeds the passenger Mode B pay sheet (LankaQR + transfer details); Paid vehicles gated on Verified (items 1, US-27.1/27.2) |
| **Fleet Dashboard** | Fleet KPIs (active vehicles, online/stale/offline counts), today's trips, alerts feed, fleet wallet balance + next consolidated invoice |
| **Vehicle Onboarding** | Add single vehicle or **bulk CSV upload**; per-row validation + downloadable error report; **named document slots — registration copy (CR book), insurance certificate, revenue license, route permit (Mode A required)** — AI document extraction (reg-no match, expiries, permit) with per-document Verified/Pending/Missing status gating approval (US-27.3); approval status (Pending / Approved / Rejected) per vehicle. **Mode B vehicles require a Service payment setting — Free or Paid — + default monthly fare for Paid** (item 16b; renamed by US-27.4); Paid gated on Verified payout profile |
| **Vehicle Management** | Vehicle list, deactivate/remove, ST-901 / hardware tracker binding (IMEI/MAC), publish-cadence profile |
| **Driver Assignment** | Assign/revoke drivers (by User ID / phone) to one or more vehicles; assignment history |
| **Live Fleet Map** | Single map showing all org vehicles (mobile- or tracker-sourced), org-scoped via row-level security, fleet-health overlay |
| **Trip History & Analytics** | Per-vehicle trips, distance, active hours, utilisation, idle time; date-range filters; CSV/PDF export |
| **Scheduling** | Add/change scheduled rides per vehicle; schedule-not-started alarm config (rings in assigned driver's Android & iOS Driver App) |
| **Fleet Billing & Wallet** | Monthly per-Mode-B-vehicle invoice (consolidated, with per-vehicle breakdown; Mode A free); fleet wallet top-up (Card / OnePay / LankaQR); receipts/invoices |
| **Mode B Subscriptions & Requests** ★ NEW | Per Mode B vehicle: **incoming passenger request queue (Accept/Reject)** (item 15); subscriber roster with **per-subscriber monthly fare (editable), billing cycle (1st-of-month or join anniversary), this-month status, Mark cash received, Confirm transfer**, and **muted-until-deleted unsubscribed rows** (items 16, 17) |
| **Subscriber Payments (per vehicle)** ★ NEW | Per-subscriber payment ledger (LankaQR / OnePay / online transfer / cash) with status, summary KPIs (collected / pending verify / cash due), CSV export (item 16i) |
| **Alerts & Notifications** | Device-down alerts, schedule alarms, and (Phase 3) geofence / route-deviation alerts; email/SMS preferences |

### Passenger Web subview (Web) — `passenger.mageride.lk` (a no-login view of the Passenger App, not a separate surface)

> **Link-accessed, no login.** Opened by a proxy-booking rider or an **unregistered package recipient** via the secure, scoped, expiring token in the SMS link (US-8.22 / US-11.9 / **US-20.5**). Scoped to a single ride/package; no app install or account required.

| Screen | Key Elements |
|---|---|
| **SCR-WT-001 · Landing / token gate** ★ | Validates the SMS-link token, then routes by scope; expired/invalid → SCR-WT-006; no data rendered before validation *(US-25.1)* |
| **SCR-WT-002 · Package Tracking (Recipient, no-login)** ★ | Opened from the SMS sent **when the driver confirms pickup** to a recipient **without the app** (item 11): live map, status (Pickup → Picked → In transit → Delivered), **Delivery OTP**, driver/vehicle details, **Call driver (`tel:` link — US-26.3)** |
| **SCR-WT-003 · Confirm Pickup (unregistered proxy rider)** ★ | 5-min TTL countdown, adjustable map pin, **Share / Decline** (decline sends no GPS); feeds the same location-request state machine as the in-app confirm *(US-25.3)* |
| **SCR-WT-004 · Ride Tracking (proxy rider)** | Live map en route + trip progress polyline, ETA, driver name/photo, vehicle type & registration number, **Start OTP**, "Third-party booking" context, fare summary (paid by booker, or cash-due notice), **Call driver (`tel:` link — US-26.3)**, **SOS** |
| **SCR-WT-005 · Delivered / Trip Summary** | Outcome — OTP-verified / photo-proof (recipient absent) / COD collected / Disputed (>24 h); final route, distance, fare/payment; **receipt download**; link expires after completion |
| **SCR-WT-006 · Expired / invalid link** | Safe dead-end with zero ride data + app-download link *(US-25.1)* |

---

## 7. Priority Matrix

| Priority | Label | Meaning | Epics |
|---|---|---|---|
| **P0** | Must Have | Required for launch | Epics 1, 2, 4, 5, 6A, 7, 8, 9 (core), 9A (core), 11, **13 (Fleet Portal access, org & vehicle onboarding)**, 15 (core), 17, **21 (RBAC core)** |
| **P1** | Should Have | Expected within 3 months of launch | Epics 8 (proxy booking), 10, 12 (core), **13 (driver assignment, live fleet map, analytics, scheduling, ST-901 auto-sessions, consolidated billing)**, 14 (core), 15 (caching), 16, 18, 19, **21 (custom permission sets)**, **22 (passenger settings)** |
| **P2** | Nice to Have | Planned for Phase 2 | Epics 3, 6 (ad-hoc), 18 (driver ratings), **20 (package delivery)** |
| **P3** | Future | Phase 3+ | **Epic 13 (fleet geofence / route-deviation alerts — US-13.5)**, route analytics, road-network ETA, GTFS-RT |

> **Phase note:** Epic 13 (Fleet Operator Features) is delivered in **Phase 1** via the **Fleet Portal** (`fleet.mageride.lk`), except the geofence / route-deviation alerts (US-13.5), which remain Phase 3.

---

## 8. Glossary

| Term | Definition |
|---|---|
| **MageRide** | The platform brand name. Has **four surfaces**: MageRide Passenger App, MageRide Driver App, the Admin Portal (`admin.mageride.lk`), and the Fleet Portal (`fleet.mageride.lk`). `passenger.mageride.lk` is a no-login **web subview of the Passenger App**, not a separate surface. |
| **Passenger Web subview** (`passenger.mageride.lk`) | A lightweight, link-accessed **no-login web view of the Passenger App** (not a separate surface) that lets a **proxy-booking rider** (the person someone else booked for) track and complete the rest of a ride in the browser — live tracking, ETA, driver/vehicle details, tap-to-call driver contact (`tel:` link, US-26.3), SOS, trip summary — **without installing the MageRide app or logging in**. Reached via a **secure, ride-scoped, expiring token** in the SMS sent when the driver accepts (US-8.22). |
| **Role** | One of the nine canonical access roles (§2.1): Driver, Passenger, Fleet Owner, Admin, Super Admin, Verification Officer, Support/CSR, Finance Officer, Auditor. Determines which features a user may access via the Feature Permission Matrix. |
| **RBAC (Role-Based Access Control)** | The deny-by-default, server-enforced model (Epic 21) that grants each user only the features permitted to their role(s) per the §2.3 Feature Permission Matrix. Effective permissions are the union of a user's roles. |
| **Feature Permission Matrix** | The §2.3 table mapping each role to allowed actions (Full / Configure / Read-only / Own-scope / None) per feature area; enforced identically in the API and the UI. |
| **Admin Portal** | The back-office web application at **`admin.mageride.lk`** where **all MageRide back-office functions** are performed; the **single login surface for the six internal roles** (Admin, Super Admin, Verification Officer, Support/CSR, Finance, Auditor). Authentication is **Password or Google Sign-In** (**no MFA step**, US-24.5), with role-scoped menus. |
| **Super Admin** | Highest-privilege role: all Admin powers plus user & role management, permission/feature-flag configuration, internal-account provisioning, and system settings. The only role that can manage RBAC. |
| **Verification Officer** | Driver Onboarding / Verification Officer — internal role that reviews driver documents, licenses, vehicle registration, and background checks and approves/rejects registrations. |
| **Support Agent / CSR** | Internal role handling passenger/driver complaints, trip disputes, and refund requests via the support ticket queue. |
| **Finance / Payments Officer** | Internal role managing payouts, commissions, settlements, reconciliation, wallet adjustments/reversals, and fee and voucher-discount configuration. |
| **Auditor** | Internal read-only role with access to logs, transactions, and audit trails for compliance; cannot mutate data. |
| **Fleet Portal** | A responsive (mobile-first) web application at **`fleet.mageride.lk`** used by Fleet Owners to register an organisation, onboard multiple vehicles, assign/revoke drivers, bind ST-901 / hardware trackers, schedule rides, view the live fleet map and per-vehicle analytics, and pay a consolidated monthly Mode B fee. Authenticated via **Email + Password, Google Sign-In, or Apple Sign-In**. Delivered in **Phase 1** (Epic 13). |
| **Fleet Owner** | The role (also referred to as **fleet operator** in Epic 13 stories) for an organisation (state/private bus company, school-transport operator, book-hire fleet) that operates **Mode A and/or Mode B** vehicles and manages them through the Fleet Portal. Pays a **monthly fee per Mode B vehicle** from a fleet wallet (Mode A free; **no Mode C** — Mode C daily fees are paid from the individual driver's wallet). Billed as one consolidated invoice with a per-vehicle breakdown. Provisions org-scoped **Manager/Viewer** sub-users. Scoped to its own organisation. Org must be **verified/approved** before onboarding (US-13.A7). |
| **Assigned Driver (Fleet)** | A MageRide Driver App user who can see and start sessions on vehicles assigned to them by a fleet operator, even though they did not register those vehicles. |
| **Fleet Wallet** | A prepaid balance held by a fleet operator, used to pay the **monthly per-Mode-B-vehicle** fee (Mode A free; no Mode C); topped up via Card / OnePay / LankaQR in the Fleet Portal. |
| **Vehicle ID** | Unique identifier generated by the system when a vehicle is registered |
| **User ID** | Unique identifier generated for each registered user (shown in profile) |
| **Session** | An active period during which a vehicle's position is being tracked and broadcast |
| **Sharing Grant** | A permission record allowing a specific user to track a specific vehicle, optionally time-bounded |
| **Private Transporter** | A Mode B vehicle owner who controls visibility — only users with accepted access requests can see them. Monthly charge ~Rs 300. |
| **Geocell** | An H3 hexagonal cell (~0.46 km²) used to partition the map for efficient position broadcasting |
| **Fare Tariff** | Admin-configurable pricing structure (base fare + per-km rate) per vehicle type |
| **Plausibility Filter** | Server-side validation rejecting impossible GPS positions (teleportation, exceeding max speed) |
| **Zero-Commission Model** | MageRide's revenue model that mirrors **Namma Yatri (India)**: drivers keep 100% of passenger fares; MageRide charges Mode C drivers a flat daily platform fee (first trip free) per vehicle type, and Mode B drivers a monthly fee. No per-trip fees, no commission |
| **Daily Platform Fee** | A flat fee deducted from the Mode C driver's wallet before their 2nd trip each day. First trip is always free. Vehicle-type dependent: Motorbike = Rs 50, Three-wheeler = Rs 100, Flex = Rs 150, Sedan = Rs 200, Mini Van = Rs 250, Van = Rs 300. Public buses (Mode A) = Free. No charge on off days. |
| **Namma Yatri Methodology** | The zero-commission, daily-fee-only driver payment model originated by Namma Yatri (Bengaluru, India) and adopted by MageRide in the Sri Lankan context |
| **Reseller (informal)** | **Not a separate role, account, or enabled capability.** Simply any **driver who has purchased bulk credit** (at the bulk-voucher purchase discount) and transfers it to other drivers in the MageRide Driver App using their existing driver identity and wallet. There is **no per-transfer commission** — the reselling driver's margin is the bulk-voucher purchase discount. |
| **Wallet Balance** | A prepaid monetary balance maintained by drivers, topped up **entirely in the Driver App** via credit/debit card, OnePay, LankaQR, or bulk credit vouchers |
| **Credit Transfer (driver-to-driver)** | A wallet top-up where one driver transfers credit to another driver's wallet by **Driver ID**; the **exact amount** is debited from the sender and credited to the recipient (**no commission**); requested, approved, and initiated entirely in the Driver App |
| **Bulk Credit Voucher** | A prepaid in-app credit purchase (Rs 1,000 / 2,000 / 3,000 / 5,000 / 10,000) bought at a **per-tier purchase discount configured in the database**; the discount applies only at purchase and the credit lands in the buyer's own wallet immediately (no redeem code) |
| **Single Active Device** | The rule that a passenger/driver account has only **one active device session** at a time; logging in on a new device revokes the previous device's tokens server-side and force-logs-it-out (US-1.12, NFR-50). |
| **Eager Fetch (on login)** | The small, bounded set of essential data loaded immediately at login: profile, saved addresses, payment-method metadata, **active/ongoing trip**, driver shift/online status & today's earnings summary, and app config/feature flags (US-1.15). |
| **Lazy Fetch (per screen)** | Large/unbounded data loaded only when its screen opens: trip history (paginated), earnings breakdowns by period, and receipts/invoices on tap (US-1.16). |
| **Driver Level System** | Mode C standby drivers start at Level 3. Levels change based on ratings, no-shows, and reports. Level 1 drivers lose Job Board access. |
| **Directional Travel (Destination Filter)** | A Mode C standby-driver convenience (modelled on PickMe's driver "Directional Travel" feature) that restricts incoming hires to those heading in a driver-chosen direction, for a limited duration (default 2 h) and a limited number of times per day (default 2). Direction match = ride pickup→drop-off bearing aligns with the driver→destination bearing within tolerance, the pickup is within a detour limit (default 2 km), and the drop-off is closer to the destination than the pickup. It only filters which eligible offers reach the driver; it never alters fares, fees, or eligibility/safety rules. All parameters are admin-configurable. |
| **Job Board** | A shared list of all future scheduled rides visible to Mode C drivers within 30 km. Drivers **post intent only**; at T-30 min each ride is dispatched (by proximity and Driver Level) as an offer on the dispatch screen, where it is accepted. |
| **VoIP Call** | In-app voice call feature allowing passengers and drivers to communicate without exposing personal phone numbers |
| **Proxy Booking** | A ride booked by one person (the booker) on behalf of another person (the rider) who will be picked up. The booker may not be at the pickup location. Payment is charged to the booker or paid in cash by the rider. On driver acceptance, the rider receives an **SMS link to the Passenger Web Portal** (`passenger.mageride.lk`) to track/complete the ride without the app (US-8.22) |
| **FCM Location Request** | An in-app push notification sent via Firebase Cloud Messaging from the booker to the rider, requesting the rider to share their live GPS position as the pickup point. The rider confirms via the MageRide Passenger App |
| **Package Delivery** | A Mode C service extension where a driver picks up a package from one location and delivers it to a recipient at another location. Uses the same fare tariff and dispatch logic as passenger rides. Verified via Pickup OTP and Delivery OTP |
| **Pickup OTP** | A 4-digit one-time password shown to the sender, entered by the driver at package pickup to confirm collection |
| **Delivery OTP** | A 4-digit one-time password sent to the recipient via FCM push, entered by the driver at delivery to confirm handover |
| **Cash on Delivery (COD)** | A payment method for package delivery where the recipient pays the driver in cash upon receiving the package. Selected by the sender at booking time |

---

*End of User Requirements Document v1.0*
