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

/**
 * An **instant**, in the timezone the platform runs its day in.
 *
 * `submittedAt` is a `Timestamp`, not a `BusinessDate`, so this is the opposite
 * of the rule above: it is a moment, and the moment an officer means is the one
 * on a Sri Lankan clock (D-38). Pinning the zone rather than taking the
 * container's is what keeps "17 Jun 08:40" the same string on the operator's
 * screen wherever the process happens to be scheduled.
 *
 * No year: SCR-AP-003 lists a working queue, where the useful comparison is
 * against this morning.
 */
const dateTimes = memoise(
  (locale) =>
    new Intl.DateTimeFormat(tag(locale), {
      day: 'numeric',
      month: 'short',
      hour: '2-digit',
      minute: '2-digit',
      hour12: false,
      timeZone: 'Asia/Colombo',
    }),
);

/**
 * An OCR confidence — `0.98` from the wire, as SCR-AP-003a prints it.
 *
 * Two decimals, always, so a column of scores stays a column: `0.6` beside
 * `0.71` reads as the larger number at a glance.
 */
const confidences = memoise(
  (locale) =>
    new Intl.NumberFormat(tag(locale), { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
);

/**
 * Money to the minor unit, and **signed when it is not zero** (Δ C108).
 *
 * SCR-AP-006 is the screen the rounding above is wrong for. A reconciliation
 * variance of two cents is still a variance — "zero is what reconciled means", in
 * the contract's own words — and `formatMinorUnits`' whole-rupee rounding would
 * print it as `0` beside a pill saying the rails disagree. So the finance surface
 * formats exactly, and the two formatters exist side by side because a KPI card
 * and a ledger are asking different questions of the same integer.
 *
 * `exceptZero` is the sign rule: a variance of zero reads `0.00` rather than
 * `+0.00`, and every non-zero one carries the direction it went.
 */
const exactMoney = memoise(
  (locale) =>
    new Intl.NumberFormat(tag(locale), { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
);

const signedMoney = memoise(
  (locale) =>
    new Intl.NumberFormat(tag(locale), {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
      signDisplay: 'exceptZero',
    }),
);

/**
 * A commission rate, from the basis points the ledger keeps it in.
 *
 * Two fraction digits rather than `percents`' one: a voucher tier is set to the
 * basis point and `1875` is 18.75 %, which the KPI formatter would round to
 * 18.8 % — a rate that is not the rate anybody configured.
 */
const basisPoints = memoise(
  (locale) =>
    new Intl.NumberFormat(tag(locale), {
      style: 'percent',
      maximumFractionDigits: 2,
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

/**
 * An ISO instant as day, month and time in Asia/Colombo.
 *
 * A value that is not a timestamp comes back `null` rather than "Invalid Date":
 * the field is optional on `VehicleQueueRow`, and a cell that says nothing is
 * better than a cell that says something wrong.
 */
export function formatDateTime(locale: Locale, iso: string | undefined | null): string | null {
  if (!iso) return null;

  const instant = new Date(iso);
  if (Number.isNaN(instant.getTime())) return null;

  return dateTimes(locale).format(instant);
}

/** A `0`–`1` OCR confidence, or `null` when the field carries none. */
export function formatConfidence(locale: Locale, value: number | undefined | null): string | null {
  if (typeof value !== 'number' || !Number.isFinite(value)) return null;
  return confidences(locale).format(value);
}

/**
 * Integer minor units → rupees and cents, **without** the rupee mark.
 *
 * The finance surface's formatter; see the note beside it. Division by 100 is a
 * unit conversion of an integer, not arithmetic on money — nothing is added,
 * multiplied or rounded, so the figure printed is the figure the ledger holds.
 */
export function formatMoneyMinor(locale: Locale, minor: number): string {
  return exactMoney(locale).format(minor / 100);
}

/** The same, signed unless it is exactly zero — a reconciliation variance. */
export function formatSignedMoneyMinor(locale: Locale, minor: number): string {
  return signedMoney(locale).format(minor / 100);
}

/** One `BusinessDate`, `2026-06-17` → the operator's own rendering of that day. */
export function formatBusinessDate(locale: Locale, value: string | undefined | null): string | null {
  if (!value) return null;

  const day = new Date(`${value}T00:00:00Z`);
  if (Number.isNaN(day.getTime())) return null;

  return dates(locale).format(day);
}

/**
 * A basis-point rate as the percentage an operator set — `1250` → `12.5%`.
 *
 * Basis points are the wire unit because a percentage of money must not be a
 * float (CLAUDE.md); the division here converts a unit for display and the
 * integer is what is sent back.
 */
export function formatBasisPoints(locale: Locale, bps: number): string {
  return basisPoints(locale).format(bps / 10_000);
}
