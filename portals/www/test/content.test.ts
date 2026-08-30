import { readFile, stat } from 'node:fs/promises';
import { join, resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

import { CHAPTERS, assertRegistryIsWellFormed as assertChaptersWellFormed } from '@/content/index';
import { DAILY_FEE_SOURCE, DAILY_FEE_TIERS } from '@/content/marketing';
import { PAGES } from '@/content/pages';
import {
  SCREENS,
  assertRegistryIsWellFormed as assertScreensWellFormed,
} from '@/content/screens';
import { GUIDE_AUDIENCES } from '@/lib/routes';

/**
 * **The content registries are public factual claims, and this file is where they
 * are held to their sources.**
 *
 * Everything under `src/content/` ends up on a page that anyone can read: a fee a
 * driver will budget around, a screen a prospective driver will judge the app by, a
 * chapter order somebody follows while standing beside their vehicle. The failure
 * mode is not a crash — it is a page that renders perfectly and says something that
 * is no longer true, which nobody notices because there is nothing to notice.
 *
 * So the assertions here are deliberately *external*: the fee tiers are parsed out
 * of the URD, and every screen reference is resolved against the filesystem. A test
 * that restated the constants would pass forever and prove nothing.
 */

const appRoot = resolve(import.meta.dirname, '..');
const repoRoot = resolve(appRoot, '../..');
const screensRoot = join(appRoot, 'public/screens');
const urdPath = join(repoRoot, 'specs/user-requirements-document.md');

describe('the daily platform fee', () => {
  /**
   * **Parsed from `specs/user-requirements-document.md` §1, never restated here.**
   *
   * S20 is explicit and it is the most important rule in this file: *"do not
   * hard-code the expected numbers in the test, or the test and the site drift
   * together and prove nothing."* `portals/admin/test/routes.test.ts` parses
   * `AdminMenu.cs` for the same reason, and this follows it.
   *
   * The URD's table carries eight rows and only six of them are the Mode C daily
   * tiers — Mode A is `Free` and Mode B is a monthly subscription. The parse
   * therefore keys off the **shape of the value** (`Rs N/day`) rather than off a
   * list of vehicle names, so a seventh vehicle type added to the spec appears here
   * as a failure rather than being quietly skipped by a hard-coded name list.
   *
   * The vehicle key is a pure normalisation of the URD's own label — lowercase,
   * non-alphanumerics to `_` — so `Three-wheeler` becomes `three_wheeler` and
   * `Mini Van` becomes `mini_van` with no lookup table anywhere. A mapping table
   * would be exactly the second source of truth this test exists to prevent.
   */
  async function tiersFromUrd(): Promise<Map<string, number>> {
    const urd = await readFile(urdPath, 'utf8');

    const heading = urd.indexOf('### Daily Platform Fee Structure');
    expect(heading, 'the URD must still carry the fee-structure section').toBeGreaterThan(-1);
    const section = urd.slice(heading, urd.indexOf('\n---', heading));

    const tiers = new Map<string, number>();
    for (const line of section.split('\n')) {
      if (!line.startsWith('|')) continue;
      const cells = line.split('|').map((cell) => cell.trim());
      const label = cells[1] ?? '';
      const fee = cells[2] ?? '';

      // `Rs 50/day` — the daily tiers, and nothing else in the table has this shape.
      const daily = /^\*\*Rs\s*([\d,]+)\s*\/\s*day\*\*$/.exec(fee);
      if (!daily) continue;

      const vehicle = /^\*\*(.+?)\*\*/.exec(label)?.[1] ?? '';
      const key = vehicle.toLowerCase().replace(/[^a-z0-9]+/g, '_');
      tiers.set(key, Number(daily[1]?.replace(/,/g, '')));
    }
    return tiers;
  }

  it('matches the URD table, tier for tier', async () => {
    const urdTiers = await tiersFromUrd();

    expect(urdTiers.size, 'the URD must still state six Mode C daily tiers').toBe(6);
    expect(DAILY_FEE_TIERS).toHaveLength(urdTiers.size);

    for (const tier of DAILY_FEE_TIERS) {
      const rupees = urdTiers.get(tier.vehicleType);
      expect(rupees, `${tier.vehicleType} is not a daily tier in the URD`).toBeDefined();

      /*
       * Minor units, per the Universal Rule — the site stores paisa and the URD
       * states rupees, so the comparison is where the ×100 lives. A tier stored in
       * rupees would render as "Rs 0.50 a day".
       */
      expect(tier.dailyFeeMinor, `${tier.vehicleType}`).toBe((rupees ?? 0) * 100);
    }

    for (const vehicle of urdTiers.keys()) {
      expect(
        DAILY_FEE_TIERS.some((tier) => tier.vehicleType === vehicle),
        `the URD states a "${vehicle}" tier that the site does not render`,
      ).toBe(true);
    }
  });

  /**
   * The two rows that must **not** become tiers.
   *
   * Mode A pays nothing and Mode B pays monthly. Both live in the same URD table as
   * the six daily tiers, which is exactly why they are the likely accident: a
   * future parse that keyed off "every row of the table" would render a bus a Rs 0
   * daily fee and a private vehicle a Rs 300 *daily* one — a tenfold overstatement
   * of somebody's costs, on a public page.
   */
  it('keeps the free and the monthly rows out of the daily tiers', async () => {
    const urd = await readFile(urdPath, 'utf8');
    const heading = urd.indexOf('### Daily Platform Fee Structure');
    const section = urd.slice(heading, urd.indexOf('\n---', heading));

    expect(section).toMatch(/\*\*Public Transport Bus\*\*.*\|\s*\*\*Free\*\*/);
    expect(section).toMatch(/\*\*Private Transport\*\*.*\|\s*\*\*~Rs\s*300\/month\*\*/);

    for (const tier of DAILY_FEE_TIERS) {
      expect(tier.vehicleType).not.toBe('public_transport_bus');
      expect(tier.vehicleType).not.toBe('private_transport');
    }
  });

  /**
   * The anchor the page prints beside the table resolves to a real heading.
   *
   * `/drivers` renders `DAILY_FEE_SOURCE` as the citation for these numbers, so a
   * reader — or an auditor — can check them. A citation that points at a heading
   * which has been renamed is worse than none: it looks verifiable and is not.
   */
  it('cites a heading the URD actually has', async () => {
    const [path, anchor] = DAILY_FEE_SOURCE.split('#');
    expect(path).toBe('specs/user-requirements-document.md');

    const urd = await readFile(join(repoRoot, path ?? ''), 'utf8');
    const slugs = [...urd.matchAll(/^#{2,4}\s+(.+)$/gm)].map(([, title]) =>
      (title ?? '')
        .toLowerCase()
        .replace(/[^a-z0-9\s-]/g, '')
        .trim()
        .replace(/\s+/g, '-'),
    );

    expect(slugs, `${DAILY_FEE_SOURCE} names no heading in the URD`).toContain(anchor);
  });
});

describe('the screen registry', () => {
  it('is well formed on its own terms', () => {
    expect(() => assertScreensWellFormed()).not.toThrow();
  });

  /**
   * **Every registry entry resolves to a file that exists.**
   *
   * `scripts/check-bundle.mjs` asserts the same thing over the build, which is the
   * gate that matters on `main`; this runs in a second and names the entry rather
   * than the build step, which is what a session editing the registry wants. Both
   * check 1× only — `@2x` is a `srcset` upgrade whose absence degrades to a softer
   * image, where a missing 1× degrades to a broken `<img>` on a public page.
   */
  it('resolves every entry to an AVIF and a WebP on disk', async () => {
    const missing: string[] = [];

    for (const screen of SCREENS) {
      for (const appearance of screen.appearances) {
        const stem = appearance === 'light' ? screen.file : `${screen.file}--dark`;
        for (const format of ['avif', 'webp']) {
          try {
            await stat(join(screensRoot, `${stem}.${format}`));
          } catch {
            missing.push(`${screen.id} → ${stem}.${format}`);
          }
        }
      }
    }

    expect(missing).toEqual([]);
  });

  /**
   * And every `screenRef` a chapter names resolves to a registry entry.
   *
   * This is the join the guide pages make at render time. A `screenRef` naming a
   * screen that does not exist is a step with a missing illustration — in a
   * *how-to guide*, where the illustration is frequently the instruction.
   */
  it('resolves every chapter screenRef and screen id', () => {
    const ids = new Set(SCREENS.map((screen) => screen.id));
    const dangling: string[] = [];

    for (const chapter of CHAPTERS) {
      for (const id of chapter.screens) {
        if (!ids.has(id)) dangling.push(`${chapter.id}.screens → ${id}`);
      }
      for (const [index, step] of chapter.steps.entries()) {
        if (step.screenRef && !ids.has(step.screenRef)) {
          dangling.push(`${chapter.id}.steps[${index}].screenRef → ${step.screenRef}`);
        }
      }
    }

    expect(dangling).toEqual([]);
  });
});

describe('the chapter registry', () => {
  it('is well formed on its own terms', () => {
    expect(() => assertChaptersWellFormed()).not.toThrow();
  });

  /**
   * Unique slugs and contiguous order, asserted here **as well as** in
   * `assertRegistryIsWellFormed`.
   *
   * Not redundant: that function is the registry checking itself, and a bug in it
   * is invisible to a test that only calls it. These are the same two properties
   * derived independently, so the two have to agree.
   *
   * `order` is the reading order and it drives the previous/next chapter links, so a
   * gap sends a reader from chapter 7 to chapter 9 and a duplicate makes "next"
   * ambiguous. Contiguous from 1, per audience.
   */
  it('gives every chapter a unique slug within its audience', () => {
    for (const audience of GUIDE_AUDIENCES) {
      const slugs = CHAPTERS.filter((chapter) => chapter.audience === audience).map(
        (chapter) => chapter.slug,
      );
      expect(new Set(slugs).size, `${audience} has a duplicate slug`).toBe(slugs.length);
    }
  });

  it('numbers every audience contiguously from 1', () => {
    for (const audience of GUIDE_AUDIENCES) {
      const orders = CHAPTERS.filter((chapter) => chapter.audience === audience)
        .map((chapter) => chapter.order)
        .sort((a, b) => a - b);

      expect(orders.length, `${audience} has no chapters`).toBeGreaterThan(0);
      expect(orders, `${audience} order is not contiguous from 1`).toEqual(
        orders.map((_, index) => index + 1),
      );
    }
  });

  /**
   * A chapter with no steps would render a `HowTo` with no `step` (S19) and a page
   * with a heading over nothing. Cheap to assert, and the failure is silent.
   */
  it('gives every chapter a title, a summary and at least one step', () => {
    for (const chapter of CHAPTERS) {
      expect(chapter.title, chapter.id).toBeTruthy();
      expect(chapter.summary, chapter.id).toBeTruthy();
      expect(chapter.steps.length, `${chapter.id} has no steps`).toBeGreaterThan(0);
    }
  });

  /**
   * The guide index renders one section per audience and reads its heading from
   * `PAGES.guide.sections` **by index**, so the two arrays are an unwritten contract
   * between a copy module and a routing constant.
   *
   * Index alignment between two hand-kept lists is the kind of agreement that holds
   * right up until somebody reorders either one, and the failure is quiet in the
   * worst way: the fleet chapters would render under the heading "Driver guide". S23
   * added the third of each, which is the first time the two arrays could disagree at
   * all — before that a missing entry was `undefined` and fell through to the
   * hardcoded default beside it.
   */
  it('gives the guide index one section heading per audience', () => {
    expect(PAGES.guide?.sections).toHaveLength(GUIDE_AUDIENCES.length);
    for (const section of PAGES.guide?.sections ?? []) {
      expect(section.heading).toBeTruthy();
    }
  });
});

/**
 * **The fleet guide's spec anchors are resolved against the specs themselves.**
 *
 * README rule 7 asks every public claim to carry an anchor, and the reason it gives
 * is that *"the anchor is how the next session checks it is still true"*. An anchor
 * that names no heading cannot do that job — it is a citation to nothing, printed at
 * the foot of a public page under the word "Source".
 *
 * ## Why this is scoped to the six fleet chapters and not to all forty
 *
 * Because the other thirty-four spell their anchors differently, and that is a
 * corpus-wide question rather than S23's to settle in passing. S08–S11 render a
 * dotted section number as `us-19-1` and `1-a-service-modes`; the slugger below —
 * which is `DAILY_FEE_SOURCE`'s own, above, and is what a Markdown renderer actually
 * produces — gives `us-191` and `1a-service-modes`. **Eighty-eight distinct anchors
 * are affected and none of them is wrong about *which* section it means**; they are
 * spelled for a reader rather than for a linker.
 *
 * So S23 wrote anchors that resolve, and asserted the ones it wrote. Widening this
 * to `CHAPTERS` is a real improvement and a mechanical change to thirty-four files —
 * a session of its own, with the choice of convention made deliberately rather than
 * inherited from whichever list happened to get a test first.
 */
describe('the fleet guide’s spec anchors', () => {
  async function headingSlugs(specPath: string): Promise<Set<string>> {
    const source = await readFile(join(repoRoot, specPath), 'utf8');

    return new Set(
      [...source.matchAll(/^#{2,4}\s+(.+)$/gm)].map(([, title]) =>
        (title ?? '')
          .toLowerCase()
          .replace(/[^a-z0-9\s-]/g, '')
          .trim()
          .replace(/\s+/g, '-'),
      ),
    );
  }

  it('names a heading that each spec actually has', async () => {
    const fleet = CHAPTERS.filter((chapter) => chapter.audience === 'fleet');
    expect(fleet.length, 'no fleet chapters — this suite would assert nothing').toBe(6);

    for (const chapter of fleet) {
      const refs = [
        ...chapter.sources,
        ...chapter.callouts.map((callout) => callout.source).filter((s) => s !== undefined),
      ];

      // A chapter with no provenance passes every other check in this file.
      expect(chapter.sources.length, `${chapter.id} cites nothing`).toBeGreaterThan(0);

      for (const ref of refs) {
        const [specPath, anchor] = ref.split('#');
        expect(specPath, `${chapter.id}: "${ref}" has no spec path`).toBeTruthy();
        expect(anchor, `${chapter.id}: "${ref}" has no anchor`).toBeTruthy();

        const slugs = await headingSlugs(specPath ?? '');
        expect(slugs, `${chapter.id}: ${ref} names no heading in ${specPath}`).toContain(anchor);
      }
    }
  });

  /**
   * Every `fee` and every `privacy` callout states a fact — that is what
   * `Callout.source`'s own doc comment says makes the anchor required in practice —
   * and on this guide so does every `warning`, because each of the eight names a gate,
   * a legal requirement or a fee model. Only `tip` may go unsourced, and neither of
   * the two here does.
   */
  it('sources every callout in the fleet guide', () => {
    for (const chapter of CHAPTERS.filter((c) => c.audience === 'fleet')) {
      for (const callout of chapter.callouts) {
        expect(
          callout.source,
          `${chapter.id}: a ${callout.kind} callout states a fact with no anchor`,
        ).toBeTruthy();
      }
    }
  });
});
