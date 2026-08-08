'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';

import { NavIcon } from './icons';
import type { NavGroupView } from './nav-model';

/**
 * The org-scoped sidebar (`web_fleet.html`).
 *
 * **It draws what the server sent and nothing else.** `groups` is already
 * filtered by `permittedNav()`, so there is no `if (fleetRole === …)` in this
 * component and there must never be one — the moment the browser decides which
 * entries a Viewer may see, the decision has moved to the one place nothing on
 * the server can check.
 *
 * A group with no permitted items never arrives; `permittedNav` drops it with
 * them. That matters for the verification gate in particular: an empty "Operate"
 * heading above nothing would tell a pending organisation that its vehicles are
 * behind a door, which is exactly what the pending screen explains properly.
 */
export function SideNav({ groups, navLabel }: { groups: readonly NavGroupView[]; navLabel: string }) {
  const pathname = usePathname() ?? '/';
  const current = currentKey(groups, pathname);

  return (
    <nav aria-label={navLabel} className="flex flex-col gap-md">
      {groups.map((group) => (
        <div key={group.key} className="flex flex-col gap-xxs">
          <h2 className="px-sm pt-xs text-caption font-semibold uppercase tracking-wide text-outline-variant">
            {group.label}
          </h2>

          <ul className="flex flex-col gap-px">
            {group.items.map((item) => {
              const active = item.key === current;

              return (
                <li key={item.key}>
                  <Link
                    href={item.path}
                    // The one thing a screen reader needs that sight does not:
                    // which entry is the page currently open.
                    aria-current={active ? 'page' : undefined}
                    className={[
                      'flex items-center gap-xs rounded-sm px-sm py-xs text-body-sm transition-colors',
                      active
                        ? 'bg-primary-container font-semibold text-on-primary-container'
                        : 'text-on-surface-variant hover:bg-surface-variant hover:text-on-surface',
                    ].join(' ')}
                  >
                    <NavIcon navKey={item.key} />
                    <span className="truncate">{item.label}</span>
                  </Link>
                </li>
              );
            })}
          </ul>
        </div>
      ))}
    </nav>
  );
}

/**
 * Which entry a URL belongs to — the **longest** matching path, so a nested
 * screen wins over its parent.
 *
 * Resolved against the entries this component was handed rather than against
 * `src/server/routes.ts`, which is what keeps the manifest — and the gate
 * vocabulary on it — out of the client bundle entirely. It is the same
 * longest-prefix rule `resolveScreenRoute` applies on the server, over a subset
 * of the same paths, so the two cannot disagree about a route the caller can
 * actually see.
 */
function currentKey(groups: readonly NavGroupView[], pathname: string): string | null {
  const path = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;

  let best: NavItemMatch | null = null;
  for (const group of groups) {
    for (const item of group.items) {
      if (path !== item.path && !path.startsWith(`${item.path}/`)) continue;
      if (!best || item.path.length > best.length) best = { key: item.key, length: item.path.length };
    }
  }
  return best?.key ?? null;
}

interface NavItemMatch {
  readonly key: string;
  readonly length: number;
}

/** The label of the screen a URL is on, for the topbar heading. */
export function useCurrentScreenLabel(groups: readonly NavGroupView[]): string | null {
  const pathname = usePathname() ?? '/';
  const key = currentKey(groups, pathname);
  if (!key) return null;

  for (const group of groups) {
    const item = group.items.find((candidate) => candidate.key === key);
    if (item) return item.label;
  }
  return null;
}
