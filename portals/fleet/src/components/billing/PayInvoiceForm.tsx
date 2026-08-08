'use client';

import { useActionState } from 'react';

import { Button } from '@mageride/ui';

import { payInvoice, type PayActionState } from '@/server/billing-actions';

/**
 * **SCR-FP-010's Pay verb** — settle this month from the fleet wallet now.
 *
 * Settlement also happens by itself on the billing runner's tick
 * (`FleetBilling:AutoSettle`), so this exists "for the operator who has just
 * topped up and does not want to wait" — which is why the route answers the
 * settled invoice rather than a `202`, and why this button is not the only way an
 * invoice is ever paid.
 *
 * It is drawn only for a `DUE` or `OVERDUE` invoice. `FREE` and `PAID` both answer
 * `409 invoice-not-payable`, and both are knowable before the press; a wallet that
 * cannot cover the total is **not**, because that is an amount to top up and the
 * operator has to be able to try.
 *
 * The invoice id travels in a hidden field rather than in a closure: an action
 * bound to a value is a serialised argument in the client bundle, and a form field
 * is the same fact where the browser can see it.
 */

const INITIAL: PayActionState = {};

export function PayInvoiceForm({
  invoiceId,
  labels,
}: {
  invoiceId: string;
  labels: { readonly submit: string; readonly submitting: string };
}) {
  const [state, formAction, pending] = useActionState(payInvoice, INITIAL);

  return (
    <form action={formAction} className="flex flex-wrap items-center gap-xs">
      <input type="hidden" name="invoiceId" value={invoiceId} />

      <Button type="submit" size="compact" busy={pending} busyLabel={labels.submitting}>
        {labels.submit}
      </Button>

      {state.message ? (
        <p role="alert" className="text-body-sm text-error">
          {state.message}
        </p>
      ) : null}

      {state.paid ? (
        <p role="status" className="text-body-sm text-success">
          {state.paid}
        </p>
      ) : null}
    </form>
  );
}
