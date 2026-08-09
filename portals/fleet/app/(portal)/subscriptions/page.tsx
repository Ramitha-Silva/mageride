import { redirect } from 'next/navigation';

import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import {
  byActiveFirst,
  pendingConfirmation,
  pendingRequests,
  subscriberPaymentsTarget,
  subscriptionTotals,
  vehicleRequestsTarget,
  vehicleSubscribersTarget,
  LEDGER_PAGE_LIMIT,
  REJECT_REASON_MAX_LENGTH,
  SUBSCRIPTION_PAGE_LIMIT,
  type AccessRequestPage,
  type SubscriberPage,
  type SubscriberRow,
  type SubscriptionPaymentPage,
} from '@/api/subscriptions';
import { VEHICLES, type FleetVehicleList } from '@/api/vehicles';
import { ProblemPanel } from '@/components/ProblemPanel';
import { RequestQueue } from '@/components/subscriptions/RequestQueue';
import { ScopeForm } from '@/components/subscriptions/ScopeForm';
import { SubscriberRoster } from '@/components/subscriptions/SubscriberRoster';
import {
  requestRows,
  selectedVehicle,
  subscribableVehicles,
  subscriberRows,
  type PendingSlip,
} from '@/components/subscriptions/subscription-model';
import { formatFareMinor } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';
import { canManageSubscribers, canMutate } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-011 · `fleet_subscriptions`** — Mode B's owner side: item 15's
 * per-vehicle request queue and items 16/17's subscriber roster (Epic 23).
 *
 * ## The vehicle is the address, not a filter (AL-23)
 *
 * `subscription.access_requests` and `subscription.grants` carry a `vehicle_id`,
 * and **no contract has a fleet-wide queue or a fleet-wide roster** — every proxy
 * on this screen is `…/vehicles/{vehicleId}/…`. So `?vehicle={id}` is what the
 * page *is*, the picker is the wireframe's own topbar control, and an id from
 * another organisation resolves to a sentence rather than to a request
 * (`selectedVehicle`). The support for multi-vehicle drivers and temporarily-hired
 * vehicles that AL-23 exists for is this shape and nothing else.
 *
 * ## Four reads, and the vehicle roster is the one that decides the screen
 *
 * | part | route | if it fails |
 * |---|---|---|
 * | the picker's Mode B vehicles | `GET …/vehicles` | a problem panel; nothing else can be addressed |
 * | the queue | `GET …/vehicles/{id}/requests` | the queue shows a problem, the roster stands |
 * | the roster | `GET …/vehicles/{id}/subscribers` | the roster shows a problem, the queue stands |
 * | a pending slip's payment id | `GET …/subscribers/{id}/payments` | Confirm moves to the ledger screen |
 *
 * The fourth is the one the contract does not make easy. The wireframe draws
 * **Confirm** beside a "Transfer — verify slip" row, but `SubscriberRow` carries
 * `thisMonthStatus` and no payment id, and `confirmFleetTransferSlip` is addressed
 * *by payment*. So the id is looked up on the subscriber's own ledger — for the
 * rows in that state only, for an Owner only (the ledger route is
 * `RequireFleetSubRole(Owner)`), and for at most
 * {@link SLIP_LOOKUP_LIMIT} of them. Beyond that the row keeps its **Payments**
 * link, where the same Confirm lives, and the table says so rather than dropping
 * the control silently.
 *
 * ## Two halves, two seats
 *
 * `FleetOpsEndpoints` splits the proxies: the queue and the roster are
 * `RequireFleetSubRole(Manager)`, and the fare override, the cash mark, the slip
 * confirmation and AL-25's delete are `RequireFleetSubRole(Owner)`. A Manager
 * therefore gets a screen they can act on — accept and reject — with the money
 * verbs replaced by a sentence naming whose they are. That is `canMutate` for the
 * first half and `canManageSubscribers` for the second.
 */

export const dynamic = 'force-dynamic';

/**
 * How many `pending_verification` rows get their payment id looked up inline.
 *
 * One extra request per row, and a roster where every subscriber is awaiting a
 * slip check is a roster's worth of requests for one page. Twenty is well past
 * what a van or a school bus ever has outstanding at once, and past it the
 * **Payments** link is the same Confirm one screen away.
 */
const SLIP_LOOKUP_LIMIT = 20;

export default async function SubscriptionsPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const session = await getSession();
  if (!session) redirect('/login');

  const [t, locale, query] = await Promise.all([getTranslator(), getLocale(), searchParams]);

  const mayDecide = canMutate(session, 'fleet-operations', { requiresApprovedOrg: true });
  const mayManage = canManageSubscribers(session);

  let vehicles: FleetVehicleList | null = null;
  let problem: ProblemDetails | null = null;
  try {
    vehicles = await read<FleetVehicleList>({ org: VEHICLES });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const choices = subscribableVehicles(vehicles?.items ?? [], t);
  const chosen = selectedVehicle(choices, single(query['vehicle']));

  const scope = (
    <ScopeForm
      vehicles={choices}
      selectedVehicleId={chosen?.vehicleId ?? null}
      subscribers={null}
      selectedSubscriberId={null}
      labels={{
        legend: t('fleet.subscriptions.scope.legend'),
        vehicle: t('fleet.subscriptions.scope.vehicle'),
        subscriber: t('fleet.subscriptions.scope.subscriber'),
        apply: t('fleet.subscriptions.scope.apply'),
        noVehicles: t('fleet.subscriptions.noModeBVehicles'),
      }}
    />
  );

  if (!chosen) {
    return (
      <div className="flex flex-col gap-md">
        <h2 className="text-title font-display">{t('fleet.subscriptions.title')}</h2>
        {problem ? <ProblemPanel problem={problem} /> : null}
        {scope}
        {choices.length > 0 ? (
          <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
            {t('fleet.subscriptions.unknownVehicle')}
          </p>
        ) : null}
      </div>
    );
  }

  const [queue, roster] = await Promise.all([
    optional<AccessRequestPage>(vehicleRequestsTarget(chosen.vehicleId)),
    optional<SubscriberPage>(vehicleSubscribersTarget(chosen.vehicleId)),
  ]);

  const rosterRows = [...(roster.data?.items ?? [])].sort(byActiveFirst);
  const totals = subscriptionTotals(rosterRows);

  const awaitingSlip = rosterRows.filter((row) => row.thisMonthStatus === 'pending_verification');
  const pendingPayments = mayManage
    ? await slipPayments(chosen.vehicleId, awaitingSlip.slice(0, SLIP_LOOKUP_LIMIT))
    : new Map<string, PendingSlip>();

  const pending = pendingRequests(queue.data?.items ?? []);

  return (
    <div className="flex flex-col gap-md">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-title font-display">{t('fleet.subscriptions.title')}</h2>
      </div>

      {problem ? <ProblemPanel problem={problem} /> : null}

      {scope}

      {queue.problem ? <ProblemPanel problem={queue.problem} /> : null}

      <RequestQueue
        vehicleId={chosen.vehicleId}
        rows={requestRows(pending, locale, t)}
        mayDecide={mayDecide}
        reasonMaxLength={REJECT_REASON_MAX_LENGTH}
        labels={{
          heading: t('fleet.subscriptions.requests.heading', { vehicle: chosen.label }),
          caption: t('fleet.subscriptions.requests.caption'),
          pendingCount: t('fleet.subscriptions.requests.pending', { count: pending.length }),
          passenger: t('fleet.subscriptions.column.passenger'),
          contact: t('fleet.subscriptions.column.contact'),
          requested: t('fleet.subscriptions.column.requested'),
          action: t('fleet.subscriptions.column.action'),
          empty: t('fleet.subscriptions.requests.empty'),
          note: t('fleet.subscriptions.requests.note'),
          viewerNotice: mayDecide ? null : t('fleet.subscriptions.requests.readOnly'),
          noMobile: t('fleet.subscriptions.noMobile'),
        }}
        decisionLabels={{
          accept: t('fleet.subscriptions.accept'),
          accepting: t('fleet.subscriptions.accepting'),
          reject: t('fleet.subscriptions.reject'),
          rejecting: t('fleet.subscriptions.rejecting'),
          confirmReject: t('fleet.subscriptions.confirmReject'),
          reason: t('fleet.subscriptions.reason'),
          reasonHint: t('fleet.subscriptions.reasonHint'),
          cancel: t('common.cancel'),
        }}
      />

      {roster.problem ? <ProblemPanel problem={roster.problem} /> : null}

      <SubscriberRoster
        vehicleId={chosen.vehicleId}
        rows={subscriberRows({
          rows: rosterRows,
          vehicleId: chosen.vehicleId,
          pendingPayments,
          locale,
          t,
        })}
        mayManage={mayManage}
        labels={{
          heading: t('fleet.subscriptions.roster.heading', { vehicle: chosen.label }),
          caption: t('fleet.subscriptions.roster.caption'),
          // The sketch's own pill: "Paid · default Rs 6,000/mo". The default is
          // the *vehicle's* (SCR-FP-004's control) and is what a new subscription
          // starts on; every amount in the column below it may differ, because
          // US-23.7's override is per subscriber.
          service: !chosen.paid
            ? t('fleet.subscriptions.roster.free')
            : chosen.defaultFareMinor === null
              ? t('fleet.subscriptions.roster.paid', { count: totals.activeCount })
              : t('fleet.subscriptions.roster.paidWithDefault', {
                  amount: formatFareMinor(locale, chosen.defaultFareMinor),
                }),
          passenger: t('fleet.subscriptions.column.passenger'),
          fare: t('fleet.subscriptions.column.fare'),
          cycle: t('fleet.subscriptions.column.cycle'),
          thisMonth: t('fleet.subscriptions.column.thisMonth'),
          actions: t('fleet.subscriptions.column.actions'),
          empty: t('fleet.subscriptions.roster.empty'),
          payments: t('fleet.subscriptions.paymentsLink'),
          noMobile: t('fleet.subscriptions.noMobile'),
          cycleNote: t('fleet.subscriptions.cycleNote'),
          note: t('fleet.subscriptions.rosterNote'),
          passThroughNote: t('fleet.subscriptions.passThroughNote'),
          ownerOnly: mayManage ? null : t('fleet.subscriptions.ownerOnly'),
          moreRows:
            roster.data?.hasMore || awaitingSlip.length > SLIP_LOOKUP_LIMIT
              ? t('fleet.subscriptions.roster.truncated', {
                  rows: SUBSCRIPTION_PAGE_LIMIT,
                  slips: SLIP_LOOKUP_LIMIT,
                })
              : null,
        }}
        fareLabels={{
          edit: t('fleet.subscriptions.fare.edit'),
          fare: t('fleet.subscriptions.fare.label'),
          fareHint: t('fleet.subscriptions.fare.hint'),
          save: t('fleet.subscriptions.fare.save'),
          saving: t('fleet.subscriptions.fare.saving'),
          cancel: t('common.cancel'),
          none: t('fleet.subscriptions.fare.none'),
        }}
        cashLabels={{
          open: t('fleet.subscriptions.cash.open'),
          amount: t('fleet.subscriptions.cash.amount'),
          amountHint: t('fleet.subscriptions.cash.amountHint'),
          submit: t('fleet.subscriptions.cash.submit'),
          submitting: t('fleet.subscriptions.cash.submitting'),
          cancel: t('common.cancel'),
        }}
        confirmLabels={{
          submit: t('fleet.subscriptions.slip.confirm'),
          submitting: t('fleet.subscriptions.slip.confirming'),
          viewSlip: t('fleet.subscriptions.slip.view'),
        }}
        deleteLabels={{
          delete: t('fleet.subscriptions.delete.open'),
          confirm: t('fleet.subscriptions.delete.confirm'),
          confirming: t('fleet.subscriptions.delete.confirming'),
          cancel: t('common.cancel'),
          warning: t('fleet.subscriptions.delete.warning'),
        }}
      />

      {chosen.paid ? (
        <p className="text-caption text-on-surface-variant">
          {t('fleet.subscriptions.dueSummary', {
            amount: t('fleet.money.rupees', {
              amount: formatFareMinor(locale, totals.dueMinor),
            }),
            count: totals.dueCount,
          })}
        </p>
      ) : (
        <p className="text-caption text-on-surface-variant">
          {t('fleet.subscriptions.freeVehicleNote')}
        </p>
      )}
    </div>
  );
}

/* ------------------------------------------------------------------------- */

interface OptionalRead<T> {
  readonly data: T | null;
  readonly problem: ProblemDetails | null;
}

/** A read whose failure costs one card rather than the screen. */
async function optional<T>(org: string): Promise<OptionalRead<T>> {
  try {
    return {
      data: await read<T>({ org, searchParams: { limit: SUBSCRIPTION_PAGE_LIMIT } }),
      problem: null,
    };
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { data: null, problem: error.problem };
  }
}

/**
 * The awaiting-verification payment of each row whose month has one.
 *
 * One ledger read per row, which is why the caller bounds the list. The slip's
 * own signed URL comes back with it, so the owner can look at what they are
 * confirming before they confirm it rather than after. A read that fails is
 * simply absent from the map: the row keeps its chip and its **Payments** link,
 * and no Confirm is drawn for a payment this screen could not identify.
 */
async function slipPayments(
  vehicleId: string,
  rows: readonly SubscriberRow[],
): Promise<ReadonlyMap<string, PendingSlip>> {
  const found = new Map<string, PendingSlip>();

  const answers = await Promise.all(
    rows.map(async (row) => {
      try {
        const page = await read<SubscriptionPaymentPage>({
          org: subscriberPaymentsTarget(vehicleId, row.subscriberId),
          searchParams: { limit: LEDGER_PAGE_LIMIT },
        });
        const payment = pendingConfirmation(page.items ?? []);
        return [
          row.subscriberId,
          payment ? { paymentId: payment.paymentId, slipUrl: payment.slipUrl ?? null } : null,
        ] as const;
      } catch (error) {
        if (!(error instanceof ProblemError)) throw error;
        return [row.subscriberId, null] as const;
      }
    }),
  );

  for (const [subscriberId, slip] of answers) {
    if (slip) found.set(subscriberId, slip);
  }
  return found;
}

/** The first value of a repeated query parameter, or `undefined`. */
function single(value: string | string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}
