# C134 · S09 — passenger guide, chapters 9–16 (English)

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 9 of 22 · Phase 4 (Content), part 3 of 7.**

**Prerequisite:** S08. Same shape, same sources, same rules — read S08's "Fences" section; it
applies here unchanged.

---

## Do this — eight chapters, `src/content/guide/passenger/p09…p16.ts`

| # | slug | Chapter | Primary source |
|---|---|---|---|
| 9 | `waiting-for-a-driver` | Waiting, and what the 15-second dispatch is doing | D1 §B.9 |
| 10 | `during-the-ride` | During the ride — live track, driver card, call, share, SOS | URD Epic 12 |
| 11 | `paying` | Paying — cash, driver QR / LankaQR, receipt | URD Epic 8 · AL-15 |
| 12 | `sending-a-package` | Sending a package — sizes, recipient, delivery code, three stages | URD Epic 20 |
| 13 | `booking-for-someone-else` | Booking for someone else, and the SMS web link | URD Epic 25 · AL-44 |
| 14 | `scheduling-a-ride` | Scheduling a ride | URD US-24.2 |
| 15 | `saved-places-and-ratings` | Saved places, ratings & reviews | URD US-7.13 · Epic 18 |
| 16 | `settings-help-and-your-data` | Settings, help & support, deleting my data (PDPA) | URD Epic 22 / 16 · US-1.8 |

### Chapter-specific facts you must get right

- **Ch 9** — the offer window is **15 seconds** per driver (D1 §B.9); the passenger-side matching
  timeout is **2 minutes**, after which the app offers a retry (US-6A.11). **Cancelling before a
  driver accepts is free** (US-6A.9). Both numbers are on a public page; anchor both.
- **Ch 10** — the call chooser is **Free (in-app VoIP) / Normal (direct dial)**. **Number masking
  was withdrawn** (AL-47/48, URD v2.7): "Normal call" is a direct cellular call to the real number,
  revealed only after the driver accepts. The **booker's** number is never shown to the driver when
  the rider is a proxy (P-05 is retained). Get this exactly right — it is a privacy claim.
- **Ch 11** — three ways to pay: cash (default), **driver QR / LankaQR** (no surcharge), OnePay
  (**+5%**). Driver-QR fare payments settle **by attestation** — the passenger claims "I've paid",
  the driver confirms "QR payment received", and the ride reaches `DriverConfirmedQR` (US-26.1).
  Say so plainly: there is no bank callback for a payment into the driver's own QR, and disputes go
  to Support. This is the chapter most likely to be wrong if you paraphrase.
- **Ch 12** — packages are a **three-stage bottom-sheet flow** (US-20.12/13): review → **pickup
  OTP** → **delivery OTP + "Delivery completed"**. There is a **4-digit delivery code** the
  recipient shows the driver. COD exists on some flows; the wording changed from "Cash received
  (COD)" to "Delivery completed", so do not resurrect the old label.
- **Ch 13** — the proxy rider gets a **tokenised SMS link** to `passenger.mageride.lk` (AL-44) and
  needs **no app and no account**. The web page shows the driver's number as a plain `tel:` link
  (US-26.3). **Declining the pickup-location request transmits no GPS**, and the page says so —
  repeat that promise here; it is P-02.
- **Ch 16** — PDPA: export and erasure, **30-day due date**, served by `pdpa-svc`. Describe the
  right accurately and link to `/legal/pdpa` (S18). Do not describe a retention period the spec
  does not state.

### Every chapter

Same rules as S08: `sources` anchors on every claim; 1–3 `screens` per chapter, added to
`src/content/screens.ts` if missing; `en.ts` keys under `www.guide.p09.*`…; `TODO(si)`/`TODO(ta)`
placeholders in the other two files.

**When this session ends the passenger guide is 16 chapters complete in English.** Check the
registry ordering is 1…16 with no gap and no duplicate slug.

---

## Fences

As S08, plus:

- **The masking withdrawal is not optional wording.** Any sentence implying a masked or proxy number
  for the fare-paying rider contradicts URD v2.7 and would be a false privacy claim.
- **Do not describe a payout, refund or dispute mechanism the specs do not define.**

---

## Verify

```
npm --prefix portals run lint
npm --prefix portals run test --workspace @mageride/www
node portals/www/scripts/check-i18n-parity.mjs
```

Plus, by hand: the 16 passenger chapters resolve in the registry, slugs are unique, and every
`screenRef` names an entry that exists in `src/content/screens.ts`.

---

## Handoff

- **Component:** C134 www-informational-site — S09 (passenger guide 9–16) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** the passenger guide's total word count; every content gap; anything in the plan's
  chapter table that turned out not to match the specs.
