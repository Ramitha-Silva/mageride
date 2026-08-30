/**
 * Fleet chapter 5 — assigning drivers, and binding trackers.
 *
 * ## Two ways a fleet vehicle reports, and an owner picks one per vehicle
 *
 * A driver with the Driver App (US-13.2 / US-13.9), or an **ST-901 hardware tracker**
 * bound to the vehicle, for which "journey start/end is handled automatically … no
 * manual Start/End in the Driver App required" (US-13.12). They are one chapter
 * because the choice is made once, per vehicle, at the same point in setting the
 * fleet up — and because an owner who binds a tracker and then wonders why nobody has
 * to press Start has read half of one decision.
 *
 * ## The sub-role table lives here, and it is the whole reason the chapter is not
 * called "assigning drivers"
 *
 * S23: *"say which of the six chapters each role can actually act on, because a
 * Viewer reading a guide full of buttons they do not have is a support ticket."*
 * {@link f05.callouts}'s first entry is that sentence, and it is assembled from two
 * sources that agree:
 *
 * - **US-13.A5** defines the three — Owner = full org control **+ billing**;
 *   Manager = onboarding / assignment / scheduling / monitoring, **no billing and no
 *   owner changes**; Viewer = **read-only fleet map & analytics**.
 * - **`specs/wireframes/web_fleet.html` tags each frame's caption with its roles**,
 *   which resolves the six chapters screen by screen: SCR-FP-002 and SCR-FP-002a are
 *   `Owner`; SCR-FP-004, 005 and 006 are `Owner / Manager`; SCR-FP-010 (billing) is
 *   `Owner`.
 *
 * So: **Owner** can act on all six chapters. **Manager** can act on chapters 3, 4 and
 * 5 and not on 2 or 6. **Viewer** can act on none of them — a Viewer's portal is the
 * live map and the analytics, which this guide does not document because there is
 * nothing to get right once about reading a map.
 *
 * **Where the specs are silent**, and the copy therefore does not say: whether a
 * Viewer can *see* (as opposed to change) the billing page or the subscriber ledger.
 * US-13.A5 grants "read-only fleet map & analytics" and the wireframe leaves
 * SCR-FP-003, 007 and 009 untagged, which is consistent with either reading. The
 * chapter states what each role can **do**, which is what a reader is deciding.
 *
 * ## Revocation mid-trip is stated as the spec states it, and no further
 *
 * US-13.8: an active session "is allowed to complete or is force-ended per operator
 * policy". Walkthrough Scenario 72 flags the exact on-screen outcome as an open
 * question — "neither specifies the exact on-screen outcome if revoked mid-trip
 * (graceful finish vs immediate cut-off)". So the guide says the driver immediately
 * loses the ability to start **new** sessions, which every source agrees on, and does
 * not describe a screen nobody has specified.
 */

import type { Chapter } from '@/content/types';

const URD_ASSIGNMENT = 'specs/user-requirements-document.md#13c-driver-assignment-behaviour';
const URD_ACCESS = 'specs/user-requirements-document.md#13a-fleet-portal-access-authentication';
const URD_TRACKERS =
  'specs/user-requirements-document.md#13f-hardware-tracker-st-901-binding-auto-sessions';
const WALKTHROUGH_ASSIGN =
  'specs/MageRide_Functional_Walkthrough.md#scenario-71-assigning-a-driver-to-a-fleet-vehicle-by-phone-user-id';
const WALKTHROUGH_REVOKE =
  'specs/MageRide_Functional_Walkthrough.md#scenario-72-revoking-a-driver-assignment-effect-on-an-active-session';
const WALKTHROUGH_BIND =
  'specs/MageRide_Functional_Walkthrough.md#scenario-73-binding-an-st-901-gps-tracker-to-a-vehicle-imeimac';
const WALKTHROUGH_CADENCE =
  'specs/MageRide_Functional_Walkthrough.md#scenario-74-configuring-tracker-publish-cadence-active-hours-vs-off-hours';

export const f05: Chapter = {
  id: 'f05',
  slug: 'assigning-drivers-and-trackers',
  audience: 'fleet',
  order: 5,
  title: 'www.guide.f05.title',
  summary: 'www.guide.f05.summary',

  steps: [
    { instruction: 'www.guide.f05.step1', note: 'www.guide.f05.step1.note', screenRef: 'SCR-FP-005' },
    { instruction: 'www.guide.f05.step2' },
    { instruction: 'www.guide.f05.step3' },
    { instruction: 'www.guide.f05.step4', note: 'www.guide.f05.step4.note' },
    { instruction: 'www.guide.f05.step5', screenRef: 'SCR-FP-006' },
    { instruction: 'www.guide.f05.step6', note: 'www.guide.f05.step6.note' },
    { instruction: 'www.guide.f05.step7' },
  ],

  callouts: [
    {
      kind: 'tip',
      body: 'www.guide.f05.callout.whoCanDoWhat',
      source: URD_ACCESS,
    },
    {
      kind: 'warning',
      body: 'www.guide.f05.callout.revoking',
      source: URD_ASSIGNMENT,
    },
    {
      kind: 'privacy',
      body: 'www.guide.f05.callout.scopedToYourOrg',
      source: URD_ASSIGNMENT,
    },
  ],

  screens: ['SCR-FP-005', 'SCR-FP-006'],
  relatedChapters: ['f03', 'f04', 'f06'],
  faqRefs: ['become-a-driver'],

  sources: [
    URD_ASSIGNMENT,
    URD_ACCESS,
    URD_TRACKERS,
    WALKTHROUGH_ASSIGN,
    WALKTHROUGH_REVOKE,
    WALKTHROUGH_BIND,
    WALKTHROUGH_CADENCE,
    'specs/D2_mageride_ui_spec.md#scr-fp-005-fleetdrivers-driver-assignment-new-us-132138',
    'specs/D2_mageride_ui_spec.md#scr-fp-006-fleettrackers-tracker-binding-new-us-1312',
  ],
};
