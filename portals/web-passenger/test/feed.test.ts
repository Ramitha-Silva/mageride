import { renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { useLiveTrack } from '@/live/useLiveTrack';

/**
 * The hook behind the live feed, and the half of C117's reconnect item that is this
 * application's rather than the browser's.
 *
 * An `EventSource` handles a *dropped* stream itself: it waits, reopens and sends
 * `Last-Event-ID`, and public-bff honours that. What it does not handle is a
 * connection on which SSE never works at all — an intermediary that buffers a
 * response body turns the feed into something that arrives once, when it ends, and
 * no header of public-bff's can reach a corporate proxy or a mobile operator's
 * transcoder. D6' I-29.1 asks for the `?since=cursor` fallback for exactly that.
 *
 * So the assertions are: a live socket updates the page; a socket the browser gives
 * up on twice becomes polling and stays there; and `resolved` closes the feed for
 * good rather than reconnecting into a finished trip.
 */

const TOKEN = 'JQnQ4KcVsE9mR7tYuI0pLzXwBvNa1234';

type Listener = (event: Event) => void;

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSED = 2;

  readyState = FakeEventSource.CONNECTING;
  closed = false;
  private readonly listeners = new Map<string, Listener[]>();

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, listener: Listener): void {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }

  close(): void {
    this.closed = true;
    this.readyState = FakeEventSource.CLOSED;
  }

  emit(type: string, data?: unknown): void {
    const event = data === undefined ? new Event(type) : new MessageEvent(type, { data: JSON.stringify(data) });
    for (const listener of this.listeners.get(type) ?? []) listener(event);
  }

  /** What a browser that has given up looks like. */
  fail(): void {
    this.readyState = FakeEventSource.CLOSED;
    this.emit('error');
  }
}

const fetchMock = vi.fn();

beforeEach(() => {
  FakeEventSource.instances = [];
  fetchMock.mockReset();
  vi.stubGlobal('EventSource', FakeEventSource);
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the socket', () => {
  it('opens against this origin’s own proxy, never the gateway', () => {
    renderHook(() => useLiveTrack(TOKEN));

    expect(FakeEventSource.instances).toHaveLength(1);
    expect(FakeEventSource.instances[0]!.url).toBe(`/api/live/${TOKEN}`);
  });

  it('reports the position and the status the feed carries', async () => {
    const { result } = renderHook(() => useLiveTrack(TOKEN));
    const source = FakeEventSource.instances[0]!;

    source.emit('open');
    source.emit('position', {
      type: 'position',
      position: { lat: 6.93, lng: 79.85, ts: '2026-08-09T04:15:00Z' },
      at: '2026-08-09T04:15:00Z',
      cursor: '1754712900000.InTransit',
    });
    source.emit('status', { type: 'status', status: 'InTransit', at: '2026-08-09T04:15:00Z' });

    await waitFor(() => expect(result.current.status).toBe('InTransit'));
    expect(result.current.position).toEqual({ lat: 6.93, lng: 79.85, ts: '2026-08-09T04:15:00Z' });
    expect(result.current.connection).toBe('live');
  });

  it('closes for good on `resolved`, and does not reopen', async () => {
    // The journey is over. Reconnecting would poll a token safety-svc is about to
    // revoke, and the page is already reloading into SCR-WT-005 or SCR-WT-006.
    const { result } = renderHook(() => useLiveTrack(TOKEN));
    const source = FakeEventSource.instances[0]!;

    source.emit('open');
    source.emit('resolved', { type: 'resolved', status: 'Paid', at: '2026-08-09T04:20:00Z' });

    await waitFor(() => expect(result.current.closed).toBe(true));
    expect(result.current.connection).toBe('closed');
    expect(source.closed).toBe(true);
    expect(FakeEventSource.instances).toHaveLength(1);
  });

  it('says it is reconnecting while the browser is reconnecting', async () => {
    const { result } = renderHook(() => useLiveTrack(TOKEN));
    const source = FakeEventSource.instances[0]!;

    source.emit('open');
    // `CONNECTING` after an error is the browser already reopening with
    // `Last-Event-ID` — the design, not a failure. The page says so and waits.
    source.readyState = FakeEventSource.CONNECTING;
    source.emit('error');

    await waitFor(() => expect(result.current.connection).toBe('reconnecting'));
    expect(FakeEventSource.instances).toHaveLength(1);
  });
});

describe('the poll fallback', () => {
  it('takes over after the browser gives up twice, and keeps the cursor', async () => {
    fetchMock.mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        events: [{ type: 'status', status: 'Delivered', at: '2026-08-09T04:20:00Z' }],
        cursor: '1754713200000.Delivered',
      }),
    });

    const { result } = renderHook(() => useLiveTrack(TOKEN));

    FakeEventSource.instances[0]!.emit('open');
    FakeEventSource.instances[0]!.emit('position', {
      type: 'position',
      position: { lat: 6.93, lng: 79.85, ts: '2026-08-09T04:15:00Z' },
      at: '2026-08-09T04:15:00Z',
      cursor: '1754712900000.InTransit',
    });

    FakeEventSource.instances[0]!.fail();
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(2));

    FakeEventSource.instances[1]!.fail();

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(fetchMock.mock.calls[0]![0]).toBe(
      `/api/live/${TOKEN}?since=1754712900000.InTransit`,
    );
    await waitFor(() => expect(result.current.status).toBe('Delivered'));
    expect(result.current.connection).toBe('polling');
  });

  it('stops polling when the token is gone', async () => {
    fetchMock.mockResolvedValue({ ok: false, status: 410, json: async () => ({}) });
    vi.stubGlobal('EventSource', undefined);

    const { result } = renderHook(() => useLiveTrack(TOKEN));

    await waitFor(() => expect(result.current.closed).toBe(true));
    expect(result.current.connection).toBe('closed');
  });

  it('polls straight away in a browser with no EventSource at all', async () => {
    vi.stubGlobal('EventSource', undefined);
    fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({ events: [], cursor: null }) });

    renderHook(() => useLiveTrack(TOKEN));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(FakeEventSource.instances).toHaveLength(0);
  });
});

describe('a feed nobody asked for', () => {
  it('opens nothing when it is disabled', () => {
    renderHook(() => useLiveTrack(TOKEN, false));
    expect(FakeEventSource.instances).toHaveLength(0);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
