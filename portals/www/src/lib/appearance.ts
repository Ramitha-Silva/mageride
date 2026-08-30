/**
 * The appearance preference — the key, the resolution order, and the script that
 * applies it before the first paint.
 *
 * ## `localStorage` is permitted here and on no other MageRide surface
 *
 * `portals/web-passenger/app/layout.tsx` says in its own words that it has **no**
 * theme cookie and no stored preference, because D6′ I-29.1 gives that surface *"no
 * cookies, no localStorage of ride data"* — it holds somebody's live ride, reached
 * from an SMS, on a device that may not be theirs.
 *
 * This page holds nothing. It is public, unauthenticated, carries no personal data
 * and has no API dependency at request time (MCS-34's four negatives), so a
 * remembered appearance is both safe and what a reader expects from a site they
 * browse.
 *
 * **And it is not a cookie.** Nothing here is sent to a server — not in a header,
 * not in a request body, never. There is nothing for a server to receive, nothing
 * to correlate a reader with, and therefore nothing to consent to: the no-cookie
 * fence and the no-banner claim on `/legal/privacy` are both intact. A later reader
 * of this file should not "fix" it into a cookie, and `portals/www/CLAUDE.md`
 * records that too.
 *
 * ## Resolution order: stored → `prefers-color-scheme` → light
 *
 * S19 states it and each step earns its place. A stored preference wins because a
 * reader who pressed the button meant it, and an OS setting that overrode a
 * deliberate press would make the button look broken. `prefers-color-scheme` is the
 * answer for the reader who has never pressed anything. Light is the floor, for the
 * browser that reports no preference at all.
 *
 * ## Why this runs as a blocking inline script
 *
 * A server cannot know either input: `localStorage` and `matchMedia` are the
 * browser's, and the browser learns them as this document parses. The alternative
 * to a few lines running before `<body>` is a flash of the wrong theme on every
 * page load — worse in dark, where it is a full-screen white flash on a phone at
 * night.
 *
 * It is deliberately tiny and deliberately un-bundled. It cannot `import`, it must
 * not throw, and every storage access is wrapped: reading `localStorage` **throws**
 * in Safari's private mode and wherever site data is blocked, and an exception here
 * would leave the page unstyled rather than merely un-themed.
 */

/**
 * The storage key. Namespaced by surface, because the other portals share an origin
 * only in development and a bare `theme` is the kind of key two applications
 * collide on.
 */
export const APPEARANCE_STORAGE_KEY = 'mr-www-appearance';

/** The two values ever written. Anything else in storage is treated as absent. */
export type Appearance = 'light' | 'dark';

/**
 * The pre-paint script, built around the key above so the two cannot drift.
 *
 * The `change` listener re-reads storage on every OS change rather than capturing
 * the value once: a reader who presses the toggle and *then* changes their system
 * setting must keep their own choice, and a closure over the value read at load
 * would silently stop honouring a preference set later in the same visit.
 */
export function appearanceScript(): string {
  return `
(function(){try{
  var root=document.documentElement;
  var key=${JSON.stringify(APPEARANCE_STORAGE_KEY)};
  var read=function(){try{return window.localStorage.getItem(key)}catch(e){return null}};
  var query=window.matchMedia('(prefers-color-scheme: dark)');
  var apply=function(dark){root.classList.toggle('dark',dark)};
  var stored=read();
  apply(stored==='dark'||(stored!=='light'&&query.matches));
  query.addEventListener('change',function(e){
    var current=read();
    if(current!=='dark'&&current!=='light')apply(e.matches);
  });
}catch(e){}})();
`.trim();
}
