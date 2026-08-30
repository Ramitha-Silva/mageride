import { readdir, readFile } from 'node:fs/promises';
import { dirname, extname, join, relative, resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

/**
 * The four C134 fences, as tests over the tree.
 *
 * `scripts/check-bundle.mjs` holds the same fences over the **artefact** and is the
 * one that catches a transitive dependency. This file is the source-level half: it
 * runs in a second rather than after a build, it names the offending file, and it
 * is what a session working on a page finds out from.
 *
 * Two of these are the *only* enforcement of their fence anywhere in the toolchain
 * — the motion-library ban (README §4.3: `framer-motion` is on no banned list) and
 * "no API call at request time" (nothing else in the repo forbids `fetch` on a
 * portal, because the other three portals all need it).
 */

const appRoot = resolve(import.meta.dirname, '..');
const SOURCE_ROOTS = ['app', 'src'];
const SOURCE_EXTENSIONS = new Set(['.ts', '.tsx']);

async function* walk(
  dir: string,
  extensions: Set<string> = SOURCE_EXTENSIONS,
): AsyncGenerator<string> {
  let entries;
  try {
    entries = await readdir(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries.sort((a, b) => a.name.localeCompare(b.name))) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) yield* walk(path, extensions);
    else if (entry.isFile() && extensions.has(extname(entry.name))) yield path;
  }
}

async function sources(roots: readonly string[] = SOURCE_ROOTS): Promise<
  { path: string; text: string }[]
> {
  const found: { path: string; text: string }[] = [];
  for (const root of roots) {
    for await (const file of walk(join(appRoot, root), BUILD_EXTENSIONS)) {
      found.push({ path: relative(appRoot, file), text: await readFile(file, 'utf8') });
    }
  }
  return found;
}

/**
 * `scripts/` is scanned for **one** fence, and the asymmetry is deliberate (S20).
 *
 * The build tooling is not the site: it never ships, so AL-52, the resource rule
 * and the public-variable rule have nothing to say about it, and two of the fences
 * over `app/`/`src/` would be actively wrong here — `capture-screens.mjs` and
 * `compose-frames.mjs` exist in order to import `sharp`, and `check-a11y.mjs` reads
 * an environment variable to point itself at a server. Extending those sweeps would
 * mean writing exemptions for the scripts whose whole purpose they name.
 *
 * The **network** fence is different, and it is the one that must reach here: a
 * `fetch` in a build script makes the *build* depend on the platform being up,
 * which is the same promise failing one step earlier. `npm run screens:refresh`
 * drives a browser at local files and `check-a11y.mjs` drives one at a local
 * server; neither opens a connection of its own, and none may start.
 */
const BUILD_ROOTS = ['scripts'];
const BUILD_EXTENSIONS = new Set(['.ts', '.tsx', '.mjs', '.js']);

/**
 * The three scripts allowed to open a connection, **by exact name and with the
 * reason here** (S20: no fence exemption without one).
 *
 * The fence above is about the *build*: a `fetch` in something that runs inside
 * `npm run build` would mean a release could not be cut during an outage, which is
 * when one is most wanted. These three do not run inside the build. They are
 * **post-build auditors** — they take an already-built site, already served by
 * `next start`, and measure it. Talking to it over HTTP is not a dependency they
 * have; it is the entire job.
 *
 * S20 caught `check-lighthouse.mjs` with the first version of this sweep, which is
 * the sweep working: the script reads the running server's own `sitemap.xml` to
 * discover which pages to audit, so the audited set follows `WWW_LOCALES` and the
 * route table instead of being a list somebody has to remember to update. That is
 * better than the alternative, so the fence narrowed rather than the script.
 *
 * By exact name and never by a `check-*` pattern, so the next script added to this
 * directory is covered by default and has to argue its way out.
 */
const POST_BUILD_AUDITORS = new Set([
  'scripts/check-a11y.mjs',
  'scripts/check-lighthouse.mjs',
  'scripts/check-visual.mjs',
]);

/**
 * Every module reachable from a `'use client'` entry point — the client bundle, as a
 * set of source files.
 *
 * Walked rather than listed because the bundle is transitive and the list is not
 * obvious: `ScreenImage`, `Callout` and `GalleryBody` carry no directive of their own
 * and are in the bundle because client components import them. A fence written against
 * the seventeen entry points would have missed all three, which is exactly how the
 * resource tables got in.
 *
 * **`import type` is skipped**, because types are erased at build and cost no bytes —
 * a client component may hold `type Locale` from anywhere.
 */
async function clientGraph(): Promise<{ path: string; text: string }[]> {
  const all = new Map((await sources()).map((file) => [join(appRoot, file.path), file]));

  const resolveImport = (from: string, spec: string): string | null => {
    let base: string;
    if (spec.startsWith('@/')) base = join(appRoot, 'src', spec.slice(2));
    else if (spec.startsWith('.')) base = resolve(dirname(from), spec);
    else return null;
    for (const candidate of [`${base}.tsx`, `${base}.ts`, join(base, 'index.ts'), base]) {
      if (all.has(candidate)) return candidate;
    }
    return null;
  };

  const seen = new Set<string>();
  const queue = [...all.keys()].filter((path) =>
    /^\s*['"]use client['"]/m.test((all.get(path)?.text ?? '').slice(0, 200)),
  );

  while (queue.length > 0) {
    const file = queue.shift();
    if (!file || seen.has(file)) continue;
    seen.add(file);

    const text = all.get(file)?.text ?? '';
    for (const match of text.matchAll(/^\s*import\s+(?:type\s+)?[^'"]*from\s+['"]([^'"]+)['"]/gm)) {
      if (/^\s*import\s+type\s/.test(match[0])) continue;
      const target = resolveImport(file, match[1] ?? '');
      if (target && !seen.has(target)) queue.push(target);
    }
  }

  return [...seen].map((path) => all.get(path)).filter((file) => file !== undefined);
}

const manifest = JSON.parse(await readFile(join(appRoot, 'package.json'), 'utf8')) as {
  dependencies: Record<string, string>;
  devDependencies: Record<string, string>;
};

describe('fence · no API call at request time', () => {
  /**
   * MCS-34's fourth negative, and the load-bearing one: this site renders with the
   * entire MageRide platform down. Every page below `app/[locale]/` is statically
   * rendered from typed content modules, so a network call at request time would
   * not merely be slow — it would make the one promise this surface exists to keep
   * untrue, and it would do so silently, on the day the platform was already
   * having a bad time.
   */
  it('opens no network connection anywhere in the source', async () => {
    const offenders = (await sources()).filter(({ text }) =>
      /\bfetch\s*\(|\baxios\b|new\s+EventSource|new\s+WebSocket|navigator\.sendBeacon/.test(text),
    );

    expect(offenders.map(({ path }) => path)).toEqual([]);
  });

  /**
   * And the build does not either.
   *
   * The site rendering with the platform down is worth little if the *build* needs
   * it up: a `fetch` in a script under `scripts/` would mean a release could not be
   * cut during an outage, which is exactly when one is most likely to be wanted.
   * Playwright's `page.goto` is not this — it drives a browser at a local file or a
   * local server, and it is how the screens are captured and the audit is run.
   */
  it('opens no network connection at build time either', async () => {
    const offenders = (await sources(BUILD_ROOTS))
      .filter(({ path }) => !POST_BUILD_AUDITORS.has(path))
      .filter(({ text }) =>
        /\bfetch\s*\(|\baxios\b|new\s+EventSource|new\s+WebSocket|navigator\.sendBeacon/.test(
          text,
        ),
      );

    expect(offenders.map(({ path }) => path)).toEqual([]);
  });

  /**
   * The exemptions are checked in the other direction, so one cannot outlive its
   * file — the same shape, and for the same reason, as `UNPUBLISHED_PAGES` in
   * `test/routes.test.ts`. A renamed auditor silently re-enters the sweep; a deleted
   * one fails here rather than leaving a permanent hole with nothing behind it.
   */
  it('exempts no auditor that is not there', async () => {
    const present = new Set((await sources(BUILD_ROOTS)).map(({ path }) => path));
    for (const exempt of POST_BUILD_AUDITORS) {
      expect(present.has(exempt), `${exempt} is exempted and does not exist`).toBe(true);
    }
  });

  /**
   * And the exemption is **narrow, not a hole**: an auditor may talk to the site it
   * is auditing and to nothing else.
   *
   * Each one reads its origin from an overridable constant with a `127.0.0.1`
   * default, so this asserts the property that actually matters — no literal remote
   * origin anywhere in a script that is allowed to open a connection. A hard-coded
   * `https://` in one of these would be a check quietly measuring production, or
   * some third party, instead of the build in front of it.
   */
  it('lets an auditor reach the site under test and nowhere else', async () => {
    const offenders = (await sources(BUILD_ROOTS))
      .filter(({ path }) => POST_BUILD_AUDITORS.has(path))
      .filter(({ text }) => /(['"`])https?:\/\/(?!127\.0\.0\.1|localhost)/.test(text));

    expect(offenders.map(({ path }) => path)).toEqual([]);
  });

  it('depends on no HTTP client', () => {
    const all = { ...manifest.dependencies, ...manifest.devDependencies };
    for (const name of ['axios', 'ky', 'got', 'node-fetch', 'swr', '@tanstack/react-query']) {
      expect(all, name).not.toHaveProperty(name);
    }
  });

  /**
   * No map, either. The other three surfaces draw one; this one has no live
   * anything to draw on it, and MapLibre is larger than everything else here put
   * together.
   */
  it('depends on no map library', () => {
    const all = { ...manifest.dependencies, ...manifest.devDependencies };
    expect(all).not.toHaveProperty('maplibre-gl');
    expect(all).not.toHaveProperty('pmtiles');
  });
});

describe('fence · no NEXT_PUBLIC_ variable', () => {
  it('names none in any source file', async () => {
    const offenders = (await sources()).filter(({ text }) => text.includes('NEXT_PUBLIC_'));
    expect(offenders.map(({ path }) => path)).toEqual([]);
  });

  /**
   * And no `process.env` at all. `web-passenger` permits it in exactly one module
   * because it has a gateway to reach; this surface has nothing to configure, so
   * the absence of the *idiom* is a cheaper thing to check than the absence of
   * each variable — and it is what keeps `.env.example` honestly empty.
   */
  it('reads no environment variable in any source file', async () => {
    const offenders = (await sources()).filter(({ text }) => /process\.env\./.test(text));
    expect(offenders.map(({ path }) => path)).toEqual([]);
  });
});

describe('fence · no cookie', () => {
  /**
   * **A36: no cookies, so no cookie banner** — and the second clause is why the
   * first one is worth a test rather than a note.
   *
   * `/legal/privacy` states plainly that this site sets no cookies and runs no
   * analytics. That is an unusual claim and a checkable one, and it is the kind that
   * decays quietly: one `document.cookie` for a dismissed banner, a locale
   * preference or an A/B flag, and the published privacy policy becomes false while
   * every page still renders.
   *
   * **`localStorage` is deliberately not banned here.** The theme toggle uses it
   * (S19, A35) and D6′ I-29.1's fence is about a surface holding somebody's live
   * ride, which this one does not. Nothing is sent to a server, so there is nothing
   * to consent to — the distinction is stated on the privacy page and in CLAUDE.md,
   * and a test that conflated the two would force a later session to weaken it.
   */
  it('writes no cookie anywhere in the source', async () => {
    const offenders = (await sources()).filter(({ text }) =>
      /document\s*\.\s*cookie|['"`]Set-Cookie['"`]|\bcookies\s*\(\s*\)/.test(text),
    );

    expect(offenders.map(({ path }) => path)).toEqual([]);
  });

  /** And depends on nothing whose job is cookies. */
  it('depends on no cookie library', () => {
    const all = { ...manifest.dependencies, ...manifest.devDependencies };
    for (const name of ['js-cookie', 'cookie', 'cookies-next', 'universal-cookie', 'nookies']) {
      expect(all, name).not.toHaveProperty(name);
    }
  });
});

describe('fence · AL-52, and no motion library', () => {
  /**
   * README §4.3: `framer-motion` / `motion` is on neither
   * `banned-styling-packages.json`'s package list nor its prefix list, so
   * `check-al52.mjs` would pass a bundle full of it. MCS-34 declined to widen that
   * shared list for one marketing page, which makes this assertion — and its twin
   * in `scripts/check-bundle.mjs` — the entire enforcement.
   */
  it('declares no motion library', () => {
    const all = Object.keys({ ...manifest.dependencies, ...manifest.devDependencies });
    const motion = all.filter(
      (name) =>
        /^(framer-motion|motion|motion-dom|popmotion|react-spring|gsap)$/.test(name) ||
        name.startsWith('@react-spring/') ||
        name.startsWith('@motionone/') ||
        name.startsWith('@gsap/'),
    );

    expect(motion).toEqual([]);
  });

  it('imports no motion library', async () => {
    const offenders = (await sources()).filter(({ text }) =>
      /from\s+'(framer-motion|motion\/react|motion|@react-spring\/[\w-]+|gsap)'/.test(text),
    );

    expect(offenders.map(({ path }) => path)).toEqual([]);
  });

  /**
   * A Tailwind v4 JS config *merges* `screens` rather than replacing them, so its
   * mere existence would restore Tailwind's 640px `sm:` over D2's 375px one. The
   * file must not exist under any of its four names.
   */
  it('has no tailwind config file', async () => {
    const entries = await readdir(appRoot);
    expect(entries.filter((name) => name.startsWith('tailwind.config.'))).toEqual([]);
  });

  /**
   * "There is no second stylesheet, no CSS module and no runtime style injection"
   * — `app/globals.css`'s own opening claim, checked. A `.module.css` appearing
   * beside a component is the way AL-52 is most likely to be broken by accident,
   * because it looks like Tailwind's neighbour rather than its replacement.
   */
  it('has exactly one stylesheet', async () => {
    const found: string[] = [];
    for (const root of SOURCE_ROOTS) {
      for await (const file of walk(join(appRoot, root), new Set(['.css']))) {
        found.push(relative(appRoot, file));
      }
    }

    expect(found).toEqual(['app/globals.css']);
  });
});

describe('fence · the capture pipeline stays out of the site', () => {
  /**
   * S05 added two things that would be fences broken if they ever moved: a second
   * `.css` file, and a browser automation library. Both are legitimate where they
   * are — `scripts/` is a build-tool directory, not a source root — and both would
   * be violations one import away.
   *
   * The "exactly one stylesheet" test above walks `app/` and `src/` only, so it
   * passes whether or not `scripts/polish.css` exists. That is correct, and it also
   * means nothing above notices if a component imports it.
   */
  it('never imports the capture stylesheet from the site', async () => {
    const offenders = (await sources()).filter(({ text }) => text.includes('polish.css'));

    expect(offenders.map(({ path }) => path)).toEqual([]);
  });

  /**
   * `playwright-core` rather than `playwright`, and the distinction is load-bearing
   * rather than stylistic: `playwright`'s postinstall downloads a browser, CI runs
   * `npm --prefix portals ci` with no skip flag, and plan §A17 exists to keep a
   * browser download off the critical path of every portal build.
   */
  it('depends on no browser that CI would have to download', () => {
    const all = { ...manifest.dependencies, ...manifest.devDependencies };

    expect(all).not.toHaveProperty('playwright');
    expect(all).not.toHaveProperty('@playwright/test');
    expect(all).not.toHaveProperty('puppeteer');
    expect(manifest.devDependencies).toHaveProperty('playwright-core');
  });

  it('keeps the capture tooling out of `dependencies`', () => {
    for (const name of [
      'playwright-core',
      'sharp',
      '@fontsource-variable/inter',
      '@fontsource-variable/outfit',
    ]) {
      expect(manifest.dependencies, name).not.toHaveProperty(name);
      expect(manifest.devDependencies, name).toHaveProperty(name);
    }
  });

  /**
   * `sharp` is a native module and by far the heaviest thing in this workspace. It
   * composites at refresh time, by hand, a handful of times in the project's life —
   * a page that imported it would pull libvips into a marketing site's server
   * bundle to render an image that was already rendered months earlier.
   */
  it('never imports sharp from the site', async () => {
    const offenders = (await sources()).filter(({ text }) => /from\s+'sharp'/.test(text));

    expect(offenders.map(({ path }) => path)).toEqual([]);
  });

  /**
   * The two font packages are capture-time assets: the site self-hosts the same
   * families through `next/font` (S04), and importing a `@fontsource` stylesheet
   * into the app would put a second copy of Inter in the bundle *and* a second
   * stylesheet in a surface that is allowed exactly one.
   */
  it('never imports a @fontsource package from the site', async () => {
    const offenders = (await sources()).filter(({ text }) => text.includes('@fontsource'));

    expect(offenders.map(({ path }) => path)).toEqual([]);
  });
});

describe('fence · an unpublished locale ships no bytes', () => {
  /**
   * **`src/i18n/messages/all.ts` is the total lookup and nothing that renders may
   * import it.**
   *
   * S19 measured the client bundle and found all three message tables in it — 133 kB
   * gzipped of the 320 kB a browser downloads for `/`, roughly a third of it Tamil
   * that no URL renders. The cause was one static import: eleven client components
   * pull `@/i18n` for the translator, and that module imported every table. A static
   * import is a graph edge whether or not the value is read, so nothing in lint,
   * `tsc` or the tests could see it. Nothing was wrong with the code.
   *
   * The total lookup moved to its own module and `@/i18n` now carries the published
   * tables only, which buys the invariant this test exists to keep:
   *
   *   > A locale's table reaches a browser **if and only if** the locale is published.
   *
   * One import from a page or a component undoes all of it, silently, and the build
   * would still be green — so it is asserted rather than documented. Tests read it
   * freely; that is what it is for.
   */
  it('is imported by no page and no component', async () => {
    const offenders = (await sources()).filter(
      ({ path, text }) =>
        !path.startsWith('src/i18n/') && /from\s*['"`][^'"`]*i18n\/messages\/all['"`]/.test(text),
    );

    expect(offenders.map(({ path }) => path)).toEqual([]);
  });

  /**
   * And the published module names no unpublished table.
   *
   * The other half of the same invariant, from the other direction: the check above
   * stops a *new* importer, this one stops the original mistake being made again in
   * the module where it was made.
   */
  it('keeps every unpublished table out of the translator module', async () => {
    const index = (await sources()).find(({ path }) => path === 'src/i18n/index.ts');
    expect(index, 'src/i18n/index.ts must exist').toBeDefined();

    const { WWW_LOCALES } = await import('../src/i18n/index.ts');
    const unpublished = (['si', 'ta', 'en'] as const).filter(
      (locale) => !WWW_LOCALES.includes(locale),
    );

    for (const locale of unpublished) {
      expect(
        new RegExp(`from\\s*['"\`]\\./messages/${locale}['"\`]`).test(index?.text ?? ''),
        `src/i18n/index.ts must not import the ${locale} table while ${locale} is unpublished`,
      ).toBe(false);
    }
  });
});

describe('fence · no resource table in the client bundle (MCS-36 D3)', () => {
  /**
   * **No client component imports the translator**, and this is the fence the whole
   * decision rests on.
   *
   * `@/i18n` exists to hold `createWwwTranslator`, which needs the message tables —
   * so importing *anything* from it drags **~88 kB gzipped** into whatever imported
   * it. Until D3 that was fourteen client modules, one of them the shared header, so
   * both published locales' entire corpus was in every page's bundle. Measured after
   * converting them: first-party JS on `/` fell from **113.7 kB to 17.0 kB**.
   *
   * Nothing in the type system prevents this coming back. A client component that
   * calls `createWwwTranslator(locale)` compiles, renders correctly, and passes every
   * other test in this suite — the only symptom is 88 kB nobody notices. So it is
   * asserted by import, over the actual client graph rather than over a list of files
   * somebody maintains.
   *
   * **What a client component may import instead:** `@/i18n/locales` for the published
   * set and the tags, `@/i18n/substitute` for placeholder filling, and
   * `@/i18n/error-strings` for the generated four. None of them touches a table.
   */
  it('is imported by no client component, however deep', async () => {
    const graph = await clientGraph();
    const offenders = graph.filter(({ text }) =>
      /from\s*['"`]@\/i18n['"`]/.test(text.replace(/import\s+type[^;]+;/g, '')),
    );

    expect(offenders.map(({ path }) => path)).toEqual([]);
  });

  /**
   * And the entry points are what they are believed to be.
   *
   * A guard on the guard: if `clientGraph()` ever resolved nothing — a changed alias, a
   * renamed directory — the check above would pass over an empty list and prove
   * nothing. This is the same shape as the `UNPUBLISHED_PAGES` assertion in
   * `test/routes.test.ts`, and for the same reason.
   */
  it('walks a client graph that is actually there', async () => {
    const graph = await clientGraph();
    expect(graph.length).toBeGreaterThan(20);
    expect(graph.some(({ path }) => path.endsWith('components/nav/Header.tsx'))).toBe(true);
  });
});

describe('fence · every user-facing string is a resource', () => {
  /**
   * The ESLint rule catches a literal in JSX as it is typed. What it cannot see is
   * a **literal handed in as a prop** — `title="Privacy policy"` on a component
   * that renders it — because from the rule's point of view that is an attribute
   * value on an element it knows nothing about.
   *
   * S18 generalised this. It used to be one assertion about `StubPage`, which took
   * a route *path* and resolved its heading through the translator rather than
   * being passed a title; that component is gone (below), and the property it was
   * demonstrating is worth holding over the whole tree instead of over one file.
   *
   * The attribute list is `mageride/no-literal-user-facing-strings`' own, plus
   * `closeLabel` — `@mageride/ui`'s `Modal` requires it, and it is a control name
   * read aloud like any other.
   */
  it('hands no user-facing literal to any component as a prop', async () => {
    const offenders = (await sources()).filter(({ text }) =>
      /\s(?:title|label|alt|placeholder|aria-label|closeLabel)=["'][^"']/.test(text),
    );

    expect(offenders.map(({ path }) => path)).toEqual([]);
  });

  /**
   * **The scaffold is gone, and this is what stops it coming back.**
   *
   * `portals/www/CLAUDE.md`: *"`src/components/scaffold/` is temporary. Every use
   * of `StubPage` is a page nobody has written yet; S14–S18 delete them one at a
   * time, and the directory goes with the last one."* S18 wrote the last five —
   * `/screens`, `/faq`, `/download`, `/contact` and the three `legal/*` — so the
   * directory went with them, along with `www.scaffold.notice` in all three
   * languages.
   *
   * Asserted rather than merely done, because the failure it prevents is quiet: a
   * later session that needs a placeholder page would reach for the obvious name,
   * reintroduce "a page that says it is not written yet", and there would be
   * nothing to notice that Phase 5's gate had been reopened.
   */
  it('has no scaffold directory left', async () => {
    const components = await readdir(join(appRoot, 'src/components'));
    expect(components).not.toContain('scaffold');
  });
});
