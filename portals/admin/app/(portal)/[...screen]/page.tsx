import { ScreenPlaceholder } from '@/components/ScreenPlaceholder';

/**
 * The catch-all every not-yet-built screen falls through to.
 *
 * The body is `<ScreenPlaceholder>` rather than this file, because it is no
 * longer only this route that needs it: a screen component whose path gained a
 * dynamic segment out-ranks this catch-all for its siblings' URLs too, and has to
 * be able to hand them back (Δ C106 — see that component's own note).
 */

export const dynamic = 'force-dynamic';

export default async function ScreenPlaceholderPage({
  params,
}: {
  params: Promise<{ screen: string[] }>;
}) {
  const { screen } = await params;

  return <ScreenPlaceholder pathname={`/${screen.join('/')}`} />;
}
