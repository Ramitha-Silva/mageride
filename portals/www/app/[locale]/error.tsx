'use client';

import { ERROR_STRINGS, type ErrorStringKey } from '@/i18n/error-strings';
import { HREFLANG, WWW_LOCALES } from '@/i18n/locales';
import { href } from '@/lib/routes';

/**
 * The error boundary, in every language this site publishes at once — the same
 * reasoning as `not-found.tsx` beside it, arrived at from the other direction, and
 * carrying the same S13 note: `WWW_LOCALES` is two while MCS-34 D2's Tamil
 * deferral stands, because English prose under `lang="ta-LK"` would be a worse
 * answer than no Tamil block.
 *
 * An error boundary is a **client** component (React requires the `reset` handler
 * to be callable), and a client component on this surface takes a locale rather
 * than label props: React cannot serialise a translator across the boundary. But
 * this particular one has no locale to be handed — it can be thrown by a segment
 * that failed before its params resolved. So it renders all three, and the reader
 * finds their own.
 *
 * It calls nothing and reports nothing. A44/A36: no analytics, no error beacon, no
 * third party. Whatever went wrong is in the container's log, where the platform's
 * own observability already looks.
 */
export default function Error({ reset }: { error: Error; reset: () => void }) {
  return (
    /*
      No `<main>`, no header, no footer — `app/[locale]/layout.tsx` renders all
      three around this, correctly localised from the route segment. A second
      `<main>` would be a second landmark (S20's a11y assertion), and re-rendering
      the header here would be a second `<nav>` with the same name.

      Unlike `not-found.tsx`, this file **is** reachable: an error boundary catches
      a throw from inside its own subtree, and this one is inside `[locale]`, so it
      renders with the chrome around it exactly as designed.
    */
    <div className="mx-auto flex max-w-[1200px] flex-col gap-lg px-4 py-section-lg">
      {WWW_LOCALES.map((locale, index) => {
        /*
         * The generated subset, not a translator (MCS-36 D3). This is the one client
         * module with no server parent to hand it strings — Next instantiates an error
         * boundary itself — so importing `@/i18n` here would put the whole resource
         * table back into every page's bundle for four strings.
         */
        const t = (key: ErrorStringKey) => ERROR_STRINGS[locale]?.[key] ?? '';
        const Heading = index === 0 ? 'h1' : 'h2';

        return (
          <div key={locale} lang={HREFLANG[locale]} className="flex flex-col items-start gap-xs">
            <Heading className="font-display text-hero-sm text-on-surface">
              {t('www.error.title')}
            </Heading>
            <p className="text-body text-on-surface-variant">{t('www.error.body')}</p>
            <div className="mt-xxs flex flex-wrap items-center gap-sm">
              <button
                type="button"
                onClick={reset}
                className="min-h-cta rounded-lg bg-primary px-lg py-xs text-body font-medium mr-on-primary focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
              >
                {t('common.retry')}
              </button>
              {/*
                A second way out (§5). `reset()` re-renders the same subtree, which
                is the right first try and the wrong only option — if the throw is
                deterministic, pressing it again fails again and the reader is
                stuck on a page with one dead control.
              */}
              <a
                href={href(locale, '')}
                className="min-h-cta rounded-lg border border-outline px-lg py-xs text-body font-medium text-on-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
              >
                {t('www.notFound.home')}
              </a>
            </div>
          </div>
        );
      })}
    </div>
  );
}
