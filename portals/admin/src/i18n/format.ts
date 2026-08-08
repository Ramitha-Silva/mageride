/**
 * Numbers, percentages, money and date ranges, formatted for the operator's
 * language.
 *
 * These are not resource strings and must not become them: `48,210` and
 * `1 – 28 Jun 2026` are the *same* fact in Sinhala, Tamil and English, rendered
 * by the rules of each. `Intl` knows those rules; a translated template would be
 * three copies of a grouping convention that nobody would keep in step. What *is*
 * a resource string is the rupee mark — `admin.dashboard.money` — because "Rs" is
 * a word, and this module hands it the formatted amount.
 *
 * One formatter per locale per kind, memoised: the dashboard formats a dozen
 * figures per render and constructing an `Intl.NumberFormat` is not free.
 */

import type { Locale } from '@mageride/i18n';

/** Sri Lanka, in the operator's language — the same region tag `@mageride/i18n` uses. */
function tag(locale: Locale): string {
  return `${locale}-LK`;
}

function memoise<T>(build: (locale: Locale) => T): (locale: Locale) => T {
  const cache = new Map<Locale, T>();
  return (locale) => {
    const hit = cache.get(locale);
    if (hit) return hit;

    const made = build(locale);
    cache.set(locale, made);
    return made;
  };
}

const counts = memoise((locale) => new Intl.NumberFormat(tag(locale)));

const percents = memoise(
  (locale) =>
    new Intl.NumberFormat(tag(locale), {
      style: 'percent',
      maximumFractionDigits: 1,
      // The arrow and the sentence beside it carry the direction; a minus sign as
      // well would read as "down −9 %".
      signDisplay: 'never',
    }),
);

const dates = memoise(
  (locale) =>
    new Intl.DateTimeFormat(tag(locale), {
      day: 'numeric',
      month: 'short',
      year: 'numeric',
      // A `BusinessDate` is a calendar date, not an instant. Formatting it in the
      // container's timezone would move `2026-06-01` to the 31st of May wherever
      // the process happens to run west of UTC — and the dates are Asia/Colombo's
      // to begin with, resolved by admin-bff.
      timeZone: 'UTC',
    }),
);

/** A count, grouped for the locale. */
export function formatCount(locale: Locale, value: number): string {
  return counts(locale).format(value);
}

/**
 * A `deltaVsPrev` percentage — `9.2` from the wire becomes `9.2%`, unsigned.
 *
 * The wire carries whole percentage points and `Intl`'s percent style expects a
 * fraction, so the division here is a unit conversion rather than arithmetic on
 * the figure.
 */
export function formatPercent(locale: Locale, percentagePoints: number): string {
  return percents(locale).format(Math.abs(percentagePoints) / 100);
}

/**
 * Integer minor units → the amount an operator reads, **without** the rupee mark.
 *
 * Rounded to whole rupees: this is a KPI card, and 48 million rupees and two
 * cents is 48 million rupees. The exact minor-unit figure is in the CSV, which is
 * where a reconciliation starts — the file says so in its own preamble
 * ("money,integer minor units (LKR cents)"), so nothing is lost, only unstated on
 * a card.
 */
export function formatMinorUnits(locale: Locale, minor: number): string {
  return counts(locale).format(Math.round(minor / 100));
}

/**
 * An inclusive `from`–`to` pair as one phrase.
 *
 * `formatRange` collapses the parts the two dates share, so a range inside one
 * month reads "1 – 28 Jun 2026" rather than repeating the month and the year.
 * Both ends are parsed as UTC midnight — see the formatter's own note.
 */
export function formatDateRange(locale: Locale, from: string, to: string): string {
  return dates(locale).formatRange(new Date(`${from}T00:00:00Z`), new Date(`${to}T00:00:00Z`));
}
