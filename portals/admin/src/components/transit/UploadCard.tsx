'use client';

import { useCallback, useRef, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';

import { Button, Dropzone, type DropzoneRejection } from '@mageride/ui';

import { errorCode, type ProblemDetails } from '@/api/problem';
import { duplicateFeed, FEED_ACCEPT, MAX_FEED_BYTES } from '@/api/transit';

/**
 * SCR-AP-016's upload dropzone (US-28.1) — `.zip` only, ≤ 200 MB, with a progress
 * bar and the sha256-duplicate refusal inline.
 *
 * ## Why this is the one place in the portal that is not `fetch`
 *
 * D2 asks for a "Progress bar during upload", and **`fetch` cannot report upload
 * progress**: it has an event for bytes received and none for bytes sent. A server
 * action cannot either, and would additionally have to buffer the body. So the
 * transport here is `XMLHttpRequest`, whose `upload.onprogress` is the only API in
 * a browser that answers the question — and a 200 MB national feed on an office
 * connection is exactly the upload that must not look frozen.
 *
 * The shell's fourth decision is untouched. The request goes to **this
 * application's own route handler**, which is where the operator's bearer is
 * attached and where the body is streamed on to the platform; the browser holds no
 * token and never sees the gateway.
 *
 * ## The local checks are a courtesy and are not the gate
 *
 * `accept` and `maxSizeBytes` stop a 400 MB file before it is sent, which saves
 * the operator ten minutes. They decide nothing: transit-svc refuses a declared
 * `Content-Length` over the ceiling, raises Kestrel's own limit to exactly it, and
 * counts the file's bytes as it stores them — three guards, because they catch
 * three different clients (`GtfsAdminEndpoints`).
 *
 * ## A duplicate is an inline error with somewhere to go
 *
 * `409 feed-duplicate` is BR-32.1 refusing the **bytes**, not the request, so it
 * catches a retry that regenerated its idempotency key and the same file uploaded
 * a month later by somebody else. The refusal names the version that already holds
 * those bytes, so the message names it and links to it — "a bare 409 leaves the
 * operator with a message and nowhere to go", in transit-svc's own words.
 */

export interface UploadLabels {
  readonly heading: string;
  readonly dropzone: string;
  readonly hint: string;
  readonly required: string;
  readonly externalNote: string;
  readonly uploading: string;
  readonly percent: string;
  readonly cancel: string;
  readonly rejectedType: string;
  readonly rejectedSize: string;
  readonly rejectedCount: string;
  readonly duplicate: string;
  readonly duplicateOpen: string;
  readonly sessionEnded: string;
  readonly failed: string;
  readonly audit: string;
}

interface DuplicateNotice {
  readonly message: string;
  readonly href: string;
}

/** Where the relay lives. A sibling of the screen, so `proxy.ts` gates it on the same nav item. */
const UPLOAD_ENDPOINT = '/config/transit/gtfs/upload';

/**
 * The translator's own placeholder syntax, applied in the browser.
 *
 * The copy is resolved on the server like every other string on this screen; what
 * cannot be is the *value* — a file the operator picked a moment ago and a byte
 * count that changes ten times a second exist only here.
 */
function fill(template: string, name: string, value: string): string {
  return template.replace(`{${name}}`, value);
}

export function UploadCard({
  screenPath,
  labels,
}: {
  /** The screen's own path, so a finished upload can select the version it created. */
  screenPath: string;
  labels: UploadLabels;
}) {
  const router = useRouter();
  const request = useRef<XMLHttpRequest | null>(null);

  const [uploading, setUploading] = useState(false);
  const [percent, setPercent] = useState<number | null>(null);
  const [fileName, setFileName] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [duplicate, setDuplicate] = useState<DuplicateNotice | null>(null);

  const finish = useCallback(() => {
    request.current = null;
    setUploading(false);
    setPercent(null);
    setFileName(null);
  }, []);

  const refused = useCallback(
    (problem: ProblemDetails | null) => {
      const code = problem ? errorCode(problem) : 'unknown';
      const existing = problem && code === 'feed-duplicate' ? duplicateFeed(problem) : null;

      if (existing) {
        setDuplicate({
          message: fill(
            labels.duplicate,
            'version',
            existing.feedInfoVersion ?? existing.feedVersionId,
          ),
          href: `${screenPath}?feed=${encodeURIComponent(existing.feedVersionId)}`,
        });
        return;
      }

      // Three codes this control can say something specific about; everything else
      // is "it did not finish", because an operator's next move is the same for a
      // 500 as for a socket that closed — pick the file again.
      setMessage(
        code === 'payload-too-large'
          ? labels.rejectedSize
          : code === 'unauthorized'
            ? labels.sessionEnded
            : labels.failed,
      );
    },
    [labels, screenPath],
  );

  const send = useCallback(
    (file: File) => {
      setMessage(null);
      setDuplicate(null);
      setFileName(file.name);
      setUploading(true);
      setPercent(null);

      const body = new FormData();
      body.append('file', file);

      const xhr = new XMLHttpRequest();
      request.current = xhr;

      xhr.open('POST', UPLOAD_ENDPOINT);
      xhr.responseType = 'text';

      xhr.upload.addEventListener('progress', (event) => {
        // `lengthComputable` is false while the browser is still measuring the
        // body. An indeterminate bar is honest there; a fabricated percentage is
        // not, and this one would sit at 100 % for the whole upload.
        if (event.lengthComputable && event.total > 0) {
          setPercent(Math.min(100, Math.round((event.loaded / event.total) * 100)));
        }
      });

      xhr.addEventListener('load', () => {
        finish();

        const answer = parseJson(xhr.responseText);

        if (xhr.status === 202) {
          const id = answer?.['feedVersionId'];
          // Selecting the new version *is* the navigation: the screen re-renders
          // server-side against `?feed=`, which puts the stepper on the feed that
          // was just uploaded and starts the two-second poll.
          if (typeof id === 'string' && id) {
            router.replace(`${screenPath}?feed=${encodeURIComponent(id)}`);
          } else {
            router.refresh();
          }
          return;
        }

        refused(answer as ProblemDetails | null);
      });

      // A dropped connection mid-upload and an abandoned one look the same to the
      // operator: nothing was created, and the file is still on their disk.
      xhr.addEventListener('error', () => {
        finish();
        setMessage(labels.failed);
      });
      xhr.addEventListener('abort', finish);

      xhr.send(body);
    },
    [finish, labels, refused, router, screenPath],
  );

  const reject = useCallback(
    (rejections: readonly DropzoneRejection[]) => {
      const reason = rejections[0]?.reason;
      setDuplicate(null);
      setMessage(
        reason === 'size'
          ? labels.rejectedSize
          : reason === 'count'
            ? labels.rejectedCount
            : labels.rejectedType,
      );
    },
    [labels],
  );

  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <Dropzone
        label={labels.dropzone}
        hint={labels.hint}
        accept={FEED_ACCEPT}
        maxSizeBytes={MAX_FEED_BYTES}
        disabled={uploading}
        onFiles={(files) => {
          const file = files[0];
          if (file) send(file);
        }}
        onReject={reject}
      >
        {uploading ? (
          <div className="flex flex-col gap-xxs">
            <div className="flex items-center justify-between gap-sm">
              <p className="min-w-0 truncate text-body-sm text-on-surface">
                {labels.uploading}
                {fileName ? <span className="text-on-surface-variant"> {fileName}</span> : null}
              </p>
              <Button
                type="button"
                size="compact"
                variant="ghost"
                onClick={() => request.current?.abort()}
              >
                {labels.cancel}
              </Button>
            </div>

            {/*
              A native <progress>, because the alternative is a div whose width is
              an inline style — and AL-52 leaves this portal no inline styles at
              all. It also gets the indeterminate state for free: no `value` is
              exactly the "the browser has not measured this body yet" case, which
              a hand-rolled bar can only fake.
            */}
            <progress
              aria-label={labels.uploading}
              {...(percent === null ? {} : { value: percent })}
              max={100}
              className="h-2 w-full overflow-hidden rounded-full bg-surface-variant [&::-moz-progress-bar]:bg-primary [&::-webkit-progress-bar]:bg-surface-variant [&::-webkit-progress-value]:bg-primary"
            />

            {percent === null ? null : (
              <p className="text-caption text-on-surface-variant">
                {fill(labels.percent, 'percent', String(percent))}
              </p>
            )}
          </div>
        ) : null}
      </Dropzone>

      {duplicate ? (
        <p role="alert" className="flex flex-wrap items-center gap-xs text-body-sm text-error">
          {duplicate.message}
          <Link href={duplicate.href} className="text-primary underline">
            {labels.duplicateOpen}
          </Link>
        </p>
      ) : null}

      {message ? (
        <p role="alert" className="text-body-sm text-error">
          {message}
        </p>
      ) : null}

      <p className="text-caption text-on-surface-variant">{labels.required}</p>
      <p className="text-caption text-on-surface-variant">{labels.externalNote}</p>
      <p className="text-caption text-on-surface-variant">{labels.audit}</p>
    </section>
  );
}

/** A JSON body, or `null` for anything that is not one. */
function parseJson(text: string): Record<string, unknown> | null {
  if (!text) return null;

  try {
    const body: unknown = JSON.parse(text);
    return body && typeof body === 'object' ? (body as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}
