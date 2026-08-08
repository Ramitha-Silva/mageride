'use client';

import { StatusPill, type StatusTone } from '@mageride/ui';

import { AccountMenu, type AccountMenuLabels, type OptionLabel } from './AccountMenu';
import { Brand } from './Brand';
import { MobileNav } from './MobileNav';
import type { NavGroupView } from './nav-model';
import { SideNav, useCurrentScreenLabel } from './SideNav';

/**
 * The page chrome every Fleet Portal screen sits inside: sidebar, topbar, and the
 * content column (`web_fleet.html`).
 *
 * **Responsive at D2 §FP's three widths, and only those three.** The preset
 * replaces Tailwind's breakpoints with 375 / 768 / 1024 (`sm:`/`md:`/`lg:`), so
 * `lg:` here means desktop: the static sidebar appears at 1024, and below it the
 * same nav is behind {@link MobileNav}. Nothing keys off a width D2 does not
 * define, because no such utility exists.
 *
 * **It is a client component and that is a routing decision, not a styling one.**
 * An App Router layout is not re-rendered when navigation moves between its
 * children, so a server-rendered "which entry is current" would be correct once
 * per session. The labels, the filtering and the account details are all resolved
 * on the server and passed in; what happens here is `usePathname()` and two
 * disclosure states.
 *
 * **The colours are D2 §0.2 tokens, and that is a deliberate departure from the
 * wireframe.** `web_fleet.html` draws the sidebar in `#0E2A1C` — a green that
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

/** The organisation's verification state, as the topbar chip renders it. */
export interface StatusChip {
  readonly label: string;
  readonly tone: StatusTone;
}

export function PortalChrome({
  groups,
  labels,
  accountName,
  organisation,
  role,
  status,
  appearances,
  locales,
  children,
}: {
  groups: readonly NavGroupView[];
  labels: ChromeLabels;
  accountName: string;
  organisation: string | null;
  role: string | null;
  /** Null for an account with no organisation — there is no state to report yet. */
  status: StatusChip | null;
  appearances: readonly OptionLabel[];
  locales: readonly OptionLabel[];
  children: React.ReactNode;
}) {
  const screenLabel = useCurrentScreenLabel(groups);

  return (
    <div className="flex min-h-dvh bg-surface">
      <a
        href="#fleet-content"
        className="sr-only focus:not-sr-only focus:absolute focus:z-50 focus:m-xs focus:rounded-sm focus:bg-primary focus:px-sm focus:py-xs focus:text-on-primary"
      >
        {labels.skipToContent}
      </a>

      {/*
        Desktop rail. D2 §FP puts sidebar + content at 1024.

        Δ C114 — `print:hidden` on the rail and on the topbar below it. SCR-FP-009's
        "Export CSV / **PDF**" has no export route on any contract, so the PDF is
        the browser's own print renderer over the same page (see `ExportControls`),
        and a report that came out with a navigation sidebar down the left of every
        page would not be one. Nothing else about the chrome changes.
      */}
      <aside className="hidden w-[240px] shrink-0 flex-col gap-md border-e border-outline bg-background p-sm lg:flex print:hidden">
        <div className="px-sm py-xs">
          <Brand label={labels.appName} />
        </div>
        <SideNav groups={groups} navLabel={labels.nav} />
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="sticky top-0 z-30 flex h-14 items-center gap-xs border-b border-outline bg-background px-sm print:hidden">
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

          {/*
            The verification chip, exactly where SCR-FP-002 and SCR-FP-002a draw
            it. In the topbar rather than on the setup screens alone, because
            US-13.A7's gate applies to the whole console and a state that is only
            visible on the screen it is about is a state somebody discovers by
            being refused somewhere else.
          */}
          {status ? (
            <StatusPill tone={status.tone} className="hidden md:inline-flex">
              {status.label}
            </StatusPill>
          ) : null}

          <AccountMenu
            name={accountName}
            organisation={organisation}
            role={role}
            labels={labels.account}
            appearances={appearances}
            locales={locales}
          />
        </header>

        <main id="fleet-content" className="flex min-w-0 flex-1 flex-col gap-sm p-sm md:p-md">
          {children}
        </main>
      </div>
    </div>
  );
}
