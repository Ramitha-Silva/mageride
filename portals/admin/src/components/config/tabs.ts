/**
 * SCR-AP-007's tab strip, resolved against the nav items `AdminMenu.cs` gates each
 * configuration surface on.
 *
 * | Wireframe tab | Nav item | Answered by |
 * |---|---|---|
 * | Fare tariffs | `fare-tariffs` | admin-bff |
 * | Daily fee | `daily-fee-rates` | subscription-svc |
 * | Commission & vouchers | `voucher-tiers` | subscription-svc |
 * | Vehicle types | — **not drawn** | — |
 * | Driver Level | `driver-levels` | dispatch-svc |
 * | Feature flags | `feature-flags` | admin-bff |
 * | *(Transit data — the C108 deliverable's "GTFS entry point")* | `gtfs` | transit-svc |
 *
 * **"Vehicle types" is the one wireframe tab with no screen behind it.** AL-09's
 * list is a CHECK on `registry.vehicles.vehicle_type`, a second `HashSet` in
 * subscription-svc that refuses a rate for anything outside it, and an `enum` in
 * `_shared.yaml`; no route on the platform adds one. A tab offering to configure
 * them would be offering a migration. What the wireframe's own note means by "all
 * admin-configurable" is the *rates* per type, and those are the first two tabs.
 * See the C108 handoff.
 *
 * **Transit data is a tab and not a screen here.** The GTFS Dataset Manager is
 * SCR-AP-016 and belongs to C110; the deliverable this component owns is the
 * *entry point*, which is the tab. It links to the path the caller's own menu gave
 * the `gtfs` item, so it appears for the two roles that hold Platform settings ·
 * Configure and for nobody else.
 *
 * `cities`, `trains` and `announcements` are Configuration-group nav items too and
 * are deliberately absent from the strip: the wireframe does not draw them as tabs,
 * they belong to no C108 deliverable, and they still reach their own screens from
 * the sidebar. The handoff records that no component claims them.
 */

import type { AdminMessageKey } from '@/i18n';

import type { AdminMenuGroup } from '@/api/types';
import { permittedItems } from '@/server/access';

export type ConfigTabId = 'tariffs' | 'fees' | 'vouchers' | 'levels' | 'flags' | 'gtfs';

interface ConfigTabSpec {
  readonly id: ConfigTabId;
  readonly itemKey: string;
  readonly labelKey: AdminMessageKey;
}

const SPECS: readonly ConfigTabSpec[] = [
  { id: 'tariffs', itemKey: 'fare-tariffs', labelKey: 'admin.config.tab.tariffs' },
  { id: 'fees', itemKey: 'daily-fee-rates', labelKey: 'admin.config.tab.fees' },
  { id: 'vouchers', itemKey: 'voucher-tiers', labelKey: 'admin.config.tab.vouchers' },
  { id: 'levels', itemKey: 'driver-levels', labelKey: 'admin.config.tab.levels' },
  { id: 'flags', itemKey: 'feature-flags', labelKey: 'admin.config.tab.flags' },
  { id: 'gtfs', itemKey: 'gtfs', labelKey: 'admin.config.tab.gtfs' },
];

export interface ConfigTabView {
  readonly id: ConfigTabId;
  readonly href: string;
  readonly labelKey: AdminMessageKey;
  readonly current: boolean;
}

/** The strip as this caller sees it, hrefs taken from the menu the server sent. */
export function configTabs(
  menu: readonly AdminMenuGroup[],
  current: ConfigTabId,
): ConfigTabView[] {
  const paths = new Map(permittedItems(menu).map(({ item }) => [item.key, item.path]));

  return SPECS.flatMap((spec) => {
    const path = paths.get(spec.itemKey);
    if (!path) return [];

    return [{ id: spec.id, href: path, labelKey: spec.labelKey, current: spec.id === current }];
  });
}
