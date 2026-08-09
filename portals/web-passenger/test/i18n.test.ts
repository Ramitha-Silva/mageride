import { describe, expect, it } from 'vitest';

import { LOCALES } from '@mageride/i18n';

import { createWebTranslator, isWebMessageKey, localeFor } from '@/i18n';
import { webEn } from '@/i18n/messages/en';
import { webSi } from '@/i18n/messages/si';
import { webTa } from '@/i18n/messages/ta';

/**
 * The trilingual rule (root CLAUDE.md, D-26) on the surface where it matters most.
 *
 * The type system already makes a missing key a compile error. What it cannot see
 * is an untranslated value that was copied from English, or a placeholder that
 * survived in one language and not another — which is what this file is for.
 *
 * There is no stored preference and no sign-in on this surface, so a reader who
 * lands in the wrong language has exactly one way out: the `?lang=` switch. That
 * makes every one of these assertions load-bearing rather than tidy.
 */

const TABLES = { si: webSi, ta: webTa, en: webEn } as const;

describe('the three tables agree', () => {
  it('has the same keys in every language', () => {
    const expected = Object.keys(webEn).sort();
    for (const locale of LOCALES) {
      expect(Object.keys(TABLES[locale]).sort(), locale).toEqual(expected);
    }
  });

  it('has a non-empty value for every key in every language', () => {
    for (const locale of LOCALES) {
      for (const [key, value] of Object.entries(TABLES[locale])) {
        expect(value.trim(), `${locale}:${key}`).not.toBe('');
      }
    }
  });

  it('carries the same placeholders in every language', () => {
    const placeholders = (value: string) =>
      [...value.matchAll(/\{(\w+)\}/g)].map((match) => match[1]).sort();

    for (const [key, english] of Object.entries(webEn)) {
      for (const locale of LOCALES) {
        expect(
          placeholders(TABLES[locale][key as keyof typeof webEn]),
          `${locale}:${key}`,
        ).toEqual(placeholders(english));
      }
    }
  });

  it('actually translates the copy rather than copying the English', () => {
    // Brand names and identifiers are legitimately identical across languages;
    // sentences are not. Anything longer than a couple of words that is
    // byte-identical to the English is an untranslated string that would otherwise
    // ship silently.
    const ALLOWED_IDENTICAL = new Set<string>([
      // The brand and its letterform.
      'web.appName',
      'web.appMark',
      // `SOS` is the international distress signal, written the same way on every
      // emergency control in the country. Transliterating it would produce a button
      // nobody recognises in the one second they have to find it.
      'web.ride.sos',
      // Two placeholders and a separator — see `isProse` below.
      'web.driver.vehicleLine',
    ]);

    // A template that is only placeholders and separators carries no language at
    // all, so identical is the correct translation of it — words are what has
    // letters in them.
    const isProse = (value: string) => /\p{L}/u.test(value.replaceAll(/\{\w+\}/g, ''));

    for (const [key, english] of Object.entries(webEn)) {
      if (ALLOWED_IDENTICAL.has(key)) continue;
      if (!isProse(english)) continue;
      if (english.split(' ').length < 3) continue;

      for (const locale of ['si', 'ta'] as const) {
        expect(TABLES[locale][key as keyof typeof webEn], `${locale}:${key}`).not.toBe(english);
      }
    }
  });
});

describe('the translator', () => {
  it('falls back to this surface’s own table before the shared one', () => {
    expect(createWebTranslator('si')('web.expired.title')).toBe(webSi['web.expired.title']);
  });

  it('resolves the shared @mageride/i18n keys too', () => {
    expect(createWebTranslator('en')('common.cancel')).toBe('Cancel');
    expect(createWebTranslator('si')('common.cancel')).not.toBe('Cancel');
  });

  it('substitutes placeholders', () => {
    expect(createWebTranslator('en')('web.pickup.title', { name: 'Ramith' })).toContain('Ramith');
  });

  it('leaves a placeholder alone when nothing was supplied for it', () => {
    // Visibly wrong beats plausibly wrong: "{name} is booking a ride for you" gets
    // reported, "undefined is booking a ride for you" reads like real copy.
    expect(createWebTranslator('en')('web.pickup.title', {})).toContain('{name}');
  });

  it('knows which keys are this surface’s', () => {
    expect(isWebMessageKey('web.expired.title')).toBe(true);
    expect(isWebMessageKey('common.cancel')).toBe(false);
  });
});

describe('which language a request renders in', () => {
  it('prefers the ?lang= switch over the browser', () => {
    expect(localeFor('ta', 'en-GB,en;q=0.9')).toBe('ta');
  });

  it('reads the first value when the parameter arrives twice', () => {
    // The language switch strips the old `?lang=` before appending its own; this is
    // the behaviour that makes a stale one harmless if it ever survives.
    expect(localeFor(['si', 'en'], null)).toBe('si');
  });

  it('negotiates Accept-Language on the primary subtag', () => {
    expect(localeFor(undefined, 'ta-LK,ta;q=0.9,en;q=0.5')).toBe('ta');
    expect(localeFor(undefined, 'en-US,en;q=0.9')).toBe('en');
  });

  it('is Sinhala-first when the browser says nothing', () => {
    // D1' §283. A Sri Lankan platform whose apps are Sinhala-first should not become
    // English-first because the surface happens to be a browser.
    expect(localeFor(undefined, null)).toBe('si');
  });

  it('ignores a language nobody supports', () => {
    expect(localeFor('fr', null)).toBe('si');
  });
});
