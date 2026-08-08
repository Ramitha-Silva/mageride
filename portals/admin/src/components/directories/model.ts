import type { StatusTone } from '@mageride/ui';

import type {
  AdminVehicleDetail,
  CursorPage,
  DailyFeeCharge,
  DirectoryCreditTransfer,
  DirectoryDispute,
  DirectoryPackage,
  DirectoryPayment,
  DirectoryTrip,
  DirectoryVehicleReport,
  DriverProfile,
  DriverRow,
  LinkedVehicle,
  OperatingMode,
  PassengerProfile,
  PassengerRow,
  RegistrationStatus,
  VehicleEarningsDay,
  VehicleInfo,
  VehicleRow,
  VehicleType,
  WalletLedgerEntry,
} from '@/api/directories';
import { categoryLabel, statusPill as ticketStatusPill } from '@/components/support/model';
import type { AdminMessageKey, AdminTranslator, Locale } from '@/i18n';
import {
  formatBusinessDate,
  formatCount,
  formatDateTime,
  formatDay,
  formatMoneyMinor,
  formatRating,
  formatSignedMoneyMinor,
} from '@/i18n/format';

/**
 * SCR-AP-010…015's view model: the vocabulary, the arithmetic and the pills, apart
 * from the markup that draws them.
 *
 * Everything here is pure, which is what lets the properties the Definition of Done
 * is actually about be asserted directly — that a list row's number is the masked
 * one it arrived as, that a session shows no fare, that an expired certificate
 * reads differently from one that has three weeks left.
 *
 * ## Why there are vocabulary tables here at all
 *
 * `vehicle_type`, `payment.method`, `wallet_transactions.kind` and the registration
 * statuses are **stored identifiers**, not copy. They arrive in one spelling for
 * every operator and the screen has to put a Tamil word in front of a Tamil reader,
 * so the mapping from identifier to resource key lives on this side, where all
 * three languages are. C106's `verification/model.ts` states the same rule.
 *
 * **An unknown identifier renders as itself.** A ride state is a service's enum
 * (D5' §6 gives Mode C eighteen of them) and this portal will be behind it the day
 * one is added; a raw `DriverArrived` in a cell is ugly, obviously a key, and gets
 * reported, where an empty cell is an operator reading a trip whose outcome they
 * cannot see.
 *
 * ## What is masked here: nothing
 *
 * The mask is applied server-side and the clear value is not in the payload beside
 * it (`MaskablePhone`), so every number, NIC and address in this file is rendered
 * exactly as it arrived. There is no branch on which form it is, because a portal
 * able to tell would be one that had been sent both.
 */

export interface RenderContext {
  readonly t: AdminTranslator;
  readonly locale: Locale;
}

export interface PillView {
  readonly tone: StatusTone;
  readonly label: string;
}

/** The em dash every unanswered cell renders as — a fact the platform does not hold. */
export const ABSENT = '—';

/** `Rs {amount}` — the mark is a word and is translated, the amount is `Intl`'s. */
function money(minor: number, { t, locale }: RenderContext): string {
  return t('admin.dashboard.money', { amount: formatMoneyMinor(locale, minor) });
}

/** The same, signed: a wallet debit is a negative row and has to read as one (D-09 §10). */
function signedMoney(minor: number, { t, locale }: RenderContext): string {
  return t('admin.dashboard.money', { amount: formatSignedMoneyMinor(locale, minor) });
}

function label(
  table: Readonly<Record<string, AdminMessageKey>>,
  key: string | undefined,
  t: AdminTranslator,
): string {
  if (!key) return ABSENT;
  const resource = table[key];
  return resource ? t(resource) : key;
}

// ---------------------------------------------------------------------------
// Vocabularies
// ---------------------------------------------------------------------------

/** `registry.vehicles.vehicle_type` — the ten canonical types (AL-09). */
const VEHICLE_TYPE_LABELS: Readonly<Record<string, AdminMessageKey>> = {
  motorbike: 'admin.config.vehicle.motorbike',
  three_wheeler: 'admin.config.vehicle.threeWheeler',
  flex: 'admin.config.vehicle.flex',
  sedan: 'admin.config.vehicle.sedan',
  mini_van: 'admin.config.vehicle.miniVan',
  van: 'admin.config.vehicle.van',
  truck: 'admin.config.vehicle.truck',
  mini_truck: 'admin.config.vehicle.miniTruck',
  bus: 'admin.config.vehicle.bus',
  train: 'admin.config.vehicle.train',
};

/** `D-10`'s methods, plus the two an admin sees on a driver's own rows. */
const PAYMENT_METHOD_LABELS: Readonly<Record<string, AdminMessageKey>> = {
  cash: 'admin.directory.pay.cash',
  lankaqr: 'admin.directory.pay.lankaqr',
  onepay: 'admin.directory.pay.onepay',
  cod: 'admin.directory.pay.cod',
  scan_driver_qr: 'admin.directory.pay.scanDriverQr',
  wallet: 'admin.directory.pay.wallet',
};

/**
 * `billing.wallet_transactions.kind`, as far as the platform names them in code.
 *
 * The column carries no CHECK the portal can read, so this is the same call
 * C107 made for a ticket category: the kinds the platform writes itself are
 * translated and anything else renders as the stored key rather than as a blank.
 */
const WALLET_KIND_LABELS: Readonly<Record<string, AdminMessageKey>> = {
  topup: 'admin.directory.wallet.topup',
  voucher_topup: 'admin.directory.wallet.topup',
  daily_fee: 'admin.directory.wallet.dailyFee',
  adjustment: 'admin.directory.wallet.adjustment',
  fee_reversal: 'admin.directory.wallet.reversal',
  credit_transfer: 'admin.directory.wallet.transfer',
  transfer_in: 'admin.directory.wallet.transferIn',
  transfer_out: 'admin.directory.wallet.transferOut',
};

export function vehicleTypeLabel(type: VehicleType | string | undefined, t: AdminTranslator): string {
  return label(VEHICLE_TYPE_LABELS, type, t);
}

/** `A` / `B` / `C` — the shared `mode.*` keys, because a mode is not this surface's word. */
export function modeLabel(mode: OperatingMode | string | undefined, t: AdminTranslator): string {
  if (mode === 'A') return t('mode.a');
  if (mode === 'B') return t('mode.b');
  if (mode === 'C') return t('mode.c');
  return ABSENT;
}

export function paymentMethodLabel(method: string | undefined, t: AdminTranslator): string {
  return label(PAYMENT_METHOD_LABELS, method, t);
}

export function walletKindLabel(kind: string | undefined, t: AdminTranslator): string {
  return label(WALLET_KIND_LABELS, kind, t);
}

// ---------------------------------------------------------------------------
// Status pills
// ---------------------------------------------------------------------------

/** A passenger account. `deleted` is declared and never answered — no column records an erasure. */
export function passengerStatusPill(status: string, t: AdminTranslator): PillView {
  if (status === 'blocked') return { tone: 'error', label: t('admin.directory.status.blocked') };
  if (status === 'deleted') return { tone: 'neutral', label: t('admin.directory.status.deleted') };
  return { tone: 'success', label: t('status.active') };
}

/**
 * A driver, derived rather than stored: verified when `verified_at` is set,
 * suspended when `iam.users.is_blocked` is, pending otherwise. **Suspended wins**
 * — it is the later fact and the one the row was opened to find.
 */
export function driverStatusPill(status: string, t: AdminTranslator): PillView {
  if (status === 'suspended') return { tone: 'error', label: t('status.suspended') };
  if (status === 'pending') return { tone: 'warning', label: t('status.pending') };
  return { tone: 'success', label: t('status.verified') };
}

/** A vehicle's **registration** status. Dispatch state is a separate fact and a separate pill. */
export function registrationStatusPill(status: RegistrationStatus | string, t: AdminTranslator): PillView {
  if (status === 'APPROVED') return { tone: 'success', label: t('status.approved') };
  if (status === 'REJECTED') return { tone: 'error', label: t('status.rejected') };
  if (status === 'DEACTIVATED') {
    return { tone: 'neutral', label: t('admin.directory.status.deactivated') };
  }
  return { tone: 'warning', label: t('status.pending') };
}

/**
 * E-03's "do not offer rides to it", which is **not** the end of a registration.
 *
 * `null` for a vehicle nobody suspended: a pill on every row saying "this one is
 * fine" is a pill nobody reads, and what an operator opened the record to find is
 * the exception.
 */
export function dispatchStatePill(state: string, t: AdminTranslator): PillView | null {
  return state === 'DISPATCH_SUSPENDED'
    ? { tone: 'error', label: t('admin.directory.status.dispatchSuspended') }
    : null;
}

/** `safety.vehicle_reports.status`. Three CONFIRMED delist the vehicle (US-12.6). */
export function reportStatusPill(status: string, t: AdminTranslator): PillView {
  if (status === 'CONFIRMED') return { tone: 'error', label: t('admin.directory.report.confirmed') };
  if (status === 'DISMISSED') return { tone: 'neutral', label: t('admin.directory.report.dismissed') };
  return { tone: 'warning', label: t('status.pending') };
}

/** `billing.credit_transfers.status` (US-9.13/9.21). */
export function transferStatusPill(status: string, t: AdminTranslator): PillView {
  if (status === 'APPROVED') return { tone: 'success', label: t('status.approved') };
  if (status === 'REJECTED') return { tone: 'error', label: t('status.rejected') };
  return { tone: 'warning', label: t('status.pending') };
}

/**
 * A daily-fee charge (D-13).
 *
 * `WAIVED_FIRST_TRIP` is not a failure and must not read as one: US-9.6 waives the
 * fee on a driver's first trip of the day, and colouring it as an error would send
 * an operator hunting for a charge the platform deliberately did not make.
 */
export function dailyFeeStatusPill(status: string, t: AdminTranslator): PillView {
  return status === 'WAIVED_FIRST_TRIP'
    ? { tone: 'info', label: t('admin.directory.fee.waived') }
    : { tone: 'success', label: t('admin.directory.fee.paid') };
}

/**
 * A certificate expiry, against today.
 *
 * Three bands, and the middle one is the reason this is a pill rather than a date:
 * an insurance certificate that lapses in a fortnight is the fact an operator
 * opened the record to find, and it looks exactly like one that lapses in a year
 * unless something says so. `null` where the platform holds no expiry — an absent
 * date is not an expired one.
 */
export const EXPIRY_WARNING_DAYS = 30;

export function expiryPill(
  isoDate: string | undefined,
  { t, locale }: RenderContext,
  today: Date = new Date(),
): PillView | null {
  const rendered = formatBusinessDate(locale, isoDate);
  if (!isoDate || !rendered) return null;

  const expiry = new Date(`${isoDate}T00:00:00Z`);
  const midnight = Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate());
  const days = Math.floor((expiry.getTime() - midnight) / 86_400_000);

  if (days < 0) return { tone: 'error', label: t('admin.directory.expiry.expired', { date: rendered }) };
  if (days <= EXPIRY_WARNING_DAYS) {
    return { tone: 'warning', label: t('admin.directory.expiry.soon', { date: rendered, days }) };
  }
  return { tone: 'success', label: t('admin.directory.expiry.valid', { date: rendered }) };
}

// ---------------------------------------------------------------------------
// Tables — one shape for the three result lists and the thirteen activity tabs
// ---------------------------------------------------------------------------

/**
 * One cell.
 *
 * `text` is the value, `sub` the identifier under it — a directory prints ids in
 * full, because a truncated identifier is an ambiguous one and these screens exist
 * to disambiguate people.
 */
export interface CellView {
  readonly text?: string;
  readonly sub?: string;
  readonly pill?: PillView;
  readonly pills?: readonly PillView[];
  /** Right-aligned, for a column of money or counts. */
  readonly numeric?: boolean;
}

export interface TableRowView {
  readonly key: string;
  readonly cells: readonly CellView[];
  /** The detail this row opens. Absent on an activity row, which opens nothing. */
  readonly href?: string;
  /** The full sentence the row's control announces — "Open the record for …". */
  readonly openNamed?: string;
}

/** SCR-AP-010's five columns, plus the control. */
export function passengerRows(
  rows: readonly PassengerRow[],
  href: (passengerId: string) => string,
  { t, locale }: RenderContext,
): TableRowView[] {
  return rows.map((row) => ({
    key: row.passengerId,
    href: href(row.passengerId),
    openNamed: t('admin.directory.openNamed', { subject: row.name }),
    cells: [
      { text: row.name, sub: row.passengerId },
      { text: row.mobileMasked ?? ABSENT },
      { text: formatCount(locale, row.trips), numeric: true },
      { text: formatDateTime(locale, row.joinedAt) ?? ABSENT },
      { pill: passengerStatusPill(row.status, t) },
    ],
  }));
}

/** SCR-AP-012's six columns. `vehicles` is a list of plates and can be empty. */
export function driverRows(
  rows: readonly DriverRow[],
  href: (driverId: string) => string,
  { t, locale }: RenderContext,
): TableRowView[] {
  return rows.map((row) => ({
    key: row.driverId,
    href: href(row.driverId),
    openNamed: t('admin.directory.openNamed', { subject: row.name }),
    cells: [
      { text: row.name, sub: row.driverId },
      { text: row.mobileMasked ?? ABSENT },
      { text: row.vehicles.length > 0 ? row.vehicles.join(' · ') : ABSENT },
      { text: t('admin.directory.driver.levelShort', { level: row.level }) },
      { text: formatCount(locale, row.trips), numeric: true },
      { pill: driverStatusPill(row.status, t) },
    ],
  }));
}

/**
 * SCR-AP-014's six columns.
 *
 * "Owner / fleet" is one column and the fleet wins where there is one: a Mode A bus
 * belongs to Lanka Transit and naming the driver who happens to be on it would
 * answer a different question from the one the column asks.
 */
export function vehicleRows(
  rows: readonly VehicleRow[],
  href: (vehicleId: string) => string,
  { t, locale }: RenderContext,
): TableRowView[] {
  return rows.map((row) => ({
    key: row.vehicleId,
    href: href(row.vehicleId),
    openNamed: t('admin.directory.openNamed', { subject: row.regNo }),
    cells: [
      { text: row.regNo, sub: row.vehicleId },
      { text: `${vehicleTypeLabel(row.type, t)} · ${modeLabel(row.mode, t)}` },
      { text: row.fleetOrg ?? row.owner ?? ABSENT },
      { text: formatCount(locale, row.trips), numeric: true },
      { pill: registrationStatusPill(row.status, t) },
    ],
  }));
}

/**
 * How many rows this page answered.
 *
 * Cursor pagination carries no total (C002 decision 9), so the count is what came
 * back and `{n}+` says there is another page rather than reporting a page size as a
 * result count. `—` for a search that failed: "no matches" is a claim, and a 503 is
 * not evidence for it.
 */
export function resultCount(
  page: CursorPage<unknown> | null,
  { t, locale }: RenderContext,
): string {
  if (!page) return ABSENT;

  const count = formatCount(locale, page.items.length);
  return t(page.hasMore ? 'admin.directory.results.countMore' : 'admin.directory.results.count', {
    count,
  });
}

// ---------------------------------------------------------------------------
// The activity tabs
// ---------------------------------------------------------------------------

/**
 * A trip row, for whichever detail is drawing it.
 *
 * **A session carries no fare and the cell says so.** `kind: session` is a Mode A/B
 * journey out of `trips.sessions`, where the money is a subscription and not a
 * per-journey charge (D-03); printing `Rs 0.00` there would be a fare of zero,
 * which is a different claim from "this is not the kind of journey that has one".
 *
 * The wireframe's **Route** column is not drawn. `DirectoryTrip` carries no origin
 * and no destination — see the C109 handoff — and a column headed "Route" over the
 * vehicle's plate would be a label that lies about its own cells.
 */
export function tripRows(
  trips: readonly DirectoryTrip[],
  { t, locale }: RenderContext,
): TableRowView[] {
  return trips.map((trip) => ({
    key: trip.tripId,
    cells: [
      { text: formatDateTime(locale, trip.startedAt) ?? ABSENT, sub: trip.tripId },
      {
        text: t(trip.kind === 'session' ? 'admin.directory.trip.session' : 'admin.directory.trip.ride'),
        sub: trip.vehicleType ? vehicleTypeLabel(trip.vehicleType, t) : undefined,
      },
      { text: trip.regNo ?? ABSENT },
      { text: trip.counterpartyName ?? ABSENT, sub: trip.counterpartyId },
      {
        text: typeof trip.fareMinor === 'number' ? money(trip.fareMinor, { t, locale }) : ABSENT,
        numeric: true,
      },
      { text: trip.state },
    ],
  }));
}

/** SCR-AP-011's Payments tab — one attempt of D-10's machine; a retry is its own row. */
export function paymentRows(
  payments: readonly DirectoryPayment[],
  context: RenderContext,
): TableRowView[] {
  const { t, locale } = context;

  return payments.map((payment) => ({
    key: payment.paymentId,
    cells: [
      { text: formatDateTime(locale, payment.createdAt) ?? ABSENT, sub: payment.rideId },
      { text: paymentMethodLabel(payment.method, t) },
      { text: money(payment.amountMinor, context), numeric: true },
      {
        // Surcharge and tip are two additions to one fare and belong in one cell:
        // two more columns for figures that are usually zero would push the state
        // off a 1024px screen, which is the width D2 §AP gives this portal.
        text: extras(payment, context),
        numeric: true,
      },
      { text: t('admin.directory.payment.attempt', { attempt: payment.attemptNo }) },
      { text: payment.state },
    ],
  }));
}

function extras(payment: DirectoryPayment, context: RenderContext): string {
  const parts: string[] = [];
  if (payment.surchargeMinor) {
    parts.push(context.t('admin.directory.payment.surcharge', { amount: money(payment.surchargeMinor, context) }));
  }
  if (payment.tipMinor) {
    parts.push(context.t('admin.directory.payment.tip', { amount: money(payment.tipMinor, context) }));
  }
  return parts.length > 0 ? parts.join(' · ') : ABSENT;
}

/** SCR-AP-011's Packages tab — a `kind = 2` ride (P-06). The recipient's number is masked upstream. */
export function packageRows(
  packages: readonly DirectoryPackage[],
  context: RenderContext,
): TableRowView[] {
  const { locale } = context;

  return packages.map((delivery) => ({
    key: delivery.rideId,
    cells: [
      { text: formatDateTime(locale, delivery.createdAt) ?? ABSENT, sub: delivery.rideId },
      { text: delivery.packageSize ?? ABSENT, sub: delivery.description },
      { text: delivery.recipientName ?? ABSENT, sub: delivery.recipientMobile },
      {
        text: typeof delivery.fareMinor === 'number' ? money(delivery.fareMinor, context) : ABSENT,
        numeric: true,
      },
      { text: formatDateTime(locale, delivery.completedAt) ?? ABSENT },
      { text: delivery.state },
    ],
  }));
}

/**
 * SCR-AP-011's Disputes tab.
 *
 * The **whole** ticket list, not a "disputes" subset: `support.tickets.category` is
 * free text and US-9.23's daily-fee refund request rides in it, so a filter on a
 * vocabulary nobody has fixed would silently hide tickets. The category and the
 * status vocabularies are C107's own — one screen's word for a ticket status is
 * every screen's word for it.
 */
export function disputeRows(
  disputes: readonly DirectoryDispute[],
  href: ((ticketId: string) => string) | null,
  { t, locale }: RenderContext,
): TableRowView[] {
  return disputes.map((dispute) => ({
    key: dispute.ticketId,
    ...(href ? { href: href(dispute.ticketId), openNamed: t('admin.directory.dispute.openNamed') } : {}),
    cells: [
      { text: formatDateTime(locale, dispute.createdAt) ?? ABSENT, sub: dispute.ticketId },
      { text: categoryLabel(dispute.category, t) },
      { text: dispute.description ?? ABSENT },
      { text: formatDateTime(locale, dispute.updatedAt) ?? ABSENT },
      { pill: ticketStatusPill(dispute.status, t) },
    ],
  }));
}

/** SCR-AP-013's Wallet-ledger tab (D-09 §10). Signed, and the balance travels with it. */
export function walletRows(
  entries: readonly WalletLedgerEntry[],
  context: RenderContext,
): TableRowView[] {
  const { t, locale } = context;

  return entries.map((entry) => ({
    key: String(entry.entryNo),
    cells: [
      { text: formatDateTime(locale, entry.ts) ?? ABSENT },
      { text: walletKindLabel(entry.kind, t), sub: entry.description },
      { text: signedMoney(entry.amountMinor, context), numeric: true },
      { text: money(entry.balanceAfterMinor, context), numeric: true },
    ],
  }));
}

/**
 * A Daily-fee tab, on either detail (D-13).
 *
 * The charge is idempotent per driver, per vehicle, per **business day** — so the
 * date is the key and is printed as a calendar date, not as the instant the job
 * happened to run. Both are on the row and they answer different questions.
 */
export function dailyFeeRows(
  charges: readonly DailyFeeCharge[],
  context: RenderContext,
): TableRowView[] {
  const { t, locale } = context;

  return charges.map((charge) => ({
    key: `${charge.feeDate}:${charge.driverId}:${charge.vehicleId}`,
    cells: [
      { text: formatBusinessDate(locale, charge.feeDate) ?? ABSENT },
      { text: charge.regNo ?? ABSENT, sub: charge.vehicleId },
      { text: money(charge.amountMinor, context), numeric: true },
      { text: formatCount(locale, charge.tripsThatDay), numeric: true },
      { text: formatDateTime(locale, charge.chargedAt) ?? ABSENT },
      { pill: dailyFeeStatusPill(charge.status, t) },
    ],
  }));
}

/**
 * SCR-AP-013's Credit-transfers tab (US-9.13/9.21).
 *
 * **Exact value, no commission** (AL-01), so there is no fee column and there must
 * not be one. `direction` is computed against the driver whose record this is;
 * `initiation` is who started it. Both are shown because "money left this wallet"
 * and "this driver asked for it" are different facts and an investigator needs
 * each.
 */
export function transferRows(
  transfers: readonly DirectoryCreditTransfer[],
  context: RenderContext,
): TableRowView[] {
  const { t, locale } = context;

  return transfers.map((transfer) => ({
    key: transfer.transferId,
    cells: [
      { text: formatDateTime(locale, transfer.createdAt) ?? ABSENT, sub: transfer.transferId },
      {
        text: t(
          transfer.direction === 'in'
            ? 'admin.directory.transfer.in'
            : 'admin.directory.transfer.out',
        ),
      },
      { text: transfer.counterpartyName ?? ABSENT, sub: transfer.counterpartyId },
      {
        text: signedMoney(
          transfer.direction === 'in' ? transfer.amountMinor : -transfer.amountMinor,
          context,
        ),
        numeric: true,
      },
      {
        text: t(
          transfer.initiation === 'REQUESTED'
            ? 'admin.directory.transfer.requested'
            : 'admin.directory.transfer.direct',
        ),
      },
      { pill: transferStatusPill(transfer.status, t) },
    ],
  }));
}

/** A Reports tab, on either detail — `safety.vehicle_reports` as this record sees it. */
export function reportRows(
  reports: readonly DirectoryVehicleReport[],
  { t, locale }: RenderContext,
): TableRowView[] {
  return reports.map((report) => ({
    key: report.reportId,
    cells: [
      { text: formatDateTime(locale, report.createdAt) ?? ABSENT, sub: report.reportId },
      { text: report.regNo ?? ABSENT, sub: report.vehicleId },
      { text: report.reason },
      { pill: reportStatusPill(report.status, t) },
    ],
  }));
}

/** SCR-AP-015's Earnings tab — one Asia/Colombo business day of settled fare (D-38). */
export function earningsRows(
  days: readonly VehicleEarningsDay[],
  context: RenderContext,
): TableRowView[] {
  const { locale } = context;

  return days.map((day) => ({
    key: day.earnDate,
    cells: [
      { text: formatBusinessDate(locale, day.earnDate) ?? ABSENT },
      { text: formatCount(locale, day.trips), numeric: true },
      { text: money(day.grossMinor, context), numeric: true },
    ],
  }));
}

// ---------------------------------------------------------------------------
// The profile cards
// ---------------------------------------------------------------------------

/**
 * One row of a profile card.
 *
 * `value` is optional because two of them are pills and nothing else — an expiry
 * whose whole content is "to 31 Dec 2026, valid" would otherwise be printed twice
 * beside itself.
 */
export interface FactView {
  readonly key: string;
  readonly label: string;
  readonly value?: string;
  readonly pill?: PillView;
}

/** `★ 4.9`, or the em dash for somebody nobody has rated (`rating` is absent until they have). */
function ratingValue(rating: number | undefined, { t, locale }: RenderContext): string {
  const formatted = formatRating(locale, rating);
  return formatted ? t('admin.directory.field.ratingValue', { rating: formatted }) : ABSENT;
}

/**
 * SCR-AP-011's profile card.
 *
 * The SOS contacts are a **count and the names**, because that is what the card has
 * room for and what an operator on a safety call needs first; the numbers are
 * masked by the same rule as the passenger's own and are rendered as they arrived.
 */
export function passengerFacts(profile: PassengerProfile, { t, locale }: RenderContext): FactView[] {
  return [
    { key: 'mobile', label: t('admin.directory.field.mobile'), value: profile.mobile ?? ABSENT },
    { key: 'email', label: t('admin.directory.field.email'), value: profile.email ?? ABSENT },
    {
      key: 'joined',
      label: t('admin.directory.field.joined'),
      value: formatDay(locale, profile.joinedAt) ?? ABSENT,
    },
    {
      key: 'rating',
      label: t('admin.directory.field.rating'),
      value: ratingValue(profile.rating, { t, locale }),
    },
    {
      key: 'defaultPay',
      label: t('admin.directory.field.defaultPay'),
      value: paymentMethodLabel(profile.defaultPay, t),
    },
    {
      key: 'sos',
      label: t('admin.directory.field.sosContacts'),
      value:
        profile.sosContacts && profile.sosContacts.length > 0
          ? profile.sosContacts.map((contact) => contact.name).join(' · ')
          : t('admin.directory.field.sosNone'),
    },
  ];
}

/** SCR-AP-013's profile card. Wallet and Level are the two figures the screen exists for. */
export function driverFacts(profile: DriverProfile, context: RenderContext): FactView[] {
  const { t, locale } = context;

  return [
    { key: 'mobile', label: t('admin.directory.field.mobile'), value: profile.mobile ?? ABSENT },
    { key: 'nic', label: t('admin.directory.field.nic'), value: profile.nic ?? ABSENT },
    {
      key: 'joined',
      label: t('admin.directory.field.joined'),
      value: formatDay(locale, profile.joinedAt) ?? ABSENT,
    },
    {
      key: 'verified',
      label: t('admin.directory.field.verifiedAt'),
      value: formatDay(locale, profile.verifiedAt) ?? ABSENT,
    },
    {
      key: 'rating',
      label: t('admin.directory.field.rating'),
      value: ratingValue(profile.rating, context),
    },
    {
      key: 'wallet',
      label: t('admin.directory.field.wallet'),
      value: money(profile.walletMinor, context),
    },
    {
      key: 'level',
      label: t('admin.directory.field.level'),
      value: t('admin.directory.driver.levelPoints', {
        level: profile.level,
        points: formatCount(locale, profile.points),
      }),
    },
  ];
}

/** SCR-AP-015's information card — registration, the two certificates, and the tracker. */
export function vehicleFacts(info: VehicleInfo, context: RenderContext): FactView[] {
  const { t, locale } = context;

  const insurance = expiryPill(info.insuranceExpiry, context);
  const revenue = expiryPill(info.revenueLicenceExpiry, context);

  return [
    { key: 'type', label: t('admin.directory.field.type'), value: vehicleTypeLabel(info.type, t) },
    { key: 'regNo', label: t('admin.directory.field.regNo'), value: info.regNo },
    { key: 'mode', label: t('admin.directory.field.mode'), value: modeLabel(info.mode, t) },
    {
      key: 'owner',
      label: t('admin.directory.field.owner'),
      value: info.owner ?? info.ownerId,
    },
    {
      key: 'fleet',
      label: t('admin.directory.field.fleetOrg'),
      value: info.fleetOrg ?? ABSENT,
    },
    {
      key: 'insurance',
      label: t('admin.directory.field.insurance'),
      ...(insurance ? { pill: insurance } : { value: ABSENT }),
    },
    {
      key: 'revenue',
      label: t('admin.directory.field.revenueLicence'),
      ...(revenue ? { pill: revenue } : { value: ABSENT }),
    },
    {
      key: 'tracker',
      label: t('admin.directory.field.tracker'),
      value: info.tracker ? info.tracker.imei : t('admin.directory.tracker.none'),
      ...(info.tracker ? { pill: trackerPill(info.tracker.online, info.tracker.state, t) } : {}),
    },
    {
      key: 'registered',
      label: t('admin.directory.field.registered'),
      value: formatDay(locale, info.registeredAt) ?? ABSENT,
    },
    {
      key: 'onboarding',
      label: t('admin.directory.field.onboarding'),
      value: t(
        info.onboardingStatus === 'approved'
          ? 'admin.directory.onboarding.approved'
          : 'admin.directory.onboarding.incomplete',
      ),
    },
  ];
}

/**
 * The bound tracker (T-08).
 *
 * `online` is "pinged inside US-3.13's 30-minute silence window" — the same
 * threshold C044's fleet-health screen uses, so the two surfaces cannot disagree
 * about one device. A quarantined or revoked tracker is a different fact from a
 * silent one and outranks it: a device the platform has stopped trusting is not
 * merely offline.
 */
export function trackerPill(online: boolean, state: string, t: AdminTranslator): PillView {
  if (state === 'REVOKED') return { tone: 'error', label: t('admin.directory.tracker.revoked') };
  if (state === 'QUARANTINED') {
    return { tone: 'warning', label: t('admin.directory.tracker.quarantined') };
  }
  return online
    ? { tone: 'success', label: t('admin.directory.tracker.online') }
    : { tone: 'warning', label: t('admin.directory.tracker.offline') };
}

/**
 * A driver's linked vehicles (US-24.10).
 *
 * **The href comes from the caller's own menu, not from `LinkedVehicle.link`.**
 * That field is the *API* path, and a chip pointed at a portal route the operator
 * does not hold would be a link `proxy.ts` answers 403 on — `AlertsCard`'s rule.
 * Without the item the chip is still drawn, because "this driver also drives
 * NB-4412" is information a Finance Officer reconciling a daily fee needs whether
 * or not they may open the vehicle's own screen.
 */
export interface VehicleChipView {
  readonly key: string;
  readonly regNo: string;
  readonly detail: string;
  readonly href?: string;
  readonly status: PillView;
  readonly suspended: PillView | null;
  readonly ownership: string;
}

export function vehicleChips(
  vehicles: readonly LinkedVehicle[],
  href: ((vehicleId: string) => string) | null,
  t: AdminTranslator,
): VehicleChipView[] {
  return vehicles.map((vehicle) => ({
    key: vehicle.vehicleId,
    regNo: vehicle.regNo,
    detail: `${vehicleTypeLabel(vehicle.type, t)} · ${modeLabel(vehicle.mode, t)}`,
    ...(href ? { href: href(vehicle.vehicleId) } : {}),
    status: registrationStatusPill(vehicle.status, t),
    suspended: dispatchStatePill(vehicle.dispatchState, t),
    ownership: t(vehicle.owned ? 'admin.directory.vehicle.owned' : 'admin.directory.vehicle.assigned'),
  }));
}

/**
 * The heading pill on a vehicle detail: its registration status and its mode, which
 * is what the wireframe puts there ("Approved · Mode C").
 */
export function vehicleHeadline(detail: AdminVehicleDetail, t: AdminTranslator): PillView {
  const status = registrationStatusPill(detail.info.status, t);
  return { tone: status.tone, label: `${status.label} · ${modeLabel(detail.info.mode, t)}` };
}
