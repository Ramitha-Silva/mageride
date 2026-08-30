/**
 * The half of the trilingual rule the type system cannot see.
 *
 * `src/i18n/messages/en.ts` declares a literal object and *defines* `WwwMessages`;
 * `si.ts` and `ta.ts` are annotated with it. That already makes a key present in
 * one language and absent in another a **compile error**, and it is the cheaper,
 * stronger half — it fails in the editor. This script exists for the three
 * failures a type cannot express:
 *
 *   1. **An orphan.** A key that exists in all three languages, is perfectly
 *      typed, and is rendered nowhere. Every one is a string somebody translated
 *      for no reason, and on a surface whose translation budget is the plan's
 *      largest single cost (A23) that is the expensive kind of dead code. It also
 *      catches the *other* direction of a deletion: a page removed in one session
 *      leaves its keys behind, and the next translator translates them.
 *
 *   2. **A placeholder that survives in one language and is dropped in another.**
 *      `'{route} is being written'` translated as a sentence with no `{route}` in
 *      it type-checks perfectly and renders a sentence with a hole in it. This is
 *      a real and common translation failure mode — a translator working from a
 *      spreadsheet does not always know that the braces are load-bearing — and
 *      nothing else in the toolchain looks at it.
 *
 *   3. **A key in `si`/`ta` that `en` does not have.** The compiler does catch
 *      this one. It is checked here anyway so that this script is a complete
 *      statement of the rule rather than a footnote to it, and so that it still
 *      holds if a later session ever loosens the annotation.
 *
 * Runs inside `npm run lint`, so it is part of the component's Verify line rather
 * than something somebody remembers to run.
 *
 * The message tables are TypeScript and are imported as TypeScript: Node 24 strips
 * types natively, `package.json` says `"type": "module"`, and the only import in
 * `si.ts`/`ta.ts` is a type-only one that stripping erases. No parser, no build
 * step and no dependency — so this check cannot drift from what the application
 * actually loads.
 */

import { readFile, readdir } from 'node:fs/promises';
import { dirname, extname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const messagesDir = join(appRoot, 'src/i18n/messages');

/** Where a key may be referenced from. The tables themselves are not references. */
const SOURCE_ROOTS = ['app', 'src'];
const SOURCE_EXTENSIONS = new Set(['.ts', '.tsx']);
const SKIP_DIRS = new Set(['node_modules', '.next', 'out', 'coverage']);

const PLACEHOLDER = /\{(\w+)\}/g;

const { wwwEn } = await import(join(messagesDir, 'en.ts'));
const { wwwSi } = await import(join(messagesDir, 'si.ts'));
const { wwwTa } = await import(join(messagesDir, 'ta.ts'));

/** The tables, in D1' §283's display order — Sinhala first. */
const TABLES = [
  ['si', wwwSi],
  ['ta', wwwTa],
  ['en', wwwEn],
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
    if (entry.isDirectory()) {
      if (!SKIP_DIRS.has(entry.name)) yield* walk(path);
    } else if (entry.isFile() && SOURCE_EXTENSIONS.has(extname(entry.name))) {
      yield path;
    }
  }
}

/** @type {string[]} */
const findings = [];

const englishKeys = Object.keys(wwwEn);

// --- 1 · every key in every table, and no table carrying one `en` does not ------
for (const [locale, table] of TABLES) {
  for (const key of englishKeys) {
    if (!Object.hasOwn(table, key)) findings.push(`${locale}: missing "${key}"`);
  }
  for (const key of Object.keys(table)) {
    if (!Object.hasOwn(wwwEn, key)) {
      findings.push(`${locale}: defines "${key}", which en.ts does not have`);
    }
  }
}

// --- 2 · placeholders, per key, across the three tables -------------------------
for (const key of englishKeys) {
  /** @type {Map<string, Set<string>>} locale -> placeholder names */
  const perLocale = new Map();

  for (const [locale, table] of TABLES) {
    const template = table[key];
    if (typeof template !== 'string') continue;
    perLocale.set(locale, new Set([...template.matchAll(PLACEHOLDER)].map(([, name]) => name)));
  }

  const expected = perLocale.get('en') ?? new Set();

  for (const [locale, names] of perLocale) {
    if (locale === 'en') continue;
    for (const name of expected) {
      if (!names.has(name)) {
        findings.push(`${locale}: "${key}" drops the {${name}} placeholder that en.ts carries`);
      }
    }
    for (const name of names) {
      if (!expected.has(name)) {
        findings.push(`${locale}: "${key}" adds a {${name}} placeholder that en.ts does not have`);
      }
    }
  }
}

// --- 3 · orphans ----------------------------------------------------------------
//
// A key is referenced if its literal string appears in any source file outside the
// tables. That covers both `t('www.nav.home')` and the indirect form this surface
// uses — `src/lib/routes.ts` binds a `labelKey` per route and `StubPage` resolves
// it — because the literal is written down either way. It is deliberately a
// *string* search rather than an AST walk: a key assembled at runtime from
// fragments would defeat any analysis, and the rule against doing that is exactly
// what this check would otherwise have to encode.
const referenced = new Set();

for (const root of SOURCE_ROOTS) {
  for await (const file of walk(join(appRoot, root))) {
    if (file.startsWith(messagesDir)) continue;
    const source = await readFile(file, 'utf8');
    for (const key of englishKeys) {
      if (source.includes(`'${key}'`) || source.includes(`"${key}"`)) referenced.add(key);
    }
  }
}

for (const key of englishKeys) {
  if (!referenced.has(key)) {
    findings.push(
      `orphan: "${key}" is translated into all three languages and rendered nowhere under ` +
        `${SOURCE_ROOTS.map((r) => `${r}/`).join(' or ')}`,
    );
  }
}

// --- 4 · untranslated strings, counted but not failed ---------------------------
//
// S07 writes the English corpus and gives `si`/`ta` the same keys carrying the
// English text behind a `TODO(si)` / `TODO(ta)` marker. That is what "deferred, not
// dropped" looks like in a table the compiler checks: the key exists, so nothing
// breaks and no key can quietly go missing, and the marker makes the debt visible.
//
// **A warning, never a failure.** MCS-34 D2 defers Tamil to the release after
// launch, so failing on a TODO would mean this component could not go green until a
// decision the user deliberately postponed had been reversed.
//
// **Where this stands now.** S12 drove `si` to zero. S13 did *not* do the same for
// `ta` — it executed D2's deferral instead: `ta.ts` stays complete and type-checked
// while `/ta` is unpublished and 404s, gated on `WWW_LOCALES` in
// `src/i18n/index.ts`. So the remaining count is not an outstanding C134 task; it is
// the size of what the next release owes, printed on every build so that "deferred"
// cannot quietly drift into "forgotten".
//
// The one thing worth watching: a TODO string is *English wearing a Tamil label*. On
// this surface no reader can reach one — that is the point of the deferral — but the
// moment `WWW_LOCALES` grows, every one of them becomes visible to somebody.
const TODO_MARKER = /TODO\((si|ta)\)/;

/** @type {Map<string, number>} */
const untranslated = new Map();

for (const [locale, table] of TABLES) {
  const pending = Object.values(table).filter(
    (value) => typeof value === 'string' && TODO_MARKER.test(value),
  ).length;
  if (pending > 0) untranslated.set(locale, pending);
}

if (findings.length > 0) {
  process.stderr.write(
    `i18n parity failed for ${relative(resolve(appRoot, '../..'), appRoot)}:\n\n` +
      findings.map((finding) => `  ${finding}\n`).join('') +
      '\n',
  );
  process.exit(1);
}

process.stdout.write(
  `i18n: ${englishKeys.length} keys × ${TABLES.length} languages — complete, ` +
    'placeholder-consistent, and every one of them rendered.\n',
);

if (untranslated.size > 0) {
  const summary = [...untranslated]
    .map(([locale, count]) => `${locale}: ${count}`)
    .join(' · ');
  process.stdout.write(
    `i18n: ${summary} strings still carry a TODO marker. Not a failure — Tamil is ` +
      'formally deferred by MCS-34 D2 and no /ta URL is published, so nobody reads ' +
      'these. Add the table to WWW_PUBLISHED_MESSAGES in src/i18n/index.ts and they ' +
      'become visible — and, since S19, are downloaded for the first time.\n',
  );
}
