import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  analyticsFileName,
  analyticsRange,
  analyticsTotals,
  colomboToday,
  csvRows,
  CSV_BOM,
  DEFAULT_ANALYTICS_DAYS,
  daysBetween,
  FLEET_ALERTS,
  FLEET_ANALYTICS,
  FLEET_MAP,
  FLEET_SCHEDULES,
  idleHours,
  isBusinessDate,
  MAP_STALE_AFTER_SECONDS,
  MAX_ANALYTICS_DAYS,
  rangeProblem,
  shiftBusinessDate,
  singleDay,
  speedKmh,
  type VehicleAnalytics,
} from '@/api/insights';

import { contractEnum, FLEET_CONTRACT } from './support/fleet';

/**
 * **The three numbers SCR-FP-003/007/009 print that no response carries**, held
 * against the service that owns them, plus the arithmetic the screens do on top of
 * the answers.
 *
 * `MapStaleAfter`, `MaxAnalyticsDays` and the analytics default window are
 * `FleetOptions`' and `FleetInsightsService`'s. They are not on the wire and they
 * change what an operator is looking at — "no vehicle has reported in the last 15
 * minutes" is a sentence about a configured window — so a retuned deployment has
 * to show up here as a failing test rather than as a caption that is quietly wrong.
 */

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const FLEET_OPTIONS = join(REPO_ROOT, 'backend/src/Fleet.Api/Configuration/FleetOptions.cs');
const INSIGHTS_SERVICE = join(REPO_ROOT, 'backend/src/Fleet.Api/Operations/FleetInsightsService.cs');
const OPS_ENDPOINTS = join(REPO_ROOT, 'backend/src/Fleet.Api/Endpoints/FleetOpsEndpoints.cs');

const options = readFileSync(FLEET_OPTIONS, 'utf8');
const service = readFileSync(INSIGHTS_SERVICE, 'utf8');
const endpoints = readFileSync(OPS_ENDPOINTS, 'utf8');
const contract = readFileSync(FLEET_CONTRACT, 'utf8');

describe('the windows the screens name are the service’s own', () => {
  it('reads the map staleness window off FleetOptions', () => {
    const match = /MapStaleAfter\s*{\s*get;\s*set;\s*}\s*=\s*TimeSpan\.FromMinutes\((\d+)\)/.exec(
      options,
    );

    expect(match, 'FleetOptions.MapStaleAfter could not be read').toBeTruthy();
    expect(MAP_STALE_AFTER_SECONDS).toBe(Number(match![1]) * 60);
  });

  it('reads the analytics range cap off FleetOptions', () => {
    const match = /MaxAnalyticsDays\s*{\s*get;\s*set;\s*}\s*=\s*(\d+)/.exec(options);

    expect(match, 'FleetOptions.MaxAnalyticsDays could not be read').toBeTruthy();
    expect(MAX_ANALYTICS_DAYS).toBe(Number(match![1]));
  });

  it('opens on the same default window the service would have chosen', () => {
    // `var first = from ?? last.AddDays(-29);` — thirty inclusive days.
    const match = /first\s*=\s*from\s*\?\?\s*last\.AddDays\(-(\d+)\)/.exec(service);

    expect(match, 'the analytics default window could not be read').toBeTruthy();
    expect(DEFAULT_ANALYTICS_DAYS).toBe(Number(match![1]) + 1);
  });
});

describe('the four targets are the four routes', () => {
  it('names paths fleet-svc actually maps on the ops group', () => {
    for (const target of [FLEET_MAP, FLEET_ANALYTICS, FLEET_ALERTS, FLEET_SCHEDULES]) {
      expect(endpoints, `no ops route is mapped at ${target}`).toContain(`MapGet("${target}"`);
    }
  });

  it('names paths the contract declares under the org prefix', () => {
    for (const target of [FLEET_MAP, FLEET_ANALYTICS, FLEET_ALERTS, FLEET_SCHEDULES]) {
      expect(contract).toContain(`/v1/fleets/{fleetId}${target}:`);
    }
  });

  it('is org-relative, so the data layer is the only thing that names an organisation', () => {
    for (const target of [FLEET_MAP, FLEET_ANALYTICS, FLEET_ALERTS, FLEET_SCHEDULES]) {
      expect(target.startsWith('/')).toBe(true);
      expect(target).not.toContain('/v1/');
      expect(target).not.toContain('fleets');
    }
  });
});

describe('the shapes are the contract’s', () => {
  it('carries every required field of FleetVehiclePosition and VehicleAnalytics', () => {
    // The transcription is checked by reading the contract's own `required` list
    // rather than by trusting the interfaces: a field added to the contract as
    // required and missed here is a screen rendering `undefined`.
    const positionRequired = /FleetVehiclePosition:\s*\n\s*type: object\s*\n\s*required: \[([^\]]*)\]/
      .exec(contract)?.[1]
      ?.split(',')
      .map((field) => field.trim());

    expect(positionRequired).toEqual(['vehicleId', 'lat', 'lng', 'sampleTs']);

    const analyticsRequired = /VehicleAnalytics:\s*\n\s*type: object\s*\n\s*required: \[([^\]]*)\]/
      .exec(contract)?.[1]
      ?.split(',')
      .map((field) => field.trim());

    expect(analyticsRequired).toEqual(['vehicleId', 'tripCount', 'distanceKm']);
  });

  it('mirrors the alert kinds and the schedule states', () => {
    expect(contractEnum(contract, 'route_deviation')).toEqual([
      'route_deviation',
      'geofence_enter',
      'geofence_exit',
    ]);
    expect(contractEnum(contract, 'SCHEDULED')).toEqual([
      'SCHEDULED',
      'STARTED',
      'MISSED',
      'CANCELLED',
    ]);
  });

  it('knows earnings are absent by design, not by omission', () => {
    // `EarningsMinor: null` with the reason beside it. If fleet-svc ever starts
    // answering a figure, SCR-FP-009's "there is no earnings column" sentence is
    // wrong and this test is where that surfaces.
    const repository = readFileSync(
      join(REPO_ROOT, 'backend/src/Fleet.Api/Persistence/FleetInsightsRepository.cs'),
      'utf8',
    );
    expect(repository).toContain('EarningsMinor: null');
  });
});

describe('the reporting period', () => {
  it('accepts only a real BusinessDate', () => {
    expect(isBusinessDate('2026-06-17')).toBe(true);
    expect(isBusinessDate('2026-6-17')).toBe(false);
    expect(isBusinessDate('2026-02-30')).toBe(false);
    expect(isBusinessDate('')).toBe(false);
    expect(isBusinessDate(undefined)).toBe(false);
  });

  it('counts both ends, because `to` is inclusive of the day named', () => {
    expect(daysBetween('2026-06-01', '2026-06-17')).toBe(17);
    expect(daysBetween('2026-06-01', '2026-06-01')).toBe(1);
    expect(singleDay('2026-06-01').days).toBe(1);
  });

  it('shifts by calendar days across a month boundary', () => {
    expect(shiftBusinessDate('2026-03-01', -1)).toBe('2026-02-28');
    expect(shiftBusinessDate('2026-12-31', 1)).toBe('2027-01-01');
  });

  it('defaults to the last thirty days ending today', () => {
    const range = analyticsRange({}, '2026-06-17');
    expect(range).toEqual({ from: '2026-05-19', to: '2026-06-17', days: 30 });
  });

  it('keeps a range an operator typed', () => {
    expect(analyticsRange({ from: '2026-06-01', to: '2026-06-17' }, '2026-06-17')).toEqual({
      from: '2026-06-01',
      to: '2026-06-17',
      days: 17,
    });
  });

  it('falls back rather than sending a range the service would refuse', () => {
    // `from > to` is `400 validation-failed`, and so is a range over the cap.
    // Answering a typo with an error page instead of a report is a worse screen —
    // the page says the period was adjusted.
    expect(rangeProblem('2026-06-17', '2026-06-01')).toBe('inverted');
    expect(rangeProblem('2020-01-01', '2026-06-17')).toBe('too-long');
    expect(rangeProblem('2026-06-01', '2026-06-17')).toBe(null);

    expect(analyticsRange({ from: '2026-06-18', to: '2026-06-17' }, '2026-06-17').from).toBe(
      '2026-05-19',
    );
    expect(analyticsRange({ from: '2020-01-01', to: '2026-06-17' }, '2026-06-17').days).toBe(30);
  });

  it('reads today in Colombo, not in the container’s UTC', () => {
    // 19:00 UTC on the 16th is 00:30 on the 17th in Colombo — the five and a half
    // hours of the evening an operator is most likely to be looking at "today".
    expect(colomboToday(new Date('2026-06-16T19:00:00Z'))).toBe('2026-06-17');
    expect(colomboToday(new Date('2026-06-16T18:00:00Z'))).toBe('2026-06-16');
  });
});

describe('idle and the totals', () => {
  const range = { from: '2026-06-01', to: '2026-06-02', days: 2 };

  const bus: VehicleAnalytics = {
    vehicleId: 'A',
    tripCount: 10,
    distanceKm: 100,
    activeHours: 8,
    utilisationPct: 16.67,
  };
  const parked: VehicleAnalytics = {
    vehicleId: 'B',
    tripCount: 0,
    distanceKm: 0,
    activeHours: 0,
    utilisationPct: 0,
  };

  it('is the complement of the service’s own utilisation definition', () => {
    // 2 days × 24 h = 48 available hours; 8 active leaves 40 idle.
    expect(idleHours(bus, range)).toBe(40);
    expect(idleHours(parked, range)).toBe(48);
  });

  it('never goes negative on a row whose active hours overrun the period', () => {
    expect(idleHours({ ...bus, activeHours: 1_000 }, range)).toBe(0);
  });

  it('treats a missing activeHours as none rather than as unknown', () => {
    expect(idleHours({ vehicleId: 'C', tripCount: 0, distanceKm: 0 }, range)).toBe(48);
  });

  it('totals utilisation over the fleet, not as a mean of the column', () => {
    // A busy vehicle and a parked one: 8 active hours over 96 available is 8.3%,
    // where averaging 16.67% and 0% would report 8.3% by coincidence — so the
    // fixture makes them differ.
    const totals = analyticsTotals([bus, parked], range);

    expect(totals.vehicles).toBe(2);
    expect(totals.trips).toBe(10);
    expect(totals.distanceKm).toBe(100);
    expect(totals.utilisationPct).toBeCloseTo((8 * 100) / 96, 6);
    // (40 + 48) idle hours ÷ 2 vehicles ÷ 2 days.
    expect(totals.idleHoursPerDay).toBeCloseTo(22, 6);
  });

  it('reports zeros for a fleet with no vehicles rather than dividing by none', () => {
    const totals = analyticsTotals([], range);
    expect(totals).toEqual({
      vehicles: 0,
      trips: 0,
      distanceKm: 0,
      utilisationPct: 0,
      idleHoursPerDay: 0,
    });
  });
});

describe('speed', () => {
  it('converts the wire’s m/s to the km/h every speedometer here reads', () => {
    expect(speedKmh(10)).toBe(36);
    expect(speedKmh(0)).toBe(0);
  });

  it('answers null for a speed the device did not report', () => {
    expect(speedKmh(undefined)).toBe(null);
    expect(speedKmh(Number.NaN)).toBe(null);
  });
});

describe('the CSV', () => {
  it('leads with a BOM and separates rows with CRLF, for the spreadsheet', () => {
    const csv = csvRows([
      ['Vehicle', 'Trips'],
      ['NB-4521', 318],
    ]);

    expect(csv.startsWith(CSV_BOM)).toBe(true);
    expect(CSV_BOM).toBe('﻿');
    expect(csv.slice(1)).toBe('Vehicle,Trips\r\nNB-4521,318\r\n');
  });

  it('quotes a cell that would otherwise break the row', () => {
    expect(csvRows([['a,b', 'c"d', 'e\nf']]).slice(1)).toBe('"a,b","c""d","e\nf"\r\n');
  });

  it('puts the period in the file name', () => {
    expect(analyticsFileName({ from: '2026-06-01', to: '2026-06-17', days: 17 })).toBe(
      'mageride-fleet-analytics-2026-06-01-to-2026-06-17.csv',
    );
  });
});
