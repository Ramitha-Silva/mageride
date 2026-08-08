/**
 * `@mageride/tailwind-preset` — the single home for the D2' §0.2 design tokens
 * on the web (AL-52).
 *
 * Three things ship from here:
 *
 *  - `theme.css`   the Tailwind v4 `@theme` layer every surface imports. This
 *                  is the consumption path; see `src/theme-css.ts` for why.
 *  - `mageridePreset`  the `tailwind.config` preset AL-52 names, for a surface
 *                  that keeps a JS config.
 *  - the tokens    as data, so non-CSS consumers (a MapLibre style, a chart, a
 *                  PDF export) read the same hexes rather than re-typing them.
 */

export * from './tokens.js';
export { mageridePreset, type MageridePreset, type FontSizeEntry } from './preset.js';
export { renderThemeCss } from './theme-css.js';
export { renderSmokePage, CTA_CLASS_NAMES } from './smoke-page.js';
