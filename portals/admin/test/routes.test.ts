import { describe, expect, it } from 'vitest';

import { ADMIN_ROUTES, covers, normalisePath, PUBLIC_PATHS, resolveRoute, UNSCREENED_PATHS } from '@/server/routes';

import { adminMenuManifest } from './support/urd';

/**
 * The portal's route table against admin-bff's nav manifest.
 *
 * `ADMIN_ROUTES` is the one thing the shell keeps a local copy of — every
 * screen's key and path — and the copy has a job: it is what lets a nested screen
 * be checked on its own gate instead of inheriting its parent's. A copy with a
 * job still drifts, so this parses `AdminMenu.cs` and holds the two together.
 */

describe('ADMIN_ROUTES mirrors AdminMenu.All', () => {
  const manifest = adminMenuManifest().flatMap((group) => group.items);

  it('covers every nav item, with the same path', () => {
    const expected = manifest.map((item) => `${item.key} ${item.path}`).sort();
    const actual = ADMIN_ROUTES.map((route) => `${route.key} ${route.path}`).sort();

    expect(actual).toEqual(expected);
  });

  it('claims no route the manifest does not have', () => {
    const keys = new Set(manifest.map((item) => item.key));
    for (const route of ADMIN_ROUTES) expect(keys.has(route.key)).toBe(true);
  });

  it('gives every screen a distinct path', () => {
    const paths = ADMIN_ROUTES.map((route) => route.path);
    expect(new Set(paths).size).toBe(paths.length);
  });

  it('does not gate the sign-in flow or the two unscreened routes as screens', () => {
    for (const path of [...PUBLIC_PATHS, ...UNSCREENED_PATHS]) {
      expect(resolveRoute(path)).toBeNull();
    }
  });
});

describe('resolveRoute', () => {
  it('prefers the more specific screen', () => {
    // The failure this whole table exists to prevent: `/verification/expiring`
    // is the document-expiry screen, not a page inside the verification queue.
    expect(resolveRoute('/verification')?.key).toBe('verification');
    expect(resolveRoute('/verification/expiring')?.key).toBe('document-expiry');
    expect(resolveRoute('/verification/expiring/01JQ')?.key).toBe('document-expiry');
    expect(resolveRoute('/verification/01JQ')?.key).toBe('verification');
  });

  it('treats a trailing slash as the same route', () => {
    expect(resolveRoute('/dashboard/')?.key).toBe('dashboard');
    expect(normalisePath('/dashboard/')).toBe('/dashboard');
    expect(normalisePath('/')).toBe('/');
    expect(normalisePath('')).toBe('/');
  });

  it('does not match a sibling whose path merely starts the same way', () => {
    expect(covers('/config/fares', '/config/fares-old')).toBe(false);
    expect(resolveRoute('/config/faresomething')).toBeNull();
    expect(resolveRoute('/passengersx')).toBeNull();
  });

  it('claims nothing for a path no screen owns', () => {
    for (const path of ['/', '/finance', '/config', '/moderation', '/support', '/access', '/nope']) {
      expect(resolveRoute(path)).toBeNull();
    }
  });
});
