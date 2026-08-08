/**
 * SCR-AP-006's tab strip: the wireframe's five tabs, resolved against the four
 * nav items `AdminMenu.cs` actually gates them on.
 *
 * | Wireframe tab | Nav item | URD §2.3 row |
 * |---|---|---|
 * | Gateway settlement | `reconciliation` | Finance |
 * | Wallet ledger | `transactions` | Finance |
 * | Reversals & refunds | `refunds` **and** `wallet-adjustments` | Refunds · Driver wallet adjustments |
 * | Payouts | `reconciliation` (`?view=payouts`) | Finance |
 * | Credit transfers | `transactions` (`?kind=driver_transfer`) | Finance |
 *
 * **The one deviation is the third row, and it is forced.** The wireframe draws
 * "Reversals & refunds" as a single tab. They are two URD §2.3 rows with different
 * cells — Refunds gives a Support CSR `◐ raise/recommend` and Driver-wallet
 * adjustments gives them `➖`, while Finance holds `✅` on both — so one tab would
 * have to either show a CSR the reversal form the matrix withholds, or hide the
 * refund queue the matrix grants them. `AdminMenu.cs` already split them into two
 * items for exactly that reason; the strip follows the manifest. See the C108
 * handoff.
 *
 * **The last two tabs are views of a screen, not screens.** Payouts is
 * `/finance/reconciliation?view=payouts` and Credit transfers is
 * `/finance/transactions?kind=driver_transfer` — both are the same data the item's
 * own gate already covers, so neither needs a nav item, an entry in `routes.ts`,
 * or an exemption. A tab that pointed at a path no nav item claims would be
 * refused by `proxy.ts`, which is the correct outcome and a useless screen.
 */

import type { AdminMessageKey } from '@/i18n';

import type { AdminMenuGroup } from '@/api/types';
import { permittedItems } from '@/server/access';

export type FinanceTabId =
  | 'settlement'
  | 'ledger'
  | 'refunds'
  | 'reversals'
  | 'payouts'
  | 'transfers';

interface FinanceTabSpec {
  readonly id: FinanceTabId;
  /** The `AdminMenuItem.key` this tab is gated on. */
  readonly itemKey: string;
  readonly labelKey: AdminMessageKey;
  /** Appended to the path admin-bff sent, where the tab is a view rather than a screen. */
  readonly search?: string;
}

const SPECS: readonly FinanceTabSpec[] = [
  { id: 'settlement', itemKey: 'reconciliation', labelKey: 'admin.finance.tab.settlement' },
  { id: 'ledger', itemKey: 'transactions', labelKey: 'admin.finance.tab.ledger' },
  { id: 'refunds', itemKey: 'refunds', labelKey: 'admin.finance.tab.refunds' },
  { id: 'reversals', itemKey: 'wallet-adjustments', labelKey: 'admin.finance.tab.reversals' },
  {
    id: 'payouts',
    itemKey: 'reconciliation',
    labelKey: 'admin.finance.tab.payouts',
    search: 'view=payouts',
  },
  {
    id: 'transfers',
    itemKey: 'transactions',
    labelKey: 'admin.finance.tab.transfers',
    search: 'kind=driver_transfer',
  },
];

export interface FinanceTabView {
  readonly id: FinanceTabId;
  readonly href: string;
  readonly labelKey: AdminMessageKey;
  readonly current: boolean;
}

/**
 * The strip as this caller sees it.
 *
 * The href is built from **the path the item carried on `GET /v1/admin/session`**,
 * not from `src/server/routes.ts`: the server's own path is the one its own gate
 * agrees with, and the local table exists to resolve a URL to a screen rather than
 * to be a source of links.
 */
export function financeTabs(
  menu: readonly AdminMenuGroup[],
  current: FinanceTabId,
): FinanceTabView[] {
  const paths = new Map(permittedItems(menu).map(({ item }) => [item.key, item.path]));

  return SPECS.flatMap((spec) => {
    const path = paths.get(spec.itemKey);
    if (!path) return [];

    return [
      {
        id: spec.id,
        href: spec.search ? `${path}?${spec.search}` : path,
        labelKey: spec.labelKey,
        current: spec.id === current,
      },
    ];
  });
}
