/**
 * The four role landing pages, **as data**.
 *
 * S16's first instruction is a warning: *"Four pages, one template. If you find
 * yourself writing a fourth bespoke layout, stop and extract the template — S19's
 * Lighthouse gate runs on `/drivers`, and four hand-built pages means four
 * independent ways to fail it."* So there is one component
 * (`src/components/pages/RolePage.tsx`) and this module is the whole of the
 * difference between the pages.
 *
 * Every field is either a key, an id, or a filter over a content module that
 * already exists. **Nothing here is prose** — the copy is `PAGES` in `./pages.ts`,
 * written in S07, and the bands are the components S15 built for the home page.
 *
 * ## The bands, and which are optional
 *
 * hero → benefits → how-it-works → screen strip → guide entry → FAQ subset → CTA.
 *
 * `howItWorks` and `fareTable` are optional because two of the four pages have
 * neither: `/vision` is read by a journalist or an official and carries no
 * onboarding funnel, and `/fleets` has no how-it-works constant because fleet
 * onboarding happens in a portal this site does not document. An absent band is a
 * missing field rather than an empty array, so a page cannot render a heading over
 * nothing.
 *
 * **`/fleets` still has no how-it-works after S23, and that is not an oversight.**
 * The fleet guide documents the portal at chapter length; a four-step marketing
 * funnel restating it would be the one band on these pages with no content module
 * behind it, which is the original reason the field is absent. What S23 changed is
 * where the page's call to action *goes* — see {@link RolePageDefinition.guide}.
 */

import { FAQ, type FaqEntry } from './faq';
import {
  HOW_IT_WORKS_DRIVER,
  HOW_IT_WORKS_PASSENGER,
  type HowItWorksStep,
} from './marketing';
import { PAGES } from './pages';
import { SCREENS, type ScreenEntry, type Surface } from './screens';
import { VALUES, type Value } from './vision';
import type { WwwMessageKey } from '@/i18n';
import type { GuideAudience, RoutePath } from '@/lib/routes';

/** Which of the four this is. Keyed by route path so a typo cannot compile. */
export type RolePath = Extract<RoutePath, 'vision' | 'passengers' | 'drivers' | 'fleets'>;

export interface RolePageDefinition {
  readonly path: RolePath;

  /**
   * The benefit grid.
   *
   * `/vision` shows `VALUES` — which carry a `source` each, and S16 says those
   * anchors are *"for the reader as much as for review"*, so that page renders
   * them. The other three show their `PAGES` sections, which are heading + body
   * pairs with no anchor of their own because the claim they make is the one the
   * linked band already anchors.
   */
  readonly benefits: 'values' | 'sections';

  /**
   * The vision paragraphs, the mission, and the mission's qualifier — `/vision`
   * only (S16 §27: *"Vision, the chosen mission (MCS-34 D1), and the values cards"*).
   *
   * A band of its own rather than part of `benefits`, because **MCS-34 D1 makes the
   * qualifier required furniture wherever the mission renders**: the
   * national-infrastructure framing carries a coverage claim that is not true on
   * launch day, and D1's own decision note obliges an honest correction directly
   * beneath it. Coupling the two in one band is how that obligation survives a
   * later layout change — a session that moves the mission moves the qualifier
   * with it, because they are one thing here.
   */
  readonly mission?: true;

  /** The four-step funnel, where the page has one. */
  readonly howItWorks?: readonly HowItWorksStep[];

  /** The six Mode C tiers. `/drivers` only — see the note on `FEE_PAGE`. */
  readonly fareTable?: true;

  /**
   * Which registry entries the screen strip draws from.
   *
   * A `Surface` rather than a hand-listed set of ids: the registry already knows
   * which frames belong to the passenger app, and a second list here would be one
   * more thing to keep in step with S05's curation.
   */
  readonly screenSurface?: Surface;

  /**
   * Where the guide entry point points.
   *
   * A {@link GuideAudience} deep-links into `/guide` through `guideEntryPath()`,
   * which resolves against `GUIDE_CHAPTERS` and falls back to the guide index — so
   * this is a *preference* rather than a URL and can never name a chapter that does
   * not exist. `'contact'` and `'none'` are the two pages that point elsewhere.
   *
   * **`'contact'` no longer has a user.** It was `/fleets`, for exactly as long as
   * MCS-34 **D7** deferred the fleet guide: with no fleet chapters, "read the guide"
   * had nowhere to go and pointing it at `/contact` was the honest answer. S23 wrote
   * the six chapters, so `/fleets` names its audience like the other two role pages
   * and the CTA is a guide link again. The member stays in the union because it is
   * the right answer for the *next* role page whose guide has not been written yet.
   */
  readonly guide: GuideAudience | 'contact' | 'none';

  /** The call to action at the foot. Absent on `/vision`, deliberately. */
  readonly cta?: WwwMessageKey;

  /**
   * The FAQ subset.
   *
   * Ids, never duplicated prose — S16's fence. `'passenger'` / `'driver'` select by
   * the registry's own `audience` field (which includes `both`); an explicit list
   * is for the two pages the audience field does not describe.
   */
  readonly faq: { readonly audience: FaqEntry['audience'] } | { readonly ids: readonly string[] };
}

/**
 * `/drivers` is the only page with the fee table, and that is the fence.
 *
 * S16: *"The fee tiers come from the one constant. No second copy on `/drivers`."*
 * The table renders through the same `FareTable` band the home page uses, reading
 * `DAILY_FEE_TIERS` — so "the same six values" is a property of there being one
 * array, not of two lists agreeing.
 */
export const ROLE_PAGES: readonly RolePageDefinition[] = [
  {
    path: 'vision',
    benefits: 'values',
    mission: true,
    // No how-it-works, no fee table, no CTA. S16: "This is the page a journalist or
    // an official reads; it carries no CTA pressure and no store badge."
    guide: 'none',
    faq: { ids: ['why-free', 'coverage', 'modes', 'maps', 'my-data'] },
  },
  {
    path: 'passengers',
    benefits: 'sections',
    howItWorks: HOW_IT_WORKS_PASSENGER,
    screenSurface: 'passenger',
    guide: 'passenger',
    cta: 'www.page.passengers.guideCta',
    faq: { audience: 'passenger' },
  },
  {
    path: 'drivers',
    benefits: 'sections',
    howItWorks: HOW_IT_WORKS_DRIVER,
    fareTable: true,
    screenSurface: 'driver',
    guide: 'driver',
    cta: 'www.page.drivers.guideCta',
    faq: { audience: 'driver' },
  },
  {
    path: 'fleets',
    benefits: 'sections',
    // No how-it-works: fleet onboarding happens in `fleet.mageride.lk`, which this
    // site describes but does not document. Inventing four steps for it would be
    // the one band on these pages with no content module behind it.
    screenSurface: 'fleet',
    // S23. `guideEntryPath('fleet')` resolves to chapter 1, *registering your
    // organisation*, which is the right first page for this reader for the same
    // reason the driver guide opens on onboarding: it is the gate everything else
    // waits behind (US-13.A7).
    guide: 'fleet',
    cta: 'www.page.fleets.guideCta',
    faq: { ids: ['mode-b-access', 'mode-b-price', 'modes', 'vehicle-types'] },
  },
];

export function roleDefinition(path: RolePath): RolePageDefinition {
  const found = ROLE_PAGES.find((role) => role.path === path);
  if (!found) throw new Error(`roles.ts: no definition for "${path}"`);
  return found;
}

/** The page copy for a role — `PAGES` is partial, and these four are always present. */
export function roleCopy(path: RolePath) {
  const copy = PAGES[path];
  if (!copy) throw new Error(`roles.ts: PAGES has no copy for "${path}"`);
  return copy;
}

/** The values, in `vision.ts`'s order. `/vision` renders every one with its anchor. */
export function roleValues(): readonly Value[] {
  return VALUES;
}

/** The screen strip's frames, in registry order. */
export function roleScreens(surface: Surface): readonly ScreenEntry[] {
  return SCREENS.filter((screen) => screen.surface === surface);
}

/** The FAQ subset, in registry order so two pages cannot disagree about it. */
export function roleFaq(definition: RolePageDefinition): readonly FaqEntry[] {
  if ('audience' in definition.faq) {
    const wanted = definition.faq.audience;
    return FAQ.filter((entry) => entry.audience === wanted || entry.audience === 'both');
  }

  const ids = definition.faq.ids;
  return ids.map((id) => {
    const entry = FAQ.find((candidate) => candidate.id === id);
    if (!entry) {
      throw new Error(
        `roles.ts: no FAQ entry with id "${id}" — a role page would render an empty ` +
          'question list. Check src/content/faq.ts.',
      );
    }
    return entry;
  });
}
