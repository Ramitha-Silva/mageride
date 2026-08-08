'use server';

import { cookies } from 'next/headers';
import { revalidatePath } from 'next/cache';

import { cookiesAreSecure } from '@/config/env';
import { isLocale } from '@/i18n';

import { LOCALE_COOKIE, sessionCookieOptions, THEME_COOKIE } from './cookies';

/**
 * The two per-browser preferences the shell owns: display language and
 * appearance.
 *
 * Both are plain `<form action={…}>` submissions rather than fetch calls, so
 * neither needs a client-side data layer and neither can get out of step with
 * what the server rendered. A year, because a preference that expired with the
 * session would have to be re-chosen after every sign-out.
 *
 * **Language is stored here, not on the account.** `PUT /v1/me/prefs/language`
 * exists and is what the apps' Settings screen calls (AL-26, C027) — but that
 * column is the language MageRide *messages* this person in, and an operator who
 * switches the console to English for an afternoon has not asked for their SMS to
 * change. The account language is still read: it is the second source
 * `getLocale()` consults, so a new browser opens in the language they chose
 * somewhere else. D2 §FP gives SCR-FP-002 a language control of its own, and that
 * one is the organisation's — C112's, and a different fact from this.
 */

const ONE_YEAR_SECONDS = 365 * 24 * 60 * 60;

export async function setAppearance(formData: FormData): Promise<void> {
  const value = String(formData.get('appearance') ?? '');
  if (value !== 'light' && value !== 'dark' && value !== 'system') return;

  (await cookies()).set(
    THEME_COOKIE,
    value,
    sessionCookieOptions(cookiesAreSecure(), ONE_YEAR_SECONDS),
  );

  // The class lives on <html>, which the root layout renders — so the whole
  // layout tree has to be re-rendered, not just the page the menu was open on.
  revalidatePath('/', 'layout');
}

export async function setLocale(formData: FormData): Promise<void> {
  const value = String(formData.get('locale') ?? '');
  if (!isLocale(value)) return;

  (await cookies()).set(
    LOCALE_COOKIE,
    value,
    sessionCookieOptions(cookiesAreSecure(), ONE_YEAR_SECONDS),
  );

  revalidatePath('/', 'layout');
}
