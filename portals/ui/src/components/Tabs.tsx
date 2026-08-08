'use client';

/**
 * Tabs — Radix Tabs styled with Tailwind. The Finance tabs (SCR-AP-006), the
 * Config groups (SCR-AP-007) and the fleet analytics ranges (SCR-FP-009) are
 * all this.
 *
 * Radix carries the roving tabindex and the arrow-key navigation, which is the
 * whole reason not to build tabs out of buttons.
 *
 * The tab list scrolls sideways rather than wrapping: at 375px a four-tab row
 * that wraps to two lines pushes the panel below the fold on every screen that
 * uses it.
 */

import type { ReactNode } from 'react';
import { Tabs as TabsPrimitive } from 'radix-ui';

import { cx } from '../lib/cx.js';

export interface TabItem {
  /** Stable identifier — also the value `onValueChange` reports. */
  value: string;
  /** The visible label. A resource string. */
  label: string;
  disabled?: boolean;
  content: ReactNode;
}

export interface TabsProps {
  items: readonly TabItem[];
  value?: string;
  defaultValue?: string;
  onValueChange?: (value: string) => void;
  /** Accessible name for the tab list. A resource string. */
  label: string;
  className?: string;
}

export function Tabs({
  items,
  value,
  defaultValue,
  onValueChange,
  label,
  className,
}: TabsProps) {
  return (
    <TabsPrimitive.Root
      value={value}
      defaultValue={defaultValue ?? items[0]?.value}
      onValueChange={onValueChange}
      className={cx('flex flex-col gap-md', className)}
    >
      <TabsPrimitive.List
        aria-label={label}
        className="flex gap-xs overflow-x-auto border-b border-outline"
      >
        {items.map((item) => (
          <TabsPrimitive.Trigger
            key={item.value}
            value={item.value}
            disabled={item.disabled}
            className={cx(
              'shrink-0 border-b-2 border-transparent px-sm py-xs text-subtitle whitespace-nowrap',
              'text-on-surface-variant transition-colors hover:text-on-surface',
              'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary',
              'disabled:pointer-events-none disabled:opacity-40',
              'data-[state=active]:border-primary data-[state=active]:text-primary',
            )}
          >
            {item.label}
          </TabsPrimitive.Trigger>
        ))}
      </TabsPrimitive.List>

      {items.map((item) => (
        <TabsPrimitive.Content
          key={item.value}
          value={item.value}
          className="focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
        >
          {item.content}
        </TabsPrimitive.Content>
      ))}
    </TabsPrimitive.Root>
  );
}

export { TabsPrimitive };
