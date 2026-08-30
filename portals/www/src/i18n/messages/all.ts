import type { Locale } from '@mageride/i18n';

import { wwwEn, type WwwMessages } from './en';
import { wwwSi } from './si';
import { wwwTa } from './ta';

/**
 * **Every locale's table, including the ones this surface does not publish — and
 * this module exists so that `src/i18n/index.ts` does not have to.**
 *
 * ## What it is for
 *
 * `ta.ts` is complete, type-checked and deliberately not rendered: MCS-34 **D2**
 * defers Tamil, `WWW_LOCALES` is `['si', 'en']`, and `/ta/anything` answers 404.
 * The tests that keep it complete, and `scripts/check-i18n-parity.mjs`'s TODO
 * count, both need to *read* it. Nothing that renders does.
 *
 * ## Why the separation is worth a file
 *
 * S19 measured the client bundle and found **all three tables in it** — 133 kB
 * gzipped of the 320 kB a browser downloads for `/`, of which roughly a third is
 * Tamil that no URL renders. The cause was ordinary and invisible: eleven client
 * components import `@/i18n` for `createWwwTranslator`, that module statically
 * imported all three tables, and a static import is a graph edge whether or not the
 * value is ever read. Nothing in lint, `tsc` or the tests could see it, because
 * nothing was wrong with the code.
 *
 * So the total lookup moved here, and `src/i18n/index.ts` now imports **only the
 * published tables**. The invariant that buys is worth stating plainly:
 *
 * > A locale's table reaches a browser **if and only if** the locale is published.
 *
 * Before this it was: every table reaches every browser, always. Re-enabling Tamil
 * is still a single edit and it is now the edit that also ships the bytes, rather
 * than the bytes having shipped all along.
 *
 * ## The rule
 *
 * **Nothing under `app/` or `src/components/` may import this module.** Doing so
 * puts every unpublished table back into the client graph and undoes the whole
 * saving in one line — which is precisely how it got there the first time, so
 * `test/fences.test.ts` asserts it rather than trusting this paragraph.
 */
export const WWW_MESSAGES_BY_LOCALE: Readonly<Record<Locale, WwwMessages>> = {
  si: wwwSi,
  ta: wwwTa,
  en: wwwEn,
};

/**
 * The resource table for any locale, published or not.
 *
 * Total over `Locale` on purpose: its callers are the ones asking about the
 * deferral itself — "is `ta.ts` still complete?", "how many markers are left?" — and
 * a lookup that could only answer for published locales could not answer those.
 * Anything that *renders* wants {@link createWwwTranslator} instead, which is
 * published-only by construction.
 */
export function wwwMessagesFor(locale: Locale): WwwMessages {
  return WWW_MESSAGES_BY_LOCALE[locale];
}
