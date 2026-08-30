/**
 * The screen registry — which of the 202 approved wireframe screens this site
 * publishes, what each one is called, and which guide chapters show it.
 *
 * `specs/wireframes/*.html` is the team-approved structural baseline for every
 * MageRide surface. It describes itself as *"Mid-fidelity, self-contained HTML keyed
 * to the D2′ §0.2 design tokens"*, and MCS-34 D10 chose to re-render those frames
 * through a polish stylesheet and composite them into device frames rather than
 * either shipping raw mid-fidelity pictures or waiting on real app screenshots
 * (which need a Mac for iOS and seeded state for every shot — `CLAUDE.md`, Build
 * Host). **The captions must say so.** These are faithful renderings of approved
 * screens, not photographs of a shipped app, and a public site that implies
 * otherwise is making a claim it cannot support.
 *
 * This module is data only. `scripts/capture-screens.mjs` reads it to drive
 * Playwright; the guide and showcase pages read it to place images. Neither the
 * script nor a page decides *which* screens exist — that is curation, it is the
 * deliverable of S05, and it lives here.
 *
 * ## What was selected, and why
 *
 * **73 of 202** — 69 curated by S05, plus SCR-DA-026 added by S10 for the chapter
 * that turns on ＋ against Resume ›, plus SCR-FP-002 / 002a / 006 added by S23 for
 * the fleet guide. (S05's own prose said 68 and its per-surface comments
 * 28 + 30 + 6 + 4; the passenger group has always held 29 entries, so the stated
 * totals were one low from the start. Corrected here rather than left to be
 * rediscovered.) The selection rule, in priority order:
 *
 * 1. **Every guide chapter gets at least one screen.** All 40 chapters
 *    ({@link ./chapters.ts}) are covered — the 34 of S08–S11 and the 6 of S23; a
 *    chapter with no illustration is the failure this registry exists to prevent.
 * 2. **The four hero concepts get their striking frame in both cuts** — the live
 *    map, booking, the driver dashboard and the 15-second offer, package tracking.
 *    Marked {@link ScreenEntry.hero}.
 * 3. **Android over iOS** wherever a screen exists in both. One frame per concept,
 *    not two. The iOS twin is not listed per entry because it does not need to be:
 *    the Android and iOS files carry **identical numeric suffixes** (verified —
 *    `SCR-PA-*`/`SCR-PI-*` and `SCR-DA-*`/`SCR-DI-*` differ in no ID), so
 *    {@link iosTwin} derives it. A later session can offer an iOS cut without
 *    re-curating anything.
 * 4. **No `SCR-AP-*` at all.** The Admin Portal is internal back-office for six
 *    staff roles behind deny-by-default RBAC (AL-02, AL-06). S05's brief permits one
 *    or two on `/fleets`; none earned a place, because every admin screen that could
 *    illustrate something public — the verification queue behind driver chapter 4,
 *    say — shows staff tooling and real-looking personal records to an audience that
 *    will never sign in to it.
 *
 * The `SCR-FP-*` nine are a different case and are in: `fleet.mageride.lk` is a
 * surface a *customer* uses, fleet owners are the third end-user role, and `/fleets`
 * is a public landing page that would otherwise have nothing to show. S05 chose six
 * for that page; S23 added three more once the fleet guide gave them chapters to
 * illustrate.
 *
 * ## Appearances: light only, and this is a finding rather than a preference
 *
 * `ScreenEntry.appearances` is a list because MCS-34 asked for both, and the capture
 * script implements both. **Every entry ships `['light']`,** because the wireframes
 * cannot produce an honest dark rendering:
 *
 * - No wireframe has a dark mode — `prefers-color-scheme` and `.dark` appear **zero**
 *   times in all eight files — so dark cannot be captured by toggling anything. The
 *   route considered was injecting a `:root` override built from
 *   `@mageride/tailwind-preset`'s own dark tokens, which is why
 *   `scripts/wireframe-appearances.mjs` exists and is complete.
 * - That route does not work, and the reason is not marginal. **231 rules across the
 *   seven stylesheets hard-code a light surface hex** (`.card{background:#fff}`,
 *   `.sheet`, `.field`, `.btn-out`, `.map`'s green tiles, the status pills), and
 *   every one of the 202 frames inherits them; 44 frames add their own inline
 *   `style=` light hexes on top. A `:root` override therefore repaints the *text*
 *   dark-on-dark while leaving the *surfaces* white — rendered and inspected, it
 *   gives grey-on-white body copy that fails WCAG contrast outright.
 * - Overriding the component rules as well would mean assigning a dark value to
 *   ~250 hard-coded colours whose meaning is ambiguous (of 52 inline `color:#fff`,
 *   most are text on a coloured chip and must *stay* white). That is not a
 *   derivation from D2 — it is inventing a dark appearance the design system has
 *   never specified, on screens the team approved, and publishing it as what the app
 *   looks like.
 *
 * The upgrade path is one change, and it is not in this component: tokenise the
 * wireframes so every colour is a `var(--…)` on `:root`. The pipeline then produces
 * dark captures by flipping this field, with no script change at all.
 */

import type { GuideChapterRef } from './chapters';
import type { WwwMessageKey } from '@/i18n';

import { SCREEN_DIMENSIONS } from './screen-dimensions.ts';

/** Which wireframe file a frame is cut from — the basename under `specs/wireframes/`. */
export type WireframeFile =
  | 'passenger_android'
  | 'passenger_ios'
  | 'driver_android'
  | 'driver_ios'
  | 'web_passenger'
  | 'web_fleet'
  | 'web_admin';

/**
 * The element that *is* the frame.
 *
 * Measured in Chromium rather than taken from the plan, which assumed 375×812 and
 * 1440×900 and is wrong on both:
 *
 * | kind      | where                        | rendered size                    |
 * |-----------|------------------------------|----------------------------------|
 * | `phone`   | the four mobile files        | **320 × 680**, 9px bezel         |
 * | `mweb`    | `web_passenger`              | **330 × 616**                    |
 * | `browser` | `web_admin`, `web_fleet`     | **944 wide, height 440–1037**    |
 *
 * `browser` height is *per screen*, not per file — S06 cannot composite the portal
 * frames into one fixed mockup the way it can the phones.
 */
export type FrameKind = 'phone' | 'browser' | 'mweb';

export type Device = 'android' | 'ios' | 'web';

export type Surface = 'passenger' | 'driver' | 'fleet' | 'admin' | 'web';

export type Appearance = 'light' | 'dark';

export interface ScreenEntry {
  /** The approved screen ID, exactly as `specs/wireframes/*.html` spells it. */
  readonly id: string;
  readonly wireframe: WireframeFile;
  readonly frame: FrameKind;
  readonly device: Device;
  readonly surface: Surface;
  /** Trilingual — never a literal. The keys live in `src/i18n/messages/*.ts`. */
  readonly captionKey: WwwMessageKey;
  /** The guide chapters that show this screen. May be empty for a hero-only frame. */
  readonly chapters: readonly GuideChapterRef[];
  /**
   * The deterministic output stem — no extension, no `@2x`. S06 emits
   * `<file>.avif`, `<file>.webp`, `<file>@2x.avif`, … from it.
   *
   * **No two entries may share one.** `assertRegistryIsWellFormed` enforces it, and
   * S20's `test/content.test.ts` runs that assertion: two entries with one stem is
   * one image silently overwriting another, which looks like a wrong screenshot
   * rather than like a bug.
   */
  readonly file: string;
  /** See the module note — every entry is `['light']` today, and why. */
  readonly appearances: readonly Appearance[];
  /** One of the four hero concepts (plan §A14). Drives the home carousel. */
  readonly hero?: true;
}

/** Light only, until the wireframes are tokenised. Shared so the reason has one home. */
const LIGHT: readonly Appearance[] = ['light'];

/**
 * Where these images come from, said once.
 *
 * A caption per screen would repeat it 68 times and still not be a claim anybody
 * reads; S18 renders this once on the showcase page and the guide pages link to it.
 * Exported as a key rather than inlined at the call site so that the parity script
 * can see it referenced and so there is exactly one sentence to change if the
 * provenance ever does (real screenshots, post-launch).
 */
export const SCREEN_PROVENANCE_KEY: WwwMessageKey = 'www.screens.provenance';

/**
 * The heading each surface's group renders under in S18's gallery.
 *
 * **Partial over {@link Surface}, and the missing member is the point.** `admin` is
 * a legal surface for a `ScreenEntry` and S05 selected no `SCR-AP-*` frame; giving
 * it a heading here would be a section that can never have anything in it and a
 * fourth English string to translate for nobody. If an admin screen is ever added,
 * this map is where the compiler asks for its heading.
 *
 * The four keys are S07's, written for `PAGES.screens.sections` in the same order.
 * They are named here rather than read out of that array by index because index
 * alignment between a copy list and a data-derived one is the kind of agreement that
 * holds until somebody reorders either; `test/screens-gallery.test.ts` asserts the
 * two still say the same thing.
 */
export const SURFACE_SECTION_KEYS: Partial<Record<Surface, WwwMessageKey>> = {
  passenger: 'www.page.screens.passengerHeading',
  driver: 'www.page.screens.driverHeading',
  fleet: 'www.page.screens.fleetHeading',
  web: 'www.page.screens.webHeading',
};

/**
 * The registry. Ordered by surface, then by the wireframe's own order — which is
 * the order a user meets the screens in, so it is also a sensible gallery order for
 * S18 with no second sort key to maintain.
 */
export const SCREENS: readonly ScreenEntry[] = [
  // ---------------------------------------------------------------------------
  // Passenger · Android — 29 frames, covering all 16 passenger chapters.
  // ---------------------------------------------------------------------------
  {
    id: 'SCR-PA-001',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa001.caption',
    chapters: ['passenger/install-and-first-run'],
    file: 'pa-001-splash',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-002',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa002.caption',
    chapters: ['passenger/install-and-first-run'],
    file: 'pa-002-choose-your-language',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-003',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa003.caption',
    chapters: ['passenger/install-and-first-run'],
    file: 'pa-003-phone-and-otp',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-004',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa004.caption',
    chapters: ['passenger/install-and-first-run'],
    file: 'pa-004-profile-setup',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-005',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa005.caption',
    chapters: ['passenger/permissions'],
    file: 'pa-005-location-permission',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-006',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa006.caption',
    chapters: ['passenger/reading-the-live-map'],
    file: 'pa-006-mode-and-vehicle-filter',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-007',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa007.caption',
    chapters: ['passenger/reading-the-live-map', 'passenger/tracking-buses-and-trains'],
    file: 'pa-007-vehicle-details',
    appearances: LIGHT,
    hero: true,
  },
  {
    id: 'SCR-PA-008',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa008.caption',
    // S08 added `tracking-buses-and-trains`: the same search box starts a public
    // transport trip (D1′ F-23.1, and the Walkthrough's screen index lists this
    // screen under scenario 11 as well as scenario 4). A destination is a place
    // whichever mode answers it.
    chapters: ['passenger/tracking-buses-and-trains', 'passenger/booking-a-ride'],
    file: 'pa-008-search-a-place',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-009',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa009.caption',
    // S08 added `tracking-buses-and-trains`: this one screen carries both halves of
    // the booking result — the GTFS public routes above and the Mode C tiers below —
    // so the Mode A chapter needs it as much as the Mode C ones do.
    chapters: [
      'passenger/tracking-buses-and-trains',
      'passenger/booking-a-ride',
      'passenger/choosing-a-vehicle-and-fare',
    ],
    file: 'pa-009-book-a-ride',
    appearances: LIGHT,
    hero: true,
  },
  {
    id: 'SCR-PA-010',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa010.caption',
    chapters: ['passenger/reading-the-live-map'],
    file: 'pa-010-the-live-map',
    appearances: LIGHT,
    hero: true,
  },
  {
    id: 'SCR-PA-011',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa011.caption',
    chapters: ['passenger/booking-for-someone-else'],
    file: 'pa-011-confirm-pickup',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-012',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa012.caption',
    chapters: ['passenger/sending-a-package'],
    file: 'pa-012-send-a-package',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-013',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa013.caption',
    chapters: ['passenger/scheduling-a-ride'],
    file: 'pa-013-schedule-a-ride',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-014',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa014.caption',
    chapters: ['passenger/waiting-for-a-driver'],
    file: 'pa-014-finding-a-driver',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-015',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa015.caption',
    chapters: ['passenger/during-the-ride'],
    file: 'pa-015-your-ride-in-progress',
    appearances: LIGHT,
    hero: true,
  },
  {
    id: 'SCR-PA-016',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa016.caption',
    // S08 dropped `choosing-a-vehicle-and-fare`. The frame draws an "OnePay +5%
    // Rs 893" row, and ADD v3.6 (AL-57/AL-59) removed OnePay as a ride method
    // entirely — D2's own SCR-PA-016 paragraph now reads Cash / Wallet / Driver QR
    // and D3's `POST /fare/pay` enum agrees. So the fare chapter must not be
    // illustrated by a surcharge that no longer exists; and with no surcharge on any
    // method, the payment sheet no longer changes the fare, which is what that
    // chapter is about. The stale frame is filed as a finding against the wireframes
    // (MCS-35 Scope A, first row) rather than fixed here — re-rendering it means
    // changing an approved spec artefact.
    //
    // **S09 dropped `passenger/paying` too**, which S08 had left for it to judge.
    // Chapter 11 states in as many words that no way of paying for a ride costs
    // extra; a picture of a Rs 43 surcharge beside that sentence contradicts the
    // page in the medium a reader believes most. Same call as SCR-PA-025a, same
    // evidence, and the entry now publishes in no chapter at all.
    //
    // It still renders on `/screens`, so this is a mitigation and not a fix. The fix
    // is MCS-35 Scope A + D: edit the wireframe row-for-row, then re-run
    // `npm run screens:refresh` and commit the image in the same change.
    chapters: [],
    file: 'pa-016-payment-method',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-017',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa017.caption',
    // Kept by S09, deliberately, where SCR-PA-016 was dropped. This frame's headline
    // and primary action — "Scan driver's QR to pay", the awaiting-confirmation
    // state, "Switch to Cash" — are exactly chapter 11's subject and are correct
    // after AL-47/AL-59. Its secondary "Pay with my bank app (LankaQR)" row is the
    // platform-merchant deep link AL-59 retired for rides: a stale control rather
    // than a false price, which is a materially smaller defect than a surcharge the
    // platform does not charge. **MCS-35's Scope A table does not list this frame** —
    // it reaches SCR-PA-017 only through D2's component tables — so S09 files it as a
    // new location for that change set.
    chapters: ['passenger/paying'],
    file: 'pa-017-pay-the-fare',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-018',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa018.caption',
    chapters: ['passenger/paying'],
    file: 'pa-018-trip-summary',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-019',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa019.caption',
    chapters: ['passenger/saved-places-and-ratings'],
    file: 'pa-019-rate-your-driver',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-020',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa020.caption',
    chapters: ['passenger/sending-a-package'],
    file: 'pa-020-track-your-package',
    appearances: LIGHT,
    hero: true,
  },
  {
    id: 'SCR-PA-021',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa021.caption',
    chapters: ['passenger/sending-a-package'],
    file: 'pa-021-package-on-the-way',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-022',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa022.caption',
    chapters: ['passenger/scheduling-a-ride', 'passenger/saved-places-and-ratings'],
    file: 'pa-022-your-trips',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-024',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa024.caption',
    chapters: ['passenger/following-a-private-vehicle'],
    file: 'pa-024-ask-to-follow-a-vehicle',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-025',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa025.caption',
    chapters: ['passenger/following-a-private-vehicle', 'passenger/mode-b-payments'],
    file: 'pa-025-vehicles-you-follow',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-025a',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa025a.caption',
    // S08 dropped `mode-b-payments`, for the same reason as SCR-PA-016 and with the
    // same evidence: the frame offers "OnePay · cards / wallets · +5%", and AL-59
    // removed OnePay from Mode B subscription payment precisely because a
    // subscription is pass-through to the fleet owner and OnePay would land it in
    // MageRide's account. D2's SCR-PA-025a row carries the correction and D3's
    // `POST /mode-b/subscriptions/{id}/pay` enumerates four methods without it.
    chapters: [],
    file: 'pa-025a-subscription-payment',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-026',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa026.caption',
    // S08 added `booking-a-ride`: saved places are one of the four ways a passenger
    // sets a location, so the booking chapter shows this screen as well as the
    // chapter that owns managing them.
    chapters: ['passenger/booking-a-ride', 'passenger/saved-places-and-ratings'],
    file: 'pa-026-saved-places',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-027',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa027.caption',
    chapters: ['passenger/settings-help-and-your-data'],
    file: 'pa-027-profile-and-settings',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-029',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa029.caption',
    chapters: ['passenger/during-the-ride'],
    file: 'pa-029-emergency-help',
    appearances: LIGHT,
  },
  {
    id: 'SCR-PA-030',
    wireframe: 'passenger_android',
    frame: 'phone',
    device: 'android',
    surface: 'passenger',
    captionKey: 'www.screens.pa030.caption',
    chapters: ['passenger/settings-help-and-your-data'],
    file: 'pa-030-help-and-support',
    appearances: LIGHT,
  },

  // ---------------------------------------------------------------------------
  // Driver · Android — 31 frames, covering all 18 driver chapters.
  // ---------------------------------------------------------------------------
  {
    id: 'SCR-DA-001',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da001.caption',
    chapters: ['driver/install-and-first-run'],
    file: 'da-001-splash',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-002',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da002.caption',
    chapters: ['driver/install-and-first-run'],
    file: 'da-002-language-and-city',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-003',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da003.caption',
    chapters: ['driver/install-and-first-run'],
    file: 'da-003-phone-and-otp',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-003a',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da003a.caption',
    chapters: ['driver/install-and-first-run'],
    file: 'da-003a-driver-profile',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-004',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da004.caption',
    chapters: ['driver/onboarding-your-vehicle'],
    file: 'da-004-add-your-vehicle',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-004a',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da004a.caption',
    chapters: ['driver/onboarding-your-vehicle'],
    file: 'da-004a-insurance-details',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-005',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da005.caption',
    chapters: ['driver/photographing-documents'],
    file: 'da-005-photograph-a-document',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-006',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da006.caption',
    chapters: ['driver/approval'],
    file: 'da-006-approval-status',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-007',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da007.caption',
    chapters: ['driver/permissions-and-background-location'],
    file: 'da-007-permissions',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-010',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da010.caption',
    chapters: ['driver/your-dashboard', 'driver/going-on-standby'],
    file: 'da-010-your-dashboard',
    appearances: LIGHT,
    hero: true,
  },
  {
    id: 'SCR-DA-011',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da011.caption',
    chapters: ['driver/your-dashboard', 'driver/mode-a-and-b-driving'],
    file: 'da-011-start-and-end-a-journey',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-013',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da013.caption',
    chapters: ['driver/directional-travel'],
    file: 'da-013-directional-travel',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-014',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da014.caption',
    chapters: ['driver/the-15-second-offer'],
    file: 'da-014-a-new-ride-offer',
    appearances: LIGHT,
    hero: true,
  },
  {
    id: 'SCR-DA-015',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da015.caption',
    chapters: ['driver/running-a-trip'],
    file: 'da-015-running-a-trip',
    appearances: LIGHT,
    hero: true,
  },
  {
    id: 'SCR-DA-016a',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da016a.caption',
    chapters: ['driver/package-jobs'],
    file: 'da-016a-review-a-delivery',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-016b',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da016b.caption',
    chapters: ['driver/package-jobs'],
    file: 'da-016b-collect-the-package',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-016c',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da016c.caption',
    chapters: ['driver/package-jobs'],
    file: 'da-016c-complete-the-delivery',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-017',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da017.caption',
    // Δ S10: was `driver/package-jobs`. The job board lists **future scheduled
    // rides** within 30 km and its only action is *post intent* — US-6A.5, D2's
    // SCR-DA-017 (*"no accept here"*) and the drawn frame all agree, and none of
    // them mentions a delivery. Acceptance happens at T-30 min on SCR-DA-014, so
    // the board belongs to the chapter about that screen. The caption was wrong in
    // the same way and moved with it.
    chapters: ['driver/the-15-second-offer'],
    file: 'da-017-the-job-board',
    appearances: LIGHT,
    hero: true,
  },
  {
    id: 'SCR-DA-018',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da018.caption',
    chapters: ['driver/running-a-trip'],
    file: 'da-018-scheduled-rides',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-019',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da019.caption',
    chapters: ['driver/ratings-and-driver-level'],
    file: 'da-019-your-level-and-stats',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-020',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da020.caption',
    chapters: ['driver/getting-paid'],
    file: 'da-020-your-earnings',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-021',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da021.caption',
    chapters: ['driver/your-wallet', 'driver/the-daily-platform-fee'],
    file: 'da-021-wallet-and-daily-fee',
    appearances: LIGHT,
    hero: true,
  },
  {
    id: 'SCR-DA-022',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da022.caption',
    chapters: ['driver/your-wallet'],
    file: 'da-022-top-up-your-wallet',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-023',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da023.caption',
    chapters: ['driver/bulk-credit-and-transfers'],
    file: 'da-023-request-credit',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-024',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da024.caption',
    chapters: ['driver/bulk-credit-and-transfers'],
    file: 'da-024-transfer-credit',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-025',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da025.caption',
    chapters: ['driver/the-daily-platform-fee'],
    file: 'da-025-fee-history',
    appearances: LIGHT,
  },
  {
    // Δ S10: added. S05's first pass did not select My Vehicles, and driver chapter
    // 2 is built on the distinction MCS-06 drew there — **＋ means add** (a fresh
    // Step 1/4, unconditionally) against **Resume ›** on a row (that vehicle, at its
    // own next step). This frame is the only one that draws both, together with the
    // Incomplete/Approved states and the temporarily-assigned group, so the chapter
    // would otherwise describe three controls beside a picture of none of them.
    //
    // Note the frame is right and its `.states` prose is not: that paragraph still
    // carries the superseded US-2.27 wording. The capture is the `.phone` element
    // alone, so the published image shows only the corrected screen.
    id: 'SCR-DA-026',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da026.caption',
    chapters: ['driver/onboarding-your-vehicle'],
    file: 'da-026-my-vehicles',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-027',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da027.caption',
    chapters: ['driver/mode-a-and-b-driving'],
    file: 'da-027-pair-a-gps-tracker',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-028',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da028.caption',
    chapters: ['driver/mode-a-and-b-driving'],
    file: 'da-028-who-can-follow-you',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-032',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da032.caption',
    chapters: ['driver/safety-and-support'],
    file: 'da-032-emergency-help',
    appearances: LIGHT,
  },
  {
    id: 'SCR-DA-033',
    wireframe: 'driver_android',
    frame: 'phone',
    device: 'android',
    surface: 'driver',
    captionKey: 'www.screens.da033.caption',
    chapters: ['driver/safety-and-support'],
    file: 'da-033-help-and-support',
    appearances: LIGHT,
  },

  // ---------------------------------------------------------------------------
  // Fleet portal — 9 frames for `/fleets` and the fleet guide.
  //
  // S05 curated six and left every `chapters` array empty, because the fleet-owner
  // guide was MCS-34 **D7** and conditional: *"If it lands, its chapters attach here
  // and nothing else changes."* It landed (S23), and that is exactly what happened —
  // the six gained their chapter refs and three more frames were added, because the
  // guide documents three things a landing page never had to show: the organisation
  // KYC form, the Owner-only bank & payout screen, and tracker binding.
  //
  // **SCR-FP-002a is the one that had to be added rather than merely attached.** It
  // is the screen behind the hardest gate in the whole fleet flow — no vehicle can
  // be Service payment = Paid until the profile on it is Verified (US-27.2) — and
  // chapter 2 is a page-long explanation of a form the reader would otherwise never
  // have seen. The remaining four (008 scheduling, 009 analytics, 011 subscriptions,
  // 012 the payment ledger) stay out: they are the daily-use half of the portal, the
  // guide's six chapters are the get-it-right-once half, and a screen with no chapter
  // and no strip placement is 60 kB of nothing.
  // ---------------------------------------------------------------------------
  {
    id: 'SCR-FP-001',
    wireframe: 'web_fleet',
    frame: 'browser',
    device: 'web',
    surface: 'fleet',
    captionKey: 'www.screens.fp001.caption',
    chapters: ['fleet/registering-your-organisation'],
    file: 'fp-001-fleet-sign-up',
    appearances: LIGHT,
  },
  {
    id: 'SCR-FP-002',
    wireframe: 'web_fleet',
    frame: 'browser',
    device: 'web',
    surface: 'fleet',
    captionKey: 'www.screens.fp002.caption',
    chapters: ['fleet/registering-your-organisation'],
    file: 'fp-002-organisation-setup',
    appearances: LIGHT,
  },
  {
    id: 'SCR-FP-002a',
    wireframe: 'web_fleet',
    frame: 'browser',
    device: 'web',
    surface: 'fleet',
    captionKey: 'www.screens.fp002a.caption',
    chapters: ['fleet/kyc-and-your-payout-profile'],
    file: 'fp-002a-bank-and-payout-details',
    appearances: LIGHT,
  },
  {
    id: 'SCR-FP-003',
    wireframe: 'web_fleet',
    frame: 'browser',
    device: 'web',
    surface: 'fleet',
    captionKey: 'www.screens.fp003.caption',
    chapters: ['fleet/billing'],
    file: 'fp-003-fleet-dashboard',
    appearances: LIGHT,
    hero: true,
  },
  {
    id: 'SCR-FP-004',
    wireframe: 'web_fleet',
    frame: 'browser',
    device: 'web',
    surface: 'fleet',
    captionKey: 'www.screens.fp004.caption',
    chapters: ['fleet/adding-vehicles', 'fleet/vehicle-documents'],
    file: 'fp-004-onboard-a-vehicle',
    appearances: LIGHT,
  },
  {
    id: 'SCR-FP-005',
    wireframe: 'web_fleet',
    frame: 'browser',
    device: 'web',
    surface: 'fleet',
    captionKey: 'www.screens.fp005.caption',
    chapters: ['fleet/assigning-drivers-and-trackers'],
    file: 'fp-005-assign-your-drivers',
    appearances: LIGHT,
  },
  {
    id: 'SCR-FP-006',
    wireframe: 'web_fleet',
    frame: 'browser',
    device: 'web',
    surface: 'fleet',
    captionKey: 'www.screens.fp006.caption',
    chapters: ['fleet/assigning-drivers-and-trackers'],
    file: 'fp-006-bind-a-tracker',
    appearances: LIGHT,
  },
  {
    id: 'SCR-FP-007',
    wireframe: 'web_fleet',
    frame: 'browser',
    device: 'web',
    surface: 'fleet',
    captionKey: 'www.screens.fp007.caption',
    chapters: [],
    file: 'fp-007-live-fleet-map',
    appearances: LIGHT,
    hero: true,
  },
  {
    id: 'SCR-FP-010',
    wireframe: 'web_fleet',
    frame: 'browser',
    device: 'web',
    surface: 'fleet',
    captionKey: 'www.screens.fp010.caption',
    chapters: ['fleet/billing'],
    file: 'fp-010-billing-and-wallet',
    appearances: LIGHT,
  },

  // ---------------------------------------------------------------------------
  // The no-login web subview (`passenger.mageride.lk`, AL-04 / URD Epic 25) —
  // 4 frames. This is the surface a package recipient without the app actually
  // sees, so it belongs in the passenger guide even though it is not the app.
  // ---------------------------------------------------------------------------
  {
    id: 'SCR-WT-001',
    wireframe: 'web_passenger',
    frame: 'mweb',
    device: 'web',
    surface: 'web',
    captionKey: 'www.screens.wt001.caption',
    chapters: ['passenger/booking-for-someone-else'],
    file: 'wt-001-tracking-link',
    appearances: LIGHT,
  },
  {
    id: 'SCR-WT-002',
    wireframe: 'web_passenger',
    frame: 'mweb',
    device: 'web',
    surface: 'web',
    captionKey: 'www.screens.wt002.caption',
    chapters: ['passenger/sending-a-package'],
    file: 'wt-002-track-without-the-app',
    appearances: LIGHT,
  },
  {
    id: 'SCR-WT-003',
    wireframe: 'web_passenger',
    frame: 'mweb',
    device: 'web',
    surface: 'web',
    captionKey: 'www.screens.wt003.caption',
    chapters: ['passenger/booking-for-someone-else'],
    file: 'wt-003-confirm-a-pickup',
    appearances: LIGHT,
  },
  {
    id: 'SCR-WT-005',
    wireframe: 'web_passenger',
    frame: 'mweb',
    device: 'web',
    surface: 'web',
    captionKey: 'www.screens.wt005.caption',
    chapters: ['passenger/sending-a-package'],
    file: 'wt-005-package-delivered',
    appearances: LIGHT,
  },
];

/** The hero frames, in registry order — what the home carousel draws from (S14). */
export const HERO_SCREENS: readonly ScreenEntry[] = SCREENS.filter((screen) => screen.hero);

/**
 * The pixel size of a screen's 1× plate.
 *
 * **Measured, never assumed** — `portals/www/CLAUDE.md`'s `next/image` contract
 * says to "read the real numbers rather than assuming a constant", and S14
 * confirmed that is not a hypothetical: the committed output holds **eight
 * distinct plate sizes**, and the phone frames alone split **34 at 416×777 and 26
 * at 416×776**. A constant would have given the wrong aspect ratio to 26 screens.
 *
 * The map is generated by `scripts/screen-dimensions.mjs` from the committed 1×
 * WebPs and re-generated by `npm run screens:refresh`. It is a build-time import,
 * so a stem missing from it is a **type error at the call site** rather than a
 * broken image at runtime.
 */
export function plateSize(screen: ScreenEntry): { readonly width: number; readonly height: number } {
  const size = SCREEN_DIMENSIONS[screen.file];

  if (!size) {
    throw new Error(
      `screens.ts: no recorded plate size for "${screen.file}" — run \`npm run screens:dimensions\``,
    );
  }

  return size;
}

/** Every screen a chapter shows, in registry order. */
export function screensForChapter(ref: GuideChapterRef): readonly ScreenEntry[] {
  return SCREENS.filter((screen) => screen.chapters.includes(ref));
}

/**
 * The iOS twin of an Android screen ID, or `null` if there is not one.
 *
 * A derivation rather than 68 hand-copied fields, because the two files agree:
 * `SCR-PA-*` and `SCR-PI-*` carry an identical set of numeric suffixes, as do
 * `SCR-DA-*` and `SCR-DI-*`. A field would be 68 chances to mistype a string that
 * a one-line rule already knows.
 */
export function iosTwin(id: string): string | null {
  const match = /^SCR-(PA|DA)-(.+)$/.exec(id);
  if (!match) return null;
  return `SCR-${match[1] === 'PA' ? 'PI' : 'DI'}-${match[2]}`;
}

/**
 * The registry's own invariants, as one callable assertion.
 *
 * Lives here rather than in the test file because both the test (S20) and
 * `scripts/capture-screens.mjs` need it, and a rule enforced in only one of the two
 * is a rule the other can violate. Throws rather than returning findings: every one
 * of these is a typo, and there is nothing to do with a half-valid registry.
 */
export function assertRegistryIsWellFormed(): void {
  const seenIds = new Set<string>();
  const seenFiles = new Set<string>();

  for (const screen of SCREENS) {
    if (seenIds.has(screen.id)) throw new Error(`screens.ts: duplicate id ${screen.id}`);
    seenIds.add(screen.id);

    if (seenFiles.has(screen.file)) {
      throw new Error(
        `screens.ts: two entries share the file stem "${screen.file}" — one image would ` +
          'overwrite the other',
      );
    }
    seenFiles.add(screen.file);

    if (screen.appearances.length === 0) {
      throw new Error(`screens.ts: ${screen.id} lists no appearance, so nothing would be captured`);
    }
  }
}
