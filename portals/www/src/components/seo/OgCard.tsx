import type { ReactElement } from 'react';

import { SITE_ORIGIN } from '@/lib/seo';

/**
 * The Open Graph card — one design, four families, **rendered in English in every
 * locale.**
 *
 * ## The font finding, which is what decided this
 *
 * S19 asks for per-family OG cards and anticipates exactly this outcome: *"Fonts
 * must be loadable at edge runtime — if Noto Sans Sinhala is too large to embed,
 * render OG cards in English for all locales and record that; a broken OG image is
 * worse than an English one."*
 *
 * It is not a size problem. It is an availability one, and it is worse:
 *
 *   - **`next/og` (satori) accepts TTF, OTF and WOFF, and not WOFF2.** Every font
 *     file this repository can reach is WOFF2. The two capture-time font packages
 *     in `devDependencies` ship `.woff2` only, and `next/font/google`'s build cache
 *     under `.next/static/media/` is WOFF2 with content-hashed names — not a path
 *     anything may depend on.
 *   - **There is no Noto Sans Sinhala on this host in any format.** The face the
 *     site serves is fetched by `next/font/google` at build time and never lands as
 *     a file this module could read.
 *   - Those two packages are **devDependencies**, and `test/fences.test.ts` asserts
 *     the site never imports either — importing a font file here to feed satori
 *     would break that fence for a picture. (Named by description rather than by
 *     package name on purpose: that fence is a raw text sweep, so spelling the name
 *     in a comment about not using it is what trips it. CLAUDE.md, *rules for a
 *     page or a component*.)
 *
 * So the choice was: commit a Sinhala TTF to the repository purely for card
 * rendering, or render the cards in English. **English, per S19's own instruction**,
 * because the failure mode of the alternative is not "an English card" — it is
 * *tofu*, a row of empty boxes where the Sinhala title should be, on the one image
 * a reader sees before they decide whether to click. A card that renders is worth
 * more than a card that is in the right language and unreadable.
 *
 * **What would change this:** a Sinhala TTF or WOFF committed under `public/brand/`
 * (or fetched at build), passed to `ImageResponse`'s `fonts` option. The card
 * design below already takes its strings as props, so that is a one-file change and
 * no redesign.
 *
 * ## The design
 *
 * Deliberately typographic and image-free. The screen plates are the obvious thing
 * to put on a card and they are the wrong thing: they are 416px-wide device frames,
 * they are light-only, and at 1200×630 they would be a postage stamp on a slab of
 * colour. What a link preview has to do is say which document this is, and a title
 * over a brand does that at thumbnail size.
 *
 * Colours are D2 §0.2's, written as literals because **satori resolves no CSS
 * variables and no Tailwind** — it lays out inline styles only. Every value here is
 * copied from `portals/tailwind-preset/src/tokens.ts` and named in the comment
 * beside it, which is the same arrangement `scripts/compose-frames.mjs` uses for the
 * plates and for the same reason: the token module is the source, this is a
 * transcription, and a D2 change means updating both.
 */

/** D2 §0.2 `primary`. */
const PRIMARY = '#FF6D00';
/** D2 §0.2 `on-surface`, dark variant — the card is always dark. */
const ON_SURFACE = '#E3E2E6';
/** D2 §0.2 `on-surface-variant`, dark variant. */
const ON_SURFACE_VARIANT = '#C3C7CF';
/** D2 §0.2 `background`, dark variant. */
const BACKGROUND = '#121316';

export const OG_SIZE = { width: 1200, height: 630 } as const;
export const OG_CONTENT_TYPE = 'image/png';

/** The canonical host, without its scheme. `SITE_ORIGIN` is where it is decided. */
const OG_HOST = SITE_ORIGIN.replace(/^https?:\/\//, '');

/**
 * One card.
 *
 * `eyebrow` names the family, `title` is the document. **Both arrive already
 * translated — into English — and every one of them comes from the resource table**
 * rather than from a literal at the call site.
 *
 * That distinction is the whole of it, and the lint rule was right to insist on it.
 * "The card is English" is a *font* finding; it is not a licence to retype copy
 * that already exists. Every caller reads its strings through
 * `createWwwTranslator(FALLBACK_LOCALE)` from the same key the page renders, so a
 * card cannot describe a page by a name the page stopped using — and the day a
 * Sinhala face can be embedded, the callers change one argument and the cards
 * become trilingual with no redesign.
 */
export function OgCard({
  brand,
  eyebrow,
  title,
  tagline,
}: {
  /** `www.brand.name`, resolved by the caller. */
  readonly brand: string;
  readonly eyebrow: string;
  readonly title: string;
  readonly tagline?: string;
}): ReactElement {
  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        justifyContent: 'space-between',
        width: '100%',
        height: '100%',
        backgroundColor: BACKGROUND,
        padding: 72,
        // satori has no default font-family resolution beyond what it is given;
        // the family name here is the built-in one `next/og` ships.
        fontFamily: 'sans-serif',
      }}
    >
      {/* The bar is the brand mark this site does not have yet. */}
      <div style={{ display: 'flex', width: 120, height: 10, backgroundColor: PRIMARY }} />

      <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
        <div
          style={{
            display: 'flex',
            fontSize: 28,
            letterSpacing: 2,
            textTransform: 'uppercase',
            color: PRIMARY,
          }}
        >
          {eyebrow}
        </div>
        <div
          style={{
            display: 'flex',
            fontSize: title.length > 42 ? 64 : 80,
            fontWeight: 700,
            lineHeight: 1.1,
            color: ON_SURFACE,
          }}
        >
          {title}
        </div>
        {tagline ? (
          <div style={{ display: 'flex', fontSize: 32, color: ON_SURFACE_VARIANT }}>{tagline}</div>
        ) : null}
      </div>

      {/* The brand, then the host. `OG_HOST` is a hostname, not prose. */}
      <div style={{ display: 'flex', fontSize: 30, fontWeight: 700, color: ON_SURFACE }}>
        {`${brand} · ${OG_HOST}`}
      </div>
    </div>
  );
}
