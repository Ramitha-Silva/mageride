/**
 * The D-35 vocabulary, as the portal's side of it.
 *
 * Transcribed from `MageRide.AdminBff.Auditing.AdminAuditActions`
 * (`backend/src/AdminBff/Auditing/AdminAuditActions.cs`), which is the writer —
 * and, **Δ C110**, from `MageRide.Transit.Gtfs.GtfsAuditActions`, which is the
 * other one. `test/audit.test.ts` parses both files and asserts they agree with
 * the unions below — an action added there and missing here is a build failure,
 * not a screen that names a row nobody writes.
 *
 * **Why the portal knows these at all.** D-35 makes every admin-bff mutation write
 * an `audit.events` row, and the interceptor *throws* on a mutating 2xx that
 * recorded nothing — so on this surface "did this click get written down" has
 * exactly one answer, and it is yes. The portal's job is therefore not to record
 * anything: it is to be able to **tell the operator, before they press the button,
 * which row their name is about to appear on**. A confirm dialog that says "this
 * will be recorded in the audit trail" and cannot name the action is a dialog that
 * is guessing.
 */

/** Every `audit.events.action` an Admin Portal screen can cause. */
export type AdminAuditAction =
  // Moderation
  | 'VEHICLE_SUSPENDED'
  | 'DRIVER_SUSPENDED'
  | 'REPORT_CONFIRMED'
  | 'REPORT_DISMISSED'
  | 'TICKET_RESOLVED'
  // Verification (AL-39)
  | 'DOC_VIEW'
  | 'VERIFICATION_FIELD_CONFIRMED'
  | 'VERIFICATION_APPROVED'
  | 'VERIFICATION_REJECTED'
  | 'VERIFICATION_REOPENED'
  | 'PAYOUT_PROFILE_APPROVED'
  | 'PAYOUT_PROFILE_REJECTED'
  // Directories (AL-40/41/42)
  | 'PII_READ'
  // Finance (E-05, US-14.11)
  | 'WALLET_FEE_REVERSED'
  | 'REFUND_ISSUED'
  // Data rights (E-06)
  | 'PDPA_REQUESTED'
  | 'PDPA_FULFILLED'
  | 'PDPA_REJECTED'
  // Configuration
  | 'TARIFFS_PUBLISHED'
  | 'CITY_CREATED'
  | 'CITY_UPDATED'
  | 'FEATURE_FLAG_SET'
  | 'TRAIN_CREATED'
  | 'TRAIN_UPDATED'
  | 'TRAIN_RETIRED'
  | 'ANNOUNCEMENT_PUBLISHED'
  | 'GTFS_PROXIED';

/**
 * The rows **transit-svc** writes for SCR-AP-016 (Δ C110), which are the ones an
 * auditor will actually find.
 *
 * `gateway-routes.json` matches `/v1/admin/transit/**` at **Order 20** and sends
 * it to transit-svc, ahead of admin-bff's Order 90 catch-all — so on the deployed
 * topology admin-bff never sees a GTFS call and `GTFS_PROXIED` is never written.
 * What *is* written, in both topologies, is the dataset-level row transit-svc
 * commits inside the same transaction as the change (`GtfsAuditActions`).
 *
 * **This is emphatically not the C108 case.** There, four prefixes reach their
 * owning service and leave **nothing** in `audit.events`, so those calls declare
 * {@link AuditedElsewhere} and the console says a row will not exist. Here a row
 * always exists; it is simply written by the service that owns the tables the
 * swap renames. Declaring `auditedElsewhere: 'transit-svc'` would tell an operator
 * their activation is unrecorded, which is the opposite of true — and declaring
 * `GTFS_PROXIED` would name a row that, in production, nobody can find.
 *
 * `GTFS_FEED_VALIDATED` is here for completeness and is **actor-less by
 * construction**: a queued job reaches that verdict, not a person, and no screen
 * declares it. It is in the union for the same reason `DOC_VIEW` and `PII_READ`
 * are in admin-bff's — the vocabulary is what the log can contain, not what a
 * button can cause.
 */
export type TransitAuditAction =
  | 'GTFS_FEED_UPLOADED'
  | 'GTFS_FEED_VALIDATED'
  | 'GTFS_FEED_ACTIVATED';

/** Every `audit.events.action` this console can name, whichever service writes it. */
export type AuditAction = AdminAuditAction | TransitAuditAction;

/** `audit.events.entity_type` — which kind of record the action was about. */
export type AdminAuditEntity =
  | 'vehicle'
  | 'driver'
  | 'passenger'
  | 'fleet_org'
  | 'document'
  | 'driver_payout_profile'
  | 'driver_wallet'
  | 'ride_payment'
  | 'pdpa_request'
  | 'vehicle_report'
  | 'support_ticket'
  | 'fare_tariff'
  | 'operating_city'
  | 'feature_flag'
  | 'broadcast'
  | 'gtfs_feed';

/**
 * What a mutation declares about the row it is going to cause.
 *
 * `entityId` is optional because two of the actions have no id until the server
 * has minted one (`CITY_CREATED`, `TRAIN_CREATED`); everything else names the
 * record it is about, which is what a confirm dialog puts in front of the operator.
 */
export interface AuditIntent {
  readonly action: AuditAction;
  readonly entity: AdminAuditEntity;
  readonly entityId?: string;
}

/**
 * The services whose `/v1/admin/**` routes the gateway sends **past** admin-bff,
 * so that no `audit.events` row is written for them (Δ C108).
 *
 * `gateway-routes.json` matches `/v1/admin/fees/**`,
 * `/v1/admin/voucher-discount-tiers`, `/v1/admin/drivers/level-config` and
 * `/v1/admin/rbac/**` at **Order 20** — ahead of the Order 90 admin-bff
 * catch-all — and admin-bff maps no route of its own onto any of the four. The
 * GTFS proxy is the shape that *does* work and says why in its own remark: it is
 * shadowed by an Order 20 route, but a copy exists here, so "the RBAC matrix and
 * the audit guard cover the path either way". These four have no such copy.
 *
 * So on the deployed topology a daily-fee rate change, a voucher-tier change, a
 * Driver-Level change and every role grant reach their owning service with the
 * operator's bearer and leave **nothing in the immutable log** — against D-35 and
 * against US-21.14 ("permission changes … are themselves audited"). Raised as a
 * micro-change-set in the C108 handoff.
 */
export type AuditedElsewhereService = 'subscription-svc' | 'dispatch-svc' | 'iam-svc';

/**
 * A mutation that will **not** produce a D-35 row, and the service that answers
 * it instead.
 *
 * Declared rather than omitted. `mutate()` could have taken an optional
 * `AuditIntent` and let these four pass `undefined`, but then "this screen forgot
 * to declare its row" and "this route writes no row" would be the same value —
 * and the operator-facing consequence is the opposite in each case. Naming the
 * service is what lets {@link AuditNotice} tell the truth: an Auditor who is told
 * a change is in the trail and then cannot find it has been misled by the console
 * about the one thing the console exists to be trusted on.
 */
export interface AuditedElsewhere {
  readonly auditedElsewhere: AuditedElsewhereService;
}

/** What a call to `mutate()` says about the audit row it does or does not cause. */
export type MutationAudit = AuditIntent | AuditedElsewhere;

/** Whether a declaration is a D-35 row admin-bff's interceptor will write. */
export function isAuditIntent(audit: MutationAudit): audit is AuditIntent {
  return 'action' in audit;
}
