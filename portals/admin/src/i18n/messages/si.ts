/** Sinhala (si) resources for the Admin Portal. Typed against `AdminMessages`, so it cannot fall behind `en.ts`. */

import type { AdminMessages } from './en';

export const adminSi: AdminMessages = {
  /* ---- Shell chrome ---------------------------------------------------- */
  'admin.appName': 'MageRide පරිපාලනය',
  'admin.tagline': 'අභ්‍යන්තර කාර්ය මණ්ඩලය සඳහා පමණි',
  'admin.skipToContent': 'අන්තර්ගතයට යන්න',
  'admin.nav.label': 'මොඩියුල',
  'admin.nav.open': 'මෙනුව විවෘත කරන්න',
  'admin.nav.close': 'මෙනුව වසන්න',
  'admin.user.menu': 'ඔබේ ගිණුම',
  'admin.user.signOut': 'ඉවත් වන්න',
  'admin.user.roles': 'භූමිකා',
  'admin.appearance.label': 'පෙනුම',
  'admin.appearance.light': 'ලා පැහැ',
  'admin.appearance.dark': 'අඳුරු',
  'admin.appearance.system': 'මගේ උපාංගයට අනුව',
  'admin.language.label': 'භාෂාව',

  /* ---- SCR-AP-001 · sign-in -------------------------------------------- */
  'admin.signIn.heading': 'MageRide පරිපාලනය',
  'admin.signIn.email': 'කාර්යාල විද්‍යුත් තැපෑල',
  'admin.signIn.password': 'මුරපදය',
  'admin.signIn.submit': 'පිවිසෙන්න',
  'admin.signIn.submitting': 'පිවිසෙමින්…',
  'admin.signIn.or': 'නැතහොත්',
  'admin.signIn.google': 'Google සමඟ පිවිසෙන්න',
  'admin.signIn.noSecondFactor':
    'OTP හෝ authenticator පියවරක් නැත — පිවිසීමෙන් පසු ඔබ කෙලින්ම ඔබේ වැඩට යයි.',
  'admin.signIn.emailRequired': 'ඔබේ කාර්යාල විද්‍යුත් තැපැල් ලිපිනය ඇතුළත් කරන්න',
  'admin.signIn.passwordRequired': 'ඔබේ මුරපදය ඇතුළත් කරන්න',
  'admin.signIn.signedOut': 'ඔබ ගිණුමෙන් ඉවත් කර ඇත.',
  'admin.signIn.forgot': 'මුරපදය අමතක වුණාද?',
  'admin.signIn.forgotBody':
    'අභ්‍යන්තර ගිණුම් සාදන්නේත් නැවත සකසන්නේත් Super Admin කෙනෙකි — මෙහි ස්වයං-සේවා යළි සැකසීමක් නැත. ඔබට නව මුරපදයක් ලබා දෙන ලෙස ඔහුගෙන්/ඇයගෙන් ඉල්ලන්න.',

  /* ---- Errors ---------------------------------------------------------- */
  'admin.error.title': 'එය සාර්ථක නොවීය',
  'admin.error.unauthorized': 'ඔබේ සැසිය අවසන් වී ඇත. නැවත පිවිසෙන්න.',
  'admin.error.forbidden': 'ඔබේ භූමිකාව එයට අවසර නොදේ.',
  'admin.error.notFound': 'එම වාර්තාව තවදුරටත් නොපවතී.',
  'admin.error.validationFailed': 'ලකුණු කර ඇති ක්ෂේත්‍ර පරීක්ෂා කර නැවත උත්සාහ කරන්න.',
  'admin.error.conflict': 'වෙනත් අයෙක් මෙය පළමුව වෙනස් කර ඇත. පිටුව නැවත පූරණය කර උත්සාහ කරන්න.',
  'admin.error.accountBlocked': 'මෙම ගිණුම අවහිර කර ඇත. ප්‍රධාන පරිපාලකයෙකුට එය යළි සක්‍රිය කළ හැක.',
  'admin.error.invalidCredentials': 'එම විද්‍යුත් තැපෑල සහ මුරපදය ගිණුමකට නොගැළපේ.',
  'admin.error.accountLocked':
    'අසාර්ථක පිවිසුම් උත්සාහ ගණන වැඩියි. මෙම ගිණුම කෙටි කලකට අගුළු දමා ඇත.',
  'admin.error.accountLockedFor':
    'අසාර්ථක පිවිසුම් උත්සාහ ගණන වැඩියි. මිනිත්තු {minutes}කින් පමණ නැවත උත්සාහ කරන්න.',
  'admin.error.rateLimited': 'ඉල්ලීම් ගණන වැඩියි. මොහොතක් රැඳී නැවත උත්සාහ කරන්න.',
  'admin.error.serviceUnavailable': 'දැනට MageRide වෙත සම්බන්ධ විය නොහැක. ටික වේලාවකින් උත්සාහ කරන්න.',
  'admin.error.unexpected': 'අපගේ පැත්තෙන් යම් දෝෂයක් ඇති විය.',
  'admin.error.googleFailed':
    'Google පිවිසුම සම්පූර්ණ නොවීය. නැවත උත්සාහ කරන්න, නැතහොත් මුරපදය භාවිත කරන්න.',
  'admin.error.reference': 'යොමුව: {traceId}',

  /* ---- Refusals and dead ends ------------------------------------------ */
  'admin.denied.title': 'මෙම පිටුවට ඔබට ප්‍රවේශය නැත',
  'admin.denied.body':
    'මෙම මොඩියුලය ඔබේ භූමිකාවට ඇතුළත් නොවේ. ඔබේ රාජකාරියට එය අවශ්‍ය නම් ප්‍රධාන පරිපාලකයෙකුගෙන් ඉල්ලන්න.',
  'admin.denied.back': 'ඔබේ පළමු මොඩියුලයට යන්න',
  'admin.notFound.title': 'පිටුව හමු නොවීය',
  'admin.notFound.body': 'එම ලිපිනය කිසිදු පරිපාලන පෝට්ලයේ තිරයකට නොගැළපේ.',
  'admin.noModules.title': 'තවම ඔබට මොඩියුල පවරා නැත',
  'admin.noModules.body':
    'ඔබේ ගිණුම සාර්ථකව පිවිසුණි, නමුත් ඔබේ භූමිකා කිසිදු පරිපාලන පෝට්ල තිරයක් විවෘත නොකරයි. ඔබට අවශ්‍ය දේ ලබා දෙන ලෙස ප්‍රධාන පරිපාලකයෙකුගෙන් ඉල්ලන්න.',

  /* ---- The shell's placeholder for a screen a later component owns ------ */
  'admin.screen.pendingTitle': 'මෙම තිරය තවම නිර්මාණය කර නැත',
  'admin.screen.pendingBody':
    'පරිපාලන පෝට්ල රාමුව මෙම මාර්ගය හඳුනාගත් අතර ඔබේ භූමිකාව එයට අවසර දෙයි. තිරය පසුව එන ගොඩනැගීමේ අංගයක් සමඟ ලැබෙනු ඇත.',
  'admin.screen.servedBy': 'API සපයන්නේ {service}',

  /* ---- SCR-AP-002 · dashboard and its statistics filter (AL-38) -------- */
  'admin.dashboard.filter.legend': 'සංඛ්‍යාලේඛන කාලය',
  'admin.dashboard.filter.today': 'අද',
  'admin.dashboard.filter.week': 'මේ සතිය',
  'admin.dashboard.filter.month': 'මේ මාසය',
  'admin.dashboard.filter.custom': 'තෝරාගත් කාල පරාසය',
  'admin.dashboard.filter.from': 'සිට',
  'admin.dashboard.filter.to': 'දක්වා',
  'admin.dashboard.filter.apply': 'යොදන්න',
  'admin.dashboard.filter.comparison': 'පෙර කාලයට සාපේක්ෂව',
  'admin.dashboard.filter.export': 'CSV ලෙස බාගන්න',
  'admin.dashboard.filter.chooseRange': 'එම කාලයේ සංඛ්‍යා බැලීමට පරාසයේ දෙකෙළවරම තෝරන්න.',
  'admin.dashboard.filter.timezone': 'දින ශ්‍රී ලංකා වේලාවෙනි (Asia/Colombo).',

  'admin.dashboard.period.heading': 'තෝරාගත් කාලය සඳහා',
  'admin.dashboard.live.heading': 'දැන් මේ මොහොතේ',
  'admin.dashboard.live.note': 'සජීවී ගණන්. මේ තුන කාල පෙරහනට අනුව වෙනස් නොවේ.',

  'admin.dashboard.kpi.completedTrips': 'සම්පූර්ණ කළ ගමන්',
  'admin.dashboard.kpi.grossFare': 'දළ ගාස්තු',
  'admin.dashboard.kpi.newRidersDrivers': 'නව මගීන් / රියදුරන්',
  'admin.dashboard.kpi.newRiders': 'නව මගීන්',
  'admin.dashboard.kpi.newDrivers': 'නව රියදුරන්',
  'admin.dashboard.kpi.riders': 'මගීන්',
  'admin.dashboard.kpi.drivers': 'රියදුරන්',
  'admin.dashboard.kpi.dailyFeeRevenue': 'දෛනික ගාස්තු ආදායම',
  'admin.dashboard.kpi.onlineDrivers': 'මාර්ගගත රියදුරන්',
  'admin.dashboard.kpi.pendingVerifications': 'තහවුරු කිරීමට ඇති',
  'admin.dashboard.kpi.openTickets': 'විවෘත ටිකට්පත්',

  'admin.dashboard.delta.up': '{metric}: පෙර කාලයට වඩා {value} කින් ඉහළ',
  'admin.dashboard.delta.down': '{metric}: පෙර කාලයට වඩා {value} කින් පහළ',
  'admin.dashboard.delta.flat': '{metric}: පෙර කාලයට සමානයි',
  'admin.dashboard.delta.unknown': '{metric}: සැසඳීමක් නැත, පෙර කාලයේ කිසිවක් නොතිබුණි',

  'admin.dashboard.money': 'රු. {amount}',

  'admin.dashboard.alerts.heading': 'අවධානය අවශ්‍යයි',
  'admin.dashboard.alerts.clear': 'දැනට ඔබ වෙනුවෙන් රැඳී ඇති කිසිවක් නැත.',
  'admin.dashboard.alerts.verification': 'තහවුරු කිරීමට රැඳී ඇති ඉදිරිපත් කිරීම්',
  'admin.dashboard.alerts.tickets': 'තවම විවෘත සහාය ටිකට්පත්',
  'admin.dashboard.alerts.count': '{count} ක් රැඳී ඇත',

  /* ---- SCR-AP-003 · verification queues (AL-39, C106) ------------------ */
  'admin.verification.queue.navLabel': 'සත්‍යාපන පෝලිම්',
  'admin.verification.queue.drivingLicence': 'රියදුරු බලපත්‍ර පොරොත්තුවේ',
  'admin.verification.queue.vehicleRegistration': 'වාහන ලියාපදිංචිය පොරොත්තුවේ',
  'admin.verification.queue.fleetOrg': 'වාහන සමූහ අනුමැතිය',
  'admin.verification.queue.headingDrivingLicence': 'රියදුරු බලපත්‍ර සත්‍යාපන — පොරොත්තුවේ',
  'admin.verification.queue.headingVehicleRegistration': 'වාහන ලියාපදිංචි සත්‍යාපන — පොරොත්තුවේ',
  'admin.verification.queue.headingFleetOrg': 'වාහන සමූහ ආයතන — අනුමැතිය බලාපොරොත්තුවෙන්',
  'admin.verification.queue.caption': 'පොරොත්තුවේ ඇති සත්‍යාපන',
  'admin.verification.queue.flagsOnly': 'අතින් ඇතුළත් කළ / සැක සහිත ලකුණු පමණි',
  'admin.verification.queue.orgGate': 'ඕනෑම වාහන සමූහ ක්‍රියාවකට පෙර අනුමැති දොරටුව',
  'admin.verification.queue.search': 'සොයන්න',
  'admin.verification.queue.searchHint':
    'රියදුරු, වාහන හෝ ආයතන — නම, ලියාපදිංචි අංකය හෝ හැඳුනුම අනුව.',
  'admin.verification.queue.status': 'තත්ත්වය',
  'admin.verification.queue.statusAll': 'ඕනෑම තත්ත්වයක්',
  'admin.verification.queue.apply': 'යොදන්න',
  'admin.verification.queue.clear': 'ඉවත් කරන්න',
  'admin.verification.queue.review': 'සමාලෝචනය',
  'admin.verification.queue.empty': 'මෙම පෝලිමේ කිසිවක් රැඳී නැත.',
  'admin.verification.queue.total': 'පොරොත්තුවේ {count} ක්',
  'admin.verification.queue.totalMore': 'පොරොත්තුවේ {count}+ ක්',
  'admin.verification.queue.countMore': '{count}+',
  'admin.verification.queue.capped': 'පළමු {count} පෙන්වයි. ඉතිරිය සඳහා සෙවීම පටු කරන්න.',
  'admin.verification.status.pendingCount': 'පොරොත්තුවේ · {count}',

  'admin.verification.column.driver': 'රියදුරු',
  'admin.verification.column.vehicle': 'වාහනය',
  'admin.verification.column.organisation': 'ආයතනය',
  'admin.verification.column.submitted': 'ඉදිරිපත් කළේ',
  'admin.verification.column.flagged': 'සලකුණු කළ ක්ෂේත්‍ර',
  'admin.verification.column.vehicles': 'වාහන',
  'admin.verification.column.evidence': 'සාක්ෂි',
  'admin.verification.column.field': 'ක්ෂේත්‍රය',
  'admin.verification.column.value': 'අගය',
  'admin.verification.column.source': 'මූලාශ්‍රය',
  'admin.verification.column.status': 'තත්ත්වය',
  'admin.verification.column.action': 'ක්‍රියාව',

  'admin.verification.decided.approved': 'අනුමත කරන ලදී.',
  'admin.verification.decided.rejected': 'ප්‍රතික්ෂේප කරන ලදී.',

  /* ---- SCR-AP-003a ----------------------------------------------------- */
  'admin.verification.detail.back': 'පෝලිමට ආපසු',
  'admin.verification.detail.pendingFields': 'සමාලෝචනය පොරොත්තුවේ · සලකුණු කළ ක්ෂේත්‍ර {count} ක්',
  'admin.verification.detail.pendingReview': 'සමාලෝචනය පොරොත්තුවේ',
  'admin.verification.detail.readyToApprove': 'සියලු ක්ෂේත්‍ර තහවුරු කර ඇත',

  'admin.verification.doc.heading': 'අමුණන ලද ලේඛන',
  'admin.verification.doc.hint': 'සම්පූර්ණ ප්‍රමාණයෙන් විවෘත කිරීමට සිඟිති රූපයක් තට්ටු කරන්න',
  'admin.verification.doc.empty': 'මෙම ඉදිරිපත් කිරීමට ලේඛන අමුණා නැත.',
  'admin.verification.doc.note':
    'සෑම සිඟිති රූපයක්ම ගබඩා කළ ලියාපදිංචි ලේඛනයකි. එකක් විවෘත කිරීමද විගණන ලොගයට ලියැවේ.',
  'admin.verification.doc.position': '{index} / {total}',
  'admin.verification.doc.capturedDragCrop': 'මුල් උඩුගත කිරීම · යෙදුම තුළ ස්කෑනරයෙන් ගන්නා ලදී',
  'admin.verification.doc.capturedUpload': 'මුල් උඩුගත කිරීම · ගැලරියෙන් තෝරන ලදී',
  'admin.verification.doc.drivingLicense': 'රියදුරු බලපත්‍රය',
  'admin.verification.doc.registration': 'ලියාපදිංචිය',
  'admin.verification.doc.permit': 'මාර්ග බලපත්‍රය',
  'admin.verification.doc.insurance': 'රක්ෂණය',
  'admin.verification.doc.revenueLicense': 'ආදායම් බලපත්‍රය',
  'admin.verification.doc.vehiclePhoto': 'වාහන ඡායාරූපය',
  'admin.verification.doc.bankStatement': 'බැංකු ප්‍රකාශය',
  'admin.verification.doc.passbookFirstPage': 'බැංකු පොතේ පළමු පිටුව',
  'admin.verification.doc.proofOfAccount': 'ගිණුමේ සාක්ෂිය',
  'admin.verification.doc.lankaQr': 'LankaQR කේතය',

  'admin.verification.fields.heading': 'AI මගින් ලබාගත් ක්ෂේත්‍ර',
  'admin.verification.fields.engine': 'Gemini Flash 3.0 · පුද්ගලික දත්ත සඟවා ඇත',
  'admin.verification.fields.empty': 'මෙම ඉදිරිපත් කිරීමෙන් කිසිවක් ලබාගෙන නැත.',
  'admin.verification.fields.note':
    'රියදුරු විසින්ම ටයිප් කළ විට, ස්කෑනය සැක සහිත වූ විට, හෝ තහඩුව ලියාපදිංචි අංකයට නොගැළපෙන විට පේළිය පොරොත්තුවේ පවතී. එක් එක් ක්ෂේත්‍රය තහවුරු කළ යුතුය, නැතහොත් සංස්කරණය කර තහවුරු කළ යුතුය.',

  'admin.verification.field.licenceNo': 'බලපත්‍ර අංකය',
  'admin.verification.field.licenceExpiry': 'බලපත්‍රය කල් ඉකුත් වීම',
  'admin.verification.field.nicNo': 'ජා.හැ. අංකය',
  'admin.verification.field.allowedVehicleTypes': 'අවසර ලත් වාහන වර්ග',
  'admin.verification.field.insuranceExpiry': 'රක්ෂණය කල් ඉකුත් වීම',
  'admin.verification.field.revenueNo': 'ආදායම් බලපත්‍ර අංකය',
  'admin.verification.field.revenueExpiry': 'ආදායම් බලපත්‍රය කල් ඉකුත් වීම',
  'admin.verification.field.regNoMatch': 'ලියාපදිංචි අංකය සහ තහඩුව',
  'admin.verification.field.editConfirm': 'සංස්කරණය කර තහවුරු කරන්න',
  'admin.verification.field.confirmNamed': '{field} තහවුරු කරන්න',
  'admin.verification.field.editNamed': '{field} සංස්කරණය කරන්න',
  'admin.verification.field.correctedValue': 'නිවැරදි කළ අගය',
  'admin.verification.field.working': 'සටහන් කරමින්…',
  'admin.verification.field.valueRequired':
    'නිවැරදි කළ අගය ටයිප් කරන්න, නැතහොත් ලබාගත් අගය පිළිගැනීමට තහවුරු කරන්න ඔබන්න.',

  'admin.verification.source.ai': 'AI',
  'admin.verification.source.aiScored': 'AI {confidence}',
  'admin.verification.source.manual': 'අතින්',
  'admin.verification.fieldStatus.autoVerified': 'ස්වයංක්‍රීයව සත්‍යාපිතයි',
  'admin.verification.fieldStatus.confirmed': 'තහවුරු කර ඇත',
  'admin.verification.fieldStatus.pendingDoubtful': 'පොරොත්තුවේ · සැක සහිතයි',
  'admin.verification.fieldStatus.pendingMismatch': 'පොරොත්තුවේ · නොගැළපේ',

  'admin.verification.step.profile': 'පැතිකඩ / බලපත්‍රය',
  'admin.verification.step.details': 'වාහන විස්තර',
  'admin.verification.step.insurance': 'රක්ෂණය',
  'admin.verification.step.revenue': 'ආදායම් බලපත්‍රය',
  'admin.verification.step.photos': 'වාහන ඡායාරූප',
  'admin.verification.step.registration': 'ලියාපදිංචිය',
  'admin.verification.step.permit': 'මාර්ග බලපත්‍රය',
  'admin.verification.step.kyc': 'ආයතන KYC',
  'admin.verification.step.awaitingUpload': 'උඩුගත කර නැත',

  'admin.verification.decision.heading': 'තීරණය',
  'admin.verification.decision.steps': 'ලියාපදිංචි පියවර',
  'admin.verification.decision.reason': 'ප්‍රතික්ෂේප කිරීමේ හේතුව (ඇත්නම්)',
  'admin.verification.decision.reasonHint': 'ලියූ ආකාරයටම අයදුම්කරුට පෙන්වයි.',
  'admin.verification.decision.approveDriver': 'රියදුරු අනුමත කරන්න',
  'admin.verification.decision.approveVehicle': 'වාහනය අනුමත කරන්න',
  'admin.verification.decision.approveOrg': 'ආයතනය අනුමත කරන්න',
  'admin.verification.decision.reject': 'හේතුව සමඟ ප්‍රතික්ෂේප කරන්න',
  'admin.verification.decision.working': 'සටහන් කරමින්…',
  'admin.verification.approve.blocked':
    'පොරොත්තුවේ ඇති සෑම ක්ෂේත්‍රයක්ම තහවුරු කළ පසු අනුමැතිය විවෘත වේ.',
  'admin.verification.reject.reasonRequired':
    'හේතුවක් දෙන්න. ලියූ ආකාරයටම එය අයදුම්කරුට පෙන්වයි.',

  /* ---- SCR-AP-003b ----------------------------------------------------- */
  'admin.verification.viewer.title': '{document} · {position}',
  'admin.verification.viewer.previous': 'පෙර',
  'admin.verification.viewer.zoomIn': 'විශාල කරන්න',
  'admin.verification.viewer.zoomOut': 'කුඩා කරන්න',
  'admin.verification.viewer.rotate': 'කාල් හැරවුමක් කරකවන්න',
  'admin.verification.viewer.reset': 'විශාලනය සහ කරකැවීම නැවත සකසන්න',

  /* ---- SCR-AP-003c ----------------------------------------------------- */
  'admin.verification.org.vehicleCount': 'වාහන {count} ක්',
  'admin.verification.org.kycComplete': 'KYC සම්පූර්ණයි',
  'admin.verification.org.kycIncomplete': 'KYC අසම්පූර්ණයි',
  'admin.verification.org.heading': 'ආයතන KYC',
  'admin.verification.org.caption': 'ආයතන KYC විස්තර',
  'admin.verification.org.registeredName': 'ලියාපදිංචි නම',
  'admin.verification.org.registrationNo': 'ව්‍යාපාර ලියාපදිංචි අංකය',
  'admin.verification.org.contactPhone': 'බලයලත් සම්බන්ධතාවය',
  'admin.verification.org.contactEmail': 'සම්බන්ධතා ඊමේල්',
  'admin.verification.org.address': 'ලියාපදිංචි ලිපිනය',
  'admin.verification.org.rejectionReason': 'ප්‍රතික්ෂේප කිරීමේ හේතුව',
  'admin.verification.org.payoutHeading': 'බැංකු සහ ගෙවීම් විස්තර',
  'admin.verification.org.payoutCaption': 'බැංකු සහ ගෙවීම් විස්තර',
  'admin.verification.org.payoutNone': 'මෙම ආයතනය තවම බැංකු විස්තර ඉදිරිපත් කර නැත.',
  'admin.verification.org.bank': 'බැංකුව',
  'admin.verification.org.branch': 'ශාඛාව',
  'admin.verification.org.accountNo': 'ගිණුම් අංකය',
  'admin.verification.org.accountHolder': 'ගිණුම් හිමියාගේ නම',
  'admin.verification.org.payoutRejection': 'ගෙවීම් ප්‍රතික්ෂේප කිරීමේ හේතුව',
  'admin.verification.org.payoutGate':
    'ආයතනය අනුමත කිරීමෙන් මෙම විස්තර සත්‍යාපනය වේ. එතෙක් ගෙවුම් වාහනයක් හෝ ගෙවුම් දායකත්වයක් අයකළ නොහැක.',
  'admin.verification.org.documents': 'අමුණන ලද සාක්ෂි',
  'admin.verification.org.documentsEmpty': 'මෙම ආයතනය තවම ලේඛන අමුණා නැත.',
  'admin.verification.payout.pending': 'ගෙවීම් පොරොත්තුවේ',
  'admin.verification.payout.verified': 'ගෙවීම් සත්‍යාපිතයි',
  'admin.verification.payout.rejected': 'ගෙවීම් ප්‍රතික්ෂේපිතයි',
  'admin.verification.payout.superseded': 'ගෙවීම් අභිබවා ගොස් ඇත',

  /* ---- SCR-AP-004 · moderation ----------------------------------------- */
  'admin.moderation.queue.heading': 'වාහන පැමිණිලි — සමාලෝචනය පොරොත්තුවේ',
  'admin.moderation.queue.caption': 'තීරණයක් පොරොත්තුවෙන් සිටින වාහන පැමිණිලි',
  'admin.moderation.queue.rule': 'තහවුරු කළ පැමිණිලි {count}ක් වාහනය ඉවත් කරයි',
  'admin.moderation.queue.scope':
    'තවම කිසිවෙකු තීරණය නොකළ පැමිණිලි. තහවුරු කළ හෝ ඉවත ලූ පැමිණිල්ලක් මෙම පෝලිමෙන් ඉවත් වේ.',
  'admin.moderation.queue.total': 'පොරොත්තුවේ {count}ක්',
  'admin.moderation.queue.totalMore': 'පොරොත්තුවේ {count}+ ක්',
  'admin.moderation.queue.capped': 'පළමු {count} පෙන්වයි.',
  'admin.moderation.queue.empty': 'ඔබ වෙත පොරොත්තුවෙන් වාහන පැමිණිල්ලක් නැත.',

  'admin.moderation.column.subject': 'විෂය',
  'admin.moderation.column.reports': 'පැමිණිලි',
  'admin.moderation.column.reason': 'හේතුව',
  'admin.moderation.column.raised': 'ඉදිරිපත් කළේ',
  'admin.moderation.column.action': 'ක්‍රියාව',

  'admin.moderation.report.pendingCount': 'පොරොත්තුවේ {count}ක්',
  'admin.moderation.report.noReason': 'හේතුවක් දක්වා නැත',
  'admin.moderation.report.suspendVehicle': 'මෙම වාහනය අත්හිටුවන්න',
  'admin.moderation.report.confirm': 'පැමිණිල්ල තහවුරු කරන්න',
  'admin.moderation.report.dismiss': 'ඉවත ලන්න',
  'admin.moderation.report.working': 'සටහන් කරමින්…',
  'admin.moderation.report.confirmNamed': '{vehicle} වාහනයට එරෙහි පැමිණිල්ල තහවුරු කරන්න',
  'admin.moderation.report.dismissNamed': '{vehicle} වාහනයට එරෙහි පැමිණිල්ල ඉවත ලන්න',

  'admin.moderation.verdict.confirmed': 'පැමිණිල්ල තහවුරු කරන ලදී.',
  'admin.moderation.verdict.confirmedCount':
    'පැමිණිල්ල තහවුරු කරන ලදී. දැන් මෙම වාහනයට තහවුරු කළ පැමිණිලි {count}ක් ඇත; තවත් {remaining}ක් එය ඉවත් කරයි.',
  'admin.moderation.verdict.delisted':
    'පැමිණිල්ල තහවුරු කරන ලදී. එය තහවුරු කළ පැමිණිලි {count}ක් වන බැවින් වාහනය ඉවත් කර ඇත.',
  'admin.moderation.verdict.dismissed': 'පැමිණිල්ල ඉවත ලන ලදී.',

  'admin.moderation.suspend.heading': 'අත්හිටුවීම / තහනම',
  'admin.moderation.suspend.subject': 'අත්හිටුවන්නේ',
  'admin.moderation.suspend.driver': 'රියදුරු',
  'admin.moderation.suspend.vehicle': 'වාහනය',
  'admin.moderation.suspend.subjectId': 'රියදුරු / වාහන ID',
  'admin.moderation.suspend.subjectIdHint': 'වාර්තාවේ ඇති ආකාරයටම වේදිකා හැඳුනුම.',
  'admin.moderation.suspend.reason': 'හේතුව',
  'admin.moderation.suspend.reasonHint': 'අනිවාර්යයි; ඔබේ නම සමඟ සටහන් වේ.',
  'admin.moderation.suspend.apply': 'යොදන්න',
  'admin.moderation.suspend.working': 'සටහන් කරමින්…',
  'admin.moderation.suspend.idRequired': 'වාර්තාවේ ඇති ආකාරයටම හැඳුනුම ඇතුළත් කරන්න.',
  'admin.moderation.suspend.reasonRequired':
    'හේතුවක් දක්වන්න. එය විගණන ලොගයට ලියැවෙන අතර අභියාචනයකට පිළිතුරු දෙන්නේ එයිනි.',
  'admin.moderation.suspend.noDuration':
    'යමෙකු එය ඉවත් කරන තුරු අත්හිටුවීම බලපැවැත්වේ. තෝරා ගැනීමට කාලයක් නැති අතර ස්වයංක්‍රීයව එය යථා තත්ත්වයට පත් නොවේ.',
  'admin.moderation.suspend.doneDriver':
    'රියදුරු අත්හිටුවන ලදී. ඔහුගේ/ඇයගේ සැසිය අවසන් වී ඇති අතර නව ගමන් ලබා නොදේ; දැනටමත් ආරම්භ වී ඇති ගමනක් අවසන් වීමට ඉඩ දෙයි.',
  'admin.moderation.suspend.doneVehicle':
    'වාහනය අත්හිටුවන ලදී. එය ගමන් බෙදාහැරීමෙන් සහ සජීවී සිතියමෙන් ඉවත් වී ඇත.',

  /* ---- SCR-AP-005 · support & disputes ---------------------------------- */
  'admin.support.filter.status': 'තත්ත්වය',
  'admin.support.filter.statusAll': 'ඕනෑම තත්ත්වයක්',
  'admin.support.filter.category': 'ප්‍රවර්ගය',
  'admin.support.filter.categoryHint': 'ගබඩා කළ ප්‍රවර්ග යතුර, උදා: driver_qr_dispute.',
  'admin.support.filter.apply': 'යොදන්න',
  'admin.support.filter.clear': 'ඉවත් කරන්න',

  'admin.support.status.open': 'විවෘතයි',
  'admin.support.status.inProgress': 'කටයුතු කරමින්',
  'admin.support.status.resolved': 'විසඳා ඇත',

  'admin.support.category.dailyFeeRefund': 'දෛනික ගාස්තු ආපසු ගෙවීමේ ඉල්ලීම',
  'admin.support.category.driverQrDispute': 'රියදුරු QR ගෙවීම් ආරවුල',

  'admin.support.queue.heading': 'පෝලිම',
  'admin.support.queue.empty': 'මෙම පෙරහනට ගැළපෙන ටිකට් පතක් නැත.',
  'admin.support.queue.finance': 'මූල්‍ය',
  'admin.support.queue.total': 'මෙම පෝලිමේ {count}ක්',
  'admin.support.queue.totalMore': 'මෙම පෝලිමේ {count}+ ක්',
  'admin.support.queue.capped': 'පළමු {count} පෙන්වයි. ඉතිරිය සඳහා පෙරහන පටු කරන්න.',

  'admin.support.detail.raisedBy': 'ඉදිරිපත් කළේ',
  'admin.support.detail.noneHeading': 'ටිකට් පතක් විවෘත කර නැත',
  'admin.support.detail.noneBody': 'කියවීමට පෝලිමෙන් ටිකට් පතක් තෝරන්න.',
  'admin.support.detail.notInView':
    'ඔබ පෙරහන යොදා ඇති කොටසේ එම ටිකට් පත නැත. එය සොයා ගැනීමට පෙරහන ඉවත් කරන්න.',

  'admin.support.thread.heading': 'සංවාදය',
  'admin.support.thread.empty': 'මෙම ටිකට් පතේ පණිවිඩයක් නැත.',
  'admin.support.thread.raiser': 'ඉදිරිපත් කළ පුද්ගලයා',
  'admin.support.thread.agent': 'MageRide සහාය',

  'admin.support.lookup.heading': 'කියවීම පමණක් වන සෙවුම',
  'admin.support.lookup.passenger': 'මගී වාර්තාව විවෘත කරන්න',
  'admin.support.lookup.driver': 'රියදුරු වාර්තාව විවෘත කරන්න',
  'admin.support.lookup.note':
    'නාමාවලි වාර්තාවක් කියවීම පමණි, සහ එකක් විවෘත කිරීමද විගණන ලොගයට ලියැවේ.',
  'admin.support.lookup.none': 'නාමාවලි ඔබේ භූමිකාවට අයත් නොවේ.',

  'admin.support.refund.heading': 'ආපසු ගෙවීමේ ඉල්ලීම',
  'admin.support.refund.note':
    'සහාය අංශය මුදල් නොගෙනයයි. ආපසු ගෙවීමක් ඉල්ලා සිදු කරන්නේ මූල්‍ය අංශය, ආපසු ගෙවීම් පෝලිමේදීය — දෛනික ගාස්තු ආපසු ගෙවීමක් හෝ රියදුරු QR ආරවුලක් එහි ප්‍රවර්ගය නිසාම දැනටමත් එම පෝලිමේ ඇත.',
  'admin.support.refund.link': 'ආපසු ගෙවීම් පෝලිම විවෘත කරන්න',

  'admin.support.resolved.heading': 'විසඳා ඇත',
  'admin.support.resolved.note':
    'මෙම ටිකට් පත වසා ඇත. ඉදිරිපත් කළ පුද්ගලයාට ඉහත පිළිතුර තම යෙදුමෙන් කියවිය හැක.',

  'admin.support.resolve.response': 'ඔබේ පිළිතුර',
  'admin.support.resolve.responseHint':
    'ඔබ ලියන ආකාරයටම ටිකට් පත ඉදිරිපත් කළ පුද්ගලයාට පෙන්වයි.',
  'admin.support.resolve.submit': 'විසඳන්න',
  'admin.support.resolve.working': 'සටහන් කරමින්…',
  'admin.support.resolve.responseRequired':
    'මුලින්ම පිළිතුර ලියන්න — ටිකට් පත ඉදිරිපත් කළ පුද්ගලයාට පෙන්වන්නේ එයයි.',
  'admin.support.resolve.done': 'ටිකට් පත විසඳන ලදී.',

  /* ---- D-35 ------------------------------------------------------------ */
  'admin.audit.notice': 'මෙම ක්‍රියාව ඔබේ නම සමඟ විගණන ලොගයට ලියැවේ.',
  'admin.audit.recorded': '{action} ලෙස විගණන ලොගයේ සටහන් විය.',

  /* ---- The nine canonical roles (AL-06) -------------------------------- */
  'admin.role.admin': 'පරිපාලක',
  'admin.role.super_admin': 'ප්‍රධාන පරිපාලක',
  'admin.role.verification_officer': 'සත්‍යාපන නිලධාරී',
  'admin.role.support_csr': 'සහාය නිලධාරී',
  'admin.role.finance_officer': 'මූල්‍ය නිලධාරී',
  'admin.role.auditor': 'විගණක',
  'admin.role.driver': 'රියදුරු',
  'admin.role.passenger': 'මගියා',
  'admin.role.fleet_owner': 'වාහන සමූහ හිමිකරු',

  /* ---- Nav groups ------------------------------------------------------ */
  'nav.group.overview': 'දළ විශ්ලේෂණය',
  'nav.group.onboarding': 'ලියාපදිංචිය',
  'nav.group.directories': 'නාමාවලිය',
  'nav.group.moderation': 'මධ්‍යස්ථකරණය සහ සහාය',
  'nav.group.finance': 'මූල්‍ය',
  'nav.group.configuration': 'වින්‍යාසය',
  'nav.group.access': 'ප්‍රවේශය',

  /* ---- Nav items ------------------------------------------------------- */
  'nav.dashboard': 'උපකරණ පුවරුව',
  'nav.auditLog': 'විගණන ලොගය',
  'nav.verification': 'සත්‍යාපන පෝලිම්',
  'nav.documentExpiry': 'කල් ඉකුත් වන ලේඛන',
  'nav.passengers': 'මගීන්',
  'nav.drivers': 'රියදුරන්',
  'nav.vehicles': 'වාහන',
  'nav.reports': 'වාහන වාර්තා',
  'nav.supportTickets': 'සහාය ටිකට්පත්',
  'nav.fraudReview': 'වංචා සමාලෝචනය',
  'nav.reconciliation': 'ගිණුම් සැසඳීම',
  'nav.transactions': 'ගනුදෙනු',
  'nav.refunds': 'මුදල් ආපසු ගෙවීම්',
  'nav.walletAdjustments': 'පසුම්බි ගැලපුම්',
  'nav.pdpa': 'දත්ත අයිතිවාසිකම්',
  'nav.fareTariffs': 'ගාස්තු තීරු',
  'nav.cities': 'නගර',
  'nav.featureFlags': 'විශේෂාංග සලකුණු',
  'nav.trains': 'දුම්රිය',
  'nav.announcements': 'නිවේදන',
  'nav.gtfs': 'ප්‍රවාහන දත්ත (GTFS)',
  'nav.dailyFeeRates': 'දෛනික ගාස්තු අනුපාත',
  'nav.voucherTiers': 'වවුචර් ස්තර',
  'nav.driverLevels': 'රියදුරු මට්ටම්',
  'nav.rbac': 'පරිශීලකයින් සහ භූමිකා',
};

export default adminSi;
