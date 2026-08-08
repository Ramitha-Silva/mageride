import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { apiDownload, apiFetch } from '@/api/http';
import { ProblemError } from '@/api/problem';

/**
 * The one module that talks to MageRide.
 *
 * Everything the portal knows about the platform arrives through this function,
 * so what it does with a redirect, an error body and an unset gateway address is
 * what the whole console does with them.
 */

const BASE = 'http://gateway.internal:8080';

let fetchMock: ReturnType<typeof vi.fn>;

function ok(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

beforeEach(() => {
  process.env.MAGERIDE_API_BASE_URL = BASE;
  fetchMock = vi.fn().mockResolvedValue(ok({ ok: true }));
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
  delete process.env.MAGERIDE_API_BASE_URL;
});

describe('the request', () => {
  it('is built from the gateway origin the deployment was given', async () => {
    await apiFetch({ path: '/v1/admin/session' });
    expect(fetchMock.mock.calls[0]?.[0]).toBe(`${BASE}/v1/admin/session`);
  });

  it('tolerates a trailing slash on the configured origin', async () => {
    process.env.MAGERIDE_API_BASE_URL = `${BASE}/`;
    await apiFetch({ path: '/v1/admin/session' });
    expect(fetchMock.mock.calls[0]?.[0]).toBe(`${BASE}/v1/admin/session`);
  });

  it('drops undefined search parameters instead of sending the word "undefined"', async () => {
    await apiFetch({ path: '/v1/admin/drivers', searchParams: { q: '077', status: undefined } });
    expect(fetchMock.mock.calls[0]?.[0]).toBe(`${BASE}/v1/admin/drivers?q=077`);
  });

  it('carries the bearer, the idempotency key and the content type', async () => {
    await apiFetch({
      path: '/v1/admin/trains',
      method: 'POST',
      accessToken: 'token',
      idempotencyKey: 'key-1',
      body: { registrationNo: 'TRN-1' },
    });

    const init = fetchMock.mock.calls[0]?.[1] as RequestInit;
    const headers = init.headers as Headers;

    expect(headers.get('authorization')).toBe('Bearer token');
    expect(headers.get('idempotency-key')).toBe('key-1');
    expect(headers.get('content-type')).toBe('application/json');
    expect(headers.get('accept')).toContain('application/problem+json');
    expect(init.body).toBe('{"registrationNo":"TRN-1"}');
  });

  it('sends no Authorization header on the routes that have no session yet', async () => {
    await apiFetch({ path: '/v1/admin/auth/login', method: 'POST', body: {} });
    expect((fetchMock.mock.calls[0]?.[1] as RequestInit & { headers: Headers }).headers.get('authorization')).toBeNull();
  });

  it('never caches — every response is one caller’s RBAC-evaluated view', async () => {
    await apiFetch({ path: '/v1/admin/passengers' });
    expect((fetchMock.mock.calls[0]?.[1] as RequestInit).cache).toBe('no-store');
  });

  it('refuses a relative path rather than resolving it against something', async () => {
    await expect(apiFetch({ path: 'v1/admin/session' })).rejects.toBeInstanceOf(TypeError);
  });
});

describe('the response', () => {
  it('hands back the parsed body', async () => {
    fetchMock.mockResolvedValue(ok({ userId: '01JQ0' }));
    const { data, status } = await apiFetch<{ userId: string }>({ path: '/v1/admin/session' });

    expect(status).toBe(200);
    expect(data.userId).toBe('01JQ0');
  });

  it('does not follow a 302 — it hands back the Location', async () => {
    // `GET /v1/admin/documents/{id}` answers 302 with a signed object-storage
    // URL, and C063 writes its DOC_VIEW row on the way out of exactly that
    // response. Following it here would put somebody's licence through this
    // process; handing over the Location keeps the bytes on the far side.
    fetchMock.mockResolvedValue(
      new Response(null, { status: 302, headers: { location: 'https://objects.example/signed' } }),
    );

    const { status, data } = await apiFetch<{ location: string }>({
      path: '/v1/admin/documents/01JQ0',
      accessToken: 'token',
    });

    expect(status).toBe(302);
    expect(data.location).toBe('https://objects.example/signed');
    expect((fetchMock.mock.calls[0]?.[1] as RequestInit).redirect).toBe('manual');
  });

  it('turns a problem body into a ProblemError', async () => {
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({ type: 'https://mageride.lk/errors/forbidden', title: 'Forbidden', status: 403 }),
        { status: 403, headers: { 'content-type': 'application/problem+json' } },
      ),
    );

    await expect(apiFetch({ path: '/v1/admin/audit-log', accessToken: 't' })).rejects.toMatchObject({
      status: 403,
      code: 'forbidden',
    });
  });

  it('answers a 204 with no body rather than a parse error', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 204 }));
    const { data } = await apiFetch({ path: '/v1/auth/logout', method: 'POST', accessToken: 't' });
    expect(data).toBeUndefined();
  });
});

describe('when the platform is not there', () => {
  it('reports an unreachable gateway as a 503, not a stack trace', async () => {
    fetchMock.mockRejectedValue(new Error('ECONNREFUSED'));

    const error = await apiFetch({ path: '/v1/admin/session' }).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ProblemError);
    expect((error as ProblemError).status).toBe(503);
    expect((error as ProblemError).code).toBe('dependency-unavailable');
    // The cause is kept for the log, where it is useful, and off the screen,
    // where it is not.
    expect((error as ProblemError).problem.detail).toContain('ECONNREFUSED');
  });

  it('reports a missing gateway address as this process being unable to serve', async () => {
    delete process.env.MAGERIDE_API_BASE_URL;

    const error = await apiFetch({ path: '/v1/admin/session' }).catch((e: unknown) => e);

    // Not a 500: a deployment with no gateway address is a misconfiguration, and
    // saying so is what gets it fixed.
    expect((error as ProblemError).status).toBe(503);
    expect((error as ProblemError).problem.detail).toContain('MAGERIDE_API_BASE_URL');
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe('a download', () => {
  const CSV = 'metric,value\r\ncompletedTrips,48210\r\n';

  function file(headers: Record<string, string> = {}): Response {
    return new Response(CSV, {
      status: 200,
      headers: { 'content-type': 'text/csv; charset=utf-8', ...headers },
    });
  }

  it('asks for its own media type and carries the operator’s bearer', async () => {
    fetchMock.mockResolvedValue(file());

    await apiDownload({
      path: '/v1/admin/dashboard/stats.csv',
      accept: 'text/csv',
      accessToken: 'token',
      searchParams: { period: 'month' },
    });

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit & { headers: Headers }];

    expect(url).toBe(`${BASE}/v1/admin/dashboard/stats.csv?period=month`);
    expect(init.headers.get('accept')).toBe('text/csv, application/problem+json');
    expect(init.headers.get('authorization')).toBe('Bearer token');
    expect(init.cache).toBe('no-store');
  });

  it('hands back the bytes, the media type and the platform’s own filename', async () => {
    fetchMock.mockResolvedValue(
      file({ 'content-disposition': 'attachment; filename=mageride-stats-20260601-20260628.csv' }),
    );

    const download = await apiDownload({ path: '/v1/admin/dashboard/stats.csv', accept: 'text/csv' });

    expect(new TextDecoder().decode(download.body)).toBe(CSV);
    expect(download.contentType).toBe('text/csv; charset=utf-8');
    expect(download.filename).toBe('mageride-stats-20260601-20260628.csv');
  });

  it('drops a filename carrying a path separator rather than sanitising one', async () => {
    // The value goes straight back out in a header of this portal's own. The safe
    // reading of an unexpected one is that there is no filename.
    fetchMock.mockResolvedValue(file({ 'content-disposition': 'attachment; filename="../../etc/passwd"' }));

    const download = await apiDownload({ path: '/v1/admin/dashboard/stats.csv', accept: 'text/csv' });
    expect(download.filename).toBeUndefined();
  });

  it('refuses to follow a redirect on a file route', async () => {
    fetchMock.mockResolvedValue(
      new Response(null, { status: 302, headers: { location: 'https://elsewhere.example/f.csv' } }),
    );

    const error = await apiDownload({ path: '/v1/admin/dashboard/stats.csv', accept: 'text/csv' }).catch(
      (e: unknown) => e,
    );

    // The bytes would be fetched from an origin nothing here vetted.
    expect((error as ProblemError).status).toBe(502);
  });

  it('turns a refusal into the same ProblemError every read produces', async () => {
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({ type: 'https://mageride.lk/errors/forbidden', title: 'Forbidden', status: 403 }),
        { status: 403, headers: { 'content-type': 'application/problem+json' } },
      ),
    );

    await expect(
      apiDownload({ path: '/v1/admin/dashboard/stats.csv', accept: 'text/csv' }),
    ).rejects.toMatchObject({ status: 403, code: 'forbidden' });
  });
});
