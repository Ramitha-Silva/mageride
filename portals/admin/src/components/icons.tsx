import type { SVGProps } from 'react';

/**
 * The nav's glyphs.
 *
 * Drawn rather than imported: an icon package is another dependency to audit for
 * AL-52 (several ship their own CSS), and twenty-five small strokes are cheaper
 * than that. Each is a 24×24 `currentColor` stroke, so a glyph inherits the
 * colour of whatever nav state it is in and nothing here spells a hex.
 *
 * Every one is `aria-hidden`. The nav item's label is the accessible name; an
 * icon that also announced itself would make every entry read twice.
 */

const SHAPES: Readonly<Record<string, React.ReactNode>> = {
  // Overview
  grid: (
    <>
      <rect x="3" y="3" width="7" height="7" rx="1.5" />
      <rect x="14" y="3" width="7" height="7" rx="1.5" />
      <rect x="3" y="14" width="7" height="7" rx="1.5" />
      <rect x="14" y="14" width="7" height="7" rx="1.5" />
    </>
  ),
  scroll: (
    <>
      <path d="M6 3h9l4 4v14H6z" />
      <path d="M15 3v4h4" />
      <path d="M9 12h7M9 16h7" />
    </>
  ),

  // Onboarding
  badgeCheck: (
    <>
      <path d="M12 3l2.3 1.7 2.8-.3 1 2.7 2.4 1.6-1 2.7 1 2.7-2.4 1.6-1 2.7-2.8-.3L12 21l-2.3-1.7-2.8.3-1-2.7L3.5 15.3l1-2.7-1-2.7 2.4-1.6 1-2.7 2.8.3z" />
      <path d="M9 12l2 2 4-4" />
    </>
  ),
  clockDoc: (
    <>
      <path d="M14 3H6v18h7" />
      <path d="M17.5 17.5V15" />
      <circle cx="17.5" cy="17.5" r="4.5" />
    </>
  ),

  // Directory
  user: (
    <>
      <circle cx="12" cy="8" r="4" />
      <path d="M4 21c0-4 3.6-6 8-6s8 2 8 6" />
    </>
  ),
  car: (
    <>
      <path d="M4 16v3M20 16v3" />
      <path d="M3 16v-3l2-5h14l2 5v3z" />
      <path d="M6.5 13h.01M17.5 13h.01" />
    </>
  ),
  truck: (
    <>
      <path d="M2 7h11v9H2zM13 10h4l4 3v3h-8z" />
      <circle cx="6.5" cy="18" r="1.8" />
      <circle cx="17" cy="18" r="1.8" />
    </>
  ),

  // Moderation & support
  flag: (
    <>
      <path d="M5 21V4" />
      <path d="M5 5h11l-2 3 2 3H5z" />
    </>
  ),
  chat: (
    <>
      <path d="M20 15a3 3 0 0 1-3 3H9l-5 3 1.4-3.6A3 3 0 0 1 4 15V7a3 3 0 0 1 3-3h10a3 3 0 0 1 3 3z" />
    </>
  ),
  shieldAlert: (
    <>
      <path d="M12 3l8 3v6c0 4.4-3.2 8-8 9-4.8-1-8-4.6-8-9V6z" />
      <path d="M12 8v4M12 15h.01" />
    </>
  ),

  // Finance
  scales: (
    <>
      <path d="M12 4v16M7 20h10" />
      <path d="M4 9h16M4 9l-2 5h4zM20 9l-2 5h4z" />
    </>
  ),
  list: (
    <>
      <path d="M8 6h13M8 12h13M8 18h13" />
      <path d="M3.5 6h.01M3.5 12h.01M3.5 18h.01" />
    </>
  ),
  undo: (
    <>
      <path d="M4 10h10a5 5 0 0 1 0 10h-3" />
      <path d="M8 6l-4 4 4 4" />
    </>
  ),
  wallet: (
    <>
      <path d="M3 7a2 2 0 0 1 2-2h11v4" />
      <path d="M3 7v10a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V9H5a2 2 0 0 1-2-2z" />
      <path d="M16.5 14h.01" />
    </>
  ),
  lock: (
    <>
      <rect x="4" y="10" width="16" height="11" rx="2" />
      <path d="M8 10V7a4 4 0 0 1 8 0v3" />
    </>
  ),

  // Configuration
  tag: (
    <>
      <path d="M3 12V5a2 2 0 0 1 2-2h7l9 9-9 9z" />
      <path d="M7.5 7.5h.01" />
    </>
  ),
  pin: (
    <>
      <path d="M12 21s7-6.3 7-11a7 7 0 1 0-14 0c0 4.7 7 11 7 11z" />
      <circle cx="12" cy="10" r="2.5" />
    </>
  ),
  toggle: (
    <>
      <rect x="2" y="7" width="20" height="10" rx="5" />
      <circle cx="16" cy="12" r="3" />
    </>
  ),
  train: (
    <>
      <rect x="5" y="3" width="14" height="13" rx="3" />
      <path d="M5 10h14M9 20l-2 2M15 20l2 2" />
      <path d="M9.5 7h.01M14.5 7h.01" />
    </>
  ),
  megaphone: (
    <>
      <path d="M4 10v4a1 1 0 0 0 1 1h3l8 4V5L8 9H5a1 1 0 0 0-1 1z" />
      <path d="M19 9.5a3.5 3.5 0 0 1 0 5" />
    </>
  ),
  route: (
    <>
      <circle cx="6" cy="6" r="2.5" />
      <circle cx="18" cy="18" r="2.5" />
      <path d="M6 8.5V14a4 4 0 0 0 4 4h5.5" />
    </>
  ),
  calendar: (
    <>
      <rect x="3" y="5" width="18" height="16" rx="2" />
      <path d="M3 10h18M8 3v4M16 3v4" />
    </>
  ),
  ticket: (
    <>
      <path d="M3 9V6h18v3a3 3 0 0 0 0 6v3H3v-3a3 3 0 0 0 0-6z" />
      <path d="M12 6v12" />
    </>
  ),
  bars: (
    <>
      <path d="M4 20V13M10 20V8M16 20v-5M22 20V4" />
    </>
  ),

  // Access
  key: (
    <>
      <circle cx="7.5" cy="12" r="4.5" />
      <path d="M12 12h9M17 12v4M20 12v3" />
    </>
  ),
};

/** `AdminMenuItem.key` → glyph. An unmapped key falls back to a neutral dot. */
const BY_NAV_KEY: Readonly<Record<string, keyof typeof SHAPES>> = {
  dashboard: 'grid',
  'audit-log': 'scroll',
  verification: 'badgeCheck',
  'document-expiry': 'clockDoc',
  passengers: 'user',
  drivers: 'car',
  vehicles: 'truck',
  reports: 'flag',
  'support-tickets': 'chat',
  'fraud-review': 'shieldAlert',
  reconciliation: 'scales',
  transactions: 'list',
  refunds: 'undo',
  'wallet-adjustments': 'wallet',
  pdpa: 'lock',
  'fare-tariffs': 'tag',
  cities: 'pin',
  'feature-flags': 'toggle',
  trains: 'train',
  announcements: 'megaphone',
  gtfs: 'route',
  'daily-fee-rates': 'calendar',
  'voucher-tiers': 'ticket',
  'driver-levels': 'bars',
  rbac: 'key',
};

export function NavIcon({ navKey, className }: { navKey: string; className?: string }) {
  const shape = SHAPES[BY_NAV_KEY[navKey] ?? ''];

  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.6}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      className={className ?? 'size-cta-icon shrink-0'}
    >
      {shape ?? <circle cx="12" cy="12" r="3.5" />}
    </svg>
  );
}

/** Google's mark, in its own four brand colours — theirs, not D2's, by definition. */
export function GoogleGlyph(props: SVGProps<SVGSVGElement>) {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" className="size-cta-icon shrink-0" {...props}>
      <path
        fill="#4285F4"
        d="M23.5 12.3c0-.8-.1-1.6-.2-2.3H12v4.5h6.5a5.6 5.6 0 0 1-2.4 3.6v3h3.9c2.3-2.1 3.5-5.2 3.5-8.8z"
      />
      <path
        fill="#34A853"
        d="M12 24c3.2 0 5.9-1.1 7.9-2.9l-3.9-3c-1.1.7-2.4 1.2-4 1.2-3.1 0-5.7-2.1-6.6-4.9H1.4v3.1A12 12 0 0 0 12 24z"
      />
      <path fill="#FBBC05" d="M5.4 14.4a7.2 7.2 0 0 1 0-4.6V6.7H1.4a12 12 0 0 0 0 10.8z" />
      <path
        fill="#EA4335"
        d="M12 4.8c1.8 0 3.3.6 4.6 1.8l3.4-3.4C17.9 1.2 15.2 0 12 0A12 12 0 0 0 1.4 6.7l4 3.1C6.3 6.9 8.9 4.8 12 4.8z"
      />
    </svg>
  );
}

/** The hamburger, for the sub-desktop drawer. */
export function MenuGlyph() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.8}
      strokeLinecap="round"
      aria-hidden="true"
      className="size-cta-icon"
    >
      <path d="M4 7h16M4 12h16M4 17h16" />
    </svg>
  );
}

export function CloseGlyph() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.8}
      strokeLinecap="round"
      aria-hidden="true"
      className="size-cta-icon"
    >
      <path d="M6 6l12 12M18 6L6 18" />
    </svg>
  );
}
