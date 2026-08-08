import Link from 'next/link';

import { Button, Field, Input, Select, StatusPill } from '@mageride/ui';

/**
 * SCR-AP-010 / 012 / 014's search card — the wireframe's row of criteria and its
 * one **Search** button, for all three directories.
 *
 * ## It is a `<form method="get">` and holds no state
 *
 * The criteria *are* the URL. An operator who narrowed the driver directory to a
 * plate and Level 2 can reload it, bookmark it, paste it into a ticket and step
 * back through their attempts with the back button — and an investigator handing
 * over mid-shift sends a link rather than a list of what they typed. No JavaScript
 * is involved in searching a directory, which is also why nothing here needs to be
 * a client component. `StatsFilter`, `QueueFilter` and `TicketFilter` are the same
 * shape for the same reasons.
 *
 * Every criterion is an independent input and the form submits all of them, so
 * "any combination" is what the markup does rather than something the screen has to
 * arrange: admin-bff ANDs whatever arrives and an empty box is not a criterion.
 *
 * **The cursor is deliberately not a hidden field.** Pressing Search is a new
 * question, and carrying page three of the previous answer into it would open the
 * results somewhere in the middle of a list nobody has seen the start of.
 *
 * ## One field renders as three shapes and no more
 *
 * A text box, a select over a closed enum, and a select over an enum whose absence
 * is itself a value (`status=verified` on the driver directory). There is no
 * combo-box and no free-text field pretending to be a dropdown: the wireframe draws
 * "Fleet org: Lanka Transit ▾", but there is no route that lists fleet
 * organisations and `fleetOrg` is a `maxLength: 200` string, so it is a text box
 * here. C107 made the same call for a ticket category. See the C109 handoff.
 */

export interface SearchTextField {
  readonly kind: 'text';
  readonly name: string;
  readonly label: string;
  readonly hint?: string;
  readonly value?: string;
  readonly maxLength: number;
  readonly error?: string;
  readonly type?: 'search' | 'email' | 'tel';
}

export interface SearchSelectField {
  readonly kind: 'select';
  readonly name: string;
  readonly label: string;
  readonly value?: string;
  readonly options: readonly { readonly value: string; readonly label: string }[];
}

export type SearchField = SearchTextField | SearchSelectField;

export interface SearchFormLabels {
  readonly heading: string;
  /** The wireframe's "any criterion" / "multiple criteria" pill. */
  readonly hint: string;
  readonly submit: string;
  readonly clear: string;
  /** "{n} results" — the count of the page on screen. */
  readonly results: string;
}

export function SearchForm({
  action,
  fields,
  labels,
  filtered,
}: {
  /** The screen's own path. The form posts to itself, so the URL is the state. */
  readonly action: string;
  readonly fields: readonly SearchField[];
  readonly labels: SearchFormLabels;
  /** Whether anything is narrowed, for the Clear control. */
  readonly filtered: boolean;
}) {
  return (
    <form
      method="get"
      action={action}
      className="flex flex-col gap-sm rounded-card border border-outline bg-background p-sm shadow-card"
    >
      <div className="flex flex-wrap items-center gap-xs">
        <h2 className="text-subtitle font-semibold text-on-surface">{labels.heading}</h2>
        <StatusPill tone="info" dot={false}>
          {labels.hint}
        </StatusPill>
        <span className="flex-1" />
        <StatusPill tone="neutral" dot={false}>
          {labels.results}
        </StatusPill>
      </div>

      <div className="flex flex-wrap items-end gap-sm">
        {fields.map((field) =>
          field.kind === 'select' ? (
            <Field key={field.name} label={field.label} className="w-[180px]">
              <Select name={field.name} defaultValue={field.value ?? ''}>
                {field.options.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </Select>
            </Field>
          ) : (
            <Field
              key={field.name}
              label={field.label}
              className="min-w-[180px] flex-1"
              {...(field.hint ? { hint: field.hint } : {})}
              {...(field.error ? { error: field.error } : {})}
            >
              <Input
                type={field.type ?? 'search'}
                name={field.name}
                defaultValue={field.value ?? ''}
                maxLength={field.maxLength}
                autoCapitalize="none"
                spellCheck={false}
              />
            </Field>
          ),
        )}

        <Button type="submit" size="compact">
          {labels.submit}
        </Button>

        {filtered ? (
          <Link
            href={action}
            className="inline-flex h-10 items-center rounded-sm px-md text-body-sm text-on-surface-variant underline underline-offset-2 hover:bg-surface-variant"
          >
            {labels.clear}
          </Link>
        ) : null}
      </div>
    </form>
  );
}
