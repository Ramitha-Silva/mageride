import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { DownloadOptions } from '@/api/client';
import type { ApiDownload } from '@/api/http';
import { ProblemError } from '@/api/problem';

/**
 * SCR-AP-002's CSV export, as the route handler that serves it (AL-38).
 *
 * Two properties, and both are about the file being *the platform's* rather than
 * the portal's. It goes out under the caller's own session, through the same data
 * layer every read uses; and the bytes are relayed, so the figures in the file are
 * the figures on the screen because admin-bff computed both from one service call
 * rather than because two implementations agree.
 */

const download = vi.fn<(options: DownloadOptions) => Promise<ApiDownload>>();

vi.mock('@/api/client', () => ({ download: (options: DownloadOptions) => download(options) }));

const { GET } = await import('../app/(portal)/dashboard/export/route');

const CSV = '# MageRide admin dashboard statistics\r\nmetric,value,previousValue,deltaPct\r\n';

function csv(filename?: string): ApiDownload {
  return {
    status: 200,
    body: new TextEncoder().encode(CSV).buffer as ArrayBuffer,
    contentType: 'text/csv; charset=utf-8',
    ...(filename ? { filename } : {}),
  };
}

function get(query: string): Promise<Response> {
  return GET(new Request(`https://admin.mageride.lk/dashboard/export${query}`));
}

beforeEach(() => {
  vi.clearAllMocks();
  download.mockResolvedValue(csv('mageride-stats-20260601-20260628.csv'));
});

describe('the export', () => {
  it('asks admin-bff for the contract’s CSV route, with the screen’s own query', () => {
    return get('?period=custom&from=2026-06-01&to=2026-06-28').then(() => {
      expect(download).toHaveBeenCalledWith({
        path: '/v1/admin/dashboard/stats.csv',
        accept: 'text/csv',
        searchParams: { period: 'custom', from: '2026-06-01', to: '2026-06-28' },
      });
    });
  });

  it('relays the bytes unchanged rather than rendering a second CSV', async () => {
    const response = await get('?period=month');

    expect(await response.text()).toBe(CSV);
    expect(response.headers.get('content-type')).toBe('text/csv; charset=utf-8');
  });

  it('keeps the filename admin-bff chose, which names the range', async () => {
    // A folder of `stats.csv` files is a folder of files nobody can tell apart —
    // `DashboardEndpoints` says so, and it is the service that knows the range.
    const response = await get('?period=custom&from=2026-06-01&to=2026-06-28');

    expect(response.headers.get('content-disposition')).toBe(
      'attachment; filename="mageride-stats-20260601-20260628.csv"',
    );
  });

  it('names the file itself when the platform named none', async () => {
    download.mockResolvedValue(csv());

    const response = await get('?period=today');
    expect(response.headers.get('content-disposition')).toContain('mageride-dashboard-stats.csv');
  });

  it('is never cached: it is a per-caller view of other people’s platform', async () => {
    const response = await get('?period=today');
    expect(response.headers.get('cache-control')).toBe('no-store');
  });

  it('sends only the period on a fixed window, as the screen does', async () => {
    await get('?period=week&from=2026-01-01&to=2026-01-31');

    expect(download.mock.calls[0]?.[0].searchParams).toEqual({ period: 'week' });
  });
});

describe('when there is nothing to export', () => {
  it('refuses a half-chosen custom range instead of exporting today under its name', async () => {
    const response = await get('?period=custom&from=2026-06-01');

    expect(response.status).toBe(400);
    expect(response.headers.get('content-type')).toBe('application/problem+json');
    expect(download).not.toHaveBeenCalled();
  });

  it('answers a refusal with its own status, never a 200 carrying an error page', async () => {
    download.mockRejectedValue(
      new ProblemError({
        type: 'https://mageride.lk/errors/forbidden',
        title: 'Forbidden',
        status: 403,
      }),
    );

    const response = await get('?period=today');

    expect(response.status).toBe(403);
    // A 200 with an HTML body would be a download that silently produced a
    // broken CSV — the operator would open it in a spreadsheet and see markup.
    expect(response.headers.get('content-type')).toBe('application/problem+json');
    expect(((await response.json()) as { status: number }).status).toBe(403);
  });

  it('never serves a failure as a success, whatever status the problem claims', async () => {
    download.mockRejectedValue(
      new ProblemError({ type: 'https://mageride.lk/errors/internal-error', title: 'x', status: 200 }),
    );

    expect((await get('?period=today')).status).toBe(502);
  });
});
