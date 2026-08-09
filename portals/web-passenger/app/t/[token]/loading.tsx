import { TokenGate } from '@/components/TokenGate';
import { getNegotiatedLocale, translatorFor } from '@/i18n/server';

/** The same ≤1 s gate as `/track`. See `app/track/loading.tsx`. */
export default async function Loading() {
  const locale = await getNegotiatedLocale();
  return <TokenGate t={translatorFor(locale)} locale={locale} />;
}
