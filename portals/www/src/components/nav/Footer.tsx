import Link from 'next/link';

import { cx } from '@mageride/ui';

import { FOOTER_COLUMNS, FOOTER_MADE_IN_KEY, FOOTER_RIGHTS_KEY } from '@/content/marketing';
import { createWwwTranslator, type Locale } from '@/i18n';
import { href, routesInGroup } from '@/lib/routes';

/**
 * The footer — sitemap-style link columns and the rights line.
 *
 * ## What is deliberately absent
 *
 * **No contact details.** MCS-34 **D4** is unanswered: the address itself has not
 * been chosen between a domain mailbox and the account address, and S01's handoff
 * records that. A placeholder email in a footer is worse than none — it is a
 * support channel that looks real, so somebody writes to it.
 *
 * **No newsletter form, ever.** A form is a request at request time, which ends
 * the second of MCS-34's four negatives, and a signup list is personal data, which
 * ends the third. `/download` is an email-notify page *with no form* for exactly
 * this reason (D3). This is the place that invitation would most naturally be
 * added, so the refusal is written here rather than assumed.
 *
 * **No copyright year.** `FOOTER_RIGHTS_KEY`'s own comment explains it: every page
 * below `app/[locale]/` is prerendered at build, so a `{year}` placeholder freezes
 * at whatever year the last deploy happened in and is then quietly wrong every
 * January.
 *
 * ## The links come from `routes.ts` and the headings from `marketing.ts`
 *
 * Only the *headings* are content. The links are `ROUTES` filtered by `group`, so
 * the footer cannot list a page that does not exist and cannot omit one that does
 * — which is the inversion `src/lib/routes.ts` exists for. A second hand-kept list
 * here would be the thing that drifts.
 *
 * ## The language band moved out of here in S15
 *
 * S14 put the trilingual band in this footer. S15's section list puts it on the
 * home page as section 8, and rendering it in both would show the same three
 * sentences twice on one page — so it lives in
 * `src/components/marketing/LanguageBand.tsx` and this footer keeps the links and
 * the rights line. The trade is recorded in the S15 handoff: the band is now on the
 * home page only rather than on every page.
 */
export function Footer({ locale }: { readonly locale: Locale }) {
  const t = createWwwTranslator(locale);

  return (
    <footer className="mt-section border-t border-outline-variant bg-surface">
      <div className="mx-auto flex max-w-[1200px] flex-col gap-section px-4 py-section">
        <nav aria-label={t('www.nav.footer')} className="grid gap-lg sm:grid-cols-3">
          {FOOTER_COLUMNS.map((column) => (
            <div key={column.group} className="flex flex-col gap-xs">
              <h2 className="font-display text-body-sm font-bold tracking-wide text-on-surface uppercase">
                {t(column.heading)}
              </h2>
              <ul className="flex flex-col gap-xxs">
                {routesInGroup(column.group).map((route) => (
                  <li key={route.path}>
                    <Link
                      href={href(locale, route.path)}
                      className={cx(
                        'rounded-sm text-body-sm text-on-surface-variant transition-colors hover:text-on-surface',
                        'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary',
                      )}
                    >
                      {t(route.labelKey)}
                    </Link>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </nav>

        <div className="flex flex-col gap-xxs text-body-sm text-on-surface-variant sm:flex-row sm:justify-between">
          <p>{t(FOOTER_RIGHTS_KEY)}</p>
          <p>{t(FOOTER_MADE_IN_KEY)}</p>
        </div>
      </div>
    </footer>
  );
}
