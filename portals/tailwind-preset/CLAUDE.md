# Tailwind Preset Conventions
- Plain TypeScript/JavaScript package — `@mageride/tailwind-preset`, consumed by all three
  web surfaces (admin, fleet, web-passenger)
- The single home for the D2' §A / §0.2 design tokens on the web: brand + semantic colours
  (light + dark), all 11 vehicle-type tokens, mode badges, the Outfit (display/headline) +
  Inter (body) type scale, the 4px spacing grid, radius sm/md/lg/card, elevation, and the
  375 / 768 / 1024 breakpoints
- Tailwind CSS is the SOLE styling system (AL-52). MUI, Bootstrap, styled-components, Emotion
  and any runtime CSS-in-JS are excluded — reject them on sight, here and downstream
- CSS is compiled at build time by PostCSS inside `npm run build`; no runtime style injection
- Headless primitives (Radix UI / Headless UI) styled with Tailwind are permitted;
  pre-styled component kits are not
- Dark mode is the `dark:` variant, never a second stylesheet
- Verify: `npm --prefix portals run build -w @mageride/tailwind-preset`
