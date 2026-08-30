/**
 * The marketing copy corpus — everything S14–S18 renders, structured here so those
 * sessions compose rather than author.
 *
 * Content modules hold **keys**; the strings live in `src/i18n/messages/*.ts`. What
 * lives here is the shape, the ordering, and — the part that matters — **the
 * numbers and their anchors**.
 *
 * ## The one rule this file exists to enforce
 *
 * README rule 7: *every public claim carries a spec anchor in the content module
 * that makes it.* A fee, a tier, a vehicle count or a "first trip free" on a public
 * site is a factual assertion about a real service that real drivers will make
 * decisions on. So no number is written inline in a message string where nobody can
 * check it — every one is a constant here, with the URD line it came from, and
 * `test/content.test.ts` (S20) asserts the six fee tiers against the URD directly.
 */

import type { WwwMessageKey } from '@/i18n';
import type { RouteGroup } from '@/lib/routes';

// ---------------------------------------------------------------------------
// The daily platform fee — the six Mode C tiers
// ---------------------------------------------------------------------------

/**
 * The six daily platform fee tiers, **in minor units** (cents of a Rupee).
 *
 * Minor units because `CLAUDE.md`'s Universal Rules say so — "all currency values
 * stored and transmitted as integers (cents/paisa)" — and a marketing page is not
 * the place to make the one exception. Rendered through `Intl.NumberFormat` at the
 * point of display, never string-concatenated.
 *
 * **The URD states these four times and all four agree**, which is why they can be
 * asserted rather than trusted:
 *
 * | Where | What it says |
 * |---|---|
 * | §1 Product Vision | "Motorbike Rs 50, Three-wheeler Rs 100, Flex Rs 150, Sedan Rs 200, Mini Van Rs 250, Van Rs 300" |
 * | Epic 9 · US-9.1 | "Public Bus = Free, Motorbike = Rs 50, … Van = Rs 300" |
 * | Epic 9 · Daily Platform Fee Structure | the per-vehicle table, with "first trip free" per row |
 * | Glossary · "Daily Platform Fee" | the same six, plus "Public buses (Mode A) = Free" |
 *
 * `vehicleType` is the canonical `registry.vehicles.vehicle_type` value from URD
 * §1.B — not a display name — so the table joins to the backend enum and to
 * `@mageride/tailwind-preset`'s vehicle colours without a second mapping to keep.
 */
export interface FeeTier {
  readonly vehicleType: string;
  readonly label: WwwMessageKey;
  /** Cents of a Rupee. Rs 50 → 5000. */
  readonly dailyFeeMinor: number;
}

export const DAILY_FEE_TIERS: readonly FeeTier[] = [
  { vehicleType: 'motorbike', label: 'www.fees.tier.motorbike', dailyFeeMinor: 5_000 },
  { vehicleType: 'three_wheeler', label: 'www.fees.tier.threeWheeler', dailyFeeMinor: 10_000 },
  { vehicleType: 'flex', label: 'www.fees.tier.flex', dailyFeeMinor: 15_000 },
  { vehicleType: 'sedan', label: 'www.fees.tier.sedan', dailyFeeMinor: 20_000 },
  { vehicleType: 'mini_van', label: 'www.fees.tier.miniVan', dailyFeeMinor: 25_000 },
  { vehicleType: 'van', label: 'www.fees.tier.van', dailyFeeMinor: 30_000 },
];

export const DAILY_FEE_SOURCE =
  'specs/user-requirements-document.md#daily-platform-fee-structure-namma-yatri-methodology';

/**
 * Where the free-first-trip sentence on `/drivers` is quoted **from** (S16).
 *
 * A constant rather than a string in the page, for two reasons. The
 * `mageride/no-literal-user-facing-strings` rule refuses a literal in JSX — and it
 * is right to here, even though a spec path is not prose: an anchor rendered beside
 * a quotation is part of the claim, and it belongs in the content layer with every
 * other anchor rather than in markup where nobody looks for it.
 *
 * §1 Product Vision, and not Epic 9 or the Glossary, because those restate the rule
 * while §1 is where the platform *defines* it — and S16 asks for §1 by name.
 */
export const FREE_FIRST_TRIP_SOURCE = 'specs/user-requirements-document.md#1-product-vision';

/** The quoted sentence itself. Verbatim §1 — see the key's note in `en.ts`. */
export const FREE_FIRST_TRIP_QUOTE_KEY: WwwMessageKey = 'www.page.drivers.freeFirstTripQuote';

/**
 * Mode A pays nothing and Mode B pays monthly — the two rows that are *not* in the
 * tier table above, and are needed beside it so the table is not read as "what
 * everybody pays".
 *
 * **Mode B's figure is deliberately not a constant.** The URD says "**approximately**
 * Rs 300" and "a monthly charge of approximately Rs 300" in both places it appears.
 * An approximate price rendered as a precise one is a false claim with a decimal
 * point on it, so the copy says "about Rs 300 a month" and the exact figure is a
 * content gap for whoever sets the real price.
 */
export const MODE_A_FEE_KEY: WwwMessageKey = 'www.fees.modeA';
export const MODE_B_FEE_KEY: WwwMessageKey = 'www.fees.modeB';

// ---------------------------------------------------------------------------
// Hero — four slides
// ---------------------------------------------------------------------------

export interface HeroSlide {
  readonly id: string;
  readonly headline: WwwMessageKey;
  readonly sub: WwwMessageKey;
  readonly primaryCta: WwwMessageKey;
  readonly secondaryCta: WwwMessageKey;
  /** `ScreenEntry.id`s — the frames S06 composited, drawn from `HERO_SCREENS`. */
  readonly screens: readonly string[];
}

export const HERO_SLIDES: readonly HeroSlide[] = [
  {
    id: 'track',
    headline: 'www.hero.track.headline',
    sub: 'www.hero.track.sub',
    primaryCta: 'www.cta.getTheApp',
    secondaryCta: 'www.cta.seeHowItWorks',
    screens: ['SCR-PA-010', 'SCR-PA-007'],
  },
  {
    id: 'book',
    headline: 'www.hero.book.headline',
    sub: 'www.hero.book.sub',
    primaryCta: 'www.cta.getTheApp',
    secondaryCta: 'www.cta.passengerGuide',
    screens: ['SCR-PA-009', 'SCR-PA-015'],
  },
  {
    id: 'drivers',
    headline: 'www.hero.drivers.headline',
    sub: 'www.hero.drivers.sub',
    primaryCta: 'www.cta.driveWithUs',
    secondaryCta: 'www.cta.seeTheFees',
    screens: ['SCR-DA-010', 'SCR-DA-014', 'SCR-DA-021'],
  },
  {
    id: 'deliver',
    headline: 'www.hero.deliver.headline',
    sub: 'www.hero.deliver.sub',
    primaryCta: 'www.cta.getTheApp',
    secondaryCta: 'www.cta.passengerGuide',
    screens: ['SCR-PA-020', 'SCR-DA-017'],
  },
];

// ---------------------------------------------------------------------------
// The three modes
// ---------------------------------------------------------------------------

/**
 * Mode A, Mode B, Mode C — one paragraph each.
 *
 * **The service boundary is load-bearing and the copy must respect it.**
 * `CLAUDE.md`: *ride-svc owns Mode C (on-demand); trip-state-svc owns Mode A/B
 * (scheduled). Never cross this boundary.* On a marketing page that translates into
 * a plain-English rule: these are **three different things you can do**, not one
 * feature with a switch on it. Copy that says "toggle between live tracking and
 * booking" describes an app MageRide is not, and would set an expectation the
 * product cannot meet.
 *
 * The user-visible difference is sharper than the architectural one anyway: Mode A
 * is free to watch and you pay nothing ever; Mode B needs someone's permission and
 * may cost a monthly subscription; Mode C is a fare per trip, shown before you book.
 */
/**
 * Which of the three services something belongs to.
 *
 * Named rather than left inline on {@link TransportMode.id} because S18's screen
 * gallery filters by it — `?mode=c` — and a facet spelled `'a' | 'b' | 'c'` in two
 * modules is two places to add a Mode D. `src/content/screen-modes.ts` is the other
 * reader.
 */
export type TransportModeId = 'a' | 'b' | 'c';

export interface TransportMode {
  readonly id: TransportModeId;
  readonly name: WwwMessageKey;
  readonly tagline: WwwMessageKey;
  readonly body: WwwMessageKey;
  readonly source: string;
  readonly screens: readonly string[];
}

export const TRANSPORT_MODES: readonly TransportMode[] = [
  {
    id: 'a',
    name: 'www.modes.a.name',
    tagline: 'www.modes.a.tagline',
    body: 'www.modes.a.body',
    source: 'specs/user-requirements-document.md#1-a-service-modes',
    screens: ['SCR-PA-007'],
  },
  {
    id: 'b',
    name: 'www.modes.b.name',
    tagline: 'www.modes.b.tagline',
    body: 'www.modes.b.body',
    source: 'specs/user-requirements-document.md#1-a-service-modes',
    screens: ['SCR-PA-024', 'SCR-PA-025'],
  },
  {
    id: 'c',
    name: 'www.modes.c.name',
    tagline: 'www.modes.c.tagline',
    body: 'www.modes.c.body',
    source: 'specs/user-requirements-document.md#1-a-service-modes',
    screens: ['SCR-PA-009', 'SCR-PA-015'],
  },
];

// ---------------------------------------------------------------------------
// How it works — four steps, two cuts
// ---------------------------------------------------------------------------

export interface HowItWorksStep {
  readonly title: WwwMessageKey;
  readonly body: WwwMessageKey;
  readonly screenRef?: string;
}

export const HOW_IT_WORKS_PASSENGER: readonly HowItWorksStep[] = [
  { title: 'www.how.p1.title', body: 'www.how.p1.body', screenRef: 'SCR-PA-003' },
  { title: 'www.how.p2.title', body: 'www.how.p2.body', screenRef: 'SCR-PA-010' },
  { title: 'www.how.p3.title', body: 'www.how.p3.body', screenRef: 'SCR-PA-009' },
  { title: 'www.how.p4.title', body: 'www.how.p4.body', screenRef: 'SCR-PA-017' },
];

export const HOW_IT_WORKS_DRIVER: readonly HowItWorksStep[] = [
  { title: 'www.how.d1.title', body: 'www.how.d1.body', screenRef: 'SCR-DA-004' },
  { title: 'www.how.d2.title', body: 'www.how.d2.body', screenRef: 'SCR-DA-006' },
  { title: 'www.how.d3.title', body: 'www.how.d3.body', screenRef: 'SCR-DA-010' },
  { title: 'www.how.d4.title', body: 'www.how.d4.body', screenRef: 'SCR-DA-014' },
];

// ---------------------------------------------------------------------------
// Feature splits — five, headline + ~60 words
// ---------------------------------------------------------------------------

export interface FeatureSplit {
  readonly id: string;
  readonly headline: WwwMessageKey;
  readonly body: WwwMessageKey;
  readonly source: string;
  readonly screens: readonly string[];
}

export const FEATURE_SPLITS: readonly FeatureSplit[] = [
  {
    id: 'live-map',
    headline: 'www.feature.liveMap.headline',
    body: 'www.feature.liveMap.body',
    source: 'specs/user-requirements-document.md#1-b-canonical-vehicle-types',
    screens: ['SCR-PA-010', 'SCR-PA-006'],
  },
  {
    id: 'upfront-fare',
    headline: 'www.feature.upfrontFare.headline',
    body: 'www.feature.upfrontFare.body',
    source: 'specs/user-requirements-document.md#1-product-vision',
    screens: ['SCR-PA-009', 'SCR-PA-016'],
  },
  {
    id: 'packages',
    headline: 'www.feature.packages.headline',
    body: 'www.feature.packages.body',
    source: 'specs/user-requirements-document.md#epic-20',
    screens: ['SCR-PA-012', 'SCR-PA-020', 'SCR-WT-002'],
  },
  {
    id: 'safety',
    headline: 'www.feature.safety.headline',
    body: 'www.feature.safety.body',
    source: 'specs/user-requirements-document.md#epic-12',
    screens: ['SCR-PA-029', 'SCR-PA-015'],
  },
  {
    id: 'trilingual',
    headline: 'www.feature.trilingual.headline',
    body: 'www.feature.trilingual.body',
    source: 'CLAUDE.md#universal-rules',
    screens: ['SCR-PA-002'],
  },
];

// ---------------------------------------------------------------------------
// Stats
// ---------------------------------------------------------------------------

/**
 * The four numbers on the stats band.
 *
 * **`vehicleTypes` is 10, not 11**, and that correction is the reason this is a
 * constant rather than a string. S07's brief asks for "11 vehicle types"; the
 * authoritative enumeration is URD §1.B and the backend's
 * `Registry.Api/Domain/VehicleTypes.cs`, and both list **ten**: motorbike,
 * three_wheeler, flex, sedan, mini_van, van, truck, mini_truck, bus, train.
 *
 * Eleven is the number of *map marker colours* — `@mageride/tailwind-preset`'s
 * `VEHICLE_COLORS` has an eleventh token, `veh-private`, whose own comment says it
 * is "a Mode B **display** token, not a vehicle type": a private vehicle is a
 * sedan or a van drawn grey because of its mode. Publishing "11 vehicle types"
 * would invent one that a reader could then go looking for.
 *
 * `value` is a plain number because these are counts, not money.
 */
export interface Stat {
  readonly id: string;
  readonly value: number;
  readonly label: WwwMessageKey;
  readonly source: string;
  /** Rendered as "0%" / "10" / "3". `suffix` is a key so it can localise. */
  readonly suffix?: WwwMessageKey;
}

export const STATS: readonly Stat[] = [
  {
    id: 'vehicle-types',
    value: 10,
    label: 'www.stats.vehicleTypes',
    source: 'specs/user-requirements-document.md#1-b-canonical-vehicle-types',
  },
  {
    id: 'languages',
    value: 3,
    label: 'www.stats.languages',
    source: 'CLAUDE.md#universal-rules',
  },
  {
    id: 'commission',
    value: 0,
    label: 'www.stats.commission',
    source: 'specs/user-requirements-document.md#1-product-vision',
    suffix: 'www.stats.percentSuffix',
  },
  {
    id: 'first-trip-free',
    value: 1,
    label: 'www.stats.firstTripFree',
    source: 'specs/user-requirements-document.md#epic-9-daily-platform-fee-billing',
  },
];

// ---------------------------------------------------------------------------
// The language band
// ---------------------------------------------------------------------------

/**
 * One sentence, shown in all three languages **at once**, on one card.
 *
 * Deliberately **not** a translation lookup. Every other string on this site
 * resolves to the reader's own language; this one is three strings side by side,
 * because the point being made is not *what* the sentence says but that the app
 * speaks all three — and a reader only ever sees one language at a time cannot see
 * that. Rendering it through the translator would show a Sinhala reader the Sinhala
 * line three times.
 *
 * Each needs its own `lang` attribute when rendered (A33), so a screen reader
 * switches voice per line rather than reading Tamil with a Sinhala pronunciation.
 */
export const LANGUAGE_BAND: readonly { readonly lang: string; readonly key: WwwMessageKey }[] = [
  { lang: 'si-LK', key: 'www.languageBand.si' },
  { lang: 'ta-LK', key: 'www.languageBand.ta' },
  { lang: 'en-LK', key: 'www.languageBand.en' },
];

// ---------------------------------------------------------------------------
// Footer
// ---------------------------------------------------------------------------

/**
 * The footer's three columns — a heading each, over the routes already grouped in
 * `src/lib/routes.ts`.
 *
 * Only the *headings* live here. The links do not: `ROUTES` already carries a
 * `group` per route and a `labelKey` that is both its nav label and its page
 * heading, so listing them again would be a second list to keep in step with the
 * first, and the first is the one the sitemap reads.
 */
export const FOOTER_COLUMNS: readonly {
  readonly group: RouteGroup;
  readonly heading: WwwMessageKey;
}[] = [
  { group: 'primary', heading: 'www.footer.explore' },
  { group: 'support', heading: 'www.footer.support' },
  { group: 'legal', heading: 'www.footer.legal' },
];

/**
 * Carries no year, deliberately. Every page below `app/[locale]/` is pre-rendered
 * at build time, so a `{year}` placeholder would freeze at whatever year the last
 * deploy happened in and then quietly be wrong every January.
 */
export const FOOTER_RIGHTS_KEY: WwwMessageKey = 'www.footer.rights';

export const FOOTER_MADE_IN_KEY: WwwMessageKey = 'www.footer.madeIn';
