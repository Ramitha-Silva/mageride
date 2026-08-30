# C134 · S03 — scaffold `portals/www` and wire it into the workspace

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 3 of 22 · Phase 1 (Scaffold).**

**Prerequisite:** S02 is complete. C134 is in the manifest; `build/prompts/C134.md` exists.

The deliverable is an **empty site that builds**. Four routes that say almost nothing, in three
locales, with every gate already wired. Everything after this session is content poured into a shape
that is already green.

---

## Before you start

Read these, in this order. They are not background — each one is a decision you must copy:

1. `portals/web-passenger/CLAUDE.md` — the voice and structure of a surface CLAUDE.md.
2. `portals/web-passenger/next.config.ts` — read the whole comment block. `outputFileTracingRoot`
   and the "no `tailwind.config.js`" reasoning both apply here verbatim.
3. `portals/web-passenger/app/globals.css` — the two imports and the `@source` line are the entire
   styling setup.
4. `portals/web-passenger/app/layout.tsx` — `next/font/google`, the pre-paint appearance script.
5. `portals/web-passenger/src/i18n/index.ts` — **the two-table i18n pattern** (README §4.4).
6. `portals/web-passenger/package.json`, `vitest.config.ts`, `eslint.config.js`, `tsconfig.json`.
7. `portals/scripts/ensure-workspace-deps.mjs` — why every portal has `prebuild`/`prelint`/`pretest`.
8. `infra/docker/Dockerfile.portal` — the header table and the `deps` stage COPY list.

---

## Do this

### 1 · Create `portals/www/` — the shape from `docs/www-site-plan.md` §A5

Create the tree the plan names. Everything below `app/[locale]/` may be a stub that renders its
heading from a resource key; the point of this session is that the **shape compiles**.

Non-obvious pieces, and why:

| File | What it must contain |
|---|---|
| `package.json` | name `@mageride/www`, `"type": "module"`, deps `@mageride/i18n` `@mageride/tailwind-preset` `@mageride/ui` `next` `react` `react-dom` at the **same versions `web-passenger` pins**. Scripts: the same `predev`/`prebuild`/`prelint`/`pretest` → `node ../scripts/ensure-workspace-deps.mjs` hooks, `build: "next build && node scripts/check-bundle.mjs"`, `lint: "eslint . && tsc --noEmit"`, `test: "vitest run"`. **No `maplibre-gl`, no `pmtiles`** — there is no map on this surface. |
| `next.config.ts` | `output: 'standalone'`, `outputFileTracingRoot` two levels up, `reactStrictMode: true`, `poweredByHeader: false`, `images: { formats: ['image/avif','image/webp'] }`. **No `tailwind.config.js`, ever** — a v4 JS config *merges* `screens` and would restore Tailwind's 640px `sm:` over D2's 375px. Cache headers arrive in S21; leave a comment saying so. |
| `postcss.config.mjs` | byte-identical to `web-passenger`'s. One plugin. |
| `app/globals.css` | `@import 'tailwindcss'; @import '@mageride/tailwind-preset/theme.css';` plus `@source '../../ui/src/**/*.tsx';` — the `@source` line is required because `@mageride/ui` ships from a gitignored `dist/` and Tailwind's source detection would skip it. |
| `vitest.config.ts` | copy `web-passenger`'s, minus the `server-only` alias unless you end up needing it. Keep `oxc: { jsx: { runtime: 'automatic' } }` — `tsconfig` says `jsx: preserve` and Vitest has no Next in front of it. |
| `.env.example` | **Deliberately near-empty.** This surface reads no gateway. If a variable is ever needed it is server-read and never `NEXT_PUBLIC_*`. Write that sentence in the file. |

### 2 · Locale routing — path-prefixed, unlike `web-passenger`

`web-passenger` uses `?lang=` and explains why (an SMS carries one URL, minted before anyone knew
the reader's language). **This surface is the opposite case**: it is indexed, it needs `hreflang`,
and a locale must be a canonical URL. So:

- `app/[locale]/layout.tsx` with `generateStaticParams()` over `LOCALES`.
- `app/page.tsx` at the root redirects to the negotiated locale — `negotiateLocale()` from
  `@mageride/i18n` over `Accept-Language`, defaulting to `si`.
- An unknown `[locale]` segment must `notFound()`, not fall through to Sinhala.
- `<html lang>` is the segment, not a header read.

### 3 · The i18n layer — two tables, `en` defines the key set

Follow `portals/web-passenger/src/i18n/` exactly (README §4.4):

- `src/i18n/messages/en.ts` — `export const wwwEn = { … } as const` plus
  `export type WwwMessages = typeof wwwEn` and `export type WwwMessageKey = keyof WwwMessages`.
- `si.ts` / `ta.ts` — `export const wwwSi: WwwMessages = { … }`. A key present in one and not the
  others is now a **compile error**, which is the whole guarantee.
- `src/i18n/index.ts` — `createWwwTranslator(locale)` layering the surface table over
  `@mageride/i18n`'s shared one.
- `src/i18n/server.ts` — `'server-only'`, a `cache()`d locale resolver per request.

Seed it with ~15 keys: the brand, the nav labels, a placeholder heading per route. That is enough to
prove the ESLint rule and the parity script.

### 4 · `scripts/check-i18n-parity.mjs`

Every key in `si`, `ta` and `en`; no orphans; no key defined in `si`/`ta` that `en` does not have.
The compiler already catches most of this — the script exists for the cases it cannot see (a key
that exists everywhere but is never rendered, and a placeholder `{name}` present in one language and
missing in another, which is a real translation failure mode). Wire it into `lint`.

### 5 · `scripts/check-bundle.mjs`

Copy `portals/web-passenger/scripts/check-bundle.mjs` and adapt:

- keep the runtime-signature sweep and the `banned.packages` / `banned.prefixes` scan;
- keep the "no stylesheet emitted" assertion;
- `SERVER_ONLY_VARIABLES` becomes **"no `NEXT_PUBLIC_` prefix appears in any client chunk at all"** —
  this surface has no server-only config to leak, so the stronger statement is the true one;
- add the **motion-library signature sweep**: `framer-motion`, `motion/react`, `@react-spring`,
  `gsap`, `popmotion` — the C134 fence AL-52's list does not cover (README §4.3);
- leave the JS/CSS byte budgets as reported-only for now; S19 turns them into thresholds.

### 6 · `src/lib/routes.ts` — the typed route table

One module that enumerates every route. `app/sitemap.ts`, the nav, the footer, the `hreflang` block
and `test/routes.test.ts` all read it, so **a route that exists and is not listed is a test failure**
rather than a page nobody links to. Seed it with the ~14 top-level routes; the guide chapter slugs
join it in S17.

### 7 · Register the workspace — four places, all four or none

| File | Change |
|---|---|
| `portals/package.json` | add `"www"` to `workspaces` |
| `infra/docker/Dockerfile.portal` — deps stage | add `COPY ["portals/www/package.json", "./portals/www/"]`. **This is not optional**: the header comment says the list must be kept in step with the workspaces array, because `npm ci` resolves the whole workspace in one pass and a missing member fails the install for **unrelated** portals. |
| `infra/docker/Dockerfile.portal` — header | add `www` to the PORTAL table (`www — Public informational site, www.mageride.lk, port 3004 (MCS-34, C134)`), and update the header's "One template for the **three** web surfaces" to four |
| `infra/docker/Dockerfile.portal` — build stage | the `PORTAL` guard's error string `<admin\|fleet\|web-passenger>` gains `\|www` |

`infra/k8s/service-catalog.yaml` is the fifth place and belongs to **S21**, not here.

### 8 · `portals/www/CLAUDE.md`

In the house voice of the other three. It must state:

- the four C134 fences and, for each, the executable form that enforces it;
- the motion policy (S04's decision, forward-referenced) and why no motion library;
- **"no API at request time"** and what that forbids concretely: `fetch`, `axios`, SSE, map tiles,
  a live vehicle count, `NEXT_PUBLIC_*`;
- the content-format decision — typed TS under `src/content/`, not MDX — with the reasoning from
  `docs/www-site-plan.md` §A6 (a per-locale MDX chapter lets the *structure* diverge between
  Sinhala and English; a typed `Chapter` keeps the shape shared and localises only the strings);
- the image-pipeline contract: `public/screens/` is **generated**, refreshed by
  `npm run screens:refresh`, committed on purpose so CI never downloads a browser;
- the locale-routing divergence from `web-passenger` and why (§2 above).

---

## Fences

- **No `tailwind.config.js`.** Ever. State it in the file that would otherwise be it.
- **No dependency that is not already in `web-passenger`**, except the ones this session
  deliberately adds. Playwright and `sharp` arrive in S05/S06 as **devDependencies only**.
- **No `NEXT_PUBLIC_*`.** Not one.
- **No literal user-facing string**, including in the stub pages. The stubs render resource keys.

---

## Verify

```
npm --prefix portals ci
npm --prefix portals run lint
npm --prefix portals run build
npm --prefix portals run test --workspace @mageride/www
test -f portals/www/.next/standalone/portals/www/server.js && echo "standalone layout correct"
docker build -f infra/docker/Dockerfile.portal --build-arg PORTAL=www --build-arg PORT=3004 -t mageride/www-site:dev .
```

The `standalone` path assertion is the one that catches a wrong `outputFileTracingRoot` — the
Dockerfile turns it into a build-time error, and finding it here is cheaper than finding it in S21.

**Build-host note:** keep the replica stack **down** (`CLAUDE.md`, Build Host). The Docker build and
a fifth Next build are fine on the 24 GB box; the replica beside them is not.

---

## Handoff

- **Component:** C134 www-informational-site — S03 (scaffold) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** the route count `next build` reported, the four workspace registration points touched,
  any place `web-passenger`'s shape did not transfer and what you did instead.
