'use client';

import { useEffect, useId, useRef, useState } from 'react';

import { signOut } from '@/server/auth-actions';
import { setAppearance, setLocale } from '@/server/preferences';

/**
 * The topbar's account menu: who is signed in, which roles they hold, the two
 * per-browser preferences, and Sign out.
 *
 * **Roles are shown, not chosen from.** URD §2.1 makes effective permissions the
 * *union* of every role held, so there is nothing to switch between — and a role
 * picker would imply an operator could drop a grant to see less, which is not how
 * the matrix works and not what the API would enforce.
 *
 * Both preference controls are ordinary forms posting to server actions rather
 * than fetch calls, so the page the operator sees afterwards is one the server
 * rendered with the new preference already applied — the appearance class lives
 * on `<html>`, which no client-side state could reach from here.
 */

export interface AccountMenuLabels {
  readonly menu: string;
  readonly roles: string;
  readonly signOut: string;
  readonly appearance: string;
  readonly language: string;
}

export interface OptionLabel {
  readonly value: string;
  readonly label: string;
  readonly current: boolean;
}

export function AccountMenu({
  name,
  roles,
  labels,
  appearances,
  locales,
}: {
  /** Display name or email — whatever the sign-in response carried. */
  name: string;
  roles: readonly string[];
  labels: AccountMenuLabels;
  appearances: readonly OptionLabel[];
  locales: readonly OptionLabel[];
}) {
  const [open, setOpen] = useState(false);
  const panelId = useId();
  const container = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;

    const onPointerDown = (event: MouseEvent) => {
      if (!container.current?.contains(event.target as Node)) setOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };

    document.addEventListener('mousedown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('mousedown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  return (
    <div ref={container} className="relative">
      <button
        type="button"
        onClick={() => setOpen((value) => !value)}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={panelId}
        aria-label={labels.menu}
        className="flex items-center gap-xs rounded-sm px-xs py-xxs text-body-sm text-on-surface hover:bg-surface-variant"
      >
        <span
          aria-hidden="true"
          className="grid size-7 shrink-0 place-items-center rounded-full bg-secondary-container font-semibold text-secondary"
        >
          {name.charAt(0).toUpperCase()}
        </span>
        <span className="hidden max-w-[16ch] truncate md:inline">{name}</span>
      </button>

      {open ? (
        <div
          id={panelId}
          className="absolute end-0 z-40 mt-xxs flex w-[260px] flex-col gap-sm rounded-md border border-outline bg-background p-sm shadow-elevation-3"
        >
          <div className="flex flex-col gap-xxs">
            <p className="truncate text-body-sm font-semibold">{name}</p>
            <p className="text-caption text-on-surface-variant">{labels.roles}</p>
            <ul className="flex flex-wrap gap-xxs">
              {roles.map((role) => (
                <li
                  key={role}
                  className="rounded-sm bg-surface-variant px-xs py-px text-caption text-on-surface-variant"
                >
                  {role}
                </li>
              ))}
            </ul>
          </div>

          <OptionRow
            legend={labels.appearance}
            name="appearance"
            options={appearances}
            action={setAppearance}
          />
          <OptionRow legend={labels.language} name="locale" options={locales} action={setLocale} />

          <form action={signOut}>
            <button
              type="submit"
              className="w-full rounded-sm border border-outline px-sm py-xs text-body-sm text-on-surface hover:bg-surface-variant"
            >
              {labels.signOut}
            </button>
          </form>
        </div>
      ) : null}
    </div>
  );
}

function OptionRow({
  legend,
  name,
  options,
  action,
}: {
  legend: string;
  name: string;
  options: readonly OptionLabel[];
  action: (formData: FormData) => Promise<void>;
}) {
  return (
    <form action={action} className="flex flex-col gap-xxs">
      <fieldset>
        <legend className="pb-xxs text-caption text-on-surface-variant">{legend}</legend>
        <div className="flex gap-xxs">
          {options.map((option) => (
            <button
              key={option.value}
              type="submit"
              name={name}
              value={option.value}
              aria-pressed={option.current}
              className={[
                'flex-1 rounded-sm border px-xs py-xxs text-caption transition-colors',
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
    </form>
  );
}
