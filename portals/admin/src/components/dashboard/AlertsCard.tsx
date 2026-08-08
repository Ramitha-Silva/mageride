import Link from 'next/link';

import { StatusPill, TBody, TD, TR, Table } from '@mageride/ui';

import type { AlertView } from './model';

/**
 * The alerts feed (`web_admin.html`, SCR-AP-002): what is waiting, how much of
 * it, and the screen it is worked on.
 *
 * **A row is a link only when the operator's menu carries that module.** The
 * count is on the dashboard payload every permitted role receives, so a Finance
 * Officer sees that thirty-eight submissions are pending; the verification queue
 * is not theirs, so for them the row is text. Drawing the link anyway would be
 * this component promising a screen `proxy.ts` then answers 403 on — the shell's
 * whole rule is that hiding the nav entry and refusing the route are one act
 * (AL-06), and a link drawn from anything other than the menu breaks it.
 *
 * `AlertView.href` comes from the item admin-bff sent, not from
 * `src/server/routes.ts`: the server's own path is the one its own gate agrees
 * with.
 */

export function AlertsCard({
  heading,
  caption,
  emptyLabel,
  alerts,
}: {
  heading: string;
  /** The table's accessible name. */
  caption: string;
  emptyLabel: string;
  alerts: readonly AlertView[];
}) {
  return (
    <section className="flex flex-col gap-sm">
      <h2 className="text-subtitle font-semibold">{heading}</h2>

      {alerts.length === 0 ? (
        <p className="rounded-card border border-outline bg-background p-md text-body-sm text-on-surface-variant shadow-card">
          {emptyLabel}
        </p>
      ) : (
        <Table caption={caption}>
          <TBody>
            {alerts.map((alert) => (
              <TR key={alert.key}>
                <TD>
                  {alert.href ? (
                    <Link href={alert.href} className="underline underline-offset-2">
                      {alert.label}
                    </Link>
                  ) : (
                    alert.label
                  )}
                </TD>
                <TD className="text-right">
                  <StatusPill tone="warning">
                    <span aria-hidden="true">{alert.display}</span>
                    <span className="sr-only">{alert.description}</span>
                  </StatusPill>
                </TD>
              </TR>
            ))}
          </TBody>
        </Table>
      )}
    </section>
  );
}
