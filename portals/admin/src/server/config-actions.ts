'use server';

import { revalidatePath } from 'next/cache';

import { mutate } from '@/api/client';
import {
  FARED_VEHICLE_TYPES,
  FEE_RATES_PATH,
  featureFlagPath,
  isVehicleType,
  LEVEL_CONFIG_PATH,
  OPERATING_MODES,
  percentToBps,
  TARIFFS_PATH,
  VOUCHER_TIERS_PATH,
  type DailyFeeRate,
  type LevelConfig,
  type OperatingMode,
  type PeakWindow,
  type Tariff,
  type VoucherDiscountTier,
} from '@/api/config';
import { ProblemError } from '@/api/problem';
import { getTranslator } from '@/i18n/server';

/**
 * SCR-AP-007's five writes, and the fence they are all under.
 *
 * ## Forward-only, and it is a property of the services rather than of this file
 *
 * A new tariff **version** is published (`effectiveFrom`, never mutated), a fee
 * rate is upserted and read again at the moment of the next charge, a voucher
 * discount is applied only at purchase, and a Driver-Level parameter moves points
 * awarded from now on. None of the four revisits a row that has already been
 * written, so no code path here or there re-bills anybody — and there is nothing
 * to add to make that true, only something to avoid adding.
 *
 * ## Two of these five write a D-35 row and three do not
 *
 * `TARIFFS_PUBLISHED` and `FEATURE_FLAG_SET` go through admin-bff, whose
 * interceptor writes the row inside the same transaction. The daily-fee rates, the
 * voucher tiers and the Driver-Level configuration are matched by
 * `gateway-routes.json` at **Order 20** and reach subscription-svc and dispatch-svc
 * without touching admin-bff at all, so nothing is written to `audit.events` for
 * them. They therefore declare `auditedElsewhere` rather than an action, the screen
 * says so beside the button, and the C108 handoff raises it: the component fence
 * "configuration writes are audited" holds for two of the five surfaces and not for
 * the other three.
 */

export interface ConfigState {
  readonly message?: string;
  readonly field?: string;
  /** Set on success, so the form can confirm what it published. */
  readonly saved?: boolean;
}

function text(formData: FormData, name: string): string {
  const value = formData.get(name);
  return typeof value === 'string' ? value.trim() : '';
}

/** Rupees as typed → integer minor units. `null` for anything that is not money. */
function minorUnits(value: string): number | null {
  if (value === '') return null;

  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed < 0) return null;

  return Math.round(parsed * 100);
}

function wholeNumber(value: string): number | null {
  if (value === '') return null;

  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed < 0) return null;

  return parsed;
}

const LOCAL_TIME = /^\d{2}:\d{2}$/;

/**
 * Publish a new Mode C tariff version (US-14.4).
 *
 * **Every fared vehicle type is required, and the form draws them all.** The `PUT`
 * replaces the published set, so a submission carrying six of the eight would
 * publish a version in which two types have no price — and unlike the daily-fee
 * upsert beside it, there is no "keep what was there" semantic to fall back on.
 * Requiring the whole ladder is what makes a partial publication unrepresentable
 * rather than merely discouraged.
 *
 * **A window whose end is before its start is sent unchanged.** The night window
 * wraps midnight (22:00–05:00) and fare-svc handles it; a form that validated
 * `end > start` would refuse the platform's own default.
 */
export async function publishTariffs(
  _state: ConfigState,
  formData: FormData,
): Promise<ConfigState> {
  const t = await getTranslator();

  const tariffs: Tariff[] = [];

  for (const vehicleType of FARED_VEHICLE_TYPES) {
    const firstKmMinor = minorUnits(text(formData, `firstKm.${vehicleType}`));
    const perKmMinor = minorUnits(text(formData, `perKm.${vehicleType}`));
    const peak = wholeNumber(text(formData, `peak.${vehicleType}`));
    const night = wholeNumber(text(formData, `night.${vehicleType}`));

    if (firstKmMinor === null || perKmMinor === null) {
      return {
        message: t('admin.config.tariffs.rowRequired'),
        field: `firstKm.${vehicleType}`,
      };
    }

    tariffs.push({
      vehicleType,
      firstKmMinor,
      perKmMinor,
      ...(peak === null ? {} : { peakSurchargePct: peak }),
      ...(night === null ? {} : { nightSurchargePct: night }),
      currency: 'LKR',
    });
  }

  const peakWindows: PeakWindow[] = [];

  for (const kind of ['peak', 'night'] as const) {
    const startLocal = text(formData, `window.${kind}.start`);
    const endLocal = text(formData, `window.${kind}.end`);
    const multiplierPct = wholeNumber(text(formData, `window.${kind}.pct`));

    // A window is all three fields or none of them: two out of three is an
    // operator half-way through a thought, and publishing it would put a
    // surcharge on a period nobody chose.
    if (!startLocal && !endLocal && multiplierPct === null) continue;

    if (!LOCAL_TIME.test(startLocal) || !LOCAL_TIME.test(endLocal) || multiplierPct === null) {
      return { message: t('admin.config.tariffs.windowIncomplete'), field: `window.${kind}.start` };
    }

    peakWindows.push({ kind, startLocal, endLocal, multiplierPct });
  }

  const effectiveFrom = text(formData, 'effectiveFrom');

  try {
    await mutate<
      unknown,
      { effectiveFrom?: string; tariffs: readonly Tariff[]; peakWindows?: readonly PeakWindow[] }
    >({
      method: 'PUT',
      path: TARIFFS_PATH,
      body: {
        // Absent means "now" to admin-bff. Sending a timestamp this process made up
        // would date the version to the moment the form rendered rather than the
        // moment it was published.
        ...(effectiveFrom ? { effectiveFrom: new Date(effectiveFrom).toISOString() } : {}),
        tariffs,
        ...(peakWindows.length > 0 ? { peakWindows } : {}),
      },
      audit: { action: 'TARIFFS_PUBLISHED', entity: 'fare_tariff' },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  revalidatePath('/config/fares');
  return { saved: true };
}

/**
 * Set the daily-fee rate for one vehicle type in one mode (US-14.4).
 *
 * **One rung at a time, because the `PUT` is an upsert of what it is sent.**
 * subscription-svc's own remark is explicit that it does not delete the rows it
 * was not given, precisely so "a Config screen rendering six of the eight tiers"
 * cannot silently un-configure the other two — and an un-configured type cannot go
 * online at all. Sending one rung is the shape that cannot get that wrong, and it
 * is also the only shape available to a screen that has no read route to render
 * the other seven from.
 */
export async function setDailyFeeRate(
  _state: ConfigState,
  formData: FormData,
): Promise<ConfigState> {
  const t = await getTranslator();

  const vehicleType = text(formData, 'vehicleType');
  const mode = text(formData, 'mode');
  const dailyFeeMinor = minorUnits(text(formData, 'dailyFee'));

  if (!isVehicleType(vehicleType)) return { message: t('admin.error.unexpected') };
  if (!(OPERATING_MODES as readonly string[]).includes(mode)) {
    return { message: t('admin.error.unexpected') };
  }
  if (dailyFeeMinor === null) {
    return { message: t('admin.config.fees.amountRequired'), field: 'dailyFee' };
  }

  try {
    await mutate<unknown, { items: readonly DailyFeeRate[] }>({
      method: 'PUT',
      path: FEE_RATES_PATH,
      body: {
        items: [
          { vehicleType, dailyFeeMinor, mode: mode as OperatingMode, currency: 'LKR' },
        ],
      },
      // Routed to subscription-svc at Order 20; admin-bff never sees it and writes
      // no `audit.events` row. See `api/audit.ts`.
      audit: { auditedElsewhere: 'subscription-svc' },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  revalidatePath('/config/fees');
  return { saved: true };
}

/**
 * Set the bulk-voucher discount for one denomination (US-9A.15, AL-01).
 *
 * **The whole ladder is sent, not the one rung that changed.** Unlike the fee
 * rates, this surface *has* a read (`GET /v1/admin/voucher-discount-tiers`), so the
 * screen holds the current ladder and a read-modify-write is well defined —
 * and it is what keeps the DoD's "changing a tier is reflected in the Driver App
 * top-up screen" a single round trip against one table (`billing.voucher_discount_tiers`,
 * which wallet-svc serves the Driver App from).
 *
 * The percentage is **per voucher value**, in basis points. There is no per-driver
 * rate and no per-transfer commission anywhere on the platform; a driver-to-driver
 * transfer later moves the exact value (AL-01).
 */
export async function setVoucherTier(
  _state: ConfigState,
  formData: FormData,
): Promise<ConfigState> {
  const t = await getTranslator();

  const denominationMinor = minorUnits(text(formData, 'denomination'));
  const percent = Number(text(formData, 'percent'));
  const active = formData.get('active') === 'on';
  const existing = formData.get('ladder');

  if (denominationMinor === null || denominationMinor < 1) {
    return { message: t('admin.config.vouchers.denominationRequired'), field: 'denomination' };
  }
  if (!Number.isFinite(percent) || percent < 0 || percent > 100) {
    return { message: t('admin.config.vouchers.percentRequired'), field: 'percent' };
  }

  const discountBps = percentToBps(percent);

  let ladder: VoucherDiscountTier[];
  try {
    ladder = typeof existing === 'string' && existing ? (JSON.parse(existing) as VoucherDiscountTier[]) : [];
  } catch {
    // The ladder travels on a hidden field so this action does not read it a
    // second time and race the screen that rendered it. A body that will not parse
    // is a bug, not an operator error, and must not become a `PUT` that publishes
    // one rung and drops the rest.
    return { message: t('admin.error.unexpected') };
  }

  const tiers = [
    ...ladder.filter((tier) => tier.denominationMinor !== denominationMinor),
    { denominationMinor, discountBps, active },
  ].sort((left, right) => left.denominationMinor - right.denominationMinor);

  try {
    await mutate<unknown, { tiers: readonly VoucherDiscountTier[] }>({
      method: 'PUT',
      path: VOUCHER_TIERS_PATH,
      body: { tiers },
      audit: { auditedElsewhere: 'subscription-svc' },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  revalidatePath('/config/voucher-tiers');
  return { saved: true };
}

/**
 * Tune the Driver Level system (US-14.12).
 *
 * **Every field optional, as dispatch-svc's own body is** — "an admin changing one
 * threshold should not have to restate the other three", and a form that sent
 * zeros for the boxes left blank would set a no-show penalty of zero because
 * somebody edited the level-up threshold.
 */
export async function setLevelConfig(
  _state: ConfigState,
  formData: FormData,
): Promise<ConfigState> {
  const t = await getTranslator();

  const levelUpThreshold = wholeNumber(text(formData, 'levelUpThreshold'));
  const noShowPenaltyPoints = wholeNumber(text(formData, 'noShowPenaltyPoints'));
  const cancellationPenaltyPoints = wholeNumber(text(formData, 'cancellationPenaltyPoints'));
  const jobBoardMinLevel = wholeNumber(text(formData, 'jobBoardMinLevel'));

  if (levelUpThreshold !== null && levelUpThreshold < 1) {
    return { message: t('admin.config.levels.thresholdRequired'), field: 'levelUpThreshold' };
  }
  // Level 1 is excluded from the Job Board (US-6A.8), so the floor is 2 and the
  // ceiling is the top level. dispatch-svc says the same; this copy exists so an
  // operator is told before the request rather than by a validation problem.
  if (jobBoardMinLevel !== null && (jobBoardMinLevel < 1 || jobBoardMinLevel > 3)) {
    return { message: t('admin.config.levels.jobBoardRange'), field: 'jobBoardMinLevel' };
  }

  const body: Partial<Record<keyof LevelConfig, number>> = {
    ...(levelUpThreshold === null ? {} : { levelUpThreshold }),
    ...(noShowPenaltyPoints === null ? {} : { noShowPenaltyPoints }),
    ...(cancellationPenaltyPoints === null ? {} : { cancellationPenaltyPoints }),
    ...(jobBoardMinLevel === null ? {} : { jobBoardMinLevel }),
  };

  if (Object.keys(body).length === 0) {
    return { message: t('admin.config.levels.nothingToSave') };
  }

  try {
    await mutate<unknown, typeof body>({
      method: 'PUT',
      path: LEVEL_CONFIG_PATH,
      body,
      audit: { auditedElsewhere: 'dispatch-svc' },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  revalidatePath('/config/driver-levels');
  return { saved: true };
}

/**
 * Turn a platform feature flag on or off (US-14.12).
 *
 * An **upsert**: a flag's first appearance and its next change are the same
 * operator action, and a `PUT` that 404'd on a key the running deploy has started
 * reading would leave nobody able to turn on the thing the release shipped. Omitting
 * `description` keeps the one already stored, so the toggle beside an existing flag
 * sends none.
 */
export async function setFeatureFlag(
  _state: ConfigState,
  formData: FormData,
): Promise<ConfigState> {
  const t = await getTranslator();

  const key = text(formData, 'key');
  const enabled = text(formData, 'enabled') === 'true';
  const description = text(formData, 'description');

  if (!/^[a-z][a-z0-9_.-]{1,80}$/.test(key)) {
    return { message: t('admin.config.flags.keyInvalid'), field: 'key' };
  }

  try {
    await mutate<unknown, { enabled: boolean; description?: string }>({
      method: 'PUT',
      path: featureFlagPath(key),
      body: { enabled, ...(description ? { description } : {}) },
      audit: { action: 'FEATURE_FLAG_SET', entity: 'feature_flag', entityId: key },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  revalidatePath('/config/feature-flags');
  return { saved: true };
}
