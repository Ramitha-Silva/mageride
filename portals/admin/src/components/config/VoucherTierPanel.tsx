'use client';

import { useActionState } from 'react';

import {
  Button,
  Field,
  Input,
  StatusPill,
  TBody,
  TD,
  TH,
  THead,
  TR,
  Table,
  TableEmpty,
} from '@mageride/ui';

import type { VoucherDiscountTier } from '@/api/config';
import { setVoucherTier, type ConfigState } from '@/server/config-actions';

/**
 * SCR-AP-007's "Commission & vouchers" tab — the bulk-voucher ladder
 * (US-9A.15, AL-01).
 *
 * ## What this percentage is, and what it is not
 *
 * "Reseller" is **not a role** and there is **no per-driver commission**. Any
 * driver who buys bulk credit resells it at face value; their margin is this
 * admin-set percentage, charged **only at purchase** and configured **per voucher
 * value**. A driver-to-driver transfer later moves the exact value with no further
 * commission, which is why the credit-transfer view on SCR-AP-006 is read-only and
 * says the same thing.
 *
 * ## The ladder is read, modified and written whole
 *
 * This is the one configuration surface with a `GET` beside its `PUT`, so a
 * read-modify-write is well defined and the table below is the platform's actual
 * ladder rather than a form starting empty. The rows travel back on a hidden field
 * so the action publishes the ladder **that was rendered**, not a second read that
 * could have moved underneath it.
 *
 * ## "Driver pays" is a preview, not a price
 *
 * wallet-svc computes the charge at purchase from the tier in force at that
 * instant. The column exists so an operator setting 18 % on a Rs 10,000 voucher
 * sees Rs 8,200 before they publish it rather than after a driver has bought one —
 * and it is integer arithmetic on minor units, because money is never a float here.
 *
 * This is the DoD's "changing a voucher discount tier is reflected in the Driver
 * App top-up screen": one write to `billing.voucher_discount_tiers`, which is the
 * table wallet-svc serves `GET /v1/wallet/voucher/discount-tiers` from.
 */

export interface VoucherTierRowView {
  readonly key: string;
  readonly denomination: string;
  readonly percent: string;
  readonly pays: string;
  readonly credit: string;
  readonly active: boolean;
}

export interface VoucherTierLabels {
  readonly heading: string;
  readonly note: string;
  readonly caption: string;
  readonly denomination: string;
  readonly percent: string;
  readonly pays: string;
  readonly credit: string;
  readonly active: string;
  readonly activeYes: string;
  readonly activeNo: string;
  readonly empty: string;
  readonly editHeading: string;
  readonly denominationHint: string;
  readonly percentHint: string;
  readonly activeLabel: string;
  readonly submit: string;
  readonly working: string;
  readonly audit: string;
  readonly saved: string;
}

const INITIAL: ConfigState = {};

export function VoucherTierPanel({
  rows,
  ladder,
  labels,
}: {
  rows: readonly VoucherTierRowView[];
  /** The ladder as it was read, sent back verbatim so the write is a modification of it. */
  ladder: readonly VoucherDiscountTier[];
  labels: VoucherTierLabels;
}) {
  const [state, formAction, pending] = useActionState(setVoucherTier, INITIAL);

  return (
    <section className="flex flex-col gap-md">
      <div className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
        <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
        <p className="text-caption text-on-surface-variant">{labels.note}</p>

        <Table caption={labels.caption}>
          <THead>
            <TR>
              <TH>{labels.denomination}</TH>
              <TH className="text-right">{labels.percent}</TH>
              <TH className="text-right">{labels.pays}</TH>
              <TH className="text-right">{labels.credit}</TH>
              <TH>{labels.active}</TH>
            </TR>
          </THead>
          <TBody>
            {rows.length === 0 ? (
              <TableEmpty colSpan={5}>{labels.empty}</TableEmpty>
            ) : (
              rows.map((row) => (
                <TR key={row.key}>
                  <TD className="tabular-nums">{row.denomination}</TD>
                  <TD className="text-right tabular-nums">{row.percent}</TD>
                  <TD className="text-right tabular-nums">{row.pays}</TD>
                  <TD className="text-right tabular-nums">{row.credit}</TD>
                  <TD>
                    <StatusPill tone={row.active ? 'success' : 'neutral'} dot={false}>
                      {row.active ? labels.activeYes : labels.activeNo}
                    </StatusPill>
                  </TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </div>

      <form
        action={formAction}
        className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card"
      >
        <h2 className="text-subtitle font-semibold">{labels.editHeading}</h2>

        {state.saved ? (
          <p
            role="status"
            className="rounded-md border border-success/40 bg-success/10 p-sm text-body-sm text-on-surface"
          >
            {labels.saved}
          </p>
        ) : null}

        <input type="hidden" name="ladder" value={JSON.stringify(ladder)} />

        <div className="flex flex-wrap items-end gap-sm">
          <Field
            label={labels.denomination}
            hint={labels.denominationHint}
            className="w-[220px]"
            {...(state.field === 'denomination' && state.message ? { error: state.message } : {})}
          >
            <Input name="denomination" type="number" min="1" step="1" inputMode="numeric" />
          </Field>

          <Field
            label={labels.percent}
            hint={labels.percentHint}
            className="w-[180px]"
            {...(state.field === 'percent' && state.message ? { error: state.message } : {})}
          >
            <Input name="percent" type="number" min="0" max="100" step="0.01" inputMode="decimal" />
          </Field>

          <label className="mb-sm flex items-center gap-xs text-body-sm text-on-surface">
            <input type="checkbox" name="active" defaultChecked className="size-4 accent-primary" />
            {labels.activeLabel}
          </label>

          <Button
            type="submit"
            size="compact"
            disabled={pending}
            busy={pending}
            busyLabel={labels.working}
          >
            {labels.submit}
          </Button>
        </div>

        {state.message && !state.field ? (
          <p role="alert" className="text-body-sm text-error">
            {state.message}
          </p>
        ) : null}

        <p className="text-caption text-on-surface-variant">{labels.audit}</p>
      </form>
    </section>
  );
}
