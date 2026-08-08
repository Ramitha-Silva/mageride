/**
 * English resources — and, because it is the only locale declared with a literal
 * object, the file that *defines* the key set. `si.ts` and `ta.ts` are annotated
 * `Messages`, so a key added here and not there is a compile error, and a key
 * present there and not here is a compile error too. The trilingual rule
 * (CLAUDE.md) is enforced by the type checker rather than by review.
 *
 * Keys are dotted and grouped by surface area. Placeholders are `{name}` and
 * are substituted by `createTranslator`.
 *
 * This is scaffolding: the strings every web surface shares. Screen copy belongs
 * to the component that owns the screen (C104 admin, C111 fleet, C117 passenger
 * web), added to all three files at once.
 */

export const en = {
  'common.appName': 'MageRide',
  'common.cancel': 'Cancel',
  'common.confirm': 'Confirm',
  'common.save': 'Save',
  'common.close': 'Close',
  'common.back': 'Back',
  'common.next': 'Next',
  'common.retry': 'Retry',
  'common.loading': 'Loading…',
  'common.search': 'Search',
  'common.delete': 'Delete',
  'common.edit': 'Edit',
  'common.yes': 'Yes',
  'common.no': 'No',
  'common.requiredField': 'This field is required',
  'common.unexpectedError': 'An unexpected error occurred',

  'status.pending': 'Pending',
  'status.approved': 'Approved',
  'status.rejected': 'Rejected',
  'status.active': 'Active',
  'status.suspended': 'Suspended',
  'status.verified': 'Verified',

  'table.empty': 'Nothing to show',
  'table.rowsSelected': '{count} selected',

  'dropzone.prompt': 'Drag a file here, or browse',
  'dropzone.browse': 'Browse',

  'toast.dismiss': 'Dismiss',
  'modal.close': 'Close dialog',

  'mode.a': 'Public transport',
  'mode.b': 'Private transport',
  'mode.c': 'On-demand ride',

  // Language names are written in the language they name, in all three files.
  'language.si': 'සිංහල',
  'language.ta': 'தமிழ்',
  'language.en': 'English',
} as const;

/** Every key the platform knows. Adding one here obliges `si.ts` and `ta.ts`. */
export type MessageKey = keyof typeof en;

/** A complete set of resources for one locale. */
export type Messages = Record<MessageKey, string>;

export default en;
