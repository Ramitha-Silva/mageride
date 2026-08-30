/**
 * The guide chapter **slugs** — the 16 passenger and 18 driver chapters of
 * `docs/www-site-plan.md` §A19 and §A20, the 6 fleet chapters S23 added, and
 * nothing else about them.
 *
 * This file exists because {@link ../lib/routes.ts `GUIDE_CHAPTERS`} is empty until
 * S17 and the chapter *bodies* are not written until S08–S11, yet S05's screen
 * registry has to name the chapters each screen illustrates **now**. Without a
 * canonical list the registry would spell its chapter references as free strings,
 * S08–S11 would independently invent slugs, and the two would drift silently —
 * the registry would still compile, the guide would still render, and the screens
 * would simply stop appearing in the chapters that reference them.
 *
 * So the slug is the contract, and it is the *only* thing here. A slug is a URL
 * segment: it is decided once, it is expensive to change after launch because a
 * changed slug is a dead link, and it is the one property of a chapter that is not
 * a translation. Titles, summaries, steps and callouts are content — they belong to
 * S08–S11 and to the three message tables, and adding them here would put English
 * chapter titles in a module that is not a resource table.
 *
 * **The order is the plan's order and is the reading order**, so S17 can build
 * {@link ../lib/routes.ts `GUIDE_CHAPTERS`} from these arrays directly rather than
 * restating the sequence a third time.
 *
 * Spec anchors: `docs/www-site-plan.md` §A19 (passenger, sourced from Walkthrough
 * Section A, D1 §A.1–A.6 and URD Epics 1/4/7/8/10/12/15/16/18/19/20/22/23) and §A20
 * (driver, from Walkthrough Section B, D1 §B.1–B.11 and URD Epics
 * 1/2/3/5/6/6A/9/9A/12/17/20). The fleet six are S23's, from Walkthrough Section E
 * (Scenarios 67–79) and URD Epics 13 and 27.
 */

/**
 * The 16 passenger chapters, in reading order (§A19).
 *
 * Slugs are the English chapter title reduced to its subject, not a transliteration
 * of it — `waiting-for-a-driver` rather than `waiting-and-what-the-15-second-
 * dispatch-is-doing`. A slug is read in a URL and said out loud in support calls;
 * the full title lives in the message tables, where it can differ per language
 * while the URL stays one thing.
 */
export const PASSENGER_CHAPTER_SLUGS = [
  'install-and-first-run',
  'permissions',
  'reading-the-live-map',
  'tracking-buses-and-trains',
  'following-a-private-vehicle',
  'mode-b-payments',
  'booking-a-ride',
  'choosing-a-vehicle-and-fare',
  'waiting-for-a-driver',
  'during-the-ride',
  'paying',
  'sending-a-package',
  'booking-for-someone-else',
  'scheduling-a-ride',
  'saved-places-and-ratings',
  'settings-help-and-your-data',
] as const;

/** The 18 driver chapters, in reading order (§A20). */
export const DRIVER_CHAPTER_SLUGS = [
  'install-and-first-run',
  'onboarding-your-vehicle',
  'photographing-documents',
  'approval',
  'permissions-and-background-location',
  'your-dashboard',
  'going-on-standby',
  'the-15-second-offer',
  'running-a-trip',
  'directional-travel',
  'package-jobs',
  'your-wallet',
  'the-daily-platform-fee',
  'getting-paid',
  'bulk-credit-and-transfers',
  'mode-a-and-b-driving',
  'ratings-and-driver-level',
  'safety-and-support',
] as const;

/**
 * The 6 fleet-owner chapters, in reading order (S23).
 *
 * **Conditional on MCS-34 D7, which answered "yes, in the second delivery phase."**
 * Fleet Owner is the third end-user role — the URD's §2.1 table has exactly three
 * that are not staff — and a site that names `fleet.mageride.lk` and publishes a
 * `/fleets` page was documenting two of the three.
 *
 * Six and not eighteen because the Fleet Portal's thirteen screens divide into what
 * an owner must get *right once* — registering, being verified, being paid, getting
 * a vehicle approved, putting a driver in it, and paying the invoice — and what they
 * will simply use daily and can discover (the live map, the analytics export, the
 * scheduling board). This guide is the first kind. The reading order is the order an
 * owner meets them, which is also the order the portal blocks them in: nothing can
 * be onboarded before the organisation is approved (US-13.A7), and no vehicle can be
 * Service payment = Paid before the payout profile is Verified (US-27.2).
 *
 * `billing` is the one bare noun in the three lists. It stays bare because the
 * chapter is about **MageRide's monthly charge to the fleet** and any qualifier that
 * would fit in a URL — `monthly-billing`, `fleet-billing` — reads as though it were
 * about the money a fleet *collects*, which is a different chapter's subject and the
 * single most likely thing for a reader to conflate.
 */
export const FLEET_CHAPTER_SLUGS = [
  'registering-your-organisation',
  'kyc-and-your-payout-profile',
  'adding-vehicles',
  'vehicle-documents',
  'assigning-drivers-and-trackers',
  'billing',
] as const;

export type PassengerChapterSlug = (typeof PASSENGER_CHAPTER_SLUGS)[number];
export type DriverChapterSlug = (typeof DRIVER_CHAPTER_SLUGS)[number];
export type FleetChapterSlug = (typeof FLEET_CHAPTER_SLUGS)[number];

/**
 * How one chapter is named from anywhere outside its own guide — `audience/slug`,
 * which is exactly the published path minus its `guide/` prefix.
 *
 * Qualified rather than bare because the two guides genuinely share slugs:
 * `install-and-first-run` is chapter 1 of both, and a passenger screen tagged with
 * the bare slug would be ambiguous in the one place ambiguity is most expensive —
 * the join between a screen and the chapter that shows it. The template-literal
 * type means a typo in either half is a compile error rather than a screen that
 * quietly illustrates nothing.
 */
export type GuideChapterRef =
  | `passenger/${PassengerChapterSlug}`
  | `driver/${DriverChapterSlug}`
  | `fleet/${FleetChapterSlug}`;

/** Every chapter reference the site can publish, in reading order, passengers first. */
export const GUIDE_CHAPTER_REFS: readonly GuideChapterRef[] = [
  ...PASSENGER_CHAPTER_SLUGS.map((slug): GuideChapterRef => `passenger/${slug}`),
  ...DRIVER_CHAPTER_SLUGS.map((slug): GuideChapterRef => `driver/${slug}`),
  ...FLEET_CHAPTER_SLUGS.map((slug): GuideChapterRef => `fleet/${slug}`),
];
