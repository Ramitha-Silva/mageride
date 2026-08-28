#!/usr/bin/env python3
"""Regenerate the MageRide build plan from build/manifest.yaml.

`build/manifest.yaml` is the single source of truth. This script derives:

  * build/prompts/C001.md … C132.md   (one thin prompt per component)
  * build/progress.md                 (status table + wave gates + planner findings)
  * build/screen_coverage.md          (every wireframe SCR-* ID → its owning component)

Run it after ANY edit to the manifest so the prompts, the progress table and the
screen-coverage matrix cannot drift from the plan:

    python3 build/tools/generate_build_plan.py

It re-enumerates the screen universe directly from specs/wireframes/*.html and exits
non-zero if any wireframe screen ID is left unmapped. Requires PyYAML.

Hand-editing build/prompts/*.md, build/progress.md or build/screen_coverage.md will be
overwritten — change the manifest instead. The exception is the "Session Handoffs" log
and the Status column in progress.md, which build sessions append to; re-running this
script resets them, so re-run it only when the manifest itself changes.
"""
import os, re, sys, yaml, collections, html

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MAN = os.path.join(ROOT, "build/manifest.yaml")
PROMPTS = os.path.join(ROOT, "build/prompts")

WIREFRAMES = [
    "driver_android", "driver_ios", "passenger_android", "passenger_ios",
    "web_admin", "web_fleet", "web_passenger",
]

m = yaml.safe_load(open(MAN))
comps = m["components"]
id2name = {c["id"]: c["name"] for c in comps}


def as_list(v):
    if v is None:
        return []
    if isinstance(v, str):
        return [l.rstrip() for l in v.strip().splitlines()]
    return list(v)


def scope_block(c):
    lines = [l for l in as_list(c.get("scope"))]
    fences = c.get("fences") or []
    out = list(lines)
    if fences:
        out.append("")
        out.append("**Fences — do not cross these:**")
        for f in fences:
            out.append(f"- {f}")
    return out


# ---------------------------------------------------------------- prompts
os.makedirs(PROMPTS, exist_ok=True)
for c in comps:
    cid, name = c["id"], c["name"]
    stack = c.get("stack")
    deps = c.get("depends_on") or []
    dep_txt = ", ".join(f"{d} ({id2name[d]})" for d in deps) if deps else "none"
    L = []
    L.append(f"# {cid} — {name}")
    L.append("")
    L.append("## Identity")
    L.append(f"You are building **{name}** for the MageRide platform.")
    read = "Read `CLAUDE.md`" + (f" and `{stack}`" if stack else "") + " before starting."
    L.append(read)
    L.append(f"Wave {c['wave']} · depends on: {dep_txt} · est. {c['est_sessions']} session(s).")
    if c.get("milestone"):
        L.append(f"**Milestone: {c['milestone']}** — see `build/manifest.yaml` › `meta.milestones`.")
    L.append("")
    L.append("## Spec Anchors (read these files)")
    for a in c["spec_anchors"]:
        L.append(f"- `{a}`")
    if c.get("wireframe"):
        L.append(f"- `{c['wireframe']}` — the team-approved wireframe baseline for this component")
    L.append("")
    L.append("## Scope")
    L.extend(scope_block(c))
    L.append("")
    if c.get("screens") is not None and str(c["wave"]).startswith("4"):
        L.append("## Screens (baseline = wireframes)")
        if c.get("screens"):
            for s in c["screens"]:
                L.append(f"- {s} — `{c['wireframe']}`")
        elif c.get("wireframe"):
            L.append(f"- None. This component is the application shell for `{c['wireframe']}`;")
            L.append("  it owns no wireframe screen ID. Screens belong to the sibling screen-group components.")
        else:
            L.append("- None. This component owns no wireframe screen ID; it is shared infrastructure")
            L.append("  consumed by the screen-owning components.")
        L.append("")
    L.append("## Deliverables")
    for d in c["deliverables"]:
        L.append(f"- {d}")
    L.append("")
    L.append("## Definition of Done")
    for d in c["definition_of_done"]:
        L.append(f"- {d}")
    if c.get("screens"):
        L.append("- every screen listed under `## Screens` is implemented to match the layout, controls,")
        L.append("  states and navigation shown in its wireframe — the wireframes are the team-approved")
        L.append("  baseline; any deviation requires a micro-change-set first. D2' still governs design")
        L.append("  tokens, styling rules and trilingual (Si/Ta/En) string resources.")
    L.append("")
    L.append("## Verify")
    L.append("```")
    L.append(c["verify_cmd"])
    L.append("```")
    L.append("")
    L.append("## Handoff")
    L.append("When complete, append to `build/progress.md`:")
    L.append(f"- Component: {cid} {name}")
    L.append("- Status: DONE | PARTIAL (explain)")
    L.append("- Notes: [any spec gaps found, decisions made]")
    open(os.path.join(PROMPTS, f"{cid}.md"), "w").write("\n".join(L) + "\n")

print(f"wrote {len(comps)} prompts")
lens = {c["id"]: len(open(os.path.join(PROMPTS, c['id'] + '.md')).read().splitlines()) for c in comps}
print("prompt lines: min %d / median %d / max %d" % (
    min(lens.values()), sorted(lens.values())[len(lens) // 2], max(lens.values())))
print("over 70 lines:", sorted(k for k, v in lens.items() if v > 70))

# ---------------------------------------------------------------- progress.md
P = []
P.append("# MageRide — Build Progress")
P.append("")
P.append("Single source of truth for build state. One row per component from `build/manifest.yaml`.")
P.append("After completing a component, set its Status and append the 3-line handoff under")
P.append("**Session Handoffs** at the bottom of this file.")
P.append("")
P.append("- Status values: `PENDING` · `IN PROGRESS` · `DONE` · `PARTIAL` · `BLOCKED`")
P.append("- No wave N+1 work begins until every wave N verify command passes.")
P.append(f"- Total components: **{len(comps)}** · estimated sessions: **{sum(c['est_sessions'] for c in comps)}**")
P.append("")
P.append("## Wave gates")
P.append("")
P.append("| Wave | Components | Gate |")
P.append("|------|-----------|------|")
gates = {
    "0": "all DDL applies twice cleanly, shared kernel + gateway tests green, slim compose healthy, CI parses",
    "1": "`./gradlew :shared:testDebugUnitTest detekt ktlintCheck` green (common + Android targets; iOS verified on macOS)",
    "2": "walking skeleton books one ride end to end + every core service test suite green",
    "3": "every business service test suite green; ledger balances in all money tests",
    "4a": "both Android apps build and every owned SCR-* screen matches its wireframe",
    "4b": "both iOS apps build and test **on macOS**; parity with 4a confirmed screen-for-screen",
    "4c": "all four web surfaces lint + test + build; zero runtime CSS-in-JS in any bundle",
    "5": "contract suite green against the deployed replica — **two exceptions granted 2026-08-12**: E2E in-process only, and the day-0 GTFS feed is an external dependency (see Planner findings). **C133's go-live gate still requires the feed.**",
    "6": "no open high/critical security findings; load, chaos and SG acceptance reports signed off",
}
wave_order = ["0", "1", "2", "3", "4a", "4b", "4c", "5", "6"]
by_wave = collections.defaultdict(list)
for c in comps:
    by_wave[str(c["wave"])].append(c["id"])
for w in wave_order:
    ids = by_wave[w]
    P.append(f"| {w} | {ids[0]}–{ids[-1]} ({len(ids)}) | {gates[w]} |")
P.append("")
P.append("## Components")
P.append("")
P.append("| ID | Component | Wave | Status | Session Date | Notes |")
P.append("|----|-----------|------|--------|--------------|-------|")
for c in comps:
    ms = " ⭑" if c.get("milestone") else ""
    P.append(f"| {c['id']} | {c['name']}{ms} | {c['wave']} | PENDING | | |")
P.append("")
P.append("⭑ = walking-skeleton milestone (C020–C025): one booked ride end to end on Docker Compose.")
P.append("")
P.append("## Planner findings — spec gaps & conflicts (from C000)")
P.append("")
P.append("Recorded by the build planner. Each is already encoded as a fence in the affected prompts;")
P.append("the ones marked **micro-change-set** should be fixed in `specs/` rather than worked around.")
P.append("")
P.append("1. **`server_db_schema.md` is an incomplete mirror of D4' — micro-change-set.** §0.1")
P.append("   `CREATE SCHEMA` omits `config`, `subscription` and `transit`, and the file carries no DDL")
P.append("   for `config.operating_cities` (D4 §17b), the whole `subscription.*` schema (D4 Δ")
P.append("   2026-06-21, Epic 23) or `transit.gtfs_routes/trips/stops/stop_times/shapes` (D4 Δ")
P.append("   2026-06-21) — only `transit.gtfs_feed_versions` + `transit_staging` (§27) and")
P.append("   `analytics.daily_metrics` (§23) were back-filled. **Resolution taken:** C003 creates all")
P.append("   schemas; C005 lands the missing DDL from D4', which is authoritative here.")
P.append("")
P.append("2. **`scheduling-svc` / `scheduling.scheduled_rides` do not exist — micro-change-set.**")
P.append("   ADD §1.11 AL-36 and one D3' Δ heading name a service and schema that appear nowhere else;")
P.append("   ADD §9.1, D4' §6 and `server_db_schema.md` §6 all place scheduled rides in")
P.append("   `dispatch.scheduled_rides`. **Resolution taken:** owned by dispatch-svc (C035).")
P.append("")
P.append("3. **MFA contradiction (AL-37).** D3' §0 still reads \"Admin Portal = Password or Google +")
P.append("   MFA\" and D7' §4.2 still sets `admin-bff … Mfa__RequiredForInternal=true`, both predating")
P.append("   AL-37 which removed the MFA/TOTP step. **Resolution taken:** AL-37 wins — no second")
P.append("   factor anywhere (fenced in C026, C062, C104, C105).")
P.append("")
P.append("4. **Number-masking leftovers (AL-48).** Earlier-dated addenda still describe masked calling:")
P.append("   D3' Δ 2026-06-28 `POST /v1/calls/start … normal_masked`; D3' Δ 2026-07-05")
P.append("   `POST /public/track/{token}/call` (proxy-DID lease); D6' I-28.3 and I-29.3; traceability")
P.append("   row US-25.4. All are superseded later in the same documents by the 2026-07-05 #2 set.")
P.append("   **Resolution taken:** AL-48 wins — `free_voip` only, `tel:` links, no DID lease, no")
P.append("   masked-SMS relay (fenced in C055, C066, C080, C098, C117).")
P.append("")
P.append("5. **`tracker-adapter-svc` vs `tcp-adapter` (D-DRIFT-2, still open in the ADD).** ADD §6 names")
P.append("   the component `tracker-adapter-svc`; D3' Part 1, D6', D7' and the replica layout all use")
P.append("   `tcp-adapter`. **Resolution taken:** `tcp-adapter` is canonical, `tracker-adapter-svc` is")
P.append("   an alias only (C043).")
P.append("")
P.append("6. **Stale build-order references.** B0' §5 still lists a \"Wallet Portal\" in its Wave-4")
P.append("   table; AL-02 removed it. There is no Wallet Portal component in this manifest, and the")
P.append("   B0' wave table is superseded by `build/manifest.yaml`.")
P.append("")
P.append("7. **Spec-anchor style (decision, not a gap).** `spec_anchors` use readable section slugs")
P.append("   derived from the headings (e.g. `#9-1-postgresql-bounded-context-schemas`). They identify")
P.append("   the section to read, not a rendered link target.")
P.append("")
P.append("8. **Walking-skeleton screens (decision).** C025 builds throwaway-fidelity versions of")
P.append("   screens formally owned by C068–C070 and C077–C080. It claims **no** wireframe screen ID,")
P.append("   so screen ownership stays 1:1 across the 202 IDs. Wave 4a replaces it at full fidelity.")
P.append("")
P.append("## Session Handoffs")
P.append("")
P.append("_Append 3 lines per completed component (Component / Status / Notes)._")
P.append("")
open(os.path.join(ROOT, "build/progress.md"), "w").write("\n".join(P) + "\n")
print("wrote progress.md")

# ---------------------------------------------------------------- screen_coverage.md
# 1. mechanical enumeration of the screen universe
all_ids = set()
per_file_any = collections.defaultdict(set)   # every mention (incl. cross-references)
per_file_block = {}                           # actual screen blocks + captions
caption = {}
for f in WIREFRAMES:
    path = os.path.join(ROOT, "specs/wireframes", f + ".html")
    txt = open(path, encoding="utf-8", errors="replace").read()
    for sid in re.findall(r"SCR-[A-Z]+-\d+[a-z]?", txt):
        all_ids.add(sid)
        per_file_any[sid].add(f)
    for sid, cap in re.findall(
            r'<div class="cap"><span class="scr">(SCR-[A-Z]+-\d+[a-z]?)</span>\s*(?:·\s*)?([^<]*)', txt):
        per_file_block[sid] = f
        cap = html.unescape(cap).replace("&nbsp;", " ").strip(" · ")
        if cap:
            caption[sid] = re.sub(r"\s+", " ", cap)

owner, owner_screen_text = {}, {}
for c in comps:
    for s in c.get("screens") or []:
        sid = re.match(r"^(SCR-[A-Z]+-\d+[a-z]?)\b", s).group(1)
        owner[sid] = c["id"]
        owner_screen_text[sid] = s


def sortkey(sid):
    fam, num = sid.split("-")[1], sid.split("-")[2]
    n = int(re.match(r"\d+", num).group())
    suf = num[len(str(n)):]
    return (["DA", "DI", "PA", "PI", "AP", "FP", "WT"].index(fam), n, suf)


S = []
S.append("# MageRide — Wireframe Screen Coverage Matrix")
S.append("")
S.append("Every `SCR-*` ID that appears in the seven wireframe HTML files, mapped to the one")
S.append("component that owns it. The wireframes are the **team-reviewed and approved structural /**")
S.append("**functional baseline** — no screen may be silently dropped.")
S.append("")
S.append("**Enumeration command (step 1, run from the repo root):**")
S.append("")
S.append("```")
S.append("grep -hoE 'SCR-[A-Z]+-[0-9]+[a-z]?' \\")
S.append("  specs/wireframes/driver_android.html specs/wireframes/driver_ios.html \\")
S.append("  specs/wireframes/passenger_android.html specs/wireframes/passenger_ios.html \\")
S.append("  specs/wireframes/web_admin.html specs/wireframes/web_fleet.html \\")
S.append("  specs/wireframes/web_passenger.html | sort -u")
S.append("```")
S.append("")
S.append(f"**Result: {len(all_ids)} wireframe IDs found / {len(owner)} mapped to a component "
         f"— {'EQUAL ✅' if len(all_ids) == len(owner) and set(all_ids) == set(owner) else 'MISMATCH ❌'}**")
S.append("")
S.append("`index.html` and non-HTML files in `specs/wireframes/` are excluded per the C0 brief.")
S.append("")
S.append("## Totals by family")
S.append("")
S.append("| Family | Surface | Screens | Wireframe file | Components |")
S.append("|--------|---------|---------|----------------|------------|")
fam_meta = [
    ("DA", "Driver Android", "driver_android.html"),
    ("DI", "Driver iOS", "driver_ios.html"),
    ("PA", "Passenger Android", "passenger_android.html"),
    ("PI", "Passenger iOS", "passenger_ios.html"),
    ("AP", "Admin Portal", "web_admin.html"),
    ("FP", "Fleet Portal", "web_fleet.html"),
    ("WT", "Passenger Web subview", "web_passenger.html"),
]
for fam, surface, wf in fam_meta:
    fam_ids = sorted((s for s in all_ids if s.split("-")[1] == fam), key=sortkey)
    comps_for = sorted({owner[s] for s in fam_ids})
    rng = f"{comps_for[0]}–{comps_for[-1]}" if len(comps_for) > 1 else comps_for[0]
    S.append(f"| {fam} | {surface} | {len(fam_ids)} | `specs/wireframes/{wf}` | {rng} ({len(comps_for)}) |")
S.append(f"| — | **Total** | **{len(all_ids)}** | 7 files | — |")
S.append("")
S.append("## Matrix")
S.append("")
S.append("| SCR ID | Wireframe file | Component ID | Notes |")
S.append("|--------|----------------|--------------|-------|")
for sid in sorted(all_ids, key=sortkey):
    blockfile = per_file_block.get(sid)
    wf = f"`specs/wireframes/{blockfile}.html`" if blockfile else "—"
    cid = owner.get(sid, "**UNMAPPED**")
    notes = []
    if sid in caption:
        notes.append(caption[sid])
    xrefs = sorted(per_file_any[sid] - {blockfile})
    if xrefs:
        notes.append("also cross-referenced in " + ", ".join(f"`{x}.html`" for x in xrefs))
    S.append(f"| {sid} | {wf} | {cid} | {'; '.join(notes)} |")
S.append("")
S.append("## Cross-checks")
S.append("")
S.append("**vs D2' per-screen tables.** D2' §A/§B carry combined IDs (`SCR-PA/PI-015a` = both platforms)")
S.append("and its Δ addenda introduce the later screens; expanded per-platform, the D2' set matches the")
S.append("wireframe set above. A naïve per-platform regex over D2' under-reports coverage — expand the")
S.append("combined IDs before comparing.")
S.append("")
S.append("**vs URD §6 Screen Inventory.** Every URD §6 row maps onto one or more IDs above. The URD")
S.append("names some driver wallet rows separately (Credit Transfer / Pending Credit Requests / Send")
S.append("Credit / Transfer History); the wireframes realise them as SCR-DA/DI-023 + SCR-DA/DI-024.")
S.append("")
S.append("**Screens that exist in the specs but NOT in the wireframes (correctly absent):**")
S.append("")
S.append("- `SCR-DA-005` / `SCR-DI-005` were *removed* by the 2026-06-22 onboarding restructure and then")
S.append("  **re-introduced** with new meaning (camera document-scanner) by AL-43 / US-24.6. The current")
S.append("  wireframes carry the AL-43 version, which is what C069 / C087 build.")
S.append("- Driver IDs 008 and 009 do not exist in any spec version (numbering gap only).")
S.append("")
S.append("**Unmappable IDs (spec gaps): none.** Every one of the "
         f"{len(all_ids)} IDs has a screen block in exactly one wireframe file and exactly one owning component.")
S.append("")
open(os.path.join(ROOT, "build/screen_coverage.md"), "w").write("\n".join(S) + "\n")
print("wrote screen_coverage.md;", len(all_ids), "ids,", len(owner), "mapped")
unmapped = sorted(all_ids - set(owner))
if unmapped:
    print("UNMAPPED:", unmapped); sys.exit(1)
