/**
 * SCR-AP-010…015's wire shapes, the three paths behind them, and the state each
 * of the six screens keeps in its URL.
 *
 * Transcribed from `backend/contracts/admin-bff.yaml` — the AL-40/41/42 block:
 * `searchPassengers`, `getPassengerDetail`, `searchDrivers`, `getDriverDetail`,
 * `searchVehicles` and `getVehicleDetail`. C064 owns the joins behind them;
 * admin-bff is the RBAC-gated, audited front door.
 *
 * ## Six GETs and nothing else
 *
 * BR-28.8: "All are read-only — refunds route to Finance and wallet reversals stay
 * Finance-only." There is no `mutate()` in this screen group and there must not be
 * one. What the details carry instead are **hand-offs**: a link to the reversal
 * form on SCR-AP-006, a link to the suspension card on SCR-AP-004, a link to the
 * verification subject on SCR-AP-003a — each drawn only when the caller's own menu
 * carries the screen it points at (`AlertsCard`'s rule, AL-06).
 *
 * ## The two reads that are themselves the auditable act
 *
 * `GET …/{id}` on all three directories emits **`PII_READ`** — "exactly one per
 * open, carrying whether the contact details were actually revealed". The portal
 * does not declare it the way `mutate()` declares a D-35 row, because it does not
 * cause it: opening the screen *is* the event, and admin-bff's `.Audited(PiiRead,
 * …)` writes it once the response is known to be a success. What this side owes
 * the operator is that the screen says so before they open it and while they are
 * reading it — `admin.directory.piiNotice`.
 *
 * ## Masking is server-side and the portal cannot undo it
 *
 * A **list** row is never a clear MSISDN — `PhoneMasked` for every caller,
 * whatever they hold, which is what makes "every clear number this surface has
 * emitted has a `PII_READ` row behind it" true rather than approximate. A
 * **detail** carries `MaskablePhone`: the clear number for a caller holding URD
 * §2.3's End-user account management row as write-unscoped, the `+9477*****67`
 * form for everybody else. Both arrive as a plain string and the portal renders
 * what it was given — there is no branch here on which one it is, because a portal
 * that could tell would be a portal that had been sent both.
 *
 * ## An id the operator typed is checked before it is sent
 *
 * `?id=` is `Guid.TryParse` in `DirectoryEndpoints.Identifier` and anything else is
 * a `400` naming the field. So a malformed id **asks admin-bff nothing** and the
 * screen says which box is wrong — `StatsSelection.awaitingRange`'s rule (C105):
 * sending an incomplete query answers the operator's first press with a validation
 * error about a form they are still filling in.
 */

import { isAdminId } from './moderation';
import type { CursorPage } from './types';
import type { DocumentRef } from './verification';

export type { CursorPage, DocumentRef };

/** `GET /v1/admin/passengers` · `GET …/{passengerId}` (AL-40, SCR-AP-010/011). */
export const PASSENGERS_PATH = '/v1/admin/passengers';

/** `GET /v1/admin/drivers` · `GET …/{driverId}` (AL-41, SCR-AP-012/013). */
export const DRIVERS_PATH = '/v1/admin/drivers';

/** `GET /v1/admin/vehicles` · `GET …/{vehicleId}` (AL-42, SCR-AP-014/015). */
export const VEHICLES_PATH = '/v1/admin/vehicles';

/**
 * One page of results, and it is a page rather than a cap.
 *
 * SCR-AP-010 says "results list is paginated" in as many words, so unlike the
 * verification queues — which are a backlog worked with a search box and no pager
 * — a directory follows the cursor. Fifty rather than the contract's maximum
 * hundred: a plate somebody half-remembers is refined, not scrolled, and a shorter
 * page is a faster first answer.
 */
export const DIRECTORY_PAGE_SIZE = 50;

export function passengerPath(passengerId: string): string {
  return `${PASSENGERS_PATH}/${passengerId}`;
}

export function driverPath(driverId: string): string {
  return `${DRIVERS_PATH}/${driverId}`;
}

export function vehiclePath(vehicleId: string): string {
  return `${VEHICLES_PATH}/${vehicleId}`;
}

/** Whether a path segment or a typed criterion is an id this portal will send. */
export { isAdminId as isDirectoryId };

// ---------------------------------------------------------------------------
// Vocabularies — every closed enum the three searches filter on
// ---------------------------------------------------------------------------

/** `_shared.yaml#/components/schemas/VehicleType` — the ten canonical types (AL-09). */
export const VEHICLE_TYPES = [
  'motorbike',
  'three_wheeler',
  'flex',
  'sedan',
  'mini_van',
  'van',
  'truck',
  'mini_truck',
  'bus',
  'train',
] as const;

export type VehicleType = (typeof VEHICLE_TYPES)[number];

/** `_shared.yaml#/components/schemas/OperatingMode`. */
export const OPERATING_MODES = ['A', 'B', 'C'] as const;

export type OperatingMode = (typeof OPERATING_MODES)[number];

/** `registry.vehicles.registration_status` — the four `searchVehicles` admits. */
export const REGISTRATION_STATUSES = ['PENDING', 'APPROVED', 'REJECTED', 'DEACTIVATED'] as const;

export type RegistrationStatus = (typeof REGISTRATION_STATUSES)[number];

/**
 * `searchDrivers`' `status`, **defaulting to `verified`** (US-24.10).
 *
 * The directory is for the people currently driving; an operator who wants an
 * applicant asks for one. `all` is the fourth value and is how the default is
 * lifted — there is no "no filter" here, because the absent parameter *is* the
 * default.
 */
export const DRIVER_STATUSES = ['verified', 'pending', 'suspended', 'all'] as const;

export type DriverStatus = (typeof DRIVER_STATUSES)[number];

export const DEFAULT_DRIVER_STATUS: DriverStatus = 'verified';

/**
 * The Driver Levels the platform has.
 *
 * **Three, not the wireframe's five.** `dispatch.driver_levels.level` is 1–3,
 * `searchDrivers` bounds `level` to `[1, 3]` and answers `400` outside it, and D-14
 * describes three bands. SCR-AP-012's "L1–L5 ▾" is a wireframe deviation recorded
 * in the C109 handoff; a fourth option here would be a filter admin-bff refuses.
 */
export const DRIVER_LEVELS = [1, 2, 3] as const;

export type DriverLevel = (typeof DRIVER_LEVELS)[number];

function isOneOf<T extends string>(values: readonly T[], value: string | undefined): value is T {
  return value !== undefined && (values as readonly string[]).includes(value);
}

export const isVehicleType = (value: string | undefined): value is VehicleType =>
  isOneOf(VEHICLE_TYPES, value);

export const isOperatingMode = (value: string | undefined): value is OperatingMode =>
  isOneOf(OPERATING_MODES, value);

export const isRegistrationStatus = (value: string | undefined): value is RegistrationStatus =>
  isOneOf(REGISTRATION_STATUSES, value);

export const isDriverStatus = (value: string | undefined): value is DriverStatus =>
  isOneOf(DRIVER_STATUSES, value);

export function isDriverLevel(value: number | undefined): value is DriverLevel {
  return value !== undefined && (DRIVER_LEVELS as readonly number[]).includes(value);
}

// ---------------------------------------------------------------------------
// Shared row shapes
// ---------------------------------------------------------------------------

/**
 * `DirectoryTrip` — one journey, from either surface.
 *
 * `kind: ride` is a Mode C booking (`rides.rides`, R-01) and `kind: session` is a
 * Mode A/B journey (`trips.sessions`, D-03). Both appear because a directory is not
 * mode-aware: the ride-svc / trip-state-svc boundary is about who writes the row,
 * not about what an operator may read back. **A session carries no fare.**
 */
export interface DirectoryTrip {
  readonly tripId: string;
  readonly kind: 'ride' | 'session';
  readonly state: string;
  readonly vehicleType?: VehicleType;
  readonly vehicleId?: string;
  readonly regNo?: string;
  readonly counterpartyId?: string;
  /** The driver on a passenger's tab, the passenger on a driver's. */
  readonly counterpartyName?: string;
  readonly fareMinor?: number;
  readonly currency?: string;
  readonly startedAt: string;
  readonly endedAt?: string;
}

/** `DirectoryPayment` — one attempt of D-10's state machine; retries are their own rows. */
export interface DirectoryPayment {
  readonly paymentId: string;
  readonly rideId: string;
  readonly method: 'cash' | 'lankaqr' | 'onepay' | 'cod' | 'scan_driver_qr' | 'wallet';
  readonly state: string;
  readonly amountMinor: number;
  readonly surchargeMinor?: number;
  readonly tipMinor?: number;
  readonly currency: string;
  readonly attemptNo: number;
  readonly createdAt: string;
}

/** `DirectoryPackage` — a `kind = 2` ride (P-06). The recipient's number masks like the passenger's. */
export interface DirectoryPackage {
  readonly rideId: string;
  readonly state: string;
  readonly packageSize?: 'S' | 'M' | 'L';
  readonly description?: string;
  readonly recipientName?: string;
  readonly recipientMobile?: string;
  readonly fareMinor?: number;
  readonly currency?: string;
  readonly createdAt: string;
  readonly completedAt?: string;
}

/**
 * `DirectoryDispute` — one `support.tickets` row.
 *
 * The whole ticket list rather than a "disputes" subset: `category` is free text
 * and US-9.23's daily-fee refund request rides in it, so a server-side filter on a
 * vocabulary nobody has fixed would silently hide tickets.
 */
export interface DirectoryDispute {
  readonly ticketId: string;
  readonly category: string;
  readonly status: 'OPEN' | 'IN_PROGRESS' | 'RESOLVED';
  readonly description?: string;
  readonly response?: string;
  readonly rideId?: string;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/** `WalletLedgerEntry` — one `billing.wallet_transactions` row (D-09 §10). Signed: a debit is negative. */
export interface WalletLedgerEntry {
  readonly entryNo: number;
  readonly kind: string;
  readonly amountMinor: number;
  readonly balanceAfterMinor: number;
  readonly description?: string;
  readonly ts: string;
}

/** `DailyFeeCharge` — one D-13 charge. `feeDate` is the Asia/Colombo business date (D-38). */
export interface DailyFeeCharge {
  readonly feeDate: string;
  readonly driverId: string;
  readonly vehicleId: string;
  readonly regNo?: string;
  readonly amountMinor: number;
  readonly currency: string;
  readonly tripsThatDay: number;
  readonly status: 'PAID' | 'WAIVED_FIRST_TRIP';
  readonly chargedAt: string;
}

/**
 * `DirectoryCreditTransfer` — one `billing.credit_transfers` row (US-9.13/9.21).
 *
 * Exact value, no commission (AL-01). `direction` is computed against the driver
 * whose detail this is; `initiation` is the stored column — who started it — and
 * the two answer different questions.
 */
export interface DirectoryCreditTransfer {
  readonly transferId: string;
  readonly direction: 'in' | 'out';
  readonly initiation: 'REQUESTED' | 'DIRECT';
  readonly counterpartyId: string;
  readonly counterpartyName?: string;
  readonly amountMinor: number;
  readonly currency: string;
  readonly status: 'PENDING' | 'APPROVED' | 'REJECTED';
  readonly createdAt: string;
}

/** `DirectoryVehicleReport` — one `safety.vehicle_reports` row. The third CONFIRMED one delists (US-12.6). */
export interface DirectoryVehicleReport {
  readonly reportId: string;
  readonly vehicleId: string;
  readonly regNo?: string;
  readonly reason: string;
  readonly status: 'PENDING' | 'CONFIRMED' | 'DISMISSED';
  readonly createdAt: string;
}

// ---------------------------------------------------------------------------
// Passengers (AL-40)
// ---------------------------------------------------------------------------

/** `PassengerRow` — one row of SCR-AP-010. The number is masked for **every** caller. */
export interface PassengerRow {
  readonly passengerId: string;
  readonly name: string;
  readonly mobileMasked?: string;
  /** Rides that reached a terminal successful state (R-05). Deliveries are counted too. */
  readonly trips: number;
  readonly joinedAt: string;
  /** `deleted` is never answered yet — no column records a PDPA erasure (C065's). */
  readonly status: 'active' | 'blocked' | 'deleted';
}

export interface PassengerProfile {
  readonly passengerId: string;
  readonly name: string;
  /** Clear or `+9477*****67`, decided server-side. See this file's masking note. */
  readonly mobile?: string;
  /** Clear or `a***@domain` — still a valid address, so a schema-validating client keeps the response. */
  readonly email?: string;
  readonly joinedAt: string;
  /** What drivers rated them (`driver_to_passenger`, US-18.2). Absent until somebody has. */
  readonly rating?: number;
  readonly defaultPay: 'cash' | 'lankaqr' | 'onepay';
  readonly status: 'active' | 'blocked' | 'deleted';
  readonly sosContacts?: readonly SosContact[];
}

/** One AL-13 emergency contact. The number masks by the same rule as the passenger's own. */
export interface SosContact {
  readonly name: string;
  readonly phone?: string;
}

/** `PassengerDetail` — SCR-AP-011. The read that emits `PII_READ`. */
export interface PassengerDetail {
  readonly profile: PassengerProfile;
  readonly trips?: readonly DirectoryTrip[];
  readonly payments?: readonly DirectoryPayment[];
  readonly packages?: readonly DirectoryPackage[];
  readonly disputes?: readonly DirectoryDispute[];
}

// ---------------------------------------------------------------------------
// Drivers (AL-41)
// ---------------------------------------------------------------------------

/** `DriverRow` — one row of SCR-AP-012. */
export interface DriverRow {
  readonly driverId: string;
  readonly name: string;
  readonly mobileMasked?: string;
  /** Registration numbers — owned or assigned, the row does not distinguish. */
  readonly vehicles: readonly string[];
  /** `dispatch.driver_levels.level`, defaulting to 3 for a driver nobody has scored. */
  readonly level: number;
  readonly trips: number;
  /** Derived, not stored: suspended wins over verified — it is the later fact. */
  readonly status: 'verified' | 'pending' | 'suspended';
}

export interface DriverProfile {
  readonly driverId: string;
  readonly name: string;
  readonly mobile?: string;
  /**
   * Clear, or masked to its **last two characters**.
   *
   * Harder than a phone number, deliberately: an NIC's leading digits are the
   * holder's year of birth and day of year, so a prefix is not a hint — it is a
   * date of birth.
   */
  readonly nic?: string;
  readonly joinedAt: string;
  readonly rating?: number;
  readonly walletMinor: number;
  readonly currency: string;
  readonly level: number;
  readonly points: number;
  readonly status: 'verified' | 'pending' | 'suspended';
  readonly verifiedAt?: string;
}

/**
 * `LinkedVehicle` — a vehicle chip on SCR-AP-013, **owned _or_ assigned**.
 *
 * A Mode C driver owns their vehicle; a fleet's driver owns nothing and drives what
 * `registry.fleet_assignments` gives them (AL-03). Both are "linked vehicles"
 * (US-24.10), and an operator looking at a suspension needs to know which.
 */
export interface LinkedVehicle {
  readonly vehicleId: string;
  readonly regNo: string;
  readonly type: VehicleType;
  readonly mode: OperatingMode;
  readonly status: RegistrationStatus;
  readonly dispatchState: 'ACTIVE' | 'DISPATCH_SUSPENDED';
  readonly owned: boolean;
  /**
   * `/v1/admin/vehicles/{vehicleId}` — the **API** path, not a portal route.
   *
   * Deliberately unused as an href. The screen the chip jumps to is
   * `/vehicles/{vehicleId}`, and that path comes from the item
   * `GET /v1/admin/session` sent, so a caller whose menu has no vehicle directory
   * gets a chip and not a link the proxy would refuse.
   */
  readonly link: string;
}

/** `DriverDetail` — SCR-AP-013. The read that emits `PII_READ`. */
export interface DriverDetail {
  readonly profile: DriverProfile;
  readonly vehicles?: readonly LinkedVehicle[];
  readonly trips?: readonly DirectoryTrip[];
  readonly walletLedger?: readonly WalletLedgerEntry[];
  readonly dailyFee?: readonly DailyFeeCharge[];
  readonly creditTransfers?: readonly DirectoryCreditTransfer[];
  readonly reports?: readonly DirectoryVehicleReport[];
}

// ---------------------------------------------------------------------------
// Vehicles (AL-42)
// ---------------------------------------------------------------------------

/** `VehicleRow` — one row of SCR-AP-014. */
export interface VehicleRow {
  readonly vehicleId: string;
  readonly type: VehicleType;
  readonly mode: OperatingMode;
  readonly owner?: string;
  /** Present for Mode A/B vehicles owned by a fleet. */
  readonly fleetOrg?: string;
  readonly regNo: string;
  /** Completed Mode C rides **plus** completed Mode A/B sessions — a bus has no rides. */
  readonly trips: number;
  readonly status: RegistrationStatus;
}

/** The bound tracker (T-08), absent where the vehicle has none. */
export interface VehicleTracker {
  readonly imei: string;
  /** Pinged inside US-3.13's 30-minute silence window — C044's threshold, so the two agree. */
  readonly online: boolean;
  readonly state: 'ACTIVE' | 'QUARANTINED' | 'REVOKED';
  readonly lastSeen?: string;
}

export interface VehicleInfo {
  readonly vehicleId: string;
  readonly type: VehicleType;
  readonly regNo: string;
  readonly mode: OperatingMode;
  readonly ownerId: string;
  readonly owner?: string;
  readonly fleetId?: string;
  readonly fleetOrg?: string;
  readonly status: RegistrationStatus;
  /** `DISPATCH_SUSPENDED` is E-03's "do not offer rides to it" and is not the end of a registration. */
  readonly dispatchState: 'ACTIVE' | 'DISPATCH_SUSPENDED';
  readonly onboardingStatus: 'incomplete' | 'approved';
  /** The newest certificate's expiry, as an Asia/Colombo calendar date (D-38). */
  readonly insuranceExpiry?: string;
  readonly revenueLicenceExpiry?: string;
  readonly registeredAt: string;
  readonly tracker?: VehicleTracker;
}

/** `VehicleEarningsDay` — one Asia/Colombo business day (D-38) of settled fare on this vehicle. */
export interface VehicleEarningsDay {
  readonly earnDate: string;
  readonly trips: number;
  readonly grossMinor: number;
  readonly currency: string;
}

/** `AdminVehicleDetail` — SCR-AP-015. The read that emits `PII_READ` too (Δ C064). */
export interface AdminVehicleDetail {
  readonly info: VehicleInfo;
  readonly documents?: readonly DocumentRef[];
  readonly trips?: readonly DirectoryTrip[];
  readonly earnings?: readonly VehicleEarningsDay[];
  readonly dailyFee?: readonly DailyFeeCharge[];
  readonly reports?: readonly DirectoryVehicleReport[];
}

// ---------------------------------------------------------------------------
// The searches, as the URL holds them
// ---------------------------------------------------------------------------

function first(value: string | readonly string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : (value as string | undefined);
}

/**
 * One free-text criterion, trimmed, capped at the contract's own `maxLength`, and
 * absent when it says nothing.
 *
 * Blank is "no filter" rather than "match the empty string" — a box the operator
 * cleared submits `?name=`, and treating that as a criterion would answer an empty
 * page for a search they think they cancelled. `DirectoryEndpoints.Criterion`
 * makes the same call on the other side.
 */
function criterion(
  key: string,
  value: string | readonly string[] | undefined,
  maxLength: number,
): Record<string, string> {
  const trimmed = first(value)?.trim();
  return trimmed ? { [key]: trimmed.slice(0, maxLength) } : {};
}

/**
 * A `?id=` criterion, split into what may be sent and what the operator typed.
 *
 * `null` id with a non-empty `raw` is the malformed case: the screen keeps the box
 * filled so the operator can see and fix what they pasted, marks it, and sends
 * nothing. See this file's note.
 */
interface IdCriterion {
  readonly id?: string;
  readonly rawId?: string;
  readonly invalidId?: boolean;
}

function identifier(value: string | readonly string[] | undefined): IdCriterion {
  const raw = criterion('id', value, 64).id;
  if (!raw) return {};
  if (isAdminId(raw)) return { id: raw, rawId: raw };
  return { rawId: raw, invalidId: true };
}

/** A cursor is opaque and is only ever echoed back — length is the only check worth making. */
function cursorOf(value: string | readonly string[] | undefined): Record<string, string> {
  return criterion('cursor', value, 512);
}

export interface PassengerSelection extends IdCriterion {
  readonly name?: string;
  readonly mobile?: string;
  readonly email?: string;
  readonly cursor?: string;
}

export function passengerSelection(
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): PassengerSelection {
  return {
    ...criterion('name', params.name, 200),
    ...criterion('mobile', params.mobile, 20),
    ...identifier(params.id),
    ...criterion('email', params.email, 200),
    ...cursorOf(params.cursor),
  };
}

export interface DriverSelection extends IdCriterion {
  readonly name?: string;
  readonly mobile?: string;
  readonly nic?: string;
  readonly regNo?: string;
  readonly level?: DriverLevel;
  /** Always present: the absent parameter *is* `verified`, so the control has to say so. */
  readonly status: DriverStatus;
  readonly cursor?: string;
}

export function driverSelection(
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): DriverSelection {
  const level = Number.parseInt(first(params.level) ?? '', 10);
  const status = first(params.status);

  return {
    ...criterion('name', params.name, 200),
    ...criterion('mobile', params.mobile, 20),
    ...identifier(params.id),
    ...criterion('nic', params.nic, 20),
    ...criterion('regNo', params.regNo, 32),
    ...(isDriverLevel(level) ? { level } : {}),
    status: isDriverStatus(status) ? status : DEFAULT_DRIVER_STATUS,
    ...cursorOf(params.cursor),
  };
}

export interface VehicleSelection extends IdCriterion {
  readonly regNo?: string;
  readonly type?: VehicleType;
  readonly mode?: OperatingMode;
  readonly ownerMobile?: string;
  readonly fleetOrg?: string;
  readonly status?: RegistrationStatus;
  readonly cursor?: string;
}

export function vehicleSelection(
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): VehicleSelection {
  const type = first(params.type);
  const mode = first(params.mode);
  const status = first(params.status);

  return {
    ...criterion('regNo', params.regNo, 32),
    ...identifier(params.id),
    ...(isVehicleType(type) ? { type } : {}),
    ...(isOperatingMode(mode) ? { mode } : {}),
    ...criterion('ownerMobile', params.ownerMobile, 20),
    ...criterion('fleetOrg', params.fleetOrg, 200),
    ...(isRegistrationStatus(status) ? { status } : {}),
    ...cursorOf(params.cursor),
  };
}

type Query = Record<string, string | number>;

/**
 * The criteria as admin-bff's query, **all of them, combining with AND**.
 *
 * Every parameter the contract declares is here and each is independent, which is
 * the whole of "every documented search criterion works singly and in
 * combination": there is no mode, no primary field and no branch that drops one
 * criterion when another is present.
 */
export function passengerSearch(selection: PassengerSelection): Query {
  return {
    ...(selection.name ? { name: selection.name } : {}),
    ...(selection.mobile ? { mobile: selection.mobile } : {}),
    ...(selection.id ? { id: selection.id } : {}),
    ...(selection.email ? { email: selection.email } : {}),
    ...(selection.cursor ? { cursor: selection.cursor } : {}),
    limit: DIRECTORY_PAGE_SIZE,
  };
}

export function driverSearch(selection: DriverSelection): Query {
  return {
    ...(selection.name ? { name: selection.name } : {}),
    ...(selection.mobile ? { mobile: selection.mobile } : {}),
    ...(selection.id ? { id: selection.id } : {}),
    ...(selection.nic ? { nic: selection.nic } : {}),
    ...(selection.regNo ? { regNo: selection.regNo } : {}),
    ...(selection.level ? { level: selection.level } : {}),
    // Sent even when it is the default: `verified` is what the screen says it is
    // showing, and a query that relied on admin-bff's default would be a screen
    // whose caption is true only for as long as that default is.
    status: selection.status,
    ...(selection.cursor ? { cursor: selection.cursor } : {}),
    limit: DIRECTORY_PAGE_SIZE,
  };
}

export function vehicleSearch(selection: VehicleSelection): Query {
  return {
    ...(selection.regNo ? { regNo: selection.regNo } : {}),
    ...(selection.id ? { id: selection.id } : {}),
    ...(selection.type ? { type: selection.type } : {}),
    ...(selection.mode ? { mode: selection.mode } : {}),
    ...(selection.ownerMobile ? { ownerMobile: selection.ownerMobile } : {}),
    ...(selection.fleetOrg ? { fleetOrg: selection.fleetOrg } : {}),
    ...(selection.status ? { status: selection.status } : {}),
    ...(selection.cursor ? { cursor: selection.cursor } : {}),
    limit: DIRECTORY_PAGE_SIZE,
  };
}

/**
 * The criteria as a **portal** query string, so a detail can send the operator back
 * to the results they came from.
 *
 * The malformed `?id=` travels too — `rawId`, not `id`. An operator who mistyped an
 * id, opened a row found by another criterion and pressed Back should find the box
 * as they left it rather than silently emptied.
 */
export function passengerQuery(selection: PassengerSelection): Record<string, string> {
  return {
    ...(selection.name ? { name: selection.name } : {}),
    ...(selection.mobile ? { mobile: selection.mobile } : {}),
    ...(selection.rawId ? { id: selection.rawId } : {}),
    ...(selection.email ? { email: selection.email } : {}),
    ...(selection.cursor ? { cursor: selection.cursor } : {}),
  };
}

export function driverQuery(selection: DriverSelection): Record<string, string> {
  return {
    ...(selection.name ? { name: selection.name } : {}),
    ...(selection.mobile ? { mobile: selection.mobile } : {}),
    ...(selection.rawId ? { id: selection.rawId } : {}),
    ...(selection.nic ? { nic: selection.nic } : {}),
    ...(selection.regNo ? { regNo: selection.regNo } : {}),
    ...(selection.level ? { level: String(selection.level) } : {}),
    ...(selection.status === DEFAULT_DRIVER_STATUS ? {} : { status: selection.status }),
    ...(selection.cursor ? { cursor: selection.cursor } : {}),
  };
}

export function vehicleQuery(selection: VehicleSelection): Record<string, string> {
  return {
    ...(selection.regNo ? { regNo: selection.regNo } : {}),
    ...(selection.rawId ? { id: selection.rawId } : {}),
    ...(selection.type ? { type: selection.type } : {}),
    ...(selection.mode ? { mode: selection.mode } : {}),
    ...(selection.ownerMobile ? { ownerMobile: selection.ownerMobile } : {}),
    ...(selection.fleetOrg ? { fleetOrg: selection.fleetOrg } : {}),
    ...(selection.status ? { status: selection.status } : {}),
    ...(selection.cursor ? { cursor: selection.cursor } : {}),
  };
}

/** `path` with a portal query on it, or `path` alone when there is nothing to carry. */
export function withQuery(path: string, query: Readonly<Record<string, string>>): string {
  const search = new URLSearchParams(query).toString();
  return search ? `${path}?${search}` : path;
}

/**
 * Whether this selection asks admin-bff anything at all.
 *
 * A directory with no criterion is a legitimate query — "show me the first page" —
 * so the only refusal is the malformed id, which admin-bff answers `400` on the
 * field. See this file's note.
 */
export function isSendable(selection: IdCriterion): boolean {
  return selection.invalidId !== true;
}

/** Whether the operator has actually narrowed anything, for the Clear control. */
export function isFiltered(query: Readonly<Record<string, string>>): boolean {
  return Object.keys(query).some((key) => key !== 'cursor');
}

// ---------------------------------------------------------------------------
// The activity tabs, which are also the URL
// ---------------------------------------------------------------------------

/**
 * A detail's tabs, in the wireframe's order, held in `?tab=`.
 *
 * **Links, not `@mageride/ui`'s `Tabs`** — the third time this portal makes that
 * call (C106's queues, C108's finance strip) and here it buys something neither of
 * those did: **only the tab being read reaches the browser.** The whole payload —
 * every ledger entry, every recipient's number, the NIC — is on the server either
 * way, but a client component holding it would serialise all five tabs into the
 * RSC payload so that four of them could be shown by a press. A `<Link>` renders
 * one.
 *
 * The cost is stated rather than hidden: switching a tab is a second
 * `GET …/{id}`, and that read is `PII_READ`-audited, so an investigator who opens
 * four tabs leaves four rows. That is what happened — four looks at one person's
 * record — and it is the same reading C106 applied to a grid of six thumbnails
 * being six `DOC_VIEW` rows. Under-counting a disclosure is the failure an audit
 * trail cannot have.
 */
export const PASSENGER_TABS = ['trips', 'payments', 'packages', 'disputes'] as const;

export type PassengerTab = (typeof PASSENGER_TABS)[number];

export const DRIVER_TABS = ['trips', 'wallet', 'dailyFee', 'transfers', 'reports'] as const;

export type DriverTab = (typeof DRIVER_TABS)[number];

export const VEHICLE_TABS = ['trips', 'earnings', 'dailyFee', 'reports'] as const;

export type VehicleTab = (typeof VEHICLE_TABS)[number];

/**
 * The tab a URL selects, falling back to the first.
 *
 * An unrecognised value is not forwarded anywhere — the tab chooses which array of
 * the one payload is drawn — so the fallback is a rendering decision and not a
 * query the operator has to be told about.
 */
export function tabSelection<T extends string>(
  tabs: readonly [T, ...T[]],
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): T {
  const requested = first(params.tab);
  return (tabs as readonly string[]).includes(requested ?? '') ? (requested as T) : tabs[0];
}
