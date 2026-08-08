import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { ReadOptions } from '@/api/client';
import { ProblemError } from '@/api/problem';

/**
 * SCR-AP-015's document bytes, and the two properties that make the shared relay
 * shareable:
 *
 *  - it is the **same call** the verification screens make, so a thumbnail on a
 *    vehicle record writes the same `DOC_VIEW` row (`src/server/document-media.ts`);
 *  - it is a **different URL**, so `proxy.ts` gates it on the vehicle directory's
 *    own nav item. A Support CSR holds that item and not the verification queues,
 *    and routing this through `/verification/media/…` would answer 403 for every
 *    thumbnail on a screen they are permitted to open.
 *
 * `test/verification-media.test.ts` covers the relay's own behaviour — the manual
 * redirect, the open-redirect refusal, `no-store`. What is asserted here is that
 * this door reaches the same room.
 */

const read = vi.fn<(options: ReadOptions) => Promise<unknown>>();

vi.mock('@/api/client', () => ({ read: (options: ReadOptions) => read(options) }));

const { GET } = await import('../app/(portal)/vehicles/media/[docId]/route');

const DOC = '0199a1f0-0000-7000-8000-0000000000aa';
const SIGNED = 'https://objects.mageride.lk/docs/abc?X-Amz-Signature=deadbeef';

function get(docId: string, query = ''): Promise<Response> {
  return GET(new Request(`https://admin.mageride.lk/vehicles/media/${docId}${query}`), {
    params: Promise.resolve({ docId }),
  });
}

beforeEach(() => {
  vi.clearAllMocks();
  read.mockResolvedValue({ location: SIGNED });
});

describe('the vehicle directory’s relay', () => {
  it('asks AL-39’s audited viewer, which is what writes the DOC_VIEW row', () => {
    return get(DOC, '?variant=thumb').then(() => {
      expect(read).toHaveBeenCalledWith({
        path: `/v1/admin/documents/${DOC}`,
        searchParams: { variant: 'thumb' },
      });
    });
  });

  it('opens the full rendition unless the grid asked for a thumbnail', async () => {
    await get(DOC);
    expect(read.mock.calls[0]?.[0].searchParams).toEqual({ variant: 'full' });
  });

  it('hands the signed URL to the browser rather than fetching the object here', async () => {
    const response = await get(DOC);

    expect(response.status).toBe(302);
    expect(response.headers.get('location')).toBe(SIGNED);
    expect(response.headers.get('cache-control')).toBe('no-store');
    expect(response.headers.get('referrer-policy')).toBe('no-referrer');
  });

  it('will not put an id it did not recognise into an API path', async () => {
    const response = await get('..%2F..%2Fadmin');

    expect(response.status).toBe(404);
    expect(read).not.toHaveBeenCalled();
  });

  it('relays a refusal with its own status, not as a broken image', async () => {
    read.mockRejectedValue(
      new ProblemError({ type: 'https://mageride.lk/errors/forbidden', title: 'Forbidden', status: 403 }),
    );

    const response = await get(DOC);

    expect(response.status).toBe(403);
    expect(response.headers.get('content-type')).toBe('application/problem+json');
  });
});
