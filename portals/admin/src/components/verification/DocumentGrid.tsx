import Link from 'next/link';

import { StatusPill } from '@mageride/ui';

import type { DocumentTileView } from './model';

/**
 * SCR-AP-003a/003c's attached-document grid — the wireframe's `thumbgrid`.
 *
 * **Every tile is a look at somebody's document, and every look is a row.** The
 * `src` is the portal's relay of `GET /v1/admin/documents/{docId}?variant=thumb`,
 * which records `DOC_VIEW` and *then* mints the short-lived signed
 * object-storage URL it redirects to (AL-39, C063). So rendering this grid writes
 * one audit row per thumbnail — deliberately, because a grid of six licences is
 * six looks at six documents, and the alternative reading (audit only the
 * lightbox) would leave the thumbnails as an unlogged way to read a NIC number.
 *
 * They are **not** lazy-loaded for the same reason: with `loading="lazy"` the
 * audit trail would record what the officer happened to scroll past, which is a
 * different fact from what the screen showed them.
 *
 * Tapping a tile opens SCR-AP-003b.
 */

export interface DocumentGridLabels {
  readonly heading: string;
  readonly hint: string;
  readonly empty: string;
  readonly note: string;
}

export function DocumentGrid({
  tiles,
  labels,
}: {
  tiles: readonly DocumentTileView[];
  labels: DocumentGridLabels;
}) {
  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-sm shadow-card">
      <div className="flex flex-wrap items-center gap-xs">
        <h2 className="text-subtitle font-semibold text-on-surface">{labels.heading}</h2>
        <StatusPill tone="info" dot={false}>
          {labels.hint}
        </StatusPill>
      </div>

      {tiles.length === 0 ? (
        <p className="text-body-sm text-on-surface-variant">{labels.empty}</p>
      ) : (
        <>
          <ul className="grid grid-cols-2 gap-sm md:grid-cols-3 lg:grid-cols-6">
            {tiles.map((tile) => (
              <li key={tile.docId}>
                <Link
                  href={tile.href}
                  className="group flex flex-col gap-xxs rounded-md border border-outline bg-surface p-xxs focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
                >
                  <span className="relative block overflow-hidden rounded-sm bg-surface-variant">
                    {/* A plain <img>, not next/image: the source answers a 302 to a signed
                        object-storage URL minted per view, and the optimiser would proxy and
                        cache it — one cached rendition served to a later caller is a read of
                        somebody's licence with no `DOC_VIEW` row behind it. */}
                    <img
                      src={tile.src}
                      alt={tile.label}
                      className="block h-[104px] w-full object-cover transition-transform group-hover:scale-105"
                    />
                    <span className="absolute right-xxs bottom-xxs rounded-sm bg-black/60 px-xxs text-caption text-white">
                      {tile.position}
                    </span>
                  </span>
                  <span className="px-xxs pb-xxs text-caption text-on-surface">{tile.label}</span>
                </Link>
              </li>
            ))}
          </ul>

          <p className="text-caption text-on-surface-variant">{labels.note}</p>
        </>
      )}
    </section>
  );
}
