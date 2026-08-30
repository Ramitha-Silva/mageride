import { Hero } from '@/components/hero/Hero';
import { DownloadBand } from '@/components/marketing/DownloadBand';
import { FareTable } from '@/components/marketing/FareTable';
import { FeatureSplits } from '@/components/marketing/FeatureSplits';
import { HowItWorks } from '@/components/marketing/HowItWorks';
import { howItWorksLabels } from '@/components/marketing/howItWorksLabels';
import { LanguageBand } from '@/components/marketing/LanguageBand';
import { ModeCards } from '@/components/marketing/ModeCards';
import { StatsBand } from '@/components/marketing/StatsBand';
import { ScreenCarousel } from '@/components/showcase/ScreenCarousel';
import { screenCarouselLabels } from '@/components/showcase/showcaseLabels';
import { HERO_SCREENS } from '@/content/screens';
import { localeFrom, type LocaleParams } from '@/lib/params';

/**
 * The locale home — `/si`, `/en`. **Nine bands, and this file is the running order.**
 *
 * S14 built band 1 (the hero) and the shell around it; S15 built 2–9. The page
 * itself deliberately holds no markup beyond the sequence: every band owns its own
 * container, its own `max-w-[1200px]` cap and its own vertical rhythm, so a band
 * can be reordered, and S16–S18 can reuse one on another page, without inheriting a
 * wrapper written for the home page.
 *
 * The `<main>` landmark and the single `<h1>` are one level up in
 * `app/[locale]/layout.tsx` and in `Hero` respectively — every other band opens
 * with an `<h2>`, so the heading outline is one page title over eight peers.
 *
 * ## The showcase is fed `HERO_SCREENS`, not the whole registry
 *
 * S15 asks for "a curated subset". `HERO_SCREENS` is that subset and it is already
 * curated in the registry — twelve frames flagged `hero` by S05 as the striking
 * ones for each concept — so the choice is data rather than a judgement made here
 * and made differently on the next page. `/screens` (S18) is the page that shows
 * all seventy.
 */
export default async function HomePage({ params }: { params: Promise<LocaleParams> }) {
  const locale = await localeFrom(params);

  return (
    <>
      <Hero locale={locale} />
      <ModeCards locale={locale} />
      <HowItWorks labels={howItWorksLabels(locale)} />
      <FeatureSplits locale={locale} />
      <FareTable locale={locale} />
      <ScreenCarousel labels={screenCarouselLabels(locale, HERO_SCREENS)} screens={HERO_SCREENS} />
      <StatsBand locale={locale} />
      <LanguageBand locale={locale} />
      <DownloadBand locale={locale} />
    </>
  );
}
