import type { StatusTone } from '@mageride/ui';

import { INVITABLE_FLEET_ROLES, type FleetMember } from '@/api/org';
import type { FleetRole, FleetSession } from '@/api/types';
import type { FleetTranslator } from '@/i18n';

import type { TeamLabels, TeamMemberView, TeamRoleOption } from './TeamPanel';

/**
 * The team roster as SCR-FP-002 renders it — the translation and the ordering,
 * kept off the client component so `TeamPanel` receives strings and nothing else.
 *
 * Shared by the Organisation screen's team card and the Team screen, which are
 * the same panel at two sizes (`web_fleet.html` draws it in both places).
 */

const ROLE_KEYS = {
  owner: 'fleet.role.owner',
  manager: 'fleet.role.manager',
  viewer: 'fleet.role.viewer',
} as const;

/**
 * Owner is tinted and the other two are not — the wireframe's own emphasis
 * (`pill info` against `pill mut`), and it reads as an org chart rather than as a
 * ranking of people.
 */
const ROLE_TONES: Readonly<Record<FleetRole, StatusTone>> = {
  owner: 'info',
  manager: 'neutral',
  viewer: 'neutral',
};

export function teamRows(
  members: readonly FleetMember[],
  session: FleetSession,
  t: FleetTranslator,
): TeamMemberView[] {
  return members.map((member) => ({
    key: member.memberId,
    // The name when there is one, the address otherwise — and the address is
    // never dropped when both exist, because it is what the person signs in with
    // and what an owner types to invite them again.
    who: member.name ? `${member.name} · ${member.email ?? ''}`.replace(/ · $/, '') : (member.email ?? member.memberId),
    role: t(ROLE_KEYS[member.fleetRole]),
    tone: ROLE_TONES[member.fleetRole],
    isSelf: member.memberId === session.userId,
  }));
}

/** Manager and Viewer. Never Owner — see `@/api/org`. */
export function teamRoleOptions(t: FleetTranslator): TeamRoleOption[] {
  return INVITABLE_FLEET_ROLES.map((value) => ({ value, label: t(ROLE_KEYS[value]) }));
}

export function teamLabels(t: FleetTranslator): TeamLabels {
  return {
    heading: t('fleet.team.heading'),
    caption: t('fleet.team.caption'),
    member: t('fleet.team.column.member'),
    role: t('fleet.team.column.role'),
    you: t('fleet.team.you'),
    empty: t('fleet.team.empty'),
    inviteHeading: t('fleet.team.invite.heading'),
    email: t('fleet.team.invite.email'),
    name: t('fleet.team.invite.name'),
    nameHint: t('fleet.org.optional'),
    roleField: t('fleet.team.invite.role'),
    submit: t('fleet.team.invite.submit'),
    submitting: t('fleet.team.invite.submitting'),
    ownerOnly: t('fleet.team.invite.ownerOnlyNotice'),
    noInvitationEmail: t('fleet.team.invite.noEmail'),
    noOwnerSeat: t('fleet.team.invite.noOwnerSeat'),
    invited: t('fleet.team.invite.done'),
  };
}
