import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

/**
 * The four fences C104 is built under, asserted against the tree rather than
 * trusted.
 *
 * Each one is a property that is easy to hold today and easy to break in a later
 * component's first pull request — which is exactly the kind of rule that has to
 * be executable. They are the portal's counterpart to `AdminBffApplication`'s
 * start-up guard: the API refuses to start if a mutating route escapes the audit
 * interceptor, and the portal refuses to build if a screen goes round the data
 * layer.
 */

const APP_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const SOURCE_ROOTS = ['app', 'src'];
const EXTRA_FILES = ['proxy.ts'];

interface SourceFile {
  readonly path: string;
  readonly source: string;
}

function sources(): SourceFile[] {
  const files: SourceFile[] = [];

  const walk = (dir: string) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = join(dir, entry.name);
      if (entry.isDirectory()) {
        walk(full);
      } else if (/\.tsx?$/.test(entry.name)) {
        files.push({
          path: relative(APP_ROOT, full).replaceAll('\\', '/'),
          source: readFileSync(full, 'utf8'),
        });
      }
    }
  };

  for (const root of SOURCE_ROOTS) walk(join(APP_ROOT, root));
  for (const file of EXTRA_FILES) {
    files.push({ path: file, source: readFileSync(join(APP_ROOT, file), 'utf8') });
  }

  return files;
}

const FILES = sources();

/** Strips comments, so a rule about code is not tripped by a paragraph about it. */
function code(source: string): string {
  return source.replaceAll(/\/\*[\s\S]*?\*\//g, '').replaceAll(/(^|[^:])\/\/.*$/gm, '$1');
}

describe('the data layer is the only way out', () => {
  it('calls fetch in exactly one module', () => {
    // Everything that leaves this process for MageRide goes through `apiFetch`,
    // which is where the bearer, the problem+json handling and `cache: no-store`
    // live. A screen that reached for `fetch` would have none of the three.
    const callers = FILES.filter(({ source }) => /(?<![\w.])fetch\(/.test(code(source))).map(
      ({ path }) => path,
    );

    expect(callers).toEqual(['src/api/http.ts']);
  });

  it('lets only the session, the client and the proxy call the transport directly', () => {
    // The session module owns the three iam-svc auth routes, which have no
    // bearer to attach and no audit row to declare; the proxy owns the rotation
    // and the AL-06 evaluation. Every *screen* goes through `read`/`mutate`/
    // `download`.
    //
    // Both transport functions are named, not just `apiFetch`: `apiDownload`
    // (C105's CSV export) leaves this process the same way and must stay behind
    // the same door. A third one has to be added here on purpose.
    const callers = FILES.filter(
      ({ path, source }) =>
        path !== 'src/api/http.ts' && /api(?:Fetch|Download)[(<]/.test(code(source)),
    ).map(({ path }) => path);

    expect(callers.sort()).toEqual(['proxy.ts', 'src/api/client.ts', 'src/server/session.ts']);
  });
});

describe('AL-02 — nothing here is driver-facing or passenger-facing', () => {
  it('calls only the admin console and the two auth surfaces its session needs', () => {
    // The API-side form of the same fence admin-bff holds with a start-up guard.
    // A screen that started calling `/v1/rides/**` or `/v1/drivers/**` directly
    // would be this console growing an end-user surface.
    const ALLOWED = new Set([
      // The shell's four.
      '/v1/admin/session',
      '/v1/admin/auth/login',
      '/v1/auth/refresh',
      '/v1/auth/logout',
      // C105 · SCR-AP-002. A screen component adds its own endpoints here, in the
      // change that starts calling them — which is the point of enumerating the
      // set rather than admitting `/v1/admin/**` wholesale.
      '/v1/admin/dashboard/stats',
      '/v1/admin/dashboard/stats.csv',
      // C106 · SCR-AP-003/003a/003b/003c. The three queues, the subject-agnostic
      // family (detail, `…/fields/{key}`, `…/approve`, `…/reject`), the org's own
      // detail, and the audited document viewer.
      '/v1/admin/verification/queues/driving-license',
      '/v1/admin/verification/queues/vehicle-registration',
      '/v1/admin/verification/queues/fleet-org',
      '/v1/admin/verification',
      '/v1/admin/verification/org',
      '/v1/admin/documents',
      // C107 · SCR-AP-004/005. The vehicle-report queue and its verdict, the two
      // suspensions, and the support-ticket queue with its resolution. The two
      // directory prefixes are here because US-14.3's routes are
      // `…/vehicles/{id}/suspend` and `…/drivers/{id}/suspend` — the *only* two
      // writes anywhere under them (`No_directory_route_accepts_a_write`).
      '/v1/admin/reports/queue',
      '/v1/admin/reports',
      '/v1/admin/vehicles',
      '/v1/admin/drivers',
      '/v1/admin/support/tickets',
      // C108 · SCR-AP-006. Gateway settlement and its exception queue, the refund
      // queue and its decision, the wallet-ledger report with its two exports, and
      // the daily-fee reversal.
      '/v1/admin/finance/reconciliation',
      '/v1/admin/finance/reconciliation/exceptions',
      '/v1/admin/finance/refunds',
      '/v1/admin/finance/transactions',
      '/v1/admin/finance/transactions.csv',
      '/v1/admin/finance/transactions.pdf',
      '/v1/admin/drivers/wallet',
      // AL-58's payout run. **Answered by payout-svc, which `gateway-routes.json`
      // has no cluster for**, so these two resolve to admin-bff's Order 90
      // catch-all and 404 until C008 adds it. They are listed because the screen
      // calls them and the contract declares them; see the C108 handoff.
      '/v1/admin/payouts',
      '/v1/admin/payouts/batches',
      // C108 · SCR-AP-007. Two are admin-bff's; three are routed past it at
      // Order 20 to subscription-svc and dispatch-svc, which is why their writes
      // declare `auditedElsewhere` rather than a D-35 action (`api/audit.ts`).
      '/v1/admin/fares/tariffs',
      '/v1/admin/config/feature-flags',
      '/v1/admin/fees/rates',
      '/v1/admin/voucher-discount-tiers',
      '/v1/admin/drivers/level-config',
      // C108 · SCR-AP-008. iam-svc's RBAC surface, likewise routed past admin-bff.
      '/v1/admin/rbac/matrix',
      '/v1/admin/rbac/roles',
      '/v1/admin/rbac/users',
      // C108 · SCR-AP-009.
      '/v1/admin/audit-log',
    ]);

    const called = new Set<string>();
    for (const { source } of FILES) {
      for (const match of code(source).matchAll(/'(\/v1\/[^']*)'/g)) called.add(match[1]!);
    }

    expect([...called].filter((path) => !ALLOWED.has(path))).toEqual([]);
  });
});

describe('AL-37 — there is no second factor and no way to add one by accident', () => {
  it('has no MFA route, screen or branch', () => {
    for (const { path, source } of FILES) {
      const body = code(source);
      expect(body, `${path} names an MFA path`).not.toMatch(/['"`][^'"`]*\/mfa/i);
      expect(body, `${path} branches on mfaRequired`).not.toMatch(/if\s*\([^)]*mfaRequired/);
      expect(body, `${path} names a TOTP flow`).not.toMatch(/\btotp\b/i);
    }
  });

  it('types mfaRequired so it can only ever be false', () => {
    // The wire field exists — D3' §0 and D7' §4.2 still describe a challenge, and
    // answering the question explicitly is what stops a portal waiting for one —
    // but the literal type means no code path can be written for the true case.
    const types = readFileSync(join(APP_ROOT, 'src/api/types.ts'), 'utf8');
    expect(types).toMatch(/mfaRequired:\s*false/);
  });
});

describe('AL-52 — Tailwind is the sole styling system', () => {
  it('has no inline style attribute carrying colour, size or radius', () => {
    // `style={…}` is not CSS-in-JS by itself, and `@mageride/ui`'s Chip uses one
    // to pass a D2 vehicle hex through a custom property. In the shell there is
    // no such case, so the simplest true statement is that there are none.
    for (const { path, source } of FILES) {
      expect(code(source), `${path} sets an inline style`).not.toMatch(/\sstyle=\{/);
    }
  });

  it('has exactly one stylesheet', () => {
    const stylesheets = readdirSync(join(APP_ROOT, 'app')).filter((name) => name.endsWith('.css'));
    expect(stylesheets).toEqual(['globals.css']);
  });
});

describe('the browser is never handed the platform', () => {
  it('exposes no NEXT_PUBLIC_ variable', () => {
    // Every value the portal reads is a server value: the gateway address, the
    // OAuth client id, the cookie policy. A `NEXT_PUBLIC_` here would be one of
    // them inlined into a chunk anybody can read.
    for (const { path, source } of FILES) {
      expect(source, `${path} reads a NEXT_PUBLIC_ variable`).not.toContain('NEXT_PUBLIC_');
    }
  });

  it('reads process.env only through the config module', () => {
    const readers = FILES.filter(({ source }) => /process\.env\./.test(code(source))).map(
      ({ path }) => path,
    );

    expect(readers).toEqual(['src/config/env.ts']);
  });

  it('marks the modules that must never reach a client bundle', () => {
    for (const path of ['src/api/http.ts', 'src/api/client.ts', 'src/server/session.ts', 'src/i18n/server.ts']) {
      const source = FILES.find((file) => file.path === path)?.source ?? '';
      expect(source.startsWith("import 'server-only';"), `${path} is not marked server-only`).toBe(true);
    }
  });
});
