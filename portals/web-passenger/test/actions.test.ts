import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The three writes, at the layer where the request is actually composed.
 *
 * `apiFetch` is mocked and every assertion is about **what was sent**, because on
 * this surface that is where the guarantees live: no coordinate on a decline (P-02),
 * no `Idempotency-Key` the page invented (public-bff's derived key is better than
 * anything this side could compose), and a dead token turned into a screen rather
 * than into an error.
 */

const apiFetch = vi.fn();

vi.mock('@/api/http', () => ({ apiFetch: (request: unknown) => apiFetch(request) }));

const TOKEN = 'Zx8CvBnMaSdFgHjKlQwErTyUiOp01234';

beforeEach(() => {
  vi.clearAllMocks();
  apiFetch.mockResolvedValue({ status: 200, data: {} });
});

describe('SCR-WT-003 · share', () => {
  it('posts the coordinate to the pickup-confirm route', async () => {
    const { sharePickupLocation } = await import('@/server/track-actions');

    expect(await sharePickupLocation(TOKEN, { lat: 6.9271, lng: 79.8612 })).toEqual({ ok: true });

    const sent = apiFetch.mock.calls[0]![0] as Record<string, unknown>;
    expect(sent['path']).toBe(`/public/track/${TOKEN}/pickup/confirm`);
    expect(sent['method']).toBe('POST');
    expect(sent['body']).toEqual({ lat: 6.9271, lng: 79.8612 });
  });

  it('carries the accuracy only when the browser measured one', async () => {
    const { sharePickupLocation } = await import('@/server/track-actions');

    await sharePickupLocation(TOKEN, { lat: 6.9271, lng: 79.8612, accuracy: 12 });
    expect((apiFetch.mock.calls[0]![0] as { body: unknown }).body).toEqual({
      lat: 6.9271,
      lng: 79.8612,
      accuracy: 12,
    });
  });

  it('refuses a coordinate that is not a number, before it costs a round trip', async () => {
    // `JSON.stringify` turns a `NaN` into `null`, which public-bff would answer
    // `validation-failed` for — the same sentence, one round trip later.
    const { sharePickupLocation } = await import('@/server/track-actions');

    expect(await sharePickupLocation(TOKEN, { lat: Number.NaN, lng: 79.8612 })).toEqual({
      ok: false,
      messageKey: 'web.error.badLocation',
    });
    expect(apiFetch).not.toHaveBeenCalled();
  });
});

describe('SCR-WT-003 · decline (P-02)', () => {
  it('posts no body at all', async () => {
    const { declinePickupLocation } = await import('@/server/track-actions');

    expect(await declinePickupLocation(TOKEN)).toEqual({ ok: true });

    const sent = apiFetch.mock.calls[0]![0] as Record<string, unknown>;
    expect(sent['path']).toBe(`/public/track/${TOKEN}/pickup/decline`);
    expect(sent['method']).toBe('POST');
    expect(Object.hasOwn(sent, 'body')).toBe(false);
  });

  it('carries no coordinate anywhere in the request, on any key', async () => {
    const { declinePickupLocation } = await import('@/server/track-actions');
    await declinePickupLocation(TOKEN);

    const serialised = JSON.stringify(apiFetch.mock.calls[0]![0]);
    for (const word of ['lat', 'lng', 'accuracy']) {
      expect(serialised, `the decline request mentions "${word}"`).not.toContain(word);
    }
  });
});

describe('SCR-WT-004 · SOS', () => {
  it('posts the coordinate and returns safety-svc’s own outcome', async () => {
    apiFetch.mockResolvedValue({
      status: 202,
      data: { sosId: '01J0', dispatchedAt: '2026-08-09T04:15:00Z', smsStatus: 'Dispatched' },
    });

    const { raiseWebSos } = await import('@/server/track-actions');
    const outcome = await raiseWebSos(TOKEN, { lat: 6.9271, lng: 79.8612 });

    expect((apiFetch.mock.calls[0]![0] as { path: string }).path).toBe(
      `/public/track/${TOKEN}/sos`,
    );
    // The status, not a boolean: `NoContact` means the alert is on a console in an
    // office and nowhere else, and on a panic button that is the whole difference.
    expect(outcome).toEqual({ ok: true, smsStatus: 'Dispatched' });
  });

  it('passes NoContact through rather than reporting success', async () => {
    apiFetch.mockResolvedValue({ status: 202, data: { sosId: '01J0', smsStatus: 'NoContact' } });

    const { raiseWebSos } = await import('@/server/track-actions');
    expect(await raiseWebSos(TOKEN, { lat: 6.9271, lng: 79.8612 })).toEqual({
      ok: true,
      smsStatus: 'NoContact',
    });
  });
});

describe('every write', () => {
  it('sends no Idempotency-Key, so public-bff derives the business one', async () => {
    // `pickup:{verb}:{token}` is stable for ever — a location request can be
    // answered exactly once and a retried tap should replay rather than read a
    // refusal — while `sos:{window}:{token}` is windowed, so a second genuine
    // emergency twenty minutes later is not a replay of the first. A fresh UUID per
    // attempt would replace both with a key that dedupes nothing.
    const { declinePickupLocation, raiseWebSos, sharePickupLocation } = await import(
      '@/server/track-actions'
    );

    await sharePickupLocation(TOKEN, { lat: 1, lng: 1 });
    await declinePickupLocation(TOKEN);
    await raiseWebSos(TOKEN, { lat: 1, lng: 1 });

    for (const [sent] of apiFetch.mock.calls) {
      expect(Object.hasOwn(sent as object, 'idempotencyKey')).toBe(false);
    }
  });

  it('turns a dead token into a screen rather than an error', async () => {
    const { ProblemError, localProblem } = await import('@/api/problem');
    apiFetch.mockRejectedValue(
      new ProblemError(localProblem('token-expired-or-revoked', 410, '/public/track')),
    );

    const { declinePickupLocation } = await import('@/server/track-actions');
    expect(await declinePickupLocation(TOKEN)).toEqual({ ok: false, dead: true });
  });

  it('returns a resource key for a failure, never the English title', async () => {
    // `_shared.yaml`: `title` is "Short English summary for developers. Never
    // localised." Returning it would put English in front of every reader in the
    // country, one failure at a time.
    const { ProblemError } = await import('@/api/problem');
    apiFetch.mockRejectedValue(
      new ProblemError({
        type: 'https://mageride.lk/errors/rate-limited',
        title: 'Too many requests',
        status: 429,
        traceId: '00-abc-def-01',
      }),
    );

    const { declinePickupLocation } = await import('@/server/track-actions');
    const outcome = await declinePickupLocation(TOKEN);

    expect(outcome).toEqual({
      ok: false,
      messageKey: 'web.error.rateLimited',
      traceId: '00-abc-def-01',
    });
    expect(JSON.stringify(outcome)).not.toContain('Too many requests');
  });

  it('says one sentence for a failure that never reached public-bff', async () => {
    apiFetch.mockRejectedValue(new TypeError('boom'));

    const { declinePickupLocation } = await import('@/server/track-actions');
    expect(await declinePickupLocation(TOKEN)).toEqual({
      ok: false,
      messageKey: 'web.error.unexpected',
    });
  });
});
