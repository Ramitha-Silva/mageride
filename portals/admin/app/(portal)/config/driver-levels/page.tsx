import { LevelConfigForm } from '@/components/config/LevelConfigForm';
import { configTabs } from '@/components/config/tabs';
import { ScreenTabs } from '@/components/ScreenTabs';
import { getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-007 · the Driver-Level tab** — `PUT /v1/admin/drivers/level-config`,
 * answered by dispatch-svc (US-14.12).
 *
 * Nothing is read: dispatch-svc exposes the `PUT` and a per-driver
 * `GET /v1/drivers/{driverId}/level`, which is one driver's standing rather than
 * the system's tuning. See `LevelConfigForm`, and the C108 handoff for the missing
 * read and the missing audit row.
 */

export const dynamic = 'force-dynamic';

export default async function DriverLevelsPage() {
  const [t, session] = await Promise.all([getTranslator(), getSession()]);
  const tabs = configTabs(session?.menu ?? [], 'levels');

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

      <LevelConfigForm
        labels={{
          heading: t('admin.config.levels.heading'),
          noReadNote: t('admin.config.levels.noReadNote'),
          defaultsNote: t('admin.config.levels.defaultsNote'),
          levelUpThreshold: t('admin.config.levels.threshold'),
          levelUpThresholdHint: t('admin.config.levels.thresholdHint'),
          noShowPenalty: t('admin.config.levels.noShow'),
          noShowPenaltyHint: t('admin.config.levels.noShowHint'),
          cancellationPenalty: t('admin.config.levels.cancellation'),
          cancellationPenaltyHint: t('admin.config.levels.cancellationHint'),
          jobBoardMinLevel: t('admin.config.levels.jobBoard'),
          jobBoardMinLevelHint: t('admin.config.levels.jobBoardHint'),
          submit: t('admin.config.levels.submit'),
          working: t('admin.config.working'),
          audit: t('admin.audit.notRecorded', { service: 'dispatch-svc' }),
          saved: t('admin.config.levels.saved'),
        }}
      />
    </div>
  );
}
