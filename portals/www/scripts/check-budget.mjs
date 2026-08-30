/**
 * A34's performance budget — **S21 split this out of `check-bundle.mjs`, and the split is
 * about what a failure may stop.**
 *
 * ## Why it is its own script
 *
 * `check-bundle.mjs` runs inside `npm run build` and asserts things about the *artefact's
 * integrity*: no style-injecting runtime, no motion library, no public environment variable,
 * every screen in the registry resolving to a file. A build that fails one of those has
 * produced something that must not ship, so stopping the build is exactly right.
 *
 * **A byte budget is a different kind of claim.** It says a page is slower than it should
 * be, not that the artefact is wrong — and S19 wired it into `build`, where it turned out
 * to stop three things that have nothing to do with performance:
 *
 *   - **the container image.** `infra/docker/Dockerfile.portal` runs
 *     `npm run build --workspace www`, so C134 could not produce a deployable image at all
 *     (S21 · step 1);
 *   - **every portal's tests.** `portals/package.json` has `pretest: npm run build`, so one
 *     surface's budget breach aborted the suites of all eight workspaces (S20);
 *   - **S22**, which needs an image to smoke-test.
 *
 * So the budget moved here and the fences stayed there. **Nothing was lowered and nothing
 * stopped being enforced** — `npm run budget` is in CI's portal leg beside `lint` and
 * `build`, so a regression still fails `main`. What changed is that a performance finding
 * now blocks a *merge* rather than an *artefact*, which is the thing it is actually about.
 *
 * ## The thresholds, and the one that is red
 *
 *   first-party JS on `/`   <= 90 kB gzipped    **113.7 kB — fails by 23.7 kB**
 *   total JS on `/`         <= 300 kB gzipped   277.1 kB
 *   CSS on `/`              <= 25 kB gzipped    9.8 kB
 *   hero plate              <= 120 kB AVIF      36 kB at 2x
 *   third-party origins     0                   0, asserted rather than assumed
 *
 * **A34 now states these as three figures rather than one — MCS-36 D1 and D2, accepted
 * 2026-08-30 and written into `docs/www-site-plan.md` §A34.** The numbers here did not
 * change; the spec caught up with them, which closes the divergence CLAUDE.md's first
 * Universal Rule opens whenever code and spec disagree.
 *
 * **A34's 90 kB was below the framework's own floor**, which is the finding
 * `build/prompts/MCS-36-a34-js-budget-below-the-framework-floor.md` exists to resolve:
 * react-dom, Next's router and React are 163 kB gzipped before a line of this surface's
 * code, so an empty App Router page breaches A34 by 73 kB. The number is therefore applied
 * to the bytes this surface controls and the floor is reported beside it — which **D1 wrote
 * into the spec on 2026-08-30**, so this script and §A34 now agree rather than the code
 * carrying a reading the document did not have.
 *
 * **The rule from S19 and S20 stands unchanged: do not raise a threshold to make a build
 * pass.** The remaining 23.7 kB is the resource tables, and **MCS-36 D3 is still open** — it
 * is the decision that removes them, and it is the only thing keeping this red.
 *
 * ## What is measured
 *
 * The **prerendered HTML for each locale home page** — literally the bytes a browser is
 * handed — and the files it references, gzipped at level 9. Two consequences worth stating
 * because they separate a real number from a comfortable one: the `noModule` polyfill chunk
 * is **excluded** (38 kB that no browser built this decade fetches, reported separately),
 * and gzip is what a CDN serves, roughly a third of the raw totals `check-bundle.mjs`
 * prints for its own fences.
 */

import { readFile, readdir, stat } from 'node:fs/promises';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const buildRoot = join(appRoot, '.next');
const screensRoot = join(appRoot, 'public/screens');
const prerenderRoot = join(buildRoot, 'server/app');

try {
  await stat(buildRoot);
} catch {
  process.stderr.write(
    'check-budget: no .next/ directory. This measures a build; it never replaces one.\n',
  );
  process.exit(1);
}

/** @type {string[]} */
const findings = [];

const A34_JS_FIRST_PARTY_BUDGET_BYTES = 90 * 1024;
/**
 * The ceiling on what a browser actually downloads — **the row that stops the split being
 * used to hide growth.**
 *
 * A34 states two JS figures rather than one (MCS-36 D1, accepted 2026-08-30), and a
 * first-party budget on its own has an obvious hole: move code into a vendor chunk and the
 * enforced number falls while the page gets no faster. The classifier makes that hard — it
 * keys off this surface's own string literals, which minification preserves and vendor code
 * cannot contain — but "hard" is not "checked", and this is the check.
 *
 * 300 kB against 277.1 kB measured, so the headroom is deliberate and small: it absorbs a
 * framework point release without a spec edit and refuses a second one.
 */
const A34_JS_TOTAL_BUDGET_BYTES = 300 * 1024;
const A34_CSS_BUDGET_BYTES = 25 * 1024;
const A34_HERO_IMAGE_BUDGET_BYTES = 120 * 1024;

/**
 * How a chunk is recognised as ours.
 *
 * Turbopack strips module paths from production output, so "which package is this"
 * cannot be asked of the artefact — but our own **string literals** survive
 * minification untouched, and none of them can occur in vendor code. The message
 * key namespace catches the resource tables, the utility-class prefixes catch the
 * component chunks, and the brand and a chapter slug catch the content registries.
 *
 * The failure direction matters: a first-party chunk carrying none of these would
 * be counted as framework and would make the budget *more* lenient. That is why the
 * list is broad rather than minimal, and why the report prints the split — a
 * framework total that jumps without a dependency change is the tell.
 */
const FIRST_PARTY_SIGNATURES = [
  '"www.',
  'mr-carousel',
  'mr-sticky',
  'mr-reveal',
  'mr-aurora',
  'MageRide',
  'install-and-first-run',
];

/** `<script src=…>`, in document order, with the modern/legacy split preserved. */
function scriptRefs(html) {
  const refs = [];
  for (const [tag] of html.matchAll(/<script\b[^>]*>/g)) {
    const src = /\ssrc="([^"]+)"/.exec(tag)?.[1];
    if (src) refs.push({ src, legacy: /\snoModule\b/i.test(tag) });
  }
  return refs;
}

/** `<link rel="stylesheet" href=…>`. */
function stylesheetRefs(html) {
  const refs = [];
  for (const [tag] of html.matchAll(/<link\b[^>]*>/g)) {
    if (!/\srel="[^"]*\bstylesheet\b/i.test(tag)) continue;
    const href = /\shref="([^"]+)"/.exec(tag)?.[1];
    if (href) refs.push(href);
  }
  return refs;
}

async function gzippedSize(ref) {
  const file = join(buildRoot, ref.replace(/^\/_next\//, ''));
  const buffer = await readFile(file);
  return { gz: gzipSync(buffer, { level: 9 }).byteLength, raw: buffer.byteLength, buffer };
}

/*
 * The published locales, read from what the build actually prerendered rather than
 * from `src/i18n`. Importing the module would drag both resource tables into this
 * script for a list of two strings, and — more to the point — the question here is
 * "which home pages did this build emit", which the build itself is the authority
 * on. A locale that is configured but somehow not rendered should not be budgeted.
 */
const localeHomePages = (await readdir(prerenderRoot).catch(() => []))
  .filter((name) => /^[a-z]{2}\.html$/.test(name))
  .sort();

if (localeHomePages.length === 0) {
  findings.push(
    '.next/server/app: no locale home page was prerendered, so A34\'s budget has nothing ' +
      'to measure. Either the build did not run or every page became dynamic — both are ' +
      'failures on a surface whose defining property is that it renders with the platform down.',
  );
}

/** @type {string[]} */
const budgetReport = [];

for (const page of localeHomePages) {
  const locale = basename(page, '.html');
  const html = await readFile(join(prerenderRoot, page), 'utf8');

  let firstPartyJs = 0;
  let frameworkJs = 0;
  let legacyJs = 0;
  let cssBytes = 0;
  /** @type {{name: string, gz: number}[]} */
  const firstPartyChunks = [];

  for (const { src, legacy } of scriptRefs(html)) {
    const { gz, buffer } = await gzippedSize(src);
    if (legacy) {
      legacyJs += gz;
      continue;
    }
    const source = buffer.toString('utf8');
    if (FIRST_PARTY_SIGNATURES.some((signature) => source.includes(signature))) {
      firstPartyJs += gz;
      firstPartyChunks.push({ name: basename(src), gz });
    } else {
      frameworkJs += gz;
    }
  }

  for (const href of stylesheetRefs(html)) {
    cssBytes += (await gzippedSize(href)).gz;
  }

  const kb = (bytes) => `${(bytes / 1024).toFixed(1)} kB`;

  if (firstPartyJs > A34_JS_FIRST_PARTY_BUDGET_BYTES) {
    const worst = firstPartyChunks.sort((a, b) => b.gz - a.gz)[0];
    findings.push(
      `/${locale}: ${kb(firstPartyJs)} of first-party JavaScript (gzipped) exceeds A34's ` +
        `${kb(A34_JS_FIRST_PARTY_BUDGET_BYTES)} budget by ${kb(firstPartyJs - A34_JS_FIRST_PARTY_BUDGET_BYTES)}. ` +
        `The largest chunk is ${worst?.name} at ${kb(worst?.gz ?? 0)}. Do not raise this number.\n` +
        "      The cause is known and it is the resource tables. Eleven client components take a\n" +
        '      locale and build a translator, so every published locale\'s table is in the bundle\n' +
        '      on every page — including the guide corpus, on a page with no guide on it.\n' +
        '      **Shipping only the active locale is not the fix and will not work**: the locale is\n' +
        '      a runtime value and the translator is synchronous, so every published table has to\n' +
        '      be statically present. S19 removed the unpublished ones, which is the whole of what\n' +
        '      that idea can buy. What removes the rest is moving a component to the server, or\n' +
        '      handing an island its resolved strings instead of a locale — the second contradicts\n' +
        '      a rule in CLAUDE.md, so it is a decision rather than a refactor.',
    );
  }

  /*
   * The total, excluding the `noModule` chunk — a modern browser fetches every module script
   * and never that one, so counting it would inflate this by 38 kB of bytes nobody receives.
   */
  const totalJs = firstPartyJs + frameworkJs;
  if (totalJs > A34_JS_TOTAL_BUDGET_BYTES) {
    findings.push(
      `/${locale}: ${kb(totalJs)} of JavaScript (gzipped, modern browser) exceeds A34's ` +
        `${kb(A34_JS_TOTAL_BUDGET_BYTES)} ceiling by ${kb(totalJs - A34_JS_TOTAL_BUDGET_BYTES)}. ` +
        `First-party ${kb(firstPartyJs)}, framework floor ${kb(frameworkJs)}.\n` +
        '      If the framework half is what grew, that is a dependency change: re-measure the\n' +
        '      floor recorded in A34 and record it with its date — do not raise this ceiling.',
    );
  }

  if (cssBytes > A34_CSS_BUDGET_BYTES) {
    findings.push(
      `/${locale}: ${kb(cssBytes)} of CSS (gzipped) exceeds A34's ${kb(A34_CSS_BUDGET_BYTES)} ` +
        'budget. One stylesheet is the fence; its size is this one.',
    );
  }

  /*
   * Zero render-blocking third-party requests — **asserted rather than assumed**,
   * which is S19's wording and the right instinct. There is no analytics, no CDN
   * font and no map on this surface, so the honest check is not "are the known
   * offenders absent" but "does the document reach any origin at all". A
   * `preconnect` counts: it is a third-party request that happens before anybody
   * decides whether to make one.
   */
  for (const [tag] of html.matchAll(/<(?:script|link)\b[^>]*>/g)) {
    /*
     * **Only tags that fetch.** `<link rel="canonical">` and the `hreflang`
     * alternates both carry `https://www.mageride.lk/…` and both must — they are
     * how the surface tells a crawler which document it is (S19 §1/§2). They cause
     * no request whatsoever, and an earlier draft of this check failed the build on
     * eight of them: the site's own SEO, reported as a third-party dependency.
     *
     * So the rel list is the fetching one, and `canonical`/`alternate` are not on
     * it. A `<script src>` always fetches and needs no rel.
     */
    const isScript = /^<script\b/i.test(tag);
    const fetches =
      isScript ||
      /\srel="[^"]*\b(?:stylesheet|preload|modulepreload|prefetch|preconnect|dns-prefetch|icon|manifest)\b/i.test(
        tag,
      );
    if (!fetches) continue;

    const url = /\s(?:src|href)="(?:[a-z]+:)?\/\/([^"/]+)/i.exec(tag);
    if (url) {
      findings.push(
        `/${locale}: the document fetches from the external origin "${url[1]}". This surface ` +
          'serves every byte itself — no analytics, no CDN font, no map — and the first ' +
          'external request is the one that ends that.',
      );
    }
    if (/\srel="[^"]*\b(?:preconnect|dns-prefetch)\b/i.test(tag)) {
      findings.push(
        `/${locale}: a preconnect or dns-prefetch hint. There is no third party to warm up.`,
      );
    }
  }

  budgetReport.push(
    `A34 /${locale}: first-party JS ${kb(firstPartyJs)} of ${kb(A34_JS_FIRST_PARTY_BUDGET_BYTES)} gz · ` +
      `total ${kb(totalJs)} of ${kb(A34_JS_TOTAL_BUDGET_BYTES)} gz · ` +
      `CSS ${kb(cssBytes)} of ${kb(A34_CSS_BUDGET_BYTES)} gz · framework floor ${kb(frameworkJs)} gz ` +
      `(reported) · ${kb(legacyJs)} noModule polyfill no modern browser fetches · ` +
      'zero third-party origins.',
  );
}

/*
 * The hero plates, against A34's 120 kB.
 *
 * `HERO_SCREENS` is the registry's own answer to "which images load eagerly", so
 * this cannot drift from what the pages mark `priority`. Both densities are checked:
 * the 2x is what a phone with a 2x screen actually downloads, and it is the one that
 * would breach first.
 */
{
  const { HERO_SCREENS } = await import(join(appRoot, 'src/content/screens.ts'));

  for (const screen of HERO_SCREENS) {
    for (const stem of [screen.file, `${screen.file}@2x`]) {
      const file = join(screensRoot, `${stem}.avif`);
      const { size } = await stat(file).catch(() => ({ size: 0 }));
      if (size > A34_HERO_IMAGE_BUDGET_BYTES) {
        findings.push(
          `public/screens/${stem}.avif: ${(size / 1024).toFixed(0)} kB exceeds A34's ` +
            `${A34_HERO_IMAGE_BUDGET_BYTES / 1024} kB hero budget. A hero image is downloaded ` +
            'before anything below the fold and is on the LCP path on a 3G-throttled phone.',
        );
      }
    }
  }
}

/*
 * The A34 lines print **whether or not the check passed**, and before the failures.
 *
 * A budget breach that shows the overage and hides the context is the version of
 * this message that sends somebody looking for 24 kB in the wrong half of the
 * bundle. The framework floor is exactly the number that stops that, so it is on
 * screen at the moment it is needed rather than only on a green run.
 */

process.stdout.write(
  `A34 budget — per page, gzipped, as a browser receives it:\n${budgetReport.join('\n')}\n`,
);

if (findings.length > 0) {
  process.stderr.write(
    '\nA34 budget failed:\n\n' +
      findings.map((finding) => `  ${finding}\n`).join('') +
      '\n  The numbers are the fence (S19 · S20 · A34). Do not raise one to make this pass.\n' +
      '  MCS-36 D3 is the open decision that removes the overage.\n',
  );
  process.exit(1);
}

process.stdout.write('\nA34: every page is inside its budget.\n');
