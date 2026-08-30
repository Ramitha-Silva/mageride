import { FAQ_GROUPS, faqAnchorId, faqGroup, type FaqEntry } from '@/content/faq';
import { createWwwTranslator, type Locale } from '@/i18n';

/**
 * `/faq`'s accordion. **A server component with no JavaScript in it at all.**
 *
 * ## Why this is `<details>` and not a Radix disclosure
 *
 * S18 says *"`@mageride/ui`'s `Tabs`/disclosure primitives are Radix-backed; use
 * them"*, and there is no disclosure primitive in `@mageride/ui` to use —
 * `src/index.ts` exports Button, Field, Chip, StatusPill, Table, Modal, Toast, Tabs
 * and Dropzone. Adding one would mean a component in the **shared** package, built
 * on `radix-ui`'s Accordion, for one page on one surface.
 *
 * That was worth doing only if it satisfied the harder half of the same brief, and
 * it does not:
 *
 *   - **"All answers must be in the DOM whether open or closed."** Radix's
 *     `Accordion.Content` unmounts when closed. `forceMount` keeps it mounted, so a
 *     crawler would see it — that clause is satisfiable.
 *   - **"a JS-off reader need them."** This one is not. A Radix accordion without
 *     JavaScript is a column of buttons that do nothing above answers CSS has set to
 *     `display: none`. Every answer on the page becomes unreachable, on the surface
 *     whose defining property is that it works when nothing else does.
 *
 * `<details>`/`<summary>` satisfies all of it natively: the answer is in the markup
 * whether open or closed, the browser exposes `<summary>` as a real disclosure
 * button carrying its own expanded state, and it opens and closes with no script
 * loaded. **`aria-expanded` is deliberately not written here** — a hand-authored one
 * would be a server-rendered `false` that never changes, which is worse than the
 * native state it would shadow.
 *
 * The single thing the platform does not give is the deep link, and that is
 * `./FaqHashOpener`: twenty lines of progressive enhancement over a page that is
 * complete without it.
 *
 * ## Grouped, and every entry appears once
 *
 * `FAQ_GROUPS` partitions the corpus — shared questions, then passenger, then
 * driver. Not `faqFor()`, which returns an audience's entries *plus* the shared
 * ones: rendering it twice would duplicate every `both` entry and every `id`, which
 * breaks the deep link and the `FAQPage` JSON-LD S19 builds from the same data.
 *
 * ## The sources are on the page
 *
 * Each answer shows the spec anchors `faq.ts` requires of it. Same decision as the
 * value cards on `/vision` and the guide's chapter footers: these are factual claims
 * about a real service, and the reader who wants to check one should be able to see
 * what it rests on rather than take it on the site's word.
 */
export function FaqAccordion({ locale }: { readonly locale: Locale }) {
  const t = createWwwTranslator(locale);

  return (
    <>
      {FAQ_GROUPS.map((group) => {
        const entries = faqGroup(group.audience);
        if (entries.length === 0) return null;

        return (
          <section key={group.audience} className="flex flex-col gap-md">
            <h2 className="font-display text-hero-sm text-on-surface">{t(group.heading)}</h2>
            <div className="flex max-w-[70ch] flex-col gap-xs">
              {entries.map((entry) => (
                <FaqItem key={entry.id} entry={entry} locale={locale} />
              ))}
            </div>
          </section>
        );
      })}
    </>
  );
}

function FaqItem({ entry, locale }: { readonly entry: FaqEntry; readonly locale: Locale }) {
  const t = createWwwTranslator(locale);

  return (
    /*
     * No `name` attribute, so items do not close each other. Exclusive accordions
     * are for a rail with one visible pane; here a reader comparing "what does a
     * driver keep" against "what is the daily fee" wants both open, and on paper
     * every one of them is open anyway.
     *
     * `open` is never set: the page ships closed, which is what makes twenty-one
     * questions scannable. The answers are in the markup regardless — that is the
     * property `<details>` has and a JS accordion does not.
     */
    <details
      id={faqAnchorId(entry.id)}
      className="group rounded-card border border-outline-variant px-lg py-md open:bg-surface-variant print-plain"
    >
      <summary
        className={
          'cursor-pointer list-none font-display text-title text-on-surface ' +
          'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary ' +
          '[&::-webkit-details-marker]:hidden'
        }
      >
        <span className="flex items-start justify-between gap-md">
          {t(entry.question)}
          {/*
            The marker is drawn rather than left to the UA triangle, because
            `list-style` is not reliably printed and the two `::marker` spellings
            behave differently across engines. `aria-hidden` — the state is already
            on the `<summary>` itself, and a screen reader announcing "expanded,
            plus" is one announcement too many.
          */}
          <span
            aria-hidden
            className="mt-xxs shrink-0 text-body text-on-surface-variant transition-transform group-open:rotate-45"
          >
            +
          </span>
        </span>
      </summary>

      <div className="mt-sm flex flex-col gap-xs">
        <p className="text-body-sm text-on-surface-variant">{t(entry.answer)}</p>
        <p className="text-body-sm text-on-surface-variant">
          <span className="font-medium">{t('www.common.sourceLabel')}</span>{' '}
          {entry.refs.map((ref, index) => (
            <span key={ref} className="break-all font-mono text-[0.75em]">
              {index > 0 ? ' · ' : ''}
              {ref}
            </span>
          ))}
        </p>
      </div>
    </details>
  );
}
