/**
 * Table — the back-office workhorse: verification queues, directories, audit
 * logs, GTFS version history.
 *
 * A real `<table>`, not a grid of divs, so row and column semantics reach
 * assistive tech for free. D2 gives tables no token of their own, so the parts
 * are built from `surface` / `surface-variant` / `outline` and the `body-sm` /
 * `label` roles.
 *
 * At 375px a wide table cannot reflow into something readable, so `Table`
 * scrolls horizontally inside its own container rather than forcing the page to.
 */

import type { ComponentProps, ReactNode } from 'react';

import { cx } from '../lib/cx.js';

export interface TableProps extends ComponentProps<'table'> {
  /** The accessible name for the table. A resource string. */
  caption: string;
  /** Whether to show the caption. Hidden captions still name the table. */
  captionVisible?: boolean;
  containerClassName?: string;
}

export function Table({
  caption,
  captionVisible = false,
  className,
  containerClassName,
  children,
  ...rest
}: TableProps) {
  return (
    <div
      className={cx(
        'w-full overflow-x-auto rounded-md border border-outline bg-surface',
        containerClassName,
      )}
    >
      <table {...rest} className={cx('w-full border-collapse text-body-sm', className)}>
        <caption
          className={cx(
            'px-md py-sm text-left text-label text-on-surface-variant',
            captionVisible ? '' : 'sr-only',
          )}
        >
          {caption}
        </caption>
        {children}
      </table>
    </div>
  );
}

export function THead({ className, ...rest }: ComponentProps<'thead'>) {
  return <thead {...rest} className={cx('bg-surface-variant', className)} />;
}

export function TBody({ className, ...rest }: ComponentProps<'tbody'>) {
  return <tbody {...rest} className={cx('divide-y divide-outline', className)} />;
}

export interface TRProps extends ComponentProps<'tr'> {
  /** Row-level selection, surfaced as `aria-selected` and a tinted background. */
  selected?: boolean;
}

export function TR({ selected, className, ...rest }: TRProps) {
  return (
    <tr
      {...rest}
      aria-selected={selected}
      className={cx(selected ? 'bg-primary-container/40' : 'hover:bg-surface-variant/60', className)}
    />
  );
}

export function TH({ className, scope = 'col', ...rest }: ComponentProps<'th'>) {
  return (
    <th
      {...rest}
      scope={scope}
      className={cx('px-md py-sm text-left text-label font-body text-on-surface-variant', className)}
    />
  );
}

export function TD({ className, ...rest }: ComponentProps<'td'>) {
  return <td {...rest} className={cx('px-md py-sm text-left text-on-surface', className)} />;
}

export interface TableEmptyProps {
  /** How many columns the message should span. */
  colSpan: number;
  /** The empty-state message. A resource string. */
  children: ReactNode;
}

/** The "nothing to show" row. A `<td colSpan>` keeps the table's shape intact. */
export function TableEmpty({ colSpan, children }: TableEmptyProps) {
  return (
    <tr>
      <td colSpan={colSpan} className="px-md py-xl text-center text-body-sm text-outline-variant">
        {children}
      </td>
    </tr>
  );
}
