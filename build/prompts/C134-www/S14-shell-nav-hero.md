# C134 · S14 — the shell, the navigation, and the hero slider

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 14 of 22 · Phase 5 (Pages), part 1 of 5.**

**Prerequisites:** S04 (motion primitives), S06 (device-framed images), S07 (marketing copy).
S12 is not required but makes the typography check real.

The hero is the single most-scrutinised component on the site and the one most likely to be built
wrong from an accessibility standpoint. It gets its own session with the rest of the chrome.

---

## Do this

### 1 · `app/[locale]/layout.tsx` — the shell

Locale provider, header, footer, skip link, and the landmark structure the a11y test asserts:
one `<header>`, one `<nav>`, one `<main>`, one `<footer>`, exactly one `<h1>` per page.

`app/layout.tsx` (the root) keeps the fonts, the pre-paint appearance script and the base metadata;
`app/[locale]/layout.tsx` owns everything that varies by language.

### 2 · Header — `src/components/nav/`

- `Header` — brand mark, primary nav, `LocaleSwitcher`, `ThemeToggle`, `MobileMenu` trigger.
- `LocaleSwitcher` — **real links**, one per rendered locale, each pointing at the *same page* in
  that locale. Not a `<select>` with an `onChange` router push: links are crawlable, are what
  `hreflang` describes, and work with JavaScript off. Drive it from `src/lib/routes.ts` so a page
  with no counterpart in a locale cannot produce a dead link.
- `ThemeToggle` — S19 owns the persistence; here it is the control and its `aria-pressed` state.
- `MobileMenu` — a dialog with focus trapped while open, `Escape` to close, focus restored to the
  trigger. `@mageride/ui`'s `Modal` is Radix-backed; use it rather than hand-rolling a trap.
- `Footer` — sitemap-style link columns from `routes.ts`, the language band, the legal links.
  **No contact details unless D4 was answered** (S01's handoff). No newsletter form, ever.

### 3 · The hero slider — `src/components/hero/`

Four slides, from S07's copy: *Track everything live* · *Book a ride in seconds* · *Drivers keep
100%* · *Send a package across town*. Each slide is headline + sub + dual CTA + a device mockup from
`public/screens/`.

**Mechanism** (S04's decision — no library): CSS `scroll-snap-type: x mandatory` on the track,
`scrollIntoView({behavior:'smooth'})` to move, an `IntersectionObserver` over the slides driving the
active dot. Swipe is the browser's own scrolling, free.

**Follow the WAI-ARIA APG carousel pattern.** Concretely:

- the region carries `role="group"` (or `region`) with `aria-roledescription="carousel"` and an
  accessible name;
- each slide carries `aria-roledescription="slide"` and `aria-label` of the form "3 of 4";
- the dots are real `<button>`s with `aria-label` naming the slide they go to, and the current one
  carries `aria-current="true"` (not `aria-selected` — these are not tabs);
- ←/→ move between slides when focus is inside the carousel;
- a **live region** (`aria-live="polite"`) announces the slide change — but **only for user-driven
  changes**, never for autoplay, or a screen reader is interrupted every 6 seconds;
- autoplay **pauses on hover, on focus-within, and on `document.hidden`**, and there is a visible
  pause/play control. Autoplay without a pause control fails WCAG 2.2 SC 2.2.2.

**Reduced motion** (S04 §3): `matchMedia('(prefers-reduced-motion: reduce)')` is checked **at
mount** and the timer **does not start**. The carousel collapses to the first slide with manual
controls still operable. This is a URD Epic 19 obligation and S20's a11y test asserts it.

**Progress ring on the dots**: a CSS `@property`-registered custom property animated by
`Element.animate()` — not a `setInterval` writing inline styles.

### 4 · Typography reality check

At **375px**, in **all rendered locales**, in **both appearances**: the hero headline must not wrap
past three lines, the CTAs must not overflow, and the Sinhala and Tamil faces must actually load
(devtools → Network → Fonts). If Sinhala overflows, the fix is shorter Sinhala copy (S12) or a
`clamp()` adjustment in `@layer utilities` — **never a token change, never a fourth breakpoint**.

### 5 · `not-found.tsx` and `error.tsx`

Both localised, both with a route back into the site, both with the header and footer. A 404 in
Sinhala that renders English chrome is the failure this whole i18n structure exists to prevent.

---

## Fences

- **No motion library.** CSS + WAAPI only.
- **No `fetch`, no `NEXT_PUBLIC_*`, no cookie.**
- **No literal user-facing string** — including `aria-label`s, which are on the ESLint rule's
  attribute list, and including the "3 of 4" slide labels (use a placeholder key).
- **Autoplay must be pausable and must not start under reduced motion.**
- **The locale switcher is links, not a script.**

---

## Verify

```
npm --prefix portals run lint
npm --prefix portals run build --workspace @mageride/www
npm --prefix portals run test --workspace @mageride/www
```

By hand, and this is the session where hand-checking matters most:

- Tab through the hero: every control reachable, visible focus ring, no trap.
- ←/→ move slides; the pause control works; hover and focus pause it.
- Force reduced motion in devtools → autoplay never starts, nothing translates.
- 375px, every locale, both appearances.
- JavaScript off → all four slides are still readable and the nav still works.

---

## Handoff

- **Component:** C134 www-informational-site — S14 (shell, nav, hero) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** the APG deviations, if any, and why; the reduced-motion behaviour as implemented; any
  copy that had to shorten for Sinhala or Tamil at 375px.
