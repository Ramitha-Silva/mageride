import { TokenGate } from '@/components/TokenGate';
import { getNegotiatedLocale, translatorFor } from '@/i18n/server';

/**
 * The ≤1 s spinner D2 §SCR-WT-001 asks for, as Next's Suspense fallback for this
 * route.
 *
 * It resolves the language off `Accept-Language` alone and not off `?lang=`: a
 * fallback that awaited the page's own search params would be a fallback that
 * blocked on the thing it exists to cover for. One second in the wrong language is
 * a better trade than a second of blank page, and the resolved render underneath
 * corrects it.
 */
export default async function Loading() {
  const locale = await getNegotiatedLocale();
  return <TokenGate t={translatorFor(locale)} locale={locale} />;
}
