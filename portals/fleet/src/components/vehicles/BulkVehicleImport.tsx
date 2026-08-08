'use client';

import { startTransition, useActionState, useEffect, useState } from 'react';

import { Button, Dropzone, StatusPill, type DropzoneRejection } from '@mageride/ui';

import { BULK_UPLOAD_MAX_BYTES, type BulkVehicleJob } from '@/api/vehicles';
import { importVehicleCsv, readBulkJob, type VehicleActionState } from '@/server/vehicle-actions';

/**
 * **SCR-FP-004's Bulk CSV card** (US-13.1, US-13.6).
 *
 * ## A partial import is reported as a partial import
 *
 * `fleet.yaml` is explicit that "`COMPLETED` with `failedRows > 0` is a partial
 * import with a downloadable report, **not a failure**" — so the panel reports two
 * numbers and a link rather than a red box. The operator's next action is to fix
 * the rows in the report and upload those, which is only possible if the good
 * rows landed.
 *
 * ## The error report is a link, not a fetch
 *
 * `errorReportUrl` is HMAC-signed and expiring, and its route is `security: []`
 * because "the signature in the query string **is** the credential: that is what
 * lets the Fleet Portal hand the link straight to a browser download". So it is an
 * `<a download>` and no bearer travels with it. It is absent when nothing failed —
 * the contract omits it deliberately, because "a link that downloads an empty
 * report is a button an operator learns nothing from".
 *
 * ## Polling stops on its own
 *
 * The job is asynchronous (5,000 rows in ≤ 5 minutes, NFR-43), so the panel asks
 * again every three seconds while it is `PROCESSING` and stops the moment it is
 * not. `readBulkJob` revalidates the page when the job settles, so the status
 * table below gains the imported rows without anybody pressing anything — and the
 * button stays, because a browser that lost the interval to a backgrounded tab
 * still needs a way to ask.
 */

export interface BulkVehicleLabels {
  readonly heading: string;
  readonly prompt: string;
  readonly hint: string;
  readonly columns: string;
  readonly docsPending: string;
  readonly uploading: string;
  readonly processing: (total: number) => string;
  readonly imported: (imported: number, total: number) => string;
  readonly someFailed: (failed: number) => string;
  readonly allImported: string;
  readonly report: string;
  readonly refresh: string;
  readonly jobFailed: string;
  readonly rejectedType: string;
  readonly rejectedSize: string;
}

const INITIAL: VehicleActionState = {};
const POLL_MS = 3000;

export function BulkVehicleImport({
  accept,
  disabled = false,
  labels,
}: {
  accept: string;
  disabled?: boolean;
  labels: BulkVehicleLabels;
}) {
  const [state, submit, pending] = useActionState(importVehicleCsv, INITIAL);
  const [polled, setPolled] = useState<BulkVehicleJob | null>(null);
  const [rejection, setRejection] = useState<string | null>(null);

  const job = polled ?? state.job ?? null;
  const jobId = job?.jobId ?? null;
  const processing = job?.status === 'PROCESSING';

  useEffect(() => {
    if (!jobId || !processing) return;

    const timer = setTimeout(() => {
      void readBulkJob(jobId).then((answer) => {
        if (answer.job) setPolled(answer.job);
      });
    }, POLL_MS);

    return () => clearTimeout(timer);
    // `job` is in the deps through `processing`: each answer that is still
    // PROCESSING re-runs this and schedules the next ask, and the first answer
    // that is not stops the chain.
  }, [jobId, processing, polled]);

  const send = (files: File[]) => {
    const file = files[0];
    if (!file) return;

    setRejection(null);
    setPolled(null);

    const body = new FormData();
    body.set('file', file);

    startTransition(() => submit(body));
  };

  const reject = (rejections: readonly DropzoneRejection[]) => {
    const first = rejections[0];
    if (!first) return;
    setRejection(first.reason === 'size' ? labels.rejectedSize : labels.rejectedType);
  };

  const refresh = () => {
    if (!jobId) return;
    void readBulkJob(jobId).then((answer) => {
      if (answer.job) setPolled(answer.job);
    });
  };

  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <Dropzone
        label={labels.prompt}
        hint={labels.hint}
        accept={accept}
        maxSizeBytes={BULK_UPLOAD_MAX_BYTES}
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
      <p className="text-caption text-on-surface-variant">{labels.docsPending}</p>

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
              <p>{labels.imported(job.importedRows, job.totalRows)}</p>
              {job.failedRows > 0 ? (
                <StatusPill tone="warning" dot={false}>
                  {labels.someFailed(job.failedRows)}
                </StatusPill>
              ) : (
                <StatusPill tone="success" dot={false}>
                  {labels.allImported}
                </StatusPill>
              )}
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
