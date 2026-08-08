import { StatusPill, TBody, TD, TH, TR, Table } from '@mageride/ui';

import type { OrgKyc } from '@/api/verification';

import type { PillView } from './model';

/**
 * SCR-AP-003c's **Organisation KYC** card: what US-13.A7 asks an officer to read
 * before an organisation's Fleet Portal is unlocked, and the AL-49 bank details
 * approving it will verify.
 *
 * **The two blocks are separate because the decisions are.** `kycStatus` is
 * whether there is evidence to read at all; the payout profile has its own state,
 * its own rejection reason and its own effect — approving here is what sets
 * `payout_profiles.status = 'verified'`, which is what unlocks Paid classification
 * (BR-31.1). An officer refusing a bank statement and an officer refusing an
 * organisation are doing different things, and a card that ran the two together
 * would hide which one their click did.
 *
 * A missing value renders as `—` rather than as an empty row: the row is the
 * question, and its absence would read as one nobody asked.
 */

export interface OrgKycLabels {
  readonly heading: string;
  readonly caption: string;
  readonly registeredName: string;
  readonly registrationNo: string;
  readonly contactPhone: string;
  readonly contactEmail: string;
  readonly address: string;
  readonly rejectionReason: string;
  readonly payoutHeading: string;
  readonly payoutCaption: string;
  readonly payoutNone: string;
  readonly bank: string;
  readonly branch: string;
  readonly accountNo: string;
  readonly accountHolder: string;
  readonly payoutRejection: string;
  readonly payoutGate: string;
}

function Row({ label, value }: { label: string; value: string | null | undefined }) {
  return (
    <TR>
      <TH scope="row" className="w-[45%] align-top">
        {label}
      </TH>
      <TD className="break-words">{value || '—'}</TD>
    </TR>
  );
}

export function OrgKycCard({
  kyc,
  payoutStatus,
  labels,
}: {
  kyc: OrgKyc;
  /** The pill for `payoutProfileStatus`, already toned and translated. */
  payoutStatus: PillView | null;
  labels: OrgKycLabels;
}) {
  const payout = kyc.payoutProfile;

  return (
    <section className="flex min-w-0 flex-1 flex-col gap-sm rounded-card border border-outline bg-background p-sm shadow-card">
      <h2 className="text-subtitle font-semibold text-on-surface">{labels.heading}</h2>

      <Table caption={labels.caption}>
        <TBody>
          <Row label={labels.registeredName} value={kyc.name} />
          <Row label={labels.registrationNo} value={kyc.registrationNo} />
          <Row label={labels.contactPhone} value={kyc.contactPhone} />
          <Row label={labels.contactEmail} value={kyc.contactEmail} />
          <Row label={labels.address} value={kyc.address} />
          {kyc.rejectionReason ? (
            <Row label={labels.rejectionReason} value={kyc.rejectionReason} />
          ) : null}
        </TBody>
      </Table>

      <div className="flex flex-wrap items-center gap-xs">
        <h3 className="text-subtitle font-semibold text-on-surface">{labels.payoutHeading}</h3>
        {payoutStatus ? (
          <StatusPill tone={payoutStatus.tone}>{payoutStatus.label}</StatusPill>
        ) : null}
      </div>

      {payout ? (
        <>
          <Table caption={labels.payoutCaption}>
            <TBody>
              <Row label={labels.bank} value={payout.bank} />
              <Row label={labels.branch} value={payout.branch} />
              <Row label={labels.accountNo} value={payout.accountNo} />
              <Row label={labels.accountHolder} value={payout.accountHolderName} />
              {payout.rejectionReason ? (
                <Row label={labels.payoutRejection} value={payout.rejectionReason} />
              ) : null}
            </TBody>
          </Table>
          <p className="text-caption text-on-surface-variant">{labels.payoutGate}</p>
        </>
      ) : (
        <p className="text-body-sm text-on-surface-variant">{labels.payoutNone}</p>
      )}
    </section>
  );
}
