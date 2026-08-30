/**
 * Driver chapter 5 — permissions and background location.
 *
 * SCR-DA-007 asks for four things and the chapter's job is to say what each one buys
 * the driver, because a permissions screen that explains nothing is a permissions
 * screen people deny.
 *
 * | Ask | Why |
 * |---|---|
 * | **Location · Always / Background** | position is published while driving, and a foreground service keeps publishing with the screen off (D1 §B.5) |
 * | **Notifications** | this is how a ride offer arrives at all, and how it wakes a sleeping phone |
 * | **Battery optimisation off** | the OS otherwise stops the service that does both of the above |
 * | **Display over apps** | the full-screen offer takeover can appear over whatever is on screen |
 *
 * ## iOS and Android are one guide (S10's fence), so the difference is a note
 *
 * D2 tags this screen `[DELTA:PLATFORM]` and the difference is real: Android asks for
 * background location, foreground-service location, notifications and a
 * battery-optimisation exemption, and draws a **Display over apps** row; iOS asks for
 * *Always* location authorisation and notification authorisation and has no equivalent
 * of the overlay permission. That is one `note` on one step, not a forked chapter.
 *
 * ## The privacy claim is bounded to what the specs actually say
 *
 * Publishing **starts when the driver goes online or starts a journey and stops when
 * they go offline or end it** — D1 §B.3 starts the GPS foreground service and the MQTT
 * publish on Go Online and stops both on End Journey, and §B.8 says going offline
 * clears availability. For a **hardware tracker** on a Mode C vehicle US-3.21 is
 * explicit in the same direction: pings sent while the vehicle is offline are not
 * accepted onto the live map or into dispatch.
 *
 * Two things the chapter deliberately does **not** claim: that nothing whatsoever
 * leaves the phone when offline (the specs describe availability and ingest, not every
 * byte), and any retention period for position history — that is `pdpa-svc`'s and it
 * is not stated per-role anywhere this session could anchor to.
 *
 * What it does say, because it is stated and it is reassuring: **the driver's own home
 * map shows only their own vehicle** (US-7.18), and a passenger cannot see a Mode C
 * vehicle that is engaged on a hire (US-7.16).
 */

import type { Chapter } from '@/content/types';

const D1_BACKGROUND = 'specs/D1_mageride_user_flows.md#b-5-background-behaviors';
const D2_PERMISSIONS = 'specs/D2_mageride_ui_spec.md#scr-da-007-scr-di-007-permission-permissions-adapt-delta-platform';

export const d05: Chapter = {
  id: 'd05',
  slug: 'permissions-and-background-location',
  audience: 'driver',
  order: 5,
  title: 'www.guide.d05.title',
  summary: 'www.guide.d05.summary',

  steps: [
    { instruction: 'www.guide.d05.step1', screenRef: 'SCR-DA-007' },
    {
      instruction: 'www.guide.d05.step2',
      note: 'www.guide.d05.step2.note',
      screenRef: 'SCR-DA-007',
    },
    { instruction: 'www.guide.d05.step3', screenRef: 'SCR-DA-007' },
    { instruction: 'www.guide.d05.step4', note: 'www.guide.d05.step4.note' },
    { instruction: 'www.guide.d05.step5' },
    { instruction: 'www.guide.d05.step6' },
  ],

  callouts: [
    {
      kind: 'privacy',
      body: 'www.guide.d05.callout.whenYouArePublished',
      source: D1_BACKGROUND,
    },
    {
      kind: 'warning',
      body: 'www.guide.d05.callout.batteryOptimisation',
      source: D2_PERMISSIONS,
    },
    {
      kind: 'privacy',
      body: 'www.guide.d05.callout.ownVehicleOnly',
      source: 'specs/user-requirements-document.md#epic-7-live-map-passenger-experience',
    },
  ],

  screens: ['SCR-DA-007'],
  relatedChapters: ['d06', 'd07', 'd01'],
  faqRefs: ['my-data', 'become-a-driver'],

  sources: [
    D1_BACKGROUND,
    'specs/D1_mageride_user_flows.md#b-3-per-screen-user-actions-key',
    D2_PERMISSIONS,
    'specs/user-requirements-document.md#epic-7-live-map-passenger-experience',
    'specs/user-requirements-document.md#epic-3-hardware-gps-tracker-support',
  ],
};
