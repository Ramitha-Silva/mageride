import type { MetadataRoute } from 'next';

import { SITE_ORIGIN } from '@/lib/seo';

/**
 * `/robots.txt` — **the inverse of every other MageRide portal's.**
 *
 * `admin`, `fleet` and the passenger subview all serve `Disallow: /`, each for its
 * own reason: an operator console has no public reading, and every URL on the
 * subview is somebody's live share token. Nothing here is anybody's data — every
 * page is pre-rendered marketing copy and documentation — and being found is the
 * entire point of the surface.
 *
 * ## This replaced `public/robots.txt`, and both could not exist
 *
 * S03 shipped a static file and left the choice to S19, correctly noting that
 * **Next fails the build on a public file that collides with a metadata route**.
 * The static one is deleted. The reason to prefer this one is not that the sitemap
 * URL needs composing — it does not, `SITE_ORIGIN` is a constant and has to be
 * (see `src/lib/seo.ts`) — it is that **the sitemap URL is now written once**.
 * A static `robots.txt` naming `https://www.mageride.lk/sitemap.xml` is a second
 * copy of the canonical origin that no test reads and no compiler checks, and the
 * failure mode of the two disagreeing is silent: a crawler pointed at a sitemap on
 * the wrong host finds nothing and reports nothing.
 *
 * ## `/` is crawlable even though it redirects
 *
 * It negotiates a locale from `Accept-Language` and 307s. A crawler that follows it
 * lands on a canonical `/si` or `/en` that the sitemap also lists, which is the
 * outcome wanted; disallowing it would make the bare host look closed.
 *
 * ## There is nothing to disallow, and the empty `disallow` is the point
 *
 * Until S20 this file excluded `/{locale}/_motion-demo`, S04's motion workbench —
 * unlisted was enough for a crawler that follows links and not enough for one that
 * guesses. **S20 deleted the workbench, so the rule went with it.** A `Disallow`
 * naming a path that returns 404 is worse than no rule: `robots.txt` is the one
 * public file on the site whose whole content is a list of URLs, so a stale entry
 * *advertises* the address it was added to hide, to everyone, forever.
 *
 * The correct state for this surface is therefore `Allow: /` and nothing else —
 * every page here is meant to be found, which is the inverse of what the other
 * three portals serve. If a future session adds an unpublished page it belongs in
 * `UNPUBLISHED_PAGES` in `test/routes.test.ts` **and** here, in the same change.
 */
export default function robots(): MetadataRoute.Robots {
  return {
    rules: {
      userAgent: '*',
      allow: '/',
    },
    sitemap: `${SITE_ORIGIN}/sitemap.xml`,
    host: SITE_ORIGIN,
  };
}
