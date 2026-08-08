'use client';

import { startTransition, useActionState, useState } from 'react';

import { Dropzone, Select, StatusPill, type DropzoneRejection } from '@mageride/ui';

import { DOCUMENT_MAX_BYTES, type PayoutDocumentKind } from '@/api/payout';
import { uploadPayoutDocument, type PayoutActionState } from '@/server/payout-actions';

/**
 * **SCR-FP-002a's two uploads** (AL-49) — the proof of account, and the bank-app
 * LankaQR image.
 *
 * One component, used twice, because they are the same call with a different
 * `kind`: `POST /v1/fleets/{id}/payout-profile/documents`, multipart, `kind` +
 * `file`.
 *
 * ## Why the proof slot has a chooser and the QR slot does not
 *
 * BR-31.1 asks for "the latest bank statement **or** the first page of the
 * passbook", and §26 gives the two one column (`proof_upload_id`) — so they are
 * one slot with two names, and the officer needs to know which of the two they
 * are looking at. `web_fleet.html` draws one dropzone; this adds the one control
 * the wire needs to tell them apart, and nothing else.
 *
 * ## The upload starts when the file is chosen
 *
 * There is no second "Upload" press. A dropzone whose file sits there until a
 * button is found is a dropzone people leave half-used — and the action is
 * idempotent in the only sense that matters here: uploading a second proof
 * *replaces* the first, which is what somebody re-photographing a blurred page
 * is trying to do.
 *
 * ## The size check here is a courtesy, and it is stated as one
 *
 * `Fleet:DocumentMaxBytes` is 8 MiB and fleet-svc counts the bytes **as they
 * arrive** rather than trusting a declared length. This check saves an 8 MB
 * upload that would end in a 413; it is not what enforces the ceiling, and if it
 * disagreed with the service the service would win.
 */

export interface PayoutKindOption {
  readonly value: PayoutDocumentKind;
  readonly label: string;
}

export interface PayoutUploadLabels {
  readonly heading: string;
  readonly prompt: string;
  readonly hint: string;
  readonly note?: string;
  readonly kindLegend?: string;
  readonly uploading: string;
  readonly uploaded: string;
  readonly missing: string;
  readonly rejectedType: string;
  readonly rejectedSize: string;
}

const INITIAL: PayoutActionState = {};

export function PayoutDocumentUpload({
  kinds,
  accept,
  present,
  disabled = false,
  labels,
}: {
  /** One option for the QR slot, two for the proof slot. */
  kinds: readonly PayoutKindOption[];
  accept: string;
  /** Whether the profile already carries a document in this slot. */
  present: boolean;
  /** True before there is a profile to attach anything to. */
  disabled?: boolean;
  labels: PayoutUploadLabels;
}) {
  const [state, upload, pending] = useActionState(uploadPayoutDocument, INITIAL);
  const [kind, setKind] = useState<PayoutDocumentKind>(kinds[0]!.value);
  const [rejection, setRejection] = useState<string | null>(null);

  const send = (files: File[]) => {
    const file = files[0];
    if (!file) return;

    setRejection(null);

    const body = new FormData();
    body.set('kind', kind);
    body.set('file', file);

    startTransition(() => upload(body));
  };

  const reject = (rejections: readonly DropzoneRejection[]) => {
    const first = rejections[0];
    if (!first) return;
    setRejection(first.reason === 'size' ? labels.rejectedSize : labels.rejectedType);
  };

  const settled = present || state.uploaded !== undefined;

  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <div className="flex flex-wrap items-center gap-xs">
        <h2 className="flex-1 text-subtitle font-semibold">{labels.heading}</h2>
        <StatusPill tone={settled ? 'success' : 'neutral'} dot={false}>
          {settled ? labels.uploaded : labels.missing}
        </StatusPill>
      </div>

      {kinds.length > 1 && labels.kindLegend ? (
        <label className="flex flex-col gap-xxs">
          <span className="text-label text-on-surface-variant">{labels.kindLegend}</span>
          <Select
            value={kind}
            disabled={disabled || pending}
            onChange={(event) => setKind(event.target.value as PayoutDocumentKind)}
          >
            {kinds.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Select>
        </label>
      ) : null}

      <Dropzone
        label={labels.prompt}
        hint={labels.hint}
        accept={accept}
        maxSizeBytes={DOCUMENT_MAX_BYTES}
        disabled={disabled || pending}
        onFiles={send}
        onReject={reject}
      >
        {pending ? (
          <p role="status" className="text-caption text-on-surface-variant">
            {labels.uploading}
          </p>
        ) : null}
        {rejection ?? state.message ? (
          <p role="alert" className="text-caption text-error">
            {rejection ?? state.message}
          </p>
        ) : null}
      </Dropzone>

      {labels.note ? <p className="text-caption text-on-surface-variant">{labels.note}</p> : null}
    </section>
  );
}
