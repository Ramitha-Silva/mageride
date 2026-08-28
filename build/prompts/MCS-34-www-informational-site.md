# MCS-34 — `www.mageride.lk` is a fifth surface, and the specs never said so

## Identity

This is a **micro-change-set**, not a manifest component, and this file is **hand-written**.

`build/tools/generate_build_plan.py` writes one `build/prompts/Cxxx.md` per entry in
`build/manifest.yaml` and deletes nothing, so this file survives a regeneration untouched — but it
is also not produced by it.

**One difference from MCS-06, and it matters.** MCS-06 added no component and therefore forbade
re-running the generator. This change set **does** add one — C134 `www-informational-site` — so the
generator **will** be re-run, but **not in this session**. S02 runs it, under the backup → regenerate
→ restore → one-row-diff procedure written into
`build/prompts/C134-www/S02-manifest-c134-regenerate.md`. That procedure exists because re-running
resets the Status column and the whole Session Handoffs log in `build/progress.md`, and that log is
roughly 21,900 lines of accumulated component history.

Raised from a planning sweep, 2026-08-27, re-verified against the repo 2026-08-28.
**Unlike MCS-06, the work is not done** — this file is written *before* the code, because the code
would otherwise be built against declarations that do not admit it exists.

---

## The finding

A sweep of every `.md`, `.yml`, `.yaml`, `.ts`, `.cs`, `.sh` and `.json` in the tree returns
**141 references to `mageride.lk` and zero to `www.mageride.lk`**. The specs are explicit, and they
are explicit in a way that makes the marketing site a contradiction rather than an omission:

> *"**Note:** The platform has **four surfaces**: the **MageRide Passenger App**, **MageRide Driver
> App**, the **Fleet Portal** (`fleet.mageride.lk`), and the **Admin Portal**
> (`admin.mageride.lk`)."*
> — `specs/user-requirements-document.md` §2.2

The declarations agree with it: `infra/k8s/service-catalog.yaml` lists three `portals:` entries,
`infra/k8s/base/ingress/ingress.yaml` routes three portal hosts plus `api.mageride.lk`, and
`infra/docker/Dockerfile.portal`'s header names three portals.

**This is what makes it a change set rather than a to-do.** Every automated drift check in this
repository is derived from a declaration file — `generate_manifests.py --check`,
`infra/k8s/tools/check_fences.py` and `k8s-verify.sh` each assert that the deployed topology matches
the declared one. Adding a fifth surface without moving the declarations first does not turn a build
red. It turns the green ones **false**, which is worse, because a green check is the only evidence
anyone reads. CLAUDE.md's first Universal Rule names the remedy: *"If code contradicts a spec, the
spec wins — file a micro-change-set if the spec needs updating."*

**Three further findings shaped the deltas below.**

**The count is stated in five places, not three.** The planning documents
(`docs/www-site-plan.md`, `build/prompts/C134-www/README.md` §5, and S01's own preamble) all name
three URD locations — line 69, US-11.1 and the glossary. `grep -n "four surfaces"` returns **five**:
those three, plus the **v2.2 entry in the version header at line 5** and **US-11.8 at line 615**.
Editing three of five would have left the document contradicting itself in exactly the way this
change set exists to prevent, and would have failed S01's own verify command.

**US-11.8 must not simply have its number incremented.** It reads *"All four surfaces share the same
backend platform and identity service."* The informational site authenticates nobody and calls no
API at request time, so *"all five surfaces share the same identity service"* would be a **new false
statement** introduced by a change set whose whole purpose is removing one. The clause is scoped
instead, and the exclusion is stated.

**Sinhala and Tamil have no glyphs in the platform's display faces**, and the repo already says so
in its own words:

> *"Neither face carries Sinhala or Tamil glyphs and neither is asked to: those subsets fall through
> to the phone's own system face, which is what a Sinhala reader already reads everything else in."*
> — `portals/web-passenger/app/layout.tsx`

That reasoning is sound **for the surface that wrote it** — a token-gated utility page opened from an
SMS, at body sizes. It does not survive a marketing hero setting 32–72 px display type in Sinhala,
where a system-font fallback beside the English cut reads as unfinished work rather than as a
deliberate choice.

## The decision

**Five surfaces, and the fifth is described by what it does not do.** `www.mageride.lk` is
**public, unauthenticated, carries no personal data, and has no API dependency at request time** —
four negatives, because the negatives are the load-bearing part. They are what let the site sit
outside the identity model, outside the PDPA surface, and outside every availability argument the
platform makes about itself: it renders with the entire backend down.

**The apex follows the `www`.** `mageride.lk` 301-redirects to `www.mageride.lk` rather than serving
a copy, so the canonical host is unambiguous for both search engines and certificate issuance.

**The two script faces are scoped to the web, and that scope is written down.** Compose resolves
Sinhala and Tamil from the Android platform type stack and SwiftUI from the iOS one; neither needs
a font added, and adding one to D2 without a scope would read as an instruction to both. This is a
web-only gap, and `web-passenger`'s own layout comment is the repo's evidence for it.

**AL-52 is not widened, and the reason is recorded because the checker cannot enforce it.**
`portals/eslint-config/banned-styling-packages.json` lists 18 packages and 6 prefixes;
`framer-motion` / `motion` is on **neither** list, so a motion library would pass
`check-al52.mjs` clean and still violate AL-52's stated intent — *CSS compiled at build time by
PostCSS, one plugin, no runtime style injection*. Motion on this surface is **CSS keyframes plus the
Web Animations API**. The fence is stated in the C134 manifest entry and made greppable by
`test/fences.test.ts`, and the banned list is **not** widened to "fix" it: that would be a
platform-wide styling change made for one marketing page.

## What changed

**Spec**

* `specs/user-requirements-document.md` — **five** locations, all carrying `Δ 2026-08-28 (MCS-34)`:
  * **line 5** (v2.2 entry in the version header) — the parenthetical `(four surfaces)` becomes
    `(not a fifth surface)`. This is deliberately **not** a history rewrite: the clause's original
    claim was that the Passenger Web Portal did *not* add a surface, and that claim is preserved
    verbatim in meaning. What is removed is a **stale total** that the same document now states
    differently four lines of section apart.
  * **line 69** (§2.2 Note), **line 608** (US-11.1) and **line 1227** (glossary) — *four surfaces* →
    **five**, the new one described in the four-negative wording above.
  * **line 615** (US-11.8) — scoped to the **four product surfaces**, with `www.mageride.lk`
    explicitly excluded from the shared-identity clause. See the finding.
  * **Untouched on purpose:** every `passenger.mageride.lk` clause. That subview is still not a
    separate surface, this change set does not make it one, and the two facts are independent.
* `specs/D7_mageride_devops.md` — three deltas:
  * §2.1 container table gains **one optional Next.js container**, `www-site`, **512 MB / 0.25 vCPU,
    port 3004**, placed after `fleet-portal` and modelled on it.
  * The headroom note under the table: **~18.9 GB → ~19.4 GB**, OS headroom **~5 GB → ~4.6 GB**, with
    the eviction order stated — the marketing site is the **first** container to leave the box, since
    it is the only one whose absence costs no platform function.
  * §5 gains a **host-list sentence** after the Ingress example. Not a manifest rewrite: that example
    carries `api.mageride.lk` only, and the portal hosts live in `infra/k8s/base/ingress/`.
* `specs/D2_mageride_ui_spec.md` — two deltas, both stating the **web-only** scope:
  * the **Typography** line (§0.2) gains **Noto Sans Sinhala** and **Noto Sans Tamil** as
    script-scoped display faces.
  * the **AL-52 addendum** gains the same two faces, so a reader who arrives at the stack ruling
    without reading §0.2 still learns the scope.

**Build plan** — `build/manifest.yaml` gains C134 in wave 4c, and the wave-4c gate moves from *three*
web surfaces to *four*. **Not in this session.** S02 does it, under the restore procedure named in
the Identity block above.

**No code.** No `portals/www/`. That is S03.

**Deliberately untouched, and each for a reason:**

* `build/screen_coverage.md` — decision D9. **No `SCR-*` ID is claimed by this surface**, so the
  202 / 202 equality is untouched and the file is not edited. A marketing site is a design artefact,
  not a spec'd screen set; 14 new `SCR-WW-###` IDs would be permanent maintenance in the coverage
  matrix for no gain, and every one of them would need a wireframe nobody will ever build against.
* `.github/workflows/ci.yml` — the portals job runs `npm --prefix portals ci && run lint && run
  build`, which already covers every workspace member. Adding one adds it to CI for free.
  **The test suite is not in that chain** — it is enforced by C134's own `verify_cmd`. S20 says so
  out loud rather than letting a green main imply the tests ran.
* `portals/tailwind-preset/src/tokens.ts` — **this one matters most.** Every marketing-scale value in
  this project is **composed in `@layer utilities` from existing tokens**, never added as a token. A
  token is a platform-wide contract that Compose and SwiftUI also read; a marketing site needing a
  bigger hero size is not a reason to move it. If a later session finds it genuinely needs a new
  token, **that is a second change set, not a quiet edit**.

## Decisions taken (D1–D10)

D1–D5 were put to the user on 2026-08-28 and answered. D6–D10 carry the planning recommendation and
were not contested.

| # | Decision | Answer | Consequence |
|---|---|---|---|
| **D1** | Mission statement | **National-infrastructure-led.** *"MageRide exists to give Sri Lanka one live picture of how the country moves — every bus, every train, every three-wheeler and every van on one map, as public infrastructure rather than a private service."* | **Unblocks S07.** The draft above is the chosen *framing*, not final copy — S07 writes the published wording from it. **It carries a coverage claim** ("every bus, every train") that is not true on launch day, so README rule 7 binds hardest here: the hero needs an honest qualifier directly beneath it, and S07 owns writing one. |
| **D2** | Tamil at launch? | **No — si + en complete first**, Tamil next release | **S13 becomes conditional** and is *formally deferred*, not dropped: the `ta` message table still exists and still type-checks, so a missing key stays a compile error. Rather than machine-translating ~21k words with no native reviewer identified anywhere in the repo. |
| **D3** | Store URLs | **Not live yet** | `/download` is an **email-notify page with no form** — a form collects personal data and needs a backend, a PDPA position and a retention policy, all separate change sets. Store links are **resource keys with placeholder values**; S18 wires them, and the go-live checklist gains a row. Nothing is invented. |
| **D4** | Contact details | **Email only; no phone in the footer** | `/contact` ships email-only. The address itself is a **placeholder key** — the user has not chosen between a domain mailbox and the account address, and no address goes on a public page that was not chosen. Go-live checklist row. |
| **D5** | Terms / Privacy | **Counsel supplies later** | S18 builds both routes with structure, headings and si/ta/en scaffolding; **the body text stays a clearly-marked placeholder until counsel supplies it**. **No legal text is authored by any session in C134.** Launch is gated on the text arriving — the app store listings will need a privacy URL regardless. |
| **D6** | DOKS container vs Pages | **Container** | Consistency with the three existing portals: same `output: 'standalone'` shape, same `Dockerfile.portal`, same ingress. S21/S22 assume it. |
| **D7** | Fleet-owner guide | **Yes, second delivery phase** | → S23, optional, run after S17. |
| **D8** | 4th breakpoint (1440 px) | **No** | Cap at `max-w-[1200px]`. D2's three widths (375 / 768 / 1024) stay. A breakpoint is a token-level change reaching every surface; a wide marketing page is not a reason to move it. |
| **D9** | `SCR-WW-###` IDs | **No** | Coverage stays **202 / 202**; `build/screen_coverage.md` is not edited. |
| **D10** | Real app screenshots | **Later** | Wireframe-derived now; upgraded post-launch. iOS needs a Mac and Android needs seeded state, neither of which gates a marketing page. |

## What a later session should know

* **`specs/MageRide_Functional_Walkthrough.md` line 42 still says "four surfaces"** and was **not**
  edited — S01's diff is fenced to three spec files. It is not strictly false there: the phrase is
  scoped as *"the four surfaces (the four 'apps' people use)"*, and a marketing site is not an app
  anyone uses. It should still be revisited the next time that document is touched, and it is the
  reason S01's verify greps the URD specifically rather than all of `specs/`.
* **`MageRide_Government_Proposal.md` does not exist in this repository.** `docs/www-site-plan.md`
  §0.4 and `S07-content-vision-mission.md` both cite it as a vision source and quote it — *"A Sri
  Lankan citizen opens one app and sees every bus, every train, every three-wheeler, and every van in
  the country moving in real time…"* — but `find` turns up no such file, and that sentence appears
  **only inside the plan and S07 themselves**. The quote survives as a phrasing to work from; its
  provenance does not. **S07 has one real source, `specs/user-requirements-document.md` §1**, and
  should be read accordingly.
* **The URD count is now stated in five places.** Any future surface change must move all five, and
  US-11.8 is the one that needs thought rather than a number bumped — it is about the identity model,
  not the surface list.
* **`web-passenger` is in neither D7 §2.1's container table nor the replica compose**, so `www-site`
  at port 3004 now sits in the table beside `admin-portal` (3001) and `fleet-portal` (3002) with
  3003 unlisted. That gap predates this change set and was not filled by it; `service-catalog.yaml`
  is the accurate port record.
* **The container is `*(opt)*`, and that is not a hedge.** It is the first thing evicted from the
  24 GB box. Any later session that makes something depend on the marketing site being up has
  misread the table.
* **D1's mission is a framing, not published copy.** Nothing is published until S07 writes it, and
  the coverage-claim qualifier is part of that work, not optional polish.
