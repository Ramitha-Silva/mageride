import { beforeEach, describe, expect, it, vi } from 'vitest';

import { isAuditIntent } from '@/api/audit';
import {
  FARED_VEHICLE_TYPES,
  VEHICLE_TYPES,
  voucherPriceMinor,
  percentToBps,
  bpsToPercent,
} from '@/api/config';
import type { MutateOptions } from '@/api/client';
import { createAdminTranslator } from '@/i18n';

/**
 * SCR-AP-007's five writes.
 *
 * ## The fence, and where it actually holds
 *
 * The component's fence is "configuration writes are audited and take effect
 * forward-only — never retro-bill". Forward-only holds everywhere and is a
 * property of what the services do *not* do. **Audited holds for two of the five**:
 * tariffs and feature flags go through admin-bff's interceptor, while the daily-fee
 * rates, the voucher tiers and the Driver-Level parameters are matched by
 * `gateway-routes.json` at Order 20 and never reach it. These tests hold the
 * declarations to that, so the day a proxy is added the assertion changes with it
 * rather than the console quietly starting to tell the truth by accident.
 */

const mutate = vi.fn<(options: MutateOptions) => Promise<unknown>>();
const revalidatePath = vi.fn<(path: string) => void>();

vi.mock('@/api/client', () => ({ mutate: (options: MutateOptions) => mutate(options) }));
vi.mock('next/cache', () => ({ revalidatePath: (path: string) => revalidatePath(path) }));
vi.mock('@/i18n/server', () => ({ getTranslator: async () => createAdminTranslator('en') }));

const { publishTariffs, setDailyFeeRate, setVoucherTier, setLevelConfig, setFeatureFlag } =
  await import('@/server/config-actions');

function form(values: Record<string, string>): FormData {
  const data = new FormData();
  for (const [name, value] of Object.entries(values)) data.append(name, value);
  return data;
}

/** Every fared type filled in — the only submission `publishTariffs` accepts. */
function wholeLadder(overrides: Record<string, string> = {}): Record<string, string> {
  const values: Record<string, string> = {};
  for (const type of FARED_VEHICLE_TYPES) {
    values[`firstKm.${type}`] = '100';
    values[`perKm.${type}`] = '80';
  }
  return { ...values, ...overrides };
}

beforeEach(() => {
  vi.clearAllMocks();
  mutate.mockResolvedValue({ data: {}, status: 200 });
});

describe('AL-09 — the vehicle types are a fixed list, not a setting', () => {
  it('carries the contract enum in the contract’s order', () => {
    expect(VEHICLE_TYPES).toEqual([
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
    ]);
  });

  it('excludes Mode A from the fare ladder, because bus and train carry no fare', () => {
    expect(FARED_VEHICLE_TYPES).not.toContain('bus');
    expect(FARED_VEHICLE_TYPES).not.toContain('train');
    expect(FARED_VEHICLE_TYPES).toHaveLength(8);
  });

  it('has no `car`, which maps to `sedan`', () => {
    expect(VEHICLE_TYPES as readonly string[]).not.toContain('car');
  });
});

describe('publishing tariffs', () => {
  it('sends every fared type, because a version publishes the whole ladder', async () => {
    await publishTariffs({}, form(wholeLadder()));

    const body = mutate.mock.calls[0]?.[0].body as { tariffs: { vehicleType: string }[] };
    expect(body.tariffs.map((row) => row.vehicleType)).toEqual([...FARED_VEHICLE_TYPES]);
  });

  it('refuses a partial ladder rather than publishing a type with no price', async () => {
    const state = await publishTariffs({}, form(wholeLadder({ 'firstKm.van': '' })));

    expect(state.message).toContain('Every vehicle type');
    expect(mutate).not.toHaveBeenCalled();
  });

  it('converts rupees to integer minor units', async () => {
    await publishTariffs({}, form(wholeLadder({ 'firstKm.sedan': '150.50' })));

    const body = mutate.mock.calls[0]?.[0].body as {
      tariffs: { vehicleType: string; firstKmMinor: number }[];
    };
    expect(body.tariffs.find((row) => row.vehicleType === 'sedan')?.firstKmMinor).toBe(15_050);
  });

  it('accepts a night window that wraps midnight, because the platform’s own default does', async () => {
    await publishTariffs(
      {},
      form(
        wholeLadder({
          'window.night.start': '22:00',
          'window.night.end': '05:00',
          'window.night.pct': '15',
        }),
      ),
    );

    const body = mutate.mock.calls[0]?.[0].body as { peakWindows?: { kind: string }[] };
    expect(body.peakWindows).toEqual([
      { kind: 'night', startLocal: '22:00', endLocal: '05:00', multiplierPct: 15 },
    ]);
  });

  it('refuses a half-filled window rather than surcharging a period nobody chose', async () => {
    const state = await publishTariffs(
      {},
      form(wholeLadder({ 'window.peak.start': '07:00', 'window.peak.pct': '20' })),
    );

    expect(state.message).toContain('start, an end');
    expect(mutate).not.toHaveBeenCalled();
  });

  it('sends no effectiveFrom when the box is empty, so the version dates itself', async () => {
    await publishTariffs({}, form(wholeLadder()));
    expect(mutate.mock.calls[0]?.[0].body).not.toHaveProperty('effectiveFrom');
  });

  it('declares TARIFFS_PUBLISHED — admin-bff answers this one', async () => {
    await publishTariffs({}, form(wholeLadder()));

    expect(mutate.mock.calls[0]?.[0].audit).toEqual({
      action: 'TARIFFS_PUBLISHED',
      entity: 'fare_tariff',
    });
  });
});

describe('the daily-fee ladder', () => {
  it('sends exactly the rung that was edited, because the PUT is an upsert', async () => {
    // A call that sent six of eight rungs would silently un-configure the other
    // two if the semantics were ever a replacement — and an un-configured type
    // cannot go online at all.
    await setDailyFeeRate({}, form({ vehicleType: 'sedan', mode: 'C', dailyFee: '200' }));

    expect(mutate.mock.calls[0]?.[0].body).toEqual({
      items: [{ vehicleType: 'sedan', dailyFeeMinor: 20_000, mode: 'C', currency: 'LKR' }],
    });
  });

  it('sets the Mode B monthly platform fee through the same rung', async () => {
    await setDailyFeeRate({}, form({ vehicleType: 'van', mode: 'B', dailyFee: '300' }));

    expect(mutate.mock.calls[0]?.[0].body).toMatchObject({
      items: [expect.objectContaining({ mode: 'B', dailyFeeMinor: 30_000 })],
    });
  });

  it('declares that subscription-svc answers it and no audit row is written', async () => {
    await setDailyFeeRate({}, form({ vehicleType: 'sedan', mode: 'C', dailyFee: '200' }));

    const audit = mutate.mock.calls[0]?.[0].audit ?? { auditedElsewhere: 'iam-svc' as const };
    expect(isAuditIntent(audit)).toBe(false);
    expect(audit).toEqual({ auditedElsewhere: 'subscription-svc' });
  });

  it('refuses an amount that is not money', async () => {
    const state = await setDailyFeeRate({}, form({ vehicleType: 'sedan', mode: 'C', dailyFee: 'x' }));

    expect(state).toMatchObject({ field: 'dailyFee' });
    expect(mutate).not.toHaveBeenCalled();
  });
});

describe('the bulk-voucher ladder (AL-01)', () => {
  const LADDER = JSON.stringify([
    { denominationMinor: 100_000, discountBps: 1000, active: true },
    { denominationMinor: 500_000, discountBps: 1500, active: true },
  ]);

  it('publishes the whole ladder with the edited rung merged in', async () => {
    await setVoucherTier(
      {},
      form({ ladder: LADDER, denomination: '2000', percent: '12', active: 'on' }),
    );

    expect(mutate.mock.calls[0]?.[0].body).toEqual({
      tiers: [
        { denominationMinor: 100_000, discountBps: 1000, active: true },
        { denominationMinor: 200_000, discountBps: 1200, active: true },
        { denominationMinor: 500_000, discountBps: 1500, active: true },
      ],
    });
  });

  it('replaces a rung rather than adding a second one at the same value', async () => {
    await setVoucherTier(
      {},
      form({ ladder: LADDER, denomination: '1000', percent: '18', active: 'on' }),
    );

    const body = mutate.mock.calls[0]?.[0].body as { tiers: { denominationMinor: number }[] };
    expect(body.tiers).toHaveLength(2);
    expect(body.tiers[0]).toEqual({ denominationMinor: 100_000, discountBps: 1800, active: true });
  });

  it('withdraws a value when the box is unticked, rather than deleting the rung', async () => {
    await setVoucherTier({}, form({ ladder: LADDER, denomination: '1000', percent: '10' }));

    const body = mutate.mock.calls[0]?.[0].body as { tiers: { active: boolean }[] };
    expect(body.tiers[0]?.active).toBe(false);
  });

  it('refuses a percentage outside 0–100', async () => {
    const state = await setVoucherTier(
      {},
      form({ ladder: LADDER, denomination: '1000', percent: '140' }),
    );

    expect(state).toMatchObject({ field: 'percent' });
    expect(mutate).not.toHaveBeenCalled();
  });

  it('declares that subscription-svc answers it', async () => {
    await setVoucherTier({}, form({ ladder: LADDER, denomination: '1000', percent: '10' }));
    expect(mutate.mock.calls[0]?.[0].audit).toEqual({ auditedElsewhere: 'subscription-svc' });
  });
});

describe('DoD — a voucher tier change reaches the Driver App top-up screen', () => {
  it('writes the one table wallet-svc serves that screen from', async () => {
    // `billing.voucher_discount_tiers` has one writer pair and two readers: this
    // screen's `PUT /v1/admin/voucher-discount-tiers`, and the Driver App's
    // `GET /v1/wallet/voucher/discount-tiers`. One write, no synchronisation.
    await setVoucherTier({}, form({ ladder: '[]', denomination: '1000', percent: '10' }));

    expect(mutate.mock.calls[0]?.[0].path).toBe('/v1/admin/voucher-discount-tiers');
    expect(mutate.mock.calls[0]?.[0].method).toBe('PUT');
  });

  it('previews the price the driver will actually be charged', () => {
    // The wireframe's "Driver pays" column. Integer arithmetic on minor units,
    // because money is never a float on this platform.
    expect(voucherPriceMinor(100_000, 1000)).toBe(90_000);
    expect(voucherPriceMinor(1_000_000, 1800)).toBe(820_000);
    expect(voucherPriceMinor(300_000, 1300)).toBe(261_000);
  });

  it('round-trips a percentage through basis points', () => {
    expect(percentToBps(12.5)).toBe(1250);
    expect(bpsToPercent(1250)).toBe(12.5);
    expect(percentToBps(bpsToPercent(1875))).toBe(1875);
  });
});

describe('the Driver Level parameters', () => {
  it('omits every box that was left empty', async () => {
    // dispatch-svc's body is all-optional so "an admin changing one threshold
    // should not have to restate the other three". Sending zeros would set a
    // no-show penalty of zero because somebody edited the level-up threshold.
    await setLevelConfig({}, form({ levelUpThreshold: '600' }));

    expect(mutate.mock.calls[0]?.[0].body).toEqual({ levelUpThreshold: 600 });
  });

  it('refuses a submission with nothing in it rather than sending an empty object', async () => {
    const state = await setLevelConfig({}, form({}));

    expect(state.message).toContain('at least one value');
    expect(mutate).not.toHaveBeenCalled();
  });

  it('holds the Job Board floor to the range US-6A.8 leaves', async () => {
    const state = await setLevelConfig({}, form({ jobBoardMinLevel: '4' }));

    expect(state).toMatchObject({ field: 'jobBoardMinLevel' });
    expect(mutate).not.toHaveBeenCalled();
  });

  it('declares that dispatch-svc answers it', async () => {
    await setLevelConfig({}, form({ levelUpThreshold: '500' }));
    expect(mutate.mock.calls[0]?.[0].audit).toEqual({ auditedElsewhere: 'dispatch-svc' });
  });
});

describe('feature flags', () => {
  it('upserts the key, so a flag’s first appearance and its next change are one action', async () => {
    await setFeatureFlag({}, form({ key: 'new_pay_sheet', enabled: 'true', description: 'AL-59' }));

    expect(mutate.mock.calls[0]?.[0].method).toBe('PUT');
    expect(mutate.mock.calls[0]?.[0].path).toBe('/v1/admin/config/feature-flags/new_pay_sheet');
    expect(mutate.mock.calls[0]?.[0].body).toEqual({ enabled: true, description: 'AL-59' });
  });

  it('omits the description so the stored one is kept', async () => {
    await setFeatureFlag({}, form({ key: 'new_pay_sheet', enabled: 'false' }));
    expect(mutate.mock.calls[0]?.[0].body).toEqual({ enabled: false });
  });

  it('refuses a key the contract’s pattern would reject', async () => {
    const state = await setFeatureFlag({}, form({ key: 'New Pay Sheet', enabled: 'true' }));

    expect(state).toMatchObject({ field: 'key' });
    expect(mutate).not.toHaveBeenCalled();
  });

  it('declares FEATURE_FLAG_SET — admin-bff answers this one too', async () => {
    await setFeatureFlag({}, form({ key: 'new_pay_sheet', enabled: 'true' }));

    expect(mutate.mock.calls[0]?.[0].audit).toEqual({
      action: 'FEATURE_FLAG_SET',
      entity: 'feature_flag',
      entityId: 'new_pay_sheet',
    });
  });
});
