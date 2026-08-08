import type { ReactNode } from 'react';

import { StatusPill } from '@mageride/ui';

import type { FactView } from './model';

/**
 * The left-hand card on SCR-AP-011 / 013 / 015 — the wireframe's `Profile`,
 * `Driver` and `Vehicle` panels.
 *
 * A **description list**, not a table. The wireframe draws a two-column grid and
 * `<table>` would be the lazy reading of it, but these are labelled facts about one
 * subject rather than rows of a dataset: a screen reader announcing "row 3, column
 * 2" over "Wallet · Rs 3,250.00" describes a spreadsheet that is not there. `<dl>`
 * announces the pairing, which is the structure.
 *
 * A fact carries a value, a pill, or both. The two certificate expiries are pills
 * and nothing else — the pill already says the date and the tone is the point of
 * showing it (`expiryPill`).
 *
 * `children` is what the card carries **below** the facts: a driver's linked
 * vehicles, a vehicle's document grid, the hand-off links. Each belongs to one
 * screen and none of them belongs here.
 */

export function ProfileCard({
  heading,
  facts,
  note,
  children,
}: {
  readonly heading: string;
  readonly facts: readonly FactView[];
  /** The PII notice, on every one of the three details. */
  readonly note?: string;
  readonly children?: ReactNode;
}) {
  return (
    <section className="flex w-full flex-col gap-sm rounded-card border border-outline bg-background p-sm shadow-card lg:w-[320px] lg:shrink-0">
      <h2 className="text-subtitle font-semibold text-on-surface">{heading}</h2>

      <dl className="flex flex-col gap-xxs">
        {facts.map((fact) => (
          <div key={fact.key} className="flex flex-wrap items-baseline gap-xs border-b border-outline/40 pb-xxs last:border-b-0">
            <dt className="min-w-[104px] text-label text-on-surface-variant">{fact.label}</dt>
            <dd className="flex min-w-0 flex-1 flex-wrap items-center gap-xs text-body-sm break-all text-on-surface">
              {fact.value ? <span>{fact.value}</span> : null}
              {fact.pill ? <StatusPill tone={fact.pill.tone}>{fact.pill.label}</StatusPill> : null}
            </dd>
          </div>
        ))}
      </dl>

      {note ? <p className="text-caption text-on-surface-variant">{note}</p> : null}

      {children}
    </section>
  );
}
