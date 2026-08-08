# Shared ESLint Config Conventions
- Plain ESM JavaScript package — `@mageride/eslint-config`, no build step. Flat config
  (ESLint 9). Every web surface's `eslint.config.js` is two lines: import `base` (plain
  TypeScript) or `react` (anything with JSX), and export it
- Carries the two MageRide rules — the ones enforcing platform-wide rules that no
  off-the-shelf plugin knows about:
  - `mageride/no-runtime-css-in-js` — AL-52. Tailwind is the sole styling system; MUI,
    Bootstrap, styled-components, Emotion, pre-styled kits and `<style jsx>` are refused
  - `mageride/no-literal-user-facing-strings` — root CLAUDE.md. JSX text, literal JSX
    children and literals in user-facing attributes (`alt`, `title`, `placeholder`,
    `aria-label`, …) must come from `@mageride/i18n`. Off in test files, where a fixture
    label is not shipped copy
- **`banned-styling-packages.json` is the ONE place the excluded package names are spelled
  inside `portals/`.** The C103 DoD asks that a grep for them come back empty; this file,
  its rule test and `portals/scripts/check-al52.mjs` are the expected hits. Add a name here,
  never inline in the rule
- ESLint is pinned to 9.x: `eslint-plugin-react` and `eslint-plugin-jsx-a11y` do not yet
  declare ESLint 10 support. Revisit when they do
- Both rules have RuleTester tests, and the *valid* cases matter more than the invalid ones
  — a rule that fires on `className` or on a route string gets switched off, and the
  guarantee goes with it
- Verify: `npm --prefix portals run lint && npm --prefix portals run test -w @mageride/eslint-config`
