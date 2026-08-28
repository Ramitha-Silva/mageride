# C134 · S15 — the rest of the home page

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 15 of 22 · Phase 5 (Pages), part 2 of 5.**

**Prerequisites:** S14 (the hero and shell), S06 (images), S07 (copy).

Nine sections; the hero is section 1 and shipped in S14. This session builds sections 2–9 and the
marketing components they share.

---

## Do this — `app/[locale]/page.tsx` and `src/components/marketing/`, `src/components/showcase/`

### 2 · The three modes — `ModeCard`

A/B/C cards on the **`bg-mode-a` / `bg-mode-b` / `bg-mode-c` tokens** (the three mode badge colours
the preset already carries). One paragraph each from S07's copy.

**Do not blur the boundary.** Mode C (on-demand rides and deliveries) is `ride-svc`; Mode A/B
(scheduled and followed vehicles) is `trip-state-svc`. Three cards, three distinct propositions, not
one product with a toggle.

### 3 · How it works — sticky scroll-through

`position: sticky` + `IntersectionObserver`, **zero JS animation** (S04). Four steps, with a
passenger/driver toggle. The toggle is a real control with `aria-pressed`; both cuts are in the DOM,
and switching is a class change, so the content is crawlable either way.

Under reduced motion the sticky section degrades to a plain vertical list of four steps — not a
sticky section with the transitions removed, which leaves a reader stuck scrolling a pinned panel.

### 4 · Feature splits — `FeatureSplit`

Five alternating image/text blocks, scroll-revealed with the `Reveal` primitive from S04. Images
from `public/screens/` through `next/image` with explicit dimensions (S06's usage contract). The
first is above the fold on tall viewports — give it `priority` and no reveal transition, or the LCP
element animates in and the metric suffers.

### 5 · Zero-commission band — `FareTable`

The six driver fee tiers, rendered from **the single exported constant S07 created**, formatted as
Rupees from minor units. Never inline a figure. `test/content.test.ts` (S20) asserts the six values
against URD §1, so this table cannot drift from the spec without a red build.

State the rule the way URD §1 states it: zero commission, passengers pay drivers directly, a flat
daily fee for Mode C with **the first trip of the day always free**, monthly for Mode B, **nothing
for Mode A**.

### 6 · Screen showcase — `ScreenCarousel` + `ScreenLightbox`

Horizontal `scroll-snap` carousel over a curated subset of `src/content/screens.ts`, opening into a
lightbox.

The lightbox is the second-most-likely a11y failure on the site after the hero:

- focus moves into it on open and is **trapped** while open;
- `Escape` closes; focus returns to the thumbnail that opened it;
- ←/→ move between images; the current position is announced;
- the underlying page does not scroll behind it;
- it is a `<dialog>` or Radix-backed (`@mageride/ui`'s `Modal`), not a `div` with `role="dialog"`
  and hand-rolled key handling.

### 7 · Stats — `StatTile` with counters

11 vehicle types · 3 languages · 0% commission · first trip free. Counters use `Element.animate()`
over an `@property`-registered custom property (S04) and run **once**, on first intersection.

**Under reduced motion the final value renders immediately** — and it must also be the value in the
server-rendered HTML, so a crawler and a JS-off reader see "11", not "0".

### 8 · Language band

The same sentence in Sinhala, Tamil and English, shown together on one card. This is deliberately
not a translator lookup — it is three strings side by side, and each needs the right `lang`
attribute on its own element so a screen reader switches voice. That per-element `lang` is a WCAG
3.1.2 requirement and it is the whole point of the section.

If Tamil was deferred (S13), **still show all three sentences here** — the card is about the
platform being trilingual, which is true of the apps regardless of what this site renders.

### 9 · Download CTA

Store badges if D3 was answered; otherwise the email-notify variant **with no form** (S18 owns
`/download` itself; this is the band that links to it).

---

## Fences

- **No API call.** No live vehicle count, no "N rides today" — it would break the "renders with the
  backend down" fence and put an API dependency in front of the platform's front door.
- **No inline rupee figure.** One constant, one anchor.
- **No motion library**; every effect degrades under reduced motion, and the degraded form is usable.
- **No literal user-facing string.**
- Content cap `max-w-[1200px]`; three breakpoints (375/768/1024). **No fourth.**

---

## Verify

```
npm --prefix portals run lint
npm --prefix portals run build --workspace @mageride/www
npm --prefix portals run test --workspace @mageride/www
```

By hand: the whole page at 375 / 768 / 1024, in every rendered locale, in both appearances, with
reduced motion on and off; the lightbox with keyboard only; JavaScript off (every section readable,
stats showing real numbers).

---

## Handoff

- **Component:** C134 www-informational-site — S15 (home sections) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** any section that needed a fact the content corpus did not have; the reduced-motion
  degradation for each animated section; the JS-off read.
