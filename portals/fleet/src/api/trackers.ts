import type { StatusTone } from '@mageride/ui';

import type { FleetMessageKey } from '@/i18n';

/**
 * **SCR-FP-006** — ST-901 tracker binding and the health rollup behind it
 * (US-13.12, US-3.13, T-09), transcribed from `backend/contracts/fleet.yaml`,
 * `fleet-health.yaml` and `provisioning.yaml`.
 *
 * ## Three services answer this one screen, and the paths are all org-scoped
 *
 * | what | route | owner |
 * |---|---|---|
 * | bind one IMEI | `POST …/trackers/bind` | fleet-svc |
 * | bind a CSV of them | `POST …/trackers/bulk` | provisioning-svc |
 * | the health of the fleet's devices | `GET …/health` | fleet-health-svc |
 *
 * All three sit under `/v1/fleets/{fleetId}`, so all three are `{ org: … }`
 * targets and the caller's own organisation is the only one addressable
 * (`./client`). The gateway resolves the cluster from the contract that declares
 * the operation, which is why the health rollup is not in `fleet.yaml` and why
 * this file reads three documents rather than one.
 *
 * ## The screen has no publish-cadence control, and that is not an omission
 *
 * `web_fleet.html` prints a "Cadence profile" column. **US-3.18 — "fleet
 * operators can configure per-vehicle publish cadence profiles" — has no route on
 * any contract**: the only cadence surface on the platform is the MQTT downlink
 * `veh/{vehicleId}/cmd` (US-3.17), which is a device topic and not an API. So the
 * column reports the standing cadence every Mode A/B session publishes at
 * ({@link PUBLISH_CADENCE}), and the caption says the per-vehicle profile is not
 * something this screen sets yet. Raised in the C113 handoff.
 */

/* ---------------------------------------------------------------------------
 * Targets
 * ------------------------------------------------------------------------ */

/** `POST /v1/fleets/{own id}/trackers/bind` — fleet-svc, one IMEI. */
export const TRACKER_BIND = '/trackers/bind';

/** `POST /v1/fleets/{own id}/trackers/bulk` — provisioning-svc, multipart CSV (T-09). */
export const TRACKER_BULK = '/trackers/bulk';

/** `GET /v1/fleets/{own id}/trackers/bulk/{jobId}` — the poll the 202 points at. */
export function trackerBulkJobTarget(jobId: string): string {
  return `${TRACKER_BULK}/${jobId}`;
}

/** `GET /v1/fleets/{own id}/health` — fleet-health-svc's rollup (US-3.13). */
export const FLEET_HEALTH = '/health';

/* ---------------------------------------------------------------------------
 * Binding
 * ------------------------------------------------------------------------ */

/** `provisioning.yaml#/components/schemas/Imei` — 15 digits, and only digits. */
export const IMEI_PATTERN = /^\d{15}$/;

export function isImei(value: string): boolean {
  return IMEI_PATTERN.test(value);
}

/**
 * Accepts the two ways an ST-901's identifier is printed on the label and
 * produces the fifteen digits the contract admits.
 *
 * The wireframe calls the field "IMEI / MAC" and the sticker is routinely
 * grouped — `86 1234 5678 9012 3` — or hyphenated in the MAC style. Separators
 * are stripped; **nothing else is inferred**. A value this cannot reduce to
 * fifteen digits comes back unchanged so {@link isImei} refuses it on the field
 * the operator is looking at, rather than being sent as a number nobody typed.
 */
export function normaliseImei(value: string): string {
  return value.replaceAll(/[\s.:-]/g, '');
}

/** The body `POST …/trackers/bind` takes. */
export interface BindTrackerBody {
  readonly imei: string;
  readonly vehicleId: string;
  /**
   * AL-32 / US-3.22 — arm tracker-driven journey auto-start. Defaults to `true`
   * on the wire, and the screen sends it explicitly so the checkbox and the
   * request agree even when it is turned off.
   */
  readonly autoStartSession: boolean;
}

/** What `POST …/trackers/bind` answers with — `fleet.yaml`'s narrow 201. */
export interface TrackerBinding {
  readonly bindingId: string;
  readonly imei: string;
  readonly vehicleId: string;
}

/**
 * `provisioning.yaml#/components/schemas/BulkJob` — **not** the vehicle import's
 * job. The two differ: this one counts `succeededRows` where the vehicle job
 * counts `importedRows`, and its report lists **every** row rather than only the
 * failures, "because an operator reconciling 5,000 trackers needs to diff the
 * report against the file they uploaded".
 */
export interface BulkTrackerJob {
  readonly jobId: string;
  readonly totalRows: number;
  readonly status: 'PROCESSING' | 'COMPLETED' | 'FAILED';
  readonly succeededRows?: number;
  readonly failedRows?: number;
  /** HMAC-signed and bearer-free, so the link is handed to the browser as it is. */
  readonly errorReportUrl?: string;
}

/** T-09/NFR-43's ceiling, and provisioning-svc's own upload cap. */
export const TRACKER_BULK_MAX_ROWS = 5000;
export const TRACKER_BULK_MAX_BYTES = 2 * 1024 * 1024;
export const TRACKER_BULK_CSV_ACCEPT = 'text/csv,.csv';
export const TRACKER_BULK_CSV_COLUMNS = 'imei,registrationNumber';

/**
 * `provisioning.yaml#/components/schemas/CredentialType` — what the batch mints.
 *
 * The choice belongs to the batch rather than to the row (C030's micro-change-set:
 * "a fleet is one hardware generation more often than not") and defaults to
 * `x509`, which is what an MQTT-capable tracker needs and what D6' §4.1 lists as
 * the current-firmware path. `psk` is the legacy TCP device behind an adapter.
 */
export const CREDENTIAL_TYPES = ['x509', 'psk'] as const;

export type CredentialType = (typeof CREDENTIAL_TYPES)[number];

export function isCredentialType(value: string): value is CredentialType {
  return (CREDENTIAL_TYPES as readonly string[]).includes(value);
}

/* ---------------------------------------------------------------------------
 * Health — fleet-health.yaml
 * ------------------------------------------------------------------------ */

/**
 * `fleet-health.yaml#/components/schemas/TrackerState` — US-3.13's four states,
 * which are thresholds on silence rather than a device saying anything.
 *
 * `decommissioned` is the one that is not about signal: it is a **revoked
 * credential** (US-3.8), which is why the screen renders it as the credential
 * column's answer and not as another shade of offline. A `QUARANTINED` binding
 * (T-08's anti-clone hold) is deliberately *not* decommissioned — it may return —
 * and no route on this portal's surface exposes it.
 */
export type TrackerState = 'online' | 'stale' | 'offline' | 'decommissioned';

/** `fleet-health.yaml#/components/schemas/TrackerHealth`. */
export interface TrackerHealth {
  readonly vehicleId: string;
  readonly imei?: string;
  readonly state: TrackerState;
  readonly online: boolean;
  /** Platform receive instant of the newest accepted sample — not the GNSS clock. */
  readonly lastSeen?: string;
  readonly since?: string;
  readonly battery?: number;
  readonly batteryMv?: number;
  readonly signalStrength?: number;
  readonly sats?: number;
}

export interface TrackerStateCounts {
  readonly online: number;
  readonly stale: number;
  readonly offline: number;
  readonly decommissioned: number;
  readonly total: number;
}

/**
 * The silence windows this answer was classified against, returned so a console
 * can label its own legend "instead of hardcoding US-3.13's 5 and 30 minutes,
 * and so a retuned deployment does not need a portal release".
 */
export interface HealthThresholds {
  readonly staleAfterSeconds: number;
  readonly offlineAfterSeconds: number;
}

/** `fleet-health.yaml#/components/schemas/FleetHealthRollup`, narrowed to SCR-FP-006. */
export interface FleetHealthRollup {
  readonly fleetId: string;
  readonly vehiclesOnline: number;
  readonly vehiclesOffline: number;
  readonly counts: TrackerStateCounts;
  readonly thresholds: HealthThresholds;
  readonly items: readonly TrackerHealth[];
  /** `items` hit `Health:MaxItems`; the counts still cover the whole fleet. */
  readonly itemsTruncated: boolean;
  readonly asOf: string;
}

/* ---------------------------------------------------------------------------
 * The cadence the platform actually publishes at
 * ------------------------------------------------------------------------ */

/**
 * US-5.5's adaptive rates for a Mode A/B session — the two numbers behind
 * `web_fleet.html`'s "4s / 10s" cadence column.
 *
 * These are the app's own publish rates, not a setting: US-5.5 fixes moving at
 * one call every 4 seconds and stationary at one every 10. The sketch prints a
 * third figure, which belongs to the on-demand plane's idle standby and is
 * therefore not a fleet vehicle's cadence at all — a fleet operates Mode A and
 * Mode B (AL-03), so the column shows the two rates that apply to it.
 */
export const PUBLISH_CADENCE = { movingSeconds: 4, stationarySeconds: 10 } as const;

/* ---------------------------------------------------------------------------
 * Rendering
 * ------------------------------------------------------------------------ */

export interface TrackerStateView {
  readonly tone: StatusTone;
  readonly labelKey: FleetMessageKey;
}

export function trackerStateView(state: TrackerState): TrackerStateView {
  switch (state) {
    case 'online':
      return { tone: 'success', labelKey: 'fleet.trackers.state.online' };
    case 'stale':
      return { tone: 'warning', labelKey: 'fleet.trackers.state.stale' };
    case 'offline':
      return { tone: 'error', labelKey: 'fleet.trackers.state.offline' };
    default:
      return { tone: 'neutral', labelKey: 'fleet.trackers.state.decommissioned' };
  }
}

/**
 * The credential column — bound and live, or revoked.
 *
 * There are only two answers the platform gives this screen. `decommissioned` is
 * US-3.8's revocation, enforced within a second by the shared Redis revocation
 * cache (T-12); everything else is a credential the broker is still accepting,
 * whatever the device is doing about it.
 */
export function credentialView(state: TrackerState): TrackerStateView {
  return state === 'decommissioned'
    ? { tone: 'error', labelKey: 'fleet.trackers.credential.revoked' }
    : { tone: 'success', labelKey: 'fleet.trackers.credential.active' };
}
