import { DailyFeeForm } from '@/components/config/DailyFeeForm';
import { vehicleTypeLabels } from '@/components/config/model';
import { configTabs } from '@/components/config/tabs';
import { ScreenTabs } from '@/components/ScreenTabs';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-007 · the Daily-fee tab** — `PUT /v1/admin/fees/rates`, answered by
 * subscription-svc (US-14.4).
 *
 * This is also where **subscription pricing** is set: `DailyFeeRate` is keyed on
 * (vehicle type, mode), and the Mode B rung is the per-vehicle monthly platform
 * fee the wireframe puts at "≈ Rs 300/mo, first month free". The passenger-facing
 * Mode B fare is **not** here and is not the platform's to set — it is fleet-set
 * per subscriber in the Fleet Portal (SCR-FP-011, Epic 23).
 *
 * No audit row is written for this write; see `DailyFeeForm` and the C108 handoff.
 */

export const dynamic = 'force-dynamic';

export default async function DailyFeeRatesPage() {
  const [t, locale, session] = await Promise.all([getTranslator(), getLocale(), getSession()]);
  const tabs = configTabs(session?.menu ?? [], 'fees');

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

      <DailyFeeForm
        labels={{
          heading: t('admin.config.fees.heading'),
          noReadNote: t('admin.config.fees.noReadNote'),
          vehicle: t('admin.config.column.vehicle'),
          mode: t('admin.config.fees.mode'),
          modeA: t('admin.config.fees.modeA'),
          modeB: t('admin.config.fees.modeB'),
          modeC: t('admin.config.fees.modeC'),
          amount: t('admin.config.fees.amount'),
          amountHint: t('admin.config.fees.amountHint'),
          submit: t('admin.config.fees.submit'),
          working: t('admin.config.working'),
          audit: t('admin.audit.notRecorded', { service: 'subscription-svc' }),
          saved: t('admin.config.fees.saved'),
          vehicleTypes: vehicleTypeLabels({ t, locale }),
        }}
      />

      <p className="rounded-card border border-outline bg-background p-sm text-caption text-on-surface-variant shadow-card">
        {t('admin.config.fees.modeBNote')}
      </p>
    </div>
  );
}
