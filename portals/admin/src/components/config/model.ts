import {
  bpsToPercent,
  voucherPriceMinor,
  type FeatureFlag,
  type VehicleType,
  type VoucherDiscountTier,
  VEHICLE_TYPES,
} from '@/api/config';
import type { AdminMessageKey, AdminTranslator, Locale } from '@/i18n';
import { formatBasisPoints, formatDateTime, formatMoneyMinor } from '@/i18n/format';

import type { FeatureFlagRowView } from './FeatureFlagList';
import type { VoucherTierRowView } from './VoucherTierPanel';

/**
 * SCR-AP-007's view model.
 *
 * Two things live here and nothing else does: the AL-09 vehicle-type vocabulary,
 * which every tab that draws a per-type row needs, and the voucher ladder's
 * preview arithmetic.
 */

export interface RenderContext {
  readonly t: AdminTranslator;
  readonly locale: Locale;
}

const VEHICLE_LABEL = {
  motorbike: 'admin.config.vehicle.motorbike',
  three_wheeler: 'admin.config.vehicle.threeWheeler',
  flex: 'admin.config.vehicle.flex',
  sedan: 'admin.config.vehicle.sedan',
  mini_van: 'admin.config.vehicle.miniVan',
  van: 'admin.config.vehicle.van',
  truck: 'admin.config.vehicle.truck',
  mini_truck: 'admin.config.vehicle.miniTruck',
  bus: 'admin.config.vehicle.bus',
  train: 'admin.config.vehicle.train',
} as const satisfies Record<VehicleType, AdminMessageKey>;

/**
 * AL-09's canonical types, translated.
 *
 * A `Record` over the whole enum rather than a lookup function, because the two
 * consumers are client components and a function does not cross the RSC boundary
 * (`ReverseFeeCard`'s rule, and the reason `SignInForm` takes labels as props).
 */
export function vehicleTypeLabels({ t }: RenderContext): Record<VehicleType, string> {
  return Object.fromEntries(
    VEHICLE_TYPES.map((type) => [type, t(VEHICLE_LABEL[type])]),
  ) as Record<VehicleType, string>;
}

/**
 * The voucher ladder as the wireframe draws it: value → commission % → driver
 * pays → wallet credit → active.
 *
 * "Driver pays" and "Wallet credit" are the same fact stated twice on purpose — a
 * ladder whose face value and whose price are in adjacent columns is one an
 * operator can check at a glance, and the whole point of AL-01 is that the
 * difference between them is the reseller's entire margin.
 */
export function voucherTierRows(
  tiers: readonly VoucherDiscountTier[],
  { t, locale }: RenderContext,
): VoucherTierRowView[] {
  const money = (minor: number) =>
    t('admin.dashboard.money', { amount: formatMoneyMinor(locale, minor) });

  return [...tiers]
    .sort((left, right) => left.denominationMinor - right.denominationMinor)
    .map((tier) => ({
      key: String(tier.denominationMinor),
      denomination: money(tier.denominationMinor),
      percent: formatBasisPoints(locale, tier.discountBps),
      pays: money(voucherPriceMinor(tier.denominationMinor, tier.discountBps)),
      // The credit is the face value: a voucher buys its denomination, and the
      // discount is taken off what is paid rather than added to what is received.
      credit: money(tier.denominationMinor),
      active: tier.active,
    }));
}

/** Exposed for the tier form's default, and for the test that checks the preview. */
export function tierPercent(tier: VoucherDiscountTier): number {
  return bpsToPercent(tier.discountBps);
}

export function featureFlagRows(
  flags: readonly FeatureFlag[],
  { locale }: RenderContext,
): FeatureFlagRowView[] {
  return [...flags]
    .sort((left, right) => left.key.localeCompare(right.key))
    .map((flag) => ({
      key: flag.key,
      enabled: flag.enabled,
      description: flag.description?.trim() ? flag.description.trim() : null,
      updated: formatDateTime(locale, flag.updatedAt),
    }));
}
