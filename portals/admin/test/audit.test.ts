import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import type { AdminAuditAction, AdminAuditEntity, TransitAuditAction } from '@/api/audit';

/**
 * The D-35 vocabulary, held against its writers.
 *
 * `AdminAuditActions` (`backend/src/AdminBff/Auditing/AdminAuditActions.cs`) is
 * where most of it lives, and its own remark says why the strings are constants
 * there: "an action string that differs by one character between the route that
 * writes it and the screen that filters on it is a gap in an immutable log nobody
 * notices until somebody goes looking for the row". The portal names the same
 * strings — in `AuditIntent`, and in the notice it shows an operator before they
 * press a button — so it is the second place that can drift.
 *
 * **Δ C110: there are two writers, not one.** `gateway-routes.json` sends
 * `/v1/admin/transit/**` to transit-svc at Order 20, so a GTFS upload or
 * activation never reaches admin-bff and the row an auditor finds is the one
 * transit-svc commits inside the swap transaction — `GtfsAuditActions`. SCR-AP-016
 * declares those actions, so that file is parsed here too.
 */

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const SOURCE = join(REPO_ROOT, 'backend/src/AdminBff/Auditing/AdminAuditActions.cs');
const TRANSIT_SOURCE = join(REPO_ROOT, 'backend/src/Transit.Api/Gtfs/GtfsAuditActions.cs');

/** Every `public const string X = "VALUE";` in the file, as `name → value`. */
function constants(path: string = SOURCE): Map<string, string> {
  const source = readFileSync(path, 'utf8');
  const found = new Map<string, string>();
  for (const match of source.matchAll(/public const string (\w+) = "([^"]+)";/g)) {
    found.set(match[1]!, match[2]!);
  }
  return found;
}

/** Entity types are the ones whose constant name ends in `Entity`. */
function partition(all: Map<string, string>) {
  const actions: string[] = [];
  const entities: string[] = [];
  for (const [name, value] of all) {
    (name.endsWith('Entity') ? entities : actions).push(value);
  }
  return { actions, entities };
}

// The union types are erased at runtime, so the portal's copy is listed here
// once and the type system holds it to `AdminAuditAction` / `AdminAuditEntity`:
// a value that is not in the union fails to compile, and a union member missing
// from these arrays fails the test below.
const PORTAL_ACTIONS: readonly AdminAuditAction[] = [
  'VEHICLE_SUSPENDED',
  'DRIVER_SUSPENDED',
  'REPORT_CONFIRMED',
  'REPORT_DISMISSED',
  'TICKET_RESOLVED',
  'DOC_VIEW',
  'VERIFICATION_FIELD_CONFIRMED',
  'VERIFICATION_APPROVED',
  'VERIFICATION_REJECTED',
  'VERIFICATION_REOPENED',
  'PAYOUT_PROFILE_APPROVED',
  'PAYOUT_PROFILE_REJECTED',
  'PII_READ',
  'WALLET_FEE_REVERSED',
  'REFUND_ISSUED',
  'PDPA_REQUESTED',
  'PDPA_FULFILLED',
  'PDPA_REJECTED',
  'TARIFFS_PUBLISHED',
  'CITY_CREATED',
  'CITY_UPDATED',
  'FEATURE_FLAG_SET',
  'TRAIN_CREATED',
  'TRAIN_UPDATED',
  'TRAIN_RETIRED',
  'ANNOUNCEMENT_PUBLISHED',
  'GTFS_PROXIED',
];

const PORTAL_ENTITIES: readonly AdminAuditEntity[] = [
  'vehicle',
  'driver',
  'passenger',
  'fleet_org',
  'document',
  'driver_payout_profile',
  'driver_wallet',
  'ride_payment',
  'pdpa_request',
  'vehicle_report',
  'support_ticket',
  'fare_tariff',
  'operating_city',
  'feature_flag',
  'broadcast',
  'gtfs_feed',
];

/**
 * The three facts transit-svc's GTFS lifecycle records (Δ C110).
 *
 * `GTFS_FEED_VALIDATED` is here and no screen declares it: a queued job reaches
 * that verdict, so the row is actor-less. The vocabulary is what the log can
 * contain, not what a button can cause — the same reason `DOC_VIEW` and
 * `PII_READ` are in admin-bff's list.
 */
const PORTAL_TRANSIT_ACTIONS: readonly TransitAuditAction[] = [
  'GTFS_FEED_UPLOADED',
  'GTFS_FEED_VALIDATED',
  'GTFS_FEED_ACTIVATED',
];

describe('the portal knows exactly the D-35 vocabulary admin-bff writes', () => {
  const { actions, entities } = partition(constants());

  it('found the writer', () => {
    expect(actions.length).toBeGreaterThan(20);
    expect(entities.length).toBeGreaterThan(10);
  });

  it('names every action admin-bff can record', () => {
    expect([...PORTAL_ACTIONS].sort()).toEqual([...actions].sort());
  });

  it('names every entity type admin-bff can record', () => {
    expect([...PORTAL_ENTITIES].sort()).toEqual([...entities].sort());
  });

  it('spells them in screaming snake, as server_db_schema.md §23 does', () => {
    for (const action of PORTAL_ACTIONS) expect(action).toMatch(/^[A-Z][A-Z0-9_]*$/);
  });
});

describe('and exactly the vocabulary transit-svc writes for SCR-AP-016', () => {
  const { actions, entities } = partition(constants(TRANSIT_SOURCE));

  it('names every GTFS lifecycle action', () => {
    expect([...PORTAL_TRANSIT_ACTIONS].sort()).toEqual([...actions].sort());
  });

  it('records them against the entity type admin-bff already knows', () => {
    // `gtfs_feed` is declared in both files and has to be the same string in
    // both: two services writing one entity type under two spellings would split
    // one auditor question across two filters.
    expect(entities).toEqual(['gtfs_feed']);
    expect(PORTAL_ENTITIES).toContain('gtfs_feed');
  });

  it('spells them in screaming snake too', () => {
    for (const action of PORTAL_TRANSIT_ACTIONS) expect(action).toMatch(/^[A-Z][A-Z0-9_]*$/);
  });
});
