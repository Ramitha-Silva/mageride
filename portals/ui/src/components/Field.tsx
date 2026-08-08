/**
 * Field — the label / control / hint / error wrapper, plus the three controls
 * the portals actually type into.
 *
 * D2 radius `md 12` is the fields token, so every control here is `rounded-md`.
 *
 * The wiring is the point: the label's `htmlFor`, the control's `id`, and the
 * `aria-describedby` that reaches the hint and the error are all derived from
 * one generated id, so a field cannot ship with its error message invisible to
 * a screen reader. Controls read their id and description from context rather
 * than taking them as props, because a caller that has to pass them is a caller
 * that can forget to.
 */

import type { ComponentProps, ReactNode } from 'react';
import { createContext, useContext, useId } from 'react';

import { cx } from '../lib/cx.js';

interface FieldContextValue {
  readonly controlId: string;
  readonly describedBy: string | undefined;
  readonly invalid: boolean;
}

const FieldContext = createContext<FieldContextValue | null>(null);

function useFieldContext(): FieldContextValue | null {
  return useContext(FieldContext);
}

export interface FieldProps {
  /** The visible label. A resource string — never a literal. */
  label: string;
  /** Helper text under the control. */
  hint?: string;
  /** When set the field renders invalid and the control is `aria-invalid`. */
  error?: string;
  /** Marks the control required and shows the required affordance. */
  required?: boolean;
  /** Accessible text for the required marker, e.g. the si/ta/en word for "required". */
  requiredLabel?: string;
  className?: string;
  children: ReactNode;
}

export function Field({
  label,
  hint,
  error,
  required = false,
  requiredLabel,
  className,
  children,
}: FieldProps) {
  const controlId = useId();
  const hintId = `${controlId}-hint`;
  const errorId = `${controlId}-error`;

  const describedBy = [hint ? hintId : null, error ? errorId : null].filter(Boolean).join(' ');

  return (
    <FieldContext.Provider
      value={{ controlId, describedBy: describedBy || undefined, invalid: Boolean(error) }}
    >
      <div className={cx('flex flex-col gap-xxs', className)}>
        <label htmlFor={controlId} className="text-label text-on-surface-variant">
          {label}
          {required ? (
            <span className="text-error" aria-hidden="true">
              {' *'}
            </span>
          ) : null}
          {required && requiredLabel ? <span className="sr-only">{requiredLabel}</span> : null}
        </label>

        {children}

        {hint ? (
          <p id={hintId} className="text-caption text-outline-variant">
            {hint}
          </p>
        ) : null}
        {error ? (
          <p id={errorId} role="alert" className="text-caption text-error">
            {error}
          </p>
        ) : null}
      </div>
    </FieldContext.Provider>
  );
}

const CONTROL = [
  'w-full rounded-md border bg-background px-sm text-body text-on-surface',
  'placeholder:text-outline-variant',
  'focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-primary',
  'disabled:cursor-not-allowed disabled:opacity-40',
].join(' ');

function controlClasses(invalid: boolean, className: string | undefined): string {
  return cx(CONTROL, invalid ? 'border-error' : 'border-outline', className);
}

export type InputProps = ComponentProps<'input'>;

export function Input({ className, ...rest }: InputProps) {
  const field = useFieldContext();
  return (
    <input
      {...rest}
      id={rest.id ?? field?.controlId}
      aria-describedby={rest['aria-describedby'] ?? field?.describedBy}
      aria-invalid={rest['aria-invalid'] ?? (field?.invalid || undefined)}
      className={controlClasses(field?.invalid ?? false, cx('h-10', className))}
    />
  );
}

export type TextareaProps = ComponentProps<'textarea'>;

export function Textarea({ className, ...rest }: TextareaProps) {
  const field = useFieldContext();
  return (
    <textarea
      {...rest}
      id={rest.id ?? field?.controlId}
      aria-describedby={rest['aria-describedby'] ?? field?.describedBy}
      aria-invalid={rest['aria-invalid'] ?? (field?.invalid || undefined)}
      className={controlClasses(field?.invalid ?? false, cx('min-h-24 py-sm', className))}
    />
  );
}

export type SelectProps = ComponentProps<'select'>;

export function Select({ className, ...rest }: SelectProps) {
  const field = useFieldContext();
  return (
    <select
      {...rest}
      id={rest.id ?? field?.controlId}
      aria-describedby={rest['aria-describedby'] ?? field?.describedBy}
      aria-invalid={rest['aria-invalid'] ?? (field?.invalid || undefined)}
      className={controlClasses(field?.invalid ?? false, cx('h-10', className))}
    />
  );
}
