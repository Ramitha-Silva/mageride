import type { FleetSession } from '@/api/types';
import type { FleetTranslator } from '@/i18n';
import { permittedNav } from '@/server/access';

/**
 * The nav, filtered and translated on the server and handed to the client as
 * data.
 *
 * The split exists because of one App Router fact: **a layout is not re-rendered
 * when navigation moves between its children.** So the component that knows
 * "which entry is the current page" has to be a client one reading
 * `usePathname()`, or the highlight would be right on the first page load of a
 * session and wrong for the rest of it.
 *
 * What crosses the boundary is therefore this — at most fifteen short labels and
 * their paths — and **not** the manifest. `src/server/routes.ts` carries each
 * screen's URD §2.3 row, its minimum sub-role and its approval gate; none of that
 * is any use to a browser, and shipping it would put the gate's own vocabulary in
 * a bundle while the filtering it describes has already happened. `SideNav`
 * decides which entry is current from the paths it was given, so it needs no
 * route table at all.
 */

export interface NavItemView {
  readonly key: string;
  readonly label: string;
  readonly path: string;
}

export interface NavGroupView {
  readonly key: string;
  readonly label: string;
  readonly items: readonly NavItemView[];
}

export function buildNavModel(session: FleetSession, t: FleetTranslator): NavGroupView[] {
  return permittedNav(session).map((group) => ({
    key: group.key,
    label: t(group.labelKey),
    items: group.items.map((screen) => ({
      key: screen.key,
      label: t(screen.labelKey),
      path: screen.path,
    })),
  }));
}
