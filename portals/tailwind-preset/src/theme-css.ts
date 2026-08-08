/**
 * Renders `dist/theme.css` — the Tailwind v4 `@theme` layer every MageRide web
 * surface imports — from `tokens.ts`.
 *
 * Why a generated stylesheet and not only the JS preset (AL-52 names a preset):
 * Tailwind v4 reads its theme from CSS. A JS config loaded through `@config`
 * still works (and `preset.ts` ships one), but its `theme.screens` *merges*
 * with v4's built-in breakpoints instead of replacing them, so `sm:` would
 * keep meaning 640px alongside D2's 375px. AL-52 says the D2 breakpoints
 * "become the Tailwind `screens` config" — the only way to make that true on
 * v4 is `--breakpoint-*: initial` in CSS. Both files are generated from the
 * same tokens and `test/parity.test.ts` holds them to each other.
 */

import {
  BREAKPOINTS,
  BREAKPOINT_ALIASES,
  CTA,
  DARK_CLASS,
  ELEVATIONS,
  ELEVATION_ALIASES,
  FONT_FAMILIES,
  MODE_COLORS,
  RADII,
  SEMANTIC_COLORS,
  SPACING,
  TYPE_ROLES,
  VEHICLE_COLORS,
  semanticColorVar,
  type SemanticColorName,
} from './tokens.js';

const BANNER = `/*
 * @mageride/tailwind-preset — D2' §0.2 design tokens (AL-52).
 *
 * GENERATED FILE — do not edit. Source: src/tokens.ts, emitted by
 * \`npm run build\` in portals/tailwind-preset. Edit the tokens, not this.
 *
 * Usage (every MageRide web surface):
 *
 *   @import "tailwindcss";
 *   @import "@mageride/tailwind-preset/theme.css";
 *
 * Dark mode is the \`.${DARK_CLASS}\` class on a document ancestor (normally
 * <html>). It drives both halves at once: the raw \`--mr-color-*\` values below
 * flip to their D2 dark hex, and the \`dark:\` variant matches. A surface that
 * wants to follow the OS setting sets the class from
 * \`prefers-color-scheme\` — the two must never be wired independently or a
 * \`dark:\` utility would disagree with the token underneath it.
 */`;

function block(selector: string, lines: string[]): string {
  return `${selector} {\n${lines.map((l) => `  ${l}`).join('\n')}\n}`;
}

function semanticVarLines(appearance: 'light' | 'dark'): string[] {
  return (Object.keys(SEMANTIC_COLORS) as SemanticColorName[]).map((name) => {
    const token = SEMANTIC_COLORS[name];
    return `${semanticColorVar(name)}: ${token[appearance]}; /* ${token.d2} */`;
  });
}

/** Renders the whole stylesheet. Pure — the build script just writes what it returns. */
export function renderThemeCss(): string {
  const sections: string[] = [BANNER];

  // --- Appearance-dependent raw values -------------------------------------
  // Held one level below the theme so a single class flips all sixteen roles.
  sections.push(
    [
      '/* D2 §0.2 brand & semantic colours — light appearance. */',
      block(':root', semanticVarLines('light')),
      '',
      '/* …and the dark appearance. Same roles, D2 "Dark hex" column. */',
      block(`.${DARK_CLASS}`, semanticVarLines('dark')),
    ].join('\n'),
  );

  // --- dark: as a class variant -------------------------------------------
  sections.push(
    [
      '/*',
      ' * Tailwind v4 defaults `dark:` to `prefers-color-scheme`. The colour',
      ' * tokens above key off a class, so the variant is re-pointed at the same',
      ' * class — otherwise `dark:bg-surface` and `bg-surface` could resolve for',
      ' * different reasons and disagree.',
      ' */',
      `@custom-variant dark (&:where(.${DARK_CLASS}, .${DARK_CLASS} *));`,
    ].join('\n'),
  );

  // --- Theme: values that reference the appearance variables ---------------
  // `inline` matters: without it Tailwind emits `var(--color-primary)`, whose
  // own `var(--mr-color-primary)` would already have been resolved against
  // :root, and no descendant `.dark` could change it. `inline` puts
  // `var(--mr-color-primary)` straight into the utility, where it resolves on
  // the element and picks the nearest `.dark` ancestor up.
  sections.push(
    [
      '/* Semantic colours — resolved on the element so `.dark` reaches them. */',
      block(
        '@theme inline',
        (Object.keys(SEMANTIC_COLORS) as SemanticColorName[]).map(
          (name) => `--color-${name}: var(${semanticColorVar(name)});`,
        ),
      ),
    ].join('\n'),
  );

  // --- Theme: appearance-independent values --------------------------------
  const themeLines: string[] = [];

  themeLines.push('/* Vehicle-type marker legend (MAP-03, AL-09) — 11 tokens, one hex each. */');
  for (const [name, token] of Object.entries(VEHICLE_COLORS)) {
    themeLines.push(`--color-${name}: ${token.hex}; /* ${token.d2} ${token.name} */`);
  }

  themeLines.push('');
  themeLines.push('/* Mode badges. */');
  for (const [name, token] of Object.entries(MODE_COLORS)) {
    themeLines.push(`--color-${name}: ${token.hex}; /* Mode ${token.mode} */`);
  }

  themeLines.push('');
  themeLines.push('/* Font families — Outfit for display/headline, Inter for body. */');
  themeLines.push(`--font-display: ${FONT_FAMILIES.display};`);
  themeLines.push(`--font-body: ${FONT_FAMILIES.body};`);
  themeLines.push('/* Unstyled text is body text. */');
  themeLines.push(`--font-sans: ${FONT_FAMILIES.body};`);

  themeLines.push('');
  themeLines.push('/* Type scale — the D2 role table. Line heights: see tokens.ts. */');
  for (const [name, role] of Object.entries(TYPE_ROLES)) {
    themeLines.push(`--text-${name}: ${role.fontSize}; /* ${role.androidM3} / ${role.iosDynamicType} */`);
    themeLines.push(`--text-${name}--line-height: ${role.lineHeight};`);
    themeLines.push(`--text-${name}--font-weight: ${role.fontWeight};`);
  }

  themeLines.push('');
  themeLines.push('/* Named steps on D2\'s 4px grid. The numeric scale is that grid already. */');
  for (const [name, value] of Object.entries(SPACING)) {
    themeLines.push(`--spacing-${name}: ${value};`);
  }
  themeLines.push('/* D2 CTA control height (56dp) — `h-cta`. */');
  themeLines.push(`--spacing-cta: ${CTA.height};`);
  themeLines.push('/* D2 CTA optional leading/trailing icon box (20dp). */');
  themeLines.push(`--spacing-cta-icon: ${CTA.iconSize};`);

  themeLines.push('');
  themeLines.push('/* Corner radius. These override Tailwind\'s sm/md/lg with the D2 values. */');
  for (const [name, value] of Object.entries(RADII)) {
    themeLines.push(`--radius-${name}: ${value};`);
  }

  themeLines.push('');
  themeLines.push('/* Elevation ladder — Android M3 dp levels rendered as web shadows. */');
  for (const [name, token] of Object.entries(ELEVATIONS)) {
    themeLines.push(`--shadow-${name}: ${token.shadow}; /* M3 ${token.dp}dp */`);
  }
  for (const [name, shadow] of Object.entries(ELEVATION_ALIASES)) {
    themeLines.push(`--shadow-${name}: ${shadow};`);
  }

  themeLines.push('');
  themeLines.push('/* Breakpoints. AL-52: the D2 three BECOME the screens config, so the');
  themeLines.push('   built-in scale is cleared first — `sm:` must not still mean 640px. */');
  themeLines.push('--breakpoint-*: initial;');
  for (const [name, value] of Object.entries(BREAKPOINTS)) {
    themeLines.push(`--breakpoint-${name}: ${value};`);
  }
  themeLines.push('/* Conventional aliases for the same three widths. */');
  for (const [name, value] of Object.entries(BREAKPOINT_ALIASES)) {
    themeLines.push(`--breakpoint-${name}: ${value};`);
  }

  sections.push(block('@theme', themeLines));

  return `${sections.join('\n\n')}\n`;
}
