/**
 * The chapter registry — the **only** way a guide chapter is reached.
 *
 * `generateStaticParams`, `app/sitemap.ts` and the guide index all read this
 * module, which inverts the usual failure the same way `src/lib/routes.ts` does for
 * routes: **a chapter that exists on disk and is not registered here is a test
 * failure**, not a page nobody links to. `test/content.test.ts` (S20) asserts that
 * every file under `src/content/guide/**` appears in {@link CHAPTERS}, so a chapter
 * cannot be written, translated, and then silently never published.
 *
 * **S08 filled the first eight and S09 the second eight, so the passenger guide is
 * complete at 16. S10 added driver chapters 1–9 and S11 the remaining nine**, and
 * **S23 added the six fleet chapters MCS-34 D7 made conditional** — so the corpus is
 * complete at **40 chapters**, past the Definition of Done's "34+ chapters cover
 * every passenger and driver capability across URD Epics 1–27" and now covering the
 * third end-user role as well. Each session adds its modules here in the same change.
 *
 * A partially filled registry is a normal state rather than a broken one, and
 * {@link assertRegistryIsWellFormed} is written for it: it checks that `order` runs
 * `1..n` **within what exists**, so sixteen passenger chapters and no driver chapter
 * is well-formed and sixteen numbered 1–15 and 17 is not.
 * `app/[locale]/guide/{passenger,driver}/[chapter]/
 * page.tsx` still publishes nothing until S17 lists the chapters in
 * `src/lib/routes.ts` — written and published are two steps, deliberately.
 *
 * The slug lists in `./chapters` are the *contract* (what may exist); this is the
 * *inventory* (what does). They are separate because S05 needed the contract before
 * any chapter existed, and because a slug the registry has not filled yet is a
 * planned chapter rather than a broken one.
 */

import {
  DRIVER_CHAPTER_SLUGS,
  FLEET_CHAPTER_SLUGS,
  PASSENGER_CHAPTER_SLUGS,
  type GuideChapterRef,
} from './chapters';
import { p01 } from './guide/passenger/p01';
import { p02 } from './guide/passenger/p02';
import { p03 } from './guide/passenger/p03';
import { p04 } from './guide/passenger/p04';
import { p05 } from './guide/passenger/p05';
import { p06 } from './guide/passenger/p06';
import { p07 } from './guide/passenger/p07';
import { p08 } from './guide/passenger/p08';
import { p09 } from './guide/passenger/p09';
import { p10 } from './guide/passenger/p10';
import { p11 } from './guide/passenger/p11';
import { p12 } from './guide/passenger/p12';
import { p13 } from './guide/passenger/p13';
import { p14 } from './guide/passenger/p14';
import { p15 } from './guide/passenger/p15';
import { p16 } from './guide/passenger/p16';
import { d01 } from './guide/driver/d01';
import { d02 } from './guide/driver/d02';
import { d03 } from './guide/driver/d03';
import { d04 } from './guide/driver/d04';
import { d05 } from './guide/driver/d05';
import { d06 } from './guide/driver/d06';
import { d07 } from './guide/driver/d07';
import { d08 } from './guide/driver/d08';
import { d09 } from './guide/driver/d09';
import { d10 } from './guide/driver/d10';
import { d11 } from './guide/driver/d11';
import { d12 } from './guide/driver/d12';
import { d13 } from './guide/driver/d13';
import { d14 } from './guide/driver/d14';
import { d15 } from './guide/driver/d15';
import { d16 } from './guide/driver/d16';
import { d17 } from './guide/driver/d17';
import { d18 } from './guide/driver/d18';
import { f01 } from './guide/fleet/f01';
import { f02 } from './guide/fleet/f02';
import { f03 } from './guide/fleet/f03';
import { f04 } from './guide/fleet/f04';
import { f05 } from './guide/fleet/f05';
import { f06 } from './guide/fleet/f06';
import type { Chapter } from './types';

/**
 * Every published chapter, in reading order.
 *
 * S08–S11 push their modules in here. Order within an audience is
 * {@link Chapter.order}; the array order is what the guide index renders, and
 * {@link assertRegistryIsWellFormed} holds the two to each other so they cannot
 * disagree.
 */
export const CHAPTERS: readonly Chapter[] = [
  p01,
  p02,
  p03,
  p04,
  p05,
  p06,
  p07,
  p08,
  p09,
  p10,
  p11,
  p12,
  p13,
  p14,
  p15,
  p16,
  d01,
  d02,
  d03,
  d04,
  d05,
  d06,
  d07,
  d08,
  d09,
  d10,
  d11,
  d12,
  d13,
  d14,
  d15,
  d16,
  d17,
  d18,
  f01,
  f02,
  f03,
  f04,
  f05,
  f06,
];

/** Chapters for one audience, in reading order. */
export function chaptersFor(audience: Chapter['audience']): readonly Chapter[] {
  return CHAPTERS.filter((chapter) => chapter.audience === audience).toSorted(
    (a, b) => a.order - b.order,
  );
}

/**
 * A chapter by audience and slug, or `undefined`.
 *
 * Keyed by both halves because the two guides genuinely share slugs —
 * `install-and-first-run` is chapter 1 of each — so a lookup by slug alone would
 * return whichever happened to be registered first.
 */
export function chapterBySlug(
  audience: Chapter['audience'],
  slug: string,
): Chapter | undefined {
  return CHAPTERS.find((chapter) => chapter.audience === audience && chapter.slug === slug);
}

/** A chapter by its {@link Chapter.id} — what `relatedChapters` resolves through. */
export function chapterById(id: string): Chapter | undefined {
  return CHAPTERS.find((chapter) => chapter.id === id);
}

/** `audience/slug` for a chapter — the form `screens.ts` tags entries with. */
export function refFor(chapter: Chapter): string {
  return `${chapter.audience}/${chapter.slug}`;
}

/**
 * The inverse of {@link refFor} — a chapter from the `audience/slug` string the
 * screen registry tags entries with.
 *
 * Added in S18, because `/screens` is the first page to go the *other* way: a
 * gallery tile has a screen and needs the chapters that show it, by title and by
 * URL. Splitting the ref at the call site would be a `split('/')` in a page file
 * and a second place that knows the ref's grammar — and the grammar is not quite
 * obvious, since a slug may itself contain hyphens but never a slash.
 *
 * `undefined` for a ref naming a chapter that is not registered. With S23 landed
 * every ref `GuideChapterRef` admits now resolves, so this is unreachable through the
 * type — it stays `undefined`-returning rather than throwing because the callers
 * start from a `string` (a URL segment, a test) and skipping is the right answer for
 * an unregistered one.
 */
export function chapterByRef(ref: string): Chapter | undefined {
  const separator = ref.indexOf('/');
  if (separator === -1) return undefined;

  return chapterBySlug(
    ref.slice(0, separator) as Chapter['audience'],
    ref.slice(separator + 1),
  );
}

/**
 * The registry's invariants, as one callable assertion.
 *
 * Shared between `test/content.test.ts` and any script that reads chapters, so a
 * rule enforced in only one of the two cannot be violated by the other — the same
 * arrangement `screens.ts` uses. Throws rather than collecting findings: every one
 * of these is a typo, and there is nothing to do with a half-valid registry.
 */
export function assertRegistryIsWellFormed(): void {
  const seenIds = new Set<string>();
  const seenRefs = new Set<string>();

  const allowed: Record<string, readonly string[]> = {
    passenger: PASSENGER_CHAPTER_SLUGS,
    driver: DRIVER_CHAPTER_SLUGS,
    // S23 landed (MCS-34 D7 = yes, second delivery phase) and brought the fleet
    // slug list with it. This map is now total over `Chapter['audience']`.
    fleet: FLEET_CHAPTER_SLUGS,
  };

  for (const chapter of CHAPTERS) {
    if (seenIds.has(chapter.id)) {
      throw new Error(`content: duplicate chapter id "${chapter.id}"`);
    }
    seenIds.add(chapter.id);

    const ref = refFor(chapter);
    if (seenRefs.has(ref)) {
      throw new Error(`content: two chapters resolve to "${ref}" — one URL, two documents`);
    }
    seenRefs.add(ref);

    if (!allowed[chapter.audience]?.includes(chapter.slug)) {
      throw new Error(
        `content: "${ref}" is not in the ${chapter.audience} slug list. Chapter slugs are a ` +
          'published contract (src/content/chapters.ts) — add it there first, deliberately.',
      );
    }
  }

  // `order` is the reading order and drives "next chapter" links, so a gap or a
  // repeat is a chapter nobody can reach by reading forwards.
  for (const audience of Object.keys(allowed)) {
    const orders = CHAPTERS.filter((c) => c.audience === audience)
      .map((c) => c.order)
      .toSorted((a, b) => a - b);

    orders.forEach((order, index) => {
      if (order !== index + 1) {
        throw new Error(
          `content: ${audience} chapter order is ${orders.join(', ')} — it must run 1..n with ` +
            'no gap and no repeat, because it is what "next chapter" walks.',
        );
      }
    });
  }

  // Cross-references must resolve. A `relatedChapters` entry naming a chapter that
  // does not exist yet renders as a dead link on a public page.
  for (const chapter of CHAPTERS) {
    for (const related of chapter.relatedChapters) {
      if (!seenIds.has(related)) {
        throw new Error(`content: "${chapter.id}" relates to "${related}", which is not registered`);
      }
    }
  }
}

/** Every chapter reference the registry actually publishes today. */
export function publishedRefs(): readonly GuideChapterRef[] {
  return CHAPTERS.map((chapter) => refFor(chapter) as GuideChapterRef);
}
