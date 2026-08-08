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
  // There is no self-service reset on this surface and no contract for one —
  // internal accounts are provisioned by a Super Admin (AL-06, Epic 21). Saying so
  // is the whole feature: a link to a route that does not exist would be worse
  // than the silence it replaced.
  'admin.signIn.forgot': 'Forgotten your password?',
  'admin.signIn.forgotBody':
    'Internal accounts are created and reset by a Super Admin — there is no self-service reset here. Ask yours to set a new password for you.',

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

  /* ---- SCR-AP-002 · dashboard and its statistics filter (AL-38) -------- */
  'admin.dashboard.filter.legend': 'Statistics for',
  'admin.dashboard.filter.today': 'Today',
  'admin.dashboard.filter.week': 'This week',
  'admin.dashboard.filter.month': 'This month',
  'admin.dashboard.filter.custom': 'Custom range',
  'admin.dashboard.filter.from': 'From',
  'admin.dashboard.filter.to': 'To',
  'admin.dashboard.filter.apply': 'Apply',
  'admin.dashboard.filter.comparison': 'vs previous period',
  'admin.dashboard.filter.export': 'Export CSV',
  'admin.dashboard.filter.chooseRange': 'Pick both ends of the range to see the figures for it.',
  'admin.dashboard.filter.timezone': 'Dates are Sri Lanka time (Asia/Colombo).',

  'admin.dashboard.period.heading': 'For the chosen period',
  'admin.dashboard.live.heading': 'Right now',
  'admin.dashboard.live.note': 'Live counts. These three do not move with the period filter.',

  'admin.dashboard.kpi.completedTrips': 'Completed trips',
  'admin.dashboard.kpi.grossFare': 'Gross fare',
  'admin.dashboard.kpi.newRidersDrivers': 'New riders / drivers',
  'admin.dashboard.kpi.newRiders': 'New riders',
  'admin.dashboard.kpi.newDrivers': 'New drivers',
  'admin.dashboard.kpi.riders': 'riders',
  'admin.dashboard.kpi.drivers': 'drivers',
  'admin.dashboard.kpi.dailyFeeRevenue': 'Daily-fee revenue',
  'admin.dashboard.kpi.onlineDrivers': 'Online drivers',
  'admin.dashboard.kpi.pendingVerifications': 'Pending verifications',
  'admin.dashboard.kpi.openTickets': 'Open tickets',

  // The whole sentence, because this is what a screen reader is given: the arrow
  // and the percentage beside it are `aria-hidden`.
  'admin.dashboard.delta.up': '{metric}: up {value} on the previous period',
  'admin.dashboard.delta.down': '{metric}: down {value} on the previous period',
  'admin.dashboard.delta.flat': '{metric}: unchanged on the previous period',
  // Not "0%". C061 answers null when the previous period was empty — growth from
  // nothing has no percentage — and that is a different fact from no change.
  'admin.dashboard.delta.unknown': '{metric}: no comparison, the previous period had none',

  // The mark is a word and is translated; the amount is formatted by `Intl`.
  'admin.dashboard.money': 'Rs {amount}',

  'admin.dashboard.alerts.heading': 'Needs attention',
  'admin.dashboard.alerts.clear': 'Nothing is waiting on you right now.',
  'admin.dashboard.alerts.verification': 'Submissions waiting to be verified',
  'admin.dashboard.alerts.tickets': 'Support tickets still open',
  'admin.dashboard.alerts.count': '{count} waiting',

  /* ---- SCR-AP-003 · verification queues (AL-39, C106) ------------------ */
  'admin.verification.queue.navLabel': 'Verification queues',
  'admin.verification.queue.drivingLicence': 'Driving-licence pending',
  'admin.verification.queue.vehicleRegistration': 'Vehicle-registration pending',
  'admin.verification.queue.fleetOrg': 'Fleet-org approval',
  'admin.verification.queue.headingDrivingLicence': 'Driving-licence verifications — pending',
  'admin.verification.queue.headingVehicleRegistration':
    'Vehicle-registration verifications — pending',
  'admin.verification.queue.headingFleetOrg': 'Fleet organisations — awaiting approval',
  'admin.verification.queue.caption': 'Pending verifications',
  // AL-27 in four words: an auto-verified document produces no pending field row,
  // so it cannot reach this queue at all.
  'admin.verification.queue.flagsOnly': 'Manual / doubtful flags only',
  'admin.verification.queue.orgGate': 'Approval gate before any non-read fleet operation',
  'admin.verification.queue.search': 'Search',
  'admin.verification.queue.searchHint':
    'Driver, vehicle or organisation — by name, registration number or ID.',
  'admin.verification.queue.status': 'Status',
  'admin.verification.queue.statusAll': 'Any status',
  'admin.verification.queue.apply': 'Apply',
  'admin.verification.queue.clear': 'Clear',
  'admin.verification.queue.review': 'Review',
  'admin.verification.queue.empty': 'Nothing is waiting in this queue.',
  'admin.verification.queue.total': '{count} pending',
  'admin.verification.queue.totalMore': '{count}+ pending',
  'admin.verification.queue.countMore': '{count}+',
  'admin.verification.queue.capped': 'Showing the first {count}. Narrow the search to reach the rest.',
  'admin.verification.status.pendingCount': 'Pending · {count}',

  'admin.verification.column.driver': 'Driver',
  'admin.verification.column.vehicle': 'Vehicle',
  'admin.verification.column.organisation': 'Organisation',
  'admin.verification.column.submitted': 'Submitted',
  'admin.verification.column.flagged': 'Flagged fields',
  'admin.verification.column.vehicles': 'Vehicles',
  'admin.verification.column.evidence': 'Evidence',
  'admin.verification.column.field': 'Field',
  'admin.verification.column.value': 'Value',
  'admin.verification.column.source': 'Source',
  'admin.verification.column.status': 'Status',
  'admin.verification.column.action': 'Action',

  'admin.verification.decided.approved': 'Approved.',
  'admin.verification.decided.rejected': 'Rejected.',

  /* ---- SCR-AP-003a · the entry, its documents and its fields ----------- */
  'admin.verification.detail.back': 'Back to queue',
  'admin.verification.detail.pendingFields': 'Pending review · {count} flagged fields',
  'admin.verification.detail.pendingReview': 'Pending review',
  'admin.verification.detail.readyToApprove': 'Every field confirmed',

  'admin.verification.doc.heading': 'Attached documents',
  'admin.verification.doc.hint': 'Tap a thumbnail to open it full size',
  'admin.verification.doc.empty': 'No documents are attached to this submission.',
  // AL-39: opening a document is itself an auditable act, and the officer should
  // know that before they open one rather than find it in a review afterwards.
  'admin.verification.doc.note':
    'Each thumbnail is a stored onboarding document. Opening one is itself written to the audit trail.',
  'admin.verification.doc.position': '{index} / {total}',
  'admin.verification.doc.capturedDragCrop': 'Original upload · captured with the in-app scanner',
  'admin.verification.doc.capturedUpload': 'Original upload · chosen from the gallery',
  'admin.verification.doc.drivingLicense': 'Driving licence',
  'admin.verification.doc.registration': 'Registration',
  'admin.verification.doc.permit': 'Route permit',
  'admin.verification.doc.insurance': 'Insurance',
  'admin.verification.doc.revenueLicense': 'Revenue licence',
  'admin.verification.doc.vehiclePhoto': 'Vehicle photo',
  'admin.verification.doc.bankStatement': 'Bank statement',
  'admin.verification.doc.passbookFirstPage': 'Passbook first page',
  'admin.verification.doc.proofOfAccount': 'Proof of account',
  'admin.verification.doc.lankaQr': 'LankaQR code',

  'admin.verification.fields.heading': 'AI-extracted fields',
  'admin.verification.fields.engine': 'Gemini Flash 3.0 · PII redacted',
  'admin.verification.fields.empty': 'Nothing was extracted for this submission.',
  'admin.verification.fields.note':
    'A row is Pending when the driver typed it, when the scan was doubtful, or when the plate does not match the registration number. Each has to be confirmed, or edited and confirmed.',

  'admin.verification.field.licenceNo': 'Licence no',
  'admin.verification.field.licenceExpiry': 'Licence expiry',
  'admin.verification.field.nicNo': 'NIC no',
  'admin.verification.field.allowedVehicleTypes': 'Allowed vehicle types',
  'admin.verification.field.insuranceExpiry': 'Insurance expiry',
  'admin.verification.field.revenueNo': 'Revenue licence no',
  'admin.verification.field.revenueExpiry': 'Revenue licence expiry',
  'admin.verification.field.regNoMatch': 'Registration number vs plate',
  'admin.verification.field.editConfirm': 'Edit & confirm',
  'admin.verification.field.confirmNamed': 'Confirm {field}',
  'admin.verification.field.editNamed': 'Edit {field}',
  'admin.verification.field.correctedValue': 'Corrected value',
  'admin.verification.field.working': 'Recording…',
  'admin.verification.field.valueRequired':
    'Type the corrected value, or press Confirm to accept what was extracted.',

  'admin.verification.source.ai': 'AI',
  'admin.verification.source.aiScored': 'AI {confidence}',
  'admin.verification.source.manual': 'Manual',
  'admin.verification.fieldStatus.autoVerified': 'Auto-verified',
  'admin.verification.fieldStatus.confirmed': 'Confirmed',
  'admin.verification.fieldStatus.pendingDoubtful': 'Pending · doubtful',
  'admin.verification.fieldStatus.pendingMismatch': 'Pending · mismatch',

  'admin.verification.step.profile': 'Profile / licence',
  'admin.verification.step.details': 'Vehicle details',
  'admin.verification.step.insurance': 'Insurance',
  'admin.verification.step.revenue': 'Revenue licence',
  'admin.verification.step.photos': 'Vehicle photos',
  'admin.verification.step.registration': 'Registration',
  'admin.verification.step.permit': 'Route permit',
  'admin.verification.step.kyc': 'Organisation KYC',
  'admin.verification.step.awaitingUpload': 'Not uploaded',

  'admin.verification.decision.heading': 'Decision',
  'admin.verification.decision.steps': 'Onboarding steps',
  'admin.verification.decision.reason': 'Reject reason (if any)',
  'admin.verification.decision.reasonHint': 'Shown to the applicant exactly as written.',
  'admin.verification.decision.approveDriver': 'Approve driver',
  'admin.verification.decision.approveVehicle': 'Approve vehicle',
  'admin.verification.decision.approveOrg': 'Approve organisation',
  'admin.verification.decision.reject': 'Reject with reason',
  'admin.verification.decision.working': 'Recording…',
  // US-2.10a, and the sentence the disabled button needs beside it — a control
  // that is off for a reason nobody states reads as broken.
  'admin.verification.approve.blocked': 'Approve unlocks once every pending field is confirmed.',
  'admin.verification.reject.reasonRequired':
    'Give a reason. The applicant is shown it exactly as written.',

  /* ---- SCR-AP-003b · the full-size viewer ------------------------------ */
  'admin.verification.viewer.title': '{document} · {position}',
  'admin.verification.viewer.previous': 'Previous',
  'admin.verification.viewer.zoomIn': 'Zoom in',
  'admin.verification.viewer.zoomOut': 'Zoom out',
  'admin.verification.viewer.rotate': 'Rotate a quarter turn',
  'admin.verification.viewer.reset': 'Reset zoom and rotation',

  /* ---- SCR-AP-003c · fleet-org approval -------------------------------- */
  'admin.verification.org.vehicleCount': '{count} vehicles',
  'admin.verification.org.kycComplete': 'KYC complete',
  'admin.verification.org.kycIncomplete': 'KYC incomplete',
  'admin.verification.org.heading': 'Organisation KYC',
  'admin.verification.org.caption': 'Organisation KYC details',
  'admin.verification.org.registeredName': 'Registered name',
  'admin.verification.org.registrationNo': 'Business registration no',
  'admin.verification.org.contactPhone': 'Authorised contact',
  'admin.verification.org.contactEmail': 'Contact email',
  'admin.verification.org.address': 'Registered address',
  'admin.verification.org.rejectionReason': 'Rejection reason',
  'admin.verification.org.payoutHeading': 'Bank & payout details',
  'admin.verification.org.payoutCaption': 'Bank and payout details',
  'admin.verification.org.payoutNone': 'This organisation has submitted no bank details yet.',
  'admin.verification.org.bank': 'Bank',
  'admin.verification.org.branch': 'Branch',
  'admin.verification.org.accountNo': 'Account number',
  'admin.verification.org.accountHolder': 'Account holder name',
  'admin.verification.org.payoutRejection': 'Payout rejection reason',
  // BR-31.1, said where the decision is taken: approving the organisation is also
  // authorising where its money goes.
  'admin.verification.org.payoutGate':
    'Approving the organisation verifies these details. Until then it can have no Paid vehicle and no Paid subscription billed.',
  'admin.verification.org.documents': 'Attached evidence',
  'admin.verification.org.documentsEmpty': 'This organisation has attached no documents yet.',
  'admin.verification.payout.pending': 'Payout pending',
  'admin.verification.payout.verified': 'Payout verified',
  'admin.verification.payout.rejected': 'Payout rejected',
  'admin.verification.payout.superseded': 'Payout superseded',

  /* ---- SCR-AP-004 · moderation (US-12.6, US-14.3, C107) ---------------- */
  'admin.moderation.queue.heading': 'Vehicle reports — pending review',
  'admin.moderation.queue.caption': 'Vehicle reports awaiting a decision',
  // US-12.6, stated where the decision is taken. The count is the platform's, not
  // this screen's: safety-svc reaches it inside the transaction that writes the
  // third status.
  'admin.moderation.queue.rule': '{count} confirmed reports delist the vehicle',
  'admin.moderation.queue.scope':
    'Reports nobody has decided yet. A confirmed or dismissed report leaves this queue.',
  'admin.moderation.queue.total': '{count} waiting',
  'admin.moderation.queue.totalMore': '{count}+ waiting',
  'admin.moderation.queue.capped': 'Showing the first {count}.',
  'admin.moderation.queue.empty': 'No vehicle report is waiting on you.',

  'admin.moderation.column.subject': 'Subject',
  'admin.moderation.column.reports': 'Reports',
  'admin.moderation.column.reason': 'Reason',
  'admin.moderation.column.raised': 'Raised',
  'admin.moderation.column.action': 'Action',

  // "Pending", never "strikes": these are reports nobody has upheld yet, and the
  // confirmed total is a figure only a decision returns.
  'admin.moderation.report.pendingCount': '{count} pending',
  'admin.moderation.report.noReason': 'No reason was given',
  'admin.moderation.report.suspendVehicle': 'Suspend this vehicle',
  'admin.moderation.report.confirm': 'Confirm report',
  'admin.moderation.report.dismiss': 'Dismiss',
  'admin.moderation.report.working': 'Recording…',
  'admin.moderation.report.confirmNamed': 'Confirm the report against vehicle {vehicle}',
  'admin.moderation.report.dismissNamed': 'Dismiss the report against vehicle {vehicle}',

  'admin.moderation.verdict.confirmed': 'Report confirmed.',
  'admin.moderation.verdict.confirmedCount':
    'Report confirmed. This vehicle now has {count} confirmed reports; {remaining} more delists it.',
  'admin.moderation.verdict.delisted':
    'Report confirmed. That is {count} confirmed reports, so the vehicle has been delisted.',
  'admin.moderation.verdict.dismissed': 'Report dismissed.',

  'admin.moderation.suspend.heading': 'Suspend / ban',
  'admin.moderation.suspend.subject': 'Suspend a',
  'admin.moderation.suspend.driver': 'Driver',
  'admin.moderation.suspend.vehicle': 'Vehicle',
  'admin.moderation.suspend.subjectId': 'Driver / vehicle ID',
  'admin.moderation.suspend.subjectIdHint': 'The platform id, exactly as it appears on the record.',
  'admin.moderation.suspend.reason': 'Reason',
  'admin.moderation.suspend.reasonHint': 'Required, and recorded against your name.',
  'admin.moderation.suspend.apply': 'Apply',
  'admin.moderation.suspend.working': 'Recording…',
  'admin.moderation.suspend.idRequired': 'Enter the id exactly as it appears on the record.',
  // admin-bff refuses one without a reason and its own comment says why: a
  // suspension nobody can explain is one nobody can appeal.
  'admin.moderation.suspend.reasonRequired':
    'Give a reason. It is recorded in the audit trail and it is what an appeal is answered from.',
  'admin.moderation.suspend.noDuration':
    'A suspension stays in force until somebody lifts it. There is no duration to choose and nothing reinstates it automatically.',
  'admin.moderation.suspend.doneDriver':
    'Driver suspended. Their session has ended and they take no new dispatch; a ride already in flight is left to finish.',
  'admin.moderation.suspend.doneVehicle':
    'Vehicle suspended. It has left dispatch and the live map.',

  /* ---- SCR-AP-005 · support & disputes (US-14.13, US-16.3, C107) ------- */
  'admin.support.filter.status': 'Status',
  'admin.support.filter.statusAll': 'Any status',
  'admin.support.filter.category': 'Category',
  // A stored key, not copy: `support.tickets.category` carries no CHECK, so the
  // agent filters on the value the row holds.
  'admin.support.filter.categoryHint': 'The stored category key, such as driver_qr_dispute.',
  'admin.support.filter.apply': 'Apply',
  'admin.support.filter.clear': 'Clear',

  'admin.support.status.open': 'Open',
  'admin.support.status.inProgress': 'In progress',
  'admin.support.status.resolved': 'Resolved',

  'admin.support.category.dailyFeeRefund': 'Daily-fee refund request',
  'admin.support.category.driverQrDispute': 'Driver-QR payment dispute',

  'admin.support.queue.heading': 'Queue',
  'admin.support.queue.empty': 'No ticket matches this filter.',
  'admin.support.queue.finance': 'Finance',
  'admin.support.queue.total': '{count} in this queue',
  'admin.support.queue.totalMore': '{count}+ in this queue',
  'admin.support.queue.capped': 'Showing the first {count}. Narrow the filter to reach the rest.',

  'admin.support.detail.raisedBy': 'Raised by',
  'admin.support.detail.noneHeading': 'No ticket open',
  'admin.support.detail.noneBody': 'Choose a ticket from the queue to read it.',
  'admin.support.detail.notInView':
    'That ticket is not in the pile you are filtered to. Clear the filter to find it.',

  'admin.support.thread.heading': 'Thread',
  'admin.support.thread.empty': 'This ticket carries no message.',
  'admin.support.thread.raiser': 'The person who raised it',
  'admin.support.thread.agent': 'MageRide support',

  'admin.support.lookup.heading': 'Read-only lookup',
  'admin.support.lookup.passenger': 'Open the passenger record',
  'admin.support.lookup.driver': 'Open the driver record',
  'admin.support.lookup.note':
    'A directory record is read-only, and opening one is itself written to the audit trail.',
  'admin.support.lookup.none': 'The directories are not part of your role.',

  // The C107 fence, in the words an agent needs: not "you cannot", but "here is
  // who does, and this ticket is already with them".
  'admin.support.refund.heading': 'Refund request',
  'admin.support.refund.note':
    'Support does not move money. A refund is raised and paid by Finance on the refunds queue — and a daily-fee refund or a driver-QR dispute is already on that queue, because that is what its category means.',
  'admin.support.refund.link': 'Open the refunds queue',

  'admin.support.resolved.heading': 'Resolved',
  'admin.support.resolved.note':
    'This ticket is closed. The person who raised it can read the reply above in their app.',

  'admin.support.resolve.response': 'Your reply',
  'admin.support.resolve.responseHint':
    'Shown to the person who raised the ticket, exactly as you write it.',
  'admin.support.resolve.submit': 'Resolve',
  'admin.support.resolve.working': 'Recording…',
  'admin.support.resolve.responseRequired':
    'Write the reply first — it is what the person who raised the ticket is shown.',
  'admin.support.resolve.done': 'Ticket resolved.',

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
