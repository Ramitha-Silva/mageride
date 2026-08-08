import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { DRIVER_TABS, PASSENGER_TABS, VEHICLE_TABS } from '@/api/directories';
import { ActivityPanel } from '@/components/directories/ActivityPanel';
import { DetailHeader } from '@/components/directories/DetailHeader';
import { Handoffs } from '@/components/directories/Handoffs';
import { LinkedVehicles } from '@/components/directories/LinkedVehicles';
import { MoreResults, ResultsTable } from '@/components/directories/ResultsTable';
import { SearchForm } from '@/components/directories/SearchForm';
import { menuPath, tabHref, vehicleDocHref, vehicleMediaHref } from '@/components/directories/links';
import { passengerRows, vehicleChips, type RenderContext } from '@/components/directories/model';
import { DocumentGrid } from '@/components/verification/DocumentGrid';
import { documentTiles } from '@/components/verification/model';
import { createAdminTranslator } from '@/i18n';

import { adminMenuManifest, menuFor } from './support/urd';

/**
 * SCR-AP-010…015 as they are drawn — the Definition-of-Done items that are
 * properties of the rendered screen rather than of the model behind it:
 *
 *  - each screen sits at the path its **nav item** gives it, so the AL-06 gate the
 *    proxy applies is the one the page is behind;
 *  - every documented criterion is a control an operator can actually fill in;
 *  - a mistyped id is marked on its own box rather than sent;
 *  - the document thumbnails on a vehicle detail open the **shared** viewer, and
 *    fetch their bytes through the vehicle directory's own gate;
 *  - nothing on any of the six screens writes anything — every action is a link to
 *    the screen that owns it, and only when the caller's menu carries that screen.
 */

const APP_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

afterEach(cleanup);

const t = createAdminTranslator('en');
const context: RenderContext = { t, locale: 'en' };

const PASSENGER = '0199a1f0-0000-7000-8000-000000090431';
const VEHICLE = '0199a1f0-0000-7000-8000-000000048213';
const DOC = '0199a1f0-0000-7000-8000-0000000000aa';

function sources(dir: string): { path: string; source: string }[] {
  const full = join(APP_ROOT, dir);
  if (!existsSync(full)) return [];

  const files: { path: string; source: string }[] = [];
  const walk = (at: string) => {
    for (const entry of readdirSync(at, { withFileTypes: true })) {
      const child = join(at, entry.name);
      if (entry.isDirectory()) walk(child);
      else if (/\.tsx?$/.test(entry.name)) {
        files.push({ path: child.slice(APP_ROOT.length + 1), source: readFileSync(child, 'utf8') });
      }
    }
  };
  walk(full);
  return files;
}

/** Strips comments, so a paragraph about mutation is not read as one. */
function code(source: string): string {
  return source.replaceAll(/\/\*[\s\S]*?\*\//g, '').replaceAll(/(^|[^:])\/\/.*$/gm, '$1');
}

describe('each screen is served at the path its nav item names', () => {
  // `routes.test.ts` holds `src/server/routes.ts` to `AdminMenu.cs`; this holds the
  // *files* to it. A page under any other path would be a screen the proxy
  // resolves to a different item's gate — or to no item, which is a 403.
  const paths = new Map(
    adminMenuManifest()
      .flatMap((group) => group.items)
      .map((item) => [item.key, item.path]),
  );

  it.each([
    ['passengers', 'SCR-AP-010'],
    ['drivers', 'SCR-AP-012'],
    ['vehicles', 'SCR-AP-014'],
  ])('%s (%s)', (key) => {
    const path = paths.get(key);
    expect(path, `AdminMenu.cs has no ${key} item`).toBeDefined();
    expect(existsSync(join(APP_ROOT, 'app/(portal)', path!, 'page.tsx'))).toBe(true);
  });

  it.each([
    ['passengers', '[passengerId]', 'SCR-AP-011'],
    ['drivers', '[driverId]', 'SCR-AP-013'],
    ['vehicles', '[vehicleId]', 'SCR-AP-015'],
  ])('%s detail (%s)', (key, segment) => {
    const path = paths.get(key)!;
    expect(existsSync(join(APP_ROOT, 'app/(portal)', path, segment, 'page.tsx'))).toBe(true);
  });

  it('serves a vehicle’s documents and their bytes under the vehicle directory', () => {
    const path = paths.get('vehicles')!;

    expect(existsSync(join(APP_ROOT, 'app/(portal)', path, '[vehicleId]/doc/[docId]/page.tsx'))).toBe(true);
    expect(existsSync(join(APP_ROOT, 'app/(portal)', path, 'media/[docId]/route.ts'))).toBe(true);
  });
});

describe('the search card', () => {
  const LABELS = {
    heading: 'Search drivers',
    hint: 'multiple criteria',
    submit: 'Search',
    clear: 'Clear',
    results: '2 results',
  };

  const FIELDS = [
    { kind: 'text' as const, name: 'name', label: 'Name', maxLength: 200 },
    { kind: 'text' as const, name: 'mobile', label: 'Mobile', maxLength: 20 },
    { kind: 'text' as const, name: 'id', label: 'Driver ID', maxLength: 64 },
    { kind: 'text' as const, name: 'nic', label: 'NIC no', maxLength: 20 },
    { kind: 'text' as const, name: 'regNo', label: 'Registration no', maxLength: 32 },
    {
      kind: 'select' as const,
      name: 'level',
      label: 'Driver Level',
      options: [
        { value: '', label: 'Any level' },
        { value: '1', label: 'L1' },
        { value: '2', label: 'L2' },
        { value: '3', label: 'L3' },
      ],
    },
    {
      kind: 'select' as const,
      name: 'status',
      label: 'Status',
      value: 'verified',
      options: [
        { value: 'verified', label: 'Verified' },
        { value: 'all', label: 'Any status' },
      ],
    },
  ];

  it('draws every documented criterion as a control, so they can be combined', () => {
    render(<SearchForm action="/drivers" fields={FIELDS} labels={LABELS} filtered={false} />);

    for (const field of FIELDS) {
      expect(screen.getByLabelText(field.label), `${field.name} has no control`).toBeDefined();
    }
  });

  it('is a GET form aimed at its own screen, so the criteria are the URL', () => {
    const { container } = render(
      <SearchForm action="/drivers" fields={FIELDS} labels={LABELS} filtered={false} />,
    );

    const form = container.querySelector('form')!;
    expect(form.getAttribute('method')).toBe('get');
    expect(form.getAttribute('action')).toBe('/drivers');
    // A new search is a new question: page three of the previous answer must not
    // be carried into it.
    expect(container.querySelector('input[name="cursor"]')).toBeNull();
  });

  it('offers the three Driver Levels the platform has and no more', () => {
    render(<SearchForm action="/drivers" fields={FIELDS} labels={LABELS} filtered={false} />);

    const options = within(screen.getByLabelText('Driver Level')).getAllByRole('option');
    expect(options.map((option) => option.textContent)).toEqual(['Any level', 'L1', 'L2', 'L3']);
  });

  it('marks a mistyped id on its own box', () => {
    render(
      <SearchForm
        action="/drivers"
        filtered
        labels={LABELS}
        fields={[
          {
            kind: 'text',
            name: 'id',
            label: 'Driver ID',
            maxLength: 64,
            value: 'DRV-22011',
            error: 'That is not a platform id.',
          },
        ]}
      />,
    );

    const control = screen.getByLabelText('Driver ID');
    expect(control.getAttribute('value')).toBe('DRV-22011');
    expect(control.getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByRole('alert').textContent).toContain('not a platform id');
  });

  it('offers Clear only once something is narrowed', () => {
    const { unmount } = render(
      <SearchForm action="/drivers" fields={FIELDS} labels={LABELS} filtered={false} />,
    );
    expect(screen.queryByRole('link', { name: LABELS.clear })).toBeNull();
    unmount();

    render(<SearchForm action="/drivers" fields={FIELDS} labels={LABELS} filtered />);
    expect(screen.getByRole('link', { name: LABELS.clear }).getAttribute('href')).toBe('/drivers');
  });
});

describe('the results list', () => {
  const LABELS = {
    caption: 'Passengers matching your search',
    columns: ['Passenger', 'Mobile', 'Trips', 'Joined', 'Status'],
    empty: 'No matching passengers.',
    open: 'Open',
  };

  const rows = passengerRows(
    [
      {
        passengerId: PASSENGER,
        name: 'Ramith de Silva',
        mobileMasked: '+9477*****67',
        trips: 128,
        joinedAt: '2026-01-14T04:00:00Z',
        status: 'active',
      },
    ],
    (id) => `/passengers/${id}?name=Ramith`,
    context,
  );

  it('opens the record on a link, and names it after its subject', () => {
    render(<ResultsTable rows={rows} labels={LABELS} />);

    const link = screen.getByRole('link', { name: 'Open the record for Ramith de Silva' });
    expect(link.getAttribute('href')).toBe(`/passengers/${PASSENGER}?name=Ramith`);
  });

  it('says the search found nothing rather than drawing a row nobody matched', () => {
    render(<ResultsTable rows={[]} labels={LABELS} />);

    expect(screen.getByText(LABELS.empty)).toBeDefined();
  });

  it('draws no row control at all on an activity tab', () => {
    // A trip is not a record with a screen behind it.
    render(<ResultsTable rows={rows} labels={{ ...LABELS, open: undefined }} />);

    expect(screen.queryByRole('link')).toBeNull();
  });

  it('offers the next page only forward, because a cursor names one page', () => {
    render(<MoreResults href="/passengers?cursor=abc" label="Next page" />);

    expect(screen.getByRole('link', { name: 'Next page' }).getAttribute('href')).toBe(
      '/passengers?cursor=abc',
    );
  });
});

describe('the record header', () => {
  it('goes back to the results the operator came from, criteria intact', () => {
    render(
      <DetailHeader
        backHref="/passengers?name=Ramith"
        backLabel="Passengers"
        title="Ramith de Silva"
        subjectId={PASSENGER}
        pill={{ tone: 'success', label: 'Active' }}
      />,
    );

    expect(screen.getByRole('link', { name: 'Passengers' }).getAttribute('href')).toBe(
      '/passengers?name=Ramith',
    );
    // The id is what an operator copies into a ticket or a suspension.
    expect(screen.getByText(PASSENGER)).toBeDefined();
  });
});

describe('the activity tabs', () => {
  const rows = [{ key: 'a', cells: [{ text: '17 Jun 08:32' }, { text: 'On-demand ride' }] }];

  function panel(current: string) {
    return (
      <ActivityPanel
        navLabel="Activity"
        tabs={PASSENGER_TABS.map((id) => ({
          id,
          href: tabHref(`/passengers/${PASSENGER}`, id, PASSENGER_TABS[0]),
          label: id,
          current: id === current,
        }))}
        rows={rows}
        labels={{ caption: 'Trips', columns: ['When', 'Journey'], empty: 'Nothing here.' }}
        note="Pick-up and drop-off are not part of this record."
      />
    );
  }

  it('draws each tab as a link, so one press is a navigation and survives Back', () => {
    render(panel('trips'));

    const links = screen.getAllByRole('link');
    expect(links).toHaveLength(PASSENGER_TABS.length);
    expect(links.map((link) => link.getAttribute('href'))).toEqual([
      `/passengers/${PASSENGER}`,
      `/passengers/${PASSENGER}?tab=payments`,
      `/passengers/${PASSENGER}?tab=packages`,
      `/passengers/${PASSENGER}?tab=disputes`,
    ]);
  });

  it('marks the tab being read for a screen reader as well as a sighted operator', () => {
    render(panel('packages'));

    const current = screen.getAllByRole('link').filter((link) => link.getAttribute('aria-current') === 'page');
    expect(current).toHaveLength(1);
    expect(current[0]?.getAttribute('href')).toContain('tab=packages');
  });

  it('says what the platform does not hold rather than heading a column it cannot fill', () => {
    render(panel('trips'));

    expect(screen.getByText(/Pick-up and drop-off/)).toBeDefined();
    expect(screen.queryByText('Route')).toBeNull();
  });

  it('gives the driver and the vehicle the tabs their wireframes draw', () => {
    expect([...DRIVER_TABS]).toEqual(['trips', 'wallet', 'dailyFee', 'transfers', 'reports']);
    expect([...VEHICLE_TABS]).toEqual(['trips', 'earnings', 'dailyFee', 'reports']);
  });
});

describe('a vehicle’s document thumbnails open the shared viewer', () => {
  const tiles = documentTiles(
    [
      { docId: DOC, kind: 'insurance' },
      { docId: '0199a1f0-0000-7000-8000-0000000000bb', kind: 'revenue_license' },
    ],
    {
      viewer: (docId) => vehicleDocHref({ regNo: 'ABC-1234' }, VEHICLE, docId),
      media: (docId) => vehicleMediaHref(docId, 'thumb'),
    },
    context,
  );

  it('links a tile to SCR-AP-003b under the vehicle’s own path', () => {
    render(
      <DocumentGrid
        tiles={tiles}
        labels={{ heading: 'Attached documents', hint: 'Tap a thumbnail', empty: 'None.', note: 'Each view is recorded.' }}
      />,
    );

    expect(screen.getAllByRole('link')[0]?.getAttribute('href')).toBe(
      `/vehicles/${VEHICLE}/doc/${DOC}?regNo=ABC-1234`,
    );
  });

  it('fetches every rendition through the audited viewer, on this screen’s gate', () => {
    // One tile is one `DOC_VIEW` row, and the relay is the vehicle directory's own
    // so an operator holding it is not refused by the verification queues' gate.
    const { container } = render(
      <DocumentGrid
        tiles={tiles}
        labels={{ heading: 'Attached documents', hint: 'Tap a thumbnail', empty: 'None.', note: 'Each view is recorded.' }}
      />,
    );

    const sources = [...container.querySelectorAll('img')].map((image) => image.getAttribute('src'));
    expect(sources).toEqual([
      `/vehicles/media/${DOC}?variant=thumb`,
      '/vehicles/media/0199a1f0-0000-7000-8000-0000000000bb?variant=thumb',
    ]);
    // Lazy loading would record what an officer scrolled past rather than what
    // the screen showed them.
    expect([...container.querySelectorAll('img')].some((image) => image.getAttribute('loading'))).toBe(false);
  });
});

describe('nothing on these screens writes anything', () => {
  // BR-28.8: "All are read-only — refunds route to Finance and wallet reversals
  // stay Finance-only." That is stronger than a role check, so it is asserted
  // against the *tree*: every file the six screens are made of, checked for a
  // mutation, a server action or a form that posts. `audit-screen.test.tsx` holds
  // SCR-AP-009 the same way, for the same reason.
  const FILES = [
    ...sources('app/(portal)/passengers'),
    ...sources('app/(portal)/drivers'),
    ...sources('app/(portal)/vehicles'),
    ...sources('src/components/directories'),
  ];

  it('found the six screens', () => {
    expect(FILES.length).toBeGreaterThan(10);
  });

  it('calls no mutation anywhere in the group', () => {
    for (const { path, source } of FILES) {
      expect(code(source), `${path} mutates`).not.toMatch(/\bmutate\s*[(<]/);
    }
  });

  it('imports no server action', () => {
    for (const { path, source } of FILES) {
      expect(code(source), `${path} imports a server action`).not.toMatch(
        /from '@\/server\/[\w-]*actions'/,
      );
    }
  });

  it('has no form that posts — the one form on each search is a GET', () => {
    for (const { path, source } of FILES) {
      for (const form of code(source).matchAll(/<form\b[^>]*>/g)) {
        expect(form[0], `${path} has a form that is not method="get"`).toMatch(/method="get"/);
      }
    }
  });

  const HANDOFFS = [
    { key: 'reversal', href: '/finance/adjustments?driverId=x', label: 'Reverse a daily fee' },
    { key: 'suspend', href: '/reports?subject=driver&subjectId=x#suspend', label: 'Suspend this driver' },
  ];

  it('draws each hand-off as a link and no control that moves money', () => {
    render(<Handoffs heading="Go to" items={HANDOFFS} />);

    expect(screen.getByRole('link', { name: /Reverse a daily fee/ }).getAttribute('href')).toBe(
      '/finance/adjustments?driverId=x',
    );
    expect(screen.queryByRole('button')).toBeNull();
  });

  it('draws nothing at all when the caller holds none of the screens', () => {
    const { container } = render(<Handoffs heading="Go to" items={[]} />);

    expect(container.firstChild).toBeNull();
  });

  it('offers a Support CSR the ticket queue and withholds the reversal form', () => {
    // Built from URD §2.3 rather than a fixture: the CSR holds Support · Read and
    // has ➖ on Driver wallet adjustments, so a reversal link would point at a
    // screen `proxy.ts` answers 403 on.
    const csr = menuFor(['support_csr']);

    expect(menuPath(csr, 'support-tickets')).toBe('/support/tickets');
    expect(menuPath(csr, 'wallet-adjustments')).toBeUndefined();
  });

  it('offers a Finance Officer the reversal form and withholds the verification queues', () => {
    const finance = menuFor(['finance_officer']);

    expect(menuPath(finance, 'wallet-adjustments')).toBe('/finance/adjustments');
    expect(menuPath(finance, 'verification')).toBeUndefined();
  });
});

describe('a driver’s linked vehicles', () => {
  const vehicle = {
    vehicleId: VEHICLE,
    regNo: 'ABC-1234',
    type: 'sedan' as const,
    mode: 'C' as const,
    status: 'APPROVED' as const,
    dispatchState: 'ACTIVE' as const,
    owned: true,
    link: `/v1/admin/vehicles/${VEHICLE}`,
  };

  const LABELS = { heading: 'Linked vehicles', empty: 'No vehicle is owned by or assigned to this driver.' };

  it('links a plate to the vehicle record when the operator holds that directory', () => {
    render(
      <LinkedVehicles
        vehicles={vehicleChips([vehicle], (id) => `/vehicles/${id}`, t)}
        labels={LABELS}
      />,
    );

    expect(screen.getByRole('link', { name: 'ABC-1234' }).getAttribute('href')).toBe(
      `/vehicles/${VEHICLE}`,
    );
  });

  it('still names the plate when they do not, rather than drawing a refused link', () => {
    render(<LinkedVehicles vehicles={vehicleChips([vehicle], null, t)} labels={LABELS} />);

    expect(screen.queryByRole('link')).toBeNull();
    expect(screen.getByText('ABC-1234')).toBeDefined();
  });

  it('says so when a driver drives nothing', () => {
    render(<LinkedVehicles vehicles={[]} labels={LABELS} />);

    expect(screen.getByText(LABELS.empty)).toBeDefined();
  });
});
