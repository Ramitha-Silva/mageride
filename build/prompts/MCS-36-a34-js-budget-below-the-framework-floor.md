# MCS-36 — A34's JS budget is below the framework's own floor

## Identity

This is a **micro-change-set**, not a manifest component, and this file is **hand-written**.

`build/tools/generate_build_plan.py` writes one `build/prompts/Cxxx.md` per entry in
`build/manifest.yaml` and deletes nothing, so this file survives a regeneration untouched.
**It adds no component**, so the generator must **not** be re-run for it.

Raised from C134 · S19 (SEO, accessibility, performance), 2026-08-30, from a measurement rather
than a reading. Extended by S20 with the Lighthouse evidence.

**D1, D2 and D3 were put to the user on 2026-08-30 and all three were accepted.**

| # | Delta | State |
|---|---|---|
| **D1** | A34's JS budget restated as three figures — first-party ≤ 90 KB gz, framework floor reported, total ≤ 300 KB gz | **ACCEPTED 2026-08-30.** Written into `docs/www-site-plan.md` §A34, which was the only place A34 is stated. The new total ceiling is enforced by `portals/www/scripts/check-budget.mjs` in the same change, so the delta did not open a fresh spec/code gap of its own |
| **D2** | The framework floor recorded as a measured value with its date and dependency set | **ACCEPTED 2026-08-30.** §A34 records **163.4 KB gzipped, Next 16.3.0 · React 19.2.8 · react-dom 19.2.8, Turbopack**, and says to re-measure on a framework upgrade and record the new value with its date |
| **D3** | The client-boundary rule in `portals/www/CLAUDE.md` — either it stands and the surface carries ~88 KB gz of resource tables in every page load, or it is amended and a session converts the islands | **ACCEPTED 2026-08-30, and done.** The rule is amended and **fourteen** modules were converted — more than the eleven this change set estimated, because `ScreenImage`, `Callout` and `GalleryBody` carry no `'use client'` of their own and were in the bundle transitively. **First-party JS on `/`: 113.7 → 17.0 KB gzipped. The A34 budget passes with 73 KB spare.** |

**What accepting D1 and D2 did.** It closed the divergence between the code and the spec:
`check-budget.mjs` had been applying 90 KB to first-party bytes while §A34 said *total*, and under
CLAUDE.md's first Universal Rule the spec wins, so the code was in the wrong until the document
caught up. It changed no threshold and moved no byte.

**What accepting D3 did — and one thing it did not.** It moved 96.7 KB gzipped off every page, and
the byte result is deterministic and verified twice over: `npm run budget` passes, and no chunk the
document loads contains a resource table any more (checked by searching the built chunks for a table
value marker, not by trusting the total).

**It did not visibly move Lighthouse Performance, and this build host cannot say whether it should
have.** The gate still reports 59–86 against 95. That is not evidence the change failed: the box was
carrying the 23-container replica at load average 6.25 while measuring, and three runs of the *same
build* returned Total Blocking Time of 780, 870 and 1,220 ms — a 56% spread, wider than any effect
worth attributing. LCP sat at a flat 4.0 s across all three, which is consistent with LCP here being
gated by render rather than by hydration, and therefore by something D3 was never going to touch.
**Re-measure on a quiet machine before drawing any conclusion about Performance**, and see the
caveat under *What S20 added* — it applies with more force now, not less.

---

## The finding

**A34 states a number that no build of this surface can meet, and the shortfall is not ours.**

> JS ≤ 90 KB gzipped on `/`

Measured against the C134 build of 2026-08-30, on the prerendered home page in each published
locale, gzipped at level 9, counting exactly the files the document references and excluding the
`noModule` polyfill chunk no modern browser fetches:

| | gzipped | what it is |
|---|---|---|
| `react-dom` | 69.8 kB | |
| Next's app-router client | 42.5 kB | flight, prefetch, the router |
| React + scheduler | 32.9 kB | |
| Turbopack runtime + `next/dist` + bootstrap | 18.1 kB | |
| **framework floor** | **163.3 kB** | **before one line of this surface's code** |
| this surface's own JavaScript | 114.6 kB | 91.9 of it the si + en resource tables |
| **total a browser downloads for `/`** | **277.9 kB** | |
| *(excluded)* `noModule` polyfills | 38.5 kB | fetched by no browser built this decade |

**The framework floor alone is 1.8× the whole budget.** An empty App Router page — no components, no
content, no resources — breaches A34 by 73 kB. The budget is therefore not a constraint that the
surface is failing; it is a number that cannot be satisfied while the surface is a React App Router
application, which is what AL-52 and D6′ already commit it to.

This is the case CLAUDE.md's first Universal Rule names:

> *"If code contradicts a spec, the spec wins — file a micro-change-set if the spec needs updating."*

The spec wins, so S19 did **not** raise a threshold to make a build pass — S19's own fences forbid
exactly that. What it did instead is in *What S19 did in the meantime* below.

### Where the number probably came from

A34 pairs "JS ≤ 90 KB gzipped" with "CSS ≤ 25 KB" and "LCP < 2.0s on a 3G-throttled mid-range
Android", and the other two are sound: CSS measures **9.8 kB** against 25, and the LCP target is a
statement about the reader's experience rather than about a bundler. 90 kB is a good figure for a
*hand-written* page and a familiar one from performance guidance written before the App Router. It
reads as a number carried over from that context rather than one measured against this stack.

**The intent behind it is right and should survive this change set.** A marketing page on a
3G-throttled phone in Sri Lanka is exactly the thing a byte budget is for. What needs restating is
the boundary the number is drawn around.

---

## The decision requested

**Restate A34's JavaScript budget as two figures instead of one.**

| | proposed | rationale |
|---|---|---|
| **first-party JS on `/`** | **≤ 90 KB gzipped** | A34's number, unchanged, applied to the bytes the surface controls. Nothing is loosened: this is the half a session can actually regress |
| **framework floor** | **reported, with a recorded value** | Changes only when a dependency changes. A Next or React upgrade that adds 20 kB should surface as a changed floor, reviewed once, not as a mysteriously smaller allowance |
| **total on `/`** | **≤ 300 KB gzipped** | A ceiling on what a reader actually downloads, so the split cannot be used to hide growth. 277.9 kB today |

Three properties this shape has that a single number does not:

- **It cannot be gamed by moving code into a vendor chunk.** The classifier keys off this surface's
  own string literals, which minification preserves and vendor code cannot contain.
- **It fails on our regressions**, which is the whole point of a budget.
- **It tells the truth about the framework**, so nobody spends a session hunting 70 kB that belongs
  to `react-dom`.

**A34's LCP target and CSS budget are unchanged and are met.** So is the hero-image budget: the
largest hero plate is 36 kB AVIF at 2× against a 120 kB limit.

---

## What S19 did in the meantime

`portals/www/scripts/check-bundle.mjs` enforces the split above **today**, with the 90 KB applied to
first-party bytes and the floor reported beside it. Until this change set is accepted that is the
repo's honest reading of A34 rather than a replacement for it, and it is currently **red**:

```
/si: 114.6 kB of first-party JavaScript (gzipped) exceeds A34's 90.0 kB budget by 24.6 kB.
```

**The build fails, deliberately, and the remaining 24.6 kB is a real finding rather than a rounding
error.** 91.9 kB of the 114.6 is the si and en resource tables, which reach the browser because
eleven client components take a `locale` and construct a translator — so every published locale's
entire table, including the whole guide corpus, is downloaded on every page, including pages with no
guide on them.

S19 removed what could be removed without a decision: the **unpublished** locale's table is no longer
in the client graph at all, which took 42 kB gz off every page and gave the surface an invariant it
did not have —

> A locale's table reaches a browser **if and only if** the locale is published.

— held by `portals/www/test/fences.test.ts`.

**Two things that look like the remaining fix and are not:**

- **Shipping only the active locale.** The locale is a runtime value and the translator is
  synchronous, so every *published* table must be statically present. Dropping the unpublished ones
  is the whole of what this idea can buy, and S19 has already banked it.
- **Serving the table through the RSC payload.** It moves ~55 kB gz per page out of a *cached* JS
  chunk and into *uncached* HTML, on every navigation. It is worse, not better.

What does remove it is moving a component to the server, or handing an island its resolved strings
instead of a locale. The second contradicts a stated rule in `portals/www/CLAUDE.md` —

> *"A client component takes a `locale`, not label props. React cannot serialise a translator across
> the boundary, and thirty strings is not a prop list."*

— which is a reasonable rule that was written before anyone had measured what it costs. **Revisiting
it is a decision, not a refactor**, and it is the second thing this change set asks for.

---

## What S20 added: the cost in time, not bytes

S19 could argue this in kilobytes, which is an argument about a number. **S20 measured
it in Lighthouse, which is an argument about a reader**, and the finding is materially
worse than the byte total suggested.

Six pages — `/`, `/drivers` and a guide chapter in both rendered locales — under
Lighthouse's default mobile emulation (4x CPU throttle, slow-4G link), against
S20's thresholds of ≥95 Performance / 100 Accessibility / ≥95 SEO:

| page | perf | a11y | seo | LCP | TBT |
|---|---|---|---|---|---|
| `/en` | **71** | 100 | 100 | 3.9 s | 640 ms |
| `/en/drivers` | **88** | 100 | 100 | 2.4 s | 410 ms |
| `/en/guide/driver/approval` | **88** | 100 | 100 | 2.9 s | 310 ms |
| `/si` | **59** | 100 | 100 | 3.5 s | **2,280 ms** |
| `/si/drivers` | **63** | 100 | 100 | 4.4 s | 820 ms |
| `/si/guide/driver/approval` | **66** | 100 | 100 | 3.4 s | 1,170 ms |

**Accessibility and SEO are met on every page. Performance is not met on any page**,
and the dominant term is Total Blocking Time — the main thread executing and hydrating
the bundle. A34's own LCP target (< 2.0s on a 3G-throttled mid-range Android) is missed
on all six as well, by between 0.4 s and 2.4 s.

Three things follow that the byte figure did not show:

- **Sinhala is consistently the worse experience**, by 9 to 22 Performance points. It
  carries the larger resource table (318 kB of source against English's 170 kB) and an
  additional script face. The surface's *default* locale is its slowest one.
- **The blocking time is not proportional to page weight.** `/si` and `/si/drivers`
  differ by 1,460 ms of TBT while serving nearly identical JavaScript, because what is
  being paid for is hydration of whatever islands the page happens to mount — which is
  the shape of a client-boundary problem, not a payload problem.
- **The remedy is the same one.** Every failing page fails on the tables, and no amount
  of image or CSS work touches it: CSS is 9.8 kB of a 25 kB budget and the largest hero
  plate is 36 kB of 120 kB. Both of those budgets pass comfortably.

`portals/www/scripts/check-lighthouse.mjs` and `.github/workflows/lighthouse.yml` hold
these thresholds and are **red on Performance by design**. S20's fence is the same as
S19's — *fix the page, do not lower the number* — so the number stayed at 95.

**One caveat, stated because a threshold nobody trusts is worse than none.** Lighthouse
Performance is noisy on shared hardware: `/si` measured TBT of 1,270 ms and 2,280 ms on
two runs of the same build. Accessibility and SEO are deterministic audits and can be
read as pass/fail today; Performance should be re-checked for flap once it is back
inside the threshold, before anyone relies on it as a merge gate.

---

## Deltas requested

**D1 — `specs/` (wherever A34 is stated).** Replace the single JS figure with the three-row table
above. Keep the CSS budget, the LCP target and the hero-image budget as they stand.

**D2 — record the framework floor as a measured value with its date and dependency set**, so the
next reader knows whether 163.3 kB is still current or is a number from a Next version ago.

**D3 — decide the client-boundary rule in `portals/www/CLAUDE.md`.** Either it stands and the
surface accepts ~91 kB of resource tables in every page load, or it is amended and a later session
converts the eleven islands. S19 took no position beyond having measured the cost in bytes; **S20
has now measured it in Lighthouse and the case is stronger** — this is not only 24 kB over a byte
budget, it is a Performance score of 59 on the site's default locale and an LCP target missed on
every audited page. Both answers remain defensible; neither is a session's call to make quietly.

---

## What a later session should know

- **The measurement is reproducible and is in the repo.** `portals/www/scripts/check-bundle.mjs`
  prints the split on every build; it reads the prerendered HTML rather than walking `.next/static/`,
  because the question is what one page makes a browser download, not what the build emitted.
- **Do not "fix" the red build by raising the number.** S19's brief and this change set agree:
  the fix is fewer bytes or an accepted spec delta, in that order.
- **The `noModule` exclusion is deliberate.** Counting the legacy polyfill chunk would add 38.5 kB
  to every figure here for bytes no reader receives, and would make the surface look a third worse
  than it is.
