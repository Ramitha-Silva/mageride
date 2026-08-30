import { cx } from '@mageride/ui';

import { plateSize, type ScreenEntry } from '@/content/screens';

/**
 * One composited device plate.
 *
 * This is `portals/www/CLAUDE.md`'s *"the `next/image` contract (S06 · A16) — so
 * S15–S18 do not each invent one"*, written as a component so the remaining page
 * sessions compose it rather than re-deriving it. Every clause of that contract
 * holds here, and **one is deliberately departed from** — see the last section.
 *
 * - **Explicit `width` and `height`, read from the real file.** `plateSize()` looks
 *   the stem up in the map `scripts/screen-dimensions.mjs` generates from the
 *   committed images. S14 measured the output and found **eight distinct plate
 *   sizes**, with the phone frames alone splitting 34 at 416×777 and 26 at
 *   416×776 — so the constant the contract warns against would have given the
 *   wrong aspect ratio to twenty-six screens. A wrong ratio is either a squashed
 *   screenshot or a layout shift, and on A34's 3G-throttled mid-range Android the
 *   shift is the expensive one.
 * - **`alt` from the registry's `captionKey`, through the translator.** `alt` is on
 *   `mageride/no-literal-user-facing-strings`'s attribute list, so a literal here
 *   fails lint — the correct outcome: an `alt` is read aloud to somebody and
 *   belongs in every published language.
 * - **Hero frames eager and high priority; everything else lazy.** The caller
 *   decides, because "which are the heroes" is data in the registry
 *   (`HERO_SCREENS`) rather than a judgement each page makes again.
 * - **No JavaScript art direction.** When there is ever light/dark art direction it
 *   is a `prefers-color-scheme` `<source>` in the `<picture>` below. A `src` swap
 *   after hydration is a visible flash on a surface that hydrates as little as it
 *   can.
 *
 * ## The departure: `<picture>` rather than `next/image`
 *
 * The contract says "renders through `next/image`". It is served here by a plain
 * `<picture>`, and the reason is that S06 already did the optimiser's job:
 *
 *   - `public/screens/` holds **AVIF and WebP, at 1× and 2×**, composited by
 *     `scripts/compose-frames.mjs` to a measured budget, and `scripts/check-bundle.mjs`
 *     gates every one of them on every build (≤ 12 MB total, ≤ 220 kB each).
 *     Feeding an already-minimal AVIF through `/_next/image` to be re-encoded as
 *     AVIF spends CPU to produce a slightly worse file, and the file the budget
 *     gate measured is then not the file that ships.
 *   - `/_next/image` is a **request-time route**. This surface's defining property
 *     is the fourth MCS-34 negative — it renders with the entire platform down —
 *     and while the optimiser lives in the same container rather than in the
 *     platform, it is still a code path that has to run for a picture to appear.
 *     A `<picture>` over static files has none.
 *
 * `<picture>` also gives exactly what `next/image` would have: `type`-negotiated
 * AVIF with a WebP floor, density switching via `srcSet`, reserved space from
 * `width`/`height`, native lazy loading, and `fetchPriority` for the hero. The
 * `images.formats` setting in `next.config.ts` stays as it is — it governs any
 * image that *does* go through the optimiser later.
 *
 * ## The light-plate constraint, which is a real design limit and not a nit
 *
 * **Every plate is a light capture on a light surface**, because the wireframes
 * cannot be rendered dark honestly — 231 rules across the seven stylesheets
 * hard-code a light hex, and overriding `:root` produces grey-on-white body copy
 * that fails WCAG contrast. So `appearances` is `['light']` for all 70 entries.
 *
 * The consequence for a **dark** page: these must sit on their own light surface —
 * a card, the way a printed screenshot sits on paper — rather than as a bright
 * rectangle directly on a dark section. That is what `plate` does, and it defaults
 * to on so a page has to opt *out* of the correct thing. It becomes unnecessary by
 * itself if the wireframes are ever tokenised.
 */
export function ScreenImage({
  screen,
  alt,
  priority = false,
  sizes,
  plate = true,
  className,
  imageClassName,
}: {
  readonly screen: ScreenEntry;
  /**
   * The caption, already translated (MCS-36 D3).
   *
   * A resolved string rather than a `locale`, because this component is rendered
   * inside five client components and a translator here put the whole resource table
   * in every one of their bundles. `alt` is on the shared ESLint rule's attribute
   * list, so a literal at a call site still fails lint — the string has to come from
   * `screen.captionKey` through a translator, just one boundary further out.
   */
  readonly alt: string;
  /** `true` only for a frame above the fold on first paint. */
  readonly priority?: boolean;
  readonly sizes?: string;
  /** The light card these light captures need on a dark page. */
  readonly plate?: boolean;
  readonly className?: string;
  readonly imageClassName?: string;
}) {
  const { width, height } = plateSize(screen);
  const stem = `/screens/${screen.file}`;

  return (
    <div
      className={cx(
        plate &&
          // Not a `dark:` variant: the card is light in *both* appearances, because
          // the image inside it is light in both. A card that followed the theme
          // would put a light screenshot on a dark card, which is the exact thing
          // this exists to prevent.
          'rounded-card bg-white p-sm shadow-elevation-2',
        className,
      )}
    >
      <picture>
        <source
          type="image/avif"
          srcSet={`${stem}.avif 1x, ${stem}@2x.avif 2x`}
          sizes={sizes}
        />
        <source
          type="image/webp"
          srcSet={`${stem}.webp 1x, ${stem}@2x.webp 2x`}
          sizes={sizes}
        />
        {/*
          The `<img>` is the element that actually lays out, so it carries the
          dimensions, the alt text and the loading hints. Its `src` is the WebP
          rather than the AVIF: an engine that ignored both `<source>`s is by
          definition one that did not match `image/avif`, and WebP is the universal
          floor (every engine has shipped it since Safari 14).
        */}
        <img
          src={`${stem}.webp`}
          alt={alt}
          width={width}
          height={height}
          loading={priority ? 'eager' : 'lazy'}
          fetchPriority={priority ? 'high' : 'auto'}
          decoding="async"
          className={cx('h-auto w-full', imageClassName)}
        />
      </picture>
    </div>
  );
}
