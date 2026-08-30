import { RolePage } from '@/components/pages/RolePage';
import { localeFrom, type LocaleParams } from '@/lib/params';
import { metadataForRoute } from '@/lib/seo';

/**
 * `/{locale}/passengers` — one of the four role landing pages.
 *
 * **The layout is `src/components/pages/RolePage.tsx` and the difference between
 * the four is `src/content/roles.ts`** (S16). A bespoke layout here would be one
 * of four independent ways to fail S19's Lighthouse gate, which runs on
 * `/drivers`.
 */
export async function generateMetadata({ params }: { params: Promise<LocaleParams> }) {
  return metadataForRoute(await localeFrom(params), 'passengers');
}

export default async function PassengersPage({ params }: { params: Promise<LocaleParams> }) {
  return <RolePage locale={await localeFrom(params)} path="passengers" />;
}
