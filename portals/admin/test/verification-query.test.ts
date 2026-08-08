import { describe, expect, it } from 'vitest';

import {
  isSubjectId,
  QUEUE_PAGE_SIZE,
  queueHref,
  queueSearch,
  queueSelection,
  subjectFieldPath,
} from '@/api/verification';
import { approveLabelKey, backHref, mediaHref, queryString } from '@/components/verification/links';
import { resolveRoute } from '@/server/routes';

/**
 * SCR-AP-003's URL contract.
 *
 * The filter is the URL and nothing else, so what the URL means is a property
 * worth asserting: which tab, which search, which status — and that a value the
 * screen would not have produced is dropped rather than forwarded to a route that
 * answers `400` for it.
 *
 * The route table is here too, because the detail screen's segment sits directly
 * under the queue's path and `/verification/expiring` is a **different screen**
 * with its own nav item. That is the one collision this component introduces, and
 * `resolveRoute` is what decides it.
 */

describe('the queue selection', () => {
  it('defaults to the first tab, unfiltered', () => {
    expect(queueSelection({})).toEqual({ queue: 'driving-license' });
  });

  it('reads the tab, the search and the status the officer chose', () => {
    expect(queueSelection({ queue: 'fleet-org', search: ' Lanka ', status: 'REJECTED' })).toEqual({
      queue: 'fleet-org',
      search: 'Lanka',
      status: 'REJECTED',
    });
  });

  it('drops a value admin-bff would refuse rather than forwarding it', () => {
    // `status` is a closed enum and a pasted or hand-edited URL is whatever
    // somebody typed. Sending it on would greet the officer with a validation
    // error about a control they never touched.
    expect(queueSelection({ queue: 'payouts', status: 'MAYBE' })).toEqual({
      queue: 'driving-license',
    });
  });

  it('sends the same query to all three queues, and never the tab', () => {
    const selection = queueSelection({ queue: 'fleet-org', search: 'ABC-1234' });

    // The tab chooses which route is called; a queue that also received it would
    // be getting a parameter no contract declares.
    expect(queueSearch(selection)).toEqual({ search: 'ABC-1234', limit: QUEUE_PAGE_SIZE });
  });

  it('asks for the contract’s maximum page, because the badges are counts', () => {
    expect(QUEUE_PAGE_SIZE).toBe(100);
  });

  it('carries the whole filter onto every link the screen draws', () => {
    const selection = queueSelection({ queue: 'driving-license', search: 'K.', status: 'PENDING' });

    expect(queueHref('/verification', selection, { queue: 'fleet-org' })).toBe(
      '/verification?queue=fleet-org&search=K.&status=PENDING',
    );
    expect(backHref(queryString({ queue: 'fleet-org', search: 'K.' }))).toBe(
      '/verification?queue=fleet-org&search=K.',
    );
  });

  it('names the audited relay, never the bucket', () => {
    expect(mediaHref('doc-1', 'thumb')).toBe('/verification/media/doc-1?variant=thumb');
  });

  it('names the Approve button after the subject, as SCR-AP-003c does', () => {
    expect(approveLabelKey('driver')).toBe('admin.verification.decision.approveDriver');
    expect(approveLabelKey('vehicle')).toBe('admin.verification.decision.approveVehicle');
    expect(approveLabelKey('org')).toBe('admin.verification.decision.approveOrg');
  });
});

describe('the ids this screen will put into an API path', () => {
  it('accepts the shape admin-bff routes the family on', () => {
    expect(isSubjectId('0199a1f0-0000-7000-8000-000000000001')).toBe(true);
  });

  it('rejects a traversal, a wildcard and a screen name alike', () => {
    for (const value of ['../../admin/users', '*', 'expiring', '', undefined]) {
      expect(isSubjectId(value), String(value)).toBe(false);
    }
  });

  it('escapes a field key rather than trusting it to be path-safe', () => {
    expect(subjectFieldPath('0199a1f0-0000-7000-8000-000000000001', 'a/b')).toBe(
      '/v1/admin/verification/0199a1f0-0000-7000-8000-000000000001/fields/a%2Fb',
    );
  });
});

describe('the route the detail segment sits under', () => {
  it('is the verification screen for a subject', () => {
    expect(resolveRoute('/verification/0199a1f0-0000-7000-8000-000000000001')?.key).toBe(
      'verification',
    );
    expect(resolveRoute('/verification/org/0199a1f0-0000-7000-8000-000000000001')?.key).toBe(
      'verification',
    );
    expect(resolveRoute('/verification/media/0199a1f0-0000-7000-8000-0000000000aa')?.key).toBe(
      'verification',
    );
  });

  it('is still the document-expiry screen for its own nested path', () => {
    // A single dynamic segment out-ranks the shell's catch-all, so `[subjectId]`
    // is the *file* Next renders for this URL — but it belongs to another nav
    // item, gated on its own entry, and the page hands it back to the shell's
    // placeholder rather than reading "expiring" as a subject id.
    expect(resolveRoute('/verification/expiring')?.key).toBe('document-expiry');
  });
});
