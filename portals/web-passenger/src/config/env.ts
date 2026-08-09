/**
 * Every environment value the subview reads, resolved in one place and read at
 * request time rather than at module load.
 *
 * Read at request time on purpose: the page ships as one `output: 'standalone'`
 * image and the same image runs on the lightweight replica and on DOKS with
 * different values (CLAUDE.md, Infra). A value captured into a module constant is
 * captured at build, which would bake the replica's gateway address into the
 * production image.
 *
 * **There is no `NEXT_PUBLIC_*` variable here and there must not be one.** The
 * share token is already in the visitor's address bar, so hiding it from the
 * browser is not the point; keeping the *platform* out of the browser is. Every
 * call to `/public/track/**` leaves this Next server, which is what lets
 * `passenger.mageride.lk` be the only host a phone opened from an SMS ever talks
 * to — one origin, one TLS handshake, and no way for a page to be pointed at a
 * gateway by editing a script.
 */

/** Thrown when a required variable is missing. Surfaced as 503, never as a 500. */
export class MissingConfigurationError extends Error {
  constructor(readonly variable: string) {
    super(
      `${variable} is not set. The passenger web subview cannot reach public-bff without it ` +
        '(see portals/web-passenger/.env.example).',
    );
    this.name = 'MissingConfigurationError';
  }
}

/**
 * The C008 gateway origin. `/public/{**remainder}` reaches public-bff
 * (`backend/src/ApiGateway/gateway-routes.json`), which is the only service this
 * surface has, and the token in the path is the only credential it presents.
 */
export function apiBaseUrl(): string {
  const value = process.env.MAGERIDE_API_BASE_URL?.trim();
  if (!value) throw new MissingConfigurationError('MAGERIDE_API_BASE_URL');
  return value.replace(/\/+$/, '');
}

/**
 * The MapLibre **style document** the three map screens draw their basemap from —
 * D-14's `tile-cdn`, a Cloudflare R2 bucket of PMTiles behind a Worker that serves
 * range-byte requests (ADD §6, §10.2).
 *
 * The one URL in this application a **browser** fetches, and deliberately not the
 * platform: tiles are static cartography on a CDN, they carry nobody's ride and
 * take no credential. It reaches the map as a **prop**, not as a build-time public
 * variable, which is what keeps "the browser never learns where the gateway is"
 * true of this surface as well as of the other two portals.
 *
 * **Optional, and unset is a supported state.** The driver marker, the pins and
 * the route then render on an empty canvas and the screen says so — a missing
 * basemap must not read as a missing driver.
 */
export function mapStyleUrl(): string | null {
  return process.env.WEB_PASSENGER_MAP_STYLE_URL?.trim() || null;
}

/**
 * Where SCR-WT-006's "Open MageRide" and the two "Get the app" strips point.
 *
 * Two variables rather than one because the two stores are two URLs and the
 * visitor is on one of the two phones. **Both optional**: with neither set the
 * control is not drawn at all rather than drawn and dead — the wireframe's own
 * copy already tells the reader to ask the sender for a new link, so the dead end
 * is still a complete page without a store button on it.
 */
export interface AppDownloadLinks {
  readonly android: string | null;
  readonly ios: string | null;
}

export function appDownloadLinks(): AppDownloadLinks {
  return {
    android: process.env.WEB_PASSENGER_ANDROID_APP_URL?.trim() || null,
    ios: process.env.WEB_PASSENGER_IOS_APP_URL?.trim() || null,
  };
}

/**
 * The store link for the phone that is asking, or `null` when neither store is
 * configured.
 *
 * A `User-Agent` is a hint and is treated as one: an iOS marker picks the Apple
 * link, everything else prefers the Play link, and either falls back to whichever
 * one exists. Nothing depends on getting this right — a visitor sent to the wrong
 * store sees the wrong store, which is a worse link and not a broken page.
 */
export function appDownloadUrl(userAgent: string | null | undefined): string | null {
  const { android, ios } = appDownloadLinks();
  const isApple = /iphone|ipad|ipod|ios/i.test(userAgent ?? '');

  return (isApple ? (ios ?? android) : (android ?? ios)) ?? null;
}
