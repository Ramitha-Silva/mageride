import type { AdminMenuGroup } from '@/api/types';
import { translateServerKey, type AdminTranslator } from '@/i18n';

/**
 * The nav, translated on the server and handed to the client as data.
 *
 * The split exists because of one App Router fact: **a layout is not re-rendered
 * when navigation moves between its children.** So the component that knows
 * "which entry is the current page" has to be a client one reading
 * `usePathname()`, or the highlight would be right on the first page load of a
 * session and wrong for the rest of it.
 *
 * What crosses the boundary is therefore this — twenty-five short labels the
 * server has already resolved — rather than the translator or the three locale
 * tables. The filtering is still entirely the server's: this is a projection of
 * `GET /v1/admin/session`'s menu and carries no roles, no capabilities and no way
 * to name an entry the caller was not sent.
 */

export interface NavItemView {
  readonly key: string;
  readonly label: string;
  readonly path: string;
  /** Which service answers this screen's API — six entries are not admin-bff's. */
  readonly ownedBy: string;
}

export interface NavGroupView {
  readonly key: string;
  readonly label: string;
  readonly items: readonly NavItemView[];
}

export function buildNavModel(
  menu: readonly AdminMenuGroup[],
  t: AdminTranslator,
): NavGroupView[] {
  return menu.map((group) => ({
    key: group.key,
    label: translateServerKey(t, group.labelKey),
    items: group.items.map((item) => ({
      key: item.key,
      label: translateServerKey(t, item.labelKey),
      path: item.path,
      ownedBy: item.ownedBy,
    })),
  }));
}
