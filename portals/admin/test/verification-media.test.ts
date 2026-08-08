import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { ReadOptions } from '@/api/client';
import { ProblemError } from '@/api/problem';

/**
 * SCR-AP-003b's bytes, and the DoD item they carry: **opening a thumbnail records
 * a `DOC_VIEW` audit entry.**
 *
 * The row is admin-bff's to write, and it writes it on the way out of
 * `GET /v1/admin/documents/{docId}` — so what this route has to do is make sure
 * every image on the screen goes *through* that call. What is asserted here is
 * exactly that: the audited route is what is asked for, the signed URL it answers
 * with is passed on rather than followed, and nothing is cached, because a cached
 * rendition served to a later caller would be a read with no row behind it.
 */

const read = vi.fn<(options: ReadOptions) => Promise<unknown>>();

vi.mock('@/api/client', () => ({ read: (options: ReadOptions) => read(options) }));

const { GET } = await import('../app/(portal)/verification/media/[docId]/route');

const DOC = '0199a1f0-0000-7000-8000-0000000000aa';
const SIGNED = 'https://objects.mageride.lk/docs/abc?X-Amz-Signature=deadbeef';

function get(docId: string, query = ''): Promise<Response> {
  return GET(new Request(`https://admin.mageride.lk/verification/media/${docId}${query}`), {
    params: Promise.resolve({ docId }),
  });
}

beforeEach(() => {
  vi.clearAllMocks();
  read.mockResolvedValue({ location: SIGNED });
});

describe('the relay', () => {
  it('asks the audited viewer, which is what writes the DOC_VIEW row', async () => {
    await get(DOC, '?variant=thumb');

    expect(read).toHaveBeenCalledWith({
      path: `/v1/admin/documents/${DOC}`,
      searchParams: { variant: 'thumb' },
    });
  });

  it('opens the full rendition unless the grid asked for a thumbnail', async () => {
    await get(DOC);
    expect(read.mock.calls[0]?.[0].searchParams).toEqual({ variant: 'full' });

    await get(DOC, '?variant=nonsense');
    expect(read.mock.calls[1]?.[0].searchParams).toEqual({ variant: 'full' });
  });

  it('hands the signed URL to the browser rather than fetching the object here', async () => {
    const response = await get(DOC);

    // The bytes never enter this process: the row is already written, the URL is
    // short-lived, and streaming it would make that lifetime pointless.
    expect(response.status).toBe(302);
    expect(response.headers.get('location')).toBe(SIGNED);
  });

  it('is never cached and leaks no referrer to storage', async () => {
    const response = await get(DOC);

    expect(response.headers.get('cache-control')).toBe('no-store');
    expect(response.headers.get('referrer-policy')).toBe('no-referrer');
  });
});

describe('what it refuses', () => {
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

  it('refuses to redirect anywhere that is not an http(s) object URL', async () => {
    // A relay that forwarded whatever it was handed would be an open redirect
    // wearing an <img> tag.
    read.mockResolvedValue({ location: 'javascript:alert(1)' });

    expect((await get(DOC)).status).toBe(502);
  });

  it('fails visibly when the viewer answered a body instead of a redirect', async () => {
    read.mockResolvedValue({ docId: DOC });

    expect((await get(DOC)).status).toBe(502);
  });
});
