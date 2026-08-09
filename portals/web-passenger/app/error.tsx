'use client';

import { useSyncExternalStore } from 'react';

import { Button } from '@mageride/ui';

import { createWebTranslator, DEFAULT_LOCALE, isLocale, type Locale } from '@/i18n';

/**
 * The boundary for anything that throws below the root layout — in practice the
 * `503` a `ProblemError` becomes when public-bff or the gateway is unreachable,
 * which the pages deliberately do not swallow.
 *
 * That distinction is the point of letting it get this far. A dead **token** is
 * SCR-WT-006, a screen with an explanation and a next step. A dead **platform** is
 * not the reader's link having expired, and telling them it was would send them to
 * ask the sender for a new one that would fail in exactly the same way. This says
 * the service cannot be reached and offers the retry that will work when it can.
 *
 * The locale is read off `<html lang>` rather than negotiated: an error boundary is
 * a client component, it has no headers, and the document it is rendering into
 * already carries the answer the server worked out.
 *
 * `useSyncExternalStore` with a subscribe that never fires is the shape React
 * provides for exactly that — a value that exists in the DOM on the client and not
 * on the server. It renders the server snapshot during hydration and the DOM's
 * answer immediately after, so there is no mismatch to suppress and no `setState`
 * in an effect to cascade a render.
 */

/** Nothing ever changes `<html lang>` inside one document, so nothing subscribes. */
const NEVER = () => () => {};

function documentLocale(): Locale {
  const declared = document.documentElement.lang;
  return isLocale(declared) ? declared : DEFAULT_LOCALE;
}

export default function ErrorBoundary({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  const locale = useSyncExternalStore(NEVER, documentLocale, () => DEFAULT_LOCALE);
  const t = createWebTranslator(locale);

  return (
    <main className="mx-auto grid min-h-dvh max-w-[480px] place-items-center bg-background p-md">
      <div className="flex w-full flex-col items-center gap-sm text-center">
        <h1 className="text-title font-display">{t('web.error.title')}</h1>
        <p className="text-body-sm text-on-surface-variant">{t('web.error.serviceUnavailable')}</p>
        {/*
          The digest, and never `error.message`. Next replaces a server error's
          message with a digest in production precisely so an internal string cannot
          reach a browser, and this surface's browsers belong to people with no
          MageRide account at all.
        */}
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
  );
}
