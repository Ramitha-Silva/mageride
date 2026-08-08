'use client';

import { startTransition, useActionState, useEffect, useState } from 'react';

import { Button, Dropzone, Select, StatusPill, type DropzoneRejection } from '@mageride/ui';

import { TRACKER_BULK_MAX_BYTES, type BulkTrackerJob, type CredentialType } from '@/api/trackers';
import {
  importTrackerCsv,
  readTrackerBulkJob,
  type TrackerActionState,
} from '@/server/tracker-actions';

/**
 * **SCR-FP-006's bulk IMEI onboarding** (T-09, US-3.2) — provisioning-svc's
 * `POST …/trackers/bulk`.
 *
 * ## The report lists every row, not only the failures
 *
 * That is provisioning-svc's own decision and the reason the link is offered
 * whatever the outcome: "an operator reconciling 5,000 trackers needs to diff the
 * report against the file they uploaded". It is the opposite of the vehicle
 * import's report, which is offered only when rows failed — the two jobs answer
 * different questions, so they get different links.
 *
 * ## The credential is one choice for the batch
 *
 * `x509` for MQTT-capable devices, `psk` for legacy TCP ones behind an adapter,
 * and it belongs to the upload rather than to the row because "a fleet is one
 * hardware generation more often than not" (C030). The default is `x509`, which
 * is the current-firmware path.
 *
 * ## This is the one control on the portal the gateway can refuse for being a browser
 *
 * `POST /v1/fleets/{fleetId}/trackers/bulk` carries `X-Attestation`, so the
 * gateway lists it as a D-30 sensitive operation and a request with no
 * `X-Platform` is `401 attestation-failed`. The control is drawn — the route is a
 * fleet-operator route in its own contract, and the policy is `Disabled` outside
 * production — and the refusal has a sentence of its own rather than the generic
 * one. See `src/server/tracker-actions.ts` and the C113 handoff.
 */

export interface BulkTrackerLabels {
  readonly heading: string;
  readonly prompt: string;
  readonly hint: string;
  readonly columns: string;
  readonly credentialType: string;
  readonly credentialHint: string;
  readonly x509: string;
  readonly psk: string;
  readonly uploading: string;
  readonly processing: (total: number) => string;
  readonly bound: (succeeded: number, total: number) => string;
  readonly someFailed: (failed: number) => string;
  readonly report: string;
  readonly refresh: string;
  readonly jobFailed: string;
  readonly rejectedType: string;
  readonly rejectedSize: string;
}

const INITIAL: TrackerActionState = {};
const POLL_MS = 3000;

export function BulkTrackerImport({
  accept,
  disabled = false,
  labels,
}: {
  accept: string;
  disabled?: boolean;
  labels: BulkTrackerLabels;
}) {
  const [state, submit, pending] = useActionState(importTrackerCsv, INITIAL);
  const [credentialType, setCredentialType] = useState<CredentialType>('x509');
  const [polled, setPolled] = useState<BulkTrackerJob | null>(null);
  const [rejection, setRejection] = useState<string | null>(null);

  const job = polled ?? state.job ?? null;
  const jobId = job?.jobId ?? null;
  const processing = job?.status === 'PROCESSING';

  useEffect(() => {
    if (!jobId || !processing) return;

    const timer = setTimeout(() => {
      void readTrackerBulkJob(jobId).then((answer) => {
        if (answer.job) setPolled(answer.job);
      });
    }, POLL_MS);

    return () => clearTimeout(timer);
  }, [jobId, processing, polled]);

  const send = (files: File[]) => {
    const file = files[0];
    if (!file) return;

    setRejection(null);
    setPolled(null);

    const body = new FormData();
    body.set('file', file);
    body.set('credentialType', credentialType);

    startTransition(() => submit(body));
  };

  const reject = (rejections: readonly DropzoneRejection[]) => {
    const first = rejections[0];
    if (!first) return;
    setRejection(first.reason === 'size' ? labels.rejectedSize : labels.rejectedType);
  };

  const refresh = () => {
    if (!jobId) return;
    void readTrackerBulkJob(jobId).then((answer) => {
      if (answer.job) setPolled(answer.job);
    });
  };

  return (
    <section className="flex w-full flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card lg:w-[320px] lg:shrink-0">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <label className="flex flex-col gap-xxs">
        <span className="text-label text-on-surface-variant">{labels.credentialType}</span>
        <Select
          value={credentialType}
          disabled={disabled || pending}
          onChange={(event) => setCredentialType(event.target.value as CredentialType)}
        >
          <option value="x509">{labels.x509}</option>
          <option value="psk">{labels.psk}</option>
        </Select>
        <span className="text-caption text-outline-variant">{labels.credentialHint}</span>
      </label>

      <Dropzone
        label={labels.prompt}
        hint={labels.hint}
        accept={accept}
        maxSizeBytes={TRACKER_BULK_MAX_BYTES}
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

      <p className="text-caption text-on-surface-variant">{labels.columns}</p>

      {job ? (
        <div
          role="status"
          className="flex flex-col gap-xs border-t border-outline pt-sm text-body-sm"
        >
          {job.status === 'FAILED' ? (
            <p className="text-error">{labels.jobFailed}</p>
          ) : processing ? (
            <p className="text-on-surface-variant">{labels.processing(job.totalRows)}</p>
          ) : (
            <>
              <p>{labels.bound(job.succeededRows ?? 0, job.totalRows)}</p>
              {(job.failedRows ?? 0) > 0 ? (
                <StatusPill tone="warning" dot={false}>
                  {labels.someFailed(job.failedRows ?? 0)}
                </StatusPill>
              ) : null}
            </>
          )}

          <div className="flex flex-wrap items-center gap-sm">
            {job.errorReportUrl ? (
              <a
                href={job.errorReportUrl}
                download
                className="text-body-sm text-primary underline underline-offset-2"
              >
                {labels.report}
              </a>
            ) : null}

            {processing ? (
              <Button type="button" size="compact" variant="ghost" onClick={refresh}>
                {labels.refresh}
              </Button>
            ) : null}
          </div>
        </div>
      ) : null}
    </section>
  );
}
