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

  /* ---- SCR-FP-001 · the sign-up half (US-13.A2/A3) --------------------- */
  // `web_fleet.html` draws one card whose states are "sign-up vs login", so the
  // card has two tabs. What the second one cannot have is a Create-account
  // button: `POST /v1/fleets` needs the caller to already hold `fleet_owner`,
  // and nothing on any contract grants it to a stranger. So the tab says how an
  // account really comes to exist — see the C112 handoff.
  'fleet.auth.tabs': 'Fleet Portal access',
  'fleet.auth.tab.signIn': 'Sign in',
  'fleet.auth.tab.signUp': 'Create account',
  'fleet.signUp.title': 'How a Fleet Portal account is created',
  'fleet.signUp.unavailable':
    'MageRide cannot open a new Fleet Portal account from this screen. There are two ways to get one, and both start somewhere else.',
  'fleet.signUp.byOwner':
    'Your organisation already uses MageRide — ask its owner to add your work email address to the team from Organisation setup. You can sign in as soon as they have.',
  'fleet.signUp.byMageRide':
    'Your organisation is new to MageRide — contact MageRide so an operator account can be opened for you.',
  'fleet.signUp.thenOrg':
    'Once you can sign in, you register the organisation itself on the first screen you are shown.',
  'fleet.signUp.verification': 'Verifying an email address?',
  'fleet.signUp.verificationBody':
    'MageRide does not send a verification email for a Fleet Portal account yet, and there is no self-service password reset. Your address is confirmed by whoever adds you to the team.',
  'fleet.signUp.identities': 'Using Google or Apple?',
  'fleet.signUp.identitiesBody':
    'Google and Apple work as soon as your account exists, as long as you use the same work email address. Linking or unlinking a provider afterwards is not available yet.',

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
  // Δ C112 — the six codes SCR-FP-002 and SCR-FP-002a can be answered with.
  'fleet.error.registrationExists':
    'That business registration number is already registered with MageRide. Contact MageRide support if it should be yours.',
  'fleet.error.memberExists': 'That email address already has a seat in this organisation.',
  'fleet.error.payoutNotFound': 'This organisation has no bank and payout details yet.',
  'fleet.error.payoutNotVerified':
    'That needs a verified bank and payout profile. Add the account details and documents, and a verification officer will approve them.',
  'fleet.error.fileTooLarge': 'That file is larger than {megabytes} MB. Upload a smaller copy.',
  'fleet.error.fileNotAccepted': 'That kind of file is not accepted here.',
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

  /* ---- SCR-FP-002 · organisation setup (US-13.A5/A7) ------------------- */
  'fleet.org.profile.heading': 'Org profile & KYC',
  'fleet.org.field.name': 'Organisation name',
  'fleet.org.field.registrationNo': 'Business registration no',
  'fleet.org.field.contactPhone': 'Contact mobile',
  'fleet.org.field.contactEmail': 'Contact email',
  'fleet.org.field.address': 'Address',
  'fleet.org.field.registered': 'Registered with MageRide',
  'fleet.org.field.language': 'Language',
  'fleet.org.hint.registrationNo': 'Exactly as printed on the business registration certificate.',
  'fleet.org.hint.contactPhone': 'A Sri Lankan mobile number, for example 0771234567.',
  'fleet.org.optional': 'Optional',
  'fleet.org.required': 'required',
  // The wireframe puts a language control on the organisation card, and no
  // organisation-level language exists to put behind it — `registry.fleets` has
  // no such column and `POST /v1/fleets` takes no such field. The control is
  // real and sets this console's language; the caption says exactly that.
  'fleet.org.language.note':
    'Sets the language of this console in this browser. MageRide does not store a language for the organisation itself.',
  'fleet.org.readOnly':
    'These details are the record a verification officer reads, and the portal cannot edit them yet. Contact MageRide support to correct anything here.',
  'fleet.org.kyc.heading': 'KYC and verification',
  'fleet.org.kyc.gate':
    'A MageRide verification officer checks the registration before vehicle onboarding and driver assignment open. Until then only read operations are available.',
  'fleet.org.kyc.unavailable':
    'The business registration certificate and the owner identity document are collected by MageRide support; the portal has nowhere to upload them yet. The bank documents below are attached to the bank and payout profile.',
  'fleet.org.payout.link': 'Bank & payout details',
  'fleet.org.payout.linkBody':
    'The account that receives Mode B subscription payments, and the bank-app QR your passengers scan. Owner only.',
  'fleet.org.register.heading': 'Register your organisation',
  'fleet.org.register.body':
    'This account can create an organisation but does not belong to one yet. Register it to begin onboarding vehicles and drivers.',
  'fleet.org.register.gate':
    'The organisation is created pending verification. A MageRide verification officer reviews it, and until they approve it only read operations are available.',
  'fleet.org.register.submit': 'Register organisation',
  'fleet.org.register.submitting': 'Registering…',
  'fleet.org.error.nameRequired': 'Enter the organisation name',
  'fleet.org.error.registrationRequired': 'Enter the business registration number',
  'fleet.org.error.phoneInvalid': 'Enter a Sri Lankan mobile number, for example 0771234567',
  'fleet.org.error.emailInvalid': 'Enter a valid email address',

  /* ---- SCR-FP-002 · the team (US-13.A5) -------------------------------- */
  'fleet.team.heading': 'Team members',
  'fleet.team.caption': 'The people who can sign in for this organisation, and their roles',
  'fleet.team.column.member': 'Member',
  'fleet.team.column.role': 'Role',
  'fleet.team.you': '(you)',
  'fleet.team.empty': 'No team members yet.',
  'fleet.team.backToOrg': 'Back to organisation setup',
  'fleet.team.invite.heading': 'Invite a team member',
  'fleet.team.invite.email': 'Work email',
  'fleet.team.invite.name': 'Name',
  'fleet.team.invite.role': 'Role',
  'fleet.team.invite.submit': 'Invite member',
  'fleet.team.invite.submitting': 'Inviting…',
  'fleet.team.invite.done': 'That address now has a seat in this organisation.',
  // US-13.A5 gives the Owner "Manager and Viewer". A second Owner is a change of
  // who the organisation belongs to, which no route makes.
  'fleet.team.invite.noOwnerSeat':
    'A second Owner cannot be added here — the organisation belongs to the person who registered it.',
  // fleet-svc provisions the seat and tells nobody: no fleet-org invitation
  // template exists on the platform. An owner who is not told that waits for a
  // mail that never arrives. C112 handoff.
  'fleet.team.invite.noEmail':
    'MageRide does not send an invitation email yet. Tell your colleague their address has been added — they can then sign in with Google or Apple using it, or ask MageRide support to set a password.',
  'fleet.team.invite.ownerOnlyNotice':
    'Only the organisation owner can add or change team members.',
  'fleet.team.error.ownerOnly': 'Only the organisation owner can add a team member.',
  'fleet.team.error.roleRequired': 'Choose Manager or Viewer',

  /* ---- SCR-FP-002a · bank & payout details (AL-49, US-27.1/27.2) ------- */
  'fleet.payout.title': 'Bank & payout details',
  'fleet.payout.heading': 'Bank account — receives Mode B subscription payments',
  'fleet.payout.field.bank': 'Bank',
  'fleet.payout.field.bankPlaceholder': 'Choose your bank',
  'fleet.payout.field.branch': 'Branch',
  'fleet.payout.field.accountNo': 'Account number',
  'fleet.payout.field.holder': 'Account holder name',
  'fleet.payout.holderHint':
    'Must match the organisation or owner KYC name. A verification officer compares the two.',
  'fleet.payout.editWarning':
    'Saving these details sends them for verification. They are checked against the documents on the right.',
  // BR-31.1's second half, before the press rather than after it.
  'fleet.payout.editVerifiedWarning':
    'This profile is verified. Saving a change sends the new details for verification, and your Mode B subscribers keep paying into the approved account until an officer has approved the change.',
  'fleet.payout.submit': 'Save bank details',
  'fleet.payout.submitting': 'Saving…',
  'fleet.payout.saved': 'Saved. The details are now pending verification.',
  'fleet.payout.backToOrg': 'Back to organisation setup',
  'fleet.payout.status.none': 'Not submitted',
  'fleet.payout.status.pending': 'Pending verification',
  'fleet.payout.status.verified': 'Verified',
  'fleet.payout.status.rejected': 'Rejected',
  'fleet.payout.status.superseded': 'Replaced by a newer version',
  'fleet.payout.rejectedReason': 'Reason given: {reason}',
  'fleet.payout.verifiedOn': 'Verified on {date}.',
  'fleet.payout.gate.heading': 'What is waiting on this profile',
  // The sentence C113's SCR-FP-004 puts beside the disabled "Paid" option.
  'fleet.payout.gate.paid':
    'A Mode B vehicle cannot be set to Service payment "Paid" until this profile is verified.',
  'fleet.payout.gate.paidReady':
    'Mode B vehicles can be set to Service payment "Paid" with a monthly fare.',
  'fleet.payout.gate.billing':
    'Paid subscriptions do not start billing, and the passenger pay sheet shows nothing to pay into, until an officer approves these details.',
  'fleet.payout.gate.paySheetReady':
    'Mode B subscribers see these account details for an online transfer, and the QR image below for a LankaQR payment.',
  'fleet.payout.proof.heading': 'Proof of account',
  'fleet.payout.proof.which': 'Which document is this?',
  'fleet.payout.proof.prompt': 'Upload the bank statement or passbook page',
  'fleet.payout.proof.hint': 'PDF or photograph, up to 8 MB.',
  'fleet.payout.qr.heading': 'Bank-app LankaQR code',
  'fleet.payout.qr.prompt': 'Upload the LankaQR code image from your bank app',
  'fleet.payout.qr.hint': 'Photograph or screenshot, up to 8 MB.',
  'fleet.payout.qr.note':
    'Shown to Mode B subscribers in the passenger pay sheet, to scan or open in their bank app. Passengers paying by transfer see the verified account details instead.',
  // BR-31.1 gives the statement and the passbook page one slot, so the screen
  // asks which of the two the file is.
  'fleet.payout.kind.bankStatement': 'Latest bank statement',
  'fleet.payout.kind.passbook': 'First page of the passbook',
  'fleet.payout.kind.lankaqr': 'Bank-app LankaQR code',
  'fleet.payout.doc.uploading': 'Uploading…',
  'fleet.payout.doc.uploaded': 'Uploaded',
  'fleet.payout.doc.missing': 'Not uploaded',
  'fleet.payout.error.bankRequired': 'Choose the bank',
  'fleet.payout.error.branchRequired': 'Enter the branch',
  'fleet.payout.error.accountRequired': 'Enter the account number',
  'fleet.payout.error.holderRequired': 'Enter the account holder name',
  'fleet.payout.error.kindRequired': 'Choose which document this is',
  'fleet.payout.error.fileRequired': 'Choose a file to upload',
  'fleet.payout.error.profileFirst':
    'Save the bank details first — a document is attached to a payout profile, not to an organisation.',

  /* ---- The shell's placeholder for a screen a later component owns ------ */
  'fleet.screen.pendingTitle': 'This screen is not built yet',
  'fleet.screen.pendingBody':
    'The Fleet Portal shell resolved this route and your role permits it. The screen itself arrives with a later build component.',
  'fleet.screen.servedBy': 'API served by {service}',
  'fleet.screen.wireframe': 'Wireframe {screen}',
} as const;

export type FleetMessages = Record<keyof typeof fleetEn, string>;
export type FleetMessageKey = keyof typeof fleetEn;
