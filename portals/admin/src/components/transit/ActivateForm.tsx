'use client';

import { useActionState, useState } from 'react';

import { Button, Modal, Toast } from '@mageride/ui';

import { activateFeed, type ActivateFeedState } from '@/server/transit-actions';

/**
 * "Activate feed" and "Re-activate" — one control, because they are one call
 * (US-28.2, US-28.3).
 *
 * ## The confirm dialog names the feed being replaced, and that is the point of it
 *
 * Activation swaps the dataset every passenger route query is answered from. What
 * an operator has to be sure of before pressing it is not "am I activating
 * something" but **"what am I switching off"** — so the dialog states the outgoing
 * version by name and says it will be archived. Where nothing is live yet (day 0,
 * AL-55) it says that instead, because "replacing —" is not a sentence.
 *
 * ## Rollback is the same dialog with one extra line
 *
 * BR-32.3's rollback is `activate` on an archived, validated version: same route,
 * same single-transaction swap, same guarantee. Giving it its own button and its
 * own confirmation would imply a different mechanism and a different risk. What it
 * gets is a line saying that this *is* a rollback, because an operator reaching
 * for it is usually doing so in a hurry.
 *
 * ## The failure message stays in the dialog
 *
 * `409 feed-not-validated` and `409 feed-already-active` are answers to the
 * question the dialog asked, so they belong where the question was — and the
 * dialog stays open, because closing it would leave a screen that looks exactly
 * like the one before the press. Success is the opposite: the dialog closes and a
 * toast says which feed is live, which is D2's own wording.
 *
 * ## It is a `<form>` and a submit button, not an `onClick`
 *
 * The mutation, its `Idempotency-Key` and its D-35 declaration all have to leave
 * from the server (`@/server/transit-actions`). A submit inside `useActionState`
 * is what makes the pending state real — the button disables itself for as long as
 * the swap is running, which is the only thing standing between an impatient
 * operator and a second POST.
 *
 * **The `ToastProvider` is the screen's, not this component's.** Every activatable
 * history row draws one of these, and over a few years of refreshes most of a
 * hundred-row history is archived and therefore activatable — a provider each
 * would be a hundred fixed viewports pinned to the same corner of the page. The
 * page wraps the whole screen in one.
 */

export interface ActivateLabels {
  readonly open: string;
  readonly reactivate: string;
  readonly title: string;
  /** `{outgoing}` and `{incoming}`. */
  readonly replacing: string;
  /** `{incoming}`, for the day-0 case where nothing is live. */
  readonly firstFeed: string;
  readonly atomic: string;
  readonly rollbackNote: string;
  readonly confirm: string;
  readonly cancel: string;
  readonly close: string;
  readonly working: string;
  /** `{version}`. */
  readonly done: string;
  readonly reload: string;
  readonly dismiss: string;
  readonly audit: string;
}

const INITIAL: ActivateFeedState = {};

/** The translator's placeholder syntax, applied to values only the browser holds. */
function fill(template: string, values: Readonly<Record<string, string>>): string {
  return template.replace(/\{(\w+)\}/g, (match, name: string) => values[name] ?? match);
}

export function ActivateForm({
  feedVersionId,
  incoming,
  outgoing,
  rollback,
  labels,
  compact = false,
}: {
  feedVersionId: string;
  /** The version about to go live, as the operator reads it. */
  incoming: string;
  /** The version being replaced, or `null` when nothing is live yet. */
  outgoing: string | null;
  /** Whether this is BR-32.3's rollback — an archived feed being made live again. */
  rollback: boolean;
  labels: ActivateLabels;
  /** History rows draw a small secondary button; the preview card draws the primary one. */
  compact?: boolean;
}) {
  const [state, formAction, pending] = useActionState(activateFeed, INITIAL);

  // Both the dialog and the toast are **derived** from the action's own result
  // rather than pushed into state by an effect. "The dialog is open while the
  // operator has asked for it and the swap has not happened" and "the toast is up
  // until this result is acknowledged" are the two facts, and writing them as
  // conditions means there is no window in which the dialog and the outcome
  // disagree — which a `useEffect` that closes the dialog necessarily has.
  const done = state.activated ?? null;
  const [asked, setAsked] = useState(false);
  const [acknowledged, setAcknowledged] = useState<string | null>(null);

  const open = asked && done === null;
  const toast = done && acknowledged !== done.feedVersionId
    ? fill(labels.done, { version: done.label })
    : null;

  return (
    <>
      <Button
        type="button"
        size="compact"
        variant={compact ? 'secondary' : 'primary'}
        onClick={() => setAsked(true)}
      >
        {rollback ? labels.reactivate : labels.open}
      </Button>

      <Modal
        open={open}
        onOpenChange={setAsked}
        title={labels.title}
        closeLabel={labels.close}
        description={
          outgoing
            ? fill(labels.replacing, { outgoing, incoming })
            : fill(labels.firstFeed, { incoming })
        }
        footer={
          <form action={formAction} className="flex flex-wrap items-center gap-sm">
            <input type="hidden" name="feedVersionId" value={feedVersionId} />
            <Button
              type="button"
              size="compact"
              variant="ghost"
              disabled={pending}
              onClick={() => setAsked(false)}
            >
              {labels.cancel}
            </Button>
            <Button
              type="submit"
              size="compact"
              disabled={pending}
              busy={pending}
              busyLabel={labels.working}
            >
              {labels.confirm}
            </Button>
          </form>
        }
      >
        <div className="flex flex-col gap-xs">
          {rollback ? <p className="text-body-sm text-on-surface">{labels.rollbackNote}</p> : null}
          <p className="text-body-sm text-on-surface-variant">{labels.atomic}</p>
          <p className="text-caption text-on-surface-variant">{labels.audit}</p>

          {state.message ? (
            <p role="alert" className="text-body-sm text-error">
              {state.message}
            </p>
          ) : null}
        </div>
      </Modal>

      {toast === null || done === null ? null : (
        <Toast
          open
          onOpenChange={(next) => {
            if (!next) setAcknowledged(done.feedVersionId);
          }}
          tone="success"
          title={toast}
          description={labels.reload}
          dismissLabel={labels.dismiss}
        />
      )}
    </>
  );
}
