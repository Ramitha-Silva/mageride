/**
 * The Lighthouse gate — **S20, and the thresholds are the fence.**
 *
 * ## Usage
 *
 *   npm run build  --workspace @mageride/www      # a real build, never `next dev`
 *   npx next start -p 3104                        # from portals/www
 *   npm run lighthouse --workspace @mageride/www  # MR_LH_BASE overrides the origin
 *
 * A dev server would measure unminified modules served one per import, which is a
 * different application from the one readers get. The build is the subject.
 *
 * ## What it measures, and why the routes are discovered rather than listed
 *
 * S20 asks for `/`, `/drivers` and one guide chapter, **in every rendered locale**.
 * The list comes out of the running server's own `sitemap.xml` rather than being
 * written here, so it follows `WWW_LOCALES` and the route table without an edit —
 * the same reason `test/seo.test.ts` asserts sitemap coverage rather than a list of
 * paths. Re-enabling Tamil adds three audited pages by itself.
 *
 * Mobile emulation with Lighthouse's default throttling — a 4x CPU slowdown and a
 * slow-4G link. A34's target is the Sri Lankan median device, and a desktop run
 * passes comfortably while the real page is slow. That is the whole point of the
 * gate and it is why no `--preset=desktop` appears anywhere in this file.
 *
 * ## The thresholds, and the one that fails
 *
 *   Performance   >= 95     **currently 60-80 — see below**
 *   Accessibility  = 100    met on all six pages
 *   SEO           >= 95     met on all six pages (100)
 *
 * S20 is explicit: *"If a threshold cannot be met, fix the page, do not lower the
 * number. If it genuinely cannot be met, record why in the handoff and raise it as
 * a finding."* So the number stays at 95 and this job is **red**, for one reason,
 * measured: Total Blocking Time of 1,670 ms on `/`, which is the main thread
 * hydrating a bundle whose largest single item is 92 kB gzipped of resource tables.
 *
 * That is the same finding `scripts/check-bundle.mjs` reports as an A34 breach and
 * `build/prompts/MCS-36-a34-js-budget-below-the-framework-floor.md` asks a decision
 * about. S19 could argue it in bytes; S20 can price it in blocking time, which is
 * the number a reader on a mid-range Android actually experiences. **Do not lower
 * 95 to make this green** — the fix is the client-boundary decision MCS-36 **D3**
 * requests, and until then this job says so.
 */

import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { launch } from 'chrome-launcher';
import lighthouse from 'lighthouse';

const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const BASE = process.env.MR_LH_BASE ?? 'http://127.0.0.1:3104';
const REPORT_DIR = join(appRoot, '.lighthouse');

/**
 * A34/S20's numbers, and the comment above is the reason none of them moves.
 * Accessibility is `100` and not `>= 95` because S20 says 100 and because the site
 * is at 100 — a threshold set below what a surface already achieves is a ratchet
 * pointing the wrong way.
 */
const THRESHOLDS = {
  performance: 95,
  accessibility: 100,
  seo: 95,
};

/**
 * Chrome, from wherever this host keeps one.
 *
 * `chrome-launcher` finds an installed Chrome and **downloads nothing**, which is
 * what keeps this compatible with the fence `test/fences.test.ts` holds: no
 * dependency whose postinstall fetches a browser (A17). On a GitHub runner that is
 * the preinstalled Chrome; on this build host it is the one `playwright-core`
 * was pointed at once, by hand.
 */
function chromePath() {
  return process.env.CHROME_PATH ?? undefined;
}

/** The audited set, read from the server's own sitemap. */
async function targets() {
  const response = await fetch(`${BASE}/sitemap.xml`);
  if (!response.ok) {
    throw new Error(
      `${BASE}/sitemap.xml answered ${response.status}. This gate runs against a built site ` +
        'served by `next start`; nothing else produces a sitemap.',
    );
  }

  const paths = [...(await response.text()).matchAll(/<loc>([^<]+)<\/loc>/g)]
    .map(([, loc]) => new URL(loc).pathname)
    .sort();

  const locales = [...new Set(paths.map((path) => path.split('/')[1]))].filter(Boolean);
  const chosen = [];

  for (const locale of locales) {
    const home = `/${locale}`;
    const drivers = paths.find((path) => path === `/${locale}/drivers`);
    // One chapter, and the *first* one by sort order rather than a named slug, so a
    // renamed chapter cannot quietly drop this page out of the audit.
    const chapter = paths.find((path) => path.startsWith(`/${locale}/guide/driver/`));

    for (const path of [home, drivers, chapter]) {
      if (path) chosen.push(path);
    }
  }

  if (chosen.length === 0) throw new Error('the sitemap named no auditable page');
  return chosen;
}

const paths = await targets();
const chrome = await launch({
  chromePath: chromePath(),
  chromeFlags: ['--headless=new', '--no-sandbox', '--disable-dev-shm-usage'],
});

await mkdir(REPORT_DIR, { recursive: true });

/** @type {string[]} */
const failures = [];
/** @type {string[]} */
const report = [];

try {
  for (const path of paths) {
    const result = await lighthouse(
      `${BASE}${path}`,
      { port: chrome.port, output: 'json', logLevel: 'error' },
      // Default (mobile) config: 4x CPU throttling and a slow-4G link. Deliberate.
      undefined,
    );

    if (!result?.lhr) {
      failures.push(`${path}: Lighthouse returned no result`);
      continue;
    }

    const { lhr } = result;
    const scores = Object.fromEntries(
      Object.entries(THRESHOLDS).map(([category]) => [
        category,
        Math.round((lhr.categories[category]?.score ?? 0) * 100),
      ]),
    );

    await writeFile(
      join(REPORT_DIR, `${path.replace(/\//g, '_').replace(/^_/, '')}.json`),
      JSON.stringify(lhr, null, 2),
    );

    const metric = (id) => lhr.audits[id]?.displayValue ?? '—';
    report.push(
      `  ${path.padEnd(44)} perf ${String(scores.performance).padStart(3)} · ` +
        `a11y ${String(scores.accessibility).padStart(3)} · seo ${String(scores.seo).padStart(3)}` +
        `   LCP ${metric('largest-contentful-paint')} · TBT ${metric('total-blocking-time')} · ` +
        `CLS ${metric('cumulative-layout-shift')}`,
    );

    for (const [category, floor] of Object.entries(THRESHOLDS)) {
      if (scores[category] < floor) {
        failures.push(
          `${path}: ${category} ${scores[category]} is below the ${floor} threshold` +
            (category === 'performance'
              ? ` (TBT ${metric('total-blocking-time')}, LCP ${metric('largest-contentful-paint')})`
              : ''),
        );
      }
    }
  }
} finally {
  await chrome.kill();
}

process.stdout.write(
  `Lighthouse — mobile emulation, default throttling, ${paths.length} pages:\n` +
    `${report.join('\n')}\n`,
);

if (failures.length > 0) {
  process.stderr.write(
    '\nLighthouse gate failed:\n\n' +
      failures.map((failure) => `  ${failure}\n`).join('') +
      '\n  The thresholds are the fence (S20). Do not lower one to make this pass.\n' +
      '  Every Performance failure here has the same cause and the same open decision:\n' +
      '  MCS-36 D3 — the client-boundary rule that keeps 92 kB gzipped of resource\n' +
      '  tables in the bundle, which is what the main thread is blocked on.\n',
  );
  process.exit(1);
}

process.stdout.write(`\nLighthouse: every page meets ${JSON.stringify(THRESHOLDS)}.\n`);
