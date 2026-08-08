'use client';

import { useActionState } from 'react';

import { Button, Field, Input, StatusPill } from '@mageride/ui';

import { setFeatureFlag, type ConfigState } from '@/server/config-actions';

/**
 * SCR-AP-007's "Feature flags" tab — `config.feature_flags` (US-14.12, Δ C062).
 *
 * ## A toggle is a form, and it says which way it is about to go
 *
 * The wireframe draws a switch. A switch that flips on click and posts in the
 * background would leave an operator unable to tell a slow request from a refused
 * one on a control that turns platform behaviour on and off — so each row is a
 * one-button form whose label states the change it will make ("Turn on" / "Turn
 * off"), and the current state is a pill beside it. `enabled` travels as a hidden
 * field, so what is submitted is what the row was rendered showing.
 *
 * ## The upsert is why there is an "add a flag" form as well
 *
 * `PUT …/{key}` is an upsert on purpose: "a flag's first appearance and its next
 * change are the same operator action, and a `PUT` that 404'd on a key the running
 * deploy has started reading would leave nobody able to turn on the thing the
 * release shipped". The second form is that case — a key the list does not yet
 * carry — and it takes a description, because a flag nobody can explain is one
 * nobody dares turn off.
 */

export interface FeatureFlagRowView {
  readonly key: string;
  readonly enabled: boolean;
  readonly description: string | null;
  readonly updated: string | null;
}

export interface FeatureFlagLabels {
  readonly heading: string;
  readonly note: string;
  readonly on: string;
  readonly off: string;
  readonly turnOn: string;
  readonly turnOff: string;
  readonly working: string;
  readonly updated: string;
  readonly empty: string;
  readonly addHeading: string;
  readonly key: string;
  readonly keyHint: string;
  readonly description: string;
  readonly descriptionHint: string;
  readonly enable: string;
  readonly add: string;
  readonly audit: string;
  readonly saved: string;
}

const INITIAL: ConfigState = {};

export function FeatureFlagList({
  flags,
  labels,
}: {
  flags: readonly FeatureFlagRowView[];
  labels: FeatureFlagLabels;
}) {
  const [state, formAction, pending] = useActionState(setFeatureFlag, INITIAL);

  return (
    <section className="flex flex-col gap-md">
      <div className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
        <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
        <p className="text-caption text-on-surface-variant">{labels.note}</p>

        {state.saved ? (
          <p
            role="status"
            className="rounded-md border border-success/40 bg-success/10 p-sm text-body-sm text-on-surface"
          >
            {labels.saved}
          </p>
        ) : null}

        {flags.length === 0 ? (
          <p className="text-body-sm text-on-surface-variant">{labels.empty}</p>
        ) : (
          <ul className="flex flex-col divide-y divide-outline">
            {flags.map((flag) => (
              <li key={flag.key} className="flex flex-wrap items-center gap-sm py-sm">
                <div className="min-w-[220px] flex-1">
                  <p className="font-mono text-body-sm break-all">{flag.key}</p>
                  {flag.description ? (
                    <p className="text-caption text-on-surface-variant">{flag.description}</p>
                  ) : null}
                  {flag.updated ? (
                    <p className="text-caption text-on-surface-variant">
                      {labels.updated} {flag.updated}
                    </p>
                  ) : null}
                </div>

                <StatusPill tone={flag.enabled ? 'success' : 'neutral'}>
                  {flag.enabled ? labels.on : labels.off}
                </StatusPill>

                <form action={formAction}>
                  <input type="hidden" name="key" value={flag.key} />
                  {/* The opposite of what the row is showing — so a stale page
                      asks for the change the operator can see, not the one the
                      server happens to be in. */}
                  <input type="hidden" name="enabled" value={flag.enabled ? 'false' : 'true'} />
                  <Button
                    type="submit"
                    size="compact"
                    variant="secondary"
                    disabled={pending}
                    busy={pending}
                    busyLabel={labels.working}
                  >
                    {flag.enabled ? labels.turnOff : labels.turnOn}
                  </Button>
                </form>
              </li>
            ))}
          </ul>
        )}
      </div>

      <form
        action={formAction}
        className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card"
      >
        <h2 className="text-subtitle font-semibold">{labels.addHeading}</h2>

        <div className="flex flex-wrap items-end gap-sm">
          <Field
            label={labels.key}
            hint={labels.keyHint}
            className="min-w-[260px] flex-1"
            {...(state.field === 'key' && state.message ? { error: state.message } : {})}
          >
            <Input name="key" maxLength={81} autoCapitalize="none" spellCheck={false} />
          </Field>

          <Field
            label={labels.description}
            hint={labels.descriptionHint}
            className="min-w-[260px] flex-1"
          >
            <Input name="description" maxLength={1000} />
          </Field>

          <label className="mb-sm flex items-center gap-xs text-body-sm text-on-surface">
            <input type="checkbox" name="enabled" value="true" className="size-4 accent-primary" />
            {labels.enable}
          </label>

          <Button
            type="submit"
            size="compact"
            disabled={pending}
            busy={pending}
            busyLabel={labels.working}
          >
            {labels.add}
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
