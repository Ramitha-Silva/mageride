# C134 · S16 — `/vision`, `/passengers`, `/drivers`, `/fleets`

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 16 of 22 · Phase 5 (Pages), part 3 of 5.**

**Prerequisites:** S14, S15 (the components these pages compose already exist).

Four pages, one template. If you find yourself writing a fourth bespoke layout, stop and extract the
template — S19's Lighthouse gate runs on `/drivers`, and four hand-built pages means four
independent ways to fail it.

---

## Do this

### The shared template

hero → benefit grid → three-step how-it-works → screen strip → guide entry point → FAQ subset → CTA.

Parameterised by an entry in `src/content/` per page, so the page component is one file and the
differences are data. The FAQ subset is a list of `faq.ts` ids, not duplicated prose.

### `/vision`

Vision, the chosen mission (MCS-34 D1), and the values cards from S07's `vision.ts`. This is the
page a journalist or an official reads; it carries no CTA pressure and no store badge. Every value
card shows what makes it true — the anchors S07 attached are for the reader as much as for review.

### `/passengers`

Benefits from the passenger guide's own material. Entry point into `/guide/passenger`. Screen strip
from the passenger `SCR-PA-*` registry entries.

### `/drivers` — the page that carries commercial claims

Everything the template gives, **plus the fee table with the six vehicle-type tiers**, rendered from
the same constant `FareTable` uses on the home page.

It must state the **"first trip of the day is always free"** rule **exactly as URD §1 states it**.
Not a paraphrase. This is a public commercial commitment, it is the single most attractive thing
about the platform to a driver, and a loose restatement of it is the kind of error that ends up in a
screenshot.

Also state, with anchors: zero commission (passengers pay drivers directly); Mode B is monthly;
Mode A pays nothing.

Link into `/guide/driver`, and specifically into the onboarding chapters — a driver who arrives here
from an ad wants to know what documents they need before they download anything.

### `/fleets`

The Fleet Owner is the third end-user role and `fleet.mageride.lk` is a real surface, so this page
must exist even if the fleet **guide** is deferred (MCS-34 D7 → S23). Cover: what a fleet account
does, KYC, vehicles single and bulk, assigning drivers, binding trackers, billing (monthly per Mode
B vehicle; Mode A free; Mode C non-fleet).

If S23 has not run, the guide entry point on this page links to `/contact` or to the relevant driver
chapters instead of to a 404. **Never render a link to a chapter that does not exist** —
`test/routes.test.ts` will catch it, but it should not get that far.

### Register the routes

All four go into `src/lib/routes.ts` this session, so `app/sitemap.ts`, the nav, the footer and the
`hreflang` block pick them up without a further edit.

---

## Fences

- **One template, four data files.**
- **The fee tiers come from the one constant.** No second copy on `/drivers`.
- **The free-first-trip rule is quoted, not paraphrased.**
- **No form on any of these pages.** A "talk to us about fleets" CTA is a `mailto:` or a link to
  `/contact`, never a field.
- **No API call, no cookie, no literal string.**

---

## Verify

```
npm --prefix portals run lint
npm --prefix portals run build --workspace @mageride/www
npm --prefix portals run test --workspace @mageride/www
```

By hand: all four pages × every rendered locale × both appearances at 375/768/1024. Confirm the
`/drivers` fee table renders the same six values as the home page band — they read the same constant,
so a difference means one of them is not reading it.

---

## Handoff

- **Component:** C134 www-informational-site — S16 (role landing pages) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** the template's shape and what each page parameterises; the exact URD §1 sentence quoted
  on `/drivers`; where `/fleets` points its guide link if S23 has not run.
