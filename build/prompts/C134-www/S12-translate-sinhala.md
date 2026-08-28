# C134 · S12 — the Sinhala corpus

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 12 of 22 · Phase 4 (Content), part 6 of 7.**

**Prerequisite:** S07–S11 complete. Every English string exists; `si.ts` and `ta.ts` are full of
`TODO(si)` / `TODO(ta)` placeholders.

**Sinhala is the platform default** (`DEFAULT_LOCALE = 'si'` in `@mageride/i18n`, D1′ §283). On this
site it is not a translation of the English page — it is the page most visitors will land on. Treat
it that way.

---

## Scale, stated honestly

The plan's estimate: **34–40 chapters × ~450 words + ~6,000 words of marketing copy ≈ 21,000 English
words**, so ~63,000 words across three languages. Take the real number from S11's handoff.

**What can be guaranteed here:** structural parity. Every key present, every placeholder present,
every chapter the same shape — because `WwwMessages` makes a missing key a compile error and
`check-i18n-parity.mjs` catches the rest.

**What cannot be guaranteed here:** that the Sinhala reads naturally to a native speaker. Transport,
payment and legal terminology in particular. **Native review is a separate line item and this
session's handoff must say so explicitly**, with the word count, so it can be budgeted rather than
assumed.

---

## Do this

### 1 · Replace every `TODO(si)` in `src/i18n/messages/si.ts`

Work in key-family order so a partial session leaves a coherent boundary: nav and footer → hero and
home → the four role pages → FAQ → passenger chapters 1–16 → driver chapters 1–18 → captions →
error and 404 copy.

### 2 · Terminology — decide once, in a glossary, before translating prose

Create `src/content/glossary.si.ts` (a translator aid, not a rendered artefact) fixing the Sinhala
term for each of these, and use it consistently everywhere:

Mode A / Mode B / Mode C · standby · offer · trip · journey · ride · package · delivery code ·
pickup OTP · wallet · top-up · daily platform fee · tier · commission · fare · surcharge · LankaQR ·
OnePay · onboarding · verification · Verification Officer · revenue licence · route permit ·
insurance certificate · CR book · tracker · fleet · saved place · rating · SOS · emergency contact ·
PDPA · data export · erasure.

Where the app itself already ships a Sinhala string for a concept, **use the app's word.** The apps'
resource files are the platform's existing Sinhala vocabulary — a marketing site that invents a
different word for "standby" than the driver app uses teaches the wrong term. Check
`apps/driver-android` and `apps/passenger-android` string resources and
`portals/*/src/i18n/messages/si.ts` first.

### 3 · Things that are not translated

- **Slugs.** URLs stay Latin and stable (decision recorded in S07).
- **Brand names**: MageRide, LankaQR, OnePay, Google Maps, OpenStreetMap.
- **Numbers, currency and dates** — formatted by `Intl`, not written into strings.
- **Placeholders.** `{count}`, `{driver}`, `{fee}` must appear in the Sinhala string **exactly** as
  in the English one. A dropped placeholder is a visible bug; a renamed one is a silent one.
  `check-i18n-parity.mjs` must compare placeholder sets per key, not just key presence — if it does
  not yet, **add that check this session**.

### 4 · Typography check — this is why S04 added the fonts

Sinhala words are longer than their English equivalents and the hero sets 48–72px. After the
translation lands, look at the actual pages at **375px** in Sinhala:

- Does the hero headline wrap to four lines? Rewrite the Sinhala headline shorter — do not shrink
  the type, and do not let it overflow.
- Do the nav labels fit? Do the CTA buttons?
- Does Noto Sans Sinhala actually load, or is a system face being used? (Devtools → Network → Fonts.)

Line-height needs checking too: Sinhala's ascenders and descenders are taller than Latin's, and D2's
type tokens were set for Latin. If a fix is needed it is a `@layer utilities` line-height for the
Sinhala `<html lang>` — **not** a token change.

---

## Fences

- **No placeholder dropped, renamed or reordered.**
- **No key added that does not exist in `en.ts`.** English defines the key set.
- **No slug translated.**
- **No token change** to fix Sinhala typography — compose in `@layer utilities`.
- **Do not claim the Sinhala is reviewed.** It is a first pass until a native speaker says otherwise.

---

## Verify

```
npm --prefix portals run lint                        # tsc proves si.ts is a complete WwwMessages
node portals/www/scripts/check-i18n-parity.mjs       # 0 TODO(si) remaining; placeholder sets match
npm --prefix portals run build --workspace @mageride/www
npm --prefix portals run test --workspace @mageride/www
grep -c "TODO(si)" portals/www/src/i18n/messages/si.ts   # 0
```

Then by eye, at 375px, in Sinhala: `/si`, `/si/drivers`, and one guide chapter.

---

## Handoff

- **Component:** C134 www-informational-site — S12 (Sinhala corpus) — <date>
- **Status:** DONE — **first pass, not native-reviewed**
- **Notes:** word count translated; the glossary decisions and which app strings they were taken
  from; every layout fix Sinhala needed; **an explicit statement that native review is outstanding**
  and roughly how much text it covers.
