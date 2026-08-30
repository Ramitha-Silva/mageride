# C134 · S18 — `/screens`, `/faq`, `/download`, `/contact`, `/legal/[doc]`

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 18 of 22 · Phase 5 (Pages), part 5 of 5.**

**Prerequisites:** S14–S17.

**Three of these five pages are gated on decisions from S01.** Read S01's handoff for D3 (store
URLs), D4 (contact details) and D5 (legal text) before starting. Where a decision is unanswered,
build the constrained variant named below — do not invent an address, a phone number or a privacy
policy.

At the end of this session **every route in `src/lib/routes.ts` renders in every rendered locale**,
which is Phase 5's gate.

---

## Do this

### `/screens` — the gallery

Every entry in `src/content/screens.ts`, filterable by **surface × mode × chapter**.

- Filters are **URL state**, not component state: `?surface=driver&mode=c`. A filtered view survives
  a reload, a bookmark and the back button, and it is shareable. `portals/admin`'s SCR-AP-002 is the
  precedent in this repo for "a server render whose entire state is the URL" — follow it.
- The lightbox is S15's, keyboard-navigable, focus-trapped.
- Each screen's caption comes from its `captionKey`. Where a screen belongs to a chapter, link to it.
- **Caption honestly.** These are wireframe-derived compositions, not screenshots of a running
  build (MCS-34 decision D10). One clear line somewhere on the page saying the images show the
  approved interface designs is worth more than a defensive footnote on every tile.

### `/faq` — the accordion

From `src/content/faq.ts` (S07). Grouped by audience.

- `FAQAccordion` — `@mageride/ui`'s `Tabs`/disclosure primitives are Radix-backed; use them. Each
  item is a real button with `aria-expanded` and a controlled region.
- **All answers must be in the DOM whether open or closed** — a crawler and a JS-off reader need
  them, and `FAQPage` JSON-LD (S19) must describe content that is actually on the page.
- Deep-linkable: `#faq-<id>` opens the item.

### `/download`

**If D3 was answered:** store badges linking to the real listings, plus a short "which app do I
want" split (Passenger vs Driver).

**If D3 was not answered:** an email-notify page **with no form**. A `mailto:` link with a
pre-filled subject, and a sentence saying the apps are not yet listed. A form means a backend, a
data-protection position and a retention policy — all three are separate change sets
(`docs/www-site-plan.md` §13).

Either way: state the minimum Android version (URD NFR-22: **Android 8.0 / API 26**). There is no
stated minimum iOS in any spec — do not invent one; omit it.

### `/contact` — **no form**

Email, phone, address, hours, from D4. If D4 was not answered, ship **email-only** and leave the
other rows out rather than showing placeholders. The proposal's `📧 [To be added]` must never reach
a public page.

### `/legal/[doc]` — terms · privacy · pdpa

**The text is supplied, not authored** (D5). This session builds the shell: a document layout, a
table of contents, a last-updated line, and the three routes.

- If the text was supplied, lay it out and translate the **structure** (S12/S13 handle strings).
- If it was not, the routes exist and render a short, honest "this document is being prepared"
  page — **not** a generic template pulled from elsewhere. A wrong privacy policy is worse than an
  absent one.
- The **privacy** page should accurately describe what `pdpa-svc` actually does — export and
  erasure, 30-day due date — and what **this site** collects, which is **nothing**: no cookies, no
  analytics, no form, no logs beyond the ingress's. Say that plainly; it is unusual and it is true.

### Finish `src/lib/routes.ts`

Every route registered. `app/sitemap.ts` and `app/robots.ts` are wired in S19, but the table they
read is complete at the end of this session.

---

## Fences

- **No form anywhere on this surface.** No contact form, no newsletter, no chat widget.
- **No invented contact detail, store URL, or legal text.**
- **No cookie, no analytics, no API call.**
- **FAQ answers are in the DOM when collapsed.**
- **No literal user-facing string.**

---

## Verify

```
npm --prefix portals run lint
npm --prefix portals run build --workspace @mageride/www
npm --prefix portals run test --workspace @mageride/www
grep -rnE "<form|onSubmit|action=\"/" portals/www/app portals/www/src    # nothing that posts
grep -rn "To be added\|TODO\|Lorem" portals/www/src/i18n portals/www/src/content   # nothing
```

By hand: every route in `routes.ts`, in every rendered locale. That is Phase 5's gate — walk it.

---

## Handoff

- **Component:** C134 www-informational-site — S18 (remaining pages) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** which of D3/D4/D5 were answered and which constrained variant shipped for the rest; the
  full route count; anything still carrying placeholder copy and what unblocks it.
