'use client';

import { Button } from '@mageride/ui';

/**
 * **SCR-FP-009's "Export CSV / PDF"** — the wireframe's control, and the two
 * different things behind it.
 *
 * ## Neither is an API route, because there is no analytics export route
 *
 * `exportFleetInvoice` is fleet-billing-svc's and produces one month's **invoice**
 * — a different document about different numbers — and nothing on any contract
 * exports a trip report. So neither half of this control invents a platform
 * capability:
 *
 *  - **CSV** is a link to `/analytics/export`, a route handler in this application
 *    that re-reads the same org-scoped `GET …/analytics` with the same `from` and
 *    `to` and writes the rows out. It carries no figure that is not on the screen,
 *    and it sits under the analytics path so `proxy.ts` gates it as the analytics
 *    screen — a Viewer may download it and somebody with no seat may not.
 *    An `<a download>` rather than a fetch: the browser streams it to disk with no
 *    JavaScript holding a blob, and it still works if this component never
 *    hydrates.
 *  - **PDF** is `window.print()`. Every browser's print dialogue offers "Save as
 *    PDF", and the screen carries `print:` rules so the shell, the nav and these
 *    controls drop out and the report is what lands on the page. A real server-side
 *    renderer would be a PDF engine, a font pipeline for Sinhala and Tamil, and a
 *    second layout to keep in step with this one — for a document the browser
 *    already produces from the layout that is on screen.
 *
 * The button is `type="button"` and does nothing on the server, so a Viewer's
 * session renders it exactly as an Owner's: a report is a read, and nothing here
 * mutates anything.
 */

export interface ExportControlsProps {
  /** `/analytics/export?from=…&to=…` — built by the page, which owns the range. */
  readonly csvHref: string;
  readonly labels: {
    readonly csv: string;
    readonly pdf: string;
  };
}

export function ExportControls({ csvHref, labels }: ExportControlsProps) {
  return (
    <div className="flex flex-wrap items-center gap-xs print:hidden">
      {/*
        A plain anchor, not a `next/link`: this is a download of a different
        content type, not a navigation into the router's own tree, and prefetching
        a CSV would generate the file on hover.
      */}
      <a
        href={csvHref}
        download
        className="inline-flex h-10 items-center justify-center rounded-sm border border-outline bg-surface px-md text-body-sm font-body text-on-surface hover:bg-surface-variant focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
      >
        {labels.csv}
      </a>
      <Button variant="ghost" size="compact" onClick={() => window.print()}>
        {labels.pdf}
      </Button>
    </div>
  );
}
