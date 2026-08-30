/**
 * Which of the three services a screen belongs to — the middle facet of S18's
 * `/screens` filter, **derived rather than typed onto seventy registry entries**.
 *
 * ## The gap this closes
 *
 * S18 asks for a gallery "filterable by **surface × mode × chapter**", with
 * `?surface=driver&mode=c` as the worked example. `src/content/screens.ts` carries
 * `surface` and `chapters`; it has never carried a mode, because S05 curated the
 * registry against the wireframes and the wireframes are organised by app, not by
 * service. So the facet had to come from somewhere, and there were two places it
 * could come from:
 *
 *   1. **A `modes` field on each entry.** Seventy judgements, made once, checkable
 *      by nobody afterwards — and wrong the first time a screen's subject shifts.
 *   2. **A rule over data the registry already curates.** A screen's chapters were
 *      chosen by S05 and revised by S08–S11 with exactly this question in front of
 *      them: SCR-PA-009's own registry comment says *"this one screen carries both
 *      halves of the booking result — the GTFS public routes above and the Mode C
 *      tiers below"*, and it is tagged with `tracking-buses-and-trains` **and**
 *      `booking-a-ride` for that reason.
 *
 * The second is what this module does, and the choice is not stylistic. A screen
 * that changes chapters changes mode with it; a chapter added in S23 needs one row
 * here, not a sweep of the registry; and the mapping below is nineteen lines a
 * reviewer can read against URD §1-A in one sitting, which seventy scattered fields
 * are not.
 *
 * ## What is mapped, and what is deliberately not
 *
 * **Only chapters whose subject *is* one of the services.** Installing the app,
 * granting permissions, photographing a revenue licence, topping up a wallet and
 * reading your ratings are all mode-agnostic: they happen once, they happen the same
 * way whichever service the reader came for, and tagging them with a mode would make
 * `?mode=a` return the whole app.
 *
 * Three rows are worth their reasoning:
 *
 *   - **`passenger/reading-the-live-map` carries all three.** The map is where the
 *     services meet — SCR-PA-006 is literally the mode filter, drawing all three —
 *     so a reader filtering for Mode A should be shown the map their bus appears on.
 *     Tagging it with one mode would be false and tagging it with none would hide
 *     the site's best three frames behind every filter.
 *   - **`driver/the-daily-platform-fee` is Mode C and only Mode C.** URD Epic 9 and
 *     the fee table charge it on the second *on-demand* trip of a day; public buses
 *     are free and Mode B is billed monthly to the fleet owner, not daily to the
 *     driver. This is a fact about the fee, not a guess about the screen.
 *   - **`driver/getting-paid`, `driver/your-wallet` and
 *     `driver/bulk-credit-and-transfers` are unmapped**, though a fare is a Mode C
 *     thing. Those chapters are about money moving, and the wallet holds the daily
 *     fee for an on-demand driver and a Mode B subscription settlement for a fleet
 *     one. Tagging them `c` would state something narrower than the truth.
 *
 * `TRANSPORT_MODES[].screens` is unioned in on top. Those five ids are S07's own
 * choice of the frame that illustrates each mode on the home page, and a gallery
 * that disagreed with the home page about which screen shows Mode B would be the
 * kind of inconsistency nobody notices until a reader does. Today the union adds
 * nothing the chapter map has not already produced — asserted in
 * `test/screen-modes.test.ts`, so it stays a belt-and-braces and never becomes a
 * silent second source of truth.
 *
 * Spec anchor for the whole table: `specs/user-requirements-document.md#1-a-service-modes`.
 */

import type { GuideChapterRef } from './chapters';
import { TRANSPORT_MODES, type TransportModeId } from './marketing';
import { SCREENS, type ScreenEntry } from './screens';

export const MODE_IDS: readonly TransportModeId[] = ['a', 'b', 'c'];

/** The URD section every row below is read from. */
export const MODE_SOURCE = 'specs/user-requirements-document.md#1-a-service-modes';

/**
 * Chapter → the services it is about. **Partial on purpose** — an absent chapter is
 * mode-agnostic, not an oversight, and the absences are listed in the module note.
 *
 * Typed against {@link GuideChapterRef}, so a chapter renamed in the registry is a
 * compile error here rather than a row that silently stops matching.
 */
export const MODE_CHAPTERS: Partial<Record<GuideChapterRef, readonly TransportModeId[]>> = {
  // Passenger — where the three services are actually met.
  'passenger/reading-the-live-map': ['a', 'b', 'c'],
  'passenger/tracking-buses-and-trains': ['a'],
  'passenger/following-a-private-vehicle': ['b'],
  'passenger/mode-b-payments': ['b'],
  'passenger/booking-a-ride': ['c'],
  'passenger/choosing-a-vehicle-and-fare': ['c'],
  'passenger/waiting-for-a-driver': ['c'],
  'passenger/during-the-ride': ['c'],
  'passenger/paying': ['c'],
  'passenger/sending-a-package': ['c'],
  'passenger/booking-for-someone-else': ['c'],
  'passenger/scheduling-a-ride': ['c'],

  // Driver — standby, the offer, the trip and the fee are the on-demand loop; the
  // chapter named for Mode A and Mode B is the only one on the other side of it.
  'driver/going-on-standby': ['c'],
  'driver/the-15-second-offer': ['c'],
  'driver/running-a-trip': ['c'],
  'driver/directional-travel': ['c'],
  'driver/package-jobs': ['c'],
  'driver/the-daily-platform-fee': ['c'],
  'driver/mode-a-and-b-driving': ['a', 'b'],
};

/** The ids S07 named on the home page's mode cards, as a lookup. */
const EXPLICIT: Readonly<Record<TransportModeId, ReadonlySet<string>>> = {
  a: new Set(modeCardScreens('a')),
  b: new Set(modeCardScreens('b')),
  c: new Set(modeCardScreens('c')),
};

function modeCardScreens(id: TransportModeId): readonly string[] {
  return TRANSPORT_MODES.find((mode) => mode.id === id)?.screens ?? [];
}

/**
 * The services a screen is about, in `a`, `b`, `c` order. Empty for the frames that
 * belong to none — a splash screen, an OTP field, a document photograph.
 */
export function modesForScreen(screen: ScreenEntry): readonly TransportModeId[] {
  const found = new Set<TransportModeId>();

  for (const chapter of screen.chapters) {
    for (const mode of MODE_CHAPTERS[chapter] ?? []) found.add(mode);
  }
  for (const mode of MODE_IDS) {
    if (EXPLICIT[mode].has(screen.id)) found.add(mode);
  }

  return MODE_IDS.filter((mode) => found.has(mode));
}

/**
 * Every screen for a mode, in registry order — the gallery's `?mode=` filter, and
 * the counter beside each chip.
 */
export function screensForMode(mode: TransportModeId): readonly ScreenEntry[] {
  return SCREENS.filter((screen) => modesForScreen(screen).includes(mode));
}
