# Shared UI Primitives Conventions
- React + TypeScript package — `@mageride/ui`, consumed by all three web surfaces
  (admin, fleet, web-passenger). Headless primitives (Radix UI) styled with Tailwind
  from `@mageride/tailwind-preset` (AL-52)
- **No literal user-facing text, ever.** Every label, caption, placeholder and accessible
  name is a REQUIRED prop. A component with an English default ships a string no Sinhala or
  Tamil user can be shown (CLAUDE.md, Trilingual resources) and the lint rule cannot see it
  because it lives in a library rather than a screen
- **No colour, size or radius that is not a D2 §0.2 token.** Where D2 has no token — a table
  header, a 40px dense control — compose from tokens or from D2's own 4px grid. Never
  introduce a value
- `Button` with its defaults IS the D2 CTA token (height 56, radius sm 8, `primary` on
  `onPrimary`, `titleMedium` label). Its class list comes from `CTA_CLASS_NAMES` in the
  preset, so the token and the component cannot drift
- Use `cx()` for class composition — it resolves Tailwind conflicts last-one-wins so a
  caller's `className` actually applies. It is class-string arithmetic, not CSS-in-JS
- Radix primitives are permitted because focus traps, roving tabindex and live regions are
  behaviour, not styling. Pre-styled component kits are not
- Components that use hooks, context or Radix carry `'use client'`; the rest stay usable
  from a React Server Component
- Verify: `npm --prefix portals run build -w @mageride/ui && npm --prefix portals run test -w @mageride/ui`
