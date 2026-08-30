# C134 · S11 — driver guide, chapters 10–18 (English)

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 11 of 22 · Phase 4 (Content), part 5 of 7.**

**Prerequisite:** S10. Same shape, same sources, same fences.

---

## Do this — nine chapters, `src/content/guide/driver/d10…d18.ts`

| # | slug | Chapter | Primary source |
|---|---|---|---|
| 10 | `directional-travel` | Directional travel — destination, daily uses, max duration | D2 SCR-DA · DT-01..08 |
| 11 | `package-jobs` | Package jobs — the three stages, proof of delivery | URD Epic 20 |
| 12 | `your-wallet` | Your wallet — top-up by card, OnePay, LankaQR | URD Epic 9A |
| 13 | `the-daily-platform-fee` | The daily platform fee — **first trip free**, the six tiers | URD §1 · Epic 9 |
| 14 | `getting-paid` | Getting paid — cash and driver-QR settlement | URD Epic 26 |
| 15 | `bulk-credit-and-transfers` | Bulk credit and transferring to other drivers | URD §2.2 (AL-01) |
| 16 | `mode-a-and-b-driving` | Mode A/B driving — journeys, ignition-started trips, trackers | URD Epic 3 / 5 |
| 17 | `ratings-and-driver-level` | Ratings, driver level, and what affects the offers you get | URD Epic 18 · reputation-svc |
| 18 | `safety-and-support` | Safety, emergency contact, support, app updates | URD Epic 12 / 16 / 17 · AL-13 |

### Chapter-specific facts you must get right

- **Ch 12** — top-up methods are **card, OnePay and LankaQR**. **Bank Transfer was removed** as a
  top-up method (URD v2.2 conflict-resolution pass) — do not list it. **QR scanning was removed from
  the driver credit-request flow** (US-9.10).
- **Ch 13 — the most important chapter on the site for a driver.** The model is **zero commission**:
  passengers pay fares directly to drivers, and MageRide charges Mode C drivers a **flat daily fee**
  with the **first trip of the day always free**; Mode B drivers pay **monthly**; **Mode A (public
  buses) pay no fee**. There are **six vehicle-type tiers**. Put the six values in the single
  exported constant S07 created, with its URD anchor — `test/content.test.ts` asserts them against
  URD §1, so a drifted number is a red build rather than a public error. State the rule exactly as
  URD §1 states it, in the same words where you can.
- **Ch 14** — cash is direct to the driver. **Driver-QR settles by attestation**: passenger claims
  paid → driver confirms received → `DriverConfirmedQR`; **no gateway callback exists** for a payment
  into the driver's own bank QR, and disputes route to Support/Finance (US-26.1). If you describe a
  weekly payout, note that `payout-svc` (C133) is the rail for **wallet balances**, not for cash
  fares — a driver who thinks their cash rides arrive in a weekly payout has been misled.
- **Ch 16** — a **GPS device started by ignition auto-starts the journey, and the dashboard can
  override it** (US-5.11). Trackers are provisioned devices, not the phone; say what a driver does
  and does not have to do.
- **Ch 17** — reputation-svc drives driver level. Only describe an effect on dispatch if the specs
  state one. **If they do not, say ratings are visible to passengers and leave it there** — inventing
  a dispatch consequence is both a false claim and a bad incentive.
- **Ch 18** — emergency contact, SOS, in-app support, and how app updates arrive (AL-13).

### Every chapter

Same rules as S08/S10. When this session ends the **driver guide is 18 chapters complete in
English**, and the combined corpus is **34 chapters** — the Definition of Done's "34+ chapters cover
every passenger and driver capability across URD Epics 1–27".

**Before finishing, run the coverage read:** list URD Epics 1–27 and, for each, name the chapter
that covers it. Any epic with a user-facing capability and no chapter is a gap — record it in the
handoff, and if it is material, add a chapter rather than leaving it.

---

## Fences

- **The six fee tiers live in one constant with one anchor.** Never inline a rupee figure in prose.
- **No payout promise that `payout-svc` does not implement.**
- **No dispatch-consequence claim for ratings unless a spec states it.**
- **Bank Transfer is not a top-up method.**

---

## Verify

```
npm --prefix portals run lint
npm --prefix portals run test --workspace @mageride/www
node portals/www/scripts/check-i18n-parity.mjs
```

Plus the epic-coverage read above, written into the handoff.

---

## Handoff

- **Component:** C134 www-informational-site — S11 (driver guide 10–18) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** the URD Epic 1–27 → chapter coverage table; the six fee tiers and their anchor; total
  English word count across all 34 chapters (this is the input to S12/S13's estimate).
