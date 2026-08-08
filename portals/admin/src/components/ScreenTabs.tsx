import Link from 'next/link';

import { StatusPill } from '@mageride/ui';

/**
 * The tab strip SCR-AP-006 and SCR-AP-007 are drawn with.
 *
 * **Links, not `@mageride/ui`'s `Tabs`** — the same decision C106 made for the
 * verification queues, for the same reason: that primitive holds the active tab in
 * component state, and these tabs are separate *screens* behind separate AL-06
 * gates. There is no panel to swap in place, so `aria-current="page"` is the
 * honest annotation and `role="tab"` would promise something that does not happen.
 *
 * **Every tab comes from the caller's own menu.** D2 draws Finance as one screen
 * with five tabs; `AdminMenu.cs` splits it across four nav items because they are
 * four different URD §2.3 rows, and a Support CSR holds `◐ raise/recommend` on
 * Refunds and nothing at all on Finance. A strip that drew all five would offer
 * that CSR four screens `proxy.ts` answers 403 on — the shell's rule is that
 * hiding the nav entry and refusing the route are one act, and a tab built from
 * anything but the menu breaks it (`AlertsCard`'s rule, restated).
 */

export interface ScreenTab {
  readonly id: string;
  /** The href the caller's own menu gave this item, plus any view the tab selects. */
  readonly href: string;
  readonly label: string;
  readonly current: boolean;
  /** A count or a variance beside the label, already rendered. */
  readonly badge?: string;
  /** Whether that badge is the bad kind — a non-zero variance, an open exception queue. */
  readonly badgeAlarming?: boolean;
}

export function ScreenTabs({ navLabel, tabs }: { navLabel: string; tabs: readonly ScreenTab[] }) {
  if (tabs.length === 0) return null;

  return (
    <nav aria-label={navLabel} className="flex gap-xs overflow-x-auto border-b border-outline">
      {tabs.map((tab) => (
        <Link
          key={tab.id}
          href={tab.href}
          aria-current={tab.current ? 'page' : undefined}
          className={`flex shrink-0 items-center gap-xs border-b-2 px-sm py-xs text-subtitle whitespace-nowrap transition-colors ${
            tab.current
              ? 'border-primary text-primary'
              : 'border-transparent text-on-surface-variant hover:text-on-surface'
          }`}
        >
          {tab.label}
          {tab.badge ? (
            <StatusPill tone={tab.badgeAlarming ? 'error' : 'neutral'} dot={false}>
              {tab.badge}
            </StatusPill>
          ) : null}
        </Link>
      ))}
    </nav>
  );
}
