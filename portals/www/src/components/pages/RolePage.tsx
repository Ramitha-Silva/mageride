import Link from 'next/link';

import { cx } from '@mageride/ui';

import { ScreenImage } from '@/components/ScreenImage';
import { FareTable } from '@/components/marketing/FareTable';
import { Reveal } from '@/components/motion/Reveal';
import { FREE_FIRST_TRIP_QUOTE_KEY, FREE_FIRST_TRIP_SOURCE } from '@/content/marketing';
import { HOME } from '@/content/pages';
import { MISSION_KEY, MISSION_QUALIFIER_KEY, VISION_BODY_KEYS } from '@/content/vision';
import {
  roleCopy,
  roleDefinition,
  roleFaq,
  roleScreens,
  roleValues,
  type RolePath,
} from '@/content/roles';
import { createWwwTranslator, type Locale } from '@/i18n';
import { guideEntryPath, href } from '@/lib/routes';

/**
 * The screen strip's `<h2>`, so the scrollable region below it can borrow the
 * heading as its accessible name (S19). One constant because the two uses have to
 * agree and an `aria-labelledby` pointing at nothing fails silently — the region
 * simply goes unnamed, which is the failure this id exists to prevent.
 *
 * Page-unique rather than instance-unique on purpose: `RolePage` renders once per
 * page, so there is never a second strip on the document to collide with.
 */
const SCREEN_STRIP_HEADING_ID = 'role-screens-heading';

/**
 * **The** role landing page. One component, four pages, and the difference between
 * them is `src/content/roles.ts`.
 *
 * S16 opens with the reason: *"If you find yourself writing a fourth bespoke layout,
 * stop and extract the template — S19's Lighthouse gate runs on `/drivers`, and four
 * hand-built pages means four independent ways to fail it."* One template also means
 * one place to fix a heading level, one place a band's reduced-motion behaviour is
 * decided, and one page whose `<h1>` count can be wrong.
 *
 * ## The bands, in order
 *
 * hero → **vision + mission (`/vision` only)** → benefits → how-it-works →
 * **fee table (`/drivers` only)** → screen strip → guide entry → FAQ subset → CTA.
 * Four are optional and are absent rather than empty, so no page renders a heading
 * over nothing.
 *
 * ## Two things this page does not do
 *
 * **No form, on any of the four.** S16's fence, and `/fleets` is where the pressure
 * comes from: a "talk to us about fleets" control is a link to `/contact`, never a
 * field. A field here would end two of MCS-34's four negatives at once.
 *
 * **No link to a guide chapter that does not exist.** `guideEntryPath()` resolves
 * against `GUIDE_CHAPTERS`, which is empty until S17, so today every guide link is
 * the index and the day S17 lands they become deep links with no edit here.
 */
export function RolePage({ locale, path }: { readonly locale: Locale; readonly path: RolePath }) {
  const t = createWwwTranslator(locale);
  const definition = roleDefinition(path);
  const copy = roleCopy(path);
  const faq = roleFaq(definition);

  return (
    <>
      {/* 1 · Hero. The page's single `<h1>` — the layout owns `<main>`. */}
      <section className="mx-auto max-w-[1200px] px-4 pt-section pb-lg">
        <h1 className="max-w-[20ch] font-display text-hero text-balance text-on-surface">
          {t(copy.title)}
        </h1>
        <p className="mt-md max-w-[62ch] text-body text-on-surface-variant">{t(copy.intro)}</p>
      </section>

      {/*
        2 · The vision and the mission — `/vision` only.

        **The qualifier is not optional and is not small print.** MCS-34 D1 chose a
        national-infrastructure framing ("one live picture of how the country
        moves") and its own decision note records that the framing carries a
        coverage claim which is false on launch day. S07 wrote the correction;
        `portals/www/CLAUDE.md` calls it "required furniture wherever the mission
        renders". It sits directly beneath the mission at body size, because a claim
        and its correction set at different sizes is how a correction gets read as a
        disclaimer.
      */}
      {definition.mission ? (
        <section className="mx-auto max-w-[1200px] px-4 py-section">
          <div className="flex max-w-[62ch] flex-col gap-md">
            {VISION_BODY_KEYS.map((key) => (
              <p key={key} className="text-body text-on-surface-variant">
                {t(key)}
              </p>
            ))}
          </div>

          <h2 className="mt-section font-display text-hero-sm text-on-surface">
            {t('www.page.vision.missionHeading')}
          </h2>
          <p className="mt-md max-w-[62ch] text-body text-on-surface">{t(MISSION_KEY)}</p>
          <p className="mt-sm max-w-[62ch] text-body text-on-surface-variant">
            {t(MISSION_QUALIFIER_KEY)}
          </p>
        </section>
      ) : null}

      {/* 3 · Benefits. `/vision` shows the value cards with their anchors. */}
      {definition.benefits === 'values' ? (
        <section className="mx-auto max-w-[1200px] px-4 py-section">
          <h2 className="font-display text-hero-sm text-on-surface">
            {t('www.page.vision.valuesHeading')}
          </h2>
          <ul className="mt-lg grid gap-md md:grid-cols-2 lg:grid-cols-3">
            {roleValues().map((value) => (
              <li
                key={value.id}
                className="flex flex-col gap-xs rounded-card border border-outline-variant p-lg"
              >
                <h3 className="font-display text-title text-on-surface">{t(value.title)}</h3>
                <p className="text-body-sm text-on-surface-variant">{t(value.body)}</p>
                {/*
                  S16: "Every value card shows what makes it true — the anchors S07
                  attached are for the reader as much as for review." A spec path is
                  not a hyperlink here (the specs are not published), so it renders
                  as a citation: visible, copyable, checkable by anyone with the
                  repository, and quiet enough not to compete with the claim.
                */}
                <p className="mt-auto pt-sm text-body-sm text-on-surface-variant">
                  <span className="font-medium">{t('www.common.sourceLabel')}</span>{' '}
                  <span className="break-all font-mono text-[0.75em]">{value.source}</span>
                </p>
              </li>
            ))}
          </ul>
        </section>
      ) : (
        <section className="mx-auto max-w-[1200px] px-4 py-section">
          <ul className="grid gap-lg md:grid-cols-2">
            {copy.sections.map((section) => (
              <li key={section.heading} className="flex flex-col gap-xs">
                <h2 className="font-display text-title text-on-surface">{t(section.heading)}</h2>
                {section.body ? (
                  <p className="max-w-[54ch] text-body-sm text-on-surface-variant">
                    {t(section.body)}
                  </p>
                ) : null}
              </li>
            ))}
          </ul>
        </section>
      )}

      {/* 4 · How it works, where the page has a funnel. */}
      {definition.howItWorks ? (
        <section className="bg-surface py-section">
          <div className="mx-auto max-w-[1200px] px-4">
            <h2 className="font-display text-hero-sm text-on-surface">{t(HOME.how.heading)}</h2>
            <ol className="mt-lg grid gap-lg sm:grid-cols-2 lg:grid-cols-4">
              {definition.howItWorks.map((step, index) => (
                <li key={step.title} className="flex flex-col gap-xs">
                  <span aria-hidden className="text-body-sm font-medium text-secondary">
                    {index + 1}
                  </span>
                  <h3 className="font-display text-title text-on-surface">{t(step.title)}</h3>
                  <p className="text-body-sm text-on-surface-variant">{t(step.body)}</p>
                </li>
              ))}
            </ol>
          </div>
        </section>
      ) : null}

      {/*
        5 · The fee table — `/drivers` only, and the same `FareTable` band the home
        page renders. Both read `DAILY_FEE_TIERS`, so "the same six values" is a
        property of there being one array rather than of two lists agreeing.

        The URD §1 quotation sits directly above it, because the table without the
        rule reads as "drivers pay this much" when the rule is "drivers keep
        everything and pay this once a day from the second trip".
      */}
      {definition.fareTable ? (
        <>
          <section className="mx-auto max-w-[1200px] px-4 pt-section">
            <blockquote className="border-l-4 border-primary pl-lg">
              <p className="max-w-[62ch] text-body text-on-surface">
                {t(FREE_FIRST_TRIP_QUOTE_KEY)}
              </p>
              <footer className="mt-xs text-body-sm text-on-surface-variant">
                <span className="font-medium">{t('www.common.sourceLabel')}</span>{' '}
                <span className="break-all font-mono text-[0.75em]">
                  {FREE_FIRST_TRIP_SOURCE}
                </span>
              </footer>
            </blockquote>
          </section>
          <FareTable locale={locale} />
        </>
      ) : null}

      {/* 6 · The screen strip. */}
      {definition.screenSurface ? (
        <section className="py-section">
          <div className="mx-auto max-w-[1200px] px-4">
            <h2 id={SCREEN_STRIP_HEADING_ID} className="font-display text-hero-sm text-on-surface">
              {t(HOME.screens.heading)}
            </h2>
          </div>
          {/*
            **The strip is a focusable region, and without that it was unreachable
            by keyboard** — S19's axe run reported `scrollable-region-focusable`
            here on every role page, in both appearances.

            `ScreenCarousel` shares this `mr-carousel-track` utility and *is* fine,
            which is what made the difference easy to miss: its thumbnails are
            `<button>`s, so tabbing through them scrolls the container as a side
            effect. This strip is images and captions — 7456px of content in a
            1280px viewport with **zero focusable descendants**, which means a
            keyboard reader could see the first two screens and had no way to reach
            the rest. That is WCAG 2.1.1, not a nicety.

            The fix is a tab stop and a name, after which the arrow keys are the
            browser's own. The name comes from the `<h2>` above rather than from a
            new resource, so the list cannot end up announcing something different
            from the heading a sighted reader sees.

            **It is deliberately not `role="region"`, and that is the second half of
            this fix.** `HeroCarousel` documents the full APG scrollable-region
            pattern and can afford it — its track is a `<div>`. Putting the same
            `role` on a `<ul>` *replaces* the implicit `list`, which orphans all 31
            `<li>` children and trades one serious axe violation for another. S19 hit
            that trap twice in one pass — the first time was `role="group"` on the
            `/screens` filter chips — so it is written down here rather than
            rediscovered a third time: **a `role` on a `<ul>` is never additive.**
            `scrollable-region-focusable` asks only that the scroller be focusable;
            the role was never what satisfied it.

            The lint rule below assumes a static element handed interactive
            behaviour it should not have. A scroll container is keyboard-interactive
            by construction and this adds no handler at all — only the tab stop the
            browser needs before it will scroll for anyone.
          */}
          <ul
            aria-labelledby={SCREEN_STRIP_HEADING_ID}
            // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex
            tabIndex={0}
            className="mr-carousel-track mt-lg gap-md px-4 pb-sm"
          >
            {roleScreens(definition.screenSurface).map((screen) => (
              <li key={screen.id} className="shrink-0 snap-start">
                <ScreenImage
                  screen={screen}
                  alt={t(screen.captionKey)}
                  sizes="14rem"
                  className="w-[14rem]"
                />
                <p className="mt-xs w-[14rem] text-body-sm text-on-surface-variant">
                  {t(screen.captionKey)}
                </p>
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      {/* 7 · The FAQ subset — ids from `faq.ts`, never duplicated prose. */}
      <section className="mx-auto max-w-[1200px] px-4 py-section">
        <h2 className="font-display text-hero-sm text-on-surface">{t(HOME.faq.heading)}</h2>
        <Reveal stagger variant="rise" className="mt-lg flex flex-col gap-md">
          {faq.map((entry) => (
            <div key={entry.id} className="flex flex-col gap-xxs">
              <h3 className="font-display text-title text-on-surface">{t(entry.question)}</h3>
              <p className="max-w-[70ch] text-body-sm text-on-surface-variant">{t(entry.answer)}</p>
            </div>
          ))}
        </Reveal>
        <Link
          href={href(locale, 'faq')}
          className="mt-lg inline-flex min-h-cta items-center rounded-lg border border-outline px-lg py-xs text-body font-medium text-on-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
        >
          {t(HOME.faq.more)}
        </Link>
      </section>

      {/* 8 · The guide entry point and the closing CTA. */}
      {definition.cta ? (
        <section className="mx-auto max-w-[1200px] px-4 pb-section-lg">
          <div className="flex flex-col items-start gap-md rounded-card bg-primary-container p-lg sm:p-section">
            <Link
              href={href(
                locale,
                definition.guide === 'contact'
                  ? 'contact'
                  : definition.guide === 'none'
                    ? ''
                    : guideEntryPath(definition.guide),
              )}
              className={cx(
                'min-h-cta rounded-lg bg-primary px-lg py-xs text-body font-medium mr-on-primary',
                'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary',
              )}
            >
              {t(definition.cta)}
            </Link>

            {/*
              `/fleets` names the portal it links nowhere near — fleet owners work in
              `fleet.mageride.lk`, which is a different surface with its own login,
              and this site does not link into an authenticated product.
            */}
            {path === 'fleets' ? (
              <p className="max-w-[54ch] text-body-sm text-on-primary-container">
                {t('www.page.fleets.portalNote')}
              </p>
            ) : null}
          </div>
        </section>
      ) : null}
    </>
  );
}
