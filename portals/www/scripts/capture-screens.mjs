/**
 * Capture every frame in `src/content/screens.ts` from `specs/wireframes/*.html`.
 *
 * MCS-34 D10: the site's screen imagery is rendered from the team-approved
 * wireframes through a polish stylesheet, not photographed from a shipped app —
 * iOS does not build on this Linux host and every real shot would need seeded state
 * (`CLAUDE.md`, Build Host). This script is the first half of that: it writes raw
 * PNGs to `.screens-raw/`, and S06's compositor turns them into the committed
 * artefacts under `public/screens/`.
 *
 * Run it with `npm run screens:capture`. It is **not** wired into `build`: CI must
 * never need a browser.
 *
 *     node scripts/capture-screens.mjs [--only <id-or-prefix>] [--no-polish]
 *
 * ## Read-only, and strictly
 *
 * `specs/` is the source of truth and this script opens it `file://`. It writes
 * only to `.screens-raw/`, which is gitignored. `git status --porcelain specs/`
 * after a run must be empty, and C134's Verify line checks exactly that.
 *
 * ## How a frame is found
 *
 * Not by `id` — there is none, and not by `.cell` either, because two of the seven
 * files do not have one. `docs/www-site-plan.md` §A15 assumed an `SCR-*` anchor;
 * `C134-www/README.md` §4.1 corrected that to "the `.cell` whose `.cap .scr` text
 * equals the ID". Both are wrong for `web_admin` and `web_fleet`, where the caption
 * and the frame are **flat siblings inside one `.wrap`** with no per-screen
 * wrapper at all:
 *
 *     <div class="wrap">
 *       <div class="cap"><span class="scr">SCR-AP-002</span> · … </div>
 *       <div class="browser"> … the frame … </div>
 *       <div class="states"> … </div>
 *
 * So the locator used here is the one shape both layouts share: find the `.cap`
 * whose `.scr` text is the ID, then walk **forward through its siblings** to the
 * first `.phone` / `.browser` / `.mweb`, stopping at the next `.cap` so a screen
 * with no frame can never silently borrow the next screen's. Verified against all
 * seven files: 202 captions, 202 frames, no misses.
 *
 * ## Geometry, measured rather than assumed
 *
 * | frame     | files                    | rendered size                 |
 * |-----------|--------------------------|-------------------------------|
 * | `.phone`  | the four app files       | 320 × 680, 9px bezel          |
 * | `.mweb`   | `web_passenger`          | 330 × 616                     |
 * | `.browser`| `web_admin`, `web_fleet` | 944 wide, **height 440–1037** |
 *
 * The plan's 375 × 812 and 1440 × 900 are both wrong, and portal height varies per
 * *screen* rather than per file — S06 cannot composite the portal frames into one
 * fixed mockup the way it can the phones.
 *
 * ## Determinism
 *
 * Re-running produces byte-identical PNGs for an unchanged wireframe, on this host.
 * What that costs:
 *
 * - `deviceScaleFactor: 3`, fixed viewport per frame kind, and a screenshot clipped
 *   to the element's own box.
 * - `prefers-reduced-motion: reduce`, plus `polish.css` killing every `animation`
 *   and `transition` outright — several frames animate with plain declarations that
 *   the media query alone does not stop.
 * - Both D2 faces pinned to files in `node_modules` and awaited via
 *   `document.fonts.ready`. Without that the captures render in whatever sans the
 *   host happens to have — DejaVu on this box — which is a fidelity gap *and* makes
 *   the output differ per machine.
 * - No timestamp, no random id and no network: `file://` only.
 *
 * ## The polish fence, enforced rather than asserted
 *
 * `polish.css` may lift type, elevation and radii; it may **not** move, add or
 * remove a control, because these frames are the approved structural baseline and a
 * picture showing a control the app does not have is a false public claim. Rather
 * than trust that, every capture measures each control's bounding box before and
 * after the stylesheet is injected and fails if one moves. See {@link CONTROLS}.
 */

/*
 * `document` is a browser global here on purpose. The callbacks passed to
 * `page.evaluate` / `page.evaluateHandle` are serialised and executed inside
 * Chromium, not in this Node process — so they legitimately touch the DOM while
 * every other line in the file does not.
 *
 * Declared per file rather than by giving `scripts/**` the browser globals in
 * `@mageride/eslint-config`: that config is shared by five surfaces, and loosening
 * it platform-wide so one screenshot script can say `document` is the same trade
 * MCS-34 refused when it declined to widen the banned-styling list for one
 * marketing page. Scoped here, the rule still catches a stray `document` in any
 * other Node script.
 */
/* global document */

import { mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { chromium } from 'playwright-core';

import { appearanceCss } from './wireframe-appearances.mjs';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const appRoot = resolve(scriptDir, '..');
const repoRoot = resolve(appRoot, '../..');
const wireframeDir = join(repoRoot, 'specs/wireframes');
const outputDir = join(appRoot, '.screens-raw');

/** Rendered at 3× — D2's own top raster density, and what S06 downsamples from. */
const DEVICE_SCALE_FACTOR = 3;

/**
 * Viewport per frame kind. Wide and tall enough that no frame is inside a scroll
 * container when it is measured — an element partly outside the viewport still
 * screenshots correctly, but its siblings' layout is what we compare, and a
 * viewport narrower than the contact sheet would reflow the grid.
 */
const VIEWPORTS = {
  phone: { width: 1400, height: 1200 },
  mweb: { width: 1400, height: 1200 },
  browser: { width: 1400, height: 1400 },
};

/**
 * What counts as "a control" for the layout fence.
 *
 * Everything a user could press, type into, read a value from or navigate by. Not
 * every element — decorative rules and spacers move by a sub-pixel when a font
 * changes and nobody cares — but every element whose position is a claim about
 * the interface.
 */
const CONTROLS = [
  '.cta',
  '.btn-out',
  '.btn',
  '.field',
  '.chip',
  '.iconbtn',
  '.toggle',
  '.fab',
  '.navbar .t',
  '.seg b',
  '.otp .b',
  '.card',
  '.sheet',
  '.appbar',
  '.nv',
  '.tab',
  '.textlink',
].join(', ');

/**
 * How far a control may move before PART 2 of `polish.css` is considered to have
 * changed the layout.
 *
 * 1px, because PART 2 is shadows, radii, colour and hiding the contact-sheet
 * chrome — none of which touches the box model, so the honest expectation is zero
 * and the tolerance only absorbs sub-pixel rounding.
 *
 * The font correction in PART 1 *does* move things and is deliberately outside this
 * check: see {@link loadPolishCss}.
 */
const LAYOUT_TOLERANCE_PX = 1;

/**
 * Splits `polish.css` into its metric-changing half and its fenced half.
 *
 * Must appear **exactly once** in that file, as its own comment. Both halves of
 * that requirement were learned the hard way: an earlier marker was also named in
 * the file's own header prose, so the split landed in the middle of the docstring —
 * which put every font rule on the wrong side of the boundary *and* left the second
 * half starting mid-comment, where the CSS parser silently discards tokens until it
 * resyncs at the next `{`. The fence still ran; it was simply measuring the wrong
 * thing. {@link loadPolishCss} now rejects zero or multiple occurrences rather than
 * quietly taking the first.
 */
const SPLIT_MARKER = '@CAPTURE-SPLIT@';

function parseArgs(argv) {
  const options = { only: null, polish: true };
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--only') {
      options.only = argv[i + 1];
      i += 1;
    } else if (argv[i] === '--no-polish') {
      options.polish = false;
    } else {
      throw new Error(`capture-screens: unknown argument "${argv[i]}"`);
    }
  }
  return options;
}

/**
 * `polish.css`, resolved and split into the two halves the fence needs.
 *
 * **Resolved**, because `addStyleTag({ content })` resolves relative URLs against
 * the *page* — a wireframe in `specs/` — so the two `@font-face` URLs have to
 * become absolute `file://` paths. The files are looked up rather than hard-coded:
 * npm may hoist `@fontsource-variable/*` to `portals/node_modules` or keep it in
 * `portals/www/node_modules`, and which it picks is not this script's business.
 *
 * **Split**, because the two halves deserve different treatment:
 *
 * - `fonts` pins Inter and Outfit, which the wireframes ask for by name and this
 *   host does not have. It changes text metrics — some labels rewrap — and that is
 *   the approved screen rendered in its intended typeface rather than in DejaVu.
 *   It is injected *before* the baseline measurement, so it defines the layout the
 *   fence measures from instead of being something the fence has to forgive.
 * - `rest` is shadows, radii, colour and hiding the contact-sheet chrome. None of it
 *   touches the box model, so it is held to {@link LAYOUT_TOLERANCE_PX}.
 *
 * Measuring the whole file in one go would mean setting a tolerance loose enough to
 * absorb ~90px of legitimate text rewrap — which is a tolerance that would no longer
 * catch a control genuinely moving. Splitting keeps the check strict.
 */
async function loadPolishCss() {
  const css = await readFile(join(scriptDir, 'polish.css'), 'utf8');

  const faces = {
    'inter-latin-wght-normal.woff2': '@fontsource-variable/inter',
    'outfit-latin-wght-normal.woff2': '@fontsource-variable/outfit',
  };

  let resolved = css;
  for (const [file, pkg] of Object.entries(faces)) {
    const candidates = [
      join(appRoot, 'node_modules', pkg, 'files', file),
      join(repoRoot, 'portals/node_modules', pkg, 'files', file),
      join(repoRoot, 'node_modules', pkg, 'files', file),
    ];

    let found = null;
    for (const candidate of candidates) {
      try {
        await readFile(candidate);
        found = candidate;
        break;
      } catch {
        // try the next hoist location
      }
    }

    if (!found) {
      throw new Error(
        `capture-screens: cannot find ${file} from ${pkg}. Run \`npm --prefix portals ci\`. ` +
          'Without both D2 faces the captures render in the host\'s default sans and stop ' +
          'being reproducible across machines.',
      );
    }

    resolved = resolved.replace(`url('${file}')`, `url('${pathToFileURL(found).href}')`);
  }

  const first = resolved.indexOf(SPLIT_MARKER);
  const last = resolved.lastIndexOf(SPLIT_MARKER);

  if (first === -1) {
    throw new Error(
      `capture-screens: polish.css has no ${SPLIT_MARKER} marker. It separates the rules that ` +
        'legitimately change text metrics from the ones that must not change any metric, and ' +
        'without it the layout fence cannot tell the two apart.',
    );
  }
  if (first !== last) {
    throw new Error(
      `capture-screens: polish.css names ${SPLIT_MARKER} more than once, so the split point is ` +
        'ambiguous and would almost certainly be taken in the wrong place. Keep it on one line, ' +
        'once, and describe it in prose without writing the token.',
    );
  }

  // Past the end of the marker's own comment: slicing at the marker itself would
  // leave `rest` beginning with a dangling comment body, and a CSS parser recovers
  // from that by discarding everything up to the next `{` — silently eating the
  // first real rule.
  const commentEnd = resolved.indexOf('*/', first);
  const boundary = commentEnd === -1 ? first + SPLIT_MARKER.length : commentEnd + 2;

  return {
    fonts: resolved.slice(0, boundary),
    rest: resolved.slice(boundary),
  };
}

/**
 * The frame element for one screen ID, as a Playwright handle, or `null`.
 *
 * Runs in the page because it is a DOM walk over text content, and text content is
 * the only thing that identifies a screen in these files.
 */
async function frameHandle(page, id) {
  const handle = await page.evaluateHandle((screenId) => {
    const FRAME = /(^|\s)(phone|browser|mweb)(\s|$)/;

    for (const cap of document.querySelectorAll('.cap')) {
      const scr = cap.querySelector('.scr');
      if (!scr || scr.textContent.trim() !== screenId) continue;

      // Forward through the siblings, stopping at the next caption so a screen
      // with no frame of its own cannot borrow the following screen's.
      for (let node = cap.nextElementSibling; node; node = node.nextElementSibling) {
        if (node.classList.contains('cap')) break;
        if (FRAME.test(node.className)) return node;
      }
      return null;
    }
    return null;
  }, id);

  const element = handle.asElement();
  if (!element) {
    await handle.dispose();
    return null;
  }
  return element;
}

/**
 * Every control's box, **relative to the frame's own origin**.
 *
 * Relative and not viewport-relative, and the difference is the whole check.
 * `polish.css` hides the contact sheet's page header and note bar, which lifts every
 * frame on the page by 200–430px; measured against the viewport, all 69 frames look
 * like they moved wholesale and the fence reports a violation on every one of them
 * while being blind to the thing it exists to catch. What matters is a control's
 * position *inside the picture*, because the picture is clipped to the frame.
 */
function measureControls(page, frame, selector) {
  return page.evaluate(
    ([element, controlSelector]) => {
      const origin = element.getBoundingClientRect();
      return [...element.querySelectorAll(controlSelector)].map((node, index) => {
        const box = node.getBoundingClientRect();
        return {
          key: `${node.className || node.tagName}#${index}`,
          x: box.x - origin.x,
          y: box.y - origin.y,
          width: box.width,
          height: box.height,
        };
      });
    },
    [frame, selector],
  );
}

/**
 * Compare two control measurements and describe what moved.
 *
 * A changed *count* is reported on its own and first: a stylesheet that adds or
 * removes a control is the failure this fence exists for, and reporting it as
 * "control 14 moved" would bury it.
 */
function layoutViolations(before, after) {
  if (before.length !== after.length) {
    return [`the control count changed: ${before.length} → ${after.length}`];
  }

  const violations = [];
  for (let i = 0; i < before.length; i += 1) {
    const a = before[i];
    const b = after[i];
    const deltas = {
      x: Math.abs(a.x - b.x),
      y: Math.abs(a.y - b.y),
      width: Math.abs(a.width - b.width),
      height: Math.abs(a.height - b.height),
    };
    const worst = Math.max(...Object.values(deltas));
    if (worst > LAYOUT_TOLERANCE_PX) {
      violations.push(
        `${a.key} moved by ${worst.toFixed(2)}px ` +
          `(x ${deltas.x.toFixed(2)}, y ${deltas.y.toFixed(2)}, ` +
          `w ${deltas.width.toFixed(2)}, h ${deltas.height.toFixed(2)})`,
      );
    }
  }
  return violations;
}

async function main() {
  const options = parseArgs(process.argv.slice(2));

  const { SCREENS, assertRegistryIsWellFormed } = await import('../src/content/screens.ts');
  assertRegistryIsWellFormed();

  const selected = options.only
    ? SCREENS.filter((s) => s.id === options.only || s.id.startsWith(options.only))
    : SCREENS;

  if (selected.length === 0) {
    throw new Error(`capture-screens: --only "${options.only}" matched no registry entry`);
  }

  const polishCss = options.polish ? await loadPolishCss() : null;

  // A full run starts from a clean directory, so a screen removed from the registry
  // cannot leave a stale PNG behind for S06 to composite and publish.
  //
  // A `--only` run must NOT do that: it is the debugging path, and wiping the other
  // 68 captures to re-shoot one of them would make the flag actively hostile. It
  // overwrites just its own targets instead, and the manifest it writes says so.
  if (options.only) {
    await mkdir(outputDir, { recursive: true });
  } else {
    await rm(outputDir, { recursive: true, force: true });
    await mkdir(outputDir, { recursive: true });
  }

  const browser = await chromium.launch().catch((cause) => {
    throw new Error(
      'capture-screens: could not launch Chromium. `playwright-core` ships no browser on ' +
        'purpose — installing `playwright` instead would make every CI `npm ci` download one, ' +
        'which A17 exists to avoid. Install it once with:\n\n' +
        '  npx playwright-core install chromium\n\n' +
        `Original error: ${cause.message}`,
    );
  });

  // Grouped by wireframe file so the output is ordered per file and each path is
  // resolved once. Note that the *page* is deliberately not reused across screens:
  // `addStyleTag` accumulates, so a shared page would carry the previous screen's
  // polish into the next one's baseline measurement and quietly disarm the fence.
  const byWireframe = new Map();
  for (const screen of selected) {
    if (!byWireframe.has(screen.wireframe)) byWireframe.set(screen.wireframe, []);
    byWireframe.get(screen.wireframe).push(screen);
  }

  const written = [];
  const missing = [];
  const fenceFailures = [];

  for (const [wireframe, screens] of byWireframe) {
    const source = join(wireframeDir, `${wireframe}.html`);
    const url = pathToFileURL(source).href;

    for (const screen of screens) {
      for (const appearance of screen.appearances) {
        const page = await browser.newPage({
          viewport: VIEWPORTS[screen.frame],
          deviceScaleFactor: DEVICE_SCALE_FACTOR,
          reducedMotion: 'reduce',
          colorScheme: appearance,
        });

        await page.goto(url, { waitUntil: 'load' });

        const frame = await frameHandle(page, screen.id);
        if (!frame) {
          missing.push(`${screen.id} (${wireframe})`);
          await page.close();
          continue;
        }

        const dark = appearanceCss(screen.wireframe, appearance);
        if (dark) await page.addStyleTag({ content: dark });

        // Stage 1 — the D2 faces, then wait for them, then take the baseline. The
        // baseline is measured *with* the correct typeface so that the fence below
        // is checking PART 2 and nothing else.
        if (polishCss) {
          await page.addStyleTag({ content: polishCss.fonts });
          await page.evaluate(() => document.fonts.ready);
        }

        const before = polishCss ? await measureControls(page, frame, CONTROLS) : null;

        // Stage 2 — the half that must move nothing.
        if (polishCss) await page.addStyleTag({ content: polishCss.rest });

        await page.evaluate(() => document.fonts.ready);

        if (before) {
          const after = await measureControls(page, frame, CONTROLS);
          const violations = layoutViolations(before, after);
          if (violations.length > 0) {
            fenceFailures.push(
              `${screen.id} (${wireframe}, ${appearance}):\n` +
                violations.map((v) => `      ${v}`).join('\n'),
            );
          }
        }

        const name = appearance === 'light' ? screen.file : `${screen.file}--dark`;
        await frame.screenshot({ path: join(outputDir, `${name}.png`), animations: 'disabled' });
        written.push(name);

        await page.close();
      }
    }
  }

  await browser.close();

  // A registry entry whose caption text matches nothing is a typo, and a silently
  // skipped screen is a hole in the guide nobody notices until launch.
  if (missing.length > 0) {
    throw new Error(
      `capture-screens: ${missing.length} registry entr${missing.length === 1 ? 'y' : 'ies'} ` +
        'matched no caption in the wireframe:\n\n' +
        missing.map((m) => `  ${m}\n`).join(''),
    );
  }

  if (fenceFailures.length > 0) {
    throw new Error(
      `capture-screens: polish.css moved a control on ${fenceFailures.length} frame(s).\n\n` +
        'The wireframes are the approved structural baseline — polish may change type, ' +
        'elevation and radii, but not what is where. Fix polish.css, or file a change set if ' +
        'the wireframe itself should change:\n\n' +
        fenceFailures.map((f) => `  ${f}\n`).join('\n'),
    );
  }

  const manifest = {
    note:
      'Generated by scripts/capture-screens.mjs. Raw 3x PNGs, gitignored — S06 composites ' +
      'these into the committed artefacts under public/screens/. Do not edit by hand.',
    deviceScaleFactor: DEVICE_SCALE_FACTOR,
    polished: options.polish,
    // A `--only` run leaves earlier captures in place, so `files` below lists what
    // *this* run wrote and the directory may hold more. Recorded so that a stale
    // partial directory cannot be mistaken for a complete set.
    partial: options.only ?? false,
    count: written.length,
    files: [...written].sort(),
  };
  await writeFile(join(outputDir, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);

  const actual = (await readdir(outputDir)).filter((f) => f.endsWith('.png')).length;
  process.stdout.write(
    `screens: ${actual} PNG${actual === 1 ? '' : 's'} from ${byWireframe.size} wireframe file` +
      `${byWireframe.size === 1 ? '' : 's'} at ${DEVICE_SCALE_FACTOR}x → .screens-raw/` +
      `${options.polish ? '' : ' (unpolished)'}\n`,
  );
}

await main();
