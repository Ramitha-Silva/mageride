# C134 · S08 — passenger guide, chapters 1–8 (English)

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 8 of 22 · Phase 4 (Content), part 2 of 7.**

**Prerequisite:** S07 — `src/content/types.ts` and the registry exist. S05's `screens.ts` should
exist too; if it does not, write the chapters with empty `screens: []` and fill them in S17.

---

## The work is selection and rewriting, not research

`specs/MageRide_Functional_Walkthrough.md` is 198 KB and **114 numbered scenarios** across Sections
A–K. Section A (line 87) is the passenger app end to end. `specs/D1_mageride_user_flows.md` Section
A is six passenger flow groups. The URD's 24 epics carry the acceptance criteria.

**The corpus already exists in plain language.** Your job is to select from it, rewrite it for a
member of the public who has never opened the app, and keep every factual claim anchored.

Target **~450 English words per chapter**, 5–9 steps, 1–3 callouts.

---

## Do this — eight chapters, `src/content/guide/passenger/p01…p08.ts`

| # | slug | Chapter | Primary source |
|---|---|---|---|
| 1 | `install-and-first-run` | Install & first run — language, city, phone + OTP, profile | Walkthrough §A · URD Epic 1 |
| 2 | `permissions` | Permissions — location, notifications, background | D1 §A.5 · URD Epic 1 |
| 3 | `reading-the-live-map` | Reading the live map — modes, the 11 vehicle colours, clusters | D2 §0.2 table 2 · MAP-03/05/06 |
| 4 | `buses-and-trains` | Tracking buses & trains (Mode A) | URD Epic 7 · transit-svc GTFS |
| 5 | `following-a-private-vehicle` | Following a private vehicle (Mode B) — sharing grants, subscribe | URD Epic 4 |
| 6 | `mode-b-payments` | Mode B payments — Paid vs Free, monthly, history, unsubscribe | URD Epic 23 |
| 7 | `booking-a-ride` | Booking a ride (Mode C) — search / map pin / paste link / request | URD Epic 8 · D1 §F-23.2 |
| 8 | `choosing-a-vehicle` | Choosing a vehicle and reading the upfront fare | URD Epic 8 · D5 §1 |

### Chapter-specific facts you must get right

- **Ch 1** — Sinhala is the **default and first** language (D1′ §283); the three boxes are vertical,
  Si/Ta/En. Auth is **Phone OTP only** for the apps (AL-07); email/password is portals, not apps.
- **Ch 3** — the **11** vehicle-marker colours are a canonical palette (AL-09), listed in
  `portals/tailwind-preset/src/tokens.ts` and in every wireframe's `:root`. Name all eleven vehicle
  types. Mode A/B/C badge colours are a separate set of three.
- **Ch 4** — buses and trains come from a **GTFS feed** owned by transit-svc. Say plainly that
  coverage depends on the feed. Do not promise a route that is not in it.
- **Ch 5/6** — Mode B is **follow-with-permission**: a passenger requests access to a specific
  vehicle and the owner grants it. "Service payment" is the label; Paid vs Free is the setting
  (US-27.4 renamed the UI label only — API and DB names are unchanged, so do not use the old word).
- **Ch 7** — four ways to set a location: geo-search, a map pin, **paste a Google Maps link**
  (AL-20, transit-svc `/v1/geo/parse-maps-link`), and saved places. The search box is query-svc's
  Nominatim (`/v1/geo/search`). **There is no Google Places fallback, ever** (D3′ map hard rule,
  D-14) — if you describe search, describe it honestly: it is OpenStreetMap-based.
- **Ch 8** — the fare is **upfront**, per vehicle type, from D5 §1's tariff. OnePay adds **+5%**
  shown as a recomputed total (US-8.11); LankaQR has **no surcharge**; cash is the default.

### Every chapter

- `sources: []` lists the anchors. `test/content.test.ts` will not check prose, but a reviewer can
  follow every claim back.
- `screens: []` names 1–3 `SCR-*` IDs from `src/content/screens.ts`. If the ID you want is not in
  the registry, **add it there** — S05's curation was a first pass, and the guide is the customer.
- `callouts` carry `source` whenever they state a fact. A `fee` callout without an anchor is how a
  public site ends up quoting a price that changed.
- Add every string to `src/i18n/messages/en.ts` under a `www.guide.p01.*` key family, and to
  `si.ts` / `ta.ts` as `TODO(si)` / `TODO(ta)` placeholders.

---

## Fences

- **Nothing invented.** If the specs do not say it, it does not go on a public site. List the gap.
- **`ride-svc` ≠ `trip-state-svc`.** Mode C is on-demand; Mode A/B are scheduled/followed. The guide
  must not blur them, because a passenger who expects to hail a school van has been misled.
- **No screenshot claim the wireframes do not support.** A step that references a control must
  reference a screen that has it.
- **No literal user-facing string** anywhere.

---

## Verify

```
npm --prefix portals run lint
npm --prefix portals run test --workspace @mageride/www
node portals/www/scripts/check-i18n-parity.mjs
node -e "const {chapters}=require('./portals/www/src/content/index.ts');" 2>/dev/null || true
```

`test/content.test.ts` (S20) will later assert every `screenRef` resolves and every chapter is
registered; until it exists, check by hand that the eight chapters appear in the registry in order.

---

## Handoff

- **Component:** C134 www-informational-site — S08 (passenger guide 1–8) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** word count per chapter; every screen ID added to the registry; every content gap where
  the guide needs a fact the specs do not state.
