/**
 * Page-level copy — the header, intro and section furniture of every route.
 *
 * Written here, in S07, so that S14–S18 **compose** a page out of copy that already
 * exists rather than authoring it while fighting a grid. A section heading invented
 * under layout pressure is how a site ends up with four names for the same idea,
 * and it is how a factual claim gets onto a public page without an anchor.
 *
 * The split against `./marketing.ts` is by scope, not by kind: this module holds
 * what belongs to *one route*, and `marketing.ts` holds the reusable bands (hero,
 * modes, features, stats, fee table) that several routes draw from. `/passengers`
 * renders `PAGES.passengers` plus the shared mode and feature content; it does not
 * restate either.
 *
 * ## Two pages are deliberately thin, and both are gated
 *
 * **`/download` links to nothing** (MCS-34 D3 — the apps are not published). It says
 * so plainly instead of showing a dead store badge, and it carries **no form**: a
 * notify-me box would be a personal-data collection point on a surface whose entire
 * claim is that it holds none, and it would break MCS-34's fourth negative for a
 * mailing list.
 *
 * **`/contact` names no address** (MCS-34 D4 — email only, and the address itself is
 * still unchosen). The copy below says where support actually lives, which is inside
 * the app where a ticket arrives attached to the trip it is about. The address is a
 * content gap with a go-live-checklist row against it, not a placeholder somebody
 * might mistake for real.
 *
 * **No legal text appears anywhere in this module** (MCS-34 D5). `/legal/*` renders
 * the scaffold notice until counsel supplies bodies; no C134 session authors it.
 */

import type { WwwMessageKey } from '@/i18n';
import type { RoutePath } from '@/lib/routes';

/** A heading with optional body — the unit every page section is built from. */
export interface Section {
  readonly heading: WwwMessageKey;
  readonly body?: WwwMessageKey;
}

export interface PageCopy {
  /** The `<h1>`. Distinct from the route's nav label, which is often shorter. */
  readonly title: WwwMessageKey;
  /** The standfirst under the `<h1>`. */
  readonly intro: WwwMessageKey;
  readonly sections: readonly Section[];
  /** A closing call to action, where the page has one. */
  readonly cta?: WwwMessageKey;
}

/**
 * Copy for the routes that have prose of their own.
 *
 * Keyed by {@link RoutePath} so a page cannot be written for a route the site does
 * not publish, and a typo in a path is a compile error. Partial on purpose: the
 * three `legal/*` routes are absent because D5 forbids authoring them here, and the
 * home page is {@link HOME} because it is composed of bands rather than sections.
 */
export const PAGES: Partial<Record<RoutePath, PageCopy>> = {
  vision: {
    title: 'www.page.vision.title',
    intro: 'www.page.vision.intro',
    sections: [
      { heading: 'www.page.vision.missionHeading' },
      { heading: 'www.page.vision.valuesHeading' },
    ],
  },

  passengers: {
    title: 'www.page.passengers.title',
    intro: 'www.page.passengers.intro',
    sections: [
      { heading: 'www.page.passengers.trackHeading', body: 'www.page.passengers.trackBody' },
      { heading: 'www.page.passengers.bookHeading', body: 'www.page.passengers.bookBody' },
      { heading: 'www.page.passengers.sendHeading', body: 'www.page.passengers.sendBody' },
      { heading: 'www.page.passengers.costHeading', body: 'www.page.passengers.costBody' },
    ],
    cta: 'www.page.passengers.guideCta',
  },

  drivers: {
    title: 'www.page.drivers.title',
    intro: 'www.page.drivers.intro',
    sections: [
      { heading: 'www.page.drivers.earnHeading', body: 'www.page.drivers.earnBody' },
      { heading: 'www.page.drivers.feeHeading', body: 'www.page.drivers.feeBody' },
      // The fee table itself is `DAILY_FEE_TIERS` in ./marketing.ts — the numbers
      // are never spelled in a message string.
      {
        heading: 'www.page.drivers.feeTableHeading',
        body: 'www.page.drivers.feeTableNote',
      },
      { heading: 'www.page.drivers.startHeading', body: 'www.page.drivers.startBody' },
      {
        heading: 'www.page.drivers.directionalHeading',
        body: 'www.page.drivers.directionalBody',
      },
    ],
    cta: 'www.page.drivers.guideCta',
  },

  fleets: {
    title: 'www.page.fleets.title',
    intro: 'www.page.fleets.intro',
    sections: [
      { heading: 'www.page.fleets.manageHeading', body: 'www.page.fleets.manageBody' },
      { heading: 'www.page.fleets.accessHeading', body: 'www.page.fleets.accessBody' },
      { heading: 'www.page.fleets.billingHeading', body: 'www.page.fleets.billingBody' },
    ],
    cta: 'www.page.fleets.portalNote',
  },

  screens: {
    title: 'www.page.screens.title',
    intro: 'www.page.screens.intro',
    sections: [
      { heading: 'www.page.screens.passengerHeading' },
      { heading: 'www.page.screens.driverHeading' },
      { heading: 'www.page.screens.fleetHeading' },
      { heading: 'www.page.screens.webHeading' },
    ],
  },

  guide: {
    title: 'www.page.guide.title',
    intro: 'www.page.guide.intro',
    // Three sections, index-aligned with `GUIDE_AUDIENCES` — `/guide` maps that
    // array and reads `sections[index]`, so the order here is not cosmetic. S23
    // added the third; the two arrays are held to each other by
    // `test/content.test.ts`.
    sections: [
      { heading: 'www.page.guide.passengerHeading' },
      { heading: 'www.page.guide.driverHeading' },
      { heading: 'www.page.guide.fleetHeading' },
    ],
  },

  faq: {
    title: 'www.page.faq.title',
    intro: 'www.page.faq.intro',
    sections: [],
  },

  download: {
    title: 'www.page.download.title',
    intro: 'www.page.download.intro',
    sections: [
      { heading: 'www.page.download.notYet', body: 'www.page.download.notYetBody' },
      // S18. Which of the two apps a reader wants needs no store URL, so it is
      // publishable while D3 is open; the Android minimum is URD NFR-22 and the page
      // cites it. There is deliberately no iOS minimum — no spec states one.
      { heading: 'www.page.download.whichAppHeading' },
      {
        heading: 'www.page.download.requirementsHeading',
        body: 'www.page.download.androidMinimum',
      },
    ],
  },

  contact: {
    title: 'www.page.contact.title',
    intro: 'www.page.contact.intro',
    sections: [
      { heading: 'www.page.contact.inAppHeading', body: 'www.page.contact.inAppBody' },
      { heading: 'www.page.contact.questionsHeading', body: 'www.page.contact.questionsBody' },
      // `emailPending` is the sentence that stands in for the address D4 has not
      // chosen — see the module note. It is copy, not a placeholder: it says there
      // is no address rather than pretending to be one.
      { heading: 'www.page.contact.emailHeading', body: 'www.page.contact.emailBody' },
    ],
  },
};

/**
 * The home page, which is bands rather than sections.
 *
 * Its content — hero slides, the three modes, how-it-works, values, stats — all
 * lives in `./marketing.ts` and `./vision.ts`; what is here is only the furniture
 * that introduces each band.
 */
export const HOME = {
  modes: { heading: 'www.home.modes.heading', body: 'www.home.modes.intro' },
  how: {
    heading: 'www.home.how.heading',
    passengerTab: 'www.home.how.passengerTab',
    driverTab: 'www.home.how.driverTab',
  },
  values: { heading: 'www.home.values.heading', body: 'www.home.values.intro' },
  screens: { heading: 'www.home.screens.heading' },
  faq: { heading: 'www.home.faq.heading', more: 'www.home.faq.more' },
} as const satisfies Record<string, Record<string, WwwMessageKey>>;

/** Chrome that appears on more than one page and belongs to none of them. */
export const COMMON = {
  learnMore: 'www.common.learnMore',
  backToTop: 'www.common.backToTop',
  onThisPage: 'www.common.onThisPage',
  sourceLabel: 'www.common.sourceLabel',
  previous: 'www.common.previous',
  next: 'www.common.next',
} as const satisfies Record<string, WwwMessageKey>;

/** The two app names on `/download`, and the chapter-count label on `/guide`. */
export const MISC_KEYS = {
  passengerApp: 'www.page.download.passengerApp',
  driverApp: 'www.page.download.driverApp',
  chapterCount: 'www.page.guide.chapterCount',
  readChapter: 'www.page.guide.readChapter',
} as const satisfies Record<string, WwwMessageKey>;
