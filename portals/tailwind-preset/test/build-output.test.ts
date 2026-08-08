/**
 * The Definition of Done, read off the build output.
 *
 *   "every colour, type, spacing and radius token in D2 §0.2 is expressed in
 *    the preset and resolvable in both light and dark"
 *   "a smoke page consuming the preset builds with zero runtime CSS-in-JS in
 *    the bundle"
 *
 * "Resolvable" is a claim about compiled CSS, not about a JavaScript object, so
 * this file reads `smoke/dist/smoke.css` — the stylesheet Tailwind actually
 * produced from the generated smoke page — and looks for a rule per token. The
 * `pretest` script builds it, so a stale artifact cannot pass.
 *
 * It also holds `dist/preset.js` (the JS config AL-52 names) against
 * `dist/theme.css`, since both are generated and either could drift alone.
 */

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { beforeAll, describe, expect, it } from 'vitest';

import { mageridePreset } from '../src/preset.js';
import {
  BREAKPOINTS,
  BREAKPOINT_ALIASES,
  CTA,
  ELEVATIONS,
  MODE_COLORS,
  RADII,
  SEMANTIC_COLORS,
  SPACING,
  TYPE_ROLES,
  VEHICLE_COLORS,
  semanticColorVar,
  type SemanticColorName,
} from '../src/tokens.js';

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

let css = '';
let themeCss = '';
let smokeHtml = '';

beforeAll(() => {
  css = readFileSync(resolve(packageRoot, 'smoke/dist/smoke.css'), 'utf8');
  themeCss = readFileSync(resolve(packageRoot, 'dist/theme.css'), 'utf8');
  smokeHtml = readFileSync(resolve(packageRoot, 'smoke/dist/index.html'), 'utf8');
});

/** The declaration block of the first rule with this exact selector. */
function ruleBody(selector: string): string {
  const index = css.indexOf(`${selector} {`);
  if (index === -1) return '';
  return css.slice(index, css.indexOf('}', index));
}

/** The contents of a top-level `:root`-style block, e.g. `.dark`. */
function blockBody(source: string, selector: string): string {
  const index = source.indexOf(`${selector} {`);
  if (index === -1) return '';
  return source.slice(index, source.indexOf('\n}', index));
}

describe('every D2 §0.2 colour resolves in both light and dark', () => {
  const names = Object.keys(SEMANTIC_COLORS) as SemanticColorName[];

  it.each(names)('%s has a light value, a dark value and a utility', (name) => {
    const token = SEMANTIC_COLORS[name];
    const variable = semanticColorVar(name);

    // Light: declared on :root, which is the block that is NOT inside .dark.
    expect(css).toContain(`${variable}: ${token.light};`);
    // Dark: the same custom property, redeclared under the dark class.
    expect(blockBody(css, '.dark')).toContain(`${variable}: ${token.dark};`);
    // …and a utility that reads it, so the flip actually reaches an element.
    expect(ruleBody(`.bg-${name}`)).toContain(`var(${variable})`);
  });

  it('declares exactly the sixteen roles in each appearance', () => {
    const dark = blockBody(css, '.dark');
    expect(dark.match(/--mr-color-[a-z-]+:/g)).toHaveLength(names.length);
  });
});

describe('every D2 §0.2 vehicle and mode colour resolves', () => {
  it.each(Object.entries(VEHICLE_COLORS))('%s is %s', (name, token) => {
    expect(css).toContain(`--color-${name}: ${token.hex};`);
    expect(ruleBody(`.bg-${name}`)).toContain(`var(--color-${name})`);
  });

  it.each(Object.entries(MODE_COLORS))('%s is %s', (name, token) => {
    expect(css).toContain(`--color-${name}: ${token.hex};`);
    expect(ruleBody(`.bg-${name}`)).toContain(`var(--color-${name})`);
  });
});

describe('every D2 §0.2 type, spacing, radius and elevation token resolves', () => {
  it.each(Object.entries(TYPE_ROLES))('text-%s', (name, role) => {
    expect(css).toContain(`--text-${name}: ${role.fontSize};`);
    expect(css).toContain(`--text-${name}--line-height: ${role.lineHeight};`);
    expect(css).toContain(`--text-${name}--font-weight: ${role.fontWeight};`);
    expect(ruleBody(`.text-${name}`)).toContain(`font-size: var(--text-${name})`);
  });

  it.each(Object.entries(SPACING))('spacing %s = %s', (name, value) => {
    expect(css).toContain(`--spacing-${name}: ${value};`);
    expect(ruleBody(`.p-${name}`)).toContain(`padding: var(--spacing-${name})`);
  });

  it.each(Object.entries(RADII))('radius %s = %s', (name, value) => {
    expect(css).toContain(`--radius-${name}: ${value};`);
    expect(ruleBody(`.rounded-${name}`)).toContain(`border-radius: var(--radius-${name})`);
  });

  it.each(Object.entries(ELEVATIONS))('%s', (name, token) => {
    // Declared in the theme layer…
    expect(themeCss).toContain(`--shadow-${name}: ${token.shadow};`);
    // …and inlined into the utility, which is how Tailwind v4 compiles shadows:
    // the offsets stay put and the colour is threaded through --tw-shadow-color
    // so `shadow-<colour>` can override it.
    const [offsets, color] = token.dp === 0 ? ['0 0', '#0000'] : [token.shadow.slice(0, token.shadow.indexOf(' rgb')), 'rgb(0 0 0 / 0.12)'];
    const rule = ruleBody(`.shadow-${name}`);
    expect(rule).toContain(`--tw-shadow: ${offsets} var(--tw-shadow-color, ${color})`);
    expect(rule).toContain('box-shadow:');
  });

  it("keeps Tailwind's numeric scale on D2's 4px base grid", () => {
    expect(css).toContain('--spacing: 0.25rem;');
  });
});

describe('the D2 breakpoints become the screens config', () => {
  it.each(Object.entries(BREAKPOINTS))('%s = %s compiles to a media query', (_name, value) => {
    expect(css).toContain(`@media (width >= ${value})`);
  });

  it("clears Tailwind's own scale so sm: cannot still mean 640px", () => {
    // The reset is the point of emitting theme.css at all — a JS-config preset
    // merges with the built-in breakpoints on Tailwind v4 instead of replacing
    // them, and a `sm:` that silently means 640px is worse than no `sm:`.
    expect(themeCss).toContain('--breakpoint-*: initial;');
    for (const stale of ['640px', '1280px', '1536px']) {
      expect(css).not.toContain(`@media (width >= ${stale})`);
    }
    expect(new Set(Object.values(BREAKPOINT_ALIASES))).toEqual(
      new Set(Object.values(BREAKPOINTS)),
    );
  });
});

describe('the CTA primitive matches the D2 CTA token', () => {
  it('is 56px tall', () => {
    expect(css).toContain(`--spacing-cta: ${CTA.height};`);
    expect(ruleBody('.h-cta')).toContain('height: var(--spacing-cta)');
  });

  it('has radius sm 8', () => {
    expect(css).toContain(`--radius-sm: ${CTA.radius};`);
    expect(ruleBody('.rounded-sm')).toContain('border-radius: var(--radius-sm)');
  });

  it('is `primary` background with an `onPrimary` label', () => {
    expect(ruleBody(`.bg-${CTA.background}`)).toContain(`var(${semanticColorVar('primary')})`);
    expect(ruleBody(`.text-${CTA.label}`)).toContain(`var(${semanticColorVar('on-primary')})`);
  });

  it('sets the label at the titleMedium role — 16px, weight 600', () => {
    const role = TYPE_ROLES[CTA.labelRole];
    expect(css).toContain(`--text-${CTA.labelRole}: ${role.fontSize};`);
    expect(css).toContain(`--text-${CTA.labelRole}--font-weight: ${role.fontWeight};`);
    expect(role.fontSize).toBe('16px');
    expect(role.fontWeight).toBe('600');
  });

  it('gives the optional icon D2\'s 20dp box', () => {
    expect(css).toContain(`--spacing-cta-icon: ${CTA.iconSize};`);
  });
});

describe('the smoke bundle', () => {
  it('is CSS and markup only — there is no script to inject styles from', () => {
    // The strongest form of "zero runtime CSS-in-JS in the bundle": the bundle
    // contains no JavaScript at all, and exactly one stylesheet, the one PostCSS
    // compiled.
    expect(smokeHtml).not.toMatch(/<script/i);
    expect(smokeHtml.match(/<link[^>]+rel="stylesheet"/g)).toHaveLength(1);
    expect(smokeHtml).toContain('href="./smoke.css"');
  });

  it('compiled a real stylesheet, not an empty one', () => {
    expect(css.length).toBeGreaterThan(10_000);
    expect(css).toContain('@layer utilities');
  });

  it('carries no runtime style-injection marker', () => {
    for (const marker of ['data-styled', 'data-emotion', 'sc-component-id', '__jsx-']) {
      expect(css).not.toContain(marker);
      expect(smokeHtml).not.toContain(marker);
    }
  });
});

describe('the JS preset and theme.css agree', () => {
  it('carries the same colour values', () => {
    const colors = mageridePreset.theme.extend.colors;
    for (const name of Object.keys(SEMANTIC_COLORS) as SemanticColorName[]) {
      expect(colors[name]).toBe(`var(${semanticColorVar(name)})`);
      expect(themeCss).toContain(`--color-${name}: var(${semanticColorVar(name)});`);
    }
    for (const [name, token] of Object.entries({ ...VEHICLE_COLORS, ...MODE_COLORS })) {
      expect(colors[name]).toBe(token.hex);
      expect(themeCss).toContain(`--color-${name}: ${token.hex};`);
    }
  });

  it('carries the same type scale', () => {
    for (const [name, role] of Object.entries(TYPE_ROLES)) {
      expect(mageridePreset.theme.extend.fontSize[name]).toEqual([
        role.fontSize,
        { lineHeight: role.lineHeight, fontWeight: role.fontWeight },
      ]);
    }
  });

  it('carries the same spacing, radius, shadow and screens', () => {
    expect(mageridePreset.theme.extend.spacing).toMatchObject({
      ...SPACING,
      cta: CTA.height,
      'cta-icon': CTA.iconSize,
    });
    expect(mageridePreset.theme.extend.borderRadius).toEqual({ ...RADII });
    for (const [name, token] of Object.entries(ELEVATIONS)) {
      expect(mageridePreset.theme.extend.boxShadow[name]).toBe(token.shadow);
    }
    expect(mageridePreset.theme.screens).toEqual({ ...BREAKPOINTS, ...BREAKPOINT_ALIASES });
  });

  it('keys the dark: variant off a class, matching the token flip', () => {
    expect(mageridePreset.darkMode).toBe('class');
    expect(themeCss).toContain('@custom-variant dark (&:where(.dark, .dark *));');
    // A compiled `dark:` utility keys off the class, not `prefers-color-scheme`
    // — the same class the token flip keys off, so the two cannot disagree.
    expect(css).toContain('.dark\\:hidden:where(.dark, .dark *)');
    expect(css).not.toContain('prefers-color-scheme');
  });
});
