import { existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { FARED_VEHICLE_TYPES } from '@/api/config';
import { FeatureFlagList } from '@/components/config/FeatureFlagList';
import { featureFlagRows, vehicleTypeLabels, voucherTierRows } from '@/components/config/model';
import { TariffForm } from '@/components/config/TariffForm';
import { VoucherTierPanel } from '@/components/config/VoucherTierPanel';
import { PermissionSetTable } from '@/components/rbac/PermissionSetTable';
import { createAdminTranslator } from '@/i18n';

import { adminMenuManifest } from './support/urd';

/**
 * SCR-AP-007 and SCR-AP-008 as they are drawn.
 *
 * Three wireframe affordances are deliberately not here, and each is asserted as
 * an absence:
 *
 *  - a **Vehicle types** tab. AL-09's list is a CHECK constraint, a `HashSet` and
 *    an enum; no route adds one, so a tab offering to configure them would be
 *    offering a migration.
 *  - a fare table pre-filled with the sketch's figures. Nothing serves the rates
 *    in force, and inventing them is how somebody publishes a version over the top
 *    of what is really running.
 *  - **permission toggles.** The matrix is read-only by design — a Super Admin who
 *    could edit it could grant themselves something URD §2.3 forbids.
 */

const APP_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

vi.mock('@/server/config-actions', () => ({
  publishTariffs: vi.fn(async () => ({})),
  setDailyFeeRate: vi.fn(async () => ({})),
  setVoucherTier: vi.fn(async () => ({})),
  setLevelConfig: vi.fn(async () => ({})),
  setFeatureFlag: vi.fn(async () => ({})),
}));

afterEach(cleanup);

const t = createAdminTranslator('en');
const context = { t, locale: 'en' } as const;

describe('every configuration screen sits at the path its nav item names', () => {
  const paths = new Map(
    adminMenuManifest()
      .flatMap((group) => group.items)
      .map((item) => [item.key, item.path]),
  );

  it.each([
    ['fare-tariffs'],
    ['daily-fee-rates'],
    ['voucher-tiers'],
    ['driver-levels'],
    ['feature-flags'],
  ])('%s', (key) => {
    const path = paths.get(key);
    expect(path, `AdminMenu.cs has no ${key} item`).toBeDefined();
    expect(existsSync(join(APP_ROOT, 'app/(portal)', path!, 'page.tsx'))).toBe(true);
  });

  it('leaves SCR-AP-016 to C110 and links to it as an entry point', () => {
    // The GTFS Dataset Manager is a screen this component does not own; the
    // deliverable here is the way in, which is the tab.
    expect(paths.get('gtfs')).toBe('/config/transit/gtfs');
    expect(existsSync(join(APP_ROOT, 'app/(portal)/config/transit/gtfs/page.tsx'))).toBe(false);
  });
});

describe('the fare-tariff form', () => {
  const LABELS = {
    heading: 'Fare tariffs',
    noReadNote: 'MageRide does not yet serve the rates currently in force back to this screen',
    modeANote: 'Bus and train are Mode A',
    caption: 'Mode C fare per vehicle type',
    vehicle: 'Vehicle',
    firstKm: 'First km (Rs)',
    perKm: 'Per km (Rs)',
    peak: 'Peak %',
    night: 'Night %',
    effectiveFrom: 'In force from',
    effectiveFromHint: 'Leave empty to publish immediately.',
    windowsHeading: 'Peak and night windows',
    peakWindow: 'Peak window',
    nightWindow: 'Night window',
    windowStart: 'Starts',
    windowEnd: 'Ends',
    windowPct: 'Uplift %',
    windowNote: 'A window may run past midnight',
    submit: 'Publish new tariffs',
    working: 'Saving…',
    audit: 'This action is written to the audit trail against your name.',
    saved: 'New tariff version published.',
    vehicleTypes: vehicleTypeLabels(context),
  };

  it('draws a row for every fared vehicle type and none for Mode A', () => {
    render(<TariffForm labels={LABELS} />);

    for (const type of FARED_VEHICLE_TYPES) {
      expect(
        screen.getByLabelText(`${LABELS.vehicleTypes[type]} — ${LABELS.firstKm}`),
      ).toBeDefined();
    }

    expect(screen.queryByLabelText(/Bus —/)).toBeNull();
    expect(screen.queryByLabelText(/Train —/)).toBeNull();
    expect(screen.getByText(LABELS.modeANote)).toBeDefined();
  });

  it('starts empty and says why, rather than showing figures it invented', () => {
    render(<TariffForm labels={LABELS} />);

    const sedan = screen.getByLabelText(
      `${LABELS.vehicleTypes.sedan} — ${LABELS.firstKm}`,
    ) as HTMLInputElement;

    expect(sedan.value).toBe('');
    expect(screen.getByText(LABELS.noReadNote)).toBeDefined();
  });

  it('offers a peak window and a night window, and says one may wrap midnight', () => {
    render(<TariffForm labels={LABELS} />);

    expect(screen.getByRole('group', { name: LABELS.peakWindow })).toBeDefined();
    expect(screen.getByRole('group', { name: LABELS.nightWindow })).toBeDefined();
    expect(screen.getByText(LABELS.windowNote)).toBeDefined();
  });

  it('offers no control that adds a vehicle type (AL-09)', () => {
    render(<TariffForm labels={LABELS} />);

    expect(screen.queryByRole('button', { name: /add.*vehicle|vehicle.*type/i })).toBeNull();
  });
});

describe('the voucher ladder', () => {
  const tiers = [
    { denominationMinor: 100_000, discountBps: 1000, active: true },
    { denominationMinor: 1_000_000, discountBps: 1800, active: false },
  ];

  const LABELS = {
    heading: 'Bulk voucher commission, by voucher value',
    note: 'This percentage is their whole margin and is charged once, at purchase.',
    caption: 'Voucher value, commission, what the driver pays and what they receive',
    denomination: 'Voucher value',
    percent: 'Commission %',
    pays: 'Driver pays',
    credit: 'Wallet credit',
    active: 'On sale',
    activeYes: 'On sale',
    activeNo: 'Withdrawn',
    empty: 'No voucher value has been configured yet.',
    editHeading: 'Add or change a voucher value',
    denominationHint: 'In rupees.',
    percentHint: 'The discount at purchase.',
    activeLabel: 'On sale',
    submit: 'Save this value',
    working: 'Saving…',
    audit: 'This change is answered by subscription-svc directly.',
    saved: 'Voucher ladder saved.',
  };

  it('draws the wireframe’s five columns', () => {
    render(<VoucherTierPanel rows={voucherTierRows(tiers, context)} ladder={tiers} labels={LABELS} />);

    for (const column of [
      LABELS.denomination,
      LABELS.percent,
      LABELS.pays,
      LABELS.credit,
      LABELS.active,
    ]) {
      expect(screen.getByRole('columnheader', { name: column })).toBeDefined();
    }
  });

  it('previews what the driver pays beside the face value they receive', () => {
    render(<VoucherTierPanel rows={voucherTierRows(tiers, context)} ladder={tiers} labels={LABELS} />);

    // Rs 1,000 at 10 % → pays 900, receives 1,000. The wireframe's own example.
    expect(screen.getByText('Rs 900.00')).toBeDefined();
    expect(screen.getByText('Rs 8,200.00')).toBeDefined();
  });

  it('sends the ladder that was rendered, so the write is a modification of it', () => {
    const { container } = render(
      <VoucherTierPanel rows={voucherTierRows(tiers, context)} ladder={tiers} labels={LABELS} />,
    );

    const hidden = container.querySelector('input[name="ladder"]') as HTMLInputElement;
    expect(JSON.parse(hidden.value)).toEqual(tiers);
  });

  it('says which service answers the write, because no audit row is written', () => {
    render(<VoucherTierPanel rows={voucherTierRows(tiers, context)} ladder={tiers} labels={LABELS} />);
    expect(screen.getByText(LABELS.audit)).toBeDefined();
  });
});

describe('the feature-flag list', () => {
  const LABELS = {
    heading: 'Platform feature flags',
    note: 'A flag takes effect for every request made after it is set.',
    on: 'On',
    off: 'Off',
    turnOn: 'Turn on',
    turnOff: 'Turn off',
    working: 'Saving…',
    updated: 'Last changed',
    empty: 'No feature flag has been set on this platform yet.',
    addHeading: 'Add a flag',
    key: 'Flag key',
    keyHint: 'Lower case.',
    description: 'What it does',
    descriptionHint: 'In your own words.',
    enable: 'Turn it on now',
    add: 'Save the flag',
    audit: 'This action is written to the audit trail against your name.',
    saved: 'Feature flag saved.',
  };

  it('offers the opposite of what each row is showing', () => {
    // The button posts the change the operator can see, not the state the server
    // happens to be in — so a stale page asks for the flip that is on screen.
    render(
      <FeatureFlagList
        flags={featureFlagRows(
          [
            { key: 'new_pay_sheet', enabled: true, updatedAt: '2026-06-17T03:10:00Z' },
            { key: 'job_board', enabled: false, updatedAt: '2026-06-17T03:10:00Z' },
          ],
          context,
        )}
        labels={LABELS}
      />,
    );

    expect(screen.getByRole('button', { name: LABELS.turnOff })).toBeDefined();
    expect(screen.getByRole('button', { name: LABELS.turnOn })).toBeDefined();
  });

  it('says so when the platform carries no flag at all', () => {
    render(<FeatureFlagList flags={[]} labels={LABELS} />);
    expect(screen.getByText(LABELS.empty)).toBeDefined();
  });
});

describe('SCR-AP-008’s permission set is a table, not a set of toggles', () => {
  it('renders the cell and offers nothing to change it with', () => {
    render(
      <PermissionSetTable
        rows={[
          {
            key: 'verification:verification_officer',
            area: 'verification',
            symbol: '✅',
            grants: 'Read · Change',
            qualifier: null,
            tone: 'success',
          },
          {
            key: 'role-management:verification_officer',
            area: 'role-management',
            symbol: '➖',
            grants: 'Nothing',
            qualifier: null,
            tone: 'neutral',
          },
        ]}
        labels={{
          heading: 'Permission set — Verification Officer',
          note: 'read-only',
          caption: 'What this role may do',
          area: 'Area',
          cell: 'Permission',
          capabilities: 'What that allows',
          empty: 'Nothing to show.',
        }}
      />,
    );

    expect(screen.getByText('verification')).toBeDefined();
    expect(screen.getByText('role-management')).toBeDefined();
    expect(screen.queryAllByRole('checkbox')).toHaveLength(0);
    expect(screen.queryAllByRole('switch')).toHaveLength(0);
    expect(screen.queryAllByRole('button')).toHaveLength(0);
  });
});
