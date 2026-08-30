import Link from 'next/link';

import { cx } from '@mageride/ui';

import { ScreenImage } from '@/components/ScreenImage';
import { AuroraBackdrop } from '@/components/motion/AuroraBackdrop';
import { HeroCarousel } from '@/components/motion/HeroCarousel';
import { heroCarouselLabels } from '@/components/motion/heroCarouselLabels';
import { HERO_SLIDES, type HeroSlide } from '@/content/marketing';
import { SCREENS } from '@/content/screens';
import { createWwwTranslator, type Locale, type WwwMessageKey } from '@/i18n';
import { href } from '@/lib/routes';

/**
 * The home hero — four slides, from `HERO_SLIDES`.
 *
 * The carousel *mechanism* is S04's `HeroCarousel` (scroll-snap for swipe, one
 * shared `IntersectionObserver` for the dots, a timer that never starts under
 * reduced motion). What this adds is the content, the layout, and the two APG
 * obligations that need to know what a slide *is*: the live-region announcement
 * names the slide by its headline, and the first slide's image is the one frame
 * on the page fetched eagerly.
 *
 * ## Exactly one `<h1>`, and it is not per slide
 *
 * Each slide has a headline, and the tempting markup gives each one an `<h1>`.
 * That produces four `<h1>`s on the home page, three of which are scrolled out of
 * view — so a screen reader's heading list offers four competing page titles and
 * the a11y test S20 writes ("exactly one `<h1>` per page") fails. The slide
 * headlines are `<h2>`, and the page's single `<h1>` is the mission statement
 * above the carousel: a fixed, honest description of what MageRide is, which is
 * also the better thing for a search result to quote.
 *
 * ## Where `www.mission.qualifier` goes, and why it is not optional
 *
 * MCS-34 **D1** chose a national-infrastructure framing — *"one live picture of how
 * the country moves"* — and its own decision note records that the framing carries
 * a coverage claim which **is not true on launch day**. S07 wrote the qualifier for
 * exactly that, and `portals/www/CLAUDE.md` calls it *"required furniture wherever
 * the mission renders"*. It renders here, directly beneath the mission, at readable
 * size. **A later session may move it; it may not drop it for balance.**
 *
 * ## No fare, no count, no live anything
 *
 * The hero is where a marketing site reaches for a number, and every number worth
 * reaching for here would break a fence: a live vehicle count is a request at
 * request time, a fare estimate is an API call, and "serving N cities" is a claim
 * that goes stale silently. The stats band (S15) renders the four numbers that are
 * constants in `src/content/marketing.ts` with their spec anchors. This section
 * asserts nothing that is not in the copy.
 */
export function Hero({ locale }: { readonly locale: Locale }) {
  const t = createWwwTranslator(locale);

  return (
    <section className="relative isolate overflow-hidden">
      <AuroraBackdrop className="-z-10" />

      <div className="mx-auto max-w-[1200px] px-4 pt-section pb-lg">
        <h1 className="max-w-[22ch] font-display text-hero text-balance text-on-surface">
          {t('www.vision.hero')}
        </h1>
        <p className="mt-md max-w-[62ch] text-body text-on-surface-variant">
          {t('www.mission.statement')}
        </p>
        {/*
          Required furniture (MCS-34 D1 · S07). Not small print: it is set at body
          size in the same column as the mission it qualifies, because a coverage
          claim and its correction being different sizes is how a correction gets
          read as a disclaimer.
        */}
        <p className="mt-sm max-w-[62ch] text-body-sm text-on-surface-variant">
          {t('www.mission.qualifier')}
        </p>
      </div>

      <HeroCarousel
        labels={heroCarouselLabels(locale, HERO_SLIDES.map((slide) => t(slide.headline)))}
        className="mx-auto max-w-[1200px] px-4 pb-section"
        slides={HERO_SLIDES.map((slide, index) => (
          <HeroSlideContent key={slide.id} locale={locale} slide={slide} first={index === 0} />
        ))}
      />
    </section>
  );
}

/**
 * One slide: headline, sub, two calls to action, and the device frames.
 *
 * The CTA targets are derived from the key rather than added to `HeroSlide`,
 * because a `href` in a content module is a route that `src/lib/routes.ts` does not
 * know about — and an unlisted route is the failure that table exists to catch.
 */
function HeroSlideContent({
  locale,
  slide,
  first,
}: {
  readonly locale: Locale;
  readonly slide: HeroSlide;
  /** The first slide's frames are the only eagerly-fetched images on the page. */
  readonly first: boolean;
}) {
  const t = createWwwTranslator(locale);
  const screens = slide.screens
    .map((id) => SCREENS.find((screen) => screen.id === id))
    .filter((screen) => screen !== undefined);

  return (
    <div className="grid items-center gap-lg lg:grid-cols-2 lg:gap-section">
      <div className="flex flex-col items-start gap-md">
        {/*
          `<h2>`, not `<h1>` — see the note on `Hero`. The hero scale is the
          marketing utility `text-hero-sm`, whose Sinhala and Tamil leading S12
          widened in `@layer utilities` because Noto's ink is taller than its own
          line box.
        */}
        <h2 className="max-w-[18ch] font-display text-hero-sm text-balance text-on-surface">
          {t(slide.headline)}
        </h2>
        <p className="max-w-[52ch] text-body text-on-surface-variant">{t(slide.sub)}</p>

        <div className="flex flex-wrap items-center gap-sm">
          <HeroCta locale={locale} labelKey={slide.primaryCta} variant="primary" />
          <HeroCta locale={locale} labelKey={slide.secondaryCta} variant="secondary" />
        </div>
      </div>

      {/*
        The frames. `aria-hidden` is deliberately NOT set: each carries its
        registry caption as `alt`, and those captions are the only description a
        screen-reader user gets of what the app looks like.

        Two or three frames per slide, overlapped on wide screens and reduced to
        the first on a phone — three 416px plates side by side at 375px is three
        illegible thumbnails, and the second and third are decorative there.
      */}
      <div className="flex items-center justify-center gap-md">
        {screens.map((screen, index) => (
          <ScreenImage
            key={screen.id}
            screen={screen}
            alt={t(screen.captionKey)}
            priority={first && index === 0}
            sizes="(min-width: 1024px) 20rem, 60vw"
            className={cx(
              'w-[min(60vw,20rem)] shrink-0',
              index > 0 && 'hidden lg:block',
              index === 1 && 'lg:-ml-section lg:mt-lg lg:rotate-3',
              index === 2 && 'lg:-ml-section lg:mb-lg lg:-rotate-3',
            )}
          />
        ))}
      </div>
    </div>
  );
}

/**
 * A hero call to action.
 *
 * Every CTA on this site is a **link**, never a button: each one navigates, and a
 * `<button>` that navigates is a control a keyboard user cannot open in a new tab
 * and a crawler cannot follow. The five CTA keys map to the five routes below;
 * `href()` composes them from the route table, so a CTA cannot point at a page
 * this site does not publish.
 */
const CTA_TARGETS: Readonly<Record<string, string>> = {
  'www.cta.getTheApp': 'download',
  'www.cta.seeHowItWorks': 'guide',
  'www.cta.passengerGuide': 'passengers',
  'www.cta.driveWithUs': 'drivers',
  'www.cta.seeTheFees': 'drivers',
};

function HeroCta({
  locale,
  labelKey,
  variant,
}: {
  readonly locale: Locale;
  readonly labelKey: WwwMessageKey;
  readonly variant: 'primary' | 'secondary';
}) {
  const t = createWwwTranslator(locale);
  const target = CTA_TARGETS[labelKey] ?? '';

  return (
    <Link
      href={href(locale, target)}
      className={cx(
        'inline-flex min-h-cta items-center rounded-lg px-lg py-xs text-body font-medium',
        'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary',
        variant === 'primary'
          ? 'bg-primary mr-on-primary hover:bg-primary/90'
          : 'border border-outline text-on-surface hover:bg-surface-variant',
      )}
    >
      {t(labelKey)}
    </Link>
  );
}
