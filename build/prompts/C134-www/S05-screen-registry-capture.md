# C134 · S05 — curate the frame list and build the wireframe capture pipeline

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 5 of 22 · Phase 3 (Screen imagery), part 1 of 2.**

**Prerequisite:** S03. (S04 is not required, but its `@layer utilities` gradients are what S06
composites onto — running S04 first is easier.)

**Honest framing, and say it in the captions:** `specs/wireframes/*.html` describes itself as
*"Mid-fidelity, self-contained HTML keyed to the D2′ §0.2 design tokens"*. Screenshotting them raw
gives mid-fidelity pictures. The route chosen (plan §A14–A17, decision D10) is to **re-render the
wireframe frames through a token-accurate polish stylesheet and composite them into device frames** —
marketing-grade, structurally faithful to the approved screens, fully reproducible. Real app
screenshots are a post-launch upgrade: iOS does not build on this Linux host (`CLAUDE.md`, Build
Host) and every shot would need seeded state.

---

## Read `build/prompts/C134-www/README.md` §4.1 and §4.2 before writing a selector

Two things in `docs/www-site-plan.md` §A15 are wrong about the wireframes, and both change the
script.

### The screens have no `id` attribute

A screen is **caption text**, not an anchor:

```html
<div class="cell">
  <div class="cap"><span class="scr">SCR-PA-014</span> · finding driver <span class="tag rep">REPLACE</span></div>
  <div class="phone"> … the frame … </div>
  <div class="states"><b>States:</b> Matching → pulse; NoDriver (2-min timeout) → … </div>
</div>
```

So: find the `.cell` whose `.cap .scr` **textContent** equals the ID, then screenshot the frame
element inside it.

| Wireframe file | Frame element | Geometry |
|---|---|---|
| `driver_android.html`, `driver_ios.html`, `passenger_android.html`, `passenger_ios.html` | `.phone` | **320 × 680**, 9px bezel — not the 375 × 812 the plan assumed |
| `web_admin.html`, `web_fleet.html` | `.browser` | measure it; do not assume 1440 × 900 |
| `web_passenger.html` | `.mweb` | measure it |

The `.states` block beside each frame is a gift: it names the screen's real states in plain language
and is a good source for a caption. It is **not** publishable copy — rewrite it.

### There is no dark mode in any wireframe

`grep -c "prefers-color-scheme\|\.dark" specs/wireframes/*.html` returns **0 for all eight files**.
"Capture both appearances" cannot be done by toggling a class.

**Every colour in those files is a CSS custom property on `:root`** (`--primary`, `--surface`,
`--onSurface`, the 11 `--veh*`, the three `--mode*`, …). So the dark capture is done by
`page.addStyleTag()` injecting a `:root { … }` override whose values come from
`portals/tailwind-preset/src/tokens.ts`'s **dark** palette — the same source of truth the site
itself resolves. Write the mapping as a data file, not inline in the script, so a token change is a
one-file edit.

If a wireframe hard-codes a hex outside `:root` (several inline `style=` attributes do —
`background:var(--success)` is fine, `#15171B` is not), the dark capture of that frame will be
wrong. **List every such frame in the handoff** rather than shipping a broken dark image; if there
are many, ship light-only for those IDs and record it as a known limitation.

---

## Do this

### 1 · Enumerate the screen universe

Use the command `build/screen_coverage.md` already documents:

```
grep -hoE 'SCR-[A-Z]+-[0-9]+[a-z]?' \
  specs/wireframes/driver_android.html specs/wireframes/driver_ios.html \
  specs/wireframes/passenger_android.html specs/wireframes/passenger_ios.html \
  specs/wireframes/web_admin.html specs/wireframes/web_fleet.html \
  specs/wireframes/web_passenger.html | sort -u
```

202 IDs. **Select ~70.**

### 2 · Curate — selection is the deliverable, not the script

Choose by what the site actually needs:

- **~12 hero screens** — the four hero slides need one striking frame each, in both a passenger and
  a driver cut: the live map, booking, the driver dashboard/offer, package tracking.
- **The screens each guide chapter references.** The passenger guide is 16 chapters (S08/S09) and
  the driver guide 18 (S10/S11); their chapter tables in `docs/www-site-plan.md` §A19/§A20 name the
  URD epics, and `build/screen_coverage.md` maps every SCR ID to its component. Pick 1–3 per chapter.
- **Prefer Android over iOS** where a screen exists in both — one frame per concept, not two. Note
  the iOS twin's ID in the registry so a later session can offer an iOS cut without re-curating.
- **Skip the Admin Portal almost entirely.** This is a public site; `SCR-AP-*` is back-office. One
  or two on `/fleets` at most.

### 3 · Write `src/content/screens.ts` — the typed registry

```ts
export interface ScreenEntry {
  id: string;                    // 'SCR-PA-014'
  wireframe: string;             // 'passenger_android'
  frame: 'phone' | 'browser' | 'mweb';
  device: 'android' | 'ios' | 'web';
  surface: 'passenger' | 'driver' | 'fleet' | 'admin' | 'web';
  captionKey: WwwMessageKey;     // trilingual — not a literal
  chapters: string[];            // guide chapter slugs that reference it
  file: string;                  // 'pa-014-finding-driver' — no extension, no @2x
  appearances: ('light' | 'dark')[];
}
```

`file` is the **deterministic** name; S06 emits `<file>.avif`, `<file>.webp`, `<file>@2x.avif`, …
Two entries may not share a `file`. `captionKey` is a resource key because a caption is user-facing
text and the ESLint rule will catch a literal — the keys themselves are written in S07–S09.

`test/content.test.ts` (S20) asserts every registry entry resolves to files that exist, and every
`chapters` entry names a real chapter.

### 4 · Write `scripts/capture-screens.mjs` (Playwright, headless Chromium)

- Playwright is a **devDependency of `portals/www` only** and never reaches the bundle. It is not a
  styling package, so `check-al52.mjs` passes cleanly — but confirm that, don't assume it.
- Headless Chromium runs on this Contabo Linux host with no Android or iOS toolchain, which is the
  whole reason this route was chosen.
- Per entry: open the wireframe from `file://`, `deviceScaleFactor: 3`, locate the `.cell` by its
  caption text, screenshot the frame element by bounding box, for **each** appearance in
  `appearances`.
- Deterministic output to `portals/www/.screens-raw/` — **gitignored**. S06 turns these into the
  committed artefacts; raw 3x PNGs are large and are an intermediate.
- Idempotent: re-running produces byte-identical output for an unchanged wireframe. No timestamps,
  no random ids, no animation mid-flight — `prefers-reduced-motion` forced and a settle delay before
  each shot.
- **Fail loudly on a missing ID.** A registry entry whose caption text matches nothing is a typo,
  and a silently skipped screen is a hole in the guide nobody notices until launch.

### 5 · The polish stylesheet

A single `scripts/polish.css`, injected before capture, that lifts the frame from mid- to
marketing-fidelity **without changing structure**: real type scale from the D2 tokens, correct
elevations, crisper radii, no wireframe page chrome (`.page-header`, `.note-bar`, `.cluster`,
`.states`, `.cap` are all cropped out anyway, but hide them so layout does not shift).

**It must not move, add or remove a control.** These wireframes are the team-approved structural and
functional baseline; a screenshot that shows a button the app does not have is a false public claim.
If polish requires a structural change, that is a wireframe change and needs a change set.

### 6 · `npm run screens:capture`

Wire the script as a package script. Do **not** put it in `build` — CI must never need a browser.

---

## Fences

- **Playwright and any capture dependency are `devDependencies` only.**
- **`.screens-raw/` is gitignored.** Only S06's composited output is committed.
- **No structural edit to any wireframe.** Read-only. `git status` must show `specs/` clean.
- **Captions are resource keys**, never literals — including in the registry.

---

## Verify

```
npm --prefix portals run lint
node portals/www/scripts/capture-screens.mjs
ls portals/www/.screens-raw | wc -l          # 2 × the number of entries with both appearances
git status --porcelain specs/                 # empty
git status --porcelain | grep screens-raw     # empty — gitignored
npm --prefix portals run test --workspace @mageride/www
```

Then open six captures by eye — one per wireframe file — in both appearances.

---

## Handoff

- **Component:** C134 www-informational-site — S05 (screen registry & capture) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** how many IDs were selected and the selection rule; every frame whose dark capture is
  wrong because of a hard-coded hex outside `:root`; any wireframe whose frame element or geometry
  differed from the table above.
