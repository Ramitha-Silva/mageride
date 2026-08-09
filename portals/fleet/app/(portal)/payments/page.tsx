import { redirect } from 'next/navigation';

import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import {
  byActiveFirst,
  byNewestPeriod,
  subscriberPaymentsTarget,
  subscriptionTotals,
  vehicleSubscribersTarget,
  LEDGER_PAGE_LIMIT,
  SUBSCRIPTION_PAGE_LIMIT,
  type SubscriberPage,
  type SubscriberRow,
  type SubscriptionPaymentPage,
} from '@/api/subscriptions';
import { VEHICLES, type FleetVehicleList } from '@/api/vehicles';
import { KpiTiles, type KpiTile } from '@/components/KpiTiles';
import { ProblemPanel } from '@/components/ProblemPanel';
import { PaymentLedger } from '@/components/subscriptions/PaymentLedger';
import { ScopeForm, type SubscriberChoice } from '@/components/subscriptions/ScopeForm';
import {
  paymentRows,
  selectedVehicle,
  subscribableVehicles,
} from '@/components/subscriptions/subscription-model';
import { formatFareMinor } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';
import type { FleetTranslator, Locale } from '@/i18n';
import { canManageSubscribers } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-012 · `fleet_subscriber_payments`** — item 16i's per-subscriber,
 * per-vehicle payment ledger with the summary KPIs above it (US-23.10).
 *
 * ## Owner-only, and the screen says so before it reads anything
 *
 * `GET …/subscribers/{id}/payments` and `POST …/payments/{id}/confirm` are both
 * `RequireFleetSubRole(Owner)` — US-23.6, "only the fleet Owner can mark it
 * received". `canManageSubscribers()` is checked first, so a Manager who follows a
 * link gets one sentence naming whose screen it is rather than a run of 403s and
 * an audit trail of an access attempt nobody made. Same shape as SCR-FP-010, and
 * for the same reason.
 *
 * ## The KPIs are the roster's own answer about this month, and say what they are
 *
 * The four tiles are computed from **one** `GET …/subscribers` read, over
 * `thisMonthStatus` — subscription-svc's verdict on the current Colombo month —
 * and the fare each subscriber is on. That is a figure per vehicle from one
 * request, where per-payment accuracy would need one ledger read per subscriber
 * for a number the ledger below already prints exactly.
 *
 * Two things the tiles therefore are not, and the captions say both:
 *
 *  - **"Cash due" is "due"**. Nothing knows in advance how a subscriber will pay:
 *    a `cash` row is written by the owner's own mark, and a passenger who has
 *    chosen no rail has no row at all — which `ThisMonthStatusOf` reports as
 *    `unpaid` exactly like one who opened the pay sheet and walked away.
 *  - **"Collected" is the fares of the subscribers marked paid**, not the sum of
 *    what arrived: a cash mark takes an `amountMinor` of its own and a part
 *    payment is a part payment. The exact amounts are the ledger's.
 *
 * ## The CSV is written here, because no contract exports this
 *
 * `web_fleet.html` draws "Export CSV" and subscription-svc's only document route
 * is the signed slip/QR file. So `/payments/export` re-reads the same org-scoped
 * ledger and writes the rows — the same arrangement SCR-FP-009's analytics CSV
 * has, and the opposite of SCR-FP-010's invoice, which fleet-billing-svc renders
 * and the portal only streams.
 */

export const dynamic = 'force-dynamic';

export default async function PaymentsPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const session = await getSession();
  if (!session) redirect('/login');

  const [t, locale, query] = await Promise.all([getTranslator(), getLocale(), searchParams]);

  if (!canManageSubscribers(session)) {
    return (
      <div className="flex flex-col gap-md">
        <h2 className="text-title font-display">{t('fleet.payments.title')}</h2>
        <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
          {t('fleet.payments.ownerOnly')}
        </p>
      </div>
    );
  }

  let vehicles: FleetVehicleList | null = null;
  let problem: ProblemDetails | null = null;
  try {
    vehicles = await read<FleetVehicleList>({ org: VEHICLES });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const choices = subscribableVehicles(vehicles?.items ?? [], t);
  const chosenVehicle = selectedVehicle(choices, single(query['vehicle']));

  const roster = chosenVehicle
    ? await optional<SubscriberPage>(vehicleSubscribersTarget(chosenVehicle.vehicleId))
    : { data: null, problem: null };

  const rosterRows = [...(roster.data?.items ?? [])].sort(byActiveFirst);
  const totals = subscriptionTotals(rosterRows);

  const subscriberChoices = rosterRows.map((row) => subscriberChoice(row, t));
  const wantedSubscriber = single(query['subscriber']);
  const chosenSubscriber =
    rosterRows.find((row) => row.subscriberId === wantedSubscriber) ?? rosterRows[0] ?? null;

  const ledger =
    chosenVehicle && chosenSubscriber
      ? await optional<SubscriptionPaymentPage>(
          subscriberPaymentsTarget(chosenVehicle.vehicleId, chosenSubscriber.subscriberId),
          LEDGER_PAGE_LIMIT,
        )
      : { data: null, problem: null };

  const payments = [...(ledger.data?.items ?? [])].sort(byNewestPeriod);

  const exportHref =
    chosenVehicle && chosenSubscriber
      ? `/payments/export?vehicle=${encodeURIComponent(chosenVehicle.vehicleId)}&subscriber=${encodeURIComponent(chosenSubscriber.subscriberId)}`
      : null;

  return (
    <div className="flex flex-col gap-md">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-title font-display">{t('fleet.payments.title')}</h2>

        {exportHref ? (
          <a
            href={exportHref}
            download
            className="inline-flex h-10 items-center justify-center rounded-sm border border-outline px-md text-body-sm font-body text-on-surface hover:bg-surface-variant focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary print:hidden"
          >
            {t('fleet.payments.exportCsv')}
          </a>
        ) : null}
      </div>

      {problem ? <ProblemPanel problem={problem} /> : null}

      <ScopeForm
        vehicles={choices}
        selectedVehicleId={chosenVehicle?.vehicleId ?? null}
        subscribers={subscriberChoices.length > 0 ? subscriberChoices : null}
        selectedSubscriberId={chosenSubscriber?.subscriberId ?? null}
        labels={{
          legend: t('fleet.payments.scope.legend'),
          vehicle: t('fleet.subscriptions.scope.vehicle'),
          subscriber: t('fleet.subscriptions.scope.subscriber'),
          apply: t('fleet.subscriptions.scope.apply'),
          noVehicles: t('fleet.subscriptions.noModeBVehicles'),
        }}
      />

      {roster.problem ? <ProblemPanel problem={roster.problem} /> : null}

      {chosenVehicle ? (
        <>
          <KpiTiles tiles={kpiTiles(totals, chosenVehicle.plate, locale, t)} />
          <p className="text-caption text-on-surface-variant">{t('fleet.payments.kpiNote')}</p>
          <p className="text-caption text-on-surface-variant">
            {t('fleet.subscriptions.passThroughNote')}
          </p>
        </>
      ) : (
        <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
          {choices.length > 0
            ? t('fleet.subscriptions.unknownVehicle')
            : t('fleet.subscriptions.noModeBVehicles')}
        </p>
      )}

      {ledger.problem ? <ProblemPanel problem={ledger.problem} /> : null}

      {chosenVehicle && chosenSubscriber ? (
        <PaymentLedger
          rows={paymentRows(payments, locale, t)}
          mayConfirm
          labels={{
            heading: t('fleet.payments.ledger.heading', {
              subscriber: subscriberName(chosenSubscriber, t),
              vehicle: chosenVehicle.label,
            }),
            caption: t('fleet.payments.ledger.caption'),
            month: t('fleet.payments.column.month'),
            date: t('fleet.payments.column.date'),
            method: t('fleet.payments.column.method'),
            amount: t('fleet.payments.column.amount'),
            status: t('fleet.payments.column.status'),
            action: t('fleet.subscriptions.column.action'),
            empty: t('fleet.payments.ledger.empty'),
            notPaidYet: t('fleet.payments.notPaidYet'),
            note: t('fleet.payments.ledgerNote'),
            passThroughNote: t('fleet.subscriptions.passThroughNote'),
            moreRows: ledger.data?.hasMore
              ? t('fleet.payments.ledger.truncated', { rows: LEDGER_PAGE_LIMIT })
              : null,
          }}
          confirmLabels={{
            submit: t('fleet.subscriptions.slip.confirm'),
            submitting: t('fleet.subscriptions.slip.confirming'),
            viewSlip: t('fleet.subscriptions.slip.view'),
          }}
        />
      ) : chosenVehicle ? (
        <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
          {t('fleet.payments.noSubscribers')}
        </p>
      ) : null}
    </div>
  );
}

/* ------------------------------------------------------------------------- */

/**
 * The wireframe's four tiles.
 *
 * The money is the tone: green for what is in, amber for what is waiting on the
 * owner's own check, red for what is owed. The subscriber count carries no tone —
 * a roster is a fact, not a health signal.
 */
function kpiTiles(
  totals: ReturnType<typeof subscriptionTotals>,
  vehicle: string,
  locale: Locale,
  t: FleetTranslator,
): KpiTile[] {
  const money = (minor: number) =>
    t('fleet.money.rupees', { amount: formatFareMinor(locale, minor) });

  return [
    {
      key: 'collected',
      label: t('fleet.payments.kpi.collected'),
      value: money(totals.paidMinor),
      detail: t('fleet.payments.kpi.collectedDetail', { count: totals.paidCount, vehicle }),
      tone: 'success',
    },
    {
      key: 'pending',
      label: t('fleet.payments.kpi.pending'),
      value: money(totals.pendingMinor),
      detail: t('fleet.payments.kpi.pendingDetail', { count: totals.pendingCount }),
      tone: 'warning',
    },
    {
      key: 'due',
      label: t('fleet.payments.kpi.due'),
      value: money(totals.dueMinor),
      detail: t('fleet.payments.kpi.dueDetail', { count: totals.dueCount }),
      tone: 'error',
    },
    {
      key: 'subscribers',
      label: t('fleet.payments.kpi.subscribers'),
      value: String(totals.activeCount),
      detail: t('fleet.payments.kpi.subscribersDetail', {
        muted: totals.mutedCount,
        free: totals.freeCount,
      }),
    },
  ];
}

function subscriberName(row: SubscriberRow, t: FleetTranslator): string {
  return row.name?.trim() || t('fleet.subscriptions.passengerUnnamed');
}

function subscriberChoice(row: SubscriberRow, t: FleetTranslator): SubscriberChoice {
  return {
    subscriberId: row.subscriberId,
    // A muted row keeps its ledger — the payments a passenger made before they
    // unsubscribed are the owner's record of a month that was collected, and the
    // grant survives until the owner deletes it (AL-25).
    label: row.muted
      ? t('fleet.payments.scope.mutedOption', { passenger: subscriberName(row, t) })
      : subscriberName(row, t),
  };
}

interface OptionalRead<T> {
  readonly data: T | null;
  readonly problem: ProblemDetails | null;
}

/** A read whose failure costs one card rather than the screen. */
async function optional<T>(org: string, limit = SUBSCRIPTION_PAGE_LIMIT): Promise<OptionalRead<T>> {
  try {
    return { data: await read<T>({ org, searchParams: { limit } }), problem: null };
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { data: null, problem: error.problem };
  }
}

/** The first value of a repeated query parameter, or `undefined`. */
function single(value: string | string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}
