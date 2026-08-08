import Link from 'next/link';

import { StatusPill } from '@mageride/ui';

import type { DetailFact } from './map-model';

/**
 * **SCR-FP-007's per-vehicle drill-in** — one vehicle, everything the four reads
 * know about it, and a sentence for each thing they do not.
 *
 * Rendered on the **server** from the same answers the table and the map are built
 * from, so it cannot show a speed the row beside it disagrees with. It appears for
 * `?vehicle={id}` — pushed by a marker click, followed from a table row, or pasted
 * from somebody's message — and the close control is a link back to the bare path
 * rather than a button, because dismissing a panel that is a URL is navigation.
 */

export interface VehicleDetailLabels {
  readonly heading: string;
  readonly close: string;
  /** Shown when `?vehicle=` names an id no read answered for. */
  readonly unknown: string;
}

export interface VehicleDetailPanelProps {
  readonly plate: string;
  readonly type: string | null;
  readonly accentClass: string;
  readonly health: string;
  readonly healthTone: 'neutral' | 'info' | 'success' | 'warning' | 'error';
  readonly facts: readonly DetailFact[];
  readonly labels: VehicleDetailLabels;
}

export function VehicleDetailPanel({
  plate,
  type,
  accentClass,
  health,
  healthTone,
  facts,
  labels,
}: VehicleDetailPanelProps) {
  return (
    <section
      aria-label={labels.heading}
      className="flex flex-col gap-sm rounded-md border border-outline bg-surface p-sm"
    >
      <div className="flex flex-wrap items-center gap-xs">
        <span aria-hidden="true" className={`size-xs rounded-full ${accentClass}`} />
        <h2 className="text-subtitle font-semibold">{plate}</h2>
        {type ? <span className="text-caption text-on-surface-variant">{type}</span> : null}
        <span className="flex-1" />
        <StatusPill tone={healthTone}>{health}</StatusPill>
        <Link
          href="/map"
          scroll={false}
          className="rounded-sm px-xs py-xxs text-body-sm text-secondary underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
        >
          {labels.close}
        </Link>
      </div>

      <dl className="grid grid-cols-1 gap-sm sm:grid-cols-2 lg:grid-cols-3">
        {facts.map((fact) => (
          <div key={fact.key} className="flex flex-col gap-xxs">
            <dt className="text-label text-on-surface-variant">{fact.label}</dt>
            <dd className="text-body text-on-surface">{fact.value}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

/** `?vehicle=` named an id nothing answered for — a stale link, or another org's. */
export function UnknownVehiclePanel({ labels }: { labels: VehicleDetailLabels }) {
  return (
    <section
      aria-label={labels.heading}
      className="flex flex-wrap items-center gap-sm rounded-md border border-outline bg-surface-variant p-sm"
    >
      <p className="flex-1 text-body-sm text-on-surface-variant">{labels.unknown}</p>
      <Link
        href="/map"
        scroll={false}
        className="rounded-sm px-xs py-xxs text-body-sm text-secondary underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
      >
        {labels.close}
      </Link>
    </section>
  );
}
