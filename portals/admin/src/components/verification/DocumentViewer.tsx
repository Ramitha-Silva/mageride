'use client';

import { useCallback, useEffect, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';

import { cx } from '@mageride/ui';

/**
 * SCR-AP-003b — the full-size document viewer, with zoom, rotate and prev/next
 * paging across the entry's documents.
 *
 * ## It is a route, not an overlay
 *
 * `/verification/{subject}/doc/{docId}` is the wireframe's own address bar. That
 * is what makes prev/next real links (so they work with a middle click, and the
 * back button walks back through the pages an officer read), what lets a colleague
 * be sent the exact document under discussion, and what makes closing a
 * navigation rather than a piece of state. A lightbox held in component state
 * would have none of it, and would be the second place this screen decides which
 * document is open.
 *
 * ## Zoom and rotate are the only state here
 *
 * Both are CSS transforms on the image. Nothing is fetched again to zoom — the
 * bytes are already in the browser and re-requesting them would write a second
 * `DOC_VIEW` row for the same look. Rotation is quarter turns because that is
 * what a photograph of a licence needs; zoom is capped so an operator cannot lose
 * the image off the edge of a pane they then have to close to recover.
 *
 * Keyboard: Escape closes, ← and → page. A viewer that can only be left with the
 * mouse is one an officer working a queue of forty will not use.
 */

export interface DocumentViewerLabels {
  readonly close: string;
  readonly previous: string;
  readonly next: string;
  readonly zoomIn: string;
  readonly zoomOut: string;
  readonly rotate: string;
  readonly reset: string;
  /** The alt text — the document's kind, which is all this side knows about it. */
  readonly image: string;
}

/**
 * Zoom and rotation are **class names, not an inline `style`**.
 *
 * AL-52 makes Tailwind the sole styling system and `test/fences.test.ts` asserts
 * the absence of `style={…}` across the tree — a blanket rule, because the moment
 * one component earns an exception the next one cites it. Discrete steps are all
 * a document viewer needs (quarter turns, half-steps of zoom), so each is a class
 * spelled out in full here — spelled out because Tailwind's scanner reads source
 * text, and a class assembled from a template would compile to nothing.
 */
const SCALES = [
  'scale-100',
  'scale-150',
  'scale-200',
  'scale-250',
  'scale-300',
  'scale-350',
  'scale-400',
] as const;

/** `-rotate-90` rather than `rotate-270`: the same quarter turn, in Tailwind's own set. */
const ROTATIONS = ['rotate-0', 'rotate-90', 'rotate-180', '-rotate-90'] as const;

export function DocumentViewer({
  src,
  title,
  closeHref,
  previousHref,
  nextHref,
  provenance,
  labels,
}: {
  src: string;
  /** "Licence front · 1 / 6" — built on the server, where the translator is. */
  title: string;
  closeHref: string;
  previousHref?: string;
  nextHref?: string;
  /** AL-43's capture provenance, when the upload recorded one. */
  provenance?: string;
  labels: DocumentViewerLabels;
}) {
  const router = useRouter();
  const [scale, setScale] = useState(0);
  const [rotation, setRotation] = useState(0);

  const zoom = useCallback((by: number) => {
    setScale((current) => Math.min(SCALES.length - 1, Math.max(0, current + by)));
  }, []);

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') router.push(closeHref);
      else if (event.key === 'ArrowLeft' && previousHref) router.push(previousHref);
      else if (event.key === 'ArrowRight' && nextHref) router.push(nextHref);
      else if (event.key === '+' || event.key === '=') zoom(1);
      else if (event.key === '-') zoom(-1);
    }

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [router, closeHref, previousHref, nextHref, zoom]);

  return (
    <div className="flex min-h-[520px] flex-col gap-sm rounded-card bg-black/90 p-sm">
      <div className="flex flex-wrap items-center gap-xs">
        <p className="min-w-0 flex-1 truncate text-subtitle font-semibold text-white">{title}</p>

        <div className="flex items-center gap-xxs">
          <ToolButton
            label={labels.rotate}
            onClick={() => setRotation((turn) => (turn + 1) % ROTATIONS.length)}
          >
            {'↻'}
          </ToolButton>
          <ToolButton label={labels.zoomOut} onClick={() => zoom(-1)} disabled={scale === 0}>
            {'−'}
          </ToolButton>
          <ToolButton
            label={labels.zoomIn}
            onClick={() => zoom(1)}
            disabled={scale === SCALES.length - 1}
          >
            {'+'}
          </ToolButton>
          <ToolButton
            label={labels.reset}
            onClick={() => {
              setScale(0);
              setRotation(0);
            }}
            disabled={scale === 0 && rotation === 0}
          >
            {'⤢'}
          </ToolButton>
        </div>

        <Link
          href={closeHref}
          className="inline-flex h-10 items-center rounded-sm px-md text-body-sm font-semibold text-primary hover:bg-white/10"
        >
          {labels.close}
        </Link>
      </div>

      <div className="flex flex-1 items-center justify-center overflow-auto rounded-md bg-black/40 p-sm">
        {/* Not next/image: the source answers a 302 to a per-view signed URL, and an
            optimiser that cached one rendition would serve a later caller a document
            with no `DOC_VIEW` row behind it. */}
        <img
          src={src}
          alt={labels.image}
          className={cx(
            'max-h-[70vh] max-w-full origin-center object-contain transition-transform',
            SCALES[scale],
            ROTATIONS[rotation],
          )}
        />
      </div>

      <div className="flex flex-wrap items-center gap-xs">
        {previousHref ? (
          <Link
            href={previousHref}
            className="inline-flex h-10 items-center rounded-sm border border-white/30 px-md text-body-sm text-white hover:bg-white/10"
          >
            <span aria-hidden="true">{'‹ '}</span>
            {labels.previous}
          </Link>
        ) : null}

        <span className="flex-1" />

        {provenance ? <p className="text-caption text-white/70">{provenance}</p> : null}

        <span className="flex-1" />

        {nextHref ? (
          <Link
            href={nextHref}
            className="inline-flex h-10 items-center rounded-sm border border-white/30 px-md text-body-sm text-white hover:bg-white/10"
          >
            {labels.next}
            <span aria-hidden="true">{' ›'}</span>
          </Link>
        ) : null}
      </div>
    </div>
  );
}

/**
 * One glyph control.
 *
 * The glyph is `aria-hidden` and the accessible name is the resource string
 * beside it: "↻" announces as "clockwise open circle arrow", which is not the
 * word an operator was given for it in any of the three languages.
 */
function ToolButton({
  label,
  onClick,
  disabled,
  children,
}: {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={label}
      title={label}
      className="flex size-10 items-center justify-center rounded-sm text-title text-white hover:bg-white/10 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary disabled:opacity-40"
    >
      <span aria-hidden="true">{children}</span>
    </button>
  );
}
