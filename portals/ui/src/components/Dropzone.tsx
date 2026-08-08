'use client';

/**
 * Dropzone — drag-and-drop plus file picker. The GTFS upload (SCR-AP-016,
 * ".zip only, ≤ 200 MB") and the fleet document slots (SCR-FP-004) both land
 * here.
 *
 * There is no headless primitive for this, so it is built directly. The two
 * things it must not get wrong:
 *
 *   - it stays a real `<input type="file">` under a `<label>`, so the keyboard
 *     and the screen reader get the browser's own control rather than a div
 *     pretending to be one;
 *   - `accept` and `maxSizeBytes` are checked here *and* meant to be checked
 *     again on the server. The client check is a courtesy that saves a 200 MB
 *     upload; it is not a gate, because a client-side gate is not a gate.
 */

import type { ChangeEvent, DragEvent, ReactNode } from 'react';
import { useCallback, useId, useRef, useState } from 'react';

import { cx } from '../lib/cx.js';

export type DropzoneRejection =
  | { readonly reason: 'type'; readonly file: File }
  | { readonly reason: 'size'; readonly file: File }
  | { readonly reason: 'count'; readonly file: File };

export interface DropzoneProps {
  /** The prompt shown in the resting state. A resource string. */
  label: string;
  /** Secondary line — the accepted formats and size limit, already localised. */
  hint?: string;
  /** `accept` attribute, e.g. `.zip` or `image/*`. Also used for the local pre-check. */
  accept?: string;
  multiple?: boolean;
  maxSizeBytes?: number;
  disabled?: boolean;
  /** Files that passed the local checks. */
  onFiles: (files: File[]) => void;
  /** Files that did not, with the reason — so the caller can show its own message. */
  onReject?: (rejections: DropzoneRejection[]) => void;
  /** Rendered under the prompt: a progress bar, the selected file name, an error. */
  children?: ReactNode;
  className?: string;
}

/** Matches a file against an `accept` list — extensions, exact types and `image/*` forms. */
function matchesAccept(file: File, accept: string | undefined): boolean {
  if (!accept) return true;
  const name = file.name.toLowerCase();
  return accept
    .split(',')
    .map((entry) => entry.trim().toLowerCase())
    .filter(Boolean)
    .some((entry) => {
      if (entry.startsWith('.')) return name.endsWith(entry);
      if (entry.endsWith('/*')) return file.type.startsWith(entry.slice(0, -1));
      return file.type === entry;
    });
}

export function Dropzone({
  label,
  hint,
  accept,
  multiple = false,
  maxSizeBytes,
  disabled = false,
  onFiles,
  onReject,
  children,
  className,
}: DropzoneProps) {
  const inputId = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);

  const partition = useCallback(
    (incoming: File[]) => {
      const accepted: File[] = [];
      const rejected: DropzoneRejection[] = [];

      for (const [index, file] of incoming.entries()) {
        if (!multiple && index > 0) rejected.push({ reason: 'count', file });
        else if (!matchesAccept(file, accept)) rejected.push({ reason: 'type', file });
        else if (maxSizeBytes !== undefined && file.size > maxSizeBytes)
          rejected.push({ reason: 'size', file });
        else accepted.push(file);
      }

      if (accepted.length > 0) onFiles(accepted);
      if (rejected.length > 0) onReject?.(rejected);
    },
    [accept, maxSizeBytes, multiple, onFiles, onReject],
  );

  const handleChange = useCallback(
    (event: ChangeEvent<HTMLInputElement>) => {
      partition(Array.from(event.target.files ?? []));
      // Reset so re-picking the same file fires `change` again — otherwise a
      // failed upload cannot be retried with the same file.
      event.target.value = '';
    },
    [partition],
  );

  const handleDrop = useCallback(
    (event: DragEvent<HTMLLabelElement>) => {
      event.preventDefault();
      setDragging(false);
      if (disabled) return;
      partition(Array.from(event.dataTransfer.files));
    },
    [disabled, partition],
  );

  return (
    <div className={cx('flex flex-col gap-xs', className)}>
      <label
        htmlFor={inputId}
        onDragOver={(event) => {
          event.preventDefault();
          if (!disabled) setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={handleDrop}
        className={cx(
          'flex cursor-pointer flex-col items-center justify-center gap-xxs rounded-md border-2 border-dashed p-xl text-center',
          'transition-colors focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-primary',
          dragging
            ? 'border-primary bg-primary-container/40'
            : 'border-outline bg-surface hover:bg-surface-variant',
          disabled && 'pointer-events-none opacity-40',
        )}
      >
        <span className="text-subtitle text-on-surface">{label}</span>
        {hint ? <span className="text-caption text-on-surface-variant">{hint}</span> : null}
        <input
          ref={inputRef}
          id={inputId}
          type="file"
          className="sr-only"
          accept={accept}
          multiple={multiple}
          disabled={disabled}
          onChange={handleChange}
        />
      </label>
      {children}
    </div>
  );
}
