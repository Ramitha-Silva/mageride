'use client';

import { useActionState } from 'react';

import { Button, Field, Input, TBody, TD, TH, THead, TR, Table } from '@mageride/ui';

import { FARED_VEHICLE_TYPES, type VehicleType } from '@/api/config';
import { publishTariffs, type ConfigState } from '@/server/config-actions';

/**
 * SCR-AP-007's "Fare tariffs" tab — `PUT /v1/admin/fares/tariffs` (US-14.4).
 *
 * ## It starts empty, and that is the honest rendering
 *
 * Nothing on the platform serves the *current* tariffs back: `admin-bff.yaml` has
 * the `PUT` and no `GET`, and no other contract carries one. A form pre-filled
 * with D2's illustrative figures would be this screen inventing the platform's
 * live prices — and the first operator to trust them would publish a version over
 * the top of whatever is really in force. So every box is blank, the note says
 * why, and the C108 handoff raises the missing read.
 *
 * ## Every fared type is required
 *
 * The `PUT` publishes a **version**, and a version missing a vehicle type is a
 * version in which that type has no price. Unlike the daily-fee upsert next door
 * there is no "keep what was there" semantic to fall back on, so the form draws all
 * eight rows and the action refuses a partial one.
 *
 * `bus` and `train` are absent because they are Mode A — free, never onboarded
 * through the Driver App, and priced by nothing this screen writes. The wireframe
 * draws them as a "Free / —" row and the note says the same thing in words.
 *
 * ## A version is published, never edited
 *
 * `effectiveFrom` is optional and means "now" when it is left blank. Tariffs are
 * versioned and never mutated (C005) so that a completed ride stays reconcilable
 * against the rate that priced it — which is the whole of "forward-only, never
 * retro-bill" on this tab.
 */

export interface TariffLabels {
  readonly heading: string;
  readonly noReadNote: string;
  readonly modeANote: string;
  readonly caption: string;
  readonly vehicle: string;
  readonly firstKm: string;
  readonly perKm: string;
  readonly peak: string;
  readonly night: string;
  readonly effectiveFrom: string;
  readonly effectiveFromHint: string;
  readonly windowsHeading: string;
  readonly peakWindow: string;
  readonly nightWindow: string;
  readonly windowStart: string;
  readonly windowEnd: string;
  readonly windowPct: string;
  readonly windowNote: string;
  readonly submit: string;
  readonly working: string;
  readonly audit: string;
  readonly saved: string;
  readonly vehicleTypes: Readonly<Record<VehicleType, string>>;
}

const INITIAL: ConfigState = {};

export function TariffForm({ labels }: { labels: TariffLabels }) {
  const [state, formAction, pending] = useActionState(publishTariffs, INITIAL);

  return (
    <form action={formAction} className="flex flex-col gap-md">
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

        <Table caption={labels.caption}>
          <THead>
            <TR>
              <TH>{labels.vehicle}</TH>
              <TH>{labels.firstKm}</TH>
              <TH>{labels.perKm}</TH>
              <TH>{labels.peak}</TH>
              <TH>{labels.night}</TH>
            </TR>
          </THead>
          <TBody>
            {FARED_VEHICLE_TYPES.map((vehicleType) => (
              <TR key={vehicleType}>
                <TD className="whitespace-nowrap">{labels.vehicleTypes[vehicleType]}</TD>
                {/* A `Field` per cell would draw eight columns of visible labels
                    inside a table that already has headers. The accessible name is
                    built per row instead, so a screen reader hears "Motorbike —
                    first km" rather than eight boxes called "first km". */}
                <TD>
                  <Input
                    name={`firstKm.${vehicleType}`}
                    aria-label={`${labels.vehicleTypes[vehicleType]} — ${labels.firstKm}`}
                    type="number"
                    min="0"
                    step="0.01"
                    inputMode="decimal"
                    className="w-[130px]"
                  />
                </TD>
                <TD>
                  <Input
                    name={`perKm.${vehicleType}`}
                    aria-label={`${labels.vehicleTypes[vehicleType]} — ${labels.perKm}`}
                    type="number"
                    min="0"
                    step="0.01"
                    inputMode="decimal"
                    className="w-[130px]"
                  />
                </TD>
                <TD>
                  <Input
                    name={`peak.${vehicleType}`}
                    aria-label={`${labels.vehicleTypes[vehicleType]} — ${labels.peak}`}
                    type="number"
                    min="0"
                    step="1"
                    className="w-[100px]"
                  />
                </TD>
                <TD>
                  <Input
                    name={`night.${vehicleType}`}
                    aria-label={`${labels.vehicleTypes[vehicleType]} — ${labels.night}`}
                    type="number"
                    min="0"
                    step="1"
                    className="w-[100px]"
                  />
                </TD>
              </TR>
            ))}
          </TBody>
        </Table>

        <p className="text-caption text-on-surface-variant">{labels.modeANote}</p>
      </section>

      <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
        <h2 className="text-subtitle font-semibold">{labels.windowsHeading}</h2>
        {/* The night window wraps midnight and `endLocal < startLocal` is legal.
            Nothing here validates the ordering, because the platform's own default
            would fail it. */}
        <p className="text-caption text-on-surface-variant">{labels.windowNote}</p>

        {(['peak', 'night'] as const).map((kind) => (
          <fieldset key={kind} className="flex flex-wrap items-end gap-sm border-0 p-0">
            <legend className="text-label text-on-surface-variant">
              {kind === 'peak' ? labels.peakWindow : labels.nightWindow}
            </legend>

            <Field label={labels.windowStart} className="w-[140px]">
              <Input name={`window.${kind}.start`} type="time" />
            </Field>
            <Field label={labels.windowEnd} className="w-[140px]">
              <Input name={`window.${kind}.end`} type="time" />
            </Field>
            <Field label={labels.windowPct} className="w-[120px]">
              <Input name={`window.${kind}.pct`} type="number" min="0" step="1" />
            </Field>
          </fieldset>
        ))}
      </section>

      <section className="flex flex-wrap items-end gap-sm">
        <Field
          label={labels.effectiveFrom}
          hint={labels.effectiveFromHint}
          className="w-[240px]"
        >
          <Input name="effectiveFrom" type="datetime-local" />
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

        <span className="text-caption text-on-surface-variant">{labels.audit}</span>
      </section>

      {/* One alert rather than a message bound to a box: a tariff version is
          published whole, so what fails is the submission and the sentence names
          the rule that was broken rather than the cell that broke it. */}
      {state.message ? (
        <p role="alert" className="text-body-sm text-error">
          {state.message}
        </p>
      ) : null}
    </form>
  );
}
