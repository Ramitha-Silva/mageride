/**
 * Driver chapter 12 — your wallet.
 *
 * URD Epic 9A end to end, the **Wallet Top-Up Methods** table, and Walkthrough
 * scenario 43. Screens SCR-DA-021 (wallet & fee) and SCR-DA-022 (top up).
 *
 * ## What the wallet is *for*, before what it does
 *
 * A driver meeting this screen for the first time will read a balance and assume it
 * is their earnings. It is not, and the chapter opens by saying so: **the wallet is
 * a prepaid balance that the daily platform fee comes out of.** A cash fare is
 * handed to the driver and never touches it; a fare paid into the driver's own bank
 * QR never touches it either. Chapter 14 owns the whole of that and this chapter
 * points at it rather than restating it — but the *pointer* is not optional, because
 * a driver who thinks their fares accumulate here will wait for money that is
 * already in their pocket.
 *
 * ## Three top-up methods, and Bank Transfer is not one of them
 *
 * **Card, OnePay and LankaQR** (US-9A.4, US-9.18), all in the app, all crediting
 * instantly. **Bank transfer was removed** — ADD AL-05 dropped it, D1/D2/D6 record
 * it, `billing.bank_transfer_topups` was deleted from the schema, and the wireframe
 * draws three methods. It is listed as a *negative* in the copy for one reason: a
 * driver who has been told by another driver that they can pay a MageRide bank
 * account will otherwise send money to nowhere.
 *
 * The URD's own top-up table carries the surcharge column and the chapter carries it
 * too: **LankaQR has no surcharge; card and OnePay carry OnePay's processing fee.**
 * No percentage is quoted — the table does not state one, and inventing a figure for
 * a public page is the exact failure README rule 7 exists to prevent.
 *
 * ## Why "in the app" is repeated, and is not filler
 *
 * Epic 9A's rationale sentence is *"Drivers never access the web portal"*, and it is
 * repeated in five stories. It is a **security fact** for the reader, not a product
 * boast: there is no driver login to any MageRide website, so a page asking a driver
 * to sign in with their MageRide account to top up is a phishing page. Stated
 * plainly, once.
 *
 * ## The balance can go negative
 *
 * US-9A.7's parenthesis, and it is honest to publish: an admin reversal or a
 * post-acceptance cancellation debit can take a balance below zero, and the
 * "Top Up Required" banner then shows the amount needed to get back to serviceable.
 * A driver who sees a minus sign and has never been warned will assume a bug.
 *
 * The **six per-vehicle amounts are chapter 13's** and appear nowhere here;
 * `DAILY_FEE_TIERS` is their single home.
 */

import type { Chapter } from '@/content/types';

const URD_WALLET =
  'specs/user-requirements-document.md#epic-9a-in-app-driver-wallet-admin-portal-reconciliation';
const URD_TOPUP = 'specs/user-requirements-document.md#wallet-top-up-methods';
const URD_FEES = 'specs/user-requirements-document.md#epic-9-daily-platform-fee-billing';

export const d12: Chapter = {
  id: 'd12',
  slug: 'your-wallet',
  audience: 'driver',
  order: 12,
  title: 'www.guide.d12.title',
  summary: 'www.guide.d12.summary',

  steps: [
    { instruction: 'www.guide.d12.step1', note: 'www.guide.d12.step1.note', screenRef: 'SCR-DA-021' },
    { instruction: 'www.guide.d12.step2', screenRef: 'SCR-DA-021' },
    { instruction: 'www.guide.d12.step3', screenRef: 'SCR-DA-022' },
    {
      instruction: 'www.guide.d12.step4',
      note: 'www.guide.d12.step4.note',
      screenRef: 'SCR-DA-022',
    },
    { instruction: 'www.guide.d12.step5', screenRef: 'SCR-DA-022' },
    { instruction: 'www.guide.d12.step6', screenRef: 'SCR-DA-021' },
    { instruction: 'www.guide.d12.step7' },
    { instruction: 'www.guide.d12.step8' },
  ],

  callouts: [
    {
      kind: 'warning',
      body: 'www.guide.d12.callout.noBankTransfer',
      source: URD_TOPUP,
    },
    {
      kind: 'privacy',
      body: 'www.guide.d12.callout.neverAWebPortal',
      source: URD_WALLET,
    },
    {
      kind: 'fee',
      body: 'www.guide.d12.callout.whatTheWalletIsFor',
      source: URD_FEES,
    },
  ],

  screens: ['SCR-DA-021', 'SCR-DA-022'],
  relatedChapters: ['d13', 'd15', 'd14'],
  faqRefs: ['wallet-topup', 'daily-fee', 'driver-keeps'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-42-viewing-wallet-balance-and-the-daily-fee-deduction-logic',
    'specs/MageRide_Functional_Walkthrough.md#scenario-43-topping-up-the-wallet-onepay-and-lankaqr-no-bank-transfer',
    URD_WALLET,
    URD_TOPUP,
    URD_FEES,
    'specs/D2_mageride_ui_spec.md#scr-da-021-scr-di-021-wallet_fee-wallet-fee-status-primary-replace-ny-juspay-subscription-wallet',
    'specs/D2_mageride_ui_spec.md#scr-da-022-scr-di-022-wallet_topup-top-up-wallet-replace-payment-hard-rule',
    'specs/architecture-design-document.md#6-service-catalogue-wallet-svc',
  ],
};
