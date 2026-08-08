'use client';

import { useActionState } from 'react';

import {
  Button,
  Field,
  Input,
  Select,
  StatusPill,
  TBody,
  TD,
  TH,
  THead,
  TR,
  Table,
  TableEmpty,
  type StatusTone,
} from '@mageride/ui';

import { inviteMember, type OrgActionState } from '@/server/org-actions';

/**
 * **SCR-FP-002's team card** (US-13.A5) — the roster, and the Owner's invite.
 *
 * `web_fleet.html` draws it twice: as the right-hand card of the Organisation
 * screen and as its own *Team* nav entry. Both are this component; the compact
 * form is the card beside the profile, and the full one is the screen.
 *
 * ## Two gates, and they are not the same gate
 *
 * `GET …/members` is `RequireFleetSubRole(Viewer)` and `POST …/members` is
 * `RequireFleetSubRole(Owner)`, so **the roster is everybody's and the invite is
 * the Owner's**. `canInvite` is `canManageTeam()`'s answer, computed on the
 * server — the one control on this portal gated on the seat as well as on the
 * URD §2.3 row, because no row in the matrix separates an Owner from a Manager
 * outside `fleet-billing`.
 *
 * ## The seat picker offers two values, not three greyed to two
 *
 * `addFleetMember` admits `manager` and `viewer`. A second Owner is a change of
 * who the organisation belongs to — `registry.fleets.owner_id`, which no route
 * rewrites — so "Owner" is not a disabled option here; it is not an option
 * anywhere, and a greyed one would imply the transfer exists on some other
 * screen.
 */

export interface TeamMemberView {
  readonly key: string;
  /** Name, or the address the seat was granted to. There is no phone (see `@/api/org`). */
  readonly who: string;
  /** The translated sub-role. */
  readonly role: string;
  readonly tone: StatusTone;
  /** Whether this row is the signed-in member. */
  readonly isSelf: boolean;
}

export interface TeamRoleOption {
  readonly value: string;
  readonly label: string;
}

export interface TeamLabels {
  readonly heading: string;
  readonly caption: string;
  readonly member: string;
  readonly role: string;
  readonly you: string;
  readonly empty: string;
  readonly inviteHeading: string;
  readonly email: string;
  readonly name: string;
  readonly nameHint: string;
  readonly roleField: string;
  readonly submit: string;
  readonly submitting: string;
  readonly ownerOnly: string;
  readonly noInvitationEmail: string;
  readonly noOwnerSeat: string;
  readonly invited: string;
}

const INITIAL: OrgActionState = {};

export function TeamPanel({
  members,
  roles,
  canInvite,
  labels,
}: {
  members: readonly TeamMemberView[];
  roles: readonly TeamRoleOption[];
  canInvite: boolean;
  labels: TeamLabels;
}) {
  const [state, formAction, pending] = useActionState(inviteMember, INITIAL);

  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.member}</TH>
            <TH>{labels.role}</TH>
          </TR>
        </THead>
        <TBody>
          {members.length === 0 ? (
            <TableEmpty colSpan={2}>{labels.empty}</TableEmpty>
          ) : (
            members.map((member) => (
              <TR key={member.key}>
                <TD>
                  <span className="break-all">{member.who}</span>
                  {member.isSelf ? (
                    <span className="ps-xs text-caption text-on-surface-variant">
                      {labels.you}
                    </span>
                  ) : null}
                </TD>
                <TD>
                  <StatusPill tone={member.tone} dot={false}>
                    {member.role}
                  </StatusPill>
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>

      {canInvite ? (
        <form action={formAction} className="flex flex-col gap-sm border-t border-outline pt-sm">
          <h3 className="text-body-sm font-semibold">{labels.inviteHeading}</h3>

          <Field
            label={labels.email}
            {...(state.field === 'email' && state.message ? { error: state.message } : {})}
          >
            <Input
              name="email"
              type="email"
              autoComplete="off"
              autoCapitalize="none"
              spellCheck={false}
              required
            />
          </Field>

          <div className="flex flex-col gap-sm md:flex-row">
            <Field label={labels.name} hint={labels.nameHint} className="flex-1">
              <Input name="name" maxLength={200} />
            </Field>

            <Field
              label={labels.roleField}
              className="flex-1"
              {...(state.field === 'fleetRole' && state.message ? { error: state.message } : {})}
            >
              <Select name="fleetRole" defaultValue={roles[0]?.value} required>
                {roles.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </Select>
            </Field>
          </div>

          <p className="text-caption text-on-surface-variant">{labels.noOwnerSeat}</p>

          {state.message && !state.field ? (
            <p role="alert" className="text-body-sm text-error">
              {state.message}
            </p>
          ) : null}

          {state.done ? (
            <p role="status" className="text-body-sm text-success">
              {labels.invited}
            </p>
          ) : null}

          {/*
            fleet-svc provisions the seat and **tells nobody**: AL-07's three
            sign-in methods are iam-svc's, and no fleet-org invitation template
            exists in `content.notification_templates`. An owner who is not told
            that would wait for a mail that never arrives and conclude the invite
            failed. Raised in the C112 handoff.
          */}
          <p className="rounded-md bg-surface-variant px-sm py-xs text-caption text-on-surface-variant">
            {labels.noInvitationEmail}
          </p>

          <Button
            type="submit"
            size="compact"
            busy={pending}
            busyLabel={labels.submitting}
            className="self-start"
          >
            {labels.submit}
          </Button>
        </form>
      ) : (
        <p className="text-caption text-on-surface-variant">{labels.ownerOnly}</p>
      )}
    </section>
  );
}
