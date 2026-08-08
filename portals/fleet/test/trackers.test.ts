import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  CREDENTIAL_TYPES,
  FLEET_HEALTH,
  PUBLISH_CADENCE,
  TRACKER_BIND,
  TRACKER_BULK,
  TRACKER_BULK_CSV_COLUMNS,
  TRACKER_BULK_MAX_ROWS,
  credentialView,
  isImei,
  normaliseImei,
  trackerBulkJobTarget,
  trackerStateView,
} from '@/api/trackers';

import { FLEET_CONTRACT, contractEnum } from './support/fleet';

/**
 * **SCR-FP-006 against the three contracts it reads.**
 *
 * The screen is unusual on this portal in being served by three services, so the
 * vocabulary is pinned against all three — and the two facts that are easiest to
 * get wrong are asserted as facts: the four health states are US-3.13's, and
 * `decommissioned` means a **revoked credential** rather than another shade of
 * offline.
 */

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const HEALTH_CONTRACT = join(REPO_ROOT, 'backend/contracts/fleet-health.yaml');
const PROVISIONING_CONTRACT = join(REPO_ROOT, 'backend/contracts/provisioning.yaml');
const GATEWAY_SETTINGS = join(REPO_ROOT, 'backend/src/ApiGateway/appsettings.json');

const FLEET = readFileSync(FLEET_CONTRACT, 'utf8');
const HEALTH = readFileSync(HEALTH_CONTRACT, 'utf8');
const PROVISIONING = readFileSync(PROVISIONING_CONTRACT, 'utf8');

describe('the IMEI is fifteen digits, however the label prints them', () => {
  it('matches provisioning-svc’s own pattern', () => {
    expect(PROVISIONING).toContain("pattern: '^\\d{15}$'");
    expect(isImei('861234567890123')).toBe(true);
    expect(isImei('86123456789012')).toBe(false);
    expect(isImei('86123456789012A')).toBe(false);
  });

  it('strips the separators a sticker is grouped with, and infers nothing else', () => {
    expect(normaliseImei('86 1234 5678 9012 3')).toBe('861234567890123');
    expect(normaliseImei('86-12-34-56-78-90-12-3')).toBe('861234567890123');
    expect(normaliseImei('86:12:34:56:78:90:123')).toBe('861234567890123');

    // A value this cannot reduce comes back unchanged, so `isImei` refuses it on
    // the field rather than a guess reaching the platform.
    expect(normaliseImei('not an imei')).toBe('notanimei');
    expect(isImei(normaliseImei('not an imei'))).toBe(false);
  });
});

describe('US-3.13 — four states, and one of them is about a credential', () => {
  it('renders every state fleet-health-svc can answer with', () => {
    const states = contractEnum(HEALTH, 'online,');
    expect(states).toEqual(['online', 'stale', 'offline', 'decommissioned']);

    for (const state of states) {
      const view = trackerStateView(state as 'online');
      expect(view.labelKey.startsWith('fleet.trackers.state.'), state).toBe(true);
    }

    expect(trackerStateView('online').tone).toBe('success');
    expect(trackerStateView('stale').tone).toBe('warning');
    expect(trackerStateView('offline').tone).toBe('error');
    expect(trackerStateView('decommissioned').tone).toBe('neutral');
  });

  it('reads `decommissioned` as a revoked credential and nothing else as one', () => {
    // US-3.8's revocation, not another shade of offline — a table that folded
    // the two together would report a signal problem where somebody retired a
    // device. A T-08 quarantine is deliberately *not* this state.
    expect(credentialView('decommissioned').labelKey).toBe('fleet.trackers.credential.revoked');
    for (const state of ['online', 'stale', 'offline'] as const) {
      expect(credentialView(state).labelKey, state).toBe('fleet.trackers.credential.active');
    }

    expect(HEALTH).toContain('`decommissioned` is a revoked credential (US-3.8), not a T-08');
  });
});

describe('the batch is provisioning-svc’s and the single bind is fleet-svc’s', () => {
  it('offers the two credential types, defaulting to the current-firmware path', () => {
    expect([...CREDENTIAL_TYPES]).toEqual(contractEnum(PROVISIONING, 'x509,'));
    expect(CREDENTIAL_TYPES[0]).toBe('x509');
  });

  it('reads T-09’s row shape and its cap off the contract', () => {
    expect(TRACKER_BULK_CSV_COLUMNS).toBe('imei,registrationNumber');
    expect(PROVISIONING).toContain('rows `imei,registrationNumber`, at most 5,000');
    expect(TRACKER_BULK_MAX_ROWS).toBe(5000);
  });

  it('addresses all three services inside the caller’s own organisation', () => {
    expect(TRACKER_BIND).toBe('/trackers/bind');
    expect(TRACKER_BULK).toBe('/trackers/bulk');
    expect(FLEET_HEALTH).toBe('/health');
    expect(trackerBulkJobTarget('01JQ')).toBe('/trackers/bulk/01JQ');

    for (const target of [TRACKER_BIND, TRACKER_BULK, FLEET_HEALTH]) {
      expect(target.startsWith('/v1/'), target).toBe(false);
    }

    // Each is declared by a different document, which is how the gateway
    // resolves the cluster — and why this file reads three of them.
    expect(FLEET).toContain('/v1/fleets/{fleetId}/trackers/bind');
    expect(PROVISIONING).toContain('/v1/fleets/{fleetId}/trackers/bulk');
    expect(HEALTH).toContain('/v1/fleets/{fleetId}/health');
  });
});

describe('the two gates the portal transcribes rather than smooths over', () => {
  it('keeps the single bind approval-gated and the batch not', () => {
    // `FleetOpsEndpoints` puts `RequireApprovedFleet()` on `trackers/bind`;
    // `FleetTrackerEndpoints` gates the batch on the canonical `fleet_owner`
    // role and nothing else. Guessing high on the second would refuse a write
    // the platform allows.
    const ops = readFileSync(
      join(REPO_ROOT, 'backend/src/Fleet.Api/Endpoints/FleetOpsEndpoints.cs'),
      'utf8',
    );
    expect(ops).toMatch(
      /WithName\("bindFleetTracker"\)[\s\S]{0,200}?RequireApprovedFleet\(\)/,
    );

    const provisioningEndpoints = readFileSync(
      join(REPO_ROOT, 'backend/src/Provisioning.Api/Endpoints/FleetTrackerEndpoints.cs'),
      'utf8',
    );
    expect(provisioningEndpoints).not.toContain('RequireApprovedFleet');
  });

  it('knows the batch is a D-30 sensitive operation, which is why its refusal has its own sentence', () => {
    // `bulkBindTrackers` carries `X-Attestation`, and the gateway's policy list
    // is asserted equal to that set by its own tests. A browser sends no
    // `X-Platform`, so the middleware answers `401 attestation-failed` — the one
    // refusal on this portal that is about the *client* rather than the caller.
    expect(PROVISIONING).toMatch(
      /operationId: bulkBindTrackers[\s\S]{0,900}?XAttestation/,
    );
    expect(readFileSync(GATEWAY_SETTINGS, 'utf8')).toContain(
      '"Path": "/v1/fleets/{fleetId}/trackers/bulk"',
    );
  });
});

describe('the cadence column is US-5.5’s published rates, not a setting', () => {
  it('reports the two rates a Mode A/B session publishes at', () => {
    expect(PUBLISH_CADENCE).toEqual({ movingSeconds: 4, stationarySeconds: 10 });

    const urd = readFileSync(join(REPO_ROOT, 'specs/user-requirements-document.md'), 'utf8');
    expect(urd).toContain('**Moving** — 1 call every 4 seconds');
    expect(urd).toContain('**Stationary (GPS idle)** — 1 call every 10 seconds');
  });

  it('has no route to set a per-vehicle profile, which is why there is no control', () => {
    // US-3.18 asks for one; no contract serves it. The only cadence surface is
    // the MQTT downlink `veh/{vehicleId}/cmd`, which is a device topic.
    for (const contract of [FLEET, HEALTH, PROVISIONING]) {
      expect(contract).not.toMatch(/cadence-?profile|publishCadence/i);
    }
  });
});
