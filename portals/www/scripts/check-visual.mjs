/**
 * Visual regression — **the one class of change nothing else here catches** (S20).
 *
 * ## Usage
 *
 *   npm run build --workspace @mageride/www
 *   npx next start -p 3104                          # from portals/www
 *   npm run visual         --workspace @mageride/www
 *   npm run visual:update  --workspace @mageride/www   # re-baseline, after review
 *
 * ## What it is for
 *
 * Every other check in this component asserts something *nameable*: a key exists, a
 * route resolves, a byte count, a contrast ratio, an ARIA attribute. None of them
 * can see the failure S20 names — **a token edit in `@mageride/tailwind-preset`
 * silently reflowing this site.** That preset is a platform-wide contract shared by
 * five surfaces; a session tuning a spacing step for the admin portal has no reason
 * to open this directory, the build stays green, every test passes, and the hero
 * moves 40px. This is the only check that would notice.
 *
 * It is therefore deliberately *narrow*: six frames, chosen because they are where
 * the preset's type scale, spacing scale and dark palette all land at once.
 *
 * ## Why it is not in CI
 *
 * It drives a browser, and **A17's fence is that no portal build downloads one**:
 * `test/fences.test.ts` requires `playwright-core` (which ships no browser) and
 * refuses `playwright`, `@playwright/test` and `puppeteer` (which all fetch one on
 * install). CI would have to install a browser to run this, on every portal push,
 * for a check whose failure needs a human to look at a picture anyway. So it is a
 * local gate, run by the session that changes styling — and `lighthouse.yml` is the
 * separate workflow for the thing that *does* belong on a runner.
 *
 * The diff is `sharp` and not `pixelmatch`: `sharp` is already a devDependency for
 * the screen compositor, and one more dependency for a subtraction is not a trade
 * worth making on a surface with this many fences about dependencies.
 *
 * ## When it fails
 *
 * **Review the diff, then re-baseline. Do not delete the test.** A failure is this
 * script working. `.visual/diff-*.png` is written for every mismatch — the changed
 * pixels in red over a dimmed original — so "what moved" is a picture rather than a
 * number. If the change was intended, `npm run visual:update` and commit the new
 * baselines *in the same change as the styling edit*, which is what makes the diff
 * reviewable.
 */

/*
 * Browser globals, and only inside `page.evaluate` — those callbacks are serialised
 * and run in Chromium, not in this Node process. Declared per file rather than by
 * loosening `@mageride/eslint-config` for `scripts/**`, which is
 * `scripts/capture-screens.mjs`'s reasoning and is right: that config is shared by
 * five surfaces, and the rule still catches a stray `document` in any other script.
 */
/* global document, getComputedStyle */

import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { chromium } from 'playwright-core';
import sharp from 'sharp';

const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const BASE = process.env.MR_VISUAL_BASE ?? 'http://127.0.0.1:3104';
const BASELINE_DIR = join(appRoot, 'test/visual');
const OUTPUT_DIR = join(appRoot, '.visual');
const UPDATE = process.argv.includes('--update');

/**
 * 375 wide — D2's smallest breakpoint and A34's target device — at `deviceScaleFactor`
 * 1, clipped to the first 900px.
 *
 * Every one of those three is a size decision. A 2x capture quadruples the baseline
 * for no extra signal, since a reflow of any consequence is visible at 1x. A full-page
 * capture would make every frame's height depend on content further down, so adding a
 * paragraph to chapter 12 would "fail" the hero. And 375 is where the type scale is
 * tightest, so it is where a token change shows first — the same reason S12 measured
 * the Sinhala leading collision at 375 and not at 1024.
 */
const VIEWPORT = { width: 375, height: 900 };

/**
 * Six frames.
 *
 * The home hero carries the display scale, the aurora backdrop and the carousel; a
 * guide chapter carries body type, the step list and a screen plate. Both in both
 * appearances, because the dark palette is a separate set of tokens that nothing
 * else in the suite renders. Sinhala rather than English on one chapter on purpose:
 * `--mr-www-leading-hero` is re-stated for `html[lang='si']` (S12) and a change to
 * that ramp is invisible on an English page.
 */
const FRAMES = [
  { id: 'home-en', path: '/en', appearance: 'light' },
  { id: 'home-en-dark', path: '/en', appearance: 'dark' },
  { id: 'home-si', path: '/si', appearance: 'light' },
  { id: 'chapter-en', path: '/en/guide/driver/approval', appearance: 'light' },
  { id: 'chapter-en-dark', path: '/en/guide/driver/approval', appearance: 'dark' },
  { id: 'chapter-si', path: '/si/guide/driver/approval', appearance: 'light' },
];

/** Per-pixel tolerance, and the share of pixels allowed to exceed it. */
const CHANNEL_TOLERANCE = 12;
const MAX_DIFFERING_RATIO = 0.002;

await mkdir(BASELINE_DIR, { recursive: true });
await mkdir(OUTPUT_DIR, { recursive: true });

const browser = await chromium.launch({ args: ['--no-sandbox', '--disable-dev-shm-usage'] });

/** @type {string[]} */
const failures = [];
/** @type {string[]} */
const written = [];

try {
  for (const frame of FRAMES) {
    const context = await browser.newContext({
      viewport: VIEWPORT,
      colorScheme: frame.appearance,
      /*
       * Animations are the enemy of a stable baseline: the aurora backdrop, the
       * marquee and the carousel are all mid-flight at an arbitrary moment. Reduced
       * motion is not a workaround for that — it is a *supported reader setting*
       * that this surface honours everywhere (A12), so capturing under it is
       * capturing a real rendering rather than a doctored one.
       */
      reducedMotion: 'reduce',
    });
    const page = await context.newPage();
    await page.goto(`${BASE}${frame.path}`, { waitUntil: 'networkidle' });

    // The stylesheet must have applied, or the "baseline" is an unstyled page — the
    // failure that cost S19 a full audit run. Cheap to assert, impossible to spot.
    const styled = await page.evaluate(() =>
      getComputedStyle(document.documentElement).getPropertyValue('--spacing-cta').trim(),
    );
    if (styled === '') {
      throw new Error(
        `${frame.id}: the page loaded with no stylesheet applied. The server is serving a ` +
          'build that does not match .next/ — kill it and restart `next start`.',
      );
    }

    await page.evaluate(() => document.fonts.ready);
    const shot = await page.screenshot({ clip: { x: 0, y: 0, ...VIEWPORT } });
    await context.close();

    const baselinePath = join(BASELINE_DIR, `${frame.id}.png`);

    if (UPDATE) {
      await writeFile(baselinePath, shot);
      written.push(frame.id);
      continue;
    }

    let baseline;
    try {
      baseline = await readFile(baselinePath);
    } catch {
      failures.push(
        `${frame.id}: no baseline at test/visual/${frame.id}.png. Run ` +
          '`npm run visual:update` and commit it with the change that needs it.',
      );
      continue;
    }

    const [a, b] = await Promise.all(
      [baseline, shot].map((buffer) =>
        sharp(buffer).ensureAlpha().raw().toBuffer({ resolveWithObject: true }),
      ),
    );

    if (a.info.width !== b.info.width || a.info.height !== b.info.height) {
      failures.push(
        `${frame.id}: size changed — baseline ${a.info.width}x${a.info.height}, ` +
          `now ${b.info.width}x${b.info.height}`,
      );
      continue;
    }

    const { width, height } = a.info;
    const mask = Buffer.alloc(width * height * 4);
    let differing = 0;

    for (let pixel = 0; pixel < width * height; pixel++) {
      const at = pixel * 4;
      const delta = Math.max(
        Math.abs(a.data[at] - b.data[at]),
        Math.abs(a.data[at + 1] - b.data[at + 1]),
        Math.abs(a.data[at + 2] - b.data[at + 2]),
      );

      if (delta > CHANNEL_TOLERANCE) {
        differing++;
        mask[at] = 255;
        mask[at + 1] = 0;
        mask[at + 2] = 0;
        mask[at + 3] = 255;
      } else {
        // The unchanged page, dimmed, so the red has something to sit on.
        mask[at] = 255 - (255 - b.data[at]) / 4;
        mask[at + 1] = 255 - (255 - b.data[at + 1]) / 4;
        mask[at + 2] = 255 - (255 - b.data[at + 2]) / 4;
        mask[at + 3] = 255;
      }
    }

    const ratio = differing / (width * height);
    if (ratio > MAX_DIFFERING_RATIO) {
      const diffPath = join(OUTPUT_DIR, `diff-${frame.id}.png`);
      await sharp(mask, { raw: { width, height, channels: 4 } }).png().toFile(diffPath);
      await writeFile(join(OUTPUT_DIR, `actual-${frame.id}.png`), shot);

      failures.push(
        `${frame.id}: ${(ratio * 100).toFixed(2)}% of pixels moved (limit ` +
          `${(MAX_DIFFERING_RATIO * 100).toFixed(2)}%) — see .visual/diff-${frame.id}.png`,
      );
    }
  }
} finally {
  await browser.close();
}

if (UPDATE) {
  const sizes = await readdir(BASELINE_DIR);
  process.stdout.write(
    `visual: re-baselined ${written.length} frame(s) — ${written.join(', ')}\n` +
      `        ${sizes.length} file(s) in test/visual/. Commit them with the change that moved them.\n`,
  );
  process.exit(0);
}

if (failures.length > 0) {
  process.stderr.write(
    'Visual regression failed:\n\n' +
      failures.map((failure) => `  ${failure}\n`).join('') +
      '\n  Review the diff, then re-baseline — do not delete the test. A failure here is\n' +
      '  usually a `@mageride/tailwind-preset` token change reaching this surface, which is\n' +
      '  exactly what it exists to surface. `npm run visual:update` after review, and commit\n' +
      '  the baselines in the same change as the styling edit.\n',
  );
  process.exit(1);
}

process.stdout.write(`visual: ${FRAMES.length} frames match their baselines.\n`);
