import Link from 'next/link';
import { redirect } from 'next/navigation';

import { StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import { MEMBERS, type FleetMember, type FleetMemberList } from '@/api/org';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { ConsoleLanguage } from '@/components/org/ConsoleLanguage';
import { OrgRegisterForm } from '@/components/org/OrgRegisterForm';
import { TeamPanel } from '@/components/org/TeamPanel';
import { teamLabels, teamRoleOptions, teamRows } from '@/components/org/team-model';
import { ProblemPanel } from '@/components/ProblemPanel';
import { LOCALES } from '@/i18n';
import { getLocale, getTranslator } from '@/i18n/server';
import { canManageTeam, canMutate, permittedScreens } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-002 · `fleet_org_setup`** — the organisation, its KYC standing, its
 * team and the link to where its money goes (US-13.A5/A7, US-27.1).
 *
 * ## It is two screens, because the organisation is a thing that starts existing
 *
 * `web_fleet.html` draws the state *after* registration, which is the state this
 * screen spends its life in. Before it there is a signed-in `fleet_owner` with no
 * membership row — the one session for which `./access` opens exactly one screen
 * — and this is it. `POST /v1/fleets` is the only fleet route with no `{fleetId}`
 * precisely because it is the call that creates the thing the others are scoped
 * to.
 *
 * ## The profile is read back, not edited
 *
 * `fleet.yaml` has **no route that updates an organisation**: `POST /v1/fleets`
 * creates, `GET /v1/fleets/{id}` reads, and there is no `PUT`. So the fields the
 * wireframe draws as inputs are rendered as what they are — the KYC record a
 * Verification Officer is reading — with the honest sentence about how to change
 * them. Drawing an editable form over a route that does not exist would be a
 * screen that loses an operator's correction. Raised in the C112 handoff.
 *
 * ## And the KYC dropzone has no route either
 *
 * The wireframe's "⬆ Upload KYC documents (BR, owner ID)" has nothing behind it:
 * fleet-svc's only document route is `POST …/payout-profile/documents`, whose
 * three kinds are the AL-49 bank evidence, and `registry.documents`' fleet kinds
 * are AL-50's four **per-vehicle** slots. There is no org-level document kind on
 * any contract. Same treatment, same reason, same handoff.
 */

export const dynamic = 'force-dynamic';

export default async function OrgSetupPage() {
  const session = await getSession();
  if (!session) redirect('/login');

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);

  /* -- Before there is an organisation ------------------------------------ */

  if (!session.organisation) {
    return canMutate(session, 'fleet-operations', { allowsNoOrganisation: true }) ? (
      <div className="mx-auto flex w-full max-w-[720px] flex-col gap-md">
        <OrgRegisterForm
          labels={{
            heading: t('fleet.org.register.heading'),
            body: t('fleet.org.register.body'),
            name: t('fleet.org.field.name'),
            registrationNo: t('fleet.org.field.registrationNo'),
            registrationHint: t('fleet.org.hint.registrationNo'),
            contactPhone: t('fleet.org.field.contactPhone'),
            contactPhoneHint: t('fleet.org.hint.contactPhone'),
            contactEmail: t('fleet.org.field.contactEmail'),
            address: t('fleet.org.field.address'),
            optional: t('fleet.org.optional'),
            required: t('fleet.org.required'),
            gate: t('fleet.org.register.gate'),
            submit: t('fleet.org.register.submit'),
            submitting: t('fleet.org.register.submitting'),
          }}
        />
      </div>
    ) : (
      <Card>
        <h2 className="text-subtitle font-semibold">{t('fleet.org.none.title')}</h2>
        <p className="text-body-sm text-on-surface-variant">{t('fleet.org.none.body')}</p>
      </Card>
    );
  }

  /* -- After ---------------------------------------------------------------- */

  const organisation = session.organisation;

  let members: readonly FleetMember[] = [];
  let problem: ProblemDetails | null = null;
  try {
    const answer = await read<FleetMemberList>({ org: MEMBERS });
    members = answer.items ?? [];
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const payoutOpen = permittedScreens(session).some((screen) => screen.key === 'payout');
  const dates = new Intl.DateTimeFormat(`${locale}-LK`, { dateStyle: 'medium' });

  return (
    <div className="flex flex-col gap-md lg:flex-row lg:items-start">
      {/* `web_fleet.html`: "Org profile & KYC" on the left, team on the right. */}
      <div className="flex min-w-0 flex-1 flex-col gap-md">
        <Card>
          <div className="flex flex-wrap items-center gap-xs">
            <h2 className="flex-1 text-subtitle font-semibold">{t('fleet.org.profile.heading')}</h2>
            <StatusPill tone={STATUS_TONES[organisation.status]}>
              {t(STATUS_KEYS[organisation.status])}
            </StatusPill>
          </div>

          <dl className="flex flex-col gap-xs">
            <Detail label={t('fleet.org.field.name')} value={organisation.name} />
            <Detail
              label={t('fleet.org.field.registrationNo')}
              value={organisation.registrationNo}
            />
            <Detail label={t('fleet.org.field.contactPhone')} value={organisation.contactPhone} />
            <Detail label={t('fleet.org.field.contactEmail')} value={organisation.contactEmail} />
            <Detail label={t('fleet.org.field.address')} value={organisation.address} />
            <Detail
              label={t('fleet.org.field.registered')}
              value={
                organisation.createdAt ? dates.format(new Date(organisation.createdAt)) : undefined
              }
            />
          </dl>

          <ConsoleLanguage
            legend={t('fleet.org.field.language')}
            note={t('fleet.org.language.note')}
            options={LOCALES.map((value) => ({
              value,
              label: t(`language.${value}` as const),
              current: value === locale,
            }))}
          />

          {/* No route edits an organisation, so the screen says how to. */}
          <p className="rounded-md bg-surface-variant px-sm py-xs text-caption text-on-surface-variant">
            {t('fleet.org.readOnly')}
          </p>
        </Card>

        <Card>
          <h2 className="text-subtitle font-semibold">{t('fleet.org.kyc.heading')}</h2>
          <p className="text-body-sm text-on-surface-variant">{t('fleet.org.kyc.gate')}</p>
          {/*
            The wireframe's dropzone, as the sentence it can honestly be: there is
            no org-level document route on any contract. The one document upload
            this organisation has is the payout evidence, and it is linked below
            because that is where AL-49 puts the officer's reading material.
          */}
          <p className="rounded-md border border-outline px-sm py-xs text-caption text-on-surface-variant">
            {t('fleet.org.kyc.unavailable')}
          </p>
        </Card>

        {/*
          "SCR-FP-002 adds a Bank & payout link/nav entry → SCR-FP-002a"
          (D2 Δ 2026-07-18, item 1 / US-27.1). Owner-only, so it is drawn from the
          same evaluation the sidebar is — a Manager sees neither.
        */}
        {payoutOpen ? (
          <Link
            href="/org/payout"
            className="flex flex-col gap-xxs rounded-card border border-outline bg-background p-md shadow-card hover:bg-surface-variant"
          >
            <span className="text-subtitle font-semibold text-primary">
              {t('fleet.org.payout.link')}
            </span>
            <span className="text-body-sm text-on-surface-variant">
              {t('fleet.org.payout.linkBody')}
            </span>
          </Link>
        ) : null}
      </div>

      <div className="flex w-full flex-col gap-md lg:w-[320px] lg:shrink-0">
        {problem ? <ProblemPanel problem={problem} /> : null}
        <TeamPanel
          members={teamRows(members, session, t)}
          roles={teamRoleOptions(t)}
          canInvite={canManageTeam(session)}
          labels={teamLabels(t)}
        />
      </div>
    </div>
  );
}

/* ------------------------------------------------------------------------- */

const STATUS_KEYS = {
  PENDING: 'fleet.status.pending',
  APPROVED: 'fleet.status.approved',
  REJECTED: 'fleet.status.rejected',
} as const;

const STATUS_TONES = {
  PENDING: 'warning',
  APPROVED: 'success',
  REJECTED: 'error',
} as const;

function Card({ children }: { children: React.ReactNode }) {
  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      {children}
    </section>
  );
}

/**
 * One KYC row. An absent optional field renders an em dash rather than being
 * dropped: "there is no contact email on this organisation" is a fact an owner
 * looking at their own verification record needs to see.
 */
function Detail({ label, value }: { label: string; value?: string }) {
  return (
    <div className="flex flex-col gap-px border-b border-outline pb-xs last:border-b-0 last:pb-0">
      <dt className="text-label text-on-surface-variant">{label}</dt>
      <dd className="text-body break-words text-on-surface">{value ?? '—'}</dd>
    </div>
  );
}
