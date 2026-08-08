import { redirect } from 'next/navigation';

import { StatusPill, Tabs } from '@mageride/ui';

import { read } from '@/api/client';
import {
  PAID_SERVICE_PAYMENT_BLOCKED_KEY,
  PAYOUT_PROFILE,
  canSetPaidServicePayment,
  type PayoutProfile,
} from '@/api/payout';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import {
  BULK_CSV_ACCEPT,
  BULK_CSV_COLUMNS,
  BULK_MAX_ROWS,
  BULK_UPLOAD_MAX_BYTES,
  FLEET_MODES,
  ONBOARDABLE_VEHICLE_TYPES,
  VEHICLES,
  vehicleDocumentsTarget,
  type FleetVehicle,
  type FleetVehicleList,
  type VehicleDocumentSlot,
  type VehicleDocumentSlotList,
} from '@/api/vehicles';
import { ProblemPanel } from '@/components/ProblemPanel';
import { AddVehicleForm } from '@/components/vehicles/AddVehicleForm';
import { BulkVehicleImport } from '@/components/vehicles/BulkVehicleImport';
import { ServicePaymentForm } from '@/components/vehicles/ServicePaymentForm';
import { VehicleDocumentPanel } from '@/components/vehicles/VehicleDocumentPanel';
import { VehicleStatusTable } from '@/components/vehicles/VehicleStatusTable';
import { vehicleRow, vehicleTypeLabel } from '@/components/vehicles/vehicle-model';
import { getLocale, getTranslator } from '@/i18n/server';
import { canMutate, satisfiesFleetRole } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-004 · `fleet_vehicle_onboarding`** — vehicle onboarding with AL-50's
 * named document slots and AL-51's Service payment (US-13.1/13.1b/13.6,
 * US-27.3/27.4).
 *
 * ## The screen is behind the approval gate, and that is fleet-svc's gate
 *
 * `FleetEndpoints.FleetVehiclesGroup` carries `RequireApprovedFleet()` on the
 * group — the list as well as the writes — so a PENDING organisation cannot read
 * its own roster and `src/server/routes.ts` marks the entry
 * `requiresApprovedOrg`. `proxy.ts` sends such a caller to `/pending` before this
 * file runs. What is left here is the *sub-role* split: the entry is Viewer, and
 * every mutating control is `canMutate(…, { requiresApprovedOrg: true })`.
 *
 * ## Which vehicle's documents are open is in the URL
 *
 * A document is attached to a vehicle (`POST …/vehicles/{vehicleId}/documents`),
 * so the four slots need one — and the wireframe draws them inside the add card,
 * which is the one place a vehicle does not exist yet. `?vehicle={id}` resolves
 * it: the add form navigates there on success, every roster row links to it, and
 * the panel is server-rendered from that vehicle's own `GET …/documents`. A
 * reload, a bookmark and a shared link all land on the same slots.
 *
 * ## The payout profile is read only for an Owner, and that is not a shortcut
 *
 * BR-31.1's gate is a fact about the *organisation*, but
 * `GET …/payout-profile` is `RequireFleetSubRole(Owner)` — a Manager who may
 * onboard vehicles may not read the bank details. So an Owner gets the option
 * disabled with `PAID_SERVICE_PAYMENT_BLOCKED_KEY`'s sentence before they press
 * it, and a Manager gets the option enabled and fleet-svc's
 * `409 payout-profile-not-verified` translated to the same sentence after. Both
 * are blocked; only one can be told in advance, and pretending otherwise would
 * mean either a second Owner-only read that 403s on every Manager's page load or
 * a refusal this session cannot justify.
 *
 * ## Two tabs, and the wireframe draws three cards
 *
 * `web_fleet.html` puts a `Single vehicle | Bulk CSV` tab strip above a row that
 * holds *both* the add card and the bulk card. The tab strip is a real control,
 * so it selects: Single vehicle is the add card and the document slots, Bulk CSV
 * is the CSV card. Nothing the sketch draws is missing; the bulk card is behind
 * its own tab rather than also beside the form. Noted in the C113 handoff.
 */

export const dynamic = 'force-dynamic';

export default async function VehiclesPage({
  searchParams,
}: {
  searchParams: Promise<{ vehicle?: string }>;
}) {
  const session = await getSession();
  if (!session) redirect('/login');

  const [t, locale, params] = await Promise.all([getTranslator(), getLocale(), searchParams]);

  const mayWrite = canMutate(session, 'fleet-operations', { requiresApprovedOrg: true });
  const isOwner = satisfiesFleetRole(session.fleetRole, 'owner');

  let vehicles: readonly FleetVehicle[] = [];
  let problem: ProblemDetails | null = null;
  try {
    const roster = await read<FleetVehicleList>({ org: VEHICLES });
    vehicles = roster.items ?? [];
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const selected = params.vehicle
    ? (vehicles.find((vehicle) => vehicle.vehicleId === params.vehicle) ?? null)
    : null;

  // Two conditional reads, and each is conditional for its own reason: the slots
  // because there is no vehicle to read them for until one is chosen, and the
  // payout profile because a Manager is forbidden from reading it at all.
  const [slots, paidAvailable] = await Promise.all([
    selected ? documentSlots(selected.vehicleId) : Promise.resolve([]),
    isOwner ? readPaidAvailability() : Promise.resolve(null),
  ]);

  const paidBlocked = paidAvailable === false ? t(PAID_SERVICE_PAYMENT_BLOCKED_KEY) : null;

  const typeOptions = ONBOARDABLE_VEHICLE_TYPES.map((value) => ({
    value,
    label: vehicleTypeLabel(value, t),
  }));

  const modeOptions = FLEET_MODES.map((value) => ({
    value,
    label: value === 'A' ? t('fleet.vehicles.mode.a') : t('fleet.vehicles.mode.b'),
  }));

  const singlePanel = (
    <div className="flex flex-col gap-md">
      {mayWrite ? (
        <AddVehicleForm
          types={typeOptions}
          modes={modeOptions}
          paidAvailable={paidAvailable}
          labels={{
            heading: t('fleet.vehicles.add.heading'),
            plate: t('fleet.vehicles.field.plate'),
            plateHint: t('fleet.vehicles.field.plateHint'),
            type: t('fleet.vehicles.field.type'),
            mode: t('fleet.vehicles.field.mode'),
            noTrain: t('fleet.vehicles.type.noTrain'),
            servicePayment: t('fleet.vehicles.field.servicePayment'),
            servicePaymentHint: t('fleet.vehicles.field.servicePaymentHint'),
            free: t('fleet.vehicles.servicePayment.free'),
            paid: t('fleet.vehicles.servicePayment.paid'),
            fare: t('fleet.vehicles.field.fare'),
            fareHint: t('fleet.vehicles.field.fareHint'),
            paidBlocked,
            required: t('fleet.org.required'),
            submit: t('fleet.vehicles.add.submit'),
            submitting: t('fleet.vehicles.add.submitting'),
          }}
        />
      ) : (
        <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
          {t('fleet.vehicles.viewerNotice')}
        </p>
      )}

      {selected ? (
        <>
          {selected.mode === 'B' && mayWrite ? (
            <ServicePaymentForm
              vehicleId={selected.vehicleId}
              current={selected.modeBBilling ?? null}
              currentFareMinor={selected.defaultMonthlyFareMinor ?? null}
              paidAvailable={paidAvailable}
              labels={{
                heading: t('fleet.vehicles.servicePayment.heading'),
                field: t('fleet.vehicles.field.servicePayment'),
                hint: t('fleet.vehicles.field.servicePaymentHint'),
                free: t('fleet.vehicles.servicePayment.free'),
                paid: t('fleet.vehicles.servicePayment.paid'),
                fare: t('fleet.vehicles.field.fare'),
                fareHint: t('fleet.vehicles.field.fareHint'),
                paidBlocked,
                submit: t('fleet.vehicles.servicePayment.save'),
                submitting: t('fleet.vehicles.servicePayment.saving'),
                saved: t('fleet.vehicles.servicePayment.saved'),
              }}
            />
          ) : null}

          {selected.mode !== 'B' ? (
            <p className="text-caption text-on-surface-variant">
              {t('fleet.vehicles.servicePayment.modeANote')}
            </p>
          ) : null}

          <VehicleDocumentPanel
            vehicle={selected}
            slots={slots}
            canUpload={mayWrite}
            locale={locale}
            t={t}
          />
        </>
      ) : (
        <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
          {t('fleet.vehicles.docs.chooseVehicle')}
        </p>
      )}
    </div>
  );

  const bulkPanel = mayWrite ? (
    <BulkVehicleImport
      accept={BULK_CSV_ACCEPT}
      labels={{
        heading: t('fleet.vehicles.bulk.heading'),
        prompt: t('fleet.vehicles.bulk.prompt'),
        hint: t('fleet.vehicles.bulk.hint', {
          rows: BULK_MAX_ROWS,
          megabytes: BULK_UPLOAD_MAX_BYTES / (1024 * 1024),
        }),
        columns: t('fleet.vehicles.bulk.columns', { columns: BULK_CSV_COLUMNS }),
        docsPending: t('fleet.vehicles.bulk.docsPending'),
        uploading: t('fleet.vehicles.bulk.uploading'),
        allImported: t('fleet.vehicles.bulk.allImported'),
        report: t('fleet.vehicles.bulk.report'),
        refresh: t('fleet.vehicles.bulk.refresh'),
        jobFailed: t('fleet.vehicles.bulk.jobFailed'),
        rejectedType: t('fleet.error.fileNotAccepted'),
        rejectedSize: t('fleet.vehicles.error.csvTooLarge', {
          megabytes: BULK_UPLOAD_MAX_BYTES / (1024 * 1024),
        }),
      }}
    />
  ) : (
    <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
      {t('fleet.vehicles.viewerNotice')}
    </p>
  );

  return (
    <div className="flex flex-col gap-md">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-title font-display">{t('fleet.vehicles.title')}</h2>
        <StatusPill tone="info" dot={false}>
          {t('fleet.vehicles.modesOnly')}
        </StatusPill>
      </div>

      <p className="text-caption text-on-surface-variant">{t('fleet.vehicles.modesOnlyNote')}</p>

      {problem ? <ProblemPanel problem={problem} /> : null}

      <Tabs
        label={t('fleet.vehicles.tabs')}
        items={[
          { value: 'single', label: t('fleet.vehicles.tab.single'), content: singlePanel },
          { value: 'bulk', label: t('fleet.vehicles.tab.bulk'), content: bulkPanel },
        ]}
      />

      <VehicleStatusTable
        rows={vehicles.map((vehicle) =>
          vehicleRow(vehicle, selected?.vehicleId ?? null, locale, t),
        )}
        labels={{
          heading: t('fleet.vehicles.table.heading'),
          caption: t('fleet.vehicles.table.caption'),
          plate: t('fleet.vehicles.column.plate'),
          type: t('fleet.vehicles.column.type'),
          servicePayment: t('fleet.vehicles.column.servicePayment'),
          documents: t('fleet.vehicles.column.documents'),
          status: t('fleet.vehicles.column.status'),
          empty: t('fleet.vehicles.table.empty'),
          manage: t('fleet.vehicles.manage'),
        }}
      />
    </div>
  );
}

/**
 * One vehicle's slots, or none.
 *
 * A failure here is deliberately **not** a problem panel: the roster rendered,
 * and a `404 vehicle-not-found` on a stale `?vehicle=` link should leave the
 * screen usable rather than replace it with an error. The panel then draws four
 * Missing slots, which is the honest reading of "nothing is known about this
 * vehicle's documents".
 */
async function documentSlots(vehicleId: string): Promise<readonly VehicleDocumentSlot[]> {
  try {
    const answer = await read<VehicleDocumentSlotList>({ org: vehicleDocumentsTarget(vehicleId) });
    return answer.items ?? [];
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return [];
  }
}

/**
 * BR-31.1's gate, for a caller allowed to read the profile it is a fact about.
 *
 * `404 payout-profile-not-found` is the state every organisation starts in and
 * means the same thing as unverified — {@link canSetPaidServicePayment} answers
 * `false` for `null`, and the caller sees the same sentence either way.
 */
async function readPaidAvailability(): Promise<boolean> {
  try {
    const profile = await read<PayoutProfile>({ org: PAYOUT_PROFILE });
    return canSetPaidServicePayment(profile);
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return false;
  }
}
