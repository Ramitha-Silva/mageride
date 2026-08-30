/**
 * The three legal documents — **their structure, and no legal text.**
 *
 * MCS-34 **D5**: Terms and Privacy are supplied by counsel later, and *"every C134
 * session structures and translates and authors no legal text"*. S18 builds the
 * shell that receives them: a document layout, a table of contents, a last-updated
 * line, and the three routes.
 *
 * ## What is on these pages today, and why it is not a template
 *
 * S18 is explicit that the alternative to a supplied document is *"a short, honest
 * 'this document is being prepared' page — **not** a generic template pulled from
 * elsewhere. A wrong privacy policy is worse than an absent one."* That is not
 * squeamishness. A privacy policy is a **binding statement about how a company
 * handles personal data**; one lifted from a template describes a company that does
 * not exist, and every clause of it is either a promise MageRide has not made or a
 * permission its users have not given. An absent policy blocks a launch. A wrong one
 * survives it.
 *
 * So each document renders its status honestly, and two of the three carry sections
 * that are **descriptions of what the software does**, not policy:
 *
 *   - **Privacy · "what this website collects".** S18 asks for this in as many
 *     words — *"what **this site** collects, which is **nothing**: no cookies, no
 *     analytics, no form, no logs beyond the ingress's. Say that plainly; it is
 *     unusual and it is true."* Every clause of it is enforced by something in this
 *     repo rather than promised. `test/fences.test.ts` refuses every network call
 *     and every environment read anywhere under `app/` or `src/`, and any HTTP
 *     client in `package.json`; `scripts/check-bundle.mjs` refuses the public-env
 *     prefix in any client chunk; there is no form on any of the thirteen routes;
 *     and the only client-side storage is S19's theme preference, which is
 *     `localStorage` and is never sent anywhere.
 *
 *     (Those three sentences are deliberately vague about *which* tokens are
 *     banned. The first draft named them and **the fence caught its own
 *     description** — those sweeps are text searches over the source rather than
 *     AST walks, on purpose, because a key assembled from fragments would defeat
 *     any analysis. A comment cannot be distinguished from a call, and the honest
 *     fix is to describe the rule rather than to weaken the check.)
 *   - **Your data rights · what `pdpa-svc` actually does.** Export and erasure, due
 *     within 30 days. A factual description of a service, anchored to the ADD and to
 *     US-1.8, saying the same thing the FAQ's `my-data` entry already says.
 *
 * Neither is the policy, both say so, and **the moment counsel's text arrives it
 * replaces the status section** — {@link LegalDocument.lastUpdated} stops being
 * `null` and the layout is already built around it.
 *
 * ## `lastUpdated` is a string, not a `Date`, and never "now"
 *
 * An ISO date typed by the person who supplied the text — **never read from the
 * clock**. A last-updated line that moved every time the site was rebuilt would
 * tell a reader the document had changed when it had not, which on a legal document
 * is the one inaccuracy that matters. `null` means no version has been published,
 * and the layout says that rather than printing a build date. `test/legal.test.ts`
 * sweeps this file and the layout for a clock read; like every other sweep here it
 * is a text search, so neither file spells the call it forbids.
 */

import type { WwwMessageKey } from '@/i18n';
import { LEGAL_DOCS, type LegalDoc } from '@/lib/routes';

export interface LegalSection {
  /** The in-page anchor, unique within a document. */
  readonly id: string;
  readonly heading: WwwMessageKey;
  /** One key per paragraph. */
  readonly body: readonly WwwMessageKey[];
  /** The spec this section describes, where it describes one. README rule 7. */
  readonly source?: string;
}

export interface LegalDocument {
  readonly doc: LegalDoc;
  /**
   * The standfirst. The `<h1>` is **not** here: it is the route's `labelKey`, so a
   * document's nav label and its heading cannot drift (`portals/www/CLAUDE.md`).
   */
  readonly intro: WwwMessageKey;
  /**
   * `YYYY-MM-DD` as supplied with the text, or `null` while none has been. See the
   * module note — this is never derived from the clock.
   */
  readonly lastUpdated: string | null;
  /**
   * The **status** section: what is true about this document right now. Rendered
   * first and dropped entirely once `lastUpdated` is set.
   */
  readonly status: WwwMessageKey;
  readonly sections: readonly LegalSection[];
}

const PDPA_SVC = 'specs/architecture-design-document.md#pdpa-svc';
const URD_DATA_RIGHTS = 'specs/user-requirements-document.md#us-1-8';
const MCS34 = 'build/prompts/MCS-34-www-informational-site.md';

export const LEGAL_DOCUMENTS: readonly LegalDocument[] = [
  {
    doc: 'terms',
    intro: 'www.legal.terms.intro',
    lastUpdated: null,
    status: 'www.legal.terms.status',
    // Nothing else. There is no factual description of a service that stands in for
    // terms of service — terms *are* the policy, all the way down, and a section
    // describing "what using MageRide involves" would be exactly the generic
    // template S18 rules out, wearing a different heading.
    sections: [],
  },
  {
    doc: 'privacy',
    intro: 'www.legal.privacy.intro',
    lastUpdated: null,
    status: 'www.legal.privacy.status',
    sections: [
      {
        id: 'this-site',
        heading: 'www.legal.privacy.siteHeading',
        body: [
          'www.legal.privacy.siteBody',
          'www.legal.privacy.siteLogs',
          'www.legal.privacy.siteTheme',
        ],
        source: MCS34,
      },
      {
        id: 'the-apps',
        heading: 'www.legal.privacy.appsHeading',
        body: ['www.legal.privacy.appsBody'],
        source: URD_DATA_RIGHTS,
      },
    ],
  },
  {
    doc: 'pdpa',
    intro: 'www.legal.pdpa.intro',
    lastUpdated: null,
    status: 'www.legal.pdpa.status',
    sections: [
      {
        id: 'your-rights',
        heading: 'www.legal.pdpa.rightsHeading',
        body: ['www.legal.pdpa.rightsBody', 'www.legal.pdpa.rightsExceptions'],
        source: PDPA_SVC,
      },
      {
        id: 'how-to-ask',
        heading: 'www.legal.pdpa.howHeading',
        body: ['www.legal.pdpa.howBody'],
        source: URD_DATA_RIGHTS,
      },
    ],
  },
];

/** A document by slug. Total over {@link LegalDoc} — asserted below. */
export function legalDocument(doc: LegalDoc): LegalDocument {
  const found = LEGAL_DOCUMENTS.find((entry) => entry.doc === doc);
  if (!found) throw new Error(`legal.ts: no document for "${doc}"`);
  return found;
}

/**
 * A section's DOM id — namespaced by document, because the three render under the
 * same route pattern and a future shared section would otherwise collide.
 */
export function legalSectionId(doc: LegalDoc, section: LegalSection): string {
  return `${doc}-${section.id}`;
}

/**
 * Every route in `LEGAL_DOCS` has a document, and every document has a route.
 *
 * The route table derives its slugs from `ROUTES` and this array is hand-listed, so
 * the two can disagree in both directions: a fourth legal route would publish a page
 * that throws, and a document written for a slug nobody publishes would be
 * translated and never rendered. `test/legal.test.ts` calls this.
 */
export function assertLegalDocumentsAreWellFormed(): void {
  for (const doc of LEGAL_DOCS) {
    if (!LEGAL_DOCUMENTS.some((entry) => entry.doc === doc)) {
      throw new Error(`legal.ts: route "legal/${doc}" has no document`);
    }
  }

  const seen = new Set<string>();
  for (const entry of LEGAL_DOCUMENTS) {
    if (!(LEGAL_DOCS as readonly string[]).includes(entry.doc)) {
      throw new Error(`legal.ts: "${entry.doc}" is not a published route`);
    }
    if (seen.has(entry.doc)) throw new Error(`legal.ts: duplicate document "${entry.doc}"`);
    seen.add(entry.doc);

    const anchors = new Set<string>();
    for (const section of entry.sections) {
      if (anchors.has(section.id)) {
        throw new Error(`legal.ts: "${entry.doc}" repeats the anchor "${section.id}"`);
      }
      anchors.add(section.id);

      if (section.body.length === 0) {
        throw new Error(`legal.ts: "${entry.doc}/${section.id}" has a heading and no body`);
      }
    }

    if (entry.lastUpdated !== null && !/^\d{4}-\d{2}-\d{2}$/.test(entry.lastUpdated)) {
      throw new Error(
        `legal.ts: "${entry.doc}" has lastUpdated "${entry.lastUpdated}", which is not YYYY-MM-DD`,
      );
    }
  }
}
