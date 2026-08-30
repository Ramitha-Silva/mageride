/**
 * `/screens`' filter, as a value — parsed from a URL, rendered back into one, and
 * applied to the registry. **No React, no DOM, no `window`.**
 *
 * S18: *"Filters are **URL state**, not component state: `?surface=driver&mode=c`. A
 * filtered view survives a reload, a bookmark and the back button, and it is
 * shareable. `portals/admin`'s SCR-AP-002 is the precedent in this repo for 'a
 * server render whose entire state is the URL' — follow it."*
 *
 * `portals/admin/src/components/dashboard/StatsFilter.tsx` is that precedent and
 * this follows its shape exactly: **the controls are links, and the component holds
 * no state whatsoever.** Every chip is an `<a>` to the URL that selection produces,
 * so the back button steps through a reader's comparisons, a filtered view pastes
 * into a message, and a keyboard reaches every option without a single key handler.
 *
 * ## Where it diverges from SCR-AP-002, and why it must
 *
 * The admin page reads `searchParams` **on the server** and renders the filtered
 * rows. This surface cannot: `portals/www/CLAUDE.md` — *"No page below `[locale]`
 * may read a header, a cookie or a search param. That is what keeps all 39 URLs
 * statically renderable, and static rendering is how the site survives the platform
 * being down."* Awaiting `searchParams` in `app/[locale]/screens/page.tsx` would opt
 * that route out of the prerender, and it would do so to sort seventy items that are
 * all already in the bundle.
 *
 * So the **page** ships every screen and the **filter** is applied in the browser
 * from `useSearchParams()`. What that buys, and it is more than the static render:
 *
 *   - the prerendered HTML holds all seventy captions, so a crawler — and a reader
 *     with no JavaScript — gets the **whole** gallery rather than a filtered slice
 *     of it or an empty grid waiting on hydration;
 *   - the URL is still the entire state, so every property S18 asks for holds;
 *   - a filter link with JavaScript off navigates to a page showing everything,
 *     which is a degradation a reader can see past rather than a broken control.
 *
 * This module is the half of that arrangement that has no environment in it, which
 * is what makes it the half with the tests.
 */

import { modesForScreen } from '@/content/screen-modes';
import type { GuideChapterRef } from '@/content/chapters';
import { SCREENS, type ScreenEntry, type Surface } from '@/content/screens';
import type { TransportModeId } from '@/content/marketing';
import { MODE_IDS } from '@/content/screen-modes';

/** The query parameter each facet is spelled with. One place, three readers. */
export const FILTER_PARAMS = {
  surface: 'surface',
  mode: 'mode',
  chapter: 'chapter',
} as const;

/**
 * A selection. `null` on a facet means "every value" — the unfiltered gallery is
 * three nulls and a bare `/screens`, with no query string at all.
 */
export interface ScreenSelection {
  readonly surface: Surface | null;
  readonly mode: TransportModeId | null;
  readonly chapter: GuideChapterRef | null;
}

export const ALL: ScreenSelection = { surface: null, mode: null, chapter: null };

/**
 * The surfaces the gallery offers, **derived from the registry in registry order**.
 *
 * Not the `Surface` union: that type admits `'admin'`, and S05 selected no
 * `SCR-AP-*` frame at all — *"every admin screen that could illustrate something
 * public shows staff tooling and real-looking personal records to an audience that
 * will never sign in to it"*. A chip that filters seventy screens down to none is a
 * broken control, and hard-coding four values would be a fifth place to remember
 * when S23's fleet chapters land.
 */
export const GALLERY_SURFACES: readonly Surface[] = [
  ...new Set(SCREENS.map((screen) => screen.surface)),
];

/** The chapters that actually illustrate something, in registry order. */
export const GALLERY_CHAPTERS: readonly GuideChapterRef[] = [
  ...new Set(SCREENS.flatMap((screen) => screen.chapters)),
];

function asSurface(value: string | null): Surface | null {
  return value !== null && (GALLERY_SURFACES as readonly string[]).includes(value)
    ? (value as Surface)
    : null;
}

function asMode(value: string | null): TransportModeId | null {
  return value !== null && (MODE_IDS as readonly string[]).includes(value)
    ? (value as TransportModeId)
    : null;
}

function asChapter(value: string | null): GuideChapterRef | null {
  return value !== null && (GALLERY_CHAPTERS as readonly string[]).includes(value)
    ? (value as GuideChapterRef)
    : null;
}

/**
 * The selection a URL asks for.
 *
 * **An unrecognised value is dropped, never honoured and never an error.** A query
 * string is the one input on this site that arrives from outside it — a truncated
 * paste, a stale bookmark from before a slug changed, a crawler appending its own
 * tracking parameter — and the only sane answer to `?surface=lorry` is the whole
 * gallery. Throwing would 500 a marketing page over a typo; showing nothing would
 * look like a site with no screens in it.
 *
 * Takes the read-only shape both `URLSearchParams` and Next's `useSearchParams()`
 * satisfy, so the tests need no browser.
 */
export function selectionFrom(params: { get(name: string): string | null }): ScreenSelection {
  return {
    surface: asSurface(params.get(FILTER_PARAMS.surface)),
    mode: asMode(params.get(FILTER_PARAMS.mode)),
    chapter: asChapter(params.get(FILTER_PARAMS.chapter)),
  };
}

/**
 * The query string for a selection, `?` included, or `''` for the unfiltered one.
 *
 * Order is fixed — surface, then mode, then chapter — so the same selection always
 * produces the same URL. Two spellings of one view would be two entries in a
 * reader's history and, once S19 wires the sitemap and canonicals, two addresses
 * for one document.
 */
export function searchFor(selection: ScreenSelection): string {
  const params = new URLSearchParams();
  if (selection.surface) params.set(FILTER_PARAMS.surface, selection.surface);
  if (selection.mode) params.set(FILTER_PARAMS.mode, selection.mode);
  if (selection.chapter) params.set(FILTER_PARAMS.chapter, selection.chapter);

  const query = params.toString();
  return query === '' ? '' : `?${query}`;
}

/**
 * The selection produced by pressing one chip — **a toggle, not an assignment**.
 *
 * Pressing the chip that is already active clears that facet, so every filter can be
 * undone from the control that set it. Without it the only way out of `?surface=fleet`
 * is a separate "clear" affordance, and a reader who has narrowed to two screens on a
 * phone has to find it.
 */
export function toggled<K extends keyof ScreenSelection>(
  selection: ScreenSelection,
  facet: K,
  value: NonNullable<ScreenSelection[K]>,
): ScreenSelection {
  return { ...selection, [facet]: selection[facet] === value ? null : value };
}

/** Whether a screen survives a selection. Facets are ANDed; a null facet passes. */
export function matches(screen: ScreenEntry, selection: ScreenSelection): boolean {
  if (selection.surface !== null && screen.surface !== selection.surface) return false;
  if (selection.mode !== null && !modesForScreen(screen).includes(selection.mode)) return false;
  if (selection.chapter !== null && !screen.chapters.includes(selection.chapter)) return false;
  return true;
}

/** The screens a selection shows, in registry order. */
export function filterScreens(selection: ScreenSelection): readonly ScreenEntry[] {
  return SCREENS.filter((screen) => matches(screen, selection));
}
