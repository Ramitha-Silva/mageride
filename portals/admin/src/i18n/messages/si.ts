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
