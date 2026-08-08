import { fileURLToPath } from 'node:url';

import type { NextConfig } from 'next';

/**
 * The Admin Portal is a **server-rendered** application and stays one.
 *
 * Two settings carry a decision rather than a preference:
 *
 *   - `experimental.authInterrupts` turns on `forbidden()`, which is how a route
 *     the caller's URD §2.3 row does not permit answers **403** rather than a
 *     200 whose body says "no". The Definition of Done asks for the status, and
 *     a status is not something a React tree can set on its own.
 *   - `output: 'standalone'` because the deployment target is a container in the
 *     lightweight replica / DOKS (CLAUDE.md, Infra) rather than a Node tree with
 *     `node_modules` copied beside it.
 *
 * There is deliberately **no** styling configuration here. Tailwind v4 reads its
 * theme from CSS (`app/globals.css` → `@mageride/tailwind-preset/theme.css`), and
 * a `tailwind.config.js` on v4 *merges* `screens` instead of replacing them,
 * which would quietly restore Tailwind's 640px `sm:` over D2's 375px one
 * (`portals/tailwind-preset/README.md`).
 */
const nextConfig: NextConfig = {
  output: 'standalone',
  /**
   * Where the standalone bundle's directory tree is rooted.
   *
   * Left alone, Next roots it at the nearest lockfile — `portals/` — and emits
   * `.next/standalone/admin/server.js`. `infra/docker/Dockerfile.portal` (C010)
   * asserts `.next/standalone/portals/<portal>/server.js` and starts
   * `node portals/$PORTAL/server.js`, i.e. it roots the tree one level higher.
   * Rooting it here rather than changing that file keeps one shape in the image
   * and in a local build, and it is the portal's own build output either way.
   */
  outputFileTracingRoot: fileURLToPath(new URL('../..', import.meta.url)),
  reactStrictMode: true,
  poweredByHeader: false,
  experimental: {
    authInterrupts: true,
  },
};

// Linting is `npm run lint` and runs the shared flat config
// (`@mageride/eslint-config`). Next 16 dropped its own build-time lint step, so
// there is nothing to switch off here — the gate is the verify command.

export default nextConfig;
