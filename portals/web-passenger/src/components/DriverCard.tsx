import type { PublicDriver } from '@/api/types';
import type { WebTranslator } from '@/i18n';
import { isWebMessageKey } from '@/i18n';

/**
 * The driver, as a non-app viewer sees them — shared by SCR-WT-002 and SCR-WT-004.
 *
 * ## The call control is a `tel:` link, and there is nothing behind it
 *
 * AL-48 withdrew the number-masking requirement **in full**: `POST
 * /public/track/{token}/call`, the ride-scoped proxy-DID lease, the CPaaS bridge
 * and the confirm-your-number step were all removed, and the snapshot carries the
 * driver's real MSISDN so the page can dial it (US-26.3, D2 Δ 2026-07-05 #2).
 *
 * So this is an `<a href="tel:…">` and it must stay one. It makes **no request to
 * MageRide** — the browser hands the number to the dialler and nothing on the
 * platform is told, which is also why `comms.call_log` describes a web call as
 * best-effort and why there is no confirmation step to render. public-bff's
 * start-up guard refuses to map any route whose path contains `/call`, by name, so
 * the endpoint this control does not call cannot come back either.
 *
 * With no number on the snapshot the control is **not drawn** and a sentence takes
 * its place. A dead `tel:` link on a phone opens the dialler with nothing in it,
 * which reads as the platform having lost the driver.
 *
 * ## The photograph
 *
 * A plain `<img>` rather than `next/image`. The URL is a signed object-store link
 * public-bff minted, on a host this deployment does not know at build time and
 * cannot list in `images.remotePatterns`; routing it through the optimiser would
 * put a passenger-facing pod in the path of every avatar and re-fetch a URL that
 * expires. It is sized and cropped by CSS, so nothing about the layout depends on
 * the file.
 */
export function DriverCard({
  t,
  driver,
  /** SCR-WT-004 draws the full-width bordered card; SCR-WT-002 draws a plain row. */
  variant = 'row',
  /** SCR-WT-002's line under the name — the plate, and whatever else the screen knows. */
  secondary,
  /**
   * Whether the card carries the call control.
   *
   * `false` on SCR-WT-004, where the wireframe puts "📞 Call driver" in the button
   * row beside SOS instead. Two controls dialling the same number would be two
   * things to find in an emergency and one of them redundant — and, to a screen
   * reader, the same accessible name twice on one page.
   */
  showCall = true,
}: {
  t: WebTranslator;
  driver: PublicDriver;
  variant?: 'row' | 'card';
  secondary?: string | undefined;
  showCall?: boolean;
}) {
  const vehicleKey = `web.vehicle.${driver.vehicleType ?? ''}`;
  const vehicleName = isWebMessageKey(vehicleKey) ? t(vehicleKey) : t('web.vehicle.unknown');

  const line =
    secondary ??
    t('web.driver.vehicleLine', { type: vehicleName, reg: driver.regNo });

  return (
    <div
      className={
        variant === 'card'
          ? 'flex items-center gap-sm rounded-md border border-outline bg-background p-sm'
          : 'flex items-center gap-sm'
      }
    >
      {driver.photo ? (
        <img
          src={driver.photo}
          alt={t('web.driver.photoAlt', { name: driver.name })}
          className="size-10 shrink-0 rounded-full object-cover"
        />
      ) : (
        <span
          aria-hidden="true"
          className="grid size-10 shrink-0 place-items-center rounded-full bg-surface-variant text-subtitle text-on-surface-variant"
        >
          👤
        </span>
      )}

      <div className="flex min-w-0 flex-1 flex-col">
        <span className="truncate text-body font-semibold">{driver.name}</span>
        <span className="truncate text-caption text-on-surface-variant">{line}</span>
      </div>

      {!showCall ? null : driver.phone ? (
        <a
          href={`tel:${driver.phone}`}
          aria-label={t('web.driver.callAria', { name: driver.name, phone: driver.phone })}
          className="flex shrink-0 items-center gap-xxs rounded-sm px-xs py-xxs text-body-sm font-semibold text-primary"
        >
          <span aria-hidden="true">📞</span>
          {t(variant === 'card' ? 'web.driver.callDriver' : 'web.driver.call')}
        </a>
      ) : (
        <span className="shrink-0 text-caption text-on-surface-variant">{t('web.driver.noPhone')}</span>
      )}
    </div>
  );
}
