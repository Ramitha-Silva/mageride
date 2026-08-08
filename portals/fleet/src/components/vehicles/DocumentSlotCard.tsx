'use client';

import { startTransition, useActionState, useState } from 'react';

import { Dropzone, Input, StatusPill, type DropzoneRejection, type StatusTone } from '@mageride/ui';

import { VEHICLE_DOCUMENT_MAX_BYTES, type VehicleDocumentUploadKind } from '@/api/vehicles';
import { uploadVehicleDocument, type VehicleActionState } from '@/server/vehicle-actions';

/**
 * **One of AL-50's four named slots** (US-27.3, SCR-FP-004).
 *
 * The component takes its `kind` as a prop and never reads one from a control:
 * the page mounts exactly four of these, each with its own literal, which is what
 * makes "no generic dropzone" a property of the code rather than a rule somebody
 * remembers. There is no fifth slot to add without adding a fifth card.
 *
 * ## The chip is the server's answer and the extraction's, not this component's
 *
 * `verified | pending | missing` comes from `GET …/documents`, which derives it
 * from the document's own status **and its extracted fields** — a field ocr-svc
 * could not read holds the slot at `pending`, which is what holds the vehicle out
 * of Approved. So the card shows the fields underneath the chip: an operator
 * whose insurance certificate is stuck on `pending` can see that it is the expiry
 * date nobody could read, rather than being told to wait.
 *
 * ## The upload starts when the file is chosen
 *
 * Same as SCR-FP-002a's evidence slots, and for the same reason: a dropzone whose
 * file waits for a second button is a dropzone people leave half-used. Uploading
 * again replaces what is there, which is what somebody re-photographing a blurred
 * CR book page is trying to do.
 */

export interface DocumentSlotField {
  readonly key: string;
  /** The field's own label, translated by the page, or its raw key when unknown. */
  readonly label: string;
  /** The extracted value, or the sentence that stands in for one. */
  readonly value: string;
  readonly tone: StatusTone;
}

export interface DocumentSlotLabels {
  readonly title: string;
  readonly hint: string;
  readonly status: string;
  readonly statusTone: StatusTone;
  readonly requirement: string;
  readonly prompt: string;
  readonly accept: string;
  readonly expiry: string;
  readonly expiryHint: string;
  readonly expires: string | null;
  readonly uploading: string;
  readonly replace: string;
  readonly extracted: string;
  readonly rejectedType: string;
  readonly rejectedSize: string;
}

const INITIAL: VehicleActionState = {};

export function DocumentSlotCard({
  vehicleId,
  kind,
  accept,
  present,
  disabled = false,
  fields,
  labels,
}: {
  vehicleId: string;
  kind: VehicleDocumentUploadKind;
  accept: string;
  /** Whether a document already sits in this slot. */
  present: boolean;
  disabled?: boolean;
  fields: readonly DocumentSlotField[];
  labels: DocumentSlotLabels;
}) {
  const [state, upload, pending] = useActionState(uploadVehicleDocument, INITIAL);
  const [expiresAt, setExpiresAt] = useState('');
  const [rejection, setRejection] = useState<string | null>(null);

  const send = (files: File[]) => {
    const file = files[0];
    if (!file) return;

    setRejection(null);

    const body = new FormData();
    body.set('vehicleId', vehicleId);
    body.set('kind', kind);
    body.set('file', file);
    if (expiresAt) body.set('expiresAt', expiresAt);

    startTransition(() => upload(body));
  };

  const reject = (rejections: readonly DropzoneRejection[]) => {
    const first = rejections[0];
    if (!first) return;
    setRejection(first.reason === 'size' ? labels.rejectedSize : labels.rejectedType);
  };

  return (
    <section className="flex flex-1 flex-col gap-xs rounded-md border border-outline bg-surface p-sm">
      <div className="flex flex-wrap items-start gap-xs">
        <div className="flex-1">
          <h3 className="text-body-sm font-semibold">{labels.title}</h3>
          <p className="text-caption text-on-surface-variant">{labels.hint}</p>
        </div>
        <StatusPill tone={labels.statusTone} dot={false}>
          {labels.status}
        </StatusPill>
      </div>

      <p className="text-caption text-outline-variant">{labels.requirement}</p>

      <Dropzone
        label={labels.prompt}
        hint={labels.accept}
        accept={accept}
        maxSizeBytes={VEHICLE_DOCUMENT_MAX_BYTES}
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

      {/*
        Typed only because the extraction may not find one — fleet-svc uses it
        "only when extraction returned no expiry of its own", so a date read off
        the certificate always wins over a date somebody remembered.
      */}
      <label className="flex flex-col gap-xxs">
        <span className="text-caption text-on-surface-variant">{labels.expiry}</span>
        <Input
          type="date"
          value={expiresAt}
          disabled={disabled || pending}
          onChange={(event) => setExpiresAt(event.target.value)}
        />
        <span className="text-caption text-outline-variant">{labels.expiryHint}</span>
      </label>

      {labels.expires ? (
        <p className="text-caption text-on-surface-variant">{labels.expires}</p>
      ) : null}

      {fields.length > 0 ? (
        <div className="flex flex-col gap-xxs border-t border-outline pt-xs">
          <h4 className="text-caption font-semibold text-on-surface-variant">{labels.extracted}</h4>
          <dl className="flex flex-col gap-xxs">
            {fields.map((field) => (
              <div key={field.key} className="flex flex-wrap items-center gap-xs">
                <dt className="text-caption text-on-surface-variant">{field.label}</dt>
                <dd className="flex-1">
                  <StatusPill tone={field.tone} dot={false}>
                    {field.value}
                  </StatusPill>
                </dd>
              </div>
            ))}
          </dl>
        </div>
      ) : null}

      {present ? <p className="text-caption text-outline-variant">{labels.replace}</p> : null}
    </section>
  );
}
