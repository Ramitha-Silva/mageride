/**
 * Passenger chapter 13 — booking for someone else, and the SMS web link.
 *
 * ## Two promises the specs make in writing, and this chapter repeats both
 *
 * **Declining a pickup-location request transmits no GPS.** P-02 states it, US-25.3
 * restates it for the browser path, and D5 §10 says *"Decline never leaks GPS"*. The
 * web page says it to the rider's face and this page says it to the booker's, because
 * the person being asked for their location is not the person reading this chapter —
 * a booker who knows the rider is under no obligation is a booker who does not press
 * again. It is a `privacy` callout with its anchor, which is what README rule 7 is for.
 *
 * **The driver sees the rider's number, never the booker's** (P-05, retained
 * explicitly by US-26.2 when the masking requirement was withdrawn). Chapter 10
 * carries the general statement that matched parties see real numbers; this chapter
 * carries the proxy half, because it is the case where the two differ.
 *
 * ## The web link is the point of the chapter, not a footnote
 *
 * AL-44 formalised `passenger.mageride.lk` into six contracted pages,
 * SCR-WT-001…006, reached by a **tokenised SMS link** and **no login and no
 * account** (US-8.22, US-25.1/25.2). The rider gets the ride page — driver, vehicle
 * and plate, live map and ETA, the start code to read out — and **Call driver is a
 * plain `tel:` link with the driver's real number** (US-26.3; the proxy-DID
 * round-trip `POST /public/track/{token}/call` was removed by AL-48). SOS is there
 * too, and it texts the **booker** (US-25.5) — which is worth saying, because the
 * booker is the one who will be surprised by it otherwise.
 *
 * The token is scope-shaped and expiring: `proxy_rider` lives until the trip
 * completes, `pickup_confirm` for **5 minutes** and is burned on use. An expired link
 * is a dead end with **zero ride data** (SCR-WT-006). The copy says the link stops
 * working rather than quoting a TTL table.
 *
 * ## Who pays a proxy booking is an open question and the chapter does not answer it
 *
 * US-8.21's rule was *Cash ⇒ the rider pays the driver; LankaQR/OnePay ⇒ the booker
 * is charged.* Chapter 11's rails retired both of those card methods. `wallet` maps
 * cleanly to the booker's balance, but **`scan_driver_qr` has no answer in any
 * spec** — the person holding the phone at the drop-off is the rider, not the booker
 * — and that is recorded as **MCS-35 decision D1**, unresolved.
 *
 * So the chapter publishes only the half that survived: **if the booker chose cash,
 * the rider pays the driver and is told so**, which is unchanged (US-8.21, P-04) and
 * is drawn on SCR-WT-004 as a cash-due notice. It says nothing about who is charged
 * on the other rails. Inventing an answer here would be a public commitment to a
 * decision nobody has taken.
 *
 * **Also not published:** the abuse limits on location requests (5 per hour, 30 per
 * day per booker, P-12). They are real and they are server-side; a booker who trips
 * one is told by the app. Printing a rate limit on a marketing site invites reading
 * it as a feature.
 */

import type { Chapter } from '@/content/types';

const URD_PROXY = 'specs/user-requirements-document.md#epic-8-passenger-trip-booking-fare-calculation';
const URD_WEB_SUBVIEW = 'specs/user-requirements-document.md#epic-25-new-2026-07-05-change-set-passenger-web-subview-contracts-spec-hygiene';
const D5_PROXY = 'specs/D5_mageride_business_logic.md#10-proxy-booking-new-p-01-p-05-p-12-p-13';

export const p13: Chapter = {
  id: 'p13',
  slug: 'booking-for-someone-else',
  audience: 'passenger',
  order: 13,
  title: 'www.guide.p13.title',
  summary: 'www.guide.p13.summary',

  steps: [
    { instruction: 'www.guide.p13.step1' },
    { instruction: 'www.guide.p13.step2' },
    {
      instruction: 'www.guide.p13.step3',
      note: 'www.guide.p13.step3.note',
      screenRef: 'SCR-PA-011',
    },
    { instruction: 'www.guide.p13.step4', screenRef: 'SCR-WT-003' },
    { instruction: 'www.guide.p13.step5' },
    { instruction: 'www.guide.p13.step6', screenRef: 'SCR-WT-001' },
    { instruction: 'www.guide.p13.step7' },
    { instruction: 'www.guide.p13.step8' },
  ],

  callouts: [
    {
      kind: 'privacy',
      body: 'www.guide.p13.callout.declineSendsNothing',
      source: URD_WEB_SUBVIEW,
    },
    {
      kind: 'privacy',
      body: 'www.guide.p13.callout.riderNumberOnly',
      source: 'specs/user-requirements-document.md#epic-26-new-2026-07-05-change-set-2-driver-qr-settlement-number-masking-removal',
    },
    {
      kind: 'warning',
      body: 'www.guide.p13.callout.cashIsTheRiders',
      source: URD_PROXY,
    },
  ],

  screens: ['SCR-PA-011', 'SCR-WT-001', 'SCR-WT-003'],
  relatedChapters: ['p07', 'p10'],
  faqRefs: ['phone-number', 'how-to-pay'],

  sources: [
    URD_PROXY,
    URD_WEB_SUBVIEW,
    D5_PROXY,
    'specs/D1_mageride_user_flows.md#f-29-2-package-recipient-proxy-rider-web-tracking-items-1-2-us-25-1-25-2',
    'specs/MageRide_Functional_Walkthrough.md#scenario-5-booking-a-ride-for-someone-else-proxy-booking',
    'specs/MageRide_Functional_Walkthrough.md#scenario-103-a-proxy-rider-without-the-app-tracks-the-whole-ride-from-one-sms-link',
    'specs/MageRide_Functional_Walkthrough.md#scenario-104-a-rider-without-the-app-shares-their-pickup-spot-from-the-browser',
  ],
};
