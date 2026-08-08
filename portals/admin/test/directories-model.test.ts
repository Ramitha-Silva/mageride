import { describe, expect, it } from 'vitest';

import type {
  DirectoryTrip,
  DriverProfile,
  LinkedVehicle,
  PassengerRow,
  VehicleInfo,
} from '@/api/directories';
import {
  ABSENT,
  dailyFeeStatusPill,
  dispatchStatePill,
  driverFacts,
  driverRows,
  driverStatusPill,
  expiryPill,
  passengerRows,
  passengerStatusPill,
  registrationStatusPill,
  resultCount,
  trackerPill,
  transferRows,
  tripRows,
  vehicleChips,
  vehicleFacts,
  walletRows,
  type RenderContext,
} from '@/components/directories/model';
import { createAdminTranslator } from '@/i18n';

/**
 * SCR-AP-010…015's view model — the facts the screens make claims about, asserted
 * where they are decided rather than through the markup that draws them.
 */

const t = createAdminTranslator('en');
const context: RenderContext = { t, locale: 'en' };

const PASSENGER = '0199a1f0-0000-7000-8000-000000090431';
const DRIVER = '0199a1f0-0000-7000-8000-000000022011';
const VEHICLE = '0199a1f0-0000-7000-8000-000000048213';

function cellText(cells: readonly { readonly text?: string }[], index: number): string | undefined {
  return cells[index]?.text;
}

describe('a list row shows the number it was sent, and nothing more', () => {
  const row: PassengerRow = {
    passengerId: PASSENGER,
    name: 'Ramith de Silva',
    mobileMasked: '+9477*****67',
    trips: 128,
    joinedAt: '2026-01-14T04:00:00Z',
    status: 'active',
  };

  it('renders the masked MSISDN verbatim', () => {
    // A list row is `PhoneMasked` for every caller, whatever they hold — which is
    // what makes "every clear number this surface emitted has a PII_READ row
    // behind it" true. There is nothing here that could unmask one.
    const [rendered] = passengerRows([row], () => '/passengers/x', context);

    expect(cellText(rendered!.cells, 1)).toBe('+9477*****67');
  });

  it('says nothing rather than something wrong when the number is absent', () => {
    const [rendered] = passengerRows(
      [{ ...row, mobileMasked: undefined }],
      () => '/passengers/x',
      context,
    );

    expect(cellText(rendered!.cells, 1)).toBe(ABSENT);
  });

  it('prints the id in full beside the name, because a truncated id is ambiguous', () => {
    const [rendered] = passengerRows([row], () => '/passengers/x', context);

    expect(rendered!.cells[0]?.sub).toBe(PASSENGER);
  });

  it('names the row’s control after its subject', () => {
    const [rendered] = passengerRows([row], () => '/passengers/x', context);

    expect(rendered!.openNamed).toContain('Ramith de Silva');
  });

  it('lists a driver’s plates, and says so when there are none', () => {
    const base = {
      driverId: DRIVER,
      name: 'K. Fernando',
      mobileMasked: '+9477*****67',
      level: 3,
      trips: 980,
      status: 'verified' as const,
    };

    const [withPlates] = driverRows([{ ...base, vehicles: ['ABC-1234', 'TK-7781'] }], () => '/x', context);
    const [without] = driverRows([{ ...base, vehicles: [] }], () => '/x', context);

    expect(cellText(withPlates!.cells, 2)).toBe('ABC-1234 · TK-7781');
    expect(cellText(without!.cells, 2)).toBe(ABSENT);
  });
});

describe('the status pills', () => {
  it('gives a suspended driver the later fact, not the earlier one', () => {
    // Derived, not stored: suspended wins over verified because it is what the row
    // was opened to find.
    expect(driverStatusPill('suspended', t).tone).toBe('error');
    expect(driverStatusPill('verified', t).tone).toBe('success');
    expect(driverStatusPill('pending', t).tone).toBe('warning');
  });

  it('keeps a dispatch suspension apart from a registration status', () => {
    // E-03: "do not offer rides to it" is not the end of a registration, and the
    // two must not read as one another.
    expect(registrationStatusPill('APPROVED', t).tone).toBe('success');
    expect(dispatchStatePill('DISPATCH_SUSPENDED', t)?.tone).toBe('error');
    expect(dispatchStatePill('ACTIVE', t)).toBeNull();
  });

  it('does not colour a waived daily fee as a failure', () => {
    // US-9.6 waives the first trip of the day. An error tone would send an
    // operator hunting for a charge the platform deliberately did not make.
    expect(dailyFeeStatusPill('WAIVED_FIRST_TRIP', t).tone).toBe('info');
    expect(dailyFeeStatusPill('PAID', t).tone).toBe('success');
  });

  it('marks a blocked passenger and never claims one was erased', () => {
    // `deleted` is declared by the contract and never answered — no column records
    // a PDPA erasure yet (C065's).
    expect(passengerStatusPill('blocked', t).tone).toBe('error');
    expect(passengerStatusPill('active', t).tone).toBe('success');
  });

  it('ranks a distrusted tracker above a silent one', () => {
    expect(trackerPill(true, 'REVOKED', t).tone).toBe('error');
    expect(trackerPill(true, 'QUARANTINED', t).tone).toBe('warning');
    expect(trackerPill(false, 'ACTIVE', t).tone).toBe('warning');
    expect(trackerPill(true, 'ACTIVE', t).tone).toBe('success');
  });
});

describe('a certificate expiry is a pill because the date alone says nothing', () => {
  const today = new Date('2026-08-08T00:00:00Z');

  it('warns before it lapses and fails after', () => {
    expect(expiryPill('2026-12-31', context, today)?.tone).toBe('success');
    expect(expiryPill('2026-08-20', context, today)?.tone).toBe('warning');
    expect(expiryPill('2026-08-07', context, today)?.tone).toBe('error');
  });

  it('treats today as still valid', () => {
    expect(expiryPill('2026-08-08', context, today)?.tone).toBe('warning');
  });

  it('is absent for a certificate the platform holds no date for', () => {
    // An absent expiry is not an expired one.
    expect(expiryPill(undefined, context, today)).toBeNull();
    expect(expiryPill('not-a-date', context, today)).toBeNull();
  });

  it('draws the vehicle card without inventing either date', () => {
    const info: VehicleInfo = {
      vehicleId: VEHICLE,
      type: 'sedan',
      regNo: 'ABC-1234',
      mode: 'C',
      ownerId: DRIVER,
      status: 'APPROVED',
      dispatchState: 'ACTIVE',
      onboardingStatus: 'approved',
      registeredAt: '2026-02-02T04:00:00Z',
    };

    const facts = vehicleFacts(info, context);
    const insurance = facts.find((fact) => fact.key === 'insurance');

    expect(insurance?.pill).toBeUndefined();
    expect(insurance?.value).toBe(ABSENT);
    expect(facts.find((fact) => fact.key === 'tracker')?.value).toBe('Not paired');
  });
});

describe('a trip row', () => {
  const ride: DirectoryTrip = {
    tripId: '0199a1f0-0000-7000-8000-000000000001',
    kind: 'ride',
    state: 'Completed',
    vehicleType: 'sedan',
    regNo: 'ABC-1234',
    counterpartyId: DRIVER,
    counterpartyName: 'K. Fernando',
    fareMinor: 85_000,
    currency: 'LKR',
    startedAt: '2026-06-17T03:02:00Z',
  };

  it('shows a Mode C fare', () => {
    const [rendered] = tripRows([ride], context);

    expect(cellText(rendered!.cells, 4)).toBe('Rs 850.00');
  });

  it('shows no fare on a scheduled journey, rather than a fare of zero', () => {
    // A Mode A/B session is covered by a subscription; `Rs 0.00` would be a
    // different claim from "this is not the kind of journey that has one".
    const [rendered] = tripRows(
      [{ ...ride, kind: 'session', fareMinor: undefined, currency: undefined }],
      context,
    );

    expect(cellText(rendered!.cells, 1)).toBe('Scheduled journey');
    expect(cellText(rendered!.cells, 4)).toBe(ABSENT);
  });
});

describe('the money columns', () => {
  it('signs a wallet debit and carries the balance the ledger held after it', () => {
    const [rendered] = walletRows(
      [
        {
          entryNo: 4021,
          kind: 'daily_fee',
          amountMinor: -20_000,
          balanceAfterMinor: 325_000,
          ts: '2026-06-17T00:30:00Z',
        },
      ],
      context,
    );

    expect(cellText(rendered!.cells, 1)).toBe('Daily fee');
    expect(cellText(rendered!.cells, 2)).toBe('Rs -200.00');
    expect(cellText(rendered!.cells, 3)).toBe('Rs 3,250.00');
  });

  it('signs a credit transfer by the direction it went for this driver', () => {
    // `direction` is computed against the driver whose record this is; the ledger
    // amount itself is unsigned on the wire.
    const base = {
      transferId: '0199a1f0-0000-7000-8000-000000000009',
      initiation: 'DIRECT' as const,
      counterpartyId: DRIVER,
      counterpartyName: 'S. Perera',
      amountMinor: 100_000,
      currency: 'LKR',
      status: 'APPROVED' as const,
      createdAt: '2026-06-16T06:32:00Z',
    };

    const [sent] = transferRows([{ ...base, direction: 'out' }], context);
    const [received] = transferRows([{ ...base, direction: 'in' }], context);

    expect(cellText(sent!.cells, 3)).toBe('Rs -1,000.00');
    expect(cellText(received!.cells, 3)).toBe('Rs +1,000.00');
  });

  it('formats to the cent, because a directory is where a variance is chased', () => {
    const [rendered] = walletRows(
      [
        {
          entryNo: 1,
          kind: 'topup',
          amountMinor: 500_001,
          balanceAfterMinor: 500_001,
          ts: '2026-06-16T13:40:00Z',
        },
      ],
      context,
    );

    expect(cellText(rendered!.cells, 2)).toBe('Rs +5,000.01');
  });
});

describe('a driver’s linked vehicles', () => {
  const vehicle: LinkedVehicle = {
    vehicleId: VEHICLE,
    regNo: 'ABC-1234',
    type: 'sedan',
    mode: 'C',
    status: 'APPROVED',
    dispatchState: 'ACTIVE',
    owned: true,
    link: `/v1/admin/vehicles/${VEHICLE}`,
  };

  it('links a chip to the portal route the caller’s menu gave, not to the API path', () => {
    const [chip] = vehicleChips([vehicle], (id) => `/vehicles/${id}`, t);

    expect(chip!.href).toBe(`/vehicles/${VEHICLE}`);
    expect(chip!.href).not.toContain('/v1/');
  });

  it('still names the vehicle for an operator who may not open its screen', () => {
    // A Finance Officer reconciling a daily fee needs to know which plate it was
    // charged on, whether or not they hold the vehicle directory.
    const [chip] = vehicleChips([vehicle], null, t);

    expect(chip!.href).toBeUndefined();
    expect(chip!.regNo).toBe('ABC-1234');
  });

  it('says whether the plate is owned or assigned by a fleet (AL-03)', () => {
    const [owned] = vehicleChips([vehicle], null, t);
    const [assigned] = vehicleChips([{ ...vehicle, owned: false }], null, t);

    expect(owned!.ownership).toBe('owned');
    expect(assigned!.ownership).toBe('assigned by a fleet');
  });

  it('surfaces a dispatch suspension on the chip', () => {
    const [chip] = vehicleChips([{ ...vehicle, dispatchState: 'DISPATCH_SUSPENDED' }], null, t);

    expect(chip!.suspended?.tone).toBe('error');
  });
});

describe('the result count', () => {
  it('reports the rows this page answered, and says when there are more', () => {
    // Cursor pagination carries no total, so the count is what came back.
    expect(resultCount({ items: [1, 2, 3], cursor: null, hasMore: false }, context)).toBe('3 results');
    expect(resultCount({ items: [1, 2, 3], cursor: 'c', hasMore: true }, context)).toBe('3+ results');
  });

  it('is a dash for a search that failed, never a zero', () => {
    // "No matches" is a claim, and a 503 is not evidence for it.
    expect(resultCount(null, context)).toBe(ABSENT);
  });
});

describe('the driver profile card', () => {
  const profile: DriverProfile = {
    driverId: DRIVER,
    name: 'K. Fernando',
    mobile: '+94 77 123 4567',
    nic: '**********78',
    joinedAt: '2026-02-02T04:00:00Z',
    rating: 4.8,
    walletMinor: 325_000,
    currency: 'LKR',
    level: 3,
    points: 1180,
    status: 'verified',
  };

  it('renders the NIC exactly as it arrived, masked or not', () => {
    // The mask is applied server-side and the clear value is not in the payload
    // beside it, so there is no branch here on which form this is.
    const facts = driverFactsOf(profile);

    expect(facts.nic).toBe('**********78');
  });

  it('puts the wallet and the level where the screen exists to show them', () => {
    const facts = driverFactsOf(profile);

    expect(facts.wallet).toBe('Rs 3,250.00');
    expect(facts.level).toBe('L3 · 1,180 pts');
  });

  it('says nothing about a rating nobody has given', () => {
    const facts = driverFactsOf({ ...profile, rating: undefined });

    expect(facts.rating).toBe(ABSENT);
  });
});

function driverFactsOf(profile: DriverProfile): Record<string, string | undefined> {
  return Object.fromEntries(driverFacts(profile, context).map((fact) => [fact.key, fact.value]));
}
