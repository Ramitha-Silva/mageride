import type { Locale, WebTranslator } from '@/i18n';

import { Shell } from './Shell';

/**
 * **SCR-WT-001's gate, as the reader sees it while the token is being redeemed.**
 *
 * D2 §SCR-WT-001 asks for a "loading spinner ≤ 1 s; no data rendered before
 * validation", and those two clauses pull in opposite directions on most
 * architectures: a spinner usually means the page shipped first and fetched
 * afterwards, which is the arrangement in which data *can* arrive before it has
 * been validated.
 *
 * Streaming SSR is what makes both true at once. Next renders this as the Suspense
 * fallback for the route while the server's own `GET /public/track/{token}` is in
 * flight, so the reader gets a branded frame and a spinner immediately — and the
 * first byte of anything about a ride is written only after public-bff has said the
 * token is live. **There is no render in which this page holds ride data**, because
 * this component takes none: a translator, a locale, and nothing else.
 *
 * `role="status"` rather than an ARIA live region with a busy attribute: this
 * replaces itself, and a screen reader should hear "Checking your link" once.
 */
export function TokenGate({ t, locale }: { t: WebTranslator; locale: Locale }) {
  return (
    <Shell t={t} locale={locale} titleKey="web.appName" here="/" centred>
      <span
        aria-hidden="true"
        className="block size-10 animate-spin rounded-full border-2 border-outline border-t-primary"
      />
      <p role="status" className="text-body-sm text-on-surface-variant">
        {t('web.loading.title')}
      </p>
    </Shell>
  );
}
