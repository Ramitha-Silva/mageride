import { cx } from '@mageride/ui';

import { TRANSPORT_MODES, type TransportMode } from '@/content/marketing';
import { HOME } from '@/content/pages';
import { createWwwTranslator, type Locale } from '@/i18n';

/**
 * Section 2 — the three transport modes, one card each.
 *
 * ## The boundary these cards must not blur
 *
 * `CLAUDE.md`'s universal rule: **ride-svc owns Mode C (on-demand); trip-state-svc
 * owns Mode A/B (scheduled). Never cross this boundary.** On a marketing page that
 * translates into a layout rule rather than an architecture one — **three cards,
 * three propositions, never one product with a toggle on it.** Copy that said
 * "switch between live tracking and booking" would describe an app MageRide is not,
 * and would set an expectation the product cannot meet.
 *
 * So this is a grid of three peers. It is deliberately *not* the tab pattern used
 * for how-it-works in section 3, where two cuts really are two views of one thing.
 *
 * The user-visible difference is sharper than the architectural one anyway, and the
 * taglines carry it: Mode A is free to watch and always will be; Mode B needs
 * somebody's permission and may carry a monthly charge; Mode C is a fare per trip,
 * shown before you book.
 *
 * ## The colours are the preset's mode tokens, not new ones
 *
 * `mode-a` / `mode-b` / `mode-c` are D2's own badge colours — green, grey, orange —
 * and are already in `@mageride/tailwind-preset`. Using them here means the card a
 * reader meets on the marketing site is the colour of the badge they will meet in
 * the app, which is the entire value of a design token. **Nothing here adds one.**
 *
 * The colour is a 4px rule at the top of the card rather than a filled panel: these
 * are brand hues chosen for small badges, and `mode-b`'s grey behind body text
 * would fail contrast in the light appearance while `mode-c`'s orange would fail it
 * in both.
 */
const MODE_ACCENT: Readonly<Record<TransportMode['id'], string>> = {
  a: 'bg-mode-a',
  b: 'bg-mode-b',
  c: 'bg-mode-c',
};

export function ModeCards({ locale }: { readonly locale: Locale }) {
  const t = createWwwTranslator(locale);

  return (
    <section className="mx-auto max-w-[1200px] px-4 py-section">
      <h2 className="font-display text-hero-sm text-on-surface">{t(HOME.modes.heading)}</h2>
      <p className="mt-md max-w-[62ch] text-body text-on-surface-variant">
        {t(HOME.modes.body)}
      </p>

      <ul className="mt-lg grid gap-md md:grid-cols-3">
        {TRANSPORT_MODES.map((mode) => (
          <li
            key={mode.id}
            className="flex flex-col overflow-hidden rounded-card border border-outline-variant bg-surface"
          >
            <span aria-hidden className={cx('h-1 w-full', MODE_ACCENT[mode.id])} />
            <div className="flex flex-1 flex-col gap-xs p-lg">
              <h3 className="font-display text-title text-on-surface">{t(mode.name)}</h3>
              <p className="text-body-sm font-medium text-on-surface-variant">
                {t(mode.tagline)}
              </p>
              <p className="mt-xxs text-body-sm text-on-surface-variant">{t(mode.body)}</p>
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}
