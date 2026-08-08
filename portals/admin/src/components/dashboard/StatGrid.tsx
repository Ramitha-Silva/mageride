import type { DeltaDirection, StatView } from './model';

/**
 * A row of KPI tiles (`web_admin.html`, SCR-AP-002).
 *
 * **The delta is drawn twice and read once.** The glyph-and-percentage a sighted
 * operator scans (`▲ 9.2%`) is `aria-hidden`, and the sentence beside it —
 * "Completed trips: up 9.2% on the previous period" — is `sr-only`. A screen
 * reader announcing "up-pointing triangle nine point two percent" under a heading
 * it has already left behind is not the same information.
 *
 * **Up is green and down is red, and that is only safe because of what is on
 * these cards.** All five period figures are ones where more is better — trips,
 * fare, new riders, new drivers, fee revenue. A tile whose metric was a failure
 * rate would need the opposite mapping, and this component must not grow one
 * without being told which direction is good.
 *
 * No hooks, so it renders on the server. The whole screen is a server render;
 * nothing on it is interactive except the filter.
 */

const TONES: Readonly<Record<DeltaDirection, string>> = {
  up: 'text-success',
  down: 'text-error',
  flat: 'text-on-surface-variant',
  unknown: 'text-outline-variant',
};

export function StatGrid({
  heading,
  note,
  stats,
}: {
  heading: string;
  /** The line under the heading, where one is needed — the live block's, chiefly. */
  note?: string;
  stats: readonly StatView[];
}) {
  return (
    <section className="flex flex-col gap-sm">
      <div className="flex flex-col gap-xxs">
        <h2 className="text-subtitle font-semibold">{heading}</h2>
        {note ? <p className="text-caption text-on-surface-variant">{note}</p> : null}
      </div>

      {/* D2 §AP's three widths and no others: one tile, two, then four. */}
      <div className="grid grid-cols-1 gap-sm sm:grid-cols-2 lg:grid-cols-4">
        {stats.map((stat) => (
          <StatCard key={stat.key} stat={stat} />
        ))}
      </div>
    </section>
  );
}

function StatCard({ stat }: { stat: StatView }) {
  return (
    <div className="flex flex-col gap-xxs rounded-card border border-outline bg-background p-md shadow-card">
      <p className="text-label text-on-surface-variant">{stat.label}</p>
      <p className="text-headline font-display text-on-surface">{stat.value}</p>

      {stat.deltas.length > 0 ? (
        <ul className="flex flex-wrap items-center gap-x-sm gap-y-xxs">
          {stat.deltas.map((delta) => (
            <li key={delta.key} className={`text-caption ${TONES[delta.direction]}`}>
              <span aria-hidden="true">
                {delta.display}
                {delta.qualifier ? ` ${delta.qualifier}` : ''}
              </span>
              <span className="sr-only">{delta.description}</span>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
