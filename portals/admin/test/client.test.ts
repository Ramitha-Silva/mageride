import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { AuditIntent } from '@/api/audit';
import type { ApiRequest } from '@/api/http';
import { ProblemError } from '@/api/problem';

/**
 * The typed data layer — the two functions every Admin Portal screen reaches
 * admin-bff through.
 *
 * What is worth asserting is not that a GET fetches. It is the three things a
 * screen would otherwise have to remember and would eventually forget: the
 * caller's bearer, the `Idempotency-Key` on every mutation, and the D-35 row the
 * mutation is going to cause.
 */

const apiFetch = vi.fn<(request: ApiRequest) => Promise<{ status: number; data: unknown }>>();
const accessToken = vi.fn<() => Promise<string | null>>();

vi.mock('@/api/http', () => ({ apiFetch: (request: ApiRequest) => apiFetch(request) }));
vi.mock('@/server/session', () => ({ accessToken: () => accessToken() }));

const { read, mutate } = await import('@/api/client');

const SUSPEND: AuditIntent = {
  action: 'VEHICLE_SUSPENDED',
  entity: 'vehicle',
  entityId: '01JQ0000000000000000000000',
};

beforeEach(() => {
  vi.clearAllMocks();
  accessToken.mockResolvedValue('access-token');
  apiFetch.mockResolvedValue({ status: 200, data: { ok: true } });
});

describe('read', () => {
  it('presents the operator’s bearer', async () => {
    await read({ path: '/v1/admin/dashboard' });

    expect(apiFetch).toHaveBeenCalledWith(
      expect.objectContaining({ path: '/v1/admin/dashboard', accessToken: 'access-token' }),
    );
  });

  it('passes search parameters through rather than making callers build a query string', async () => {
    await read({ path: '/v1/admin/drivers', searchParams: { q: '077', limit: 25 } });

    expect(apiFetch.mock.calls[0]?.[0].searchParams).toEqual({ q: '077', limit: 25 });
  });

  it('refuses to make an anonymous request when the session has gone', async () => {
    accessToken.mockResolvedValue(null);

    await expect(read({ path: '/v1/admin/dashboard' })).rejects.toMatchObject({
      status: 401,
      code: 'unauthorized',
    });
    // An operator console has no anonymous reads; sending one would turn a
    // signed-out tab into a 403 that reads like a permissions bug.
    expect(apiFetch).not.toHaveBeenCalled();
  });

  it('lets a problem out unchanged, so the caller branches on the code', async () => {
    apiFetch.mockRejectedValue(
      new ProblemError({
        type: 'https://mageride.lk/errors/forbidden',
        title: 'Forbidden',
        status: 403,
      }),
    );

    await expect(read({ path: '/v1/admin/audit-log' })).rejects.toBeInstanceOf(ProblemError);
  });
});

describe('mutate', () => {
  it('always sends an Idempotency-Key, whether or not the caller thought about it', async () => {
    await mutate({ method: 'POST', path: '/v1/admin/vehicles/01JQ/suspend', audit: SUSPEND });

    const sent = apiFetch.mock.calls[0]?.[0];
    expect(sent?.method).toBe('POST');
    expect(sent?.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('uses the caller’s key when the action must not double-apply', async () => {
    await mutate({
      method: 'POST',
      path: '/v1/admin/vehicles/01JQ/suspend',
      audit: SUSPEND,
      idempotencyKey: 'suspend:01JQ:2026-08-08',
    });

    expect(apiFetch.mock.calls[0]?.[0].idempotencyKey).toBe('suspend:01JQ:2026-08-08');
  });

  it('echoes the declared audit row back, so a toast can name what was recorded', async () => {
    const outcome = await mutate({
      method: 'POST',
      path: '/v1/admin/vehicles/01JQ/suspend',
      audit: SUSPEND,
    });

    expect(outcome.audit).toEqual(SUSPEND);
    expect(outcome.status).toBe(200);
    expect(outcome.data).toEqual({ ok: true });
  });

  it('never posts an audit row of its own', async () => {
    await mutate({ method: 'POST', path: '/v1/admin/vehicles/01JQ/suspend', audit: SUSPEND });

    // The row is written by admin-bff's interceptor inside the same transaction
    // as the change. A second, portal-written entry would be an unbacked claim
    // in an immutable log — one that survives a transaction that rolled back.
    expect(apiFetch).toHaveBeenCalledOnce();
    expect(apiFetch.mock.calls[0]?.[0].path).toBe('/v1/admin/vehicles/01JQ/suspend');
  });

  it('refuses to mutate without a session', async () => {
    accessToken.mockResolvedValue(null);

    await expect(
      mutate({ method: 'POST', path: '/v1/admin/vehicles/01JQ/suspend', audit: SUSPEND }),
    ).rejects.toMatchObject({ status: 401 });
    expect(apiFetch).not.toHaveBeenCalled();
  });

  it('omits an absent body rather than sending null', async () => {
    await mutate({ method: 'DELETE', path: '/v1/admin/trains/01JQ', audit: { action: 'TRAIN_RETIRED', entity: 'vehicle' } });

    expect(apiFetch.mock.calls[0]?.[0]).not.toHaveProperty('body');
  });
});
