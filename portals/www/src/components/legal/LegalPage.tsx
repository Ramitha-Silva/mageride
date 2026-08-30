import { legalSectionId, type LegalDocument } from '@/content/legal';
import { createWwwTranslator, type Locale } from '@/i18n';
import { ROUTE_BY_PATH, type LegalDoc } from '@/lib/routes';

/**
 * The document layout the three legal routes render through — **the shell S18 asks
 * for, built to receive counsel's text rather than to stand in for it.**
 *
 * A reading column, a table of contents, a last-updated line, and a status block
 * that disappears the moment a document has a version. When the text arrives, a
 * session fills `sections` in `src/content/legal.ts` and sets `lastUpdated`; nothing
 * here changes, and the Sinhala and English cuts get the same structure because the
 * structure is data (`src/content/types.ts`'s reasoning, applied to a second corpus).
 *
 * ## The `<h1>` is the route's nav label
 *
 * `portals/www/CLAUDE.md`: *"A route's nav label and its heading are one key."*
 * The footer's legal row and this heading read `ROUTE_BY_PATH[…].labelKey`, so
 * "Privacy policy" in the footer cannot become "Privacy notice" on the page.
 *
 * ## The table of contents is dropped when there is nothing to contain
 *
 * A `terms` page today has a status block and no sections, and a table of contents
 * over one item is furniture that says "this document is longer than it looks". The
 * TOC appears at two sections or more, which is also when it starts being useful on
 * a phone.
 *
 * ## `print-plain`, not `print-hidden`
 *
 * Legal documents get printed. The reading column, the headings and the sources all
 * survive; the TOC does not, because a list of in-page anchors is meaningless on
 * paper — the same call `ChapterPage` makes for the guide, for the same reason.
 */
export function LegalPage({
  locale,
  document,
}: {
  readonly locale: Locale;
  readonly document: LegalDocument;
}) {
  const t = createWwwTranslator(locale);
  const title = t(ROUTE_BY_PATH[legalPath(document.doc)].labelKey);
  const showContents = document.sections.length >= 2;

  return (
    <div className="mx-auto grid max-w-[1200px] gap-lg px-4 py-section lg:grid-cols-[minmax(0,1fr)_14rem] lg:gap-xl">
      <article className="flex max-w-[65ch] flex-col gap-lg">
        <header className="flex flex-col gap-xs">
          <h1 className="font-display text-hero-sm text-balance text-on-surface">{title}</h1>
          <p className="text-body text-on-surface-variant">{t(document.intro)}</p>

          {/*
            The last-updated line. It is a fact about the *document*, so it sits
            with the title rather than at the foot where a reader finds it after
            they have already trusted the text.

            `lastUpdated` is a supplied string and is never read from the clock —
            see `src/content/legal.ts`. A date that moved on every rebuild would
            tell a reader this document had changed when it had not, which is the
            one kind of inaccuracy a legal page cannot afford.
          */}
          <p className="text-body-sm text-on-surface-variant">
            <span className="font-medium">{t('www.legal.lastUpdatedLabel')}</span>{' '}
            {document.lastUpdated ?? t('www.legal.lastUpdatedNone')}
          </p>
        </header>

        {/*
          The status block, while there is no text. Bordered rather than tinted so it
          survives a cheap printer and a forced-colours mode — the same reason the
          guide's callouts carry a word as well as a colour (WCAG 1.4.1).
        */}
        {document.lastUpdated === null ? (
          <section
            aria-labelledby={STATUS_ID}
            className="flex flex-col gap-xs rounded-card border border-outline p-lg"
          >
            <h2 id={STATUS_ID} className="font-display text-title text-on-surface">
              {t('www.legal.status.heading')}
            </h2>
            <p className="text-body-sm text-on-surface-variant">{t(document.status)}</p>
          </section>
        ) : null}

        {document.sections.map((section) => (
          <section
            key={section.id}
            id={legalSectionId(document.doc, section)}
            className="flex flex-col gap-xs scroll-mt-[6rem]"
          >
            <h2 className="font-display text-title text-on-surface">{t(section.heading)}</h2>
            {section.body.map((paragraph) => (
              <p key={paragraph} className="text-body-sm text-on-surface-variant">
                {t(paragraph)}
              </p>
            ))}
            {section.source ? (
              <p className="text-body-sm text-on-surface-variant">
                <span className="font-medium">{t('www.common.sourceLabel')}</span>{' '}
                <span className="break-all font-mono text-[0.75em]">{section.source}</span>
              </p>
            ) : null}
          </section>
        ))}
      </article>

      {showContents ? (
        <nav
          aria-label={t('www.common.onThisPage')}
          className="print-hidden lg:sticky lg:top-[6rem] lg:self-start"
        >
          <p className="text-body-sm font-bold text-on-surface">{t('www.common.onThisPage')}</p>
          <ol className="mt-sm flex flex-col gap-xxs">
            {document.sections.map((section) => (
              <li key={section.id}>
                <a
                  href={`#${legalSectionId(document.doc, section)}`}
                  className="block rounded-sm py-xxs text-body-sm text-on-surface-variant hover:text-on-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
                >
                  {t(section.heading)}
                </a>
              </li>
            ))}
          </ol>
        </nav>
      ) : null}
    </div>
  );
}

const STATUS_ID = 'legal-status';

/** `'privacy'` → `'legal/privacy'`, the key `ROUTE_BY_PATH` is total over. */
function legalPath(doc: LegalDoc) {
  return `legal/${doc}` as const;
}
