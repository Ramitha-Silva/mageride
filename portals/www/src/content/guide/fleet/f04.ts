/**
 * Fleet chapter 4 — vehicle documents: the four named slots, and what gates approval.
 *
 * ## Why this is its own chapter and not a paragraph in chapter 3
 *
 * Because it is the thing that decides whether a vehicle can earn. US-27.3: the
 * vehicle **cannot be Approved while a required document is Missing or Pending**, and
 * **expiry of any required document auto-suspends dispatch**. An owner with a
 * hundred-vehicle CSV uploaded and nothing moving is looking at a per-document status
 * chip, not at a vehicle status, and the guide should have put them there.
 *
 * ## The four slots, exactly as the spec names them
 *
 * **Registration copy (CR book), insurance certificate, revenue licence** — required
 * for *all* vehicles — and **route permit**, required for **Mode A**. Epic 27's own
 * rationale explains why the generic upload it replaced was not good enough: it was
 * "too thin to gate approval on the four documents a Sri Lankan operator actually
 * holds", the route permit among them because Mode A passenger transport legally
 * requires one. Naming the four in the reader's own paperwork terms is most of this
 * chapter's value.
 *
 * The site spells it **licence**, which is the spelling the rest of this corpus uses
 * (the driver guide and the FAQ both say "revenue licence"); the URD writes "revenue
 * license". Same document, and consistency inside the published English wins over
 * transcribing the spec's spelling.
 *
 * **Insurance is mandatory for every mode** — the URD's v2.2 conflict-resolution pass
 * settled that explicitly ("insurance mandatory for **all** modes"), so it is stated
 * rather than left to be inferred from "required for all vehicles".
 *
 * ## Extraction is a convenience, not a verdict
 *
 * Each upload is AI-extracted — registration number against the plate, expiry dates,
 * permit number and route — and each slot then carries **Verified / Pending /
 * Missing**. The chapter says what the extraction *reads* and does not promise what
 * it *decides*: the approval is the same admin workflow every vehicle on the platform
 * goes through (US-13.6), and Scenario 69's edge table has the only other outcome a
 * reader needs, which is Rejected → re-upload.
 */

import type { Chapter } from '@/content/types';

const URD_DOCS =
  'specs/user-requirements-document.md#epic-27-new-2026-07-18-change-set-fleet-portal-payout-vehicle-document-detail';
const URD_ONBOARDING = 'specs/user-requirements-document.md#13b-organisation-vehicle-onboarding';
const D1_DOC_FLOW =
  'specs/D1_mageride_user_flows.md#f-312-vehicle-onboarding-with-named-document-slots-item-2-us-273';
const WALKTHROUGH_SINGLE =
  'specs/MageRide_Functional_Walkthrough.md#scenario-69-onboarding-a-single-vehicle-document-upload-automatic-reading-status';

export const f04: Chapter = {
  id: 'f04',
  slug: 'vehicle-documents',
  audience: 'fleet',
  order: 4,
  title: 'www.guide.f04.title',
  summary: 'www.guide.f04.summary',

  steps: [
    { instruction: 'www.guide.f04.step1', screenRef: 'SCR-FP-004' },
    { instruction: 'www.guide.f04.step2', note: 'www.guide.f04.step2.note' },
    { instruction: 'www.guide.f04.step3' },
    { instruction: 'www.guide.f04.step4' },
    { instruction: 'www.guide.f04.step5' },
    { instruction: 'www.guide.f04.step6' },
    { instruction: 'www.guide.f04.step7' },
  ],

  callouts: [
    {
      kind: 'warning',
      body: 'www.guide.f04.callout.approvalIsGated',
      source: URD_DOCS,
    },
    {
      kind: 'warning',
      body: 'www.guide.f04.callout.insuranceEveryMode',
      source: URD_ONBOARDING,
    },
    {
      kind: 'warning',
      body: 'www.guide.f04.callout.expiryStopsDispatch',
      source: URD_DOCS,
    },
  ],

  screens: ['SCR-FP-004'],
  relatedChapters: ['f03', 'f05', 'f01'],
  faqRefs: ['vehicle-types'],

  sources: [
    URD_DOCS,
    URD_ONBOARDING,
    D1_DOC_FLOW,
    WALKTHROUGH_SINGLE,
    'specs/D2_mageride_ui_spec.md#scr-fp-004-fleetvehicleonboarding-vehicle-onboarding-new-us-131136-us-273274',
  ],
};
