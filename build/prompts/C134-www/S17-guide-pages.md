# C134 · S17 — `/guide` and the 34 chapter pages

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 17 of 22 · Phase 5 (Pages), part 4 of 5.**

**Prerequisites:** S08–S11 (the corpus), S14 (the shell), S06 (the images).

This is the largest route family on the site: **34 chapters × the rendered locales**, plus the index.
It is also the reason the content was typed rather than authored per-locale — one component renders
every chapter, and a chapter that renders in English renders in Sinhala by construction.

---

## Do this

### 1 · `app/[locale]/guide/page.tsx` — the index

Two audience sections, chapters in `order`, each with title and summary. A chapter's card shows how
many steps it has — a reader deciding whether to open "Onboarding your vehicle" wants to know it is
nine steps before they start.

### 2 · `app/[locale]/guide/passenger/[chapter]/page.tsx` and `.../driver/[chapter]/page.tsx`

`generateStaticParams` from `src/content/index.ts` × the rendered locales. **A chapter that exists
and does not appear here is impossible** because the registry is the only path to a chapter — that
was S07's design and this is where it pays.

Layout:

- **Sticky left chapter rail** at `desktop:` only (1024px — D2's third breakpoint, and there is no
  fourth). Collapses to a select-free disclosure below that.
- **Reading column**, capped for line length (~65 characters — this is prose, and 1200px of Sinhala
  body text is unreadable).
- **Right-hand on-page TOC** built from the chapter's own steps, at `desktop:`.
- **Prev/next pager** — `ChapterPager`, driven by `order`. The last passenger chapter does not roll
  into the first driver chapter; it ends.
- **Screen references** — `ScreenRef` renders a thumbnail from `screens.ts` inline in a step and
  opens S15's lightbox. Reuse the lightbox; do not build a second one.
- **Callouts** — `tip` / `warning` / `fee` / `privacy`, each visually distinct and each with a text
  label, not colour alone (WCAG 1.4.1 — a `fee` callout that is only orange is invisible to a
  colour-blind reader).

### 3 · The print stylesheet

A driver in a three-wheeler with no data plan is a real reader of the driver guide. `@media print`:
drop the nav, the rail, the TOC, the pager and the lightbox triggers; keep the images at a sensible
size; print the step numbers. `portals/web-passenger/app/globals.css` has the `print-hidden` /
`print-plain` precedent — follow its shape.

### 4 · "Was this helpful?" — the constrained version

It **sets no cookie and calls nothing**. Options, in order of preference:

1. Omit it entirely.
2. A `mailto:` link pre-filled with the chapter slug in the subject.

Anything else — a fetch, a beacon, a localStorage counter that later syncs — is a fence violation
and a PDPA surface. If the user asks for real feedback measurement, that is its own change set with
its own data-protection position (`docs/www-site-plan.md` §13).

### 5 · Fill in the screen references

S08–S11 wrote `screens: []` per chapter, possibly empty if S05 had not run. Complete them now:
every chapter that describes a screen shows it. If a needed `SCR-*` is not in
`src/content/screens.ts`, add the entry and **re-run `npm run screens:refresh`** (S06) so the image
exists — a registry entry with no file is what `check-bundle.mjs` fails on.

### 6 · Register every chapter route in `src/lib/routes.ts`

Generated from the registry, not hand-listed — 34 hand-typed slugs will drift. `app/sitemap.ts`
reads `routes.ts`, so this is what makes "a new chapter cannot be omitted from the sitemap" true.

---

## Fences

- **No network call**, including the helpfulness control.
- **No cookie, no localStorage.**
- **No localised slugs** — the URL is stable across locales (S07's decision).
- **One lightbox implementation** across the whole site.
- **Callouts carry a text label, not colour alone.**
- **No literal user-facing string.**

---

## Verify

```
npm --prefix portals run lint
npm --prefix portals run build --workspace @mageride/www      # route count = 34 x locales + index x locales + the rest
npm --prefix portals run test --workspace @mageride/www
grep -rnE "\bfetch\(|axios|navigator\.sendBeacon" portals/www/src portals/www/app   # nothing
```

By hand: three chapters (one short, one long, one image-heavy) × every rendered locale; keyboard
through the rail, the TOC, the pager and the lightbox; print preview of one chapter; the whole guide
with JavaScript off.

---

## Handoff

- **Component:** C134 www-informational-site — S17 (guide pages) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** the route count `next build` reported; which "was this helpful" option was taken and on
  whose decision; any screen registry entries added and whether `screens:refresh` was re-run.
