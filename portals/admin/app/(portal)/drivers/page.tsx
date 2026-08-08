import { StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import {
  DEFAULT_DRIVER_STATUS,
  driverQuery,
  driverSearch,
  driverSelection,
  DRIVER_LEVELS,
  DRIVER_STATUSES,
  DRIVERS_PATH,
  isFiltered,
  isSendable,
  type CursorPage,
  type DriverRow,
} from '@/api/directories';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { ProblemPanel } from '@/components/ProblemPanel';
import { driverHref, driversHref, DRIVERS_ROUTE } from '@/components/directories/links';
import { driverRows, resultCount, type RenderContext } from '@/components/directories/model';
import { MoreResults, ResultsTable } from '@/components/directories/ResultsTable';
import { SearchForm } from '@/components/directories/SearchForm';
import type { AnyMessageKey } from '@/i18n';
import { getLocale, getTranslator } from '@/i18n/server';

/**
 * **SCR-AP-012 · `driver_search`** — find a driver by name, mobile, driver id, NIC,
 * plate, Driver Level or status (AL-41, US-24.10).
 *
 * ## It defaults to verified drivers, and the default is sent
 *
 * US-24.10 asks for the people currently driving; an operator who wants an
 * applicant asks for one. `status` is therefore **always** on the query even when
 * it is the default — the pill in the corner says "verified drivers only", and a
 * screen whose caption is true only for as long as admin-bff's own default is would
 * be a caption waiting to become a lie. `all` is how the default is lifted; there is
 * no "no filter" here, because the absent parameter *is* a filter.
 *
 * ## Three Driver Levels, not the wireframe's five
 *
 * `dispatch.driver_levels.level` is 1–3, `searchDrivers` bounds `level` to that and
 * answers `400` outside it, and D-14 describes three bands. SCR-AP-012 draws
 * "L1–L5 ▾"; a fourth option in this select would be a filter the platform refuses.
 * Recorded in the C109 handoff.
 *
 * ## `?level=1` is the list ADD Appendix C asked for
 *
 * The literal `/drivers/level-1` path it named is gone — it would be ambiguous
 * against `/drivers/{driverId}` — and this control is what replaced it.
 */

export const dynamic = 'force-dynamic';

const STATUS_LABELS: Readonly<Record<string, AnyMessageKey>> = {
  verified: 'status.verified',
  pending: 'status.pending',
  suspended: 'status.suspended',
  all: 'admin.directory.driver.statusAll',
};

export default async function DriverSearchPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const selection = driverSelection(params);

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const context: RenderContext = { t, locale };

  let page: CursorPage<DriverRow> | null = null;
  let problem: ProblemDetails | null = null;

  if (isSendable(selection)) {
    try {
      page = await read<CursorPage<DriverRow>>({
        path: DRIVERS_PATH,
        searchParams: driverSearch(selection),
      });
    } catch (error) {
      if (!(error instanceof ProblemError)) throw error;
      problem = error.problem;
    }
  }

  const rows = page ? driverRows(page.items, (id) => driverHref(selection, id), context) : [];

  return (
    <div className="flex flex-col gap-md">
      <div className="flex flex-wrap items-center gap-xs">
        <StatusPill tone="warning" dot={false}>
          {t('admin.directory.piiPill')}
        </StatusPill>
        <StatusPill tone="neutral" dot={false}>
          {t(
            selection.status === DEFAULT_DRIVER_STATUS
              ? 'admin.directory.driver.verifiedOnly'
              : 'admin.directory.driver.statusShown',
            { status: t(STATUS_LABELS[selection.status] ?? 'status.verified') },
          )}
        </StatusPill>
        <p className="text-caption text-on-surface-variant">{t('admin.directory.piiNotice')}</p>
      </div>

      <SearchForm
        action={DRIVERS_ROUTE}
        filtered={isFiltered(driverQuery(selection))}
        fields={[
          {
            kind: 'text',
            name: 'name',
            label: t('admin.directory.field.name'),
            maxLength: 200,
            ...(selection.name ? { value: selection.name } : {}),
          },
          {
            kind: 'text',
            name: 'mobile',
            label: t('admin.directory.field.mobile'),
            type: 'tel',
            maxLength: 20,
            ...(selection.mobile ? { value: selection.mobile } : {}),
          },
          {
            kind: 'text',
            name: 'id',
            label: t('admin.directory.field.driverId'),
            hint: t('admin.directory.field.idHint'),
            maxLength: 64,
            ...(selection.rawId ? { value: selection.rawId } : {}),
            ...(selection.invalidId ? { error: t('admin.directory.field.idInvalid') } : {}),
          },
          {
            kind: 'text',
            name: 'nic',
            label: t('admin.directory.field.nic'),
            maxLength: 20,
            ...(selection.nic ? { value: selection.nic } : {}),
          },
          {
            kind: 'text',
            name: 'regNo',
            label: t('admin.directory.field.regNo'),
            maxLength: 32,
            ...(selection.regNo ? { value: selection.regNo } : {}),
          },
          {
            kind: 'select',
            name: 'level',
            label: t('admin.directory.field.level'),
            ...(selection.level ? { value: String(selection.level) } : {}),
            options: [
              { value: '', label: t('admin.directory.driver.anyLevel') },
              ...DRIVER_LEVELS.map((level) => ({
                value: String(level),
                label: t('admin.directory.driver.levelShort', { level }),
              })),
            ],
          },
          {
            kind: 'select',
            name: 'status',
            label: t('admin.directory.column.status'),
            value: selection.status,
            options: DRIVER_STATUSES.map((status) => ({
              value: status,
              label: t(STATUS_LABELS[status] ?? 'status.verified'),
            })),
          },
        ]}
        labels={{
          heading: t('admin.directory.driver.searchHeading'),
          hint: t('admin.directory.hint.multipleCriteria'),
          submit: t('common.search'),
          clear: t('admin.directory.clear'),
          results: resultCount(page, context),
        }}
      />

      {problem ? <ProblemPanel problem={problem} /> : null}

      {page ? (
        <>
          <ResultsTable
            rows={rows}
            labels={{
              caption: t('admin.directory.driver.caption'),
              columns: [
                t('admin.directory.column.driver'),
                t('admin.directory.field.mobile'),
                t('admin.directory.column.vehicles'),
                t('admin.directory.field.level'),
                t('admin.directory.column.trips'),
                t('admin.directory.column.status'),
              ],
              empty: t('admin.directory.driver.empty'),
              open: t('admin.directory.open'),
            }}
          />

          {page.hasMore && page.cursor ? (
            <MoreResults
              href={driversHref({ ...selection, cursor: page.cursor })}
              label={t('admin.directory.more')}
            />
          ) : null}
        </>
      ) : null}
    </div>
  );
}
