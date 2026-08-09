import type { Metadata, Viewport } from 'next';
import { Inter, Outfit } from 'next/font/google';

import { getNegotiatedLocale } from '@/i18n/server';

import './globals.css';

/**
 * The document. Language, appearance and fonts — the three things every one of the
 * six SCR-WT pages shares and none of them can decide for itself.
 *
 * **The fonts are self-hosted, not linked.** D2 §0.2 names Outfit (display) and
 * Inter (body); `next/font/google` downloads and serves both from this origin, and
 * the CSS variables it mints are exactly the ones the preset's type tokens resolve
 * first (`var(--mr-font-outfit, 'Outfit')`). A CDN `<link>` would put a third
 * party in the render path of a page opened from an SMS, and is the one thing
 * incompatible with the strict-CSP posture AL-52 was chosen for. Neither face
 * carries Sinhala or Tamil glyphs and neither is asked to: those subsets fall
 * through to the phone's own system face, which is what a Sinhala reader already
 * reads everything else in.
 *
 * **There is no theme cookie and no stored preference.** D6' I-29.1 gives this
 * surface "no cookies, no localStorage of ride data", so appearance follows the
 * operating system and nothing else. The preset drives light/dark off the `.dark`
 * class, and the small script below is the only thing that sets it — a class
 * toggle, no CSS, so AL-52 is untouched.
 */

const outfit = Outfit({
  subsets: ['latin'],
  variable: '--mr-font-outfit',
  display: 'swap',
});

const inter = Inter({
  subsets: ['latin'],
  variable: '--mr-font-inter',
  display: 'swap',
});

export const metadata: Metadata = {
  title: 'MageRide',
  description: 'Track a MageRide delivery or ride — no app, no login.',
  // Every URL on this host carries somebody's live share token. A crawler that
  // indexed one would publish a credential; a crawler that *followed* one would
  // spend that token's rate budget and, on a `pickup_confirm` link, burn it.
  robots: { index: false, follow: false, nocache: true },
};

export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
  // D2 §0.2 `primary`. Colours the browser's own chrome on Android so the page
  // does not sit under a white strip above its orange bar.
  themeColor: '#FF6D00',
};

/**
 * Applies the OS appearance before the first paint.
 *
 * A server cannot know `prefers-color-scheme` — the browser learns it as this
 * document parses — and the alternative to eight lines running before the body is
 * a flash of the wrong theme on every page load.
 */
const APPEARANCE_SCRIPT = `
(function(){try{
  var root=document.documentElement;
  var query=window.matchMedia('(prefers-color-scheme: dark)');
  var apply=function(dark){root.classList.toggle('dark',dark)};
  apply(query.matches);
  query.addEventListener('change',function(e){apply(e.matches)});
}catch(e){}})();
`;

export default async function RootLayout({ children }: { children: React.ReactNode }) {
  const locale = await getNegotiatedLocale();

  return (
    <html lang={locale} className={`${outfit.variable} ${inter.variable}`} suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: APPEARANCE_SCRIPT }} />
      </head>
      {/*
        `min-h-dvh` and not `min-h-screen`: on a phone `100vh` is the viewport with
        the browser's collapsing address bar counted out, so a full-height layout
        jumps as the bar hides. This page is a phone page first.

        `overflow-x-clip` and not `overflow-x-hidden`: the two look identical and
        only one of them is safe here. `hidden` makes the body a scroll container,
        which is what silently breaks `position: sticky` on a descendant — and the
        brand bar is sticky. `clip` refuses the overflow without becoming one.
        It is a backstop rather than the plan: the layout is a single 480px-capped
        column and nothing in it has a fixed width above 375px.
      */}
      <body className="min-h-dvh overflow-x-clip">{children}</body>
    </html>
  );
}
