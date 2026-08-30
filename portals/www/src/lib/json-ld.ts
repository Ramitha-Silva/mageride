/**
 * Structured data — **every block built from the data the page renders, never from
 * a literal.**
 *
 * S19 states the reason and it is not stylistic: if the JSON-LD and the visible
 * page drift, a search engine has been told something the reader cannot see, which
 * is what "structured data spam" means and what the penalty is for. So every
 * function here takes the same registry entry the component takes — a `Chapter`,
 * the `FAQ` array, a `LegalDocument` — and there is no path by which one can be
 * updated without the other.
 *
 * ## What is emitted, and the one thing deliberately left out
 *
 * | Type | Where | Built from |
 * |---|---|---|
 * | `Organization` | the locale layout, so every page | the brand keys and `SITE_ORIGIN` |
 * | `WebSite` | the locale layout | the same |
 * | `SoftwareApplication` ×2 | `/download` | `MISC_KEYS`, URD NFR-22 |
 * | `FAQPage` | `/faq` | `src/content/faq.ts` |
 * | `HowTo` | every guide chapter | the chapter's own `Step[]` |
 * | `BreadcrumbList` | guide chapters and legal documents | `src/lib/seo.ts`'s crumbs |
 *
 * **`SearchAction` is out, and that is the interesting decision.** The usual
 * `WebSite` block carries a `potentialAction` describing a site search, and this
 * site has no search endpoint — no `/search`, no query parameter that finds
 * anything, and nothing at request time that could (MCS-34's fourth negative). A
 * `potentialAction` pointing at a URL that does not exist is a false declaration
 * about the site, made in a machine-readable format, which is a worse kind of wrong
 * than a missing one. `WebSite` ships alone.
 *
 * **`installUrl` is out of `SoftwareApplication` for the same reason** — MCS-34 D3
 * leaves the store listings unpublished, so there is no URL to install from.
 * `offers` at zero is not a placeholder: the apps genuinely cost nothing (URD §1).
 *
 * ## `HowTo` is the payoff for the uniform chapter shape
 *
 * S19: *"S07's `Step[]` shape maps directly."* It does, and that is the return on
 * having typed the guide instead of writing 34 MDX files: `name` is the chapter
 * title, `step` is the steps in order, `image` is the screen a step references —
 * resolved through the same registry the page resolves it through, so a chapter
 * that renders four screens describes four images and never five.
 *
 * ## Serialisation
 *
 * `JsonLd` in `src/components/seo/JsonLd.tsx` renders these. It escapes `<` before
 * writing into the script element — a JSON string containing `</script` would end
 * the element early, and every string here comes from a translated resource that a
 * translator could put anything into.
 */

import { FAQ, type FaqEntry } from '@/content/faq';
import type { LegalDocument } from '@/content/legal';
import { MISC_KEYS } from '@/content/pages';
import { plateSize, SCREENS } from '@/content/screens';
import type { Chapter } from '@/content/types';
import { createWwwTranslator, type Locale } from '@/i18n';
import { ROUTE_BY_PATH } from './routes';
import { absoluteUrl, SITE_ORIGIN, type Crumb } from './seo';

/** A JSON-LD node. Deliberately loose — the shapes are schema.org's, not ours. */
export type JsonLdNode = Record<string, unknown>;

/** URD §1 — the apps are free to install and free to use. */
const FREE_OFFER = {
  '@type': 'Offer',
  price: '0',
  priceCurrency: 'LKR',
};

/**
 * The platform itself.
 *
 * `sameAs` is absent rather than empty: MageRide has no published social profile,
 * and an empty array says "we looked and there are none" no more clearly than
 * omitting the property does. `logo` is absent for the same reason — `public/brand/`
 * holds no mark yet, and pointing at a 404 is worse than saying nothing.
 */
export function organization(locale: Locale): JsonLdNode {
  const t = createWwwTranslator(locale);

  return {
    '@context': 'https://schema.org',
    '@type': 'Organization',
    name: t('www.brand.name'),
    url: SITE_ORIGIN,
    description: t('www.brand.tagline'),
    areaServed: { '@type': 'Country', name: 'Sri Lanka' },
  };
}

/** The site. No `SearchAction` — see the module note. */
export function webSite(locale: Locale): JsonLdNode {
  const t = createWwwTranslator(locale);

  return {
    '@context': 'https://schema.org',
    '@type': 'WebSite',
    name: t('www.brand.name'),
    url: SITE_ORIGIN,
    inLanguage: locale,
    publisher: { '@type': 'Organization', name: t('www.brand.name'), url: SITE_ORIGIN },
  };
}

/**
 * The two apps, for `/download`.
 *
 * `operatingSystem` names **Android only**, and the qualifier is URD NFR-22's own:
 * Android 8.0 (API 26). The URD states no minimum iOS anywhere, so no iOS version
 * is claimed here — the same call `/download` makes in its visible copy, for the
 * same reason. Adding `"iOS"` with no version would be the structured-data form of
 * inventing a requirement.
 */
export function softwareApplications(locale: Locale): readonly JsonLdNode[] {
  const t = createWwwTranslator(locale);

  return [
    {
      name: t(MISC_KEYS.passengerApp),
      description: t('www.page.download.passengerAppBody'),
    },
    {
      name: t(MISC_KEYS.driverApp),
      description: t('www.page.download.driverAppBody'),
    },
  ].map((app) => ({
    '@context': 'https://schema.org',
    '@type': 'SoftwareApplication',
    ...app,
    applicationCategory: 'TravelApplication',
    operatingSystem: 'Android 8.0+',
    offers: FREE_OFFER,
    // No `installUrl`: MCS-34 D3 leaves the listings unpublished, and a store URL
    // that does not resolve is a claim this site cannot support.
  }));
}

/**
 * `/faq`, from the array the accordion renders.
 *
 * The page keeps every answer in the DOM whether its item is open or closed (S18's
 * `<details>` decision), which is what makes this block honest: `FAQPage` markup
 * describing answers a visitor cannot reach is exactly the abuse the guideline
 * names, and here the two are the same twenty-one entries by construction.
 */
export function faqPage(locale: Locale, entries: readonly FaqEntry[] = FAQ): JsonLdNode {
  const t = createWwwTranslator(locale);

  return {
    '@context': 'https://schema.org',
    '@type': 'FAQPage',
    mainEntity: entries.map((entry) => ({
      '@type': 'Question',
      name: t(entry.question),
      acceptedAnswer: { '@type': 'Answer', text: t(entry.answer) },
    })),
  };
}

/**
 * One guide chapter as a `HowTo`.
 *
 * `image` on a step is the screen that step references, at its real plate size —
 * `plateSize()` rather than a constant, for the reason `ScreenImage` gives: the
 * committed output holds eight distinct sizes and 26 of the 70 phone plates are a
 * pixel shorter than the other 34. Absolute URLs, because a consumer of this block
 * is not on this page.
 *
 * `totalTime` is absent. It would be a guess, and a `HowTo` claiming a chapter
 * takes four minutes is a claim nobody measured.
 */
export function howTo(locale: Locale, chapter: Chapter): JsonLdNode {
  const t = createWwwTranslator(locale);

  return {
    '@context': 'https://schema.org',
    '@type': 'HowTo',
    name: t(chapter.title),
    description: t(chapter.summary),
    inLanguage: locale,
    url: absoluteUrl(locale, `guide/${chapter.audience}/${chapter.slug}`),
    step: chapter.steps.map((step, index) => {
      const screen = step.screenRef
        ? SCREENS.find((entry) => entry.id === step.screenRef)
        : undefined;

      return {
        '@type': 'HowToStep',
        position: index + 1,
        name: t('www.guide.stepLabel', { number: index + 1 }),
        text: t(step.instruction),
        ...(screen
          ? {
              image: {
                '@type': 'ImageObject',
                url: `${SITE_ORIGIN}/screens/${screen.file}.webp`,
                caption: t(screen.captionKey),
                ...plateSize(screen),
              },
            }
          : {}),
      };
    }),
  };
}

/** A trail, from `src/lib/seo.ts`'s crumbs. */
export function breadcrumbs(crumbs: readonly Crumb[]): JsonLdNode {
  return {
    '@context': 'https://schema.org',
    '@type': 'BreadcrumbList',
    itemListElement: crumbs.map((crumb, index) => ({
      '@type': 'ListItem',
      position: index + 1,
      name: crumb.name,
      item: crumb.url,
    })),
  };
}

/**
 * A legal document.
 *
 * `WebPage` rather than `Article` or `TermsOfService`: these documents have no
 * author, no publication date and — while MCS-34 D5 stands — no body. `dateModified`
 * appears **only** when `lastUpdated` does, so a document that has never been
 * published does not carry a modification date, which would be the structured-data
 * equivalent of the build-date bug `src/content/legal.ts` exists to prevent.
 */
export function legalPage(locale: Locale, document: LegalDocument): JsonLdNode {
  const t = createWwwTranslator(locale);

  return {
    '@context': 'https://schema.org',
    '@type': 'WebPage',
    // The route's nav label, which is also the page's `<h1>` — one key, so the
    // structured name and the visible heading cannot say different things.
    name: t(ROUTE_BY_PATH[`legal/${document.doc}`].labelKey),
    description: t(document.intro),
    inLanguage: locale,
    url: absoluteUrl(locale, `legal/${document.doc}`),
    ...(document.lastUpdated ? { dateModified: document.lastUpdated } : {}),
  };
}
