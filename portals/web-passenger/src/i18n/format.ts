import type { Locale } from './index';

/**
 * The three things the six screens have to format, in one place.
 *
 * **Every formatter is Colombo's**, not the server's and not the browser's. A Next
 * process runs in UTC in a container, and "handed over at 09:48" has to be 09:48
 * where the parcel was handed over — without `timeZone` it reads as 04:18, and a
 * delivery either side of midnight lands on the wrong day. This module is
 * deliberately usable from a client component too (`Intl` is in every browser),
 * because the countdown and the live tracker format on the client and must agree
 * with what the server rendered above them.
 */

const TIME_ZONE = 'Asia/Colombo';

function tag(locale: Locale): string {
  return `${locale}-LK`;
}

/** One cache per (formatter, locale), because `Intl` construction is the cost. */
function memoise<T>(build: (locale: Locale) => T): (locale: Locale) => T {
  const cache = new Map<Locale, T>();
  return (locale) => {
    const existing = cache.get(locale);
    if (existing) return existing;

    const created = build(locale);
    cache.set(locale, created);
    return created;
  };
}

const clocks = memoise(
  (locale) => new Intl.DateTimeFormat(tag(locale), { timeStyle: 'short', timeZone: TIME_ZONE }),
);

const instants = memoise(
  (locale) =>
    new Intl.DateTimeFormat(tag(locale), {
      dateStyle: 'medium',
      timeStyle: 'short',
      timeZone: TIME_ZONE,
    }),
);

const rupees = memoise((locale) => new Intl.NumberFormat(tag(locale)));

const elapsed = memoise(
  (locale) => new Intl.RelativeTimeFormat(tag(locale), { numeric: 'auto', style: 'short' }),
);

/**
 * Integer minor units → rupees, grouped, **without** the mark.
 *
 * The mark is in the resource string (`"Rs {amount}"`), because where it goes
 * relative to the number is a property of the language and not of the amount.
 * Cents are shown only when there are any, rather than printing `.00` on a fare
 * that is whole rupees — which every fare on this platform is in practice.
 */
export function formatAmountMinor(locale: Locale, minor: number): string {
  return minor % 100 === 0
    ? rupees(locale).format(minor / 100)
    : new Intl.NumberFormat(tag(locale), { minimumFractionDigits: 2 }).format(minor / 100);
}

/** An ISO instant as a Colombo wall clock — the wireframe's "09:48". */
export function formatClock(locale: Locale, iso: string | undefined | null): string | null {
  const instant = parse(iso);
  return instant ? clocks(locale).format(instant) : null;
}

/** An ISO instant as day and time in Colombo — the receipt's "09:48, 17 Jun". */
export function formatInstant(locale: Locale, iso: string | undefined | null): string | null {
  const instant = parse(iso);
  return instant ? instants(locale).format(instant) : null;
}

/**
 * How long ago a fix was reported — "3s ago", "18 min ago".
 *
 * The largest unit that fits is used. A future instant is not special-cased:
 * `RelativeTimeFormat` renders it as "in 3 seconds", which is the honest reading
 * of a device whose clock is ahead of the platform's.
 */
export function formatSince(locale: Locale, iso: string | undefined | null, now = Date.now()): string | null {
  const instant = parse(iso);
  if (!instant) return null;

  const seconds = Math.round((instant.getTime() - now) / 1000);
  const format = elapsed(locale);

  const magnitude = Math.abs(seconds);
  if (magnitude < 60) return format.format(seconds, 'second');
  if (magnitude < 3600) return format.format(Math.round(seconds / 60), 'minute');
  if (magnitude < 86_400) return format.format(Math.round(seconds / 3600), 'hour');
  return format.format(Math.round(seconds / 86_400), 'day');
}

/**
 * SCR-WT-003's countdown — `m:ss`, the wireframe's "4:38".
 *
 * Digits and a colon carry no language, so this is not a resource string; the
 * sentence it sits inside (`web.pickup.expiresIn`) is.
 */
export function formatCountdown(totalSeconds: number): string {
  const clamped = Math.max(0, Math.floor(totalSeconds));
  const minutes = Math.floor(clamped / 60);
  const seconds = clamped % 60;

  return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}

function parse(iso: string | undefined | null): Date | null {
  if (!iso) return null;
  const instant = new Date(iso);
  return Number.isNaN(instant.getTime()) ? null : instant;
}
