import { redirect } from 'next/navigation';

import { StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import { ASSIGNMENTS, type AssignmentList } from '@/api/drivers';
import { FLEET_MAP, MAP_STALE_AFTER_SECONDS, type FleetMapAnswer } from '@/api/insights';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { FLEET_HEALTH, trackerStateView, type FleetHealthRollup } from '@/api/trackers';
import { VEHICLES, vehicleAccentClass, type FleetVehicleList } from '@/api/vehicles';
import { ProblemPanel } from '@/components/ProblemPanel';
import { FleetMap, type MapVehicle } from '@/components/map/FleetMap';
import { FleetOverlayTable } from '@/components/map/FleetOverlayTable';
import { UnknownVehiclePanel, VehicleDetailPanel } from '@/components/map/VehicleDetailPanel';
import {
  detailFacts,
  fleetMapVehicles,
  isLive,
  overlayRow,
  vehicleAccentHex,
} from '@/components/map/map-model';
import { vehicleTypeLabel } from '@/components/vehicles/vehicle-model';
import { mapStyleUrl } from '@/config/env';
import { formatInstant } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';
import type { FleetTranslator, Locale } from '@/i18n';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-007 · `fleet_map`** — the org-scoped live map and its fleet-health
 * overlay (US-13.3, T-06, T-10).
 *
 * ## "Only this org's vehicles" is three fences, and the portal is the weakest
 *
 * `GET …/map` reads `telemetry.positions_fleet`, a security-barrier view filtered
 * on the `app.fleet_id` GUC under `SET LOCAL ROLE mageride_fleet_reader` — "which
 * is fail-closed: an unscoped connection sees zero rows rather than an error a
 * caller might retry unscoped" (`fleet.yaml`, C006). `FleetAccessFilter` re-reads
 * the caller's seat before that. This side's contribution is smaller and is still
 * worth having: `read({ org: FLEET_MAP })` resolves against the session's own
 * `fleetId`, and there is no parameter, prop or query string by which this screen
 * could name another organisation (`@/api/client`; `test/fences.test.ts`
 * enumerates the tree to keep it that way). The Definition of Done's "verified
 * against a second org's data" is C059's `RowLevelSecurityTests`, which connects
 * as a real non-superuser login with none of this code in the path.
 *
 * ## The screen opens for a PENDING organisation, and loses two columns
 *
 * `FleetOpsGroup` carries no approval gate — "US-13.A7 disables onboarding and
 * assignment, not monitoring, and an organisation waiting on a Verification
 * Officer still has to be able to watch the vehicles it already runs". The roster
 * and the assignments do carry it (`FleetVehiclesGroup`, `FleetAssignmentsGroup`),
 * so for a pending org those two reads answer 403, and the table falls back to the
 * plate the map answer itself carries with no type and no driver. A poorer table,
 * not a refused screen.
 *
 * ## What is not here
 *
 * **No alerting.** Route-deviation and geofence alerts are US-13.5, Phase 3, and
 * this component's own fence says not to build them: nothing on this screen
 * watches a boundary, and the dashboard renders the empty state `GET …/alerts` was
 * mapped for. **No polling.** `dynamic = 'force-dynamic'` makes every navigation a
 * fresh read and the caption says how old the answer is; a live push would be
 * `/hubs/live`, D3' §3.1's entitled SignalR surface, which this portal is not on.
 */

export const dynamic = 'force-dynamic';

export default async function MapPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const session = await getSession();
  if (!session) redirect('/login');

  const [t, locale, query] = await Promise.all([getTranslator(), getLocale(), searchParams]);

  const requested = query['vehicle'];
  const selectedVehicleId = typeof requested === 'string' && requested ? requested : null;

  let answer: FleetMapAnswer | null = null;
  let problem: ProblemDetails | null = null;
  try {
    answer = await read<FleetMapAnswer>({ org: FLEET_MAP });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  // Three labelling reads. Each one failing costs a column, so none of them may
  // take the screen down with it — the rule SCR-FP-006 applies to its own roster.
  const [health, vehicles, assignments] = await Promise.all([
    optional<FleetHealthRollup>(FLEET_HEALTH),
    optional<FleetVehicleList>(VEHICLES),
    optional<AssignmentList>(ASSIGNMENTS),
  ]);

  const rows = fleetMapVehicles({
    positions: answer?.vehicles ?? [],
    health: health?.items ?? [],
    vehicles: vehicles?.items ?? [],
    assignments: assignments?.items ?? [],
  });

  const plotted: MapVehicle[] = rows.flatMap((row) =>
    row.position
      ? [
          {
            vehicleId: row.vehicleId,
            lng: row.position.lng,
            lat: row.position.lat,
            color: vehicleAccentHex(row.vehicle?.vehicleType),
            live: isLive(row),
          },
        ]
      : [],
  );

  const selected = selectedVehicleId
    ? (rows.find((row) => row.vehicleId === selectedVehicleId) ?? null)
    : null;

  const detailLabels = {
    heading: t('fleet.map.detail.heading'),
    close: t('fleet.map.detail.close'),
    unknown: t('fleet.map.detail.unknown'),
  };

  const styleUrl = mapStyleUrl();

  return (
    <div className="flex flex-col gap-md">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-title font-display">{t('fleet.map.title')}</h2>
        {health ? (
          <>
            <StatusPill tone="success" dot={false}>
              {t('fleet.map.count.online', { count: health.counts.online })}
            </StatusPill>
            <StatusPill tone="warning" dot={false}>
              {t('fleet.map.count.stale', { count: health.counts.stale })}
            </StatusPill>
            <StatusPill tone="error" dot={false}>
              {t('fleet.map.count.offline', { count: health.counts.offline })}
            </StatusPill>
          </>
        ) : null}
      </div>

      {problem ? <ProblemPanel problem={problem} /> : null}

      {/*
        Keyed on the locale: MapLibre takes its own UI strings once, at
        construction, so switching language has to give it a new instance rather
        than leave English zoom buttons on a Sinhala console.
      */}
      <FleetMap
        key={locale}
        vehicles={plotted}
        selectedVehicleId={selected?.position ? selected.vehicleId : null}
        styleUrl={styleUrl}
        labels={{
          region: t('fleet.map.region'),
          empty: t('fleet.map.noPositions', { minutes: MAP_STALE_AFTER_SECONDS / 60 }),
          zoomIn: t('fleet.map.zoomIn'),
          zoomOut: t('fleet.map.zoomOut'),
          attribution: t('fleet.map.attribution'),
          metres: t('fleet.map.unit.metres'),
          kilometres: t('fleet.map.unit.kilometres'),
        }}
      />

      {styleUrl ? null : (
        <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
          {t('fleet.map.noBasemap')}
        </p>
      )}

      {selectedVehicleId && !selected ? <UnknownVehiclePanel labels={detailLabels} /> : null}

      {selected ? (
        <VehicleDetailPanel
          plate={
            selected.vehicle?.registrationNumber ??
            selected.position?.registrationNumber ??
            selected.vehicleId
          }
          type={
            selected.vehicle
              ? t('fleet.vehicles.typeWithMode', {
                  type: vehicleTypeLabel(selected.vehicle.vehicleType, t),
                  mode: selected.vehicle.mode,
                })
              : null
          }
          accentClass={vehicleAccentClass(selected.vehicle?.vehicleType ?? '')}
          health={
            selected.health
              ? t(trackerStateView(selected.health.state).labelKey)
              : t('fleet.map.noTracker')
          }
          healthTone={selected.health ? trackerStateView(selected.health.state).tone : 'neutral'}
          facts={detailFacts(selected, locale, t)}
          labels={detailLabels}
        />
      ) : null}

      <FleetOverlayTable
        rows={rows.map((row) => overlayRow(row, selectedVehicleId, t))}
        labels={{
          heading: t('fleet.map.overlay.heading'),
          caption: t('fleet.map.overlay.caption'),
          vehicle: t('fleet.map.column.vehicle'),
          driver: t('fleet.map.column.driver'),
          speed: t('fleet.map.column.speed'),
          battery: t('fleet.map.column.battery'),
          health: t('fleet.map.column.health'),
          empty: t('fleet.map.overlay.empty'),
          scoping: t('fleet.map.scoping'),
          // The two windows the screen is read through, in the deployment's own
          // numbers: fleet-health-svc returns its thresholds "so a dashboard can
          // label its own legend instead of hardcoding US-3.13's 5 and 30 minutes".
          windows: t('fleet.map.windows', {
            map: MAP_STALE_AFTER_SECONDS / 60,
            stale: Math.round((health?.thresholds.staleAfterSeconds ?? 300) / 60),
            offline: Math.round((health?.thresholds.offlineAfterSeconds ?? 1800) / 60),
          }),
          truncated: health?.itemsTruncated ? t('fleet.map.truncated') : null,
          asOf: asOfSentence(answer, locale, t),
        }}
      />
    </div>
  );
}

/**
 * A read whose failure costs a column rather than the screen.
 *
 * `null` on any problem — including the `403 fleet-not-approved` a pending
 * organisation gets from the roster and the assignments, which is not an error
 * state anybody has to act on here: those two screens are already hidden from the
 * nav for exactly that reason.
 */
async function optional<T>(org: string): Promise<T | null> {
  try {
    return await read<T>({ org });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return null;
  }
}

function asOfSentence(
  answer: FleetMapAnswer | null,
  locale: Locale,
  t: FleetTranslator,
): string | null {
  const time = formatInstant(locale, answer?.asOf);
  return time ? t('fleet.map.asOf', { time }) : null;
}
