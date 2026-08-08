import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

/**
 * The five fences C111 is built under, asserted against the tree rather than
 * trusted.
 *
 * Each one is a property that is easy to hold today and easy to break in a later
 * component's first pull request — which is exactly the kind of rule that has to
 * be executable. They are the portal's counterpart to `FleetAccessFilter`'s
 * group metadata: the service gates a route the moment it is mapped, and the
 * portal refuses to build if a screen goes round the data layer or names another
 * organisation.
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
    // The session module owns the auth routes, which have no bearer to attach;
    // the proxy owns the rotation and the seat evaluation. Every *screen* goes
    // through `read`/`mutate`. A second transport function has to be added here
    // on purpose.
    const callers = FILES.filter(
      ({ path, source }) => path !== 'src/api/http.ts' && /apiFetch[(<]/.test(code(source)),
    ).map(({ path }) => path);

    expect(callers.sort()).toEqual(['proxy.ts', 'src/api/client.ts', 'src/server/session.ts']);
  });

  it('declares the capability every mutation needs', () => {
    // `mutate()` cannot be called without a `requires` declaration — the type
    // makes it mandatory — and this is the tree-level form of the same rule, so a
    // screen component cannot satisfy the compiler by widening the type.
    for (const { path, source } of FILES) {
      const body = code(source);
      const calls = [...body.matchAll(/\bmutate[(<]/g)];
      if (calls.length === 0) continue;
      expect(body, `${path} calls mutate without declaring what it requires`).toMatch(/requires:/);
    }
  });
});

describe('every read is org-scoped, and the org is never the screen’s to choose', () => {
  it('builds a /v1/fleets/{id} path in exactly three places', () => {
    // `src/api/client.ts` resolves every org-relative target against the
    // session's own `fleetId`, and that is the one a screen reaches.
    // `src/server/session.ts` and `proxy.ts` read the organisation itself, which
    // is the call that has to name the id before there is a session to scope to.
    // A fourth would be a screen naming an organisation.
    const builders = FILES.filter(({ source }) => /\/v1\/fleets\/\$\{/.test(code(source))).map(
      ({ path }) => path,
    );

    expect(builders.sort()).toEqual(['proxy.ts', 'src/api/client.ts', 'src/server/session.ts']);
  });

  it('never accepts a fleet id from a screen', () => {
    // The data layer takes `{ org: '/vehicles' }`. Nothing in the tree may pass a
    // fleet id into it, because a screen that could name one is a screen that
    // could name somebody else's.
    for (const { path, source } of FILES) {
      expect(code(source), `${path} passes a fleetId to the data layer`).not.toMatch(
        /\b(read|mutate)\(\{[^}]*fleetId/s,
      );
    }
  });
});

describe('AL-03 — a fleet operates Mode A and Mode B, and never Mode C', () => {
  it('names no Mode C concept anywhere in the tree', () => {
    for (const { path, source } of FILES) {
      const body = code(source);
      expect(body, `${path} names Mode C`).not.toMatch(/\bmode[\s_-]?c\b/i);
      // The on-demand plane is ride-svc's and its screens are the passenger and
      // driver apps'. A fleet console that called a dispatch route would be this
      // portal growing an on-demand surface.
      expect(body, `${path} calls the on-demand plane`).not.toMatch(/['"`]\/v1\/(rides|dispatch)/);
    }
  });
});

describe('AL-37 — there is no second factor and no way to add one by accident', () => {
  it('has no MFA route, screen or branch', () => {
    for (const { path, source } of FILES) {
      const body = code(source);
      expect(body, `${path} names an MFA path`).not.toMatch(/['"`][^'"`]*\/mfa/i);
      expect(body, `${path} branches on mfaRequired`).not.toMatch(/mfaRequired/);
      expect(body, `${path} names a TOTP flow`).not.toMatch(/\btotp\b/i);
    }
  });

  it('has no phone-OTP arm — AL-07 gives the portals password and the two providers', () => {
    for (const { path, source } of FILES) {
      expect(code(source), `${path} calls an OTP route`).not.toMatch(/['"`]\/v1\/auth\/otp/);
    }
  });
});

describe('the API surface is the one this portal is allowed', () => {
  it('calls only fleet-svc’s org tree and the iam-svc routes its session needs', () => {
    const ALLOWED = new Set([
      // The shell's session, in both halves.
      '/v1/me/permissions',
      '/v1/fleets',
      // AL-07's three sign-in arms, plus rotation and sign-out.
      '/v1/auth/password',
      '/v1/auth/google',
      '/v1/auth/apple',
      '/v1/auth/refresh',
      '/v1/auth/logout',
      // Two fragments the scan sees rather than two routes. `/v1/` is the guard
      // in `resolvePath` that refuses a path which is not absolute, and
      // `/v1/auth` is the prefix `signInWithProvider` completes with the provider
      // id — both arms of which are named above.
      '/v1/',
      '/v1/auth',
    ]);

    const called = new Set<string>();
    for (const { source } of FILES) {
      for (const match of code(source).matchAll(/'(\/v1\/[^'`]*)'/g)) called.add(match[1]!);
      // The two template-literal paths, reduced to their prefix.
      for (const match of code(source).matchAll(/`(\/v1\/[^`$]*)\$\{/g)) {
        called.add(match[1]!.replace(/\/$/, ''));
      }
    }

    expect([...called].filter((path) => !ALLOWED.has(path)).sort()).toEqual([]);
  });
});

describe('AL-52 — Tailwind is the sole styling system', () => {
  it('has no inline style attribute carrying colour, size or radius', () => {
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
    for (const path of [
      'src/api/http.ts',
      'src/api/client.ts',
      'src/server/session.ts',
      'src/i18n/server.ts',
    ]) {
      const source = FILES.find((file) => file.path === path)?.source ?? '';
      expect(source.startsWith("import 'server-only';"), `${path} is not marked server-only`).toBe(
        true,
      );
    }
  });

  it('keeps the screen manifest out of every client component', () => {
    // `src/server/routes.ts` carries each screen's URD row, its minimum sub-role
    // and its approval gate. None of that is any use to a browser, and shipping
    // it would put the gate's own vocabulary in a bundle. `SideNav` resolves the
    // current entry from the paths it was handed instead.
    for (const { path, source } of FILES) {
      if (!source.includes("'use client'")) continue;
      expect(code(source), `${path} imports the screen manifest`).not.toMatch(
        /from '@\/server\/routes'/,
      );
      expect(code(source), `${path} imports the access rules`).not.toMatch(
        /from '@\/server\/access'/,
      );
    }
  });
});
