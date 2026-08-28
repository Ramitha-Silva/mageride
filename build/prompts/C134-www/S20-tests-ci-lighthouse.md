# C134 · S20 — the test suite, the Lighthouse gate, visual regression

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 20 of 22 · Phase 7 (Testing & CI).**

**Prerequisite:** S19.

Tests have been referenced by nine earlier sessions as "S20 asserts this". This is where those
promises are paid. Read the handoffs from S14–S19 for the specific claims each one deferred here.

---

## Know this before you start (README §4.6)

`.github/workflows/ci.yml`'s portal leg is:

```
npm --prefix portals ci
npm --prefix portals run lint
npm --prefix portals run build
```

**It does not run `test`.** Adding `www` to `portals/package.json`'s `workspaces` (S03) does put
this surface in CI's lint and build — `docs/www-site-plan.md` §A38 is right that no workflow edit is
needed for that — but **the Vitest suite is enforced by C134's own `verify_cmd`, not by CI**.

So: anything that must never regress on `main` belongs in `lint` or `build` (the ESLint rules,
`check-al52.mjs`, `check-i18n-parity.mjs`, `check-bundle.mjs`), and the Vitest suite is the
component's gate. Write the suite anyway — it is the Definition of Done — and **say this out loud in
the handoff** so nobody later reads a green main as "the tests ran".

Also **measure the portals job's runtime** with a fifth Next build in it. If it approaches the job
timeout, matrix-split the portals leg by workspace (plan §A38's contingency) and record the numbers.

---

## Do this — `portals/www/test/`

`portals/web-passenger/test/` is the precedent for all of these; `fences.test.ts` especially.

| File | Asserts |
|---|---|
| `i18n.test.ts` | every key exists in every rendered locale; no orphan keys; **placeholder sets match per key**; zero `TODO(` markers; and — if Tamil was deferred — that `ta.ts` is still structurally complete while `/ta` 404s |
| `routes.test.ts` | every route in `src/lib/routes.ts` renders; every guide slug resolves in every rendered locale; **the sitemap covers 100% of routes**; no route renders that is absent from the table |
| `a11y.test.ts` | axe-core over every page shell; the carousel's roles, labels and `aria-current`; the lightbox's focus trap and restore; focus order; one `<h1>` per page; **reduced motion stops autoplay** |
| `seo.test.ts` | canonical present; **`hreflang` reciprocal both ways**; every JSON-LD block parses and validates for its declared type; no `hreflang` for an unrendered locale; cache headers as specified (once S21 sets them) |
| `fences.test.ts` | **no `fetch` / `axios` / `sendBeacon` / `EventSource` anywhere in the tree**; no `NEXT_PUBLIC_*`; no `document.cookie`; no banned motion package in `package.json`; no `tailwind.config.*` |
| `content.test.ts` | every `screenRef` resolves to a file that exists in `public/screens/`; every chapter is in the registry with a unique slug and contiguous `order`; **the six driver fee tiers match URD §1** |

### The two that carry the most weight

**`fences.test.ts`** is the executable form of the C134 fence "renders with the backend down". A
grep-based test over the source tree is exactly the right shape here — the same reasoning
`portals/scripts/check-al52.mjs` documents for its own stricter-than-grep sweep. It must scan
`app/`, `src/` and `scripts/`, and it must **not** exempt anything without a named reason in a
comment.

**`content.test.ts`**'s fee-tier assertion is the one that keeps a public page honest. Parse the six
values out of `specs/user-requirements-document.md` §1 and compare against the exported constant —
**do not** hard-code the expected numbers in the test, or the test and the site drift together and
prove nothing. `portals/admin/test/routes.test.ts` parses `AdminMenu.cs` and builds expectations from
the URD's own table; that is the pattern to copy.

---

## Lighthouse CI gate

A job asserting **≥95 Performance / 100 Accessibility / ≥95 SEO** on `/`, `/drivers` and one guide
chapter, in every rendered locale. Below threshold fails.

- Run it against a real `next build && next start`, not a dev server.
- Mobile emulation with throttling — the target is the Sri Lankan median device (S19), and a desktop
  run will pass while the real page is slow.
- Where to put it is a judgement call: a separate workflow keeps the portals job fast and is easier
  to make non-blocking while it stabilises. **Whichever you choose, the thresholds are the fence** —
  a job that reports without failing is not a gate.
- If a threshold cannot be met, **fix the page, do not lower the number.** If it genuinely cannot be
  met, record why in the handoff and raise it as a finding.

## Visual regression *(optional, recommended)*

Playwright screenshot diffs on the hero and two guide chapters, both appearances. It catches the one
class of change nothing else does: a token edit in `@mageride/tailwind-preset` silently reflowing
this site. Playwright is already a devDependency from S05.

Commit the baselines; keep them small; note in the handoff that a preset change is expected to fail
this and that the fix is to review-and-rebaseline, not to delete the test.

## Housekeeping

Delete `app/[locale]/_motion-demo/` (S04). Confirm it is gone from `routes.ts` and the sitemap.

---

## Fences

- **No test that asserts a hard-coded copy of a spec value.** Parse the spec.
- **No threshold lowered to make a build pass.**
- **No fence exemption without a named reason in a comment.**

---

## Verify

```
npm --prefix portals run lint
npm --prefix portals run build --workspace @mageride/www
npm --prefix portals run test --workspace @mageride/www
npm --prefix portals run lint && npm --prefix portals run test     # all eight workspaces green
```

Then the full C134 verify command, exactly as `build/manifest.yaml` declares it:

```
npm --prefix portals run lint && npm --prefix portals run build --workspace @mageride/www && npm --prefix portals run test --workspace @mageride/www
```

---

## Handoff

- **Component:** C134 www-informational-site — S20 (tests & CI) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** test count and file count; the measured portals-job runtime with the fifth build and
  whether a matrix split was needed; the Lighthouse scores actually achieved per page per locale;
  **an explicit note that CI runs lint+build only and the Vitest suite is the component's own gate**.
