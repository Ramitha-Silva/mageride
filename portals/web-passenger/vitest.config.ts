import { fileURLToPath } from 'node:url';

import { defineConfig } from 'vitest/config';

/**
 * `jsx.runtime: 'automatic'` is set explicitly because the app's own
 * `tsconfig.json` says `"jsx": "preserve"` — Next compiles JSX itself, and the
 * type checker must not. Vitest has no Next in front of it, so it needs to be
 * told, or every `.tsx` test fails on an undefined `React`.
 *
 * Vite 8 transforms with Oxc rather than esbuild, so the option lives under `oxc`.
 */
export default defineConfig({
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
      // `server-only` resolves to a module that throws unless the `react-server`
      // condition is set, which is the whole point of it. Vitest is neither
      // bundle, so it gets the empty module the server condition would give.
      'server-only': fileURLToPath(new URL('./test/support/server-only.ts', import.meta.url)),
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
