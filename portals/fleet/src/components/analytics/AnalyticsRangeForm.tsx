import { Button, Field, Input } from '@mageride/ui';

import type { AnalyticsRange } from '@/api/insights';

/**
 * **SCR-FP-009's date filter** — `web_fleet.html`'s "📅 01–17 Jun 2026" control,
 * as a plain `GET` form.
 *
 * A form with `method="get"` and no `action`, so submitting it puts `from` and
 * `to` in this screen's own query string and the server re-renders the report
 * from them. That makes the period a **URL**: a bookmark, a link pasted into a
 * message and the browser's back button all work, the CSV route can be handed the
 * same two values, and the screen has no client-side state that could disagree
 * with the figures beside it. It also works with JavaScript off, which no fleet
 * console should need but every report ought to survive.
 *
 * `type="date"` is the browser's own picker in the operator's own locale and
 * calendar, and it submits `YYYY-MM-DD` — which is `_shared.yaml`'s `BusinessDate`
 * exactly, so nothing is parsed or reformatted between the control and the wire.
 *
 * `max` is today and the span is capped at `Fleet:MaxAnalyticsDays`; both are
 * courtesies, and fleet-svc answers `400 validation-failed` either way. The
 * screen states the cap rather than silently truncating a range somebody typed.
 */

export interface AnalyticsRangeLabels {
  readonly legend: string;
  readonly from: string;
  readonly to: string;
  readonly apply: string;
  readonly hint: string;
  /** Set when what arrived in the query string was not what is being reported. */
  readonly adjusted: string | null;
}

export function AnalyticsRangeForm({
  range,
  today,
  labels,
}: {
  range: AnalyticsRange;
  today: string;
  labels: AnalyticsRangeLabels;
}) {
  return (
    <form
      method="get"
      aria-label={labels.legend}
      className="flex flex-col gap-sm md:flex-row md:items-end print:hidden"
    >
      <Field label={labels.from} className="md:w-48">
        <Input type="date" name="from" defaultValue={range.from} max={today} />
      </Field>
      <Field label={labels.to} className="md:w-48">
        <Input type="date" name="to" defaultValue={range.to} max={today} />
      </Field>
      <Button type="submit" variant="secondary" size="compact" className="md:mb-xxs">
        {labels.apply}
      </Button>
      <div className="flex flex-1 flex-col gap-xxs md:mb-xxs">
        <p className="text-caption text-on-surface-variant">{labels.hint}</p>
        {labels.adjusted ? (
          <p role="status" className="text-caption text-warning">
            {labels.adjusted}
          </p>
        ) : null}
      </div>
    </form>
  );
}
