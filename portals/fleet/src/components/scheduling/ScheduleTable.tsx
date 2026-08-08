import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { ScheduleRow } from './schedule-model';

/**
 * **SCR-FP-008's "Per-vehicle scheduled rides"** — the wireframe's five columns,
 * soonest departure first, which is the order `listFleetSchedules` answers in.
 *
 * Three of the five carry something the sketch draws and the platform reports
 * differently, and each says so rather than being quietly reshaped:
 *
 *  - **Route** is a `spatial.routes` reference and never a name. Nothing on any
 *    contract lists those rows, so "138 Pettah ↔ Maharagama" has no source.
 *  - **Not-started alarm** is an offset, not a switch. The column is `NOT NULL`
 *    with a 1…120 CHECK, so every departure has an alarm and the sketch's
 *    off-toggle is not a state the platform can hold.
 *  - **Status** carries the alarm's own verdict. `MISSED` means the alarm rang;
 *    it does not mean the bus never went, and the caption says so.
 *
 * The **Vehicle** cell also names whose app the alarm would ring, which is the
 * one thing an operator can act on before a departure is missed.
 *
 * A server component.
 */

export interface ScheduleTableLabels {
  readonly heading: string;
  readonly caption: string;
  readonly vehicle: string;
  readonly route: string;
  readonly start: string;
  readonly alarm: string;
  readonly status: string;
  readonly empty: string;
  /** "The alarm rings in the assigned driver's app" — the sketch's own footnote. */
  readonly alarmNote: string;
  /** The window the list covers, in the deployment's own hours. */
  readonly windowNote: string;
  /** That a booked departure cannot be changed or cancelled from anywhere. */
  readonly writeOnceNote: string;
  /** That a route cannot be named. */
  readonly routeNote: string;
  readonly noRoute: string;
}

export function ScheduleTable({
  rows,
  labels,
}: {
  rows: readonly ScheduleRow[];
  labels: ScheduleTableLabels;
}) {
  return (
    <section className="flex flex-col gap-sm">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.vehicle}</TH>
            <TH>{labels.route}</TH>
            <TH>{labels.start}</TH>
            <TH>{labels.alarm}</TH>
            <TH>{labels.status}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={5}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.key}>
                <TD>
                  <span className="whitespace-nowrap">{row.vehicle}</span>
                  {row.vehicleType ? (
                    <span className="ms-xs text-caption text-on-surface-variant">
                      {row.vehicleType}
                    </span>
                  ) : null}
                  {/*
                    Whose app rings. An error tone when the answer is "nobody" —
                    a booked departure with no driver assigned over it is an alarm
                    that will be raised and delivered to no one.
                  */}
                  <span
                    className={`block text-caption ${
                      row.ringsSomebody ? 'text-on-surface-variant' : 'text-error'
                    }`}
                  >
                    {row.rings}
                  </span>
                </TD>
                <TD>
                  {row.route ? (
                    <span className="font-mono text-caption break-all">{row.route}</span>
                  ) : (
                    <span className="text-caption text-on-surface-variant">{labels.noRoute}</span>
                  )}
                </TD>
                <TD className="whitespace-nowrap tabular-nums">{row.start}</TD>
                <TD>
                  <span className="whitespace-nowrap tabular-nums">{row.alarm}</span>
                  {row.alarmRang ? (
                    <span className="block text-caption text-error">{row.alarmRang}</span>
                  ) : null}
                </TD>
                <TD>
                  <StatusPill tone={row.statusTone}>{row.status}</StatusPill>
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>

      <p className="text-caption text-on-surface-variant">{labels.alarmNote}</p>
      <p className="text-caption text-on-surface-variant">{labels.windowNote}</p>
      <p className="text-caption text-on-surface-variant">{labels.routeNote}</p>
      <p className="text-caption text-on-surface-variant">{labels.writeOnceNote}</p>
    </section>
  );
}
