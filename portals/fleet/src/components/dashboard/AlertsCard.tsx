import Link from 'next/link';

import { StatusPill } from '@mageride/ui';

/**
 * **SCR-FP-003's "Alerts" card** — the wireframe's list of counts, each linking to
 * the screen an operator acts on it from.
 *
 * Nothing here raises anything: every count is rows a service already decided
 * about (see `./dashboard-model` for which service decided which). Route-deviation
 * and geofence alerting is Phase 3 and has no producer, so its empty state is a
 * sentence at the bottom of the card rather than a row of zero — a row would imply
 * a watcher that is not running.
 *
 * A server component.
 */

export interface AlertsCardRow {
  readonly key: string;
  readonly label: string;
  readonly count: string;
  readonly tone: 'neutral' | 'info' | 'success' | 'warning' | 'error';
  readonly href: string | null;
}

export interface AlertsCardLabels {
  readonly heading: string;
  /** US-13.5's empty state, in words. */
  readonly phaseThree: string;
  /** Why there is no insurance-expiry row. */
  readonly noExpiryRow: string;
}

export function AlertsCard({
  rows,
  banner,
  labels,
}: {
  rows: readonly AlertsCardRow[];
  /** US-3.16's device-down breach for the current window, or `null`. */
  banner: string | null;
  labels: AlertsCardLabels;
}) {
  return (
    <section
      aria-label={labels.heading}
      className="flex flex-1 flex-col gap-sm rounded-md border border-outline bg-surface p-sm"
    >
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      {banner ? (
        <p role="status" className="rounded-sm bg-error/10 px-xs py-xxs text-body-sm text-error">
          {banner}
        </p>
      ) : null}

      <ul className="flex flex-col">
        {rows.map((row) => (
          <li
            key={row.key}
            className="flex items-center gap-sm border-b border-surface-variant py-xs last:border-b-0"
          >
            {row.href ? (
              <Link
                href={row.href}
                className="flex-1 rounded-sm text-body-sm text-secondary underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
              >
                {row.label}
              </Link>
            ) : (
              <span className="flex-1 text-body-sm text-on-surface">{row.label}</span>
            )}
            <StatusPill tone={row.tone} dot={false}>
              {row.count}
            </StatusPill>
          </li>
        ))}
      </ul>

      <p className="text-caption text-on-surface-variant">{labels.phaseThree}</p>
      <p className="text-caption text-on-surface-variant">{labels.noExpiryRow}</p>
    </section>
  );
}
