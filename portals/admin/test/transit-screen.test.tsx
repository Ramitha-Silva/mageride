import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { ToastProvider } from '@mageride/ui';

import type { FeedUploadStatus } from '@/api/transit';
import { isReachable } from '@/server/access';
import { ActivateForm, type ActivateLabels } from '@/components/transit/ActivateForm';
import { FeedCard } from '@/components/transit/FeedCard';
import { feedCardView, versionRows } from '@/components/transit/model';
import { UploadCard } from '@/components/transit/UploadCard';
import { VersionHistory } from '@/components/transit/VersionHistory';
import { createAdminTranslator } from '@/i18n';

import { adminMenuManifest } from './support/urd';

/**
 * SCR-AP-016 as it is drawn — the GTFS Dataset Manager's seven states, its two
 * refusals, and the fence around the whole screen.
 *
 * Two things are asserted as **absences**, and each is a rule rather than an
 * oversight:
 *
 *  - **no control that edits a feed.** AL-56 makes the dataset an externally
 *    provided file; this screen is the sole ingestion surface, and an authoring
 *    affordance here would be a workstream the platform does not have.
 *  - **no Activate on a failed feed.** "Failed feeds can never be activated" is
 *    the wireframe's own note. A disabled button would promise that a fix exists
 *    on this screen; there isn't one — the operator fixes the zip and re-uploads.
 */

const APP_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const SCREEN_DIR = join(APP_ROOT, 'app/(portal)/config/transit/gtfs');

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

vi.mock('next/navigation', () => ({
  useRouter: () => ({ replace: vi.fn(), refresh: vi.fn(), push: vi.fn() }),
  usePathname: () => '/config/transit/gtfs',
}));

const activate = vi.hoisted(() => vi.fn(async () => ({})));
vi.mock('@/server/transit-actions', () => ({ activateFeed: activate }));

afterEach(cleanup);

const t = createAdminTranslator('en');
const context = { t, locale: 'en' } as const;

const ACTIVATE_LABELS: ActivateLabels = {
  open: t('admin.transit.activate.open'),
  reactivate: t('admin.transit.activate.reactivate'),
  title: t('admin.transit.activate.title'),
  replacing: t('admin.transit.activate.replacing'),
  firstFeed: t('admin.transit.activate.firstFeed'),
  atomic: t('admin.transit.activate.atomic'),
  rollbackNote: t('admin.transit.activate.rollbackNote'),
  confirm: t('admin.transit.activate.confirm'),
  cancel: t('common.cancel'),
  close: t('common.close'),
  working: t('admin.transit.activate.working'),
  done: t('admin.transit.activate.done'),
  reload: t('admin.transit.activate.reload'),
  dismiss: t('admin.transit.activate.dismiss'),
  audit: t('admin.audit.notice'),
};

const CARD_LABELS = {
  heading: t('admin.transit.preview.heading'),
  stepperLabel: t('admin.transit.stepper.label'),
  version: t('admin.transit.preview.version'),
  noVersion: t('admin.transit.preview.noVersion'),
  serviceWindow: t('admin.transit.preview.serviceWindow'),
  noWindow: t('admin.transit.preview.noWindow'),
  countsCaption: t('admin.transit.preview.countsCaption'),
  noCounts: t('admin.transit.preview.noCounts'),
  validatingNote: t('admin.transit.preview.validatingNote'),
  noWarnings: t('admin.transit.preview.noWarnings'),
  failedHeading: t('admin.transit.failed.heading'),
  failedBody: t('admin.transit.failed.body'),
  reportCsv: t('admin.transit.failed.reportCsv'),
  reportJson: t('admin.transit.failed.reportJson'),
  liveNote: t('admin.transit.preview.liveNote'),
  archivedNote: t('admin.transit.preview.archivedNote'),
  activate: ACTIVATE_LABELS,
};

const HISTORY_LABELS = {
  heading: t('admin.transit.history.heading'),
  caption: t('admin.transit.history.caption'),
  version: t('admin.transit.history.version'),
  file: t('admin.transit.history.file'),
  uploaded: t('admin.transit.history.uploaded'),
  routes: t('admin.transit.history.routes'),
  status: t('admin.transit.history.status'),
  actions: t('admin.transit.history.actions'),
  empty: t('admin.transit.history.empty'),
  report: t('admin.transit.history.report'),
  zip: t('admin.transit.history.zip'),
  capped: t('admin.transit.history.capped', { limit: 100 }),
  none: t('admin.transit.none'),
  activate: ACTIVATE_LABELS,
};

const UPLOAD_LABELS = {
  heading: t('admin.transit.upload.heading'),
  dropzone: t('admin.transit.upload.dropzone'),
  hint: t('admin.transit.upload.hint'),
  required: t('admin.transit.upload.required'),
  externalNote: t('admin.transit.upload.externalNote'),
  uploading: t('admin.transit.upload.uploading'),
  percent: t('admin.transit.upload.percent'),
  cancel: t('admin.transit.upload.cancel'),
  rejectedType: t('admin.transit.upload.rejectedType'),
  rejectedSize: t('admin.transit.upload.rejectedSize'),
  rejectedCount: t('admin.transit.upload.rejectedCount'),
  duplicate: t('admin.transit.upload.duplicate'),
  duplicateOpen: t('admin.transit.upload.duplicateOpen'),
  sessionEnded: t('admin.error.unauthorized'),
  failed: t('admin.transit.upload.failed'),
  audit: t('admin.audit.notice'),
};

const VALIDATED: FeedUploadStatus = {
  feedVersionId: '018f6b2c-1111-7000-8000-000000000001',
  status: 'validated',
  counts: { agency: 3, routes: 1858, trips: 41_220, stops: 12_431, stop_times: 512_908 },
  feedInfoVersion: 'feed-20260722',
  serviceStart: '2026-07-22',
  serviceEnd: '2027-01-31',
  warnings: ['service window < 30 days on 4 trips'],
  errorSummary: [],
};

/**
 * The screen owns the one `ToastProvider` — every activatable history row draws an
 * `ActivateForm`, and a provider each would pin a hundred viewports to the same
 * corner. Rendering under one here is what makes these tests the page's shape.
 */
function card(status: FeedUploadStatus, outgoing: string | null = 'feed-20260720') {
  return (
    <ToastProvider>
      <FeedCard
        feed={feedCardView(status, context)}
        outgoing={outgoing}
        reportHref={`/config/transit/gtfs/report/${status.feedVersionId}`}
        labels={CARD_LABELS}
      />
    </ToastProvider>
  );
}

describe('the screen sits at the path its nav item names', () => {
  it('is /config/transit/gtfs, with its three relays beside it', () => {
    const item = adminMenuManifest()
      .flatMap((group) => group.items)
      .find((entry) => entry.key === 'gtfs');

    expect(item?.path).toBe('/config/transit/gtfs');
    expect(existsSync(join(SCREEN_DIR, 'page.tsx'))).toBe(true);
    expect(existsSync(join(SCREEN_DIR, 'upload/route.ts'))).toBe(true);
    expect(existsSync(join(SCREEN_DIR, 'report/[feedVersionId]/route.ts'))).toBe(true);
    expect(existsSync(join(SCREEN_DIR, 'zip/[feedVersionId]/route.ts'))).toBe(true);
  });

  it('refuses the route to a role whose menu does not carry Transit data', () => {
    // The whole of the RBAC decision on this side: a screen is reachable iff its
    // nav item is in the menu admin-bff already filtered. transit-svc gates the
    // API on Admin/Super Admin, so nobody else is sent the item — and every
    // sub-path of the screen inherits the same gate, which is why the three
    // relays live under it.
    const withoutGtfs = [
      { key: 'onboarding', labelKey: 'nav.group.onboarding', items: [
        { key: 'verification', labelKey: 'nav.verification', path: '/verification', ownedBy: 'admin-bff' },
      ] },
    ];

    for (const path of [
      '/config/transit/gtfs',
      '/config/transit/gtfs/upload',
      '/config/transit/gtfs/zip/018f6b2c-1111-7000-8000-000000000001',
    ]) {
      expect(isReachable(withoutGtfs, path)).toBe(false);
    }

    const withGtfs = [
      { key: 'configuration', labelKey: 'nav.group.configuration', items: [
        { key: 'gtfs', labelKey: 'nav.gtfs', path: '/config/transit/gtfs', ownedBy: 'transit-svc' },
      ] },
    ];

    expect(isReachable(withGtfs, '/config/transit/gtfs/upload')).toBe(true);
  });
});

describe('the upload dropzone (US-28.1)', () => {
  it('accepts a .zip and states the 200 MB ceiling', () => {
    const { container } = render(<UploadCard screenPath="/config/transit/gtfs" labels={UPLOAD_LABELS} />);

    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    expect(input.accept).toBe('.zip');
    expect(input.multiple).toBe(false);
    expect(screen.getByText(UPLOAD_LABELS.hint)).toBeDefined();
  });

  it('says the feed is somebody else’s file and offers nothing that edits one', () => {
    render(<UploadCard screenPath="/config/transit/gtfs" labels={UPLOAD_LABELS} />);

    expect(screen.getByText(UPLOAD_LABELS.externalNote)).toBeDefined();
    expect(screen.queryByRole('button', { name: /edit|create|add route|new route/i })).toBeNull();
  });

  it('refuses a file that is not a zip before anything is sent', () => {
    const { container } = render(<UploadCard screenPath="/config/transit/gtfs" labels={UPLOAD_LABELS} />);

    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    const notAFeed = new File(['x'], 'routes.txt', { type: 'text/plain' });
    Object.defineProperty(input, 'files', { value: [notAFeed], configurable: true });
    fireEvent.change(input);

    expect(screen.getByRole('alert').textContent).toContain(UPLOAD_LABELS.rejectedType);
  });

  it('shows the duplicate error naming the version, with a link to it', async () => {
    const problem = {
      type: 'https://mageride.lk/errors/feed-duplicate',
      title: 'This GTFS file has already been uploaded',
      status: 409,
      feedVersionId: 'bbbbbbbb-0000-4000-8000-000000000002',
      feedInfoVersion: 'feed-20260720',
    };

    const { container } = render(
      <UploadCard screenPath="/config/transit/gtfs" labels={UPLOAD_LABELS} />,
    );

    withFakeXhr(409, JSON.stringify(problem), () => {
      const input = container.querySelector('input[type="file"]') as HTMLInputElement;
      const feed = new File(['zip'], 'gtfs_lk_full_0720.zip', { type: 'application/zip' });
      Object.defineProperty(input, 'files', { value: [feed], configurable: true });
      fireEvent.change(input);
    });

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('already uploaded (version feed-20260720)');

    const link = screen.getByRole('link', { name: UPLOAD_LABELS.duplicateOpen });
    expect(link.getAttribute('href')).toBe(
      '/config/transit/gtfs?feed=bbbbbbbb-0000-4000-8000-000000000002',
    );
  });
});

describe('the validation and preview card (US-28.1/28.2)', () => {
  it('draws the stepper in every state', () => {
    render(card(VALIDATED));

    const steps = screen.getByRole('list', { name: CARD_LABELS.stepperLabel });
    expect(steps.textContent).toContain(t('admin.transit.step.uploaded'));
    expect(steps.textContent).toContain(t('admin.transit.step.validated'));
  });

  it('says a feed is still being checked, and offers no Activate for it', () => {
    render(card({ ...VALIDATED, status: 'validating', counts: {}, warnings: [] }));

    expect(screen.getByText(CARD_LABELS.validatingNote)).toBeDefined();
    expect(screen.queryByRole('button', { name: ACTIVATE_LABELS.open })).toBeNull();
  });

  it('previews the per-file counts, the feed_info version and the service window', () => {
    render(card(VALIDATED));

    expect(screen.getByRole('columnheader', { name: 'Stop times' })).toBeDefined();
    expect(screen.getByText('512,908')).toBeDefined();
    // Twice on purpose: beside the status chip as the feed's name, and under
    // "Feed version" as the value read out of `feed_info.txt`.
    expect(screen.getAllByText('feed-20260722')).toHaveLength(2);
    expect(screen.getByText(/2026.*2027/)).toBeDefined();
  });

  it('collapses warnings beside Activate rather than instead of it', () => {
    render(card(VALIDATED));

    // BR-32.1: warnings never block activation.
    expect(screen.getByText(/1 warnings/)).toBeDefined();
    expect(screen.getByRole('button', { name: ACTIVATE_LABELS.open })).toBeDefined();
  });

  it('shows the first errors and the report on a failed feed, and no Activate', () => {
    render(
      card({
        ...VALIDATED,
        status: 'failed',
        warnings: [],
        errorSummary: [
          'stop_times.txt row 4102: unknown_stop_id',
          'stops.txt row 88: stop_outside_sri_lanka',
        ],
      }),
    );

    expect(screen.getByText(CARD_LABELS.failedHeading)).toBeDefined();
    expect(screen.getByText('stop_times.txt row 4102: unknown_stop_id')).toBeDefined();
    expect(screen.getByRole('link', { name: CARD_LABELS.reportCsv }).getAttribute('href')).toContain(
      'format=csv',
    );
    expect(screen.queryByRole('button', { name: ACTIVATE_LABELS.open })).toBeNull();
  });

  it('says which feed is live rather than offering to activate it again', () => {
    render(card({ ...VALIDATED, status: 'active', warnings: [] }, null));

    expect(screen.getByText(CARD_LABELS.liveNote)).toBeDefined();
    expect(screen.queryByRole('button', { name: ACTIVATE_LABELS.open })).toBeNull();
  });
});

describe('activation (US-28.2) and rollback (US-28.3)', () => {
  it('asks for confirmation and names the version being replaced', () => {
    render(card(VALIDATED));

    fireEvent.click(screen.getByRole('button', { name: ACTIVATE_LABELS.open }));

    const dialog = screen.getByRole('dialog');
    expect(dialog.textContent).toContain('feed-20260720');
    expect(dialog.textContent).toContain('feed-20260722');
    expect(dialog.textContent).toContain('will be archived');
  });

  it('says so instead when nothing is live yet (AL-55, day 0)', () => {
    render(card(VALIDATED, null));

    fireEvent.click(screen.getByRole('button', { name: ACTIVATE_LABELS.open }));

    expect(screen.getByRole('dialog').textContent).toContain('No feed is live yet');
  });

  it('promises the previous feed survives a failed swap, and names the audit row', () => {
    render(card(VALIDATED));

    fireEvent.click(screen.getByRole('button', { name: ACTIVATE_LABELS.open }));

    const dialog = screen.getByRole('dialog');
    expect(dialog.textContent).toContain('the feed that is live now stays live');
    expect(dialog.textContent).toContain(ACTIVATE_LABELS.audit);
  });

  it('re-activating an archived version is the same flow, said to be a rollback', () => {
    render(card({ ...VALIDATED, status: 'archived', warnings: [] }));

    fireEvent.click(screen.getByRole('button', { name: ACTIVATE_LABELS.reactivate }));

    const dialog = screen.getByRole('dialog');
    expect(dialog.textContent).toContain(ACTIVATE_LABELS.rollbackNote);
    expect(dialog.textContent).toContain('feed-20260720');
  });

  it('carries the version id on the form, so the confirm posts what the dialog named', () => {
    render(
      <ToastProvider>
        <ActivateForm
          feedVersionId="018f6b2c-1111-7000-8000-000000000001"
          incoming="feed-20260722"
          outgoing="feed-20260720"
          rollback={false}
          labels={ACTIVATE_LABELS}
        />
      </ToastProvider>,
    );

    fireEvent.click(screen.getByRole('button', { name: ACTIVATE_LABELS.open }));

    // A hidden field rather than a closure: the confirm is a real submit into a
    // server action, so the id has to be on the form the operator pressed.
    const hidden = document.querySelector('input[name="feedVersionId"]') as HTMLInputElement;
    expect(hidden.value).toBe('018f6b2c-1111-7000-8000-000000000001');
    expect(activate).not.toHaveBeenCalled();
  });
});

describe('the version history (US-28.3)', () => {
  const rows = versionRows(
    [
      {
        feedVersionId: 'aaaaaaaa-0000-4000-8000-000000000001',
        feedInfoVersion: 'feed-20260722',
        fileName: 'gtfs_lk_full_0722.zip',
        uploadedBy: '9f1a0b3c-2d4e-4f60-8a12-3b4c5d6e7f80',
        uploadedAt: '2026-07-22T03:10:00Z',
        counts: { routes: 1858 },
        status: 'validated',
      },
      {
        feedVersionId: 'bbbbbbbb-0000-4000-8000-000000000002',
        feedInfoVersion: 'feed-20260720',
        fileName: 'gtfs_lk_full_0720.zip',
        uploadedBy: '9f1a0b3c-2d4e-4f60-8a12-3b4c5d6e7f80',
        uploadedAt: '2026-07-20T03:10:00Z',
        counts: { routes: 1842 },
        status: 'active',
      },
      {
        feedVersionId: 'cccccccc-0000-4000-8000-000000000003',
        feedInfoVersion: 'feed-20260718',
        fileName: 'gtfs_lk_full_0718.zip',
        uploadedBy: '9f1a0b3c-2d4e-4f60-8a12-3b4c5d6e7f80',
        uploadedAt: '2026-07-18T03:10:00Z',
        counts: { routes: 1842 },
        status: 'archived',
      },
      {
        feedVersionId: 'dddddddd-0000-4000-8000-000000000004',
        feedInfoVersion: null,
        fileName: 'gtfs_lk_draft.zip',
        uploadedBy: '9f1a0b3c-2d4e-4f60-8a12-3b4c5d6e7f80',
        uploadedAt: '2026-07-17T03:10:00Z',
        status: 'failed',
      },
    ],
    'aaaaaaaa-0000-4000-8000-000000000001',
    context,
  );

  function history(capped = false) {
    return (
      <ToastProvider>
        <VersionHistory
          rows={rows}
          outgoing="feed-20260720"
          screenPath="/config/transit/gtfs"
          reportPath="/config/transit/gtfs/report"
          zipPath="/config/transit/gtfs/zip"
          capped={capped}
          labels={HISTORY_LABELS}
        />
      </ToastProvider>
    );
  }

  it('draws the wireframe’s six columns', () => {
    render(history());

    for (const column of [
      HISTORY_LABELS.version,
      HISTORY_LABELS.file,
      HISTORY_LABELS.uploaded,
      HISTORY_LABELS.routes,
      HISTORY_LABELS.status,
      HISTORY_LABELS.actions,
    ]) {
      expect(screen.getByRole('columnheader', { name: column })).toBeDefined();
    }
  });

  it('offers Re-activate on the archived row and on no other', () => {
    render(history());

    // Validated → Activate, Archived → Re-activate, Active and Failed → neither.
    expect(screen.getAllByRole('button', { name: HISTORY_LABELS.activate.open })).toHaveLength(1);
    expect(
      screen.getAllByRole('button', { name: HISTORY_LABELS.activate.reactivate }),
    ).toHaveLength(1);
  });

  it('offers the report and the zip on every row, including a failed one', () => {
    render(history());

    expect(screen.getAllByRole('link', { name: HISTORY_LABELS.report })).toHaveLength(4);
    expect(screen.getAllByRole('link', { name: HISTORY_LABELS.zip })).toHaveLength(4);
    expect(
      screen.getAllByRole('link', { name: HISTORY_LABELS.zip })[3]!.getAttribute('href'),
    ).toBe('/config/transit/gtfs/zip/dddddddd-0000-4000-8000-000000000004');
  });

  it('names an unnamed feed by its id rather than inventing a version number', () => {
    render(history());

    expect(
      screen.getByRole('link', { name: 'dddddddd-0000-4000-8000-000000000004' }),
    ).toBeDefined();
  });

  it('says when it is showing only the most recent page', () => {
    render(history(true));
    expect(screen.getByText(HISTORY_LABELS.capped)).toBeDefined();
  });

  it('says so when nothing has ever been uploaded', () => {
    render(
      <ToastProvider>
        <VersionHistory
          rows={[]}
          outgoing={null}
          screenPath="/config/transit/gtfs"
          reportPath="/config/transit/gtfs/report"
          zipPath="/config/transit/gtfs/zip"
          capped={false}
          labels={HISTORY_LABELS}
        />
      </ToastProvider>,
    );

    expect(screen.getByText(HISTORY_LABELS.empty)).toBeDefined();
  });
});

describe('AL-56 — this screen ingests a feed and cannot author one', () => {
  it('names no route that could write GTFS content', () => {
    // The only `/v1/**` paths in the group are the six-operation lifecycle, and
    // the two literals are already enumerated in `test/fences.test.ts`. What is
    // asserted here is the *absence* of anything shaped like an editor: no
    // `gtfs-import` (superseded, and never the operator path), no route or stop
    // writes.
    for (const file of walk(join(APP_ROOT, 'src/components/transit'))) {
      const source = readFileSync(file, 'utf8');
      expect(source, `${file} names the superseded import route`).not.toContain('gtfs-import');
    }

    const api = readFileSync(join(APP_ROOT, 'src/api/transit.ts'), 'utf8');
    expect(api).not.toContain('gtfs-import');
    expect(api).not.toMatch(/\/v1\/admin\/transit\/gtfs\/(routes|stops|trips)/);
  });
});

/* ------------------------------------------------------------------------- */

function walk(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = join(dir, entry.name);
    return entry.isDirectory() ? walk(full) : [full];
  });
}

/**
 * A stand-in for `XMLHttpRequest` that answers immediately.
 *
 * The upload is the one thing on this screen that is not `fetch` — `fetch` cannot
 * report upload progress — so exercising the duplicate path means standing in for
 * the transport it does use.
 */
function withFakeXhr(status: number, responseText: string, act: () => void): void {
  class FakeXhr {
    status = 0;
    responseText = '';
    responseType = '';
    readonly upload = { addEventListener: () => {} };
    private readonly listeners = new Map<string, () => void>();

    open() {}

    addEventListener(type: string, listener: () => void) {
      this.listeners.set(type, listener);
    }

    send() {
      this.status = status;
      this.responseText = responseText;
      this.listeners.get('load')?.();
    }

    abort() {}
  }

  const original = globalThis.XMLHttpRequest;
  globalThis.XMLHttpRequest = FakeXhr as unknown as typeof XMLHttpRequest;
  try {
    act();
  } finally {
    globalThis.XMLHttpRequest = original;
  }
}
