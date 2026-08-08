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

  /* ---- SCR-AP-006 · finance & reconciliation (C108) --------------------- */
  'admin.finance.tabs.label': 'Finance views',
  'admin.finance.tab.settlement': 'Gateway settlement',
  'admin.finance.tab.ledger': 'Wallet ledger',
  'admin.finance.tab.refunds': 'Refunds',
  'admin.finance.tab.reversals': 'Wallet reversals',
  'admin.finance.tab.payouts': 'Payouts',
  'admin.finance.tab.transfers': 'Credit transfers',

  'admin.finance.column.action': 'Action',
  'admin.finance.filter.from': 'From',
  'admin.finance.filter.to': 'To',
  'admin.finance.filter.method': 'Gateway',
  'admin.finance.filter.methodAll': 'Both gateways',
  'admin.finance.filter.kind': 'Kind',
  'admin.finance.filter.kindAll': 'All four kinds',
  'admin.finance.filter.party': 'Driver, passenger or fleet ID',
  'admin.finance.filter.partyHint': 'Either side of the entry. Leave empty for every party.',
  'admin.finance.filter.apply': 'Apply',
  'admin.finance.filter.clear': 'Clear',
  'admin.finance.filter.timezone': 'Dates are Sri Lanka time (Asia/Colombo).',

  // AL-57 removed the +5% the wireframe still prints beside OnePay: the rail is
  // no longer a ride payment method, so nothing charges it. The name is the label.
  'admin.finance.method.onepay': 'OnePay',
  'admin.finance.method.lankaqr': 'LankaQR',

  'admin.finance.settlement.heading': 'OnePay / LankaQR settlement reconciliation',
  'admin.finance.settlement.caption': 'Gateway settlement against the platform ledger',
  'admin.finance.settlement.window': 'Window:',
  'admin.finance.settlement.gateway': 'Gateway',
  'admin.finance.settlement.sessions': 'Settled top-ups',
  'admin.finance.settlement.settled': 'Confirmed by gateway',
  'admin.finance.settlement.posted': 'Reached the ledger',
  'admin.finance.settlement.variance': 'Difference',
  'admin.finance.settlement.investigate': 'Investigate',
  'admin.finance.settlement.investigateNamed': 'Investigate the {gateway} exceptions',
  'admin.finance.settlement.empty': 'Neither gateway settled anything in this window.',
  // AL-05, stated on the screen because its absence is the design.
  'admin.finance.settlement.noBankTransfer':
    'There is no bank-transfer rail to reconcile — the two gateways above settle wallet top-ups, and nothing else.',
  'admin.finance.settlement.ledgerNote':
    'Every figure is read from the double-entry ledger in whole cents.',
  // Zero is not "a small difference": it is the definition of reconciled.
  'admin.finance.variance.none': 'Reconciled',

  'admin.finance.exceptions.heading': 'Settlement exceptions',
  'admin.finance.exceptions.caption': 'Gateway sessions that need a person to look at them',
  'admin.finance.exceptions.note':
    'Worked oldest first. A session that settles itself leaves this queue on its own — there is nothing here to close.',
  'admin.finance.exceptions.count': '{count} exceptions',
  'admin.finance.exceptions.kind': 'What happened',
  'admin.finance.exceptions.driver': 'Driver',
  'admin.finance.exceptions.amount': 'Amount',
  'admin.finance.exceptions.opened': 'Opened',
  'admin.finance.exceptions.reference': 'Gateway reference',
  'admin.finance.exceptions.empty': 'Nothing is waiting. Both gateways match the ledger.',
  'admin.finance.exception.amountMismatch': 'Amount does not match',
  'admin.finance.exception.settledNotPosted': 'Settled, never posted',
  'admin.finance.exception.unsettled': 'Still open',
  'admin.finance.exception.gatewayFailed': 'Gateway reported a failure',

  'admin.finance.ledger.heading': 'Wallet transactions',
  'admin.finance.ledger.caption': 'Top-ups, daily fees, voucher purchases and credit transfers',
  'admin.finance.ledger.when': 'When',
  'admin.finance.ledger.fromParty': 'From',
  'admin.finance.ledger.toParty': 'To',
  'admin.finance.ledger.total': 'Total {amount}',
  'admin.finance.ledger.exportCsv': 'Export CSV',
  'admin.finance.ledger.exportPdf': 'Export PDF',
  // D-26: the PDF renderer has no Sinhala or Tamil glyphs and must not pretend to.
  'admin.finance.ledger.pdfNote':
    'The PDF is an English-only table of figures and identifiers. Use the CSV for anything that has to be read in Sinhala or Tamil.',
  'admin.finance.ledger.empty': 'No wallet transaction matches this filter.',
  'admin.finance.ledger.capped':
    'Showing the first {count}. Narrow the window or the party to reach the rest.',
  'admin.finance.kind.topup': 'Top-up',
  'admin.finance.kind.dailyFee': 'Daily fee',
  'admin.finance.kind.voucherPurchase': 'Voucher purchase',
  'admin.finance.kind.driverTransfer': 'Credit transfer',
  'admin.finance.account.passenger': 'Passenger account',
  'admin.finance.account.driver': 'Driver wallet',
  'admin.finance.account.fleet': 'Fleet account',
  'admin.finance.account.platform': 'MageRide',
  'admin.finance.account.suspense': 'Suspense account',
  // AL-01, on the credit-transfer view and nowhere else.
  'admin.finance.transfers.note':
    'A driver-to-driver transfer moves the exact value. There is no commission on a transfer and no per-driver rate — the only commission on the platform is the bulk-voucher discount, charged once at purchase and set in Configuration.',

  'admin.finance.refund.heading': 'Refund queue',
  'admin.finance.refund.caption': 'Refunds awaiting settlement, and overpaid payments nobody has raised one for',
  'admin.finance.refund.note':
    'Two kinds of row: refunds somebody already raised, and payments that took too much and that nobody has acted on yet.',
  'admin.finance.refund.readOnlyNote':
    'You can read this queue and hand a case to Finance. Raising a refund is theirs to do.',
  'admin.finance.refund.source': 'Kind',
  'admin.finance.refund.sourceAll': 'Both kinds',
  'admin.finance.refund.raised': 'Refund raised',
  'admin.finance.refund.overpaid': 'Overpaid, not raised',
  'admin.finance.refund.passenger': 'Passenger',
  'admin.finance.refund.payment': 'Payment',
  'admin.finance.refund.paymentHint': 'The payment the refund is raised against.',
  'admin.finance.refund.status': 'Status',
  'admin.finance.refund.statusAll': 'Open ones, plus every unraised overpayment',
  'admin.finance.refund.statusHint': 'Leave empty to see everything that still needs somebody.',
  'admin.finance.refund.requested': 'Raised',
  'admin.finance.refund.ofPayment': 'of',
  'admin.finance.refund.raise': 'Raise a refund',
  'admin.finance.refund.raiseHeading': 'Raise a refund',
  'admin.finance.refund.empty': 'Nothing is waiting in the refund queue.',
  'admin.finance.refund.queueTotal': '{count} in this queue',
  'admin.finance.refund.kind': 'How much',
  'admin.finance.refund.kindFull': 'The whole payment',
  'admin.finance.refund.kindPartial': 'Part of it',
  'admin.finance.refund.kindOverpaid': 'Give back the overpayment',
  'admin.finance.refund.amount': 'Amount',
  'admin.finance.refund.amountHint': 'In rupees. Only a partial refund needs one.',
  'admin.finance.refund.ceiling': 'The payment collected',
  'admin.finance.refund.reasonCode': 'Reason code',
  'admin.finance.refund.reasonCodeHint': 'A short code the finance team agrees on, kept with the refund.',
  'admin.finance.refund.submit': 'Raise the refund',
  'admin.finance.refund.working': 'Raising…',
  'admin.finance.refund.done': 'Refund raised. It is now {status}.',
  'admin.finance.refund.notInQueue':
    'That payment is not in the queue below. Check the ID before raising anything against it.',
  'admin.finance.refund.paymentRequired': 'Enter the payment ID, exactly as it appears on the row.',
  'admin.finance.refund.reasonRequired': 'Give a reason code — it is kept with the refund.',
  'admin.finance.refund.amountRequired': 'A partial refund has to say how much.',
  'admin.finance.refund.amountInvalid': 'Enter the amount in rupees, for example 250.00',

  'admin.finance.reversal.heading': 'Wallet reversal / adjustment',
  'admin.finance.reversal.note':
    'Puts a daily fee back on a driver’s wallet as a new ledger entry. Nothing is edited and nothing is deleted.',
  'admin.finance.reversal.driver': 'Driver ID',
  'admin.finance.reversal.driverHint': 'The platform ID, exactly as it appears on the record.',
  'admin.finance.reversal.vehicle': 'Vehicle ID',
  'admin.finance.reversal.vehicleHint': 'The vehicle the fee was charged for.',
  'admin.finance.reversal.feeDate': 'Day of the fee',
  'admin.finance.reversal.feeDateHint': 'Sri Lanka time. One reversal per charge, ever.',
  'admin.finance.reversal.amount': 'Amount',
  'admin.finance.reversal.amountHint': 'Leave empty to reverse the whole fee that was charged.',
  'admin.finance.reversal.reason': 'Reason',
  'admin.finance.reversal.reasonHint': 'Required, and kept with the entry against your name.',
  'admin.finance.reversal.submit': 'Post the reversal',
  'admin.finance.reversal.working': 'Posting…',
  'admin.finance.reversal.done': 'Reversal posted.',
  // `replayed: true` — the second press of a double click. The operator is told
  // it did nothing rather than left to wonder whether it credited twice.
  'admin.finance.reversal.replayed':
    'This fee had already been reversed. Nothing was credited a second time.',
  'admin.finance.reversal.balanceAfter': 'Wallet balance now:',
  'admin.finance.reversal.driverRequired': 'Enter the driver ID, exactly as it appears on the record.',
  'admin.finance.reversal.vehicleRequired': 'Enter the vehicle ID the fee was charged for.',
  'admin.finance.reversal.dateRequired': 'Pick the day the fee was charged.',
  'admin.finance.reversal.reasonRequired': 'Say why. The reason is kept with the entry.',

  'admin.finance.payouts.heading': 'Payout instructions',
  'admin.finance.payouts.note':
    'The weekly sweep pays each driver their whole wallet balance to the bank account they had verified.',
  'admin.finance.payouts.batchesHeading': 'Weekly runs',
  'admin.finance.payouts.batchesCaption': 'Payout runs, newest first',
  'admin.finance.payouts.instructionsCaption': 'Payout instructions and where each one got to',
  'admin.finance.payouts.run': 'Run date',
  'admin.finance.payouts.status': 'Status',
  'admin.finance.payouts.instructions': 'Instructions',
  'admin.finance.payouts.total': 'Total paid out',
  'admin.finance.payouts.completed': 'Completed',
  'admin.finance.payouts.driver': 'Driver',
  'admin.finance.payouts.account': 'Account',
  'admin.finance.payouts.created': 'Created',
  'admin.finance.payouts.settled': 'Settled',
  'admin.finance.payouts.batchesEmpty': 'No payout run has been recorded yet.',
  'admin.finance.payouts.instructionsEmpty': 'No payout instruction has been recorded yet.',
  // C133 removed the retry route: a failed instruction has already been reversed,
  // so there is nothing to re-send and the money is back where it started.
  'admin.finance.payouts.noRetry':
    'A failed payout has already been put back on the driver’s wallet, so there is nothing to send again — the next weekly run picks it up.',

  /* ---- SCR-AP-007 · platform configuration (C108) ----------------------- */
  'admin.config.tabs.label': 'Configuration',
  'admin.config.tab.tariffs': 'Fare tariffs',
  'admin.config.tab.fees': 'Daily fee',
  'admin.config.tab.vouchers': 'Commission & vouchers',
  'admin.config.tab.levels': 'Driver Level',
  'admin.config.tab.flags': 'Feature flags',
  'admin.config.tab.gtfs': 'Transit data',
  'admin.config.column.vehicle': 'Vehicle',
  'admin.config.working': 'Saving…',

  'admin.config.vehicle.motorbike': 'Motorbike',
  'admin.config.vehicle.threeWheeler': 'Three-wheeler',
  'admin.config.vehicle.flex': 'Flex',
  'admin.config.vehicle.sedan': 'Sedan',
  'admin.config.vehicle.miniVan': 'Mini van',
  'admin.config.vehicle.van': 'Van',
  'admin.config.vehicle.truck': 'Truck',
  'admin.config.vehicle.miniTruck': 'Mini truck',
  'admin.config.vehicle.bus': 'Bus',
  'admin.config.vehicle.train': 'Train',

  'admin.config.tariffs.heading': 'Fare tariffs',
  // The platform serves no read of the tariffs in force, so the form starts empty
  // rather than showing figures this screen would have had to invent.
  'admin.config.tariffs.noReadNote':
    'MageRide does not yet serve the rates currently in force back to this screen, so these boxes start empty. Publishing replaces the whole table — fill in every row.',
  'admin.config.tariffs.modeANote':
    'Bus and train are Mode A: they carry no fare and no daily fee, so they have no row here.',
  'admin.config.tariffs.caption': 'Mode C fare per vehicle type',
  'admin.config.tariffs.firstKm': 'First km (Rs)',
  'admin.config.tariffs.perKm': 'Per km (Rs)',
  'admin.config.tariffs.peak': 'Peak %',
  'admin.config.tariffs.night': 'Night %',
  'admin.config.tariffs.windowsHeading': 'Peak and night windows',
  'admin.config.tariffs.peakWindow': 'Peak window',
  'admin.config.tariffs.nightWindow': 'Night window',
  'admin.config.tariffs.windowStart': 'Starts',
  'admin.config.tariffs.windowEnd': 'Ends',
  'admin.config.tariffs.windowPct': 'Uplift %',
  'admin.config.tariffs.windowNote':
    'Sri Lanka time. A window may run past midnight — 22:00 to 05:00 is a night window, not a mistake. Leave all three boxes empty to publish without one.',
  'admin.config.tariffs.effectiveFrom': 'In force from',
  'admin.config.tariffs.effectiveFromHint': 'Leave empty to publish immediately. A trip already priced keeps its rate.',
  'admin.config.tariffs.submit': 'Publish new tariffs',
  'admin.config.tariffs.saved': 'New tariff version published.',
  'admin.config.tariffs.rowRequired': 'Every vehicle type needs a first-km and a per-km rate.',
  'admin.config.tariffs.windowIncomplete': 'A window needs a start, an end and an uplift — or all three left empty.',

  'admin.config.fees.heading': 'Daily fee and subscription pricing',
  'admin.config.fees.noReadNote':
    'MageRide does not yet serve the rates currently in force back to this screen. Saving changes only the rung you set here and leaves the others alone.',
  'admin.config.fees.mode': 'Mode',
  'admin.config.fees.modeA': 'Mode A — bus and train (free)',
  'admin.config.fees.modeB': 'Mode B — monthly subscription fee',
  'admin.config.fees.modeC': 'Mode C — on-demand daily fee',
  'admin.config.fees.amount': 'Amount (Rs)',
  'admin.config.fees.amountHint': 'Per day for Mode C, per month for Mode B.',
  'admin.config.fees.submit': 'Save this rate',
  'admin.config.fees.saved': 'Rate saved. It applies to the next charge and to no charge already taken.',
  'admin.config.fees.amountRequired': 'Enter the amount in rupees, for example 200.00',
  'admin.config.fees.modeBNote':
    'The fare a Mode B passenger pays is set by the fleet owner per subscriber in the Fleet Portal, not here. What is set here is what MageRide charges the fleet for the vehicle.',

  'admin.config.vouchers.heading': 'Bulk voucher commission, by voucher value',
  'admin.config.vouchers.note':
    'A driver who buys bulk credit resells it at face value; this percentage is their whole margin and is charged once, at purchase. There is no per-driver rate and no commission on a transfer.',
  'admin.config.vouchers.caption': 'Voucher value, commission, what the driver pays and what they receive',
  'admin.config.vouchers.denomination': 'Voucher value',
  'admin.config.vouchers.percent': 'Commission %',
  'admin.config.vouchers.pays': 'Driver pays',
  'admin.config.vouchers.credit': 'Wallet credit',
  'admin.config.vouchers.active': 'On sale',
  'admin.config.vouchers.activeYes': 'On sale',
  'admin.config.vouchers.activeNo': 'Withdrawn',
  'admin.config.vouchers.empty': 'No voucher value has been configured yet.',
  'admin.config.vouchers.editHeading': 'Add or change a voucher value',
  'admin.config.vouchers.denominationHint': 'In rupees. Setting an existing value changes it.',
  'admin.config.vouchers.percentHint': 'The discount at purchase. 10 means a Rs 1,000 voucher costs Rs 900.',
  'admin.config.vouchers.activeLabel': 'On sale',
  'admin.config.vouchers.submit': 'Save this value',
  'admin.config.vouchers.saved': 'Voucher ladder saved. The Driver App top-up screen shows it immediately.',
  'admin.config.vouchers.denominationRequired': 'Enter the voucher value in rupees, for example 1000',
  'admin.config.vouchers.percentRequired': 'Enter a commission between 0 and 100.',

  'admin.config.levels.heading': 'Driver Level parameters',
  'admin.config.levels.noReadNote':
    'MageRide does not yet serve the values currently in force back to this screen. A box left empty is left alone.',
  'admin.config.levels.defaultsNote':
    'The documented starting values are: every driver begins at Level 3, 500 points move a driver a level, and three confirmed reports delist a vehicle.',
  'admin.config.levels.threshold': 'Points per level',
  'admin.config.levels.thresholdHint': 'How many rating points move a driver up one level.',
  'admin.config.levels.noShow': 'No-show penalty',
  'admin.config.levels.noShowHint': 'Points taken off when a driver does not turn up.',
  'admin.config.levels.cancellation': 'Cancellation penalty',
  'admin.config.levels.cancellationHint': 'Points taken off when a driver cancels an accepted trip.',
  'admin.config.levels.jobBoard': 'Lowest level on the Job Board',
  'admin.config.levels.jobBoardHint': 'Level 1 never sees the Job Board, so this is 2 or 3.',
  'admin.config.levels.submit': 'Save these parameters',
  'admin.config.levels.saved': 'Driver Level parameters saved. They apply to points awarded from now on.',
  'admin.config.levels.thresholdRequired': 'Points per level has to be at least 1.',
  'admin.config.levels.jobBoardRange': 'The lowest Job Board level is between 1 and 3.',
  'admin.config.levels.nothingToSave': 'Change at least one value before saving.',

  'admin.config.flags.heading': 'Platform feature flags',
  'admin.config.flags.note': 'A flag takes effect for every request made after it is set.',
  'admin.config.flags.on': 'On',
  'admin.config.flags.off': 'Off',
  'admin.config.flags.turnOn': 'Turn on',
  'admin.config.flags.turnOff': 'Turn off',
  'admin.config.flags.updated': 'Last changed',
  'admin.config.flags.empty': 'No feature flag has been set on this platform yet.',
  'admin.config.flags.addHeading': 'Add a flag, or set one this list does not carry yet',
  'admin.config.flags.key': 'Flag key',
  'admin.config.flags.keyHint': 'Lower case, digits, dots, dashes and underscores — as the code reads it.',
  'admin.config.flags.description': 'What it does',
  'admin.config.flags.descriptionHint': 'In your own words. Leave empty to keep what is already stored.',
  'admin.config.flags.enable': 'Turn it on now',
  'admin.config.flags.add': 'Save the flag',
  'admin.config.flags.saved': 'Feature flag saved.',
  'admin.config.flags.keyInvalid': 'A flag key is lower case and starts with a letter, for example new_pay_sheet',

  /* ---- SCR-AP-008 · users & roles (Epic 21, AL-06, C108) ---------------- */
  'admin.rbac.lookupHeading': 'Find an internal user',
  // There is no route that lists internal users, so this is a lookup by ID rather
  // than a directory that came back empty. Saying which it is matters.
  'admin.rbac.noDirectory':
    'MageRide does not yet serve a list of internal users, so this screen works from a user ID.',
  'admin.rbac.noProvisioning':
    'Creating an internal account is not possible from here yet — the platform has no route for it. Ask an engineer to provision the account, then grant its roles here.',
  'admin.rbac.userId': 'User ID',
  'admin.rbac.userIdHint': 'The platform ID of the person whose roles you want to see.',
  'admin.rbac.userIdInvalid': 'That is not a MageRide user ID. Check it and try again.',
  'admin.rbac.lookup': 'Show roles',
  'admin.rbac.grantsHeading': 'Roles held',
  'admin.rbac.role': 'Role to grant',
  'admin.rbac.roleHint': 'Only the six internal roles can be granted here.',
  'admin.rbac.grant': 'Grant',
  'admin.rbac.granting': 'Granting…',
  'admin.rbac.revoke': 'Revoke',
  'admin.rbac.revoking': 'Revoking…',
  'admin.rbac.primary': 'the account’s own role — it cannot be revoked',
  'admin.rbac.nothingToGrant': 'This account already holds every internal role.',
  'admin.rbac.granted': '{role} granted. It applies to their next sign-in.',
  'admin.rbac.revoked': '{role} revoked. It applies to their next sign-in.',
  'admin.rbac.roleRequired': 'Choose a role to grant.',
  'admin.rbac.noAccountRevoke':
    'Suspending an account and ending its live sessions is not possible from here yet — the platform has no route for it. Revoking a role is what this screen can do.',
  'admin.rbac.showRole': 'Show the permissions of',
  'admin.rbac.showRoleApply': 'Show',
  'admin.rbac.permissionSet': 'Permission set — {role}',
  'admin.rbac.readOnly': 'read-only',
  'admin.rbac.permissionCaption': 'What this role may do, area by area',
  'admin.rbac.effectiveHeading': 'What this account can actually do',
  'admin.rbac.effectiveNote': 'all their roles together',
  'admin.rbac.effectiveCaption': 'The permissions this account holds across every role it has',
  'admin.rbac.area': 'Area',
  'admin.rbac.cell': 'Permission',
  'admin.rbac.capabilities': 'What that allows',
  'admin.rbac.permissionEmpty': 'Nothing to show for this role.',
  // US-21.3 asks for editable permission sets; the platform deliberately has no
  // route, and the reason is worth telling the one person who could have used one.
  'admin.rbac.matrixNote':
    'These permissions come from the MageRide role model itself and cannot be edited here — changing them is a change to the platform, not a setting.',
  'admin.rbac.grant.read': 'Read',
  'admin.rbac.grant.write': 'Change',
  'admin.rbac.grant.configure': 'Configure',
  'admin.rbac.grant.raise': 'Raise a request',
  'admin.rbac.grant.ownScope': 'own records only',
  'admin.rbac.grant.none': 'Nothing',

  /* ---- SCR-AP-009 · audit trail (US-19.3, D-35, C108) ------------------- */
  'admin.audit.heading': 'Immutable admin-action log',
  'admin.audit.caption': 'Every admin action, who took it and what it was about',
  'admin.audit.readOnly': 'read-only',
  'admin.audit.appendOnly':
    'This log is append-only. Nothing on the platform can edit or delete an entry, including this screen.',
  'admin.audit.column.when': 'Time',
  'admin.audit.column.actor': 'Actor',
  'admin.audit.column.role': 'Role',
  'admin.audit.column.action': 'Action',
  'admin.audit.column.target': 'Target',
  'admin.audit.column.change': 'Before / after',
  'admin.audit.empty': 'No entry matches this filter.',
  'admin.audit.filter.actor': 'Actor ID',
  'admin.audit.filter.subject': 'Target ID',
  'admin.audit.filter.idHint': 'A MageRide platform ID. Leave empty for everybody.',
  'admin.audit.filter.idInvalid': 'That is not a MageRide ID. Check it and try again.',
  'admin.audit.filter.action': 'Action',
  'admin.audit.filter.actionHint': 'The action exactly as the log records it, e.g. WALLET_FEE_REVERSED',
  'admin.audit.filter.from': 'From',
  'admin.audit.filter.to': 'To',
  'admin.audit.filter.timezone': 'Times are Sri Lanka time (Asia/Colombo).',
  'admin.audit.filter.apply': 'Apply',
  'admin.audit.filter.clear': 'Clear',
  'admin.audit.first': 'Back to the newest entries',
  'admin.audit.next': 'Older entries',
  'admin.audit.export': 'Export CSV',
  // A truncation nobody is told about is the one failure an audit export cannot
  // have, so the cap is on the screen as well as in the file.
  'admin.audit.exportCap':
    'The export follows this filter up to {count} entries and says so in the file when it stops there.',

  /* ---- D-35 ------------------------------------------------------------ */
  'admin.audit.notice': 'This action is written to the audit trail against your name.',
  'admin.audit.recorded': 'Recorded in the audit trail as {action}.',
  // Δ C108. Four /v1/admin/** routes are answered by their owning service without
  // passing through admin-bff, so no audit.events row is written for them. Telling
  // an operator otherwise would make this console untrustworthy on the one subject
  // it exists to be trusted on. See the C108 handoff.
  'admin.audit.notRecorded':
    'This change is answered by {service} directly, so it does not appear in the audit trail.',

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
