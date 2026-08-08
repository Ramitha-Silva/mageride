import { read } from '@/api/client';
import { VOUCHER_TIERS_PATH, type VoucherDiscountTier } from '@/api/config';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { voucherTierRows } from '@/components/config/model';
import { configTabs } from '@/components/config/tabs';
import { VoucherTierPanel } from '@/components/config/VoucherTierPanel';
import { ProblemPanel } from '@/components/ProblemPanel';
import { ScreenTabs } from '@/components/ScreenTabs';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-007 · the Commission & vouchers tab** — the bulk-voucher discount
 * ladder (US-9A.15, AL-01).
 *
 * The one configuration surface with a read beside its write, so this is the one
 * tab that shows the platform's actual values. `GET /v1/admin/voucher-discount-tiers`
 * and its `PUT` are both subscription-svc's; wallet-svc serves the same rows to the
 * Driver App at `GET /v1/wallet/voucher/discount-tiers`, which is why the DoD item
 * "changing a tier is reflected in the Driver App top-up screen" is one write to
 * one table rather than a synchronisation.
 *
 * Both spellings of the write are landed (`/v1/admin/voucher-discount-tiers` and
 * wallet-svc's `/v1/wallet/admin/voucher-discount-tiers`) and the C007 handoff asks
 * for one to be retired. This screen uses the subscription-svc pair, because that
 * is the one the gateway routes under `/v1/admin/**` and therefore the one this
 * console can reach without opening a second prefix (AL-02).
 */

export const dynamic = 'force-dynamic';

export default async function VoucherTiersPage() {
  const [t, locale, session] = await Promise.all([getTranslator(), getLocale(), getSession()]);
  const tabs = configTabs(session?.menu ?? [], 'vouchers');

  let tiers: readonly VoucherDiscountTier[] = [];
  let problem: ProblemDetails | null = null;

  try {
    const answer = await read<{ tiers: VoucherDiscountTier[] }>({ path: VOUCHER_TIERS_PATH });
    tiers = answer.tiers ?? [];
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  return (
    <div className="flex flex-col gap-md">
      <ScreenTabs
        navLabel={t('admin.config.tabs.label')}
        tabs={tabs.map((tab) => ({
          id: tab.id,
          href: tab.href,
          label: t(tab.labelKey),
          current: tab.current,
        }))}
      />

      {problem ? <ProblemPanel problem={problem} /> : null}

      <VoucherTierPanel
        rows={voucherTierRows(tiers, { t, locale })}
        ladder={tiers}
        labels={{
          heading: t('admin.config.vouchers.heading'),
          note: t('admin.config.vouchers.note'),
          caption: t('admin.config.vouchers.caption'),
          denomination: t('admin.config.vouchers.denomination'),
          percent: t('admin.config.vouchers.percent'),
          pays: t('admin.config.vouchers.pays'),
          credit: t('admin.config.vouchers.credit'),
          active: t('admin.config.vouchers.active'),
          activeYes: t('admin.config.vouchers.activeYes'),
          activeNo: t('admin.config.vouchers.activeNo'),
          empty: t('admin.config.vouchers.empty'),
          editHeading: t('admin.config.vouchers.editHeading'),
          denominationHint: t('admin.config.vouchers.denominationHint'),
          percentHint: t('admin.config.vouchers.percentHint'),
          activeLabel: t('admin.config.vouchers.activeLabel'),
          submit: t('admin.config.vouchers.submit'),
          working: t('admin.config.working'),
          audit: t('admin.audit.notRecorded', { service: 'subscription-svc' }),
          saved: t('admin.config.vouchers.saved'),
        }}
      />
    </div>
  );
}
