/**
 * Driver chapter 15 — bulk credit, and transferring to another driver.
 *
 * URD §2.2, Epic 9 (US-9.10–9.21), Epic 9A (US-9A.8–9A.12), the **Bulk Credit
 * Vouchers** and **Driver-to-Driver Credit Transfer Flow** tables, and Walkthrough
 * scenarios 44–47.
 *
 * ## "Reseller" is not a thing, and the chapter has to say so out loud
 *
 * **AL-01 is the whole of this chapter.** *"Reseller is not a separate role, account,
 * or enabled capability — it is simply any driver who has purchased bulk credit and
 * transfers it to other drivers in the Driver App."* `iam`'s role enum has no
 * `reseller`, `billing.accounts.owner_type` has no `reseller`, and
 * `billing.resellers` became `billing.credit_transfers`.
 *
 * That matters to a reader because the *old* arrangement is what a driver will have
 * heard about from another driver: a reseller account, a reseller code, a commission
 * on the transfer. All three are gone, and each is a thing somebody could be talked
 * out of money with. So the copy states the three negatives explicitly — **no
 * separate account, no reseller code, no commission** — rather than only describing
 * what does exist.
 *
 * ## Two numbers, and only one of them is quotable
 *
 * - **The denominations are fixed and stated**: Rs 1,000 / 2,000 / 3,000 / 5,000 /
 *   10,000, in the URD four times and in the drawn frame.
 * - **The discount percentage is not.** It is *"configured per tier in the
 *   database"*, *"variable/admin-configurable"*, and the wireframe's own tiles are
 *   labelled *"admin-set, variable"*. Publishing a percentage would be publishing a
 *   price MageRide has not set. The chapter says larger denominations typically earn
 *   a higher discount, which is what the URD says, and shows no figure.
 *
 * (S11's fence — *never inline a rupee figure in prose* — is read as scoped to the
 * sentence it sits under, **the six fee tiers**, which are asserted by
 * `test/content.test.ts` and must have exactly one home. The five denominations are
 * a different fact with a different anchor, and chapters 7 and 9 already carry
 * anchored figures of the same kind. Recorded in the S11 handoff so the next session
 * can overturn the reading deliberately rather than by accident.)
 *
 * ## QR scanning is gone from the request flow — and D2 has not caught up
 *
 * **US-9.10: *"QR scanning is not required (removed)"***, 2026-06-25 item 10. The
 * Walkthrough agrees (*"no QR scan"*, scenario 44) and so does the drawn frame,
 * whose own card reads *"Enter the Driver ID … **No QR scan**, no special reseller
 * codes"*. **`specs/D2_mageride_ui_spec.md`'s SCR-DA-023 line still says "or scan
 * their QR" and still lists a QR-scanner component** — a stale line in one document
 * against three current ones. The guide follows the URD, and the drift is recorded
 * in the S11 handoff.
 *
 * ## The transfer itself
 *
 * Exact value, both directions, no commission (US-9.13, US-9.21) — *"the exact
 * transfer amount is debited from the sender and credited to the recipient"*, both
 * sides in the double-entry ledger. Either party can start it: **request** by
 * entering the holder's Driver ID (they approve or reject by push), or **send**
 * directly by Driver ID. An insufficient balance blocks the transfer.
 *
 * And the safety line, which is the Walkthrough's own advice rather than this
 * guide's invention: **verify a Driver ID out of band before approving**. A credit
 * transfer is irreversible money moving to a stranger's wallet on the strength of an
 * identifier typed into a phone.
 */

import type { Chapter } from '@/content/types';

const URD_FEES = 'specs/user-requirements-document.md#epic-9-daily-platform-fee-billing';
const URD_WALLET =
  'specs/user-requirements-document.md#epic-9a-in-app-driver-wallet-admin-portal-reconciliation';
const URD_VOUCHERS = 'specs/user-requirements-document.md#bulk-credit-vouchers-in-app';
const URD_TRANSFERS = 'specs/user-requirements-document.md#driver-to-driver-credit-transfer-flow';

export const d15: Chapter = {
  id: 'd15',
  slug: 'bulk-credit-and-transfers',
  audience: 'driver',
  order: 15,
  title: 'www.guide.d15.title',
  summary: 'www.guide.d15.summary',

  steps: [
    { instruction: 'www.guide.d15.step1' },
    { instruction: 'www.guide.d15.step2', note: 'www.guide.d15.step2.note' },
    { instruction: 'www.guide.d15.step3' },
    { instruction: 'www.guide.d15.step4', note: 'www.guide.d15.step4.note', screenRef: 'SCR-DA-023' },
    { instruction: 'www.guide.d15.step5', screenRef: 'SCR-DA-024' },
    { instruction: 'www.guide.d15.step6', screenRef: 'SCR-DA-024' },
    { instruction: 'www.guide.d15.step7' },
    { instruction: 'www.guide.d15.step8' },
  ],

  callouts: [
    {
      kind: 'fee',
      body: 'www.guide.d15.callout.noCommission',
      source: URD_TRANSFERS,
    },
    {
      kind: 'warning',
      body: 'www.guide.d15.callout.noResellerAccount',
      source: URD_WALLET,
    },
    {
      kind: 'warning',
      body: 'www.guide.d15.callout.checkTheDriverId',
      source: URD_FEES,
    },
  ],

  screens: ['SCR-DA-023', 'SCR-DA-024'],
  relatedChapters: ['d12', 'd13'],
  faqRefs: ['wallet-topup', 'daily-fee'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-44-requesting-credit-from-another-reseller-capable-driver',
    'specs/MageRide_Functional_Walkthrough.md#scenario-45-a-reseller-driver-approving-or-rejecting-an-incoming-credit-request',
    'specs/MageRide_Functional_Walkthrough.md#scenario-46-reseller-driver-purchasing-bulk-vouchers-at-a-tiered-discount',
    'specs/user-requirements-document.md#2-2-admin-portal-authentication',
    URD_FEES,
    URD_WALLET,
    URD_VOUCHERS,
    URD_TRANSFERS,
    'specs/architecture-design-document.md#1-8-remediation-log-al-01-al-16',
    'specs/D2_mageride_ui_spec.md#scr-da-023-scr-di-023-request_credit-request-credit-driver-id-new-us-9-10',
    'specs/D2_mageride_ui_spec.md#scr-da-024-scr-di-024-credit_transfer-credit-transfer-requests-new-us-9-11-9-12-9-20-9-21',
  ],
};
