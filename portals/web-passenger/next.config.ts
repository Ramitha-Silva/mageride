import { fileURLToPath } from 'node:url';

import type { NextConfig } from 'next';

/**
 * The passenger web subview is **server-rendered**, and that is the fence rather
 * than a preference.
 *
 * D2 §SCR-WT-001 requires that "no data is rendered before validation" and the
 * C117 Definition of Done requires that an expired token leaves "no ride data in
 * the DOM or network payload". A statically exported shell that fetched the
 * snapshot from the browser could satisfy neither: the page would exist before the
 * token was known to be live, and the refusal would arrive after the document had.
 * Here the token is redeemed on the server and the first byte of HTML is already
 * the answer.
 *
 * Three settings carry a decision:
 *
 *   - `output: 'standalone'` because the deployment target is a container
 *     (CLAUDE.md, Infra) rather than a Node tree with `node_modules` beside it.
 *   - `outputFileTracingRoot` because `infra/docker/Dockerfile.portal` (C010)
 *     asserts `.next/standalone/portals/<portal>/server.js`. Left alone, Next roots
 *     the trace at the nearest lockfile — `portals/` — and emits one segment less.
 *   - `poweredByHeader: false`. This surface is opened from an SMS by somebody who
 *     has no account; it should announce nothing about itself.
 *
 * There is deliberately **no** `experimental.authInterrupts` (this application has
 * no roles to forbid — the token either resolves or the page is SCR-WT-006) and no
 * styling configuration at all: Tailwind v4 reads its theme from CSS
 * (`app/globals.css` → `@mageride/tailwind-preset/theme.css`), and a
 * `tailwind.config.js` on v4 *merges* `screens` instead of replacing them, which
 * would quietly restore Tailwind's 640px `sm:` over D2's 375px one
 * (`portals/tailwind-preset/README.md`). On a mobile-first surface that is the one
 * breakpoint that must not move.
 */
const nextConfig: NextConfig = {
  output: 'standalone',
  outputFileTracingRoot: fileURLToPath(new URL('../..', import.meta.url)),
  reactStrictMode: true,
  poweredByHeader: false,
};

export default nextConfig;
