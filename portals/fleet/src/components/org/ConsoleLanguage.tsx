'use client';

import { setLocale } from '@/server/preferences';

/**
 * **SCR-FP-002's "Language ▾"** — the third row of the wireframe's org-profile
 * card.
 *
 * ## It sets the console's language, not the organisation's
 *
 * `web_fleet.html` puts the control on the organisation card, which reads as a
 * property of the organisation. **It cannot be one:** `registry.fleets` has no
 * language column, `POST /v1/fleets` takes no language, and nothing on any
 * contract stores one against an org. What the platform does have is
 * `iam.users.language` — the language MageRide *messages* a person in — and this
 * console's own per-browser preference, which is what the shell's account menu
 * already sets.
 *
 * So the control is real and works, and the caption says exactly what it changes.
 * The alternative — a picker that appears to set an organisation-wide language
 * and silently sets a cookie — is the kind of control an operator only discovers
 * is a lie when a colleague's console is still in English. Asking for
 * `registry.fleets.language` is raised in the C112 handoff.
 *
 * It is the same `setLocale` server action the account menu posts to, and the
 * same shape of control, so the two cannot disagree about what "the language" is.
 */

export interface LanguageOption {
  readonly value: string;
  readonly label: string;
  readonly current: boolean;
}

export function ConsoleLanguage({
  legend,
  note,
  options,
}: {
  legend: string;
  note: string;
  options: readonly LanguageOption[];
}) {
  return (
    <form action={setLocale} className="flex flex-col gap-xxs">
      <fieldset>
        <legend className="pb-xxs text-label text-on-surface-variant">{legend}</legend>
        <div className="flex flex-wrap gap-xxs">
          {options.map((option) => (
            <button
              key={option.value}
              type="submit"
              name="locale"
              value={option.value}
              aria-pressed={option.current}
              className={[
                'rounded-sm border px-sm py-xxs text-body-sm transition-colors',
                option.current
                  ? 'border-primary bg-primary-container text-on-primary-container'
                  : 'border-outline text-on-surface-variant hover:bg-surface-variant',
              ].join(' ')}
            >
              {option.label}
            </button>
          ))}
        </div>
      </fieldset>
      <p className="text-caption text-outline-variant">{note}</p>
    </form>
  );
}
