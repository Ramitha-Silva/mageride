/**
 * Passenger chapter 16 — settings, help & support, and your data.
 *
 * The last passenger chapter, and the one that ends on a right rather than a
 * feature. It is also the only chapter that links out to a legal page, so the
 * wording has to be accurate in a way marketing copy does not.
 *
 * ## PDPA: what the platform actually commits to, and what it does not
 *
 * `pdpa-svc` (E-06) exposes exactly two user-initiated requests — **export** and
 * **erasure** — each answering `202` with a `dueBy`, and `pdpa.requests.due_by` is
 * defined in the data model as **`+30d`**. The ADD's risk register calls it a 30-day
 * SLA. So the chapter says **within 30 days**, twice, with its anchor.
 *
 * **Erasure is a soft-anonymise with a statutory hold list, not a hard delete**, and
 * the chapter says so. E-06 names the holds: active rides, open disputes, and the
 * immutable subset of the audit log; §12's entry adds *"no hard delete of immutable
 * audit trail"*. A public page that promised "everything, gone" would be making a
 * commitment the platform has already documented that it cannot keep — which is a
 * worse failure than saying the true thing, because it is discoverable. S07's
 * `www.faq.myData.a` already words it correctly (*"except for records the law
 * requires us to keep"*) and this chapter matches it rather than paraphrasing.
 *
 * **What is deliberately not stated: a retention period.** S09's fence says so
 * outright, and it is right — the specs carry several unrelated retention numbers
 * (proof photos 365 days, raw uploads 90 days, telemetry 30 days at full resolution)
 * and none of them is "how long MageRide keeps your account". Picking one would be
 * inventing a policy.
 *
 * **US-1.8** is the account-deletion story in the app (PDPA / Play Store
 * compliance); it is the same right reached from the same screen, so the copy names
 * one action rather than two.
 *
 * ## The rest of the chapter is Settings and Support, and both are small on purpose
 *
 * The drawer (US-22.7) reaches private transport, subscriptions, saved addresses and
 * profile & settings — four destinations, three of which have their own chapter, so
 * this one points rather than repeats.
 *
 * **Profile & settings and Edit profile are two screens and the copy keeps them
 * apart**, because US-1.5 turns on exactly that distinction: *"Language is no longer
 * part of the Edit-profile screen — language is set during onboarding (SCR-PA-002)
 * and from Profile & settings (SCR-PA-027)"* (Discussion item 10). The URD's screen
 * table agrees — SCR-PA-027 carries language, notifications, Home & Work, saved
 * addresses, default payment and Help & Support; **Edit Profile** carries name,
 * photo, notification preferences and **SOS contacts**, and says *"No language
 * selector here"*. A first draft of this chapter put the name, the photo and the
 * language on one screen, which is three facts and two screens.
 *
 * The **emergency contact** is therefore named here, on the screen that actually
 * holds it, and pointed at from chapter 10's SOS callout — a reader told "set one
 * before you need it" is owed the location.
 *
 * The **default payment method** (US-22.4) is described as *"the way you usually
 * pay"* and names no rail: that story's Cash / LankaQR / OnePay wording is retired by
 * chapter 11's rails and is MCS-35 Scope B line 855, still unapplied.
 * **Notification choices** are US-10.7.
 *
 * **Blocking a driver** (US-12.10) is in this chapter and not in chapter 10, because
 * it is not something anybody does mid-ride: a blocked driver stops appearing on the
 * map and **cannot be dispatched to you again**, which is a real guarantee and worth
 * stating precisely.
 *
 * Support is the FAQ plus a ticket — **description, a dropdown of past trips, an
 * optional screenshot** — and a status you can follow (US-16.1/16.2/16.3, and
 * SCR-PA-030a's sheet). Chapter 11 sends QR-payment disputes here, so the two agree
 * on where a problem goes.
 */

import type { Chapter } from '@/content/types';

const ADD_PDPA = 'specs/architecture-design-document.md#pdpa-svc';
const URD_SETTINGS = 'specs/user-requirements-document.md#epic-22-new-passenger-app-settings';
const URD_SUPPORT = 'specs/user-requirements-document.md#epic-16-new-in-app-support';

export const p16: Chapter = {
  id: 'p16',
  slug: 'settings-help-and-your-data',
  audience: 'passenger',
  order: 16,
  title: 'www.guide.p16.title',
  summary: 'www.guide.p16.summary',

  steps: [
    { instruction: 'www.guide.p16.step1', screenRef: 'SCR-PA-027' },
    { instruction: 'www.guide.p16.step2', screenRef: 'SCR-PA-027' },
    { instruction: 'www.guide.p16.step3' },
    { instruction: 'www.guide.p16.step4' },
    { instruction: 'www.guide.p16.step5', screenRef: 'SCR-PA-030' },
    { instruction: 'www.guide.p16.step6', screenRef: 'SCR-PA-030' },
    {
      instruction: 'www.guide.p16.step7',
      note: 'www.guide.p16.step7.note',
    },
    { instruction: 'www.guide.p16.step8' },
  ],

  callouts: [
    {
      kind: 'privacy',
      body: 'www.guide.p16.callout.thirtyDays',
      source: ADD_PDPA,
    },
    {
      kind: 'privacy',
      body: 'www.guide.p16.callout.whatIsKept',
      source: 'specs/architecture-design-document.md#e-06-pdpa-right-to-erasure-data-export',
    },
    {
      kind: 'tip',
      body: 'www.guide.p16.callout.blockADriver',
      source: 'specs/user-requirements-document.md#epic-12-safety-trust',
    },
  ],

  screens: ['SCR-PA-027', 'SCR-PA-030'],
  relatedChapters: ['p10', 'p15'],
  faqRefs: ['my-data', 'phone-number', 'languages'],

  sources: [
    ADD_PDPA,
    'specs/architecture-design-document.md#e-06-pdpa-right-to-erasure-data-export',
    URD_SETTINGS,
    URD_SUPPORT,
    'specs/user-requirements-document.md#epic-1-user-registration-onboarding',
    'specs/user-requirements-document.md#us-1-8',
    'specs/user-requirements-document.md#epic-12-safety-trust',
    'specs/MageRide_Functional_Walkthrough.md#scenario-19-raising-a-support-ticket-with-and-without-a-trip-attached',
  ],
};
