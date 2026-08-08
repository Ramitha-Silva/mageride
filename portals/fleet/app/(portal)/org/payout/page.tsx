import Link from 'next/link';
import { forbidden, redirect } from 'next/navigation';

import { StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import {
  LANKAQR_ACCEPT,
  LICENSED_BANKS,
  PAID_SERVICE_PAYMENT_BLOCKED_KEY,
  PAYOUT_PROFILE,
  PROOF_ACCEPT,
  canSetPaidServicePayment,
  payoutStatusView,
  type PayoutProfile,
} from '@/api/payout';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { PayoutDocumentUpload } from '@/components/payout/PayoutDocumentUpload';
import { PayoutProfileForm } from '@/components/payout/PayoutProfileForm';
import { ProblemPanel } from '@/components/ProblemPanel';
import { getLocale, getTranslator } from '@/i18n/server';
import { canMutate, satisfiesFleetRole } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-002a · `fleet_bank_payout`** — the organisation's bank & payout
 * profile (AL-49, US-27.1/27.2). **Owner only.**
 *
 * ## Three gates, and this page is the third
 *
 * `proxy.ts` refuses the URL for anybody whose seat does not carry it — the
 * manifest entry is `area: 'fleet-billing'`, `needs: 'write'`,
 * `minimumFleetRole: 'owner'`, all three transcribed off fleet-svc's own
 * `RequireFleetSubRole(FleetRoles.Owner)` on the three payout routes. fleet-svc
 * refuses every request behind it for the same reason, re-reading the seat from
 * `iam.fleet_members` each time. And this page checks again, because a proxy
 * matcher that stopped covering a path would otherwise turn a 403 into a rendered
 * screen — `forbidden()` is the component's DoD item "a Manager cannot open
 * SCR-FP-002a by direct URL" held one layer lower than the thing that normally
 * holds it.
 *
 * ## A missing profile is a state, not a failure
 *
 * `GET …/payout-profile` answers **`404 payout-profile-not-found`** when the
 * organisation has never submitted one, which is where every organisation starts.
 * That is the empty form, not a problem panel — and it is also why the document
 * slots are disabled until the bank details exist: fleet-svc attaches a document
 * to a *profile*, and says so.
 *
 * ## The screen is not approval-gated, deliberately
 *
 * These documents are part of what the Verification Officer reads **before**
 * approving the organisation, so a PENDING org's owner reaches this screen and
 * `requiresApprovedOrg` is false on the entry and on both writes. Gating it would
 * mean approving an organisation before seeing the evidence you approve it on.
 */

export const dynamic = 'force-dynamic';

export default async function PayoutProfilePage() {
  const session = await getSession();
  if (!session) redirect('/login');

  // The third gate. `canMutate` reads the caller's own evaluated `fleet-billing`
  // row — which URD §2.1 leaves to the Owner alone — and the sub-role check is
  // the route's own declaration, not a rule invented here.
  if (!canMutate(session, 'fleet-billing') || !satisfiesFleetRole(session.fleetRole, 'owner')) {
    forbidden();
  }

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);

  let profile: PayoutProfile | null = null;
  let problem: ProblemDetails | null = null;
  try {
    profile = await read<PayoutProfile>({ org: PAYOUT_PROFILE });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    // "This organisation has not submitted a bank and payout profile yet" is the
    // starting state of every organisation, so it renders the form rather than a
    // failure. Anything else is a real failure and is shown as one.
    if (error.code !== 'payout-profile-not-found') problem = error.problem;
  }

  const status = payoutStatusView(profile);
  const paidAvailable = canSetPaidServicePayment(profile);
  const dates = new Intl.DateTimeFormat(`${locale}-LK`, { dateStyle: 'medium' });

  return (
    <div className="flex flex-col gap-md">
      {/*
        `web_fleet.html` puts the profile's own chip in the topbar. The shell's
        topbar chip is the *organisation's* verification state and is drawn on
        every screen, so this one lives at the head of the screen it belongs to —
        two chips in one bar reporting two different verifications would be the
        worse reading of the sketch.
      */}
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-title font-display">{t('fleet.payout.title')}</h2>
        <StatusPill tone={status.tone}>{t(status.labelKey)}</StatusPill>
      </div>

      {problem ? <ProblemPanel problem={problem} /> : null}

      {profile?.status === 'rejected' && profile.rejectionReason ? (
        <p className="rounded-md border border-error/40 bg-error/10 px-sm py-xs text-body-sm">
          {t('fleet.payout.rejectedReason', { reason: profile.rejectionReason })}
        </p>
      ) : null}

      {profile?.status === 'verified' && profile.verifiedAt ? (
        <p className="text-body-sm text-on-surface-variant">
          {t('fleet.payout.verifiedOn', { date: dates.format(new Date(profile.verifiedAt)) })}
        </p>
      ) : null}

      {/*
        What is waiting on this profile — the wireframe's states line, on the
        screen rather than in a spec. The first item is BR-31.1's gate, and it is
        the same predicate and the same sentence C113's SCR-FP-004 puts beside
        the disabled "Paid" option (`@/api/payout`).
      */}
      <section
        className={`flex flex-col gap-xxs rounded-md border px-sm py-xs ${
          paidAvailable ? 'border-success/40 bg-success/10' : 'border-warning/40 bg-warning/10'
        }`}
      >
        <h3 className="text-body-sm font-semibold">{t('fleet.payout.gate.heading')}</h3>
        <p className="text-body-sm text-on-surface">
          {paidAvailable ? t('fleet.payout.gate.paidReady') : t(PAID_SERVICE_PAYMENT_BLOCKED_KEY)}
        </p>
        <p className="text-caption text-on-surface-variant">
          {paidAvailable ? t('fleet.payout.gate.paySheetReady') : t('fleet.payout.gate.billing')}
        </p>
      </section>

      <div className="flex flex-col gap-md lg:flex-row lg:items-start">
        <PayoutProfileForm
          profile={profile}
          banks={LICENSED_BANKS}
          labels={{
            heading: t('fleet.payout.heading'),
            bank: t('fleet.payout.field.bank'),
            bankPlaceholder: t('fleet.payout.field.bankPlaceholder'),
            branch: t('fleet.payout.field.branch'),
            accountNo: t('fleet.payout.field.accountNo'),
            accountHolderName: t('fleet.payout.field.holder'),
            holderHint: t('fleet.payout.holderHint'),
            required: t('fleet.org.required'),
            editWarning: t('fleet.payout.editWarning'),
            editVerifiedWarning: t('fleet.payout.editVerifiedWarning'),
            submit: t('fleet.payout.submit'),
            submitting: t('fleet.payout.submitting'),
            saved: t('fleet.payout.saved'),
          }}
        />

        <div className="flex w-full flex-col gap-md lg:w-[320px] lg:shrink-0">
          <PayoutDocumentUpload
            kinds={[
              { value: 'bank_statement', label: t('fleet.payout.kind.bankStatement') },
              { value: 'passbook_first_page', label: t('fleet.payout.kind.passbook') },
            ]}
            accept={PROOF_ACCEPT}
            present={Boolean(profile?.proofDocId)}
            disabled={profile === null}
            labels={{
              heading: t('fleet.payout.proof.heading'),
              kindLegend: t('fleet.payout.proof.which'),
              prompt: t('fleet.payout.proof.prompt'),
              hint: t('fleet.payout.proof.hint'),
              uploading: t('fleet.payout.doc.uploading'),
              uploaded: t('fleet.payout.doc.uploaded'),
              missing: t('fleet.payout.doc.missing'),
              rejectedType: t('fleet.error.fileNotAccepted'),
              rejectedSize: t('fleet.error.fileTooLarge', { megabytes: 8 }),
            }}
          />

          <PayoutDocumentUpload
            kinds={[{ value: 'lankaqr_code', label: t('fleet.payout.kind.lankaqr') }]}
            accept={LANKAQR_ACCEPT}
            present={Boolean(profile?.lankaqrDocId)}
            disabled={profile === null}
            labels={{
              heading: t('fleet.payout.qr.heading'),
              prompt: t('fleet.payout.qr.prompt'),
              hint: t('fleet.payout.qr.hint'),
              note: t('fleet.payout.qr.note'),
              uploading: t('fleet.payout.doc.uploading'),
              uploaded: t('fleet.payout.doc.uploaded'),
              missing: t('fleet.payout.doc.missing'),
              rejectedType: t('fleet.error.fileNotAccepted'),
              rejectedSize: t('fleet.error.fileTooLarge', { megabytes: 8 }),
            }}
          />

          {profile === null ? (
            <p className="rounded-md bg-surface-variant px-sm py-xs text-caption text-on-surface-variant">
              {t('fleet.payout.error.profileFirst')}
            </p>
          ) : null}
        </div>
      </div>

      <Link
        href="/org/setup"
        className="self-start text-body-sm text-primary underline underline-offset-2"
      >
        {t('fleet.payout.backToOrg')}
      </Link>
    </div>
  );
}
