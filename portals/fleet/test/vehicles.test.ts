import { readFileSync } from 'node:fs';

import { describe, expect, it } from 'vitest';

import {
  BULK_CSV_COLUMNS,
  BULK_MAX_ROWS,
  BULK_UPLOAD_MAX_BYTES,
  FLEET_MODES,
  ONBOARDABLE_VEHICLE_TYPES,
  SERVICE_PAYMENTS,
  VEHICLE_DOCUMENT_MAX_BYTES,
  VEHICLE_DOCUMENT_SLOTS,
  canBeApproved,
  documentsSummary,
  fareMinorFrom,
  isVehicleDocumentUploadKind,
  slotIsRequiredFor,
  vehicleAccentClass,
  vehicleClassificationTarget,
  vehicleDocumentsTarget,
  type VehicleDocumentSlot,
} from '@/api/vehicles';

import { FLEET_CONTRACT, SHARED_CONTRACT, contractEnum } from './support/fleet';

/**
 * **SCR-FP-004 against the contract it is transcribed from.**
 *
 * The vocabulary is pinned to `fleet.yaml` and `_shared.yaml` rather than to a
 * list somebody typed, because AL-50's four slots and AL-51's two values are the
 * component's fences and a fence nothing checks is a comment. The rules —
 * "a required slot holds the vehicle out of Approved", "the route permit is Mode
 * A's" — are checked as behaviour, because they are what SCR-FP-004 renders.
 */

const FLEET = readFileSync(FLEET_CONTRACT, 'utf8');
const SHARED = readFileSync(SHARED_CONTRACT, 'utf8');

describe('AL-50 — four named slots, and no generic dropzone', () => {
  it('draws exactly the four the contract names, in SCR-FP-004’s order', () => {
    expect(VEHICLE_DOCUMENT_SLOTS.map((slot) => slot.kind)).toEqual([
      'registration',
      'insurance',
      'revenue_license',
      'permit',
    ]);
  });

  it('names each slot by the stored kind the read answers with', () => {
    // `VehicleDocumentSlot.kind` — `registry.documents.kind` (C003).
    expect(contractEnum(FLEET, 'registration,')).toEqual([
      'registration',
      'insurance',
      'revenue_license',
      'permit',
    ]);

    expect(VEHICLE_DOCUMENT_SLOTS.map((slot) => slot.kind)).toEqual(
      contractEnum(FLEET, 'registration,'),
    );
  });

  it('posts each slot under the wire kind the upload admits, which is not the same list', () => {
    // `POST …/documents`' own `kind` enum — SCR-FP-004's slot labels. Two of the
    // four differ from the stored name, and fleet-svc refuses a stored name in
    // its place (`VehicleDocumentKinds.ToStoredKind`).
    const wire = contractEnum(FLEET, 'registration_copy,');
    expect(wire).toEqual(['registration_copy', 'insurance', 'revenue_license', 'route_permit']);

    expect(VEHICLE_DOCUMENT_SLOTS.map((slot) => slot.uploadKind)).toEqual(wire);

    for (const kind of wire) expect(isVehicleDocumentUploadKind(kind), kind).toBe(true);
    // The stored names are not upload kinds, which is the half a client gets
    // wrong by reading a slot back and posting it again.
    expect(isVehicleDocumentUploadKind('registration')).toBe(false);
    expect(isVehicleDocumentUploadKind('permit')).toBe(false);
  });

  it('makes the route permit Mode A’s and the other three everybody’s', () => {
    for (const kind of ['registration', 'insurance', 'revenue_license'] as const) {
      expect(slotIsRequiredFor(kind, 'A'), kind).toBe(true);
      expect(slotIsRequiredFor(kind, 'B'), kind).toBe(true);
    }

    expect(slotIsRequiredFor('permit', 'A')).toBe(true);
    expect(slotIsRequiredFor('permit', 'B')).toBe(false);
  });
});

describe('US-27.3 — a required document holds the vehicle out of Approved', () => {
  const slot = (
    kind: VehicleDocumentSlot['kind'],
    status: VehicleDocumentSlot['status'],
    required: boolean,
  ): VehicleDocumentSlot => ({ kind, status, required });

  const modeA = (permit: VehicleDocumentSlot['status']): VehicleDocumentSlot[] => [
    slot('registration', 'verified', true),
    slot('insurance', 'verified', true),
    slot('revenue_license', 'verified', true),
    slot('permit', permit, true),
  ];

  it('refuses a Mode A vehicle whose route permit is Missing or Pending', () => {
    // The component's Definition of Done, as one assertion.
    expect(canBeApproved(modeA('missing'))).toBe(false);
    expect(canBeApproved(modeA('pending'))).toBe(false);
    expect(canBeApproved(modeA('verified'))).toBe(true);
  });

  it('lets a Mode B vehicle through with no permit at all, because it needs none', () => {
    expect(
      canBeApproved([
        slot('registration', 'verified', true),
        slot('insurance', 'verified', true),
        slot('revenue_license', 'verified', true),
        slot('permit', 'missing', false),
      ]),
    ).toBe(true);
  });

  it('refuses a vehicle nobody has read the documents of, rather than passing vacuously', () => {
    expect(canBeApproved([])).toBe(false);
  });

  it('counts only the required slots, and names the first one outstanding', () => {
    const summary = documentsSummary([
      slot('registration', 'verified', true),
      slot('insurance', 'pending', true),
      slot('revenue_license', 'verified', true),
      slot('permit', 'missing', false),
    ]);

    expect(summary).toMatchObject({ verified: 2, required: 3, complete: false });
    expect(summary.outstanding?.kind).toBe('insurance');
  });
});

describe('the vocabulary is the contract’s', () => {
  it('offers every AL-09 vehicle type except the one this surface cannot onboard', () => {
    const all = contractEnum(SHARED, 'motorbike,');
    expect(all).toContain('train');

    // `FleetVehicleTypes.IsFleetOnboardable` refuses `train` with
    // `403 mode-not-allowed` — US-2.17/2.18 administer them centrally.
    expect([...ONBOARDABLE_VEHICLE_TYPES].sort()).toEqual(
      all.filter((type) => type !== 'train').sort(),
    );
  });

  it('gives every onboardable type a D2 §0.2 colour utility, written out in full', () => {
    for (const type of ONBOARDABLE_VEHICLE_TYPES) {
      // Whole class names, so Tailwind compiles a rule for each. A concatenated
      // one would come out transparent.
      expect(vehicleAccentClass(type), type).toMatch(/^bg-veh-[a-z-]+$/);
    }

    expect(vehicleAccentClass('bus')).toBe('bg-veh-bus');
    expect(vehicleAccentClass('three_wheeler')).toBe('bg-veh-tuk');
    // An unknown type still gets a dot — the sketch's own grey.
    expect(vehicleAccentClass('hovercraft')).toBe('bg-veh-private');
  });

  it('operates the two modes a fleet has and no third', () => {
    expect(FLEET_MODES).toEqual(['A', 'B']);
    // `POST …/vehicles` declares the same pair on its own body.
    expect(FLEET).toMatch(/mode:\n\s+type: string\n\s+enum: \[A, B\]/);
  });

  it('keeps AL-51’s two values on the wire name the rename did not touch', () => {
    expect([...SERVICE_PAYMENTS].sort()).toEqual([...contractEnum(FLEET, 'paid,')].sort());
    // The path and the field are unchanged; only the label moved (US-27.4).
    expect(vehicleClassificationTarget('01JQ')).toBe('/vehicles/01JQ/classification');
    expect(FLEET).toContain('/v1/fleets/{fleetId}/vehicles/{vehicleId}/classification');
  });

  it('addresses documents inside the caller’s own organisation and nowhere else', () => {
    // No `{fleetId}`: `src/api/client.ts` is the only module that writes one.
    expect(vehicleDocumentsTarget('01JQ')).toBe('/vehicles/01JQ/documents');
    expect(vehicleDocumentsTarget('01JQ').startsWith('/v1/')).toBe(false);
  });
});

describe('the bulk import’s own limits are fleet-svc’s', () => {
  it('matches `Fleet:BulkMaxRows` and `Fleet:BulkUploadMaxBytes`', () => {
    expect(BULK_MAX_ROWS).toBe(5000);
    // 2 MiB, and deliberately not the 8 MiB a document gets: 5,000 rows of
    // `registrationNumber,vehicleType,mode` is around 150 kB.
    expect(BULK_UPLOAD_MAX_BYTES).toBe(2 * 1024 * 1024);
    expect(VEHICLE_DOCUMENT_MAX_BYTES).toBe(8 * 1024 * 1024);
  });

  it('tells the operator the columns `BulkVehicleCsv` actually reads', () => {
    expect(BULK_CSV_COLUMNS).toBe(
      'registrationNumber,vehicleType,mode[,modeBBilling[,defaultMonthlyFareMinor]]',
    );
    // Documents are not among them and cannot be — a CSV carries no files, so
    // every row lands `docs_pending` (AL-50).
    expect(BULK_CSV_COLUMNS).not.toContain('document');
    expect(FLEET).toContain('Rows are created **`docs_pending`**');
  });
});

describe('money crosses the boundary in minor units', () => {
  it('turns rupees as typed into integer cents', () => {
    expect(fareMinorFrom('6000')).toBe(600_000);
    // Somebody writes the grouping separator, because a fare board has one.
    expect(fareMinorFrom('6,000')).toBe(600_000);
    expect(fareMinorFrom('5500.50')).toBe(550_050);
    expect(fareMinorFrom(' 6000 ')).toBe(600_000);
  });

  it('refuses what is not an amount rather than sending a zero', () => {
    expect(fareMinorFrom('')).toBeNull();
    expect(fareMinorFrom('free')).toBeNull();
    expect(fareMinorFrom('-100')).toBeNull();
  });
});
