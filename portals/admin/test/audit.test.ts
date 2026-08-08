import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import type { AdminAuditAction, AdminAuditEntity } from '@/api/audit';

/**
 * The D-35 vocabulary, held against its writer.
 *
 * `AdminAuditActions` (`backend/src/AdminBff/Auditing/AdminAuditActions.cs`) is
 * the only thing that puts a row in `audit.events` on this surface, and its own
 * remark says why the strings are constants there: "an action string that differs
 * by one character between the route that writes it and the screen that filters
 * on it is a gap in an immutable log nobody notices until somebody goes looking
 * for the row". The portal names the same strings — in `AuditIntent`, and in the
 * notice it shows an operator before they press a button — so it is the second
 * place that can drift.
 */

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const SOURCE = join(REPO_ROOT, 'backend/src/AdminBff/Auditing/AdminAuditActions.cs');

/** Every `public const string X = "VALUE";` in the file, as `name → value`. */
function constants(): Map<string, string> {
  const source = readFileSync(SOURCE, 'utf8');
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
