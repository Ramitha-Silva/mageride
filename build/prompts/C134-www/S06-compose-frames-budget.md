# C134 · S06 — composite into device frames, commit the images, gate their size

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 6 of 22 · Phase 3 (Screen imagery), part 2 of 2.**

**Prerequisite:** S05 — `src/content/screens.ts` exists and `.screens-raw/` is populated.

---

## Do this

### 1 · `scripts/compose-frames.mjs`

Uses **`sharp`** — a devDependency, no styling implications, on neither AL-52 list. Per raw capture:

- composite into a device mockup: rounded corners, bezel, a status bar for the phone families, and a
  drop shadow built from the **D2 `shadow-elevation-*` values**, on a token-derived gradient plate.
  Every colour comes from `portals/tailwind-preset/src/tokens.ts` — no new hex enters the system,
  which is the same rule S04 works under.
- Emit **AVIF + WebP + a PNG fallback**, at 1x and 2x.
- Deterministic naming from the registry's `file` field:
  `pa-014-finding-driver.avif`, `pa-014-finding-driver@2x.avif`, `…-dark.avif`, etc.
- Output to `portals/www/public/screens/`.

Wire `npm run screens:refresh` = capture + compose, and say in `portals/www/CLAUDE.md` that it is
the only sanctioned way these files change.

### 2 · Commit the outputs — deliberately

`public/screens/**` is **committed**. CI must not have to download a browser to build the site, and
`.github/workflows/ci.yml`'s portal leg is `npm ci && lint && build` with no Playwright in it.

Add `portals/www/public/screens/README.md` recording: that the files are generated, from which
wireframes, by which two scripts, and that a hand-edited image will be silently overwritten by the
next refresh.

### 3 · The budget — an assertion, not a hope

Extend `portals/www/scripts/check-bundle.mjs` (created in S03):

- total `public/screens/` **≤ 12 MB**;
- no single image **> 220 KB**;
- every `file` in `src/content/screens.ts` resolves to at least the AVIF and WebP at 1x — a registry
  entry with no image is a broken `<img>` on a public page.

It runs inside `npm run build`, so C134's own verify command enforces it.

**If you blow the budget**, the fix is fewer screens or tighter AVIF quality, in that order — not a
larger number. 70 screens × 2 appearances × 2 densities × 3 formats is a lot of files; if the
arithmetic does not close, drop the PNG fallback for dark captures first (every browser that reads
`prefers-color-scheme` also reads WebP) and record the decision.

### 4 · `next/image` usage contract

Write it into `portals/www/CLAUDE.md` so S15–S18 do not each invent one:

- every screen image renders through `next/image` with explicit `width`/`height` (no CLS);
- `<picture>`-style art direction between light and dark is done with `prefers-color-scheme` in CSS,
  not with JS that swaps a `src` — a swap after hydration is a flash;
- `alt` comes from the registry's `captionKey` through the translator. **`alt` is on the ESLint
  rule's attribute list**, so a literal there fails lint, which is the correct outcome.
- hero images get `priority`; everything else is lazy.

---

## Fences

- **`sharp` and Playwright stay devDependencies.**
- **No hand-edited image in `public/screens/`.** It is generated output.
- **No new colour.** Bezels, plates and shadows come from D2 tokens.
- The 12 MB / 220 KB numbers are the fence. Do not raise them to make a build pass.

---

## Verify

```
npm --prefix portals run screens:refresh --workspace @mageride/www
du -sh portals/www/public/screens                      # <= 12M
find portals/www/public/screens -size +220k            # empty
npm --prefix portals run lint
npm --prefix portals run build --workspace @mageride/www
npm --prefix portals run test --workspace @mageride/www
git status --porcelain portals/www/public/screens | head
```

---

## Handoff

- **Component:** C134 www-informational-site — S06 (compositing & budget) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** final image count and total size; anything dropped to fit the budget and why; whether
  any dark capture had to ship light-only (carried forward from S05).
