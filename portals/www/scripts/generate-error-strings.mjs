/**
 * Generates `src/i18n/error-strings.ts` — **the four strings the error boundary needs,
 * and nothing else.**
 *
 * ## Why this file exists (MCS-36 D3)
 *
 * Every other client component on this surface receives its strings as props from a
 * server parent, so the resource tables never cross the boundary. `app/[locale]/error.tsx`
 * cannot: Next instantiates an error boundary itself and hands it `{ error, reset }`
 * and no params, so it has no server parent and no locale. It must know its strings at
 * module scope.
 *
 * Importing the tables to get them costs **90 kB gzipped on every page**, because a
 * bundler cannot tree-shake four members out of an object literal — which is exactly
 * what the measurement showed after the other thirteen components were converted:
 * first-party JS fell 113.7 → 108.3 kB and the tables were still there, held by this
 * one file.
 *
 * So the subset is generated. The pattern is `src/content/screen-dimensions.ts`'s and
 * the reasoning is the same one CLAUDE.md gives there: **a `.ts` module and not JSON**,
 * because three toolchains read this tree — Next's bundler, `tsc`, and raw Node — and
 * Node needs `with { type: 'json' }` where the bundler does not.
 *
 * ## The guarantee
 *
 * `test/i18n.test.ts` asserts every generated value equals the table it came from, so
 * a translator editing `si.ts` without re-running this turns the suite red rather than
 * shipping a stale error page. Run `npm run i18n:error-strings` after any change to the
 * four keys below.
 */

import { writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const i18nDir = join(appRoot, 'src/i18n');

/**
 * The keys the error boundary renders. Adding one here and re-running is the whole
 * maintenance story; the parity test catches the case where somebody adds a `t()` call
 * to `error.tsx` and forgets.
 */
const KEYS = ['www.error.title', 'www.error.body', 'www.notFound.home'];

/** From the shared table rather than this surface's — `common.retry` is platform-wide. */
const SHARED_KEYS = ['common.retry'];

const { WWW_LOCALES } = await import(join(i18nDir, 'locales.ts'));
const tables = {};
for (const locale of WWW_LOCALES) {
  const module = await import(join(i18nDir, `messages/${locale}.ts`));
  tables[locale] = Object.values(module)[0];
}

const shared = {};
for (const locale of WWW_LOCALES) {
  const module = await import(join(appRoot, `../i18n/src/locales/${locale}.ts`));
  shared[locale] = Object.values(module).find((value) => typeof value === 'object');
}

const rows = WWW_LOCALES.map((locale) => {
  const entries = [
    ...KEYS.map((key) => [key, tables[locale][key]]),
    ...SHARED_KEYS.map((key) => [key, shared[locale]?.[key]]),
  ];
  for (const [key, value] of entries) {
    if (typeof value !== 'string') {
      throw new Error(`generate-error-strings: no value for "${key}" in ${locale}`);
    }
  }
  const body = entries
    .map(([key, value]) => `    ${JSON.stringify(key)}: ${JSON.stringify(value)},`)
    .join('\n');
  return `  ${locale}: {\n${body}\n  },`;
}).join('\n');

const out = `// GENERATED FILE — do not edit.
// Source: src/i18n/messages/*.ts and @mageride/i18n's shared tables.
// Regenerate: npm run i18n:error-strings --workspace @mageride/www
//
// The four strings \`app/[locale]/error.tsx\` renders, extracted so that boundary does
// not have to import the resource tables. It is the one client module on this surface
// with no server parent to receive props from — Next instantiates it itself — and
// importing the tables for four strings costs 90 kB gzipped on every page (MCS-36 D3).
//
// \`test/i18n.test.ts\` asserts every value here still equals the table it came from.

import type { Locale } from './locales';

export type ErrorStringKey = ${[...KEYS, ...SHARED_KEYS].map((k) => JSON.stringify(k)).join(' | ')};

export const ERROR_STRINGS: Readonly<
  Partial<Record<Locale, Readonly<Record<ErrorStringKey, string>>>>
> = {
${rows}
};
`;

await writeFile(join(i18nDir, 'error-strings.ts'), out);
process.stdout.write(
  `i18n: error-strings.ts regenerated — ${KEYS.length + SHARED_KEYS.length} keys x ${WWW_LOCALES.length} locales\n`,
);
