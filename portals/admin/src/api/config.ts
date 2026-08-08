/**
 * SCR-AP-007's wire shapes and the five configuration surfaces behind its tabs.
 *
 * Transcribed from four contracts, because the Configuration group is one console
 * over four services (`AdminMenu.cs`: "Six items are answered by other services and
 * are here anyway"):
 *
 * | Tab | Read | Write | Answered by |
 * |---|---|---|---|
 * | Fare tariffs | **none** | `PUT /v1/admin/fares/tariffs` | admin-bff |
 * | Daily fee | **none** | `PUT /v1/admin/fees/rates` | subscription-svc |
 * | Commission & vouchers | `GET /v1/admin/voucher-discount-tiers` | `PUT` the same | subscription-svc |
 * | Driver Level | **none** | `PUT /v1/admin/drivers/level-config` | dispatch-svc |
 * | Feature flags | `GET /v1/admin/config/feature-flags` | `PUT …/{key}` | admin-bff |
 *
 * ## Three of the five have no read route, and the screens say so
 *
 * Nothing in D3', in `admin-bff.yaml`, in `subscription.yaml` or in `dispatch.yaml`
 * serves the current fare tariffs, the current daily-fee ladder or the current
 * Driver-Level parameters back to an operator. A Config screen that pretended
 * otherwise would have to invent the values — printing D5' §12.1's defaults as
 * though they were what the platform is running — and the first person to trust
 * that would be the one publishing a new tariff version over the top of it.
 *
 * So those three tabs are **publish forms that start empty and say why**, and the
 * gap is raised as a micro-change-set in the C108 handoff. The two that do have a
 * read are read.
 *
 * ## What "forward-only" means for each of them
 *
 * Tariffs are **versioned by `effectiveFrom` and never mutated** (C005): a
 * completed ride stays reconcilable against the rate that priced it. Daily-fee
 * rates are an **upsert of what was sent** — subscription-svc's own remark says a
 * `PUT` that deleted the rows it was not given "would let a Config screen
 * rendering six of the eight tiers silently un-configure the other two", and an
 * un-configured type cannot go online at all. Voucher discounts apply **only at
 * purchase**. None of the three revisits a charge that has already been taken;
 * that is the whole of "never retro-bill", and it is a property of what those
 * services do not do rather than of anything here.
 */

/* ---------------------------------------------------------------------------
 * Paths
 * ------------------------------------------------------------------------ */

/** `PUT /v1/admin/fares/tariffs` — publishes a new Mode C tariff version (US-14.4). */
export const TARIFFS_PATH = '/v1/admin/fares/tariffs';

/** `GET` every platform feature flag · `PUT …/{key}` sets one (US-14.12). */
export const FEATURE_FLAGS_PATH = '/v1/admin/config/feature-flags';

/** `PUT /v1/admin/fees/rates` — the daily-fee ladder, per vehicle type per mode. */
export const FEE_RATES_PATH = '/v1/admin/fees/rates';

/** `GET`/`PUT /v1/admin/voucher-discount-tiers` — the bulk-voucher ladder (AL-01). */
export const VOUCHER_TIERS_PATH = '/v1/admin/voucher-discount-tiers';

/** `PUT /v1/admin/drivers/level-config` — the Driver Level system (US-14.12). */
export const LEVEL_CONFIG_PATH = '/v1/admin/drivers/level-config';

export function featureFlagPath(key: string): string {
  return `${FEATURE_FLAGS_PATH}/${encodeURIComponent(key)}`;
}

/* ---------------------------------------------------------------------------
 * AL-09's canonical vehicle types
 * ------------------------------------------------------------------------ */

/**
 * `_shared.yaml#/components/schemas/VehicleType`, in the contract's order.
 *
 * **The wireframe's "Vehicle types" tab is not drawn, and this is why.** AL-09's
 * list is the CHECK on `registry.vehicles.vehicle_type` (C003) and a `HashSet` in
 * subscription-svc that refuses a rate for anything outside it; no route anywhere
 * on the platform adds a type, and D2's own note says "there is no `car`: it maps
 * to `sedan`". A tab offering to configure them would be offering a migration.
 * What *is* per-vehicle-type and configurable is the fare row and the daily-fee
 * row, and both are drawn against this list. See the C108 handoff.
 */
export const VEHICLE_TYPES = [
  'motorbike',
  'three_wheeler',
  'flex',
  'sedan',
  'mini_van',
  'van',
  'truck',
  'mini_truck',
  'bus',
  'train',
] as const;

export type VehicleType = (typeof VEHICLE_TYPES)[number];

export function isVehicleType(value: string | undefined): value is VehicleType {
  return value !== undefined && (VEHICLE_TYPES as readonly string[]).includes(value);
}

/**
 * The eight types a Mode C fare tariff can be set for.
 *
 * `bus` and `train` are Mode A — free, never onboarded through the Driver App,
 * and priced by nothing this screen writes. The wireframe's tariff table draws
 * them as a "Free / —" row, which is what this exclusion renders as.
 */
export const FARED_VEHICLE_TYPES = VEHICLE_TYPES.filter(
  (type) => type !== 'bus' && type !== 'train',
);

/** `_shared.yaml#/components/schemas/OperatingMode`. */
export const OPERATING_MODES = ['A', 'B', 'C'] as const;

export type OperatingMode = (typeof OPERATING_MODES)[number];

/* ---------------------------------------------------------------------------
 * Wire shapes
 * ------------------------------------------------------------------------ */

/** One row of the Mode C fare ladder. Money in integer minor units. */
export interface Tariff {
  readonly vehicleType: VehicleType;
  readonly firstKmMinor: number;
  readonly perKmMinor: number;
  readonly peakSurchargePct?: number;
  readonly nightSurchargePct?: number;
  readonly currency: string;
}

/**
 * A peak or night window in **local Asia/Colombo wall-clock**.
 *
 * `endLocal` may be earlier than `startLocal` — the night window wraps midnight
 * (22:00–05:00) — and no constraint enforces ordering for exactly that reason
 * (C005). A form that validated `end > start` would refuse the platform's own
 * default.
 */
export interface PeakWindow {
  readonly kind: 'peak' | 'night';
  readonly startLocal: string;
  readonly endLocal: string;
  readonly multiplierPct: number;
}

export interface TariffPublication {
  readonly effectiveFrom?: string;
  readonly tariffs: readonly Tariff[];
  readonly peakWindows?: readonly PeakWindow[];
}

/** One row of `config.feature_flags` (migration 0202, US-14.12). */
export interface FeatureFlag {
  readonly key: string;
  readonly enabled: boolean;
  /** What flipping it does, in the operator's words. Shown beside the toggle. */
  readonly description?: string;
  readonly updatedBy?: string;
  readonly updatedAt: string;
}

/** One rung of the daily-fee ladder. Zero for Mode A — bus and train carry no fee. */
export interface DailyFeeRate {
  readonly vehicleType: VehicleType;
  readonly dailyFeeMinor: number;
  readonly mode: OperatingMode;
  readonly currency: string;
}

/**
 * One rung of the bulk-voucher ladder — the informal reseller's **entire** margin.
 *
 * `discountBps` is basis points per **voucher value**: `1000` is 10 %, so a driver
 * pays Rs 900 for Rs 1,000 of credit. There is no per-driver rate and no
 * per-transfer commission anywhere on the platform (AL-01) — a driver-to-driver
 * transfer later moves the exact value.
 */
export interface VoucherDiscountTier {
  readonly denominationMinor: number;
  readonly discountBps: number;
  readonly active: boolean;
  readonly updatedAt?: string;
}

/** The Driver Level system's tuning (D5' §4, US-6A.6/6A.8/6A.14). */
export interface LevelConfig {
  readonly levelUpThreshold: number;
  readonly noShowPenaltyPoints?: number;
  readonly cancellationPenaltyPoints?: number;
  /** **Level 1 is excluded from the Job Board** (US-6A.8), so this is 2 or 3. */
  readonly jobBoardMinLevel?: number;
}

/* ---------------------------------------------------------------------------
 * Arithmetic the screen states rather than the operator computes
 * ------------------------------------------------------------------------ */

/** One hundred per cent, in basis points. */
export const BASIS_POINTS = 10_000;

/**
 * What a driver pays for a voucher of `denominationMinor` at `discountBps`.
 *
 * The wireframe prints "Driver pays" and "Wallet credit" beside the percentage,
 * and this is that column. **It is a preview, not the price**: wallet-svc computes
 * the charge at purchase from the tier in force at that instant, and this exists
 * so an operator setting 18 % on a Rs 10,000 voucher can see Rs 8,200 before they
 * publish it rather than after a driver has bought one.
 *
 * Rounded down to the minor unit, matching an integer-arithmetic ledger: money is
 * never a float on this platform (CLAUDE.md).
 */
export function voucherPriceMinor(denominationMinor: number, discountBps: number): number {
  return Math.floor((denominationMinor * (BASIS_POINTS - discountBps)) / BASIS_POINTS);
}

/** A basis-point rate as whole percent — `1250` is `12.5`. */
export function bpsToPercent(bps: number): number {
  return bps / 100;
}

/** Whole percent back to basis points, rounded — what the tier form submits. */
export function percentToBps(percent: number): number {
  return Math.round(percent * 100);
}
