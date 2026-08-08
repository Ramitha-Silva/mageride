import { fileURLToPath } from 'node:url';

import type { NextConfig } from 'next';

/**
 * The Fleet Portal is a **server-rendered** application and stays one.
 *
 * The three settings that carry a decision rather than a preference are the same
 * three the Admin Portal made, and for the same reasons:
 *
 *   - `experimental.authInterrupts` turns on `forbidden()`, which is how a route
 *     the caller's sub-role does not permit answers a real **403** instead of a
 *     200 whose body says "no". A status is not something a React tree can set.
 *   - `output: 'standalone'` because the deployment target is a container in the
 *     lightweight replica / DOKS (CLAUDE.md, Infra) rather than a Node tree with
 *     `node_modules` copied beside it.
 *   - `outputFileTracingRoot` because `infra/docker/Dockerfile.portal` (C010)
 *     asserts `.next/standalone/portals/<portal>/server.js` and starts
 *     `node portals/$PORTAL/server.js`. Left alone, Next roots the tree at the
 *     nearest lockfile — `portals/` — and emits one path segment less.
 *
 * There is deliberately **no** styling configuration here. Tailwind v4 reads its
 * theme from CSS (`app/globals.css` → `@mageride/tailwind-preset/theme.css`), and
 * a `tailwind.config.js` on v4 *merges* `screens` instead of replacing them,
 * which would quietly restore Tailwind's 640px `sm:` over D2's 375px one
 * (`portals/tailwind-preset/README.md`).
 */
const nextConfig: NextConfig = {
  output: 'standalone',
  outputFileTracingRoot: fileURLToPath(new URL('../..', import.meta.url)),
  reactStrictMode: true,
  poweredByHeader: false,
  experimental: {
    authInterrupts: true,
  },
};

export default nextConfig;
