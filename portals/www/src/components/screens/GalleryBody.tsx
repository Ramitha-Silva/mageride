import Link from 'next/link';

import { cx } from '@mageride/ui';

import { ScreenImage } from '@/components/ScreenImage';
import type { GuideChapterRef } from '@/content/chapters';
import type { TransportModeId } from '@/content/marketing';
import { SCREENS, type ScreenEntry, type Surface } from '@/content/screens';
import { substitute } from '@/i18n/substitute';
import {
  ALL,
  GALLERY_SURFACES,
  filterScreens,
  matches,
  searchFor,
  toggled,
  type ScreenSelection,
} from '@/lib/screen-filter';

/**
 * The gallery's markup — filter bar, count, and every screen — **rendered by the
 * server and by the browser from the same component**.
 *
 * ## Why this file has no `'use client'` on it
 *
 * A module with no directive belongs to whichever graph imports it. `page.tsx`
 * imports it as a server component and prerenders it with {@link ALL}; `./ScreenGallery`
 * imports it as a client component and re-renders it with whatever the URL asks
 * for. One component, two roles, one piece of markup.
 *
 * **That arrangement is the fix for a real bug, found by walking the built site.**
 * The first cut had the whole gallery inside the client component, under the
 * `<Suspense>` boundary `useSearchParams()` requires. Next then prerenders the
 * *fallback* and nothing else — the served HTML for `/en/screens` was 27 kB with
 * **zero** screens, zero captions and zero chapter links in it. Every claim made
 * for the design ("a crawler and a JS-off reader get the whole gallery") was false,
 * and it was false in the way that is hardest to notice: the page looks perfect in
 * a browser, because a browser runs the JavaScript.
 *
 * Rendering the same body on both sides fixes three things at once:
 *
 *   - **the prerendered HTML is the complete gallery** — all seventy plates, their
 *     captions, and their links into the guide;
 *   - **there is no layout shift on hydration**, because the client renders the
 *     same elements in the same order rather than inserting a filter bar above a
 *     grid that has already painted;
 *   - **with JavaScript off the page is whole.** The chips are real links, so they
 *     navigate; every destination renders the unfiltered gallery, which is a
 *     degradation a reader can see past rather than a control that does nothing.
 *
 * ## `onOpen` is what makes a tile interactive
 *
 * The server pass has no lightbox to open, so it passes nothing and each tile is a
 * plain figure. The client pass passes `useLightbox`'s opener and each tile becomes
 * a button. A function prop never crosses the boundary — the server renders its own
 * tree — so this stays a shared component rather than a serialisation problem.
 */
/**
 * Every string the gallery renders, resolved on the server (MCS-36 D3).
 *
 * **This component renders on both sides of the boundary**, which is why the labels
 * are a prop rather than something a hook fetches: `app/[locale]/screens/page.tsx`
 * prerenders it as the `<Suspense>` fallback — the complete, unfiltered gallery, so a
 * JS-off reader gets everything — and `ScreenGallery` re-renders it with the URL's
 * selection. Both passes need the same strings and neither may hold a table.
 *
 * `tiles` is keyed by screen id rather than being an array, because the client
 * filters the list and an index would stop meaning what it meant on the server.
 */
export interface GalleryLabels {
  readonly legend: string;
  readonly facetSurface: string;
  readonly facetMode: string;
  readonly facetChapter: string;
  readonly surfaceChips: readonly { readonly value: string; readonly label: string }[];
  readonly modeChips: readonly { readonly value: string; readonly label: string }[];
  readonly chapterChips: readonly { readonly value: string; readonly label: string }[];
  /**
   * `"Showing {count} of {total}"`, still carrying its placeholders.
   *
   * The one string on this surface the server cannot finish: the count depends on a
   * filter the URL chooses. `@/i18n/substitute` fills it — nine lines and no table.
   */
  readonly showingTemplate: string;
  readonly total: number;
  /** `/{locale}/screens`, for building the chips' hrefs. */
  readonly galleryHref: string;
  readonly clear: string;
  readonly empty: string;
  /** Section heading per surface. */
  readonly sectionHeadings: Readonly<Record<string, string>>;
  readonly inGuide: string;
  readonly tiles: Readonly<Record<string, TileLabels>>;
  /**
   * The lightbox's fixed strings, plus the announcement as a **template**.
   *
   * Everywhere else the dialog's `positions` are resolved server-side, because the
   * slide count is known at build. Here it is not: `ScreenGallery` opens the dialog
   * over the *filtered* list, so both the count and which screens are in it come from
   * the URL. The four fixed strings still resolve on the server; the per-slide ones
   * are built on the client from this template and the tiles' captions.
   */
  readonly lightbox: {
    readonly title: string;
    readonly close: string;
    readonly previous: string;
    readonly next: string;
    /** `"Screen {index} of {count}"`, still carrying its placeholders. */
    readonly positionTemplate: string;
  };
}

/** One screen tile's strings and its guide links. */
export interface TileLabels {
  readonly caption: string;
  readonly openLabel: string;
  readonly chapters: readonly { readonly href: string; readonly title: string }[];
}

export function GalleryBody({
  labels,
  selection,
  onOpen,
}: {
  readonly labels: GalleryLabels;
  readonly selection: ScreenSelection;
  /** Absent on the server pass; present once the lightbox exists. */
  readonly onOpen?: (index: number, trigger: HTMLElement | null) => void;
}) {

  const visible = filterScreens(selection);
  const position = new Map(visible.map((screen, index) => [screen.id, index]));
  const filtered = selection.surface !== null || selection.mode !== null || selection.chapter !== null;

  // The base is a label too: `href(locale, 'screens')` needs a locale, and this
  // component no longer has one — the server resolves it once instead.
  const galleryHref = (next: ScreenSelection) => `${labels.galleryHref}${searchFor(next)}`;

  /** Would this value leave anything? A selected value always stays reachable. */
  const survives = <K extends keyof ScreenSelection>(
    facet: K,
    value: NonNullable<ScreenSelection[K]>,
  ) =>
    selection[facet] === value ||
    SCREENS.some((screen) => matches(screen, { ...selection, [facet]: value }));

  function chip<K extends keyof ScreenSelection>(
    facet: K,
    value: NonNullable<ScreenSelection[K]>,
    label: string,
  ) {
    const current = selection[facet] === value;

    return (
      <li key={String(value)}>
        <Link
          href={galleryHref(toggled(selection, facet, value))}
          aria-current={current ? 'true' : undefined}
          className={cx(
            'inline-flex min-h-cta items-center rounded-lg border px-md py-xxs text-body-sm transition-colors',
            'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary',
            current
              ? 'border-primary bg-primary mr-on-primary'
              : 'border-outline text-on-surface-variant hover:bg-surface-variant hover:text-on-surface',
          )}
        >
          {label}
        </Link>
      </li>
    );
  }

  return (
    <>
      {/*
        The filter. `print-hidden` for the reason the guide's rail is: a set of links
        that narrow a page means nothing once the page is paper.
      */}
      <section aria-labelledby={FILTER_LEGEND_ID} className="print-hidden flex flex-col gap-md">
        <h2 id={FILTER_LEGEND_ID} className="text-body font-bold text-on-surface">
          {labels.legend}
        </h2>

        <Facet id={SURFACE_GROUP_ID} label={labels.facetSurface}>
          {labels.surfaceChips
            .filter(({ value }) => survives('surface', value as Surface))
            .map(({ value, label }) => chip('surface', value as Surface, label))}
        </Facet>

        <Facet id={MODE_GROUP_ID} label={labels.facetMode}>
          {labels.modeChips
            .filter(({ value }) => survives('mode', value as TransportModeId))
            .map(({ value, label }) => chip('mode', value as TransportModeId, label))}
        </Facet>

        <Facet id={CHAPTER_GROUP_ID} label={labels.facetChapter}>
          {labels.chapterChips
            .filter(({ value }) => survives('chapter', value as GuideChapterRef))
            .map(({ value, label }) => chip('chapter', value as GuideChapterRef, label))}
        </Facet>

        <div className="flex flex-wrap items-center gap-md">
          {/*
            Announced, because a chip is a client-side navigation: the page does not
            reload and the heading does not change, so without this a screen reader
            is given no evidence that pressing it did anything. Polite and atomic —
            the whole sentence, once, after the grid has settled.
          */}
          <p aria-live="polite" aria-atomic="true" className="text-body-sm text-on-surface-variant">
            {substitute(labels.showingTemplate, { count: visible.length, total: labels.total })}
          </p>

          {filtered ? (
            <Link
              href={galleryHref(ALL)}
              className="rounded-sm text-body-sm text-secondary underline underline-offset-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
            >
              {labels.clear}
            </Link>
          ) : null}
        </div>
      </section>

      {/*
        One section per surface, in registry order, under S07's own headings. The
        surface filter hides whole sections rather than reshuffling tiles, so the
        outline a crawler reads is the one a reader sees and the unfiltered page is a
        four-part document rather than a wall of seventy pictures.
      */}
      {GALLERY_SURFACES.map((surface) => {
        const group = SCREENS.filter((screen) => screen.surface === surface);
        const shown = group.filter((screen) => position.has(screen.id));

        return (
          <section key={surface} hidden={shown.length === 0} className="flex flex-col gap-lg">
            <h2 className="font-display text-hero-sm text-on-surface">
              {labels.sectionHeadings[surface]}
            </h2>

            <ul className="grid gap-lg sm:grid-cols-2 lg:grid-cols-3">
              {group.map((screen) => (
                <Tile
                  key={screen.id}
                  screen={screen}
                  labels={labels.tiles[screen.id]!}
                  inGuide={labels.inGuide}
                  index={position.get(screen.id)}
                  onOpen={onOpen}
                />
              ))}
            </ul>
          </section>
        );
      })}

      {visible.length === 0 ? (
        <p className="text-body text-on-surface-variant">
          {labels.empty}{' '}
          <Link
            href={galleryHref(ALL)}
            className="rounded-sm text-secondary underline underline-offset-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
          >
            {labels.clear}
          </Link>
        </p>
      ) : null}
    </>
  );
}

const FILTER_LEGEND_ID = 'screens-filter';
const SURFACE_GROUP_ID = 'screens-filter-surface';
const MODE_GROUP_ID = 'screens-filter-mode';
const CHAPTER_GROUP_ID = 'screens-filter-chapter';

/**
 * One row of chips.
 *
 * **Not a `<fieldset>`, and no longer a `role="group"` either.** A fieldset belongs
 * to a form and there is **no form anywhere on this surface** (MCS-34's third
 * negative) — these are links. That part was right.
 *
 * `role="group"` was the wrong repair for it, and S19's axe run is what found it:
 * the role *replaced* the `<ul>`'s implicit `list`, which orphaned all 41 `<li>`
 * children — `listitem` (serious) on `/screens` in both appearances and both
 * locales. A list is already a grouping construct and `list` takes a name from
 * `aria-labelledby` exactly as `group` does, so dropping the attribute keeps the
 * label, restores the semantics, and tells a screen reader how many chips there
 * are, which `group` never did.
 */
function Facet({
  id,
  label,
  children,
}: {
  readonly id: string;
  readonly label: string;
  readonly children: React.ReactNode;
}) {
  return (
    <div className="flex flex-col gap-xs">
      <p id={id} className="text-body-sm font-medium text-on-surface-variant">
        {label}
      </p>
      <ul aria-labelledby={id} className="flex flex-wrap gap-xs">
        {children}
      </ul>
    </div>
  );
}

/**
 * One screen.
 *
 * `index` is its position **among the visible screens**, and `undefined` when the
 * filter excludes it — which is also what sets `hidden`. Deriving both from one
 * value is deliberate: a tile that was hidden but still counted would open the
 * lightbox on the wrong picture, and nothing else here would catch that.
 *
 * Without `onOpen` the plate is not a button. That is the server pass, and a
 * non-interactive tile is the honest rendering of it — a `<button>` in markup that
 * will never have a handler is a control a keyboard reaches and nothing answers.
 */
function Tile({
  screen,
  labels,
  inGuide,
  index,
  onOpen,
}: {
  readonly screen: ScreenEntry;
  readonly labels: TileLabels;
  readonly inGuide: string;
  readonly index: number | undefined;
  readonly onOpen?: (index: number, trigger: HTMLElement | null) => void;
}) {
  const sizes = '(min-width: 1024px) 22rem, (min-width: 375px) 45vw, 90vw';

  return (
    <li hidden={index === undefined} className="flex flex-col gap-xs">
      {onOpen ? (
        <button
          type="button"
          aria-label={labels.openLabel}
          onClick={(event) => {
            if (index !== undefined) onOpen(index, event.currentTarget);
          }}
          className="block rounded-card focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
        >
          <ScreenImage screen={screen} alt={labels.caption} sizes={sizes} />
        </button>
      ) : (
        <ScreenImage screen={screen} alt={labels.caption} sizes={sizes} />
      )}

      <p className="text-body-sm text-on-surface">{labels.caption}</p>

      {labels.chapters.length > 0 ? (
        <p className="text-body-sm text-on-surface-variant">
          <span className="font-medium">{inGuide}</span>{' '}
          {labels.chapters.map((chapter, order) => (
            <span key={chapter.href}>
              {order > 0 ? ' · ' : ''}
              <Link
                href={chapter.href}
                className="rounded-sm text-secondary underline underline-offset-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
              >
                {chapter.title}
              </Link>
            </span>
          ))}
        </p>
      ) : null}
    </li>
  );
}

