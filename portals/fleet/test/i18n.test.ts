import { describe, expect, it } from 'vitest';

import { LOCALES } from '@mageride/i18n';

import { createFleetTranslator, isFleetMessageKey } from '@/i18n';
import { fleetEn } from '@/i18n/messages/en';
import { fleetSi } from '@/i18n/messages/si';
import { fleetTa } from '@/i18n/messages/ta';
import { FLEET_NAV, FLEET_SCREENS } from '@/server/routes';

/**
 * The trilingual rule (root CLAUDE.md, D-26) and the nav's contract with the
 * manifest: every label a screen or group names resolves, in all three languages.
 *
 * The type system already makes a missing key a compile error. What it cannot see
 * is an untranslated value that was copied from English, or a placeholder that
 * survived in one language and not another — which is what this file is for.
 */

const TABLES = { si: fleetSi, ta: fleetTa, en: fleetEn } as const;

describe('the three tables agree', () => {
  it('has the same keys in every language', () => {
    const expected = Object.keys(fleetEn).sort();
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

    for (const [key, english] of Object.entries(fleetEn)) {
      for (const locale of LOCALES) {
        expect(
          placeholders(TABLES[locale][key as keyof typeof fleetEn]),
          `${locale}:${key}`,
        ).toEqual(placeholders(english));
      }
    }
  });

  it('actually translates the copy rather than copying the English', () => {
    // Brand names and identifiers are legitimately identical across languages;
    // sentences are not. Anything longer than a couple of words that is
    // byte-identical to the English is an untranslated string that would
    // otherwise ship silently.
    // Δ C113 — "IMEI / MAC" is two protocol acronyms and a slash. They are
    // written that way on the ST-901's own label and in Sinhala and Tamil
    // technical copy alike; transliterating them would produce a heading an
    // operator could not match against the sticker they are reading from.
    const ALLOWED_IDENTICAL = new Set([
      'fleet.error.reference',
      'fleet.screen.servedBy',
      'fleet.trackers.field.imei',
      'fleet.trackers.column.imei',
    ]);

    // A template that is only placeholders and separators carries no language at
    // all, so identical is the correct translation of it — words are what has
    // letters in them.
    const isProse = (value: string) => /\p{L}/u.test(value.replaceAll(/\{\w+\}/g, ''));

    for (const [key, english] of Object.entries(fleetEn)) {
      if (ALLOWED_IDENTICAL.has(key)) continue;
      if (!isProse(english)) continue;
      if (english.split(' ').length < 3) continue;

      for (const locale of ['si', 'ta'] as const) {
        expect(TABLES[locale][key as keyof typeof fleetEn], `${locale}:${key}`).not.toBe(english);
      }
    }
  });
});

describe('the nav resolves', () => {
  it('gives every group and every screen a key that exists in all three languages', () => {
    const keys = [
      ...FLEET_NAV.map((group) => group.labelKey),
      ...FLEET_SCREENS.map((screen) => screen.labelKey),
    ];

    expect(keys.length).toBeGreaterThan(0);

    for (const key of keys) {
      expect(isFleetMessageKey(key), `${key} is not a Fleet Portal resource key`).toBe(true);

      for (const locale of LOCALES) {
        const t = createFleetTranslator(locale);
        expect(t(key).trim(), `${locale}:${key}`).not.toBe('');
      }
    }
  });

  it('translates every nav label out of English in both other languages', () => {
    const english = createFleetTranslator('en');
    for (const screen of FLEET_SCREENS) {
      for (const locale of ['si', 'ta'] as const) {
        expect(createFleetTranslator(locale)(screen.labelKey), `${locale}:${screen.key}`).not.toBe(
          english(screen.labelKey),
        );
      }
    }
  });
});

describe('the translator', () => {
  it('falls back to this surface’s own table before the shared one', () => {
    const t = createFleetTranslator('si');
    expect(t('fleet.appName')).toBe(fleetSi['fleet.appName']);
  });

  it('resolves the shared @mageride/i18n keys too', () => {
    expect(createFleetTranslator('en')('common.retry')).toBe('Retry');
    expect(createFleetTranslator('si')('common.retry')).not.toBe('Retry');
  });

  it('substitutes placeholders and formats numbers for the locale', () => {
    const t = createFleetTranslator('en');
    expect(t('fleet.error.accountLockedFor', { minutes: 12 })).toContain('12');
    expect(t('fleet.screen.servedBy', { service: 'fleet-billing-svc' })).toContain(
      'fleet-billing-svc',
    );
  });

  it('leaves a placeholder alone when nothing was supplied for it', () => {
    // Visibly wrong beats plausibly wrong: "{minutes} minutes" gets reported,
    // "undefined minutes" reads like real copy.
    expect(createFleetTranslator('en')('fleet.error.accountLockedFor', {})).toContain('{minutes}');
  });
});
