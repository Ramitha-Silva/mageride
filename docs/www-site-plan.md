# `www.mageride.lk` — Public Informational Site · Implementation Activity Plan

**Status:** proposal — nothing in this document has been implemented.
**Author:** planning session, 2026-08-27.
**Target:** a Next.js marketing/information site at `www.mageride.lk` with a sliding hero,
motion design, vision & mission, high-fidelity screen imagery derived from `specs/wireframes/`,
and complete how-to-use guides for passengers and drivers.

---

## 0. What I found before planning (read this first)

Five findings changed the shape of this plan. None of them is a blocker; all of them are
work that would otherwise be discovered halfway through.

### 0.1 `www.mageride.lk` does not exist anywhere in the repo or the specs

A sweep of every `.md`, `.yml`, `.yaml`, `.ts`, `.cs`, `.sh` and `.json` in the tree returns
**141 references to `mageride.lk` and zero to `www.mageride.lk`**. The specs are explicit that
the platform has **four surfaces plus one subview**:

> *"The platform has **four surfaces**: the MageRide Passenger App, MageRide Driver App, the
> Fleet Portal (`fleet.mageride.lk`), and the Admin Portal (`admin.mageride.lk`).
> `passenger.mageride.lk` is **not a separate surface**…"*
> — `specs/user-requirements-document.md` §2.2

`infra/k8s/service-catalog.yaml` lists three `portals:` entries. `infra/k8s/base/ingress/ingress.yaml`
routes three portal hosts. `infra/docker/Dockerfile.portal`'s header names three portals.
`portals/package.json` declares seven workspace members.

**A public marketing site is therefore a fifth surface, and CLAUDE.md's first Universal Rule
applies:** *"Specs are the single source of truth… If code contradicts a spec, the spec wins —
file a micro-change-set if the spec needs updating."* Phase 0 below files that change set
**before** any code is written. Skipping it would leave the repo's own drift checks
(`generate_manifests.py --check`, `infra/k8s/tools/check_fences.py`, `k8s-verify.sh`) describing
a topology that no longer matches reality.

### 0.2 The design system, the component kit and the i18n scaffolding already exist and are enforced

| Package | What it gives the site | Where |
|---|---|---|
| `@mageride/tailwind-preset` | All 16 D2 §0.2 semantic colours (light+dark), 11 vehicle-marker colours, 3 mode badges, 8 type roles, 7 spacing steps, 4 radii, 6 elevations, 3 breakpoints | `portals/tailwind-preset/` |
| `@mageride/ui` | Button/CTA, Field, Chip, StatusPill, Table, Modal, Toast, Tabs, Dropzone — headless (Radix) + Tailwind | `portals/ui/` |
| `@mageride/i18n` | `Locale`, `LOCALES`, `negotiateLocale()`, `createTranslator()`, and a `Messages` type that makes a missing translation a **compile error** | `portals/i18n/` |
| `@mageride/eslint-config` | `mageride/no-literal-user-facing-strings`, `mageride/no-runtime-css-in-js` | `portals/eslint-config/` |

Two gates run on every `npm --prefix portals run lint`:

- **AL-52** — `portals/scripts/check-al52.mjs` walks every `package.json` and every source file
  under `portals/` and fails on any of 18 banned packages, 6 banned prefixes, or a `<style jsx>`.
  MUI, Bootstrap, styled-components, Emotion, Chakra, Mantine, Ant, vanilla-extract, Linaria and
  Stitches are all excluded. **This constrains the animation approach** — see A11.
- **Trilingual** — the ESLint rule refuses a literal user-facing string in JSX. Every word on
  this site must exist as a resource key in **si, ta and en**. See §0.4 and A23, which is the
  single largest cost item in the plan.

The site inherits all of this by being a workspace member of `portals/`. Building it outside
`portals/` would fork the token system, and AL-52's check would never see it.

### 0.3 The content already exists, in plain language, and does not need to be invented

`specs/MageRide_Functional_Walkthrough.md` is 198 KB / **114 numbered scenarios** across
Sections A–K, and its §1 "Platform Overview" is already written for a lay reader:

> *"Think of it as three things in one: 1. A live map of buses, trains, school vans… 2. A
> ride-hailing service… 3. A delivery service…"*

That document, plus `specs/D1_mageride_user_flows.md` (Section A = 6 passenger flow groups,
Section B = 11 driver flow groups), plus the URD's **24 epics**, are the quarry for the how-to
guides. The work is **selection, rewriting for a public audience, and translation** — not
research. §6 maps every guide chapter to its source anchor.

### 0.4 Vision exists. Mission does not. Both need sign-off

- **Vision** — `specs/user-requirements-document.md` §1 "Product Vision" is a dense technical
  paragraph, not public copy. `MageRide_Government_Proposal.md` §"The Vision" carries a much
  better public framing ("*A Sri Lankan citizen opens one app and sees every bus, every train,
  every three-wheeler, and every van in the country moving in real time…*"). Both are sources;
  neither is publishable as-is.
- **Mission** — **there is no mission statement anywhere in the repository.** Grep for
  `mission` across `specs/` returns only "permission", "commission" and "provisioning".

A mission statement is a positioning commitment, not a technical detail. **I will draft three
options and will not publish one without your explicit choice.** Listed under Open Decisions (§11).

### 0.5 Sinhala and Tamil have no glyphs in the platform's display fonts

D2 §0.2 names **Outfit** (display/headline) and **Inter** (body). The passenger web subview's
own layout comment is candid about the consequence:

> *"Neither face carries Sinhala or Tamil glyphs and neither is asked to: those subsets fall
> through to the phone's own system face."*
> — `portals/web-passenger/app/layout.tsx`

On a token-gated utility page opened from an SMS, that is fine. On a marketing site whose hero
sets 32–72px display type in Sinhala, a system-font fallback at those sizes will look visibly
unfinished beside the English cut. Activity **A13** adds Noto Sans Sinhala + Noto Sans Tamil as
script-scoped display faces — which is a token addition, and therefore itself a small change set
against D2 §0.2.

---

## 1. Deliverable summary

| | |
|---|---|
| **New folder** | `portals/www/` — an npm workspace member, package `@mageride/www` |
| **Host** | `www.mageride.lk` (apex `mageride.lk` 301-redirects to it) |
| **Framework** | Next.js 16 App Router, React 19, TypeScript strict, Tailwind v4 |
| **Rendering** | Fully static content, shipped in the same `output: 'standalone'` container shape as the other three portals |
| **Languages** | Sinhala (default), Tamil, English — path-prefixed `/si`, `/ta`, `/en` |
| **Pages** | ~14 top-level routes + ~34 guide chapters × 3 locales ≈ **145 rendered pages** |
| **Screen images** | ~70 curated frames from `specs/wireframes/*.html`, composited into device mockups |
| **New component ID** | **C134** (C133 = `payout-svc`; C134 is free) |
| **New change-set ID** | **MCS-34** (MCS-33 is the highest used; 01–33 are taken) |
| **Estimated effort** | **20–25 build sessions**, phased (see §14) |

---

## 2. Phase 0 — Governance (before any code is written)

*This phase produces no application code. It exists because every automated drift check in this
repository is derived from a declaration file, and adding a surface without updating those
declarations turns green builds into false ones.*

### A1 · Write the micro-change-set `build/prompts/MCS-34-www-informational-site.md`

Hand-written, following the `MCS-06` template exactly: the identity block explaining it is **not**
a manifest regeneration target, the finding, the change, the affected specs.

It must record, precisely:

- URD §2.2's "four surfaces" sentence becomes **five surfaces**, with `www.mageride.lk`
  described as *public, unauthenticated, no personal data, no API dependency at request time*.
- ADD §6 / D7 §2.1's container table gains one optional Next.js container.
- D7 §5's Ingress host list gains `www.mageride.lk` and the `mageride.lk` apex redirect.
- D2 §0.2 gains two script-scoped display faces (Noto Sans Sinhala, Noto Sans Tamil), scoped to
  the web — Compose and SwiftUI already resolve Sinhala/Tamil from the platform type stack.
- A statement that **no existing SCR-\* screen ID is claimed by this surface**, so
  `build/screen_coverage.md`'s 202/202 equality is untouched. New IDs would use a fresh
  `SCR-WW-###` family **only if** you want the site's own screens wireframed; my recommendation
  is **no** — a marketing site is a design artefact, not a spec'd screen set, and adding 14 IDs
  to the coverage matrix creates permanent maintenance for no gain.

**Gate:** your approval of the spec deltas before A2 runs.

### A2 · Apply the spec edits named in MCS-34

Edit `specs/user-requirements-document.md` §2.2, `specs/D7_mageride_devops.md` §2.1 and §5, and
`specs/D2_mageride_ui_spec.md` §0.2 (font table only). Small, surgical diffs; each carries a
`Δ 2026-08-27 (MCS-34)` marker in the house style already used throughout those files.

### A3 · Add component **C134** to `build/manifest.yaml`

New entry in wave `4c`, modelled on C117's shape:

```yaml
- id: C134
  name: www-informational-site
  wave: 4c
  depends_on: [C103]          # tailwind-preset + ui + i18n
  stack: portals/www/CLAUDE.md
  screens: []                 # claims no SCR-* ID — see MCS-34
  spec_anchors:
    - specs/user-requirements-document.md#1-product-vision
    - specs/MageRide_Functional_Walkthrough.md#1-platform-overview
    - specs/D2_mageride_ui_spec.md#0-2-mageride-design-tokens
    - specs/D1_mageride_user_flows.md
  scope: |
    The public informational site at www.mageride.lk — vision & mission, the three transport
    modes, a screen showcase derived from the approved wireframes, and complete how-to-use
    guides for passengers and drivers. ADD: [AL-52].
  fences:
    - "Tailwind CSS is the SOLE styling system (AL-52). No animation library that injects styles
       at runtime — motion is CSS keyframes + the Web Animations API."
    - "No API call at request time. This surface must render with the whole backend down."
    - "No cookies, no analytics that sets one, no personal data collected on any page."
    - "Every user-facing string is a si/ta/en resource. No literal in JSX."
  definition_of_done: …          # see §15
  verify_cmd: "npm --prefix portals run lint && npm --prefix portals run build --workspace @mageride/www && npm --prefix portals run test --workspace @mageride/www"
  est_sessions: 20
```

### A4 · Regenerate the build plan, then restore hand-maintained state

```
python3 build/tools/generate_build_plan.py
```

⚠ **This is destructive to hand-maintained state.** The generator's own docstring warns that
re-running *"resets the Status column and the whole Session Handoffs log in
`build/progress.md`"* — and that log is roughly 20,000 lines of accumulated component history.

**Procedure, not optional:**

1. Commit the manifest edit alone first.
2. `cp build/progress.md /tmp/progress.before.md`
3. Run the generator.
4. Re-apply the Status column and the entire **Session Handoffs** section from the copy.
5. `git diff build/progress.md` must show **only** the C134 row added and the wave-4c gate line
   updated — nothing else.

**Deliverable:** `build/prompts/C134.md` generated; `build/progress.md` gaining exactly one row.

---

## 3. Phase 1 — Scaffold the folder and wire it into the workspace

### A5 · Create `portals/www/`

```
portals/www/
├── CLAUDE.md                  # surface conventions, in the house style
├── package.json               # @mageride/www
├── next.config.ts
├── postcss.config.mjs
├── tsconfig.json
├── eslint.config.js
├── vitest.config.ts
├── .env.example
├── app/
│   ├── layout.tsx             # fonts, <html lang>, pre-paint theme script
│   ├── globals.css            # @import tailwindcss + preset theme.css — the ONLY stylesheet
│   ├── icon.svg               # the D2 primary tile mark
│   ├── opengraph-image.tsx    # generated OG card, per route family
│   ├── sitemap.ts
│   ├── robots.ts
│   ├── [locale]/
│   │   ├── layout.tsx         # locale provider + header + footer
│   │   ├── page.tsx           # HOME
│   │   ├── vision/page.tsx
│   │   ├── passengers/page.tsx
│   │   ├── drivers/page.tsx
│   │   ├── fleets/page.tsx
│   │   ├── screens/page.tsx
│   │   ├── guide/
│   │   │   ├── page.tsx                    # guide index
│   │   │   ├── passenger/[chapter]/page.tsx
│   │   │   └── driver/[chapter]/page.tsx
│   │   ├── faq/page.tsx
│   │   ├── download/page.tsx
│   │   ├── contact/page.tsx
│   │   └── legal/[doc]/page.tsx            # terms · privacy · pdpa
│   ├── not-found.tsx
│   └── error.tsx
├── src/
│   ├── content/               # the guide corpus (typed TS, not MDX — see A6)
│   │   ├── guide/passenger/*.ts
│   │   ├── guide/driver/*.ts
│   │   ├── faq.ts
│   │   ├── vision.ts
│   │   ├── screens.ts         # SCR-* → image + caption registry
│   │   └── index.ts           # registry, chapter ordering, slug map
│   ├── i18n/
│   │   ├── messages/{si,ta,en}.ts
│   │   ├── index.ts
│   │   └── server.ts
│   ├── components/
│   │   ├── hero/              # HeroSlider, HeroSlide, HeroControls, HeroProgress
│   │   ├── motion/            # Reveal, Parallax, CountUp, StickySteps, MarqueeStrip
│   │   ├── showcase/          # DeviceFrame, ScreenCarousel, ScreenLightbox, FeatureSplit
│   │   ├── guide/             # ChapterNav, StepList, Callout, ScreenRef, ChapterPager
│   │   ├── nav/               # Header, LocaleSwitcher, ThemeToggle, MobileMenu, Footer
│   │   └── marketing/         # ModeCard, FareTable, StatTile, FAQAccordion, CTABand
│   └── lib/
│       ├── motion.ts          # reduced-motion helper, IO factory, rAF scheduler
│       ├── seo.ts             # metadata + JSON-LD builders
│       └── routes.ts          # typed route table — feeds sitemap, nav and hreflang
├── public/
│   ├── robots.txt
│   ├── screens/               # GENERATED — see Phase 3
│   └── brand/
├── scripts/
│   ├── capture-screens.mjs    # Playwright: wireframe → PNG
│   ├── compose-frames.mjs     # PNG → device mockup → AVIF/WebP
│   ├── check-bundle.mjs       # budget gate, copied from web-passenger
│   └── check-i18n-parity.mjs  # every key in si/ta/en, no orphans
└── test/
    ├── a11y.test.ts
    ├── i18n.test.ts
    ├── routes.test.ts
    ├── seo.test.ts
    ├── content.test.ts
    └── fences.test.ts
```

### A6 · Decide the content format — typed TypeScript, not MDX

**Recommendation: typed TS modules under `src/content/`.** Reasons specific to this repo:

- MDX needs `@next/mdx` plus a remark/rehype chain; every plugin is a new dependency the AL-52
  sweep must be reasoned about, and MDX authored per-locale means the **structure** of a chapter
  can silently diverge between Sinhala and English.
- A typed `Chapter` interface (`title: MessageKey`, `steps: Step[]`, `screens: ScreenRef[]`)
  keeps the *shape* shared and localises only the *strings* — so a translator cannot drop a step,
  and `@mageride/i18n`'s compile-time `Messages` check covers guide prose as well as UI labels.
- The site then renders identically with the backend down, which is C134's second fence.

### A7 · Register the workspace and update every list that enumerates portals

Four places enumerate the portal set, and **all four drift silently if one is missed**:

| File | Change |
|---|---|
| `portals/package.json` | add `"www"` to `workspaces` |
| `infra/docker/Dockerfile.portal` | add `COPY ["portals/www/package.json", "./portals/www/"]` — the header comment states this list "must be kept in step with" the workspaces array, and omitting a member fails `npm ci` for **unrelated** portals |
| `infra/docker/Dockerfile.portal` header | add `www` to the PORTAL build-arg table |
| `infra/k8s/service-catalog.yaml` | new `portals:` entry (A42) |

### A8 · `next.config.ts`

`output: 'standalone'` plus `outputFileTracingRoot` pointing two levels up — identical reasoning
to `portals/web-passenger/next.config.ts`, because the Dockerfile asserts
`.next/standalone/portals/<portal>/server.js` and Next otherwise roots the trace at
`portals/` and emits one segment less. `poweredByHeader: false`.

**No `tailwind.config.js`** — a v4 JS config *merges* `screens` instead of replacing them, which
would quietly restore Tailwind's 640px `sm:` over D2's 375px one.

Add `images: { formats: ['image/avif', 'image/webp'] }`, and cache headers per A44.

### A9 · Write `portals/www/CLAUDE.md`

Surface conventions in the same voice as the other three: the four C134 fences, the motion
policy from A11, the "no API at request time" rule, the content-format decision from A6, and the
image-pipeline contract from Phase 3.

---

## 4. Phase 2 — Design-system extension and the motion architecture

### A10 · Audit what the preset gives and what a marketing site needs that it does not

The preset is built for **product UI**. A marketing hero needs a handful of things D2 §0.2 has no
token for. Each addition below is *composed from* existing tokens rather than invented, and each
is recorded in `portals/www/CLAUDE.md` with its derivation:

| Need | Resolution |
|---|---|
| Display sizes above 32px | `text-display` is 32px (D2's largest). A hero needs ~48/64/72px. Compose `clamp()` utilities in `@layer utilities` off D2's 4px grid — **not** new tokens. |
| Gradient / aurora backdrops | Built from `primary`, `primary-container`, `secondary-container` at defined alpha. No new hex enters the system. |
| Section rhythm | D2's `xxl` is the largest spacing step; marketing sections want 96/128px — again `@layer utilities`, on the 4px grid. |
| A 4th breakpoint | **Do not add one.** D2 defines three widths (375/768/1024) and the preset *replaces* Tailwind's screens. A 1440px tier would be a genuine token addition — flag it in MCS-34 if you want it. Recommendation: cap content at `max-w-[1200px]` and stay on three. |

### A11 · Choose the motion approach — **CSS + Web Animations API, no motion library**

This is the plan's most consequential technical decision, so the reasoning is written out.

`check-al52.mjs` bans 18 packages and 6 prefixes. **Framer Motion / `motion` is not on that
list**, so it would pass the automated gate. But AL-52's stated intent, quoted in
`portals/web-passenger/postcss.config.mjs`, is *"CSS is compiled at build time by PostCSS inside
`npm run build`. One plugin, no runtime style injection."* Framer Motion's entire mechanism is
runtime style injection. Shipping it would either be a fence violation the checker happens not to
catch, or require widening AL-52 in the ADD — a platform-wide architectural change for one
marketing page. Neither is a good trade.

**Everything the brief asks for is achievable without it:**

| Effect | Mechanism |
|---|---|
| Sliding hero — autoplay, swipe, keyboard | CSS `scroll-snap-type: x mandatory` + `scrollIntoView({behavior:'smooth'})`; an `IntersectionObserver` on the slides drives the active dot. ~90 lines, no dependency. |
| Scroll reveal (fade / rise / stagger) | `IntersectionObserver` toggles a class; CSS `transition` with a per-child `--i` delay |
| Parallax on device mockups | `rAF`-throttled scroll → a CSS custom property → `translate3d`. Writes a *variable*, never a rule. |
| Stat counters | `Element.animate()` on an `@property`-registered custom property |
| Sticky "how it works" scroll-through | `position: sticky` + IO. Zero JS animation. |
| Animated map markers / route draw | Inline SVG + `@keyframes` + `stroke-dasharray` |
| Route-to-route transitions | Next.js 16 **View Transitions API** — native, no library |
| Logo / feature marquee | CSS `@keyframes` translate on a duplicated track |
| Gradient hero backdrop | `@property` + `@keyframes` on registered custom properties (GPU-composited) |

All keyframes live in `app/globals.css` under `@layer utilities` — compiled by PostCSS at build
time, which is exactly what AL-52 asks for.

### A12 · `prefers-reduced-motion` is a first-class path, not an afterthought

Every motion utility ships its reduced variant in the same rule block:

```css
@media (prefers-reduced-motion: reduce) {
  /* transforms → opacity-only or nothing; autoplay stops; parallax is inert */
}
```

The hero's autoplay timer checks `matchMedia('(prefers-reduced-motion: reduce)')` at mount and
**does not start** if set. This is a URD Epic 19 (Accessibility) obligation, and it is also the
difference between "professional animation" and "animation that makes people ill".

### A13 · Add Sinhala and Tamil display faces

`next/font/google` for **Noto Sans Sinhala** and **Noto Sans Tamil**, self-hosted (no CDN
`<link>` — the other portals keep that posture for CSP reasons). Bind them as
`--mr-font-sinhala` / `--mr-font-tamil` and select per-locale on `<html>`, so the Outfit/Inter
pair still carries Latin. Covered by the D2 §0.2 delta in MCS-34 (A2).

---

## 5. Phase 3 — High-fidelity screen imagery from the wireframes

**Honest framing up front:** `specs/wireframes/*.html` is described by its own index as
*"Mid-fidelity, self-contained HTML keyed to the D2′ §0.2 design tokens"*. Screenshotting them
raw gives mid-fidelity pictures. Three routes to "high fidelity"; I recommend the second:

| Route | What it gives | Cost | Verdict |
|---|---|---|---|
| Screenshot the wireframes as-is | Honest, fast, and too plain for a marketing site | 1 session | ✗ |
| **Re-render wireframe frames through a token-accurate polish stylesheet, then composite into device frames** | Marketing-grade, structurally faithful to the approved screens, fully reproducible | **3–4 sessions** | **✓ recommended** |
| Screenshot the real built apps | Truest, but iOS does not build on the Contabo Linux host (CLAUDE.md, Build Host) and every shot needs seeded state | 6+ sessions, blocked on a Mac | Post-launch upgrade |

### A14 · Curate the frame list

Enumerate all 202 IDs with the command already documented in `build/screen_coverage.md`, then
select **~70**: the screens each guide chapter actually references, plus ~12 hero screens.
Deliverable: `portals/www/src/content/screens.ts` — a typed registry mapping `SCR-PA-014` → caption
key, guide chapter, device family (android/ios/web), and output filename.

### A15 · Build `scripts/capture-screens.mjs` (Playwright)

Headless Chromium, so it runs on the Contabo host with no Android or iOS toolchain. Per frame:
device-scale-factor 3, exact viewport per family (375×812 mobile, 1440×900 portal), clip to the
frame's bounding box by its `SCR-*` anchor, capture **both** appearances (light and `.dark`),
deterministic file naming. Playwright is a **devDependency of `portals/www` only** and never
reaches the bundle; it is not a styling package, so `check-al52.mjs` passes cleanly.

### A16 · Build `scripts/compose-frames.mjs`

Composites each capture into a device mockup: rounded corners, bezel, status bar, and a drop
shadow built from the D2 `shadow-elevation-*` values, on a token-derived gradient plate. Emits
AVIF + WebP + PNG fallback at 1x/2x. Uses `sharp` — no styling implications.

### A17 · Commit the outputs and gate their size

Generated images land in `portals/www/public/screens/`. **Commit them** — CI must not have to
download a browser to build the site. Add a budget to `scripts/check-bundle.mjs`: total
`public/screens/` ≤ **12 MB**, no single image > 220 KB. Add an `npm run screens:refresh` script
and a `README` in that folder recording that the files are generated and from what.

---

## 6. Phase 4 — Content: vision, mission, and the complete how-to guides

### A18 · Draft vision & mission (**gated on your sign-off**)

- **Vision** — rewrite URD §1 and the proposal's "The Vision" into ~120 words of public copy plus
  a one-sentence hero line.
- **Mission** — draft **three distinct options** (access-led / driver-livelihood-led /
  national-infrastructure-led), each with a one-paragraph rationale. You pick one.
- **Values** — 4–6 cards, each traceable to something the platform actually does: zero commission
  (URD §1), passengers pay nothing, trilingual by default, open-source mapping, first trip of the
  day free, PDPA data rights (D3 `pdpa-svc`).

### A19 · Passenger how-to guide — 16 chapters

Sources: Walkthrough Section A, D1 §A.1–A.6, URD Epics 1 / 4 / 7 / 8 / 10 / 12 / 15 / 16 / 18 / 19 / 20 / 22 / 23.

| # | Chapter | Primary source |
|---|---|---|
| 1 | Install & first run — language, city, phone + OTP, profile | Walkthrough A · URD Epic 1 |
| 2 | Permissions — location, notifications, background | D1 §A.5 · URD Epic 1 |
| 3 | Reading the live map — modes, the 11 vehicle colours, clusters | D2 §0.2 tbl 2 · MAP-03/05/06 |
| 4 | Tracking buses & trains (Mode A) | URD Epic 7 · transit-svc GTFS |
| 5 | Following a private vehicle (Mode B) — sharing grants, subscribe | URD Epic 4 |
| 6 | Mode B payments — Paid vs Free, monthly, history, unsubscribe | URD Epic 23 |
| 7 | Booking a ride (Mode C) — search / map pin / paste link / request | URD Epic 8 · D1 §F-23.2 |
| 8 | Choosing a vehicle and reading the upfront fare | URD Epic 8 · D5 §1 |
| 9 | Waiting, and what the 15-second dispatch is doing | D1 §B.9 |
| 10 | During the ride — live track, driver card, call, share, SOS | URD Epic 12 |
| 11 | Paying — cash, driver QR / LankaQR, receipt | URD Epic 8 · AL-15 |
| 12 | Sending a package — sizes, recipient, delivery code, three stages | URD Epic 20 |
| 13 | Booking for someone else, and the SMS web link | URD Epic 25 · AL-44 |
| 14 | Scheduling a ride | URD US-24.2 |
| 15 | Saved places, ratings & reviews | URD US-7.13 · Epic 18 |
| 16 | Settings, help & support, deleting my data (PDPA) | URD Epic 22 / 16 · US-1.8 |

### A20 · Driver how-to guide — 18 chapters

Sources: Walkthrough Section B, D1 §B.1–B.11, URD Epics 1 / 2 / 3 / 5 / 6 / 6A / 9 / 9A / 12 / 17 / 20.

| # | Chapter | Primary source |
|---|---|---|
| 1 | Install & first run — language/city, OTP, driver profile | D1 §B.7 Phase 1 |
| 2 | Onboarding your vehicle — the four steps | D1 §B.7 Phase 2 |
| 3 | Photographing documents — camera + drag-crop | URD US-24.6 · AL-43 |
| 4 | Approval — what auto-verifies, what a human reviews | URD Epic 2 |
| 5 | Permissions and background location | D1 §B.5 |
| 6 | Your dashboard — Mode C standby vs Mode A/B journey control | D1 §B.8 |
| 7 | Going on standby and staying visible | URD Epic 6A |
| 8 | The 15-second offer — accept, reject, what a miss costs | D1 §B.9 |
| 9 | Running a trip — navigate, arrive, start, end | D1 §B.9 |
| 10 | Directional travel — destination, daily uses, max duration | D2 SCR-DA · DT-01..08 |
| 11 | Package jobs — the three stages, proof of delivery | URD Epic 20 |
| 12 | Your wallet — top-up by card, OnePay, LankaQR | URD Epic 9A |
| 13 | The daily platform fee — **first trip free**, the six tiers | URD §1 · Epic 9 |
| 14 | Getting paid — cash and driver-QR settlement | URD Epic 26 |
| 15 | Bulk credit and transferring to other drivers | URD §2.2 (AL-01) |
| 16 | Mode A/B driving — journeys, ignition-started trips, trackers | URD Epic 3 / 5 |
| 17 | Ratings, driver level, and what affects the offers you get | URD Epic 18 · reputation-svc |
| 18 | Safety, emergency contact, support, app updates | URD Epic 12 / 16 / 17 · AL-13 |

### A21 · Fleet-owner guide — 6 chapters *(scoped as optional)*

You asked for drivers and passengers. Fleet Owner is the third end-user role, and its absence
will be noticeable on a site that mentions `fleet.mageride.lk`. **Recommendation:** a 6-chapter
short guide (register the org, KYC, add vehicles single/bulk, assign drivers, bind trackers,
billing) in the second delivery phase, not the first.

### A22 · Chapter template — one shape for every chapter

`{ id, slug, title, summary, audience, steps[] (instruction, note?, screenRef?),
callouts[] (tip | warning | fee | privacy), relatedChapters[], faqRefs[] }`.

A uniform shape means uniform components, uniform translation, and uniform structured data —
the step list maps straight onto `HowTo` JSON-LD (A32).

### A23 · Translation — **the plan's largest single cost**

Estimated corpus: **34–40 chapters × ~450 words + ~6,000 words of marketing copy ≈ 21,000 English
words → ~63,000 words across three languages.**

What I can do: draft all English; produce a first-pass Sinhala and Tamil; guarantee **structural**
parity, because `scripts/check-i18n-parity.mjs` plus the compile-time `Messages` check make a
missing key a build failure.

What I cannot do: certify that the Sinhala and Tamil read naturally to a native speaker —
transport, payment and legal terminology in particular. **Budget native review as a separate line
item.** If review is not available at launch, ship **English + Sinhala** complete with Tamil held
back, rather than shipping three languages where one is visibly machine-translated. Your call —
listed as D2 in §11.

---

## 7. Phase 5 — Pages, sections and the hero

### A24 · Home (`/[locale]`)

1. **Hero slider** — four slides: *Track everything live* · *Book a ride in seconds* · *Drivers
   keep 100%* · *Send a package across town*. Each slide is headline + sub + dual CTA + a device
   mockup from Phase 3. Autoplay 6s; pauses on hover, focus and tab-hidden; swipe; ←/→ keys; dots
   with a progress ring; `aria-roledescription="carousel"`; a live region announcing slide
   changes; reduced-motion collapses it to the first slide with manual controls.
2. **The three modes** — A/B/C cards on the `bg-mode-a/b/c` tokens.
3. **How it works** — sticky scroll-through, four steps, passenger/driver toggle.
4. **Feature splits** — five alternating image/text blocks, scroll-revealed.
5. **Zero-commission band** — the fee table, in minor-unit-correct Rupees.
6. **Screen showcase** — horizontal snap carousel opening into a lightbox.
7. **Stats** — animated counters (11 vehicle types · 3 languages · 0% commission · first trip free).
8. **Language band** — the same sentence set in Sinhala, Tamil and English.
9. **Download CTA** + footer.

### A25–A28 · `/vision`, `/passengers`, `/drivers`, `/fleets`

Role landing pages sharing one template: hero, benefit grid, three-step how-it-works, screen
strip, guide entry point, FAQ subset, CTA. `/drivers` additionally carries the **fee table with
the six vehicle-type tiers** and states the "first trip of the day is always free" rule exactly as
URD §1 states it.

### A29 · `/guide` and `/guide/{passenger|driver}/[chapter]`

Sticky left chapter rail at `desktop:`, reading column, right-hand on-page TOC, prev/next pager,
per-chapter screen references opening the lightbox, a print stylesheet, and a "was this helpful"
control that sets **no cookie and calls nothing** (a `mailto:`-style link, or omitted).

### A30–A31 · `/screens`, `/faq`, `/download`, `/contact`, `/legal/[doc]`

- `/screens` — filterable gallery (surface × mode × chapter) with a keyboard-navigable lightbox.
- `/faq` — accordion from the same corpus; emits `FAQPage` JSON-LD.
- `/download` — store badges. **Store URLs are unknown — D3 in §11.**
- `/contact` — **no form** (a form means a backend and a data-protection surface). Email, phone,
  address, hours. **Details are unknown — D4 in §11.**
- `/legal/[doc]` — Terms, Privacy, PDPA data rights. **Text must be authored or supplied by
  counsel.** The repo's `pdpa-svc` (export/erasure, 30-day due date) is what the privacy page
  should describe accurately, but the policy itself is a legal document, not a dev task.

---

## 8. Phase 6 — SEO, accessibility, performance

### A32 · SEO — the first MageRide surface that *wants* to be indexed

Every other portal sets `robots: { index: false }`. This one inverts that and needs the full kit:

- Per-route `generateMetadata` — title, description, canonical, OG, Twitter card.
- **`hreflang`** — `si-LK`, `ta-LK`, `en-LK` plus `x-default`, reciprocal on every page.
- **JSON-LD** — `Organization`, `WebSite` + `SearchAction`, `SoftwareApplication` ×2 (the two
  apps), `FAQPage` on `/faq`, **`HowTo` on every guide chapter** (A22's step shape maps directly),
  `BreadcrumbList`.
- `app/sitemap.ts` generated from `src/lib/routes.ts`, so a new chapter cannot be omitted.
- `app/robots.ts` — allow all, point at the sitemap.
- Dynamic `opengraph-image.tsx` per route family.
- Apex `mageride.lk` → `www.mageride.lk` **301** at the ingress (A43): one canonical host.

### A33 · Accessibility target — **WCAG 2.2 AA**

URD Epic 19 covers the apps, not the web. Setting AA here is a deliberate raise, and the right one
for a public, government-adjacent platform. Concretely: the carousel follows the APG carousel
pattern; visible focus on every control (the preset's `:focus-visible` ring); 4.5:1 text contrast
verified against **both** D2 palettes; skip link; landmarks; `lang` switching on mixed-script
content; 44×44 touch targets; no keyboard trap in the lightbox; reduced motion honoured everywhere.

### A34 · Performance budget

Target **LCP < 2.0s on a 3G-throttled mid-range Android**, because that is the actual Sri Lankan
median device.

| Budget | Limit | Measured 2026-08-30 |
|---|---|---|
| **First-party JS on `/`** | **≤ 90 KB gzipped** | 113.7 KB — **over by 23.7 KB** |
| **Framework floor** | *reported, not budgeted* | 163.4 KB gzipped |
| **Total JS a browser downloads for `/`** | **≤ 300 KB gzipped** | 277.1 KB |
| CSS on `/` | ≤ 25 KB gzipped | 9.8 KB |
| Hero image | ≤ 120 KB AVIF | 36 KB (largest hero plate, 2×) |
| Render-blocking third-party requests | 0 | 0 |

**The JS budget is two numbers because one number could not be met by any build of this surface,
and the shortfall is not ours** (MCS-36, raised from C134 · S19/S20). This was a single
`JS ≤ 90 KB gzipped on /`. Measured against the C134 build, before one line of the site's own code
loads: `react-dom` 69.8 KB gzipped, Next's app-router client 42.5 KB, React and the scheduler
32.9 KB, the Turbopack runtime and bootstrap 18.2 KB. **An empty App Router page breaches 90 KB by
73 KB.** 90 is a good figure for a hand-written page and a familiar one from guidance written
before the App Router; it reads as carried over rather than measured against this stack.

So the number itself is unchanged and is applied to **the bytes this surface controls**, which is
the half a session can actually regress. Three properties that shape does and a single figure does
not: it cannot be gamed by moving code into a vendor chunk (the classifier keys off this surface's
own string literals, which minification preserves and vendor code cannot contain); it still fails
on our regressions, which is what a budget is for; and a framework upgrade that adds 20 KB surfaces
as a **changed floor**, reviewed once, rather than as a mysteriously smaller allowance. The
300 KB total is the ceiling that stops the split being used to hide growth.

**The framework floor is a measurement, not a constant, and it belongs to a dependency set.**
163.4 KB gzipped, measured 2026-08-30 against **Next 16.3.0 · React 19.2.8 · react-dom 19.2.8**,
built with Turbopack. It moves when one of those moves and at no other time. Re-measure it on a
framework upgrade and record the new value here with its date; a floor carried forward without one
is the number this section exists to stop anybody trusting.

Two measurement rules, because they are the difference between a real figure and a comfortable one.
**Gzip**, which is what a CDN serves and roughly a third of the raw byte totals a bundler reports.
And the **`noModule` polyfill chunk is excluded** — 38.6 KB that no browser built this decade
fetches; counting it would inflate every figure here by a third for bytes nobody receives.

Enforced per page against the prerendered HTML — literally what a browser is handed — by
`portals/www/scripts/check-budget.mjs` (`npm run budget`), which runs in CI's portal leg beside
`lint` and `build`. **Deliberately not inside `npm run build`**: `infra/docker/Dockerfile.portal`
runs that, so a budget wired into it stopped the container image, S22's smoke tests, and — through
`pretest` — all eight portal workspaces' test suites. A performance finding should fail a *merge*,
not an *artefact*. `scripts/check-bundle.mjs` keeps the AL-52 fences and the screen-size budget,
which are artefact integrity and must block an image.

**The first-party row is red, and the rule is that it stays red until the bytes go.** ~91 KB of the
113.7 is the si + en resource tables, in the browser because eleven client components take a
`locale` and construct a translator. **MCS-36 D3** is the open decision that removes them. Do not
raise a threshold to make a build pass.

### A35 · Dark mode

`.dark` on `<html>` with the same pre-paint script `web-passenger` uses, plus — unlike that
surface — a **user toggle** persisted in `localStorage`. This page holds no ride data and carries
no "no storage" constraint, so a remembered preference is both safe and expected here.

### A36 · Zero tracking, stated on the page

No Google Analytics, no Meta pixel, and no cookie banner because there are no cookies. If
measurement is wanted later, the only options compatible with the fence are server-side log
analysis or a self-hosted cookieless counter — raise it as its own change set, never as a quiet
`<script>`.

---

## 9. Phase 7 — Testing and CI

### A37 · Test suite (Vitest + Testing Library, matching the other portals)

| File | Asserts |
|---|---|
| `test/i18n.test.ts` | every key exists in si/ta/en; no orphans; no literal user-facing string in any component |
| `test/routes.test.ts` | every route in `routes.ts` renders; every guide slug resolves in all three locales; the sitemap covers 100% of routes |
| `test/a11y.test.ts` | axe-core over every page shell; carousel roles and labels; focus order |
| `test/seo.test.ts` | canonical and hreflang present and reciprocal; every JSON-LD block parses and validates for its type; cache headers as specified |
| `test/fences.test.ts` | **no `fetch`/`axios` anywhere in the tree** (the "renders with the backend down" fence); no `NEXT_PUBLIC_*` carrying a secret; no cookie set; no banned motion package in `package.json` |
| `test/content.test.ts` | every `screenRef` resolves to a file that exists in `public/screens/`; the six driver fee tiers match URD §1 |

### A38 · CI wiring

`.github/workflows/ci.yml`'s portals job already runs `npm --prefix portals ci && run lint &&
run build` across **all** workspaces, so C134 is covered the moment it is a workspace member —
**no workflow edit is required.** Verify that job's runtime does not blow its timeout with a
fifth Next.js build; if it does, split the portals job by matrix.

### A39 · Lighthouse CI gate

A job asserting ≥95 Performance / 100 Accessibility / ≥95 SEO on `/`, `/drivers` and one guide
chapter, in all three locales. Below threshold fails the build.

### A40 · Visual regression *(optional, recommended)*

Playwright screenshot diffs on the hero and two guide chapters. Catches the class of change where
a token edit in `tailwind-preset` silently reflows this site.

---

## 10. Phase 8 — Deployment to `www.mageride.lk`

### A41 · Choose the hosting shape

| Option | Fit | Recommendation |
|---|---|---|
| **Container in DOKS, alongside the three portals** | Uses `Dockerfile.portal` unchanged; one deploy pipeline; one TLS story; ArgoCD-managed | **✓ default** — consistency beats the marginal cost |
| Cloudflare Pages / Vercel static | D7 §6 explicitly permits it; cheapest; best global TTFB; but forks the delivery path | Viable if you want the marketing site independent of cluster incidents |

This plan assumes the container path; the static path is a one-session variation that changes
A42–A45.

### A42 · Add the catalog entry (one edit; everything else is generated)

`infra/k8s/service-catalog.yaml`:

```yaml
  - name: www-site
    portal: www
    port: 3004                 # 3001 admin · 3002 fleet · 3003 web-passenger
    host: www.mageride.lk
    replicas: 2
    autoscale: { min: 2, max: 6 }
    resources:
      requests: { cpu: 100m, memory: 192Mi }
      limits:   { cpu: 500m, memory: 384Mi }
    why: >-
      MCS-34. The public informational site. Static content only — no API dependency at
      request time, so it stays up when the platform does not.
```

Then `python3 infra/k8s/tools/generate_manifests.py`, which writes
`base/portals/www-site.yaml`, updates `base/portals/kustomization.yaml`, and feeds `images.yml`'s
build matrix. **CI runs the same generator with `--check`**, so drift becomes a red build.

### A43 · Ingress: the host, the apex redirect, the certificate

In `infra/k8s/base/ingress/ingress.yaml`:

- add host `www.mageride.lk` → `www-site:80` to `mr-ingress-portals`, TLS secret `mr-tls-www`;
- add a **separate** Ingress object for the apex `mageride.lk` carrying
  `nginx.ingress.kubernetes.io/permanent-redirect: https://www.mageride.lk$request_uri`.
  Separate, because ingress-nginx annotations are object-scoped — which is precisely why that
  file already holds three objects rather than one.

The file's existing comment on why certificates are per-host rather than wildcard applies here:
this host is unauthenticated public traffic, so it follows the `passenger.` precedent.

### A44 · Cache headers

A marketing site's whole advantage is cacheability. Immutable hashed assets →
`max-age=31536000, immutable`; HTML → `s-maxage=300, stale-while-revalidate=86400`. Set in
`next.config.ts` headers and asserted in `test/seo.test.ts`.

### A45 · Replica (Contabo)

Add a `www-site` service to `infra/replica/docker-compose.light-replica.yml` under the existing
`portals` profile, plus a host rule in `haproxy.replica.cfg`.

⚠ CLAUDE.md's Build Host note: the full replica and heavy builds do not fit on the 24 GB box
together. Build the image; do not bring the whole stack up alongside it.

### A46 · DNS and certificate issuance

`www.mageride.lk` A/AAAA → the ingress load balancer; the apex likewise (or ALIAS). This is
**outside the repo** — registrar / Cloudflare work. Add it as a row to
`docs/production/go-live-checklist.md` so it is not discovered on launch day.

### A47 · Smoke check

Extend `infra/replica/smoke.sh` (currently 24 checks) with: `/` returns 200 in all three locales;
`/sitemap.xml` parses and lists every route; a randomly chosen guide chapter returns 200 and
contains its `HowTo` JSON-LD; the apex 301s to `www`.

---

## 11. Open decisions — I need your answer on these

| # | Decision | Why it blocks | My recommendation |
|---|---|---|---|
| **D1** | **Mission statement** — none exists in the repo | It appears on the homepage and `/vision` | I draft three options; you pick one |
| **D2** | **Tamil at launch, or English + Sinhala first?** | ~21k words × 3, no native reviewer identified | Ship si + en complete; Tamil next release rather than machine-translated at launch |
| **D3** | **Play Store / App Store URLs** | `/download` and every CTA point somewhere | If not live, `/download` becomes an email-notify page with no form |
| **D4** | **Contact details** — the proposal still reads `📧 [To be added]` | `/contact` and the footer on every page | Supply, or `/contact` ships email-only |
| **D5** | **Terms / Privacy text** | A public site collecting nothing still needs both | Supply from counsel; I structure and translate, I do not author |
| **D6** | **DOKS container vs Cloudflare Pages** | Changes A42–A45 entirely | Container — consistency with the three existing portals |
| **D7** | **Fleet-owner guide in scope?** | 6 chapters ≈ 2 sessions | Yes, but in the second delivery phase |
| **D8** | **Add a 4th `wide:` breakpoint (1440px) to D2 §0.2?** | A token change that reaches beyond this surface | No — cap at `max-w-[1200px]` |
| **D9** | **Do the site's own pages get `SCR-WW-###` IDs?** | Would enter `build/screen_coverage.md` permanently | No |
| **D10** | **Real app screenshots later?** | iOS needs a Mac; Android needs seeded state | Ship wireframe-derived now; upgrade after go-live |

---

## 12. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| `generate_build_plan.py` wipes `progress.md`'s ~20k-line handoff log (A4) | Catastrophic loss of build history | The backup → restore → diff procedure in A4; the diff must be one row |
| The trilingual corpus is 3× the visible work | Schedule slip | Decision D2; parity enforced by the compiler and a script, so it cannot silently rot |
| A motion library arrives via a "quick fix" | AL-52 fence violation the checker will not catch | The C134 fence plus `test/fences.test.ts` greps for the known motion packages |
| Wireframe-derived images read as mockups rather than product | Credibility | Device-frame compositing (A16) and honest captions; real screenshots post-launch |
| Sinhala at 64px in a fallback system font | A visibly unfinished hero | A13 adds Noto Sans Sinhala / Tamil |
| The portals CI job times out with a fifth Next build | Red main | Measure in A38; matrix-split if needed |
| Marketing claims drift from the product (fees, tiers, free first trip) | A factual error on a public site | Every number carries a spec anchor in `src/content/`; `test/content.test.ts` asserts the six fee tiers against URD §1 |

---

## 13. What this plan deliberately does not do

- **No CMS.** Content is typed TS in the repo, versioned with the code. A CMS is a second
  deployment, a second security surface, and a second place for the fee table to go stale.
- **No blog / news section.** It needs an owner and a publishing cadence; nothing in the repo
  suggests one exists yet.
- **No contact form, no newsletter signup, no chat widget.** Each collects personal data and each
  needs a backend, a PDPA position and a retention policy. All three are separate change sets.
- **No live map or live vehicle count on the marketing site.** It would break the "renders with
  the backend down" fence and put an API dependency in front of the platform's front door.
- **No new SCR-\* screen IDs** (decision D9).

---

## 14. Sequencing and effort

| Phase | Activities | Sessions | Gate to the next phase |
|---|---|---|---|
| 0 · Governance | A1–A4 | 1–2 | MCS-34 approved; the `progress.md` diff is one row |
| 1 · Scaffold | A5–A9 | 1 | `npm --prefix portals run lint && run build` green with the empty site in the workspace |
| 2 · Design & motion | A10–A13 | 2 | Motion primitives demoed; reduced-motion verified |
| 3 · Screen imagery | A14–A17 | 3–4 | ~70 frames committed, under the 12 MB budget |
| 4 · Content | A18–A23 | **5–7** | D1 answered; English corpus complete; si/ta parity green |
| 5 · Pages | A24–A31 | 4–5 | Every route renders in all three locales |
| 6 · SEO / a11y / perf | A32–A36 | 2 | Lighthouse thresholds met |
| 7 · Testing & CI | A37–A40 | 1–2 | The full verify chain green |
| 8 · Deploy | A41–A47 | 1–2 | `www.mageride.lk` serves; the apex 301s; smoke passes |
| | **Total** | **20–25** | |

Phases 3 and 4 are independent of Phase 5 and can interleave.

---

## 15. Definition of done for C134

- [ ] MCS-34 approved, and the spec files carry their `Δ 2026-08-27` deltas
- [ ] `build/progress.md` gained exactly one row; the Session Handoffs log is byte-identical to its backup
- [ ] `portals/www` is a workspace member and appears in `Dockerfile.portal`'s COPY list
- [ ] `npm --prefix portals run lint` passes — including `check-al52.mjs` clean over the new tree
- [ ] Every user-facing string is an si/ta/en resource; `check-i18n-parity.mjs` green; zero literals
- [ ] The hero slider meets the APG carousel pattern and is fully keyboard-operable
- [ ] `prefers-reduced-motion: reduce` disables autoplay and every transform-based effect
- [ ] 34+ guide chapters cover every passenger and driver capability across URD Epics 1–27
- [ ] ~70 screen images render at 2x in both appearances, totalling ≤ 12 MB
- [ ] Lighthouse ≥95 / 100 / ≥95 on `/`, `/drivers` and one guide chapter, in all three locales
- [ ] `test/fences.test.ts` proves no network call at request time — the site renders with the backend down
- [ ] `generate_manifests.py --check` clean; the ingress serves `www.mageride.lk`; the apex 301s
- [ ] `docs/production/go-live-checklist.md` gained the DNS and certificate rows
- [ ] A three-line handoff appended to `build/progress.md`

---

## 16. Files this plan creates or touches

**Created:** `portals/www/**` (~120 files) · `build/prompts/MCS-34-www-informational-site.md` ·
`build/prompts/C134.md` *(generated)* · `portals/www/public/screens/**` *(generated, ~140 images)*

**Modified:** `portals/package.json` · `infra/docker/Dockerfile.portal` ·
`infra/k8s/service-catalog.yaml` · `infra/k8s/base/ingress/ingress.yaml` ·
`infra/k8s/base/portals/**` *(generated)* · `infra/replica/docker-compose.light-replica.yml` ·
`infra/replica/haproxy.replica.cfg` · `infra/replica/smoke.sh` · `build/manifest.yaml` ·
`build/progress.md` · `specs/user-requirements-document.md` · `specs/D7_mageride_devops.md` ·
`specs/D2_mageride_ui_spec.md` · `docs/production/go-live-checklist.md`

**Deliberately untouched:** `build/screen_coverage.md` (decision D9) ·
`.github/workflows/ci.yml` (the portals job already covers every workspace member) ·
`portals/tailwind-preset/src/tokens.ts` (additions are composed in `@layer utilities`, not new tokens)
