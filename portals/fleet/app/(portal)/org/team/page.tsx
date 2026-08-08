import Link from 'next/link';
import { redirect } from 'next/navigation';

import { read } from '@/api/client';
import { MEMBERS, type FleetMember, type FleetMemberList } from '@/api/org';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { TeamPanel } from '@/components/org/TeamPanel';
import { teamLabels, teamRoleOptions, teamRows } from '@/components/org/team-model';
import { ProblemPanel } from '@/components/ProblemPanel';
import { getTranslator } from '@/i18n/server';
import { canManageTeam } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-002 · Team** — the roster and the invite, at the nav entry
 * `web_fleet.html` gives them (US-13.A5).
 *
 * The wireframe draws the team twice: as a card beside the org profile and as
 * its own sidebar entry. Both are {@link TeamPanel}; this route is the full-width
 * one, and the difference is the space, not the content — an organisation with
 * forty sub-users has a roster the setup screen's 320px column cannot hold.
 *
 * ## The roster is everybody's and the invite is the Owner's
 *
 * `GET …/members` is `RequireFleetSubRole(Viewer)`, which is why the nav entry is
 * Viewer-gated; `POST …/members` is `RequireFleetSubRole(Owner)`, which is why
 * `canManageTeam()` decides whether the form is drawn at all. A Manager reads the
 * team and is told, in one sentence, why they cannot add to it.
 */

export const dynamic = 'force-dynamic';

export default async function TeamPage() {
  const session = await getSession();
  if (!session) redirect('/login');

  const t = await getTranslator();

  // An account with no organisation has no team; `./access` sends it to the
  // screen that creates one, and this is the belt on that strap.
  if (!session.organisation) redirect('/org/setup');

  let members: readonly FleetMember[] = [];
  let problem: ProblemDetails | null = null;
  try {
    const answer = await read<FleetMemberList>({ org: MEMBERS });
    members = answer.items ?? [];
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  return (
    <div className="mx-auto flex w-full max-w-[720px] flex-col gap-md">
      {problem ? <ProblemPanel problem={problem} /> : null}

      <TeamPanel
        members={teamRows(members, session, t)}
        roles={teamRoleOptions(t)}
        canInvite={canManageTeam(session)}
        labels={teamLabels(t)}
      />

      <Link
        href="/org/setup"
        className="self-start text-body-sm text-primary underline underline-offset-2"
      >
        {t('fleet.team.backToOrg')}
      </Link>
    </div>
  );
}
