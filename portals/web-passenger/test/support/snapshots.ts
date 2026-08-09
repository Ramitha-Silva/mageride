import type {
  PackageSnapshot,
  PickupConfirmSnapshot,
  ProxyRideSnapshot,
  Receipt,
} from '@/api/types';

/**
 * Fixtures shaped like `public-bff.yaml`'s three snapshot variants.
 *
 * Two deliberate properties, both of which the tests depend on:
 *
 *  - **`startOtp` is absent from the ride fixture**, because public-bff never emits
 *    one: `ride.yaml` and ride-svc's own contracts say a rider start OTP is
 *    "accepted and ignored in this build". A fixture that invented four digits
 *    would let SCR-WT-004 be tested against a platform that does not exist.
 *  - **The names, plates and numbers here are distinctive strings**, so a test can
 *    assert their *absence* from SCR-WT-006 and mean something by it.
 */

export const PACKAGE_TOKEN = 'JQnQ4KcVsE9mR7tYuI0pLzXwBvNa1234';
export const RIDE_TOKEN = 'Ba7fLmQ2zXcVbN9mKjHgFdSaPoIuYt56';
export const PICKUP_TOKEN = 'Zx8CvBnMaSdFgHjKlQwErTyUiOp01234';

export const DRIVER = {
  name: 'K. Fernando',
  vehicleType: 'tuk',
  regNo: 'ABC-1234',
  phone: '+94771234567',
} as const;

export function packageSnapshot(overrides: Partial<PackageSnapshot> = {}): PackageSnapshot {
  return {
    kind: 'package',
    status: 'InTransit',
    driver: DRIVER,
    position: { lat: 6.9271, lng: 79.8612, ts: '2026-08-09T04:15:00Z' },
    deliveryOtp: '7315',
    senderNameMasked: 'Ramith',
    ...overrides,
  };
}

export function rideSnapshot(overrides: Partial<ProxyRideSnapshot> = {}): ProxyRideSnapshot {
  return {
    kind: 'ride',
    state: 'Accepted',
    driver: DRIVER,
    position: { lat: 6.9271, lng: 79.8612, ts: '2026-08-09T04:15:00Z' },
    etaMin: 3,
    // `_ibE_seK` is Colombo → a point a little north-east of it, precision 5.
    route: { polyline: '{soxAybjfN_pR_pR' },
    fare: { totalMinor: 48_000, currency: 'LKR', paidBy: 'cash_due' },
    ...overrides,
  };
}

export function pickupSnapshot(
  overrides: Partial<PickupConfirmSnapshot> = {},
): PickupConfirmSnapshot {
  return {
    kind: 'pickup_confirm',
    bookerFirstName: 'Ramith',
    suggestedPin: { lat: 6.9271, lng: 79.8612 },
    expiresAt: '2026-08-09T04:20:00Z',
    ttlRemainingSec: 278,
    ...overrides,
  };
}

export function receipt(overrides: Partial<Receipt> = {}): Receipt {
  return {
    kind: 'package',
    state: 'CashOnDeliveryCollected',
    totalMinor: 48_000,
    currency: 'LKR',
    proof: 'cod_collected',
    driver: DRIVER,
    completedAt: '2026-08-09T04:18:00Z',
    ...overrides,
  };
}
