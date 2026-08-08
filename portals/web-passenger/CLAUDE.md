# Passenger Web Subview Conventions
- Next.js, TypeScript, React + Tailwind CSS (sole styling system, AL-52 — shared
  @mageride/tailwind-preset, no CSS-in-JS/MUI/Bootstrap)
- Six no-login pages at `passenger.mageride.lk` — SCR-WT-001…006
  (specs/wireframes/web_passenger.html)
- No login and no app chrome. The share token IS the credential: render nothing before it
  validates, and render nothing at all on an expired/invalid token
- Served exclusively by public-bff — never call a domain service directly
- Shared infrastructure (C103) — use it, do not re-implement it:
  `@mageride/tailwind-preset` (D2 §0.2 tokens; `@import "@mageride/tailwind-preset/theme.css"`),
  `@mageride/ui` (button/CTA, field, chip, status pill, table, modal, toast, tabs, dropzone),
  `@mageride/i18n` (si/ta/en resources), `@mageride/eslint-config` (`react` flat config)
- The driver contact is a plain `tel:` link. No masked number, no /call round-trip,
  no proxy-DID lease (AL-48)
- Declining a location request transmits NO GPS, and the copy must say so
- Mobile-first at 375 px; this is a subview of the Passenger App, not a separate product
- npm workspace member `@mageride/web-passenger` under portals/
- Verify: `npm --prefix portals run build -w @mageride/web-passenger`
