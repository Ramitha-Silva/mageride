import { LANGUAGE_BAND } from '@/content/marketing';
import { createWwwTranslator, type Locale } from '@/i18n';

/**
 * Section 8 — one sentence, in all three languages, on one card.
 *
 * ## The per-element `lang` is the whole point, not decoration
 *
 * Every other string on this site resolves to the reader's own language. These
 * three do not: they are three fixed strings side by side, because the claim being
 * made is not *what* the sentence says but that the platform speaks all three — and
 * a reader who only ever sees their own language cannot see that.
 *
 * `lang` on each `<p>` is a **WCAG 3.1.2 (Language of Parts)** requirement, not a
 * nicety: without it a screen reader pronounces the Tamil line with the page
 * language's phonetics and produces sounds that are not words. It is also what the
 * `[lang='si-LK']` / `[lang='ta-LK']` rules in `app/globals.css` key on, so it is
 * what gets each line its own script face — S14 found that without those rules two
 * of the three lines rendered in a system fallback on every page.
 *
 * ## It goes through the translator, and that is not the bug it looks like
 *
 * `t('www.languageBand.ta')` on a Sinhala page returns `si.ts`'s value for that key
 * — which is the **Tamil** sentence, because these three keys hold byte-identical
 * values in all three tables. That is deliberate and enforced: they are three of the
 * six entries in `IDENTICAL_BY_DESIGN` in `test/i18n.test.ts`, which asserts *exact*
 * equality, so a seventh key going identical fails the suite and one of these
 * quietly being translated fails it too.
 *
 * So the translator returns the right three strings whichever locale is reading,
 * and the alternative — importing `wwwEn` and indexing it directly — would only
 * duplicate the guarantee the test already makes.
 *
 * ## It shows all three even though this site publishes two
 *
 * S15 says so explicitly, and the reasoning holds: **the card is about the apps**,
 * which are trilingual today on all four product surfaces. What MCS-34 D2 defers is
 * *this website's* Tamil (S13). Different claim, both true.
 *
 * ## Moved here from the footer
 *
 * S14 put this band in `Footer`, before S15's section list was in play. Rendering it
 * in both places would put the same three sentences on the home page twice, so the
 * footer keeps the links and the rights line and the band lives here, where the plan
 * puts it. The cost is that it now appears on the home page only — recorded in the
 * S15 handoff, because "the site says it is trilingual on one page" is a weaker
 * claim than the footer made and is worth someone deciding on rather than
 * discovering.
 */
export function LanguageBand({ locale }: { readonly locale: Locale }) {
  const t = createWwwTranslator(locale);

  return (
    <section className="mx-auto max-w-[1200px] px-4 py-section">
      <div className="flex flex-col gap-sm rounded-card border border-outline-variant bg-surface p-lg sm:p-section">
        {LANGUAGE_BAND.map((line) => (
          <p
            key={line.lang}
            lang={line.lang}
            className="font-display text-title text-balance text-on-surface"
          >
            {t(line.key)}
          </p>
        ))}
      </div>
    </section>
  );
}
