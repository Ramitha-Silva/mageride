import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  DEFAULT_DRIVER_STATUS,
  driverPath,
  passengerPath,
  vehiclePath,
  DIRECTORY_PAGE_SIZE,
  driverQuery,
  driverSearch,
  driverSelection,
  isFiltered,
  isSendable,
  PASSENGER_TABS,
  passengerQuery,
  passengerSearch,
  passengerSelection,
  tabSelection,
  VEHICLE_TABS,
  vehicleQuery,
  vehicleSearch,
  vehicleSelection,
} from '@/api/directories';
import {
  driverHref,
  passengerHref,
  tabHref,
  vehicleDocHref,
  vehicleHref,
  vehicleMediaHref,
  vehiclesHref,
} from '@/components/directories/links';

/**
 * The Definition-of-Done item this file is: **every documented search criterion
 * works singly and in combination.**
 *
 * "Works" is a property of the query the screen builds, so it is asserted there
 * rather than through a rendered form: each criterion appears on its own, all of
 * them appear together, and nothing a criterion does depends on which of its
 * neighbours are filled. The three searches are checked against
 * `admin-bff.yaml`'s own parameter names, because a criterion the screen spells
 * differently is a filter that silently does nothing.
 */

const ID = '0199a1f0-0000-7000-8000-000000090431';

const DIRECTORY_ENDPOINTS = resolve(
  dirname(fileURLToPath(import.meta.url)),
  '../../../backend/src/AdminBff/Endpoints/DirectoryEndpoints.cs',
);

describe('opening a record is the audited act (D-35, US-21.14)', () => {
  /**
   * The Definition-of-Done item "opening a detail records a `PII_READ` audit
   * entry", asserted where it is actually decided.
   *
   * The portal does not write the row and must not: admin-bff's interceptor writes
   * it once the response is known to be a success, which is the only way the row
   * and the disclosure cannot disagree. So what this side has to guarantee is that
   * the **detail read goes to the route carrying `.Audited(PiiRead, …)`** — and
   * that is checked against `DirectoryEndpoints.cs` itself rather than against a
   * path somebody typed here, so a route that loses its audit declaration fails
   * this build. `test/support-model.test.ts` parses the C# for the same reason.
   */
  const source = readFileSync(DIRECTORY_ENDPOINTS, 'utf8');

  /** The `admin.MapGet("…", Handler)` … `.Audited(PiiRead, …)` chains, as written. */
  function auditedGets(): string[] {
    return [...source.matchAll(/MapGet\("([^"]+)"[\s\S]*?;/g)]
      .filter((match) => /\.Audited\(\s*AdminAuditActions\.PiiRead/.test(match[0]))
      .map((match) => `/v1/admin${match[1]}`);
  }

  it('audits exactly the three detail reads and neither of the searches', () => {
    // A search must not write a row: it discloses only masked numbers, and a row
    // per keystroke would bury the ones that name a person.
    expect(auditedGets().sort()).toEqual([
      '/v1/admin/drivers/{driverId:guid}',
      '/v1/admin/passengers/{passengerId:guid}',
      '/v1/admin/vehicles/{vehicleId:guid}',
    ]);
  });

  it.each([
    ['a passenger', passengerPath, '/v1/admin/passengers/{passengerId:guid}'],
    ['a driver', driverPath, '/v1/admin/drivers/{driverId:guid}'],
    ['a vehicle', vehiclePath, '/v1/admin/vehicles/{vehicleId:guid}'],
  ])('reads %s through the route that carries the PII_READ declaration', (_label, path, route) => {
    expect(auditedGets()).toContain(route);
    expect(path(ID)).toBe(route.replace(/\{[^}]+\}/, ID));
  });

  it('sends no write anywhere under the three directories', () => {
    // BR-28.8: "All are read-only." admin-bff holds the same fence against its own
    // route table (`No_directory_route_accepts_a_write`); this holds it against the
    // portal's own module, which is the only place a directory request is built.
    const module = readFileSync(
      resolve(dirname(fileURLToPath(import.meta.url)), '../src/api/directories.ts'),
      'utf8',
    ).replaceAll(/\/\*[\s\S]*?\*\//g, '');

    expect(module).not.toMatch(/\bmutate\b/);
    expect(module).not.toMatch(/'(POST|PUT|PATCH|DELETE)'/);
  });
});

describe('the passenger directory · SCR-AP-010', () => {
  const CRITERIA = [
    ['name', 'Ramith'],
    ['mobile', '+94771234567'],
    ['id', ID],
    ['email', 'ramith@example.lk'],
  ] as const;

  it.each(CRITERIA)('sends %s on its own', (key, value) => {
    const query = passengerSearch(passengerSelection({ [key]: value }));

    expect(query).toEqual({ [key]: value, limit: DIRECTORY_PAGE_SIZE });
  });

  it('sends all four together, because they combine with AND', () => {
    const query = passengerSearch(
      passengerSelection(Object.fromEntries(CRITERIA.map(([key, value]) => [key, value]))),
    );

    expect(query).toEqual({
      name: 'Ramith',
      mobile: '+94771234567',
      id: ID,
      email: 'ramith@example.lk',
      limit: DIRECTORY_PAGE_SIZE,
    });
  });

  it('treats a box the operator cleared as no criterion at all', () => {
    // A `method="get"` form submits every input, so an emptied search box arrives
    // as `?name=`. Forwarding it would answer an empty page for a search they
    // think they cancelled.
    expect(passengerSearch(passengerSelection({ name: '   ', email: '' }))).toEqual({
      limit: DIRECTORY_PAGE_SIZE,
    });
  });

  it('caps a criterion at the length the contract declares', () => {
    const query = passengerSearch(passengerSelection({ mobile: '9'.repeat(40) }));

    expect(query.mobile).toHaveLength(20);
  });
});

describe('a mistyped id asks admin-bff nothing', () => {
  // `DirectoryEndpoints.Identifier` parses `?id=` as a UUID and throws a 400
  // naming the field. Sending it would answer the operator's first press with a
  // validation error about a form they are still filling in — C105's
  // `awaitingRange` rule.
  it.each([
    ['a passenger', passengerSelection],
    ['a driver', driverSelection],
    ['a vehicle', vehicleSelection],
  ])('refuses to send one from %s search', (_label, select) => {
    const selection = select({ id: 'PAX-90431' });

    expect(isSendable(selection)).toBe(false);
    expect(selection.id).toBeUndefined();
    expect(selection.invalidId).toBe(true);
  });

  it('keeps what the operator typed, so the box can show it back to them', () => {
    expect(passengerSelection({ id: 'PAX-90431' }).rawId).toBe('PAX-90431');
    // …and it travels on the portal's own links, so Back does not silently empty
    // a box the operator is still fixing.
    expect(passengerQuery(passengerSelection({ id: 'PAX-90431' })).id).toBe('PAX-90431');
  });

  it('sends a well-formed one', () => {
    const selection = vehicleSelection({ id: ID });

    expect(isSendable(selection)).toBe(true);
    expect(vehicleSearch(selection).id).toBe(ID);
  });
});

describe('the driver directory · SCR-AP-012', () => {
  it('defaults to verified drivers, and says so on the wire', () => {
    // US-24.10. The screen's caption claims it, so the query has to state it
    // rather than rely on admin-bff's own default staying what it is today.
    expect(driverSelection({}).status).toBe(DEFAULT_DRIVER_STATUS);
    expect(driverSearch(driverSelection({})).status).toBe('verified');
  });

  it.each([
    ['name', 'K. Fernando', { name: 'K. Fernando' }],
    ['mobile', '+94771234567', { mobile: '+94771234567' }],
    ['id', ID, { id: ID }],
    ['nic', '199012345678', { nic: '199012345678' }],
    ['regNo', 'ABC-1234', { regNo: 'ABC-1234' }],
    ['level', '2', { level: 2 }],
  ])('sends %s on its own', (key, value, expected) => {
    expect(driverSearch(driverSelection({ [key]: value }))).toEqual({
      ...expected,
      status: 'verified',
      limit: DIRECTORY_PAGE_SIZE,
    });
  });

  it('sends every criterion together', () => {
    const query = driverSearch(
      driverSelection({
        name: 'K. Fernando',
        mobile: '+94771234567',
        id: ID,
        nic: '199012345678',
        regNo: 'ABC-1234',
        level: '3',
        status: 'all',
      }),
    );

    expect(query).toEqual({
      name: 'K. Fernando',
      mobile: '+94771234567',
      id: ID,
      nic: '199012345678',
      regNo: 'ABC-1234',
      level: 3,
      status: 'all',
      limit: DIRECTORY_PAGE_SIZE,
    });
  });

  it('drops a Driver Level the platform does not have', () => {
    // `dispatch.driver_levels.level` is 1–3 and `searchDrivers` answers 400
    // outside it. The wireframe's "L1–L5" is the deviation; see the C109 handoff.
    expect(driverSelection({ level: '5' }).level).toBeUndefined();
    expect(driverSelection({ level: '0' }).level).toBeUndefined();
    expect(driverSelection({ level: 'two' }).level).toBeUndefined();
  });

  it('falls back to the default rather than forwarding a status nobody offers', () => {
    expect(driverSelection({ status: 'retired' }).status).toBe('verified');
  });

  it('leaves the default off the portal query, so a plain URL is the plain screen', () => {
    expect(driverQuery(driverSelection({ name: 'K.' }))).toEqual({ name: 'K.' });
    expect(driverQuery(driverSelection({ status: 'all' })).status).toBe('all');
  });
});

describe('the vehicle directory · SCR-AP-014', () => {
  it.each([
    ['regNo', 'ABC-1234', { regNo: 'ABC-1234' }],
    ['id', ID, { id: ID }],
    ['type', 'sedan', { type: 'sedan' }],
    ['mode', 'A', { mode: 'A' }],
    ['ownerMobile', '+94771234567', { ownerMobile: '+94771234567' }],
    ['fleetOrg', 'Lanka Transit', { fleetOrg: 'Lanka Transit' }],
    ['status', 'APPROVED', { status: 'APPROVED' }],
  ])('sends %s on its own', (key, value, expected) => {
    expect(vehicleSearch(vehicleSelection({ [key]: value }))).toEqual({
      ...expected,
      limit: DIRECTORY_PAGE_SIZE,
    });
  });

  it('sends every criterion together', () => {
    const query = vehicleSearch(
      vehicleSelection({
        regNo: 'ABC-1234',
        id: ID,
        type: 'bus',
        mode: 'A',
        ownerMobile: '+94771234567',
        fleetOrg: 'Lanka Transit',
        status: 'APPROVED',
      }),
    );

    expect(query).toEqual({
      regNo: 'ABC-1234',
      id: ID,
      type: 'bus',
      mode: 'A',
      ownerMobile: '+94771234567',
      fleetOrg: 'Lanka Transit',
      status: 'APPROVED',
      limit: DIRECTORY_PAGE_SIZE,
    });
  });

  it('drops a type, mode or status outside the platform’s own enums', () => {
    // admin-bff validates each rather than passing it through, "because a typo'd
    // enum would answer 200 with an empty page, which reads as no such vehicle".
    expect(vehicleSelection({ type: 'car' }).type).toBeUndefined();
    expect(vehicleSelection({ mode: 'D' }).mode).toBeUndefined();
    expect(vehicleSelection({ status: 'ACTIVE' }).status).toBeUndefined();
  });
});

describe('paging is forward and it is in the URL', () => {
  it('carries the cursor onto the next page and nothing else changes', () => {
    const selection = vehicleSelection({ regNo: 'ABC' });
    const next = vehiclesHref({ ...selection, cursor: 'opaque-cursor' });

    expect(next).toBe('/vehicles?regNo=ABC&cursor=opaque-cursor');
    expect(vehicleSearch(vehicleSelection({ regNo: 'ABC', cursor: 'opaque-cursor' })).cursor).toBe(
      'opaque-cursor',
    );
  });

  it('does not count a cursor as a filter the operator can clear', () => {
    expect(isFiltered(vehicleQuery(vehicleSelection({ cursor: 'x' })))).toBe(false);
    expect(isFiltered(vehicleQuery(vehicleSelection({ regNo: 'ABC' })))).toBe(true);
  });
});

describe('a record keeps the search that found it', () => {
  it('carries the criteria onto the detail and back again', () => {
    const selection = driverSelection({ nic: '199012345678', status: 'all' });

    expect(driverHref(selection, ID)).toBe(`/drivers/${ID}?nic=199012345678&status=all`);
  });

  it('carries them onto a tab, and leaves the first tab unnamed', () => {
    const selection = passengerSelection({ name: 'Ramith' });
    const detail = passengerHref(selection, ID);

    expect(tabHref(detail, 'trips', PASSENGER_TABS[0])).toBe(`/passengers/${ID}?name=Ramith`);
    expect(tabHref(detail, 'payments', PASSENGER_TABS[0])).toBe(
      `/passengers/${ID}?name=Ramith&tab=payments`,
    );
    expect(tabHref(`/passengers/${ID}`, 'disputes', PASSENGER_TABS[0])).toBe(
      `/passengers/${ID}?tab=disputes`,
    );
  });

  it('carries them into the document viewer and back out of it', () => {
    const selection = vehicleSelection({ regNo: 'ABC-1234' });
    const doc = '0199a1f0-0000-7000-8000-0000000000aa';

    expect(vehicleDocHref(selection, ID, doc)).toBe(
      `/vehicles/${ID}/doc/${doc}?regNo=ABC-1234`,
    );
    expect(vehicleHref(selection, ID)).toBe(`/vehicles/${ID}?regNo=ABC-1234`);
  });

  it('fetches a vehicle’s renditions through the vehicle directory’s own gate', () => {
    // Not `/verification/media/…`: `proxy.ts` gates a route on the screen its path
    // resolves to, and a Support CSR holds the vehicle directory without the
    // verification queues.
    expect(vehicleMediaHref('0199a1f0-0000-7000-8000-0000000000aa', 'thumb')).toBe(
      '/vehicles/media/0199a1f0-0000-7000-8000-0000000000aa?variant=thumb',
    );
  });
});

describe('the tab is the URL', () => {
  it('opens the first tab for a URL that names none, or names one that does not exist', () => {
    expect(tabSelection(PASSENGER_TABS, {})).toBe('trips');
    expect(tabSelection(PASSENGER_TABS, { tab: 'ledger' })).toBe('trips');
    expect(tabSelection(VEHICLE_TABS, { tab: 'wallet' })).toBe('trips');
  });

  it('opens the tab a URL names', () => {
    expect(tabSelection(PASSENGER_TABS, { tab: 'packages' })).toBe('packages');
    expect(tabSelection(VEHICLE_TABS, { tab: 'earnings' })).toBe('earnings');
  });
});
