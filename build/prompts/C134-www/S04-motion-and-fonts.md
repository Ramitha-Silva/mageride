# C134 · S04 — the motion layer, the marketing-scale utilities, and Sinhala/Tamil display faces

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 4 of 22 · Phase 2 (Design system extension & motion).**

**Prerequisite:** S03 is complete and `npm --prefix portals run build` is green.

This session builds the primitives every page after it composes. Nothing here is a page; everything
here is a mechanism, and each one ships its reduced-motion variant in the same rule block.

---

## Before you start

- `portals/tailwind-preset/src/tokens.ts` — **the only place a D2 §0.2 value is spelled on the web.**
  Read it. You are composing from these, not adding to them.
- `portals/tailwind-preset/README.md` — the `screens` replacement and why a JS config would undo it.
- `portals/web-passenger/app/globals.css` — the `@layer base` / `@layer utilities` shape.
- `portals/web-passenger/app/layout.tsx` — the `next/font/google` block and the pre-paint script.
- `docs/www-site-plan.md` §A10–A13.

---

## Do this

### 1 · Compose the marketing scale — in `@layer utilities`, never as tokens

The preset is built for product UI. Four things a marketing site needs that D2 §0.2 has no token
for, and the resolution for each. **Record the derivation of every one in `portals/www/CLAUDE.md`.**

| Need | Resolution |
|---|---|
| Display type above 32px | `text-display` is D2's largest at 32px; a hero wants ~48/64/72. Compose `clamp()` utilities on D2's 4px grid. **Not new tokens.** |
| Gradient / aurora backdrops | Built from `primary`, `primary-container`, `secondary-container` at defined alpha. **No new hex enters the system.** |
| Section rhythm | `xxl` is the largest spacing step; marketing sections want 96/128px. `@layer utilities`, 4px grid. |
| A 4th breakpoint | **Do not add one** (MCS-34 decision D8). D2 defines 375/768/1024 and the preset *replaces* Tailwind's screens. Cap content at `max-w-[1200px]` and stay on three. |

### 2 · Build the motion layer — CSS keyframes + the Web Animations API, no library

This is the plan's most consequential technical decision and `portals/www/CLAUDE.md` must carry the
reasoning, not just the rule. The short form: **Framer Motion / `motion` is on neither
`banned-styling-packages.json`'s package list nor its prefix list, so it would pass
`check-al52.mjs` — and its entire mechanism is runtime style injection, which is exactly what AL-52
exists to forbid.** Shipping it is either a fence violation the checker happens not to catch, or a
platform-wide widening of AL-52 for one marketing page. Neither is a good trade.

Everything the brief asks for is achievable without one. Build these in `src/components/motion/`
and `src/lib/motion.ts`:

| Effect | Mechanism |
|---|---|
| Sliding hero (autoplay · swipe · keyboard) | CSS `scroll-snap-type: x mandatory` + `scrollIntoView({behavior:'smooth'})`; an `IntersectionObserver` over the slides drives the active dot. No dependency. |
| Scroll reveal (fade / rise / stagger) | `IntersectionObserver` toggles a class; CSS `transition` with a per-child `--i` delay |
| Parallax on device mockups | `rAF`-throttled scroll → a CSS **custom property** → `translate3d`. Writes a *variable*, never a rule — that distinction is what keeps it inside AL-52. |
| Stat counters | `Element.animate()` over an `@property`-registered custom property |
| Sticky "how it works" | `position: sticky` + IO. **Zero JS animation.** |
| Animated markers / route draw | Inline SVG + `@keyframes` + `stroke-dasharray` |
| Route transitions | Next.js 16's View Transitions support. **Verify it is available and non-experimental in `next@^16.3.0` before using it**; if it is behind an experimental flag, skip it — a marketing site does not need it, and an experimental flag is a fence question. |
| Marquee | CSS `@keyframes` translate over a duplicated track |
| Gradient hero backdrop | `@property` + `@keyframes` on registered custom properties (GPU-composited) |

All keyframes live in `app/globals.css` under `@layer utilities`, compiled by PostCSS at build —
which is exactly what AL-52 asks for.

`src/lib/motion.ts` carries three things and nothing else: the reduced-motion helper, an
`IntersectionObserver` factory (one shared observer, not one per element), and a `rAF` scheduler so
N parallax elements share one frame callback rather than N.

### 3 · `prefers-reduced-motion` is a path, not an afterthought

Every motion utility ships its reduced variant **in the same rule block**, so the two cannot drift:

```css
@media (prefers-reduced-motion: reduce) {
  /* transforms → opacity-only or nothing; autoplay stops; parallax inert */
}
```

And the hero's autoplay timer checks `matchMedia('(prefers-reduced-motion: reduce)')` **at mount and
does not start** if set — a CSS-only defence leaves a carousel that still advances, just without a
transition, which is the same vestibular problem.

This is a URD Epic 19 obligation and it is also the difference between professional animation and
animation that makes people ill. `test/a11y.test.ts` (S20) asserts it.

### 4 · Sinhala and Tamil display faces

D2 §0.2 names Outfit (display) and Inter (body). `portals/web-passenger/app/layout.tsx` says in its
own words that neither carries Sinhala or Tamil glyphs and that those subsets fall through to the
system face. On a token-gated utility page that is fine. **On a hero setting 48–72px Sinhala it is a
visibly unfinished page beside the English cut.**

- Add **Noto Sans Sinhala** and **Noto Sans Tamil** via `next/font/google`, self-hosted — no CDN
  `<link>`, matching the posture the other portals keep for CSP reasons.
- Bind as `--mr-font-sinhala` / `--mr-font-tamil`; select per-locale on `<html>` so Outfit/Inter
  still carry Latin inside a Sinhala page (a Latin brand name, a URL, a number).
- **Verify both faces resolve from `next/font/google` on this host before committing.** `next/font`
  fetches at *build* time; if the box has no egress to Google Fonts, or a subset is unavailable,
  fall back to `next/font/local` with committed `woff2` files and record that in the handoff.
- Covered by MCS-34's D2 §0.2 delta (S01) — if the delta was not applied, stop and say so.

### 5 · A demo route, deleted before launch

Add `app/[locale]/_motion-demo/page.tsx` rendering every primitive once. It is how this session is
verified by eye and how a later session checks a token change did not reflow anything. **Exclude it
from `routes.ts` and from the sitemap**, and delete it in S20.

---

## Fences

- **No motion library.** Not `framer-motion`, not `motion`, not `gsap`, not `@react-spring`,
  not `popmotion`. `scripts/check-bundle.mjs` greps for their runtime signatures (S03 §5).
- **No new design token.** Every marketing-scale value is composed in `@layer utilities` from
  existing tokens. If you conclude a real token is needed, **stop and raise a change set** — do not
  edit `portals/tailwind-preset/src/tokens.ts`.
- **No fourth breakpoint** (D8).
- **No `@media (prefers-reduced-motion)` block that only removes a transition.** Autoplay must stop.

---

## Verify

```
npm --prefix portals run lint
npm --prefix portals run build --workspace @mageride/www
npm --prefix portals run test --workspace @mageride/www
grep -rniE "framer-motion|\"motion\"|gsap|react-spring|popmotion" portals/www/package.json   # nothing
grep -rn "tailwind.config" portals/www/                                                       # nothing
```

By eye at `/si/_motion-demo`, in both appearances and with reduced motion forced in devtools:
every effect degrades, the carousel stops advancing, nothing moves under transform.

---

## Handoff

- **Component:** C134 www-informational-site — S04 (motion & fonts) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** whether the two Noto faces resolved from `next/font/google` or needed local files;
  whether Next 16's View Transitions were usable un-flagged; every `@layer utilities` value added
  and the token it was composed from.
