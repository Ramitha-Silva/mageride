'use client';

import { useActionState } from 'react';

import { Button, Field, Input } from '@mageride/ui';

import { setLevelConfig, type ConfigState } from '@/server/config-actions';

/**
 * SCR-AP-007's "Driver Level" tab — `PUT /v1/admin/drivers/level-config`
 * (US-14.12, D5' §4).
 *
 * ## Every box is optional and empty means "leave it alone"
 *
 * dispatch-svc's own body has all four members nullable, "like the level-config
 * PUT beside it: an admin changing θ_max should not have to restate the other
 * three". A form that sent zeros for the boxes left blank would set a no-show
 * penalty of zero because somebody edited the level-up threshold — so a blank box
 * is omitted from the body, and a submission with every box blank is refused
 * rather than sent as an empty object.
 *
 * ## It starts empty for the same reason the tariff form does
 *
 * There is no route that reads the current configuration back. The wireframe's
 * defaults (start L3, 500 points per level, 3 reports → delist) are stated as the
 * platform's documented starting values, in words, rather than pre-filled into
 * boxes as though they were what is running.
 *
 * **Level 1 is excluded from the Job Board** (US-6A.8), which is what bounds
 * `jobBoardMinLevel`.
 */

export interface LevelConfigLabels {
  readonly heading: string;
  readonly noReadNote: string;
  readonly defaultsNote: string;
  readonly levelUpThreshold: string;
  readonly levelUpThresholdHint: string;
  readonly noShowPenalty: string;
  readonly noShowPenaltyHint: string;
  readonly cancellationPenalty: string;
  readonly cancellationPenaltyHint: string;
  readonly jobBoardMinLevel: string;
  readonly jobBoardMinLevelHint: string;
  readonly submit: string;
  readonly working: string;
  readonly audit: string;
  readonly saved: string;
}

const INITIAL: ConfigState = {};

export function LevelConfigForm({ labels }: { labels: LevelConfigLabels }) {
  const [state, formAction, pending] = useActionState(setLevelConfig, INITIAL);

  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
      <p className="text-caption text-on-surface-variant">{labels.noReadNote}</p>
      <p className="text-caption text-on-surface-variant">{labels.defaultsNote}</p>

      {state.saved ? (
        <p
          role="status"
          className="rounded-md border border-success/40 bg-success/10 p-sm text-body-sm text-on-surface"
        >
          {labels.saved}
        </p>
      ) : null}

      <form action={formAction} className="flex flex-col gap-sm">
        <div className="flex flex-wrap gap-sm">
          <Field
            label={labels.levelUpThreshold}
            hint={labels.levelUpThresholdHint}
            className="w-[240px]"
            {...(state.field === 'levelUpThreshold' && state.message
              ? { error: state.message }
              : {})}
          >
            <Input name="levelUpThreshold" type="number" min="1" step="1" inputMode="numeric" />
          </Field>

          <Field
            label={labels.jobBoardMinLevel}
            hint={labels.jobBoardMinLevelHint}
            className="w-[240px]"
            {...(state.field === 'jobBoardMinLevel' && state.message
              ? { error: state.message }
              : {})}
          >
            <Input name="jobBoardMinLevel" type="number" min="1" max="3" step="1" inputMode="numeric" />
          </Field>
        </div>

        <div className="flex flex-wrap gap-sm">
          <Field
            label={labels.noShowPenalty}
            hint={labels.noShowPenaltyHint}
            className="w-[240px]"
          >
            <Input name="noShowPenaltyPoints" type="number" min="0" step="1" inputMode="numeric" />
          </Field>

          <Field
            label={labels.cancellationPenalty}
            hint={labels.cancellationPenaltyHint}
            className="w-[240px]"
          >
            <Input
              name="cancellationPenaltyPoints"
              type="number"
              min="0"
              step="1"
              inputMode="numeric"
            />
          </Field>
        </div>

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
