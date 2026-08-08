import { describe, expect, it } from 'vitest';

import type { DocumentRef, ExtractedField, OrgQueueRow, VerificationStep } from '@/api/verification';
import {
  documentTiles,
  driverQueueRows,
  fieldLabel,
  fieldRows,
  orgQueueRows,
  pendingSummary,
  queueCount,
  statusPill,
  stepRows,
  viewerPosition,
  type RenderContext,
} from '@/components/verification/model';
import { createAdminTranslator } from '@/i18n';

/**
 * SCR-AP-003's view model — the rules the screen exists to hold, asserted where
 * they are decided rather than through the markup that draws them.
 *
 *  - **only a Pending field is decidable.** AL-27's fence on the row that carries
 *    it: an auto-verified value was never in question, and C063 answers a confirm
 *    on one with a 404.
 *  - **a plate mismatch is not a doubtful scan.** The wireframe draws three
 *    different pending states and each is derivable from the field itself.
 *  - **an unknown identifier renders as itself.** admin-bff can extract a field
 *    key this build has never heard of, and an empty cell would be a field an
 *    officer decides on without knowing what it is.
 */

const t = createAdminTranslator('en');
const context: RenderContext = { t, locale: 'en' };

function field(over: Partial<ExtractedField> = {}): ExtractedField {
  return {
    key: 'licence_no',
    value: 'B1234567',
    source: 'ai',
    confidence: 0.98,
    verifyStatus: 'auto_verified',
    ...over,
  };
}

describe('the queue rows', () => {
  it('counts the flagged fields into the pending pill, as the wireframe does', () => {
    expect(statusPill('PENDING', 2, t)).toEqual({ tone: 'warning', label: 'Pending · 2' });
    expect(statusPill('PENDING', 0, t)).toEqual({ tone: 'warning', label: 'Pending' });
  });

  it('carries the subject’s own status, not the queue’s', () => {
    // Every row here is awaiting review by construction — membership *is* "a field
    // is still pending" (C063). What the status column distinguishes is a renewal
    // flagged on somebody already approved from a resubmission after a refusal,
    // which is what D2's status filter filters on.
    expect(statusPill('APPROVED', 1, t)).toMatchObject({ tone: 'success', label: 'Approved' });
    expect(statusPill('REJECTED', 1, t)).toMatchObject({ tone: 'error', label: 'Rejected' });
  });

  it('translates each flagged field key and joins them', () => {
    const [row] = driverQueueRows(
      [
        {
          driverId: '0199a1f0-0000-7000-8000-000000000001',
          name: 'K. Fernando',
          submittedAt: '2026-06-17T03:10:00Z',
          flaggedFields: ['nic_no', 'allowed_vehicle_types'],
          status: 'PENDING',
        },
      ],
      (id) => `/verification/${id}`,
      context,
    );

    expect(row?.primary).toBe('K. Fernando');
    expect(row?.cells[1].text).toBe('NIC no · Allowed vehicle types');
    // Asia/Colombo, not the container's zone: 03:10Z is 08:40 on a Sri Lankan clock.
    expect(row?.cells[0].text).toContain('08:40');
  });

  it('gives an organisation its two verdicts rather than one merged pill', () => {
    const org: OrgQueueRow = {
      orgId: '0199a1f0-0000-7000-8000-000000000002',
      name: 'Lanka Transit (Pvt) Ltd',
      kycStatus: 'complete',
      vehicleCount: 120,
      status: 'PENDING',
      payoutProfileStatus: 'pending_verification',
    };

    const [row] = orgQueueRows([org], (id) => `/verification/org/${id}`, context);

    expect(row?.href).toBe('/verification/org/0199a1f0-0000-7000-8000-000000000002');
    expect(row?.cells[0].text).toBe('120 vehicles');
    expect(row?.cells[1].pills?.map((pill) => pill.label)).toEqual([
      'KYC complete',
      'Payout pending',
    ]);
  });
});

describe('a tab’s badge', () => {
  it('is the exact count when the queue answered a whole page', () => {
    expect(queueCount({ items: [1, 2, 3], hasMore: false }, context)).toBe('3');
  });

  it('says so rather than reporting a page size as a backlog', () => {
    expect(queueCount({ items: Array(100).fill(0), hasMore: true }, context)).toBe('100+');
  });

  it('is “—” for a queue that could not be read, never 0', () => {
    // "Nothing waiting" is a claim, and a 503 is not evidence for it.
    expect(queueCount(null, context)).toBe('—');
  });
});

describe('the extracted fields', () => {
  it('offers a decision only on a pending row', () => {
    const rows = fieldRows(
      [
        field({ key: 'licence_no', verifyStatus: 'auto_verified' }),
        field({ key: 'nic_no', source: 'manual', confidence: null, verifyStatus: 'pending' }),
        field({ key: 'revenue_no', verifyStatus: 'confirmed' }),
      ],
      context,
    );

    expect(rows.map((row) => row.decidable)).toEqual([false, true, false]);
  });

  it('reads a plate mismatch differently from a doubtful scan', () => {
    const [mismatch, doubtful, manual] = fieldRows(
      [
        field({ key: 'reg_no_match', confidence: 0.71, verifyStatus: 'pending' }),
        field({ key: 'insurance_expiry', confidence: 0.62, verifyStatus: 'pending' }),
        field({ key: 'nic_no', source: 'manual', confidence: null, verifyStatus: 'pending' }),
      ],
      context,
    );

    expect(mismatch?.status).toEqual({ tone: 'error', label: 'Pending · mismatch' });
    expect(doubtful?.status).toEqual({ tone: 'warning', label: 'Pending · doubtful' });
    // A driver's own entry (US-2.4a) has nothing to doubt — just nothing verified.
    expect(manual?.status).toEqual({ tone: 'warning', label: 'Pending' });
  });

  it('shows the model’s score on an AI value and nothing on a manual one', () => {
    const [ai, typed] = fieldRows(
      [field(), field({ source: 'manual', confidence: null })],
      context,
    );

    // `ck_document_fields_manual_confidence`: an edited value has no score, and
    // inventing one would read as the model agreeing with the officer.
    expect(ai?.source.label).toBe('AI 0.98');
    expect(typed?.source.label).toBe('Manual');
  });

  it('names each control after its own field, for the six buttons that all say Confirm', () => {
    const [row] = fieldRows([field({ key: 'nic_no', verifyStatus: 'pending' })], context);

    expect(row?.confirmNamed).toBe('Confirm NIC no');
    expect(row?.editNamed).toBe('Edit NIC no');
  });

  it('renders an identifier this build does not know as itself', () => {
    expect(fieldLabel('licence_class', t)).toBe('licence_class');
    expect(fieldLabel('nic_no', t)).toBe('NIC no');
  });

  it('draws a missing value as an em dash rather than an empty cell', () => {
    const [row] = fieldRows([field({ value: null })], context);
    expect(row?.value).toBe('—');
  });
});

describe('the decision rail', () => {
  const steps: VerificationStep[] = [
    { step: 'profile', status: 'PENDING_REVIEW' },
    { step: 'insurance', status: 'VERIFIED' },
    { step: 'photos', status: 'PENDING_INPUT' },
  ];

  it('speaks the vocabulary of the surface that produced the subject', () => {
    expect(stepRows(steps, t).map((row) => row.label)).toEqual([
      'Profile / licence',
      'Insurance',
      'Vehicle photos',
    ]);
  });

  it('separates “nobody has uploaded it” from “somebody has to look”', () => {
    const [review, verified, input] = stepRows(steps, t);

    expect(review?.status).toEqual({ tone: 'warning', label: 'Pending' });
    expect(verified?.status).toEqual({ tone: 'success', label: 'Verified' });
    // The applicant, not the queue, is what this step is waiting on.
    expect(input?.status).toEqual({ tone: 'error', label: 'Not uploaded' });
  });

  it('summarises how much is still open, and says when nothing is', () => {
    const pending = [field({ verifyStatus: 'pending' }), field({ key: 'nic_no', verifyStatus: 'pending' })];

    expect(pendingSummary(pending, false, t).label).toBe('Pending review · 2 flagged fields');
    expect(pendingSummary([field()], true, t)).toEqual({
      tone: 'success',
      label: 'Every field confirmed',
    });
  });
});

describe('the document grid and its viewer', () => {
  const documents: DocumentRef[] = [
    { docId: 'a1', kind: 'driving_license', capturedVia: 'drag_crop' },
    { docId: 'a2', kind: 'insurance' },
    { docId: 'a3', kind: 'lankaqr_code' },
  ];

  const tiles = documentTiles(
    documents,
    { viewer: (id) => `/verification/S/doc/${id}`, media: (id) => `/verification/media/${id}` },
    context,
  );

  it('labels every kind it knows and pages them 1 / n', () => {
    expect(tiles.map((tile) => tile.label)).toEqual([
      'Driving licence',
      'Insurance',
      'LankaQR code',
    ]);
    expect(tiles.map((tile) => tile.position)).toEqual(['1 / 3', '2 / 3', '3 / 3']);
  });

  it('carries AL-43 provenance only where the upload recorded one', () => {
    // fleet-svc leaves it null on a payout document deliberately; a value invented
    // here would put a fraud signal on every bank statement on the platform.
    expect(tiles[0]?.capturedVia).toContain('in-app scanner');
    expect(tiles[1]?.capturedVia).toBeUndefined();
  });

  it('pages within the entry, and ends rather than wrapping', () => {
    const middle = viewerPosition(tiles, 'a2');
    expect(middle?.previous?.docId).toBe('a1');
    expect(middle?.next?.docId).toBe('a3');

    const last = viewerPosition(tiles, 'a3');
    expect(last?.next).toBeUndefined();
  });

  it('refuses a document that is not this subject’s', () => {
    // Otherwise the neighbours would be computed from -1 and the viewer would open
    // on somebody else's first document.
    expect(viewerPosition(tiles, 'not-here')).toBeNull();
  });
});
