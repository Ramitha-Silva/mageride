/**
 * `@mageride/ui` — the headless-primitive components every MageRide web surface
 * reuses, styled with Tailwind from `@mageride/tailwind-preset` (AL-52).
 *
 * Two rules hold across the whole set:
 *
 *  - **No literal user-facing text.** Every label, caption and accessible name
 *    is a required prop. A component with an English default would be a string
 *    no Sinhala or Tamil user can ever be shown (CLAUDE.md, Trilingual
 *    resources), and it would be invisible to the lint rule because it lives in
 *    a library rather than a screen.
 *  - **No colour, size or radius that is not a D2 §0.2 token.** Where D2 has no
 *    token — a table header, a 40px dense control — the value is composed from
 *    tokens or from D2's own 4px grid, never introduced.
 */

export { cx, CTA_HEIGHT, type ClassValue } from './lib/cx.js';

export {
  Button,
  CTA_CLASS_NAMES,
  type ButtonProps,
  type ButtonSize,
  type ButtonVariant,
} from './components/Button.js';

export {
  Field,
  Input,
  Select,
  Textarea,
  type FieldProps,
  type InputProps,
  type SelectProps,
  type TextareaProps,
} from './components/Field.js';

export { Chip, type ChipAccent, type ChipProps } from './components/Chip.js';

export { StatusPill, type StatusPillProps, type StatusTone } from './components/StatusPill.js';

export {
  TBody,
  TD,
  TH,
  THead,
  TR,
  Table,
  TableEmpty,
  type TRProps,
  type TableEmptyProps,
  type TableProps,
} from './components/Table.js';

export { Modal, ModalPrimitive, type ModalProps } from './components/Modal.js';

export {
  Toast,
  ToastPrimitive,
  ToastProvider,
  type ToastProps,
  type ToastProviderProps,
  type ToastTone,
} from './components/Toast.js';

export { Tabs, TabsPrimitive, type TabItem, type TabsProps } from './components/Tabs.js';

export { Dropzone, type DropzoneProps, type DropzoneRejection } from './components/Dropzone.js';
