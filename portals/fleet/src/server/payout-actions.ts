'use server';

import { revalidatePath } from 'next/cache';

import { mutate } from '@/api/client';
import {
  DOCUMENT_MAX_BYTES,
  PAYOUT_DOCUMENTS,
  PAYOUT_PROFILE,
  isPayoutDocumentKind,
  type PayoutDocumentKind,
  type PayoutDocumentRef,
  type PayoutProfile,
  type PayoutProfileBody,
} from '@/api/payout';
import { ProblemError } from '@/api/problem';
import { getTranslator } from '@/i18n/server';

/**
 * **SCR-FP-002a's two writes** (AL-49, US-27.1/27.2) — the bank details, and the
 * evidence behind them.
 *
 * ## Both re-enter verification, and the screen has to say so before it happens
 *
 * BR-31.1: "any edit re-triggers verification". fleet-svc means it literally —
 * an edit to a **verified** profile *inserts* a new `pending_verification`
 * version and leaves the incumbent verified, so subscription-svc's `payTo` keeps
 * rendering the account an officer approved and no subscriber's next transfer is
 * silently redirected. **A document upload is an edit too**, for the same reason:
 * replacing the statement behind a verified account is exactly the change an
 * officer would want to see again.
 *
 * That is a consequence an owner should be warned about *before* they press Save,
 * not discovered from a chip that changed colour — so the form carries the
 * warning while the profile is verified, and both actions revalidate the screen
 * so the chip and the copy agree with the row that now exists.
 *
 * ## Neither is approval-gated
 *
 * `requiresApprovedOrg` is deliberately absent. The payout routes sit outside
 * `RequireApprovedFleet()`, and AL-49 is why: these documents are part of what
 * the Verification Officer reads **before** approving the organisation. Gating
 * them would mean approving an organisation before seeing the evidence you
 * approve it on.
 */

export interface PayoutActionState {
  /** The failure, already translated. */
  readonly message?: string;
  readonly field?: 'bank' | 'branch' | 'accountNo' | 'accountHolderName' | 'file';
  /** Set on success — what the screen confirms without a second read. */
  readonly saved?: true;
  /** Set on a successful upload, so the panel can name what landed. */
  readonly uploaded?: PayoutDocumentKind;
}

function text(formData: FormData, name: string): string {
  return String(formData.get(name) ?? '').trim();
}

/* ---------------------------------------------------------------------------
 * The bank details
 * ------------------------------------------------------------------------ */

/**
 * `PUT /v1/fleets/{id}/payout-profile` — create or edit, always landing in
 * `pending_verification`.
 *
 * The account-holder name is required and is **not** checked here beyond being
 * present. US-27.1 says it "must match the org / owner-KYC name", and the only
 * side that can decide that is the Verification Officer with the statement and
 * the KYC record in front of them; a portal that compared it against the
 * organisation's own `name` would refuse "R. M. Perera" against
 * "Lanka Transit (Pvt) Ltd" — a sole proprietor's real, correct account. The
 * requirement is stated on the field instead, where it is a instruction rather
 * than a rejection.
 */
export async function savePayoutProfile(
  _state: PayoutActionState,
  formData: FormData,
): Promise<PayoutActionState> {
  const t = await getTranslator();

  const bank = text(formData, 'bank');
  const branch = text(formData, 'branch');
  const accountNo = text(formData, 'accountNo');
  const accountHolderName = text(formData, 'accountHolderName');

  if (!bank) return { message: t('fleet.payout.error.bankRequired'), field: 'bank' };
  if (!branch) return { message: t('fleet.payout.error.branchRequired'), field: 'branch' };
  if (!accountNo) return { message: t('fleet.payout.error.accountRequired'), field: 'accountNo' };
  if (!accountHolderName) {
    return { message: t('fleet.payout.error.holderRequired'), field: 'accountHolderName' };
  }

  const body: PayoutProfileBody = { bank, branch, accountNo, accountHolderName };

  try {
    await mutate<PayoutProfile, PayoutProfileBody>({
      method: 'PUT',
      org: PAYOUT_PROFILE,
      body,
      // `fleet-billing` write is the Owner's alone (URD §2.1 narrows a Manager
      // out of the row), which is the same answer fleet-svc's
      // `RequireFleetSubRole(Owner)` gives — reached from the matrix rather than
      // from a role name.
      requires: { area: 'fleet-billing' },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  revalidatePath('/org/payout');
  revalidatePath('/org/setup');

  return { saved: true };
}

/* ---------------------------------------------------------------------------
 * The evidence
 * ------------------------------------------------------------------------ */

/**
 * `POST /v1/fleets/{id}/payout-profile/documents` — multipart, `kind` + `file`.
 *
 * The **file is not read here**. It is put on a `FormData` and handed to the data
 * layer, which sends it as the multipart body it already is; buffering it into
 * this process to check anything would put the bytes in memory on a hop whose
 * only job is to carry them, and every check worth making is fleet-svc's:
 * the kind against `PayoutDocumentKinds.All`, the size against
 * `Fleet:DocumentMaxBytes` **as the bytes arrive**, and the existence of a
 * profile to attach to.
 *
 * The size *is* compared to {@link DOCUMENT_MAX_BYTES} before sending, and that
 * is a courtesy rather than a gate — it turns an 8 MB upload that ends in a 413
 * into an immediate sentence. The dropzone makes the same comparison one step
 * earlier, before a byte leaves the browser.
 */
export async function uploadPayoutDocument(
  _state: PayoutActionState,
  formData: FormData,
): Promise<PayoutActionState> {
  const t = await getTranslator();

  const kind = text(formData, 'kind');
  if (!isPayoutDocumentKind(kind)) return { message: t('fleet.payout.error.kindRequired') };

  const file = formData.get('file');
  if (!(file instanceof File) || file.size === 0) {
    return { message: t('fleet.payout.error.fileRequired'), field: 'file' };
  }
  if (file.size > DOCUMENT_MAX_BYTES) {
    return {
      message: t('fleet.error.fileTooLarge', { megabytes: DOCUMENT_MAX_BYTES / (1024 * 1024) }),
      field: 'file',
    };
  }

  const upstream = new FormData();
  upstream.set('kind', kind);
  upstream.set('file', file);

  try {
    await mutate<PayoutDocumentRef, FormData>({
      method: 'POST',
      org: PAYOUT_DOCUMENTS,
      body: upstream,
      requires: { area: 'fleet-billing' },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    // "Submit the bank details first; a document is attached to a profile, not to
    // an organisation" — fleet-svc's own words, and a real order of operations
    // rather than a failure, so it gets a sentence that says what to do.
    if (error.code === 'payout-profile-not-found') {
      return { message: t('fleet.payout.error.profileFirst') };
    }
    return { message: t(error.messageKey) };
  }

  revalidatePath('/org/payout');

  return { saved: true, uploaded: kind };
}
