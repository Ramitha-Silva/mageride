# C134 · S07 — the content architecture, and vision / mission / values / marketing copy (English)

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 7 of 22 · Phase 4 (Content), part 1 of 7.**

**Prerequisite:** S03. **Gated on MCS-34 decision D1** — the mission statement. If S01 recorded
three drafted options and no pick, **stop and ask**; do not publish a mission you chose yourself.

This session sets up the content architecture every later content session pours into, then writes
the top-level marketing copy. English only. Sinhala is S12, Tamil is S13.

---

## Before you start

- `specs/user-requirements-document.md` §1 "Product Vision" — dense and technical. A source, not copy.
- `MageRide_Government_Proposal.md` §"The Vision" — a much better public framing:
  *"A Sri Lankan citizen opens one app and sees every bus, every train, every three-wheeler, and
  every van in the country moving in real time…"*. Also a source; also not publishable as-is.
- `specs/MageRide_Functional_Walkthrough.md` §1 "Platform Overview" (line 8) — **already written for
  a lay reader**: *"Think of it as three things in one: 1. A live map of buses, trains, school
  vans… 2. A ride-hailing service… 3. A delivery service…"*. This is the closest thing the repo has
  to publishable copy; start here.
- `portals/web-passenger/src/i18n/messages/en.ts` — the key-naming and comment conventions to match.

**There is no mission statement anywhere in the repository.** `grep -i mission specs/` returns only
"permission", "commission" and "provisioning". That is why D1 is a gate.

---

## Do this

### 1 · The chapter shape — define it once, here

`src/content/types.ts`. Every guide chapter in S08–S11 and S23 conforms to it. A uniform shape means
uniform components, uniform translation, and uniform structured data — the step list maps straight
onto `HowTo` JSON-LD in S19.

```ts
export interface Step {
  instruction: WwwMessageKey;
  note?: WwwMessageKey;
  screenRef?: string;               // a ScreenEntry.id from src/content/screens.ts
}

export type CalloutKind = 'tip' | 'warning' | 'fee' | 'privacy';

export interface Callout {
  kind: CalloutKind;
  body: WwwMessageKey;
  source?: string;                  // spec anchor — REQUIRED when the callout states a fact
}

export interface Chapter {
  id: string;                       // 'p01'
  slug: string;                     // 'install-and-first-run' — stable, in the URL, never localised
  audience: 'passenger' | 'driver' | 'fleet';
  order: number;
  title: WwwMessageKey;
  summary: WwwMessageKey;
  steps: Step[];
  callouts: Callout[];
  screens: string[];                // ScreenEntry ids shown in the chapter's strip
  relatedChapters: string[];        // chapter ids
  faqRefs: string[];                // FaqEntry ids
  sources: string[];                // spec anchors — the chapter's provenance
}
```

**Structure is shared; only strings are localised.** That is the whole reason this is typed TS and
not MDX (`docs/www-site-plan.md` §A6): a per-locale MDX chapter lets the Sinhala version quietly
lose a step. Here a translator can only replace text behind a key, and a missing key is a compile
error.

`slug` is **not localised.** `/si/guide/passenger/install-and-first-run` — Sinhala content, stable
URL. Localised slugs triple the route table, break `hreflang` reciprocity and make an external link
locale-specific for no reader benefit. Record that decision in `portals/www/CLAUDE.md`.

### 2 · `src/content/index.ts` — the registry

Chapter ordering, the slug → chapter map, the audience split, and a `chapterBySlug()` that
`generateStaticParams` and `app/sitemap.ts` both read. A chapter that exists and is not registered
must be impossible: export the registry as the **only** way chapters are reached, and have
`test/content.test.ts` assert every file in `src/content/guide/**` appears in it.

### 3 · Vision — `src/content/vision.ts`

- **One hero line.** One sentence. It appears at 48–72px in three scripts, so it must survive
  Sinhala's longer word forms without wrapping to four lines at 375px.
- **~120 words of public copy**, rewritten from the URD §1 and the proposal's framing. Plain
  language. No "platform", no "ecosystem", no "leverage". A Sri Lankan reader who has never used a
  ride app should finish it knowing what the thing is.
- Every factual claim carries a `sources` anchor.

### 4 · Mission — **the chosen option only**

Write the one the user picked in S01. Keep the other two in MCS-34's record, not on the site.

### 5 · Values — 4–6 cards, each traceable to something the platform actually does

Not aspirations. Each card names a real behaviour and carries its anchor:

| Value | It is true because |
|---|---|
| Zero commission | URD §1 — passengers pay fares directly to drivers |
| Passengers pay the platform nothing | URD §1 |
| Trilingual by default | `CLAUDE.md` Universal Rules; every surface ships si/ta/en |
| Open-source mapping | D3′ map hard rule / D-14 — OSM + self-hosted Nominatim, **no Google Places fallback, ever** |
| The first trip of the day is free | URD §1, Epic 9 — the daily platform fee |
| Your data is yours | `pdpa-svc` — export and erasure with a 30-day due date |

### 6 · The marketing copy corpus — ~6,000 English words

Everything S14–S18 will render, written now so those sessions compose rather than author:

- **Hero slides ×4** — *Track everything live* · *Book a ride in seconds* · *Drivers keep 100%* ·
  *Send a package across town*. Each: headline, sub, two CTA labels.
- **The three modes** — Mode A (public buses & trains), Mode B (private/school/staff vehicles you
  follow), Mode C (on-demand rides & deliveries). One paragraph each. **Get the boundary right:**
  `ride-svc` owns Mode C, `trip-state-svc` owns Mode A/B — the copy must not describe them as one
  feature with a switch.
- **How it works** — four steps, passenger cut and driver cut.
- **Five feature splits** — headline + 60 words each.
- **The zero-commission band** — the fee narrative, and the **six driver fee tiers** in
  minor-unit-correct Rupees, read from URD §1 / Epic 9. `test/content.test.ts` (S20) asserts these
  six numbers against the URD, so put them in one exported constant with its anchor, not inline.
- **Stats** — 11 vehicle types · 3 languages · 0% commission · first trip free. Each with its source.
- **The language band** — the same sentence in Sinhala, Tamil and English, shown together. This one
  is intentionally not a translation lookup: it is three strings on one card.
- **Footer, nav labels, CTA labels, 404 and error copy.**
- **`src/content/faq.ts`** — 15–20 entries, `{ id, question, answer, refs }`. These feed `/faq`, the
  per-page FAQ subsets, and `FAQPage` JSON-LD.

### 7 · Add every string to `src/i18n/messages/en.ts`

Grouped and commented in the style of `web-passenger`'s file. `si.ts` and `ta.ts` gain the same keys
with **`TODO(si)` / `TODO(ta)` English placeholders** so the build stays green — S12 and S13 replace
them, and `check-i18n-parity.mjs` should **warn** (not fail) on a `TODO(` marker so the count is
visible in every build until it reaches zero.

---

## Fences

- **No invented fact.** If a number, a fee, a tier or a claim is not in a spec, it does not go on a
  public site. When you need one that does not exist, list it in the handoff as a content gap.
- **No mission statement other than the one chosen in D1.**
- **No legal text** (D5) and **no contact details** (D4) — those pages are S18 and are gated.
- **No literal in JSX.** Content modules hold *keys*; the strings live in the message files.
- **Money as minor units** in code; formatted for display. A fee table is currency.

---

## Verify

```
npm --prefix portals run lint
npm --prefix portals run test --workspace @mageride/www
npm --prefix portals run build --workspace @mageride/www
node portals/www/scripts/check-i18n-parity.mjs     # green; reports the TODO( count
```

---

## Handoff

- **Component:** C134 www-informational-site — S07 (content architecture & marketing copy) — <date>
- **Status:** DONE | BLOCKED (D1 unanswered)
- **Notes:** the mission chosen; the six fee tiers with the URD line they came from; every content
  gap where the site needs a fact the specs do not state; the English word count so far.
