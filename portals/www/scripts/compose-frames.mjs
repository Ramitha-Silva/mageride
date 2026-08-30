/**
 * Composite S05's raw captures into the marketing images the site actually ships.
 *
 * Reads `.screens-raw/` (gitignored, produced by `capture-screens.mjs`), writes
 * `public/screens/` (**committed** — CI must never need a browser). Run both halves
 * together with `npm run screens:refresh`, which is the only sanctioned way these
 * files change.
 *
 * ## What a capture already contains, and what this adds
 *
 * S06's brief says to composite "a device mockup: rounded corners, bezel, a status
 * bar for the phone families". **The bezel and the status bar are already in the
 * capture** — `specs/wireframes/*.html` draws `.phone` with a 9px `#0f1115` border
 * and its own `.sbar` row, and the capture is an element screenshot of exactly that
 * box. Drawing a second bezel around the first would put a phone inside a phone,
 * and a second status bar above the real one. Verified by sampling pixels: at
 * (30,30) a phone capture is `#0F1115`, the bezel; a portal capture is `#E7EAEE`,
 * the browser chrome.
 *
 * So this script adds the three things the capture genuinely lacks:
 *
 * 1. **Rounded corners as transparency.** An element screenshot is a rectangle, and
 *    the area outside the frame's `border-radius` is filled with the page
 *    background — sampled at (0,0), `#FCFCFC`. Composited onto a plate unmasked,
 *    every frame would wear four pale corner triangles. The mask uses each frame's
 *    real radius (`.phone` 26px, `.mweb` 14px, `.browser` 12px, all × the capture's
 *    3× scale).
 * 2. **A drop shadow**, from D2's elevation ladder. `polish.css` put one on the
 *    frame, but a box-shadow paints *outside* the border box and an element
 *    screenshot clips *to* it, so no shadow survives into the PNG.
 * 3. **The plate** it sits on.
 *
 * ## Every colour is imported, not transcribed
 *
 * `portals/tailwind-preset/src/tokens.ts` is "the ONLY place a D2 §0.2 value is
 * spelled on the web", and Node 24 strips types natively, so this script imports
 * that module directly rather than copying hexes out of it — the same trick
 * `check-i18n-parity.mjs` uses to read the `.ts` message tables. **No new colour
 * enters the system**, and a D2 change reaches these images by re-running the
 * refresh rather than by anybody remembering to update a second table.
 *
 * (`wireframe-appearances.mjs` transcribes instead, and that is not an
 * inconsistency: it maps D2 values onto the *wireframes'* own custom-property
 * names, which is a translation table with no importable source.)
 *
 * ## Densities and formats
 *
 * Composited once at 2×, then downsampled to 1× — never upsampled. AVIF and WebP
 * only; see {@link FORMATS} for why there is no PNG.
 */

import { mkdir, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import sharp from 'sharp';

import { ELEVATIONS, SEMANTIC_COLORS, SPACING } from '../../tailwind-preset/src/tokens.ts';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const appRoot = resolve(scriptDir, '..');
const rawDir = join(appRoot, '.screens-raw');
const outputDir = join(appRoot, 'public/screens');

/** The scale `capture-screens.mjs` shoots at. Raw pixels ÷ this = CSS pixels. */
const CAPTURE_SCALE = 3;

/**
 * Per frame kind: the CSS geometry the wireframes declare.
 *
 * `radius` is the frame's own `border-radius`, and it is what the corner mask is
 * cut with. Width is recorded for the sanity check in {@link composite}; height is
 * not, because `.browser` has none — its height is per *screen* (440–1037px), which
 * is why the portal frames cannot share one fixed mockup.
 */
const FRAMES = {
  phone: { radius: 26, width: 320 },
  mweb: { radius: 14, width: 330 },
  browser: { radius: 12, width: 944 },
};

/**
 * The plate's padding around the frame, in CSS pixels.
 *
 * `SPACING.xxl` — D2's largest spacing step, imported rather than chosen. The plate
 * is the frame plus this on every side.
 */
const PLATE_PADDING = Number.parseInt(SPACING.xxl, 10);

/**
 * The device shadow, read from D2's elevation ladder.
 *
 * `elevation-5` is the 12 dp top of the ladder, which is the step D2 puts furthest
 * from the surface — right for an object presented as floating above a plate.
 * Parsed from the token string rather than restated, so it cannot drift from it.
 */
const SHADOW = parseShadow(ELEVATIONS['elevation-5'].shadow);

/**
 * Output formats.
 *
 * **AVIF and WebP, and deliberately no PNG.** S06's brief asks for "a PNG fallback"
 * and, when the budget is tight, says to drop the PNG for dark captures first
 * because "every browser that reads `prefers-color-scheme` also reads WebP". S05
 * shipped light-only, so that lever does not exist — but its *reasoning* settles
 * the question outright: WebP is the universal floor. Every browser that has ever
 * supported `prefers-color-scheme` supports WebP, and every engine has shipped it
 * since Safari 14 in 2020. A PNG fallback in 2026 is bytes no reader will fetch.
 *
 * It is also the difference between passing and failing the fence, and that was
 * measured rather than assumed. On this set a 2× phone plate is **12 kB as AVIF,
 * 21 kB as WebP and 123 kB as PNG**; a 2× portal plate is **31 / 57 / 266 kB**. A
 * smooth gradient is close to the worst case for PNG's predictors, so the PNG of
 * every portal frame **breaches the 220 kB per-file limit on its own**, and adding
 * the format at all would put roughly 14 MB into a 12 MB budget. AVIF + WebP for
 * all 69 entries at both densities comes to about 5 MB.
 *
 * `quality` is tuned in {@link encode} and recorded in the committed README.
 */
const FORMATS = ['avif', 'webp'];

/** `0 4px 16px 0 rgb(0 0 0 / 0.12)` → the numbers, in CSS pixels. */
function parseShadow(value) {
  const lengths = [...value.matchAll(/(-?\d+(?:\.\d+)?)px/g)].map((m) => Number(m[1]));
  const alpha = /\/\s*([\d.]+)\s*\)/.exec(value);
  const [offsetX = 0, offsetY = 0, blur = 0] = lengths;
  return { offsetX, offsetY, blur, alpha: alpha ? Number(alpha[1]) : 0.12 };
}

/**
 * The plate, as SVG, at a given density.
 *
 * SVG rather than pixel arithmetic because sharp rasterises it and SVG has both of
 * the things a plate needs — a real radial gradient and a real Gaussian blur for
 * the shadow — with no manual convolution.
 *
 * The gradient mirrors `app/globals.css`'s `.mr-aurora`: `primary-container` at low
 * alpha, radial from the top, over `surface`. Same two tokens, same shape, so the
 * plate behind a screenshot and the backdrop behind a hero are visibly the same
 * surface rather than two people's guesses at one.
 *
 * @param {'light' | 'dark'} appearance
 */
function plateSvg(width, height, frame, appearance, density) {
  const surface = SEMANTIC_COLORS.surface[appearance];
  const glow = SEMANTIC_COLORS['primary-container'][appearance];

  // CSS blur radius is about twice the Gaussian standard deviation.
  const deviation = (SHADOW.blur * density) / 2;
  const shadowPad = Math.ceil(deviation * 3 + Math.abs(SHADOW.offsetY * density));

  return Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}">
  <defs>
    <radialGradient id="glow" cx="50%" cy="0%" r="70%">
      <stop offset="0%" stop-color="${glow}" stop-opacity="0.20"/>
      <stop offset="70%" stop-color="${glow}" stop-opacity="0"/>
    </radialGradient>
    <filter id="shadow"
            x="${-shadowPad}" y="${-shadowPad}"
            width="${frame.width + shadowPad * 2}" height="${frame.height + shadowPad * 2}"
            filterUnits="userSpaceOnUse">
      <feGaussianBlur stdDeviation="${deviation}"/>
    </filter>
  </defs>
  <rect width="${width}" height="${height}" fill="${surface}"/>
  <rect width="${width}" height="${height}" fill="url(#glow)"/>
  <g transform="translate(${frame.x + SHADOW.offsetX * density} ${frame.y + SHADOW.offsetY * density})">
    <rect width="${frame.width}" height="${frame.height}" rx="${frame.radius}"
          fill="rgba(0,0,0,${SHADOW.alpha})" filter="url(#shadow)"/>
  </g>
</svg>`,
  );
}

/** A rounded-rectangle alpha mask the size of the capture. */
function maskSvg(width, height, radius) {
  return Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}">` +
      `<rect width="${width}" height="${height}" rx="${radius}" fill="#fff"/></svg>`,
  );
}

/**
 * One composited plate at one density, as a raw PNG buffer.
 *
 * Order matters: the corner mask is applied to the capture at its **native 3×
 * size** and only then resized. Masking after the downscale would cut the corner
 * out of already-resampled pixels and leave the frame's own antialiased edge
 * outside the mask, which reads as a pale rim.
 */
async function composite(rawPath, kind, appearance, density) {
  const geometry = FRAMES[kind];
  const source = sharp(rawPath);
  const { width: rawWidth, height: rawHeight } = await source.metadata();

  const cssWidth = rawWidth / CAPTURE_SCALE;
  if (Math.abs(cssWidth - geometry.width) > 1) {
    throw new Error(
      `compose-frames: ${rawPath} is ${rawWidth}px wide, which is ${cssWidth}px at ` +
        `${CAPTURE_SCALE}x — but a "${kind}" frame is ${geometry.width}px. Either the wireframe ` +
        'geometry changed or the registry names the wrong frame kind.',
    );
  }

  const masked = await sharp(rawPath)
    .ensureAlpha()
    .composite([
      {
        input: maskSvg(rawWidth, rawHeight, geometry.radius * CAPTURE_SCALE),
        blend: 'dest-in',
      },
    ])
    .png()
    .toBuffer();

  const frameWidth = Math.round((rawWidth / CAPTURE_SCALE) * density);
  const frameHeight = Math.round((rawHeight / CAPTURE_SCALE) * density);
  const pad = PLATE_PADDING * density;
  const plateWidth = frameWidth + pad * 2;
  const plateHeight = frameHeight + pad * 2;

  const resized = await sharp(masked)
    .resize(frameWidth, frameHeight, { fit: 'fill', kernel: 'lanczos3' })
    .png()
    .toBuffer();

  const plate = plateSvg(
    plateWidth,
    plateHeight,
    { x: pad, y: pad, width: frameWidth, height: frameHeight, radius: geometry.radius * density },
    appearance,
    density,
  );

  return sharp(plate)
    .composite([{ input: resized, left: pad, top: pad }])
    .png()
    .toBuffer();
}

/**
 * Encode one composited buffer to one format.
 *
 * Quality and effort were both measured on this set rather than chosen by taste.
 * On the largest image here — a 2× portal plate at 2080px wide — AVIF `effort`
 * buys almost nothing for a great deal of time:
 *
 * | effort | size | time |
 * |---|---|---|
 * | 2 | 42 kB | 1.2 s |
 * | **3** | **39 kB** | **2.4 s** |
 * | 4 | 37 kB | 7.5 s |
 * | 6 | 35 kB | 19.3 s |
 *
 * Effort 6 spends **16× the time of effort 3 to save 4 kB** on an image that has
 * a 220 kB ceiling, in a directory sitting at roughly a third of its 12 MB budget.
 * A first pass at effort 6 was on course to take ~55 minutes for 276 images, which
 * is a real cost: this script is meant to be re-run whenever a wireframe changes,
 * and a refresh nobody wants to start is a refresh that does not happen.
 *
 * So: **AVIF at effort 3, WebP at effort 4** (webp effort 6 costs double the time
 * for 1 kB). If the budget ever tightens, raising effort is the cheapest lever
 * available — it costs only wall-clock, and it is the first thing to reach for
 * before dropping a screen.
 */
function encode(buffer, format) {
  const image = sharp(buffer);
  if (format === 'avif') return image.avif({ quality: 50, effort: 3, chromaSubsampling: '4:2:0' });
  return image.webp({ quality: 80, effort: 4 });
}

async function main() {
  const { SCREENS, assertRegistryIsWellFormed } = await import('../src/content/screens.ts');
  assertRegistryIsWellFormed();

  try {
    await stat(rawDir);
  } catch {
    throw new Error(
      'compose-frames: no .screens-raw/. This composites S05\'s captures and does not produce ' +
        'them — run `npm run screens:capture` first, or `npm run screens:refresh` for both.',
    );
  }

  // Regenerated wholesale, so a screen dropped from the registry cannot leave a
  // committed image behind that no page references and no session remembers.
  //
  // File by file rather than `rm -rf` on the directory: `README.md` lives here too
  // and is hand-written — it is the note explaining that everything *beside* it is
  // generated, and wiping the directory would delete the one file that says so.
  await mkdir(outputDir, { recursive: true });
  for (const name of await readdir(outputDir)) {
    if (name === 'README.md') continue;
    await rm(join(outputDir, name), { recursive: true, force: true });
  }

  const written = [];
  const missing = [];

  for (const screen of SCREENS) {
    for (const appearance of screen.appearances) {
      const stem = appearance === 'light' ? screen.file : `${screen.file}--dark`;
      const rawPath = join(rawDir, `${stem}.png`);

      try {
        await stat(rawPath);
      } catch {
        missing.push(`${screen.id} → ${stem}.png`);
        continue;
      }

      for (const density of [2, 1]) {
        const composed = await composite(rawPath, screen.frame, appearance, density);
        const suffix = density === 2 ? '@2x' : '';

        for (const format of FORMATS) {
          const name = `${stem}${suffix}.${format}`;
          await encode(composed, format).toFile(join(outputDir, name));
          written.push(name);
        }
      }
    }
  }

  if (missing.length > 0) {
    throw new Error(
      `compose-frames: ${missing.length} registry entr${missing.length === 1 ? 'y has' : 'ies have'} ` +
        'no raw capture. Re-run `npm run screens:capture`:\n\n' +
        missing.map((m) => `  ${m}\n`).join(''),
    );
  }

  let bytes = 0;
  let largest = { name: '', bytes: 0 };
  for (const name of written) {
    const { size } = await stat(join(outputDir, name));
    bytes += size;
    if (size > largest.bytes) largest = { name, bytes: size };
  }

  await writeFile(
    join(outputDir, 'manifest.json'),
    `${JSON.stringify(
      {
        note:
          'Generated by scripts/compose-frames.mjs from .screens-raw/. Committed on purpose — CI ' +
          'must not need a browser. Regenerate with `npm run screens:refresh`; a hand-edited ' +
          'image is overwritten without warning.',
        formats: FORMATS,
        densities: ['1x', '2x'],
        count: written.length,
        totalBytes: bytes,
        files: [...written].sort(),
      },
      null,
      2,
    )}\n`,
  );

  process.stdout.write(
    `screens: ${written.length} images from ${SCREENS.length} entries — ` +
      `${(bytes / 1024 / 1024).toFixed(2)} MB total, largest ${largest.name} at ` +
      `${(largest.bytes / 1024).toFixed(0)} kB → public/screens/\n`,
  );
}

await main();
