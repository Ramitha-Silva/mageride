import { readFileSync } from 'node:fs';

import { describe, expect, it } from 'vitest';

import {
  INVITABLE_FLEET_ROLES,
  isInvitableFleetRole,
  isSriLankanMobile,
  normaliseMobile,
  REGISTER_FLEET_PATH,
  MEMBERS,
} from '@/api/org';
import { canMutate } from '@/server/access';

import {
  FLEET_ENDPOINTS_SOURCE,
  SHARED_CONTRACT,
  sessionFor,
  sessionWithoutFleetRole,
  sessionWithoutOrganisation,
} from './support/fleet';

/**
 * **SCR-FP-002's two writes, against the service that answers them.**
 *
 * Both rules asserted here are fleet-svc's own and are transcribed rather than
 * invented, so each is checked against the file it was transcribed from: the
 * seats an invite may grant come out of `FleetEndpoints.cs`, and the mobile
 * pattern out of `_shared.yaml`. A transcription nothing checks is a
 * transcription that drifts.
 */

const FLEET_ENDPOINTS = readFileSync(FLEET_ENDPOINTS_SOURCE, 'utf8');
const SHARED = readFileSync(SHARED_CONTRACT, 'utf8');

describe('an invite grants a Manager or a Viewer, never an Owner', () => {
  it('offers the two seats AddMemberAsync admits', () => {
    // `fleetRole is not (FleetRoles.Manager or FleetRoles.Viewer)` → 400. The
    // contract's `FleetRole` has three values and this route takes two of them,
    // so a picker built off the schema would offer one fleet-svc refuses.
    expect(FLEET_ENDPOINTS).toMatch(
      /fleetRole is not \(FleetRoles\.Manager or FleetRoles\.Viewer\)/,
    );
    expect([...INVITABLE_FLEET_ROLES]).toEqual(['manager', 'viewer']);
  });

  it('refuses "owner" as a value the form could ever post', () => {
    expect(isInvitableFleetRole('manager')).toBe(true);
    expect(isInvitableFleetRole('viewer')).toBe(true);
    expect(isInvitableFleetRole('owner')).toBe(false);
    expect(isInvitableFleetRole('')).toBe(false);
  });

  it('is a decision about ownership, and fleet-svc says so', () => {
    // The reason matters more than the rule: `registry.fleets.owner_id` records
    // who the organisation belongs to and no route rewrites it, so a second
    // Owner is not a permission this screen is missing.
    expect(FLEET_ENDPOINTS).toMatch(/second Owner is a change of who the organisation belongs to/);
  });
});

describe('the contact mobile is _shared.yaml’s PhoneE164', () => {
  it('accepts exactly what the contract’s pattern accepts', () => {
    const declared = /PhoneE164:[\s\S]{0,200}?pattern: '([^']+)'/.exec(SHARED)?.[1];
    expect(declared).toBe(String.raw`^\+947\d{8}$`);

    const contract = new RegExp(declared!);
    for (const value of ['+94771234567', '+94712345678', '+94770000000']) {
      expect(isSriLankanMobile(value), value).toBe(contract.test(value));
      expect(isSriLankanMobile(value), value).toBe(true);
    }
    for (const value of ['0771234567', '+9477123456', '+94871234567', '771234567', '']) {
      expect(isSriLankanMobile(value), value).toBe(contract.test(value));
    }
  });

  it('normalises the two forms an operator actually types', () => {
    expect(normaliseMobile('0771234567')).toBe('+94771234567');
    expect(normaliseMobile('077 123 4567')).toBe('+94771234567');
    expect(normaliseMobile('94771234567')).toBe('+94771234567');
    expect(normaliseMobile('+94 77 123 4567')).toBe('+94771234567');
  });

  it('leaves an unrecognisable number alone, so the field refuses it', () => {
    // Silently "fixing" what it cannot parse would send fleet-svc a phone number
    // nobody entered.
    expect(isSriLankanMobile(normaliseMobile('12345'))).toBe(false);
    expect(isSriLankanMobile(normaliseMobile('+1 555 0100'))).toBe(false);
  });
});

describe('registering an organisation is the one mutation with no organisation', () => {
  it('is allowed for a fleet_owner who belongs to nothing yet', () => {
    const owner = sessionWithoutOrganisation();

    // Without the declaration this is refused, which is the shell's default and
    // the right one for every other write on the portal.
    expect(canMutate(owner, 'fleet-operations')).toBe(false);
    expect(canMutate(owner, 'fleet-operations', { allowsNoOrganisation: true })).toBe(true);
  });

  it('is still refused to an account that holds no fleet role at all', () => {
    // The exception is about the missing *organisation*, not about the missing
    // permission: `POST /v1/fleets` is `RequireMageRideRole(FleetOwner)`.
    expect(
      canMutate(sessionWithoutFleetRole(), 'fleet-operations', { allowsNoOrganisation: true }),
    ).toBe(false);
  });

  it('does not weaken any other write', () => {
    const viewer = sessionFor('viewer');
    expect(canMutate(viewer, 'fleet-operations', { allowsNoOrganisation: true })).toBe(false);

    const pendingOwner = sessionFor('owner', 'PENDING');
    expect(
      canMutate(pendingOwner, 'fleet-operations', {
        allowsNoOrganisation: true,
        requiresApprovedOrg: true,
      }),
    ).toBe(false);
  });

  it('is the one absolute path a screen names, and the roster is org-relative', () => {
    // `/v1/fleets` carries no `{fleetId}` because it creates the organisation the
    // others are scoped to; everything else is `{ org: … }` and gets the caller's
    // own id written in by the data layer.
    expect(REGISTER_FLEET_PATH).toBe('/v1/fleets');
    expect(MEMBERS.startsWith('/')).toBe(true);
    expect(MEMBERS.startsWith('/v1/')).toBe(false);
  });
});
