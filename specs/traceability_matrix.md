# MageRide Traceability Matrix (Phase-B rollup of D1′–D7′ addenda)

> **🔄 Updated for ADD v2.6 / URD v2.2 (ADD §1.8 AL-01…AL-16).** New/changed epic mappings (Fleet Epic 13 → Phase 1, RBAC Epic 21, Passenger Settings Epic 22, insurance US-2.19, driver emergency contact US-12.9, single-active-device-per-app US-1.12) are appended at the end; Epic 9/9A rows updated for driver-to-driver credit transfer (by Driver ID, **no per-transfer commission**; bulk-voucher purchase discount is DB-configurable) + bank-transfer removal; D-DRIFT-1 vehicle taxonomy marked **RESOLVED (AL-09)**.

> **Phase-B gate deliverable (B-rollup / B0 GAP-G1).** Cross-join of the seven per-document
> Traceability Addenda: D1′ (ln 631–687), D2′ (§ screen map), D3′ (ln 644–684), D4′ (ln 907–939),
> D5′ (ln 521–549), D6′ (ln 407–430), D7′ (ln 433–462, infra/ADD-item mapped). One row per URD
> user-story cluster (Epics 1–20, incl. 6A/9A/NEW). Cells cite the covering section/table/endpoint/
> screen; `—` = not applicable to that layer.
>
> **Result:** every URD **P0/P1** story cluster maps to ≥1 D′ document. **Unmapped P0/P1 stories: 0.**
> Priority column reflects URD v1.3 §7 (P2 items marked; Epic 3 trackers + Epic 20 package were
> promoted by ADD v2.3/v2.2 and are specced as `[NEW]`).
>
> Legend — D1′=user flows · D2′=UI screens · D3′=API · D4′=schema · D5′=logic · D6′=integration ·
> D7′=devops. Tag = dominant transform tag for the cluster.

---

## Epic 1 — Identity, Auth, Profile, PDPA

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-1.1 | P0 | `login_phone`, B.7 | SCR-*-003 | iam `/auth/otp/*` | iam.users/otp_attempts | §14.1 | §7.3 SMS | iam env (Sms,Otp) | [ADAPT] | +94 SMS-gateway OTP |
| US-1.2/1.3 | P0 | `onboarding` | SCR-*-002 | — | iam.users.language | — | — | — | [ADAPT] | 3-slide + **vertical** Si/Ta/En (Sinhala first) |
| US-1.3a | P1 | `onboarding_lang_city` | SCR-DA/DI-002 | `GET /config/cities` | config.operating_cities, iam.users.operating_city_code | — | §7 cache | content-svc | [NEW] | **launch cities from DB**, admin-managed (Change 6/22) |
| US-1.5 | P0 | `profile_setup`/`profile_settings` | SCR-*-004/027 | `/users/me` | iam.users | — | — | — | [KEEP] | edit profile |
| US-1.7/1.8 | P0 | `profile_settings` | SCR-*-027 | `/auth/logout`, `DELETE /users/me` | pdpa.requests | §15 | — | pdpa CronJob | [KEEP]/[NEW] | logout, account delete/erasure (E-06) |
| US-1.9/1.11 | P0 | A.4, B.2 | — | `/auth/refresh`, device revoke | iam.sessions | §14.2 | — | iam env (Jwt) | [ADAPT] | 30-min JWT + refresh, new-device revoke (D-29) |
| US-1.10 | P0 | A.3 OTP | SCR-*-003 | `/auth/otp/resend` | iam.otp_attempts | §14.1 | §7.3 | iam env | [ADAPT] | 60 s resend, 5/h (D-32) |
| US-11.5 | P0 | B.7 | SCR-DA-003 | iam (no Google on app) | — | §14.2 | — | iam `Google__ClientId` (portal only) | [ADAPT] | Phone-OTP only on apps |

## Epic 2 — Vehicle Registration & Onboarding

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-2.21 (profile setup) | P0 | `profile_setup`, B.7 | SCR-DA/DI-003a | `PUT /drivers/profile` | registry.driver_profiles, registry.documents(driving_license) | §14.1a | §7.5 OCR | ocr/registry env | [NEW] | name + **required** photo + DL; precedes Home (Change 6/22) |
| US-2.22/2.23 (Mode-C onboard) | P0 | `vehicle_onboard_*`, B.7 | SCR-DA/DI-004/004a/004b/004c | registry `POST /vehicles`, `/onboarding-status` | registry.vehicles/documents, docs.extractions | §14.1a | §7.5 OCR | `Gemini__Model`=flash-3.0 | [REPLACE]/[NEW] | 4-step, **Gemini Flash 3.0 auto-verify → auto-approve** (Change 6/22) |
| US-2.24/2.25 (Fleet split + gating) | P0 | B.2, B.7 | SCR-DA/DI-026a, 010 | — | registry.fleet_assignments, registry.shares | §14.1a | — | — | [NEW] | permits=Fleet Portal; go-online gated until vehicle available |
| US-2.8/2.16 | P0/P1 | `vehicle_mgmt` | SCR-DA-026/026a | `/vehicles/mine`,`/deactivate` | registry.vehicles | §3.2 mutex | — | — | [ADAPT]/[NEW] | multi-vehicle, deactivate, empty-state popup (D-03,E-03) |
| US-2.9/2.10 | P0 | B.7 | SCR-DA-006 | Verification Officer (Pending only) | docs.extractions | §14.1a | §7.5 | — | [ADAPT] | review queue only for Pending docs (Change 6/22) |
| US-2.13/2.14/2.15 | P0 | `vehicle_onboard_status` | SCR-DA-006 | `/vehicles/{id}/onboarding-status` | registry.vehicles.status | §14.1a | §7.4 FCM | — | [REPLACE] | 4-doc status + auto-approve + FCM + reason |
| US-2.17/2.18 | P1 | — | (admin) | admin-bff `POST /admin/trains` | registry.vehicles(train) | — | — | — | [NEW] | **train admin-only** Mode A |

## Epic 3 — Hardware GPS Trackers (ADD v2.3 → Phase 1)

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-3.1/3.2/3.5/3.6/3.8 | P1 | `tracker_pairing`, B.11 | SCR-DA (pairing) | provisioning `/trackers/*`,`/fleets/bulk` | prov.tracker_bindings/device_certs | §13 | §4.2/4.3 | provisioning env, step-ca PVC | [NEW] | IMEI bind, X.509/PSK, bulk CSV, revoke (T-02/09/12) |
| US-3.4 | P1 | B.11 | — | provisioning (409 imei-duplicate) | prov.tracker_bindings(QUARANTINED) | §13.2 | §4.3 | — | [NEW] | anti-clone quarantine (T-08) |
| US-3.9/3.10/3.11/3.17 | P1 | B.11 | — | tcp-adapter (MQTT, Part 3) | telemetry.positions(seq) | §5.3 | §4.1/4.4 | tcp-adapter env, §2.1 | [NEW] | GT06/JT808/H02/NMEA, replay dedup (T-01/05) |
| US-3.12/3.13/3.14 | P1/P2 | B.11 | (portal) | fleet-svc `/health` | telemetry.fleet_health_5m | — | §4.5 | fleet-health env | [NEW] | tracker health rollup |
| US-3.21/3.22 | P1 | B.11 | — | — | telemetry.positions | §13.4 | §4.5 | — | [NEW] | Mode A vs Mode C eligibility (T-11) |

## Epic 4 / NEW — Mode B Private Transport Sharing

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-4.1–4.4,4.7 | P1 | `sharing_mgmt` | SCR-DA (share) | registry `/share*`,`/subscribers` | registry.shares | — | §5.2 | — | [ADAPT] | share grant + accept |
| US-4.5/4.6 | P1 | `mode_b_request` | SCR-PA-024 | registry `/share-requests` | registry.shares | — | §5.2 | — | [NEW] | request by Vehicle ID (D-23) |
| US-NEW.1 | P1 | `mode_b_manage` | SCR-PA-025 | `DELETE /subscribers/{userId}` | registry.shares | §A.4 | §5.2 RemoveFromGroup | — | [NEW] | unsubscribe, revoke push (D-22) |

## Epic 5 — Mode A Journey Sessions

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-5.1–5.6 | P0 | `mode_a_session`, B.8 | SCR-DA-011 | trip-state `/sessions/start,end` | trips.sessions | §5.1/§5.2 | §3.1 cmd cadence | trip-state env | [NEW] | Start/End Journey, adaptive GPS, no fee |
| US-5.3/5.4/5.9/5.10 | P0 | B.5/B.8 | SCR-DA-011 | `/sessions/{id}/restart`, internal auto-end | trips.sessions(end_reason) | §6.3 grace | §3.4 LWT | trip-state env (Idle/Geofence) | [ADAPT] | idle-30min/geofence-100m auto-end, 5-min grace restart |

## Epic 6A — Mode C Dispatch, Ride, Level, Directional

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-6A.1 | P0 | `standby_toggle`, B.8 | SCR-DA-010/012 | dispatch `/standby/online` | dispatch.driver_presence | §3.1/§5.2 | §5.4 | dispatch env | [NEW] | standby presence (R-08) |
| US-6A.2/6A.3 | P0 | `incoming_request`, B.9 | SCR-DA-014 | ride accept (15 s atomic) | rides.rides(version), dispatch.offers | §3.3/§3.5/§6.1 | §2.2/§2.4 | dispatch env (OfferTtl=15) | [REPLACE] | atomic single-winner (R-02) |
| US-6A.4/6A.5/6A.15 | P0/P1 | `schedule_ride`/`job_board`/`scheduled_rides` | SCR-PA-013/SCR-DA | dispatch `/job-board`,`/scheduled` | dispatch.scheduled_rides/job_board_intents | §3.7 | — | dispatch env (JobBoard=30km) | [NEW] | Job Board ST_DWithin (D-06) |
| US-6A.6/6A.7/6A.8/6A.14 | P0 | `driver_level` | SCR-DA (level) | dispatch `/level`,`/stats`; reputation gRPC | dispatch.driver_levels/no_show_events | §4.2 | — | reputation env (gRPC) | [NEW]/[REPLACE] | Driver Level System (D-04) |
| US-6A.9/6A.10/6A.10b | P0 | A.4, B.9 | SCR-PA-015 | ride `/cancel` | dispatch.cancellation_penalties, reputation.counters | §7/§7.1/§7.2 | §2.1 ride.cancelled | — | [NEW] | Rs 50 cross-trip (D-05), 3-cancel disable |
| US-6A.11 | P0 | `finding_driver` | SCR-PA-014 | ride (system-cancel) | rides.rides(ExpiredNoDriver) | §3.5 | §3.4 timers | — | [REPLACE] | 2-min no-driver timeout |
| US-6A.12/6A.13 | P0 | `ride_in_progress` | SCR-PA-015 | ride (state), SignalR SubscribeRide | rides.rides | §6 | §5.1 | fanout env | [REPLACE] | live driver position |
| US-6A.16 | P1 | `voip_call` | SCR-PA-028 | voip `/token` | comms.voip_sessions | §10 | §6 | voip env | [NEW] | VoIP rider not booker (D-24/25, P-05) |
| US-6A.17–6A.23 | P1 | `directional_travel`, B.4 | SCR-DA-013 | dispatch `/standby/directional` | dispatch.directional_filters/timers/config | §12 | §3.4 clears | — | [NEW] | Directional Travel (DT-01..08) |

## Epic 7 — Live Map & Visibility

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-7.1/7.2/7.3/7.4 | P0 | `live_map`/`vehicle_popup` | SCR-PA-010/007 | query `/nearby`, SignalR `/hubs/live` | telemetry.positions, dispatch.driver_presence | §3.1/§5.4 | §5.1/5.2 | fanout env (Geocell=7) | [REPLACE] | MapLibre + geocell groups (R-06) |
| US-7.7 | P0 | `mode_filter` | SCR-PA-006 | `/nearby?types` | — | — | — | — | [NEW] | mode/type filter incl. trains |
| US-7.9/7.15 | P1 | A.1 | — | `/routes`,`/transport-options` | spatial.routes | — | — | — | [NEW] | route buses, dest options incl. trains |
| US-7.11/7.12 | P1 | A.3 / `ride_in_progress` | SCR-PA-007/015 | query / ride | — | — | — | — | [ADAPT] | ETA, driver after-accept only |
| US-7.13 | P2 | `saved_addresses` | SCR-PA-026 | — | — | — | — | — | [KEEP] | saved addresses |
| US-7.14 | P1 | A.3 (empty) | SCR-PA-010 | `/nearby` | — | — | — | — | [ADAPT] | "no X active nearby" |
| US-7.16/7.17 | P0 | A.3 `live_map` | SCR-PA-010 | query `/nearby` | dispatch.driver_presence | §5.4 | §5.2 VehicleRemoved | — | [REPLACE] | engaged hidden / stale removed |

## Epic 8 — Booking, Fare, Payment, Proxy

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-8.2/8.4/8.9 | P0 | `ride_booking` | SCR-PA-009 | fare `/estimate`; ride `/request` | fares.tariffs/peak_windows | §1.1 (B1) | §7.6 routing | — | [REPLACE] | upfront fare, total only |
| US-8.7 | P1 | `trip_history` | SCR-PA-022 | query `/trips` | rides.rides, trips.sessions | — | — | — | [KEEP] | history |
| US-8.8 | P1 | `trip_details` | SCR-PA-023 | query `/earnings` | fares.driver_earnings | §1.1 | — | — | [ADAPT] | driver sees per-trip fare |
| US-8.10/8.11/8.12/8.15 | P0 | `payment_method`/`payment_pay` | SCR-PA-016/017 | fare `/pay`,`/status`,`/fallback-cash` | fares.ride_payments | §8.1 | §7.1/7.2 | fare env (OnePay/LankaQR) | [REPLACE] | Cash/LankaQR/OnePay+5% (D-10) |
| US-8.16–8.21 | P1 | `proxy_details`/`confirm_pickup_rider` | SCR-PA-010b/011 | ride `/request`(proxy), `/location-requests/*` | rides.rides(booker/rider), location_requests | §10 | §5.3 | ride env | [NEW] | proxy + location-request (P-01..05,13) |

## Epic 9 / 9A — Wallet, Daily Fee, Reseller Capability, Vouchers (in-app; Admin Portal back-office)

> **🔄 ADD v2.6 / URD v2.2 (AL-01/05):** "reseller" is **not a role/account/capability** — any driver who bought bulk credit transfers it to others by **Driver ID** (exact value, **no per-transfer commission**); the margin is the DB-configurable bulk-voucher purchase discount; **bank-transfer top-up removed**; back-office = single **Admin Portal**.

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-9.1/9.4/9.6/9.7 | P0 | `wallet_fee`, B.10 | SCR-DA-010 | subscription `/fees/*` | billing.plans/daily_fee_charges (B1) | §2 | — | subscription env (FirstTripFree) | [NEW]/[REPLACE] | daily fee, first-trip-free (D-13) |
| US-9.9 | P1 | B.5 | SCR-DA-010 | notification LOW_BALANCE | — | §9.4 | §7.4 | wallet env (LowBalance) | [NEW] | low-balance push |
| US-9.10–9.17 | P1 | `request_credit`/`credit_transfer` (Driver App), B.10 | SCR-DA-023/024 | subscription `/credit-transfer/*` (driver) | billing.credit_transfers | §9.3 | — | — | [NEW] | driver-to-driver, exact value, **no commission** (AL-01) |
| US-9.18/9.19/9.20/9.21 | P1 | `wallet_topup`/`credit_transfer` | SCR-DA (topup) | wallet `/topup/*` (OnePay/LankaQR); subscription `/vouchers`,`/transfers` | billing.voucher_discount_tiers/voucher_purchases, journal_* | §9.3 | §7.1/7.2 | wallet env | [REPLACE]/[NEW] | top-up (no bank transfer, AL-05), DB-config voucher discount, driver transfer (exact value) |
| US-9.22/9.23 | P1 | `earnings`/`support`, B.10 | SCR-DA (earnings) | query `/earnings`; support `/tickets` | fares.driver_earnings | — | — | — | [ADAPT]/[NEW] | summary, fee-refund request |
| US-9A.1–9A.19 | P0/P1 | B.10 (in-app) | SCR-AP (Admin Portal) | wallet/subscription (in-app); admin `/voucher-discount-tiers` | billing.* (no bank_transfer_topups) | §9.3 | OnePay/LankaQR settlement | admin-portal container | [ADAPT] | **in-app wallet; bank transfer removed (AL-05); no per-transfer commission (AL-01)** |

## Epic 10 — Notifications & Push

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-10.7 | P1 | A.5/B.6 | SCR-*-027 | notify `/preferences` | iam.users.notif_prefs | §14.4 | §7.4 | notification env | [NEW] | per-type prefs |
| US-10.8/10.9/10.14 | P1 | B.5/A.5 | — | notify `/notify/*` | content.notification_templates | §14.4 | §7.4 | notification env | [ADAPT] | cancel/scheduled/directional push |
| US-10.12/10.13 | P1 | `package_track_recipient` | SCR-PA-021 | ride/package push | safety.trip_share_tokens | §11 | §7.4 | — | [NEW] | package recipient FCM/SMS (P-09) |

## Epic 12 — Safety (SOS, Report, Block, Trip-share)

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-12.1/12.8/12.11 | P0 | `sos` (A/B) | SCR-PA-029 | safety `/sos`,`/sos/{id}/history` | safety.sos_events | §14.3 | §7.3 dual SMS | safety/notification env (SloMs) | [ADAPT]/[NEW] | passenger+driver SOS p99 ≤5s (D-33) |
| US-12.5/12.6/12.10 | P0/P1 | `report_block` | SCR-PA (report) | safety `/reports`,`/drivers/{id}/block` | safety.vehicle_reports/blocked_drivers | §3.2/§7.2 | — | — | [ADAPT]/[NEW] | report → 3 delist, block driver |
| US-12 trip-share | P1 | A.6 | — | safety `/trip-share/*` | safety.trip_share_tokens | §14.3 | — | safety env (TripShare) | [NEW] | scoped share token (D-34) |

## Epic 14 — Admin Console

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-14.3/14.4/14.8 | P1 | B.10 | Admin BFF | admin-bff `/admin/*` (suspend, fares, announce) | fares.tariffs, content.broadcasts | §1.1 | — | admin env | [ADAPT] | config, tariffs, broadcast |
| US-14.11/14.12/14.13 | P1 | B.10 `support` | Admin BFF | admin-bff `/reverse-fee`, level-config | billing.journal_*, dispatch.driver_levels | §4.2 | — | — | [NEW] | fee reversal, level config |

## Epic 15 — Offline & Replay

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-15.1 | P0 | A.5/B.5/B.11 | — | tcp-adapter `/pos/replay` (MQTT) | telemetry.positions(seq) | §5.3 | §3.5/§4.4 | position-processor env (Replay) | [NEW] | offline buffer replay, seq dedup (R-17) |
| US-15.2/15.4/15.6 | P0 | A.5 | SCR-PA-032 | query `/nearby` (snapshot) | — | §5.4 | §5.4 reconnect | — | [ADAPT] | offline banner, last-known, reconnect <5s |

## Epic 16 — Support (FAQ + Tickets)

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-16.1/16.2/16.3 | P1 | `support`/`ticket_thread` | SCR-PA-030 | support `/faq`,`/tickets`; admin `/support` | support.tickets, content.faq_articles | — | — | support env | [NEW] | multilingual FAQ + tickets |

## Epic 17 — App Version Gate

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-17.1/17.2 | P1 | `app_update` | SCR-PA-031 | version-check `/version/check` + 426 gate | — | — | §8.1 gateway | YARP gateway | [NEW] | mandatory/soft update (D-31) |

## Epic 18 — Ratings

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-18.1/18.2/18.3 | P1 | `rate_driver`/`rate_passenger`/`driver_profile` | SCR-PA-019 | trip-state `/rating`,`/driver-rating`; ride | trips.ratings | §4.1 | — | — | [ADAPT]/[KEEP]/[NEW] | stars + text, both directions |

## Epic 19 — Admin / Accessibility / Audit

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-19.1/19.2 | P1 | Section C | §0/Section C | — | — | — | — | — | [ADAPT] | TalkBack/VoiceOver, Dynamic Type |
| US-19.3 | P1 | — | Admin BFF | admin-bff `/audit-log` | audit.events | §14 | §2.1 audit.events | admin env (Audit__Topic) | [NEW] | immutable admin audit (D-35) |

## Epic 20 — Package Delivery (ADD v2.2 → Phase 1)

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| US-20.1–20.11 | P1 | `package_booking`/`package_track_*`, `delivery_confirm` | SCR-PA-012/020/021, SCR-DA-016 | ride `/request`(package), `/package/*`, `/cod-collected` | rides.rides(kind/package/otp), proof_artifacts | §11 | §7.4 | ride env (Otp__PepperKey) | [NEW] | size S/M/L, pickup/delivery OTP, COD, proof photo (P-06..10) |

## Cross-cutting — Maps & Tiles

| ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| MAP-01..10 | P0 | A.3 live_map | §0.3 | query/tile-cdn/nominatim | spatial.* | §5.4 | §7.6 | osm-pipeline CronJob, R2 | [REPLACE] | MapLibre + PMTiles + Nominatim (D-14/15/16) |

---

## Coverage Summary & Gate Status

| Check | Result |
|---|---|
| URD Epics represented (1–22 incl. 6A/9A/11/13/21/22) | **All** |
| URD **P0/P1** story clusters with ≥1 D′ mapping | **100% — 0 unmapped** |
| ADD §6 services with API contract (D3′) | **All** (see D3′ Part-1 map) |
| ADD §9 schemas with DDL (D4′) | **All 18** |
| ADD §1.3–§1.7 deficit items (D-03..DT-08) | **All in-scope ✅** (B0 §4 roll-up) |
| Every D2′ screen has Android + iOS variant | **Yes** |
| Currency Rs · phones +94 · languages Si/Ta/En | **Yes** |

### Epics added/changed by ADD v2.6 / URD v2.2 (AL-01…AL-16)

| US-ID | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D7′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|
| US-13.* (Fleet, **Phase 1**) | P0/P1 | Fleet Portal flows | SCR-FP-001..010 | fleet-svc `/v1/fleets/*` | registry.fleets/fleet_vehicles/fleet_assignments, billing.fleet_invoices, iam.fleet_members | — | fleet-svc + fleet-portal | [NEW] | AL-03: org (verification-gated), Mode A/B only, assign, schedule, map, billing |
| US-21.* (RBAC) | P0/P1 | — | SCR-AP-008 | iam/admin-bff RBAC | iam.user_roles/fleet_members | §14.2 | admin-bff (DenyByDefault; **no MFA — AL-37**) | [NEW] | AL-06: nine roles, deny-by-default, Super-Admin provisioning |
| US-22.* (Passenger Settings) | P1 | `profile_settings`/`saved_addresses` | SCR-PA-026/027 | iam `/profile` | iam.saved_addresses, users.default_payment_method | — | — | [NEW] | AL-14: Home/Work map pins, saved addresses, default payment, Help |
| US-2.19 (insurance) | P0 | — | SCR-DA-004a | registry | registry.documents kind=insurance | §14.1a | §7.5 | [NEW] | AL-10: insurance mandatory all modes; Step 2/4 auto-verify (Change 6/22) |
| US-2.20 (revenue licence) | P0 | — | SCR-DA-004b/006 | registry | registry.documents kind=revenue_license | §14.1a | §7.5 | [NEW] | revenue licence mandatory all modes; Step 3/4 auto-verify; expiry auto-suspends (mirrors AL-10) |
| US-12.9 (driver emergency contact) | P1 | — | SCR-DA-031 | iam `/profile` | iam.emergency_contacts | §14.3 | — | [NEW] | AL-13 |
| US-1.12 (single active device) | P0 | A.4/B.2 | — | iam sessions | iam.sessions(app) | §14.2 | — | [ADAPT] | AL-08: per-app (US-1.11 merged) |

**Resolved (was P2/deferred or open):** **D-DRIFT-1 vehicle taxonomy — RESOLVED (AL-09)**: one canonical
set, car→sedan, Flex/Mini Van have own fare rows in `fares.tariffs` (no more "overridable assumption").
US-7.13 saved addresses promoted into **Epic 22** (P1). **fleet-svc → Phase 1** (was Phase 2). Reseller
admin surfaces are now in the **Admin Portal** (drivers use the Driver App).

### Epics added/changed by Discussion 2026-06-21 (ADD v2.7 §1.9 AL-17…AL-26 · URD v2.3)

| US-ID / Item | Pri | D1′ | D2′ | D3′ | D4′ | D5′ | D6′ | Tag | Notes |
|---|---|---|---|---|---|---|---|---|---|
| US-1.3 / item 1 (lang vertical, Sinhala-first) | P1 | F-23 note | SCR-PA/PI-002 | iam `/me/prefs/language` | iam.user_prefs.language | BR-23.12 | — | [ADAPT] | AL-26 |
| US-1.5 / item 10 (lang removed from edit-profile) | P1 | F-23 note | SCR-PA/PI-027b | — | — | BR-23.12 | — | [ADAPT] | AL-26 |
| item 2 (coloured vehicle icons) | P2 | F-23 note | SCR-PA/PI-006 | — | — | — | — | [ADAPT] | AL-26 |
| US-8.2a / item 4 (geo-only search) | P0 | F-23.1 | SCR-PA/PI-008 | transit-svc `/transit/options` | transit.* | BR-23.1 | I-23.2 | [REPLACE] | AL-17 |
| US-8.2b / item 3 (GTFS direct routes) | P0 | F-23.1 | SCR-PA/PI-009 | transit-svc | transit.gtfs_* | BR-23.2 | I-23.2 | [NEW] | AL-18 |
| US-8.2c / item 3 (Mode C price-only) | P0 | F-23.1 | SCR-PA/PI-009 | fare-svc | — | BR-23.3 | — | [ADAPT] | AL-19 |
| US-8.2d / items 5,6 (paste link) | P0 | F-23.2 | SCR-PA/PI-010b/012/**012a** | transit-svc `/geo/parse-maps-link` | — | BR-23.4 | I-23.1 | [NEW] | AL-20 |
| US-20.2 / item 6 (package drop-off) | P2 | F-23.2 | SCR-PA/PI-012 | ride-svc | rides.rides | BR-23.5 | — | [ADAPT] | AL-21 |
| US-20.5 / item 11 (recipient FCM/SMS web) | P2 | F-23.5 | SCR-PA/PI-021, web_passenger | notification-svc | safety.trip_share_tokens | BR-23.5 | I-23.3 | [ADAPT] | AL-21 |
| US-8.10b / item 18 (scan driver QR) | P0 | F-23.8 | SCR-PA/PI-017 | fare-svc `/fare/pay/scan-driver-qr` | fares.ride_payments(method) | BR-23.6 | I-23.5 | [REPLACE] | AL-22 |
| US-4.9 / item 8 (Mode B marker → request) | P0 | F-23.3 | SCR-PA/PI-007/024 | subscription-svc `/mode-b/*` | subscription.access_requests | BR-23.7 | — | [ADAPT] | AL-23 |
| US-4.10 / items 12,15 (per-vehicle requests) | P0 | F-23.3 | SCR-DA/DI-028, SCR-FP-011 | subscription-svc, fleet-svc | subscription.access_requests/grants | BR-23.7 | — | [ADAPT] | AL-23 |
| US-13.1b / item 16b (Paid/Free classification) | P1 | — | SCR-FP-004 | fleet-svc `/classification` | registry.vehicles.mode_b_billing | BR-23.8 | — | [NEW] | AL-24 |
| US-23.* / item 16 (subscription payments) | P1 | F-23.3 | SCR-PA/PI-025/025a/025b, SCR-FP-011/012 | subscription-svc `/mode-b/*/pay*` | subscription.subscriptions/payments | BR-23.9/23.10 | I-23.4 | [NEW] | AL-24 |
| US-4.11/4.12 / item 17 (unsubscribe + muted) | P0/P1 | F-23.4 | SCR-PA/PI-025, SCR-FP-011 | subscription-svc `/unsubscribe`, fleet `DELETE` | subscription.grants(deleted_at) | BR-23.11 | — | [NEW] | AL-25 |
| US-22.2 / item 7 (add-address modal lines+label) | P1 | F-23.6 | SCR-PA/PI-026a | iam `/me/saved-addresses` | iam.saved_addresses(line1/2/3,label) | BR-23.12 | — | [ADAPT] | AL-26 |
| US-22.7 / item 9 (passenger nav drawer) | P1 | F-23.7 | SCR-PA/PI-033 | — | — | — | — | [NEW] | discussion |
| item 14 (driver nav drawer) | P1 | F-23.7 | SCR-DA/DI-036 | — | — | — | — | [NEW] | discussion |

### Epics added/changed by Discussion 2026-06-28 (ADD v2.9 §1.11 AL-36…AL-43 · URD v2.5 Epic 24)

| Story / item | Pri | Flow | Screen(s) | API | Data | BR | Integration | Status | Note |
|---|---|---|---|---|---|---|---|---|---|
| US-24.1 / item 1 (Get Started bottom) | P2 | F-28.* | SCR-PA/PI-002 | — | — | — | — | [ADAPT] | AL-36 |
| US-24.2 / item 2 (schedule destination) | P1 | F-28.1 | SCR-PA/PI-013 | `POST /v1/rides/schedule` (dest required) | dispatch.scheduled_rides (dropoff_geo NOT NULL) | BR-28.1 | — | [ADAPT] | AL-36 |
| US-24.3 / item 4 (call-type chooser) — masked leg **superseded by US-26.2 / AL-48** | P1 | F-28.2 | SCR-PA/PI-015a | `POST /v1/calls/start` (**`free_voip` only**); Normal call = client `tel:` dial | comms.call_log (`free_voip`\|`direct_dial`) | BR-28.2 → BR-30.2 | I-28.3 + **I-30.2** | [ADAPT] | AL-36 → **AL-48** |
| US-24.4 / item 3 (driver mobile in history) | P2 | F-28.3 | SCR-PA/PI-022 | `GET /v1/rides/history` | rides.rides (driver join) | BR-28.3 | — | [ADAPT] | AL-36 |
| US-24.6 / item 6 (camera drag-crop capture) | P1 | F-28.4 | SCR-DA/DI-005 + 003a/004a/004b/004c | `PUT /v1/vehicles/{id}/onboarding/{step}` | docs.uploads(captured_via) | BR-28.4 | I-28.2 | [NEW] | AL-43 |
| US-24.5 / item 5 (admin no MFA) | P1 | F-28.5 | SCR-AP-001 | `POST /admin/auth/login` (no MFA) | iam (user_mfa deprecated) | BR-28.5 | I-28.1 | [ADAPT] | AL-37 |
| US-24.7 / item 7 (dashboard stats filter) | P1 | F-28.5 | SCR-AP-002 | `GET /admin/dashboard/stats` | analytics.daily_metrics | BR-28.6 | I-28.5 | [NEW] | AL-38 |
| US-24.8 / item 8 (verification split + viewer) | P1 | F-28.6 | SCR-AP-003/003a/003b/003c | `/admin/verification/queues/*`, `/admin/documents/{id}` | audit.events(DOC_VIEW) | BR-28.7 | I-28.4 | [NEW] | AL-39 |
| US-24.9 / item 9 (passenger directory) | P1 | F-28.7 | SCR-AP-010/011 | `/admin/passengers*` | read-model + audit(PII_READ) | BR-28.8 | I-28.6 | [NEW] | AL-40 |
| US-24.10 / item 10 (driver directory) | P1 | F-28.7 | SCR-AP-012/013 | `/admin/drivers*` | read-model + audit(PII_READ) | BR-28.8 | I-28.6 | [NEW] | AL-41 |
| US-24.11 / item 11 (vehicle directory) | P1 | F-28.7 | SCR-AP-014/015 | `/admin/vehicles*` | read-model | BR-28.8 | I-28.6 | [NEW] | AL-42 |

### Epics added/changed by Discussion 2026-07-05 (ADD v3.0 §1.12 AL-44…AL-46 · URD v2.6 Epic 25)

| Story / item | Pri | Flow | Screen(s) | API | Data | BR | Integration | Status | Note |
|---|---|---|---|---|---|---|---|---|---|
| US-25.1 / item 1 (SCR-WT screen IDs + states) | P1 | F-29.1 | **SCR-WT-001…006** (`web_passenger.html`) | — | — | BR-29.1 | — | [NEW] | AL-44 |
| US-25.2 / item 2 (public track API) | P1 | F-29.1 | SCR-WT-001/002/004 | `public-bff /public/track/{token}[,/live]` | safety.trip_share_tokens (read) | BR-29.1 | I-29.1 | [NEW] | AL-44 |
| US-25.3 / item 3 (web pickup-confirm, unregistered rider) | P1 | F-29.2 | SCR-WT-003 | `/public/track/{token}/pickup/confirm\|decline` | trip_share_tokens(location_request_id), rides.location_requests | BR-29.2 | I-29.2 | [NEW] | AL-45 |
| US-25.4 / item 4 (web call) — **superseded by US-26.3 / AL-48** | P1 | F-29.3 | SCR-WT-002/004 | ~~`/public/track/{token}/call`~~ **REMOVED** → `GET /public/track/{token}` (+driver.phone), `tel:` link | ~~call_log(share_token, web_masked)~~ **dropped** | BR-29.3 → BR-30.3 | I-29.3 → **I-30.2** | [ADAPT] | AL-44 → **AL-48** |
| US-25.5 / item 5 (web SOS) | P1 | F-29.3 | SCR-WT-004 | `/public/track/{token}/sos` | safety.sos_events(source, share_token) | BR-29.4 | I-29.4 | [NEW] | AL-44 |
| US-25.6 / item 6 (delivered / receipt page) | P2 | F-29.4 | SCR-WT-005 | `/public/track/{token}/receipt` | derived (proof_artifacts, ride_payments) | BR-29.5 | I-29.1 | [NEW] | AL-44 |
| US-25.7 / item 7 (token scopes + metering) | P1 | F-29.* | — | mint: notification-svc (internal) | trip_share_tokens(+2 scopes, metering) | BR-29.1 | I-29.2 | [ADAPT] | AL-44 |
| US-25.8 / item 8 (spec hygiene) | P2 | — | SCR-DA/DI-012 **[MERGED → 010]**; wireframe annotations | — | — | — | — | [ADAPT] | AL-46 |

### Epics added/changed by Discussion 2026-07-05 #2 (ADD v3.1 §1.13 AL-47…AL-48 · URD v2.7 Epic 26)

| Story / item | Pri | Flow | Screen(s) | API | Data | BR | Integration | Status | Note |
|---|---|---|---|---|---|---|---|---|---|
| US-26.1 / item 1 (driver-QR attestation) | P1 | F-30.1 | SCR-PA/PI-017, SCR-DA/DI-015 | `/v1/fare/pay/driver-qr/claim\|confirm\|dispute` | fares.ride_payments (+QrClaimedByPassenger/DriverConfirmedQR, qr_* cols), proof_artifacts(qr_receipt) | BR-30.1 | I-30.1 | [NEW] | AL-47 |
| US-26.2 / item 2 (direct-dial normal call) | P1 | F-30.2 | SCR-PA/PI-015a/022 | `GET /v1/rides/{id}` (+counterpartyPhone post-accept); `/v1/calls/start` free_voip-only | comms.call_log(call_type ∈ free_voip/direct_dial) | BR-30.2 | I-30.2 | [ADAPT] | AL-48 |
| US-26.3 / item 3 (web tel: link) | P2 | F-30.2 | SCR-WT-002/004 | `GET /public/track/{token}` (+driver.phone); **`/call` REMOVED** | call_log.share_token dropped | BR-30.3 | I-30.2 | [ADAPT] | AL-48 |
| US-26.4 / item 4 (VoIP fallback = direct dial) | P2 | F-30.2 | SCR-PA/PI-028, SCR-DA/DI-031 | — (client behaviour) | comms.voip_sessions (relay flag dropped) | BR-30.3 | I-30.2 | [ADAPT] | AL-48 (D-25 removed) |
| US-26.5 / item 5 (number-visibility consent) | P2 | — | onboarding ToS + first-call tooltip | — | — | BR-30.2 | — | [NEW] | AL-48 / PDPA |

### Epics added/changed by Discussion 2026-07-18 (ADD v3.2 §1.14 AL-49…AL-51 · URD v2.8 Epic 27)

| Story / item | Pri | Flow | Screen(s) | API | Data | BR | Integration | Status | Note |
|---|---|---|---|---|---|---|---|---|---|
| US-27.1 / item 1 (bank & payout profile + docs) | P1 | F-31.1 | **SCR-FP-002a** (`web_fleet.html`), SCR-AP-003 | fleet-svc `GET/PUT /fleets/{id}/payout-profile`, `POST …/payout-profile/documents` | registry.fleet_payout_profiles; docs.uploads(+bank_statement/passbook_first_page/lankaqr_code) | BR-31.1 | I-31.1 | [NEW] | AL-49 |
| US-27.2 / item 1 (pay sheet consumes verified payTo) | P1 | F-31.1 | SCR-PA/PI-025a | subscription-svc `POST /mode-b/subscriptions/{id}/pay` (+`payTo`) | fleet_payout_profiles (read, verified row) | BR-31.1 | I-31.1 | [ADAPT] | AL-49 |
| US-27.3 / item 2 (named vehicle-doc slots) | P1 | F-31.2 | SCR-FP-004 | fleet-svc `GET/POST /fleets/{id}/vehicles/{vid}/documents` | registry.documents(driver_id NULLable, +fleet_id); kinds registration/insurance/revenue_license/permit | BR-31.2 | I-31.2 | [ADAPT] | AL-50, extends AL-10/AL-29 |
| US-27.4 / item 3 ("Service payment" rename) | P2 | — | SCR-FP-004 (label + column) | — (`/classification` unchanged) | — (`mode_b_billing` unchanged) | BR-31.3 | — | [ADAPT] | AL-51; supersedes BR-23.8 label |

### Epics added/changed by Discussion 2026-07-22 #2 (ADD v3.4 §1.16 AL-54…AL-55 · URD v2.9 Epic 28)

| Story / item | Pri | Flow | Screen(s) | API | Data | BR | Integration | Status | Note |
|---|---|---|---|---|---|---|---|---|---|
| US-28.1 (full-feed upload + validation + error report) | P0 | F-32.1 | **SCR-AP-016** (`web_admin.html`) | transit-svc `POST /admin/transit/gtfs/uploads`, `GET …/uploads/{id}`, `GET …/uploads/{id}/report` | transit.gtfs_feed_versions (+ validation_report JSONB, sha256 dedupe) | BR-32.1 | I-32.1 | [NEW] | AL-54; supersedes raw `/gtfs-import` |
| US-28.2 (preview + atomic activate) | P0 | F-32.1 | SCR-AP-016 | `POST …/uploads/{id}/activate` (Idempotency-Key) | transit_staging.gtfs_* → transactional swap; one `active` (partial-unique) | BR-32.2 | I-32.1 | [NEW] | AL-54; transit-svc cache reload ≤ 60 s |
| US-28.3 (version history + rollback + download) | P1 | F-32.1 | SCR-AP-016 | `GET …/versions`, `GET …/versions/{id}/download` | gtfs_feed_versions (archived rows re-activatable; zips ≥ 12 mo) | BR-32.3 | I-32.1 | [NEW] | AL-54 |
| Epic 28 premise (full feed at launch) | — | F-32.1 | SCR-PA/PI-009 (degradation → safety net) | — | — | BR-32.4 | I-32.2 | [ADAPT] | AL-55/AL-56; feed externally provided (launch + refreshes) — acquisition plan retired 2026-07-23 |

**Resolved (was P2/deferred or open):** **D-DRIFT-1 vehicle taxonomy — RESOLVED (AL-09)**: one canonical
set, car→sedan, Flex/Mini Van have own fare rows in `fares.tariffs` (no more "overridable assumption").
US-7.13 saved addresses promoted into **Epic 22** (P1). **fleet-svc → Phase 1** (was Phase 2). Reseller
admin surfaces are now in the **Admin Portal** (drivers use the Driver App). **Mode B subscription payments
(Epic 23) added** (AL-24) — passenger→fleet-owner pass-through, not platform revenue.

**Phase-B gate:** with this matrix in place, the methodology's B-rollup gate item is **satisfied**
(zero unmapped P0/P1 stories). **Ready for Phase C → C0-planner.**

*Rollup of D1′–D7′ traceability addenda. Source spec docs unmodified by this file.*
