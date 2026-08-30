# `public/screens/` — generated, committed, and not hand-editable

Every image in this directory is **generated output**. Nothing here was drawn, retouched or
exported by a person, and **a hand-edited image is overwritten without warning by the next
refresh** — there is no merge, no prompt and no backup.

```
npm --prefix portals run screens:refresh --workspace @mageride/www
```

That is the only sanctioned way these files change. It runs two scripts in order:

| Step | Script | Reads | Writes |
|---|---|---|---|
| 1 · capture | `scripts/capture-screens.mjs` (`playwright-core`, headless Chromium) | `specs/wireframes/*.html` | `.screens-raw/` — 3× PNGs, **gitignored** |
| 2 · compose | `scripts/compose-frames.mjs` (`sharp`) | `.screens-raw/` | this directory |

`npm run screens:capture` and `npm run screens:compose` run the halves separately when you are
debugging one of them.

## Why they are committed

`.github/workflows/ci.yml`'s portal leg is `npm ci && lint && build`, with no browser in it. If
these images were generated during the build, **CI would have to download Chromium to render a
marketing page** — which plan §A17 exists to prevent. Committing the output is the trade: the
repository carries ~5 MB of images so that the build carries nothing.

It also keeps content diffs readable. Images that regenerated on every build would show up in every
review whether or not anybody touched them.

## Where the pictures come from — and what they are not

The source is `specs/wireframes/*.html`, the **team-approved** structural and functional baseline
for every MageRide surface, re-rendered through `scripts/polish.css` and composited here (MCS-34
D10).

**These are not screenshots of a released app**, and no caption may imply they are. Real screenshots
are a post-launch upgrade: iOS does not build on the Linux build host (`CLAUDE.md`, Build Host) and
every shot would need seeded state. The site says so once, in `www.screens.provenance`, rendered on
the showcase page.

The capture script enforces the matching fence mechanically: `polish.css` may change type, elevation
and radii, but if it moves any control by more than 1px the capture **fails**, because a picture
showing a control the app does not have is a false public claim.

## What each file is

Names come from the `file` field in `src/content/screens.ts`, which is the registry of which screens
this site publishes. Two entries may not share one, and the build fails if they do.

```
pa-010-the-live-map.avif       1× AVIF
pa-010-the-live-map.webp       1× WebP
pa-010-the-live-map@2x.avif    2× AVIF
pa-010-the-live-map@2x.webp    2× WebP
```

A `--dark` infix (`pa-010-the-live-map--dark.avif`) is reserved for dark captures. **There are none
today** — the wireframes hard-code light surface hexes in 231 stylesheet rules, so a dark rendering
of them fails WCAG contrast rather than looking dark. `scripts/wireframe-appearances.mjs` carries
the full explanation and the machinery, ready for the day the wireframes are tokenised.

### Formats: AVIF and WebP, no PNG

Measured on this set, per image:

| | 1× phone | 2× phone | 2× portal |
|---|---|---|---|
| AVIF `q50` | 6 kB | 12 kB | 31 kB |
| WebP `q80` | 9 kB | 21 kB | 57 kB |
| PNG | 50 kB | 123 kB | **266 kB** |

A PNG fallback would breach the 220 kB per-image budget on every portal frame by itself, and would
put roughly 14 MB into a 12 MB total. It also buys nothing: **WebP is the universal floor** — every
engine has shipped it since Safari 14 in 2020, and any browser new enough to honour
`prefers-color-scheme` reads it.

### Encoder settings, and why they are not maxed out

AVIF `quality: 50, effort: 3`; WebP `quality: 80, effort: 4`. Effort was measured on the largest
image in the set — a 2× portal plate — and it is nearly all cost:

| AVIF effort | size | time |
|---|---|---|
| 2 | 42 kB | 1.2 s |
| **3** (chosen) | **39 kB** | **2.4 s** |
| 4 | 37 kB | 7.5 s |
| 6 | 35 kB | 19.3 s |

Effort 6 spends 16× the time of effort 3 to save 4 kB against a 220 kB ceiling, in a directory
using about a third of its budget. A first pass at effort 6 was on course to take ~55 minutes for
276 images — and a refresh nobody wants to start is a refresh that does not happen.

**If the budget ever tightens, raise `effort` before dropping a screen.** It costs only wall-clock.

## The budget is enforced, not documented

`scripts/check-bundle.mjs` runs inside `npm run build` and fails it on any of:

- `public/screens/` total over **12 MB**;
- any single image over **220 kB**;
- a registry entry with no 1× AVIF **and** WebP.

**Do not raise those numbers to make a build pass.** The fix is fewer screens, or tighter encoder
quality, in that order.

## Colours

Every colour in the plate, the gradient and the shadow is imported from
`portals/tailwind-preset/src/tokens.ts` — the one place a D2 §0.2 value is spelled on the web —
rather than copied into the compositor. The plate mirrors `app/globals.css`'s `.mr-aurora`
(`primary-container` at 20% over `surface`), the drop shadow is D2's `elevation-5`, and the padding
is `SPACING.xxl`. **No new hex enters the system here.**

The plate is rendered in the **light** palette only. See the `next/image` contract in
`portals/www/CLAUDE.md` for what that means for a page in dark mode.
