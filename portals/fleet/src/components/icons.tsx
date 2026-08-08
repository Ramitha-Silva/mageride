/**
 * The portal's glyphs, as inline SVG.
 *
 * Inline rather than an icon package for two reasons that both matter here: an
 * icon font or a sprite sheet is a second network request in the render path of a
 * console people open on a phone at a depot gate, and every icon library worth
 * using ships its own styling layer, which AL-52 excludes. These are paths.
 *
 * Every glyph is `aria-hidden`: each one sits beside its own text label, and a
 * decorative icon that announces itself makes a screen reader read the nav twice.
 * `web_fleet.html` draws them as emoji; these are the same seven ideas in the
 * stroke weight the rest of the chrome uses.
 */

const STROKE = {
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.6,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
} as const;

function Glyph({ children }: { children: React.ReactNode }) {
  return (
    <svg viewBox="0 0 24 24" className="size-[18px] shrink-0" aria-hidden="true" {...STROKE}>
      {children}
    </svg>
  );
}

/** The nav icon for a screen key. An unknown key gets the neutral dot. */
export function NavIcon({ navKey }: { navKey: string }) {
  switch (navKey) {
    case 'organisation':
      return (
        <Glyph>
          <path d="M4 20V6l7-3v17M11 20h9V10l-9-3" />
          <path d="M14.5 13h1.5M14.5 16.5h1.5M7 9.5h1.5M7 13h1.5M7 16.5h1.5" />
        </Glyph>
      );
    case 'payout':
      return (
        <Glyph>
          <path d="M3 9.5 12 5l9 4.5M4.5 9.5v8M9 9.5v8M15 9.5v8M19.5 9.5v8M3 20.5h18" />
        </Glyph>
      );
    case 'team':
      return (
        <Glyph>
          <path d="M8.5 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM3 19.5c0-2.8 2.5-4.5 5.5-4.5s5.5 1.7 5.5 4.5" />
          <path d="M16 5.5a3 3 0 0 1 0 6M17 15.2c2.4.4 4 1.9 4 4.3" />
        </Glyph>
      );
    case 'dashboard':
      return (
        <Glyph>
          <path d="M4 4h7v7H4zM13 4h7v4.5h-7zM13 10.5h7V20h-7zM4 13h7v7H4z" />
        </Glyph>
      );
    case 'vehicles':
      return (
        <Glyph>
          <path d="M3 16.5V9l2.5-4h9l2 4H21v7.5" />
          <path d="M3 16.5h18M7 16.5a1.75 1.75 0 1 0 3.5 0 1.75 1.75 0 0 0-3.5 0ZM14 16.5a1.75 1.75 0 1 0 3.5 0 1.75 1.75 0 0 0-3.5 0Z" />
        </Glyph>
      );
    case 'drivers':
      return (
        <Glyph>
          <path d="M12 11a3.25 3.25 0 1 0 0-6.5A3.25 3.25 0 0 0 12 11ZM5 20c0-3.2 3-5.2 7-5.2s7 2 7 5.2" />
        </Glyph>
      );
    case 'trackers':
      return (
        <Glyph>
          <path d="M12 14a2 2 0 1 0 0-4 2 2 0 0 0 0 4ZM8.5 15.5a5 5 0 0 1 0-7M15.5 8.5a5 5 0 0 1 0 7" />
          <path d="M5.5 18.5a9 9 0 0 1 0-13M18.5 5.5a9 9 0 0 1 0 13" />
        </Glyph>
      );
    case 'map':
      return (
        <Glyph>
          <path d="M9 5 3.5 7v12L9 17l6 2 5.5-2V5L15 7 9 5Z" />
          <path d="M9 5v12M15 7v12" />
        </Glyph>
      );
    case 'scheduling':
      return (
        <Glyph>
          <path d="M4 6h16v14H4zM4 10h16M8.5 4v4M15.5 4v4" />
          <path d="M8 14h3M8 17h8" />
        </Glyph>
      );
    case 'analytics':
      return (
        <Glyph>
          <path d="M4 20V4M4 20h16M8 16.5V12M12.5 16.5V7.5M17 16.5v-6" />
        </Glyph>
      );
    case 'billing':
      return (
        <Glyph>
          <path d="M3 7.5h18v10H3zM3 11h18" />
          <path d="M6.5 14.5h3" />
        </Glyph>
      );
    case 'subscriptions':
      return (
        <Glyph>
          <path d="M9.5 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM3.5 19.5c0-2.9 2.7-4.7 6-4.7s6 1.8 6 4.7" />
          <path d="m17 10.5 1.6 1.6 3.1-3.2" />
        </Glyph>
      );
    case 'payments':
      return (
        <Glyph>
          <path d="M12 20a8 8 0 1 0 0-16 8 8 0 0 0 0 16Z" />
          <path d="M14.5 9.2A2.6 2.6 0 0 0 12 7.8c-1.5 0-2.5.8-2.5 2s1 1.7 2.5 2 2.5.8 2.5 2-1 2-2.5 2a2.6 2.6 0 0 1-2.5-1.4M12 6.5v11" />
        </Glyph>
      );
    default:
      return (
        <Glyph>
          <path d="M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z" />
        </Glyph>
      );
  }
}

export function MenuGlyph() {
  return (
    <Glyph>
      <path d="M4 7h16M4 12h16M4 17h16" />
    </Glyph>
  );
}

export function CloseGlyph() {
  return (
    <Glyph>
      <path d="m6 6 12 12M18 6 6 18" />
    </Glyph>
  );
}

/**
 * Google's mark, in its own four brand colours.
 *
 * The one place in the portal that names a colour outside D2 §0.2, and it has to:
 * Google's brand terms require the mark's own palette, and a monochrome or
 * re-tinted G is a trademark change rather than a styling choice. It is `fill`
 * data on a path, not a stylesheet, so AL-52 is untouched.
 */
export function GoogleGlyph() {
  return (
    <svg viewBox="0 0 18 18" className="size-[18px] shrink-0" aria-hidden="true">
      <path
        fill="#4285F4"
        d="M17.64 9.2c0-.64-.06-1.25-.16-1.84H9v3.48h4.84a4.14 4.14 0 0 1-1.8 2.72v2.26h2.92c1.7-1.57 2.68-3.88 2.68-6.62Z"
      />
      <path
        fill="#34A853"
        d="M9 18c2.43 0 4.47-.8 5.96-2.18l-2.92-2.26c-.8.54-1.84.86-3.04.86-2.34 0-4.32-1.58-5.03-3.7H.96v2.33A9 9 0 0 0 9 18Z"
      />
      <path
        fill="#FBBC05"
        d="M3.97 10.72a5.4 5.4 0 0 1 0-3.44V4.95H.96a9 9 0 0 0 0 8.1l3.01-2.33Z"
      />
      <path
        fill="#EA4335"
        d="M9 3.58c1.32 0 2.5.46 3.44 1.35l2.58-2.58C13.46.9 11.43 0 9 0A9 9 0 0 0 .96 4.95l3.01 2.33C4.68 5.16 6.66 3.58 9 3.58Z"
      />
    </svg>
  );
}

/** Apple's mark. Monochrome by Apple's own guidelines, so it takes `currentColor`. */
export function AppleGlyph() {
  return (
    <svg viewBox="0 0 18 18" className="size-[18px] shrink-0" aria-hidden="true" fill="currentColor">
      <path d="M13.03 9.5c.02-1.6.72-2.79 2.1-3.66-.77-1.1-1.94-1.71-3.48-1.83-1.46-.11-3.05.85-3.63.85-.61 0-2.02-.81-3.13-.81C2.6 4.09.2 5.87.2 9.5c0 1.07.2 2.18.59 3.32.52 1.5 2.4 5.19 4.36 5.13.92-.02 1.57-.65 2.77-.65 1.16 0 1.76.65 2.79.65 1.98-.03 3.68-3.38 4.18-4.89-2.65-1.25-2.86-3.51-2.86-3.56Zm-2.3-7.07c1.11-1.32.99-2.51.96-2.93-.96.05-2.08.65-2.71 1.38-.7.79-1.11 1.76-1.02 2.9 1.04.08 1.99-.46 2.77-1.35Z" />
    </svg>
  );
}
