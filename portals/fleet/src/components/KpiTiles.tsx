/**
 * `web_fleet.html`'s `.kpis` row — the four-across counter tiles SCR-FP-003 opens
 * with and SCR-FP-009 repeats above its table. Δ C114.
 *
 * One component for both because it is one block in the sketch, drawn identically
 * on both screens: a label, a large number, and a smaller line under it saying
 * what the number is out of. The two screens differ in what they count, which is
 * the caller's business.
 *
 * The tone colours the **number only**, as the sketch does — the tile does not
 * become a coloured card. A dashboard whose tiles were three coloured panels reads
 * as three alarms; the sketch's green 96 over a white tile reads as a count that
 * happens to be healthy, which is what it is.
 *
 * A server component. Nothing here is interactive.
 */

export type KpiTone = 'default' | 'success' | 'warning' | 'error';

/** Whole class names, never `text-${tone}` — Tailwind compiles what it can see. */
const TONES: Readonly<Record<KpiTone, string>> = {
  default: 'text-on-surface',
  success: 'text-success',
  warning: 'text-warning',
  error: 'text-error',
};

export interface KpiTile {
  readonly key: string;
  readonly label: string;
  readonly value: string;
  /** The sketch's smaller line — "of 120 vehicles", "Mode A 612 · B 230". */
  readonly detail?: string | null;
  readonly tone?: KpiTone;
}

export function KpiTiles({ tiles }: { tiles: readonly KpiTile[] }) {
  return (
    <div className="grid grid-cols-1 gap-sm sm:grid-cols-2 lg:grid-cols-4">
      {tiles.map((tile) => (
        <div
          key={tile.key}
          className="flex flex-col gap-xxs rounded-md border border-outline bg-surface p-sm"
        >
          <p className="text-label text-on-surface-variant">{tile.label}</p>
          <p className={`text-headline font-display ${TONES[tile.tone ?? 'default']}`}>
            {tile.value}
          </p>
          {tile.detail ? (
            <p className="text-caption text-on-surface-variant">{tile.detail}</p>
          ) : null}
        </div>
      ))}
    </div>
  );
}
