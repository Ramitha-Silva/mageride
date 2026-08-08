'use client';

/**
 * Modal — Radix Dialog styled with Tailwind. D2 radius `lg 16` ("modals"),
 * elevation level 5.
 *
 * Radix is what AL-52 means by "headless primitives … styled with Tailwind are
 * permitted": the focus trap, the escape handling, the `aria-modal` wiring and
 * the scroll lock are behaviour, not styling, and re-implementing them here
 * would be worse in every way. Nothing in it emits CSS.
 *
 * The close button's accessible name is a required prop — there is no English
 * default, because a default is exactly the hardcoded string the trilingual
 * rule forbids.
 */

import type { ReactNode } from 'react';
import { Dialog } from 'radix-ui';

import { cx } from '../lib/cx.js';

export interface ModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** The dialog title. Always rendered — an unnamed dialog is unusable by screen reader. */
  title: string;
  /** Optional supporting text, wired to `aria-describedby` by Radix. */
  description?: string;
  /** Accessible name for the close control. A resource string. */
  closeLabel: string;
  /** The action row. Rendered against the surface at the foot of the dialog. */
  footer?: ReactNode;
  className?: string;
  children?: ReactNode;
}

export function Modal({
  open,
  onOpenChange,
  title,
  description,
  closeLabel,
  footer,
  className,
  children,
}: ModalProps) {
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/50" />
        <Dialog.Content
          className={cx(
            'fixed top-1/2 left-1/2 z-50 w-[calc(100vw-2rem)] max-w-[560px] -translate-x-1/2 -translate-y-1/2',
            'flex max-h-[calc(100vh-2rem)] flex-col gap-md overflow-y-auto',
            'rounded-lg bg-surface p-lg shadow-elevation-5',
            className,
          )}
        >
          <div className="flex items-start justify-between gap-md">
            <div className="flex flex-col gap-xxs">
              <Dialog.Title className="text-title font-body text-on-surface">{title}</Dialog.Title>
              {description ? (
                <Dialog.Description className="text-body-sm text-on-surface-variant">
                  {description}
                </Dialog.Description>
              ) : null}
            </div>
            <Dialog.Close
              aria-label={closeLabel}
              className={cx(
                'flex size-cta-icon shrink-0 items-center justify-center rounded-sm text-on-surface-variant',
                'hover:bg-surface-variant focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary',
              )}
            >
              <svg viewBox="0 0 20 20" aria-hidden="true" className="size-cta-icon">
                <path
                  d="M5 5l10 10M15 5L5 15"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="1.75"
                  strokeLinecap="round"
                />
              </svg>
            </Dialog.Close>
          </div>

          {children}

          {footer ? <div className="flex flex-wrap justify-end gap-sm">{footer}</div> : null}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

/** Re-exported so a screen can build a non-standard dialog without leaving Radix. */
export { Dialog as ModalPrimitive };
