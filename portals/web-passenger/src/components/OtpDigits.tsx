import type { WebMessageKey, WebTranslator } from '@/i18n';

/**
 * The four-box code card the wireframe draws twice: SCR-WT-002's **Delivery OTP**
 * (US-20.5, P-07) and SCR-WT-004's **Start OTP**.
 *
 * The boxes are decoration and the code is a single accessible string. Four
 * separate `<span>`s read out as "seven, three, one, five" by a screen reader is
 * four numbers rather than one code, and the reader has to say it aloud to a
 * driver in one breath.
 *
 * **A missing code renders the caption, never four empty boxes.** public-bff emits
 * `deliveryOtp` only while the parcel is actually aboard — a code shown before
 * pickup has not been issued to the driver yet, and one shown after delivery is a
 * live credential on a finished job — and it emits no `startOtp` at all, on any
 * ride, because no endpoint on the platform issues one (`ride.yaml`: "accepted and
 * ignored in this build"). Empty boxes would suggest a code that is loading.
 */
export function OtpDigits({
  t,
  titleKey,
  pendingKey,
  code,
}: {
  t: WebTranslator;
  titleKey: WebMessageKey;
  /** The sentence shown in place of the boxes when there is no code. */
  pendingKey: WebMessageKey;
  code: string | undefined;
}) {
  if (!code) {
    return (
      <p className="rounded-md border border-outline bg-surface p-sm text-center text-caption text-on-surface-variant">
        {t(pendingKey)}
      </p>
    );
  }

  return (
    <div className="rounded-md border border-outline bg-surface p-sm text-center">
      <p className="text-label font-semibold text-on-surface-variant">{t(titleKey)}</p>
      <p
        aria-label={t('web.package.otpValue', { code: [...code].join(' ') })}
        className="mt-xs flex justify-center gap-xs"
      >
        {[...code].map((digit, index) => (
          <span
            // The digits of a fixed-length code have no identity beyond their
            // position, and two of them can legitimately be the same character.
            key={index}
            aria-hidden="true"
            className="grid h-[50px] w-[42px] place-items-center rounded-sm border-[1.5px] border-primary bg-background text-headline font-display font-bold tabular-nums"
          >
            {digit}
          </span>
        ))}
      </p>
    </div>
  );
}
