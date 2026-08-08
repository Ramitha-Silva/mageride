import { describe, expect, it } from 'vitest';

import {
  duplicateFeed,
  feedLabel,
  isActivatable,
  isFeedVersionId,
  isPending,
  isRollback,
  MAX_FEED_BYTES,
  type FeedStatus,
  type FeedUploadStatus,
  type FeedVersion,
} from '@/api/transit';
import { problemMessageKey, type ProblemDetails } from '@/api/problem';
import {
  activeVersion,
  countRows,
  feedCardView,
  routeCount,
  selectedFeedId,
  statusTone,
  stepperSteps,
  versionRow,
  versionRows,
} from '@/components/transit/model';
import { createAdminTranslator } from '@/i18n';

/**
 * SCR-AP-016's lifecycle rules, as arithmetic over the wire shapes.
 *
 * Everything here is a property of the *contract* rather than of the markup: which
 * statuses can be activated, which keep the poll running, what the counts grid
 * does with a file the feed omitted, and how a `409 feed-duplicate` names the
 * version the operator should go and look at.
 */

const t = createAdminTranslator('en');
const context = { t, locale: 'en' } as const;

const ALL_STATUSES: readonly FeedStatus[] = [
  'uploaded',
  'validating',
  'validated',
  'failed',
  'active',
  'archived',
];

function version(overrides: Partial<FeedVersion> & { feedVersionId: string }): FeedVersion {
  return {
    feedInfoVersion: null,
    fileName: 'gtfs_lk_full.zip',
    uploadedBy: '9f1a0b3c-2d4e-4f60-8a12-3b4c5d6e7f80',
    uploadedAt: '2026-07-22T03:10:00Z',
    status: 'validated',
    ...overrides,
  };
}

describe('the six statuses and what each one allows', () => {
  it('is 200 MB, which is BR-32.1’s ceiling to the byte', () => {
    expect(MAX_FEED_BYTES).toBe(209_715_200);
  });

  it('keeps polling only while a verdict is outstanding', () => {
    expect(ALL_STATUSES.filter(isPending)).toEqual(['uploaded', 'validating']);
  });

  it('offers activation on exactly validated and archived', () => {
    // Archived is BR-32.3's rollback and is the *same* call. `failed` is the one
    // that matters most: "failed feeds can never be activated".
    expect(ALL_STATUSES.filter(isActivatable)).toEqual(['validated', 'archived']);
    expect(isActivatable('failed')).toBe(false);
    expect(isActivatable('active')).toBe(false);
  });

  it('calls only an archived activation a rollback', () => {
    expect(ALL_STATUSES.filter(isRollback)).toEqual(['archived']);
  });

  it('reserves the live tone for the one feed that is answering passengers', () => {
    expect(statusTone('active')).toBe('success');
    expect(statusTone('validated')).toBe('info');
    expect(statusTone('failed')).toBe('error');
  });
});

describe('the stepper', () => {
  it('draws one outcome position, not a Validated box beside a Failed one', () => {
    const steps = stepperSteps('failed', context);

    expect(steps.map((step) => step.id)).toEqual(['uploaded', 'validating', 'outcome']);
    expect(steps[2]!.label).toBe(t('admin.transit.step.failed'));
    expect(steps[2]!.failed).toBe(true);
  });

  it('marks the verdict outstanding while a feed is still being checked', () => {
    const steps = stepperSteps('validating', context);

    expect(steps[1]!.state).toBe('current');
    expect(steps[2]!.state).toBe('todo');
  });

  it('completes the whole rail for a feed that has been live', () => {
    for (const status of ['validated', 'active', 'archived'] as const) {
      expect(stepperSteps(status, context).every((step) => step.state === 'done')).toBe(true);
    }
  });
});

describe('the counts grid', () => {
  it('renders D2’s six files in D2’s order', () => {
    const rows = countRows(
      { stops: 12_431, agency: 3, stop_times: 512_908, routes: 1858, shapes: 1795, trips: 41_220 },
      context,
    );

    expect(rows.map((row) => row.key)).toEqual([
      'agency',
      'routes',
      'trips',
      'stops',
      'stop_times',
      'shapes',
    ]);
    expect(rows[4]!.label).toBe('Stop times');
    expect(rows[1]!.value).toBe('1,858');
  });

  it('omits a file the feed does not carry rather than printing zero', () => {
    // `shapes.txt` is optional. A `0` there reads as an empty file, which is a
    // defect; an absent column reads as an absent file, which is not.
    const rows = countRows({ agency: 3, routes: 12 }, context);

    expect(rows.map((row) => row.key)).toEqual(['agency', 'routes']);
  });

  it('labels a counted file nobody translated with its own GTFS name', () => {
    const rows = countRows({ pathways: 4 }, context);

    expect(rows[0]!.label).toBe('pathways.txt');
  });

  it('reads the route count the topbar pill and the history column print', () => {
    expect(routeCount({ routes: 1842 }, context)).toBe('1,842');
    expect(routeCount(undefined, context)).toBeNull();
    expect(routeCount({}, context)).toBeNull();
  });
});

describe('the preview card’s view', () => {
  const status: FeedUploadStatus = {
    feedVersionId: '018f6b2c-1111-7000-8000-000000000001',
    status: 'validated',
    counts: { agency: 3, routes: 1858 },
    feedInfoVersion: 'feed-20260722',
    serviceStart: '2026-07-22',
    serviceEnd: '2027-01-31',
    warnings: ['service window < 30 days on 4 trips', '12 stops renamed vs active feed'],
    errorSummary: [],
  };

  it('names the service window from both ends and nothing from one', () => {
    expect(feedCardView(status, context).serviceWindow).toContain('2026');
    expect(feedCardView({ ...status, serviceEnd: null }, context).serviceWindow).toBeNull();
  });

  it('counts warnings and says they do not block activation', () => {
    const view = feedCardView(status, context);

    expect(view.warningsSummary).toContain('2');
    expect(view.warningsSummary).toContain('do not stop activation');
    expect(view.activatable).toBe(true);
  });

  it('falls back to the id when the feed carries no feed_info version', () => {
    const view = feedCardView({ ...status, feedInfoVersion: null }, context);

    expect(view.feedInfoVersion).toBeNull();
    expect(view.label).toBe(status.feedVersionId);
  });
});

describe('which version the screen is showing', () => {
  const versions = [
    version({ feedVersionId: 'aaaaaaaa-0000-4000-8000-000000000001', status: 'validated' }),
    version({ feedVersionId: 'bbbbbbbb-0000-4000-8000-000000000002', status: 'active' }),
    version({ feedVersionId: 'cccccccc-0000-4000-8000-000000000003', status: 'archived' }),
  ];

  it('defaults to the most recent upload, whatever its status', () => {
    expect(selectedFeedId(versions, undefined)).toBe(versions[0]!.feedVersionId);
  });

  it('honours ?feed= even for a version this page of history does not carry', () => {
    // The status read is by id, so a version older than the page still renders.
    const requested = 'dddddddd-0000-4000-8000-000000000009';

    expect(selectedFeedId(versions, requested)).toBe(requested);
    expect(versionRow(versions, requested)).toBeNull();
  });

  it('has nothing to show on the day-0 empty state', () => {
    expect(selectedFeedId([], undefined)).toBeNull();
    expect(activeVersion([])).toBeNull();
  });

  it('finds the one live feed, which is what every confirm dialog has to name', () => {
    expect(activeVersion(versions)?.feedVersionId).toBe(versions[1]!.feedVersionId);
  });
});

describe('the history rows', () => {
  it('marks the selected row and offers re-activation only where it can work', () => {
    const versions = [
      version({ feedVersionId: 'aaaaaaaa-0000-4000-8000-000000000001', status: 'failed' }),
      version({ feedVersionId: 'bbbbbbbb-0000-4000-8000-000000000002', status: 'active' }),
      version({ feedVersionId: 'cccccccc-0000-4000-8000-000000000003', status: 'archived' }),
    ];

    const rows = versionRows(versions, versions[2]!.feedVersionId, context);

    expect(rows.map((row) => row.activatable)).toEqual([false, false, true]);
    expect(rows[2]!.rollback).toBe(true);
    expect(rows.map((row) => row.selected)).toEqual([false, false, true]);
  });

  it('shows the uploader as the id, because nothing resolves an internal account', () => {
    const rows = versionRows([version({ feedVersionId: 'aaaaaaaa-0000-4000-8000-000000000001' })], null, context);

    expect(rows[0]!.uploadedBy).toBe('9f1a0b3c-2d4e-4f60-8a12-3b4c5d6e7f80');
  });
});

describe('a duplicate upload', () => {
  /**
   * `409 feed-duplicate` as transit-svc writes it.
   *
   * The cast is the point of the test: RFC 7807 extensions are per-error and
   * `ProblemDetails` deliberately enumerates none of them, so `duplicateFeed` has
   * to narrow what arrives rather than read a typed field.
   */
  const duplicate = (extra: Readonly<Record<string, unknown>> = {}): ProblemDetails =>
    ({
      type: 'https://mageride.lk/errors/feed-duplicate',
      title: 'This GTFS file has already been uploaded',
      status: 409,
      detail: "This exact file was already uploaded on 2026-07-20 as 'gtfs_lk_full_0720.zip'.",
      feedVersionId: 'bbbbbbbb-0000-4000-8000-000000000002',
      feedInfoVersion: 'feed-20260720',
      ...extra,
    }) as ProblemDetails;

  it('names the version that already holds those bytes', () => {
    expect(duplicateFeed(duplicate())).toEqual({
      feedVersionId: 'bbbbbbbb-0000-4000-8000-000000000002',
      feedInfoVersion: 'feed-20260720',
    });
  });

  it('falls back to the id for a duplicate that never carried a feed_info version', () => {
    expect(duplicateFeed(duplicate({ feedInfoVersion: null }))?.feedInfoVersion).toBeNull();
  });

  it('is null for any other refusal, so no message says "(version undefined)"', () => {
    expect(duplicateFeed(duplicate({ feedVersionId: 'not-an-id' }))).toBeNull();
    expect(
      duplicateFeed({ type: 'https://mageride.lk/errors/conflict', title: 'x', status: 409 }),
    ).toBeNull();
  });

  it('has its own sentence, and so do the two activation refusals', () => {
    // The generic conflict copy — "someone changed this first, reload" — is wrong
    // for all three: nothing changed, and reloading fixes none of them.
    const sentence = (code: string) =>
      t(problemMessageKey({ type: `https://mageride.lk/errors/${code}`, title: '', status: 409 }));

    expect(sentence('feed-duplicate')).not.toBe(sentence('conflict'));
    expect(sentence('feed-not-validated')).toContain('cannot be made live');
    expect(sentence('feed-already-active')).toContain('already');
  });
});

describe('an id is checked before it reaches a path this process builds', () => {
  it('accepts a uuid and nothing else', () => {
    expect(isFeedVersionId('018f6b2c-1111-7000-8000-000000000001')).toBe(true);
    expect(isFeedVersionId('../versions')).toBe(false);
    expect(isFeedVersionId('')).toBe(false);
    expect(isFeedVersionId(undefined)).toBe(false);
  });
});

describe('how a feed is named', () => {
  it('prefers the feed_info version an operator recognises', () => {
    expect(feedLabel({ feedVersionId: 'id', feedInfoVersion: 'feed-20260722' })).toBe(
      'feed-20260722',
    );
    expect(feedLabel({ feedVersionId: 'id', feedInfoVersion: '  ' })).toBe('id');
  });
});
