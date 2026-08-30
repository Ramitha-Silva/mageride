/**
 * Passenger chapter 8 — choosing a vehicle and reading the upfront fare.
 *
 * ## The payment methods changed, and this is the chapter that would have got it wrong
 *
 * S08's brief says: *"OnePay adds +5% shown as a recomputed total (US-8.11); LankaQR
 * has no surcharge; cash is the default."* That was true of URD v2.9. **ADD v3.6's
 * payment-custody change set (2026-08-01, AL-57…AL-59) retired both card rails as ride
 * methods**, and three specs carry the new answer:
 *
 * - D2's SCR-PA-016 paragraph, marked `Δ AL-57/AL-59`: *"Cash (default) · Wallet
 *   (prepaid balance, no surcharge …) · Driver QR … **OnePay is not a ride method***".
 * - D3: `POST /fare/pay`'s method enum is `cash | wallet | scan_driver_qr | cod`.
 * - ADD §6 `fare-svc`: the same four, "because no ride fare may be charged to a
 *   platform merchant account".
 *
 * OnePay survives only where **MageRide is the payee** — wallet top-ups, the driver's
 * daily fee, vouchers — and its processing cost is recovered there, not on a fare. So
 * **there is no surcharge on any ride payment method**, and publishing a 5% one would
 * be exactly the failure S08's own brief warns about: a public site quoting a price
 * that changed. S07 reached the same conclusion independently — `www.faq.howToPay.a`
 * and the SCR-PA-016 caption both say cash or the driver's QR.
 *
 * ## Which is why `SCR-PA-016` is not one of this chapter's screens
 *
 * The approved wireframe — and therefore the composited image — draws a payment sheet
 * with a live "OnePay +5% Rs 893" row. Placing it beside copy that says there is no
 * surcharge would contradict the page in a picture, and pictures are what a reader
 * believes. The registry entry keeps its `passenger/paying` tag for S09 to judge with
 * its own eyes; this chapter drops the `choosing-a-vehicle-and-fare` tag it also
 * carried, which is correct on the merits too: with no surcharge on any method, the
 * payment sheet no longer changes the fare, so it is no longer part of reading it.
 *
 * ## "Upfront" is a strong word and the second callout keeps it honest
 *
 * US-8.9 asks for an upfront fare **estimate**; US-8.4 shows a single total and not a
 * breakdown; D5 §1.2 computes the estimate from route distance and the final from the
 * distance actually travelled, with a difference beyond the admin threshold going to
 * review (§1.4). The card is what you agree to and the tariff does not move underneath
 * you — but "nothing can ever differ" is a stronger claim than the specs make, and the
 * callout says the true version instead.
 */

import type { Chapter } from '@/content/types';

const D5_FARE = 'specs/D5_mageride_business_logic.md#1-fare-calculation-mode-c';

export const p08: Chapter = {
  id: 'p08',
  slug: 'choosing-a-vehicle-and-fare',
  audience: 'passenger',
  order: 8,
  title: 'www.guide.p08.title',
  summary: 'www.guide.p08.summary',

  steps: [
    { instruction: 'www.guide.p08.step1', screenRef: 'SCR-PA-009' },
    { instruction: 'www.guide.p08.step2', screenRef: 'SCR-PA-009' },
    { instruction: 'www.guide.p08.step3' },
    { instruction: 'www.guide.p08.step4' },
    { instruction: 'www.guide.p08.step5' },
    { instruction: 'www.guide.p08.step6' },
    { instruction: 'www.guide.p08.step7' },
    { instruction: 'www.guide.p08.step8' },
  ],

  callouts: [
    {
      kind: 'fee',
      body: 'www.guide.p08.callout.tariffChanges',
      source: D5_FARE,
    },
    {
      kind: 'fee',
      body: 'www.guide.p08.callout.estimateVsFinal',
      source: 'specs/D5_mageride_business_logic.md#1-4-estimate-vs-final-disputes',
    },
  ],

  screens: ['SCR-PA-009'],
  relatedChapters: ['p07', 'p04'],
  faqRefs: ['passenger-cost', 'driver-keeps', 'how-to-pay'],

  sources: [
    'specs/user-requirements-document.md#epic-8',
    D5_FARE,
    'specs/D5_mageride_business_logic.md#1-4-estimate-vs-final-disputes',
    'specs/D2_mageride_ui_spec.md#scr-pa-016-payment-method-payment-selection',
    'specs/architecture-design-document.md#1-18-remediation-log-al-57-al-59',
    'specs/D3_mageride_api_contracts.md#fare-svc-ride-payment',
  ],
};
