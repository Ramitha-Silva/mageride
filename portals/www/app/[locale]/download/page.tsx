import { JsonLd } from '@/components/seo/JsonLd';
import { MISC_KEYS, PAGES } from '@/content/pages';
import { createWwwTranslator } from '@/i18n';
import { softwareApplications } from '@/lib/json-ld';
import { localeFrom, type LocaleParams } from '@/lib/params';
import { metadataForRoute } from '@/lib/seo';

/**
 * `/{locale}/download` — the honest state of a product that is not in a store yet.
 *
 * ## What MCS-34 D3 leaves, and what S18 asks for
 *
 * D3: the store URLs are **not live**. S18's constrained variant for that case is
 * *"an email-notify page **with no form**. A `mailto:` link with a pre-filled
 * subject, and a sentence saying the apps are not yet listed."*
 *
 * **The `mailto:` cannot be built, and the reason is D4 rather than a preference.**
 * MCS-34 D4 settled `/contact` as email-only *and left the address itself unchosen*
 * — S01's handoff: *"the user has not chosen between a domain mailbox and the
 * account address"*. S17 hit the same wall building a "was this helpful?" control
 * and reached the same conclusion, which is the one worth restating: **a `mailto:`
 * to a placeholder is worse than nothing.** It is a notify-me channel that looks
 * real, that a reader spends a message on, and that goes nowhere. There is no
 * `mailto:` anywhere on this surface and this page does not become the first.
 *
 * So what ships is the rest of the variant, in full: the apps are not listed, that
 * is said plainly, and **no address, no badge, no form and no field** appears. When
 * D3 and D4 are answered — both owe a `docs/production/go-live-checklist.md` row —
 * the store badges and the notify link land here together.
 *
 * ## The two things this page adds because they are true today
 *
 * **Which app you want.** There are two, they are not interchangeable, and a driver
 * who installs the passenger app has to be told why nothing works. S18 lists that
 * split under the D3-*answered* branch; it needs no store URL, so it is here.
 *
 * **The minimum Android version — URD NFR-22, Android 8.0 (API 26).** A fact from
 * the spec, on the page where somebody decides whether their phone will run this.
 * **No iOS minimum is stated**, because the URD states none anywhere and S18 is
 * explicit: do not invent one, omit it.
 *
 * The two `SoftwareApplication` blocks (S19) carry the same two facts and no more:
 * `operatingSystem` is `Android 8.0+` and there is **no `installUrl`**, because D3
 * leaves the listings unpublished and a store URL that does not resolve is a claim
 * this site cannot support. `offers` at zero is not a placeholder — the apps
 * genuinely cost nothing.
 */
export async function generateMetadata({ params }: { params: Promise<LocaleParams> }) {
  return metadataForRoute(await localeFrom(params), 'download');
}

export default async function DownloadPage({ params }: { params: Promise<LocaleParams> }) {
  const locale = await localeFrom(params);
  const t = createWwwTranslator(locale);
  const copy = PAGES.download;

  return (
    <div className="mx-auto flex max-w-[1200px] flex-col gap-section px-4 py-section">
      <header className="flex flex-col gap-md">
        <h1 className="max-w-[20ch] font-display text-hero text-balance text-on-surface">
          {t(copy?.title ?? 'www.page.download.title')}
        </h1>
        <p className="max-w-[62ch] text-body text-on-surface-variant">
          {t(copy?.intro ?? 'www.page.download.intro')}
        </p>
      </header>

      {/*
        The state of play, first and unmissable. A reader who came to install
        something should not have to read a feature comparison to find out there is
        nothing to install.
      */}
      <section className="flex flex-col gap-sm rounded-card border border-outline-variant bg-surface p-lg sm:p-section">
        <h2 className="font-display text-hero-sm text-on-surface">
          {t('www.page.download.notYet')}
        </h2>
        <p className="max-w-[62ch] text-body text-on-surface-variant">
          {t('www.page.download.notYetBody')}
        </p>
      </section>

      <section className="flex flex-col gap-lg">
        <h2 className="font-display text-hero-sm text-on-surface">
          {t('www.page.download.whichAppHeading')}
        </h2>
        <ul className="grid gap-md md:grid-cols-2">
          <li className="flex flex-col gap-xs rounded-card border border-outline-variant p-lg">
            <h3 className="font-display text-title text-on-surface">{t(MISC_KEYS.passengerApp)}</h3>
            <p className="text-body-sm text-on-surface-variant">
              {t('www.page.download.passengerAppBody')}
            </p>
          </li>
          <li className="flex flex-col gap-xs rounded-card border border-outline-variant p-lg">
            <h3 className="font-display text-title text-on-surface">{t(MISC_KEYS.driverApp)}</h3>
            <p className="text-body-sm text-on-surface-variant">
              {t('www.page.download.driverAppBody')}
            </p>
          </li>
        </ul>
      </section>

      {/* URD NFR-22. The one hard number on this page, and it is a spec's. */}
      <section className="flex flex-col gap-sm">
        <h2 className="font-display text-hero-sm text-on-surface">
          {t('www.page.download.requirementsHeading')}
        </h2>
        <p className="max-w-[62ch] text-body text-on-surface-variant">
          {t('www.page.download.androidMinimum')}
        </p>
        <p className="text-body-sm text-on-surface-variant">
          <span className="font-medium">{t('www.common.sourceLabel')}</span>{' '}
          <span className="break-all font-mono text-[0.75em]">{ANDROID_MINIMUM_SOURCE}</span>
        </p>
      </section>

      <JsonLd nodes={[...softwareApplications(locale)]} />
    </div>
  );
}

/**
 * README rule 7: a public claim carries the anchor it rests on. "Android 8.0 (API
 * 26)" is a number somebody checks their phone against before deciding MageRide is
 * not for them, which makes it exactly the kind of claim that needs one.
 */
const ANDROID_MINIMUM_SOURCE = 'specs/user-requirements-document.md#nfr-22';
