import Link from 'next/link';

import { Button, Field, Input, Select } from '@mageride/ui';

import { SETTLEMENT_METHODS, type ReconciliationSelection } from '@/api/finance';

/**
 * The reconciliation window and rail, as a `method="get"` form.
 *
 * **The filter is the URL and nothing else** — SCR-AP-002's rule, and it earns its
 * keep here more than anywhere: a Finance Officer who has narrowed a variance to
 * one rail and one week needs to paste that exact view into a ticket, and a
 * reconciliation figure with no stated window is unfalsifiable once the request
 * that produced it is gone.
 *
 * **Both ends or neither.** A half-chosen range is dropped by
 * `reconciliationSelection` rather than sent, so admin-bff applies its documented
 * default (the last 30 Asia/Colombo business days, D-38) and answers with the
 * window it used — which the card then prints. Sending one open end would answer
 * the operator's first click with a validation error about a form they have not
 * finished filling in.
 *
 * There are **two** rails in the dropdown and there is no third (AL-05).
 */

export interface SettlementFilterLabels {
  readonly from: string;
  readonly to: string;
  readonly method: string;
  readonly methodAll: string;
  readonly onepay: string;
  readonly lankaqr: string;
  readonly apply: string;
  readonly clear: string;
  readonly timezone: string;
}

const METHOD_LABEL = {
  onepay: 'onepay',
  lankaqr: 'lankaqr',
} as const satisfies Record<(typeof SETTLEMENT_METHODS)[number], keyof SettlementFilterLabels>;

export function SettlementFilter({
  selection,
  labels,
}: {
  selection: ReconciliationSelection;
  labels: SettlementFilterLabels;
}) {
  const filtered = Boolean(selection.from ?? selection.method);

  return (
    <form
      method="get"
      action="/finance/reconciliation"
      className="flex flex-wrap items-end gap-sm rounded-card border border-outline bg-background p-sm shadow-card"
    >
      {selection.view !== 'settlement' ? (
        <input type="hidden" name="view" value={selection.view} />
      ) : null}

      <Field label={labels.from} className="w-[170px]">
        <Input type="date" name="from" defaultValue={selection.from ?? ''} />
      </Field>

      <Field label={labels.to} hint={labels.timezone} className="w-[170px]">
        <Input type="date" name="to" defaultValue={selection.to ?? ''} />
      </Field>

      <Field label={labels.method} className="w-[180px]">
        <Select name="method" defaultValue={selection.method ?? ''}>
          <option value="">{labels.methodAll}</option>
          {SETTLEMENT_METHODS.map((method) => (
            <option key={method} value={method}>
              {labels[METHOD_LABEL[method]]}
            </option>
          ))}
        </Select>
      </Field>

      <Button type="submit" size="compact">
        {labels.apply}
      </Button>

      {filtered ? (
        <Link
          href="/finance/reconciliation"
          className="inline-flex h-10 items-center rounded-sm px-md text-body-sm text-on-surface-variant underline underline-offset-2 hover:bg-surface-variant"
        >
          {labels.clear}
        </Link>
      ) : null}
    </form>
  );
}
