'use client';

import { useActionState } from 'react';

import { Button, Field, Input, Select } from '@mageride/ui';

import { OPERATING_MODES, VEHICLE_TYPES, type VehicleType } from '@/api/config';
import { setDailyFeeRate, type ConfigState } from '@/server/config-actions';

/**
 * SCR-AP-007's "Daily fee" tab — `PUT /v1/admin/fees/rates` (US-14.4).
 *
 * ## One rung at a time
 *
 * The `PUT` is an **upsert of what it is sent**, and subscription-svc's own remark
 * says why it must be: a call that deleted the rows it was not given "would let a
 * Config screen rendering six of the eight tiers silently un-configure the other
 * two", and an un-configured type cannot go online at all. Sending exactly the
 * rung the operator edited is the shape that cannot get that wrong — and it is
 * also the only shape available to a screen with no read route to render the other
 * seven from.
 *
 * ## Mode is on the rate, so subscription pricing is set here too
 *
 * `DailyFeeRate` is keyed on (vehicle type, mode). Mode C is the on-demand
 * driver's daily fee; **Mode B is the per-vehicle monthly platform fee** the
 * wireframe calls "Mode B platform fee ≈ Rs 300/mo (1st month free)"; Mode A is
 * zero, because bus and train journeys carry no platform fee at all. One control
 * with a mode selector rather than three screens, because they are one row of one
 * table and a second form would be a second way to write it.
 *
 * ## No audit row is written for this, and the card says so
 *
 * `gateway-routes.json` matches `/v1/admin/fees/**` at Order 20 and sends it
 * straight to subscription-svc; admin-bff's interceptor never sees it. See
 * `api/audit.ts` and the C108 handoff.
 */

export interface DailyFeeLabels {
  readonly heading: string;
  readonly noReadNote: string;
  readonly vehicle: string;
  readonly mode: string;
  readonly modeA: string;
  readonly modeB: string;
  readonly modeC: string;
  readonly amount: string;
  readonly amountHint: string;
  readonly submit: string;
  readonly working: string;
  readonly audit: string;
  readonly saved: string;
  readonly vehicleTypes: Readonly<Record<VehicleType, string>>;
}

const MODE_LABEL = {
  A: 'modeA',
  B: 'modeB',
  C: 'modeC',
} as const satisfies Record<(typeof OPERATING_MODES)[number], keyof DailyFeeLabels>;

const INITIAL: ConfigState = {};

export function DailyFeeForm({ labels }: { labels: DailyFeeLabels }) {
  const [state, formAction, pending] = useActionState(setDailyFeeRate, INITIAL);

  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
      <p className="text-caption text-on-surface-variant">{labels.noReadNote}</p>

      {state.saved ? (
        <p
          role="status"
          className="rounded-md border border-success/40 bg-success/10 p-sm text-body-sm text-on-surface"
        >
          {labels.saved}
        </p>
      ) : null}

      <form action={formAction} className="flex flex-wrap items-end gap-sm">
        <Field label={labels.vehicle} className="w-[220px]">
          <Select name="vehicleType" defaultValue="sedan">
            {VEHICLE_TYPES.map((vehicleType) => (
              <option key={vehicleType} value={vehicleType}>
                {labels.vehicleTypes[vehicleType]}
              </option>
            ))}
          </Select>
        </Field>

        <Field label={labels.mode} className="w-[200px]">
          <Select name="mode" defaultValue="C">
            {OPERATING_MODES.map((mode) => (
              <option key={mode} value={mode}>
                {labels[MODE_LABEL[mode]]}
              </option>
            ))}
          </Select>
        </Field>

        <Field
          label={labels.amount}
          hint={labels.amountHint}
          className="w-[200px]"
          {...(state.field === 'dailyFee' && state.message ? { error: state.message } : {})}
        >
          <Input name="dailyFee" type="number" min="0" step="0.01" inputMode="decimal" />
        </Field>

        <Button
          type="submit"
          size="compact"
          disabled={pending}
          busy={pending}
          busyLabel={labels.working}
        >
          {labels.submit}
        </Button>
      </form>

      {state.message && !state.field ? (
        <p role="alert" className="text-body-sm text-error">
          {state.message}
        </p>
      ) : null}

      <p className="text-caption text-on-surface-variant">{labels.audit}</p>
    </section>
  );
}
