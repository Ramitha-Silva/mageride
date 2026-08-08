import { configTabs } from '@/components/config/tabs';
import { vehicleTypeLabels } from '@/components/config/model';
import { TariffForm } from '@/components/config/TariffForm';
import { ScreenTabs } from '@/components/ScreenTabs';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-007 · the Fare-tariffs tab** — `PUT /v1/admin/fares/tariffs` (US-14.4).
 *
 * The screen reads nothing, because there is nothing to read: `admin-bff.yaml`
 * carries the `PUT` and no `GET`, and no other contract on the platform serves the
 * tariffs in force. See `TariffForm` for why the boxes are left empty rather than
 * seeded with D2's illustrative figures, and the C108 handoff for the
 * micro-change-set.
 */

export const dynamic = 'force-dynamic';

export default async function FareTariffsPage() {
  const [t, locale, session] = await Promise.all([getTranslator(), getLocale(), getSession()]);
  const tabs = configTabs(session?.menu ?? [], 'tariffs');

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

      <TariffForm
        labels={{
          heading: t('admin.config.tariffs.heading'),
          noReadNote: t('admin.config.tariffs.noReadNote'),
          modeANote: t('admin.config.tariffs.modeANote'),
          caption: t('admin.config.tariffs.caption'),
          vehicle: t('admin.config.column.vehicle'),
          firstKm: t('admin.config.tariffs.firstKm'),
          perKm: t('admin.config.tariffs.perKm'),
          peak: t('admin.config.tariffs.peak'),
          night: t('admin.config.tariffs.night'),
          effectiveFrom: t('admin.config.tariffs.effectiveFrom'),
          effectiveFromHint: t('admin.config.tariffs.effectiveFromHint'),
          windowsHeading: t('admin.config.tariffs.windowsHeading'),
          peakWindow: t('admin.config.tariffs.peakWindow'),
          nightWindow: t('admin.config.tariffs.nightWindow'),
          windowStart: t('admin.config.tariffs.windowStart'),
          windowEnd: t('admin.config.tariffs.windowEnd'),
          windowPct: t('admin.config.tariffs.windowPct'),
          windowNote: t('admin.config.tariffs.windowNote'),
          submit: t('admin.config.tariffs.submit'),
          working: t('admin.config.working'),
          audit: t('admin.audit.notice'),
          saved: t('admin.config.tariffs.saved'),
          vehicleTypes: vehicleTypeLabels({ t, locale }),
        }}
      />
    </div>
  );
}
