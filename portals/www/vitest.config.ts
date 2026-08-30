import { fileURLToPath } from 'node:url';

import { defineConfig } from 'vitest/config';

/**
 * `jsx.runtime: 'automatic'` is set explicitly because the app's own
 * `tsconfig.json` says `"jsx": "preserve"` — Next compiles JSX itself, and the
 * type checker must not. Vitest has no Next in front of it, so it needs to be
 * told, or every `.tsx` test fails on an undefined `React`.
 *
 * Vite 8 transforms with Oxc rather than esbuild, so the option lives under `oxc`.
 *
 * No `server-only` alias, unlike `web-passenger`. That surface needs one because
 * its transport module is `server-only` and its tests import it; here the only
 * `server-only` module is `src/i18n/server.ts`, which is a `headers()` reader with
 * no rule in it — the rules live in `src/i18n/index.ts`, which is framework-free
 * and is what the tests import. If a later session needs to test a `server-only`
 * module, copy `web-passenger/test/support/server-only.ts` and add the alias then.
 */
export default defineConfig({
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  oxc: {
    jsx: { runtime: 'automatic' },
  },
  test: {
    environment: 'jsdom',
    include: ['test/**/*.test.ts', 'test/**/*.test.tsx'],
  },
});
