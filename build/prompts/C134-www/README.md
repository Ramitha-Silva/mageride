# C134 · `www.mageride.lk` — session prompts

## Identity

**These files are hand-written and are NOT generated.**

`build/tools/generate_build_plan.py` writes one `build/prompts/Cxxx.md` per entry in
`build/manifest.yaml` and **deletes nothing**, so this directory survives a regeneration untouched.
It is also not produced by it. `build/prompts/C134.md` — the thin, generated prompt — will appear
beside this directory once S02 adds C134 to the manifest; that file is a summary, and **these files
are the work**.

Derived from `docs/www-site-plan.md` (planning session, 2026-08-27), re-verified against the repo on
**2026-08-28**. Where the plan and the repo disagree, the corrections are recorded in §4 below and
inside the session that needs them.

---

## 1. How to use this directory

One file, one fresh session. Start each session with:

```
Read build/prompts/C134-www/S03-scaffold-workspace.md and do exactly what it says.
```

Every prompt is self-contained: it names what must already be true, what to read, what to build, the
fences, the verify command and the handoff. Do not run two in one session — the sequencing exists so
that each session's verify command is meaningful.

**After every session**, append a 3-line handoff to `build/progress.md` under *Session Handoffs*
(Component / Status / Notes), naming the session file. `build/progress.md`'s Status column for C134
moves to `DONE` only after S22.

---

## 2. Running order

| # | File | Phase | Gate before the next session |
|---|---|---|---|
| S01 | `S01-governance-mcs34.md` | 0 · Governance | MCS-34 written, D1–D10 answered, spec deltas applied |
| S02 | `S02-manifest-c134-regenerate.md` | 0 · Governance | `git diff build/progress.md` shows **exactly one added row** |
| S03 | `S03-scaffold-workspace.md` | 1 · Scaffold | `npm --prefix portals run lint && npm --prefix portals run build` green with the empty site in |
| S04 | `S04-motion-and-fonts.md` | 2 · Design | motion primitives render; reduced-motion verified; Si/Ta faces resolve |
| S05 | `S05-screen-registry-capture.md` | 3 · Imagery | `screens.ts` registry + raw captures for every listed ID |
| S06 | `S06-compose-frames-budget.md` | 3 · Imagery | `public/screens/` committed, ≤ 12 MB, budget gate wired |
| S07 | `S07-content-vision-mission.md` | 4 · Content | vision + chosen mission + values + marketing copy, English |
| S08 | `S08-guide-passenger-1-8.md` | 4 · Content | passenger chapters 1–8, English |
| S09 | `S09-guide-passenger-9-16.md` | 4 · Content | passenger chapters 9–16, English |
| S10 | `S10-guide-driver-1-9.md` | 4 · Content | driver chapters 1–9, English |
| S11 | `S11-guide-driver-10-18.md` | 4 · Content | driver chapters 10–18, English |
| S12 | `S12-translate-sinhala.md` | 4 · Content | `si` complete; parity script green |
| S13 | `S13-translate-tamil.md` | 4 · Content | `ta` complete **or** formally deferred per D2 |
| S14 | `S14-shell-nav-hero.md` | 5 · Pages | `/si`, `/ta`, `/en` render; hero passes the APG carousel pattern |
| S15 | `S15-home-sections.md` | 5 · Pages | home complete in three locales |
| S16 | `S16-role-landing-pages.md` | 5 · Pages | `/vision` `/passengers` `/drivers` `/fleets` |
| S17 | `S17-guide-pages.md` | 5 · Pages | every chapter slug resolves in three locales |
| S18 | `S18-screens-faq-download-contact-legal.md` | 5 · Pages | every route in `routes.ts` renders |
| S19 | `S19-seo-a11y-perf.md` | 6 · SEO/a11y/perf | hreflang reciprocal; JSON-LD valid; budgets met |
| S20 | `S20-tests-ci-lighthouse.md` | 7 · Testing | the full verify chain green |
| S21 | `S21-deploy-k8s-ingress.md` | 8 · Deploy | `generate_manifests.py --check` clean |
| S22 | `S22-replica-dns-smoke.md` | 8 · Deploy | replica image builds; smoke checks added; DNS rows filed |
| S23 | `S23-optional-fleet-guide.md` | *optional* | only if D7 = yes; run after S17 |

Phases 3 and 4 are independent of phase 5 and may interleave, but **S14 needs S07** (the hero needs
copy) and **S15 needs S06** (the showcase needs images).

---

## 3. Standing rules — true for every session in this directory

1. **`CLAUDE.md` first, then `portals/www/CLAUDE.md`** once S03 has written it.
2. **AL-52 — Tailwind is the sole styling system.** No runtime CSS-in-JS, no pre-styled kit, and
   **no motion library** (see §4.3). `npm --prefix portals run lint` runs `check-al52.mjs` over the
   whole tree, including files no ESLint config covers.
3. **No literal user-facing string in JSX.** `mageride/no-literal-user-facing-strings` flags JSX
   text, literal children and literals in `alt` / `title` / `placeholder` / `label` / `aria-*`.
   Everything a reader can see is a resource key in **si, ta and en**.
4. **No API call at request time.** This surface must render with the whole backend down. No
   `fetch`, no `axios`, no `NEXT_PUBLIC_*`, no SSE, no map tiles.
5. **No cookies and no analytics.** A `localStorage` theme preference is the one permitted piece of
   client state (S19), and it is not a cookie.
6. **Money as minor units**, trilingual resources, specs are the source of truth — the three
   Universal Rules in `CLAUDE.md` apply here as everywhere.
7. **Every public claim carries a spec anchor** in the content module that makes it. A fee, a tier,
   a vehicle count or a "first trip free" on a public site is a factual assertion.
8. **Build host:** keep the replica stack **down**. Slim dev compose only; a fifth Next.js build on
   the 24 GB box is fine, the replica beside it is not.

---

## 4. Corrections to `docs/www-site-plan.md` — read before S05, S12 and S22

The plan is sound. Six of its statements do not survive contact with the repo, and each is repeated
in the session that depends on it.

### 4.1 The wireframes carry no `id` attribute on a screen (affects S05)

The plan's A15 says "clip to the frame's bounding box by its `SCR-*` anchor". There is no anchor.
A screen appears as **text inside a caption**:

```html
<div class="cell">
  <div class="cap"><span class="scr">SCR-PA-014</span> · finding driver <span class="tag rep">REPLACE</span></div>
  <div class="phone"> … the frame … </div>
  <div class="states"><b>States:</b> …</div>
</div>
```

So the capture script locates the `.cell` whose `.cap .scr` **text** equals the ID, then screenshots
its `.phone` (mobile) or `.browser` (the two portal files). `web_passenger.html` uses `.mweb`.
Frame geometry: `.phone` is **320 × 680** including a 9px bezel — not the 375 × 812 the plan assumed.

### 4.2 The wireframes have no dark mode at all (affects S05)

`grep -c "prefers-color-scheme\|\.dark" specs/wireframes/*.html` returns **0 for all eight files**.
"Capture both appearances" cannot be done by toggling a class. Every colour in those files *is* a
CSS custom property on `:root`, so the honest route is to inject a dark `:root` override built from
`@mageride/tailwind-preset`'s own dark tokens before the screenshot. S05 carries that decision.

### 4.3 Framer Motion would pass `check-al52.mjs`, and is still out (affects S04)

`portals/eslint-config/banned-styling-packages.json` lists 18 packages and 6 prefixes; `motion` /
`framer-motion` is on neither list. The fence is AL-52's **intent** — "CSS is compiled at build time
by PostCSS … one plugin, no runtime style injection" — plus the C134 fence and `test/fences.test.ts`,
which greps `package.json` for the known motion packages by name. Do not add one; do not widen the
banned list to "fix" this, because that is a platform-wide change for one marketing page.

### 4.4 The i18n compile-time guarantee comes from the surface's own type, not `@mageride/i18n` (affects S07–S13)

`@mageride/i18n`'s `Messages` covers only what every surface shares. The per-surface pattern, set by
`portals/web-passenger/src/i18n/`, is two tables: `messages/en.ts` declares a literal object and
**defines** the key type; `si.ts` and `ta.ts` are annotated with it, so a key in one and not the
others is a **compile error**. `www` follows the same shape with a `WwwMessages` type.

### 4.5 `web-passenger` is not in the replica compose (affects S22)

`infra/replica/docker-compose.light-replica.yml` has `admin-portal` and `fleet-portal` under the
`portals` profile and nothing else. There is no passenger-web precedent to copy — copy
`admin-portal`'s shape, minus `MAGERIDE_API_BASE_URL`, which this surface must not have.

### 4.6 CI runs `lint` and `build`, not `test` (affects S20)

`.github/workflows/ci.yml`'s portal leg is `npm --prefix portals ci && run lint && run build`.
Adding a workspace member does put C134 in CI — the plan's A38 is right — but the **test suite is
enforced by C134's own verify command, not by CI**. S20 says so out loud rather than assuming a
green main means the tests ran.

---

## 5. Verified repo facts (2026-08-28) — so no session re-derives them

| Fact | Value |
|---|---|
| Portal workspaces | `eslint-config`, `tailwind-preset`, `i18n`, `ui`, `admin`, `fleet`, `web-passenger` (7) |
| Next / React | `next ^16.3.0`, `react ^19.2.8`, Tailwind `^4.3.3`, Node `>=24 <25` |
| Ports in use | 3001 admin · 3002 fleet · 3003 web-passenger → **www takes 3004** |
| Highest component ID | **C133** (`payout-svc`) → **C134 is free** |
| Highest change-set ID | **MCS-33** → **MCS-34 is free**; `MCS-34` appears today only in `docs/www-site-plan.md` |
| Screen coverage | **202 / 202**, and C134 claims no `SCR-*` ID, so it stays 202 / 202 |
| `build/progress.md` | **21,869 lines**; *Session Handoffs* starts at line 251 — the A4 backup is not theoretical |
| Wave 4c gate | line 21: "all three web surfaces lint + test + build" → becomes **four** |
| URD "four surfaces" | three places: line 69 (the Note), line 608 (US-11.1), line 1227 (glossary) |
| D7 container table | §2.1, line 44 — `admin-portal` / `fleet-portal` rows are the model |
| D2 fonts | line 78 (Outfit/Inter), and the AL-52 addendum at line 1484 |
| Shared package builds | `dist/` is gitignored; `portals/scripts/ensure-workspace-deps.mjs` builds them on demand — every portal wires it as `predev`/`prebuild`/`prelint`/`pretest` |
| Bundle gate precedent | `portals/web-passenger/scripts/check-bundle.mjs` |
| Fence-test precedent | `portals/web-passenger/test/fences.test.ts` |
| Surface CLAUDE.md precedent | `portals/web-passenger/CLAUDE.md` |
