import { Suspense } from 'react';

import { GalleryBody } from '@/components/screens/GalleryBody';
import { galleryLabels } from '@/components/screens/galleryLabels';
import { ScreenGallery } from '@/components/screens/ScreenGallery';
import { PAGES } from '@/content/pages';
import { SCREEN_PROVENANCE_KEY } from '@/content/screens';
import { createWwwTranslator } from '@/i18n';
import { localeFrom, type LocaleParams } from '@/lib/params';
import { metadataForRoute } from '@/lib/seo';
import { ALL } from '@/lib/screen-filter';

/**
 * `/{locale}/screens` — the gallery.
 *
 * The page itself is a **static server render**: a heading, a standfirst, the one
 * provenance sentence, and the gallery. It reads no search param, so it is
 * prerendered like every other route on this surface — the filter is applied in the
 * browser from the same URL, and `ScreenGallery`'s note gives the full reasoning.
 *
 * ## The `<Suspense>` fallback **is** the gallery, and that is not a nicety
 *
 * `useSearchParams()` inside a prerendered route must sit under a boundary — and
 * Next prerenders **the fallback, not the component**. The first cut used
 * `fallback={null}` on the reasoning that the boundary "never shows on a real
 * request", which was wrong in the expensive direction: the served HTML for
 * `/en/screens` was 27 kB containing no screens, no captions and no links into the
 * guide. In a browser it looked perfect, because a browser runs the JavaScript. To
 * a crawler — on the one page of this site whose entire content is pictures of the
 * product — it was empty.
 *
 * So the fallback is `GalleryBody` with the empty selection: the same component the
 * client re-renders, holding all seventy plates. The prerendered document is the
 * complete gallery, hydration swaps it for the filtered one without moving
 * anything, and a reader with no JavaScript keeps the whole thing.
 *
 * ## The heading says what these pictures are
 *
 * MCS-34 D10 renders the frames from `specs/wireframes/*.html` through a polish
 * stylesheet. They are faithful to designs the team approved; they are not
 * photographs of a released app, and S18 is explicit that the honest way to say so
 * is one clear line on the page rather than a disclaimer under every tile.
 */
export async function generateMetadata({ params }: { params: Promise<LocaleParams> }) {
  return metadataForRoute(await localeFrom(params), 'screens');
}

export default async function ScreensPage({ params }: { params: Promise<LocaleParams> }) {
  const locale = await localeFrom(params);
  const t = createWwwTranslator(locale);
  /*
   * Built once and handed to both passes — the prerendered `GalleryBody` fallback and
   * the hydrated `ScreenGallery` — so the two cannot render different words for the
   * same tile (MCS-36 D3).
   */
  const labels = galleryLabels(locale);
  const copy = PAGES.screens;

  return (
    <div className="mx-auto flex max-w-[1200px] flex-col gap-section px-4 py-section">
      <header className="flex flex-col gap-md">
        <h1 className="max-w-[20ch] font-display text-hero text-balance text-on-surface">
          {t(copy?.title ?? 'www.page.screens.title')}
        </h1>
        <p className="max-w-[62ch] text-body text-on-surface-variant">
          {t(copy?.intro ?? 'www.page.screens.intro')}
        </p>
        <p className="max-w-[62ch] text-body-sm text-on-surface-variant">
          {t(SCREEN_PROVENANCE_KEY)}
        </p>
      </header>

      <Suspense fallback={<GalleryBody labels={labels} selection={ALL} />}>
        <ScreenGallery labels={labels} />
      </Suspense>
    </div>
  );
}
