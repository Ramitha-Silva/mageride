import { StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import {
  isFiltered,
  isSendable,
  passengerQuery,
  passengerSearch,
  passengerSelection,
  PASSENGERS_PATH,
  type CursorPage,
  type PassengerRow,
} from '@/api/directories';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { ProblemPanel } from '@/components/ProblemPanel';
import { passengerHref, passengersHref, PASSENGERS_ROUTE } from '@/components/directories/links';
import { passengerRows, resultCount, type RenderContext } from '@/components/directories/model';
import { MoreResults, ResultsTable } from '@/components/directories/ResultsTable';
import { SearchForm } from '@/components/directories/SearchForm';
import { getLocale, getTranslator } from '@/i18n/server';

/**
 * **SCR-AP-010 · `passenger_search`** — find a passenger by name, mobile,
 * passenger id or email, in any combination (AL-40, US-16.4, US-24.9).
 *
 * ## Every criterion is independent, and that is what "any combination" means
 *
 * The form submits all four boxes, `passengerSearch` forwards whichever say
 * something, and admin-bff ANDs them. There is no primary field, no mode, and no
 * branch that drops one criterion because another is filled — which is the only
 * shape in which "works singly and in combination" is a property rather than a
 * matrix of cases somebody has to keep testing.
 *
 * ## A malformed id asks admin-bff nothing
 *
 * `?id=` is parsed as a UUID on the other side and anything else is a `400` naming
 * the field. So a mistyped id keeps the operator's text, marks that box, and sends
 * no request: C105's half-chosen date range makes the same call, because answering
 * somebody's first press with a validation error about the form they are still
 * filling in is not an answer.
 *
 * ## The list is masked and it is masked upstream
 *
 * `PassengerRow.mobileMasked` is `PhoneMasked` for **every** caller, whatever they
 * hold — which is what makes "every clear number this surface has emitted has a
 * `PII_READ` row behind it" true rather than approximate. The clear number lives on
 * SCR-AP-011, behind the read that writes that row. Nothing here can reveal it and
 * the pill in the corner says so before an operator starts.
 */

export const dynamic = 'force-dynamic';

export default async function PassengerSearchPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const selection = passengerSelection(params);

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const context: RenderContext = { t, locale };

  let page: CursorPage<PassengerRow> | null = null;
  let problem: ProblemDetails | null = null;

  if (isSendable(selection)) {
    try {
      page = await read<CursorPage<PassengerRow>>({
        path: PASSENGERS_PATH,
        searchParams: passengerSearch(selection),
      });
    } catch (error) {
      if (!(error instanceof ProblemError)) throw error;
      problem = error.problem;
    }
  }

  const rows = page ? passengerRows(page.items, (id) => passengerHref(selection, id), context) : [];

  return (
    <div className="flex flex-col gap-md">
      <div className="flex flex-wrap items-center gap-xs">
        <StatusPill tone="warning" dot={false}>
          {t('admin.directory.piiPill')}
        </StatusPill>
        <p className="text-caption text-on-surface-variant">{t('admin.directory.piiNotice')}</p>
      </div>

      <SearchForm
        action={PASSENGERS_ROUTE}
        filtered={isFiltered(passengerQuery(selection))}
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
            label: t('admin.directory.field.passengerId'),
            hint: t('admin.directory.field.idHint'),
            maxLength: 64,
            ...(selection.rawId ? { value: selection.rawId } : {}),
            ...(selection.invalidId ? { error: t('admin.directory.field.idInvalid') } : {}),
          },
          {
            kind: 'text',
            name: 'email',
            label: t('admin.directory.field.email'),
            type: 'email',
            maxLength: 200,
            ...(selection.email ? { value: selection.email } : {}),
          },
        ]}
        labels={{
          heading: t('admin.directory.passenger.searchHeading'),
          hint: t('admin.directory.hint.anyCriterion'),
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
              caption: t('admin.directory.passenger.caption'),
              columns: [
                t('admin.directory.column.passenger'),
                t('admin.directory.field.mobile'),
                t('admin.directory.column.trips'),
                t('admin.directory.column.joined'),
                t('admin.directory.column.status'),
              ],
              empty: t('admin.directory.passenger.empty'),
              open: t('admin.directory.open'),
            }}
          />

          {page.hasMore && page.cursor ? (
            <MoreResults
              href={passengersHref({ ...selection, cursor: page.cursor })}
              label={t('admin.directory.more')}
            />
          ) : null}
        </>
      ) : null}
    </div>
  );
}
