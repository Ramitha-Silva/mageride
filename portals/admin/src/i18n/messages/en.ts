/**
 * English resources for the Admin Portal, and — because it is the only locale
 * written as a literal object — the file that **defines** this surface's key set.
 * `si.ts` and `ta.ts` are annotated `AdminMessages`, so a key added here and
 * missing there is a compile error and vice versa. The trilingual rule (root
 * CLAUDE.md, D-26) is enforced by the type checker rather than by review.
 *
 * **Why these live here and not in `@mageride/i18n`.** That package "carries only
 * what every surface shares" (its CLAUDE.md); `nav.fareTariffs` and
 * `admin.signIn.noSecondFactor` are not shared with the Fleet Portal or the
 * passenger web subview, and putting them there would ship Admin Portal copy in
 * two bundles that can never render it. The shared keys are still used — the
 * translator in `../index.ts` resolves both tables.
 *
 * ## The `nav.*` block is a contract, not copy
 *
 * `GET /v1/admin/session` returns each menu entry as a **`labelKey`, never a
 * label** — deliberately, so the one server that could not be translated does not
 * exist (see `AdminMenuItem` in `backend/src/AdminBff/Navigation/AdminMenu.cs`).
 * Every key admin-bff can send has to resolve here, in all three languages, or an
 * operator's sidebar renders raw identifiers. `test/nav-labels.test.ts` parses
 * `AdminMenu.cs` and asserts exactly that.
 */

export const adminEn = {
  /* ---- Shell chrome ---------------------------------------------------- */
  'admin.appName': 'MageRide Admin',
  'admin.tagline': 'Internal staff only',
  'admin.skipToContent': 'Skip to content',
  'admin.nav.label': 'Modules',
  'admin.nav.open': 'Open the menu',
  'admin.nav.close': 'Close the menu',
  'admin.user.menu': 'Your account',
  'admin.user.signOut': 'Sign out',
  'admin.user.roles': 'Roles',
  'admin.appearance.label': 'Appearance',
  'admin.appearance.light': 'Light',
  'admin.appearance.dark': 'Dark',
  'admin.appearance.system': 'Match my device',
  'admin.language.label': 'Language',

  /* ---- SCR-AP-001 · sign-in -------------------------------------------- */
  'admin.signIn.heading': 'MageRide Admin',
  'admin.signIn.email': 'Work email',
  'admin.signIn.password': 'Password',
  'admin.signIn.submit': 'Sign in',
  'admin.signIn.submitting': 'Signing in…',
  'admin.signIn.or': 'or',
  'admin.signIn.google': 'Sign in with Google',
  // AL-37 / US-24.5. The reassurance is deliberate: staff were told to expect an
  // authenticator step, and its absence should read as designed rather than broken.
  'admin.signIn.noSecondFactor':
    'No OTP or authenticator step — signing in takes you straight to your work.',
  'admin.signIn.emailRequired': 'Enter your work email address',
  'admin.signIn.passwordRequired': 'Enter your password',
  'admin.signIn.signedOut': 'You have been signed out.',

  /* ---- Errors ---------------------------------------------------------- */
  'admin.error.title': 'That did not work',
  'admin.error.unauthorized': 'Your session has ended. Sign in again.',
  'admin.error.forbidden': 'Your role does not permit that.',
  'admin.error.notFound': 'That record no longer exists.',
  'admin.error.validationFailed': 'Check the highlighted fields and try again.',
  'admin.error.conflict': 'Someone changed this first. Reload the page and try again.',
  'admin.error.accountBlocked': 'This account is blocked. A Super Admin can restore it.',
  'admin.error.invalidCredentials': 'That email and password do not match an account.',
  // AL-37's compensating control, made visible: an operator who is locked out has
  // to be told that is what happened, or they will keep guessing and extend it.
  'admin.error.accountLocked':
    'Too many failed sign-in attempts. This account is locked for a short while.',
  'admin.error.accountLockedFor':
    'Too many failed sign-in attempts. Try again in about {minutes} minutes.',
  'admin.error.rateLimited': 'Too many requests. Wait a moment and try again.',
  'admin.error.serviceUnavailable': 'MageRide cannot be reached right now. Try again shortly.',
  'admin.error.unexpected': 'Something went wrong at our end.',
  'admin.error.googleFailed': 'Google sign-in did not complete. Try again, or use your password.',
  // Shown verbatim, in every language, because it is what support asks for.
  'admin.error.reference': 'Reference: {traceId}',

  /* ---- Refusals and dead ends ------------------------------------------ */
  'admin.denied.title': 'You do not have access to this page',
  'admin.denied.body':
    'This module is not part of your role. Ask a Super Admin if you need it for your work.',
  'admin.denied.back': 'Go to your first module',
  'admin.notFound.title': 'Page not found',
  'admin.notFound.body': 'That address does not match any Admin Portal screen.',
  'admin.noModules.title': 'No modules are assigned to you yet',
  'admin.noModules.body':
    'Your account signed in successfully, but your roles do not open any Admin Portal screen. Ask a Super Admin to grant what you need.',

  /* ---- The shell's placeholder for a screen a later component owns ------ */
  'admin.screen.pendingTitle': 'This screen is not built yet',
  'admin.screen.pendingBody':
    'The Admin Portal shell resolved this route and your role permits it. The screen itself arrives with a later build component.',
  'admin.screen.servedBy': 'API served by {service}',

  /* ---- D-35 ------------------------------------------------------------ */
  'admin.audit.notice': 'This action is written to the audit trail against your name.',
  'admin.audit.recorded': 'Recorded in the audit trail as {action}.',

  /* ---- The nine canonical roles (AL-06) -------------------------------- */
  'admin.role.admin': 'Admin',
  'admin.role.super_admin': 'Super Admin',
  'admin.role.verification_officer': 'Verification Officer',
  'admin.role.support_csr': 'Support / CSR',
  'admin.role.finance_officer': 'Finance Officer',
  'admin.role.auditor': 'Auditor',
  'admin.role.driver': 'Driver',
  'admin.role.passenger': 'Passenger',
  'admin.role.fleet_owner': 'Fleet Owner',

  /* ---- Nav groups — `AdminMenuGroup.labelKey` -------------------------- */
  'nav.group.overview': 'Overview',
  'nav.group.onboarding': 'Onboarding',
  'nav.group.directories': 'Directory',
  'nav.group.moderation': 'Moderation & support',
  'nav.group.finance': 'Finance',
  'nav.group.configuration': 'Configuration',
  'nav.group.access': 'Access',

  /* ---- Nav items — `AdminMenuItem.labelKey` ---------------------------- */
  'nav.dashboard': 'Dashboard',
  'nav.auditLog': 'Audit log',
  'nav.verification': 'Verification queues',
  'nav.documentExpiry': 'Expiring documents',
  'nav.passengers': 'Passengers',
  'nav.drivers': 'Drivers',
  'nav.vehicles': 'Vehicles',
  'nav.reports': 'Vehicle reports',
  'nav.supportTickets': 'Support tickets',
  'nav.fraudReview': 'Fraud review',
  'nav.reconciliation': 'Reconciliation',
  'nav.transactions': 'Transactions',
  'nav.refunds': 'Refunds',
  'nav.walletAdjustments': 'Wallet adjustments',
  'nav.pdpa': 'Data rights',
  'nav.fareTariffs': 'Fare tariffs',
  'nav.cities': 'Cities',
  'nav.featureFlags': 'Feature flags',
  'nav.trains': 'Trains',
  'nav.announcements': 'Announcements',
  'nav.gtfs': 'Transit data (GTFS)',
  'nav.dailyFeeRates': 'Daily fee rates',
  'nav.voucherTiers': 'Voucher tiers',
  'nav.driverLevels': 'Driver levels',
  'nav.rbac': 'Users & roles',
} as const;

/** Every key the Admin Portal owns. Adding one here obliges `si.ts` and `ta.ts`. */
export type AdminMessageKey = keyof typeof adminEn;

/** A complete set of Admin Portal resources for one locale. */
export type AdminMessages = Record<AdminMessageKey, string>;

export default adminEn;
