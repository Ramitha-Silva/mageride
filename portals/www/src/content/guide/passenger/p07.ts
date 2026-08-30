/**
 * Passenger chapter 7 — booking a ride (Mode C).
 *
 * `ride-svc` owns Mode C and this chapter stays inside it: a vehicle you hail now,
 * never a scheduled service you follow. Chapters 4 and 5 are the other two things,
 * and the copy points at them by name rather than implying one screen with a switch.
 *
 * ## The four ways to set a location, described where each one actually appears
 *
 * S08's brief lists "geo-search, a map pin, paste a Google Maps link, and saved
 * places". Three of them are everywhere a location is asked for. **Paste-link is
 * not**: US-8.2d and D1′ F-23.2 scope it to the places where you are setting a
 * location *for somebody else* — the proxy rider's pickup (SCR-PA-010b) and a
 * package's pickup and drop-off (SCR-PA-012) — and Walkthrough scenario 4, booking
 * for yourself, does not offer it. So the step describes it and the note says where
 * it turns up, rather than promising a Paste button on a screen that has none.
 *
 * ## The search box is OpenStreetMap and the chapter says so out loud
 *
 * D-14 and D3′'s map hard rule: **no Google Places fallback, ever.** The predictions
 * are `query-svc`'s self-hosted Nominatim plus the reader's own saved and recent
 * addresses, and when the geocoder is unavailable the app offers "pick on map" —
 * degrade and say so. Describing the search as anything vaguer would be the one
 * sentence on this site that a competitor could disprove with a network trace.
 *
 * The short-link resolution behind Paste-link is `transit-svc`'s
 * `/v1/geo/parse-maps-link`, which touches no geocoder and no Google API. The copy
 * says "resolving short links on its own servers" and names no endpoint, because an
 * endpoint is not public copy.
 */

import type { Chapter } from '@/content/types';

export const p07: Chapter = {
  id: 'p07',
  slug: 'booking-a-ride',
  audience: 'passenger',
  order: 7,
  title: 'www.guide.p07.title',
  summary: 'www.guide.p07.summary',

  steps: [
    // No `screenRef`: the "Where to?" panel is on the live map, which is chapter 3's
    // screen. Pointing this step at it would pull the map into this chapter's strip
    // and say nothing the next two steps do not say better.
    { instruction: 'www.guide.p07.step1' },
    { instruction: 'www.guide.p07.step2', screenRef: 'SCR-PA-008' },
    { instruction: 'www.guide.p07.step3', screenRef: 'SCR-PA-008' },
    { instruction: 'www.guide.p07.step4', screenRef: 'SCR-PA-026' },
    {
      instruction: 'www.guide.p07.step5',
      note: 'www.guide.p07.step5.note',
    },
    { instruction: 'www.guide.p07.step6', screenRef: 'SCR-PA-009' },
    { instruction: 'www.guide.p07.step7', screenRef: 'SCR-PA-009' },
    { instruction: 'www.guide.p07.step8' },
  ],

  callouts: [
    {
      kind: 'privacy',
      body: 'www.guide.p07.callout.openMaps',
      source: 'specs/D6_mageride_integration.md#7-6-maps-tiles-pmtiles-nominatim-d-14',
    },
    {
      kind: 'tip',
      body: 'www.guide.p07.callout.routeNumber',
      source: 'specs/user-requirements-document.md#epic-8',
    },
  ],

  screens: ['SCR-PA-008', 'SCR-PA-009', 'SCR-PA-026'],
  relatedChapters: ['p04', 'p08'],
  faqRefs: ['maps', 'signup'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-4-booking-a-regular-on-demand-ride-tuk-sedan-van-for-yourself',
    'specs/user-requirements-document.md#epic-8',
    'specs/D1_mageride_user_flows.md#f-23-2-paste-link-location',
    'specs/D1_mageride_user_flows.md#f-23-6-add-saved-address',
    'specs/D6_mageride_integration.md#7-6-maps-tiles-pmtiles-nominatim-d-14',
  ],
};
