# Public Informational Site (C134) — `www.mageride.lk`

Next.js 16 (App Router) + TypeScript + React 19, styled **only** with Tailwind through
`@mageride/tailwind-preset` (AL-52). npm workspace member `@mageride/www` under `portals/`, port
**3004**, the fifth MageRide surface (MCS-34).

**Verify:** `npm --prefix portals run lint --workspace @mageride/www && npm --prefix portals run test --workspace @mageride/www && npm --prefix portals run build --workspace @mageride/www`

`build/prompts/C134-www/` is the session plan — 22 hand-written prompts, one per session, run in
order. `docs/www-site-plan.md` is the planning document behind it and
`build/prompts/MCS-34-www-informational-site.md` is the change set that put this surface in the
specs. Where any of the three disagree with the repo, the repo wins and
`build/prompts/C134-www/README.md` §4 records why.

## What this is

The public site. Vision and mission, the three transport modes, a screen showcase built from the
approved wireframes, and complete how-to guides — **16 passenger chapters, 18 driver chapters and 6
fleet-owner chapters** — in Sinhala, Tamil and English.

The third guide is S23's and was conditional: MCS-34 **D7** asked whether the fleet owner — the third
end-user role, and the only one this site named without documenting — got a guide, and answered *yes,
in the second delivery phase*. That phase is done.

**It is described by what it does not do.** MCS-34 states four negatives, and they are the
load-bearing part: this surface is *public, unauthenticated, carries no personal data, and has no
API dependency at request time*. Those four are what let it sit outside the identity model
(US-11.8 is scoped to the four **product** surfaces and excludes this one), outside the PDPA
surface, and outside every availability argument the platform makes about itself. It renders with
the entire backend down.

The corollary is worth saying plainly, because it is the thing a later session will be tempted to
break: **nothing here may become useful.** A live vehicle count, a fare estimator, a "check if we
serve your area" box and a contact form are each one small feature and each one ends the four
negatives at once.

## The four fences, and the executable form of each

| Fence | What holds it |
|---|---|
| **AL-52 — Tailwind is the sole styling system, and no motion library** | `portals/scripts/check-al52.mjs` over the tree (shared) · `scripts/check-bundle.mjs` over the emitted chunks — runtime signatures, `node_modules/<name>` module ids, and a **motion** package/signature sweep the shared list does not cover · `test/fences.test.ts` on `package.json` and the imports · the "exactly one stylesheet" and "no `tailwind.config.*`" assertions |
| **No API call at request time** | `test/fences.test.ts` fails on `fetch(`, `axios`, `new EventSource`, `new WebSocket`, `navigator.sendBeacon` **anywhere** under `app/` or `src/`, and on any HTTP-client or map dependency in `package.json` |
| **No `NEXT_PUBLIC_*`, and no `process.env` at all** | `test/fences.test.ts` on the source · `scripts/check-bundle.mjs` fails if the **prefix** appears in any client chunk · `.env.example` is empty and says why |
| **Every user-facing string is an si/ta/en resource** | `mageride/no-literal-user-facing-strings` (shared ESLint rule) · `en.ts` defines the key type and `si.ts`/`ta.ts` are annotated with it, so a gap is a **compile error** · `scripts/check-i18n-parity.mjs` for the three things the compiler cannot see |

A fifth constraint is bookkeeping rather than a fence: **this surface claims no `SCR-*` ID**
(MCS-34 D9), so `build/screen_coverage.md` stays 202 / 202 and is never edited by a C134 session.

### Why `check-bundle.mjs` makes a *stronger* claim than `web-passenger`'s

That surface names its four server-only variables and searches the chunks for each, because it has
a gateway to reach and can only check that the address did not leak. This one has nothing to
configure, so the honest assertion is not "these four names are absent" but **"the `NEXT_PUBLIC_`
prefix does not appear at all."** The first public variable on this surface is the one that ends
the promise, so that is the thing the build refuses.

### The A34 budget is enforced, and it is red on purpose (S19)

**`scripts/check-budget.mjs` — `npm run budget`, and it is deliberately NOT part of `npm run
build`.** S19 put it there; **S21 moved it out**, because a byte budget wired into `build` stopped
three things that are not about performance: the **container image** (`Dockerfile.portal` runs
`npm run build --workspace www`, so C134 could not produce a deployable artefact at all), **every
portal's test suite** (`portals/package.json` has `pretest: npm run build`, so one surface's breach
aborted all eight), and **S22**, which needs an image to smoke-test. Nothing was lowered and nothing
stopped being enforced — it runs in CI's portal leg beside `lint` and `build`, so a regression still
fails `main`. A performance finding now blocks a *merge* rather than an *artefact*, which is what it
is actually about. `check-bundle.mjs` keeps the AL-52 fences and the screen budget, which **are**
artefact integrity and must block an image.

It measures the **prerendered HTML for each locale home page** — the bytes a browser is actually
handed — gzips exactly the files it references, and excludes the `noModule` polyfill chunk no modern
browser fetches. That is a different measurement from the whole-tree raw totals `check-bundle.mjs`
prints for its fences, and the two will not add up.

| | measured 2026-08-30 | budget | |
|---|---|---|---|
| first-party JS on `/` | **17.0 kB gz** | 90 kB | **passes** — was 113.7 before MCS-36 D3 |
| framework floor | 168.3 kB gz | *reported* | react-dom · Next router · React · runtime |
| total a browser downloads | **185.2 kB gz** | 300 kB | passes — was 277.0 |
| CSS | 9.8 kB gz | 25 kB | passes |
| largest hero plate (2×) | 36 kB AVIF | 120 kB | passes |
| third-party origins fetched | 0 | 0 | asserted, not assumed |

**A34's 90 KB was below the framework's own floor** — an empty App Router page breaches it by 73 kB
— so the number is applied to the bytes this surface controls, the floor is reported beside it, and
a **total ≤ 300 KB gz** ceiling stops the split being used to hide growth. **MCS-36 D1 and D2 were
accepted on 2026-08-30** and `docs/www-site-plan.md` §A34 states all three, so the spec and
`check-budget.mjs` agree.

**D3 was accepted the same day and the budget now passes with 73 kB spare.** Fourteen client
modules used to take a `locale` and build a translator, which put ~88 kB gzipped of resource tables
— three-quarters of it guide prose — into every page's bundle, including pages with no guide on
them. They take resolved strings now; see the client-boundary rule under *Rules for a page or a
component*, and `test/fences.test.ts`'s transitive client-graph check, which is what keeps the
tables out.

**Do not close a future gap by raising a number.** The rule S19 and S20 wrote still stands, and the
budget has room precisely because the fix was to ship fewer bytes rather than to move a threshold.

## Motion — CSS keyframes and the Web Animations API, no library

**Framer Motion would pass `check-al52.mjs` completely clean, and is still out.**
`portals/eslint-config/banned-styling-packages.json` lists 18 packages and 6 prefixes and
`framer-motion` / `motion` is on neither list. The fence is AL-52's *intent* — "CSS is compiled at
build time by PostCSS … one plugin, no runtime style injection" — which a library that writes
inline styles every frame violates precisely.

MCS-34 declined to widen the shared banned list to "fix" this: that is a platform-wide styling
change made for one marketing page, and it would reach Compose and SwiftUI readers who have no
stake in it. So the enforcement lives here instead, in two places, and **those two are the entire
enforcement** — `scripts/check-bundle.mjs`'s motion sweep and `test/fences.test.ts`. Do not add a
motion library; do not widen the shared list.

**S04 built the primitives.** `src/lib/motion.ts` carries three mechanisms and nothing else — the
reduced-motion reader plus its subscription, one shared `IntersectionObserver` per (root, margin,
threshold), and one `requestAnimationFrame` for the whole document. Every keyframe and every rule
lives in `app/globals.css`; the components in `src/components/motion/` own no timer, no observer and
no frame callback of their own.

| Effect | Mechanism | Component |
|---|---|---|
| Sliding hero — autoplay, swipe, keyboard | `scroll-snap-type: x mandatory` + `scrollTo` + IO over the slides | `HeroCarousel` (client) |
| Scroll reveal — fade / rise / stagger | IO toggles `data-mr-revealed`; CSS transition with a per-child `--i` delay | `Reveal` (client) |
| Parallax | `rAF`-batched scroll → one registered custom property → `translate3d` | `Parallax` (client) |
| Stat counters | `Element.animate()` over an `@property`-registered `<integer>`, rendered by `counter()` | `StatCounter` (client) |
| Sticky "how it works" | `position: sticky` + IO. Zero JS animation | `StickySteps` (client) |
| Route draw + markers | Inline SVG, `stroke-dasharray`, `@keyframes` | `RouteDraw` (**server**) |
| Marquee | `@keyframes` translating a duplicated track by `-50%` | `Marquee` (**server**) |
| Gradient hero backdrop | `@property` + composited `transform` keyframes on two blurred blobs | `AuroraBackdrop` (**server**) |

Three of the eight ship no JavaScript at all. That is the point rather than a coincidence: a marketing
site whose backdrop needs a hydration boundary has already lost the argument that made a library
unnecessary.

### Route transitions were checked and are **not** used

A11 assumed Next 16's View Transitions were available. They are not usable on the pinned dependency
set, and the reason is not a Next flag. Next's own guide says view transitions "work in the App Router
with no configuration" — because the App Router runs a React *canary* that exports `ViewTransition`.
This surface's `react` is `^19.2.8`, and stable React 19.2.8 exports neither `ViewTransition` nor an
`unstable_` alias; `@types/react@19.2.18` does not mention it at all, so `import { ViewTransition }
from 'react'` fails `tsc --noEmit` before it ever reaches a browser. Using it means adding
`react@canary` to a workspace whose other four surfaces would inherit it. **That is a dependency
change for a page transition, and a marketing site does not need one.** Revisit when the export is in
a stable React.

### `prefers-reduced-motion` is a path, not an afterthought (A12)

Every motion utility ships its reduced variant **in the same rule block**, so the two cannot drift.
The parts that are easy to get wrong, and were:

- **The hero's autoplay stops — it does not become instant.** A `@media` rule can take the transition
  off a carousel and leave it advancing every six seconds, which is the same vestibular problem with
  the animation removed. `HeroCarousel` reads `matchMedia` at mount, never starts the timer, and stops
  one already running when the setting changes mid-visit. Verified behaviourally: with the setting on,
  the track's `scrollLeft` is unchanged after eight seconds; with it off, it has advanced. The arrow
  keys and the dots keep working in both — reduced motion removes the timer, not the controls.
- **`animation: none` on the route line would be a bug, not a reduced variant.** That line's resting
  state is `stroke-dashoffset: var(--mr-www-route-length)`, which is an invisible path, so the reduced
  reader would get a blank frame. It resets the offset to `0` and shows the **finished** route.
- **A stopped marquee has an unreachable half.** Under reduced motion the track becomes an ordinary
  horizontal scroller and the `aria-hidden` duplicate is removed, so every item is reachable once.
- **`Reveal`'s hidden state lives inside `@media (scripting: enabled)`.** This site's promise is that
  it renders with the platform down; a reader with JavaScript off must not be handed a page of
  `opacity: 0`. With no script there is nothing to flip the attribute, so there is no hidden state to
  flip out of. (A script that is enabled but fails to *load* is the residual case, and it is the same
  risk any hydration-dependent UI carries.)
- **`StickySteps` gets no reduced block at all**, on purpose and not by omission: nothing in it
  transforms, and opacity is what the reduced path *is* everywhere else here.

**`test/a11y.test.tsx` (S20) asserts this**, and the workbench that used to check it by eye is gone
with it. The autoplay claim is the one that needed a test rather than a look: a carousel that
advances under `prefers-reduced-motion` is pixel-identical in a screenshot to one that does not. It
is driven with fake timers over **ten** intervals — a timer registered and cleared a microtask later
would pass a one-tick test by accident, and the fence is that autoplay never starts — and asserted
in both directions, so the test fails if the carousel stops moving for everyone.

### A finding worth not rediscovering: `counter-reset` scope

The stat counter renders through `counter()`, and **`counter-reset` must be on the element while
`content: counter()` is on its `::after`**. Declaring both on the `::after` is not in scope for
itself: the counter resolves to its initial value and the number renders a permanent `0` — while the
custom property animates correctly and `getComputedStyle` reports the right value the whole time. It
is invisible from the DOM and only visible in a screenshot. Verified in Chromium against both
spellings side by side.

## The marketing scale — composed in `@layer utilities`, never as tokens (S04 · A10)

`@mageride/tailwind-preset` is built for **product UI** and is a platform-wide contract Compose and
SwiftUI read from the same D2 §0.2 table. A marketing site needs four things it has no token for.
Every one is composed in `app/globals.css`'s `@layer utilities` from a value the preset already
publishes; **nothing here added a token**, and a session that concludes a real token is needed raises
a change set rather than editing `portals/tailwind-preset/src/tokens.ts`.

Two rules every number obeys. **Endpoints sit on D2's 4px grid** — in `rem` at the default root size
that is every multiple of `0.25rem`, so the grid survives the unit change exactly. **Every ramp runs
375px → 1024px**, which are D2 §AP/§FP's own outermost breakpoints, so no fourth width entered the
system to make a fluid range work (D8); above 1024px `clamp()` holds each value at its maximum, which
is what the `max-w-[1200px]` cap means in type.

| Utility / property | Value | Derived from |
|---|---|---|
| `--mr-www-text-hero` | `clamp(2.5rem, 1.344rem + 4.931vw, 4.5rem)` — 40 → 72px | Above D2's largest role. Slope 32px/649px = 4.931vw; constant 40 − 4.931 % × 375 = 21.51px |
| `--mr-www-leading-hero` | `clamp(2.75rem, 1.45rem + 5.547vw, 5rem)` — 44 → 80px | Same ramp. A second `clamp()` rather than a ratio, because D2 states leading as absolute grid values |
| `--mr-www-text-hero-sm` | `clamp(2rem, 1.422rem + 2.465vw, 3rem)` — 32 → 48px | **Its floor is `text-display` exactly**, so at 375px the marketing scale and D2 agree |
| `--mr-www-leading-hero-sm` | `clamp(2.5rem, 1.922rem + 2.465vw, 3.5rem)` — 40 → 56px | Floor is `display`'s own 40px leading |
| `--mr-www-section` | `clamp(3rem, 1.267rem + 7.396vw, 6rem)` — 48 → 96px | Floor is `xxl` (D2's largest step); 96 = 24 × 4 |
| `--mr-www-section-lg` | `clamp(4rem, 1.689rem + 9.861vw, 8rem)` — 64 → 128px | Floor is `xl` doubled; 128 = 32 × 4 |
| `.text-hero`, `.text-hero-sm` | size + leading + `font-weight: 700` | 700 is D2's `display` weight, unchanged. No family — `font-display` still chooses the face, exactly as `text-display` behaves |
| `.py-section`, `.py-section-lg`, `.gap-section` | the two section values | — |
| `.pt-/.pb-/.mt-/.mb-section` and `.pt-/.pb-section-lg` | the same two values on one edge | **Added in S16.** Seven call sites across S14–S16 were already using them and silently getting *nothing* — an unknown class is not an error in Tailwind, in `tsc`, or in any check here, so `pt-section` compiled to no rule and the symptom was a heading sitting against the cards above it. No new value: one edge instead of two |
| `.mr-aurora` | `color-mix(in oklab, …, transparent)` over `primary`, `primary-container`, `secondary-container` | Read as `--mr-color-*`, the raw properties the preset flips on `.dark` — so the backdrop follows the appearance with **no `dark:` variant** and no new hex |
| `.mr-reveal` rise distance | 16px | 4 × the grid |
| `.mr-aurora` blur | 64px | 16 × the grid |
| `.mr-sticky-media` top | `6rem` | 96px, the section rhythm's own smaller step |

**Two additions that are not derivations**, recorded as such rather than dressed up:

- **`letter-spacing: -0.02em` on the two hero utilities.** D2 §0.2 prints no tracking for any role. At
  32px and below its default is right; at 72px the same tracking reads loose. It is relative to the
  font size, so it adds no absolute value to the system, and it is scoped to the two utilities that
  exceed D2's largest role.
- **`--mr-www-ease-out` / `--mr-www-ease-in-out` / the reveal duration and stagger.** D2 prints no
  duration and no curve for any platform, so these are composed from nothing — they are this
  surface's, spelled once so that eight primitives cannot each invent their own. They are not
  colours, sizes or spacing, which is the family the token fence is about. A shared platform motion
  vocabulary would be a change set against D2, not a quiet addition here.

Also unchanged, and load-bearing: **there is no fourth breakpoint** (D8). `sm:` is 375px, `md:` 768px,
`lg:` 1024px, and content caps at `max-w-[1200px]`.

### The two leading ramps are re-stated for Sinhala and Tamil (S12)

D2 §0.2's roles were measured for Latin, and on this surface that assumption breaks at hero size.
**Outfit's ink at 40px is 37px in a 44px line box. Noto Sans Sinhala's is 51px** — 7px taller than
its own line box, because the script stacks vowel signs above the headline and below the baseline
inside the same em. Two stacked Sinhala hero lines overlapped by a measured 6px in Chromium at
375px. Clearance across the ramp was `.text-hero` −7px @375 / −9px @1024+ and `.text-hero-sm`
−1px / −4px; Tamil was −5px / −5px / +1px / −1px; English was fine everywhere.

| Property | Latin | `html[lang='si']`, `html[lang='ta']` |
|---|---|---|
| `--mr-www-leading-hero` | 44 → 80px | **56 → 96px** — `clamp(3.5rem, 2.055rem + 6.163vw, 6rem)` |
| `--mr-www-leading-hero-sm` | 40 → 56px | **44 → 64px** — `clamp(2.75rem, 2.028rem + 3.082vw, 4rem)` |

**It is not a token and not a new kind of value** — it re-states two *existing* ramps for two
`<html lang>`s, in `@layer utilities`, on the same 4px grid and the same 375→1024px range. Sizes,
weight and tracking are untouched and `portals/tailwind-preset/src/tokens.ts` was not opened. Both
scripts share one rule because Sinhala is the binding constraint and Tamil clears it with room, and
because `not-found.tsx` / `error.tsx` render all three scripts in one document — a Sinhala-only fix
would set two blocks differently on the same page.

**The one thing it costs:** `text-hero-sm`'s Latin floor is `text-display` *exactly* (32/40), which
S04 chose so the marketing scale and D2 agree at 375px. On a Sinhala page that floor is the thing
that collides, so there they no longer agree — 32/44.

**And a finding this surface deliberately did not act on.** The same measurement over D2's own roles
says Sinhala collides at `display` (32/40) and `caption` (12/16) by 1px and is *touching* at
`headline` and `title`. Those tokens are a platform-wide contract Compose and SwiftUI read from the
same table, so **the four product surfaces have the same problem on the same strings.** That is a
change set against D2 §0.2 — overriding it here would only make www's Sinhala metrics differ from
the apps. Only the two utilities that are this site's own were touched.

## Sinhala and Tamil display faces (S04 · A13)

D2 §0.2 names Outfit (display) and Inter (body), and `portals/web-passenger/app/layout.tsx` says in
its own words that neither carries a Sinhala or a Tamil glyph. On a token-gated utility page opened
from an SMS, falling through to the system face is right. **On a hero setting 48–72px Sinhala it is a
visibly unfinished page beside the English cut**, and this surface is the one whose whole job is a
first impression.

Both faces **resolved from `next/font/google` on this build host** — self-hosted, no CDN `<link>`, no
`next/font/local` fallback needed.

- **The order in the font stack is the entire mechanism.** `next/font` emits a `unicode-range` per
  subset, so a Latin codepoint is drawn by Outfit or Inter — a brand name, a URL, a number keeps the
  design system's face inside a Sinhala page — and a Sinhala codepoint falls through to the next
  family. Nothing selects a face per *string*; the browser selects per *character*, which is the only
  thing that works on a line reading "MageRide යනු …".
- **The Latin faces bind to `--mr-font-outfit-latin` / `--mr-font-inter-latin`**, and `globals.css`
  composes the preset's `--mr-font-outfit` / `--mr-font-inter` out of a Latin face plus the script
  face for the page's language. Binding `next/font` straight onto the preset's names would make the
  composition a cycle — `--mr-font-outfit: var(--mr-font-outfit), …` resolves to nothing. The preset's
  tokens are unchanged; what changed is who supplies them.
- **`html[lang='si']` is (0,1,1) and beats `next/font`'s own (0,1,0) class**, so stylesheet order
  cannot decide the outcome. Each `var()` carries the plain family name as a fallback, so a missing
  class degrades to an installed face rather than invalidating the declaration.
- ~~**The variable class is applied per locale**, so a Tamil page never names Noto Sans Sinhala in a
  stack and never fetches it.~~ **Superseded in S14 — all four variable classes are now applied on
  every page.** S04's reasoning was right about page *copy* and wrong about this site, because two
  components deliberately render **another script on every page**: the locale switcher's endonyms
  (සිංහල · English — a reader looking for their language scans for their own script) and the
  footer's language band, whose entire purpose is one sentence in all three at once. Measured before
  the fix: on `/en` both the Sinhala and Tamil band lines resolved to `Inter, ui-sans-serif` — *no
  script face* — and on `/si` the Tamil line resolved to the Sinhala stack. Two of three scripts in
  a system fallback, in the component that exists to prove the opposite.
  **Declaring the variable costs nothing**: `next/font` emits a `unicode-range` per subset, so a face
  downloads only when a rendered glyph needs it. `/en` now serves 5 font files instead of 2 because
  it genuinely displays Sinhala and Tamil, which is the correct reason. Both Noto faces are still
  `preload: false`, so they arrive after first paint and below the fold.
- **Cross-script text needs `font-family`, not a variable override** — `app/globals.css`'s
  `[lang='si-LK']` / `[lang='ta-LK']` rules. Two earlier attempts shipped and changed nothing, and
  both are recorded beside the rule because both look correct: overriding `--mr-font-inter` on the
  element fails because a custom property's `var()`s are substituted where the property is
  **declared** (`:root`), not where it is used; overriding `--font-body` fails because
  **`font-family` is inherited** — `body` holds the only declaration in the subtree, so a descendant
  never re-reads the variable. The rule must declare `font-family` itself. The `html[lang]` rules
  above sidestep all of it because `html` *is* `:root`.
- **`preload: false` on the two Noto faces**, which is the one uncomfortable trade. `next/font`
  preloads per *module graph*, not per rendered page, and all four faces are imported by one shared
  layout — so preloading would put a Sinhala download in front of every Tamil and English page, on a
  budget (A34) written against a 3G-throttled mid-range Android. Without it a Sinhala page's first
  paint uses the system Sinhala face for one `display: 'swap'` cycle, which is exactly what that
  reader sees on `web-passenger` today. **S19 owns revisiting this** if per-locale preloading ever
  becomes expressible.

## No API at request time — what that forbids, concretely

No `fetch`. No `axios` or any other HTTP client. No `EventSource`, no `WebSocket`, no
`sendBeacon`. No map — no MapLibre, no `pmtiles`, no tile URL, because a basemap is a request to a
third party and a moving vehicle on a marketing page is a request to the platform. No live vehicle
count, no fare quote, no coverage lookup. No `NEXT_PUBLIC_*`.

Every page below `app/[locale]/` is pre-rendered at build from typed content modules. `/` is the
one dynamic response on the whole site — it reads `Accept-Language` to choose a locale and issues a
**307**, temporary on purpose so that an intermediary cannot cache one reader's language and replay
it at the next.

**No cookies and no analytics** (A36). There is no cookie banner because there are no cookies. A
`localStorage` theme preference is the single permitted piece of client state (S19, A35) and it is
not a cookie — nothing is sent to a server, so there is nothing to consent to. If measurement is
ever wanted, the only options compatible with this fence are server-side log analysis or a
self-hosted cookieless counter, and either is **its own change set**, never a quiet `<script>`.

## Locale routing — a path segment, and the divergence from `web-passenger` is deliberate

`app/[locale]/layout.tsx` **is the root layout**: it emits `<html lang>`, and the `lang` is the path
segment rather than a header read. `app/layout.tsx` above it is a pass-through that returns its
children and exists only because Next requires a root layout above `app/page.tsx`.

`web-passenger` does the opposite — `?lang=` on the URL — and explains why: an SMS carries one URL,
minted by notification-svc before anybody knew what language the recipient reads, so the language
cannot be in the path. **This surface is the opposite case.** It is indexed, it needs reciprocal
`hreflang` (A32), and a search engine has to treat the Sinhala and English readings of `/drivers`
as two canonical documents. A query parameter can be neither of those things.

Three consequences that are easy to undo by accident:

- **An unknown `[locale]` is a 404, never a fallback to Sinhala.** `dynamicParams = false` on the
  locale layout refuses it at the router; `localeFrom()` in `src/lib/params.ts` is the same
  statement for anything the router does not gate. Answering `/de/drivers` with content would give
  a crawler a second URL for a document that already has a canonical one.
- **No page below `[locale]` may read a header, a cookie or a search param.** That is what keeps
  every URL statically renderable, and static rendering is how the site survives the platform being
  down. (39 was the count when 13 routes × 3 locales was the whole site. It is now **106 published** —
  13 routes plus **40** guide chapters, × the 2 rendered locales — after S23's six fleet chapters
  added twelve URLs to the sitemap and not one line to `app/sitemap.ts`. The prerendered total is
  larger again: `/_not-found`, `/_global-error`, `/icon.svg` and an OG card per page.)

  **S18 is the one page that wanted to break this rule, and the shape of the exception is worth
  copying.** `/screens` filters by `?surface=…&mode=…&chapter=…`, and reading that on the server
  would have made the route dynamic. Instead `page.tsx` prerenders `GalleryBody` — the complete,
  unfiltered gallery — as a `<Suspense>` **fallback**, and a client component re-renders the same
  component with the URL's selection. The rule holds, the URL is still the entire state, and the
  served HTML is the whole gallery.

  The trap, which was hit and fixed inside S18: `fallback={null}` looks harmless because "the
  boundary never shows on a real request", and it means **Next prerenders nothing**. The served
  `/en/screens` was 27 kB with no screens in it, and it looked perfect in a browser. If a page puts
  `useSearchParams()` under a boundary, the fallback must be the real content.
- **`not-found.tsx` and `error.tsx` render all three languages at once**, each in its own `lang`
  block. Next hands neither of them params, so there is no locale to render in — and picking one
  would mean answering a Tamil reader in Sinhala on the page that has already failed them. A33 asks
  for `lang` switching on mixed-script content and this is the case that needs it most.

`HREFLANG` in `src/i18n/index.ts` maps the segment to its BCP-47 tag (`si-LK`, `ta-LK`, `en-LK`).
The path stays the bare two-letter code; the annotation carries the region.

## `src/lib/routes.ts` — the route table is the source of truth

One module enumerates every route. `app/sitemap.ts`, the nav, the footer, the `hreflang` block and
`test/routes.test.ts` all read it, which inverts the usual failure: **a page that exists and is not
in the table is a test failure**, rather than a page nobody links to and no sitemap mentions. On a
surface whose entire purpose is to be found, an unreachable page looks like nothing at all.

`test/routes.test.ts` walks `app/[locale]/` and holds the tree and the table to each other in both
directions, and requires every dynamic segment to draw its `generateStaticParams` from this module.

**No page is exempt any more — S20 deleted the workbench and the exemption together.**

`app/[locale]/%5Fmotion-demo/` was S04's workbench: every motion primitive rendered once so the layer
could be checked by eye in both appearances and with reduced motion forced, and so a session could
see that a token change had not reflowed anything. Both of those jobs now have machines doing them —
`test/a11y.test.tsx` for the behaviour and `npm run visual` for the reflow — so the page went, along
with its 23 message keys in all three tables and the `Disallow` in `app/robots.ts` that named it.

**The way it went is the part worth keeping.** The exemption was a set of one, by exact name, never a
`startsWith('%5F')` pattern, and a second test asserted that the exempted page still existed. So
deleting the directory turned that test red until the exemption went with it — an exemption could not
outlive what it exempted, which is exactly what happened. The empty set is still there for the next
session that needs an unpublished page: put the name in it, with a reason, and the same check applies.

(The directory was `%5Fmotion-demo` and not `_motion-demo` because a folder whose name begins with an
underscore is a Next **private folder**, opted out of routing entirely. `%5F` is the encoded
underscore Next documents for that case. Recorded because the next person to want an unpublished
page will hit it.)
~~`GUIDE_CHAPTERS` is empty until S17~~ — **filled in S17**, derived from `CHAPTERS` rather than
hand-listed, so the route table cannot disagree with the corpus in either direction. **All thirteen
routes publish real pages as of S18**; the table is complete and `app/sitemap.ts` (S19) reads it.

## Two interaction decisions later sessions should not re-litigate (S18)

**The screen gallery filters in the browser, and the page is still prerendered.** The whole of it is
in the served HTML; a client component re-renders the same component with the URL's selection. The
reasoning and the bug that produced it are in `src/components/screens/GalleryBody.tsx` and in the
locale-routing section above. Two consequences to carry forward: **every chip is a crawlable URL
that renders the same document as `/screens`**, so S19 owes it a canonical and must keep the
query-string views out of the sitemap; and **the filter is an enhancement** — with JavaScript off a
reader gets the complete gallery and chips that navigate without narrowing, which is a degradation
rather than a broken control.

**`/faq` is native `<details>`, not a Radix accordion, and that was forced rather than preferred.**
S18 asks for the answers to be *"in the DOM whether open or closed"* because *"a crawler and a
JS-off reader need them"*, and for `FAQPage` JSON-LD (S19) to describe content that is genuinely on
the page. Radix's `Accordion` unmounts closed content; `forceMount` fixes the crawler half and
nothing fixes the other — without JavaScript it is a column of buttons that do nothing above
answers CSS has hidden. `<details>` gives all of it natively, plus a real disclosure button with its
own expanded state, for no dependency and no hydration. **Do not hand-write `aria-expanded` on a
`<summary>`** — a server-rendered `false` that never changes is worse than the native state it
shadows. The one thing the platform does not give is the deep link, and that is
`src/components/faq/FaqHashOpener.tsx`: twenty lines over a page that is complete without them.

There is a matching print rule in `app/globals.css` — **every `<details>` prints open**. Measured:
without it, printing `/faq` gave twenty-one questions and zero answers.

## Content is typed TypeScript under `src/content/`, not MDX

Three reasons, in the order they matter (A6):

1. **A per-locale MDX chapter lets the *structure* diverge between Sinhala and English.** A typed
   `Chapter` — `title: MessageKey`, `steps: Step[]`, `screens: ScreenRef[]` — keeps the shape shared
   and localises only the strings, so a translator cannot drop a step, reorder two, or quietly turn
   a five-step procedure into a paragraph. On a *how-to guide for a transport platform* that is a
   safety property, not a tidiness one.
2. **MDX needs `@next/mdx` plus a remark/rehype chain**, and every plugin is a new dependency the
   AL-52 sweep has to be reasoned about.
3. The site then renders identically with the backend down, which is the second fence.

**Every public claim carries a spec anchor in the content module that makes it** (README rule 7). A
fee, a tier, a vehicle count or a "first trip free" on a public site is a factual assertion about a
real service, and the anchor is how the next session checks it is still true. This binds hardest on
the mission (MCS-34 D1), which is framed as national infrastructure — *"every bus, every train"* —
and is **not true on launch day**. S07 wrote that qualifier: `www.mission.qualifier`, and it is
**required furniture wherever the mission renders**. A layout session may move it; it may not drop
it for balance.

**Chapter slugs are never localised.** `/si/guide/passenger/install-and-first-run` — Sinhala
content, English slug. Localised slugs would triple the route table, break `hreflang` reciprocity
(each locale needing a different path for the same document), and make an external link
locale-specific for no reader benefit. `src/content/chapters.ts` holds the 40 slugs as a published
contract; `src/content/index.ts` is the inventory of which are actually written, and a chapter on
disk that is not registered there is a **test failure**, not an unlinked page.

**Publishing a guide is one array member, and S23 is the proof.** `Chapter['audience']` admitted
`'fleet'` from S07 onward while `GUIDE_AUDIENCES` in `src/lib/routes.ts` did not, so a fleet chapter
in the registry published nothing rather than 404ing at a URL nothing linked to. Adding `'fleet'` to
that array produced six routes, twelve sitemap entries, twelve reciprocal `hreflang` sets, six OG
cards and a deep link from `/fleets` — with no slug typed into `routes.ts`, `sitemap.ts` or `seo.ts`.
`test/routes.test.ts` now also asserts that every audience in that array has a route segment on disk,
because *"registered but deliberately unpublished"* and *"somebody forgot the array"* are otherwise
the same observation.

**S17's chapter component took the third audience unchanged**, which is what S23 was told to check:
`ChapterPage`, `ChapterBody`, `chapterLabels`, `Callout`, `chapterSeo`, `chapterCrumbs`, `howTo` and
`breadcrumbs` each take a `Chapter` and read `audience` off it rather than branching on which of two
guides they are in. The only file that could not be shared is the route segment itself, because the
audience is a literal path segment and `chapterBySlug` has to be told which guide
`install-and-first-run` belongs to.

**The numbers live in `src/content/`, never in a message string.** Every fee, tier and count is an
exported constant with its spec anchor beside it, because a number inside a translated string is a
number nobody can check and three places to get it wrong. `DAILY_FEE_TIERS` is the model: minor
units (Universal Rules), the canonical `vehicle_type` as the key, and the URD table it came from
named in the doc comment. **Two corrections S07 made rather than inherit:** the stats band says
**10** vehicle types, not 11 — the eleventh `VEHICLE_COLORS` token is `veh-private`, a Mode B
*display* colour and not a vehicle type — and Mode B's monthly charge is rendered as "around
Rs 300" because the URD says "approximately" in both places it states it.

**Untranslated strings are visible, not silent.** `si.ts`/`ta.ts` carry every English key; the ones
S12/S13 have not reached yet hold the English text behind a `TODO(si)` / `TODO(ta)` marker.
`check-i18n-parity.mjs` **counts and reports them on every build and does not fail** — MCS-34 D2
deferred Tamil deliberately, so failing here would mean the component could not go green until a
decision the user made had been reversed.

## `public/screens/` is generated, and committed on purpose

The showcase imagery is derived from `specs/wireframes/*.html`, not hand-made and not screenshotted
from a running app (MCS-34 D10 defers real screenshots until after launch — iOS needs a Mac and
Android needs seeded state, and neither gates a marketing page).

The contract:

- `scripts/capture-screens.mjs` (S05, `playwright-core`) locates a frame by the **text** of its
  `.cap .scr` caption — the wireframes carry no `id` attribute on a screen, which is README §4.1's
  correction to the plan — then walks **forward through the caption's siblings** to the first
  `.phone` / `.browser` / `.mweb`, stopping at the next `.cap`. Not "the `.cell` containing the
  caption", which README §4.1 says: `web_admin.html` and `web_fleet.html` have **no `.cell` at
  all** and lay the caption and the frame out as flat siblings of one `.wrap`. The sibling walk is
  the one locator that fits all seven files — verified at 202 captions → 202 frames.
- Measured geometry: `.phone` **320 × 680** (9px bezel), `.mweb` **330 × 616**, `.browser`
  **944 wide with a per-screen height between 440 and 1037**. The plan's 375 × 812 and 1440 × 900
  are both wrong, and portal height varying per *screen* is why S06 cannot composite the portal
  frames into one fixed mockup.
- **Dark captures do not work, and the registry ships light-only.** §4.2 is right that the files
  have no dark mode and that the intended route was a `:root` override built from the preset's dark
  tokens. That route was built (`scripts/wireframe-appearances.mjs`, complete and kept) and it does
  not produce a publishable image: **231 rules across the seven stylesheets hard-code a light
  surface hex** — `.card{background:#fff}`, `.sheet`, `.field`, `.map`'s green tiles, the status
  pills — and all 202 frames inherit them, with 44 frames adding their own inline light hexes on
  top. Overriding `:root` repaints the text and not the surfaces; rendered and inspected, it is
  grey-on-white body copy that fails WCAG contrast. Painting the component rules too would mean
  assigning dark values to ~250 hard-coded colours whose meaning is ambiguous (most of the 52
  inline `color:#fff` are text on a coloured chip and must stay white) — that is inventing a dark
  appearance D2 has never specified and publishing it as what the app looks like. **The fix is to
  tokenise `specs/wireframes/*.html`; then dark is one field in the registry and no script change.**
- **`scripts/polish.css` is in two parts and the split is load-bearing.** PART 1 pins Inter and
  Outfit from `@fontsource-variable/*` — the wireframes ask for `'Inter'` by name and this host has
  neither face, so captures otherwise render in DejaVu, which is both wrong and *non-reproducible
  across machines*. PART 2 is shadows, radii and hiding the contact-sheet chrome. The capture script
  injects PART 1, takes a baseline, injects PART 2, and **fails the run if any control moves more
  than 1px**, measured relative to the frame's own origin. That is the executable form of "polish
  must not move, add or remove a control": the approved wireframes are the structural baseline, and
  a picture showing a control the app does not have is a false public claim. Fixing the font is
  outside the fence because it honours the wireframe's own declaration rather than deviating from
  it; it does rewrap some labels, by up to ~90px where a chip row reflows.
- `scripts/compose-frames.mjs` (S06) composites each capture into a device mockup and emits
  AVIF/WebP.
- **`npm run screens:refresh` runs both, and its output is committed.** That is deliberate: CI must
  never download a browser, and a marketing image that regenerated per build would make every
  content diff unreadable. S06 wires the ≤ 12 MB budget gate; changing a wireframe means re-running
  the refresh and committing the result in the same change.

- `scripts/compose-frames.mjs` (S06, `sharp`) does **not** draw a bezel or a status bar: the
  wireframes already draw both, and the capture is an element screenshot of exactly that box, so a
  second bezel would put a phone inside a phone. What it adds is the three things a capture cannot
  contain — **rounded corners as transparency** (an element screenshot is a rectangle, and the area
  outside the radius is filled with page background, sampled `#FCFCFC`), a **D2 `elevation-5` drop
  shadow** (a box-shadow paints outside the border box, which is exactly what the screenshot clips
  away), and the **plate**. Every colour is *imported* from `portals/tailwind-preset/src/tokens.ts`,
  not transcribed: Node strips types, so the compositor reads the real token module and a D2 change
  reaches the images by re-running the refresh.
- **AVIF + WebP at 1× and 2×, and no PNG.** Measured: a 2× portal plate is 31 kB AVIF / 57 kB WebP /
  **266 kB PNG**, so the PNG alone breaches the 220 kB per-image fence, and the format would put
  ~14 MB into a 12 MB budget. WebP is the universal floor — every engine has shipped it since Safari
  14 — so the fallback buys nothing. `public/screens/README.md` carries the table.
- **The budget is enforced inside `npm run build`**, by `scripts/check-bundle.mjs`: ≤ 12 MB total,
  ≤ 220 kB per image, and every registry entry resolves to a 1× AVIF *and* WebP. Unlike the JS/CSS
  totals — which stay reported-only until S19 because the pages are still empty — these are live
  from today, because `public/screens/` is complete now and grows every time somebody adds a screen.
  **Do not raise the numbers to make a build pass**; the fix is fewer screens or tighter quality.

### The image contract (S06 · A16) — **now a component, `src/components/ScreenImage.tsx`**

**S14 implemented it. Use that component; do not hand-roll a screen image.** The clauses below are
what it does, plus the one departure it makes.

- **Explicit `width` and `height`, from `plateSize()` — never a constant.** S06 warned "read the real
  numbers rather than assuming a constant" and S14 measured how right that was: the committed output
  holds **eight distinct plate sizes**, and the phone frames alone split **34 at 416×777 and 26 at
  416×776**. A constant would have given the wrong aspect ratio to twenty-six screens — a squashed
  screenshot or a layout shift, and on A34's 3G-throttled Android the shift is the expensive one.
  The map is generated by `scripts/screen-dimensions.mjs` from the committed 1× WebPs into
  `src/content/screen-dimensions.ts` and re-generated by `npm run screens:refresh`. It emits a
  **`.ts` module and not `.json`** because three toolchains import the registry — Next's bundler,
  `tsc`, and raw Node ESM (`check-bundle.mjs`, `check-i18n-parity.mjs`) — and Node needs
  `with { type: 'json' }` where the bundler does not, so JSON works in two of the three and fails the
  build in the third. That is also why `tsconfig.json` sets `allowImportingTsExtensions`: Node's
  type-stripping will not infer `.ts` on a **value** import, and every other sibling import in
  `src/content/` is type-only and therefore erased before Node sees it.
- **`<picture>` over the prebuilt files, not `next/image`** — the one departure from S06's wording,
  made deliberately. S06 already emitted AVIF **and** WebP at 1× and 2× to a measured budget that
  `check-bundle.mjs` gates on every build; sending an already-minimal AVIF through `/_next/image` to
  be re-encoded as AVIF spends CPU to produce a slightly worse file, and the file the budget gate
  measured is then not the file that ships. It also removes a **request-time** code path from the
  surface whose defining property is that it renders with the platform down. `<picture>` gives
  everything `next/image` would have here: `type`-negotiated AVIF with a WebP floor, density
  switching, reserved space, native lazy loading and `fetchPriority`. `images.formats` in
  `next.config.ts` is left alone — it governs anything that *does* go through the optimiser later.
- **`alt` comes from the registry's `captionKey`, through the translator.** `alt` is on the
  `mageride/no-literal-user-facing-strings` attribute list, so a literal there fails lint, which is
  the correct outcome — an `alt` is read aloud to somebody and belongs in all three languages.
- **Hero images get `priority`; everything else is lazy.** `HERO_SCREENS` is exported from the
  registry, so "which are the heroes" is data, not a judgement each page makes again.
- **Light/dark art direction, when there is any, is CSS `prefers-color-scheme` inside `<picture>` —
  never JavaScript swapping a `src`.** A swap after hydration is a visible flash, and this surface
  hydrates as little as it can.
- **Today there is nothing to art-direct: every image is a light capture on a light plate.** The
  screens are light because the wireframes cannot be rendered dark honestly (see above), and the
  plate follows them. **So a dark page must place these on their own light surface** — a card, the
  way a printed screenshot sits on paper — rather than letting a bright rectangle sit directly on a
  dark section. That is a real constraint on the gallery design in S18 and the showcase in S15, and
  it disappears by itself if the wireframes are ever tokenised.

S05 wired `npm run screens:capture`; S06 adds `screens:compose` and `screens:refresh` — **the only
sanctioned way `public/screens/` changes**, and a hand-edited image there is overwritten without
warning. The capture and compositing tooling is **devDependencies only** — and it is `playwright-core`, deliberately, **not**
`playwright`: `playwright`'s postinstall downloads a browser, CI runs `npm --prefix portals ci`
with no skip flag, and A17 exists to keep a browser download off the critical path of every portal
build. `playwright-core` ships no browser, so the operator installs one once with
`npx playwright-core install chromium`; the script says so in its launch-failure message.
`@fontsource-variable/inter` and `@fontsource-variable/outfit` are devDependencies for the same
reason — capture-time assets, never imported by the site, which self-hosts the same two families
through `next/font` (S04). `sharp` is a devDependency on the same footing: it composites at refresh
time and never reaches a page. `test/fences.test.ts` asserts all of this.

`public/robots.txt` **is gone — S19 chose `app/robots.ts`.** The two cannot both exist (Next fails
the build on a public file that collides with a metadata route), and the generated route is the one
that can read `src/lib/routes.ts` for the sitemap URL instead of restating it. Its content is still
the **inverse** of the one the other three portals serve: `Allow: /`, plus the sitemap. A static
`public/robots.txt` reappearing would silently win over the route, which is how a stale `Disallow: /`
outlives the decision that added it, so `test/seo.test.ts` asserts the file stays absent.

## Accessibility — WCAG 2.2 AA, and the four things that actually failed (S19)

URD Epic 19 covers the apps, not the web. AA here is a deliberate raise (A33) and the right one for a
public, government-adjacent platform. S19 audited `/`, `/drivers`, a guide chapter, `/screens`,
`/faq` and `/legal/privacy` — **× both appearances × both rendered locales × desktop and a 375px
phone**, 48 page loads — with axe-core plus a contrast walk over every rendered text node. The audit
is green. What it found is worth not rediscovering, because none of it was visible in a browser.

- **A `role` on a `<ul>` is never additive.** `role="group"` on the `/screens` filter chips replaced
  the implicit `list` and orphaned all 41 `<li>` children — `listitem`, serious, on every rendering.
  A list is already a grouping construct and takes a name from `aria-labelledby` exactly as `group`
  does, so the attribute was pure loss. **S19 then made the identical mistake a second time**, adding
  `role="region"` to the role pages' screen strip to satisfy `scrollable-region-focusable`, which
  orphaned 31 more. The rule that fixes it only needs the tab stop; `HeroCarousel` can afford the
  full APG pattern because its track is a `<div>`.
- **A scroll container with no focusable descendant is unreachable by keyboard.** The role pages'
  screen strip was 7456px of content in a 1280px viewport, images and captions only, no tab stop —
  WCAG 2.1.1, not a nicety. `ScreenCarousel` shares the same `mr-carousel-track` utility and was
  fine, because its thumbnails are buttons and tabbing through them scrolls the container as a side
  effect. That difference is what made it easy to miss.
- **`size-cta-icon` is an icon box, not a hit area.** D2's 20px is how big a mark should *look*
  beside 16px text. Used as a button's own size it made the theme toggle a 20×20 target on every
  page and the mobile menu trigger a 20×20 target at the width where it is the only way to reach
  any other page. Both are `size-11` now, with the glyph still at D2's 20px, so nothing looks
  different. Same fix on the carousel: the dot is a `<span>` the button centres, not the button.
- **Opacity is the one way to fail contrast without ever writing a failing colour.** `.mr-sticky-step`
  dimmed inactive steps to 0.45, which composited an 18px heading to 2.85:1 and its body to 2.27:1 —
  the tokens were fine and the composite was not. The measured floor across the three inks inside a
  step is **0.83**, and opacity is the *only* thing distinguishing an active step from an inactive
  one, so "passes AA" and "reads as dimmed" cannot both be true of one number. It sits at 0.8 and the
  one ink that could not survive it — the accent step index — leaves the opacity's reach by swapping
  colour instead. See the rule in `globals.css`; it is more differentiation, not less.

**`text-on-primary` on `bg-primary` is 2.82:1 and is the one D2 pairing with no safe single token.**
`.mr-on-primary` in `globals.css` is the composed replacement, and `focus-visible` rings are
`secondary` rather than `primary` for the same measured reason — a focus indicator is a non-text UI
component and SC 1.4.11 asks 3:1 against what is adjacent, which with `outline-offset-2` is the page
background.

`scripts/check-a11y.mjs` is that audit, in the repo — `npm run a11y --workspace @mageride/www`
against a **production** build on port 3104 (`MR_A11Y_BASE` overrides the origin). It exits non-zero
on an axe or contrast failure and prints the 24px sweep without gating on it. S20 owns wiring it into
CI beside Lighthouse.

### The audit tooling has two traps that produce a confident, wrong "pass"

Both cost a full run, and both look like success:

- **A stale `next start` from an earlier session keeps port 3104** and serves the *previous* build's
  HTML, whose hashed stylesheet no longer exists — so the page renders completely unstyled, every
  element measures ~17px tall, and the contrast walk reports a clean sweep because everything is
  default black on default white. Kill the port before every run; the audit script now asserts
  `--spacing-cta` is set and throws if it is not.
- **Tailwind v4 emits `oklab()` for any colour with an alpha modifier**, so the sticky header computes
  to `oklab(0.999994 … / 0.85)` — white at 85%. A regex that pulls the numbers out of a colour string
  reads that as near-black and invents three failures in the header while burying the real ones.
  Resolve colours by painting them into a 1×1 canvas and reading the pixel; it has no opinion about
  colour spaces. The same walk must fold ancestor `opacity` into the text alpha, or it cannot see the
  `.mr-sticky-step` class of bug at all.

**axe is the authority on SC 2.5.8, not a raw 24px sweep.** 92 links measure under 24px tall and axe
flags none of them: it implements the inline and spacing exceptions, and a nav link with clear space
around it passes on the undisturbed-circle rule. `sr-only` controls are not pointer targets either —
the skip link measures 24×16 hidden and 116×24 the moment it is focused, which is the only state it
has.

## Rules for a page or a component

- **Every string goes through the translator, in all three files, in the same change.** `en.ts`
  defines the key set and the other two are typed against it, so a missing translation is a compile
  error; the lint rule stops a literal reaching JSX. It also stops a literal handed in as a *prop* —
  `title="Privacy policy"` on a component that renders it is invisible to the ESLint rule, and
  `test/fences.test.ts` sweeps the tree for it.
- **A route's nav label and its heading are one key.** `labelKey` in the route table is both, so
  the two cannot drift into saying different things about the same page.
- **A client component takes resolved strings, not a `locale`** (MCS-36 **D3**, 2026-08-30).
  It used to be the opposite — *"React cannot serialise a translator across the boundary, and
  thirty strings is not a prop list"* — and the first half of that is still true. The rule changed
  because somebody finally measured the second half.

  `createWwwTranslator` needs the message tables, so a client component that builds one puts
  **~88 kB gzipped** into its bundle. Fourteen modules did, one of them the shared header, so both
  published locales' entire corpus — three-quarters of it guide prose — downloaded on every page,
  including pages with no guide on them. **Converting them took first-party JS on `/` from
  113.7 kB to 17.0 kB** and A34's budget from failing by 23.7 kB to passing with 73 kB spare.

  It is **not** thirty props. Each component takes **one** `labels` object, typed and exported, so
  a missing string is a compile error — the same guarantee `en.ts` gives the table itself, at the
  same place a reader would otherwise have found a blank `aria-label`. The server builds it: see
  `headerLabels`, `chapterLabels`, `galleryLabels`, `heroCarouselLabels`, `howItWorksLabels`,
  `showcaseLabels`.

  Three things a client component may still import, none of which touches a table:
  **`@/i18n/locales`** (the published set, the BCP-47 tags, the negotiator),
  **`@/i18n/substitute`** (placeholder filling — nine lines, for the one string whose count comes
  from the URL), and **`@/i18n/error-strings`** (four generated strings for the error boundary,
  which is the one client module with no server parent to receive props from).

  `test/fences.test.ts` walks the **transitive** client graph and refuses an import of `@/i18n`.
  Transitive, because the three modules that first put the tables in the bundle — `ScreenImage`,
  `Callout`, `GalleryBody` — carry no `'use client'` of their own and were pulled in by components
  that do.
- **`sm:` is 375px, `md:` 768px, `lg:` 1024px, and the page caps at `max-w-[1200px]`.** MCS-34 D8
  declined a fourth breakpoint: a breakpoint is a token-level change reaching every surface, and a
  wide marketing page is not a reason to move one. Mobile-first — the base styles *are* the phone,
  because A34's budget is written against a 3G-throttled mid-range Android.
- **Compose marketing-scale values in `@layer utilities` from existing tokens; never add a token.**
  `@mageride/tailwind-preset` is a platform-wide contract Compose and SwiftUI also read. If a
  session genuinely needs a new token, that is a second change set, not a quiet edit. What S04
  composed, and the derivation of each, is the marketing-scale section above — **reuse those before
  adding a ninth.**
- **Anything that moves comes from `src/components/motion/`.** No component starts its own timer,
  observer or frame callback; `src/lib/motion.ts` owns all three so that N elements cost one. A new
  effect ships its `prefers-reduced-motion` variant in the same rule block as the effect itself.
- ~~**`src/components/scaffold/` is temporary.**~~ **Gone in S18, with the last five pages that used
  it** (`/screens`, `/faq`, `/download`, `/contact`, the three `legal/*`). `www.scaffold.notice` went
  from all three tables in the same change, and `test/fences.test.ts` asserts the directory stays
  gone — a later session reaching for the obvious name would quietly reopen Phase 5's gate.
- **The source sweeps in `test/fences.test.ts` and `scripts/check-*.mjs` are text searches, and they
  cannot tell a call from a sentence about one.** That is deliberate — a key or a variable assembled
  from fragments would defeat any analysis — and the consequence is that **a comment explaining a
  fence will trip it**. S18 did this three times in one session (`fetch(`/`process.env.` in a doc
  comment, a clock read named in another, and the proposal's bracketed to-be-added placeholder
  quoted in a third). Describe the rule; do not spell it. Weakening a check to ignore comments would
  trade a guarantee for a comment style.

## Deferred, and by whose decision

| What | State | Decision |
|---|---|---|
| Tamil | **executed in S13** — `ta.ts` complete and type-checked, `/ta` unpublished and 404 | MCS-34 **D2** — si + en at launch, Tamil next release. See the section below: one constant reverses it |
| App-store URLs | **no link and no badge** (S18). `/download` says the apps are not listed, splits the two apps, and states URD NFR-22's Android 8.0 minimum. **No iOS minimum — no spec states one** | **D3** — not live yet. **No form, and no `mailto:` either**: S18's constrained variant asks for a notify link, and D4 leaves no address to point it at |
| Contact details | **no address anywhere on the site** (S18). `/contact` points at in-app support and the questions page, and says plainly that no address is published yet | **D4** — no phone; the address itself is not chosen. The proposal's bracketed placeholder must never reach a public page, and a `mailto:` to a placeholder is worse than nothing (S17 reached the same conclusion for chapter feedback) |
| Terms / Privacy / PDPA | **shell built** (S18) — `src/content/legal.ts` + `LegalPage`, with a status notice, a table of contents and a `lastUpdated` that is `null`. Privacy and PDPA also carry **factual descriptions of software** (what this website collects: nothing; what `pdpa-svc` does: export and erasure within 30 days), each citing its spec. `terms` carries none, because terms *are* the policy | **D5** — counsel supplies the text. **No session in C134 authors legal text.** Setting `lastUpdated` is what turns a document on; launch is gated on it arriving |
| Fleet-owner guide | **done (S23)** — 6 chapters at `/guide/fleet/*`, si + en, `/fleets` deep-links into it | **D7** — yes, second delivery phase |
| Cache headers | not set | A44/S21 — they belong at the ingress beside the apex 301, not in `next.config.ts` |

## Tamil is deferred, and `WWW_LOCALES` is the whole of it (S13 · MCS-34 D2)

**This surface publishes two locales, `si` and `en`. The platform still has three.**
D2 asked whether Tamil ships at launch and the answer on record — in the change set's decision table
and in S01's handoff — is *si + en complete first, Tamil next release*, because no native Tamil
reviewer is identified anywhere in this repo and ~21k words of machine-translated Tamil under a
Tamil label is worse for a Tamil reader than no Tamil at all.

**Deferring is not "leave the TODOs in".** The distinction the code makes:

| | State |
|---|---|
| `src/i18n/messages/ta.ts` | **Complete and type-checked.** A key added to `en.ts` without a Tamil string is still a compile error; `check-i18n-parity.mjs` still counts its 840 outstanding markers on every build — 676 after S13, plus the 164 S23 added with the fleet guide |
| `/ta/anything` | **Not published.** Not built, not in the sitemap, not in any `hreflang` set, answers **404** — the same as `/de` |

The reason it is a 404 and not English served under `lang="ta"`: **a wrong `lang` attribute is an
accessibility failure, not a cosmetic one.** A screen reader hands English prose to a Tamil speech
engine and produces sounds that are not words in any language.

### Re-enabling Tamil is one entry in `WWW_PUBLISHED_MESSAGES`

Import the deferred table beside the two already there in `src/i18n/index.ts`, and add it to
`WWW_PUBLISHED_MESSAGES` under its locale key. `WWW_LOCALES` is that map's keys, so the published set
follows.

**It used to be the one-line `WWW_LOCALES = LOCALES`, and S19 changed it deliberately.** The old form
published a locale without shipping anything, because every table was in the client bundle whether or
not a URL rendered it — 133 kB gzipped, a third of it Tamil that 404s. Deriving the published set from
the table map makes the publication decision and the bytes the same decision, and buys an invariant
the surface did not have:

> A locale's table reaches a browser **if and only if** the locale is published.

`src/i18n/messages/all.ts` holds the total lookup for the things that ask *about* the deferral —
`test/i18n.test.ts`, and conceptually `check-i18n-parity.mjs`, which reads the tables off disk
itself. **Nothing under `app/` or `src/components/` may import it**; one such import puts every
unpublished table back and undoes the whole saving silently, so `test/fences.test.ts` asserts it.

Everything that publishes a URL, narrows a segment, negotiates a language or renders a per-locale
block reads that constant or one of its two helpers — `isWwwLocale` and `negotiateWwwLocale` — and
**never `LOCALES` directly**: `generateStaticParams` in the locale layout and all three dynamic
segments, `localeFrom`, `allUrls`, `getNegotiatedLocale`, `not-found.tsx` and `error.tsx`. Then
delete the three cases in `test/i18n.test.ts`'s *Tamil deferral* suite that name Tamil as
unpublished; the two that assert `ta.ts` is still complete should stay.

**`@mageride/i18n`'s `LOCALES` is untouched and must stay so.** Three other surfaces read it and
Tamil is deferred on none of them — the apps are trilingual today. The deferral is this site's alone,
which is why the constant lives here.

### Two things that look like contradictions and are not

- **`negotiateWwwLocale` filters the header, not the answer.** Post-filtering (take the shared
  negotiator's result, swap Sinhala in if it says `ta`) looks equivalent and silently loses a reader:
  on `Accept-Language: ta-LK, en;q=0.8` it serves Sinhala to somebody who said they read English.
  Unpublished tags are stripped *before* ranking, so that reader gets English and a Tamil-only reader
  gets Sinhala. Tested both ways.
- **The language band still shows Tamil, and the copy still says "Sinhala, Tamil and English".**
  Those are claims about the **apps**, which are genuinely trilingual on all four product surfaces.
  What D2 defers is this marketing site's own Tamil. Different claim, both true.

### One thing S13 found and deliberately did not fix

**`app/[locale]/not-found.tsx` is bound to no route and renders for nothing** — verified against
`.next/app-path-routes-manifest.json`, which lists `/_not-found` and no `/[locale]/_not-found`. Every
404 gets Next's built-in English page. This **predates S13** (`/de/drivers` and `/si/nonexistent`
behaved identically before it), but S13 is what routes an entire language through it.

The fix is not a missing line: a segment's `not-found.tsx` only catches a `notFound()` raised inside
that subtree, so the handler would have to be `app/not-found.tsx` — which renders under the
deliberate pass-through root layout that emits no `<html>`, outside the fonts, `globals.css` and the
appearance script. Making it reachable means deciding again who emits `<html>`, which is the S03/S04
decision this file documents under *Locale routing*. **S14 or S19 owns it.**

## Testing and CI (S20)

**Verify:** `npm --prefix portals run lint && npm --prefix portals run build --workspace @mageride/www && npm --prefix portals run test --workspace @mageride/www`

| What | Where | Runs in CI? |
|---|---|---|
| **147 Vitest tests**, 9 files | `test/` | **No** — see below |
| ESLint, `check-i18n-parity.mjs`, `tsc --noEmit` | `npm run lint` | yes, `build (portal)` |
| `check-al52.mjs`, `check-bundle.mjs` — AL-52 fences + the screen budget | `npm run build` | yes, `build (portal)` |
| `check-budget.mjs` — A34's byte budget | `npm run budget` | yes, `build (portal)`, as its own step (S21) |
| `check-a11y.mjs` — axe + contrast, 48 page loads | `npm run a11y` | not yet; S20 leaves it wired and ready |
| `check-lighthouse.mjs` — 6 pages, mobile throttled | `npm run lighthouse` | yes, **`.github/workflows/lighthouse.yml`** |
| `check-visual.mjs` — 6 baseline frames | `npm run visual` | **no, deliberately** — A17 |

### CI runs lint and build. **It does not run `test`.**

`.github/workflows/ci.yml`'s portal leg is `npm ci && run lint && run build`. Adding `www` to
`portals/package.json`'s `workspaces` put this surface in both with no workflow edit — but the Vitest
suite is enforced by C134's own `verify_cmd`, not by a green `main`. **Do not read a green `main` as
"the tests ran".**

That is the division to keep: **anything that must never regress on `main` goes in `lint` or
`build`** — which is why the fences are ESLint rules and `check-*.mjs` scripts rather than tests —
and the suite is the component's definition of done.

**Measured, because S20 asked whether a fifth Next build breaks the leg:** `npm --prefix portals run
lint` is **123 s** across all eight workspaces and `npm --prefix portals run build` is **194 s**, of
which www's `next build` is **~20 s** warm. Against the matrix job's **45-minute** timeout that is
roughly 12% of budget, so **no matrix split was needed** (plan §A38's contingency stays unused).

### The two browser-driven checks, and why only one is in CI

`lighthouse.yml` is its own workflow — a served build plus six throttled page loads does not belong
on the critical path of every backend push, and it is the noisiest measurement in the repo (the same
`/si` measured TBT of 1,270 ms and 2,280 ms on consecutive runs). **It is red on Performance and
green on Accessibility (100) and SEO (100)**; the cause is in *The A34 budget* above and the decision
is MCS-36 **D3**. Do not lower 95.

`npm run visual` is **local only**, and the reason is A17: `test/fences.test.ts` refuses any
dependency whose postinstall downloads a browser, so putting screenshot diffing in CI would mean
installing one on every portal push. It exists for the one failure nothing else here can see — **a
token edit in `@mageride/tailwind-preset` silently reflowing this site** — which is a change made in
a different directory by a session with no reason to open this one. Six frames at 375px, both
appearances, captured under `reducedMotion: 'reduce'` so the aurora and the marquee are not caught
mid-flight. **When it fails, review `.visual/diff-*.png` and re-baseline; do not delete the test.**
Commit the new baselines in the same change as the styling edit, which is what makes them reviewable.

### `test/content.test.ts` parses the URD rather than restating it

The six driver fee tiers are read out of `specs/user-requirements-document.md`'s own table and
compared against `DAILY_FEE_TIERS`, because a test that hard-coded the numbers would drift with the
site and prove nothing. The parse keys off the **shape of the value** (`Rs N/day`) rather than a list
of vehicle names, so a seventh tier in the spec is a failure rather than a silent omission, and the
vehicle key is a pure normalisation of the URD's label — `Three-wheeler` → `three_wheeler` — so there
is no mapping table to become a second source of truth. Mode A (free) and Mode B (monthly) live in
the same table and are asserted **out** of the tiers: a parse that took every row would render a bus
a Rs 0 daily fee and a private vehicle a Rs 300 *daily* one.

## Deployment (S21)

**One hand-edited file, and everything else is generated.** `infra/k8s/service-catalog.yaml` gains
one `portals:` entry — `www-site`, port 3004, `www.mageride.lk`, 2–6 replicas at 192Mi/384Mi (lower
than the three portals because this one holds no session, opens no upstream and renders no map).
`python3 infra/k8s/tools/generate_manifests.py` writes `base/portals/www-site.yaml` and feeds the CI
image matrix. **Never hand-edit anything under `base/portals/`**; CI runs the generator with
`--check` and drift is a red build.

### `reads_config: false` — the only portal that mounts no ConfigMap

The three portals mount `portal-config` for `MAGERIDE_API_BASE_URL`; without it `src/config/env.ts`
throws and every request answers 503. **This surface must not have it.** MCS-34's fourth negative is
that it has no API dependency at request time, and handing a gateway address to the one pod whose
promise is that it has no gateway is the first step to breaking the promise — the variable would be
inert today and would be the thing a later session reaches for. Its own fences refuse to read any
environment variable at all, and the Dockerfile already sets `NODE_ENV`, `TZ` and the telemetry flag,
so it loses nothing it was using.

S21 added `reads_config` to the catalog and `portal_env()` / `portal_probe_note()` to the generator.
**Default is `true`**, so a new portal is configured unless it argues otherwise. Verified on the
running container: `docker exec … env` carries no `MAGERIDE_*` and no `NEXT_PUBLIC_*`.

### The probe is `/`, and it answers 307

`/` is the one dynamic response this site serves: it reads `Accept-Language` and redirects to the
negotiated locale. The kubelet sends no `Accept-Language`, so a probe gets the platform default —
and any 2xx-3xx counts as success, so **the redirect satisfies the probe**. Checked against the real
container rather than reasoned about (`curl -sI localhost:3004/` → `307 → /si`), because S21 asks for
exactly that and the alternative was pointing the probe at `/en` and hard-coding a locale into the
deployment.

### Four Ingress objects now, and the fourth is one redirect

`www.mageride.lk` joins `mr-ingress-portals` with **its own `mr-tls-www` secret** — following the
`passenger.` precedent, not the two back-office ones: unauthenticated public traffic, so its own key.
It is also the most-presented certificate on the platform, being the one meant to be crawled and
linked, which is another reason it should not be the key that serves the admin console.

**`mr-ingress-apex` is a separate object for `mageride.lk` → 301 → `https://www.mageride.lk`.** It
has to be separate: ingress-nginx annotations are **object-scoped**, so `permanent-redirect` on the
shared portals object would 301 the admin console and the fleet portal to the marketing site. That
is the same argument that already split this file into three. 301 and not 302 because the apex is a
canonical-URL decision as much as a routing one — every canonical, `hreflang` and sitemap entry is
built on `https://www.mageride.lk` (S19 · A32/A43). It carries its own `mr-tls-apex`, because the
redirect is issued over TLS and a browser hitting `https://mageride.lk` has to complete the
handshake before it can be redirected.

### Cache headers are in `next.config.ts`, not at the ingress

S03 assumed the edge; S21 put them in the app, and the reason is *which* response needs which policy.
`/_next/static/**` is content-hashed, `/screens/**` is committed and effectively immutable, HTML is
neither. At the ingress that is an nginx `location` regex per class, maintained a layer away from the
routes it describes and drifting the first time a path moves. Next already knows what it is serving.
The edge keeps what is genuinely the edge's: TLS, HSTS, the apex 301.

**`stale-while-revalidate=86400` is the "renders with the backend down" fence extended one layer
out** — a cache that cannot reach the origin keeps serving the page for a day. The site survives the
*cluster* being down, not just the platform.

**The ordering is the whole correctness of that block, and S21 got it wrong once.** Next applies
every entry whose `source` matches and the *last* match wins for one header name, so the catch-all
must come **first**. Written the intuitive way round, `/_next/static/*.css` came back `s-maxage=300`
instead of `immutable` — invisible in the config, visible only in a response header. Found with
`curl`, pinned by `test/seo.test.ts`.

`output: 'standalone'`, so the container entrypoint is
`node .next/standalone/portals/www/server.js`, not `next start`.
`outputFileTracingRoot` points two levels up because `infra/docker/Dockerfile.portal` asserts that
path — left alone, Next roots the trace at `portals/` and emits one segment less, the build
succeeds, and the container has no server to start.

The image is built from that shared Dockerfile with `--build-arg PORTAL=www --build-arg PORT=3004`.
`infra/k8s/service-catalog.yaml`, the ingress host, the apex 301 and the replica service are
**S21/S22**. D7' §2.1 lists `www-site` as *optional* and that is not a hedge: it is the first
container evicted from the 24 GB box, because it is the only one whose absence costs no platform
function. Any session that makes something depend on the marketing site being up has misread the
table.
