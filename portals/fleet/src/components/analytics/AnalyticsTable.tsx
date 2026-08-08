import { TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { AnalyticsRow } from './analytics-model';

/**
 * **SCR-FP-009's "Per-vehicle" table** — the wireframe's five columns, one row per
 * vehicle on the roster, in plate order (fleet-svc orders by `registration_number`).
 *
 * | column | source |
 * |---|---|
 * | Vehicle | `VehicleAnalytics.registrationNumber`, with the roster's type beside it |
 * | Trips | `tripCount` — `trips.sessions_fleet` over the period |
 * | Distance | `distanceKm` — great-circle hops, **not road distance** (see the caption) |
 * | Utilisation | `utilisationPct` — active hours over the period's hours |
 * | Idle | the complement of the same measurement (`idleHours`) |
 *
 * **Every vehicle appears, including the ones that did nothing.** The service
 * left-joins the roster, so a coach that never moved is a row of zeros rather than
 * an absence — which is the row an operator is looking for when they open this
 * screen.
 *
 * A server component. The screen's only interactive parts are the range form and
 * the export controls.
 */

export interface AnalyticsTableLabels {
  readonly heading: string;
  readonly caption: string;
  readonly vehicle: string;
  readonly trips: string;
  readonly distance: string;
  readonly utilisation: string;
  readonly idle: string;
  readonly empty: string;
  readonly idleNote: string;
  readonly distanceNote: string;
  readonly earningsNote: string;
}

export function AnalyticsTable({
  rows,
  labels,
}: {
  rows: readonly AnalyticsRow[];
  labels: AnalyticsTableLabels;
}) {
  return (
    <section className="flex flex-col gap-sm">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.vehicle}</TH>
            <TH>{labels.trips}</TH>
            <TH>{labels.distance}</TH>
            <TH>{labels.utilisation}</TH>
            <TH>{labels.idle}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={5}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.vehicleId}>
                <TD className="whitespace-nowrap">
                  <span className="inline-flex items-center gap-xs">
                    <span
                      aria-hidden="true"
                      className={`size-xxs rounded-full ${row.accentClass}`}
                    />
                    {row.plate}
                  </span>
                  {row.type ? (
                    <span className="ms-xs text-caption text-on-surface-variant">{row.type}</span>
                  ) : null}
                </TD>
                <TD className="whitespace-nowrap tabular-nums">{row.trips}</TD>
                <TD className="whitespace-nowrap tabular-nums">{row.distance}</TD>
                <TD className="whitespace-nowrap tabular-nums">{row.utilisation}</TD>
                <TD className="whitespace-nowrap tabular-nums">{row.idle}</TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>

      <p className="text-caption text-on-surface-variant">{labels.distanceNote}</p>
      <p className="text-caption text-on-surface-variant">{labels.idleNote}</p>
      <p className="text-caption text-on-surface-variant">{labels.earningsNote}</p>
    </section>
  );
}
