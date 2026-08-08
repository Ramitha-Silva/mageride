import { describe, expect, it } from 'vitest';

import { LOCALES } from '@mageride/i18n';

import { createAdminTranslator, isAdminMessageKey, translateServerKey } from '@/i18n';
import { adminEn } from '@/i18n/messages/en';
import { adminSi } from '@/i18n/messages/si';
import { adminTa } from '@/i18n/messages/ta';

import { adminMenuManifest } from './support/urd';

/**
 * The trilingual rule (root CLAUDE.md, D-26) and the one contract the nav has
 * with admin-bff: every `labelKey` it can send resolves, in all three languages.
 *
 * The type system already makes a missing key a compile error. What it cannot see
 * is a key the *server* names, an untranslated value that was copied from English,
 * or a placeholder that survived in one language and not another — which is what
 * this file is for.
 */

const TABLES = { si: adminSi, ta: adminTa, en: adminEn } as const;

describe('the three tables agree', () => {
  it('has the same keys in every language', () => {
    const expected = Object.keys(adminEn).sort();
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
    const placeholders = (value: string) => [...value.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();

    for (const [key, english] of Object.entries(adminEn)) {
      for (const locale of LOCALES) {
        expect(placeholders(TABLES[locale][key as keyof typeof adminEn]), `${locale}:${key}`).toEqual(
          placeholders(english),
        );
      }
    }
  });

  it('actually translates the copy rather than copying the English', () => {
    // Brand names and identifiers are legitimately identical across languages;
    // sentences are not. Anything longer than a couple of words that is
    // byte-identical to the English is an untranslated string that would
    // otherwise ship silently.
    const ALLOWED_IDENTICAL = new Set(['admin.error.reference', 'admin.screen.servedBy']);

    for (const [key, english] of Object.entries(adminEn)) {
      if (ALLOWED_IDENTICAL.has(key)) continue;
      if (english.split(' ').length < 3) continue;

      for (const locale of ['si', 'ta'] as const) {
        expect(TABLES[locale][key as keyof typeof adminEn], `${locale}:${key}`).not.toBe(english);
      }
    }
  });
});

describe('the nav contract with admin-bff', () => {
  const manifest = adminMenuManifest();

  it('resolves every group and item labelKey the server can send', () => {
    const keys = [
      ...manifest.map((group) => group.labelKey),
      ...manifest.flatMap((group) => group.items.map((item) => item.labelKey)),
    ];

    expect(keys.length).toBeGreaterThan(0);

    for (const key of keys) {
      expect(isAdminMessageKey(key), `${key} is not an Admin Portal resource key`).toBe(true);

      for (const locale of LOCALES) {
        const t = createAdminTranslator(locale);
        // Resolving to the key itself is the fallback, and it means the sidebar
        // would render `nav.somethingNew` at an operator.
        expect(translateServerKey(t, key), `${locale}:${key}`).not.toBe(key);
      }
    }
  });
});

describe('the translator', () => {
  it('falls back to English for a key a locale is somehow missing', () => {
    const t = createAdminTranslator('si');
    expect(t('admin.appName')).toBe(adminSi['admin.appName']);
  });

  it('resolves the shared @mageride/i18n keys too', () => {
    expect(createAdminTranslator('en')('common.retry')).toBe('Retry');
    expect(createAdminTranslator('si')('common.retry')).not.toBe('Retry');
  });

  it('substitutes placeholders and formats numbers for the locale', () => {
    const t = createAdminTranslator('en');
    expect(t('admin.error.accountLockedFor', { minutes: 12 })).toContain('12');
    expect(t('admin.screen.servedBy', { service: 'transit-svc' })).toContain('transit-svc');
  });

  it('leaves a placeholder alone when nothing was supplied for it', () => {
    // Visibly wrong beats plausibly wrong: "{minutes} minutes" gets reported,
    // "undefined minutes" reads like real copy.
    const t = createAdminTranslator('en');
    expect(t('admin.error.accountLockedFor', {})).toContain('{minutes}');
  });

  it('hands back an unknown server key unchanged rather than rendering nothing', () => {
    const t = createAdminTranslator('en');
    expect(translateServerKey(t, 'nav.somethingTheServerAddedFirst')).toBe(
      'nav.somethingTheServerAddedFirst',
    );
  });
});
