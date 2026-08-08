import {
  StatusPill,
  TBody,
  TD,
  TH,
  THead,
  TR,
  Table,
  TableEmpty,
  type StatusTone,
} from '@mageride/ui';

/**
 * **SCR-FP-006's ST-901 table** — the wireframe's five columns, plus the one
 * fleet-health-svc makes possible (US-13.12, US-3.13, US-3.8).
 *
 * | column | source |
 * |---|---|
 * | IMEI / MAC | `TrackerHealth.imei` |
 * | Vehicle | the roster, matched on `vehicleId` |
 * | Cadence profile | US-5.5's published rates — **not a setting**, see the page |
 * | Last seen | `TrackerHealth.lastSeen`, the platform's receive instant |
 * | Health | `TrackerHealth.state` — US-3.13's four |
 * | Credential | the same `state`, read for US-3.8's revocation |
 *
 * The last column is not in the sketch and is one word: `decommissioned` is a
 * **revoked credential**, not another shade of offline, and a table that folded
 * the two together would tell an operator their tracker had a signal problem when
 * somebody had retired it.
 *
 * A server component — nothing here is interactive.
 */

export interface TrackerRow {
  readonly key: string;
  readonly imei: string;
  readonly vehicle: string;
  readonly cadence: string;
  readonly lastSeen: string;
  readonly health: string;
  readonly healthTone: StatusTone;
  readonly credential: string;
  readonly credentialTone: StatusTone;
}

export interface TrackerTableLabels {
  readonly heading: string;
  readonly caption: string;
  readonly autoSession: string;
  readonly imei: string;
  readonly vehicle: string;
  readonly cadence: string;
  readonly lastSeen: string;
  readonly health: string;
  readonly credential: string;
  readonly empty: string;
  readonly cadenceNote: string;
  readonly thresholds: string;
  readonly truncated: string | null;
  readonly asOf: string | null;
}

export function TrackerTable({
  rows,
  labels,
}: {
  rows: readonly TrackerRow[];
  labels: TrackerTableLabels;
}) {
  return (
    <section className="flex flex-col gap-sm">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-subtitle font-semibold">{labels.heading}</h2>
        <StatusPill tone="info" dot={false}>
          {labels.autoSession}
        </StatusPill>
      </div>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.imei}</TH>
            <TH>{labels.vehicle}</TH>
            <TH>{labels.cadence}</TH>
            <TH>{labels.lastSeen}</TH>
            <TH>{labels.health}</TH>
            <TH>{labels.credential}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={6}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.key}>
                <TD className="font-mono whitespace-nowrap">{row.imei}</TD>
                <TD className="whitespace-nowrap">{row.vehicle}</TD>
                <TD className="whitespace-nowrap">{row.cadence}</TD>
                <TD className="whitespace-nowrap">{row.lastSeen}</TD>
                <TD>
                  <StatusPill tone={row.healthTone}>{row.health}</StatusPill>
                </TD>
                <TD>
                  <StatusPill tone={row.credentialTone} dot={false}>
                    {row.credential}
                  </StatusPill>
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>

      <p className="text-caption text-on-surface-variant">{labels.cadenceNote}</p>
      <p className="text-caption text-on-surface-variant">{labels.thresholds}</p>
      {labels.truncated ? (
        <p className="text-caption text-on-surface-variant">{labels.truncated}</p>
      ) : null}
      {labels.asOf ? <p className="text-caption text-outline-variant">{labels.asOf}</p> : null}
    </section>
  );
}
