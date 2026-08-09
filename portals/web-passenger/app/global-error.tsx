'use client';

import { Button } from '@mageride/ui';

import { createWebTranslator, DEFAULT_LOCALE } from '@/i18n';

import './globals.css';

/**
 * The boundary for a failure in the **root layout itself** — the one place
 * `app/error.tsx` cannot reach, because the document it would render inside is what
 * failed.
 *
 * It therefore renders its own `<html>` and `<body>`, and imports the stylesheet
 * again: the root layout's import went down with it. `lang` is the platform default
 * (`si`, D1' §283) rather than a negotiated locale, because at this point the
 * locale resolution is exactly what may have thrown.
 */
export default function GlobalError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  const t = createWebTranslator(DEFAULT_LOCALE);

  return (
    <html lang={DEFAULT_LOCALE}>
      <body>
        <main className="mx-auto grid min-h-dvh max-w-[480px] place-items-center bg-background p-md">
          <div className="flex w-full flex-col items-center gap-sm text-center">
            <h1 className="text-title font-display">{t('web.error.title')}</h1>
            <p className="text-body-sm text-on-surface-variant">{t('web.error.unexpected')}</p>
            {error.digest ? (
              <p className="text-caption text-on-surface-variant">
                {t('web.error.reference', { traceId: error.digest })}
              </p>
            ) : null}
            <Button onClick={reset} className="w-full">
              {t('web.error.retry')}
            </Button>
          </div>
        </main>
      </body>
    </html>
  );
}
