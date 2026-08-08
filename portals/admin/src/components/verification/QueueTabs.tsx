import Link from 'next/link';

import { StatusPill } from '@mageride/ui';

import { queueHref, VERIFICATION_QUEUES, type QueueSelection, type VerificationQueue } from '@/api/verification';

/**
 * SCR-AP-003's three tabs: driving-licence pending · vehicle-registration pending
 * · fleet-org approval, each with the number of rows behind it.
 *
 * **They are links, not `@mageride/ui`'s `Tabs`.** That primitive is Radix, and
 * Radix holds the active tab in component state — which is exactly the thing this
 * screen must not do. An officer working a queue opens a detail, decides, and
 * comes back; with the tab in state they come back to the first one. With the tab
 * in the URL they come back where they were, the browser's back button works
 * through their own path, and a colleague can be sent the tab and the search that
 * produced a row.
 *
 * `aria-current="page"` rather than `role="tab"`: these navigate, and announcing a
 * link as a tab promises a panel that swaps in place.
 */

export interface QueueTabsLabels {
  readonly navLabel: string;
  readonly drivingLicence: string;
  readonly vehicleRegistration: string;
  readonly fleetOrg: string;
}

const TAB_LABEL: Readonly<Record<VerificationQueue, keyof QueueTabsLabels>> = {
  'driving-license': 'drivingLicence',
  'vehicle-registration': 'vehicleRegistration',
  'fleet-org': 'fleetOrg',
};

export function QueueTabs({
  selection,
  counts,
  labels,
}: {
  selection: QueueSelection;
  /** Already rendered — `—` for a queue that could not be read, `100+` past a page. */
  counts: Readonly<Record<VerificationQueue, string>>;
  labels: QueueTabsLabels;
}) {
  return (
    <nav aria-label={labels.navLabel} className="flex gap-xs overflow-x-auto border-b border-outline">
      {VERIFICATION_QUEUES.map((queue) => {
        const current = queue === selection.queue;

        return (
          <Link
            key={queue}
            href={queueHref('/verification', selection, { queue })}
            aria-current={current ? 'page' : undefined}
            className={`flex shrink-0 items-center gap-xs border-b-2 px-sm py-xs text-subtitle whitespace-nowrap transition-colors ${
              current
                ? 'border-primary text-primary'
                : 'border-transparent text-on-surface-variant hover:text-on-surface'
            }`}
          >
            {labels[TAB_LABEL[queue]]}
            <StatusPill tone={current ? 'warning' : 'neutral'} dot={false}>
              {counts[queue]}
            </StatusPill>
          </Link>
        );
      })}
    </nav>
  );
}
