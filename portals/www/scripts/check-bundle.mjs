/**
 * The executable form of three C134 fences that a source-level check cannot reach.
 *
 * 1. **AL-52 — no runtime CSS-in-JS in the production bundle.**
 *    `portals/scripts/check-al52.mjs` already proves nothing under `portals/`
 *    *imports* a banned package. That is a statement about the source and it is
 *    the cheaper half; this is the statement about the artefact. A transitive
 *    dependency can pull Emotion in without a single import in this repo, and a
 *    CSS-in-JS library that arrived that way would be invisible to a grep over
 *    source and perfectly visible here.
 *
 * 2. **No motion library — the fence `banned-styling-packages.json` does not
 *    cover** (README §4.3). `framer-motion` / `motion` is on neither the package
 *    list nor the prefix list, so a motion library would pass `check-al52.mjs`
 *    completely clean and still violate AL-52's stated intent: *CSS compiled at
 *    build time by PostCSS, one plugin, no runtime style injection.* MCS-34
 *    declined to widen the shared list — that would be a platform-wide styling
 *    change made for one marketing page — so the fence is held here, by signature,
 *    over the artefact. Motion on this surface is CSS keyframes plus the Web
 *    Animations API (S04), neither of which leaves a marker.
 *
 * 3. **The platform is not in the browser, and here the strong form is the true
 *    one.** `web-passenger` names its four server-only variables and searches for
 *    each: it *has* server configuration, so it can only check that none of it
 *    leaked. This surface has none. It reads no gateway, no map style and no store
 *    URL — the site renders with the whole backend down (MCS-34's fourth negative)
 *    — so the honest assertion is not "these four names are absent" but **"the
 *    `NEXT_PUBLIC_` prefix does not appear in any client chunk at all."** Nothing
 *    can be promoted to a public variable without this failing, which is the point:
 *    the first `NEXT_PUBLIC_*` on this surface is the one that ends the promise.
 *
 * Runs as the second half of `npm run build`, so all three are checked by the
 * component's own Verify line rather than by somebody remembering to look.
 *
 * The whole-tree JS and CSS totals printed at the end stay **reported, not
 * enforced**, and that is now a deliberate division of labour rather than a
 * deferral. They are raw bytes over every emitted chunk, which is the right shape
 * for the fences above — "may this bundle contain X" — and the wrong shape for a
 * budget, which is a question about one page and about gzip. **S19 added the budget
 * as its own section further down**, per page, gzipped, and split into the bytes
 * this surface controls and the framework floor it cannot.
 */

import { readFile, readdir, stat } from 'node:fs/promises';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const buildRoot = join(appRoot, '.next');
const clientRoot = join(buildRoot, 'static');
const screensRoot = join(appRoot, 'public/screens');

/**
 * The screen-imagery budget (S06 / plan §A17).
 *
 * Unlike the JS and CSS totals below, **these are enforced from the day they are
 * written**, and the difference is not inconsistency. A34's JS budget describes a
 * finished page, and the pages are still empty — a threshold set against them now
 * would be met by having shipped nothing. `public/screens/` is the opposite: it is
 * complete *today*, it is committed to the repository, and it is the one part of
 * this surface that gets heavier every time somebody adds a screen without
 * thinking about it.
 *
 * S06's brief is explicit that the numbers are the fence: **"Do not raise them to
 * make a build pass."** If a future session breaches this, the fix is fewer screens
 * or tighter encoder quality, in that order.
 */
const SCREEN_BUDGET_BYTES = 12 * 1024 * 1024;
const SCREEN_MAX_IMAGE_BYTES = 220 * 1024;

/**
 * The formats every registry entry must resolve to at 1×.
 *
 * A registry entry with no image is a broken `<img>` on a public page, and it is
 * the failure mode a build should catch rather than a reader. 1× only: `@2x` is a
 * `srcset` upgrade and its absence degrades to a softer image, where a missing 1×
 * degrades to nothing at all.
 */
const REQUIRED_SCREEN_FORMATS = ['avif', 'webp'];

const banned = JSON.parse(
  await readFile(join(appRoot, '../eslint-config/banned-styling-packages.json'), 'utf8'),
);

/**
 * Runtime signatures, as opposed to package names.
 *
 * A bundler rewrites specifiers, so "does the string `styled-components` appear" is
 * a weak test on its own; what survives minification is the library's own runtime
 * vocabulary. These are the marks each one leaves in a shipped chunk.
 */
const RUNTIME_SIGNATURES = [
  ['sc-component-id', 'styled-components'],
  ['__emotion_', 'Emotion'],
  ['data-emotion', 'Emotion'],
  ['__jsx-style-dynamic-selector', 'styled-jsx'],
  ['jsx-style-registry', 'styled-jsx'],
  ['@vanilla-extract', 'vanilla-extract'],
  ['data-goober', 'goober'],
];

/**
 * The motion libraries, by module id and by runtime signature.
 *
 * Module ids survive Turbopack's output as `node_modules/<name>` paths, which is
 * the reliable half. The signatures are the second net, for a library vendored or
 * re-exported under another name: `framer-motion` and `motion` share a `projection`
 * layout engine whose node type is spelled in the bundle, and `@react-spring`
 * ships its own animated-node marker.
 */
const MOTION_PACKAGES = [
  'framer-motion',
  'motion',
  'motion-dom',
  'popmotion',
  '@react-spring/web',
  '@react-spring/core',
  'react-spring',
  'gsap',
  '@gsap/react',
];

const MOTION_PREFIXES = ['@react-spring/', '@motionone/'];

const MOTION_SIGNATURES = [
  ['framerAppearId', 'framer-motion'],
  ['data-framer-appear-id', 'framer-motion'],
  ['projectionNodeConstructor', 'framer-motion / motion'],
  ['@react-spring/animated', '@react-spring'],
  ['gsap.registerPlugin', 'gsap'],
];

/**
 * The one variable prefix, checked as a prefix.
 *
 * Next inlines `process.env.NEXT_PUBLIC_FOO` as a literal substitution and the
 * *name* survives in the module that reads it, so the prefix appearing anywhere in
 * a client chunk means a public variable exists. There is none, and there must
 * never be one.
 */
const PUBLIC_ENV_PREFIX = 'NEXT_PUBLIC_';

/** @param {string} dir @returns {AsyncGenerator<string>} */
async function* walk(dir) {
  let entries;
  try {
    entries = await readdir(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) yield* walk(path);
    else if (entry.isFile()) yield path;
  }
}

try {
  await stat(buildRoot);
} catch {
  process.stderr.write(
    'check-bundle: no .next/ directory. This runs after `next build`, never instead of it.\n',
  );
  process.exit(1);
}

/** @type {string[]} */
const findings = [];

let javascriptBytes = 0;
let stylesheetBytes = 0;
let stylesheets = 0;

for await (const file of walk(clientRoot)) {
  const rel = relative(appRoot, file).replaceAll('\\', '/');

  if (file.endsWith('.css')) {
    stylesheets += 1;
    stylesheetBytes += (await readFile(file)).byteLength;
    continue;
  }

  if (!file.endsWith('.js')) continue;

  const source = await readFile(file, 'utf8');
  javascriptBytes += source.length;

  for (const [signature, library] of RUNTIME_SIGNATURES) {
    if (source.includes(signature)) {
      findings.push(`${rel}: carries ${library}'s runtime marker "${signature}"`);
    }
  }

  for (const [signature, library] of MOTION_SIGNATURES) {
    if (source.includes(signature)) {
      findings.push(`${rel}: carries ${library}'s runtime marker "${signature}"`);
    }
  }

  // Module ids survive Turbopack's output as `node_modules/<name>` paths, so a
  // package that was bundled is still nameable in the artefact.
  for (const name of [...banned.packages, ...MOTION_PACKAGES]) {
    if (source.includes(`node_modules/${name}/`)) findings.push(`${rel}: bundles "${name}"`);
  }
  for (const prefix of [...banned.prefixes, ...MOTION_PREFIXES]) {
    if (source.includes(`node_modules/${prefix}`)) findings.push(`${rel}: bundles "${prefix}…"`);
  }

  if (source.includes(PUBLIC_ENV_PREFIX)) {
    findings.push(
      `${rel}: names a "${PUBLIC_ENV_PREFIX}" variable. This surface publishes none and must ` +
        'not — it has no request-time dependency on the platform to configure.',
    );
  }
}

/*
 * A build that emitted no stylesheet at all would pass every check above by having
 * shipped nothing — which is exactly what a surface whose styling had quietly moved
 * into JavaScript would look like.
 */
if (stylesheets === 0) {
  findings.push(
    '.next/static: no compiled stylesheet was emitted. AL-52 requires CSS compiled at ' +
      'build time by PostCSS; a bundle with no .css file is not styled at build time.',
  );
}

/*
 * The committed screen imagery: total size, per-image size, and completeness
 * against the registry.
 *
 * Read from `public/screens/` rather than from `.next/`, because these files are
 * committed *source* as far as the site is concerned — Next copies `public/`
 * through untouched, so checking the build output would measure the same bytes one
 * step later and would say nothing extra.
 */
let screenBytes = 0;
let screenCount = 0;

for await (const file of walk(screensRoot)) {
  if (file.endsWith('.json') || file.endsWith('.md')) continue;

  const rel = relative(appRoot, file).replaceAll('\\', '/');
  const { size } = await stat(file);
  screenBytes += size;
  screenCount += 1;

  if (size > SCREEN_MAX_IMAGE_BYTES) {
    findings.push(
      `${rel}: ${(size / 1024).toFixed(0)} kB exceeds the ${SCREEN_MAX_IMAGE_BYTES / 1024} kB ` +
        'per-image budget. Re-encode at lower quality — do not raise the number.',
    );
  }
}

if (screenBytes > SCREEN_BUDGET_BYTES) {
  findings.push(
    `public/screens: ${(screenBytes / 1024 / 1024).toFixed(2)} MB over ${screenCount} images ` +
      `exceeds the ${SCREEN_BUDGET_BYTES / 1024 / 1024} MB budget. The fix is fewer screens or ` +
      'tighter encoder quality, in that order.',
  );
}

/*
 * Every registry entry resolves to a real file.
 *
 * The registry is the source of truth for which screens exist, so this is checked
 * in that direction: an entry with no image is a broken image on a public page.
 * `screens.ts` is imported as TypeScript — Node strips the types, the same way
 * `check-i18n-parity.mjs` reads the message tables — so the check cannot drift from
 * what the site actually renders.
 */
if (screenCount > 0) {
  const { SCREENS } = await import(join(appRoot, 'src/content/screens.ts'));

  for (const screen of SCREENS) {
    for (const appearance of screen.appearances) {
      const stem = appearance === 'light' ? screen.file : `${screen.file}--dark`;
      for (const format of REQUIRED_SCREEN_FORMATS) {
        try {
          await stat(join(screensRoot, `${stem}.${format}`));
        } catch {
          findings.push(
            `public/screens: ${screen.id} (${appearance}) has no ${stem}.${format}. ` +
              'Run `npm run screens:refresh`.',
          );
        }
      }
    }
  }
}

/*
 * ---------------------------------------------------------------------------
 * A34's byte budget is **not here** — `scripts/check-budget.mjs`, `npm run budget`.
 *
 * It was, from S19 until S21, and moving it was about what a failure may stop rather than
 * about the numbers, which did not change. This script runs inside `npm run build` and
 * asserts the *artefact's integrity*: no style-injecting runtime, no motion library, no
 * public environment variable, every registry entry resolving to a file. Failing any of
 * those means the build produced something that must not ship, so stopping the build is
 * right.
 *
 * A byte budget says a page is slower than it should be — a different claim, and wiring it
 * in here stopped three things that are not about performance: the **container image**
 * (`Dockerfile.portal` runs `npm run build`), **every portal's tests** (`pretest` is
 * `npm run build`, so one surface's breach aborted all eight suites), and **S22**, which
 * needs an image to smoke-test.
 *
 * Nothing was lowered and nothing stopped being enforced: `npm run budget` is in CI's
 * portal leg beside `lint` and `build`. A performance regression now fails a *merge*
 * instead of an *artefact*.
 * ---------------------------------------------------------------------------
 */

if (findings.length > 0) {
  process.stderr.write(
    'Bundle check failed for the informational site:\n\n' +
      findings.map((finding) => `  ${finding}\n`).join('') +
      '\n',
  );
  process.exit(1);
}

process.stdout.write(
  `AL-52: clean — ${stylesheets} compiled stylesheet(s), ${(stylesheetBytes / 1024).toFixed(1)} kB CSS; ` +
    `${(javascriptBytes / 1024).toFixed(0)} kB of client JavaScript carries no style-injecting ` +
    'runtime, no motion library and no NEXT_PUBLIC_ variable.\n' +
    '   (whole-tree RAW totals, for these fences. A34 is per-page and gzipped and lives in\n' +
    '    `npm run budget` — the two will not add up, and should not.)\n' +
    `screens: ${screenCount} images, ${(screenBytes / 1024 / 1024).toFixed(2)} MB of ` +
    `${SCREEN_BUDGET_BYTES / 1024 / 1024} MB — every registry entry resolves, none over ` +
    `${SCREEN_MAX_IMAGE_BYTES / 1024} kB.\n`,
);
