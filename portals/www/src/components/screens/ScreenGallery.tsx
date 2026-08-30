'use client';

import { useSearchParams } from 'next/navigation';

import { ScreenLightbox, useLightbox, type LightboxLabels } from '@/components/showcase/ScreenLightbox';
import { filterScreens, selectionFrom } from '@/lib/screen-filter';

import { substitute } from '@/i18n/substitute';

import { GalleryBody, type GalleryLabels } from './GalleryBody';

/**
 * The filtered gallery — `./GalleryBody` with the URL's selection applied and the
 * lightbox wired in.
 *
 * **This component holds no state.** The selection comes from the URL and the only
 * hook that is not `useSearchParams` is the lightbox's. Every chip is a `<Link>`, so
 * a filtered view survives a reload, a bookmark and the back button, and it pastes
 * into a message — which is what S18 asks for and what `portals/admin`'s
 * `StatsFilter`, the precedent it names, gets from the same decision.
 *
 * ## Where it diverges from SCR-AP-002, and why it has to
 *
 * The admin page reads `searchParams` **on the server**. This surface may not:
 * `portals/www/CLAUDE.md` — *"No page below `[locale]` may read a header, a cookie
 * or a search param. That is what keeps all 39 URLs statically renderable, and
 * static rendering is how the site survives the platform being down."* Awaiting
 * `searchParams` in `app/[locale]/screens/page.tsx` would opt the route out of the
 * prerender to sort seventy items that are already in the bundle.
 *
 * So the page prerenders `GalleryBody` unfiltered and this replaces it on
 * hydration. The served HTML is therefore the complete gallery rather than an empty
 * grid — see that module's note for the bug that arrangement fixes, which was mine
 * and was invisible in a browser.
 *
 * ## One known consequence, for S19
 *
 * Every chip is a crawlable URL that renders the **same** document as `/screens`,
 * because the filter is applied after hydration. That is duplicate content in a
 * crawler's terms, and the fix belongs with the canonical tags and the sitemap:
 * `/screens` is the canonical, the query-string views are not sitemapped, and S19
 * owns saying so in `<head>`.
 */
export function ScreenGallery({ labels }: { readonly labels: GalleryLabels }) {
  const selection = selectionFrom(useSearchParams());
  const visible = filterScreens(selection);

  /*
   * The dialog's per-slide strings, built here because only here is the list known.
   * Everything in them still came from the server — the four fixed strings whole, the
   * announcements as a template filled with a count the URL decided, and the captions
   * looked up from `labels.tiles` by screen id. No table crosses the boundary
   * (MCS-36 D3).
   */
  const lightboxLabels: LightboxLabels = {
    ...labels.lightbox,
    positions: visible.map((_, index) =>
      substitute(labels.lightbox.positionTemplate, {
        index: index + 1,
        count: visible.length,
      }),
    ),
    captions: visible.map((screen) => labels.tiles[screen.id]?.caption ?? ''),
  };

  const lightbox = useLightbox(lightboxLabels.positions);

  return (
    <>
      <GalleryBody labels={labels} selection={selection} onOpen={lightbox.open} />
      <ScreenLightbox labels={lightboxLabels} screens={visible} controller={lightbox} />
    </>
  );
}
