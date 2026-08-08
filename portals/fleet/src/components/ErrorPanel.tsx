'use client';

import { createFleetTranslator, DEFAULT_LOCALE, isLocale } from '@/i18n';

/**
 * The body of a render that threw — used by `app/error.tsx` and
 * `app/global-error.tsx`.
 *
 * **It resolves its own language, which nothing else on the client does.** An
 * error boundary is a client component and takes no props, so the usual
 * arrangement — the server translates, the client renders — is unavailable on
 * exactly the page where an operator most needs to understand what happened. So
 * this one reads the locale off `<html lang>`, which the root layout set from the
 * same four sources every other page uses, and looks the message up itself. The
 * cost is the three locale tables in the client bundle; the alternative is an
 * English-only error screen on a Sinhala-first platform.
 *
 * `digest` is shown because it is the only handle support has: Next replaces the
 * message of a server-side error with an opaque hash before it reaches the
 * browser, on purpose, and that hash is what appears beside the real stack in the
 * server log.
 */
export function ErrorPanel({ digest, onRetry }: { digest?: string; onRetry: () => void }) {
  const t = createFleetTranslator(currentLocale());

  return (
    <main className="grid min-h-dvh place-items-center bg-surface p-md">
      <div className="flex max-w-[520px] flex-col items-center gap-sm rounded-card border border-outline bg-background p-lg text-center shadow-card">
        <h1 className="text-headline font-display">{t('fleet.error.title')}</h1>
        <p className="text-body-sm text-on-surface-variant">{t('fleet.error.unexpected')}</p>

        {digest ? (
          <p className="text-caption text-outline-variant">
            {t('fleet.error.reference', { traceId: digest })}
          </p>
        ) : null}

        <button
          type="button"
          onClick={onRetry}
          className="rounded-sm bg-primary px-md py-xs text-body-sm font-semibold text-on-primary hover:bg-primary/90"
        >
          {t('common.retry')}
        </button>
      </div>
    </main>
  );
}

function currentLocale() {
  if (typeof document === 'undefined') return DEFAULT_LOCALE;
  const lang = document.documentElement.lang;
  return isLocale(lang) ? lang : DEFAULT_LOCALE;
}
