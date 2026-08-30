import { notFound } from 'next/navigation';

import { LegalPage } from '@/components/legal/LegalPage';
import { JsonLd } from '@/components/seo/JsonLd';
import { legalDocument } from '@/content/legal';
import { createWwwTranslator, WWW_LOCALES } from '@/i18n';
import { breadcrumbs, legalPage } from '@/lib/json-ld';
import { localeFrom, type LocaleParams } from '@/lib/params';
import { LEGAL_DOCS, type LegalDoc } from '@/lib/routes';
import { legalCrumbs, metadataFor } from '@/lib/seo';

/**
 * Terms, Privacy and PDPA data rights.
 *
 * One dynamic segment rather than three sibling directories because the three
 * documents have identical structure and differ only in their body — and because
 * the slugs are then **derived from `src/lib/routes.ts`** instead of being a
 * fourth place that has to be kept in step with the sitemap and the footer.
 *
 * **MCS-34 D5: counsel supplies the text, and no session in C134 authors any of
 * it.** S18 built the shell — `src/components/legal/LegalPage.tsx` for the layout,
 * `src/content/legal.ts` for the structure — and the three documents render an
 * honest "being prepared" notice rather than a template. Privacy and PDPA also
 * carry factual sections describing what this website collects (nothing) and what
 * `pdpa-svc` does; both say what they are, and neither is the policy. Launch is
 * gated on the text arriving — the app store listings need a privacy URL regardless.
 */

export function generateStaticParams(): { locale: string; doc: LegalDoc }[] {
  return WWW_LOCALES.flatMap((locale) => LEGAL_DOCS.map((doc) => ({ locale, doc })));
}

export const dynamicParams = false;

function isLegalDoc(value: string): value is LegalDoc {
  return (LEGAL_DOCS as readonly string[]).includes(value);
}

/**
 * Per-document metadata, so the three do not share the layout's canonical.
 *
 * `metadataFor` rather than `metadataForRoute` because the description is the
 * document's own standfirst from `src/content/legal.ts` rather than a `PAGES`
 * entry — `seoFor()` knows the same thing, and this route reaches it with the
 * document already in hand.
 */
export async function generateMetadata({
  params,
}: {
  params: Promise<LocaleParams & { doc: string }>;
}) {
  const locale = await localeFrom(params);
  const { doc } = await params;
  if (!isLegalDoc(doc)) notFound();

  const t = createWwwTranslator(locale);
  const document = legalDocument(doc);

  return metadataFor({
    locale,
    path: `legal/${doc}`,
    title: t(ROUTE_LABEL[doc]),
    description: t(document.intro),
  });
}

/** The three nav labels, which are also the three `<h1>`s. */
const ROUTE_LABEL = {
  terms: 'www.nav.legal.terms',
  privacy: 'www.nav.legal.privacy',
  pdpa: 'www.nav.legal.pdpa',
} as const;

export default async function LegalDocPage({
  params,
}: {
  params: Promise<LocaleParams & { doc: string }>;
}) {
  const locale = await localeFrom(params);
  const { doc } = await params;
  if (!isLegalDoc(doc)) notFound();

  const document = legalDocument(doc);

  return (
    <>
      <LegalPage locale={locale} document={document} />
      {/*
        `WebPage` plus a trail. `dateModified` appears only when `lastUpdated` does
        — a document that has never been published must not carry a modification
        date, which would be the structured-data form of the build-date bug
        `src/content/legal.ts` exists to prevent.
      */}
      <JsonLd nodes={[legalPage(locale, document), breadcrumbs(legalCrumbs(locale, doc))]} />
    </>
  );
}
