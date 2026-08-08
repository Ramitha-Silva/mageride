import Link from 'next/link';

import { getTranslator } from '@/i18n/server';

/** The body of every 404 — a URL no Admin Portal screen claims. */
export default async function NotFound() {
  const t = await getTranslator();

  return (
    <main className="grid min-h-dvh place-items-center bg-surface p-md">
      <div className="flex max-w-[520px] flex-col items-center gap-sm rounded-card border border-outline bg-background p-lg text-center shadow-card">
        <h1 className="text-headline font-display">{t('admin.notFound.title')}</h1>
        <p className="text-body-sm text-on-surface-variant">{t('admin.notFound.body')}</p>
        <Link
          href="/"
          className="rounded-sm bg-primary px-md py-xs text-body-sm font-semibold text-on-primary hover:bg-primary/90"
        >
          {t('admin.denied.back')}
        </Link>
      </div>
    </main>
  );
}
