/**
 * The WCAG 2.2 AA audit — **S19, and it is a script rather than a session's notes
 * because the two ways it lies are both silent.**
 *
 * Runs axe-core plus a contrast walk over every rendered text node, across six
 * representative pages × both rendered locales × both appearances × a desktop and a
 * 375px phone: **48 page loads**. A33 sets AA as this surface's target, which is a
 * deliberate raise over URD Epic 19 (that covers the apps) and the right one for a
 * public, government-adjacent platform.
 *
 * ## Usage
 *
 *   npm run build --workspace @mageride/www
 *   npx next start -p 3104          # from portals/www
 *   node scripts/check-a11y.mjs     # optionally: MR_A11Y_BASE=http://host:port
 *
 * It needs a **production** build, not `next dev`: dev serves unminified CSS with
 * different cascade behaviour, and the whole point is to measure what ships.
 *
 * ## Two traps that produce a confident, wrong "pass"
 *
 * Both cost S19 a full run, and both look exactly like success.
 *
 * **1 · A stale server serves the previous build.** A `next start` left over from an
 * earlier session keeps the port and answers with HTML whose hashed stylesheet no
 * longer exists in `.next/static`. The page renders completely unstyled, every
 * element measures about 17px tall, and the contrast walk reports a clean sweep —
 * because everything is default black on default white. That is not a pass; it is
 * the absence of any CSS at all. The run now asserts a preset custom property is
 * set and throws if it is not.
 *
 * **2 · Tailwind v4 emits `oklab()` for any colour carrying an alpha modifier.**
 * The sticky header computes to `oklab(0.999994 … / 0.85)` — white at 85%. Pulling
 * the numbers out of that string with a regex reads it as *black*, so every text
 * node in the header reports about 1.1:1 against a background that is actually
 * white: three phantom failures, and the real ones buried under them. Colours are
 * resolved by painting them into a 1×1 canvas, which has no opinion about colour
 * spaces.
 *
 * ## What it is not
 *
 * **axe is the authority on SC 2.5.8, not the raw 24px sweep at the bottom.** That
 * sweep reports every control whose box is under 24×24 and is informational: 88 of
 * them are text links that axe exempts under the inline and spacing rules, because
 * a nav link with clear space around it passes on the undisturbed-circle test. Read
 * it for *non-link* controls, which should be empty. `sr-only` controls are skipped
 * outright — the skip link measures 24×16 hidden and 116×24 focused, which is the
 * only state it has.
 *
 * S20 owns wiring this into CI beside Lighthouse.
 */

/*
 * These are browser globals here on purpose. Every callback passed to
 * `page.evaluate` is serialised and run inside Chromium, not in this Node process,
 * so those lines legitimately touch the DOM while no other line in the file does.
 *
 * Declared per file rather than by giving `scripts/**` the browser globals in
 * `@mageride/eslint-config`, which is `scripts/capture-screens.mjs`'s reasoning and
 * is right: that config is shared by five surfaces, and loosening it platform-wide
 * so one audit script can say `document` is the trade MCS-34 refused when it
 * declined to widen the banned-styling list for one marketing page. Scoped here,
 * the rule still catches a stray `document` in any other Node script.
 */
/* global document, getComputedStyle, NodeFilter, window */
import { chromium } from 'playwright-core';
import { readFile } from 'node:fs/promises';

import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const BASE = process.env.MR_A11Y_BASE ?? 'http://127.0.0.1:3104';
const AXE = await readFile(join(appRoot, '../node_modules/axe-core/axe.min.js'), 'utf8');

const PAGES = [
  ['home', ''],
  ['drivers', 'drivers'],
  ['chapter', 'guide/driver/the-daily-platform-fee'],
  ['screens', 'screens'],
  ['faq', 'faq'],
  ['privacy', 'legal/privacy'],
];
const LOCALES = ['si', 'en'];
const APPEARANCES = ['light', 'dark'];
const VIEWPORTS = [
  ['desktop', { width: 1280, height: 900 }],
  ['phone', { width: 375, height: 780 }],
];

const browser = await chromium.launch();

let violations = 0;
const contrastFailures = new Map();
const smallTargets = new Map();

for (const appearance of APPEARANCES) {
 for (const [vpName, viewport] of VIEWPORTS) {
  const ctx = await browser.newContext({ viewport, colorScheme: appearance });
  const page = await ctx.newPage();

  for (const locale of LOCALES) {
    for (const [name, path] of PAGES) {
      const url = `${BASE}/${locale}${path ? `/${path}` : ''}`;
      await page.goto(url, { waitUntil: 'networkidle' });
      await page.waitForTimeout(150);
      const label = `${vpName}/${appearance}/${locale}/${name || 'home'}`;

      /*
       * The stale-stylesheet guard, and it is here because it already cost a run.
       *
       * A `next start` left over from an earlier session keeps port 3104 and serves
       * the PREVIOUS build's HTML, whose hashed stylesheet no longer exists in
       * `.next/static` — so the page renders completely unstyled, every element
       * measures ~17px tall, and the contrast walk reports a clean sweep because
       * everything is default black on default white. It looks like a pass. It is
       * the absence of any CSS at all.
       */
      const styled = await page.evaluate(() =>
        getComputedStyle(document.documentElement).getPropertyValue('--spacing-cta').trim(),
      );
      if (styled === '') {
        throw new Error(
          `${label}: the page loaded with NO stylesheet applied (--spacing-cta is unset). ` +
            'The server is serving a build that does not match .next/ — kill it and restart ' +
            '`next start` against the current build. Every measurement below would be fiction.',
        );
      }

      // --- axe -------------------------------------------------------------
      await page.addScriptTag({ content: AXE });
      const result = await page.evaluate(async () =>
        // `window.axe` is injected by the `addScriptTag` above, inside Chromium.
        await window.axe.run(document, {
          runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'] },
        }),
      );

      for (const v of result.violations) {
        violations++;
        process.stdout.write(`${`  ✗ [${label}] ${v.id} (${v.impact}) — ${v.nodes.length} node(s)`}\n`);
        process.stdout.write(`${`      ${v.help}`}\n`);
        for (const n of v.nodes.slice(0, 3)) {
          process.stdout.write(`${`      · ${n.target.join(' ')}`}\n`);
          if (n.failureSummary) {
            process.stdout.write(`${`        ${n.failureSummary.split('\n').slice(1, 3).join(' | ').trim()}`}\n`);
          }
        }
      }

      // --- our own contrast walk, over every rendered text node -------------
      const contrast = await page.evaluate(() => {
        /*
         * Resolve any computed colour to sRGB + alpha by painting it.
         *
         * The regex version this replaces — pull the numbers out of the string —
         * is correct for `rgb()`/`rgba()` and catastrophically wrong for anything
         * else. Tailwind v4 emits **`oklab()`** for a colour carrying an alpha
         * modifier, so the site's own sticky header computes to
         * `oklab(0.999994 0.0000456 0.0000201 / 0.85)` — white at 85% — and the
         * regex read it as r=0.999994, g=0.0000456, b=0.0000201: *black*. Every
         * text node in the header then reported about 1.1:1 against a background
         * that is actually white, which is three phantom failures and no real one.
         *
         * A 1x1 canvas has no opinion about colour spaces: fill it and read the
         * pixel back. `getImageData` is non-premultiplied, so the alpha comes back
         * separately and the channels are already the sRGB values WCAG's formula
         * wants.
         */
        const surface = document.createElement('canvas');
        surface.width = 1;
        surface.height = 1;
        const pen = surface.getContext('2d', { willReadFrequently: true });

        const parse = (c) => {
          pen.clearRect(0, 0, 1, 1);
          pen.fillStyle = '#000';
          pen.fillStyle = c;
          // An unparseable value leaves `fillStyle` at the previous one, so a
          // colour this browser cannot read shows up as opaque black rather than
          // as a silent pass.
          pen.fillRect(0, 0, 1, 1);
          const [r, g, b, a] = pen.getImageData(0, 0, 1, 1).data;
          return { r, g, b, a: a / 255 };
        };
        const lum = ({ r, g, b }) => {
          const f = (v) => {
            const s = v / 255;
            return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
          };
          return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
        };
        const over = (fg, bg) => ({
          r: fg.r * fg.a + bg.r * (1 - fg.a),
          g: fg.g * fg.a + bg.g * (1 - fg.a),
          b: fg.b * fg.a + bg.b * (1 - fg.a),
          a: 1,
        });
        const ratio = (a, b) => {
          const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p);
          return (x + 0.05) / (y + 0.05);
        };
        /*
         * The effective background under an element, composited properly.
         *
         * The obvious version — walk up, return the first background whose alpha is
         * non-zero — is wrong, and wrong in the direction that invents failures: a
         * `bg-on-surface/5` scrim returns as *opaque black*, so black-on-white text
         * under it reports about 1.2:1. S19's first run produced three such
         * phantoms in the header and no real finding, which is worse than useless
         * because it buries the real ones.
         *
         * So: collect the whole ancestor chain, then composite from the canvas
         * inward, and fold in each node's `opacity` on the way. Opacity is the part
         * that matters most here — it is the one way to fail contrast without ever
         * writing a failing colour, which is exactly how `.mr-sticky-step` failed.
         */
        const chainOf = (el) => {
          const chain = [];
          for (let n = el; n; n = n.parentElement) chain.push(n);
          return chain;
        };

        const bgOf = (chain) => {
          let acc = { r: 255, g: 255, b: 255, a: 1 };
          for (let i = chain.length - 1; i >= 0; i--) {
            const c = parse(getComputedStyle(chain[i]).backgroundColor);
            const o = Number(getComputedStyle(chain[i]).opacity);
            const a = c.a * (Number.isFinite(o) ? o : 1);
            if (a > 0) acc = over({ ...c, a }, acc);
          }
          return acc;
        };

        const opacityOf = (chain) =>
          chain.reduce((product, n) => {
            const o = Number(getComputedStyle(n).opacity);
            return product * (Number.isFinite(o) ? o : 1);
          }, 1);

        const out = [];
        const seen = new Set();
        const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
        let node;
        while ((node = walker.nextNode())) {
          const text = (node.textContent ?? '').trim();
          if (!text) continue;
          const el = node.parentElement;
          if (!el || !el.checkVisibility?.({ contentVisibilityAuto: true, opacityProperty: true, visibilityProperty: true })) continue;

          const style = getComputedStyle(el);
          const size = parseFloat(style.fontSize);
          const weight = Number(style.fontWeight) || 400;
          const large = size >= 24 || (size >= 18.66 && weight >= 700);
          const required = large ? 3 : 4.5;

          const chain = chainOf(el);
          const bg = bgOf(chain);
          const raw = parse(style.color);
          const fg = { ...raw, a: raw.a * opacityOf(chain) };
          const r = ratio(over(fg, bg), bg);

          const key = `${style.color}|${style.backgroundColor}|${size}|${weight}|${opacityOf(chain).toFixed(2)}`;
          if (seen.has(key)) continue;
          seen.add(key);

          if (r < required - 0.005) {
            out.push({
              ratio: Number(r.toFixed(2)),
              required,
              color: style.color,
              background: `rgb(${Math.round(bg.r)}, ${Math.round(bg.g)}, ${Math.round(bg.b)})`,
              size: `${size}px/${weight}`,
              sample: text.slice(0, 45),
              selector: `${el.tagName.toLowerCase()}${el.className && typeof el.className === 'string' ? '.' + el.className.split(/\s+/).slice(0, 3).join('.') : ''}`,
            });
          }
        }
        return out;
      });

      for (const f of contrast) {
        const key = `${f.color} on ${f.background} @ ${f.size} — ${f.ratio}:1 (needs ${f.required})`;
        if (!contrastFailures.has(key)) contrastFailures.set(key, []);
        contrastFailures.get(key).push(`${label} · ${f.selector} · "${f.sample}"`);
      }

      // --- touch targets: WCAG 2.2 SC 2.5.8 ---------------------------------
      const targets = await page.evaluate(() => {
        const out = [];
        for (const el of document.querySelectorAll('a[href], button, summary, [role="button"]')) {
          if (!el.checkVisibility?.()) continue;
          /*
           * `sr-only` controls are not pointer targets.
           *
           * The skip link measures 24x16 here and 116x24 the moment it is focused,
           * which is the only state in which it exists. `sr-only` clips it to a 1px
           * box while leaving the layout box `getBoundingClientRect` reports, so it
           * reads as a small target and is not a target at all. Checked, not
           * assumed: it clears 24x24 focused.
           */
          if (el.classList.contains('sr-only')) continue;
          const r = el.getBoundingClientRect();
          if (r.width === 0 || r.height === 0) continue;
          if (r.width < 24 || r.height < 24) {
            out.push({
              tag: el.tagName.toLowerCase(),
              size: `${Math.round(r.width)}x${Math.round(r.height)}`,
              text: (el.textContent ?? '').trim().slice(0, 30) || el.getAttribute('aria-label') || '',
            });
          }
        }
        return out;
      });
      for (const t of targets) {
        const key = `${t.tag} ${t.size} "${t.text}"`;
        if (!smallTargets.has(key)) smallTargets.set(key, []);
        smallTargets.get(key).push(label);
      }

      // --- structure --------------------------------------------------------
      const structure = await page.evaluate(() => {
        const headings = [...document.querySelectorAll('main h1,main h2,main h3,main h4,main h5,main h6')]
          .filter((h) => h.checkVisibility?.())
          .map((h) => Number(h.tagName[1]));
        let skip = null;
        for (let i = 1; i < headings.length; i++) {
          if (headings[i] - headings[i - 1] > 1) skip = `h${headings[i - 1]} → h${headings[i]}`;
        }
        return {
          h1: document.querySelectorAll('main h1').length,
          skip,
          main: document.querySelectorAll('main').length,
          navsNamed: [...document.querySelectorAll('nav')].every(
            (n) => n.getAttribute('aria-label') || n.getAttribute('aria-labelledby'),
          ),
          htmlLang: document.documentElement.lang,
        };
      });

      if (structure.h1 !== 1) { violations++; process.stdout.write(`${`  ✗ [${label}] ${structure.h1} <h1> in <main>`}\n`); }
      if (structure.skip) { violations++; process.stdout.write(`${`  ✗ [${label}] heading level skipped: ${structure.skip}`}\n`); }
      if (structure.main !== 1) { violations++; process.stdout.write(`${`  ✗ [${label}] ${structure.main} <main> landmarks`}\n`); }
      if (!structure.navsNamed) { violations++; process.stdout.write(`${`  ✗ [${label}] an unnamed <nav>`}\n`); }
      if (structure.htmlLang !== locale) { violations++; process.stdout.write(`${`  ✗ [${label}] html lang="${structure.htmlLang}"`}\n`); }
    }
  }
  await ctx.close();
 }
}

process.stdout.write(`${'\n--- contrast failures (deduplicated by colour + size) ---'}\n`);
if (contrastFailures.size === 0) process.stdout.write(`${'  none'}\n`);
for (const [key, where] of contrastFailures) {
  process.stdout.write(`${`  ✗ ${key}`}\n`);
  process.stdout.write(`${`      ${where[0]}   (${where.length} occurrence pattern(s))`}\n`);
}

process.stdout.write(`${'\n--- targets under 24x24 (WCAG 2.2 SC 2.5.8 minimum) ---'}\n`);
if (smallTargets.size === 0) process.stdout.write(`${'  none'}\n`);
for (const [key, where] of smallTargets) process.stdout.write(`${`  ✗ ${key} — ${where[0]}`}\n`);

process.stdout.write(`${`\naxe + structure violations: ${violations}`}\n`);
process.stdout.write(`${`contrast patterns failing: ${contrastFailures.size}`}\n`);
process.stdout.write(`${`small targets: ${smallTargets.size}`}\n`);

await browser.close();

/*
 * The exit code counts the two authoritative numbers only. The 24px sweep is
 * informational — see the header — so it is printed and not gated, or the run
 * would be permanently red over links WCAG exempts.
 */
if (violations > 0 || contrastFailures.size > 0) {
  process.stderr.write(
    `\naccessibility: ${violations} axe/structure violation(s) and ${contrastFailures.size} ` +
      'contrast pattern(s) across 48 page loads. WCAG 2.2 AA is the target (A33).\n',
  );
  process.exit(1);
}

process.stdout.write('\naccessibility: WCAG 2.2 AA clean across 48 page loads.\n');
