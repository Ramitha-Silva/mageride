# Shared i18n Conventions
- Plain TypeScript package — `@mageride/i18n`, consumed by all three web surfaces
  (admin, fleet, web-passenger). No framework dependency: it is used from server
  components, client components and plain modules alike
- **`src/locales/en.ts` defines the key set.** `si.ts` and `ta.ts` are annotated `Messages`,
  so a key added to one and not the others is a COMPILE error. That is how the trilingual
  rule (root CLAUDE.md) is enforced rather than reviewed
- Adding a string means adding it to all three files in the same change. Never ship a key
  in one language "for now"
- Keys are dotted and grouped by area (`common.*`, `status.*`, `table.*`, `mode.*`).
  Placeholders are `{name}` and must appear in all three translations
- Screen copy belongs to the component that owns the screen (C104 admin, C111 fleet,
  C117 passenger web). This package carries only what every surface shares
- Language names (`language.si/ta/en`) are written in the language they name, in all three
  files — that is deliberate, not an untranslated string
- `DEFAULT_LOCALE` is `si` (D1' §283, "Sinhala first & default"); `FALLBACK_LOCALE` is `en`.
  Use `negotiateLocale(acceptLanguage)` where a header is available
- The other half of the rule lives in `@mageride/eslint-config`:
  `mageride/no-literal-user-facing-strings` stops a string reaching JSX instead of a
  resource file
- Verify: `npm --prefix portals run build -w @mageride/i18n && npm --prefix portals run test -w @mageride/i18n`
