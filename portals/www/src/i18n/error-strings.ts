// GENERATED FILE — do not edit.
// Source: src/i18n/messages/*.ts and @mageride/i18n's shared tables.
// Regenerate: npm run i18n:error-strings --workspace @mageride/www
//
// The four strings `app/[locale]/error.tsx` renders, extracted so that boundary does
// not have to import the resource tables. It is the one client module on this surface
// with no server parent to receive props from — Next instantiates it itself — and
// importing the tables for four strings costs 90 kB gzipped on every page (MCS-36 D3).
//
// `test/i18n.test.ts` asserts every value here still equals the table it came from.

import type { Locale } from './locales';

export type ErrorStringKey = "www.error.title" | "www.error.body" | "www.notFound.home" | "common.retry";

export const ERROR_STRINGS: Readonly<
  Partial<Record<Locale, Readonly<Record<ErrorStringKey, string>>>>
> = {
  si: {
    "www.error.title": "යම් දෝෂයක් සිදු විය",
    "www.error.body": "මෙම පිටුව පෙන්විය නොහැකි විය. නැවත උත්සාහ කරන්න.",
    "www.notFound.home": "මුල් පිටුවට යන්න",
    "common.retry": "නැවත උත්සාහ කරන්න",
  },
  en: {
    "www.error.title": "Something went wrong",
    "www.error.body": "This page could not be shown. Try again.",
    "www.notFound.home": "Go to the home page",
    "common.retry": "Retry",
  },
};
