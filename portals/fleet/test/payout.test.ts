import { readFileSync } from 'node:fs';

import { describe, expect, it } from 'vitest';

import {
  DOCUMENT_MAX_BYTES,
  LICENSED_BANKS,
  PAID_SERVICE_PAYMENT_BLOCKED_KEY,
  PAYOUT_DOCUMENTS,
  PAYOUT_DOCUMENT_KINDS,
  PAYOUT_PROFILE,
  PROOF_DOCUMENT_KINDS,
  canSetPaidServicePayment,
  isPayoutDocumentKind,
  payoutStatusView,
  type PayoutProfile,
  type PayoutProfileStatus,
} from '@/api/payout';
import { createFleetTranslator, isFleetMessageKey } from '@/i18n';
import { canMutate, dispositionFor, satisfiesFleetRole } from '@/server/access';

import { FLEET_CONTRACT, contractEnum, sessionFor } from './support/fleet';

/**
 * **SCR-FP-002a** (AL-49, US-27.1/27.2) — the shapes against `fleet.yaml`, and
 * the rule the rest of the portal reads off this screen.
 *
 * The gate is the component's Definition of Done item "the Paid classification
 * control is disabled with an explanatory message until Verified". The control
 * itself is SCR-FP-004's and lands with C113; what is asserted here is the pair
 * C113 imports — one predicate and one sentence — because a rule two screens
 * each derive is a rule two screens can each get wrong.
 */

const CONTRACT = readFileSync(FLEET_CONTRACT, 'utf8');

function profile(status: PayoutProfileStatus, overrides: Partial<PayoutProfile> = {}): PayoutProfile {
  return {
    bank: 'Commercial Bank of Ceylon',
    branch: 'Nugegoda',
    accountNo: '8001234567',
    accountHolderName: 'Lanka Transit (Pvt) Ltd',
    status,
    ...overrides,
  };
}

describe('the shapes are fleet.yaml’s', () => {
  it('carries the four statuses the contract declares, superseded included', () => {
    // `superseded` exists because the table is versioned *and* admits one
    // verified row per org: approving an edit has to move the incumbent out of
    // `verified`, and neither printed status could carry it.
    expect(contractEnum(CONTRACT, 'pending_verification')).toEqual([
      'pending_verification',
      'verified',
      'rejected',
      'superseded',
    ]);
  });

  it('uploads exactly the three kinds the route admits', () => {
    expect(contractEnum(CONTRACT, 'bank_statement')).toEqual([...PAYOUT_DOCUMENT_KINDS]);
    expect(isPayoutDocumentKind('lankaqr_code')).toBe(true);
    expect(isPayoutDocumentKind('registration')).toBe(false);
  });

  it('treats the statement and the passbook page as one slot', () => {
    // BR-31.1 asks for one *or* the other and §26 gives them one column, so the
    // screen draws one dropzone and asks which of the two the file is.
    expect([...PROOF_DOCUMENT_KINDS]).toEqual(['bank_statement', 'passbook_first_page']);
    expect(CONTRACT).toMatch(/proofDocId/);
    expect(CONTRACT).toMatch(/lankaqrDocId/);
  });

  it('addresses both routes inside the caller’s own organisation', () => {
    expect(PAYOUT_PROFILE).toBe('/payout-profile');
    expect(PAYOUT_DOCUMENTS).toBe('/payout-profile/documents');
    for (const target of [PAYOUT_PROFILE, PAYOUT_DOCUMENTS]) {
      expect(target.startsWith('/v1/')).toBe(false);
    }
  });

  it('bounds an upload at fleet-svc’s own ceiling', () => {
    expect(DOCUMENT_MAX_BYTES).toBe(8 * 1024 * 1024);
  });
});

describe('AL-49 — Paid Service payment waits on a verified profile', () => {
  it('is available on a verified profile and on nothing else', () => {
    expect(canSetPaidServicePayment(profile('verified'))).toBe(true);

    for (const status of ['pending_verification', 'rejected', 'superseded'] as const) {
      expect(canSetPaidServicePayment(profile(status)), status).toBe(false);
    }
  });

  it('is refused when the organisation has never submitted one', () => {
    // `GET …/payout-profile` answers 404 there, which the screen renders as an
    // empty form — but it is still no account for a subscriber's money to reach.
    expect(canSetPaidServicePayment(null)).toBe(false);
    expect(canSetPaidServicePayment(undefined)).toBe(false);
  });

  it('is the 409 fleet-svc would answer, and the contract says so', () => {
    expect(CONTRACT).toMatch(/409 RFC 7807|payout-profile-not-verified/);
    expect(CONTRACT).toMatch(/x-error-codes:.*payout-profile-not-verified/);
  });

  it('ships the explanation beside the predicate, in all three languages', () => {
    // A disabled control with no sentence beside it is a control an operator
    // reports as broken.
    expect(isFleetMessageKey(PAID_SERVICE_PAYMENT_BLOCKED_KEY)).toBe(true);

    for (const locale of ['si', 'ta', 'en'] as const) {
      const sentence = createFleetTranslator(locale)(PAID_SERVICE_PAYMENT_BLOCKED_KEY);
      expect(sentence.trim(), locale).not.toBe('');
    }
    expect(createFleetTranslator('en')(PAID_SERVICE_PAYMENT_BLOCKED_KEY)).toContain('verified');
  });
});

describe('the status chip is web_fleet.html’s', () => {
  it('draws Pending → Verified → Rejected in the three semantic tones', () => {
    expect(payoutStatusView(profile('pending_verification'))).toEqual({
      tone: 'warning',
      labelKey: 'fleet.payout.status.pending',
    });
    expect(payoutStatusView(profile('verified'))).toEqual({
      tone: 'success',
      labelKey: 'fleet.payout.status.verified',
    });
    expect(payoutStatusView(profile('rejected'))).toEqual({
      tone: 'error',
      labelKey: 'fleet.payout.status.rejected',
    });
  });

  it('draws a superseded version neutral rather than as a failure', () => {
    // It is a version an officer decided on and a newer one replaced. Red would
    // tell an owner something went wrong when what happened is that their edit
    // was approved.
    expect(payoutStatusView(profile('superseded')).tone).toBe('neutral');
  });

  it('says "not submitted" rather than "pending" when there is no profile', () => {
    expect(payoutStatusView(null)).toEqual({
      tone: 'neutral',
      labelKey: 'fleet.payout.status.none',
    });
  });

  it('names only keys the three resource tables carry', () => {
    for (const status of [
      'pending_verification',
      'verified',
      'rejected',
      'superseded',
    ] as const) {
      expect(isFleetMessageKey(payoutStatusView(profile(status)).labelKey)).toBe(true);
    }
    expect(isFleetMessageKey(payoutStatusView(null).labelKey)).toBe(true);
  });
});

describe('SCR-FP-002a is the Owner’s, and the screen is gated three times', () => {
  it('is not offered to a Manager or a Viewer, by URL or in the nav', () => {
    for (const role of ['manager', 'viewer'] as const) {
      expect(dispositionFor(sessionFor(role), '/org/payout'), role).toBe('denied');
    }
    expect(dispositionFor(sessionFor('owner'), '/org/payout')).toBe('render');
  });

  it('is refused by the page’s own check for the same two seats', () => {
    // The page repeats the proxy's decision, so a matcher that stopped covering
    // the path turns into a 403 rather than into a rendered screen.
    const opens = (role: 'owner' | 'manager' | 'viewer') => {
      const session = sessionFor(role);
      return canMutate(session, 'fleet-billing') && satisfiesFleetRole(session.fleetRole, 'owner');
    };

    expect(opens('owner')).toBe(true);
    expect(opens('manager')).toBe(false);
    expect(opens('viewer')).toBe(false);
  });

  it('opens for a pending organisation, because the officer reads it before approving', () => {
    expect(dispositionFor(sessionFor('owner', 'PENDING'), '/org/payout')).toBe('render');
  });
});

describe('the bank list', () => {
  it('is a non-empty set of distinct names', () => {
    expect(LICENSED_BANKS.length).toBeGreaterThan(10);
    expect(new Set(LICENSED_BANKS).size).toBe(LICENSED_BANKS.length);
  });

  it('fits the contract’s own length bound', () => {
    for (const bank of LICENSED_BANKS) expect(bank.length).toBeLessThanOrEqual(120);
  });
});
