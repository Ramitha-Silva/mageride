import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ExtractedField } from '@/api/verification';
import { DecisionRail } from '@/components/verification/DecisionRail';
import { DocumentGrid } from '@/components/verification/DocumentGrid';
import { FieldsTable } from '@/components/verification/FieldsTable';
import { documentTiles, fieldRows, stepRows, type RenderContext } from '@/components/verification/model';
import { QueueFilter } from '@/components/verification/QueueFilter';
import { QueueTable } from '@/components/verification/QueueTable';
import { QueueTabs } from '@/components/verification/QueueTabs';
import { createAdminTranslator } from '@/i18n';

/**
 * SCR-AP-003/003a as they are drawn — the four Definition-of-Done items that are
 * properties of the rendered screen rather than of the model behind it:
 *
 *  - the tabs are **links** carrying the officer's search, so a decision and a
 *    Back return them to the queue they were reading;
 *  - a queue row opens the detail its subject belongs to — an organisation's
 *    opens SCR-AP-003c, not SCR-AP-003a;
 *  - **Approve is disabled while any flagged field is unconfirmed**, and the
 *    screen says why rather than leaving a dead control;
 *  - a thumbnail's `src` is the portal's audited relay, never the bucket.
 */

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

// The rail and the row controls call server actions on submit; nothing here
// submits, and importing the real module would pull the whole data layer into a
// render test.
vi.mock('@/server/verification-actions', () => ({
  decideField: vi.fn(async () => ({})),
  decideSubject: vi.fn(async () => ({})),
}));

afterEach(cleanup);

const t = createAdminTranslator('en');
const context: RenderContext = { t, locale: 'en' };

const SELECTION = { queue: 'vehicle-registration', search: 'ABC-1234' } as const;

describe('the three tabs', () => {
  const COUNTS = {
    'driving-license': '22',
    'vehicle-registration': '16',
    'fleet-org': '—',
  } as const;

  const LABELS = {
    navLabel: 'Verification queues',
    drivingLicence: 'Driving-licence pending',
    vehicleRegistration: 'Vehicle-registration pending',
    fleetOrg: 'Fleet-org approval',
  };

  it('are links that keep the search, so the filter is the URL and nothing else', () => {
    render(<QueueTabs selection={SELECTION} counts={COUNTS} labels={LABELS} />);

    expect(screen.getAllByRole('link').map((link) => link.getAttribute('href'))).toEqual([
      '/verification?queue=driving-license&search=ABC-1234',
      '/verification?queue=vehicle-registration&search=ABC-1234',
      '/verification?queue=fleet-org&search=ABC-1234',
    ]);
  });

  it('marks the queue being read, and only that one', () => {
    render(<QueueTabs selection={SELECTION} counts={COUNTS} labels={LABELS} />);

    const current = screen
      .getAllByRole('link')
      .filter((link) => link.getAttribute('aria-current') === 'page');

    expect(current).toHaveLength(1);
    expect(current[0]?.textContent).toContain('Vehicle-registration pending');
  });

  it('shows each queue’s own count beside its name', () => {
    render(<QueueTabs selection={SELECTION} counts={COUNTS} labels={LABELS} />);

    // The wireframe's three badges, and the third queue is one admin-bff could
    // not answer — so it says nothing rather than claiming zero.
    expect(screen.getByText('22')).toBeTruthy();
    expect(screen.getByText('—')).toBeTruthy();
  });
});

describe('the filter bar', () => {
  const LABELS = {
    search: 'Search',
    searchHint: 'Driver, vehicle or organisation',
    status: 'Status',
    statusAll: 'Any status',
    statusPending: 'Pending',
    statusApproved: 'Approved',
    statusRejected: 'Rejected',
    apply: 'Apply',
    clear: 'Clear',
    total: '38 pending',
  };

  it('submits back to the screen with the tab still on it', () => {
    const { container } = render(
      <QueueFilter selection={SELECTION} labels={LABELS} filtered />,
    );

    const form = container.querySelector('form[action="/verification"]');
    const sent = Object.fromEntries(new FormData(form as HTMLFormElement).entries());

    // Without the tab, searching from the fleet-org queue would answer on the
    // driving-licence one.
    expect(sent).toEqual({ queue: 'vehicle-registration', search: 'ABC-1234', status: '' });
  });

  it('offers Clear only when something is narrowing the queues', () => {
    const { unmount } = render(<QueueFilter selection={SELECTION} labels={LABELS} filtered />);
    expect(screen.getByRole('link', { name: 'Clear' })).toBeTruthy();
    unmount();

    render(
      <QueueFilter selection={{ queue: 'driving-license' }} labels={LABELS} filtered={false} />,
    );
    expect(screen.queryByRole('link', { name: 'Clear' })).toBeNull();
  });
});

describe('a queue’s rows', () => {
  const LABELS = {
    heading: 'Driving-licence verifications — pending',
    caption: 'Pending verifications',
    flagsOnly: 'Manual / doubtful flags only',
    subject: 'Driver',
    middleOne: 'Submitted',
    middleTwo: 'Flagged fields',
    status: 'Status',
    action: 'Action',
    review: 'Review',
    empty: 'Nothing is waiting in this queue.',
  };

  const ROW = {
    key: 'org-1',
    href: '/verification/org/org-1?queue=fleet-org',
    primary: 'Lanka Transit (Pvt) Ltd',
    secondary: 'org-1',
    cells: [{ text: '120 vehicles' }, { pills: [{ tone: 'info' as const, label: 'KYC complete' }] }] as const,
    status: { tone: 'warning' as const, label: 'Pending · 2' },
  };

  it('states AL-27 rather than implementing it', () => {
    render(<QueueTable rows={[ROW]} labels={LABELS} />);

    // An auto-verified document produces no pending field row, so it cannot reach
    // this queue at all — the caption says so; nothing here filters.
    expect(screen.getByText('Manual / doubtful flags only')).toBeTruthy();
  });

  it('opens the detail the subject belongs to', () => {
    render(<QueueTable rows={[ROW]} labels={LABELS} />);

    expect(screen.getByRole('link', { name: /Review/ }).getAttribute('href')).toBe(
      '/verification/org/org-1?queue=fleet-org',
    );
  });

  it('says the queue is clear rather than drawing an empty table body', () => {
    render(<QueueTable rows={[]} labels={LABELS} />);
    expect(screen.getByText('Nothing is waiting in this queue.')).toBeTruthy();
  });
});

describe('the attached-document grid', () => {
  it('points every thumbnail at the portal’s audited relay, never at storage', () => {
    const tiles = documentTiles(
      [{ docId: 'doc-1', kind: 'driving_license', thumbUrl: 'https://bucket.example/leaked.jpg' }],
      {
        viewer: (id) => `/verification/S/doc/${id}`,
        media: (id) => `/verification/media/${id}?variant=thumb`,
      },
      context,
    );

    render(
      <DocumentGrid
        tiles={tiles}
        labels={{ heading: 'Attached documents', hint: 'Tap one', empty: 'None', note: 'Audited' }}
      />,
    );

    // `DocumentRef` carries links, and they are deliberately unused: the fetch is
    // built from `docId` so no upstream string reaches an `src`, and the relay is
    // what records `DOC_VIEW`.
    const image = screen.getByRole('img', { name: 'Driving licence' });
    expect(image.getAttribute('src')).toBe('/verification/media/doc-1?variant=thumb');
    expect(screen.getByRole('link').getAttribute('href')).toBe('/verification/S/doc/doc-1');
  });

  it('says there is nothing attached rather than drawing an empty grid', () => {
    render(
      <DocumentGrid
        tiles={[]}
        labels={{ heading: 'Attached documents', hint: 'Tap one', empty: 'Nothing attached', note: 'x' }}
      />,
    );

    expect(screen.getByText('Nothing attached')).toBeTruthy();
    expect(screen.queryByRole('img')).toBeNull();
  });
});

describe('the fields table', () => {
  const FIELDS: ExtractedField[] = [
    { key: 'licence_no', value: 'B1234567', source: 'ai', confidence: 0.98, verifyStatus: 'auto_verified' },
    { key: 'nic_no', value: '1990 12345 678', source: 'manual', verifyStatus: 'pending' },
  ];

  const LABELS = {
    heading: 'AI-extracted fields',
    engine: 'Gemini Flash 3.0',
    caption: 'AI-extracted fields',
    field: 'Field',
    value: 'Value',
    source: 'Source',
    status: 'Status',
    action: 'Action',
    empty: 'Nothing extracted',
    note: 'Pending rows must be confirmed',
    decision: {
      confirm: 'Confirm',
      edit: 'Edit',
      editConfirm: 'Edit & confirm',
      cancel: 'Cancel',
      correctedValue: 'Corrected value',
      working: 'Recording…',
    },
  };

  function renderTable() {
    return render(
      <FieldsTable
        rows={fieldRows(FIELDS, context)}
        subjectId="0199a1f0-0000-7000-8000-000000000001"
        subjectType="driver"
        returnTo="/verification/0199a1f0-0000-7000-8000-000000000001"
        labels={LABELS}
      />,
    );
  }

  it('offers Confirm and Edit on the pending row and on no other', () => {
    renderTable();

    // Two buttons in total: the auto-verified row has none, because confirming a
    // field nobody flagged is a decision with no question in front of it.
    expect(screen.getAllByRole('button')).toHaveLength(2);
    expect(screen.getByRole('button', { name: 'Confirm NIC no' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Edit NIC no' })).toBeTruthy();
  });

  it('declares the subject and the field on the request the row will make', () => {
    const { container } = renderTable();

    const form = container.querySelector('form');
    const sent = Object.fromEntries(new FormData(form as HTMLFormElement).entries());

    expect(sent).toMatchObject({
      subjectId: '0199a1f0-0000-7000-8000-000000000001',
      subjectType: 'driver',
      fieldKey: 'nic_no',
    });
  });

  it('draws the row’s provenance and verdict, which is what the officer is judging', () => {
    renderTable();

    const row = screen.getByText('Licence no').closest('tr');
    expect(within(row as HTMLElement).getByText('AI 0.98')).toBeTruthy();
    expect(within(row as HTMLElement).getByText('Auto-verified')).toBeTruthy();
  });
});

describe('the decision rail', () => {
  const LABELS = {
    heading: 'Decision',
    stepsHeading: 'Onboarding steps',
    reason: 'Reject reason (if any)',
    reasonHint: 'Shown to the applicant exactly as written.',
    approve: 'Approve driver',
    reject: 'Reject with reason',
    working: 'Recording…',
    blocked: 'Approve unlocks once every pending field is confirmed.',
    audit: 'This action is written to the audit trail against your name.',
  };

  function renderRail(approvable: boolean) {
    return render(
      <DecisionRail
        subjectId="0199a1f0-0000-7000-8000-000000000001"
        subjectType="driver"
        approvable={approvable}
        steps={stepRows([{ step: 'profile', status: 'PENDING_REVIEW' }], t)}
        returnTo="/verification?queue=driving-license"
        labels={LABELS}
      />,
    );
  }

  it('locks Approve while a flagged field is unconfirmed, and says why', () => {
    renderRail(false);

    expect(screen.getByRole('button', { name: 'Approve driver' })).toHaveProperty('disabled', true);
    expect(screen.getByText(LABELS.blocked)).toBeTruthy();
  });

  it('unlocks it once nothing is pending, and drops the explanation with it', () => {
    renderRail(true);

    expect(screen.getByRole('button', { name: 'Approve driver' })).toHaveProperty('disabled', false);
    expect(screen.queryByText(LABELS.blocked)).toBeNull();
  });

  it('leaves Reject reachable at every moment Approve is not', () => {
    // US-2.15 is the way out of a submission that cannot be fixed; gating it on
    // the same condition as Approve would trap the officer with the queue entry.
    renderRail(false);
    expect(screen.getByRole('button', { name: 'Reject with reason' })).toHaveProperty(
      'disabled',
      false,
    );
  });

  it('tells the operator which trail their name is about to appear on', () => {
    renderRail(true);
    expect(screen.getByText(LABELS.audit)).toBeTruthy();
  });

  it('draws the per-step breakdown beside the button it explains', () => {
    renderRail(false);

    expect(screen.getByText('Onboarding steps')).toBeTruthy();
    expect(screen.getByText('Profile / licence')).toBeTruthy();
  });
});
