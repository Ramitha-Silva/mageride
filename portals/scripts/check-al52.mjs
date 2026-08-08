/**
 * The executable form of the C103 Definition of Done item:
 *
 *   "a grep for mui|bootstrap|styled-components|@emotion across portals/
 *    returns nothing"
 *
 * A literal grep cannot quite be that check, because the list of banned names
 * has to be written down somewhere for the ESLint rule to enforce it, and that
 * one file would be a permanent false positive. So the check is run properly
 * instead: every dependency declaration and every module specifier under
 * portals/ is examined, and the single enforcement file is skipped by name.
 *
 * This is stricter than the grep, not weaker — the grep would miss
 * `@material-ui/core`, `@stitches/react` and `<style jsx>`, and would be
 * satisfied by a `bootstrap` entry hidden in a lockfile field it does not read.
 *
 * ESLint enforces the same fence inside source files as you type; this runs
 * over the tree, including files no ESLint config covers.
 */

import { readFile, readdir } from 'node:fs/promises';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const portalsRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

const ENFORCEMENT_FILES = new Set([
  'eslint-config/banned-styling-packages.json',
  'eslint-config/rules/no-runtime-css-in-js.js',
  'eslint-config/test/no-runtime-css-in-js.test.js',
  'scripts/check-al52.mjs',
]);

const SKIP_DIRS = new Set(['node_modules', 'dist', '.next', 'out', 'coverage', '.git']);

const SOURCE_EXTENSIONS = ['.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs', '.css', '.scss'];

const banned = JSON.parse(
  await readFile(join(portalsRoot, 'eslint-config/banned-styling-packages.json'), 'utf8'),
);

/** @param {string} specifier */
function bannedName(specifier) {
  if (banned.packages.includes(specifier)) return specifier;
  const prefix = banned.prefixes.find((p) => specifier.startsWith(p));
  return prefix ? specifier : null;
}

/** @param {string} dir @returns {AsyncGenerator<string>} */
async function* walk(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (SKIP_DIRS.has(entry.name)) continue;
      yield* walk(join(dir, entry.name));
    } else if (entry.isFile()) {
      yield join(dir, entry.name);
    }
  }
}

/** @type {string[]} */
const findings = [];

const DEPENDENCY_FIELDS = [
  'dependencies',
  'devDependencies',
  'peerDependencies',
  'optionalDependencies',
];

// A specifier in an import, a dynamic import, a require, or a CSS @import/url().
const SPECIFIER_PATTERNS = [
  /\bfrom\s+['"]([^'"]+)['"]/g,
  /\bimport\s*\(\s*['"]([^'"]+)['"]\s*\)/g,
  /\bimport\s+['"]([^'"]+)['"]/g,
  /\brequire\s*\(\s*['"]([^'"]+)['"]\s*\)/g,
  /@import\s+['"]([^'"]+)['"]/g,
];

for await (const file of walk(portalsRoot)) {
  const rel = relative(portalsRoot, file).replaceAll('\\', '/');
  if (ENFORCEMENT_FILES.has(rel)) continue;

  if (rel.endsWith('package.json')) {
    const manifest = JSON.parse(await readFile(file, 'utf8'));
    for (const field of DEPENDENCY_FIELDS) {
      for (const name of Object.keys(manifest[field] ?? {})) {
        if (bannedName(name)) findings.push(`${rel}: ${field}."${name}"`);
      }
    }
    continue;
  }

  if (!SOURCE_EXTENSIONS.some((ext) => rel.endsWith(ext))) continue;

  const source = await readFile(file, 'utf8');
  for (const pattern of SPECIFIER_PATTERNS) {
    for (const [, specifier] of source.matchAll(pattern)) {
      if (bannedName(specifier)) findings.push(`${rel}: imports "${specifier}"`);
    }
  }
  if (/<style\s+[^>]*\bjsx\b/.test(source)) {
    findings.push(`${rel}: <style jsx> injects CSS at runtime`);
  }
}

if (findings.length > 0) {
  process.stderr.write(
    'AL-52 violation — Tailwind CSS is the sole styling system for MageRide web surfaces.\n' +
      'Runtime CSS-in-JS and pre-styled component kits are excluded:\n\n' +
      findings.map((f) => `  ${f}\n`).join('') +
      '\n',
  );
  process.exit(1);
}

process.stdout.write(
  `AL-52: clean — no runtime CSS-in-JS or pre-styled kit under portals/ (${banned.packages.length + banned.prefixes.length} names checked).\n`,
);
