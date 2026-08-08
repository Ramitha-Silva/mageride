import type { StatusTone } from '@mageride/ui';

/**
 * **SCR-FP-002a** — the organisation's bank & payout profile (AL-49, US-27.1/27.2),
 * transcribed from `backend/contracts/fleet.yaml`, plus the one rule the rest of
 * the portal reads off it.
 *
 * ## This file is what C113 imports
 *
 * BR-31.1 makes `PUT …/vehicles/{id}/classification {mode_b_billing:'paid'}`
 * answer **`409 payout-profile-not-verified`** while the organisation's profile is
 * not `verified`. The *control* that would provoke that lives on SCR-FP-004 —
 * "Service payment · Free / Paid", C113's screen — and the *fact* it is gated on
 * lives here, on the screen that produces it. {@link canSetPaidServicePayment} and
 * {@link PAID_SERVICE_PAYMENT_BLOCKED_KEY} are that pair: one predicate and one
 * sentence, so the vehicle screen disables the option and says why without
 * re-deriving a rule, and there is one place to change if the rule moves.
 */

/* ---------------------------------------------------------------------------
 * Targets
 * ------------------------------------------------------------------------ */

/** `GET`/`PUT /v1/fleets/{own id}/payout-profile`. */
export const PAYOUT_PROFILE = '/payout-profile';

/** `POST /v1/fleets/{own id}/payout-profile/documents` — multipart. */
export const PAYOUT_DOCUMENTS = '/payout-profile/documents';

/* ---------------------------------------------------------------------------
 * Shapes
 * ------------------------------------------------------------------------ */

/**
 * `fleet.yaml#/components/schemas/PayoutProfileStatus`.
 *
 * Four values, and `superseded` is the one a reader trips over: §26 makes the
 * table **versioned** and admits one `verified` row per organisation
 * (`ux_payout_profile_verified`), so when an officer approves an edit the
 * incumbent has to leave `verified` — and neither printed status could carry it.
 * `rejected` is a decision nobody took; `pending_verification` would put a
 * superseded row back on the officer's queue.
 */
export type PayoutProfileStatus = 'pending_verification' | 'verified' | 'rejected' | 'superseded';

/** `fleet.yaml#/components/schemas/PayoutProfile`. */
export interface PayoutProfile {
  readonly bank: string;
  readonly branch: string;
  readonly accountNo: string;
  readonly accountHolderName: string;
  /** The bank-app QR image (`registry.fleet_payout_profiles.lankaqr_upload_id`). */
  readonly lankaqrDocId?: string;
  /** The statement **or** passbook page — BR-31.1 gives them one column. */
  readonly proofDocId?: string;
  readonly status: PayoutProfileStatus;
  /** Set when a Verification Officer refused this version. */
  readonly rejectionReason?: string;
  readonly verifiedAt?: string;
}

/** The body `PUT …/payout-profile` takes. All four required. */
export interface PayoutProfileBody {
  readonly bank: string;
  readonly branch: string;
  readonly accountNo: string;
  readonly accountHolderName: string;
}

/**
 * The three `kind` values `POST …/payout-profile/documents` admits.
 *
 * **`bank_statement` and `passbook_first_page` are one slot**, not two: BR-31.1
 * asks for the latest statement *or* the first page of the passbook, and §26
 * gives them a single `proof_upload_id`. Uploading a passbook after a statement
 * replaces it, which is what somebody re-photographing a blurred page expects —
 * so the screen draws one dropzone with a choice of what the file is, exactly as
 * `web_fleet.html` draws it.
 */
export const PAYOUT_DOCUMENT_KINDS = [
  'bank_statement',
  'passbook_first_page',
  'lankaqr_code',
] as const;

export type PayoutDocumentKind = (typeof PAYOUT_DOCUMENT_KINDS)[number];

/** The two kinds that satisfy the proof-of-account slot. */
export const PROOF_DOCUMENT_KINDS = ['bank_statement', 'passbook_first_page'] as const;

export function isPayoutDocumentKind(value: string): value is PayoutDocumentKind {
  return (PAYOUT_DOCUMENT_KINDS as readonly string[]).includes(value);
}

/** `POST …/payout-profile/documents` — `{docId, kind}`. */
export interface PayoutDocumentRef {
  readonly docId: string;
  readonly kind: PayoutDocumentKind;
}

/**
 * `Fleet:DocumentMaxBytes`, which fleet-svc defaults to 8 MiB and counts as the
 * bytes arrive rather than trusting `Content-Length`.
 *
 * Repeated here so the dropzone can refuse an over-sized file before it is
 * uploaded. **That is a courtesy, not a gate** — the ceiling that matters is the
 * one the service enforces, and this number existing does not move it.
 */
export const DOCUMENT_MAX_BYTES = 8 * 1024 * 1024;

/** A scanned statement is routinely a PDF; a bank app's QR is always an image. */
export const PROOF_ACCEPT = 'application/pdf,image/jpeg,image/png,image/webp';
export const LANKAQR_ACCEPT = 'image/jpeg,image/png,image/webp';

/* ---------------------------------------------------------------------------
 * The AL-49 gate
 * ------------------------------------------------------------------------ */

/**
 * Whether a Mode B vehicle may be set to **Service payment = Paid** (AL-51's name
 * for `mode_b_billing`, unchanged on the wire — AL-49/BR-31.1).
 *
 * `null` is "this organisation has never submitted a profile", which is the state
 * `GET …/payout-profile` reports as `404 payout-profile-not-found`. It is refused
 * for the same reason every other unverified state is: there is no approved
 * account for a subscriber's money to be sent to.
 *
 * **Free is never gated**, and that is not an omission — an office shuttle
 * collects nothing from its passengers, so it needs no account to collect it
 * into.
 */
export function canSetPaidServicePayment(profile: PayoutProfile | null | undefined): boolean {
  return profile?.status === 'verified';
}

/**
 * The sentence that goes beside the disabled control, as a resource key.
 *
 * Exported rather than written out on each screen so that the Fleet Portal says
 * one thing about one rule. C113's SCR-FP-004 renders this next to the greyed
 * "Paid" option; SCR-FP-002a renders it under the status chip as the consequence
 * of the profile not being verified yet.
 */
export const PAID_SERVICE_PAYMENT_BLOCKED_KEY = 'fleet.payout.gate.paid' as const;

/* ---------------------------------------------------------------------------
 * Rendering
 * ------------------------------------------------------------------------ */

/**
 * The status chip's tone and its resource key — `web_fleet.html`'s
 * "Pending verification → Verified → Rejected + reason", plus the two states the
 * sketch has no chip for.
 *
 * A `superseded` row is drawn neutral rather than as a failure: it is a version
 * an officer decided on and a newer one replaced, and colouring it red would tell
 * an owner something went wrong when what happened is that their edit was
 * approved.
 */
export interface PayoutStatusView {
  readonly tone: StatusTone;
  readonly labelKey:
    | 'fleet.payout.status.pending'
    | 'fleet.payout.status.verified'
    | 'fleet.payout.status.rejected'
    | 'fleet.payout.status.superseded'
    | 'fleet.payout.status.none';
}

export function payoutStatusView(profile: PayoutProfile | null | undefined): PayoutStatusView {
  switch (profile?.status) {
    case 'verified':
      return { tone: 'success', labelKey: 'fleet.payout.status.verified' };
    case 'rejected':
      return { tone: 'error', labelKey: 'fleet.payout.status.rejected' };
    case 'superseded':
      return { tone: 'neutral', labelKey: 'fleet.payout.status.superseded' };
    case 'pending_verification':
      return { tone: 'warning', labelKey: 'fleet.payout.status.pending' };
    default:
      return { tone: 'neutral', labelKey: 'fleet.payout.status.none' };
  }
}

/**
 * The banks a Sri Lankan operator can hold a business account with — the CBSL
 * licensed commercial banks, plus the licensed specialised banks a transport
 * business realistically banks at.
 *
 * **This list is local, and it should not be** (raised in the C112 handoff).
 * `bank` is a free-text column on the wire and `web_fleet.html` draws a
 * dropdown — "Commercial Bank ▾" — because a bank name typed three ways is three
 * accounts to a Verification Officer reading a statement. Nothing on any contract
 * serves the list; content-svc already publishes `GET /v1/config/cities` and is
 * the obvious home for `GET /v1/config/banks`.
 *
 * Proper nouns, so they are data rather than copy: a bank's name is the same in
 * Sinhala, Tamil and English cheque printing, and translating one would produce a
 * value an officer could not match against the statement.
 */
export const LICENSED_BANKS: readonly string[] = [
  'Bank of Ceylon',
  "People's Bank",
  'Commercial Bank of Ceylon',
  'Hatton National Bank',
  'Sampath Bank',
  'Seylan Bank',
  'Nations Trust Bank',
  'National Development Bank',
  'DFCC Bank',
  'Pan Asia Banking Corporation',
  'Union Bank of Colombo',
  'Amana Bank',
  'Cargills Bank',
  'National Savings Bank',
  'Sanasa Development Bank',
  'HDFC Bank of Sri Lanka',
  'Regional Development Bank',
  'State Mortgage & Investment Bank',
  'HSBC',
  'Standard Chartered Bank',
  'Citibank',
  'Habib Bank',
  'MCB Bank',
  'Indian Bank',
  'Indian Overseas Bank',
  'State Bank of India',
  'Public Bank Berhad',
  'Bank of China',
];
