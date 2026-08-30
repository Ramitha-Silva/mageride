/**
 * Driver chapter 13 — the daily platform fee.
 *
 * **This is the most consequential chapter on the site.** Everything else a driver
 * reads here changes how they use an app; this one changes whether they can make a
 * living on it, and it is the chapter a driver will quote back to MageRide. So it is
 * written from URD §1 and Epic 9 in the URD's own words wherever the URD's words
 * work, and it states no number that is not asserted by a test.
 *
 * ## The rule, as URD §1 states it
 *
 * > *"The platform operates on a **zero-commission model** that mirrors **Namma
 * > Yatri (India)** — drivers keep **100% of passenger fares**. For Mode C (Standby
 * > On-Demand) drivers, the **first trip of the day is always free**; from the 2nd
 * > trip, a flat **daily platform fee** (vehicle-type dependent) is auto-deducted
 * > from their wallet. Mode A (Public Transport) buses pay **no daily fee**. Mode B
 * > (Private Transport) vehicles pay a **monthly charge of approximately Rs 300**.
 * > **No per-trip fee, no commission.**"*
 *
 * The URD states the six amounts **four times and all four agree** — §1, US-9.1, the
 * Daily Platform Fee Structure table, and the glossary — which is why they can be
 * asserted rather than trusted. `src/content/marketing.ts` records that audit beside
 * the constant.
 *
 * ## Not one rupee figure is written in this chapter's prose
 *
 * `{@link Chapter.table} = 'daily-fee-tiers'` is how the numbers reach the page:
 * {@link ../../marketing.ts `DAILY_FEE_TIERS`} in minor units, keyed by the
 * canonical `vehicle_type`, with {@link ../../marketing.ts `MODE_A_FEE_KEY`} and
 * {@link ../../marketing.ts `MODE_B_FEE_KEY`} beside it so the six are not read as
 * "what everybody pays". `test/content.test.ts` holds all of it to URD §1.
 *
 * That is not tidiness. A rupee amount typed into `en.ts` becomes three amounts once
 * `si.ts` and `ta.ts` are written, in a file the fee test does not read, and the
 * first one to drift is a **commercial claim to a driver deciding how to earn**.
 * **Mode B's own figure is not inlined here either** — S07 renders it as "about
 * Rs 300 a month" because the URD says *approximately* in both places it states it,
 * and it belongs to that key, not to this chapter's prose.
 *
 * ## The four things a driver gets wrong about the fee, and the copy for each
 *
 * - **It is charged once a day, not once a trip.** US-9.4 — "a single flat charge
 *   per day regardless of how many trips I complete". The deduction lands *before
 *   the second trip*, which makes it look like a per-trip charge exactly once.
 * - **A day you do not go online costs nothing.** US-9.1's closing sentence and the
 *   fee table's own "No charge on off days" column, repeated on all six rows.
 * - **An empty wallet does not produce an error.** The request is *missed* and a
 *   notification says why (US-9.1). Chapter 7 states the same thing from the standby
 *   side; here it is the reason the balance matters at all.
 * - **The rate follows the vehicle, and one vehicle is live at a time** (US-9.6).
 *   A driver with a motorbike and a van pays the rate of whichever is selected, is
 *   never charged twice for the same vehicle on the same day, and changes what they
 *   owe by changing what they drive.
 *
 * ## The honest qualifier
 *
 * **"All rates admin-configurable"** is the fee table's own last line. The table
 * published here is what the specification states today, not a price promise, and
 * the app always shows the rate for the vehicle that is actually live. Publishing
 * the six without that sentence would be publishing a tariff.
 */

import type { Chapter } from '@/content/types';

const URD_VISION = 'specs/user-requirements-document.md#1-product-vision';
const URD_FEES = 'specs/user-requirements-document.md#epic-9-daily-platform-fee-billing';
const URD_FEE_TABLE =
  'specs/user-requirements-document.md#daily-platform-fee-structure-namma-yatri-methodology';

export const d13: Chapter = {
  id: 'd13',
  slug: 'the-daily-platform-fee',
  audience: 'driver',
  order: 13,
  title: 'www.guide.d13.title',
  summary: 'www.guide.d13.summary',

  steps: [
    { instruction: 'www.guide.d13.step1' },
    { instruction: 'www.guide.d13.step2' },
    { instruction: 'www.guide.d13.step3', note: 'www.guide.d13.step3.note' },
    { instruction: 'www.guide.d13.step4', screenRef: 'SCR-DA-021' },
    { instruction: 'www.guide.d13.step5' },
    { instruction: 'www.guide.d13.step6', note: 'www.guide.d13.step6.note' },
    { instruction: 'www.guide.d13.step7', screenRef: 'SCR-DA-021' },
    { instruction: 'www.guide.d13.step8', screenRef: 'SCR-DA-025' },
  ],

  callouts: [
    {
      kind: 'fee',
      body: 'www.guide.d13.callout.zeroCommission',
      source: URD_VISION,
    },
    {
      kind: 'fee',
      body: 'www.guide.d13.callout.oncePerDay',
      source: URD_FEE_TABLE,
    },
    {
      kind: 'warning',
      body: 'www.guide.d13.callout.ratesAreConfigurable',
      source: URD_FEE_TABLE,
    },
  ],

  screens: ['SCR-DA-021', 'SCR-DA-025'],
  table: 'daily-fee-tiers',
  relatedChapters: ['d12', 'd07', 'd14'],
  faqRefs: ['daily-fee', 'fee-off-days', 'driver-keeps', 'why-free'],

  sources: [
    URD_VISION,
    URD_FEES,
    URD_FEE_TABLE,
    'specs/user-requirements-document.md#1-a-service-modes',
    'specs/user-requirements-document.md#2-2-admin-portal-authentication',
    'specs/MageRide_Functional_Walkthrough.md#scenario-42-viewing-wallet-balance-and-the-daily-fee-deduction-logic',
    'specs/D5_mageride_business_logic.md#2-daily-platform-fee-replace-ny-juspay-subscription-mandate-gst-wallet-daily-fee',
    'specs/D2_mageride_ui_spec.md#scr-da-021-scr-di-021-wallet_fee-wallet-fee-status-primary-replace-ny-juspay-subscription-wallet',
  ],
};
