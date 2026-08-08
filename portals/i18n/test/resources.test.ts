/**
 * The trilingual rule, checked at runtime as well as at compile time.
 *
 * `Messages` already makes an incomplete locale a type error, so most of this
 * would fail `tsc` before it failed here. What the type system cannot catch is
 * an *empty* string, or a translation left as the English original — both
 * compile, and both ship a language a user cannot read.
 */

import { describe, expect, it } from 'vitest';

import {
  DEFAULT_LOCALE,
  FALLBACK_LOCALE,
  LOCALES,
  createTranslator,
  en,
  isLocale,
  messagesFor,
  negotiateLocale,
  si,
  ta,
} from '../src/index.js';

const KEYS = Object.keys(en).sort();

describe('resource completeness', () => {
  it('supports exactly si, ta and en, Sinhala first', () => {
    expect(LOCALES).toEqual(['si', 'ta', 'en']);
    expect(DEFAULT_LOCALE).toBe('si');
    expect(FALLBACK_LOCALE).toBe('en');
  });

  it.each(LOCALES)('%s carries every key', (locale) => {
    expect(Object.keys(messagesFor(locale)).sort()).toEqual(KEYS);
  });

  it.each(LOCALES)('%s has no empty or whitespace-only value', (locale) => {
    for (const [key, value] of Object.entries(messagesFor(locale))) {
      expect(value.trim(), `${locale}.${key}`).not.toBe('');
    }
  });

  it('actually translates — si and ta are not copies of en', () => {
    // A handful of keys are the same in all three on purpose: the product name,
    // and the language names, which are always written in the language they
    // name. Everything else must differ.
    const intentionallyShared = new Set(['common.appName', 'language.si', 'language.ta', 'language.en']);

    for (const key of KEYS as (keyof typeof en)[]) {
      if (intentionallyShared.has(key)) continue;
      expect(si[key], `si.${key} is still the English string`).not.toBe(en[key]);
      expect(ta[key], `ta.${key} is still the English string`).not.toBe(en[key]);
      expect(si[key], `si.${key} and ta.${key} are identical`).not.toBe(ta[key]);
    }
  });

  it('writes Sinhala in Sinhala script and Tamil in Tamil script', () => {
    const sinhala = /\p{Script=Sinhala}/u;
    const tamil = /\p{Script=Tamil}/u;
    const intentionallyLatin = new Set(['common.appName', 'language.en']);

    for (const key of KEYS as (keyof typeof en)[]) {
      if (intentionallyLatin.has(key)) continue;
      if (key !== 'language.ta') expect(sinhala.test(si[key]), `si.${key}`).toBe(true);
      if (key !== 'language.si') expect(tamil.test(ta[key]), `ta.${key}`).toBe(true);
    }
  });

  it('keeps every placeholder in every language', () => {
    const placeholders = (value: string) => (value.match(/\{(\w+)\}/g) ?? []).sort();
    for (const key of KEYS as (keyof typeof en)[]) {
      expect(placeholders(si[key]), `si.${key}`).toEqual(placeholders(en[key]));
      expect(placeholders(ta[key]), `ta.${key}`).toEqual(placeholders(en[key]));
    }
  });
});

describe('createTranslator', () => {
  it('returns the string for the requested locale', () => {
    expect(createTranslator('si')('common.cancel')).toBe(si['common.cancel']);
    expect(createTranslator('ta')('common.cancel')).toBe(ta['common.cancel']);
    expect(createTranslator('en')('common.cancel')).toBe('Cancel');
  });

  it('defaults to Sinhala', () => {
    expect(createTranslator()('common.save')).toBe(si['common.save']);
  });

  it('substitutes placeholders and formats numbers', () => {
    expect(createTranslator('en')('table.rowsSelected', { count: 1250 })).toBe('1,250 selected');
  });

  it('leaves a placeholder alone when no value is supplied', () => {
    // "{count} selected" reaching a user is a visible bug someone reports;
    // "undefined selected" reads like real copy and ships.
    expect(createTranslator('en')('table.rowsSelected', {})).toBe('{count} selected');
  });

  it('falls back to English for a key that arrives at runtime and is untranslated', () => {
    // Cannot happen for the compiled resources — `Messages` forbids it — but a
    // string can also come from a server payload, and a missing translation must
    // degrade to readable text rather than to the raw key.
    const sparse = { ...si, 'common.retry': '' } as typeof si;
    expect(sparse['common.retry']).toBe('');
    expect(createTranslator('en')('common.retry')).toBe('Retry');
  });
});

describe('negotiateLocale', () => {
  it.each([
    ['si-LK,si;q=0.9,en;q=0.8', 'si'],
    ['ta-LK,ta;q=0.9', 'ta'],
    ['en-GB,en;q=0.9', 'en'],
    ['fr-FR,fr;q=0.9', 'si'],
    ['en;q=0.4,ta;q=0.9', 'ta'],
    ['', 'si'],
    [null, 'si'],
  ])('%s → %s', (header, expected) => {
    expect(negotiateLocale(header)).toBe(expected);
  });

  it('ignores a language the reader explicitly refused', () => {
    expect(negotiateLocale('ta;q=0, en;q=0.5')).toBe('en');
  });
});

describe('isLocale', () => {
  it.each([
    ['si', true],
    ['ta', true],
    ['en', true],
    ['SI', false],
    ['si-LK', false],
    [undefined, false],
    [7, false],
  ])('%s → %s', (value, expected) => {
    expect(isLocale(value)).toBe(expected);
  });
});
