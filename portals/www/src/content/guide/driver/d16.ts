/**
 * Driver chapter 16 — driving Mode A and Mode B.
 *
 * URD Epic 5 (bus sessions), Epic 3 §3.D (tracker behaviour per mode), Epic 4
 * (sharing grants) and US-13.9 (fleet assignment). Screens SCR-DA-011,
 * SCR-DA-027 and SCR-DA-028; Walkthrough scenarios 40, 41 and 52.
 *
 * ## A journey, not a hire — and no fee
 *
 * Mode A and Mode B have **no ride offer, no fifteen seconds and no fare**. What a
 * driver does is start a journey and end it. **Mode A pays no daily fee at all**
 * (US-5.10 says so in as many words for bus drivers) and Mode B is billed monthly to
 * the vehicle's owner, not per day to the driver. Chapter 6 owns which home screen
 * appears; this chapter is what happens on it.
 *
 * The session rules worth publishing, each anchored: **auto-end after 30 minutes
 * idle** with a push saying why (US-5.3 / US-5.9), a **5-minute grace to restart**
 * (US-5.10), and an optional **auto-end at destination** within 100 m of the previous
 * journey's end position (US-5.4).
 *
 * ## The tracker section answers "what do I have to do", because that is the question
 *
 * S11's brief asks for exactly this, and the specs support a clean answer.
 *
 * **A paired tracker is the single active publisher** (US-3.6): the phone stops
 * sending GPS for that vehicle entirely. On **Mode A and Mode B** the journey
 * **starts and ends on ignition** — ACC on starts it, ACC off or an idle timeout ends
 * it — and **the mobile app is not needed** (US-3.22 / US-3.23). Open the app after
 * the engine has been running and the dashboard already reads *Journey started*.
 *
 * **And the dashboard overrides the device** (US-5.12): the driver may Start or End
 * manually whatever the tracker is doing. That sentence is the one a driver needs on
 * the morning the device is wrong, and without it the reasonable conclusion is that
 * the box is in charge.
 *
 * **On Mode C the same tracker behaves differently** — its GPS is ingested **only
 * while the driver is Online** (US-3.21) — which is worth one line here because a
 * driver who runs a tuk on standby and a school van on Mode B has one device and two
 * behaviours.
 *
 * Trackers are **provisioned devices, not the phone**: pair by IMEI, by scanning the
 * device's QR, or by an admin-issued bind code. A duplicate IMEI quarantines **both**
 * bindings for admin review (US-3.4) — an anti-cloning rule, and the honest reason a
 * driver's own device might stop working. A tracker offline for more than 15 minutes
 * during a session that should be active raises a push (US-3.14). Fleets of thousands
 * are a portal CSV upload (US-3.2) and never a driver's job.
 *
 * ## Sharing is per vehicle, and shows the requester's number
 *
 * US-4.10: requests appear **under the particular vehicle they target**, never mixed
 * across vehicles — because a driver may hold several, or be temporarily assigned
 * one. Incoming requests and current grantees both show the passenger's **name and
 * mobile number** (US-4.4 / US-4.7), which is stated as the privacy fact it is: the
 * driver is being shown a passenger's personal data in order to decide, and the
 * passenger is told this when they ask. Grants carry an expiry and auto-revoke
 * (US-4.2 / US-4.8); an unsubscribed passenger stays **muted** on the list until
 * deleted (US-4.12).
 *
 * ## The assignment gap is recorded, not filled
 *
 * A fleet-assigned vehicle appears under **"Temporarily assigned to me (FLEET)"** and
 * auto-expires (US-13.9). Walkthrough scenarios 52 and 72 both raise the same
 * ⚠ SPEC GAP: **what happens mid-trip when a fleet revokes an assignment is not
 * specified.** The chapter therefore says what the specs say — the vehicle leaves the
 * list, and the fleet is who to ask — and invents no mid-trip behaviour. Recorded in
 * the S11 handoff.
 */

import type { Chapter } from '@/content/types';

const URD_SESSIONS =
  'specs/user-requirements-document.md#epic-5-driver-session-management-bus-mode-a';
const URD_TRACKERS = 'specs/user-requirements-document.md#epic-3-hardware-gps-tracker-support';
const URD_SHARING =
  'specs/user-requirements-document.md#epic-4-vehicle-sharing-access-control-mode-b-private-transport';

export const d16: Chapter = {
  id: 'd16',
  slug: 'mode-a-and-b-driving',
  audience: 'driver',
  order: 16,
  title: 'www.guide.d16.title',
  summary: 'www.guide.d16.summary',

  steps: [
    { instruction: 'www.guide.d16.step1', note: 'www.guide.d16.step1.note', screenRef: 'SCR-DA-011' },
    { instruction: 'www.guide.d16.step2', screenRef: 'SCR-DA-011' },
    { instruction: 'www.guide.d16.step3', note: 'www.guide.d16.step3.note', screenRef: 'SCR-DA-011' },
    { instruction: 'www.guide.d16.step4', screenRef: 'SCR-DA-027' },
    { instruction: 'www.guide.d16.step5', screenRef: 'SCR-DA-027' },
    { instruction: 'www.guide.d16.step6', note: 'www.guide.d16.step6.note' },
    { instruction: 'www.guide.d16.step7', screenRef: 'SCR-DA-028' },
    { instruction: 'www.guide.d16.step8', note: 'www.guide.d16.step8.note' },
  ],

  callouts: [
    {
      kind: 'tip',
      body: 'www.guide.d16.callout.ignitionStartsIt',
      source: URD_TRACKERS,
    },
    {
      kind: 'privacy',
      body: 'www.guide.d16.callout.youSeeTheirNumber',
      source: URD_SHARING,
    },
    {
      kind: 'fee',
      body: 'www.guide.d16.callout.noDailyFeeHere',
      source: URD_SESSIONS,
    },
  ],

  screens: ['SCR-DA-011', 'SCR-DA-027', 'SCR-DA-028'],
  relatedChapters: ['d06', 'd02', 'd05'],
  faqRefs: ['modes', 'trains', 'mode-b-access'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-40-pairing-a-gps-hardware-tracker-imei-qr-bind-code',
    'specs/MageRide_Functional_Walkthrough.md#scenario-41-managing-mode-b-sharing-grants-granting-accepting-requests-revoking',
    'specs/MageRide_Functional_Walkthrough.md#scenario-52-an-assigned-fleet-vehicle-appearing-in-the-driver-app-temporary-assignment',
    URD_SESSIONS,
    URD_TRACKERS,
    URD_SHARING,
    'specs/user-requirements-document.md#1-a-service-modes',
    'specs/user-requirements-document.md#epic-13-fleet-operator-features-fleet-portal-phase-1',
    'specs/D2_mageride_ui_spec.md#scr-da-027-scr-di-027-tracker_pairing-gps-tracker-pairing-new-us-3-1-3-2-us-3-21-3-23-t-02-t-09',
    'specs/D2_mageride_ui_spec.md#scr-da-028-scr-di-028-sharing_mgmt-sharing-management-mode-b-adapt-us-4-1-4-4-4-7',
  ],
};
