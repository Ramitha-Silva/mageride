import type { StatusTone } from '@mageride/ui';

import type { FleetMessageKey } from '@/i18n';

/**
 * **SCR-FP-004** — the vehicle roster, AL-50's four named document slots and
 * AL-51's "Service payment", transcribed from `backend/contracts/fleet.yaml`.
 *
 * Nothing here calls anything. The org-relative targets are the strings a screen
 * hands `read()`/`mutate()`, which is where the caller's own `fleetId` is written
 * into a URL and the only place it ever is (`./client`).
 *
 * ## The two rules that live here rather than on the screen
 *
 * 1. **A required slot's chip decides whether a vehicle can be approved**
 *    ({@link canBeApproved}). fleet-svc's `VehicleDocumentSlots.AreRequiredSlotsVerified`
 *    is the copy that matters — the portal never approves anything — but the same
 *    predicate is what SCR-FP-004 renders, and reading it off the slots the server
 *    sent means the screen cannot disagree with the officer's queue.
 * 2. **Whether a slot is required is the *server's* answer, not `kind`'s**
 *    ({@link VehicleDocumentSlot.required}). The route permit is required for
 *    Mode A and optional for Mode B, so one slot answers differently on two
 *    vehicles — which is exactly why C059 added the field, and why this file
 *    reads it instead of deriving it.
 */

/* ---------------------------------------------------------------------------
 * Targets
 * ------------------------------------------------------------------------ */

/** `GET`/`POST /v1/fleets/{own id}/vehicles` — the roster, and one new vehicle. */
export const VEHICLES = '/vehicles';

/** `POST /v1/fleets/{own id}/vehicles/bulk` — multipart, one `file` part. */
export const VEHICLES_BULK = '/vehicles/bulk';

/** `GET /v1/fleets/{own id}/vehicles/bulk/{jobId}` — the poll the 202 points at. */
export function bulkJobTarget(jobId: string): string {
  return `${VEHICLES_BULK}/${jobId}`;
}

/** `GET`/`POST /v1/fleets/{own id}/vehicles/{vehicleId}/documents`. */
export function vehicleDocumentsTarget(vehicleId: string): string {
  return `${VEHICLES}/${vehicleId}/documents`;
}

/** `PUT /v1/fleets/{own id}/vehicles/{vehicleId}/classification`. */
export function vehicleClassificationTarget(vehicleId: string): string {
  return `${VEHICLES}/${vehicleId}/classification`;
}

/* ---------------------------------------------------------------------------
 * Shapes
 * ------------------------------------------------------------------------ */

/**
 * `fleet.yaml`'s `mode` enum on `FleetVehicle` — **two values, not three**.
 *
 * AL-03 gives a fleet scheduled public transport and shared private vehicles;
 * `registry.fleet_vehicles.mode` admits `A` and `B`, and `FleetVehicleService`
 * answers `403 mode-not-allowed` for anything else. The on-demand plane is
 * ride-svc's and is not a fleet surface at all, which is why this union has no
 * third member and the portal offers no control that would need one.
 */
export type FleetMode = 'A' | 'B';

export const FLEET_MODES = ['A', 'B'] as const;

/** `FleetVehicle.status` — the approval workflow every onboarded vehicle runs (US-13.6). */
export type FleetVehicleStatus = 'PENDING' | 'APPROVED' | 'REJECTED' | 'DEACTIVATED';

/** `FleetVehicle.docsStatus` — AL-50's verdict on the paperwork, as one word. */
export type VehicleDocsStatus = 'docs_pending' | 'docs_complete';

/**
 * `fleet.yaml#/components/schemas/ModeBBilling` — "Service payment" in the UI.
 *
 * AL-51 renamed the *label* and left the field, the path and the column alone
 * ("a label rename is not worth a client or database migration"). So the wire
 * name is `modeBBilling` everywhere in this file, and the only place the words
 * "Service payment" appear is the resource table.
 */
export type ServicePayment = 'free' | 'paid';

export const SERVICE_PAYMENTS = ['free', 'paid'] as const;

/**
 * `_shared.yaml#/components/schemas/VehicleType`, minus the one type this
 * surface cannot onboard.
 *
 * **`train` is absent deliberately.** `FleetVehicleTypes.IsFleetOnboardable`
 * refuses it with `403 mode-not-allowed` — US-2.17/2.18 make trains
 * administered centrally, and an operator registering one here would put a
 * service on the passenger map no admin decided to run. It is not offered rather
 * than offered and refused, for the reason `INVITABLE_FLEET_ROLES` omits Owner
 * (`@/api/org`): a greyed option implies the operation exists somewhere on this
 * screen.
 */
export const ONBOARDABLE_VEHICLE_TYPES = [
  'bus',
  'van',
  'mini_van',
  'flex',
  'sedan',
  'three_wheeler',
  'motorbike',
  'truck',
  'mini_truck',
] as const;

export type OnboardableVehicleType = (typeof ONBOARDABLE_VEHICLE_TYPES)[number];

/** `fleet.yaml#/components/schemas/FleetVehicle`. */
export interface FleetVehicle {
  readonly vehicleId: string;
  readonly registrationNumber: string;
  readonly vehicleType: string;
  readonly mode: FleetMode;
  readonly status: FleetVehicleStatus;
  readonly docsStatus?: VehicleDocsStatus;
  readonly modeBBilling?: ServicePayment;
  readonly defaultMonthlyFareMinor?: number;
  /** `LKR`, always — `_shared.yaml` makes it a `const`. */
  readonly currency?: string;
}

export interface FleetVehicleList {
  readonly items: readonly FleetVehicle[];
}

/** The body `POST …/vehicles` takes. The last two are Mode B's alone. */
export interface AddVehicleBody {
  readonly registrationNumber: string;
  readonly vehicleType: string;
  readonly mode: FleetMode;
  readonly modeBBilling?: ServicePayment;
  readonly defaultMonthlyFareMinor?: number;
}

/** The body `PUT …/classification` takes. `defaultMonthlyFareMinor` is required for `paid`. */
export interface ClassificationBody {
  readonly modeBBilling: ServicePayment;
  readonly defaultMonthlyFareMinor?: number;
}

/* ---------------------------------------------------------------------------
 * AL-50 — the four named slots
 * ------------------------------------------------------------------------ */

/**
 * `VehicleDocumentSlot.kind` — the **stored** names, which are what
 * `GET …/documents` answers with (`registry.documents.kind`, C003).
 */
export type VehicleDocumentKind = 'registration' | 'insurance' | 'revenue_license' | 'permit';

/**
 * `POST …/documents`' `kind` enum — the **wire** names, which are SCR-FP-004's
 * own slot labels.
 *
 * Two of the four differ from the stored name, and fleet-svc accepts only these:
 * `VehicleDocumentKinds.ToStoredKind` maps `registration_copy → registration` and
 * `route_permit → permit` and refuses a stored name in its place, "because a
 * client sending `registration` is a client written against the wrong half of the
 * mapping". So a screen never posts the value it read back.
 */
export type VehicleDocumentUploadKind =
  | 'registration_copy'
  | 'insurance'
  | 'revenue_license'
  | 'route_permit';

/** `VehicleDocumentSlot.status` — the chip US-27.3 asks for, in as many words. */
export type VehicleDocumentSlotStatus = 'verified' | 'pending' | 'missing';

/** `_shared.yaml#/components/schemas/ExtractedField` — one AI-extracted value. */
export interface ExtractedField {
  /** A `registry.document_fields.field_key`, snake case — `insurance_expiry`, `reg_no_match`, … */
  readonly key: string;
  readonly value: string | null;
  /** `ai` for an extraction, `manual` for a typed value (always `pending`, BR-25.2). */
  readonly source: 'ai' | 'manual';
  readonly confidence?: number;
  readonly verifyStatus: 'auto_verified' | 'pending' | 'confirmed';
}

/** `fleet.yaml#/components/schemas/VehicleDocumentSlot`. */
export interface VehicleDocumentSlot {
  readonly kind: VehicleDocumentKind;
  readonly status: VehicleDocumentSlotStatus;
  /**
   * Whether AL-50 makes this slot mandatory for **this** vehicle — the server's
   * answer, because the route permit is required for Mode A and optional for
   * Mode B and no client can derive that from `kind`.
   */
  readonly required?: boolean;
  readonly docId?: string;
  readonly expiresAt?: string;
  readonly thumbUrl?: string;
  readonly fullUrl?: string;
  readonly fields?: readonly ExtractedField[];
}

export interface VehicleDocumentSlotList {
  readonly items: readonly VehicleDocumentSlot[];
}

/**
 * The four slots, in the order `web_fleet.html` draws them, each with the label
 * key it is captioned with and the wire name it is posted under.
 *
 * **This list is the AL-50 fence as data.** The component fence says "no generic
 * dropzone": a screen that mapped over an array the server sent would draw
 * however many boxes arrived, and a screen that read a `kind` off a form control
 * would let a fifth kind through. Four entries, named here, is what makes the
 * absence of a generic upload structural rather than a habit.
 */
export interface VehicleDocumentSlotSpec {
  readonly kind: VehicleDocumentKind;
  readonly uploadKind: VehicleDocumentUploadKind;
  readonly labelKey: FleetMessageKey;
  readonly hintKey: FleetMessageKey;
}

export const VEHICLE_DOCUMENT_SLOTS: readonly VehicleDocumentSlotSpec[] = [
  {
    kind: 'registration',
    uploadKind: 'registration_copy',
    labelKey: 'fleet.vehicles.doc.registration',
    hintKey: 'fleet.vehicles.doc.registrationHint',
  },
  {
    kind: 'insurance',
    uploadKind: 'insurance',
    labelKey: 'fleet.vehicles.doc.insurance',
    hintKey: 'fleet.vehicles.doc.insuranceHint',
  },
  {
    kind: 'revenue_license',
    uploadKind: 'revenue_license',
    labelKey: 'fleet.vehicles.doc.revenueLicense',
    hintKey: 'fleet.vehicles.doc.revenueLicenseHint',
  },
  {
    kind: 'permit',
    uploadKind: 'route_permit',
    labelKey: 'fleet.vehicles.doc.routePermit',
    hintKey: 'fleet.vehicles.doc.routePermitHint',
  },
];

const UPLOAD_KINDS: readonly string[] = VEHICLE_DOCUMENT_SLOTS.map((slot) => slot.uploadKind);

export function isVehicleDocumentUploadKind(value: string): value is VehicleDocumentUploadKind {
  return UPLOAD_KINDS.includes(value);
}

/**
 * Whether AL-50 requires this slot on a vehicle of this mode, for the case where
 * there is **no slot list to read it off** — the moments before the first
 * `GET …/documents`, which is the state the add-vehicle card is in.
 *
 * `VehicleDocumentKinds.RequiredForEveryMode` is the registration copy, the
 * insurance certificate and the revenue license; the route permit is Mode A's.
 * Once the server has answered, {@link VehicleDocumentSlot.required} is the value
 * that is used — this is the prediction, that is the fact.
 */
export function slotIsRequiredFor(kind: VehicleDocumentKind, mode: FleetMode): boolean {
  return kind === 'permit' ? mode === 'A' : true;
}

/**
 * The AL-50 approval rule, as the one predicate SCR-FP-004 renders: **every
 * required slot verified, or the vehicle cannot reach Approved.**
 *
 * US-27.3 states it as "the vehicle cannot be Approved while a required document
 * is Missing or Pending", and fleet-svc enforces it in
 * `VehicleApprovalService` — this side only *says* it. An empty slot list is
 * `false` rather than `true`: a vehicle whose documents have not been read yet
 * has not satisfied anything, and vacuous truth here would draw a green chip on a
 * vehicle with no paperwork at all.
 */
export function canBeApproved(slots: readonly VehicleDocumentSlot[]): boolean {
  const required = slots.filter((slot) => slot.required);
  return required.length > 0 && required.every((slot) => slot.status === 'verified');
}

/** The required slots that are not verified yet — what the vehicle is waiting on. */
export function outstandingSlots(
  slots: readonly VehicleDocumentSlot[],
): readonly VehicleDocumentSlot[] {
  return slots.filter((slot) => slot.required && slot.status !== 'verified');
}

/**
 * How many of a vehicle's required slots are verified — `web_fleet.html`'s
 * "4/4 verified (incl. route permit)" and "2/3 — insurance pending", as numbers.
 */
export interface DocumentsSummary {
  readonly verified: number;
  readonly required: number;
  /** The first outstanding slot, which is what the cell names. */
  readonly outstanding: VehicleDocumentSlot | null;
  readonly complete: boolean;
}

export function documentsSummary(slots: readonly VehicleDocumentSlot[]): DocumentsSummary {
  const required = slots.filter((slot) => slot.required);
  const verified = required.filter((slot) => slot.status === 'verified');
  const outstanding = outstandingSlots(slots);

  return {
    verified: verified.length,
    required: required.length,
    outstanding: outstanding[0] ?? null,
    complete: required.length > 0 && outstanding.length === 0,
  };
}

/* ---------------------------------------------------------------------------
 * The bulk import
 * ------------------------------------------------------------------------ */

/** `fleet.yaml#/components/schemas/BulkVehicleJob`. */
export interface BulkVehicleJob {
  readonly jobId: string;
  readonly totalRows: number;
  readonly importedRows: number;
  readonly failedRows: number;
  readonly status: 'PROCESSING' | 'COMPLETED' | 'FAILED';
  /**
   * Present only when rows failed. HMAC-signed, expiring, and **served to the
   * browser directly**: the signature in the query string *is* the credential,
   * which the contract says in as many words is "what lets the Fleet Portal hand
   * the link straight to a browser download". No bearer travels with it and none
   * is needed.
   */
  readonly errorReportUrl?: string;
}

/**
 * `Fleet:BulkMaxRows`, which fleet-svc defaults to 5,000 — `web_fleet.html`'s
 * "≤5,000 rows" and NFR-43's budget.
 */
export const BULK_MAX_ROWS = 5000;

/**
 * `Fleet:BulkUploadMaxBytes` — 2 MiB, and **not** the 8 MiB a document gets.
 *
 * Five thousand rows of `registrationNumber,vehicleType,mode` is around 150 kB,
 * so the ceiling is generous by an order of magnitude and a file that passes it
 * is not a spreadsheet somebody exported by mistake.
 */
export const BULK_UPLOAD_MAX_BYTES = 2 * 1024 * 1024;

export const BULK_CSV_ACCEPT = 'text/csv,.csv';

/**
 * The columns `BulkVehicleCsv` reads, as one string for the caption.
 *
 * Three required and two optional, and the two optional ones are US-13.1b's:
 * "the setting (and default fare for Paid) is captured at onboarding (single &
 * bulk CSV)". A header row is optional and recognised by content.
 *
 * **Documents are not among them, and cannot be** — a CSV carries no files, so
 * every imported row lands `docs_pending` and its four slots are filled per
 * vehicle afterwards (AL-50).
 */
export const BULK_CSV_COLUMNS =
  'registrationNumber,vehicleType,mode[,modeBBilling[,defaultMonthlyFareMinor]]';

/* ---------------------------------------------------------------------------
 * Money
 * ------------------------------------------------------------------------ */

/**
 * Rupees as typed → **integer cents**, or `null` when the value is not a number.
 *
 * The one conversion between what an operator writes on a fare board and what
 * `registry.vehicles.default_monthly_fare_minor` holds (CLAUDE.md, "money as
 * minor units"). `6,000` and `6000.00` are both what somebody writes for the
 * same fare, so the grouping separator is stripped before parsing; the rounding
 * is on the cents rather than the rupees, because `Math.round` there puts a
 * float on a whole cent instead of letting one reach an integer column.
 */
export function fareMinorFrom(value: string): number | null {
  const cleaned = value.replaceAll(/[\s,]/g, '');
  if (cleaned === '') return null;

  const rupees = Number(cleaned);
  if (!Number.isFinite(rupees) || rupees < 0) return null;

  return Math.round(rupees * 100);
}

/* ---------------------------------------------------------------------------
 * Documents on the wire
 * ------------------------------------------------------------------------ */

/**
 * `Fleet:DocumentMaxBytes` — the same 8 MiB ceiling the payout evidence gets,
 * and for the same reason: a CR book photographed on a phone is a large JPEG.
 *
 * Repeated so the dropzone can refuse an over-sized file before it is uploaded.
 * **That is a courtesy, not a gate** — fleet-svc counts the bytes as they arrive.
 */
export const VEHICLE_DOCUMENT_MAX_BYTES = 8 * 1024 * 1024;

/** A CR book is photographed; an insurance certificate is often a PDF. */
export const VEHICLE_DOCUMENT_ACCEPT = 'application/pdf,image/jpeg,image/png,image/webp';

/* ---------------------------------------------------------------------------
 * Rendering
 * ------------------------------------------------------------------------ */

export interface VehicleStatusView {
  readonly tone: StatusTone;
  readonly labelKey: FleetMessageKey;
}

/**
 * The approval chip — `web_fleet.html`'s Approved / Under review / Rejected.
 *
 * `PENDING` reads as "Under review" rather than "Pending" because that is what
 * the sketch calls it and what is actually happening: a Verification Officer has
 * the vehicle in a queue. `DEACTIVATED` has no chip in the sketch and gets a
 * neutral one — it is a vehicle an operator took off the road, not a failure.
 */
export function vehicleStatusView(status: FleetVehicleStatus): VehicleStatusView {
  switch (status) {
    case 'APPROVED':
      return { tone: 'success', labelKey: 'fleet.vehicles.status.approved' };
    case 'REJECTED':
      return { tone: 'error', labelKey: 'fleet.vehicles.status.rejected' };
    case 'DEACTIVATED':
      return { tone: 'neutral', labelKey: 'fleet.vehicles.status.deactivated' };
    default:
      return { tone: 'warning', labelKey: 'fleet.vehicles.status.pending' };
  }
}

export function slotStatusView(status: VehicleDocumentSlotStatus): VehicleStatusView {
  switch (status) {
    case 'verified':
      return { tone: 'success', labelKey: 'fleet.vehicles.slot.verified' };
    case 'pending':
      return { tone: 'warning', labelKey: 'fleet.vehicles.slot.pending' };
    default:
      return { tone: 'error', labelKey: 'fleet.vehicles.slot.missing' };
  }
}

/**
 * The D2 §0.2 vehicle-colour utility for a type — `web_fleet.html`'s coloured dot
 * in the status table's Type column (AL-26).
 *
 * **Whole class names, written out, rather than `bg-veh-${type}`.** Tailwind
 * compiles the rules it can see in the source; a class assembled at run time is
 * a class no rule was generated for, and the dot would come out transparent.
 * `three_wheeler` is `veh-tuk`, and a type with no token of its own falls back to
 * `veh-private` — the grey the sketch itself uses for a private vehicle.
 */
const VEHICLE_ACCENTS: Readonly<Record<string, string>> = {
  bus: 'bg-veh-bus',
  van: 'bg-veh-van',
  mini_van: 'bg-veh-mini-van',
  flex: 'bg-veh-flex',
  sedan: 'bg-veh-sedan',
  three_wheeler: 'bg-veh-tuk',
  motorbike: 'bg-veh-motorbike',
  truck: 'bg-veh-truck',
  mini_truck: 'bg-veh-mini-truck',
};

export function vehicleAccentClass(vehicleType: string): string {
  return VEHICLE_ACCENTS[vehicleType] ?? 'bg-veh-private';
}
