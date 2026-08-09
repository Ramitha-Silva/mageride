import { readFileSync, readdirSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

/**
 * The four fences C117 is built under, asserted against the tree rather than
 * trusted.
 *
 * Each one is a property that is easy to hold today and easy to break in a later
 * pull request — which is exactly the kind of rule that has to be executable. They
 * are this surface's counterpart to `PublicBffApplication.GuardTheSurface`, which
 * refuses to *start* if a route escapes the token gate: the service holds its half
 * at boot, and this holds the client's half at build.
 */

const APP_ROOT = resolve(import.meta.dirname, '..');
const SOURCE_ROOTS = ['app', 'src'];

interface SourceFile {
  readonly path: string;
  readonly source: string;
}

function sources(): SourceFile[] {
  const files: SourceFile[] = [];

  const walk = (dir: string) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (/\.tsx?$/.test(entry.name)) {
        files.push({
          path: relative(APP_ROOT, full).replaceAll('\\', '/'),
          source: readFileSync(full, 'utf8'),
        });
      }
    }
  };

  for (const root of SOURCE_ROOTS) walk(join(APP_ROOT, root));
  return files;
}

const FILES = sources();

/** Strips comments, so a rule about code is not tripped by a paragraph about it. */
function code(source: string): string {
  return source.replaceAll(/\/\*[\s\S]*?\*\//g, '').replaceAll(/(^|[^:])\/\/.*$/gm, '$1');
}

describe('the browser never reaches the platform', () => {
  it('calls fetch against MageRide in exactly one module', () => {
    // `apiFetch` is where `no-store`, the problem+json handling and the one gateway
    // origin live. The live hook's `fetch` is the *other* kind: a same-origin call
    // to this application's own `/api/live` proxy, asserted below.
    const callers = FILES.filter(({ source }) => /(?<![\w.])fetch\(/.test(code(source))).map(
      ({ path }) => path,
    );

    expect(callers.sort()).toEqual(['src/api/http.ts', 'src/live/useLiveTrack.ts']);
  });

  it('lets the live hook fetch only this origin’s own proxy', () => {
    const hook = code(
      FILES.find(({ path }) => path === 'src/live/useLiveTrack.ts')!.source,
    );

    // A relative path, always. An absolute URL here would be the gateway address in
    // a client bundle, which is the one thing this surface promises it has not got.
    expect(hook).toMatch(/`\/api\/live\//);
    expect(hook).not.toMatch(/https?:\/\//);
  });

  it('publishes no NEXT_PUBLIC_ variable anywhere', () => {
    // The share token is in the address bar, so this is not about hiding a
    // credential — it is about `passenger.mageride.lk` being the only host a phone
    // opened from an SMS ever talks to.
    // Over the code and not the comments: several modules explain *why* they have
    // no such variable, and a fence that could not be described in the file it
    // guards is a fence nobody maintains.
    for (const { path, source } of FILES) {
      expect(code(source), `${path} publishes a build-time public variable`).not.toContain(
        'NEXT_' + 'PUBLIC_',
      );
    }
  });

  it('reads process.env in exactly one module', () => {
    const readers = FILES.filter(({ source }) => /process\.env/.test(code(source))).map(
      ({ path }) => path,
    );

    expect(readers).toEqual(['src/config/env.ts']);
  });
});

describe('the token is the credential, and one module writes it into a URL', () => {
  it('builds a /public/track path in exactly one place', () => {
    const builders = FILES.filter(({ source }) => /\/public\/track/.test(code(source))).map(
      ({ path }) => path,
    );

    expect(builders).toEqual(['src/api/track.ts']);
  });

  it('never interpolates a token into a path outside that module', () => {
    for (const { path, source } of FILES) {
      if (path === 'src/api/track.ts') continue;
      expect(code(source), `${path} composes a public-bff path`).not.toMatch(
        /`\/public\/[^`]*\$\{/,
      );
    }
  });
});

describe('AL-48 — there is no /call, and there is no masked number', () => {
  it('never addresses the removed endpoint', () => {
    // public-bff's start-up guard refuses a route whose path contains `/call` **by
    // name**, because several pre-AL-48 spec lines still describe the proxy-DID
    // lease. This is the same guard on the other side of the wire.
    for (const { path, source } of FILES) {
      expect(code(source), `${path} calls the endpoint AL-48 removed`).not.toMatch(
        /\/track\/[^'"`]*\/call/,
      );
    }
  });

  it('dials the driver with a plain tel: link', () => {
    const dialers = FILES.filter(({ source }) => /href=\{`tel:/.test(source)).map(
      ({ path }) => path,
    );

    // SCR-WT-002's driver card and SCR-WT-004's button row. Exactly the two screens
    // D2 Δ 2026-07-05 #2 names.
    expect(dialers.sort()).toEqual(['src/components/DriverCard.tsx', 'src/components/RideTrack.tsx']);
  });
});

describe('P-02 — declining transmits no GPS', () => {
  it('gives the decline call no parameter that could carry a coordinate', () => {
    const track = code(FILES.find(({ path }) => path === 'src/api/track.ts')!.source);
    const declaration = /export async function declinePickup\(([^)]*)\)/.exec(track);

    expect(declaration).not.toBeNull();
    expect(declaration![1]!.replaceAll(/\s+/g, ' ').trim()).toBe('token: string');
  });

  it('gives the decline action no parameter that could carry one either', () => {
    const actions = code(
      FILES.find(({ path }) => path === 'src/server/track-actions.ts')!.source,
    );
    const declaration = /export async function declinePickupLocation\(([^)]*)\)/.exec(actions);

    expect(declaration).not.toBeNull();
    expect(declaration![1]!.replaceAll(/\s+/g, ' ').trim()).toBe('token: string');
  });

  it('sends no body on the decline request', () => {
    const track = FILES.find(({ path }) => path === 'src/api/track.ts')!.source;
    const start = track.indexOf('export async function declinePickup(');
    const body = track.slice(start, track.indexOf('\n}', start));

    expect(body).toContain("method: 'POST'");
    expect(body).not.toContain('body');
  });

  it('says so on the screen, in all three languages', () => {
    const screen = FILES.find(({ path }) => path === 'src/components/PickupConfirm.tsx')!.source;
    expect(screen).toContain("t('web.pickup.noGps')");
  });
});

describe('SCR-WT-006 carries no ride data, by construction', () => {
  it('takes a translator, a locale, a path and a store URL — and nothing else', () => {
    const deadEnd = FILES.find(({ path }) => path === 'src/components/DeadEnd.tsx')!.source;
    const props = /export function DeadEnd\(\{([^}]*)\}/.exec(deadEnd);

    expect(props).not.toBeNull();
    expect(
      props![1]!
        .split(',')
        .map((name) => name.trim())
        .filter(Boolean)
        .sort(),
    ).toEqual(['appUrl', 'here', 'locale', 't']);
  });

  it('is where every route sends a dead token, through one dispatcher', () => {
    // Three routes redeem a token and none of them decides anything about it: all
    // three call `trackScreen`, which is the only place the 404/410 branch exists.
    // Three copies of it would be three places to forget it, and forgetting it
    // renders a Next error page with a stack trace on somebody's phone.
    for (const path of ['app/track/page.tsx', 'app/t/[token]/page.tsx', 'app/p/[token]/page.tsx']) {
      const page = FILES.find((file) => file.path === path)!.source;
      expect(page, `${path} does not go through the token gate`).toContain('trackScreen(');
      expect(code(page), `${path} redeems a token itself`).not.toContain('readSnapshot');
    }

    const gate = FILES.find(({ path }) => path === 'src/server/screen.tsx')!.source;
    expect(gate).toContain('isDeadToken(error)');
    expect(gate).toContain('<DeadEnd');
  });

  it('never redirects, because the gate streams a spinner before it knows the scope', () => {
    // `loading.tsx` starts the response while the token is being redeemed (D2's
    // "≤ 1 s"), so a `redirect()` after that can only be finished as a meta refresh
    // — a spinner, a blank, and a second spinner for a rider whose car is moving.
    // Every scope renders in place instead.
    for (const { path, source } of FILES) {
      expect(code(source), `${path} redirects after the gate`).not.toMatch(/\bredirect\(/);
    }
  });
});

describe('mobile-first at 375 px', () => {
  it('gives nothing a fixed width wider than the primary viewport', () => {
    // C117's fourth fence, and the half of "no horizontal scroll at 375 px" that can
    // be checked without a browser. `overflow-x-clip` on the body is the backstop;
    // this is the rule it backs up, because a page that only *looks* right because
    // its overflow is clipped is a page with something unreachable on it.
    //
    // A `max-w-` is a ceiling rather than a width and is exempt: the column is
    // capped at 480px so a phone page is not stretched across a laptop.
    const TOO_WIDE = /(?<!max-)\b(?:min-)?w-\[(\d+)px\]|\bsize-\[(\d+)px\]/g;

    for (const { path, source } of FILES) {
      for (const match of source.matchAll(TOO_WIDE)) {
        const pixels = Number(match[1] ?? match[2]);
        expect(pixels, `${path} sets ${match[0]}, which cannot fit 375px`).toBeLessThanOrEqual(375);
      }
    }
  });

  it('never sizes anything to the viewport width', () => {
    // `w-screen` is `100vw`, which on a page with a vertical scrollbar is wider than
    // the viewport — the classic source of a phone page that scrolls sideways by
    // exactly the width of a scrollbar.
    for (const { path, source } of FILES) {
      expect(source, `${path} uses w-screen`).not.toMatch(/\bw-screen\b/);
    }
  });
});

describe('server-only stays server-only', () => {
  it('marks every module that can reach public-bff', () => {
    for (const path of [
      'src/api/http.ts',
      'src/api/track.ts',
      'src/server/render.ts',
      'src/i18n/server.ts',
    ]) {
      const module = FILES.find((file) => file.path === path)!.source;
      expect(module, `${path} is importable from a client component`).toContain(
        "import 'server-only'",
      );
    }
  });

  it('never imports one of those from a client component', () => {
    for (const { path, source } of FILES) {
      if (!source.startsWith("'use client'")) continue;

      expect(code(source), `${path} is a client component that imports the data layer`).not.toMatch(
        /from '@\/api\/(http|track)'/,
      );
    }
  });
});
