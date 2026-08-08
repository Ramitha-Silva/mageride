# Tailwind Preset Conventions
- Plain TypeScript/JavaScript package — `@mageride/tailwind-preset`, consumed by all three
  web surfaces (admin, fleet, web-passenger)
- The single home for the D2' §A / §0.2 design tokens on the web: brand + semantic colours
  (light + dark), all 11 vehicle-type tokens, mode badges, the Outfit (display/headline) +
  Inter (body) type scale, the 4px spacing grid, radius sm/md/lg/card, elevation, and the
  375 / 768 / 1024 breakpoints
- **`src/tokens.ts` is the source of truth.** `dist/theme.css` and `dist/preset.js` are both
  GENERATED from it by `npm run build` — never hand-edit either, and never spell a D2 hex,
  size or radius anywhere else in `portals/`
- Consumption path is two lines: `@import "tailwindcss";` then
  `@import "@mageride/tailwind-preset/theme.css";`. The JS preset (`mageridePreset`) is
  shipped because AL-52 names one, but a v4 JS config merges `screens` instead of replacing
  them — see README.md
- Tailwind CSS is the SOLE styling system (AL-52). MUI, Bootstrap, styled-components, Emotion
  and any runtime CSS-in-JS are excluded — reject them on sight, here and downstream.
  `portals/scripts/check-al52.mjs` is the executable form of that rule and runs inside
  `npm run lint`
- CSS is compiled at build time by PostCSS inside `npm run build`; no runtime style injection
- Headless primitives (Radix UI / Headless UI) styled with Tailwind are permitted;
  pre-styled component kits are not. They live in `@mageride/ui`, not here
- Dark mode is the `.dark` class, never a second stylesheet: it flips the `--mr-color-*`
  properties AND the `dark:` variant, and the two must stay wired together
- The D2 breakpoints REPLACE Tailwind's defaults (AL-52: they "become the `screens` config").
  `sm:` is 375px, not 640px; there is no `xl:`
- Verify: `npm --prefix portals run build -w @mageride/tailwind-preset`
