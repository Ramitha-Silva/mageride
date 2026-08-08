'use client';

import { ErrorPanel } from '@/components/ErrorPanel';
import { DEFAULT_LOCALE } from '@/i18n';

import './globals.css';

/**
 * The boundary for a failure in the **root layout itself** — the one place
 * `app/error.tsx` cannot reach, because the document it would render inside is
 * what failed.
 *
 * It therefore renders its own `<html>` and `<body>`, and imports the stylesheet
 * again: the root layout's import went down with it. `lang` is the platform
 * default rather than the member's choice, because at this point the locale
 * resolution is exactly what may have thrown — and {@link ErrorPanel} reads the
 * same attribute, so the panel and the document agree either way.
 */
export default function GlobalError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  return (
    <html lang={DEFAULT_LOCALE}>
      <body>
        <ErrorPanel {...(error.digest ? { digest: error.digest } : {})} onRetry={reset} />
      </body>
    </html>
  );
}
