/**
 * Driver chapter 18 — safety, support and updates. The last chapter of the guide.
 *
 * URD Epic 12 (safety), Epic 16 (support), Epic 17 (updates), Epic 15 (offline), and
 * AL-13 — the ADD line that put an **emergency contact** on the driver profile at
 * all. Screens SCR-DA-032 and SCR-DA-033; Walkthrough scenarios 49, 19 and 22.
 *
 * ## The chapter's one hard instruction: set the contact before you need it
 *
 * **SOS refuses with no emergency contact set.** That is the Walkthrough's own edge
 * case for scenario 49 (*"No emergency contact set → SOS refuses → Add a contact in
 * the profile first"*), and it is the single most consequential sentence in the
 * driver guide: a safety control that is discovered to be unconfigured at the moment
 * it is pressed has failed completely. So the chapter opens on the contact (US-12.9,
 * AL-13 — name and phone, from the phone's contacts or typed, editable or removable
 * at any time) and only then on the button.
 *
 * What SOS does, stated so nobody expects more of it than it offers: an **SMS with
 * the driver's GPS location and trip details goes to that contact** (US-12.8), and
 * **every SOS is logged with timestamp, location and trip context for admin review
 * and possible law-enforcement support** (US-12.11). It is available **during an
 * active trip**. It is not a call to the police, and the guide does not imply it is.
 *
 * ## Support, including the one ticket a driver most often needs
 *
 * FAQ first (US-16.1), then **Raise a ticket** — a modal sheet with an **issue
 * description**, a **dropdown of past Trip IDs** and an **attach-screenshot** button,
 * and the ticket's status is tracked with the admin's resolution visible on it
 * (US-16.2 / US-16.3). And the quick action that is specific to drivers: **a refund
 * request for a daily fee charged in error** — the URD's own example is the app
 * crashing on Go Online (US-9.23). Chapter 13 charges the fee; this is where a driver
 * disputes one.
 *
 * ## Reports and blocks, said plainly rather than avoided
 *
 * A passenger can **report** a vehicle (US-12.5) and **three reports flag a driver
 * for review and a temporary delisting** (US-12.6, and the same rule from the other
 * side in US-6A.6 — chapter 17). A passenger can also **block** a driver, after which
 * that driver is never dispatched to them and does not appear on their map
 * (US-12.10). Both are consequences a driver can be subject to without being told,
 * so both are published; a guide that only lists the safety tools a driver *has* is
 * not the honest half of Epic 12.
 *
 * ## Losing signal, and why the guide bothers
 *
 * Epic 15: position samples are **buffered on the phone and replayed in order** when
 * signal returns, and duplicates are discarded so the route stays accurate. Published
 * because the visible symptom — a trip that appears to have stopped moving — looks
 * exactly like a broken trip, and a driver who stops and restarts something is the
 * worst outcome available.
 *
 * ## Updates
 *
 * US-17.1 / US-17.2. A **critical** update is a **non-dismissible prompt that blocks
 * the app until it is updated** — stated with its reason, *API compatibility*,
 * because "the app locked me out" needs one. A non-critical update is a
 * **dismissible banner**. Both send the reader to their own app store. This sits
 * beside chapter 1's standing note that the driver app is **not published yet**
 * (MCS-34 D3): nothing here contradicts that, and the download page is where that
 * changes.
 */

import type { Chapter } from '@/content/types';

const URD_SAFETY = 'specs/user-requirements-document.md#epic-12-safety-trust';
const URD_SUPPORT = 'specs/user-requirements-document.md#epic-16-new-in-app-support';
const URD_UPDATES = 'specs/user-requirements-document.md#epic-17-new-app-update-versioning';
const ADD_CONTACT = 'specs/architecture-design-document.md#1-8-remediation-log-al-01-al-16';

export const d18: Chapter = {
  id: 'd18',
  slug: 'safety-and-support',
  audience: 'driver',
  order: 18,
  title: 'www.guide.d18.title',
  summary: 'www.guide.d18.summary',

  steps: [
    { instruction: 'www.guide.d18.step1', note: 'www.guide.d18.step1.note' },
    { instruction: 'www.guide.d18.step2', screenRef: 'SCR-DA-032' },
    { instruction: 'www.guide.d18.step3', screenRef: 'SCR-DA-032' },
    { instruction: 'www.guide.d18.step4', screenRef: 'SCR-DA-033' },
    { instruction: 'www.guide.d18.step5', note: 'www.guide.d18.step5.note', screenRef: 'SCR-DA-033' },
    { instruction: 'www.guide.d18.step6' },
    { instruction: 'www.guide.d18.step7' },
    { instruction: 'www.guide.d18.step8', note: 'www.guide.d18.step8.note' },
  ],

  callouts: [
    {
      kind: 'warning',
      body: 'www.guide.d18.callout.setTheContactFirst',
      source: URD_SAFETY,
    },
    {
      kind: 'privacy',
      body: 'www.guide.d18.callout.everySosIsLogged',
      source: URD_SAFETY,
    },
    {
      kind: 'tip',
      body: 'www.guide.d18.callout.feeChargedInError',
      source: URD_SUPPORT,
    },
  ],

  screens: ['SCR-DA-032', 'SCR-DA-033'],
  relatedChapters: ['d17', 'd13', 'd09'],
  faqRefs: ['safety', 'phone-number', 'my-data'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-49-using-sos-as-a-driver-during-an-active-ride',
    'specs/MageRide_Functional_Walkthrough.md#scenario-51-driver-offline-gps-buffering-and-reconnect-behaviour',
    'specs/MageRide_Functional_Walkthrough.md#scenario-22-mandatory-app-update-blocking-dialog-vs-soft-banner',
    URD_SAFETY,
    URD_SUPPORT,
    URD_UPDATES,
    'specs/user-requirements-document.md#epic-15-offline-low-connectivity-handling',
    'specs/user-requirements-document.md#epic-9-daily-platform-fee-billing',
    ADD_CONTACT,
    'specs/D2_mageride_ui_spec.md#scr-da-032-scr-di-032-sos-sos-driver-new-us-12-8',
    'specs/D2_mageride_ui_spec.md#scr-da-033-scr-di-033-support-raise_ticket-modal-scr-da-033a-scr-di-033a-support-adapt-us-16-2-us-9-23',
  ],
};
