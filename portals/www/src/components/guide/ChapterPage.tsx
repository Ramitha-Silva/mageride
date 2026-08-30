import Link from 'next/link';

import { cx } from '@mageride/ui';

import { FAQ } from '@/content/faq';
import { chapterById, chaptersFor } from '@/content/index';
import type { Chapter } from '@/content/types';
import { createWwwTranslator, type Locale } from '@/i18n';
import { href } from '@/lib/routes';

import { ChapterBody } from './ChapterBody';
import { chapterLabels } from './chapterLabels';
import { stepId } from './ids';

/**
 * One guide chapter. **34 pages render through this component and no other.**
 *
 * That is the payoff S17 names: the content was typed rather than authored per
 * locale, so *"a chapter that renders in English renders in Sinhala by
 * construction"*. There is no per-chapter layout to get wrong and no chapter that
 * can quietly lose a step in one language.
 *
 * ## The three columns, and where they stop
 *
 * A sticky chapter rail on the left, the reading column in the middle, an on-page
 * table of contents on the right — **all three only at `lg:` (1024px), which is
 * D2's third breakpoint and there is no fourth** (D8). Below that the page is one
 * column: the rail becomes a plain link list above the chapter and the TOC is
 * dropped, because a table of contents for a nine-step chapter on a 375px screen is
 * a screenful of links in front of the thing the reader came for.
 *
 * The reading column is capped at `65ch`. S17 gives the reason and it is not
 * aesthetic: *"this is prose, and 1200px of Sinhala body text is unreadable."*
 *
 * ## The pager does not roll over
 *
 * `ChapterPager` walks `chaptersFor(audience)`, which is one audience's chapters in
 * `order`. **The last passenger chapter has no next.** Rolling into driver chapter
 * 1 would tell a passenger that the driver guide is the rest of their document.
 *
 * ## Everything that is not the chapter is `print-hidden`
 *
 * S17 §3: a driver in a three-wheeler with no data plan is a real reader of this
 * guide. The rail, the TOC, the pager and the lightbox triggers come off on paper;
 * the steps, their numbers, the callouts and the images stay.
 */
export function ChapterPage({
  locale,
  chapter,
}: {
  readonly locale: Locale;
  readonly chapter: Chapter;
}) {
  const t = createWwwTranslator(locale);
  const siblings = chaptersFor(chapter.audience);
  const position = siblings.findIndex((entry) => entry.id === chapter.id);
  const previous = position > 0 ? siblings[position - 1] : undefined;
  const next = position >= 0 && position < siblings.length - 1 ? siblings[position + 1] : undefined;

  const related = chapter.relatedChapters
    .map((id) => chapterById(id))
    .filter((entry) => entry !== undefined);
  const questions = chapter.faqRefs
    .map((id) => FAQ.find((entry) => entry.id === id))
    .filter((entry) => entry !== undefined);

  const chapterHref = (entry: Chapter) =>
    href(locale, `guide/${entry.audience}/${entry.slug}`);

  return (
    <div className="mx-auto grid max-w-[1200px] gap-lg px-4 py-section lg:grid-cols-[16rem_minmax(0,1fr)_14rem] lg:gap-xl">
      {/* The chapter rail. */}
      <nav
        aria-label={t('www.guide.rail.label')}
        className="print-hidden lg:sticky lg:top-[6rem] lg:self-start"
      >
        <p className="text-body-sm font-bold text-on-surface">{t('www.guide.rail.heading')}</p>
        <ol className="mt-sm flex flex-col gap-xxs">
          {siblings.map((entry, index) => {
            const current = entry.id === chapter.id;
            return (
              <li key={entry.id}>
                <Link
                  href={chapterHref(entry)}
                  aria-current={current ? 'page' : undefined}
                  className={cx(
                    'block rounded-sm px-xs py-xxs text-body-sm transition-colors',
                    'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary',
                    current
                      ? 'bg-surface-variant font-medium text-on-surface'
                      : 'text-on-surface-variant hover:bg-surface-variant hover:text-on-surface',
                  )}
                >
                  <span aria-hidden className="mr-xxs tabular-nums">
                    {index + 1}.
                  </span>
                  {t(entry.title)}
                </Link>
              </li>
            );
          })}
        </ol>
      </nav>

      {/* The reading column. */}
      <article className="flex max-w-[65ch] flex-col gap-lg">
        <header className="flex flex-col gap-xs">
          <p className="text-body-sm font-medium text-secondary">
            {t('www.guide.chapterNumber', { number: chapter.order })}
          </p>
          <h1 className="font-display text-hero-sm text-balance text-on-surface">
            {t(chapter.title)}
          </h1>
          <p className="text-body text-on-surface-variant">{t(chapter.summary)}</p>
        </header>

        <ChapterBody labels={chapterLabels(locale, chapter)} chapter={chapter} />

        {/* Where this chapter came from — README rule 7, for the reader. */}
        {chapter.sources.length > 0 ? (
          <div className="flex flex-col gap-xxs border-t border-outline-variant pt-md">
            <p className="text-body-sm font-medium text-on-surface-variant">
              {t('www.common.sourceLabel')}
            </p>
            <ul className="flex flex-col gap-xxs">
              {chapter.sources.map((source) => (
                <li key={source} className="break-all font-mono text-[0.7rem] text-on-surface-variant">
                  {source}
                </li>
              ))}
            </ul>
          </div>
        ) : null}

        {related.length > 0 ? (
          <section className="flex flex-col gap-xs print-hidden">
            <h2 className="font-display text-title text-on-surface">{t('www.guide.related')}</h2>
            <ul className="flex flex-col gap-xxs">
              {related.map((entry) => (
                <li key={entry.id}>
                  <Link
                    href={chapterHref(entry)}
                    className="text-body-sm text-secondary underline underline-offset-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
                  >
                    {t(entry.title)}
                  </Link>
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        {questions.length > 0 ? (
          <section className="flex flex-col gap-xs">
            <h2 className="font-display text-title text-on-surface">{t('www.guide.questions')}</h2>
            <dl className="flex flex-col gap-sm">
              {questions.map((entry) => (
                <div key={entry.id} className="flex flex-col gap-xxs">
                  <dt className="text-body-sm font-medium text-on-surface">{t(entry.question)}</dt>
                  <dd className="text-body-sm text-on-surface-variant">{t(entry.answer)}</dd>
                </div>
              ))}
            </dl>
          </section>
        ) : null}

        {/* The pager. Ends at the last chapter of this audience — see the note. */}
        <nav
          aria-label={t('www.guide.pager.label')}
          className="flex flex-wrap items-center justify-between gap-md border-t border-outline-variant pt-md print-hidden"
        >
          {previous ? (
            <Link
              href={chapterHref(previous)}
              rel="prev"
              className="flex max-w-[45%] flex-col gap-xxs rounded-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
            >
              <span className="text-body-sm text-on-surface-variant">
                {t('www.common.previous')}
              </span>
              <span className="text-body-sm font-medium text-on-surface">{t(previous.title)}</span>
            </Link>
          ) : (
            <Link
              href={href(locale, 'guide')}
              className="rounded-sm text-body-sm text-on-surface-variant underline underline-offset-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
            >
              {t('www.guide.backToGuide')}
            </Link>
          )}

          {next ? (
            <Link
              href={chapterHref(next)}
              rel="next"
              className="flex max-w-[45%] flex-col items-end gap-xxs rounded-sm text-right focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
            >
              <span className="text-body-sm text-on-surface-variant">{t('www.common.next')}</span>
              <span className="text-body-sm font-medium text-on-surface">{t(next.title)}</span>
            </Link>
          ) : (
            <Link
              href={href(locale, 'guide')}
              className="rounded-sm text-body-sm text-on-surface-variant underline underline-offset-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
            >
              {t('www.guide.backToGuide')}
            </Link>
          )}
        </nav>
      </article>

      {/*
        The on-page table of contents, built from the chapter's own steps.
        `lg:` only, and `print-hidden` — a list of in-page anchors is meaningless
        on paper.
      */}
      <nav
        aria-label={t('www.guide.toc.label')}
        className="hidden print-hidden lg:sticky lg:top-[6rem] lg:block lg:self-start"
      >
        <p className="text-body-sm font-bold text-on-surface">{t('www.common.onThisPage')}</p>
        <ol className="mt-sm flex flex-col gap-xxs">
          {chapter.steps.map((step, index) => (
            <li key={step.instruction}>
              <a
                href={`#${stepId(index)}`}
                className="block rounded-sm py-xxs text-body-sm text-on-surface-variant hover:text-on-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
              >
                {t('www.guide.stepLabel', { number: index + 1 })}
              </a>
            </li>
          ))}
        </ol>
      </nav>
    </div>
  );
}
