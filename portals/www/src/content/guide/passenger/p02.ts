/**
 * Passenger chapter 2 — permissions.
 *
 * The brief calls this chapter "location, notifications, background", and the honest
 * version of it is shorter than that list suggests:
 *
 * - **Location, foreground only.** D2's SCR-PA-005 requests Android `FINE` +
 *   `FOREGROUND_SERVICE_LOCATION` and iOS `requestWhenInUseAuthorization()`; D1′ A.5
 *   says the same. The **driver** app is the one that asks for `ACCESS_BACKGROUND
 *   _LOCATION` and always-on (D2 SCR-DA-007). So "background" belongs in this chapter
 *   as a **negative** — the thing the passenger app does not do — and that is how
 *   {@link callouts} carries it.
 * - **Notifications are a preference, not a prompt this chapter can describe.** The
 *   passenger app's notification preference is part of the profile (US-1.5) and the
 *   push types are enumerated in D1′ A.5. No spec states a passenger-side
 *   POST_NOTIFICATIONS prompt — only the driver's permission screen lists one — so
 *   the steps describe the preference and the messages, and invent no dialog.
 * - **Contacts** is real and easy to miss: US-8.16's contact picker is the only other
 *   permission a passenger meets, and only on the proxy-booking path.
 */

import type { Chapter } from '@/content/types';

export const p02: Chapter = {
  id: 'p02',
  slug: 'permissions',
  audience: 'passenger',
  order: 2,
  title: 'www.guide.p02.title',
  summary: 'www.guide.p02.summary',

  steps: [
    { instruction: 'www.guide.p02.step1', screenRef: 'SCR-PA-005' },
    { instruction: 'www.guide.p02.step2', screenRef: 'SCR-PA-005' },
    { instruction: 'www.guide.p02.step3' },
    { instruction: 'www.guide.p02.step4' },
    { instruction: 'www.guide.p02.step5' },
    { instruction: 'www.guide.p02.step6' },
    { instruction: 'www.guide.p02.step7' },
  ],

  callouts: [
    {
      kind: 'privacy',
      body: 'www.guide.p02.callout.noBackground',
      source: 'specs/D2_mageride_ui_spec.md#scr-pa-005-permission-location-permission',
    },
    {
      kind: 'tip',
      body: 'www.guide.p02.callout.reenable',
      source: 'specs/D2_mageride_ui_spec.md#scr-pa-005-permission-location-permission',
    },
  ],

  screens: ['SCR-PA-005'],
  relatedChapters: ['p01', 'p03'],
  faqRefs: ['my-data', 'safety'],

  sources: [
    'specs/D1_mageride_user_flows.md#a-5-background-behaviors',
    'specs/D2_mageride_ui_spec.md#scr-pa-005-permission-location-permission',
    // The blue dot and its accuracy circle (MAP-02) and the map re-centring as you
    // move (US-7.6) — the only two things the guide says location *does*.
    'specs/D2_mageride_ui_spec.md#0-3-map-marker-patterns',
    'specs/user-requirements-document.md#epic-7',
    'specs/user-requirements-document.md#epic-1',
    'specs/MageRide_Functional_Walkthrough.md#scenario-1-new-passenger-signs-up-and-verifies-their-phone-number',
  ],
};
