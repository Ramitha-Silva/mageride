import { describe, expect, it } from 'vitest';

import {
  FAQ,
  FAQ_GROUPS,
  assertFaqIsWellFormed,
  faqAnchorId,
  faqById,
  faqFor,
  faqGroup,
} from '@/content/faq';
import { CHAPTERS } from '@/content/index';

/**
 * `/faq` — the properties the page depends on and the markup cannot state.
 *
 * The accordion itself is native `<details>` in a server component, so "the answer
 * is in the DOM when the item is closed" is a property of HTML rather than
 * something to assert here. What is worth asserting is the *data*: that the three
 * groups partition the corpus, that every id is unique and stays a usable anchor,
 * and that the entry S18 was handed actually landed.
 */

describe('the corpus', () => {
  it('is well formed', () => {
    expect(() => assertFaqIsWellFormed()).not.toThrow();
  });

  /**
   * **`FAQ_GROUPS` partitions; `faqFor` overlaps.** The page must use the first.
   *
   * `faqFor('passenger')` returns the passenger entries *plus* the shared ones —
   * right for a role landing page showing one subset, and wrong for `/faq`, where
   * rendering both audiences that way would put every `both` entry on the page
   * twice under two duplicate `id="faq-…"` attributes. That breaks the deep link,
   * the document outline, and the `FAQPage` JSON-LD S19 builds from the same data,
   * and it breaks all three silently.
   */
  it('is partitioned by the three groups — every entry once, in one section', () => {
    const grouped = FAQ_GROUPS.flatMap((group) => faqGroup(group.audience));

    expect(grouped).toHaveLength(FAQ.length);
    expect(new Set(grouped.map((entry) => entry.id)).size).toBe(FAQ.length);
  });

  it('is not what `faqFor` returns, and the difference is the overlap', () => {
    const shared = faqGroup('both');
    expect(shared.length).toBeGreaterThan(0);

    for (const entry of shared) {
      expect(faqFor('passenger')).toContain(entry);
      expect(faqFor('driver')).toContain(entry);
    }
  });

  it('keeps registry order inside a group', () => {
    for (const group of FAQ_GROUPS) {
      const entries = faqGroup(group.audience);
      const expected = FAQ.filter((entry) => entry.audience === group.audience);
      expect(entries.map((entry) => entry.id)).toEqual(expected.map((entry) => entry.id));
    }
  });
});

describe('deep links', () => {
  it('gives every entry a unique anchor', () => {
    const anchors = FAQ.map((entry) => faqAnchorId(entry.id));
    expect(new Set(anchors).size).toBe(FAQ.length);
  });

  /**
   * The anchor goes in a URL that a reader pastes into a message, so it has to
   * survive being typed, copied and lower-cased. An id needing percent-encoding
   * would still work and would look broken in the address bar, which on a shared
   * link is the same thing.
   */
  it('keeps every anchor URL-safe as written', () => {
    for (const entry of FAQ) {
      const anchor = faqAnchorId(entry.id);
      expect(anchor, entry.id).toMatch(/^faq-[a-z0-9-]+$/);
      expect(encodeURIComponent(anchor)).toBe(anchor);
    }
  });
});

describe('URD Epic 19 — the coverage gap S11 found and S17 handed on', () => {
  /**
   * S11's handoff named accessibility as the corpus's one real gap (US-19.1
   * TalkBack, US-19.2 system text size) and S17's named `/faq` as its home: *"if
   * S18 does not close it, it is a Definition-of-Done gap against 'every passenger
   * and driver capability'."* This is the entry, and it is asserted rather than
   * merely added, because a question deleted in a later tidy-up would reopen a gap
   * two sessions went looking for.
   */
  it('has an accessibility entry, shown to both audiences', () => {
    const entry = faqById('accessibility');

    expect(entry, 'the Epic 19 FAQ entry is missing').toBeDefined();
    expect(entry?.audience).toBe('both');
    expect(entry?.refs).toContain('specs/user-requirements-document.md#us-19-1');
    expect(entry?.refs).toContain('specs/user-requirements-document.md#us-19-2');
  });

  /**
   * And it stays in the FAQ rather than migrating into a chapter. There is nothing
   * to *do* in the app — TalkBack is switched on in Android's settings — so a
   * chapter would be a page of steps that are not steps.
   */
  it('is not duplicated as a guide chapter', () => {
    expect(CHAPTERS.some((chapter) => chapter.slug.includes('accessib'))).toBe(false);
  });
});
