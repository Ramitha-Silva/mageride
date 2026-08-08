import { describe, expect, it } from 'vitest';

import { safeNextPath } from '@/server/next-path';

/**
 * `?next=` is a redirect instruction that arrives from whoever wrote the link, on
 * the one screen a signed-out person can reach. An open redirect here is a
 * phishing page with `admin.mageride.lk` in the address bar for the whole of the
 * sign-in.
 */
describe('safeNextPath', () => {
  it('accepts a path on this origin', () => {
    expect(safeNextPath('/finance/refunds')).toBe('/finance/refunds');
    expect(safeNextPath('/passengers?q=07712')).toBe('/passengers?q=07712');
  });

  it.each([
    ['https://evil.example/login', 'an absolute URL'],
    ['//evil.example/login', 'a scheme-relative URL a naive startsWith("/") admits'],
    ['/\\evil.example', 'a backslash some browsers normalise to a slash'],
    ['javascript:alert(1)', 'a javascript: URL'],
    ['evil.example', 'a bare host'],
    ['', 'nothing'],
  ])('refuses %s — %s', (value) => {
    expect(safeNextPath(value)).toBeNull();
  });

  it('refuses the root, which is a redirect of its own', () => {
    expect(safeNextPath('/')).toBeNull();
  });

  it('refuses null and undefined', () => {
    expect(safeNextPath(null)).toBeNull();
    expect(safeNextPath(undefined)).toBeNull();
  });
});
