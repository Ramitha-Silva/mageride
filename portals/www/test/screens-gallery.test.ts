import { describe, expect, it } from 'vitest';

import { TRANSPORT_MODES } from '@/content/marketing';
import { PAGES } from '@/content/pages';
import { MODE_CHAPTERS, MODE_IDS, modesForScreen, screensForMode } from '@/content/screen-modes';
import { SCREENS, SURFACE_SECTION_KEYS } from '@/content/screens';
import {
  ALL,
  GALLERY_CHAPTERS,
  GALLERY_SURFACES,
  filterScreens,
  matches,
  searchFor,
  selectionFrom,
  toggled,
} from '@/lib/screen-filter';

/**
 * `/screens`' filter, tested where it has no environment.
 *
 * The gallery component is a thin thing — chips, a grid, `hidden` — and everything
 * that could actually be *wrong* about it is in `src/lib/screen-filter.ts` and
 * `src/content/screen-modes.ts`: which screens a selection shows, what URL a chip
 * points at, and whether the mode facet says anything true. All three are pure
 * functions over the registry, so they are tested directly rather than through a
 * render that would mostly be asserting that React works.
 */

describe('the mode facet', () => {
  /**
   * The derivation's whole justification is that it reads data somebody already
   * curated. If a chapter named here is not one the registry uses, the row is dead
   * and the mode it grants is silently never granted — which looks exactly like a
   * mode nothing is tagged with.
   */
  it('maps only chapters that screens are actually tagged with', () => {
    const tagged = new Set(SCREENS.flatMap((screen) => screen.chapters));

    for (const ref of Object.keys(MODE_CHAPTERS)) {
      expect(tagged.has(ref as never), `MODE_CHAPTERS names "${ref}", which no screen shows`).toBe(
        true,
      );
    }
  });

  it('gives every mode at least one screen', () => {
    for (const mode of MODE_IDS) {
      expect(screensForMode(mode).length, `mode ${mode} matches nothing`).toBeGreaterThan(0);
    }
  });

  /**
   * **The union with `TRANSPORT_MODES[].screens` adds nothing today, and that is
   * the assertion.**
   *
   * `screen-modes.ts` folds in the five ids S07 chose for the home page's mode
   * cards, so the gallery and the home page cannot disagree about which screen
   * illustrates Mode B. Every one of those five already carries its mode through
   * the chapter map, which is the evidence that the chapter map is not missing
   * something obvious. The day this fails, one of two things has happened and both
   * want a look: S07 pointed a mode card at a screen the chapters do not place in
   * that mode, or a chapter's mapping was dropped.
   */
  it('needs no help from the home page’s mode cards', () => {
    for (const mode of TRANSPORT_MODES) {
      for (const id of mode.screens) {
        const screen = SCREENS.find((entry) => entry.id === id);
        expect(screen, `TRANSPORT_MODES names ${id}, which is not in the registry`).toBeDefined();

        const fromChapters = (screen?.chapters ?? []).some((ref) =>
          (MODE_CHAPTERS[ref] ?? []).includes(mode.id),
        );
        expect(fromChapters, `${id} is Mode ${mode.id} only because the home page says so`).toBe(
          true,
        );
      }
    }
  });

  it('orders a screen’s modes a, b, c whatever order the chapters are in', () => {
    for (const screen of SCREENS) {
      const modes = modesForScreen(screen);
      expect([...modes]).toEqual(MODE_IDS.filter((mode) => modes.includes(mode)));
    }
  });

  /** A mode-agnostic screen is the normal case, not a gap. */
  it('leaves the frames that belong to no service untagged', () => {
    const splash = SCREENS.find((screen) => screen.id === 'SCR-PA-001');
    expect(modesForScreen(splash!)).toEqual([]);
  });
});

describe('the facets the gallery offers', () => {
  /**
   * `GALLERY_SURFACES` is derived from the registry rather than from the `Surface`
   * union, because the union admits `admin` and S05 selected no admin frame. A chip
   * that filters seventy screens down to none is a broken control.
   */
  it('offers no surface with nothing behind it', () => {
    expect(GALLERY_SURFACES).not.toContain('admin');

    for (const surface of GALLERY_SURFACES) {
      expect(filterScreens({ ...ALL, surface }).length, surface).toBeGreaterThan(0);
    }
  });

  it('offers no chapter with nothing behind it', () => {
    for (const chapter of GALLERY_CHAPTERS) {
      expect(filterScreens({ ...ALL, chapter }).length, chapter).toBeGreaterThan(0);
    }
  });

  /**
   * Every surface the gallery draws needs a heading, and the four keys are S07's —
   * written for `PAGES.screens.sections` in the same order. The map and the copy
   * list are two places, so this holds them to each other; without it, reordering
   * either would go unnoticed until a section rendered under the wrong name.
   */
  it('has a heading for every surface, and it is the one S07 wrote', () => {
    const fromMap = GALLERY_SURFACES.map((surface) => SURFACE_SECTION_KEYS[surface]);

    expect(fromMap.every((key) => key !== undefined)).toBe(true);
    expect(fromMap).toEqual(PAGES.screens?.sections.map((section) => section.heading));
  });
});

describe('the selection a URL asks for', () => {
  it('reads the three facets', () => {
    const selection = selectionFrom(
      new URLSearchParams('surface=driver&mode=c&chapter=driver/running-a-trip'),
    );

    expect(selection).toEqual({
      surface: 'driver',
      mode: 'c',
      chapter: 'driver/running-a-trip',
    });
  });

  /**
   * A query string is the one input this site takes from outside itself. The answer
   * to a value it does not recognise is the whole gallery — not an error page over a
   * typo, and not an empty grid that reads as a site with no screens in it.
   */
  it('drops a value it does not recognise instead of failing', () => {
    expect(selectionFrom(new URLSearchParams('surface=lorry&mode=z&chapter=nope'))).toEqual(ALL);
    expect(filterScreens(selectionFrom(new URLSearchParams('surface=lorry')))).toHaveLength(
      SCREENS.length,
    );
  });

  it('round-trips through the URL it produces', () => {
    const selection = { surface: 'fleet', mode: null, chapter: null } as const;
    expect(searchFor(selection)).toBe('?surface=fleet');
    expect(selectionFrom(new URLSearchParams(searchFor(selection)))).toEqual(selection);
  });

  /** The unfiltered gallery is a bare `/screens`, with no query string on it. */
  it('spells the unfiltered view as no query string at all', () => {
    expect(searchFor(ALL)).toBe('');
  });

  /**
   * One selection, one URL. Two spellings of the same view would be two entries in
   * a reader's history and — once S19 wires the sitemap and canonicals — two
   * addresses for one document.
   */
  it('always orders the parameters the same way', () => {
    const selection = {
      chapter: 'driver/running-a-trip',
      mode: 'c',
      surface: 'driver',
    } as const;

    expect(searchFor(selection)).toBe('?surface=driver&mode=c&chapter=driver%2Frunning-a-trip');
  });
});

describe('pressing a chip', () => {
  it('sets a facet', () => {
    expect(toggled(ALL, 'surface', 'driver').surface).toBe('driver');
  });

  /**
   * Pressing the active chip clears it. Without that, the only way out of
   * `?surface=fleet` is a separate control, and a reader who has narrowed to two
   * screens on a phone has to go looking for it.
   */
  it('clears the facet it is already set to', () => {
    const selected = toggled(ALL, 'surface', 'driver');
    expect(toggled(selected, 'surface', 'driver').surface).toBeNull();
  });

  it('leaves the other two facets alone', () => {
    const selection = toggled(toggled(ALL, 'surface', 'driver'), 'mode', 'c');
    expect(selection).toEqual({ surface: 'driver', mode: 'c', chapter: null });
  });
});

describe('what a selection shows', () => {
  it('shows everything when nothing is selected', () => {
    expect(filterScreens(ALL)).toHaveLength(SCREENS.length);
  });

  it('ANDs the three facets', () => {
    const driverModeC = filterScreens({ surface: 'driver', mode: 'c', chapter: null });

    expect(driverModeC.length).toBeGreaterThan(0);
    for (const screen of driverModeC) {
      expect(screen.surface).toBe('driver');
      expect(modesForScreen(screen)).toContain('c');
    }
  });

  it('keeps registry order', () => {
    const shown = filterScreens({ ...ALL, surface: 'passenger' });
    const expected = SCREENS.filter((screen) => screen.surface === 'passenger');
    expect(shown.map((screen) => screen.id)).toEqual(expected.map((screen) => screen.id));
  });

  it('agrees with `matches` screen by screen', () => {
    const selection = { surface: 'passenger', mode: 'b', chapter: null } as const;
    const shown = new Set(filterScreens(selection).map((screen) => screen.id));

    for (const screen of SCREENS) {
      expect(shown.has(screen.id), screen.id).toBe(matches(screen, selection));
    }
  });
});
