import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  auditHref,
  auditSearch,
  auditSelection,
  isFiltered,
  type AuditEvent,
} from '@/api/audit-log';
import { AuditTable } from '@/components/audit/AuditTable';
import { auditCsv, auditRows, type RenderContext } from '@/components/audit/model';
import { createAdminTranslator } from '@/i18n';

import { adminMenuManifest, sessionFor } from './support/urd';

/**
 * SCR-AP-009, and the Definition-of-Done item that is a property of the whole
 * screen rather than of any component in it: **the audit view is read-only, with
 * no mutating control rendered.**
 *
 * The contract makes that stronger than a role check — "append-only, there is no
 * write route here" — so the assertion below is against the *tree*: every file
 * under `app/(portal)/audit-log` and `src/components/audit`, checked for a
 * mutation, a server action or a form that posts. That is the executable form of
 * a sentence that would otherwise be a comment.
 */

const APP_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

afterEach(cleanup);

const t = createAdminTranslator('en');
const context: RenderContext = { t, locale: 'en' };

const ACTOR = '0199a1f0-0000-7000-8000-00000000ac01';
const SUBJECT = '0199a1f0-0000-7000-8000-00000000bd01';

function sources(dir: string): { path: string; source: string }[] {
  const full = join(APP_ROOT, dir);
  if (!existsSync(full)) return [];

  const files: { path: string; source: string }[] = [];
  const walk = (at: string) => {
    for (const entry of readdirSync(at, { withFileTypes: true })) {
      const child = join(at, entry.name);
      if (entry.isDirectory()) walk(child);
      else if (/\.tsx?$/.test(entry.name)) {
        files.push({ path: child.slice(APP_ROOT.length + 1), source: readFileSync(child, 'utf8') });
      }
    }
  };
  walk(full);
  return files;
}

/** Strips comments, so a paragraph about mutation is not read as one. */
function code(source: string): string {
  return source.replaceAll(/\/\*[\s\S]*?\*\//g, '').replaceAll(/(^|[^:])\/\/.*$/gm, '$1');
}

describe('DoD — the audit view renders no mutating control', () => {
  const FILES = [...sources('app/(portal)/audit-log'), ...sources('src/components/audit')];

  it('found the screen', () => {
    expect(FILES.length).toBeGreaterThan(2);
  });

  it('calls no mutation anywhere in the screen', () => {
    for (const { path, source } of FILES) {
      expect(code(source), `${path} mutates`).not.toMatch(/\bmutate\s*[(<]/);
    }
  });

  it('imports no server action module', () => {
    for (const { path, source } of FILES) {
      expect(code(source), `${path} imports a server action`).not.toMatch(
        /from '@\/server\/[a-z-]*actions'/,
      );
    }
  });

  it('posts no form — every form on the screen is a GET filter', () => {
    for (const { path, source } of FILES) {
      const body = code(source);
      expect(body, `${path} has a form action`).not.toMatch(/<form[^>]*\baction=\{/);
      for (const match of body.matchAll(/<form\b[\s\S]*?>/g)) {
        expect(match[0], `${path} has a form that is not method="get"`).toContain('method="get"');
      }
    }
  });

  it('is served at the path AdminMenu.cs gives its nav item', () => {
    const path = adminMenuManifest()
      .flatMap((group) => group.items)
      .find((item) => item.key === 'audit-log')?.path;

    expect(path).toBe('/audit-log');
    expect(existsSync(join(APP_ROOT, 'app/(portal)', path!, 'page.tsx'))).toBe(true);
  });

  it('is in an Auditor’s menu, which is whose screen it is', () => {
    const items = sessionFor(['auditor'])
      .menu.flatMap((group) => group.items)
      .map((item) => item.key);

    expect(items).toContain('audit-log');
  });
});

describe('the filters are the URL', () => {
  it('keeps what was typed even when it is not a usable id', () => {
    // The box has to hold a mistyped value so the screen can say what is wrong
    // with it, while the query drops it so nothing malformed is sent.
    const selection = auditSelection({ actorId: 'nuwan@mageride.lk' });

    expect(selection.typedActor).toBe('nuwan@mageride.lk');
    expect(selection.actorId).toBeUndefined();
    expect(auditSearch(selection)).toEqual({ limit: 100 });
  });

  it('upper-cases an action, because the log stores screaming snake', () => {
    expect(auditSelection({ action: 'wallet_fee_reversed' }).action).toBe('WALLET_FEE_REVERSED');
  });

  it('takes instants rather than business dates, this being a window on a clock', () => {
    expect(auditSelection({ from: '2026-06-17T09:00' }).from).toBe('2026-06-17T09:00');
    expect(auditSelection({ from: '2026-06-17' }).from).toBeUndefined();
  });

  it('rejects an instant that parses to nothing', () => {
    expect(auditSelection({ from: '2026-13-45T99:99' }).from).toBeUndefined();
  });

  it('knows when nothing is narrowing the log', () => {
    expect(isFiltered(auditSelection({}))).toBe(false);
    expect(isFiltered(auditSelection({ action: 'PII_READ' }))).toBe(true);
    // A cursor is a page, not a filter — "Clear" must not appear because somebody
    // pressed Older.
    expect(isFiltered(auditSelection({ cursor: 'abc' }))).toBe(false);
  });

  it('carries the filters to the next page and drops the cursor for the export', () => {
    const selection = auditSelection({ action: 'PII_READ', actorId: ACTOR, cursor: 'page2' });

    expect(auditHref('/audit-log', selection, { cursor: 'page3' })).toContain('cursor=page3');
    const exported = auditHref('/audit-log/export', selection, { cursor: null });
    expect(exported).toContain('action=PII_READ');
    expect(exported).not.toContain('cursor');
  });
});

describe('the table', () => {
  const event: AuditEvent = {
    eventId: '0199a1f0-0000-7000-8000-00000000ee01',
    actorId: ACTOR,
    actorRole: 'finance_officer',
    action: 'WALLET_FEE_REVERSED',
    subjectId: SUBJECT,
    subjectType: 'driver_wallet',
    before: null,
    after: { amountMinor: 20_000 },
    ip: '203.0.113.9',
    occurredAt: '2026-06-17T03:35:00Z',
  };

  const LABELS = {
    caption: 'Every admin action',
    when: 'Time',
    actor: 'Actor',
    role: 'Role',
    action: 'Action',
    target: 'Target',
    change: 'Before / after',
    empty: 'No entry matches this filter.',
  };

  it('prints the action verbatim, because it is what the filter and the export use', () => {
    render(<AuditTable rows={auditRows([event], context)} labels={LABELS} />);

    expect(screen.getByText('WALLET_FEE_REVERSED')).toBeDefined();
  });

  it('translates the role, because a role is a job somebody holds', () => {
    const [row] = auditRows([event], context);
    expect(row?.role).toBe('Finance Officer');
  });

  it('hands back an unrecognised role unchanged rather than dropping the column', () => {
    const [row] = auditRows(
      [{ ...event, actorRole: 'something_new' as AuditEvent['actorRole'] }],
      context,
    );
    expect(row?.role).toBe('something_new');
  });

  it('shows the after image without paraphrasing it', () => {
    const [row] = auditRows([event], context);
    expect(row?.change).toContain('amountMinor');
    expect(row?.change).not.toContain('−');
  });

  it('says the log is empty rather than drawing a row nobody recorded', () => {
    render(<AuditTable rows={[]} labels={LABELS} />);
    expect(screen.getByText(LABELS.empty)).toBeDefined();
  });

  it('renders no button anywhere', () => {
    render(<AuditTable rows={auditRows([event], context)} labels={LABELS} />);
    expect(screen.queryAllByRole('button')).toHaveLength(0);
  });
});

describe('the export', () => {
  const event: AuditEvent = {
    eventId: 'e1',
    actorId: ACTOR,
    action: 'ROLE_GRANT',
    occurredAt: '2026-06-17T03:35:00Z',
  };

  it('writes a header and one line per event', () => {
    const csv = auditCsv([event]);
    const lines = csv.split('\r\n');

    expect(lines[0]).toContain('"eventId"');
    expect(lines).toHaveLength(2);
    expect(lines[1]).toContain('"ROLE_GRANT"');
  });

  it('quotes every field and doubles an inner quote (RFC 4180)', () => {
    // `before`/`after` are JSON and are full of quotes. A naive join would produce
    // a file whose columns move halfway down the page.
    const csv = auditCsv([{ ...event, after: { note: 'he said "no"' } }]);

    expect(csv).toContain('""note""');
    expect(csv.split('\r\n')).toHaveLength(2);
  });

  it('writes an empty cell for an absent before image, not the word null', () => {
    const csv = auditCsv([{ ...event, before: null }]);
    expect(csv.split('\r\n')[1]).toContain(',"",""');
  });
});
