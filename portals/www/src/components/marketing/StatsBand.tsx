import { StatCounter } from '@/components/motion/StatCounter';
import { STATS } from '@/content/marketing';
import { createWwwTranslator, type Locale } from '@/i18n';

/**
 * Section 7 — the four numbers.
 *
 * ## S15 says "11 vehicle types". The constant says **10**, and the constant is right
 *
 * `STATS` in `src/content/marketing.ts` carries `vehicleTypes: 10` with S07's
 * reasoning beside it, and this session did not override it: the authoritative
 * enumeration is URD §1.B and the backend's `Registry.Api/Domain/VehicleTypes.cs`,
 * and **both list ten** — motorbike, three_wheeler, flex, sedan, mini_van, van,
 * truck, mini_truck, bus, train. Eleven is the count of *map marker colours*, whose
 * eleventh token `veh-private` says in its own comment that it is "a Mode B
 * **display** token, not a vehicle type": a private vehicle is a sedan or a van
 * drawn grey because of its mode.
 *
 * Publishing "11 vehicle types" would invent one a reader could go looking for, on
 * a page whose numbers are supposed to be checkable. The brief inherited the figure
 * from the plan; S07 corrected it with an anchor, and README rule 7 makes the
 * anchored constant the thing that renders. **Recorded in the S15 handoff** so the
 * discrepancy is visible rather than silently resolved.
 *
 * ## The numbers are real with JavaScript off
 *
 * `StatCounter` server-renders its value into an inline `--mr-www-count`, so the
 * visible digits are "10" and "3" in the HTML rather than "0" waiting for an
 * effect (S15 §7). Under reduced motion the component never calls `animate()` and
 * simply settles on the value. Both were verified in a browser, not assumed.
 *
 * The suffix is a separate key so it can localise — `%` is a symbol, but where it
 * sits relative to the digits is not universal.
 */
export function StatsBand({ locale }: { readonly locale: Locale }) {
  // A server component, so it holds the translator and `StatCounter` — which is a
  // client island for the WAAPI count-up — receives the two strings resolved.
  const t = createWwwTranslator(locale);

  return (
    <section className="bg-surface py-section">
      <ul className="mx-auto grid max-w-[1200px] gap-lg px-4 sm:grid-cols-2 lg:grid-cols-4">
        {STATS.map((stat) => (
          <li key={stat.id}>
            <StatCounter
              locale={locale}
              value={stat.value}
              label={t(stat.label)}
              suffix={stat.suffix ? t(stat.suffix) : undefined}
            />
          </li>
        ))}
      </ul>
    </section>
  );
}
