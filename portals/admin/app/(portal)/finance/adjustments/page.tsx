import { isAdminId } from '@/api/finance';
import { financeTabs } from '@/components/finance/tabs';
import { ReverseFeeCard } from '@/components/finance/ReverseFeeCard';
import { ScreenTabs } from '@/components/ScreenTabs';
import { getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-006 · the Wallet-reversal tab** — US-14.11's compensating credit.
 *
 * ## Why it is its own screen and not a card on the refunds page
 *
 * `AdminMenu.cs` gives it its own item (`wallet-adjustments`) gated on **Driver
 * wallet adjustments · Write**, whose URD §2.3 row is `➖ ➖ ➖ 👁 ✅ ➖ ➖ ✅ 👁`
 * — Super Admin and Finance write it, Admin and Auditor may only look, everybody
 * else has nothing. The refund queue next door is a different row with a different
 * shape (`◐ raise/recommend` for the CSR), so one screen carrying both controls
 * would have to be gated on the looser of two rows and hide half of itself. The
 * component's DoD item — "a wallet reversal requires the Finance or Super Admin
 * role" — is therefore satisfied by the route existing at that nav item, not by any
 * check written here.
 *
 * ## The screen is a form and nothing else
 *
 * There is no route that lists reversals: they are `billing.journal_entries` rows
 * of kind `adjustment`, and what shows them is the wallet ledger next door, which
 * this page links to rather than re-reading. A second listing here would be a
 * second answer to "what adjustments were posted", drawn from a query nobody else
 * uses.
 */

export const dynamic = 'force-dynamic';

export default async function WalletAdjustmentsPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const requested = params.driverId;
  const driverId = Array.isArray(requested) ? requested[0] : requested;

  const [t, session] = await Promise.all([getTranslator(), getSession()]);
  const tabs = financeTabs(session?.menu ?? [], 'reversals');

  return (
    <div className="flex flex-col gap-md">
      <ScreenTabs
        navLabel={t('admin.finance.tabs.label')}
        tabs={tabs.map((tab) => ({
          id: tab.id,
          href: tab.href,
          label: t(tab.labelKey),
          current: tab.current,
        }))}
      />

      <ReverseFeeCard
        // Aimed by the URL, so a driver's record can send an operator here with the
        // subject already in the box — the same shape SCR-AP-004's suspend card
        // uses, and for the same reason: the address bar says who is about to be
        // credited before anything is pressed.
        driverId={isAdminId(driverId) ? driverId : ''}
        labels={{
          heading: t('admin.finance.reversal.heading'),
          note: t('admin.finance.reversal.note'),
          driver: t('admin.finance.reversal.driver'),
          driverHint: t('admin.finance.reversal.driverHint'),
          vehicle: t('admin.finance.reversal.vehicle'),
          vehicleHint: t('admin.finance.reversal.vehicleHint'),
          feeDate: t('admin.finance.reversal.feeDate'),
          feeDateHint: t('admin.finance.reversal.feeDateHint'),
          amount: t('admin.finance.reversal.amount'),
          amountHint: t('admin.finance.reversal.amountHint'),
          reason: t('admin.finance.reversal.reason'),
          reasonHint: t('admin.finance.reversal.reasonHint'),
          submit: t('admin.finance.reversal.submit'),
          working: t('admin.finance.reversal.working'),
          audit: t('admin.audit.notice'),
          done: t('admin.finance.reversal.done'),
          replayed: t('admin.finance.reversal.replayed'),
          balanceAfter: t('admin.finance.reversal.balanceAfter'),
          recorded: t('admin.audit.recorded', { action: 'WALLET_FEE_REVERSED' }),
        }}
      />
    </div>
  );
}
