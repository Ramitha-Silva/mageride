/**
 * The FAQ — 21 entries feeding `/faq`, the per-page subsets S15–S18 pull, and
 * `FAQPage` JSON-LD in S19.
 *
 * (S07's own prose said 18 and the array has always held 20; S18 added the
 * accessibility entry that makes 21. Corrected here rather than left to be
 * rediscovered — `FAQ.length` is the number, and `/faq` reads it rather than a
 * sentence.)
 *
 * ## What belongs here
 *
 * Questions a real person asks before they trust a transport app with a journey or
 * a livelihood — money, safety, coverage, and "what is the catch". Not feature
 * documentation: **how** to do a thing is a guide chapter (S08–S11), and an FAQ that
 * duplicates the guide is two documents to keep true instead of one.
 *
 * Several answers here are the honest, slightly uncomfortable ones — `coverage`,
 * `why-free`, `mode-b-price`. That is deliberate. An FAQ is where a sceptical reader
 * goes, and the fastest way to lose them is an answer that reads like it was written
 * by somebody with something to hide.
 *
 * ## `refs` is not decoration
 *
 * Every entry carries the spec anchors its answer rests on, for the same reason the
 * rest of `src/content/` does (README rule 7): each of these is a factual claim
 * about a real service. An entry whose `refs` is empty is either not a fact or not
 * checked, and neither belongs on a public page.
 */

import type { WwwMessageKey } from '@/i18n';

export interface FaqEntry {
  readonly id: string;
  readonly question: WwwMessageKey;
  readonly answer: WwwMessageKey;
  /** Spec anchors backing the answer. */
  readonly refs: readonly string[];
  /** Which audience's page pulls this into its subset. `both` shows on either. */
  readonly audience: 'passenger' | 'driver' | 'both';
}

const URD_VISION = 'specs/user-requirements-document.md#1-product-vision';
const URD_MODES = 'specs/user-requirements-document.md#1-a-service-modes';
const URD_VEHICLES = 'specs/user-requirements-document.md#1-b-canonical-vehicle-types';
const URD_FEES = 'specs/user-requirements-document.md#epic-9-daily-platform-fee-billing';
const URD_FEE_TABLE =
  'specs/user-requirements-document.md#daily-platform-fee-structure-namma-yatri-methodology';
const PDPA = 'specs/architecture-design-document.md#pdpa-svc';
const MAPS = 'specs/D6_mageride_integration.md#7-6-maps-tiles-pmtiles-nominatim-d-14';

export const FAQ: readonly FaqEntry[] = [
  // --- Money: the four everybody asks first ------------------------------------
  {
    id: 'passenger-cost',
    question: 'www.faq.passengerCost.q',
    answer: 'www.faq.passengerCost.a',
    refs: [URD_VISION, URD_FEE_TABLE],
    audience: 'passenger',
  },
  {
    id: 'why-free',
    question: 'www.faq.whyFree.q',
    answer: 'www.faq.whyFree.a',
    refs: [URD_VISION, URD_FEES],
    audience: 'both',
  },
  {
    id: 'driver-keeps',
    question: 'www.faq.driverKeeps.q',
    answer: 'www.faq.driverKeeps.a',
    refs: [URD_VISION],
    audience: 'driver',
  },
  {
    id: 'daily-fee',
    question: 'www.faq.dailyFee.q',
    answer: 'www.faq.dailyFee.a',
    refs: [URD_FEES, URD_FEE_TABLE],
    audience: 'driver',
  },
  {
    id: 'fee-off-days',
    question: 'www.faq.feeOffDays.q',
    answer: 'www.faq.feeOffDays.a',
    refs: [URD_FEE_TABLE],
    audience: 'driver',
  },
  {
    id: 'how-to-pay',
    question: 'www.faq.howToPay.q',
    answer: 'www.faq.howToPay.a',
    refs: ['specs/user-requirements-document.md#epic-8', URD_VISION],
    audience: 'passenger',
  },
  {
    id: 'wallet-topup',
    question: 'www.faq.walletTopUp.q',
    answer: 'www.faq.walletTopUp.a',
    refs: [URD_VISION, 'specs/user-requirements-document.md#epic-9a-in-app-driver-wallet'],
    audience: 'driver',
  },

  // --- Coverage and expectations -----------------------------------------------
  {
    id: 'coverage',
    question: 'www.faq.coverage.q',
    answer: 'www.faq.coverage.a',
    refs: [URD_MODES],
    audience: 'both',
  },
  {
    id: 'vehicle-types',
    question: 'www.faq.vehicleTypes.q',
    answer: 'www.faq.vehicleTypes.a',
    refs: [URD_VEHICLES],
    audience: 'both',
  },
  {
    id: 'modes',
    question: 'www.faq.modes.q',
    answer: 'www.faq.modes.a',
    refs: [URD_MODES],
    audience: 'both',
  },
  {
    id: 'mode-b-access',
    question: 'www.faq.modeBAccess.q',
    answer: 'www.faq.modeBAccess.a',
    refs: [URD_MODES, 'specs/user-requirements-document.md#epic-4'],
    audience: 'passenger',
  },
  {
    id: 'mode-b-price',
    question: 'www.faq.modeBPrice.q',
    answer: 'www.faq.modeBPrice.a',
    refs: [URD_FEE_TABLE, URD_MODES],
    audience: 'passenger',
  },
  {
    id: 'trains',
    question: 'www.faq.trains.q',
    answer: 'www.faq.trains.a',
    refs: [URD_MODES],
    audience: 'passenger',
  },

  // --- Getting started ---------------------------------------------------------
  {
    id: 'signup',
    question: 'www.faq.signup.q',
    answer: 'www.faq.signup.a',
    refs: ['specs/user-requirements-document.md#epic-1'],
    audience: 'both',
  },
  {
    id: 'become-a-driver',
    question: 'www.faq.becomeADriver.q',
    answer: 'www.faq.becomeADriver.a',
    refs: ['specs/user-requirements-document.md#epic-2'],
    audience: 'driver',
  },
  {
    id: 'languages',
    question: 'www.faq.languages.q',
    answer: 'www.faq.languages.a',
    refs: ['CLAUDE.md#universal-rules'],
    audience: 'both',
  },
  {
    /*
     * S18. **URD Epic 19 was the corpus's one real coverage gap**, identified in
     * S11's handoff and handed to this session by S17's: US-19.1 (TalkBack on the
     * core flows) and US-19.2 (system text size) were stated nowhere in 34 chapters
     * or 20 questions, which against a Definition of Done reading *"every passenger
     * and driver capability"* is a hole rather than an omission.
     *
     * It lives here rather than in a chapter because it is not a procedure. There is
     * nothing to do in the app — you turn TalkBack on in Android's settings and the
     * app is expected to work — so a guide chapter would be a page of steps that
     * are not steps. It is exactly the question somebody asks before installing,
     * which is what this file is for.
     *
     * **Android's screen reader by name, and no VoiceOver claim.** US-19.1 says
     * TalkBack; the URD states no iOS accessibility requirement anywhere, and this
     * site does not get to infer one from the existence of an iPhone app.
     */
    id: 'accessibility',
    question: 'www.faq.accessibility.q',
    answer: 'www.faq.accessibility.a',
    refs: [
      'specs/user-requirements-document.md#us-19-1',
      'specs/user-requirements-document.md#us-19-2',
    ],
    audience: 'both',
  },

  // --- Safety, privacy, data ---------------------------------------------------
  {
    id: 'safety',
    question: 'www.faq.safety.q',
    answer: 'www.faq.safety.a',
    refs: ['specs/user-requirements-document.md#epic-12'],
    audience: 'both',
  },
  {
    id: 'phone-number',
    question: 'www.faq.phoneNumber.q',
    answer: 'www.faq.phoneNumber.a',
    refs: ['specs/user-requirements-document.md#us-26-5'],
    audience: 'both',
  },
  {
    id: 'my-data',
    question: 'www.faq.myData.q',
    answer: 'www.faq.myData.a',
    refs: [PDPA, 'specs/user-requirements-document.md#us-1-8'],
    audience: 'both',
  },
  {
    id: 'maps',
    question: 'www.faq.maps.q',
    answer: 'www.faq.maps.a',
    refs: [MAPS, URD_VISION],
    audience: 'both',
  },
];

/** The entries one audience's page shows — its own, plus the shared ones. */
export function faqFor(audience: 'passenger' | 'driver'): readonly FaqEntry[] {
  return FAQ.filter((entry) => entry.audience === audience || entry.audience === 'both');
}

/**
 * `/faq`'s three sections, in reading order — S18 asks for the page to be **grouped
 * by audience**.
 *
 * `faqFor()` is deliberately *not* what this page uses. That helper returns an
 * audience's own entries **plus the shared ones**, which is right for a role landing
 * page showing one subset and wrong here: rendering both subsets would put every
 * `both` entry on the page twice, and the second copy would carry a duplicate
 * `id="faq-…"` — breaking the deep link, the JSON-LD S19 generates from the same
 * data, and the document outline, all silently.
 *
 * So each group is exactly its own audience, the shared questions come first because
 * they are the ones everybody asks, and `FAQ_GROUPS` partitions the array: every
 * entry appears once, in one section.
 */
export const FAQ_GROUPS: readonly {
  readonly audience: FaqEntry['audience'];
  readonly heading: WwwMessageKey;
}[] = [
  { audience: 'both', heading: 'www.faq.group.everyone' },
  { audience: 'passenger', heading: 'www.nav.passengers' },
  { audience: 'driver', heading: 'www.nav.drivers' },
];

/** One group's entries, in registry order. A partition, not a filter with overlap. */
export function faqGroup(audience: FaqEntry['audience']): readonly FaqEntry[] {
  return FAQ.filter((entry) => entry.audience === audience);
}

/**
 * The DOM id an entry is deep-linkable at — S18: *"Deep-linkable: `#faq-<id>` opens
 * the item."*
 *
 * A function in this module, and not a template literal written twice, for the
 * reason S17 learned the hard way with `stepId`: the anchor and the thing it points
 * at are written in two different files — here it is the server-rendered
 * `<details>` and the client component that opens it on a hash — and two spellings
 * of one id is a deep link that silently scrolls nowhere. It lives in the content
 * module rather than beside either of them because a value exported from a
 * `'use client'` module is a client *reference* the server may not call.
 */
export function faqAnchorId(id: string): string {
  return `faq-${id}`;
}

/** An entry by id — what `Chapter.faqRefs` resolves through. */
export function faqById(id: string): FaqEntry | undefined {
  return FAQ.find((entry) => entry.id === id);
}

/** Every id is unique and every answer is sourced. Called by `test/content.test.ts`. */
export function assertFaqIsWellFormed(): void {
  const seen = new Set<string>();
  for (const entry of FAQ) {
    if (seen.has(entry.id)) throw new Error(`faq: duplicate id "${entry.id}"`);
    seen.add(entry.id);

    if (entry.refs.length === 0) {
      throw new Error(
        `faq: "${entry.id}" cites no spec. Every answer here is a public claim about a real ` +
          'service (README rule 7) — an unsourced one is either not a fact or not checked.',
      );
    }
  }
}
