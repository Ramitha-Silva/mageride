# MageRide — Wireframe Screen Coverage Matrix

Every `SCR-*` ID that appears in the seven wireframe HTML files, mapped to the one
component that owns it. The wireframes are the **team-reviewed and approved structural /**
**functional baseline** — no screen may be silently dropped.

**Enumeration command (step 1, run from the repo root):**

```
grep -hoE 'SCR-[A-Z]+-[0-9]+[a-z]?' \
  specs/wireframes/driver_android.html specs/wireframes/driver_ios.html \
  specs/wireframes/passenger_android.html specs/wireframes/passenger_ios.html \
  specs/wireframes/web_admin.html specs/wireframes/web_fleet.html \
  specs/wireframes/web_passenger.html | sort -u
```

**Result: 202 wireframe IDs found / 202 mapped to a component — EQUAL ✅**

`index.html` and non-HTML files in `specs/wireframes/` are excluded per the C0 brief.

## Totals by family

| Family | Surface | Screens | Wireframe file | Components |
|--------|---------|---------|----------------|------------|
| DA | Driver Android | 41 | `specs/wireframes/driver_android.html` | C068–C075 (8) |
| DI | Driver iOS | 41 | `specs/wireframes/driver_ios.html` | C086–C093 (8) |
| PA | Passenger Android | 41 | `specs/wireframes/passenger_android.html` | C077–C084 (8) |
| PI | Passenger iOS | 41 | `specs/wireframes/passenger_ios.html` | C095–C102 (8) |
| AP | Admin Portal | 19 | `specs/wireframes/web_admin.html` | C105–C110 (6) |
| FP | Fleet Portal | 13 | `specs/wireframes/web_fleet.html` | C112–C116 (5) |
| WT | Passenger Web subview | 6 | `specs/wireframes/web_passenger.html` | C117 (1) |
| — | **Total** | **202** | 7 files | — |

## Matrix

| SCR ID | Wireframe file | Component ID | Notes |
|--------|----------------|--------------|-------|
| SCR-DA-001 | `specs/wireframes/driver_android.html` | C068 | splash |
| SCR-DA-002 | `specs/wireframes/driver_android.html` | C068 | language / city |
| SCR-DA-003 | `specs/wireframes/driver_android.html` | C068 | phone + OTP |
| SCR-DA-003a | `specs/wireframes/driver_android.html` | C068 | profile setup |
| SCR-DA-004 | `specs/wireframes/driver_android.html` | C069 | vehicle onboarding · Step 1/4 |
| SCR-DA-004a | `specs/wireframes/driver_android.html` | C069 | Step 2/4 · insurance |
| SCR-DA-004b | `specs/wireframes/driver_android.html` | C069 | Step 3/4 · revenue license |
| SCR-DA-004c | `specs/wireframes/driver_android.html` | C069 | Step 4/4 · vehicle photos |
| SCR-DA-005 | `specs/wireframes/driver_android.html` | C069 | document capture (camera + drag-crop) |
| SCR-DA-006 | `specs/wireframes/driver_android.html` | C069 | vehicle onboarding status |
| SCR-DA-007 | `specs/wireframes/driver_android.html` | C068 | permissions |
| SCR-DA-010 | `specs/wireframes/driver_android.html` | C070 | dashboard (PRIMARY · Mode C) |
| SCR-DA-011 | `specs/wireframes/driver_android.html` | C070 | Mode A/B dashboard — Start/End Journey |
| SCR-DA-013 | `specs/wireframes/driver_android.html` | C070 | directional travel |
| SCR-DA-014 | `specs/wireframes/driver_android.html` | C070 | incoming dispatch (PRIMARY · 15s) |
| SCR-DA-015 | `specs/wireframes/driver_android.html` | C070 | active ride / trip |
| SCR-DA-016a | `specs/wireframes/driver_android.html` | C071 | delivery · review & start (sheet 1/3) |
| SCR-DA-016b | `specs/wireframes/driver_android.html` | C071 | delivery · pickup & OTP (sheet 2/3) |
| SCR-DA-016c | `specs/wireframes/driver_android.html` | C071 | delivery · complete (sheet 3/3) |
| SCR-DA-017 | `specs/wireframes/driver_android.html` | C072 | job board |
| SCR-DA-018 | `specs/wireframes/driver_android.html` | C072 | scheduled rides |
| SCR-DA-019 | `specs/wireframes/driver_android.html` | C072 | driver level & stats |
| SCR-DA-020 | `specs/wireframes/driver_android.html` | C072 | earnings dashboard |
| SCR-DA-021 | `specs/wireframes/driver_android.html` | C073 | wallet & fee (PRIMARY) |
| SCR-DA-022 | `specs/wireframes/driver_android.html` | C073 | top up wallet |
| SCR-DA-023 | `specs/wireframes/driver_android.html` | C073 | request credit (driver ID) |
| SCR-DA-024 | `specs/wireframes/driver_android.html` | C073 | credit transfer + requests |
| SCR-DA-025 | `specs/wireframes/driver_android.html` | C073 | payment / fee history |
| SCR-DA-026 | `specs/wireframes/driver_android.html` | C069 | vehicle management |
| SCR-DA-026a | `specs/wireframes/driver_android.html` | C069 | no vehicles · onboard Mode C |
| SCR-DA-027 | `specs/wireframes/driver_android.html` | C074 | GPS tracker pairing |
| SCR-DA-028 | `specs/wireframes/driver_android.html` | C074 | sharing management (Mode B); also cross-referenced in `web_fleet.html` |
| SCR-DA-029 | `specs/wireframes/driver_android.html` | C074 | driver profile |
| SCR-DA-030 | `specs/wireframes/driver_android.html` | C074 | ride history + rate passenger |
| SCR-DA-031 | `specs/wireframes/driver_android.html` | C075 | VoIP call |
| SCR-DA-032 | `specs/wireframes/driver_android.html` | C075 | SOS (driver) |
| SCR-DA-033 | `specs/wireframes/driver_android.html` | C075 | support + fee refund |
| SCR-DA-033a | `specs/wireframes/driver_android.html` | C075 | raise ticket (modal sheet) |
| SCR-DA-034 | `specs/wireframes/driver_android.html` | C075 | notifications |
| SCR-DA-035 | `specs/wireframes/driver_android.html` | C075 | offline · app update |
| SCR-DA-036 | `specs/wireframes/driver_android.html` | C070 | menu (nav drawer) |
| SCR-DI-001 | `specs/wireframes/driver_ios.html` | C086 | splash |
| SCR-DI-002 | `specs/wireframes/driver_ios.html` | C086 | language / city |
| SCR-DI-003 | `specs/wireframes/driver_ios.html` | C086 | phone + OTP |
| SCR-DI-003a | `specs/wireframes/driver_ios.html` | C086 | profile setup |
| SCR-DI-004 | `specs/wireframes/driver_ios.html` | C087 | vehicle onboarding · Step 1/4 |
| SCR-DI-004a | `specs/wireframes/driver_ios.html` | C087 | Step 2/4 · insurance |
| SCR-DI-004b | `specs/wireframes/driver_ios.html` | C087 | Step 3/4 · revenue license |
| SCR-DI-004c | `specs/wireframes/driver_ios.html` | C087 | Step 4/4 · vehicle photos |
| SCR-DI-005 | `specs/wireframes/driver_ios.html` | C087 | document capture (camera + drag-crop) |
| SCR-DI-006 | `specs/wireframes/driver_ios.html` | C087 | vehicle onboarding status |
| SCR-DI-007 | `specs/wireframes/driver_ios.html` | C086 | permissions |
| SCR-DI-010 | `specs/wireframes/driver_ios.html` | C088 | dashboard (PRIMARY · Mode C) |
| SCR-DI-011 | `specs/wireframes/driver_ios.html` | C088 | Mode A/B dashboard — Start/End Journey |
| SCR-DI-013 | `specs/wireframes/driver_ios.html` | C088 | directional travel |
| SCR-DI-014 | `specs/wireframes/driver_ios.html` | C088 | incoming dispatch (PRIMARY · 15s) |
| SCR-DI-015 | `specs/wireframes/driver_ios.html` | C088 | active ride / trip |
| SCR-DI-016a | `specs/wireframes/driver_ios.html` | C089 | delivery · review & start (sheet 1/3) |
| SCR-DI-016b | `specs/wireframes/driver_ios.html` | C089 | delivery · pickup & OTP (sheet 2/3) |
| SCR-DI-016c | `specs/wireframes/driver_ios.html` | C089 | delivery · complete (sheet 3/3) |
| SCR-DI-017 | `specs/wireframes/driver_ios.html` | C090 | job board |
| SCR-DI-018 | `specs/wireframes/driver_ios.html` | C090 | scheduled rides |
| SCR-DI-019 | `specs/wireframes/driver_ios.html` | C090 | driver level & stats |
| SCR-DI-020 | `specs/wireframes/driver_ios.html` | C090 | earnings |
| SCR-DI-021 | `specs/wireframes/driver_ios.html` | C091 | wallet & fee (PRIMARY) |
| SCR-DI-022 | `specs/wireframes/driver_ios.html` | C091 | top up wallet |
| SCR-DI-023 | `specs/wireframes/driver_ios.html` | C091 | request credit (driver ID) |
| SCR-DI-024 | `specs/wireframes/driver_ios.html` | C091 | credit transfer + requests |
| SCR-DI-025 | `specs/wireframes/driver_ios.html` | C091 | payment / fee history |
| SCR-DI-026 | `specs/wireframes/driver_ios.html` | C087 | vehicle management |
| SCR-DI-026a | `specs/wireframes/driver_ios.html` | C087 | no vehicles · onboard Mode C |
| SCR-DI-027 | `specs/wireframes/driver_ios.html` | C092 | GPS tracker pairing |
| SCR-DI-028 | `specs/wireframes/driver_ios.html` | C092 | sharing management (Mode B) |
| SCR-DI-029 | `specs/wireframes/driver_ios.html` | C092 | driver profile |
| SCR-DI-030 | `specs/wireframes/driver_ios.html` | C092 | ride history + rate passenger |
| SCR-DI-031 | `specs/wireframes/driver_ios.html` | C093 | VoIP call (CallKit) |
| SCR-DI-032 | `specs/wireframes/driver_ios.html` | C093 | SOS (driver) |
| SCR-DI-033 | `specs/wireframes/driver_ios.html` | C093 | support + fee refund |
| SCR-DI-033a | `specs/wireframes/driver_ios.html` | C093 | raise ticket (sheet) |
| SCR-DI-034 | `specs/wireframes/driver_ios.html` | C093 | notifications |
| SCR-DI-035 | `specs/wireframes/driver_ios.html` | C093 | offline · app update |
| SCR-DI-036 | `specs/wireframes/driver_ios.html` | C088 | menu |
| SCR-PA-001 | `specs/wireframes/passenger_android.html` | C077 | splash |
| SCR-PA-002 | `specs/wireframes/passenger_android.html` | C077 | onboarding + language; also cross-referenced in `driver_android.html`, `driver_ios.html` |
| SCR-PA-003 | `specs/wireframes/passenger_android.html` | C077 | phone + OTP |
| SCR-PA-004 | `specs/wireframes/passenger_android.html` | C077 | profile setup |
| SCR-PA-005 | `specs/wireframes/passenger_android.html` | C077 | location permission |
| SCR-PA-006 | `specs/wireframes/passenger_android.html` | C078 | mode / type filter |
| SCR-PA-007 | `specs/wireframes/passenger_android.html` | C078 | vehicle popup (Mode A) |
| SCR-PA-008 | `specs/wireframes/passenger_android.html` | C078 | search location |
| SCR-PA-009 | `specs/wireframes/passenger_android.html` | C079 | ride booking (PRIMARY); also cross-referenced in `web_admin.html` |
| SCR-PA-010 | `specs/wireframes/passenger_android.html` | C078 | live map (PRIMARY) |
| SCR-PA-010b | `specs/wireframes/passenger_android.html` | C079 | proxy rider details |
| SCR-PA-011 | `specs/wireframes/passenger_android.html` | C079 | confirm pickup (rider) |
| SCR-PA-012 | `specs/wireframes/passenger_android.html` | C079 | package booking |
| SCR-PA-012a | `specs/wireframes/passenger_android.html` | C079 | paste link → pin |
| SCR-PA-013 | `specs/wireframes/passenger_android.html` | C079 | schedule ride |
| SCR-PA-014 | `specs/wireframes/passenger_android.html` | C080 | finding driver |
| SCR-PA-015 | `specs/wireframes/passenger_android.html` | C080 | ride in progress |
| SCR-PA-015a | `specs/wireframes/passenger_android.html` | C080 | call type chooser |
| SCR-PA-016 | `specs/wireframes/passenger_android.html` | C080 | payment method |
| SCR-PA-017 | `specs/wireframes/passenger_android.html` | C080 | pay fare |
| SCR-PA-018 | `specs/wireframes/passenger_android.html` | C080 | trip summary |
| SCR-PA-019 | `specs/wireframes/passenger_android.html` | C080 | rate driver |
| SCR-PA-020 | `specs/wireframes/passenger_android.html` | C081 | package track (sender) |
| SCR-PA-021 | `specs/wireframes/passenger_android.html` | C081 | package track (recipient); also cross-referenced in `web_passenger.html` |
| SCR-PA-022 | `specs/wireframes/passenger_android.html` | C081 | trip & schedule history |
| SCR-PA-023 | `specs/wireframes/passenger_android.html` | C081 | trip details |
| SCR-PA-024 | `specs/wireframes/passenger_android.html` | C082 | Mode B access request |
| SCR-PA-025 | `specs/wireframes/passenger_android.html` | C082 | my private subscriptions; also cross-referenced in `web_fleet.html` |
| SCR-PA-025a | `specs/wireframes/passenger_android.html` | C082 | subscription payment |
| SCR-PA-025b | `specs/wireframes/passenger_android.html` | C082 | payment history |
| SCR-PA-026 | `specs/wireframes/passenger_android.html` | C083 | saved addresses |
| SCR-PA-026a | `specs/wireframes/passenger_android.html` | C083 | save address (modal sheet) |
| SCR-PA-027 | `specs/wireframes/passenger_android.html` | C083 | profile & settings |
| SCR-PA-027b | `specs/wireframes/passenger_android.html` | C083 | edit profile |
| SCR-PA-028 | `specs/wireframes/passenger_android.html` | C084 | VoIP call |
| SCR-PA-029 | `specs/wireframes/passenger_android.html` | C084 | SOS |
| SCR-PA-030 | `specs/wireframes/passenger_android.html` | C084 | support + ticket |
| SCR-PA-030a | `specs/wireframes/passenger_android.html` | C084 | raise ticket (modal sheet) |
| SCR-PA-031 | `specs/wireframes/passenger_android.html` | C084 | app update |
| SCR-PA-032 | `specs/wireframes/passenger_android.html` | C078 | offline state |
| SCR-PA-033 | `specs/wireframes/passenger_android.html` | C083 | menu (nav drawer) |
| SCR-PI-001 | `specs/wireframes/passenger_ios.html` | C095 | splash |
| SCR-PI-002 | `specs/wireframes/passenger_ios.html` | C095 | onboarding + language |
| SCR-PI-003 | `specs/wireframes/passenger_ios.html` | C095 | phone + OTP |
| SCR-PI-004 | `specs/wireframes/passenger_ios.html` | C095 | profile setup |
| SCR-PI-005 | `specs/wireframes/passenger_ios.html` | C095 | location permission |
| SCR-PI-006 | `specs/wireframes/passenger_ios.html` | C096 | mode / type filter |
| SCR-PI-007 | `specs/wireframes/passenger_ios.html` | C096 | vehicle popup (Mode A) |
| SCR-PI-008 | `specs/wireframes/passenger_ios.html` | C096 | search location |
| SCR-PI-009 | `specs/wireframes/passenger_ios.html` | C097 | ride booking (PRIMARY) |
| SCR-PI-010 | `specs/wireframes/passenger_ios.html` | C096 | live map (PRIMARY) |
| SCR-PI-010b | `specs/wireframes/passenger_ios.html` | C097 | proxy rider details |
| SCR-PI-011 | `specs/wireframes/passenger_ios.html` | C097 | confirm pickup (rider) |
| SCR-PI-012 | `specs/wireframes/passenger_ios.html` | C097 | package booking |
| SCR-PI-012a | `specs/wireframes/passenger_ios.html` | C097 | paste link → pin |
| SCR-PI-013 | `specs/wireframes/passenger_ios.html` | C097 | schedule ride |
| SCR-PI-014 | `specs/wireframes/passenger_ios.html` | C098 | finding driver |
| SCR-PI-015 | `specs/wireframes/passenger_ios.html` | C098 | ride in progress |
| SCR-PI-015a | `specs/wireframes/passenger_ios.html` | C098 | call type chooser |
| SCR-PI-016 | `specs/wireframes/passenger_ios.html` | C098 | payment method |
| SCR-PI-017 | `specs/wireframes/passenger_ios.html` | C098 | pay fare |
| SCR-PI-018 | `specs/wireframes/passenger_ios.html` | C098 | trip summary |
| SCR-PI-019 | `specs/wireframes/passenger_ios.html` | C098 | rate driver |
| SCR-PI-020 | `specs/wireframes/passenger_ios.html` | C099 | package track (sender) |
| SCR-PI-021 | `specs/wireframes/passenger_ios.html` | C099 | package track (recipient) |
| SCR-PI-022 | `specs/wireframes/passenger_ios.html` | C099 | trip & schedule history |
| SCR-PI-023 | `specs/wireframes/passenger_ios.html` | C099 | trip details |
| SCR-PI-024 | `specs/wireframes/passenger_ios.html` | C100 | Mode B access request |
| SCR-PI-025 | `specs/wireframes/passenger_ios.html` | C100 | my private subscriptions |
| SCR-PI-025a | `specs/wireframes/passenger_ios.html` | C100 | subscription payment |
| SCR-PI-025b | `specs/wireframes/passenger_ios.html` | C100 | payment history |
| SCR-PI-026 | `specs/wireframes/passenger_ios.html` | C101 | saved addresses |
| SCR-PI-026a | `specs/wireframes/passenger_ios.html` | C101 | save address (sheet) |
| SCR-PI-027 | `specs/wireframes/passenger_ios.html` | C101 | profile & settings |
| SCR-PI-027b | `specs/wireframes/passenger_ios.html` | C101 | edit profile |
| SCR-PI-028 | `specs/wireframes/passenger_ios.html` | C102 | VoIP call (CallKit) |
| SCR-PI-029 | `specs/wireframes/passenger_ios.html` | C102 | SOS |
| SCR-PI-030 | `specs/wireframes/passenger_ios.html` | C102 | support + ticket |
| SCR-PI-030a | `specs/wireframes/passenger_ios.html` | C102 | raise ticket (sheet) |
| SCR-PI-031 | `specs/wireframes/passenger_ios.html` | C102 | app update |
| SCR-PI-032 | `specs/wireframes/passenger_ios.html` | C096 | offline state |
| SCR-PI-033 | `specs/wireframes/passenger_ios.html` | C101 | menu |
| SCR-AP-001 | `specs/wireframes/web_admin.html` | C105 | admin_login — Login |
| SCR-AP-002 | `specs/wireframes/web_admin.html` | C105 | admin_home — Role-scoped dashboard |
| SCR-AP-003 | `specs/wireframes/web_admin.html` | C106 | verification_queues — Pending queues (list); also cross-referenced in `driver_android.html`, `driver_ios.html`, `web_fleet.html` |
| SCR-AP-003a | `specs/wireframes/web_admin.html` | C106 | verification_detail — Selected entry + document thumbnails |
| SCR-AP-003b | `specs/wireframes/web_admin.html` | C106 | document_viewer — Full-size document (lightbox) |
| SCR-AP-003c | `specs/wireframes/web_admin.html` | C106 | fleetorg_detail — Fleet-org approval detail |
| SCR-AP-004 | `specs/wireframes/web_admin.html` | C107 | moderation — Suspend / ban / reports |
| SCR-AP-005 | `specs/wireframes/web_admin.html` | C107 | support_tickets — Support & disputes |
| SCR-AP-006 | `specs/wireframes/web_admin.html` | C108 | finance — Finance & reconciliation |
| SCR-AP-007 | `specs/wireframes/web_admin.html` | C108 | config — Platform configuration |
| SCR-AP-008 | `specs/wireframes/web_admin.html` | C108 | rbac — User & role management |
| SCR-AP-009 | `specs/wireframes/web_admin.html` | C108 | audit_logs — Audit trail |
| SCR-AP-010 | `specs/wireframes/web_admin.html` | C109 | passenger_search — Find a passenger |
| SCR-AP-011 | `specs/wireframes/web_admin.html` | C109 | passenger_detail — Profile + transactions |
| SCR-AP-012 | `specs/wireframes/web_admin.html` | C109 | driver_search — Find a driver |
| SCR-AP-013 | `specs/wireframes/web_admin.html` | C109 | driver_detail — Profile, vehicles & transactions |
| SCR-AP-014 | `specs/wireframes/web_admin.html` | C109 | vehicle_search — Find a vehicle |
| SCR-AP-015 | `specs/wireframes/web_admin.html` | C109 | vehicle_detail — Information & transactions |
| SCR-AP-016 | `specs/wireframes/web_admin.html` | C110 | gtfs_manager — GTFS Dataset Manager |
| SCR-FP-001 | `specs/wireframes/web_fleet.html` | C112 | fleet_login_signup — Login / Sign-up |
| SCR-FP-002 | `specs/wireframes/web_fleet.html` | C112 | fleet_org_setup — Organisation setup |
| SCR-FP-002a | `specs/wireframes/web_fleet.html` | C112 | fleet_bank_payout — Bank & payout details |
| SCR-FP-003 | `specs/wireframes/web_fleet.html` | C114 | fleet_dashboard — Fleet dashboard |
| SCR-FP-004 | `specs/wireframes/web_fleet.html` | C113 | fleet_vehicle_onboarding — Vehicle onboarding |
| SCR-FP-005 | `specs/wireframes/web_fleet.html` | C113 | fleet_drivers — Driver assignment |
| SCR-FP-006 | `specs/wireframes/web_fleet.html` | C113 | fleet_trackers — Tracker binding |
| SCR-FP-007 | `specs/wireframes/web_fleet.html` | C114 | fleet_map — Live fleet map |
| SCR-FP-008 | `specs/wireframes/web_fleet.html` | C115 | fleet_scheduling — Scheduling & alarms |
| SCR-FP-009 | `specs/wireframes/web_fleet.html` | C114 | fleet_analytics — Trip history & analytics |
| SCR-FP-010 | `specs/wireframes/web_fleet.html` | C115 | fleet_billing — Billing & wallet |
| SCR-FP-011 | `specs/wireframes/web_fleet.html` | C116 | fleet_subscriptions — Mode B subscriptions & requests; also cross-referenced in `web_admin.html` |
| SCR-FP-012 | `specs/wireframes/web_fleet.html` | C116 | fleet_subscriber_payments — Per-subscriber payment ledger |
| SCR-WT-001 | `specs/wireframes/web_passenger.html` | C117 | passenger.mageride.lk · landing |
| SCR-WT-002 | `specs/wireframes/web_passenger.html` | C117 | passenger.mageride.lk · track (recipient) |
| SCR-WT-003 | `specs/wireframes/web_passenger.html` | C117 | passenger.mageride.lk · confirm pickup |
| SCR-WT-004 | `specs/wireframes/web_passenger.html` | C117 | passenger.mageride.lk · ride track |
| SCR-WT-005 | `specs/wireframes/web_passenger.html` | C117 | passenger.mageride.lk · delivered |
| SCR-WT-006 | `specs/wireframes/web_passenger.html` | C117 | passenger.mageride.lk · expired link |

## Cross-checks

**vs D2' per-screen tables.** D2' §A/§B carry combined IDs (`SCR-PA/PI-015a` = both platforms)
and its Δ addenda introduce the later screens; expanded per-platform, the D2' set matches the
wireframe set above. A naïve per-platform regex over D2' under-reports coverage — expand the
combined IDs before comparing.

**vs URD §6 Screen Inventory.** Every URD §6 row maps onto one or more IDs above. The URD
names some driver wallet rows separately (Credit Transfer / Pending Credit Requests / Send
Credit / Transfer History); the wireframes realise them as SCR-DA/DI-023 + SCR-DA/DI-024.

**Screens that exist in the specs but NOT in the wireframes (correctly absent):**

- `SCR-DA-005` / `SCR-DI-005` were *removed* by the 2026-06-22 onboarding restructure and then
  **re-introduced** with new meaning (camera document-scanner) by AL-43 / US-24.6. The current
  wireframes carry the AL-43 version, which is what C069 / C087 build.
- Driver IDs 008 and 009 do not exist in any spec version (numbering gap only).

**Unmappable IDs (spec gaps): none.** Every one of the 202 IDs has a screen block in exactly one wireframe file and exactly one owning component.

