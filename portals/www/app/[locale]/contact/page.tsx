import Link from 'next/link';

import { PAGES } from '@/content/pages';
import { createWwwTranslator } from '@/i18n';
import { localeFrom, type LocaleParams } from '@/lib/params';
import { href } from '@/lib/routes';
import { metadataForRoute } from '@/lib/seo';

/**
 * `/{locale}/contact` — **no form, and no address either.**
 *
 * ## Two fences, and the second one is the awkward one
 *
 * **No form** is MCS-34's and S18 restates it: a contact form is a personal-data
 * collection point on a surface whose entire claim is that it holds none, and it
 * would need a backend, a data-protection position and a retention policy —
 * *"all three are separate change sets"* (`docs/www-site-plan.md` §13).
 *
 * **No address** is D4, and it is not the same as "email-only". D4 decided the
 * *shape* — email, no phone, no call centre — and left the address itself open:
 * S01's handoff records that the user has not chosen between a domain mailbox and
 * the account address. S18's instruction for an unanswered D4 is to *"ship
 * email-only and leave the other rows out rather than showing placeholders"*, and
 * *"the proposal's `📧 [To be added]` must never reach a public page."*
 *
 * With no address chosen there is no email row to ship, so this page does what an
 * honest contact page does when it has nothing to give: **it says so, and then it
 * sends the reader somewhere that actually works.**
 *
 *   - **Support inside the app** is not a consolation prize — it is the real answer
 *     for anything about a trip, because a ticket raised there arrives attached to
 *     the trip it is about, and an email cannot be.
 *   - **The questions page and the guides** are where most of what would have been
 *     written to that address is already answered.
 *   - **The missing address is stated as missing**, rather than left as a section
 *     with a heading and no way to act on it. A reader who came here to write to
 *     somebody deserves to be told there is nobody to write to yet, in one sentence,
 *     instead of scanning the page twice looking for what they missed.
 *
 * D4 owes a `docs/production/go-live-checklist.md` row. When the address is chosen
 * it replaces one paragraph here and nothing else changes.
 */
export async function generateMetadata({ params }: { params: Promise<LocaleParams> }) {
  return metadataForRoute(await localeFrom(params), 'contact');
}

export default async function ContactPage({ params }: { params: Promise<LocaleParams> }) {
  const locale = await localeFrom(params);
  const t = createWwwTranslator(locale);
  const copy = PAGES.contact;

  return (
    <div className="mx-auto flex max-w-[1200px] flex-col gap-section px-4 py-section">
      <header className="flex flex-col gap-md">
        <h1 className="max-w-[20ch] font-display text-hero text-balance text-on-surface">
          {t(copy?.title ?? 'www.page.contact.title')}
        </h1>
        <p className="max-w-[62ch] text-body text-on-surface-variant">
          {t(copy?.intro ?? 'www.page.contact.intro')}
        </p>
      </header>

      <section className="flex flex-col gap-sm">
        <h2 className="font-display text-hero-sm text-on-surface">
          {t('www.page.contact.inAppHeading')}
        </h2>
        <p className="max-w-[62ch] text-body text-on-surface-variant">
          {t('www.page.contact.inAppBody')}
        </p>
      </section>

      <section className="flex flex-col gap-sm">
        <h2 className="font-display text-hero-sm text-on-surface">
          {t('www.page.contact.questionsHeading')}
        </h2>
        <p className="max-w-[62ch] text-body text-on-surface-variant">
          {t('www.page.contact.questionsBody')}
        </p>
        <ul className="flex flex-wrap gap-md">
          {[
            { path: 'faq', label: t('www.nav.faq') },
            { path: 'guide', label: t('www.nav.guide') },
          ].map((entry) => (
            <li key={entry.path}>
              <Link
                href={href(locale, entry.path)}
                className="inline-flex min-h-cta items-center rounded-lg border border-outline px-lg py-xs text-body font-medium text-on-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
              >
                {entry.label}
              </Link>
            </li>
          ))}
        </ul>
      </section>

      <section className="flex flex-col gap-sm">
        <h2 className="font-display text-hero-sm text-on-surface">
          {t('www.page.contact.emailHeading')}
        </h2>
        <p className="max-w-[62ch] text-body text-on-surface-variant">
          {t('www.page.contact.emailBody')}
        </p>
        {/*
          The one paragraph the go-live checklist replaces. It is body copy and not
          small print on purpose: a reader who is looking for an address should meet
          this sentence at the same size as everything else on the page.
        */}
        <p className="max-w-[62ch] text-body text-on-surface-variant">
          {t('www.page.contact.emailPending')}
        </p>
      </section>
    </div>
  );
}
