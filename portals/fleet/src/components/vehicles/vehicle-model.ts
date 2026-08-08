import type { StatusTone } from '@mageride/ui';

import {
  documentsSummary,
  slotStatusView,
  vehicleAccentClass,
  vehicleStatusView,
  type ExtractedField,
  type FleetVehicle,
  type VehicleDocumentSlot,
  type VehicleDocumentSlotStatus,
} from '@/api/vehicles';
import { formatDay, formatFareMinor } from '@/i18n/format';
import type { FleetMessageKey, FleetTranslator, Locale } from '@/i18n';

import type { DocumentSlotField } from './DocumentSlotCard';

/**
 * SCR-FP-004's view model — the wire shapes turned into the cells and chips
 * `web_fleet.html` draws, on the server, once per render.
 *
 * It lives beside the components rather than in `@/api/vehicles` because it is
 * about *this screen's* table: the API module carries the rules (which slots are
 * required, whether a vehicle can be approved) and this carries their wording.
 * Nothing here decides anything the server has not already decided.
 */

export interface VehicleRow {
  readonly vehicleId: string;
  readonly plate: string;
  /** `Bus (A)` — the type and mode `web_fleet.html` prints in one cell. */
  readonly type: string;
  /** The D2 §0.2 per-type colour utility for the leading dot (AL-26). */
  readonly accentClass: string;
  readonly servicePayment: string;
  readonly servicePaymentTone: StatusTone;
  readonly documents: string;
  readonly documentsTone: StatusTone;
  readonly status: string;
  readonly statusTone: StatusTone;
  readonly selected: boolean;
}

/**
 * One roster row.
 *
 * ## The Documents cell reads the roster's `docsStatus`, not a slot list
 *
 * `GET …/vehicles` answers `docs_pending | docs_complete` per vehicle and does
 * **not** carry the slots — reading four slot lists to draw one table would be
 * one request per row. The precise "2/3 — insurance pending" is on the panel for
 * the vehicle an operator opened, where the slots have actually been fetched; the
 * table says which of the two states each vehicle is in, which is what a scan
 * down the column is for.
 */
export function vehicleRow(
  vehicle: FleetVehicle,
  selectedVehicleId: string | null,
  locale: Locale,
  t: FleetTranslator,
): VehicleRow {
  const status = vehicleStatusView(vehicle.status);
  const payment = servicePaymentCell(vehicle, locale, t);
  const complete = vehicle.docsStatus === 'docs_complete';

  return {
    vehicleId: vehicle.vehicleId,
    plate: vehicle.registrationNumber,
    type: t('fleet.vehicles.typeWithMode', {
      type: vehicleTypeLabel(vehicle.vehicleType, t),
      mode: vehicle.mode,
    }),
    accentClass: vehicleAccentClass(vehicle.vehicleType),
    servicePayment: payment.label,
    servicePaymentTone: payment.tone,
    documents: complete
      ? t('fleet.vehicles.docsCell.complete')
      : t('fleet.vehicles.docsCell.pending'),
    documentsTone: complete ? 'success' : 'warning',
    status: t(status.labelKey),
    statusTone: status.tone,
    selected: vehicle.vehicleId === selectedVehicleId,
  };
}

/**
 * The Service payment cell — AL-51's column.
 *
 * Three answers and an em dash. A **Mode A vehicle has no service payment at
 * all** and gets the dash the wireframe draws, because `mode_b_billing` is NULL
 * for Mode A by design; a Mode B vehicle that has not been classified yet says
 * so, because that is a thing the operator still has to do and an unclassified
 * vehicle rendered as "Free" would be a fleet quietly collecting nothing.
 */
export function servicePaymentCell(
  vehicle: FleetVehicle,
  locale: Locale,
  t: FleetTranslator,
): { readonly label: string; readonly tone: StatusTone } {
  if (vehicle.mode !== 'B') {
    return { label: t('fleet.vehicles.servicePayment.notApplicable'), tone: 'neutral' };
  }

  if (vehicle.modeBBilling === 'free') {
    return { label: t('fleet.vehicles.servicePayment.freeOffice'), tone: 'success' };
  }

  if (vehicle.modeBBilling === 'paid') {
    const fare = vehicle.defaultMonthlyFareMinor;
    return {
      label:
        fare === undefined
          ? t('fleet.vehicles.servicePayment.paid')
          : t('fleet.vehicles.servicePayment.paidWithFare', {
              fare: formatFareMinor(locale, fare),
            }),
      tone: 'info',
    };
  }

  return { label: t('fleet.vehicles.servicePayment.notSet'), tone: 'warning' };
}

/**
 * The AL-09 type name, or the raw value for a type this build has not heard of.
 *
 * A literal table rather than a `fleet.vehicles.type.${type}` template, so the
 * key set is checked at compile time: a type added to `_shared.yaml` and not to
 * the resources shows the operator `mini_truck` — legible, and obviously
 * untranslated — instead of a key that looks like a bug.
 */
const TYPE_KEYS = {
  bus: 'fleet.vehicles.type.bus',
  van: 'fleet.vehicles.type.van',
  mini_van: 'fleet.vehicles.type.mini_van',
  flex: 'fleet.vehicles.type.flex',
  sedan: 'fleet.vehicles.type.sedan',
  three_wheeler: 'fleet.vehicles.type.three_wheeler',
  motorbike: 'fleet.vehicles.type.motorbike',
  truck: 'fleet.vehicles.type.truck',
  mini_truck: 'fleet.vehicles.type.mini_truck',
} as const satisfies Readonly<Record<string, FleetMessageKey>>;

export function vehicleTypeLabel(vehicleType: string, t: FleetTranslator): string {
  const key = (TYPE_KEYS as Readonly<Record<string, FleetMessageKey>>)[vehicleType];
  return key ? t(key) : vehicleType;
}

/**
 * The four slots as the cards render them, in AL-50's order, for one vehicle.
 *
 * `required` is the **server's** field and is not re-derived: the route permit is
 * required for Mode A and optional for Mode B, and the slot list says which this
 * vehicle is. A vehicle whose slots have not been read yet gets the prediction
 * (`slotIsRequiredFor`) from the caller instead — see the panel.
 */
export function slotRequirementLabel(
  slot: VehicleDocumentSlot,
  t: FleetTranslator,
): string {
  if (slot.required) {
    return slot.kind === 'permit'
      ? `${t('fleet.vehicles.slot.required')} · ${t('fleet.vehicles.slot.permitModeA')}`
      : t('fleet.vehicles.slot.required');
  }
  return t('fleet.vehicles.slot.optional');
}

export function slotStatusLabel(
  status: VehicleDocumentSlotStatus,
  t: FleetTranslator,
): { readonly label: string; readonly tone: StatusTone } {
  const view = slotStatusView(status);
  return { label: t(view.labelKey), tone: view.tone };
}

/**
 * One extracted field as a labelled chip.
 *
 * The tone is the field's **`verifyStatus`**, not its value: `auto_verified` and
 * `confirmed` both mean somebody or something settled it — the second is an
 * officer's answer and "counts as verified" (`_shared.yaml`) — and `pending` is
 * what holds the slot, and the vehicle, out of approval. A field with no value is
 * shown as unread rather than blank, because "the expiry could not be read" is
 * the whole reason the chip beside it is amber.
 */
const FIELD_KEYS = {
  reg_no_match: 'fleet.vehicles.extract.reg_no_match',
  plate_text: 'fleet.vehicles.extract.plate_text',
  insurance_expiry: 'fleet.vehicles.extract.insurance_expiry',
  revenue_no: 'fleet.vehicles.extract.revenue_no',
  revenue_expiry: 'fleet.vehicles.extract.revenue_expiry',
  permit_no: 'fleet.vehicles.extract.permit_no',
  permit_route: 'fleet.vehicles.extract.permit_route',
  permit_expiry: 'fleet.vehicles.extract.permit_expiry',
} as const satisfies Readonly<Record<string, FleetMessageKey>>;

export function extractedFieldView(field: ExtractedField, t: FleetTranslator): DocumentSlotField {
  const labelKey = (FIELD_KEYS as Readonly<Record<string, FleetMessageKey>>)[field.key];

  return {
    key: field.key,
    label: labelKey ? t(labelKey) : field.key,
    value:
      field.value ??
      (field.verifyStatus === 'pending'
        ? t('fleet.vehicles.slot.fieldPending')
        : t('fleet.vehicles.slot.fieldUnread')),
    tone: field.verifyStatus === 'pending' ? 'warning' : 'success',
  };
}

/**
 * What the vehicle is waiting on, as one sentence — the AL-50 approval rule in
 * the operator's own words (US-27.3).
 */
export function approvalGateSentence(
  slots: readonly VehicleDocumentSlot[],
  t: FleetTranslator,
): { readonly text: string; readonly ready: boolean } {
  const summary = documentsSummary(slots);
  if (summary.complete) return { text: t('fleet.vehicles.docs.ready'), ready: true };

  const outstanding = slots
    .filter((slot) => slot.required && slot.status !== 'verified')
    .map((slot) => `${slotName(slot.kind, t)} (${slotStatusLabel(slot.status, t).label})`)
    .join(', ');

  return { text: t('fleet.vehicles.docs.blocked', { slots: outstanding }), ready: false };
}

/** The slot's own name, by stored kind. */
export function slotName(kind: VehicleDocumentSlot['kind'], t: FleetTranslator): string {
  switch (kind) {
    case 'registration':
      return t('fleet.vehicles.doc.registration');
    case 'insurance':
      return t('fleet.vehicles.doc.insurance');
    case 'revenue_license':
      return t('fleet.vehicles.doc.revenueLicense');
    default:
      return t('fleet.vehicles.doc.routePermit');
  }
}

/** `Expires 30 Jun 2026`, or nothing for a slot with no expiry. */
export function slotExpiry(
  slot: VehicleDocumentSlot,
  locale: Locale,
  t: FleetTranslator,
): string | null {
  const date = formatDay(locale, slot.expiresAt);
  return date ? t('fleet.vehicles.slot.expires', { date }) : null;
}
