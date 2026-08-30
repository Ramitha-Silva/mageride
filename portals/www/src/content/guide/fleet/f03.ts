/**
 * Fleet chapter 3 — adding vehicles, one at a time and in bulk.
 *
 * ## The three rules that decide what an owner may even type
 *
 * **A fleet runs Mode A and Mode B vehicles only.** Walkthrough Scenario 69's edge
 * table is blunt about it — "Mode C attempted → Not allowed for fleets (Mode A/B
 * only)" — and URD §13.G says the same from the billing side. It is stated in this
 * chapter rather than only in chapter 6, because the moment it matters is the moment
 * an owner is choosing a mode in a form.
 *
 * **A Mode B vehicle needs a Service payment setting** — Free or Paid, with a default
 * monthly fare for Paid (US-13.1b). **Mode A skips it**: public transport is free.
 *
 * **Paid requires a Verified payout profile** (US-13.1b closing clause, US-27.2).
 * Chapter 2 is where that is explained; it is repeated here because this is the form
 * where the option will be refused.
 *
 * ## "Service payment" is the current label and the old one still exists in the wild
 *
 * US-27.4 renamed the Mode B **Paid/Free** setting from "Mode B classification" to
 * **"Service payment"**, and is explicit that this is a *UI and docs label only* —
 * the API path (`/classification`) and the DB column (`mode_b_billing`) are
 * intentionally unchanged. The guide uses the current word and says the old one once,
 * because an owner who was shown the portal before the rename, or who reads an older
 * document, needs to know the two are one thing. It does not mention the API path or
 * the column: those are true, and they are not this reader's business.
 *
 * ## Bulk CSV is a validation loop, not an upload
 *
 * US-13.1 reuses Epic 3's bulk-onboarding validation and its **downloadable error
 * report**; Scenario 70 walks the loop — upload, flagged rows, download the report,
 * fix and re-upload *only* those rows. And US-27.3's last sentence is the one that
 * saves an owner an afternoon: **bulk rows are created "Docs pending"**, so a
 * successful CSV is not an approved fleet. The documents are chapter 4.
 */

import type { Chapter } from '@/content/types';

const URD_ONBOARDING = 'specs/user-requirements-document.md#13b-organisation-vehicle-onboarding';
const URD_DOCS =
  'specs/user-requirements-document.md#epic-27-new-2026-07-18-change-set-fleet-portal-payout-vehicle-document-detail';
const URD_BILLING = 'specs/user-requirements-document.md#13g-fleet-billing';
const WALKTHROUGH_SINGLE =
  'specs/MageRide_Functional_Walkthrough.md#scenario-69-onboarding-a-single-vehicle-document-upload-automatic-reading-status';
const WALKTHROUGH_BULK =
  'specs/MageRide_Functional_Walkthrough.md#scenario-70-bulk-onboarding-vehicles-via-csv-validation-and-error-report';

export const f03: Chapter = {
  id: 'f03',
  slug: 'adding-vehicles',
  audience: 'fleet',
  order: 3,
  title: 'www.guide.f03.title',
  summary: 'www.guide.f03.summary',

  steps: [
    { instruction: 'www.guide.f03.step1', screenRef: 'SCR-FP-004' },
    { instruction: 'www.guide.f03.step2', note: 'www.guide.f03.step2.note' },
    { instruction: 'www.guide.f03.step3' },
    { instruction: 'www.guide.f03.step4', note: 'www.guide.f03.step4.note' },
    { instruction: 'www.guide.f03.step5' },
    { instruction: 'www.guide.f03.step6' },
    { instruction: 'www.guide.f03.step7' },
    { instruction: 'www.guide.f03.step8' },
  ],

  callouts: [
    {
      kind: 'warning',
      body: 'www.guide.f03.callout.modeAandBOnly',
      source: URD_BILLING,
    },
    {
      kind: 'warning',
      body: 'www.guide.f03.callout.paidNeedsVerifiedProfile',
      source: URD_ONBOARDING,
    },
    {
      kind: 'tip',
      body: 'www.guide.f03.callout.renamedLabel',
      source: URD_DOCS,
    },
  ],

  screens: ['SCR-FP-004'],
  relatedChapters: ['f04', 'f02', 'f06'],
  faqRefs: ['vehicle-types', 'modes'],

  sources: [
    URD_ONBOARDING,
    URD_DOCS,
    URD_BILLING,
    WALKTHROUGH_SINGLE,
    WALKTHROUGH_BULK,
    'specs/D2_mageride_ui_spec.md#scr-fp-004-fleetvehicleonboarding-vehicle-onboarding-new-us-131136-us-273274',
  ],
};
