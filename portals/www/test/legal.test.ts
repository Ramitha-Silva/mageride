import { readFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

import {
  LEGAL_DOCUMENTS,
  assertLegalDocumentsAreWellFormed,
  legalDocument,
  legalSectionId,
} from '@/content/legal';
import { wwwEn } from '@/i18n/messages/en';
import { LEGAL_DOCS } from '@/lib/routes';

/**
 * The three legal routes, and the one rule that matters about them.
 *
 * **MCS-34 D5: counsel supplies the text and no session in C134 authors any of
 * it.** S18 built the shell. These tests hold the shell to its shape and — the
 * point of the file — hold the *content* to the decision, because "we did not write
 * a privacy policy" is the kind of thing that stays true until a later session
 * decides an empty page looks unfinished and pastes something in.
 */

const appRoot = resolve(import.meta.dirname, '..');

describe('the legal registry', () => {
  it('is well formed', () => {
    expect(() => assertLegalDocumentsAreWellFormed()).not.toThrow();
  });

  /**
   * The route table derives its slugs from `ROUTES`; this array is hand-listed. The
   * two can disagree in both directions — a fourth route would publish a page that
   * throws, and a document for a slug nobody publishes would be written, translated
   * and never rendered.
   */
  it('has a document for every published route, and no other', () => {
    expect(LEGAL_DOCUMENTS.map((entry) => entry.doc)).toEqual([...LEGAL_DOCS]);
    for (const doc of LEGAL_DOCS) {
      expect(legalDocument(doc).doc).toBe(doc);
    }
  });

  it('namespaces section anchors by document', () => {
    const privacy = legalDocument('privacy');
    const section = privacy.sections[0]!;
    expect(legalSectionId('privacy', section)).toBe(`privacy-${section.id}`);
  });

  /** Every anchor on the site is unique, across all three documents together. */
  it('gives every section a unique id across the three documents', () => {
    const ids = LEGAL_DOCUMENTS.flatMap((entry) =>
      entry.sections.map((section) => legalSectionId(entry.doc, section)),
    );
    expect(new Set(ids).size).toBe(ids.length);
  });
});

describe('MCS-34 D5 — no session in C134 authors legal text', () => {
  /**
   * `lastUpdated` is what the layout keys off: `null` renders the "being prepared"
   * notice, a date renders the document. All three are `null` because no text has
   * been supplied, and a session that fills one in without the text arriving would
   * hide the notice behind an empty page.
   */
  it('has no published document', () => {
    for (const entry of LEGAL_DOCUMENTS) {
      expect(entry.lastUpdated, entry.doc).toBeNull();
    }
  });

  /**
   * **`lastUpdated` is never derived from the clock**, and the source is checked
   * rather than the value, because a build-time date would be *correct* on the day
   * it was written and wrong every day after. A last-updated line that moves on
   * every rebuild tells a reader a legal document changed when it did not.
   *
   * A text search over the source, like every other sweep in this component, and
   * with the same consequence: **it cannot tell a call from a sentence about one.**
   * Both files therefore describe the rule without spelling it, which is the fix
   * this session made twice — the fences suite caught `legal.ts`'s module note in
   * exactly the same way. Weakening either check to ignore comments would trade a
   * guarantee for a comment style.
   */
  it('never dates a document from the clock', async () => {
    for (const file of ['src/content/legal.ts', 'src/components/legal/LegalPage.tsx']) {
      const source = await readFile(join(appRoot, file), 'utf8');
      expect(source, file).not.toMatch(/new Date\(|Date\.now\(/);
    }
  });

  /**
   * The status keys say the document is being prepared. This asserts they still do
   * — specifically, that none of them has quietly become a clause.
   *
   * The threshold is length, which is a crude proxy and deliberately so: a
   * paragraph of "this is being written" is a handful of sentences, and a *policy*
   * is not. Anything that grows past this has stopped being a status note, and
   * whoever wrote it should have set `lastUpdated` and moved it into `sections`.
   */
  it('keeps the status notes to a status note', () => {
    for (const entry of LEGAL_DOCUMENTS) {
      const text = wwwEn[entry.status];
      expect(text, entry.doc).toBeTruthy();
      expect(text.length, `${entry.doc} status note is ${text.length} characters`).toBeLessThan(500);
    }
  });

  /**
   * The two documents that carry sections carry **descriptions of software**, and
   * every one of them cites what it describes — README rule 7, the same rule the
   * FAQ answers and the guide callouts obey. A section with no anchor is either not
   * a fact or not checked, and on these three pages that distinction is the whole
   * argument for their being there at all.
   */
  it('anchors every section to the thing it describes', () => {
    for (const entry of LEGAL_DOCUMENTS) {
      for (const section of entry.sections) {
        expect(section.source, `${entry.doc}/${section.id} cites nothing`).toBeTruthy();
      }
    }
  });

  /**
   * `terms` has no sections at all, and that is the decision rather than an
   * omission: terms of service *are* the policy all the way down, so there is no
   * factual description of software that could stand in for them the way "what this
   * website collects" stands in on the privacy page. A section here would be the
   * generic template S18 rules out, wearing a different heading.
   */
  it('offers nothing in place of terms of service', () => {
    expect(legalDocument('terms').sections).toEqual([]);
  });
});
