# C134 · S19 — SEO, accessibility, performance, dark mode

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 19 of 22 · Phase 6.**

**Prerequisite:** S18 — every route renders.

**This is the first MageRide surface that wants to be indexed.** Every other portal sets
`robots: { index: false }`; `portals/web-passenger/app/layout.tsx` explains why in its own words —
every URL on that host carries somebody's live share token. This surface inverts all of it, so none
of the other three portals is a precedent for anything in this session.

---

## Do this

### 1 · Metadata — `src/lib/seo.ts`

Per-route `generateMetadata`: title, description, canonical, Open Graph, Twitter card. Built from
`src/lib/routes.ts` and the content registries so a new chapter gets metadata without an edit.

`metadataBase` is `https://www.mageride.lk` — the canonical host, which is why the apex 301s (S21).

### 2 · `hreflang` — reciprocal on every page

`si-LK`, `ta-LK`, `en-LK`, plus `x-default`. Emit through Next's `alternates.languages`.

Two rules that are usually got wrong:

- **Reciprocity.** If `/si/drivers` lists `/en/drivers`, then `/en/drivers` must list `/si/drivers`.
  Generate both sides from one route table and `test/seo.test.ts` asserts it — do not hand-write.
- **Only rendered locales.** If Tamil was deferred (S13), `ta-LK` must **not** appear anywhere, and
  `x-default` points at Sinhala (the platform default). Advertising a locale that 404s is worse than
  advertising none.

### 3 · JSON-LD

- `Organization` — the platform, on every page or in the root layout.
- `WebSite` + `SearchAction` — **only if the site actually has a search endpoint.** It does not.
  **Omit `SearchAction`**; a `potentialAction` pointing at a URL that does not exist is a false
  declaration. Ship `WebSite` alone.
- `SoftwareApplication` ×2 — the Passenger app and the Driver app. `operatingSystem` Android (and
  iOS when listed); `offers` free. **`installUrl` only if D3 gave real store URLs.**
- `FAQPage` on `/faq` — from the same `faq.ts` the page renders, so the two cannot diverge.
- **`HowTo` on every guide chapter** — S07's `Step[]` shape maps directly: `name` from the title,
  `step` from the steps, `image` from the `screenRef`. This is the payoff for the uniform chapter
  shape.
- `BreadcrumbList` on guide chapters and legal docs.

Every block is emitted from the same data the page renders. **Never a hand-written literal**, or the
structured data and the visible page drift and Google penalises the difference.

### 4 · `app/sitemap.ts` and `app/robots.ts`

- `sitemap.ts` generated from `src/lib/routes.ts` — every route, every rendered locale, with
  `alternates`. `test/seo.test.ts` asserts **100% route coverage**, which is what makes "a new
  chapter cannot be omitted" true rather than hoped.
- `robots.ts` — allow all, point at the sitemap. Delete `public/robots.txt` if S03 created one; two
  sources for the same file is how a stale disallow survives.

### 5 · `app/opengraph-image.tsx` — per route family

Dynamic OG cards using Next's image generation. One design per family (home, role page, guide
chapter, legal). Fonts must be loadable at edge runtime — if Noto Sans Sinhala is too large to embed,
render OG cards in English for all locales and record that; a broken OG image is worse than an
English one.

### 6 · Accessibility target — **WCAG 2.2 AA**

URD Epic 19 covers the apps, not the web. AA here is a deliberate raise and the right one for a
public, government-adjacent platform. Concretely, audit and fix:

- the carousel against the APG pattern (S14) and the lightbox against the dialog pattern (S15);
- **visible focus on every control** — the preset's `:focus-visible` ring, applied globally, exactly
  as `portals/web-passenger/app/globals.css` does it and for the same stated reason;
- **4.5:1 text contrast verified against both D2 palettes** — this is the check most likely to
  actually fail, because marketing gradients and low-contrast "subtle" text are where it goes wrong.
  Measure; do not assume the tokens are safe in every pairing you invented;
- a skip link, correct landmarks, one `<h1>` per page, no heading level skipped;
- **`lang` switching on mixed-script content** — the language band (S15) and any Sinhala word inside
  an English page;
- 44×44 touch targets;
- no keyboard trap anywhere; reduced motion honoured everywhere.

### 7 · Performance budget

Target **LCP < 2.0s on a 3G-throttled mid-range Android** — the actual Sri Lankan median device, not
a desktop score.

Turn `scripts/check-bundle.mjs`'s reported figures into **thresholds**:

- JS ≤ **90 KB gzipped** on `/`
- CSS ≤ **25 KB**
- hero image ≤ **120 KB** AVIF
- **zero render-blocking third-party requests** — and there are none, because there is no analytics,
  no CDN font and no map on this surface. Assert it rather than assuming it.

If `/` is over budget, the first thing to look at is how much of the hero and the motion layer is
client-side. Most of this site should be server-rendered with a small island for the carousel.

### 8 · Dark mode with a toggle

`.dark` on `<html>` with the same pre-paint script `web-passenger` uses — read it; it exists to stop
a flash of the wrong theme, and the reasoning transfers.

**Unlike `web-passenger`, add a user toggle persisted in `localStorage`.** That surface is under
D6′ I-29.1's "no cookies, no localStorage of ride data" because it holds somebody's live ride. This
page holds nothing, so a remembered preference is both safe and expected. Order of resolution:
stored preference → `prefers-color-scheme` → light.

`localStorage` is **not a cookie** and the no-cookie fence is intact. Say so in
`portals/www/CLAUDE.md` so a later reader does not "fix" it.

### 9 · Zero tracking, stated on the page

No Google Analytics, no Meta pixel, and **no cookie banner because there are no cookies**. Put one
honest line on `/legal/privacy` saying so.

If measurement is wanted later the only fence-compatible options are server-side log analysis or a
self-hosted cookieless counter — **its own change set, never a quiet `<script>`.** Write that
sentence into `portals/www/CLAUDE.md`.

---

## Fences

- **No analytics, no third-party script, no CDN font, no cookie.**
- **No JSON-LD that describes something the page does not have** (this is why `SearchAction` is out).
- **No `hreflang` for a locale that does not render.**
- **The budget numbers are the fence.** Do not raise a threshold to make a build pass.

---

## Verify

```
npm --prefix portals run lint
npm --prefix portals run build --workspace @mageride/www    # check-bundle now enforces thresholds
npm --prefix portals run test --workspace @mageride/www
curl -s localhost:3004/sitemap.xml | head                    # after `npm run start`
grep -rnE "googletagmanager|google-analytics|facebook\.net|hotjar|segment" portals/www/   # nothing
```

Validate one page of each family through a structured-data validator, and run axe over `/`,
`/drivers` and one guide chapter in every rendered locale.

---

## Handoff

- **Component:** C134 www-informational-site — S19 (SEO, a11y, performance) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** measured JS/CSS/LCP against the budget; every contrast pairing that failed and how it
  was fixed; whether OG cards render Sinhala or fell back to English; the JSON-LD types shipped and
  any omitted with the reason.
