import type { Metadata, Viewport } from 'next';
import { Inter, Outfit } from 'next/font/google';

import { getAppearance, getLocale } from '@/i18n/server';

import './globals.css';

/**
 * The document. Language, appearance, fonts — the three things every page below
 * shares and none of them can decide for itself.
 *
 * **The fonts are self-hosted, not linked.** D2 §0.2 names Outfit (display) and
 * Inter (body); `next/font/google` downloads and serves both from this origin, and
 * the CSS variables it mints are exactly the ones the preset's type tokens resolve
 * first (`var(--mr-font-outfit, 'Outfit')`). A CDN `<link>` would be the one thing
 * incompatible with the strict-CSP posture AL-52 was chosen for, and would put a
 * third party in the render path of an operator's console.
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
  title: 'MageRide Fleet',
  description: 'MageRide fleet operations — fleet.mageride.lk',
  // A sign-in screen for named operators has nothing to say to a crawler, and
  // `fleet.mageride.lk` resolving in a search result is an invitation to try it.
  robots: { index: false, follow: false },
};

export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
};

/**
 * Finishes the `system` appearance before the first paint.
 *
 * The server knows the stored preference and applies `light` or `dark` itself; it
 * cannot know the operator's OS setting, and the browser learns it only once this
 * document is parsing. Twenty lines of script running before the body renders is
 * how that is done without a flash of the wrong theme — it adds and removes one
 * class and writes no CSS, so AL-52 is untouched.
 */
const APPEARANCE_SCRIPT = `
(function(){try{
  var root=document.documentElement;
  if(root.dataset.mrAppearance!=='system')return;
  var query=window.matchMedia('(prefers-color-scheme: dark)');
  var apply=function(dark){root.classList.toggle('dark',dark)};
  apply(query.matches);
  query.addEventListener('change',function(e){apply(e.matches)});
}catch(e){}})();
`;

export default async function RootLayout({ children }: { children: React.ReactNode }) {
  const [locale, appearance] = await Promise.all([getLocale(), getAppearance()]);

  return (
    <html
      lang={locale}
      data-mr-appearance={appearance}
      // The `.dark` class drives BOTH halves at once — the `--mr-color-*` tokens
      // and the `dark:` variant — and the preset's README is explicit that they
      // must never be wired independently. This is the only place it is set.
      className={`${outfit.variable} ${inter.variable}${appearance === 'dark' ? ' dark' : ''}`}
      suppressHydrationWarning
    >
      <head>
        <script dangerouslySetInnerHTML={{ __html: APPEARANCE_SCRIPT }} />
      </head>
      <body className="min-h-dvh">{children}</body>
    </html>
  );
}
