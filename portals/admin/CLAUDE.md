# Admin Portal Conventions
- Next.js, TypeScript, React + Tailwind CSS (sole styling system, AL-52 — shared
  @mageride/tailwind-preset, no CSS-in-JS/MUI/Bootstrap)
- Wireframe reference: specs/wireframes/web_admin.html (19 screens, SCR-AP-001…016)
- API calls go through Admin BFF (backend service)
- Shared infrastructure (C103) — use it, do not re-implement it:
  `@mageride/tailwind-preset` (D2 §0.2 tokens; `@import "@mageride/tailwind-preset/theme.css"`),
  `@mageride/ui` (button/CTA, field, chip, status pill, table, modal, toast, tabs, dropzone),
  `@mageride/i18n` (si/ta/en resources), `@mageride/eslint-config` (`react` flat config)
- Screens include the GTFS Dataset Manager (SCR-AP-016, Epic 28) — upload/validate/activate/rollback
- npm workspace member `@mageride/admin-portal` under portals/
- Verify: `npm --prefix portals run build -w @mageride/admin-portal`
