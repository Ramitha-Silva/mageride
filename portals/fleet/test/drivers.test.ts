import { readFileSync } from 'node:fs';

import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  ASSIGNMENTS,
  assignmentStatusView,
  assignmentTarget,
  byActiveThenRecent,
  driverReferenceFor,
  type Assignment,
} from '@/api/drivers';

import { FLEET_CONTRACT } from './support/fleet';

/**
 * **SCR-FP-005 against `fleet.yaml`.**
 *
 * Two things are worth an executable check here and the rest is transcription:
 * the **one field, two arms** rule that turns what an operator types into either
 * `driverId` or `driverPhone`, and the rule that the portal reads `active`
 * rather than computing it.
 */

const FLEET = readFileSync(FLEET_CONTRACT, 'utf8');

function assignment(overrides: Partial<Assignment> = {}): Assignment {
  return {
    assignmentId: '01JQA000000000000000000001',
    driverId: '01JQD000000000000000000001',
    vehicleId: '01JQV000000000000000000001',
    from: '2026-06-02T00:00:00Z',
    active: true,
    ...overrides,
  };
}

afterEach(() => vi.useRealTimers());

describe('US-13.2 — one field names the driver by id or by number', () => {
  it('reads a 26-character ULID as a User ID', () => {
    expect(driverReferenceFor('01JQD000000000000000000001')).toEqual({
      kind: 'id',
      driverId: '01JQD000000000000000000001',
    });

    // Crockford base-32 is case-insensitive; the wire form is upper case.
    expect(driverReferenceFor('01jqd000000000000000000001')).toEqual({
      kind: 'id',
      driverId: '01JQD000000000000000000001',
    });
  });

  it('reads the two forms of a Sri Lankan mobile as `PhoneE164`', () => {
    // What somebody standing in a depot actually types.
    for (const typed of ['0771234567', '+94771234567', '94771234567', '077 123 4567']) {
      expect(driverReferenceFor(typed), typed).toEqual({
        kind: 'phone',
        driverPhone: '+94771234567',
      });
    }
  });

  it('refuses what is neither rather than guessing which arm it is', () => {
    // A mistyped ULID sent as a phone number produces a `validation-failed`
    // about a field nobody filled in.
    for (const typed of ['', 'K. Fernando', '01JQD00000000000000000', '0771234']) {
      expect(driverReferenceFor(typed).kind, typed).toBe('unrecognised');
    }
  });

  it('is the pair the contract declares — exactly one of the two', () => {
    expect(FLEET).toContain('Exactly one of `driverId` and `driverPhone` names the driver.');
    expect(FLEET).toContain('driverPhone:');
  });
});

describe('the status chip reads what the database evaluated', () => {
  it('calls a revoked assignment revoked, whatever the window says', () => {
    // A person did this, and it is not the same fact as a window running out.
    const view = assignmentStatusView(
      assignment({ active: false, revokedAt: '2026-06-20T09:00:00Z' }),
    );
    expect(view.labelKey).toBe('fleet.drivers.status.revoked');
    expect(view.tone).toBe('error');
  });

  it('calls an assignment the server says is open Active', () => {
    expect(assignmentStatusView(assignment()).labelKey).toBe('fleet.drivers.status.active');
  });

  it('splits an inactive window into "starts later" and "ended"', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-15T00:00:00Z'));

    expect(
      assignmentStatusView(assignment({ active: false, from: '2026-07-01T00:00:00Z' })).labelKey,
    ).toBe('fleet.drivers.status.scheduled');

    expect(
      assignmentStatusView(
        assignment({ active: false, from: '2026-05-01T00:00:00Z', to: '2026-06-01T00:00:00Z' }),
      ).labelKey,
    ).toBe('fleet.drivers.status.expired');
  });

  it('leaves `active` to the server — US-13.9’s auto-expiry is a read, not a write', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-01T00:00:00Z'));

    // A window that has closed by the browser's clock but which the database
    // still reports open is reported open. The portal does not overrule it.
    const stillOpen = assignment({ active: true, to: '2026-07-01T00:00:00Z' });
    expect(assignmentStatusView(stillOpen).labelKey).toBe('fleet.drivers.status.active');

    expect(FLEET).toContain('The validity window evaluated by the database at read time');
  });
});

describe('the table’s order is the contract’s', () => {
  it('puts active assignments first and the most recent start above the rest', () => {
    const rows: Assignment[] = [
      assignment({ assignmentId: 'old-inactive', active: false, from: '2026-01-01T00:00:00Z' }),
      assignment({ assignmentId: 'new-active', active: true, from: '2026-06-10T00:00:00Z' }),
      assignment({ assignmentId: 'new-inactive', active: false, from: '2026-05-01T00:00:00Z' }),
      assignment({ assignmentId: 'old-active', active: true, from: '2026-02-01T00:00:00Z' }),
    ];

    expect([...rows].sort(byActiveThenRecent).map((row) => row.assignmentId)).toEqual([
      'new-active',
      'old-active',
      'new-inactive',
      'old-inactive',
    ]);
  });
});

describe('the targets stay inside the caller’s organisation', () => {
  it('names no fleet id, because the data layer writes the only one', () => {
    expect(ASSIGNMENTS).toBe('/assignments');
    expect(assignmentTarget('01JQA')).toBe('/assignments/01JQA');
    expect(assignmentTarget('01JQA').startsWith('/v1/')).toBe(false);
  });
});
