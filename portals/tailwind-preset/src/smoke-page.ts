/**
 * Renders the smoke page — one HTML document that uses every D2 §0.2 token
 * exactly once, in both appearances.
 *
 * It is generated rather than hand-written on purpose. A hand-written page
 * drifts: a token gets added to `tokens.ts`, nobody adds the swatch, and the
 * "every token is expressed" check quietly stops covering it. Generating it
 * makes the compiled stylesheet a complete inventory of the token set, which
 * is what `test/build-output.test.ts` then reads back and holds against
 * `tokens.ts` — the Definition of Done asks whether the tokens are *resolvable*
 * in both light and dark, and only compiled CSS can answer that.
 *
 * The page is also meant to be opened. `smoke/dist/index.html` is a usable
 * reference sheet for the design system.
 */

import {
  BREAKPOINTS,
  CTA,
  ELEVATIONS,
  MODE_COLORS,
  RADII,
  SEMANTIC_COLORS,
  SPACING,
  TYPE_ROLES,
  VEHICLE_COLORS,
  type SemanticColorName,
} from './tokens.js';

/** The class list D2's CTA token compiles to. `@mageride/ui`'s Button emits the same. */
export const CTA_CLASS_NAMES = [
  'inline-flex',
  'items-center',
  'justify-center',
  'gap-xs',
  'h-cta',
  'px-lg',
  `rounded-${(Object.keys(RADII) as (keyof typeof RADII)[]).find((k) => RADII[k] === CTA.radius) ?? 'sm'}`,
  `bg-${CTA.background}`,
  `text-${CTA.label}`,
  `text-${CTA.labelRole}`,
  'font-body',
].join(' ');

function esc(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function section(title: string, note: string, body: string): string {
  return `      <section class="flex flex-col gap-md">
        <h2 class="text-headline font-display text-on-surface">${esc(title)}</h2>
        <p class="text-body-sm text-on-surface-variant">${esc(note)}</p>
${body}
      </section>`;
}

function semanticSwatches(): string {
  const cells = (Object.keys(SEMANTIC_COLORS) as SemanticColorName[])
    .map((name) => {
      const token = SEMANTIC_COLORS[name];
      // The hex in force is chosen with the `dark:` variant while the swatch
      // beside it is flipped by the custom property. Both mechanisms are on the
      // same class, so a reader can see at a glance that they agree — which is
      // the thing that would be silently wrong if the variant were left on
      // Tailwind's default `prefers-color-scheme` strategy.
      return `          <li class="flex flex-col gap-xxs">
            <span class="block h-xxl rounded-md border border-outline bg-${name}"></span>
            <code class="text-label text-on-surface">${esc(name)}</code>
            <span class="text-caption text-outline-variant">${esc(token.d2)} · <span class="dark:hidden">${esc(token.light)}</span><span class="hidden dark:inline">${esc(token.dark)}</span></span>
            <span class="text-caption text-${name}">Aa</span>
          </li>`;
    })
    .join('\n');
  return `        <ul class="grid grid-cols-2 gap-md tablet:grid-cols-4">\n${cells}\n        </ul>`;
}

function vehicleSwatches(): string {
  const cells = Object.entries(VEHICLE_COLORS)
    .map(
      ([name, token]) => `          <li class="flex items-center gap-xs rounded-sm bg-surface-variant p-xs">
            <span class="block size-lg rounded-sm bg-${name}"></span>
            <span class="flex flex-col">
              <code class="text-label text-${name}">${esc(name)}</code>
              <span class="text-caption text-on-surface-variant">${esc(token.vehicleType ?? 'display-only (Mode B)')}</span>
            </span>
          </li>`,
    )
    .join('\n');
  return `        <ul class="grid grid-cols-1 gap-xs tablet:grid-cols-3 desktop:grid-cols-4">\n${cells}\n        </ul>`;
}

function modeBadges(): string {
  const cells = Object.entries(MODE_COLORS)
    .map(
      ([name, token]) => `          <li class="inline-flex items-center gap-xxs rounded-sm bg-${name} px-sm py-xxs">
            <span class="text-label text-white">Mode ${esc(token.mode)}</span>
          </li>`,
    )
    .join('\n');
  return `        <ul class="flex flex-wrap gap-xs">\n${cells}\n        </ul>`;
}

function typeScale(): string {
  const rows = Object.entries(TYPE_ROLES)
    .map(
      ([name, role]) => `          <li class="flex flex-col gap-xxs border-b border-outline pb-xs">
            <span class="text-${name} font-${role.family} text-on-surface">The quick brown fox — ${esc(name)}</span>
            <span class="text-caption text-on-surface-variant">${esc(role.androidM3)} · ${esc(role.iosDynamicType)} · ${esc(role.fontSize)}/${esc(role.lineHeight)} ${esc(role.fontWeight)}</span>
          </li>`,
    )
    .join('\n');
  return `        <ul class="flex flex-col gap-sm">\n${rows}\n        </ul>`;
}

function spacingScale(): string {
  const rows = Object.entries(SPACING)
    .map(
      ([name, value]) => `          <li class="flex items-center gap-sm">
            <code class="w-xxl text-label text-on-surface-variant">${esc(name)}</code>
            <span class="bg-primary p-${name}"></span>
            <span class="text-caption text-outline-variant">${esc(value)}</span>
          </li>`,
    )
    .join('\n');
  return `        <ul class="flex flex-col gap-xxs">\n${rows}\n        </ul>`;
}

function radiusScale(): string {
  const cells = Object.entries(RADII)
    .map(
      ([name, value]) => `          <li class="flex flex-col items-center gap-xxs">
            <span class="block size-xxl bg-primary-container rounded-${name}"></span>
            <code class="text-label text-on-surface">rounded-${esc(name)}</code>
            <span class="text-caption text-outline-variant">${esc(value)}</span>
          </li>`,
    )
    .join('\n');
  return `        <ul class="flex flex-wrap gap-lg">\n${cells}\n        </ul>`;
}

function elevationScale(): string {
  const cells = Object.entries(ELEVATIONS)
    .map(
      ([name, token]) => `          <li class="flex flex-col items-center gap-xxs">
            <span class="block size-xxl rounded-card bg-surface shadow-${name}"></span>
            <code class="text-label text-on-surface">shadow-${esc(name)}</code>
            <span class="text-caption text-outline-variant">M3 ${token.dp}dp</span>
          </li>`,
    )
    .join('\n');
  return `        <ul class="flex flex-wrap gap-lg">\n${cells}\n        <li class="flex flex-col items-center gap-xxs">
            <span class="block size-xxl rounded-card bg-surface shadow-card"></span>
            <code class="text-label text-on-surface">shadow-card</code>
            <span class="text-caption text-outline-variant">alias</span>
          </li>\n        </ul>`;
}

function breakpointProbe(): string {
  const rows = Object.entries(BREAKPOINTS)
    .map(
      ([name, value]) => `          <li class="hidden ${name}:flex items-center gap-xs text-body-sm text-on-surface">
            <span class="block size-sm rounded-sm bg-success"></span>
            <code>${esc(name)}</code>
            <span class="text-on-surface-variant">≥ ${esc(value)}</span>
          </li>`,
    )
    .join('\n');
  return `        <ul class="flex flex-col gap-xxs">\n${rows}\n        </ul>`;
}

function ctaBlock(): string {
  return `        <div class="flex flex-wrap items-center gap-md">
          <button type="button" class="${CTA_CLASS_NAMES}">
            <span class="block size-cta-icon rounded-sm bg-on-primary/30"></span>
            Confirm booking
          </button>
          <span class="text-body-sm text-on-surface-variant">height ${esc(CTA.height)} · radius ${esc(CTA.radius)} · ${esc(CTA.background)} on ${esc(CTA.label)} · ${esc(CTA.labelRole)}</span>
        </div>`;
}

function appearance(label: string, wrapperClass: string): string {
  return `  <div class="${wrapperClass ? `${wrapperClass} ` : ''}bg-background">
    <div class="mx-auto flex max-w-[1200px] flex-col gap-xl p-lg">
      <header class="flex flex-col gap-xxs">
        <p class="text-label text-primary">${esc(label)}</p>
        <h1 class="text-display font-display text-on-surface">MageRide design tokens</h1>
        <p class="text-body text-on-surface-variant">D2' §0.2 · AL-52 · @mageride/tailwind-preset</p>
      </header>
${section('Brand & semantic colours', 'Sixteen roles. One class on <html> flips every one of them.', semanticSwatches())}
${section('Vehicle-type markers', 'MAP-03 legend, AL-09 canonical types. Eleven tokens.', vehicleSwatches())}
${section('Mode badges', 'Mode A green, Mode B grey, Mode C orange.', modeBadges())}
${section('Type scale', 'Outfit for display and headline, Inter for the rest.', typeScale())}
${section('Spacing', "Named steps on D2's 4px grid.", spacingScale())}
${section('Corner radius', 'Four steps: buttons/chips, fields/sheets, modals, cards.', radiusScale())}
${section('Elevation', 'Android M3 dp levels rendered as web shadows.', elevationScale())}
${section('Breakpoints', 'Rows appear as the viewport crosses each D2 width.', breakpointProbe())}
${section('CTA', 'The D2 CTA token, as @mageride/ui emits it.', ctaBlock())}
    </div>
  </div>`;
}

/** Renders the smoke document. Pure — the build script writes what it returns. */
export function renderSmokePage(): string {
  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>MageRide design tokens — smoke page</title>
    <link rel="stylesheet" href="./smoke.css" />
  </head>
  <body class="font-body">
${appearance('Light appearance', '')}
${appearance('Dark appearance', 'dark')}
  </body>
</html>
`;
}
