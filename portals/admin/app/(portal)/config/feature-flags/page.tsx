import { read } from '@/api/client';
import { FEATURE_FLAGS_PATH, type FeatureFlag } from '@/api/config';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { FeatureFlagList } from '@/components/config/FeatureFlagList';
import { featureFlagRows } from '@/components/config/model';
import { configTabs } from '@/components/config/tabs';
import { ProblemPanel } from '@/components/ProblemPanel';
import { ScreenTabs } from '@/components/ScreenTabs';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-007 · the Feature-flags tab** — `config.feature_flags` (US-14.12).
 *
 * One of the two configuration surfaces admin-bff answers itself, so the toggle
 * writes a `FEATURE_FLAG_SET` row through the audit interceptor and the notice
 * beside it is the true one.
 */

export const dynamic = 'force-dynamic';

export default async function FeatureFlagsPage() {
  const [t, locale, session] = await Promise.all([getTranslator(), getLocale(), getSession()]);
  const tabs = configTabs(session?.menu ?? [], 'flags');

  let flags: readonly FeatureFlag[] = [];
  let problem: ProblemDetails | null = null;

  try {
    flags = await read<FeatureFlag[]>({ path: FEATURE_FLAGS_PATH });
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

      <FeatureFlagList
        flags={featureFlagRows(flags, { t, locale })}
        labels={{
          heading: t('admin.config.flags.heading'),
          note: t('admin.config.flags.note'),
          on: t('admin.config.flags.on'),
          off: t('admin.config.flags.off'),
          turnOn: t('admin.config.flags.turnOn'),
          turnOff: t('admin.config.flags.turnOff'),
          working: t('admin.config.working'),
          updated: t('admin.config.flags.updated'),
          empty: t('admin.config.flags.empty'),
          addHeading: t('admin.config.flags.addHeading'),
          key: t('admin.config.flags.key'),
          keyHint: t('admin.config.flags.keyHint'),
          description: t('admin.config.flags.description'),
          descriptionHint: t('admin.config.flags.descriptionHint'),
          enable: t('admin.config.flags.enable'),
          add: t('admin.config.flags.add'),
          audit: t('admin.audit.notice'),
          saved: t('admin.config.flags.saved'),
        }}
      />
    </div>
  );
}
