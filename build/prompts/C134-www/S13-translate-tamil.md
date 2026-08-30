# C134 · S13 — the Tamil corpus *(conditional on MCS-34 decision D2)*

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 13 of 22 · Phase 4 (Content), part 7 of 7.**

**Prerequisite:** S12 complete.

**This session is gated.** MCS-34 decision D2 asks whether Tamil ships at launch or in the next
release. The recommendation on record is: **ship English + Sinhala complete, hold Tamil back**,
rather than ship three languages where one is visibly machine-translated. Read S01's handoff for the
answer given.

---

## If D2 = "Tamil next release" — do this instead, and stop

Deferring is not "leave the TODOs". A half-translated locale that ships is worse than an absent one,
because a reader who picks Tamil gets English prose under a Tamil label and concludes the platform
does not really support them.

1. **Keep `ta.ts` structurally complete** — it must stay a valid `WwwMessages` or the build breaks.
2. **Remove `ta` from the rendered locale set for this surface only.** Add
   `WWW_LOCALES: readonly Locale[] = ['si', 'en']` in `src/i18n/index.ts`, and drive
   `generateStaticParams`, the locale switcher, `routes.ts`, the sitemap and the `hreflang` block
   from **that**, not from `@mageride/i18n`'s `LOCALES`. A Tamil URL must `notFound()`, not serve
   English under `lang="ta"` — a wrong `lang` attribute is an accessibility failure, not just a
   cosmetic one.
   **Do not change `@mageride/i18n`'s `LOCALES`.** Three other surfaces read it and Tamil is not
   deferred there.
3. Add a `test/i18n.test.ts` case asserting that `/ta` 404s **and** that `ta.ts` is still complete —
   so the day Tamil lands, flipping one constant is the whole change.
4. Record in `portals/www/CLAUDE.md`: what was deferred, the one constant that re-enables it, and
   the decision (MCS-34 D2) that deferred it.
5. Update the C134 Definition of Done note in the handoff: "three locales" is now two, by decision.

Then stop. The rest of this file is for D2 = "Tamil at launch".

---

## If D2 = "Tamil at launch" — do this

Everything in **S12 applies unchanged**, with `ta` substituted for `si`. Read that file; the
placeholder rules, the untranslated-terms list and the fences are identical.

### 1 · Replace every `TODO(ta)` in `src/i18n/messages/ta.ts`

Same key-family order as S12, so the two locales are reviewable side by side.

### 2 · `src/content/glossary.ta.ts`

Same term list as S12's glossary. Same rule: **where the apps already ship a Tamil string for a
concept, use the app's word.** Check `apps/*/…` string resources and `portals/*/src/i18n/messages/ta.ts`.

Tamil transport and payment vocabulary in Sri Lanka differs in places from Indian Tamil usage. Where
the repo gives no precedent, prefer the Sri Lankan form and **flag the term in the handoff** for
review rather than picking silently.

### 3 · Typography — Noto Sans Tamil, at hero sizes

Same check as S12 §4, at 375px in Tamil: hero wrap, nav fit, CTA fit, whether Noto Sans Tamil
actually loads or a system face is standing in. Tamil's line-height needs are closer to Latin's than
Sinhala's, but check rather than assume. Fixes go in `@layer utilities`, never in tokens.

---

## Fences

- **No placeholder dropped, renamed or reordered.**
- **No key added that does not exist in `en.ts`.**
- **No token change.**
- **Do not claim the Tamil is reviewed.** First pass until a native speaker says otherwise.
- **Do not silently ship a partial locale.** Complete, or formally deferred per the branch above.

---

## Verify

**If shipping Tamil:**

```
npm --prefix portals run lint
node portals/www/scripts/check-i18n-parity.mjs
grep -c "TODO(ta)" portals/www/src/i18n/messages/ta.ts    # 0
npm --prefix portals run build --workspace @mageride/www
npm --prefix portals run test --workspace @mageride/www
```

**If deferring:**

```
npm --prefix portals run lint
npm --prefix portals run test --workspace @mageride/www    # /ta 404s; ta.ts still complete
grep -rn "WWW_LOCALES" portals/www/src portals/www/app     # every consumer reads it, not LOCALES
grep -n "LOCALES" portals/i18n/src/index.ts                # unchanged — still si, ta, en
```

---

## Handoff

- **Component:** C134 www-informational-site — S13 (Tamil corpus) — <date>
- **Status:** DONE (shipped, first pass) | DONE (formally deferred per D2) | BLOCKED
- **Notes:** which branch was taken and on whose decision; if shipped, the word count and the terms
  flagged for review; if deferred, the one constant that re-enables Tamil and where it is documented.
