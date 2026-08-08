'use server';

import { revalidatePath } from 'next/cache';

import { mutate, read } from '@/api/client';
import { ProblemError } from '@/api/problem';
import {
  BULK_UPLOAD_MAX_BYTES,
  FLEET_MODES,
  ONBOARDABLE_VEHICLE_TYPES,
  SERVICE_PAYMENTS,
  VEHICLES,
  VEHICLES_BULK,
  VEHICLE_DOCUMENT_MAX_BYTES,
  bulkJobTarget,
  fareMinorFrom,
  isVehicleDocumentUploadKind,
  vehicleClassificationTarget,
  vehicleDocumentsTarget,
  type AddVehicleBody,
  type BulkVehicleJob,
  type ClassificationBody,
  type FleetMode,
  type FleetVehicle,
  type ServicePayment,
  type VehicleDocumentSlot,
} from '@/api/vehicles';
import { getTranslator } from '@/i18n/server';

/**
 * **SCR-FP-004's four writes** — add a vehicle, set its Service payment, fill one
 * of AL-50's named slots, and import a CSV of them.
 *
 * ## Every one of them declares the same row and the same gate
 *
 * `fleet-operations` write, `requiresApprovedOrg: true`. Both halves are
 * transcribed rather than chosen: the URD §2.3 row is the one
 * `RequireFeature(FeatureAreas.FleetOperations)` names on these endpoints, and
 * the approval gate is on `FleetEndpoints.FleetVehiclesGroup` itself — "the whole
 * group does, the list as well as the writes". A Viewer's evaluated permissions
 * carry no `write` in that row, so `mutate()` refuses before a request leaves,
 * and a PENDING organisation cannot reach this screen at all.
 *
 * ## The Service payment gate is checked twice and owned once
 *
 * BR-31.1 makes `paid` a `409 payout-profile-not-verified` while the
 * organisation's payout profile is unverified. `canSetPaidServicePayment` in
 * `@/api/payout` is the predicate the *screen* disables the option with; this
 * file does not repeat it — it lets the service refuse and translates the code —
 * because a portal that pre-empted the check would have to re-read the profile on
 * every submit and could still be a second behind an officer's decision.
 *
 * ## Money crosses this boundary in minor units and nowhere else
 *
 * The form takes rupees, because that is what an operator types on a fare board;
 * {@link fareMinorFrom} multiplies by 100 exactly once, here, and every value on
 * the wire and in `registry.vehicles.default_monthly_fare_minor` is an integer
 * number of cents (CLAUDE.md, Money as minor units).
 */

export interface VehicleActionState {
  /** The failure, already translated. */
  readonly message?: string;
  readonly field?: 'registrationNumber' | 'vehicleType' | 'mode' | 'fare' | 'file';
  /** Set on success — the plate, so the panel can name what it created. */
  readonly added?: string;
  /** The new vehicle's id, so the document slots can attach to it without a reload. */
  readonly vehicleId?: string;
  readonly saved?: true;
  /** Set by the document upload, so the slot can confirm which one landed. */
  readonly uploaded?: string;
  /** Set by the bulk import, and by every poll of it afterwards. */
  readonly job?: BulkVehicleJob;
}

const REQUIRES = { area: 'fleet-operations', requiresApprovedOrg: true } as const;

function text(formData: FormData, name: string): string {
  return String(formData.get(name) ?? '').trim();
}

/* ---------------------------------------------------------------------------
 * One vehicle
 * ------------------------------------------------------------------------ */

/**
 * `POST /v1/fleets/{id}/vehicles` — US-13.1's single entry, with US-13.1b's
 * Service payment inline.
 *
 * The classification travels **in the create body** rather than as a second call.
 * `fleet.yaml` admits it there ("a Mode B vehicle may carry its Service payment
 * classification and default monthly fare inline, AL-24 item 16b"), and
 * `FleetVehicleService` runs it as a second transaction with its own gate — so a
 * `409 payout-profile-not-verified` leaves the vehicle created and unclassified,
 * which is the state the status table renders as "Not set" and the operator fixes
 * with the control beside it. Sending two requests from here would produce the
 * same outcome with one more way to fail.
 */
export async function addVehicle(
  _state: VehicleActionState,
  formData: FormData,
): Promise<VehicleActionState> {
  const t = await getTranslator();

  const registrationNumber = text(formData, 'registrationNumber');
  const vehicleType = text(formData, 'vehicleType');
  const mode = text(formData, 'mode');

  if (!registrationNumber) {
    return { message: t('fleet.vehicles.error.plateRequired'), field: 'registrationNumber' };
  }
  if (!(ONBOARDABLE_VEHICLE_TYPES as readonly string[]).includes(vehicleType)) {
    return { message: t('fleet.vehicles.error.typeRequired'), field: 'vehicleType' };
  }
  if (!(FLEET_MODES as readonly string[]).includes(mode)) {
    return { message: t('fleet.vehicles.error.modeRequired'), field: 'mode' };
  }

  const servicePayment = text(formData, 'modeBBilling');
  const classification = classificationFrom(mode as FleetMode, servicePayment, text(formData, 'fare'));
  if ('error' in classification) return { message: t(classification.error), field: 'fare' };

  const body: AddVehicleBody = {
    registrationNumber,
    vehicleType,
    mode: mode as FleetMode,
    ...classification.value,
  };

  let created: FleetVehicle;
  try {
    const outcome = await mutate<FleetVehicle, AddVehicleBody>({
      method: 'POST',
      org: VEHICLES,
      body,
      requires: REQUIRES,
    });
    created = outcome.data;
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  revalidatePath('/vehicles');

  return { added: created.registrationNumber, vehicleId: created.vehicleId };
}

/**
 * `PUT /v1/fleets/{id}/vehicles/{vehicleId}/classification` — US-27.4's
 * "Service payment", on a vehicle that already exists.
 *
 * The path and the field are `classification` and `modeBBilling` because AL-51
 * renamed the label and nothing else: "the API path and the DB column are
 * unchanged" is the fence, so the rename lives in the resource table and stops
 * there.
 */
export async function setServicePayment(
  _state: VehicleActionState,
  formData: FormData,
): Promise<VehicleActionState> {
  const t = await getTranslator();

  const vehicleId = text(formData, 'vehicleId');
  if (!vehicleId) return { message: t('fleet.vehicles.error.vehicleRequired') };

  const servicePayment = text(formData, 'modeBBilling');
  if (!(SERVICE_PAYMENTS as readonly string[]).includes(servicePayment)) {
    return { message: t('fleet.vehicles.error.servicePaymentRequired') };
  }

  const fareMinor = fareMinorFrom(text(formData, 'fare'));
  if (servicePayment === 'paid' && (fareMinor === null || fareMinor <= 0)) {
    return { message: t('fleet.vehicles.error.fareRequired'), field: 'fare' };
  }

  const body: ClassificationBody = {
    modeBBilling: servicePayment as ServicePayment,
    ...(servicePayment === 'paid' && fareMinor !== null
      ? { defaultMonthlyFareMinor: fareMinor }
      : {}),
  };

  try {
    await mutate<FleetVehicle, ClassificationBody>({
      method: 'PUT',
      org: vehicleClassificationTarget(vehicleId),
      body,
      requires: REQUIRES,
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  revalidatePath('/vehicles');

  return { saved: true };
}

/* ---------------------------------------------------------------------------
 * AL-50's named slots
 * ------------------------------------------------------------------------ */

/**
 * `POST /v1/fleets/{id}/vehicles/{vehicleId}/documents` — multipart, `kind` +
 * `file`, one of **four** kinds and no others.
 *
 * The kind is checked against {@link isVehicleDocumentUploadKind} rather than
 * passed through, and that is the AL-50 fence at the last point it can be held:
 * the slot components each send their own literal, so a fifth kind cannot be
 * introduced by a form control, and a value that is not one of the four is
 * refused here rather than turned into a `validation-failed` from fleet-svc about
 * a field the operator never saw.
 *
 * The **file is not read here** — it goes onto a `FormData` and is handed to the
 * data layer, which sends the multipart body it already is. Buffering it to
 * inspect it would put the bytes in memory on a hop whose only job is to carry
 * them, and every check worth making is fleet-svc's: the kind against the enum,
 * the mode against the slot (a route permit on a Mode B vehicle is refused, US-27.3),
 * and the size against `Fleet:DocumentMaxBytes` **as the bytes arrive**.
 */
export async function uploadVehicleDocument(
  _state: VehicleActionState,
  formData: FormData,
): Promise<VehicleActionState> {
  const t = await getTranslator();

  const vehicleId = text(formData, 'vehicleId');
  if (!vehicleId) return { message: t('fleet.vehicles.error.vehicleRequired') };

  const kind = text(formData, 'kind');
  if (!isVehicleDocumentUploadKind(kind)) {
    return { message: t('fleet.vehicles.error.kindRequired') };
  }

  const file = formData.get('file');
  if (!(file instanceof File) || file.size === 0) {
    return { message: t('fleet.vehicles.error.fileRequired'), field: 'file' };
  }
  if (file.size > VEHICLE_DOCUMENT_MAX_BYTES) {
    return {
      message: t('fleet.error.fileTooLarge', {
        megabytes: VEHICLE_DOCUMENT_MAX_BYTES / (1024 * 1024),
      }),
      field: 'file',
    };
  }

  const upstream = new FormData();
  upstream.set('kind', kind);
  upstream.set('file', file);

  // What the operator typed on the slot, used only when extraction returns no
  // expiry of its own — fleet-svc's own rule, and the reason it is optional here.
  const expiresAt = text(formData, 'expiresAt');
  if (expiresAt) upstream.set('expiresAt', expiresAt);

  try {
    await mutate<VehicleDocumentSlot, FormData>({
      method: 'POST',
      org: vehicleDocumentsTarget(vehicleId),
      body: upstream,
      requires: REQUIRES,
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  revalidatePath('/vehicles');

  return { uploaded: kind };
}

/* ---------------------------------------------------------------------------
 * The bulk import
 * ------------------------------------------------------------------------ */

/**
 * `POST /v1/fleets/{id}/vehicles/bulk` — US-13.1's CSV, answered `202` with a job.
 *
 * **A partial import is a success and is reported as one.** The contract is
 * explicit: "`COMPLETED` with `failedRows > 0` is a partial import with a
 * downloadable report, not a failure — `FAILED` is reserved for a job that could
 * not be processed at all". So the good rows land, the bad ones are in
 * `errorReportUrl`, and this action returns the job rather than an error string.
 */
export async function importVehicleCsv(
  _state: VehicleActionState,
  formData: FormData,
): Promise<VehicleActionState> {
  const t = await getTranslator();

  const file = formData.get('file');
  if (!(file instanceof File) || file.size === 0) {
    return { message: t('fleet.vehicles.error.csvRequired'), field: 'file' };
  }
  if (file.size > BULK_UPLOAD_MAX_BYTES) {
    return {
      message: t('fleet.vehicles.error.csvTooLarge', {
        megabytes: BULK_UPLOAD_MAX_BYTES / (1024 * 1024),
      }),
      field: 'file',
    };
  }

  const upstream = new FormData();
  upstream.set('file', file);

  let job: BulkVehicleJob;
  try {
    const outcome = await mutate<BulkVehicleJob, FormData>({
      method: 'POST',
      org: VEHICLES_BULK,
      body: upstream,
      requires: REQUIRES,
    });
    job = outcome.data;
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey), field: 'file' };
  }

  revalidatePath('/vehicles');

  return { job };
}

/**
 * `GET /v1/fleets/{id}/vehicles/bulk/{jobId}` — the poll, as an action so the
 * panel can ask again without a navigation.
 *
 * A read rather than a mutation, so it carries no `Idempotency-Key` and no
 * `requires`: `getBulkVehicleJob` is `RequireFleetSubRole(Viewer)`, and a Viewer
 * who cannot start an import can still watch one somebody else started.
 */
export async function readBulkJob(jobId: string): Promise<VehicleActionState> {
  const t = await getTranslator();

  try {
    const job = await read<BulkVehicleJob>({ org: bulkJobTarget(jobId) });
    if (job.status !== 'PROCESSING') revalidatePath('/vehicles');
    return { job };
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }
}

/* ---------------------------------------------------------------------------
 * The Service payment half of the create body
 * ------------------------------------------------------------------------ */

type ClassificationPart =
  | { readonly value: Pick<AddVehicleBody, 'modeBBilling' | 'defaultMonthlyFareMinor'> }
  | { readonly error: 'fleet.vehicles.error.fareRequired' | 'fleet.vehicles.error.servicePaymentModeA' };

/**
 * What the create body carries for Service payment, or which sentence to show.
 *
 * **A Mode A vehicle carries nothing**, and a Mode A vehicle *with* a setting is
 * refused rather than trimmed: `registry.vehicles.mode_b_billing` is NULL for
 * Mode A by design and `ClassificationService` answers `400` for it, so a portal
 * that silently dropped the field would hide a form the operator filled in wrong.
 * The screen does not draw the control for Mode A in the first place; this is the
 * check that makes that structural.
 */
function classificationFrom(
  mode: FleetMode,
  servicePayment: string,
  fare: string,
): ClassificationPart {
  if (servicePayment === '') return { value: {} };

  if (mode !== 'B') return { error: 'fleet.vehicles.error.servicePaymentModeA' };
  if (!(SERVICE_PAYMENTS as readonly string[]).includes(servicePayment)) return { value: {} };

  if (servicePayment === 'free') return { value: { modeBBilling: 'free' } };

  const fareMinor = fareMinorFrom(fare);
  if (fareMinor === null || fareMinor <= 0) return { error: 'fleet.vehicles.error.fareRequired' };

  return { value: { modeBBilling: 'paid', defaultMonthlyFareMinor: fareMinor } };
}
