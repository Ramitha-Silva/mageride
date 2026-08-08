import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

/**
 * "Components that use hooks, context or Radix carry `'use client'`; the rest
 * stay usable from a React Server Component." — this package's CLAUDE.md,
 * asserted against the tree rather than trusted.
 *
 * **It is a rule about the barrel, not about one file.** `index.ts` re-exports
 * every component, so a React Server Component that imports `Table` pulls every
 * other module into the server graph with it. One hook without its directive
 * therefore does not make one component client-only — it makes the *package*
 * client-only, and every screen that wanted a table has to become a client
 * component to draw one. `Field` shipped that way and C105 was the first server
 * component to import from here and find out; a build error four components later
 * is exactly the kind of thing that should be a failing test here instead.
 */

const SOURCE = resolve(dirname(fileURLToPath(import.meta.url)), '../src/components');

/** `useState(`, `useId(`, `createContext(` — and not `useFieldContext`, which is local. */
const CLIENT_ONLY = /\b(?:use[A-Z]\w*\s*[(<]|createContext\s*[(<])|from '(?:radix-ui|react-dom)'/;

const DIRECTIVE = /^'use client';/;

function components(): { name: string; source: string }[] {
  return readdirSync(SOURCE)
    .filter((name) => name.endsWith('.tsx'))
    .map((name) => ({ name, source: readFileSync(join(SOURCE, name), 'utf8') }));
}

describe('the client boundary', () => {
  it('marks every component that reaches for a hook, a context or Radix', () => {
    const unmarked = components()
      .filter(({ source }) => CLIENT_ONLY.test(source) && !DIRECTIVE.test(source))
      .map(({ name }) => name);

    expect(unmarked).toEqual([]);
  });

  it('leaves the rest importable from a server component', () => {
    // The other half of the same rule: a directive on a component that needs none
    // would move it into a browser bundle for nothing.
    const overmarked = components()
      .filter(({ source }) => DIRECTIVE.test(source) && !CLIENT_ONLY.test(source))
      .map(({ name }) => name);

    expect(overmarked).toEqual([]);
  });

  it('keeps at least one primitive on each side, so neither assertion is vacuous', () => {
    const marked = components().filter(({ source }) => DIRECTIVE.test(source));

    expect(marked.length).toBeGreaterThan(0);
    expect(marked.length).toBeLessThan(components().length);
  });
});
