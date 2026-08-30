/**
 * The Sinhala terminology this surface is written in — decided once, here, before
 * any prose was translated (S12).
 *
 * **This is a translator's aid and renders nowhere.** It exports no `MessageKey`,
 * it is imported by no page, and `check-i18n-parity.mjs` never sees it. Its job is
 * to stop the next person who edits `src/i18n/messages/si.ts` from inventing a
 * second Sinhala word for something the apps already name — and to record, for the
 * native reviewer S12's handoff asks for, *which* app string each choice came from
 * so that a disagreement can be argued against a source rather than a preference.
 *
 * ## The rule that produced these
 *
 * S12: "Where the app itself already ships a Sinhala string for a concept, **use
 * the app's word.**" A marketing site that invents a different word for *standby*
 * than the driver app uses teaches the wrong term to the person who is about to
 * open the driver app. So every row below names its source, and the handful with
 * no source are marked `composed` rather than dressed up as inherited.
 *
 * ## Three places the apps disagree with each other
 *
 * These are real platform inconsistencies, not translation choices, and they are
 * recorded here because a site that has to pick one is the place they become
 * visible. None is fixable from this component — each is a micro-change-set
 * against the app resources.
 *
 *  1. **Mode A/B/C.** `ක්‍රමය` (fleet + admin portals, 63 uses, and the driver
 *     app's own tracker copy) · `ප්‍රකාරය` (passenger app, 6 uses) · `මාදිලි`
 *     (driver app onboarding, 5 uses). This site uses **`ක්‍රමය`** — the plurality,
 *     and already the word in this table's own `www.screens.pa006.caption`.
 *  2. **mini van / mini truck.** `මිනි` (driver app + fleet portal) ·
 *     `කුඩා` (passenger app). This site uses **`මිනි`**, because the place these
 *     two names are read here is the daily-fee table, which is driver-facing.
 *  3. **three-wheeler.** `ත්‍රීරෝද` (driver app + fleet portal) · `ත්‍රිරෝද`
 *     (passenger app). This site uses **`ත්‍රීරෝද`**, which is also what
 *     `www.screens.pa010.caption` already said before this session.
 *
 * ## One formatting rule, and it is load-bearing
 *
 * Where a row cites a `www.*` message key it is written **unquoted** — `from:
 * 'profile_trip_ratings; www.screens.da019.caption'` and never `'…'` around the
 * key itself. `check-i18n-parity.mjs`'s orphan check marks a key referenced when
 * it finds the key *inside quotes* anywhere under `src/`, so a quoted citation
 * here would make a genuinely dead caption look alive and silently disarm the
 * check. Verified: no key in `en.ts` is satisfied by this file.
 *
 * @see build/prompts/C134-www/S12-translate-sinhala.md
 */

/** Where a term was taken from, so a reviewer can check it rather than trust it. */
export type GlossarySource =
  | 'driver-android'
  | 'passenger-android'
  | 'fleet-portal'
  | 'admin-portal'
  | 'web-passenger'
  | 'www'
  | 'composed';

export interface GlossaryEntry {
  /** The English term as it appears in `en.ts`. */
  readonly en: string;
  /** The Sinhala this site uses for it, everywhere, without exception. */
  readonly si: string;
  /** Where that Sinhala came from. `composed` means nothing shipped one. */
  readonly source: GlossarySource;
  /** The resource key or file the Sinhala was taken from, where there is one. */
  readonly from?: string;
  /** Why, when the choice was not obvious — including the words *not* chosen. */
  readonly note?: string;
}

/**
 * The terms S12 was required to fix, in the order that prompt lists them, plus the
 * ones the corpus turned out to need. Everything here is used consistently in
 * `si.ts`; a Sinhala string in that file using a different word for one of these is
 * a defect, not a stylistic variant.
 */
export const GLOSSARY_SI: readonly GlossaryEntry[] = [
  // --- the three modes ------------------------------------------------------
  {
    en: 'Mode A',
    si: 'A ක්‍රමය',
    source: 'fleet-portal',
    from: "'fleet.vehicles.mode.a'",
    note: 'Passenger app says "A ප්‍රකාරය" and driver app onboarding "මාදිලි C". See the header — ක්‍රමය is the plurality and already this table\'s word in pa006.',
  },
  { en: 'Mode B', si: 'B ක්‍රමය', source: 'fleet-portal', from: "'fleet.vehicles.mode.b'" },
  {
    en: 'Mode C',
    si: 'C ක්‍රමය',
    source: 'fleet-portal',
    from: "'fleet.nav.group.subscribers' and passim",
  },
  {
    en: 'public transport',
    si: 'මහජන ප්‍රවාහනය',
    source: 'fleet-portal',
    from: "'fleet.vehicles.mode.a'",
  },
  {
    en: 'private vehicle (Mode B)',
    si: 'පෞද්ගලික වාහනය',
    source: 'passenger-android',
    from: 'mode_b, mode_b_request_title',
  },

  // --- the driver's day -----------------------------------------------------
  {
    en: 'go on standby / online',
    si: 'සබැඳි වන්න · සබැඳියි',
    source: 'driver-android',
    from: 'home_online, home_offline, home_offline_hint',
    note: 'The app labels the toggle ONLINE/OFFLINE, so the *action* is සබැඳි වීම. Not "පොරොත්තුවෙන්", which is the vehicle class below.',
  },
  {
    en: 'standby vehicle',
    si: 'පොරොත්තු වාහනය',
    source: 'passenger-android',
    from: 'booking_standby, booking_private_section',
    note: 'The driver app calls this class "රියදුරු වාහනය" (vehicle_onboard_mode_c_title), which collides with රියදුරු = driver. The passenger app\'s පොරොත්තුවෙන් names the concept and does not collide.',
  },
  {
    en: 'offer (a ride offer)',
    si: 'ගමන් ඉල්ලීම',
    source: 'driver-android',
    from: 'push_channel_rides_name; www.screens.da014.caption',
  },
  {
    en: 'hire',
    si: 'කුලිය',
    source: 'driver-android',
    from: 'onboarding_slide_dispatch_title, onboarding_slide_earn_body',
  },
  {
    en: 'job board',
    si: 'රැකියා පුවරුව',
    source: 'driver-android',
    from: 'job_board_level_gate, scheduled_empty',
  },
  {
    en: 'directional travel',
    si: 'දිශානුගත ගමන්',
    source: 'driver-android',
    from: 'home_directional, offer_badge_directional',
  },
  {
    en: 'dashboard',
    si: 'උපකරණ පුවරුව',
    source: 'driver-android',
    from: 'tracker_behaviour_ignition; fleet.nav.dashboard',
  },
  { en: 'earnings', si: 'ඉපැයීම්', source: 'driver-android', from: 'earnings_title' },

  // --- what moves -----------------------------------------------------------
  {
    en: 'trip · ride · journey',
    si: 'ගමන',
    source: 'driver-android',
    from: 'journey_start, ride_finished; nav_trips',
    note: 'Sinhala uses one word where English uses three, and the apps do too. Context carries the difference: a Mode A/B ගමන is started and ended, a Mode C ගමන is booked and paid for. Do not coin a second word to preserve an English distinction the reader does not have.',
  },
  {
    en: 'scheduled ride',
    si: 'නියමිත ගමන',
    source: 'driver-android',
    from: 'scheduled_title',
    note: 'The passenger app says කාලසටහන්ගත for the same list (history_tab_scheduled). නියමිත is shorter and is what the driver app\'s own screen is called.',
  },
  { en: 'package · parcel', si: 'පාර්සලය', source: 'passenger-android', from: 'package_title' },
  {
    en: 'delivery',
    si: 'බෙදාහැරීම',
    source: 'driver-android',
    from: 'delivery_verify_pickup; www.screens.da016a.caption',
  },
  { en: 'saved place', si: 'සුරැකි ස්ථානය', source: 'passenger-android', from: 'search_saved' },

  // --- codes ----------------------------------------------------------------
  {
    en: 'start code (ride OTP)',
    si: 'ආරම්භක කේතය',
    source: 'passenger-android',
    from: "ride_start_otp; web-passenger 'web.ride.startOtpTitle'",
  },
  {
    en: 'pickup code (pickup OTP)',
    si: 'බාරගැනීමේ කේතය',
    source: 'passenger-android',
    from: 'package_pickup_otp_label',
  },
  {
    en: 'delivery code',
    si: 'බාරදීමේ කේතය',
    source: 'passenger-android',
    from: "package_delivery_otp_label; web-passenger 'web.package.otpValue'",
  },

  // --- money ----------------------------------------------------------------
  { en: 'fare', si: 'ගාස්තුව', source: 'passenger-android', from: 'package_estimate, offer_fee_note' },
  {
    en: 'daily platform fee',
    si: 'දෛනික ගාස්තුව',
    source: 'driver-android',
    from: 'home_daily_fee_due, earnings_daily_fee',
  },
  { en: 'wallet', si: 'පසුම්බිය', source: 'driver-android', from: 'nav_wallet' },
  {
    en: 'top up',
    si: 'ණය පිරවීම',
    source: 'driver-android',
    from: 'wallet_top_up_title, wallet_kind_topup',
    note: 'The passenger app says "මුදල් එක් කරන්න" (payment_top_up) for the passenger balance. Top-up on this site is almost always the driver wallet, so the driver app\'s noun wins; where the passenger balance is meant the copy says මුදල් එක් කිරීම.',
  },
  {
    en: 'commission',
    si: 'කොමිස්',
    source: 'admin-portal',
    from: "'admin.config.vouchers.percent', 'admin.finance.transfer.*'",
    note: 'Load-bearing: "zero commission" is the platform\'s central public claim and must read as the same word every time it is made.',
  },
  {
    en: 'surcharge',
    si: 'අධිභාරය',
    source: 'admin-portal',
    from: "'admin.directory.payment.surcharge'",
  },
  {
    en: 'tier (voucher / fee band)',
    si: 'ස්තරය',
    source: 'admin-portal',
    from: "'nav.voucherTiers'",
    note: 'Kept apart from මට්ටම (level) on purpose — a driver has a මට්ටම and a voucher has a ස්තරය, and collapsing them would make "level 3" and "the Rs 5,000 tier" the same phrase.',
  },
  { en: 'subscription', si: 'දායකත්වය', source: 'passenger-android', from: 'drawer_subscriptions' },
  {
    en: 'LankaQR · OnePay',
    si: 'LankaQR · OnePay',
    source: 'www',
    note: 'Brand names, Latin, untranslated — S12 fence. The driver app\'s payment_lankaqr label ("ඔබේ බැංකු QR") is a description of a method, not the brand, and is not used as a name here.',
  },

  // --- becoming a driver ----------------------------------------------------
  {
    en: 'register / onboard a vehicle',
    si: 'වාහනයක් ලියාපදිංචි කිරීම',
    source: 'driver-android',
    from: 'vehicle_onboard_registration_label; www.screens.da004.caption',
  },
  {
    en: 'verification',
    si: 'සත්‍යාපනය',
    source: 'fleet-portal',
    from: "'fleet.status.pending', 'fleet.status.approved'",
  },
  {
    en: 'Verification Officer',
    si: 'සත්‍යාපන නිලධාරී',
    source: 'admin-portal',
    from: "'admin.role.verification_officer'",
    note: 'The apps shorten this to නිලධාරියෙක් ("an officer") in running text — verdict_pending_review, fleet.vehicles.slot.fieldPending — and this site does the same once the full title has been used at least once in a chapter.',
  },
  {
    en: 'insurance certificate',
    si: 'රක්ෂණ සහතිකය',
    source: 'fleet-portal',
    from: "'fleet.vehicles.doc.insurance'",
  },
  {
    en: 'revenue licence',
    si: 'ආදායම් බලපත්‍රය',
    source: 'driver-android',
    from: 'capture_target_revenue_licence, vehicle_onboard_revenue_title',
  },
  {
    en: 'route permit',
    si: 'මාර්ග බලපත්‍රය',
    source: 'driver-android',
    from: "error_mode_not_allowed; 'fleet.vehicles.doc.routePermit'",
  },
  {
    en: 'CR book',
    si: 'ලියාපදිංචි පිටපත (CR පොත)',
    source: 'fleet-portal',
    from: "'fleet.vehicles.doc.registration'",
    note: 'The parenthesis is the fleet portal\'s own, and it is kept: CR පොත is what the document is called out loud, ලියාපදිංචි පිටපත is what it is called on a form.',
  },
  {
    en: 'driver level',
    si: 'රියදුරු මට්ටම',
    source: 'driver-android',
    from: 'level_title, profile_driver_level',
  },
  {
    en: 'acceptance rate',
    si: 'පිළිගැනීමේ අනුපාතය',
    source: 'driver-android',
    from: 'level_acceptance (පිළිගැනීම)',
    note: 'The app shows the bare noun as a stat label; running prose needs අනුපාතය to say it is a rate.',
  },
  { en: 'no-show', si: 'නොපැමිණීම', source: 'driver-android', from: 'level_no_shows' },
  {
    en: 'rating',
    si: 'ශ්‍රේණිගත කිරීම',
    source: 'driver-android',
    from: 'profile_trip_ratings; www.screens.da019.caption',
  },

  // --- fleets ---------------------------------------------------------------
  {
    en: 'fleet',
    si: 'වාහන සමූහය',
    source: 'fleet-portal',
    from: "'fleet.tagline'",
    note: 'The admin portal also says රථ සමූහ (admin.directory.column.ownerFleet) and this table\'s own nav label is the shorter "රථ හිමියන් සඳහා" — a nav-width choice made in S07 and left alone. Body prose uses වාහන සමූහය, which is the word the fleet owner meets in the portal itself.',
  },
  {
    en: 'fleet owner',
    si: 'වාහන සමූහ හිමිකරු',
    source: 'admin-portal',
    from: "'admin.role.fleet_owner'",
  },
  {
    en: 'GPS tracker',
    si: 'GPS ට්‍රැකරය',
    source: 'driver-android',
    from: 'tracker_title, menu_tracker_pairing',
  },

  // --- safety and data ------------------------------------------------------
  {
    en: 'SOS / emergency help',
    si: 'හදිසි උදව්',
    source: 'passenger-android',
    from: 'ride_sos, sos_title',
    note: 'The three letters SOS are kept where they are a label on a button (the passenger app keeps them too); the words around them are හදිසි උදව්. The driver app says හදිසි ඇමතුම for its own button and that difference is left where it is.',
  },
  {
    en: 'emergency contact',
    si: 'හදිසි සම්බන්ධතාව',
    source: 'passenger-android',
    from: 'edit_profile_sos_contacts, sos_no_contact',
  },
  {
    en: 'report (a driver)',
    si: 'පැමිණිල්ල',
    source: 'driver-android',
    from: 'level_reports_warning (මගී පැමිණිලි)',
    note: 'Distinct from වාර්තා කිරීම, which the passenger app uses for the *act* of reporting (trip_details_report). Three පැමිණිලි drop a level; the act of raising one is ගැටලුවක් වාර්තා කිරීම.',
  },
  {
    en: 'data rights (PDPA)',
    si: 'දත්ත අයිතිවාසිකම්',
    source: 'admin-portal',
    from: "'nav.pdpa'; www.nav.legal.pdpa",
  },
  {
    en: 'erasure',
    si: 'මැකීම',
    source: 'passenger-android',
    from: 'settings_delete_title, settings_delete_requested',
  },
  {
    en: 'data export (a copy of your data)',
    si: 'ඔබේ දත්තවල පිටපතක්',
    source: 'composed',
    note: 'No app ships this yet — the passenger app has erasure and no export screen. Built from the admin portal\'s බාගන්න (download) family rather than coined: "පිටපතක්" is the word the passenger app already uses for a document copy.',
  },

  // --- things the site says that no app says --------------------------------
  {
    en: 'live map',
    si: 'සජීවී සිතියම',
    source: 'fleet-portal',
    from: "'fleet.nav.map'",
  },
  {
    en: 'upfront fare',
    si: 'කලින් දන්වන ගාස්තුව',
    source: 'passenger-android',
    from: 'booking_standby',
    note: 'The passenger app already has this exact phrase on the standby booking row. The site\'s single most repeated marketing claim therefore costs the reader no new vocabulary.',
  },
  {
    en: 'open maps (OpenStreetMap)',
    si: 'විවෘත සිතියම් · OpenStreetMap',
    source: 'composed',
    note: 'OpenStreetMap is a brand and stays Latin (S12 fence). විවෘත සිතියම් is the descriptive phrase around it.',
  },
];

/** The ten vehicle types, in the order `src/content/marketing.ts` lists them. */
export const VEHICLE_TYPES_SI: readonly GlossaryEntry[] = [
  { en: 'Bus', si: 'බස් රථය', source: 'driver-android', from: 'vehicle_type_bus' },
  { en: 'Train', si: 'දුම්රිය', source: 'driver-android', from: 'vehicle_type_train' },
  { en: 'Motorbike', si: 'යතුරුපැදිය', source: 'driver-android', from: 'vehicle_type_motorbike' },
  {
    en: 'Three-wheeler',
    si: 'ත්‍රීරෝද රථය',
    source: 'driver-android',
    from: 'vehicle_type_three_wheeler',
    note: 'Passenger app spells it ත්‍රිරෝද. See the header.',
  },
  { en: 'Flex', si: 'ෆ්ලෙක්ස්', source: 'driver-android', from: 'vehicle_type_flex' },
  { en: 'Sedan', si: 'සෙඩාන් රථය', source: 'driver-android', from: 'vehicle_type_sedan' },
  {
    en: 'Mini van',
    si: 'මිනි වෑන් රථය',
    source: 'driver-android',
    from: 'vehicle_type_mini_van',
    note: 'Passenger app says කුඩා වෑන් රථය. See the header.',
  },
  { en: 'Van', si: 'වෑන් රථය', source: 'driver-android', from: 'vehicle_type_van' },
  { en: 'Truck', si: 'ට්‍රක් රථය', source: 'driver-android', from: 'vehicle_type_truck' },
  {
    en: 'Mini truck',
    si: 'මිනි ට්‍රක් රථය',
    source: 'driver-android',
    from: 'vehicle_type_mini_truck',
  },
];

/**
 * Not translated, ever, and the reason for each. These are the S12 fences written
 * as data so that a later session can read them without opening the prompt.
 */
export const NEVER_TRANSLATED_SI = {
  slugs:
    'URLs stay Latin and stable (S07). `/si/guide/passenger/install-and-first-run` is Sinhala content at an English slug; localising it would triple the route table and break hreflang reciprocity.',
  brands: 'MageRide, LankaQR, OnePay, Google Maps, OpenStreetMap, Namma Yatri.',
  numbers:
    'Currency, counts and dates are formatted by `Intl` from constants in `src/content/`, never written into a string. A number inside a translated string is a number nobody can check and three places to get it wrong.',
  placeholders:
    '`{count}` and the rest appear in the Sinhala exactly as in the English. `scripts/check-i18n-parity.mjs` compares placeholder *sets* per key, so a dropped one fails the build rather than rendering a sentence with a hole in it.',
} as const;
