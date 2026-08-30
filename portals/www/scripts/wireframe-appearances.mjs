/**
 * The dark `:root` override for each wireframe family — data, not logic.
 *
 * A separate file from `capture-screens.mjs` because C134's README asks for it: a
 * D2 token change should be a one-file edit, and a colour table buried in a
 * Playwright script is a colour table nobody finds.
 *
 * ## Read this before using it
 *
 * **Nothing in the registry sets `appearances: ['dark']` today, so nothing here
 * runs.** It is complete, it is correct as far as it goes, and it does not
 * currently produce a publishable image. `src/content/screens.ts` carries the full
 * finding; the short version is that the wireframes hard-code light surface hexes
 * in 231 stylesheet rules (`.card{background:#fff}`, `.sheet`, `.map`), so
 * overriding `:root` repaints the text and not the surfaces — rendered, it is
 * grey-on-white body copy that fails WCAG contrast.
 *
 * This file is kept rather than deleted because the fix is not in this component.
 * If `specs/wireframes/*.html` is ever tokenised so that every colour is a
 * `var(--…)`, dark captures become a matter of adding `'dark'` to an entry's
 * `appearances`, with no change to the script and no change here.
 *
 * ## Where the values come from
 *
 * `portals/tailwind-preset/src/tokens.ts`, the module that is "the ONLY place a
 * D2 §0.2 value is spelled on the web". Its `SEMANTIC_COLORS` entries each carry a
 * `d2` name — `{ d2: 'onSurfaceVariant', light: '#44474B', dark: '#C3C7CF' }` — and
 * the wireframes' custom properties are spelled with exactly those D2 names. The
 * mapping below is therefore token-for-token, and every `dark` value is quoted from
 * that table rather than chosen here.
 *
 * They are transcribed rather than imported because this script must run without
 * building the preset: `dist/` is gitignored and generated on demand, and a
 * screenshot tool that first compiles a Tailwind preset is a screenshot tool that
 * breaks for reasons unrelated to screenshots. `test/content.test.ts` (S20) is the
 * right place to assert the two agree.
 *
 * ## Why three families and not one map
 *
 * The seven files do not share a palette contract:
 *
 * - The **Android** files use D2's full Material 3 role set.
 * - The **iOS** files add `--iosGreen` / `--iosBlue` and give `--success` /
 *   `--warning` / `--error` Apple's system colours rather than D2's. D2 has no iOS
 *   system-colour token, so those three are carried across unchanged — inventing a
 *   dark iOS system green here would be inventing a value Apple publishes and D2
 *   does not.
 * - The **portal** files use `--bg` and `--ink`, have no `--onPrimary`, and — the
 *   trap — set `--surface:#FFFFFF` as the *card* over `--bg:#EEF0F3` as the page.
 *   That is the inverse of the mobile files, where `--background` is the white and
 *   `--surface` the grey. Mapping `surface → surface.dark` for both would give the
 *   portals a card darker than the page it sits on.
 *
 * The vehicle (`--veh*`) and mode (`--mode*`) tokens appear in no family below.
 * That is deliberate and is D2's rule, not an omission: each is one hex for both
 * appearances, because the map legend must mean the same thing in either.
 */

/** @typedef {'android' | 'ios' | 'portal'} WireframeFamily */

/**
 * Which palette contract each wireframe file follows.
 *
 * `web_passenger` is a portal by palette even though it renders a phone-width view:
 * it declares `--bg` and `--ink` like the two back-office files and unlike the four
 * app files, which is the only thing this table is about.
 *
 * @type {Readonly<Record<string, WireframeFamily>>}
 */
export const WIREFRAME_FAMILY = Object.freeze({
  passenger_android: 'android',
  driver_android: 'android',
  passenger_ios: 'ios',
  driver_ios: 'ios',
  web_admin: 'portal',
  web_fleet: 'portal',
  web_passenger: 'portal',
});

/**
 * D2 §0.2 dark values, keyed by the custom-property name each family declares.
 *
 * @type {Readonly<Record<WireframeFamily, Readonly<Record<string, string>>>>}
 */
export const DARK_ROOT_TOKENS = Object.freeze({
  android: Object.freeze({
    '--primary': '#FFB68A',
    '--onPrimary': '#4A2300',
    '--primaryContainer': '#6A3500',
    '--onPrimaryContainer': '#FFDCC4',
    '--secondary': '#9FCAFF',
    '--secondaryContainer': '#00497D',
    '--background': '#121316',
    '--surface': '#1A1C1E',
    '--surfaceVariant': '#2A2D31',
    '--outline': '#43474E',
    '--onSurface': '#E3E2E6',
    '--onSurfaceVariant': '#C3C7CF',
    '--outlineVariant': '#8D9199',
    '--success': '#7FD89A',
    '--warning': '#FFCF6B',
    '--error': '#FFB4AB',
  }),

  ios: Object.freeze({
    '--primary': '#FFB68A',
    '--onPrimary': '#4A2300',
    '--primaryContainer': '#6A3500',
    '--onPrimaryContainer': '#FFDCC4',
    '--secondary': '#9FCAFF',
    '--secondaryContainer': '#00497D',
    '--background': '#121316',
    '--surface': '#1A1C1E',
    '--surfaceVariant': '#2A2D31',
    '--outline': '#43474E',
    '--onSurface': '#E3E2E6',
    '--onSurfaceVariant': '#C3C7CF',
    '--outlineVariant': '#8D9199',
    // `--success` / `--warning` / `--error` and the two `--ios*` tokens are Apple
    // system colours, which D2 does not publish a dark variant for. Left alone.
  }),

  portal: Object.freeze({
    '--primary': '#FFB68A',
    '--secondary': '#9FCAFF',
    '--secondaryContainer': '#00497D',
    // `--bg` is the page and `--surface` is the card — the inverse of the mobile
    // files. So `--bg` takes D2's `background` and `--surface` takes D2's
    // `surface`, which is the pair that keeps the card *lighter* than the page in
    // dark, as D2's elevation model requires.
    '--bg': '#121316',
    '--surface': '#1A1C1E',
    '--surfaceVariant': '#2A2D31',
    '--outline': '#43474E',
    '--onSurface': '#E3E2E6',
    '--onSurfaceVariant': '#C3C7CF',
    // `--ink` is the wireframes' own near-black for page furniture, with no D2
    // token behind it. It takes `on-surface`, the role it plays.
    '--ink': '#E3E2E6',
    '--success': '#7FD89A',
    '--warning': '#FFCF6B',
    '--error': '#FFB4AB',
  }),
});

/**
 * The stylesheet to inject for one appearance of one wireframe file.
 *
 * Returns `''` for `light` — the wireframes *are* light, so the light capture
 * injects no colour override at all and is a faithful rendering of the approved
 * file rather than a re-tint of it.
 *
 * @param {string} wireframe basename under `specs/wireframes/`
 * @param {'light' | 'dark'} appearance
 * @returns {string} CSS, or `''` when there is nothing to override
 */
export function appearanceCss(wireframe, appearance) {
  if (appearance === 'light') return '';

  const family = WIREFRAME_FAMILY[wireframe];
  if (!family) {
    throw new Error(
      `wireframe-appearances: no palette family for "${wireframe}" — add it to ` +
        'WIREFRAME_FAMILY before capturing it dark',
    );
  }

  const declarations = Object.entries(DARK_ROOT_TOKENS[family])
    .map(([property, value]) => `  ${property}: ${value};`)
    .join('\n');

  return `:root {\n${declarations}\n}\n`;
}
