# C134 · S23 — fleet-owner guide, 6 chapters *(optional — conditional on MCS-34 decision D7)*

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Optional session · Phase 4 (Content) · run after S17, before or after S22.**

**This session is gated on MCS-34 decision D7.** The recommendation on record: **yes, but in the
second delivery phase.** If D7 was "no", do not run this — `/fleets` (S16) already covers the role
at a landing-page level and links elsewhere for detail.

---

## Why it exists

The brief asked for passengers and drivers. **Fleet Owner is the third end-user role** (URD: only
Driver, Passenger and Fleet Owner are end-user roles; every other role logs in to the Admin Portal),
and its absence is noticeable on a site that names `fleet.mageride.lk` and has a `/fleets` page.

Six chapters, ~450 words each — roughly two sessions' worth of writing compressed into one because
the shape, the components and the translation pipeline all already exist.

---

## Sources

- `specs/MageRide_Functional_Walkthrough.md` **Section E — Fleet Portal** (line 1788).
- `specs/D1_mageride_user_flows.md` — the fleet flow groups.
- URD Epic 13 (fleet stories, incl. US-13.13–13.16 and US-13.1b) and **Epic 27** (fleet payout &
  vehicle-document detail, AL-49…AL-51).
- `build/screen_coverage.md` — the 13 `SCR-FP-*` screens and their owning components (C112–C116).

---

## Do this — six chapters, `src/content/guide/fleet/f01…f06.ts`

| # | slug | Chapter | Primary source |
|---|---|---|---|
| 1 | `registering-your-organisation` | Registering your organisation | Walkthrough §E · URD Epic 13 |
| 2 | `kyc-and-your-payout-profile` | KYC, and the bank & payout profile | URD Epic 27 (US-27.1/27.2) · SCR-FP-002a |
| 3 | `adding-vehicles` | Adding vehicles — one at a time, and in bulk | URD Epic 13 · SCR-FP-004 |
| 4 | `vehicle-documents` | Vehicle documents — the four named slots and what gates approval | URD US-27.3 |
| 5 | `assigning-drivers-and-trackers` | Assigning drivers and binding trackers | URD Epic 13 · Epic 3/5 |
| 6 | `billing` | Billing — monthly per Mode B vehicle | URD Epic 13 |

### Facts you must get right

- **Ch 2** — the payout profile captures bank / branch / account number / account holder name, plus
  uploads of the **latest bank statement or passbook first page** and the **bank-app-generated
  LankaQR code image**, and it is **Verification-Officer-approved** (US-27.1). The verified profile
  is what Mode B subscription payments route to. **"Service payment = Paid" requires a Verified
  profile** (US-27.2) — that is a hard gate and a fleet owner needs to know it before they price
  anything.
- **Ch 4** — the four named per-vehicle document slots: **registration copy (CR book), insurance
  certificate, revenue licence, route permit (Mode A required)**. AI-extracted with per-document
  status, **gating vehicle approval** (US-27.3). Insurance is mandatory for all modes.
- **Ch 5** — Fleet sub-roles are **Owner / Manager / Viewer**; say which of the six chapters each
  role can actually act on, because a Viewer reading a guide full of buttons they do not have is a
  support ticket.
- **Ch 6** — **monthly per Mode B vehicle; Mode A free; Mode C is non-fleet.** Anchor it. If a fleet
  owner also has Mode C drivers, those drivers pay the **daily** fee themselves — do not let the two
  fee models blur, which is the single most likely error in this chapter.
- **"Service payment"** is the current UI label; the Paid/Free classification was renamed in US-27.4
  (label only — API and DB names unchanged). Use the current word.

### Everything else follows the established pattern

- Same `Chapter` shape from `src/content/types.ts` (S07), `audience: 'fleet'`.
- Register in `src/content/index.ts`; add the routes to `src/lib/routes.ts`.
- `app/[locale]/guide/fleet/[chapter]/page.tsx` reuses **S17's chapter component unchanged**. If it
  needs a change to accept a third audience, that is a sign S17's component was over-fitted — fix it
  there rather than forking.
- Screens: `SCR-FP-*` entries in `src/content/screens.ts`. S05 curated few or none of these
  (it was told to skip back-office); add what you need and **re-run `npm run screens:refresh`** (S06)
  so the images exist before `check-bundle.mjs` looks for them.
- `en.ts` keys under `www.guide.f01.*`…, then Sinhala and Tamil — **the same first-pass, not-reviewed
  caveat from S12/S13 applies**, and if Tamil was deferred, these chapters follow that decision too.
- Point `/fleets`' guide entry point (S16) at `/guide/fleet` now that it exists.

---

## Fences

- **Fleet monthly and driver daily are different fee models.** Never one paragraph covering both.
- **The Verified-profile gate on Paid service payment is stated, not implied.**
- Every claim anchored; no invented fact; no literal user-facing string; no form; no API call.

---

## Verify

```
npm --prefix portals run screens:refresh --workspace @mageride/www    # if screens were added
npm --prefix portals run lint
npm --prefix portals run build --workspace @mageride/www
npm --prefix portals run test --workspace @mageride/www
node portals/www/scripts/check-i18n-parity.mjs
du -sh portals/www/public/screens                                     # still <= 12M
```

`test/routes.test.ts` and `test/content.test.ts` now cover **40 chapters**. Confirm the sitemap grew
by the right number of routes and that `/fleets` no longer links to a 404.

---

## Handoff

- **Component:** C134 www-informational-site — S23 (fleet guide, optional) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** whether S17's chapter component took a third audience without modification; screens
  added and the new `public/screens/` total; the sub-role capability read (which of Owner / Manager /
  Viewer can do what) and where the specs were silent.
