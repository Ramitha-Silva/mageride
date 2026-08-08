import { ScreenTabs, type ScreenTab } from '@/components/ScreenTabs';

import { ResultsTable, type ResultsTableLabels } from './ResultsTable';
import type { TableRowView } from './model';

/**
 * The right-hand pane on SCR-AP-011 / 013 / 015 — the wireframe's tab strip and
 * the table under it.
 *
 * **The tabs are links and the strip is `ScreenTabs`**, the same component C108's
 * finance screens use. That primitive draws `aria-current="page"` rather than
 * `role="tab"`, which is the honest annotation here: each tab is its own URL and
 * pressing one is a navigation, not a panel swap. `@mageride/ui`'s `Tabs` holds the
 * active tab in component state, and an investigator who opened a wallet ledger,
 * followed a vehicle chip and pressed Back would come back to Trips.
 *
 * The strip is **not** built from the caller's menu, and that is the difference
 * from `financeTabs`: these five are one screen behind one nav item and one URD
 * §2.3 row. A caller who may open the driver record may read every tab on it —
 * admin-bff answers all five arrays on the one read that let them in.
 *
 * `note` is where a tab says what the platform does **not** hold: the Trips tab
 * carries no route because `DirectoryTrip` has no origin and no destination. Saying
 * so beats a column of em dashes under a heading that promises one.
 */

export function ActivityPanel({
  navLabel,
  tabs,
  rows,
  labels,
  note,
}: {
  readonly navLabel: string;
  readonly tabs: readonly ScreenTab[];
  readonly rows: readonly TableRowView[];
  readonly labels: ResultsTableLabels;
  readonly note?: string;
}) {
  return (
    <section className="flex min-w-0 flex-1 flex-col gap-sm rounded-card border border-outline bg-background p-sm shadow-card">
      <ScreenTabs navLabel={navLabel} tabs={tabs} />

      <ResultsTable rows={rows} labels={labels} />

      {note ? <p className="text-caption text-on-surface-variant">{note}</p> : null}
    </section>
  );
}
