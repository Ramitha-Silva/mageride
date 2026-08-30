# C134 · S01 — MCS-34: make `www.mageride.lk` a declared surface

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 1 of 22 · Phase 0 (Governance) · produces no application code.**

This session exists because every automated drift check in this repository is derived from a
declaration file. `generate_manifests.py --check`, `infra/k8s/tools/check_fences.py` and the k8s
verify script all assert that the deployed topology matches the declared one. Adding a fifth surface
without moving the declarations does not break a build — it makes the green ones false.

---

## Before you start

Read, in this order:

1. `CLAUDE.md` — Universal Rules. The first one is why this session is first.
2. `docs/www-site-plan.md` §0, §2, §11 — the finding, the change set and the ten open decisions.
3. `build/prompts/MCS-06-plus-starts-a-new-vehicle.md` — **the template**. Match its identity block,
   its voice and its section order exactly.
4. `build/prompts/C134-www/README.md` §4 and §5.

---

## What is already true (verified 2026-08-28 — do not re-derive)

- `www.mageride.lk` appears **nowhere** in the repo. 141 references to `mageride.lk`, zero to `www.`.
- **MCS-34 is free.** MCS-33 is the highest used; today `MCS-34` appears only in
  `docs/www-site-plan.md`.
- The "four surfaces" sentence appears in **three** places in
  `specs/user-requirements-document.md` — line 69 (the Note under the header), line 608 (**US-11.1**)
  and line 1227 (the glossary entry for *MageRide*). All three must move together or the document
  contradicts itself.
- `specs/D7_mageride_devops.md` §2.1's container table is at line 44; the `admin-portal` and
  `fleet-portal` rows at lines 60–61 are the shape to copy. §5 begins at line 218 and its Ingress
  example carries `api.mageride.lk` only — the portal hosts live in `infra/k8s/base/ingress/`, so
  the D7 §5 delta is a host-list sentence, not a manifest rewrite.
- `specs/D2_mageride_ui_spec.md` names Outfit + Inter at **line 78**; the AL-52 stack ruling is the
  addendum at **line 1484**.
- `portals/web-passenger/app/layout.tsx` states, in its own words, that neither face carries Sinhala
  or Tamil glyphs and that those subsets fall through to the system face. Quote it in the finding —
  it is the repo's own evidence for the D2 delta.

---

## Do this

### 1 · Answer the ten open decisions first — they are gates, not preferences

`docs/www-site-plan.md` §11 lists D1–D10. **Ask the user for D1–D5 and record every answer in
MCS-34's own "Decisions taken" section.** Do not invent a mission statement, a store URL, a phone
number or a privacy policy.

| # | Decision | If the user has not answered |
|---|---|---|
| D1 | Mission statement | **Draft three options** (access-led / driver-livelihood-led / national-infrastructure-led), one paragraph of rationale each. **Publish none.** S07 is blocked on the pick. |
| D2 | Tamil at launch? | Record the recommendation (ship si + en complete, Tamil next release) and mark S13 conditional. |
| D3 | Store URLs | `/download` becomes an email-notify page **with no form**. |
| D4 | Contact details | `/contact` ships email-only; the footer carries no phone. |
| D5 | Terms / Privacy text | Structure and translate only. **Do not author legal text.** |
| D6 | DOKS container vs Pages | Recommend the container. S21/S22 assume it. |
| D7 | Fleet-owner guide | Recommend yes, second delivery phase → S23. |
| D8 | 4th breakpoint | **No.** Cap at `max-w-[1200px]`; three D2 widths stay. |
| D9 | `SCR-WW-###` IDs | **No.** Coverage stays 202/202. |
| D10 | Real screenshots later | Wireframe-derived now; post-launch upgrade. |

### 2 · Write `build/prompts/MCS-34-www-informational-site.md`

Following MCS-06's shape: **Identity** (hand-written; not a manifest regeneration target — but note
the one difference from MCS-06: this change set **does** add a component, so S02 *does* re-run the
generator, under the procedure in that session), **The finding**, **The decision**, **What changed**,
**Decisions taken (D1–D10)**, **What a later session should know**.

The change set must record, precisely:

- URD §2.2's *four surfaces* becomes **five**, with `www.mageride.lk` described as
  **public, unauthenticated, no personal data, no API dependency at request time**. Name all three
  URD locations.
- ADD §6 / D7 §2.1's container table gains **one optional Next.js container**, `www-site`,
  512 MB / 0.25 vCPU, port 3004.
- D7 §5's Ingress host list gains `www.mageride.lk` **and** the `mageride.lk` apex 301.
- D2 §0.2 gains **two script-scoped display faces** — Noto Sans Sinhala, Noto Sans Tamil — **scoped
  to the web**. Compose and SwiftUI already resolve Sinhala and Tamil from the platform type stack;
  this is a web-only gap, and the evidence is `web-passenger`'s own layout comment.
- **No `SCR-*` ID is claimed by this surface** (D9), so `build/screen_coverage.md`'s 202/202 equality
  is untouched and that file is not edited. State the reasoning: a marketing site is a design
  artefact, not a spec'd screen set, and 14 new IDs would be permanent maintenance for no gain.
- **AL-52 is not widened.** Motion is CSS keyframes + the Web Animations API. Say why: Framer Motion
  would pass `check-al52.mjs` (it is on neither the package nor the prefix list) and would still be
  a fence violation, so the fence is stated in the manifest and greppable in `test/fences.test.ts`.

### 3 · Apply the spec edits named in MCS-34

Small, surgical diffs. Each carries a `Δ 2026-08-28 (MCS-34)` marker in the house style already used
throughout those files.

- `specs/user-requirements-document.md` — lines 69, 608 (US-11.1) and 1227. Five surfaces; the new
  one described in the words above. **Do not touch** the `passenger.mageride.lk` clause — that
  subview is still not a separate surface, and the two facts are independent.
- `specs/D7_mageride_devops.md` — §2.1 table row after `fleet-portal`; §5 host-list sentence; and
  the headroom note under the table (`~18.9 GB` → recompute with 512 MB more, and say the marketing
  site is the first thing to leave the box if it is tight).
- `specs/D2_mageride_ui_spec.md` — the font line at 78 and the AL-52 addendum at 1484, both gaining
  the two script-scoped faces with the "web only" scope stated.

### 4 · Do **not** touch, and say so in the change set

`build/screen_coverage.md` · `.github/workflows/ci.yml` · `portals/tailwind-preset/src/tokens.ts`.
The last one matters: every marketing-scale value in this project is **composed in
`@layer utilities` from existing tokens**, never added as a token. If a later session finds it needs
a real new token, that is a second change set, not a quiet edit.

---

## Fences

- **Nothing is published without a decision.** A drafted mission is a draft until the user picks one.
- **No legal text is authored.** D5 is supplied by counsel; this session structures and nothing more.
- **No code.** No `portals/www/`, no manifest edit — that is S02.

---

## Verify

```
grep -rn "four surfaces" specs/user-requirements-document.md   # 0 hits
grep -rn "www.mageride.lk" specs/ | wc -l                      # >= 4
grep -c "SCR-WW" build/screen_coverage.md                      # 0
python3 build/tools/generate_build_plan.py --help 2>/dev/null || true   # NOT run this session
git diff --stat                                                 # 3 spec files + 1 new prompt only
```

`build/manifest.yaml`, `build/progress.md` and `build/screen_coverage.md` must be **unchanged** in
`git status` at the end of this session.

---

## Handoff

Append to `build/progress.md` under *Session Handoffs*:

- **Component:** C134 www-informational-site — S01 (MCS-34 governance) — <date>
- **Status:** DONE | BLOCKED (name the unanswered decision)
- **Notes:** the D1–D10 answers as given, the three URD locations edited, and anything in
  `docs/www-site-plan.md` that turned out to be wrong.
