import type { PublicDriver, Receipt } from '@/api/types';
import { formatAmountMinor, formatClock, formatInstant } from '@/i18n/format';
import type { Locale, WebMessageKey, WebTranslator } from '@/i18n';
import { isWebMessageKey } from '@/i18n';

import { PrintButton } from './PrintButton';
import { Shell } from './Shell';

/**
 * **SCR-WT-005 — delivered / receipt.** US-25.6, P-07/P-08/P-10/P-14.
 *
 * The four outcomes US-25.6 names, and public-bff resolves which one applies rather
 * than this page: **a dispute outranks the money and the money outranks the
 * evidence**, because a receipt claiming a successful handover on a disputed
 * delivery would contradict the ledger, and a COD parcel that was both photographed
 * and paid for reports the payment — that is the question a receipt is opened to
 * answer.
 *
 * ## Delivered and receiptable are two different facts, and the screen shows both
 *
 * A parcel reaches `Delivered` at `Completed`, and public-bff's `Receiptable` is
 * narrower than "terminal" — `Paid`, `CashSettled`, `CashOnDeliveryCollected`,
 * `Disputed`. In between, the handover has happened and the money has not settled,
 * and `GET …/receipt` answers `409 receipt-not-ready`. That is a normal state
 * rather than an error, so the screen renders the delivery with a sentence where
 * the figures will be. A page that showed an error there would be telling a
 * recipient something went wrong with a parcel they are holding.
 *
 * ## The sender comes from the snapshot, because a receipt has no room for them
 *
 * `public-bff.yaml#Receipt` carries the kind, the state, the money, the proof, the
 * driver and the instant — and no sender, because a receipt is a document about a
 * completed job rather than about who arranged it. The wireframe's "From" line is
 * `senderNameMasked` off the same snapshot that told this page the parcel had
 * arrived, which the page has already read.
 *
 * ## There is no `driver.phone` here, and that is deliberate upstream
 *
 * AL-48's `tel:` link exists so a recipient can reach a driver who is on the way to
 * them. Once the parcel is delivered there is nothing to call about, and a receipt
 * is a document that gets forwarded — so public-bff omits the number and this
 * screen draws no call control.
 */

const PROOF_COPY: Readonly<Record<Receipt['proof'], WebMessageKey>> = {
  otp_verified: 'web.receipt.otpVerified',
  photo_proof: 'web.receipt.photoProof',
  cod_collected: 'web.receipt.codCollected',
  disputed: 'web.receipt.disputedBody',
};

export function DeliveredReceipt({
  t,
  locale,
  here,
  kind,
  driver,
  senderName,
  receipt,
  appUrl,
}: {
  t: WebTranslator;
  locale: Locale;
  here: string;
  /**
   * Which journey ended.
   *
   * A parcel and a ride share this screen because they share the endpoint: US-25.6's
   * four `proof` values are a delivery's vocabulary, and public-bff applies them to
   * both — `disputed` and `cod_collected` are genuine on a ride, and `otp_verified`
   * degenerates to "the journey ended the ordinary way", which the state beside it
   * says precisely. What differs is the heading and the app strip: "Send packages
   * yourself" under a finished taxi ride would be an advertisement for the wrong
   * product.
   */
  kind: 'package' | 'ride';
  /** From the snapshot, because a receipt carries no phone number (see above). */
  driver: PublicDriver;
  /** A parcel's sender display name. Absent on a ride, and on a parcel P-09 may omit it. */
  senderName?: string | undefined;
  /** `null` while the handover is done and the payment has not settled. */
  receipt: Receipt | null;
  appUrl: string | null;
}) {
  const disputed = receipt?.proof === 'disputed';
  const handedOverAt = formatClock(locale, receipt?.completedAt);
  const vehicleKey = `web.vehicle.${driver.vehicleType ?? ''}`;

  return (
    <Shell
      t={t}
      locale={locale}
      titleKey={kind === 'ride' ? 'web.bar.ride' : 'web.bar.delivered'}
      here={here}
      {...(kind === 'package' ? { appStrip: { promptKey: 'web.app.sendPrompt' as const, href: appUrl } } : {})}
      centred
    >
      <span
        aria-hidden="true"
        className={`grid size-[78px] place-items-center rounded-full text-[36px] ${
          disputed ? 'bg-warning/15 text-warning' : 'bg-success/12 text-success'
        }`}
      >
        {disputed ? '!' : '✓'}
      </span>

      <h2 className="text-headline font-display">
        {t(
          disputed
            ? 'web.receipt.disputedTitle'
            : kind === 'ride'
              ? 'web.receipt.rideTitle'
              : 'web.receipt.deliveredTitle',
        )}
      </h2>

      {receipt ? (
        <p className="text-body-sm text-on-surface-variant">
          {t(PROOF_COPY[receipt.proof], { time: handedOverAt ?? '' })}
        </p>
      ) : (
        <p className="text-body-sm text-on-surface-variant">{t('web.receipt.settling')}</p>
      )}

      {receipt?.proofPhotoUrl ? (
        // P-10's doorstep photograph. A plain `<img>`: the URL is a signed
        // object-store link public-bff minted, on a host this deployment does not
        // know at build time and cannot list in `images.remotePatterns` — and it
        // expires, so routing it through the optimiser would cache bytes that stop
        // resolving.
        <img
          src={receipt.proofPhotoUrl}
          alt={t('web.receipt.photoAlt')}
          className="w-full rounded-md border border-outline object-cover"
        />
      ) : null}

      <dl className="w-full rounded-md border border-outline bg-surface p-sm text-start print-plain">
        {senderName ? <Row label={t('web.receipt.from')} value={senderName} /> : null}
        <Row
          label={t('web.receipt.driver')}
          value={
            isWebMessageKey(vehicleKey)
              ? t('web.driver.vehicleLine', { type: t(vehicleKey), reg: driver.regNo })
              : driver.name
          }
        />
        {receipt ? (
          <Row
            label={t('web.receipt.completed')}
            value={formatInstant(locale, receipt.completedAt) ?? ''}
          />
        ) : null}
        {receipt?.totalMinor !== undefined ? (
          <Row
            label={t('web.receipt.payment')}
            value={t(
              receipt.proof === 'cod_collected'
                ? 'web.receipt.paymentCod'
                : 'web.receipt.paymentAmount',
              { amount: formatAmountMinor(locale, receipt.totalMinor) },
            )}
          />
        ) : null}
      </dl>

      {receipt ? <PrintButton label={t('web.receipt.download')} /> : null}

      <p className="text-caption text-on-surface-variant">{t('web.receipt.closed')}</p>
    </Shell>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between gap-sm py-xxs text-body-sm">
      <dt className="text-on-surface-variant">{label}</dt>
      <dd className="truncate text-end font-semibold">{value}</dd>
    </div>
  );
}
