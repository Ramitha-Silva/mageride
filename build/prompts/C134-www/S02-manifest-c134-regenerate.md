# C134 · S02 — add C134 to the manifest and regenerate, without losing the handoff log

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 2 of 22 · Phase 0 (Governance).**

This is the single most destructive session in the plan and the only one whose main risk is data
loss rather than a bug. `build/tools/generate_build_plan.py` rewrites `build/progress.md` wholesale.
That file is **21,869 lines**, of which the *Session Handoffs* log from line 251 to the end is the
accumulated history of every component ever built — and the generator's own docstring says
re-running "resets them".

**Prerequisite:** S01 is complete. MCS-34 exists and the three spec files carry their deltas.

---

## Before you start

- `build/tools/generate_build_plan.py` — read the docstring **and** the `progress.md` writer.
- `build/manifest.yaml` — the `C117` entry (`web-passenger-subview`) is the shape to copy;
  `C133` is the last entry in file order.
- `build/progress.md` — lines 1–30 (header, wave gates, the Components table format) and line 251
  onward (the handoff log this session must preserve byte-for-byte).

---

## What is already true (verified 2026-08-28)

- **C133 is the highest ID.** C134 is free.
- The wave-4c gate is `build/progress.md` **line 21**:
  `| 4c | C103–C117 (15) | all three web surfaces lint + test + build; zero runtime CSS-in-JS in any bundle |`
  It is generated from the manifest, so it will move on its own once C134 is a wave-4c component —
  the diff will show **C103–C134 (16)** and the count change. That is expected; the wording
  "all three web surfaces" comes from the manifest's gate text and needs updating there, not here.
- The generator **re-enumerates the screen universe from `specs/wireframes/*.html` and exits
  non-zero if any wireframe screen ID is unmapped**. C134 declares `screens: []`, so it maps nothing
  and unmaps nothing: `build/screen_coverage.md` must come out **byte-identical**. If it does not,
  stop — something else changed.
- The header line `Total components: **133**` becomes **134**, and the estimated-sessions figure
  moves by C134's `est_sessions`. Both are generated.

---

## Do this

### 1 · Commit the current tree first

The S01 spec edits and MCS-34 must be committed **before** the generator runs, so that
`git diff build/progress.md` in step 5 shows this session's damage and nothing else.

### 2 · Back up the hand-maintained state — not optional

```
cp build/progress.md /tmp/claude-0/-root-mageride/progress.before.md
wc -l build/progress.md build/screen_coverage.md
sha256sum build/screen_coverage.md
```

Record the three numbers in your working notes. You will compare against them.

### 3 · Add C134 to `build/manifest.yaml`

Place it in file order after `C133`. Wave `4c`. Modelled on C117:

```yaml
- id: C134
  name: www-informational-site
  wave: 4c
  depends_on: [C103]
  stack: portals/www/CLAUDE.md
  screens: []
  spec_anchors:
    - specs/user-requirements-document.md#1-product-vision
    - specs/MageRide_Functional_Walkthrough.md#1-platform-overview
    - specs/D2_mageride_ui_spec.md#0-2-mageride-design-tokens
    - specs/D1_mageride_user_flows.md
  scope: |
    The public informational site at www.mageride.lk — vision & mission, the three transport
    modes, a screen showcase derived from the approved wireframes, and complete how-to-use
    guides for passengers and drivers. The fifth surface (MCS-34). ADD: [AL-52].
  fences:
    - "Tailwind CSS is the SOLE styling system (AL-52). No animation library that injects styles at runtime — motion is CSS keyframes + the Web Animations API. Framer Motion would pass check-al52.mjs and is still excluded."
    - "No API call at request time. This surface must render with the whole backend down — no fetch, no NEXT_PUBLIC_*, no live map, no vehicle count."
    - "No cookies, no analytics that sets one, no personal data collected on any page. There is no contact form."
    - "Every user-facing string is an si/ta/en resource. No literal in JSX."
    - "Claims no SCR-* ID (MCS-34, decision D9). screen_coverage.md stays 202/202."
  deliverables:
    - portals/www — a Next.js 16 workspace member consuming @mageride/tailwind-preset, @mageride/ui and @mageride/i18n
    - a sliding hero and a CSS/WAAPI motion layer with prefers-reduced-motion as a first-class path
    - ~70 wireframe-derived screen images composited into device frames, committed and size-gated
    - vision, mission and values as public copy, every factual claim carrying a spec anchor
    - a 16-chapter passenger guide and an 18-chapter driver guide, si/ta/en
    - full SEO — hreflang, JSON-LD (Organization, WebSite, SoftwareApplication ×2, FAQPage, HowTo per chapter), sitemap
    - deployment as the fourth portal container — catalog entry, ingress host, apex 301, replica service
  definition_of_done:
    - every user-facing string is an si/ta/en resource and check-i18n-parity.mjs is green
    - the hero meets the APG carousel pattern and is fully keyboard-operable
    - prefers-reduced-motion disables autoplay and every transform-based effect
    - 34+ guide chapters cover every passenger and driver capability across URD Epics 1-27
    - test/fences.test.ts proves no network call at request time — the site renders with the backend down
    - Lighthouse >=95 performance / 100 accessibility / >=95 SEO on /, /drivers and one guide chapter, in all three locales
    - generate_manifests.py --check is clean and the ingress serves www.mageride.lk with the apex 301
  verify_cmd: "npm --prefix portals run lint && npm --prefix portals run build --workspace @mageride/www && npm --prefix portals run test --workspace @mageride/www"
  est_sessions: 22
```

Also update the **wave-4c gate text in the manifest** — find where the gate string
"all three web surfaces lint + test + build" is declared and make it **four**.

### 4 · Commit the manifest edit **alone**

One commit, one file. This is what makes step 5's diff readable.

### 5 · Regenerate, then restore

```
python3 build/tools/generate_build_plan.py
```

Then, from `/tmp/claude-0/-root-mageride/progress.before.md`:

1. Re-apply the **Status column** for every existing component row.
2. Re-apply the **entire *Session Handoffs* section** — from the `## Session Handoffs` heading to
   end of file — byte-for-byte.
3. `git diff build/progress.md` must show **only**:
   - the new `| C134 | www-informational-site | 4c | PENDING | | |` row,
   - the wave-4c gate line's component range, count and wording,
   - `Total components: 133 → 134` and the estimated-sessions figure.

   **Nothing else.** If any other line moves, revert `build/progress.md` from the backup and work
   out why before continuing.
4. `sha256sum build/screen_coverage.md` must equal the value recorded in step 2.

### 6 · Confirm the generated prompt landed

`build/prompts/C134.md` now exists. It is the thin generated summary; the work lives in
`build/prompts/C134-www/`. **Do not hand-edit `C134.md`** — change the manifest and re-run, under
this same procedure.

---

## Fences

- **Never run the generator without the backup in place.**
- **Never re-run it "just to check"** — every run resets the Status column and the handoff log again.
- `build/screen_coverage.md` must not change. If it does, C134 has accidentally claimed a screen.

---

## Verify

```
python3 -c "import yaml;m=yaml.safe_load(open('build/manifest.yaml'));print(len(m['components']))"   # 134
test -f build/prompts/C134.md && echo generated
git diff --stat build/progress.md          # small
git diff --stat build/screen_coverage.md   # EMPTY
diff <(sed -n '/^## Session Handoffs/,$p' /tmp/claude-0/-root-mageride/progress.before.md) \
     <(sed -n '/^## Session Handoffs/,$p' build/progress.md) && echo "handoff log intact"
```

---

## Handoff

- **Component:** C134 www-informational-site — S02 (manifest + regeneration) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** the exact `git diff --stat` for `progress.md`, confirmation that the handoff log
  diffed clean, and the `screen_coverage.md` checksum before and after.
