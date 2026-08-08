/**
 * The D-35 vocabulary, as the portal's side of it.
 *
 * Transcribed from `MageRide.AdminBff.Auditing.AdminAuditActions`
 * (`backend/src/AdminBff/Auditing/AdminAuditActions.cs`), which is the writer.
 * `test/audit.test.ts` parses that file and asserts the two agree — an action
 * added there and missing here is a build failure, not a screen that names a row
 * nobody writes.
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
  readonly action: AdminAuditAction;
  readonly entity: AdminAuditEntity;
  readonly entityId?: string;
}
