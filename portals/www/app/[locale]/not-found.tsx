import { WWW_LOCALES } from '@/i18n';
import { createWwwTranslator, HREFLANG } from '@/i18n';
import { href } from '@/lib/routes';

/**
 * The 404, in every language this site publishes at once.
 *
 * **Not a shortcut — the only correct answer here.** Next hands `not-found.tsx` no
 * params, so this component cannot know which locale the reader was in; and the
 * request that produced it is, by definition, one whose address this site does not
 * publish. Picking a single language would mean guessing, and guessing wrong on a
 * Sinhala-first platform means answering a reader in a language they do not read on
 * the one page that already failed them.
 *
 * Stacked paragraphs, each carrying its own `lang`, is also exactly what A33 asks
 * for on mixed-script content: a screen reader switches voice per paragraph rather
 * than reading Sinhala through an English speech engine.
 *
 * **Two blocks rather than three while MCS-34 D2's Tamil deferral stands** (S13),
 * and for the same reason the deferral exists at all: `ta.ts` still holds English
 * prose behind its `TODO(ta)` markers, so a third block would emit English text
 * under `lang="ta-LK"` and hand it to a Tamil speech engine. That is worse than
 * omitting it — it is the failure mode A33 is written against, on the page least
 * able to absorb another one. It returns when `WWW_LOCALES` does.
 *
 * ---
 *
 * **⚠ NO URL ON THIS SITE CURRENTLY REACHES THIS COMPONENT, and that predates
 * S13.** Verified against the built output: `.next/app-path-routes-manifest.json`
 * lists `/_not-found` and **no `/[locale]/_not-found`**, so this file compiles and
 * is bound to no route. Every 404 — `/de/drivers`, `/si/nonexistent` and, since
 * S13, every `/ta/…` — is served by Next's built-in English "This page could not
 * be found".
 *
 * The cause is structural rather than a missing line. A segment's `not-found.tsx`
 * only handles a `notFound()` raised *inside* that segment's subtree; an address
 * that matches no route at all 404s above `[locale]`, where the handler would have
 * to be `app/not-found.tsx`. Adding one there is not a one-liner either:
 * `app/layout.tsx` is a deliberate pass-through that emits no `<html>` (the real
 * root layout is this directory's, because `<html lang>` is the path segment), so
 * a root 404 would render outside the fonts, `globals.css` and the appearance
 * script. Making this page reachable means deciding again who emits `<html>` —
 * an S03/S04 architectural decision, not a translation session's to reverse.
 *
 * **S13 did not cause it and did not fix it; S13 is why it now matters.** Before
 * the deferral, `/ta/*` was published, so a Tamil reader never took this path.
 * Now every Tamil URL does, and the reader least served by an English system page
 * is the one most likely to see it. **Left for S14 (the shell) or S19 (a11y/SEO),
 * recorded in the S13 handoff.**
 */
export default function NotFound() {
  return (
    /*
      **No `<main>` here.** `app/[locale]/layout.tsx` renders exactly one for every
      page it wraps, and this renders inside it — a second would be a second
      landmark, which is the thing S20's a11y test asserts against. Same reason
      there is no header and no footer in this file: the layout already put them
      around this content, correctly localised from the route's own segment. §5's
      "both with the header and footer" is satisfied by the shell rather than by
      each page repeating it.

      Exactly one `<h1>`, too — so the first published locale's title is the
      heading and the rest are `<h2>`. Two `<h1>`s saying the same thing in two
      languages is still two page titles.
    */
    <div className="mx-auto flex max-w-[1200px] flex-col gap-lg px-4 py-section-lg">
      {WWW_LOCALES.map((locale, index) => {
        const t = createWwwTranslator(locale);
        const Heading = index === 0 ? 'h1' : 'h2';

        return (
          <div key={locale} lang={HREFLANG[locale]} className="flex flex-col items-start gap-xs">
            <Heading className="font-display text-hero-sm text-on-surface">
              {t('www.notFound.title')}
            </Heading>
            <p className="text-body text-on-surface-variant">{t('www.notFound.body')}</p>
            {/*
              The route back into the site (§5). A plain `<a>` and not `next/link`:
              this page can be reached in states where the router's client cache is
              not the thing to trust, and a full document load to a known-good URL
              is the more reliable recovery.
            */}
            <a
              href={href(locale, '')}
              className="mt-xxs rounded-lg bg-primary px-lg py-xs text-body font-medium mr-on-primary focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
            >
              {t('www.notFound.home')}
            </a>
          </div>
        );
      })}
    </div>
  );
}
