import { redirect } from 'next/navigation';

import { StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import {
  FLEET_HEALTH,
  PUBLISH_CADENCE,
  TRACKER_BULK_CSV_ACCEPT,
  TRACKER_BULK_CSV_COLUMNS,
  TRACKER_BULK_MAX_BYTES,
  TRACKER_BULK_MAX_ROWS,
  credentialView,
  trackerStateView,
  type FleetHealthRollup,
} from '@/api/trackers';
import { VEHICLES, type FleetVehicle, type FleetVehicleList } from '@/api/vehicles';
import { ProblemPanel } from '@/components/ProblemPanel';
import { BindTrackerForm } from '@/components/trackers/BindTrackerForm';
import { BulkTrackerImport } from '@/components/trackers/BulkTrackerImport';
import { TrackerTable, type TrackerRow } from '@/components/trackers/TrackerTable';
import { vehicleTypeLabel } from '@/components/vehicles/vehicle-model';
import { formatInstant, formatSince } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';
import type { FleetTranslator, Locale } from '@/i18n';
import { canMutate, isApproved } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-006 · `fleet_trackers`** — ST-901 binding, bulk IMEI onboarding and
 * the health rollup behind both (US-13.12, US-3.13, T-09).
 *
 * ## This screen opens for a PENDING organisation, and one of its controls does not
 *
 * The manifest marks the entry `requiresApprovedOrg: false` deliberately: the
 * health rollup this screen reads is fleet-health-svc's and carries no approval
 * gate, so blocking the screen would refuse a read the platform allows. The
 * *single* bind carries the gate on itself (`FleetOpsEndpoints`), so the form is
 * replaced with the sentence that says when it opens. The **bulk** route carries
 * no gate at all — provisioning-svc gates it on the canonical `fleet_owner` role
 * and nothing else — so it stays available, which looks inconsistent and is what
 * the two services actually declare. Raised in the C113 handoff.
 *
 * ## Three services, one table
 *
 * `GET …/health` (fleet-health-svc) is the tracker rows; `GET …/vehicles`
 * (fleet-svc) turns a `vehicleId` into a plate. The health read failing is the
 * screen failing and shows a problem panel; the roster failing costs the table
 * its plates and the form its picker, and neither is worth replacing the page
 * with.
 *
 * ## The cadence column is a fact, not a control
 *
 * US-3.18 asks for per-vehicle publish-cadence profiles and **no contract has a
 * route for one** — the only cadence surface on the platform is the MQTT downlink
 * `veh/{vehicleId}/cmd`, which is a device topic. So the column reports US-5.5's
 * standing rates and the caption says the profile cannot be set from here yet,
 * which is the C111/C112 rule for an affordance the wireframe draws and the
 * platform does not serve.
 */

export const dynamic = 'force-dynamic';

export default async function TrackersPage() {
  const session = await getSession();
  if (!session) redirect('/login');

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);

  const approved = isApproved(session);
  const mayBind = canMutate(session, 'tracker-binding', { requiresApprovedOrg: true });
  const mayBulkBind = canMutate(session, 'tracker-binding');

  let health: FleetHealthRollup | null = null;
  let problem: ProblemDetails | null = null;
  try {
    health = await read<FleetHealthRollup>({ org: FLEET_HEALTH });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const vehicles = await roster();
  const plates = new Map(vehicles.map((vehicle) => [vehicle.vehicleId, vehicle]));

  const cadence = t('fleet.trackers.cadence', {
    moving: PUBLISH_CADENCE.movingSeconds,
    stationary: PUBLISH_CADENCE.stationarySeconds,
  });

  const rows: TrackerRow[] = (health?.items ?? []).map((tracker, index) => {
    const state = trackerStateView(tracker.state);
    const credential = credentialView(tracker.state);
    const vehicle = plates.get(tracker.vehicleId);

    return {
      key: tracker.imei ?? `${tracker.vehicleId}-${index}`,
      imei: tracker.imei ?? '—',
      vehicle: vehicle
        ? `${vehicle.registrationNumber} · ${vehicleTypeLabel(vehicle.vehicleType, t)}`
        : t('fleet.trackers.unknownVehicle'),
      cadence,
      lastSeen: formatSince(locale, tracker.lastSeen) ?? t('fleet.trackers.never'),
      health: t(state.labelKey),
      healthTone: state.tone,
      credential: t(credential.labelKey),
      credentialTone: credential.tone,
    };
  });

  const pickable = vehicles.map((vehicle) => ({
    vehicleId: vehicle.vehicleId,
    label: `${vehicle.registrationNumber} · ${vehicleTypeLabel(vehicle.vehicleType, t)}`,
  }));

  return (
    <div className="flex flex-col gap-md">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-title font-display">{t('fleet.trackers.title')}</h2>
        {health ? (
          <StatusPill tone={counterTone(health)} dot={false}>
            {t('fleet.trackers.counts', {
              online: health.counts.online,
              stale: health.counts.stale,
              offline: health.counts.offline,
            })}
          </StatusPill>
        ) : null}
      </div>

      {problem ? <ProblemPanel problem={problem} /> : null}

      <div className="flex flex-col gap-md lg:flex-row lg:items-start">
        {mayBind ? (
          <BindTrackerForm
            vehicles={pickable}
            labels={{
              heading: t('fleet.trackers.bind.heading'),
              imei: t('fleet.trackers.field.imei'),
              imeiHint: t('fleet.trackers.field.imeiHint'),
              vehicle: t('fleet.trackers.field.vehicle'),
              autoStart: t('fleet.trackers.field.autoStart'),
              autoStartHint: t('fleet.trackers.field.autoStartHint'),
              required: t('fleet.org.required'),
              submit: t('fleet.trackers.bind.submit'),
              submitting: t('fleet.trackers.bind.submitting'),
              noVehicles: t('fleet.trackers.noVehicles'),
              done: (imei) => t('fleet.trackers.bind.done', { imei }),
            }}
          />
        ) : (
          <p className="flex-1 rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
            {/*
              Two different refusals, and the operator does two different things
              about them. An unapproved organisation is waiting on an officer;
              a Viewer's seat is never going to carry this.
            */}
            {approved ? t('fleet.trackers.viewerNotice') : t('fleet.trackers.bind.pendingOrg')}
          </p>
        )}

        {mayBulkBind ? (
          <BulkTrackerImport
            accept={TRACKER_BULK_CSV_ACCEPT}
            labels={{
              heading: t('fleet.trackers.bulk.heading'),
              prompt: t('fleet.trackers.bulk.prompt'),
              hint: t('fleet.trackers.bulk.hint', {
                rows: TRACKER_BULK_MAX_ROWS,
                megabytes: TRACKER_BULK_MAX_BYTES / (1024 * 1024),
              }),
              columns: t('fleet.trackers.bulk.columns', { columns: TRACKER_BULK_CSV_COLUMNS }),
              credentialType: t('fleet.trackers.bulk.credentialType'),
              credentialHint: t('fleet.trackers.bulk.credentialHint'),
              x509: t('fleet.trackers.bulk.credential.x509'),
              psk: t('fleet.trackers.bulk.credential.psk'),
              uploading: t('fleet.trackers.bulk.uploading'),
              processing: (total) => t('fleet.trackers.bulk.processing', { total }),
              bound: (succeeded, total) => t('fleet.trackers.bulk.bound', { succeeded, total }),
              someFailed: (failed) => t('fleet.trackers.bulk.someFailed', { failed }),
              report: t('fleet.trackers.bulk.report'),
              refresh: t('fleet.trackers.bulk.refresh'),
              jobFailed: t('fleet.trackers.bulk.jobFailed'),
              rejectedType: t('fleet.error.fileNotAccepted'),
              rejectedSize: t('fleet.vehicles.error.csvTooLarge', {
                megabytes: TRACKER_BULK_MAX_BYTES / (1024 * 1024),
              }),
            }}
          />
        ) : null}
      </div>

      <TrackerTable
        rows={rows}
        labels={{
          heading: t('fleet.trackers.table.heading'),
          caption: t('fleet.trackers.table.caption'),
          autoSession: t('fleet.trackers.autoSession'),
          imei: t('fleet.trackers.column.imei'),
          vehicle: t('fleet.trackers.column.vehicle'),
          cadence: t('fleet.trackers.column.cadence'),
          lastSeen: t('fleet.trackers.column.lastSeen'),
          health: t('fleet.trackers.column.health'),
          credential: t('fleet.trackers.column.credential'),
          empty: t('fleet.trackers.table.empty'),
          cadenceNote: t('fleet.trackers.cadenceNote'),
          thresholds: thresholdSentence(health, t),
          truncated: health?.itemsTruncated ? t('fleet.trackers.truncated') : null,
          asOf: asOfSentence(health, locale, t),
        }}
      />
    </div>
  );
}

/**
 * The counter chip's tone — the worst state the fleet is in.
 *
 * A fleet with six offline trackers and ninety-six online ones is a fleet with a
 * problem, and a green chip over that count would be the console reporting the
 * average rather than the exception.
 */
function counterTone(health: FleetHealthRollup): 'success' | 'warning' | 'error' {
  if (health.counts.offline > 0) return 'error';
  if (health.counts.stale > 0) return 'warning';
  return 'success';
}

/**
 * The legend, in the deployment's own thresholds.
 *
 * `HealthThresholds` is returned "so a dashboard can label its own legend instead
 * of hardcoding US-3.13's 5 and 30 minutes, and so a retuned deployment does not
 * need a portal release" — so the numbers come off the answer, and 5/30 appear
 * here only as the fallback for a rollup that could not be read at all.
 */
function thresholdSentence(health: FleetHealthRollup | null, t: FleetTranslator): string {
  return t('fleet.trackers.thresholds', {
    stale: Math.round((health?.thresholds.staleAfterSeconds ?? 300) / 60),
    offline: Math.round((health?.thresholds.offlineAfterSeconds ?? 1800) / 60),
  });
}

function asOfSentence(
  health: FleetHealthRollup | null,
  locale: Locale,
  t: FleetTranslator,
): string | null {
  const time = formatInstant(locale, health?.asOf);
  return time ? t('fleet.trackers.asOf', { time }) : null;
}

/** The roster, for the plates and the picker. See the page note. */
async function roster(): Promise<readonly FleetVehicle[]> {
  try {
    const answer = await read<FleetVehicleList>({ org: VEHICLES });
    return answer.items ?? [];
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return [];
  }
}
