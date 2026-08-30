/**
 * Passenger chapter 5 — following a private vehicle (Mode B).
 *
 * Mode B is **follow-with-permission**, and every sentence in this chapter is built on
 * that: the passenger asks for a named vehicle, the owner or its assigned driver
 * decides, and nothing is visible before they accept (URD Epic 4; D-23 — the map adds
 * the vehicle only once a non-expired grant exists; D-22 — a revocation removes it in
 * under 200 ms, which the copy renders as "at once" rather than quoting a millisecond
 * budget at a member of the public).
 *
 * Two details a public page owes a reader and that are easy to leave out:
 *
 * - **Grants are per vehicle** (US-4.10), not per operator. A fleet with six vans
 *   handles six queues, which is what a parent asking for one van needs to know.
 * - **The request is not anonymous** (US-4.4): the owner sees the requester's name,
 *   mobile number and passenger ID before deciding. That is a disclosure, so it is a
 *   callout rather than a step buried in the middle.
 *
 * **Known gap, not published:** the Walkthrough records that the specs state no expiry
 * for an unanswered request — what a passenger sees if the owner never responds is
 * undefined. The chapter therefore describes Pending, Accepted and Rejected and makes
 * no promise about a timeout.
 */

import type { Chapter } from '@/content/types';

const URD_SHARING = 'specs/user-requirements-document.md#epic-4';

export const p05: Chapter = {
  id: 'p05',
  slug: 'following-a-private-vehicle',
  audience: 'passenger',
  order: 5,
  title: 'www.guide.p05.title',
  summary: 'www.guide.p05.summary',

  steps: [
    { instruction: 'www.guide.p05.step1', screenRef: 'SCR-PA-024' },
    { instruction: 'www.guide.p05.step2', screenRef: 'SCR-PA-024' },
    { instruction: 'www.guide.p05.step3' },
    { instruction: 'www.guide.p05.step4', screenRef: 'SCR-PA-024' },
    { instruction: 'www.guide.p05.step5' },
    { instruction: 'www.guide.p05.step6', screenRef: 'SCR-PA-025' },
    { instruction: 'www.guide.p05.step7' },
    { instruction: 'www.guide.p05.step8' },
  ],

  callouts: [
    {
      kind: 'privacy',
      body: 'www.guide.p05.callout.permission',
      source: URD_SHARING,
    },
    {
      kind: 'privacy',
      body: 'www.guide.p05.callout.identified',
      source: URD_SHARING,
    },
  ],

  screens: ['SCR-PA-024', 'SCR-PA-025'],
  relatedChapters: ['p03', 'p06'],
  faqRefs: ['mode-b-access', 'modes'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-12-requesting-access-to-a-private-mode-b-vehicle-school-van-office-bus',
    'specs/MageRide_Functional_Walkthrough.md#scenario-13-tracking-an-approved-mode-b-vehicle-on-the-map',
    URD_SHARING,
    'specs/D1_mageride_user_flows.md#f-23-3-mode-b-subscribe-pay',
  ],
};
