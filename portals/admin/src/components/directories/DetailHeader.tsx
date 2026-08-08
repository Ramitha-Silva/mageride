import Link from 'next/link';

import { StatusPill } from '@mageride/ui';

import type { PillView } from './model';

/**
 * The top of SCR-AP-011 / 013 / 015 — the wireframe's "‹ Back · PAX-90431 · Ramith
 * de Silva · [Active]" row.
 *
 * **Back is a `<Link>` to the results, not `history.back()`.** The href carries the
 * operator's own criteria (`links.ts`), so a record opened from a search for a
 * plate returns to that search — and one opened from a link somebody pasted into a
 * ticket still has somewhere to go, which a history call would not.
 *
 * The subject id is printed **in full and on its own line**: it is the value an
 * operator copies into a ticket, a suspension or a reversal, and a truncated
 * identifier is an ambiguous one on a screen whose whole purpose is telling two
 * people apart.
 */

export function DetailHeader({
  backHref,
  backLabel,
  title,
  subjectId,
  pill,
}: {
  readonly backHref: string;
  readonly backLabel: string;
  readonly title: string;
  readonly subjectId: string;
  /** Absent on a record that could not be read — there is no status to state. */
  readonly pill?: PillView;
}) {
  return (
    <div className="flex flex-col gap-xxs">
      <div className="flex flex-wrap items-center gap-xs">
        <Link
          href={backHref}
          className="inline-flex h-10 items-center rounded-sm border border-outline px-sm text-body-sm text-on-surface-variant hover:bg-surface-variant"
        >
          <span aria-hidden="true">{'‹ '}</span>
          {backLabel}
        </Link>

        <h2 className="min-w-0 text-title font-display break-all">{title}</h2>

        <span className="flex-1" />

        {pill ? <StatusPill tone={pill.tone}>{pill.label}</StatusPill> : null}
      </div>

      <p className="text-caption break-all text-on-surface-variant">{subjectId}</p>
    </div>
  );
}
