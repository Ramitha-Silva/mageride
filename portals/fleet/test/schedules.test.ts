import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import type { Assignment } from '@/api/drivers';
import {
  bySoonest,
  colomboLocalNow,
  colomboOffset,
  departAtFrom,
  driversCovering,
  isAlarmMinutes,
  isFutureDeparture,
  missedCount,
  scheduleStatusView,
  FLEET_SCHEDULES,
  SCHEDULE_ALARM_DEFAULT_MINUTES,
  SCHEDULE_ALARM_MAX_MINUTES,
  SCHEDULE_ALARM_MIN_MINUTES,
  SCHEDULE_EARLY_START_GRACE_MINUTES,
  SCHEDULE_IS_WRITE_ONCE,
  SCHEDULE_LOOKBACK_HOURS,
  type FleetSchedule,
} from '@/api/schedules';

import { FLEET_CONTRACT, FLEET_OPS_ENDPOINTS_SOURCE } from './support/fleet';

/**
 * **SCR-FP-008 against the contract and the service that answers it.**
 *
 * Everything this screen states as a fact — the alarm's bounds, the window the
 * table covers, what "On time" includes, whose app rings, and the two things the
 * platform has no route for — is transcribed from somewhere. A transcription
 * nothing checks is a transcription that drifts, so each is read back out of
 * `fleet.yaml` or out of the C# in this build.
 */

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');

const contract = readFileSync(FLEET_CONTRACT, 'utf8');
const opsEndpoints = readFileSync(FLEET_OPS_ENDPOINTS_SOURCE, 'utf8');
const scheduleService = readFileSync(
  join(REPO_ROOT, 'backend/src/Fleet.Api/Operations/ScheduleService.cs'),
  'utf8',
);
const alarmWorker = readFileSync(
  join(REPO_ROOT, 'backend/src/Fleet.Api/Operations/ScheduleAlarmWorker.cs'),
  'utf8',
);
const assignmentRepository = readFileSync(
  join(REPO_ROOT, 'backend/src/Fleet.Api/Persistence/FleetAssignmentRepository.cs'),
  'utf8',
);
const fleetOptions = readFileSync(
  join(REPO_ROOT, 'backend/src/Fleet.Api/Configuration/FleetOptions.cs'),
  'utf8',
);

function schedule(overrides: Partial<FleetSchedule> = {}): FleetSchedule {
  return {
    scheduleId: '01JQS000000000000000000001',
    vehicleId: '01JQV000000000000000000001',
    departAt: '2026-06-18T00:30:00Z',
    notStartedAlarmMinutes: 10,
    status: 'SCHEDULED',
    ...overrides,
  };
}

function assignment(overrides: Partial<Assignment> = {}): Assignment {
  return {
    assignmentId: 'a1',
    driverId: 'd1',
    vehicleId: '01JQV000000000000000000001',
    driverName: 'K. Fernando',
    from: '2026-06-01T00:00:00Z',
    active: true,
    ...overrides,
  };
}

describe('the target is fleet-svc’s own', () => {
  it('is org-relative, so the data layer is the only thing that names an organisation', () => {
    expect(FLEET_SCHEDULES).toBe('/schedules');
    expect(contract).toContain(`/v1/fleets/{fleetId}${FLEET_SCHEDULES}:`);
    expect(FLEET_SCHEDULES).not.toContain('/v1/');
  });

  it('mirrors the four states a departure can be in', () => {
    expect(contract).toContain('enum: [SCHEDULED, STARTED, MISSED, CANCELLED]');

    for (const status of ['SCHEDULED', 'STARTED', 'MISSED', 'CANCELLED'] as const) {
      expect(scheduleStatusView(status).labelKey.startsWith('fleet.scheduling.status.')).toBe(true);
    }

    // MISSED is the alarm's verdict and is the one the operator has to act on.
    expect(scheduleStatusView('MISSED').tone).toBe('error');
    expect(scheduleStatusView('STARTED').tone).toBe('success');
    expect(scheduleStatusView('CANCELLED').tone).toBe('neutral');
  });
});

describe('the alarm’s bounds are the contract’s and the service’s', () => {
  it('matches fleet.yaml’s notStartedAlarmMinutes', () => {
    const block = /notStartedAlarmMinutes:[\s\S]{0,300}?default: (\d+)/.exec(contract);
    expect(block, 'notStartedAlarmMinutes could not be read from the contract').not.toBeNull();

    expect(Number(block![1])).toBe(SCHEDULE_ALARM_DEFAULT_MINUTES);
    expect(contract).toMatch(
      new RegExp(
        `notStartedAlarmMinutes:[\\s\\S]{0,300}?minimum: ${SCHEDULE_ALARM_MIN_MINUTES}[\\s\\S]{0,120}?maximum: ${SCHEDULE_ALARM_MAX_MINUTES}`,
      ),
    );
  });

  it('matches ScheduleService’s own constants, which 0314’s CHECK repeats', () => {
    expect(scheduleService).toMatch(
      new RegExp(`MinAlarmMinutes = ${SCHEDULE_ALARM_MIN_MINUTES}`),
    );
    expect(scheduleService).toMatch(
      new RegExp(`MaxAlarmMinutes = ${SCHEDULE_ALARM_MAX_MINUTES}`),
    );
    expect(scheduleService).toMatch(
      new RegExp(`DefaultAlarmMinutes = ${SCHEDULE_ALARM_DEFAULT_MINUTES}`),
    );
  });

  it('refuses an offset the service would refuse', () => {
    expect(isAlarmMinutes(SCHEDULE_ALARM_MIN_MINUTES)).toBe(true);
    expect(isAlarmMinutes(SCHEDULE_ALARM_MAX_MINUTES)).toBe(true);
    expect(isAlarmMinutes(0)).toBe(false);
    expect(isAlarmMinutes(121)).toBe(false);
    expect(isAlarmMinutes(10.5)).toBe(false);
    expect(isAlarmMinutes(Number.NaN)).toBe(false);
  });

  it('states the early-start grace the deployment actually runs', () => {
    expect(fleetOptions).toMatch(
      new RegExp(
        `ScheduleEarlyStartGrace \\{ get; set; \\} = TimeSpan\\.FromMinutes\\(${SCHEDULE_EARLY_START_GRACE_MINUTES}\\)`,
      ),
    );
  });
});

describe('the window the table covers is the one the service answers with', () => {
  it('is a day back, as ListSchedulesAsync defaults it', () => {
    // `clock.GetUtcNow().AddDays(-1)` — "so the departures whose alarm just rang
    // are on the screen an operator opens to find out why".
    expect(opsEndpoints).toContain('clock.GetUtcNow().AddDays(-1)');
    expect(SCHEDULE_LOOKBACK_HOURS).toBe(24);
  });
});

describe('the departure clock is Colombo’s, not the container’s', () => {
  it('resolves a wall clock against Asia/Colombo', () => {
    // Six in the morning at the depot is 00:30 UTC. A Next container runs in UTC,
    // so `new Date('2026-06-18T06:00')` there would be 11:30 in Colombo — a bus
    // booked out five and a half hours late, with an alarm to match.
    expect(departAtFrom('2026-06-18T06:00')).toBe('2026-06-18T00:30:00.000Z');
    expect(departAtFrom('2026-06-18T06:00:00')).toBe('2026-06-18T00:30:00.000Z');
  });

  it('reads the offset from the zone rules rather than writing it down', () => {
    expect(colomboOffset(new Date('2026-06-18T00:00:00Z'))).toBe('+05:30');
    // No daylight saving: the same offset in January.
    expect(colomboOffset(new Date('2026-01-18T00:00:00Z'))).toBe('+05:30');
  });

  it('refuses anything that is not a wall clock', () => {
    expect(departAtFrom('')).toBeNull();
    expect(departAtFrom('tomorrow')).toBeNull();
    expect(departAtFrom('2026-06-18')).toBeNull();
    expect(departAtFrom('2026-13-18T06:00')).toBeNull();
  });

  it('writes the form’s `min` in the same wall clock', () => {
    expect(colomboLocalNow(new Date('2026-06-18T00:30:00Z'))).toBe('2026-06-18T06:00');
    expect(colomboLocalNow(new Date('2026-06-17T19:00:00Z'))).toBe('2026-06-18T00:30');
  });

  it('refuses a departure the service would refuse for being in the past', () => {
    const now = new Date('2026-06-18T00:30:00Z');
    expect(isFutureDeparture('2026-06-18T01:00:00Z', now)).toBe(true);
    expect(isFutureDeparture('2026-06-18T00:00:00Z', now)).toBe(false);
    expect(isFutureDeparture('not a date', now)).toBe(false);

    // The service's own reason, so the field's sentence and the 400 agree.
    expect(scheduleService).toContain('departAt must be in the future.');
  });
});

describe('whose app rings is the alarm worker’s question, asked the same way', () => {
  it('mirrors DriversCoveringAsync predicate for predicate', () => {
    const sql = /SELECT DISTINCT driver_id[\s\S]*?expires_at > @At\);/.exec(assignmentRepository);
    expect(sql, 'DriversCoveringAsync could not be read').not.toBeNull();

    expect(sql![0]).toContain('vehicle_id = @VehicleId');
    expect(sql![0]).toContain('revoked_at IS NULL');
    expect(sql![0]).toContain('valid_from <= @At');
    expect(sql![0]).toContain('(expires_at IS NULL OR expires_at > @At)');
  });

  it('asks about the booked departure and never about now', () => {
    // "An alarm raised at 06:20 about the 06:10 belongs to the 06:10's driver, and
    // a shift that changed in between must not redirect it."
    expect(alarmWorker).toContain('alarm.DepartAt');

    const departAt = '2026-06-18T00:30:00Z';
    const vehicle = '01JQV000000000000000000001';

    // Covers it: open-ended, started before.
    expect(driversCovering([assignment()], vehicle, departAt)).toHaveLength(1);

    // Another vehicle's driver. `GET …/assignments` answers the organisation's,
    // so without this predicate every departure would ring the whole fleet.
    expect(
      driversCovering([assignment({ vehicleId: '01JQV000000000000000000002' })], vehicle, departAt),
    ).toHaveLength(0);

    // Starts after the departure — a shift that changed in between.
    expect(
      driversCovering([assignment({ from: '2026-06-19T00:00:00Z' })], vehicle, departAt),
    ).toHaveLength(0);

    // Ended before it.
    expect(
      driversCovering([assignment({ to: '2026-06-17T00:00:00Z' })], vehicle, departAt),
    ).toHaveLength(0);

    // Still running over it.
    expect(
      driversCovering([assignment({ to: '2026-06-30T00:00:00Z' })], vehicle, departAt),
    ).toHaveLength(1);

    // Revoked is out whatever the window says.
    expect(
      driversCovering([assignment({ revokedAt: '2026-06-02T00:00:00Z' })], vehicle, departAt),
    ).toHaveLength(0);
  });

  it('does not read `active`, which is the database’s to evaluate', () => {
    // A future departure covered by an assignment the database calls inactive
    // today is still that departure's driver.
    expect(
      driversCovering(
        [assignment({ active: false })],
        '01JQV000000000000000000001',
        '2026-06-18T00:30:00Z',
      ),
    ).toHaveLength(1);
  });
});

describe('the write is gated where FleetOpsEndpoints gates it', () => {
  it('is Manager and approval-gated on the POST, and neither on the GET', () => {
    expect(opsEndpoints).toMatch(
      /MapPost\("\/schedules"[\s\S]{0,200}?RequireFleetSubRole\(FleetRoles\.Manager\)[\s\S]{0,80}?RequireApprovedFleet\(\)/,
    );
    expect(opsEndpoints).toMatch(
      /MapGet\("\/schedules"[\s\S]{0,160}?RequireFleetSubRole\(FleetRoles\.Viewer\)/,
    );

    // The read is not inside an approval-gated group either — that is what keeps
    // this screen open for a pending organisation.
    const opsGroup = /var ops = endpoints\.MapGroup\(FleetOpsGroup\)([\s\S]*?);/.exec(opsEndpoints);
    expect(opsGroup![1]).not.toContain('RequireApprovedFleet');
  });
});

describe('the two things the platform has no route for', () => {
  it('has no way to change or cancel a booked departure', () => {
    // `/v1/fleets/{fleetId}/schedules:` up to the next path. Two operations, and
    // if a third ever appears this constant has to be revisited rather than the
    // screen quietly continuing to say it cannot.
    const block = /\n {2}\/v1\/fleets\/\{fleetId\}\/schedules:\n([\s\S]*?)\n {2}\/v1\//.exec(
      contract,
    );
    expect(block, 'the schedules path could not be read from the contract').not.toBeNull();

    const verbs = [...block![1]!.matchAll(/^ {4}(get|post|put|patch|delete):/gm)].map(
      (match) => match[1],
    );

    expect(verbs.sort()).toEqual(['get', 'post']);
    expect(SCHEDULE_IS_WRITE_ONCE).toBe(true);
  });

  it('has no route that turns a schedule’s routeId into a name', () => {
    // `registry.fleet_schedules.route_id` is a `spatial.routes` UUID (migration
    // 1408). transit-svc's route read takes a GTFS id, which is a string in an
    // entirely different id space, so the two cannot be joined by a client.
    const transit = readFileSync(join(REPO_ROOT, 'backend/contracts/transit.yaml'), 'utf8');

    expect(transit).toContain('GTFS `route_id` from the active feed.');
    expect(transit).toMatch(/routeId\n\s+in: path[\s\S]{0,200}?type: string/);

    const migration = readFileSync(
      join(REPO_ROOT, 'db/migrations/1408__spatial_fleet_geofences.sql'),
      'utf8',
    );
    expect(migration).toContain('REFERENCES spatial.routes(id)');
  });
});

describe('the rows an operator scans', () => {
  it('orders soonest first, as listFleetSchedules answers', () => {
    const rows = [
      schedule({ scheduleId: 'late', departAt: '2026-06-18T02:00:00Z' }),
      schedule({ scheduleId: 'early', departAt: '2026-06-18T00:30:00Z' }),
    ].sort(bySoonest);

    expect(rows.map((row) => row.scheduleId)).toEqual(['early', 'late']);
  });

  it('counts the alarms the platform raised, and never raises one', () => {
    expect(
      missedCount([
        schedule({ status: 'MISSED', alarmRaisedAt: '2026-06-18T00:40:00Z' }),
        schedule({ status: 'SCHEDULED' }),
        schedule({ status: 'STARTED' }),
      ]),
    ).toBe(1);
  });
});
