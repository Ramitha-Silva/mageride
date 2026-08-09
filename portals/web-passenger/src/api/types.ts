/**
 * The wire shapes of `backend/contracts/public-bff.yaml`, transcribed.
 *
 * The contract is normative and wins over this file. Two habits keep the
 * transcription honest:
 *
 *  - **Every field the service may omit is optional here.** public-bff omits
 *    rather than nulls (`DefaultIgnoreCondition = WhenWritingNull`), and several
 *    of the omissions are load-bearing: a stale position is *absent* rather than
 *    drawn, a ride with no quote carries no `fare` block rather than a zero, and
 *    `startOtp` is absent on every ride because no endpoint on the platform issues
 *    one. A type that promised them would make the page draw values it invented.
 *  - **No field exists here that the scope may not carry.** The service holds
 *    P-02/P-09 with closed types — `PackageSnapshot` has nowhere to put the
 *    sender's number and the fare has nowhere to put an instrument — and copying
 *    that shape means a screen cannot reach for one either.
 */

/** `_shared.yaml#VehicleType` — the AL-09 canonical values. */
export type VehicleType =
  | 'bike'
  | 'three_wheeler'
  | 'tuk'
  | 'sedan'
  | 'suv'
  | 'van'
  | 'mini_van'
  | 'flex'
  | 'bus'
  | 'truck'
  | 'mini_truck';

/**
 * `public-bff.yaml#PublicDriver`.
 *
 * `phone` is E.164 and is the driver's **real** number: AL-48 withdrew the masking
 * requirement in full, removed `POST /public/track/{token}/call` and the
 * proxy-DID lease with it, and SCR-WT-002/004 dial it with a plain `tel:` link
 * (US-26.3). There is nothing behind it to broker a call.
 */
export interface PublicDriver {
  readonly name: string;
  readonly photo?: string;
  readonly vehicleType?: VehicleType | string;
  readonly regNo: string;
  readonly phone?: string;
}

export interface TrackedPosition {
  readonly lat: number;
  readonly lng: number;
  readonly ts: string;
}

/** The four steps SCR-WT-002 draws (D3' Δ 2026-07-05, US-20.5). */
export type PackageStatus = 'PickupPending' | 'PickedUp' | 'InTransit' | 'Delivered';

/** `package_recipient` scope — SCR-WT-001/002/005. */
export interface PackageSnapshot {
  readonly kind: 'package';
  readonly status: PackageStatus;
  readonly driver: PublicDriver;
  readonly position?: TrackedPosition;
  /** Four digits, and only while the parcel is aboard. */
  readonly deliveryOtp?: string;
  readonly dropoff?: { readonly addr?: string };
  /**
   * The sender's **display name**. The field name is the misleading half — the
   * schema's own description is "the sender's display name only … the sender's
   * phone number is never present in this scope" (P-09).
   */
  readonly senderNameMasked?: string;
}

/** `proxy_rider` scope — SCR-WT-004. */
export interface ProxyRideSnapshot {
  readonly kind: 'ride';
  /** One of `_shared.yaml#RideState`'s eighteen. */
  readonly state: string;
  readonly driver: PublicDriver;
  readonly position?: TrackedPosition;
  readonly etaMin?: number;
  /** Absent on every ride in this build — no endpoint on the platform issues one. */
  readonly startOtp?: string;
  readonly route?: { readonly polyline?: string };
  readonly fare?: {
    readonly totalMinor: number;
    readonly currency: string;
    /** `cash_due` is US-8.21's notice: the rider owes the driver at the end. */
    readonly paidBy: 'booker' | 'cash_due';
  };
}

/** `pickup_confirm` scope — SCR-WT-003. The narrowest of the three, deliberately. */
export interface PickupConfirmSnapshot {
  readonly kind: 'pickup_confirm';
  /** First name only — enough to recognise who is asking, and no more (P-02). */
  readonly bookerFirstName: string;
  readonly suggestedPin?: { readonly lat: number; readonly lng: number };
  readonly expiresAt: string;
  readonly ttlRemainingSec: number;
}

export type TrackSnapshot = PackageSnapshot | ProxyRideSnapshot | PickupConfirmSnapshot;

/** `public-bff.yaml#TrackEvent`. */
export interface TrackEvent {
  readonly type: 'position' | 'status' | 'resolved';
  readonly position?: TrackedPosition;
  readonly status?: string;
  readonly at: string;
  readonly cursor?: string;
}

/** The `?since` poll fallback's body. */
export interface TrackEventBatch {
  readonly events: readonly TrackEvent[];
  readonly cursor: string | null;
}

/** How the handoff was evidenced (US-25.6). */
export type ReceiptProof = 'otp_verified' | 'photo_proof' | 'cod_collected' | 'disputed';

/** `public-bff.yaml#Receipt` — SCR-WT-005. */
export interface Receipt {
  readonly kind: 'package' | 'ride';
  readonly state: string;
  readonly totalMinor?: number;
  readonly currency?: string;
  readonly proof: ReceiptProof;
  /** A signed URL, present when `proof` is `photo_proof` and a bucket is configured. */
  readonly proofPhotoUrl?: string;
  readonly driver?: PublicDriver;
  readonly completedAt: string;
}

/** The 200 on `pickup/confirm` and `pickup/decline`. */
export interface PickupResolution {
  readonly state: 'Confirmed' | 'Declined';
}

/**
 * The 202 on `sos`.
 *
 * `dispatchedAt` is nullable and `smsStatus` sits beside it — without the status a
 * caller cannot tell "the alert went out" from "the alert is on the admin console
 * and nowhere else", and on this surface that is the difference between somebody
 * having been told and nobody having been.
 */
export interface PublicSos {
  readonly sosId: string;
  readonly dispatchedAt?: string | null;
  readonly smsStatus: 'Dispatched' | 'Failed' | 'NoContact';
}
