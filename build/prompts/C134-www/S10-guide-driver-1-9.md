# C134 · S10 — driver guide, chapters 1–9 (English)

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 10 of 22 · Phase 4 (Content), part 4 of 7.**

**Prerequisite:** S08/S09 (the shape and conventions are established there — read S08's Fences).

---

## Sources

`specs/MageRide_Functional_Walkthrough.md` **Section B** (line 668) is the driver app end to end.
`specs/D1_mageride_user_flows.md` Section B is **11 driver flow groups**. URD Epics 1 / 2 / 3 / 5 /
6 / 6A / 9 / 9A / 12 / 17 / 20 carry the acceptance criteria.

**The driver guide is the more consequential of the two.** A passenger who misreads a chapter takes
a wrong turn; a driver who misreads one loses money or fails an approval. Every fee, tier, timeout
and document requirement carries its anchor, and `test/content.test.ts` (S20) asserts the six fee
tiers against URD §1.

---

## Do this — nine chapters, `src/content/guide/driver/d01…d09.ts`

| # | slug | Chapter | Primary source |
|---|---|---|---|
| 1 | `install-and-first-run` | Install & first run — language/city, OTP, driver profile | D1 §B.7 Phase 1 |
| 2 | `onboarding-your-vehicle` | Onboarding your vehicle — the four steps | D1 §B.7 Phase 2 |
| 3 | `photographing-documents` | Photographing documents — camera + drag-crop | URD US-24.6 · AL-43 |
| 4 | `approval` | Approval — what auto-verifies, what a human reviews | URD Epic 2 |
| 5 | `permissions-and-background-location` | Permissions and background location | D1 §B.5 |
| 6 | `your-dashboard` | Your dashboard — Mode C standby vs Mode A/B journey control | D1 §B.8 |
| 7 | `going-on-standby` | Going on standby and staying visible | URD Epic 6A |
| 8 | `the-15-second-offer` | The 15-second offer — accept, reject, what a miss costs | D1 §B.9 |
| 9 | `running-a-trip` | Running a trip — navigate, arrive, start, end | D1 §B.9 |

### Chapter-specific facts you must get right

- **Ch 2** — vehicle onboarding is **four steps** and steps are **saved individually**; a vehicle
  shows in *My Vehicles* as **Incomplete** or **Approved** (US-2.26).
  **＋ means add** — it starts a fresh Step 1/4 unconditionally; **Resume ›** on a row continues that
  specific vehicle (**MCS-06** — read `build/prompts/MCS-06-plus-starts-a-new-vehicle.md`, this
  changed and the old behaviour is documented in older specs). The **nav-drawer** entry keeps the
  resume behaviour. Three doors, three meanings — the guide must say which is which, because that is
  the exact confusion the change set was raised for.
- **Ch 3** — the driving-licence scan extracts **NIC number and allowed vehicle types** alongside
  licence number and expiry (US-2.4a). Per-vehicle document slots are named: **registration copy
  (CR book), insurance certificate, revenue licence, route permit (Mode A required)** (US-27.3).
  **Insurance is mandatory for all modes.**
- **Ch 4** — anything **low-confidence, driver-entered, or a plate↔registration-number mismatch**
  sets that step **Pending** for a Verification Officer's *Confirm / Edit & confirm* before approval
  (US-2.10a). Say plainly what a driver can do while waiting.
- **Ch 6** — when the active vehicle is **Mode A or Mode B, the Start/End-Journey screen IS the home
  dashboard** (US-5.11) — only Start/End buttons, with vehicle type and number below the route card.
  The driver home map shows **only the driver's own active vehicle** and there is no hamburger
  (US-7.18).
- **Ch 8** — **15 seconds** per offer. Be precise and honest about what a miss costs: state it from
  URD Epic 6A / D1 §B.9 and **anchor it**. If the specs do not define a penalty for a missed offer,
  say there is none rather than inventing a consequence — and record the gap.
- **Ch 9** — navigate → arrive → **start (OTP)** → end. Name the OTP steps; a driver who does not
  know a code is required will call support on their first trip.

### Every chapter

Same rules as S08: `sources` on every claim, 1–3 `screens` (`SCR-DA-*` preferred — one frame per
concept, per S05's curation rule), `en.ts` keys under `www.guide.d01.*`…, `TODO(si)`/`TODO(ta)`
placeholders.

---

## Fences

- **Every fee, tier, timeout and penalty carries an anchor.** A driver-facing number on a public
  page is a commercial claim.
- **MCS-06's ＋/Resume split is current behaviour.** Do not describe the superseded US-2.27 wording.
- **iOS and Android are one guide**, not two. Where a control differs materially, say so in a `note`
  on the step rather than forking the chapter.
- **No literal user-facing string.**

---

## Verify

```
npm --prefix portals run lint
npm --prefix portals run test --workspace @mageride/www
node portals/www/scripts/check-i18n-parity.mjs
```

---

## Handoff

- **Component:** C134 www-informational-site — S10 (driver guide 1–9) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** every number quoted and its anchor; whether a missed-offer consequence is actually
  specified anywhere; content gaps.
