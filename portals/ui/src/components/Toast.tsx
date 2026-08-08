'use client';

/**
 * Toast — Radix Toast styled with Tailwind. The transient confirmations D2 asks
 * for on the web ("Feed vN live — passenger route options updated", SCR-AP-016).
 *
 * Radix supplies the swipe-to-dismiss, the hover/focus pause and the polite
 * live-region announcement; all of that is behaviour, so it stays with the
 * primitive. Tones reuse the StatusPill palette so a success toast and a
 * success pill are the same green.
 */

import type { ReactNode } from 'react';
import { Toast as ToastPrimitive } from 'radix-ui';

import { cx } from '../lib/cx.js';

export type ToastTone = 'neutral' | 'success' | 'warning' | 'error';

const TONES: Record<ToastTone, string> = {
  neutral: 'border-outline',
  success: 'border-success',
  warning: 'border-warning',
  error: 'border-error',
};

export interface ToastProviderProps {
  /** How long a toast stays up, in ms. Radix default is 5000. */
  duration?: number;
  children: ReactNode;
}

/** Wrap the surface once, high in the tree. */
export function ToastProvider({ duration = 5000, children }: ToastProviderProps) {
  return (
    <ToastPrimitive.Provider duration={duration} swipeDirection="right">
      {children}
      <ToastPrimitive.Viewport
        className={cx(
          'fixed right-0 bottom-0 z-50 flex w-[calc(100vw-2rem)] max-w-[400px] flex-col gap-sm p-md',
          'outline-none',
        )}
      />
    </ToastPrimitive.Provider>
  );
}

export interface ToastProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: string;
  tone?: ToastTone;
  /** Accessible name for the dismiss control. A resource string. */
  dismissLabel: string;
  /** An optional inline action, e.g. "Undo". Its label must also be a resource string. */
  action?: { label: string; altText: string; onClick: () => void };
  className?: string;
}

export function Toast({
  open,
  onOpenChange,
  title,
  description,
  tone = 'neutral',
  dismissLabel,
  action,
  className,
}: ToastProps) {
  return (
    <ToastPrimitive.Root
      open={open}
      onOpenChange={onOpenChange}
      className={cx(
        'flex items-start gap-sm rounded-md border-l-4 bg-surface p-md shadow-elevation-4',
        'data-[state=closed]:opacity-0 data-[swipe=end]:translate-x-full',
        TONES[tone],
        className,
      )}
    >
      <div className="flex min-w-0 flex-col gap-xxs">
        <ToastPrimitive.Title className="text-subtitle text-on-surface">{title}</ToastPrimitive.Title>
        {description ? (
          <ToastPrimitive.Description className="text-body-sm text-on-surface-variant">
            {description}
          </ToastPrimitive.Description>
        ) : null}
      </div>

      <div className="ml-auto flex shrink-0 items-center gap-xs">
        {action ? (
          <ToastPrimitive.Action
            altText={action.altText}
            onClick={action.onClick}
            className="rounded-sm px-xs py-xxs text-label text-primary hover:bg-primary-container focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
          >
            {action.label}
          </ToastPrimitive.Action>
        ) : null}
        <ToastPrimitive.Close
          aria-label={dismissLabel}
          className="flex size-cta-icon items-center justify-center rounded-sm text-on-surface-variant hover:bg-surface-variant focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
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
        </ToastPrimitive.Close>
      </div>
    </ToastPrimitive.Root>
  );
}

export { ToastPrimitive };
