import type { StatusTone } from '@mageride/ui';

import type { FleetMessageKey } from '@/i18n';

/**
 * **SCR-FP-005** — driver assignment (US-13.2, US-13.8, AL-23), transcribed from
 * `backend/contracts/fleet.yaml`.
 *
 * Nothing here calls anything; the org-relative targets are the strings a screen
 * hands `read()`/`mutate()`.
 *
 * ## `active` is the server's, and a client must not compute it
 *
 * An assignment is a validity window, and `Assignment.active` is "the validity
 * window evaluated by the database at read time". US-13.9's auto-expiry is that
 * flag going false **with nothing having been written and nobody having pressed
 * anything** — so a portal that compared `to` against `Date.now()` would be
 * comparing a server's window against a browser's clock, and would report an
 * expiry that had not happened yet on any machine that matters. The screen reads
 * the flag.
 */

/* ---------------------------------------------------------------------------
 * Targets
 * ------------------------------------------------------------------------ */

/** `GET`/`POST /v1/fleets/{own id}/assignments` — the history, and one assignment. */
export const ASSIGNMENTS = '/assignments';

/** `DELETE /v1/fleets/{own id}/assignments/{assignmentId}` — US-13.8's revoke. */
export function assignmentTarget(assignmentId: string): string {
  return `${ASSIGNMENTS}/${assignmentId}`;
}

/* ---------------------------------------------------------------------------
 * Shapes
 * ------------------------------------------------------------------------ */

/** `fleet.yaml#/components/schemas/Assignment`. */
export interface Assignment {
  readonly assignmentId: string;
  readonly driverId: string;
  readonly vehicleId: string;
  readonly driverName?: string;
  readonly driverPhone?: string;
  readonly registrationNumber?: string;
  readonly from: string;
  /** Absent for an open-ended assignment; set for AL-23's temporary hire. */
  readonly to?: string;
  readonly revokedAt?: string;
  /** Evaluated by the database at read time — see the file note. */
  readonly active: boolean;
}

export interface AssignmentList {
  readonly items: readonly Assignment[];
}

/**
 * The body `POST …/assignments` takes.
 *
 * **Exactly one of `driverId` and `driverPhone` names the driver**, which the
 * contract states on the schema rather than in a `oneOf`. US-13.2 assigns "by
 * User ID / phone", and C059 added the phone arm because "an operator standing
 * in a depot has the number, not the ULID" — so the screen offers one field and
 * decides which of the two it is from what was typed
 * ({@link driverReferenceFor}).
 */
export interface AssignDriverBody {
  readonly vehicleId: string;
  readonly from: string;
  readonly to?: string;
  readonly driverId?: string;
  readonly driverPhone?: string;
}

/* ---------------------------------------------------------------------------
 * The one rule the shapes do not carry
 * ------------------------------------------------------------------------ */

/**
 * `_shared.yaml#/components/schemas/Ulid` — Crockford base-32, 26 characters.
 *
 * Used to tell a User ID from a phone number in one typed field, not to
 * validate one: a value that is neither is sent as neither and the field says
 * so. fleet-svc decides whether the driver exists (`404 driver-not-found`).
 */
const ULID = /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/i;

export type DriverReference =
  | { readonly kind: 'id'; readonly driverId: string }
  | { readonly kind: 'phone'; readonly driverPhone: string }
  | { readonly kind: 'unrecognised' };

/**
 * Which of the two arms a typed value is — a 26-character ULID, a Sri Lankan
 * mobile in either form, or neither.
 *
 * `web_fleet.html` draws **one** field, "Assign driver by User ID / phone", and
 * this is what makes one field two arms. The phone is normalised the way
 * `@/api/org` normalises the organisation's contact number, because an operator
 * types `0771234567` and `PhoneE164` admits `+94771234567`; a value this cannot
 * place comes back `unrecognised` rather than being guessed at, because sending
 * a mistyped ULID as a phone number produces a `validation-failed` about a field
 * nobody filled in.
 */
export function driverReferenceFor(value: string): DriverReference {
  const trimmed = value.trim();
  if (ULID.test(trimmed)) return { kind: 'id', driverId: trimmed.toUpperCase() };

  const digits = trimmed.replaceAll(/[\s()-]/g, '');
  if (/^0\d{9}$/.test(digits)) return { kind: 'phone', driverPhone: `+94${digits.slice(1)}` };
  if (/^94\d{9}$/.test(digits)) return { kind: 'phone', driverPhone: `+${digits}` };
  if (/^\+947\d{8}$/.test(digits)) return { kind: 'phone', driverPhone: digits };

  return { kind: 'unrecognised' };
}

/* ---------------------------------------------------------------------------
 * Rendering
 * ------------------------------------------------------------------------ */

export interface AssignmentStatusView {
  readonly tone: StatusTone;
  readonly labelKey: FleetMessageKey;
}

/**
 * The status chip — Active, Revoked, Ended, or Scheduled.
 *
 * The sketch draws "Active" and an invite state; the invite state has no route
 * (see the screen's own note) and these four are what `Assignment` can actually
 * be. **`revokedAt` is checked before `active`**: an assignment revoked a minute
 * ago and one that ran out on its own are different facts about the same row,
 * and only the first is something a person did.
 */
export function assignmentStatusView(assignment: Assignment): AssignmentStatusView {
  if (assignment.revokedAt) {
    return { tone: 'error', labelKey: 'fleet.drivers.status.revoked' };
  }
  if (assignment.active) {
    return { tone: 'success', labelKey: 'fleet.drivers.status.active' };
  }
  // Not active, never revoked: either the window has closed or it has not opened.
  // `active` still comes from the database — this only *labels* a row the server
  // already said was inactive, and the worst a clock a second out can do is call
  // an assignment starting now "Scheduled" for that second.
  if (Date.parse(assignment.from) > Date.now()) {
    return { tone: 'info', labelKey: 'fleet.drivers.status.scheduled' };
  }
  return { tone: 'neutral', labelKey: 'fleet.drivers.status.expired' };
}

/** Active first, then most recently started — the order the contract answers in. */
export function byActiveThenRecent(a: Assignment, b: Assignment): number {
  if (a.active !== b.active) return a.active ? -1 : 1;
  return Date.parse(b.from) - Date.parse(a.from);
}
