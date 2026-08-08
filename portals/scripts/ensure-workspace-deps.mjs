/**
 * Builds the three shared packages a portal imports, when — and only when —
 * their build output is missing.
 *
 * `@mageride/tailwind-preset`, `@mageride/i18n` and `@mageride/ui` all publish
 * from `dist/`, and `dist/` is gitignored (root `.gitignore`). npm workspaces
 * link them by symlink but have no build graph, so
 *
 *     npm --prefix portals run build --workspace admin
 *
 * on a fresh checkout resolves `@mageride/i18n` to a directory with no
 * `dist/index.js` in it and fails on an import that is perfectly correct. That
 * command is a component's whole Verify line in `build/manifest.yaml`, so it has
 * to work from a clean tree rather than only after somebody has run the root
 * build first.
 *
 * Deliberately a freshness check on *existence*, not on mtime: rebuilding an
 * up-to-date package on every `next dev` restart would cost seconds on every
 * edit, and a developer who has changed the preset runs its own build (or the
 * root one) — which is what its `npm run build` is for.
 */

import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const portalsRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

/** Each package, and the one artefact whose absence means "not built yet". */
const PACKAGES = [
  ['@mageride/tailwind-preset', 'tailwind-preset/dist/theme.css'],
  ['@mageride/i18n', 'i18n/dist/index.js'],
  ['@mageride/ui', 'ui/dist/index.js'],
];

const missing = PACKAGES.filter(([, artefact]) => !existsSync(join(portalsRoot, artefact)));

if (missing.length === 0) {
  process.exit(0);
}

for (const [name] of missing) {
  process.stdout.write(`ensure-workspace-deps: building ${name}…\n`);
  execFileSync('npm', ['run', 'build', '--workspace', name], {
    cwd: portalsRoot,
    stdio: 'inherit',
  });
}
