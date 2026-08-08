/**
 * The executable form of the C111 Definition of Done item:
 *
 *   "no runtime CSS-in-JS appears in the production bundle"
 *
 * `portals/scripts/check-al52.mjs` already proves nothing under `portals/`
 * *imports* a banned package. That is a statement about the source, and it is the
 * cheaper half. This is the statement about the artefact: whatever the source
 * said, what a browser is actually sent contains no style-injecting runtime, and
 * every rule it renders was compiled to a file at build time.
 *
 * The two halves catch different failures. A transitive dependency can pull
 * Emotion in without a single import in this repo, and a CSS-in-JS library that
 * arrived that way would be invisible to a grep over source and perfectly visible
 * here. Conversely a package listed in `package.json` and never imported is a
 * review comment, not a bundle.
 *
 * Runs as the second half of `npm run build`, so the DoD item is checked by the
 * component's own Verify line rather than by somebody remembering to look.
 */

import { readFile, readdir, stat } from 'node:fs/promises';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const buildRoot = join(appRoot, '.next');
const clientRoot = join(buildRoot, 'static');

const banned = JSON.parse(
  await readFile(join(appRoot, '../eslint-config/banned-styling-packages.json'), 'utf8'),
);

/**
 * Runtime signatures, as opposed to package names.
 *
 * A bundler rewrites specifiers, so "does the string `styled-components` appear"
 * is a weak test on its own; what survives minification is the library's own
 * runtime vocabulary. These are the marks each one leaves in a shipped chunk.
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

  // Module ids survive Turbopack's output as `node_modules/<name>` paths, so a
  // banned package that was bundled is still nameable in the artefact.
  for (const name of banned.packages) {
    if (source.includes(`node_modules/${name}/`)) findings.push(`${rel}: bundles "${name}"`);
  }
  for (const prefix of banned.prefixes) {
    if (source.includes(`node_modules/${prefix}`)) findings.push(`${rel}: bundles "${prefix}…"`);
  }
}

/*
 * A build that emitted no stylesheet at all would pass every check above by
 * having shipped nothing — which is exactly what a portal whose styling had
 * quietly moved into JavaScript would look like.
 */
if (stylesheets === 0) {
  findings.push(
    '.next/static: no compiled stylesheet was emitted. AL-52 requires CSS compiled at ' +
      'build time by PostCSS; a bundle with no .css file is not styled at build time.',
  );
}

if (findings.length > 0) {
  process.stderr.write(
    'AL-52 violation — runtime CSS-in-JS in the Fleet Portal production bundle:\n\n' +
      findings.map((finding) => `  ${finding}\n`).join('') +
      '\n',
  );
  process.exit(1);
}

process.stdout.write(
  `AL-52: clean — ${stylesheets} compiled stylesheet(s), ${(stylesheetBytes / 1024).toFixed(1)} kB CSS; ` +
    `${(javascriptBytes / 1024).toFixed(0)} kB of client JavaScript carries no style-injecting runtime.\n`,
);
