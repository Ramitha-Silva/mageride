import type { FleetRole } from './types';

/**
 * SCR-FP-002's wire shapes and the two facts about the organisation surface that
 * are not derivable from them — transcribed from `backend/contracts/fleet.yaml`.
 *
 * Nothing here calls anything. The org-relative targets are the strings a screen
 * hands `read()`/`mutate()`, which is where the caller's own `fleetId` is written
 * into a URL and the only place it ever is (`./client`).
 */

/* ---------------------------------------------------------------------------
 * Targets
 * ------------------------------------------------------------------------ */

/**
 * `POST /v1/fleets` — the one fleet route with **no `{fleetId}`**, because it is
 * the call that creates the organisation the rest are scoped to. It is therefore
 * the one absolute path a screen passes as `{ path: … }` rather than `{ org: … }`.
 */
export const REGISTER_FLEET_PATH = '/v1/fleets';

/** `GET`/`POST /v1/fleets/{own id}/members` — the roster and the invite. */
export const MEMBERS = '/members';

/* ---------------------------------------------------------------------------
 * Shapes
 * ------------------------------------------------------------------------ */

/**
 * `fleet.yaml#/components/schemas/FleetMember`.
 *
 * **No phone number, deliberately** — `iam.fleet_members_fleet` does not project
 * one, and the contract says why: "a sub-user's mobile is their own, and an org's
 * team list is not a reason to hand it over". SCR-FP-002's team card shows the
 * address the seat was granted to, which is also the address they sign in with.
 */
export interface FleetMember {
  /** The person's `iam.users.id` — the membership has no surrogate key. */
  readonly memberId: string;
  readonly email?: string;
  readonly name?: string;
  readonly fleetRole: FleetRole;
}

export interface FleetMemberList {
  readonly items: readonly FleetMember[];
}

/** The body `POST /v1/fleets` takes. `name`, `registrationNo` and `contactPhone` are required. */
export interface RegisterFleetBody {
  readonly name: string;
  readonly registrationNo: string;
  readonly contactPhone: string;
  readonly contactEmail?: string;
  readonly address?: string;
}

/** The body `POST /v1/fleets/{id}/members` takes. */
export interface AddFleetMemberBody {
  readonly email: string;
  readonly name?: string;
  readonly fleetRole: InvitableFleetRole;
}

/* ---------------------------------------------------------------------------
 * The two rules the shapes do not carry
 * ------------------------------------------------------------------------ */

/**
 * The seats an invite may grant: **Manager and Viewer, never Owner**.
 *
 * `FleetRole` has three values and `addFleetMember` admits two of them —
 * `FleetEndpoints` refuses anything else with a `validation-failed` on
 * `fleetRole`, and says why: US-13.A5 gives the Fleet Owner "team members for the
 * Manager and Viewer roles", and a second Owner is a change of *who the
 * organisation belongs to*, which `registry.fleets.owner_id` records and this
 * route does not rewrite.
 *
 * So the picker offers two options rather than three and greys nothing out. A
 * disabled "Owner" would be a control implying the transfer exists somewhere on
 * this screen, and it does not exist anywhere on the platform.
 */
export const INVITABLE_FLEET_ROLES = ['manager', 'viewer'] as const;

export type InvitableFleetRole = (typeof INVITABLE_FLEET_ROLES)[number];

export function isInvitableFleetRole(value: string): value is InvitableFleetRole {
  return (INVITABLE_FLEET_ROLES as readonly string[]).includes(value);
}

/**
 * `_shared.yaml#/components/schemas/PhoneE164` — `^\+947\d{8}$`.
 *
 * Checked here as well as by fleet-svc so a mistyped number is marked on the
 * field the operator is looking at rather than returned as a `validation-failed`
 * whose `errors` map they never see. The server still decides; this only decides
 * *where the message goes*.
 */
const PHONE_E164 = /^\+947\d{8}$/;

export function isSriLankanMobile(value: string): boolean {
  return PHONE_E164.test(value);
}

/**
 * Accepts the two forms a Sri Lankan operator actually types — `0771234567` and
 * `+94771234567` — and produces the one the contract admits.
 *
 * Not validation: a value this cannot normalise is returned unchanged so that
 * {@link isSriLankanMobile} refuses it and the field says so. Silently "fixing"
 * an unrecognisable number would send fleet-svc a phone nobody entered.
 */
export function normaliseMobile(value: string): string {
  const digits = value.replaceAll(/[\s()-]/g, '');
  if (/^0\d{9}$/.test(digits)) return `+94${digits.slice(1)}`;
  if (/^94\d{9}$/.test(digits)) return `+${digits}`;
  return digits;
}
