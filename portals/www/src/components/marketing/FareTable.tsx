import {
  DAILY_FEE_TIERS,
  MODE_A_FEE_KEY,
  MODE_B_FEE_KEY,
} from '@/content/marketing';
import { HOME } from '@/content/pages';
import { VALUES } from '@/content/vision';
import { createWwwTranslator, type Locale } from '@/i18n';

/**
 * Section 5 — the zero-commission band, and the six daily-fee tiers beneath it.
 *
 * ## Not one rupee figure is written here
 *
 * Every number comes from `DAILY_FEE_TIERS` in `src/content/marketing.ts`, **in
 * minor units**, formatted at the point of display. That is the Universal Rule
 * ("all currency values stored and transmitted as integers") applied to a marketing
 * page, and it is what makes the table checkable: `test/content.test.ts` (S20)
 * asserts the six values against URD §1 directly, so this table cannot drift from
 * the spec without a red build. A figure typed into a message string would become
 * three figures once `si.ts` and `ta.ts` exist, in a file no test reads.
 *
 * `Intl.NumberFormat` with `style: 'currency'` and `LKR`, so a Sinhala reader gets
 * Sinhala digit grouping and the currency mark their locale puts where it belongs —
 * rather than a hand-built `"Rs " + n / 100` that is wrong in at least one language.
 *
 * ## The rule is stated the way URD §1 states it
 *
 * Four claims, and the table is only the third of them. Stating the tiers without
 * the other three would make the site say "drivers pay this much" when the spec
 * says "drivers keep everything and pay this much once a day from the second trip":
 *
 *   1. **Zero commission**, and **passengers pay drivers directly**.
 *   2. **The first trip of the day is always free.**
 *   3. **A flat daily fee for Mode C**, by vehicle type — the table.
 *   4. **Monthly for Mode B, nothing at all for Mode A.**
 *
 * (1) and (2) are three `VALUES` entries S07 wrote with their anchors; (4) is
 * `MODE_A_FEE_KEY` / `MODE_B_FEE_KEY`, which exist as separate keys precisely so
 * that the table is never read as "what everybody pays". Mode B's figure stays
 * "about Rs 300" because the URD says *approximately* in both places it appears,
 * and an approximate price rendered as a precise one is a false claim with a
 * decimal point on it.
 */
/**
 * The three `VALUES` this band states above the table, in URD §1's own order.
 *
 * **Looked up by id and asserted, not filtered.** The first version of this used
 * `VALUES.filter(v => IDS.includes(v.id))` with the ids written in camelCase — and
 * `vision.ts` spells them in kebab-case, so it matched nothing, rendered an empty
 * list, and the band shipped showing a fee table with **none of the claims that
 * make the fee table honest**. It looked fine; it was the section deleting its own
 * point.
 *
 * A `find` that throws turns that into a build failure. This is a server component
 * rendered at build time, so a missing id cannot reach a reader — which is the
 * right severity for "the page is about to publish a price with no context".
 */
const FEE_VALUE_IDS = ['zero-commission', 'passengers-pay-nothing', 'first-trip-free'] as const;

function feeValues() {
  return FEE_VALUE_IDS.map((id) => {
    const value = VALUES.find((candidate) => candidate.id === id);
    if (!value) {
      throw new Error(
        `FareTable: no VALUES entry with id "${id}" — the zero-commission band cannot ` +
          'state its claims. Check src/content/vision.ts.',
      );
    }
    return value;
  });
}

export function FareTable({ locale }: { readonly locale: Locale }) {
  const t = createWwwTranslator(locale);

  /*
   * `currencyDisplay: 'narrowSymbol'`, and it is not a cosmetic preference.
   *
   * The default (`'symbol'`) renders **"LKR 50"** in English — while this site's own
   * prose, three sections away, says "about **Rs** 300". One page calling the same
   * currency two things is the kind of inconsistency a reader reads as carelessness
   * about the numbers themselves, on the table where the numbers are the point.
   *
   * Measured across the three locales: `narrowSymbol` gives **"Rs 50"** in English
   * and Tamil — exactly what the copy says — and **"රු. 50"** in Sinhala, which is
   * that reader's own mark rather than a Latin abbreviation. `'symbol'` would have
   * given "LKR" in English and "රු." in Sinhala; `'code'` gives "LKR" everywhere.
   *
   * `maximumFractionDigits: 0` because every tier is a whole number of rupees and
   * "Rs 50.00" on a fee table implies a precision the URD does not state.
   */
  const money = new Intl.NumberFormat(`${locale}-LK`, {
    style: 'currency',
    currency: 'LKR',
    currencyDisplay: 'narrowSymbol',
    maximumFractionDigits: 0,
  });

  const headline = feeValues();

  return (
    <section className="bg-surface py-section">
      <div className="mx-auto max-w-[1200px] px-4">
        <h2 className="font-display text-hero-sm text-on-surface">{t(HOME.values.heading)}</h2>
        <p className="mt-md max-w-[62ch] text-body text-on-surface-variant">
          {t(HOME.values.body)}
        </p>

        {/* Claims (1) and (2) — the three the table would otherwise be read without. */}
        <ul className="mt-lg grid gap-md sm:grid-cols-3">
          {headline.map((value) => (
            <li key={value.id} className="rounded-card border border-outline-variant p-lg">
              <h3 className="font-display text-title text-on-surface">{t(value.title)}</h3>
              <p className="mt-xs text-body-sm text-on-surface-variant">{t(value.body)}</p>
            </li>
          ))}
        </ul>

        {/* Claim (3) — the six Mode C tiers. */}
        <h3 className="mt-section font-display text-title text-on-surface">
          {t('www.page.drivers.feeTableHeading')}
        </h3>

        <div className="mt-md overflow-x-auto">
          <table className="w-full min-w-[20rem] border-collapse text-left">
            <caption className="sr-only">{t('www.page.drivers.feeTableHeading')}</caption>
            <tbody>
              {DAILY_FEE_TIERS.map((tier) => (
                <tr key={tier.vehicleType} className="border-b border-outline-variant">
                  <th scope="row" className="py-xs pr-md text-body font-normal text-on-surface">
                    {t(tier.label)}
                  </th>
                  <td className="py-xs text-right text-body font-medium tabular-nums text-on-surface">
                    {money.format(tier.dailyFeeMinor / 100)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <p className="mt-sm max-w-[62ch] text-body-sm text-on-surface-variant">
          {t('www.page.drivers.feeTableNote')}
        </p>

        {/* Claim (4) — the two rows that are not in the table. */}
        <div className="mt-lg grid gap-md sm:grid-cols-2">
          <p className="rounded-card bg-surface-variant p-lg text-body-sm text-on-surface-variant">
            {t(MODE_A_FEE_KEY)}
          </p>
          <p className="rounded-card bg-surface-variant p-lg text-body-sm text-on-surface-variant">
            {t(MODE_B_FEE_KEY)}
          </p>
        </div>
      </div>
    </section>
  );
}
