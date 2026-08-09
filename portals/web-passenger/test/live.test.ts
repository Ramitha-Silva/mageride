import { beforeEach, describe, expect, it, vi } from 'vitest';

import type * as TrackModule from '@/api/track';

import { GET } from '../app/api/live/[token]/route';

/**
 * The live feed's proxy route, and the two things C117's Definition of Done rests
 * on: **"the live feed reconnects after a dropped SSE stream without a full
 * reload"**, and "no ride data in the payload" for a dead token.
 *
 * The reconnect itself is the browser's — an `EventSource` reopens on its own and
 * sends the last `id:` it saw back as `Last-Event-ID`, and public-bff honours that
 * header identically to `?since`. What can break it is exactly one thing: this
 * proxy dropping the header. So that is what is asserted here, along with the poll
 * fallback the same route serves for a connection SSE does not survive.
 */

const openLiveStream = vi.fn();
const pollLive = vi.fn();

vi.mock('@/api/track', async () => {
  const actual = await vi.importActual<typeof TrackModule>('@/api/track');
  return {
    ...actual,
    openLiveStream: (...args: unknown[]) => openLiveStream(...args),
    pollLive: (...args: unknown[]) => pollLive(...args),
  };
});

const TOKEN = 'JQnQ4KcVsE9mR7tYuI0pLzXwBvNa1234';

function request(url: string, headers: Record<string, string> = {}): Request {
  return new Request(url, { headers });
}

/** Next's `NextRequest`, as much of it as the handler touches. */
function nextRequest(url: string, headers: Record<string, string> = {}) {
  const raw = request(url, headers);
  return Object.assign(raw, { nextUrl: new URL(url) }) as never;
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('the SSE arm', () => {
  it('forwards Last-Event-ID, which is the whole of the resume', async () => {
    openLiveStream.mockResolvedValue(new Response('id: 1.InTransit\n\n'));

    const response = await GET(
      nextRequest(`https://passenger.mageride.lk/api/live/${TOKEN}`, {
        'last-event-id': '1754712900000.InTransit',
      }),
      { params: Promise.resolve({ token: TOKEN }) },
    );

    expect(openLiveStream).toHaveBeenCalledTimes(1);
    expect(openLiveStream.mock.calls[0]![1]).toBe('1754712900000.InTransit');
    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toContain('text/event-stream');
  });

  it('opens a fresh stream when the browser has seen nothing yet', async () => {
    openLiveStream.mockResolvedValue(new Response(''));

    await GET(nextRequest(`https://passenger.mageride.lk/api/live/${TOKEN}`), {
      params: Promise.resolve({ token: TOKEN }),
    });

    expect(openLiveStream.mock.calls[0]![1]).toBeNull();
  });

  it('tells intermediaries not to buffer, and not to cache', async () => {
    // A buffered SSE body arrives all at once when it ends, which is the one thing a
    // live feed must not do. public-bff sets this on *its* response; this is a new
    // response, in front of a different proxy.
    openLiveStream.mockResolvedValue(new Response(''));

    const response = await GET(nextRequest(`https://passenger.mageride.lk/api/live/${TOKEN}`), {
      params: Promise.resolve({ token: TOKEN }),
    });

    expect(response.headers.get('x-accel-buffering')).toBe('no');
    expect(response.headers.get('cache-control')).toContain('no-store');
  });
});

describe('the poll arm', () => {
  it('answers the JSON batch when the client cannot hold a socket open', async () => {
    pollLive.mockResolvedValue({
      events: [{ type: 'status', status: 'InTransit', at: '2026-08-09T04:15:00Z' }],
      cursor: '1754712900000.InTransit',
    });

    const response = await GET(
      nextRequest(`https://passenger.mageride.lk/api/live/${TOKEN}?since=0.`),
      { params: Promise.resolve({ token: TOKEN }) },
    );

    expect(pollLive.mock.calls[0]![1]).toBe('0.');
    expect(await response.json()).toEqual({
      events: [{ type: 'status', status: 'InTransit', at: '2026-08-09T04:15:00Z' }],
      cursor: '1754712900000.InTransit',
    });
    expect(openLiveStream).not.toHaveBeenCalled();
  });

  it('passes an empty cursor through rather than refusing it', async () => {
    // public-bff answers an unparseable cursor with the current state, deliberately:
    // a cursor mangled by a proxy is not something the page can act on, and the
    // worst case of accepting it is one redundant frame.
    pollLive.mockResolvedValue({ events: [], cursor: null });

    await GET(nextRequest(`https://passenger.mageride.lk/api/live/${TOKEN}?since=`), {
      params: Promise.resolve({ token: TOKEN }),
    });

    expect(pollLive.mock.calls[0]![1]).toBe('');
  });
});

describe('a token this route will not serve', () => {
  it('refuses a malformed one without touching public-bff', async () => {
    const response = await GET(nextRequest('https://passenger.mageride.lk/api/live/x'), {
      params: Promise.resolve({ token: 'x' }),
    });

    expect(response.status).toBe(404);
    expect(openLiveStream).not.toHaveBeenCalled();
    expect(pollLive).not.toHaveBeenCalled();
  });

  it('answers a dead token with the status and an empty body', async () => {
    const { ProblemError, localProblem } = await import('@/api/problem');
    openLiveStream.mockRejectedValue(
      new ProblemError(localProblem('token-expired-or-revoked', 410, '/public/track')),
    );

    const response = await GET(nextRequest(`https://passenger.mageride.lk/api/live/${TOKEN}`), {
      params: Promise.resolve({ token: TOKEN }),
    });

    expect(response.status).toBe(410);
    // Not even the problem's English `detail`: this reader has no use for
    // diagnostics, and the page is about to reload into SCR-WT-006 anyway.
    expect(await response.text()).toBe('');
  });

  it('answers 503 rather than a Next error page when something else goes wrong', async () => {
    // A 500 with an HTML error page inside an `EventSource` is a body the browser
    // would retry for ever.
    openLiveStream.mockRejectedValue(new Error('socket hang up'));

    const response = await GET(nextRequest(`https://passenger.mageride.lk/api/live/${TOKEN}`), {
      params: Promise.resolve({ token: TOKEN }),
    });

    expect(response.status).toBe(503);
  });
});
