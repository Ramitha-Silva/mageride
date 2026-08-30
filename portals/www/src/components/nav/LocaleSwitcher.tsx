'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';

import { cx } from '@mageride/ui';

import type { Locale } from '@/i18n/locales';
import { href, localeRelativePath } from '@/lib/routes';

/**
 * The language switcher — **real links, one per published locale, each pointing at
 * the same page in that language.**
 *
 * ## Why links and not a `<select>`
 *
 * A `<select>` with an `onChange` router push is the common implementation and it
 * is wrong here in four separate ways, three of which are invisible in a browser
 * with JavaScript on:
 *
 *   - **A crawler cannot follow it.** This is the one MageRide surface whose
 *     entire purpose is to be found, and A32's reciprocal `hreflang` set is a
 *     promise that `/si/drivers` and `/en/drivers` are two canonical documents.
 *     `hreflang` describes *links*; a script that mutates `location` is not one,
 *     so the annotation would point at documents nothing on the page connects.
 *   - **It does not work with JavaScript off**, and this site's whole claim is
 *     that it renders with the platform down. A language chooser that needs a
 *     hydrated bundle is the one control most likely to be reached first by
 *     somebody on a bad connection.
 *   - **It cannot be opened in a new tab**, middle-clicked, copied or bookmarked.
 *   - **Its accessible name is a form control's**, so a screen reader announces a
 *     combobox where the reader is looking for a link to their own language.
 *
 * ## The current locale is a link too, and that is deliberate
 *
 * It renders with `aria-current="page"` and a distinct name — "සිංහල, current
 * language" rather than "Read this page in සිංහල" — instead of being disabled or
 * dropped. A disabled control is not focusable, so a reader tabbing the row cannot
 * tell *which* language they are in; dropping it makes the row's length change per
 * page. Both are worse than one extra tab stop.
 *
 * ## The names are endonyms and are never translated
 *
 * `language.si` / `.ta` / `.en` in `@mageride/i18n` are **සිංහල · தமிழ் ·
 * English** — each language's name for itself. That is the one string on this site
 * that must not be localised: a Tamil reader who cannot read Sinhala is scanning
 * for "தமிழ்", and rendering it as whatever Sinhala calls Tamil hides the control
 * from exactly the person it exists for. Only the sentence *around* the name is
 * translated, which is why `www.language.switchTo` takes a `{language}`
 * placeholder rather than being three separate strings.
 *
 * ## It cannot produce a dead link
 *
 * `href()` composes from `src/lib/routes.ts` and every route in that table exists
 * in every published locale — `allUrls()` is the product of the two, and
 * `test/routes.test.ts` holds the table and the app tree to each other. So "the
 * same page in the other language" is a fact about the route table rather than a
 * hope. `WWW_LOCALES` and not `LOCALES`: MCS-34 D2 defers Tamil, so a Tamil link
 * here would be a link to a 404 (S13).
 *
 * ## `'use client'` on a component whose whole point is that it is not a script
 *
 * Not a contradiction, and worth being precise about because it looks like one.
 * The directive decides where the component's *code* can run; it does not decide
 * what it renders. This renders `<a href>` elements, which Next serialises into
 * the prerendered HTML like any other markup — so a reader with JavaScript
 * disabled receives working links, and a crawler that executes nothing sees the
 * `hreflang` set it is promised.
 *
 * What the client boundary buys is `usePathname()`. The switcher has to know which
 * document is showing, and a **layout** — which is where the header lives — is
 * never handed the pathname: it wraps whatever page matched and is deliberately
 * not re-rendered per route. The alternative is threading the current path down
 * through all thirteen pages, which is thirteen chances to pass the wrong one and
 * a prop every future page has to remember. `usePathname` is available during
 * static prerendering (unlike `useSearchParams`, which would force this subtree
 * dynamic and cost the site its "renders with the platform down" property).
 */
/**
 * Everything this switcher renders, resolved on the server (MCS-36 D3).
 *
 * The endonyms are the reason each option carries its own `hrefLang` *and* `lang`:
 * the text is in the language it names, not in the page's, so both the crawler
 * annotation and the screen reader's pronunciation are per-option facts the server
 * already knows.
 */
export interface LocaleSwitcherLabels {
  /** The `<nav>`'s accessible name. */
  readonly label: string;
  readonly options: readonly {
    readonly locale: Locale;
    /** The BCP-47 tag — `si-LK`, `en-LK`. */
    readonly hrefLang: string;
    /** The language's own name, in its own script. */
    readonly language: string;
    /** "Current language: X" or "Switch to X", already chosen by the server. */
    readonly ariaLabel: string;
    readonly current: boolean;
  }[];
}

export function LocaleSwitcher({
  labels,
  className,
}: {
  readonly labels: LocaleSwitcherLabels;
  readonly className?: string;
}) {
  const path = localeRelativePath(usePathname() ?? '');

  return (
    <nav aria-label={labels.label} className={cx('flex items-center gap-xxs', className)}>
      {labels.options.map((option) => {
        const { locale: candidate, current } = option;

        return (
          <Link
            key={candidate}
            href={href(candidate, path)}
            /*
             * `hrefLang` on the link itself, matching the `<link rel="alternate">`
             * set S19 adds to the head. Both describe the same relationship and a
             * crawler reads either; a reader's browser uses this one to decide
             * whether it can render the target's script before following it.
             */
            hrefLang={option.hrefLang}
            /*
             * `lang` on the element, because the *text* is in that language and
             * not in the page's. Without it a Sinhala page's screen reader
             * pronounces "English" with Sinhala phonetics, and A33 asks for
             * exactly this on mixed-script content.
             */
            lang={option.hrefLang}
            aria-current={current ? 'page' : undefined}
            aria-label={option.ariaLabel}
            className={cx(
              'rounded-sm px-xs py-xxs text-body-sm transition-colors',
              'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary',
              current
                ? 'bg-surface-variant font-medium text-on-surface'
                : 'text-on-surface-variant hover:bg-surface-variant hover:text-on-surface',
            )}
          >
            {option.language}
          </Link>
        );
      })}
    </nav>
  );
}
