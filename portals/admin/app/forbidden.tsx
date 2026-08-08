import Link from 'next/link';

import { getTranslator } from '@/i18n/server';

/**
 * The body of every **403** the portal produces — from `proxy.ts`'s rewrite, and
 * from any screen that calls `forbidden()` itself.
 *
 * It says which door is shut and who opens it, and nothing about what is behind
 * it. A refusal that named the module, described its contents or listed the roles
 * that hold it would make the 403 an oracle over the shape of the back-office —
 * the same reason admin-bff drops an empty nav group rather than rendering a
 * heading somebody cannot reach.
 */
export default async function Forbidden() {
  const t = await getTranslator();

  return (
    <main className="grid min-h-dvh place-items-center bg-surface p-md">
      <div className="flex max-w-[520px] flex-col items-center gap-sm rounded-card border border-outline bg-background p-lg text-center shadow-card">
        <h1 className="text-headline font-display">{t('admin.denied.title')}</h1>
        <p className="text-body-sm text-on-surface-variant">{t('admin.denied.body')}</p>
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
