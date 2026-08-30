import type { Metadata, Viewport } from 'next';
import { Inter, Noto_Sans_Sinhala, Noto_Sans_Tamil, Outfit } from 'next/font/google';
import { notFound } from 'next/navigation';

import { Footer } from '@/components/nav/Footer';
import { Header } from '@/components/nav/Header';
import { headerLabels } from '@/components/nav/headerLabels';
import { JsonLd } from '@/components/seo/JsonLd';
import { appearanceScript } from '@/lib/appearance';
import { createWwwTranslator, DEFAULT_LOCALE, isWwwLocale, WWW_LOCALES, type Locale } from '@/i18n';
import { organization, webSite } from '@/lib/json-ld';
import { metadataForRoute, SITE_ORIGIN } from '@/lib/seo';

import '../globals.css';

/**
 * The skip link's target, and the `<main>` landmark's id. One constant because the
 * two must agree and they are written eighty lines apart.
 */
const MAIN_CONTENT_ID = 'main-content';

/**
 * The document, and the **root layout** — there is no `app/layout.tsx` above this
 * one.
 *
 * That is the whole reason the locale is a path segment rather than a query
 * parameter, and it is the one place this surface deliberately diverges from
 * `web-passenger`. That subview uses `?lang=` and explains why: an SMS carries one
 * URL, minted by notification-svc before anybody knew what language the recipient
 * reads, so the language cannot be in the path. **This surface is the opposite
 * case.** It is indexed, it needs reciprocal `hreflang` (A32), and a search engine
 * has to be able to treat the Sinhala and English readings of `/drivers` as two
 * canonical documents. A query parameter cannot be either of those things. So the
 * locale is a segment, `<html lang>` is that segment rather than a header read, and
 * every page below here is statically renderable.
 *
 * **The fonts are self-hosted, not linked.** D2 §0.2 names Outfit (display) and
 * Inter (body); `next/font/google` downloads and serves all four faces below from
 * this origin. A CDN `<link>` would put a third party in the render path and is
 * incompatible with the strict-CSP posture AL-52 was chosen for.
 */

/*
 * The two Latin faces D2 §0.2 names.
 *
 * They bind to `--mr-font-*-latin` rather than to the preset's `--mr-font-outfit` /
 * `--mr-font-inter` directly, because `app/globals.css` composes those two per
 * locale out of a Latin face and a script face — and a variable defined in terms of
 * itself is a cycle that resolves to nothing. The preset's tokens still read
 * `--mr-font-outfit`; what changed is who supplies it.
 */
const outfit = Outfit({
  subsets: ['latin'],
  variable: '--mr-font-outfit-latin',
  display: 'swap',
});

const inter = Inter({
  subsets: ['latin'],
  variable: '--mr-font-inter-latin',
  display: 'swap',
});

/**
 * The two script faces (S04 · A13), and the reason they exist.
 *
 * `portals/web-passenger/app/layout.tsx` says in its own words that neither Outfit
 * nor Inter carries a Sinhala or a Tamil glyph, and that those subsets fall through
 * to the reader's system face. On a token-gated utility page opened from an SMS
 * that is the right answer. **On a hero setting 48–72px Sinhala it is a visibly
 * unfinished page beside the English cut**, and this surface is the one whose whole
 * job is the first impression.
 *
 * Three deliberate settings:
 *
 *   - **`subsets` is the script alone**, not `['latin', 'sinhala']`. Outfit and
 *     Inter sit ahead of these in every font stack, so Noto is never asked to draw
 *     a Latin character and there is no reason to ship the glyphs for it.
 *   - **`preload: false`**, which is the one uncomfortable trade here. `next/font`
 *     preloads per *module graph*, not per rendered page, and all four faces are
 *     imported by this one shared layout — so preloading would put a Sinhala
 *     download in front of every Tamil and English page, on a budget (A34) written
 *     against a 3G-throttled mid-range Android. Without it the first paint of a
 *     Sinhala page uses the system Sinhala face for one swap, which is exactly what
 *     that reader sees on `web-passenger` today, and then Noto arrives. **S19 owns
 *     revisiting this** if per-locale preloading ever becomes expressible.
 *   - **`display: 'swap'`**, so that trade is a swap and never a blank.
 */
const notoSinhala = Noto_Sans_Sinhala({
  subsets: ['sinhala'],
  variable: '--mr-font-sinhala',
  display: 'swap',
  preload: false,
});

const notoTamil = Noto_Sans_Tamil({
  subsets: ['tamil'],
  variable: '--mr-font-tamil',
  display: 'swap',
  preload: false,
});

/**
 * The font variable classes — **all four, on every page** (corrected in S14).
 *
 * ## What this was, and the bug that changed it
 *
 * S04 applied only the current locale's script face, on the reasoning that "a
 * Tamil page never names Noto Sans Sinhala in a font stack, so a browser reading
 * it never fetches that face". That was right about page *copy* and wrong about
 * this site, because two components deliberately render **other** scripts on every
 * page:
 *
 *   - the **locale switcher**, whose links are endonyms — සිංහල · English — because
 *     a reader looking for their own language scans for their own script;
 *   - the **language band** in the footer, whose entire purpose is to show one
 *     sentence in all three at once, so that a reader who only ever sees their own
 *     language can see the app speaks three.
 *
 * Measured before the fix: on `/en` the Sinhala *and* Tamil band lines resolved to
 * `Inter, Inter Fallback, ui-sans-serif` — no script face at all — and on `/si` the
 * Tamil line resolved to the Sinhala stack. Every page rendered two of the three
 * scripts in whatever the system happened to have, which is the "visibly
 * unfinished page" S04 added these faces to prevent, in the one component that
 * exists to prove the opposite.
 *
 * ## Why this is not the bandwidth regression it looks like
 *
 * Declaring a CSS variable does not fetch a font. `next/font` emits a
 * `unicode-range` per subset, and a browser downloads a face only when a glyph it
 * actually renders falls in that range *and* a stack in scope names the family. So
 * a page that renders no Sinhala still fetches no Sinhala — the download on `/en`
 * happens because the footer genuinely displays Sinhala text, which is the correct
 * reason for it to happen.
 *
 * `preload: false` (above) still holds, so these arrive after first paint and
 * behind everything above the fold, which is where the band is.
 *
 * **S04's verification note is superseded**: `/si` no longer loads "Noto Sans
 * Sinhala only" and `/en` no longer loads "neither". Recorded in
 * `portals/www/CLAUDE.md`.
 *
 * Noto Sans Tamil is applied while MCS-34 D2's deferral stands (S13) for the same
 * reason it is still imported: the band renders a Tamil line on every page, and
 * re-enabling Tamil must stay a one-constant change.
 */
function fontClassNames(): string {
  return [outfit.variable, inter.variable, notoSinhala.variable, notoTamil.variable].join(' ');
}

/**
 * The document's metadata — **the defaults every page below inherits or overrides.**
 *
 * `generateMetadata` and not a static `metadata` export, because the strings are
 * resources: a Sinhala reader's tab should not say "One live picture of how Sri
 * Lanka moves" in English, and a search result is the one piece of copy on this
 * site that a reader sees *before* choosing to visit it.
 *
 * Four things are settled here and nowhere else:
 *
 *   - **`metadataBase`.** Without it every relative URL Next composes — canonical,
 *     `og:image`, `og:url` — is emitted relative, and a relative canonical is a
 *     canonical a crawler resolves against whatever host it happened to reach. It
 *     is a constant, and `src/lib/seo.ts` explains why it has to be.
 *   - **The title template.** `%s · MageRide`, with the brand alone as the default,
 *     so thirteen pages do not each remember to append it and the home page does
 *     not read "MageRide · MageRide".
 *   - **`robots: { index: true }`.** The inverse of every other MageRide portal,
 *     stated out loud rather than left to a default, because being indexed is this
 *     surface's purpose and the *other three* are the ones whose setting is a
 *     safety property.
 *   - **The layout's own `hreflang` set**, for `/si` and `/en` themselves. Every
 *     page below supplies its own through `metadataForRoute`.
 */
export async function generateMetadata({
  params,
}: {
  params: Promise<{ locale: string }>;
}): Promise<Metadata> {
  const { locale } = await params;
  const resolved = isWwwLocale(locale) ? locale : DEFAULT_LOCALE;
  const t = createWwwTranslator(resolved);

  return {
    ...metadataForRoute(resolved, ''),
    metadataBase: new URL(SITE_ORIGIN),
    title: { default: t('www.brand.name'), template: `%s · ${t('www.brand.name')}` },
    robots: { index: true, follow: true },
  };
}

export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
  // D2 §0.2 `primary`.
  themeColor: '#FF6D00',
};

/*
 * The pre-paint appearance script moved to `src/lib/appearance.ts` in S19, with the
 * storage key it now reads — the script and `ThemeToggle` have to agree on that key
 * and they are written two hundred lines and one module apart.
 *
 * Resolution order is stored preference → `prefers-color-scheme` → light. The
 * stored half is **permitted here and on no other MageRide surface**: `web-passenger`
 * is under D6′ I-29.1's "no cookies, no localStorage of ride data" because it holds
 * somebody's live ride; this page holds nothing. It is still **not a cookie** —
 * nothing is sent to a server, so there is nothing to put a banner in front of (A36).
 */

/**
 * The **published** locales, pre-rendered. Every page below this layout is static:
 * nothing on this surface reads a header, a cookie or a search param, which is what
 * lets the whole site be served from a cache with the platform down.
 *
 * `WWW_LOCALES` is `['si', 'en']` while MCS-34 D2's Tamil deferral stands, so this
 * generates two params rather than three and `/ta/…` is never built. See the
 * constant in `src/i18n/index.ts` — it is the one line that reverses this.
 */
export function generateStaticParams(): { locale: Locale }[] {
  return WWW_LOCALES.map((locale) => ({ locale }));
}

/**
 * An unknown segment is a 404 and never a fallback to Sinhala.
 *
 * `/de/drivers` is not the Sinhala drivers page — it is an address this site does
 * not publish, and answering it with content would give a crawler a second URL for
 * a document that already has a canonical one. `dynamicParams = false` makes Next
 * refuse it before this component runs; the guard below is the same statement for
 * anything that reaches the component anyway (a dev-server request, a test).
 *
 * **`/ta/drivers` is in that set today** (S13). It is a real language on a
 * trilingual platform, and it is still not an address this surface publishes —
 * answering it with English prose under `lang="ta"` would be a worse failure than
 * the 404, because a screen reader would hand English words to a Tamil voice.
 */
export const dynamicParams = false;

export default async function LocaleLayout({
  children,
  params,
}: {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
}) {
  const { locale } = await params;
  if (!isWwwLocale(locale)) notFound();

  const t = createWwwTranslator(locale);

  return (
    <html lang={locale} className={fontClassNames()} suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: appearanceScript() }} />

        {/*
          `Organization` and `WebSite`, once per document. In the layout rather than
          repeated per page because they describe the site rather than the page, and
          because thirteen pages each emitting their own is thirteen chances for one
          to disagree.

          **No `SearchAction`.** The usual `WebSite` block carries one, and this site
          has no search endpoint of any kind — a `potentialAction` pointing at a URL
          that does not exist is a false declaration in a machine-readable format.
          `src/lib/json-ld.ts` records the decision.
        */}
        <JsonLd nodes={[organization(locale), webSite(locale)]} />
      </head>
      {/*
        `min-h-dvh` and not `min-h-screen`: on a phone `100vh` is the viewport with
        the browser's collapsing address bar counted out, so a full-height layout
        jumps as the bar hides. Sri Lanka's median device is a phone and this page
        is a phone page first (A34).

        `overflow-x-clip` and not `overflow-x-hidden`: the two look identical and
        only one of them is safe. `hidden` makes the body a scroll container, which
        is what silently breaks `position: sticky` on a descendant — and S14's
        header and S17's chapter rail are both sticky. `clip` refuses the overflow
        without becoming one.
      */}
      <body className="flex min-h-dvh flex-col overflow-x-clip">
        {/*
          The skip link (S14). First focusable thing in the document, invisible
          until focused, and the reason it is here rather than in a component: it
          has to be the *first* tab stop on every page, and a component that any
          page could forget to render is a component some page eventually forgets.

          `sr-only focus:not-sr-only` is the standard pair — present in the
          accessibility tree at all times, painted only when focused. A skip link
          that is `display: none` until focus cannot be focused at all, which is
          the usual way this control is shipped broken.
        */}
        <a
          href={`#${MAIN_CONTENT_ID}`}
          className={
            'sr-only rounded-sm bg-primary px-sm py-xs mr-on-primary ' +
            'focus:not-sr-only focus:absolute focus:top-2 focus:left-2 focus:z-50'
          }
        >
          {t('www.a11y.skipToContent')}
        </a>

        <Header labels={headerLabels(locale)} />

        {/*
          One `<main>` per page, and it is here rather than in each page so that
          thirteen pages cannot disagree about the landmark. `tabIndex={-1}` makes
          it a valid target for the skip link — without it the browser moves the
          *scroll* to the anchor but leaves focus in the link, so the next Tab
          returns to the header and the skip link has skipped nothing.
        */}
        <main id={MAIN_CONTENT_ID} tabIndex={-1} className="flex-1 focus-visible:outline-none">
          {children}
        </main>

        <Footer locale={locale} />
      </body>
    </html>
  );
}
