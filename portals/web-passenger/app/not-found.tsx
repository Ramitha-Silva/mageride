import { getNegotiatedLocale, translatorFor } from '@/i18n/server';

/**
 * A URL no SCR-WT screen claims.
 *
 * Deliberately **not** SCR-WT-006. "This link has expired" is a statement about a
 * token, and a reader who is told it about `/favicon.ico` or a path a link shortener
 * mangled would go and ask the sender for a new link that would work no better. The
 * two pages say two different things because two different things went wrong.
 */
export default async function NotFound() {
  const locale = await getNegotiatedLocale();
  const t = translatorFor(locale);

  return (
    <main className="mx-auto grid min-h-dvh max-w-[480px] place-items-center bg-background p-md">
      <div className="flex flex-col items-center gap-sm text-center">
        <h1 className="text-title font-display">{t('web.notFound.title')}</h1>
        <p className="text-body-sm text-on-surface-variant">{t('web.notFound.body')}</p>
      </div>
    </main>
  );
}
