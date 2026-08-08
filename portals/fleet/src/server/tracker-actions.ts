'use server';

import { revalidatePath } from 'next/cache';

import { mutate, read } from '@/api/client';
import { ProblemError } from '@/api/problem';
import {
  TRACKER_BIND,
  TRACKER_BULK,
  TRACKER_BULK_MAX_BYTES,
  isCredentialType,
  isImei,
  normaliseImei,
  trackerBulkJobTarget,
  type BindTrackerBody,
  type BulkTrackerJob,
  type TrackerBinding,
} from '@/api/trackers';
import { getTranslator } from '@/i18n/server';

/**
 * **SCR-FP-006's two writes** — bind one ST-901 to a vehicle, and bind a CSV of
 * them (US-13.12, T-09/US-3.2).
 *
 * ## The two writes are gated differently, and the portal follows each endpoint
 *
 * | | route | owner | URD row | approval gate |
 * |---|---|---|---|---|
 * | single | `POST …/trackers/bind` | fleet-svc | `tracker-binding` | **yes** |
 * | bulk | `POST …/trackers/bulk` | provisioning-svc | `tracker-binding` | **no** |
 *
 * `FleetOpsEndpoints` puts `RequireApprovedFleet()` on the single bind;
 * `FleetTrackerEndpoints` gates the bulk route on the canonical `fleet_owner`
 * role and nothing else. Guessing high on the second would refuse a write the
 * platform allows, which is the rule `RequiredGrant.requiresApprovedOrg`
 * documents, so the asymmetry is transcribed rather than smoothed over. It is
 * raised in the C113 handoff as something for the two services to agree on.
 *
 * ## The bulk route is behind D-30, and a browser cannot satisfy it
 *
 * `POST /v1/fleets/{fleetId}/trackers/bulk` carries `X-Attestation` on its
 * contract, so the gateway's `AttestationMiddleware` lists it as a sensitive
 * operation. A Play Integrity or App Attest verdict is something the Android and
 * iOS apps produce; a Fleet Portal request carries no `X-Platform` at all, and
 * the middleware's own comment — "a sensitive mutation always comes from one of
 * our apps" — is why it answers `401 attestation-failed`.
 *
 * The control is still drawn, because T-09's route is a fleet route in its own
 * contract ("Bulk IMEI onboarding **for fleet operators**"), the gate is a
 * deployment policy rather than a business rule, and it is `Disabled` in
 * development and the replica — so the screen works where it is being built and
 * demonstrated. What the portal does not do is pretend: `attestation-failed` gets
 * a sentence of its own that says a batch has to be run by MageRide for now.
 * Removing `XAttestation` from `bulkBindTrackers` is the micro-change-set this
 * component proposes and did not take.
 */

export interface TrackerActionState {
  readonly message?: string;
  readonly field?: 'imei' | 'vehicleId' | 'file';
  /** Set on a successful bind — the IMEI, so the panel can name what landed. */
  readonly bound?: string;
  readonly job?: BulkTrackerJob;
}

/** `tracker-binding` is URD §2.3's "Hardware trackers — bind to vehicle, bulk onboard, fleet health". */
const BIND_REQUIRES = { area: 'tracker-binding', requiresApprovedOrg: true } as const;

/** The same row, and **no** approval gate — provisioning-svc declares none. */
const BULK_REQUIRES = { area: 'tracker-binding' } as const;

function text(formData: FormData, name: string): string {
  return String(formData.get(name) ?? '').trim();
}

/**
 * `POST /v1/fleets/{id}/trackers/bind` — US-13.12.
 *
 * fleet-svc delegates the credential mint to provisioning-svc (T-02) and, for a
 * hardware-tracked Mode A vehicle, arms tracker-driven journey auto-start
 * (AL-32) — "a bus broadcasts irrespective of any driver app" (T-11). That is
 * what the auto-session checkbox turns on, and it is on by default because it is
 * the reason an operator fits an ST-901 to a bus in the first place.
 */
export async function bindTracker(
  _state: TrackerActionState,
  formData: FormData,
): Promise<TrackerActionState> {
  const t = await getTranslator();

  const imei = normaliseImei(text(formData, 'imei'));
  if (!isImei(imei)) return { message: t('fleet.trackers.error.imeiInvalid'), field: 'imei' };

  const vehicleId = text(formData, 'vehicleId');
  if (!vehicleId) return { message: t('fleet.trackers.error.vehicleRequired'), field: 'vehicleId' };

  const body: BindTrackerBody = {
    imei,
    vehicleId,
    autoStartSession: formData.get('autoStartSession') !== null,
  };

  try {
    await mutate<TrackerBinding, BindTrackerBody>({
      method: 'POST',
      org: TRACKER_BIND,
      body,
      requires: BIND_REQUIRES,
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey), ...(error.code === 'imei-duplicate' ? { field: 'imei' as const } : {}) };
  }

  revalidatePath('/trackers');

  return { bound: imei };
}

/**
 * `POST /v1/fleets/{id}/trackers/bulk` — T-09's CSV of `imei,registrationNumber`.
 *
 * `credentialType` belongs to the batch rather than to the row and defaults to
 * `x509` (C030's micro-change-set). An unrecognised value is dropped rather than
 * refused, so the request goes with provisioning-svc's own default instead of a
 * `400` about a field the operator did not think they were setting.
 */
export async function importTrackerCsv(
  _state: TrackerActionState,
  formData: FormData,
): Promise<TrackerActionState> {
  const t = await getTranslator();

  const file = formData.get('file');
  if (!(file instanceof File) || file.size === 0) {
    return { message: t('fleet.trackers.error.csvRequired'), field: 'file' };
  }
  if (file.size > TRACKER_BULK_MAX_BYTES) {
    return {
      message: t('fleet.vehicles.error.csvTooLarge', {
        megabytes: TRACKER_BULK_MAX_BYTES / (1024 * 1024),
      }),
      field: 'file',
    };
  }

  const upstream = new FormData();
  upstream.set('file', file);

  const credentialType = text(formData, 'credentialType');
  if (isCredentialType(credentialType)) upstream.set('credentialType', credentialType);

  let job: BulkTrackerJob;
  try {
    const outcome = await mutate<BulkTrackerJob, FormData>({
      method: 'POST',
      org: TRACKER_BULK,
      body: upstream,
      requires: BULK_REQUIRES,
    });
    job = outcome.data;
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey), field: 'file' };
  }

  revalidatePath('/trackers');

  return { job };
}

/** `GET /v1/fleets/{id}/trackers/bulk/{jobId}` — the poll, as an action. */
export async function readTrackerBulkJob(jobId: string): Promise<TrackerActionState> {
  const t = await getTranslator();

  try {
    const job = await read<BulkTrackerJob>({ org: trackerBulkJobTarget(jobId) });
    if (job.status !== 'PROCESSING') revalidatePath('/trackers');
    return { job };
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }
}
