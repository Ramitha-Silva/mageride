/**
 * English resources for the Fleet Portal, and — because it is the only locale
 * written as a literal object — the file that **defines** this surface's key set.
 * `si.ts` and `ta.ts` are annotated `FleetMessages`, so a key added here and
 * missing there is a compile error and vice versa. The trilingual rule (root
 * CLAUDE.md, D-26) is enforced by the type checker rather than by review.
 *
 * **Why these live here and not in `@mageride/i18n`.** That package "carries only
 * what every surface shares" (its CLAUDE.md); `fleet.nav.trackers` and
 * `fleet.pending.body` are not shared with the Admin Portal or the passenger web
 * subview, and putting them there would ship Fleet Portal copy in two bundles
 * that can never render it. The shared keys are still used — the translator in
 * `../index.ts` resolves both tables.
 *
 * ## The `fleet.nav.*` block is the manifest's copy, not a contract with a server
 *
 * Unlike the Admin Portal, no service sends this portal a menu, so these keys are
 * named by `src/server/routes.ts` and typed against this table: a nav entry whose
 * `labelKey` is not a key here does not compile. That is the same guarantee
 * `admin`'s `test/nav-labels.test.ts` buys by parsing C#, obtained from the type
 * checker instead because the manifest is local.
 */

export const fleetEn = {
  /* ---- Shell chrome ---------------------------------------------------- */
  'fleet.appName': 'MageRide Fleet',
  'fleet.tagline': 'Manage your fleet of vehicles',
  'fleet.skipToContent': 'Skip to content',
  'fleet.nav.label': 'Fleet menu',
  'fleet.nav.open': 'Open the menu',
  'fleet.nav.close': 'Close the menu',
  'fleet.user.menu': 'Your account',
  'fleet.user.signOut': 'Sign out',
  'fleet.user.role': 'Your role',
  'fleet.appearance.label': 'Appearance',
  'fleet.appearance.light': 'Light',
  'fleet.appearance.dark': 'Dark',
  'fleet.appearance.system': 'Match my device',
  'fleet.language.label': 'Language',

  /* ---- The nav (web_fleet.html) ---------------------------------------- */
  'fleet.nav.group.setup': 'Setup',
  'fleet.nav.group.operate': 'Operate',
  'fleet.nav.group.manage': 'Manage',
  'fleet.nav.group.subscribers': 'Passengers (Mode B)',
  'fleet.nav.organisation': 'Organisation',
  'fleet.nav.payout': 'Bank & payout',
  'fleet.nav.team': 'Team',
  'fleet.nav.dashboard': 'Dashboard',
  'fleet.nav.vehicles': 'Vehicles',
  'fleet.nav.drivers': 'Drivers',
  'fleet.nav.trackers': 'Trackers',
  'fleet.nav.map': 'Live map',
  'fleet.nav.scheduling': 'Scheduling',
  'fleet.nav.analytics': 'Analytics',
  'fleet.nav.billing': 'Billing',
  'fleet.nav.subscriptions': 'Subscriptions',
  'fleet.nav.payments': 'Payments',

  /* ---- Org-scoped sub-roles (AL-03, US-13.A5) -------------------------- */
  'fleet.role.owner': 'Owner',
  'fleet.role.manager': 'Manager',
  'fleet.role.viewer': 'Viewer',

  /* ---- Organisation status (US-13.A7) ---------------------------------- */
  'fleet.status.pending': 'Pending verification',
  'fleet.status.approved': 'Verified',
  'fleet.status.rejected': 'Rejected',

  /* ---- SCR-FP-001 · sign-in (AL-07) ------------------------------------ */
  'fleet.signIn.heading': 'MageRide Fleet',
  'fleet.signIn.email': 'Work email',
  'fleet.signIn.password': 'Password',
  'fleet.signIn.submit': 'Sign in',
  'fleet.signIn.submitting': 'Signing in…',
  'fleet.signIn.or': 'or continue with',
  'fleet.signIn.google': 'Google',
  'fleet.signIn.apple': 'Apple',
  'fleet.signIn.emailRequired': 'Enter your work email address',
  'fleet.signIn.passwordRequired': 'Enter your password',
  'fleet.signIn.signedOut': 'You have been signed out.',
  // AL-37: the second factor was removed platform-wide and replaced by the
  // failed-attempt lock-out. Saying so keeps its absence reading as designed.
  'fleet.signIn.noSecondFactor':
    'No OTP or authenticator step — signing in takes you straight to your fleet.',
  // There is no self-service reset on this surface and **no contract for one**:
  // iam-svc has nine auth operations and none of them resets a password or
  // verifies an email address. Saying so is the whole feature — a link to a route
  // that does not exist is a dead end that looks like a way out. C111 handoff.
  'fleet.signIn.forgot': 'Forgotten your password?',
  'fleet.signIn.forgotBody':
    'MageRide cannot reset a Fleet Portal password from this screen yet. Ask your organisation owner, or contact MageRide support, and a new password will be set for you.',
  'fleet.signIn.newAccount': 'Do not have an account yet?',
  'fleet.signIn.newAccountBody':
    'A Fleet Portal account is created for you — either by the owner of an organisation that invites you, or by MageRide when a new operator is taken on. Once you can sign in, the organisation itself is set up on the first screen.',

  /* ---- Errors ---------------------------------------------------------- */
  'fleet.error.title': 'That did not work',
  'fleet.error.unauthorized': 'Your session has ended. Sign in again.',
  'fleet.error.forbidden': 'Your role does not permit that.',
  'fleet.error.notFound': 'That record no longer exists.',
  'fleet.error.validationFailed': 'Check the highlighted fields and try again.',
  'fleet.error.conflict': 'Someone changed this first. Reload the page and try again.',
  'fleet.error.accountBlocked': 'This account is blocked. MageRide support can restore it.',
  'fleet.error.invalidCredentials': 'That email and password do not match an account.',
  'fleet.error.accountLocked':
    'Too many failed sign-in attempts. This account is locked for a short while.',
  'fleet.error.accountLockedFor':
    'Too many failed sign-in attempts. Try again in about {minutes} minutes.',
  'fleet.error.rateLimited': 'Too many requests. Wait a moment and try again.',
  'fleet.error.serviceUnavailable': 'MageRide cannot be reached right now. Try again shortly.',
  'fleet.error.unexpected': 'Something went wrong at our end.',
  'fleet.error.providerFailed':
    '{provider} sign-in did not complete. Try again, or use your password.',
  // The refusal `PortalSignInService` gives an address with no fleet standing.
  'fleet.error.noFleetAccount':
    'This account cannot sign in to the Fleet Portal. Ask your organisation owner to invite your email address, or contact MageRide support.',
  // The four refusals `FleetAccessFilter` gives, in the order it gives them.
  'fleet.error.orgNotFound': 'That organisation no longer exists.',
  'fleet.error.notMember': 'You are not a member of that organisation.',
  'fleet.error.roleInsufficient': 'Your role in this organisation does not permit that.',
  'fleet.error.orgNotApproved':
    'This organisation is still being verified, so that is not available yet.',
  // Shown verbatim, in every language, because it is what support asks for.
  'fleet.error.reference': 'Reference: {traceId}',

  /* ---- Refusals and dead ends ------------------------------------------ */
  'fleet.denied.title': 'You do not have access to this page',
  'fleet.denied.body':
    'This screen is not part of your role in this organisation. Your owner can change what you can reach.',
  'fleet.denied.back': 'Go to your first screen',
  'fleet.notFound.title': 'Page not found',
  'fleet.notFound.body': 'That address does not match any Fleet Portal screen.',
  'fleet.noScreens.title': 'There is nothing here for this account yet',
  'fleet.noScreens.body':
    'You signed in successfully, but this account holds no fleet role. Ask your organisation owner to invite your email address again, or contact MageRide support.',

  /* ---- No organisation yet --------------------------------------------- */
  'fleet.org.none.title': 'Set up your organisation',
  'fleet.org.none.body':
    'This account can create an organisation but does not belong to one yet. Register it to begin onboarding vehicles and drivers.',

  /* ---- US-13.A7 · the verification gate --------------------------------- */
  'fleet.pending.title': 'Your organisation is still being verified',
  'fleet.pending.body':
    'A MageRide verification officer is reviewing your registration and documents. Vehicle onboarding and driver assignment open as soon as it is approved.',
  'fleet.pending.next':
    'While you wait you can finish your organisation profile, add your bank and payout details, and invite your team.',
  'fleet.pending.blocked': 'Vehicles and driver assignment are not available yet.',
  'fleet.rejected.title': 'Your organisation was not approved',
  'fleet.rejected.body':
    'A verification officer could not approve this registration. Correct what is noted below and MageRide support will re-open the review.',
  'fleet.rejected.reason': 'Reason given: {reason}',
  'fleet.banner.pending':
    'This organisation is awaiting verification. Only setup screens are available until it is approved.',
  'fleet.banner.rejected':
    'This organisation was not approved. Contact MageRide support to re-open the review.',
  // Drawn once, in the chrome, rather than beside every absent button — an empty
  // screen with no explanation reads as a broken screen.
  'fleet.banner.viewer': 'You are signed in as a Viewer, so this session is read-only.',

  /* ---- The shell's placeholder for a screen a later component owns ------ */
  'fleet.screen.pendingTitle': 'This screen is not built yet',
  'fleet.screen.pendingBody':
    'The Fleet Portal shell resolved this route and your role permits it. The screen itself arrives with a later build component.',
  'fleet.screen.servedBy': 'API served by {service}',
  'fleet.screen.wireframe': 'Wireframe {screen}',
} as const;

export type FleetMessages = Record<keyof typeof fleetEn, string>;
export type FleetMessageKey = keyof typeof fleetEn;
