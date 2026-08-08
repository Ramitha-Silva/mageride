import { StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import {
  isFiltered,
  isSendable,
  OPERATING_MODES,
  REGISTRATION_STATUSES,
  VEHICLE_TYPES,
  vehicleQuery,
  vehicleSearch,
  vehicleSelection,
  VEHICLES_PATH,
  type CursorPage,
  type VehicleRow,
} from '@/api/directories';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { ProblemPanel } from '@/components/ProblemPanel';
import { vehicleHref, vehiclesHref, VEHICLES_ROUTE } from '@/components/directories/links';
import {
  modeLabel,
  registrationStatusPill,
  resultCount,
  vehicleRows,
  vehicleTypeLabel,
  type RenderContext,
} from '@/components/directories/model';
import { MoreResults, ResultsTable } from '@/components/directories/ResultsTable';
import { SearchForm } from '@/components/directories/SearchForm';
import { getLocale, getTranslator } from '@/i18n/server';

/**
 * **SCR-AP-014 · `vehicle_search`** — find a vehicle by plate, vehicle id, type,
 * mode, owner mobile, fleet organisation or registration status (AL-42, US-24.11).
 *
 * ## The two enums are selects and the third is a text box
 *
 * `type` and `mode` are closed vocabularies the platform owns — AL-09's ten types
 * and D-03's three modes — and admin-bff validates them rather than passing them
 * through, "because a typo'd enum would answer 200 with an empty page, which reads
 * as *no such vehicle* and is a different fact". So they are drawn as selects and
 * an unrecognised value in the URL is dropped rather than forwarded.
 *
 * `fleetOrg` is not. The wireframe draws "Lanka Transit ▾", but there is no route
 * that lists fleet organisations and the parameter is a `maxLength: 200` string, so
 * a dropdown here would be this portal inventing a vocabulary the platform does not
 * publish. C107 made the same call for a ticket category; the C109 handoff asks for
 * the route that would let it become one.
 *
 * ## Mode A/B rows show the organisation, not a driver
 *
 * A bus belongs to Lanka Transit and naming whoever happens to be driving it would
 * answer a different question from the one the column asks. `fleetOrg` therefore
 * wins over `owner` in that cell (`vehicleRows`).
 */

export const dynamic = 'force-dynamic';

export default async function VehicleSearchPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const selection = vehicleSelection(params);

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const context: RenderContext = { t, locale };

  let page: CursorPage<VehicleRow> | null = null;
  let problem: ProblemDetails | null = null;

  if (isSendable(selection)) {
    try {
      page = await read<CursorPage<VehicleRow>>({
        path: VEHICLES_PATH,
        searchParams: vehicleSearch(selection),
      });
    } catch (error) {
      if (!(error instanceof ProblemError)) throw error;
      problem = error.problem;
    }
  }

  const rows = page ? vehicleRows(page.items, (id) => vehicleHref(selection, id), context) : [];

  return (
    <div className="flex flex-col gap-md">
      <div className="flex flex-wrap items-center gap-xs">
        <StatusPill tone="warning" dot={false}>
          {t('admin.directory.piiPill')}
        </StatusPill>
        <p className="text-caption text-on-surface-variant">{t('admin.directory.piiNotice')}</p>
      </div>

      <SearchForm
        action={VEHICLES_ROUTE}
        filtered={isFiltered(vehicleQuery(selection))}
        fields={[
          {
            kind: 'text',
            name: 'regNo',
            label: t('admin.directory.field.regNo'),
            maxLength: 32,
            ...(selection.regNo ? { value: selection.regNo } : {}),
          },
          {
            kind: 'text',
            name: 'id',
            label: t('admin.directory.field.vehicleId'),
            hint: t('admin.directory.field.idHint'),
            maxLength: 64,
            ...(selection.rawId ? { value: selection.rawId } : {}),
            ...(selection.invalidId ? { error: t('admin.directory.field.idInvalid') } : {}),
          },
          {
            kind: 'select',
            name: 'type',
            label: t('admin.directory.field.type'),
            ...(selection.type ? { value: selection.type } : {}),
            options: [
              { value: '', label: t('admin.directory.vehicle.anyType') },
              ...VEHICLE_TYPES.map((type) => ({ value: type, label: vehicleTypeLabel(type, t) })),
            ],
          },
          {
            kind: 'select',
            name: 'mode',
            label: t('admin.directory.field.mode'),
            ...(selection.mode ? { value: selection.mode } : {}),
            options: [
              { value: '', label: t('admin.directory.vehicle.anyMode') },
              ...OPERATING_MODES.map((mode) => ({ value: mode, label: modeLabel(mode, t) })),
            ],
          },
          {
            kind: 'text',
            name: 'ownerMobile',
            label: t('admin.directory.field.ownerMobile'),
            type: 'tel',
            maxLength: 20,
            ...(selection.ownerMobile ? { value: selection.ownerMobile } : {}),
          },
          {
            kind: 'text',
            name: 'fleetOrg',
            label: t('admin.directory.field.fleetOrg'),
            hint: t('admin.directory.field.fleetOrgHint'),
            maxLength: 200,
            ...(selection.fleetOrg ? { value: selection.fleetOrg } : {}),
          },
          {
            kind: 'select',
            name: 'status',
            label: t('admin.directory.column.status'),
            ...(selection.status ? { value: selection.status } : {}),
            options: [
              { value: '', label: t('admin.directory.vehicle.anyStatus') },
              ...REGISTRATION_STATUSES.map((status) => ({
                value: status,
                label: registrationStatusPill(status, t).label,
              })),
            ],
          },
        ]}
        labels={{
          heading: t('admin.directory.vehicle.searchHeading'),
          hint: t('admin.directory.hint.differentCriteria'),
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
              caption: t('admin.directory.vehicle.caption'),
              columns: [
                t('admin.directory.column.vehicle'),
                t('admin.directory.column.typeMode'),
                t('admin.directory.column.ownerFleet'),
                t('admin.directory.column.trips'),
                t('admin.directory.column.status'),
              ],
              empty: t('admin.directory.vehicle.empty'),
              open: t('admin.directory.open'),
            }}
          />

          {page.hasMore && page.cursor ? (
            <MoreResults
              href={vehiclesHref({ ...selection, cursor: page.cursor })}
              label={t('admin.directory.more')}
            />
          ) : null}
        </>
      ) : null}
    </div>
  );
}
