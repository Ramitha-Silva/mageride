import Link from 'next/link';

import { PAGES } from '@/content/pages';
import { createWwwTranslator, type Locale } from '@/i18n';
import { href } from '@/lib/routes';

/**
 * Section 9 — the closing band, which links to `/download` and offers nothing else.
 *
 * ## No store badges, because MCS-34 **D3** has not been answered
 *
 * The apps are not published. S01's handoff records the decision and its
 * consequence: the store URLs are placeholder resource keys and `/download` is an
 * email-notify page **with no form**. A dead store badge is worse than no badge —
 * it is a button that looks like it installs an app and does not, on the page whose
 * only job is to be trusted.
 *
 * So this band says the true thing (`www.page.download.notYet` /
 * `notYetBody`, S07's copy) and links to the page that says it at length. When D3
 * is answered, the badges belong on `/download` (S18's page) and this band keeps
 * linking to it — one place to change, not two.
 *
 * ## And no form, ever
 *
 * This is the most natural place on the site to put "enter your email and we'll
 * tell you when it launches", and it is the single change that would end **two** of
 * MCS-34's four negatives at once: a submit is a request at request time, and an
 * address is personal data on a surface whose claim is that it holds none. It would
 * also need a consent notice, a retention policy and a PDPA answer for a mailing
 * list that does not exist yet. The refusal is written here because this is where
 * the pressure to add it will come from.
 */
export function DownloadBand({ locale }: { readonly locale: Locale }) {
  const t = createWwwTranslator(locale);
  const download = PAGES.download;

  if (!download) return null;

  return (
    <section className="mx-auto max-w-[1200px] px-4 pb-section-lg">
      <div className="flex flex-col items-start gap-md rounded-card bg-primary-container p-lg sm:p-section">
        <h2 className="max-w-[24ch] font-display text-hero-sm text-balance text-on-primary-container">
          {t(download.title)}
        </h2>
        <p className="max-w-[54ch] text-body text-on-primary-container">{t(download.intro)}</p>

        {/*
          The honest state, in the band rather than only on the page it links to —
          a reader who never clicks through should still not be left thinking the
          app is a tap away.
        */}
        <p className="max-w-[54ch] text-body-sm text-on-primary-container">
          {t(download.sections[0]?.heading ?? 'www.page.download.notYet')}
        </p>

        <Link
          href={href(locale, 'download')}
          className="min-h-cta rounded-lg bg-primary px-lg py-xs text-body font-medium mr-on-primary focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
        >
          {t('www.cta.getTheApp')}
        </Link>
      </div>
    </section>
  );
}
