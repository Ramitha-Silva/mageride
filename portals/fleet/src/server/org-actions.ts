'use server';

import { revalidatePath } from 'next/cache';

import { mutate } from '@/api/client';
import {
  MEMBERS,
  REGISTER_FLEET_PATH,
  isInvitableFleetRole,
  isSriLankanMobile,
  normaliseMobile,
  type AddFleetMemberBody,
  type FleetMember,
  type RegisterFleetBody,
} from '@/api/org';
import { ProblemError } from '@/api/problem';
import type { FleetOrganisation } from '@/api/types';
import { getTranslator } from '@/i18n/server';

import { canManageTeam } from './access';
import { getSession } from './session';

/**
 * **SCR-FP-002's two writes** — register the organisation (US-13.A7), and
 * provision a Manager or Viewer sub-user (US-13.A5).
 *
 * There is no third. `fleet.yaml` has **no route that edits an organisation** and
 * none that removes or re-seats a member, so the screen renders the profile it
 * registered and says so; see the C112 handoff, which asks for `PUT /v1/fleets/{id}`
 * and `DELETE …/members/{id}`.
 *
 * Both actions resolve their failure to a sentence in the caller's own language
 * rather than returning a code. The alternative is shipping all three locale
 * tables to the browser so that a form can look up one line.
 */

export interface OrgActionState {
  /** The failure, already translated. */
  readonly message?: string;
  /** Which field to mark, when the failure is about one. */
  readonly field?: 'name' | 'registrationNo' | 'contactPhone' | 'contactEmail' | 'email' | 'fleetRole';
  /** Set on success, so the form can say what happened without a second read. */
  readonly done?: { readonly name: string; readonly role?: string };
}

function text(formData: FormData, name: string): string {
  return String(formData.get(name) ?? '').trim();
}

/* ---------------------------------------------------------------------------
 * Register the organisation
 * ------------------------------------------------------------------------ */

/**
 * `POST /v1/fleets` — the only mutation on this portal an account with **no
 * organisation** can make, and the only one that carries no `{fleetId}`.
 *
 * It is gated on the canonical `fleet_owner` role rather than on a sub-role,
 * because the sub-role model starts at the membership this call creates
 * (`FleetEndpoints`). `allowsNoOrganisation` is how the data layer is told that,
 * and it is set here and nowhere else.
 *
 * The organisation is created **`PENDING`** — US-13.A7's gate — and the shell's
 * banner, the topbar chip and `/pending` all read that status from the next
 * render's own `GET /v1/fleets/{id}`. So there is nothing to report back beyond
 * the name: revalidating the layout is what makes the console redraw itself
 * around an organisation that did not exist a moment ago.
 */
export async function registerOrganisation(
  _state: OrgActionState,
  formData: FormData,
): Promise<OrgActionState> {
  const t = await getTranslator();

  const name = text(formData, 'name');
  const registrationNo = text(formData, 'registrationNo');
  const contactPhone = normaliseMobile(text(formData, 'contactPhone'));
  const contactEmail = text(formData, 'contactEmail');
  const address = text(formData, 'address');

  if (!name) return { message: t('fleet.org.error.nameRequired'), field: 'name' };
  if (!registrationNo) {
    return { message: t('fleet.org.error.registrationRequired'), field: 'registrationNo' };
  }
  if (!isSriLankanMobile(contactPhone)) {
    return { message: t('fleet.org.error.phoneInvalid'), field: 'contactPhone' };
  }

  const body: RegisterFleetBody = {
    name,
    registrationNo,
    contactPhone,
    ...(contactEmail ? { contactEmail } : {}),
    ...(address ? { address } : {}),
  };

  try {
    await mutate<FleetOrganisation, RegisterFleetBody>({
      method: 'POST',
      path: REGISTER_FLEET_PATH,
      body,
      requires: { area: 'fleet-operations', allowsNoOrganisation: true },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    // `business-registration-exists` is the one refusal an operator can act on
    // without support: `ux_fleets_business_reg_active` admits one live
    // application per registration number, so somebody has already registered
    // this one — possibly them, in another account.
    if (error.code === 'business-registration-exists') {
      return { message: t('fleet.error.registrationExists'), field: 'registrationNo' };
    }
    return { message: t(error.messageKey) };
  }

  // The whole layout: the nav, the topbar chip and the standing banner are all
  // rendered from a session that has just gained a `fleetId` and a `PENDING`
  // organisation, and none of them is below this page.
  revalidatePath('/', 'layout');

  return { done: { name } };
}

/* ---------------------------------------------------------------------------
 * Invite a team member
 * ------------------------------------------------------------------------ */

/**
 * `POST /v1/fleets/{id}/members` — US-13.A5's Manager and Viewer sub-users.
 *
 * **Gated on `canManageTeam`, not on `canMutate` alone**, and this is the one
 * control on the portal where the two differ: fleet-svc declares
 * `RequireFleetSubRole(Owner)` on this route while the roster beside it is
 * Viewer, and URD §2.3 has no row that separates an Owner from a Manager outside
 * `fleet-billing`. The seat is read from fleet-svc's own declaration rather than
 * from a rule invented here — `./access` says the same in its comment, and the
 * C111 handoff records it as the place the two models do not line up.
 */
export async function inviteMember(
  _state: OrgActionState,
  formData: FormData,
): Promise<OrgActionState> {
  const t = await getTranslator();

  const email = text(formData, 'email');
  const name = text(formData, 'name');
  const fleetRole = text(formData, 'fleetRole');

  if (!email.includes('@')) return { message: t('fleet.org.error.emailInvalid'), field: 'email' };
  // Manager or Viewer. A third value cannot come from the picker, so reaching
  // this means the form was posted by something else — and fleet-svc would
  // refuse it anyway (`fleetRole must be 'manager' or 'viewer'`).
  if (!isInvitableFleetRole(fleetRole)) {
    return { message: t('fleet.team.error.roleRequired'), field: 'fleetRole' };
  }

  const session = await getSession();
  if (!session || !canManageTeam(session)) {
    return { message: t('fleet.team.error.ownerOnly') };
  }

  const body: AddFleetMemberBody = {
    email,
    fleetRole,
    ...(name ? { name } : {}),
  };

  try {
    await mutate<FleetMember, AddFleetMemberBody>({
      method: 'POST',
      org: MEMBERS,
      body,
      requires: { area: 'fleet-operations' },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    if (error.code === 'fleet-member-exists') {
      return { message: t('fleet.error.memberExists'), field: 'email' };
    }
    return { message: t(error.messageKey) };
  }

  revalidatePath('/org/team');
  revalidatePath('/org/setup');

  return { done: { name: email, role: fleetRole } };
}
