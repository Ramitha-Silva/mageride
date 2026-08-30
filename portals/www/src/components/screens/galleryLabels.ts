import { chapterByRef } from '@/content/index';
import { TRANSPORT_MODES, type TransportModeId } from '@/content/marketing';
import { MODE_IDS } from '@/content/screen-modes';
import { SCREENS, SURFACE_SECTION_KEYS } from '@/content/screens';
import { createWwwTranslator, type Locale } from '@/i18n';
import { GALLERY_CHAPTERS, GALLERY_SURFACES } from '@/lib/screen-filter';
import { href } from '@/lib/routes';

import type { GalleryLabels, TileLabels } from './GalleryBody';

/** A mode's name key — the same string the home page's mode cards use. */
function modeName(mode: TransportModeId) {
  const found = TRANSPORT_MODES.find((entry) => entry.id === mode);
  if (!found) throw new Error(`screen gallery: no TRANSPORT_MODES entry for mode "${mode}"`);
  return found.name;
}

/**
 * Every string `/screens` renders, resolved on the server (MCS-36 D3).
 *
 * **All seventy tiles, always** — not just the filtered ones. The page prerenders the
 * complete gallery as the `<Suspense>` fallback so a JS-off reader gets everything
 * (S18), and the client narrows the same list; resolving only what the current
 * selection shows would empty the page the moment somebody followed a filtered link
 * with JavaScript disabled.
 *
 * This is the largest label object on the surface — seventy captions and their guide
 * links — and it is still an order of magnitude smaller than the resource tables it
 * replaces, because it is *this page's* strings rather than every page's.
 */
export function galleryLabels(locale: Locale): GalleryLabels {
  const t = createWwwTranslator(locale);

  const tiles: Record<string, TileLabels> = {};
  for (const screen of SCREENS) {
    const caption = t(screen.captionKey);
    tiles[screen.id] = {
      caption,
      openLabel: t('www.showcase.open', { caption }),
      chapters: screen.chapters
        .map((ref) => chapterByRef(ref))
        .filter((chapter) => chapter !== undefined)
        .map((chapter) => ({
          href: href(locale, `guide/${chapter.audience}/${chapter.slug}`),
          title: t(chapter.title),
        })),
    };
  }

  return {
    legend: t('www.screens.filter.legend'),
    facetSurface: t('www.screens.filter.surface'),
    facetMode: t('www.screens.filter.mode'),
    facetChapter: t('www.screens.filter.chapter'),
    surfaceChips: GALLERY_SURFACES.map((surface) => ({
      value: surface,
      label: t(SURFACE_SECTION_KEYS[surface] ?? 'www.page.screens.title'),
    })),
    modeChips: MODE_IDS.map((mode) => ({ value: mode, label: t(modeName(mode)) })),
    chapterChips: GALLERY_CHAPTERS.map((ref) => {
      const chapter = chapterByRef(ref);
      return { value: ref, label: chapter ? t(chapter.title) : ref };
    }).filter((chip) => chip.label !== chip.value),
    // The one string the server cannot finish — the count is the URL's, not ours.
    showingTemplate: t('www.screens.filter.showing', {}),
    total: SCREENS.length,
    galleryHref: href(locale, 'screens'),
    clear: t('www.screens.filter.clear'),
    empty: t('www.screens.empty'),
    sectionHeadings: Object.fromEntries(
      GALLERY_SURFACES.map((surface) => [
        surface,
        t(SURFACE_SECTION_KEYS[surface] ?? 'www.page.screens.title'),
      ]),
    ),
    inGuide: t('www.screens.tile.inGuide'),
    tiles,
    lightbox: {
      title: t('www.showcase.lightbox.title'),
      close: t('modal.close'),
      previous: t('www.common.previous'),
      next: t('www.common.next'),
      positionTemplate: t('www.showcase.lightbox.position', {}),
    },
  };
}
