'use client';

import { AccountMenu, type AccountMenuLabels, type OptionLabel } from './AccountMenu';
import { Brand } from './Brand';
import { MobileNav } from './MobileNav';
import type { NavGroupView } from './nav-model';
import { SideNav, useCurrentScreenLabel } from './SideNav';

/**
 * The page chrome every Admin Portal screen sits inside: sidebar, topbar, and the
 * content column (`web_admin.html`).
 *
 * **Responsive at D2 §AP's three widths, and only those three.** The preset
 * replaces Tailwind's breakpoints with 375 / 768 / 1024 (`sm:`/`md:`/`lg:`), so
 * `lg:` here means desktop: the static sidebar appears at 1024, and below it the
 * same nav is behind {@link MobileNav}. Nothing keys off a width D2 does not
 * define, because no such utility exists.
 *
 * **It is a client component and that is a routing decision, not a styling one.**
 * An App Router layout is not re-rendered when navigation moves between its
 * children, so a server-rendered "which entry is current" would be correct once
 * per session. The labels and the account details are still resolved on the
 * server and passed in; what happens here is `usePathname()` and two disclosure
 * states.
 *
 * **The colours are D2 §0.2 tokens, and that is a deliberate departure from the
 * wireframe.** `web_admin.html` draws the sidebar in `#15171B` — a slate that
 * appears nowhere in D2 §0.2 and has no dark-appearance counterpart, so a portal
 * built on it would be the one surface whose chrome ignores the design system and
 * whose sidebar reads identically in both themes. The structure is the
 * wireframe's; the palette is the preset's.
 */

export interface ChromeLabels {
  readonly appName: string;
  readonly nav: string;
  readonly openNav: string;
  readonly closeNav: string;
  readonly skipToContent: string;
  readonly account: AccountMenuLabels;
}

export function PortalChrome({
  groups,
  labels,
  accountName,
  roles,
  appearances,
  locales,
  children,
}: {
  groups: readonly NavGroupView[];
  labels: ChromeLabels;
  accountName: string;
  roles: readonly string[];
  appearances: readonly OptionLabel[];
  locales: readonly OptionLabel[];
  children: React.ReactNode;
}) {
  const screenLabel = useCurrentScreenLabel(groups);

  return (
    <div className="flex min-h-dvh bg-surface">
      <a
        href="#admin-content"
        className="sr-only focus:not-sr-only focus:absolute focus:z-50 focus:m-xs focus:rounded-sm focus:bg-primary focus:px-sm focus:py-xs focus:text-on-primary"
      >
        {labels.skipToContent}
      </a>

      {/* Desktop rail. D2 §AP puts "sidebar + content + detail" at 1024. */}
      <aside className="hidden w-[240px] shrink-0 flex-col gap-md border-e border-outline bg-background p-sm lg:flex">
        <div className="px-sm py-xs">
          <Brand label={labels.appName} />
        </div>
        <SideNav groups={groups} navLabel={labels.nav} />
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="sticky top-0 z-30 flex h-14 items-center gap-xs border-b border-outline bg-background px-sm">
          <MobileNav openLabel={labels.openNav} closeLabel={labels.closeNav}>
            <Brand label={labels.appName} />
            <SideNav groups={groups} navLabel={labels.nav} />
          </MobileNav>

          {/*
            One <h1> per page, and it is here rather than in each screen: the
            heading is the nav entry's own label, and a screen that had to repeat
            it would be a screen that can disagree with the sidebar.
          */}
          <h1 className="min-w-0 flex-1 truncate text-subtitle font-semibold">
            {screenLabel ?? labels.appName}
          </h1>

          <AccountMenu
            name={accountName}
            roles={roles}
            labels={labels.account}
            appearances={appearances}
            locales={locales}
          />
        </header>

        <main id="admin-content" className="min-w-0 flex-1 p-sm md:p-md">
          {children}
        </main>
      </div>
    </div>
  );
}
