'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';

import { resolveRoute } from '@/server/routes';

import { NavIcon } from './icons';
import type { NavGroupView } from './nav-model';

/**
 * The role-scoped sidebar (`web_admin.html`; D2 §AP's "role-scoped menus,
 * deny-by-default RBAC").
 *
 * **It draws what the server sent and nothing else.** `groups` is a projection of
 * `GET /v1/admin/session`'s already-filtered manifest, so there is no
 * `if (role === …)` in this component and there must never be one: the moment the
 * portal decides for itself which entries a Support/CSR may see, it is a second
 * copy of URD §2.3 — and the one nobody's test parses the spec to check.
 *
 * A group with no permitted items never arrives; admin-bff drops it. That is
 * deliberate on its side too: an empty "Finance" heading tells a Verification
 * Officer that a Finance section exists and they cannot reach it, which is
 * information the nav has no reason to leak.
 */
export function SideNav({ groups, navLabel }: { groups: readonly NavGroupView[]; navLabel: string }) {
  const pathname = usePathname();
  const current = resolveRoute(pathname ?? '/')?.key;

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

/** The label of the screen a URL is on, for the topbar heading. */
export function useCurrentScreenLabel(groups: readonly NavGroupView[]): string | null {
  const pathname = usePathname();
  const key = resolveRoute(pathname ?? '/')?.key;
  if (!key) return null;

  for (const group of groups) {
    const item = group.items.find((candidate) => candidate.key === key);
    if (item) return item.label;
  }
  return null;
}
