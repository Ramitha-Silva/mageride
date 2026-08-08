import type { Locale } from './index';

/**
 * The four things C113's screens have to format, in one place.
 *
 * C111 and C112 formatted a date inline, once, and that was right for one date.
 * Three screens now print money, days, instants and elapsed time, and a
 * `new Intl.DateTimeFormat` built inside a table row is a formatter constructed
 * per cell — the expensive part of `Intl` is the construction, not the call.
 *
 * **Every formatter is Colombo's**, not the server's. A Next process runs in UTC
 * in a container and the operator is in Sri Lanka: without `timeZone` a departure
 * at 06:00 reads as 00:30, and a "last seen" stamp lands on the wrong day either
 * side of midnight.
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

const days = memoise(
  (locale) => new Intl.DateTimeFormat(tag(locale), { dateStyle: 'medium', timeZone: TIME_ZONE }),
);

const instants = memoise(
  (locale) =>
    new Intl.DateTimeFormat(tag(locale), {
      dateStyle: 'medium',
      timeStyle: 'short',
      timeZone: TIME_ZONE,
    }),
);

/**
 * Δ C115. A `billing.fleet_invoices.period_month` is always the first of a Colombo
 * month, and SCR-FP-010 heads a card with "Monthly invoice — June 2026". Printing
 * the day would say the invoice is about the 1st, which is the one day of the
 * month it is *not* about.
 */
const months = memoise(
  (locale) =>
    new Intl.DateTimeFormat(tag(locale), { year: 'numeric', month: 'long', timeZone: TIME_ZONE }),
);

const rupees = memoise((locale) => new Intl.NumberFormat(tag(locale)));

const elapsed = memoise(
  (locale) => new Intl.RelativeTimeFormat(tag(locale), { numeric: 'auto', style: 'short' }),
);

/**
 * Integer minor units → rupees, grouped, **without** the mark.
 *
 * The mark is in the resource string (`"Rs {fare}/mo"`), because where it goes
 * relative to the number is a property of the language and not of the amount.
 * A fare is whole rupees in practice — `registry.vehicles.default_monthly_fare_minor`
 * holds cents and nobody sets a subscription to 6,000.50 — so the cents are
 * shown only when there are any, rather than printing `.00` on every row.
 */
export function formatFareMinor(locale: Locale, minor: number): string {
  const format = rupees(locale);
  return minor % 100 === 0
    ? format.format(minor / 100)
    : new Intl.NumberFormat(tag(locale), { minimumFractionDigits: 2 }).format(minor / 100);
}

/** An ISO instant as a dated day in Colombo, or `null` if it is not one. */
export function formatDay(locale: Locale, iso: string | undefined | null): string | null {
  const instant = parse(iso);
  return instant ? days(locale).format(instant) : null;
}

/**
 * A `BusinessDate` as the month it names — "June 2026". Δ C115.
 *
 * Takes the bare `yyyy-MM-dd` an invoice's `periodMonth` is, and reads it at
 * midnight UTC: 05:30 in Colombo on the same day, so the month never slips.
 */
export function formatMonth(locale: Locale, businessDate: string | undefined | null): string | null {
  const instant = parse(businessDate ? `${businessDate}T00:00:00Z` : null);
  return instant ? months(locale).format(instant) : null;
}

/** An ISO instant as day and time in Colombo, or `null` if it is not one. */
export function formatInstant(locale: Locale, iso: string | undefined | null): string | null {
  const instant = parse(iso);
  return instant ? instants(locale).format(instant) : null;
}

/**
 * How long ago something happened — `web_fleet.html`'s "3s ago", "42s ago",
 * "18 min ago".
 *
 * The largest unit that fits is used, so a tracker silent for a day reads "1 day
 * ago" rather than "86,400 seconds ago". A future instant is not special-cased:
 * `RelativeTimeFormat` renders it as "in 3 seconds", which is the honest reading
 * of a device whose clock is ahead of the platform's.
 */
export function formatSince(locale: Locale, iso: string | undefined | null): string | null {
  const instant = parse(iso);
  if (!instant) return null;

  const seconds = Math.round((instant.getTime() - Date.now()) / 1000);
  const format = elapsed(locale);

  const magnitude = Math.abs(seconds);
  if (magnitude < 60) return format.format(seconds, 'second');
  if (magnitude < 3600) return format.format(Math.round(seconds / 60), 'minute');
  if (magnitude < 86_400) return format.format(Math.round(seconds / 3600), 'hour');
  return format.format(Math.round(seconds / 86_400), 'day');
}

function parse(iso: string | undefined | null): Date | null {
  if (!iso) return null;
  const instant = new Date(iso);
  return Number.isNaN(instant.getTime()) ? null : instant;
}
