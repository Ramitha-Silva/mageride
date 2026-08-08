import Link from 'next/link';

import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { VehicleRow } from './vehicle-model';

/**
 * **SCR-FP-004's "Onboarding status" table** — the roster, with the two columns
 * Epic 27 added: **Documents** (AL-50) and **Service payment** (AL-51).
 *
 * A server component. Nothing here is interactive except the link into a
 * vehicle's document slots, which is a URL rather than a state — so an operator
 * can send a colleague "the row that is stuck" and have them land on it.
 *
 * The type cell carries the D2 §0.2 per-type dot (AL-26), which is the only place
 * a colour means something in this table: the status and documents chips are
 * `StatusPill`s in the three semantic tones, and the dot is the vehicle type.
 */

export interface VehicleTableLabels {
  readonly heading: string;
  readonly caption: string;
  readonly plate: string;
  readonly type: string;
  readonly servicePayment: string;
  readonly documents: string;
  readonly status: string;
  readonly empty: string;
  readonly manage: string;
}

export function VehicleStatusTable({
  rows,
  labels,
}: {
  rows: readonly VehicleRow[];
  labels: VehicleTableLabels;
}) {
  return (
    <section className="flex flex-col gap-sm">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.plate}</TH>
            <TH>{labels.type}</TH>
            <TH>{labels.servicePayment}</TH>
            <TH>{labels.documents}</TH>
            <TH>{labels.status}</TH>
            <TH>
              <span className="sr-only">{labels.manage}</span>
            </TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={6}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.vehicleId} selected={row.selected}>
                <TD className="font-semibold whitespace-nowrap">{row.plate}</TD>
                <TD>
                  <span className="inline-flex items-center gap-xxs whitespace-nowrap">
                    <span
                      aria-hidden="true"
                      className={`block size-xs rounded-full ${row.accentClass}`}
                    />
                    {row.type}
                  </span>
                </TD>
                <TD>
                  <StatusPill tone={row.servicePaymentTone} dot={false}>
                    {row.servicePayment}
                  </StatusPill>
                </TD>
                <TD>
                  <StatusPill tone={row.documentsTone} dot={false}>
                    {row.documents}
                  </StatusPill>
                </TD>
                <TD>
                  <StatusPill tone={row.statusTone}>{row.status}</StatusPill>
                </TD>
                <TD>
                  <Link
                    href={`/vehicles?vehicle=${row.vehicleId}`}
                    className="text-body-sm whitespace-nowrap text-primary underline underline-offset-2"
                  >
                    {labels.manage}
                  </Link>
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>
    </section>
  );
}
