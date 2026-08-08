# `@mageride/tailwind-preset`

The single home for the **D2′ §0.2 design tokens** on the web (AL-52). Every colour,
type role, spacing step, radius, elevation and breakpoint the Admin Portal, the Fleet
Portal and the passenger web subview use is defined once, here, in
[`src/tokens.ts`](src/tokens.ts).

Nothing in this package is written twice. `dist/theme.css` and `dist/preset.js` are both
**generated** from `src/tokens.ts` by `npm run build`, and `test/build-output.test.ts`
holds them against each other and against the compiled stylesheet.

---

## Using it

Two lines in your global stylesheet:

```css
/* app/globals.css */
@import "tailwindcss";
@import "@mageride/tailwind-preset/theme.css";
```

That is the whole setup. No `tailwind.config.js` is required — Tailwind v4 reads the
theme from CSS.

### Dark mode

Dark mode is the **`.dark` class** on a document ancestor, normally `<html>`. It drives
both halves at once:

- the `--mr-color-*` custom properties flip to their D2 dark hex, so every semantic
  utility (`bg-surface`, `text-on-surface`, `border-outline`, …) follows with no `dark:`
  prefix anywhere;
- the `dark:` variant matches the same class, for the cases where a colour swap is not
  enough (`hidden dark:block`).

Wire them together — never independently, or a `dark:` utility will disagree with the
token underneath it:

```tsx
// Follow the OS setting, and let a stored preference win.
<html className={theme === 'dark' ? 'dark' : undefined}>
```

### Fonts

The type tokens name **Outfit** (display/headline) and **Inter** (body), and resolve an
optional custom property first so `next/font` can supply the self-hosted face:

```ts
import { Inter, Outfit } from 'next/font/google';

const outfit = Outfit({ subsets: ['latin'], variable: '--mr-font-outfit' });
const inter = Inter({ subsets: ['latin'], variable: '--mr-font-inter' });
// <html className={`${outfit.variable} ${inter.variable}`}>
```

With no variable set the tokens fall back to the family name and then the system stack,
so an unstyled page is still legible. No webfont is fetched at runtime — a CDN `<link>`
is not compatible with the strict-CSP posture AL-52 was chosen for.

---

## The token vocabulary

| Family | Utilities | Source |
|---|---|---|
| Semantic colours (16 roles, light + dark) | `bg-primary`, `text-on-surface`, `border-outline`, `bg-surface-variant`, `text-success`, … | D2 §0.2 table 1 |
| Vehicle-type markers (11) | `bg-veh-sedan`, `text-veh-tuk`, `bg-veh-private`, … | D2 §0.2 table 2 (MAP-03, AL-09) |
| Mode badges (3) | `bg-mode-a`, `bg-mode-b`, `bg-mode-c` | D2 §0.2 |
| Type roles (8) | `text-display`, `text-headline`, `text-title`, `text-subtitle`, `text-body`, `text-body-sm`, `text-label`, `text-caption` | D2 §0.2 table 3 |
| Font families | `font-display` (Outfit), `font-body` (Inter) | D2 §0.2 |
| Spacing (7 named) | `p-xxs` `p-xs` `p-sm` `p-md` `p-lg` `p-xl` `p-xxl`, plus `h-cta` and `size-cta-icon` | D2 §0.2 |
| Radius (4) | `rounded-sm` `rounded-md` `rounded-lg` `rounded-card` | D2 §0.2 |
| Elevation (6 + alias) | `shadow-elevation-0` … `shadow-elevation-5`, `shadow-card` | D2 §0.2 |
| Breakpoints (3) | `mobile:` `tablet:` `desktop:` — aliased `sm:` `md:` `lg:` | D2 §AP/§FP |

Each type role carries its own line height and weight, so `text-title` sets all three.

### Things worth knowing

- **The breakpoints replace Tailwind's, they do not extend them.** AL-52 says the D2
  three *become* the `screens` config, so `--breakpoint-*` is cleared first. `sm:` means
  **375px**, not 640px; `xl:` and `2xl:` do not exist. D2 defines three widths and the
  portals get three widths.
- **The numeric spacing scale is deliberately intact.** `p-4`, `gap-6`, `h-10` are D2's
  4 px base grid (Tailwind's `--spacing` is `0.25rem`), so they are on-token. The seven
  named steps are the shorthand for the sizes D2 calls out.
- **`rounded-sm/md/lg` carry the D2 values** (8/12/16 px), not Tailwind's stock ones.
- **Everything else is additive.** Tailwind's own palette and type scale still resolve.
  D2 §0.2 is the vocabulary you are expected to write in, not a wall — reach for
  `bg-red-500` and you have left the design system, which is a review comment rather
  than a build error.

---

## The JS preset

AL-52 names a `tailwind.config` preset, and one is shipped:

```js
// tailwind.config.js
import { mageridePreset } from '@mageride/tailwind-preset';
export default { presets: [mageridePreset], content: ['./app/**/*.{ts,tsx}'] };
```

```css
@import "tailwindcss";
@config "./tailwind.config.js";
@import "@mageride/tailwind-preset/theme.css";   /* still needed — see below */
```

Prefer `theme.css`. On Tailwind v4 a JS config's `screens` **merges** with the built-in
breakpoints instead of replacing them, so `sm:` would quietly keep meaning 640px, and
the `:root`/`.dark` custom properties the semantic colours resolve through are only
declared in `theme.css` anyway. The preset exists because AL-52 names it and because it
is the machine-readable form of the token table; `test/build-output.test.ts` asserts the
two carry identical values.

## The tokens as data

Non-CSS consumers — a MapLibre marker style, a CSV/PDF export, a chart — import the
tables rather than re-typing a hex:

```ts
import { VEHICLE_COLORS, SEMANTIC_COLORS, CTA } from '@mageride/tailwind-preset';

VEHICLE_COLORS['veh-tuk'].hex;          // '#F5C518'
VEHICLE_COLORS['veh-tuk'].vehicleType;  // 'three_wheeler'  (AL-09 canonical)
SEMANTIC_COLORS.primary.dark;           // '#FFB68A'
CTA.height;                             // '56px'
```

---

## The smoke page

`npm run build` also generates `smoke/dist/index.html` — every token, once, in both
appearances — and compiles a stylesheet for it. Open it in a browser; it is a usable
reference sheet, and it is what `test/build-output.test.ts` reads to prove each token
resolves rather than merely existing in a JavaScript object.

## Commands

```
npm run build      # tokens → dist/theme.css + dist/preset.js → smoke page → smoke CSS
npm run test       # rebuilds, then checks the tokens and the compiled output
npm run lint
npm run typecheck
```
