import Link from 'next/link';

import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { OverlayRow } from './map-model';

/**
 * **SCR-FP-007's "Fleet-health overlay"** — the wireframe's five columns, over the
 * union of the map answer and the health rollup (see `./map-model`).
 *
 * | column | source |
 * |---|---|
 * | Vehicle | the roster's plate and type, the map answer's plate as a fallback |
 * | Driver | the active assignment, as the database evaluated it |
 * | Speed | `FleetVehiclePosition.speedMps`, in km/h |
 * | Battery | `TrackerHealth.battery` or `.batteryMv`, in whichever the device sent |
 * | Health | `TrackerHealth.state` — US-3.13's four |
 *
 * Every row is a **link to `?vehicle={id}`**, which is what the map's own markers
 * push and what the drill-in panel reads. One selection, one URL: the table and
 * the map cannot disagree about which vehicle is open, and a row is reachable by
 * keyboard where a marker on a canvas is not.
 *
 * A server component — nothing here is interactive beyond the links.
 */

export interface FleetOverlayLabels {
  readonly heading: string;
  readonly caption: string;
  readonly vehicle: string;
  readonly driver: string;
  readonly speed: string;
  readonly battery: string;
  readonly health: string;
  readonly empty: string;
  readonly windows: string;
  readonly scoping: string;
  readonly truncated: string | null;
  readonly asOf: string | null;
}

export function FleetOverlayTable({
  rows,
  labels,
}: {
  rows: readonly OverlayRow[];
  labels: FleetOverlayLabels;
}) {
  return (
    <section className="flex flex-col gap-sm">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.vehicle}</TH>
            <TH>{labels.driver}</TH>
            <TH>{labels.speed}</TH>
            <TH>{labels.battery}</TH>
            <TH>{labels.health}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={5}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.vehicleId} selected={row.selected}>
                <TD className="whitespace-nowrap">
                  <Link
                    href={`/map?vehicle=${encodeURIComponent(row.vehicleId)}`}
                    scroll={false}
                    className="inline-flex items-center gap-xs rounded-sm text-secondary underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
                  >
                    <span aria-hidden="true" className={`size-xxs rounded-full ${row.accentClass}`} />
                    {row.plate}
                  </Link>
                  {row.type ? (
                    <span className="ms-xs text-caption text-on-surface-variant">{row.type}</span>
                  ) : null}
                </TD>
                <TD className="whitespace-nowrap">{row.driver}</TD>
                <TD className="whitespace-nowrap">{row.speed}</TD>
                <TD className="whitespace-nowrap">{row.battery}</TD>
                <TD>
                  <StatusPill tone={row.healthTone}>{row.health}</StatusPill>
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>

      <p className="text-caption text-on-surface-variant">{labels.scoping}</p>
      <p className="text-caption text-on-surface-variant">{labels.windows}</p>
      {labels.truncated ? (
        <p className="text-caption text-on-surface-variant">{labels.truncated}</p>
      ) : null}
      {labels.asOf ? <p className="text-caption text-outline-variant">{labels.asOf}</p> : null}
    </section>
  );
}
