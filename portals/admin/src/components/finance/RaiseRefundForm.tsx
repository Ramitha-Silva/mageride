'use client';

import { useActionState, useState } from 'react';

import { Button, Field, Input, Select } from '@mageride/ui';

import { REFUND_KINDS, type RefundKind } from '@/api/finance';
import { raiseRefund, type RaiseRefundState } from '@/server/finance-actions';

/**
 * E-05's decision: raise a full, partial or overpaid reversal against a payment.
 *
 * **The amount box appears only for a partial.** `amountMinor` may be omitted on a
 * `full` or an `overpaid_reversal` and means the whole payment — so the field's
 * *absence* is the instruction, and a box that was always shown would invite an
 * operator to retype a figure the platform already knows and can only get wrong.
 * That is the one piece of client state on this form, and it is about which
 * control exists rather than about what the form remembers.
 *
 * **The ceiling is stated, not enforced here.** `paymentAmountMinor` is what the
 * attempt collected and a refund cannot exceed it; fare-svc owns that rule and
 * answers `invalid-amount`. Printing it is what lets an operator get it right the
 * first time without this process holding a second copy of the arithmetic.
 */

export interface RaiseRefundLabels {
  readonly heading: string;
  readonly payment: string;
  readonly paymentHint: string;
  readonly kind: string;
  readonly kindFull: string;
  readonly kindPartial: string;
  readonly kindOverpaid: string;
  readonly amount: string;
  readonly amountHint: string;
  readonly reasonCode: string;
  readonly reasonCodeHint: string;
  readonly submit: string;
  readonly working: string;
  readonly audit: string;
  readonly ceiling: string;
  readonly notInQueue: string;
}

const KIND_LABEL = {
  full: 'kindFull',
  partial: 'kindPartial',
  overpaid_reversal: 'kindOverpaid',
} as const satisfies Record<RefundKind, keyof RaiseRefundLabels>;

const INITIAL: RaiseRefundState = {};

export function RaiseRefundForm({
  paymentId,
  defaultKind,
  ceiling,
  targetMissing,
  labels,
}: {
  /** The payment the queue aimed this form at, via the URL. */
  paymentId: string;
  /** `overpaid_reversal` when the row that sent the operator here was an Overpaid one. */
  defaultKind: RefundKind;
  /** What the payment collected, already rendered. `null` when no row is aimed at. */
  ceiling: string | null;
  /** The URL names a payment this page did not answer with — a stale bookmark. */
  targetMissing: boolean;
  labels: RaiseRefundLabels;
}) {
  const [state, formAction, pending] = useActionState(raiseRefund, INITIAL);
  const [kind, setKind] = useState<RefundKind>(defaultKind);

  return (
    <section
      id="raise"
      className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card"
    >
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      {targetMissing ? (
        <p role="status" className="text-body-sm text-on-surface-variant">
          {labels.notInQueue}
        </p>
      ) : null}

      <form action={formAction} className="flex flex-col gap-sm">
        <Field
          label={labels.payment}
          hint={labels.paymentHint}
          {...(state.field === 'paymentId' && state.message ? { error: state.message } : {})}
        >
          <Input
            name="paymentId"
            defaultValue={paymentId}
            maxLength={40}
            autoCapitalize="none"
            spellCheck={false}
          />
        </Field>

        <div className="flex flex-wrap items-end gap-sm">
          <Field label={labels.kind} className="w-[220px]">
            <Select
              name="kind"
              value={kind}
              onChange={(event) => setKind(event.target.value as RefundKind)}
            >
              {REFUND_KINDS.map((option) => (
                <option key={option} value={option}>
                  {labels[KIND_LABEL[option]]}
                </option>
              ))}
            </Select>
          </Field>

          {kind === 'partial' ? (
            <Field
              label={labels.amount}
              hint={ceiling ? `${labels.ceiling} ${ceiling}` : labels.amountHint}
              className="w-[200px]"
              {...(state.field === 'amount' && state.message ? { error: state.message } : {})}
            >
              <Input name="amount" type="number" min="0.01" step="0.01" inputMode="decimal" />
            </Field>
          ) : null}
        </div>

        <Field
          label={labels.reasonCode}
          hint={labels.reasonCodeHint}
          {...(state.field === 'reasonCode' && state.message ? { error: state.message } : {})}
        >
          <Input name="reasonCode" maxLength={60} autoCapitalize="none" />
        </Field>

        {state.message && !state.field ? (
          <p role="alert" className="text-body-sm text-error">
            {state.message}
          </p>
        ) : null}

        <div className="flex flex-wrap items-center gap-sm">
          <Button
            type="submit"
            size="compact"
            disabled={pending}
            busy={pending}
            busyLabel={labels.working}
          >
            {labels.submit}
          </Button>
          <span className="text-caption text-on-surface-variant">{labels.audit}</span>
        </div>
      </form>
    </section>
  );
}
