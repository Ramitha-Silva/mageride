import type { FleetMessages } from './en';

/**
 * Sinhala resources for the Fleet Portal (D-26, D1' §283 — Sinhala is the
 * platform default).
 *
 * Typed as {@link FleetMessages}, so a key added to `en.ts` and forgotten here is
 * a compile error rather than an English string on a Sinhala operator's screen.
 * Product names — MageRide, Google, Apple, CSV — are left in Latin script, which
 * is how they are written in Sinhala technical copy.
 */
export const fleetSi: FleetMessages = {
  /* ---- Shell chrome ---------------------------------------------------- */
  'fleet.appName': 'MageRide Fleet',
  'fleet.tagline': 'ඔබේ වාහන සමූහය කළමනාකරණය කරන්න',
  'fleet.skipToContent': 'අන්තර්ගතයට යන්න',
  'fleet.nav.label': 'සමූහ මෙනුව',
  'fleet.nav.open': 'මෙනුව විවෘත කරන්න',
  'fleet.nav.close': 'මෙනුව වසන්න',
  'fleet.user.menu': 'ඔබේ ගිණුම',
  'fleet.user.signOut': 'ඉවත් වන්න',
  'fleet.user.role': 'ඔබේ භූමිකාව',
  'fleet.appearance.label': 'පෙනුම',
  'fleet.appearance.light': 'ලා',
  'fleet.appearance.dark': 'තද',
  'fleet.appearance.system': 'උපාංගයට අනුව',
  'fleet.language.label': 'භාෂාව',

  /* ---- The nav --------------------------------------------------------- */
  'fleet.nav.group.setup': 'සැකසුම',
  'fleet.nav.group.operate': 'මෙහෙයුම්',
  'fleet.nav.group.manage': 'කළමනාකරණය',
  'fleet.nav.group.subscribers': 'මගීන් (B ක්‍රමය)',
  'fleet.nav.organisation': 'ආයතනය',
  'fleet.nav.payout': 'බැංකු හා ගෙවීම්',
  'fleet.nav.team': 'කණ්ඩායම',
  'fleet.nav.dashboard': 'උපකරණ පුවරුව',
  'fleet.nav.vehicles': 'වාහන',
  'fleet.nav.drivers': 'රියදුරන්',
  'fleet.nav.trackers': 'ට්‍රැකර්',
  'fleet.nav.map': 'සජීවී සිතියම',
  'fleet.nav.scheduling': 'කාලසටහන්',
  'fleet.nav.analytics': 'විශ්ලේෂණ',
  'fleet.nav.billing': 'බිල්පත්',
  'fleet.nav.subscriptions': 'දායකත්ව',
  'fleet.nav.payments': 'ගෙවීම්',

  /* ---- Org-scoped sub-roles -------------------------------------------- */
  'fleet.role.owner': 'හිමිකරු',
  'fleet.role.manager': 'කළමනාකරු',
  'fleet.role.viewer': 'නරඹන්නා',

  /* ---- Organisation status --------------------------------------------- */
  'fleet.status.pending': 'සත්‍යාපනය අපේක්ෂිතයි',
  'fleet.status.approved': 'සත්‍යාපිතයි',
  'fleet.status.rejected': 'ප්‍රතික්ෂේපිතයි',

  /* ---- SCR-FP-001 · sign-in -------------------------------------------- */
  'fleet.signIn.heading': 'MageRide Fleet',
  'fleet.signIn.email': 'කාර්යාල විද්‍යුත් තැපෑල',
  'fleet.signIn.password': 'මුරපදය',
  'fleet.signIn.submit': 'පිවිසෙන්න',
  'fleet.signIn.submitting': 'පිවිසෙමින්…',
  'fleet.signIn.or': 'නැතහොත් මෙයින්',
  'fleet.signIn.google': 'Google',
  'fleet.signIn.apple': 'Apple',
  'fleet.signIn.emailRequired': 'ඔබේ කාර්යාල විද්‍යුත් තැපැල් ලිපිනය ඇතුළත් කරන්න',
  'fleet.signIn.passwordRequired': 'ඔබේ මුරපදය ඇතුළත් කරන්න',
  'fleet.signIn.signedOut': 'ඔබ ගිණුමෙන් ඉවත් කර ඇත.',
  'fleet.signIn.noSecondFactor':
    'OTP හෝ authenticator පියවරක් නැත — පිවිසීමෙන් පසු ඔබ කෙලින්ම ඔබේ වාහන සමූහයට යයි.',
  'fleet.signIn.forgot': 'මුරපදය අමතක වුණාද?',
  'fleet.signIn.forgotBody':
    'මෙම තිරයෙන් Fleet Portal මුරපදයක් තවම යළි සැකසිය නොහැක. ඔබේ ආයතනයේ හිමිකරුගෙන් ඉල්ලන්න, නැතහොත් MageRide සහායට කතා කරන්න; නව මුරපදයක් ඔබ වෙනුවෙන් සකසනු ලැබේ.',
  'fleet.signIn.newAccount': 'තවම ගිණුමක් නැද්ද?',
  'fleet.signIn.newAccountBody':
    'Fleet Portal ගිණුමක් ඔබ වෙනුවෙන් සාදනු ලැබේ — ඔබව ආරාධනා කරන ආයතනයක හිමිකරු විසින්, නැතහොත් නව මෙහෙයුම්කරුවෙකු බඳවා ගන්නා විට MageRide විසින්. ඔබට පිවිසිය හැකි වූ පසු, ආයතනය පළමු තිරයේදී සකසනු ලැබේ.',

  /* ---- Errors ---------------------------------------------------------- */
  'fleet.error.title': 'එය සාර්ථක නොවීය',
  'fleet.error.unauthorized': 'ඔබේ සැසිය අවසන් වී ඇත. නැවත පිවිසෙන්න.',
  'fleet.error.forbidden': 'ඔබේ භූමිකාව එයට අවසර නොදේ.',
  'fleet.error.notFound': 'එම වාර්තාව තවදුරටත් නොපවතී.',
  'fleet.error.validationFailed': 'ලකුණු කර ඇති ක්ෂේත්‍ර පරීක්ෂා කර නැවත උත්සාහ කරන්න.',
  'fleet.error.conflict': 'වෙනත් අයෙක් මෙය පළමුව වෙනස් කර ඇත. පිටුව නැවත පූරණය කර උත්සාහ කරන්න.',
  'fleet.error.accountBlocked': 'මෙම ගිණුම අවහිර කර ඇත. MageRide සහායට එය යළි සක්‍රිය කළ හැක.',
  'fleet.error.invalidCredentials': 'එම විද්‍යුත් තැපෑල සහ මුරපදය ගිණුමකට නොගැළපේ.',
  'fleet.error.accountLocked':
    'අසාර්ථක පිවිසුම් උත්සාහ ගණන වැඩියි. මෙම ගිණුම කෙටි කලකට අගුළු දමා ඇත.',
  'fleet.error.accountLockedFor':
    'අසාර්ථක පිවිසුම් උත්සාහ ගණන වැඩියි. මිනිත්තු {minutes}කින් පමණ නැවත උත්සාහ කරන්න.',
  'fleet.error.rateLimited': 'ඉල්ලීම් ගණන වැඩියි. මොහොතක් රැඳී නැවත උත්සාහ කරන්න.',
  'fleet.error.serviceUnavailable': 'දැනට MageRide වෙත සම්බන්ධ විය නොහැක. ටික වේලාවකින් උත්සාහ කරන්න.',
  'fleet.error.unexpected': 'අපගේ පැත්තෙන් යම් දෝෂයක් ඇති විය.',
  'fleet.error.providerFailed':
    '{provider} පිවිසුම සම්පූර්ණ නොවීය. නැවත උත්සාහ කරන්න, නැතහොත් මුරපදය භාවිත කරන්න.',
  'fleet.error.noFleetAccount':
    'මෙම ගිණුමට Fleet Portal වෙත පිවිසිය නොහැක. ඔබේ ආයතනයේ හිමිකරුගෙන් ඔබේ විද්‍යුත් තැපැල් ලිපිනය ආරාධනා කරන ලෙස ඉල්ලන්න, නැතහොත් MageRide සහායට කතා කරන්න.',
  'fleet.error.orgNotFound': 'එම ආයතනය තවදුරටත් නොපවතී.',
  'fleet.error.notMember': 'ඔබ එම ආයතනයේ සාමාජිකයෙක් නොවේ.',
  'fleet.error.roleInsufficient': 'මෙම ආයතනයේ ඔබේ භූමිකාව එයට අවසර නොදේ.',
  'fleet.error.orgNotApproved':
    'මෙම ආයතනය තවමත් සත්‍යාපනය වෙමින් පවතී, එබැවින් එය තවම ලබා ගත නොහැක.',
  'fleet.error.reference': 'යොමුව: {traceId}',

  /* ---- Refusals and dead ends ------------------------------------------ */
  'fleet.denied.title': 'මෙම පිටුවට ඔබට ප්‍රවේශය නැත',
  'fleet.denied.body':
    'මෙම තිරය මෙම ආයතනයේ ඔබේ භූමිකාවට ඇතුළත් නොවේ. ඔබට ළඟා විය හැකි දේ හිමිකරුට වෙනස් කළ හැක.',
  'fleet.denied.back': 'ඔබේ පළමු තිරයට යන්න',
  'fleet.notFound.title': 'පිටුව හමු නොවීය',
  'fleet.notFound.body': 'එම ලිපිනය කිසිදු Fleet Portal තිරයකට නොගැළපේ.',
  'fleet.noScreens.title': 'මෙම ගිණුම සඳහා තවම මෙහි කිසිවක් නැත',
  'fleet.noScreens.body':
    'ඔබ සාර්ථකව පිවිසුණි, නමුත් මෙම ගිණුමට කිසිදු සමූහ භූමිකාවක් නැත. ඔබේ ආයතනයේ හිමිකරුගෙන් ඔබේ විද්‍යුත් තැපැල් ලිපිනය නැවත ආරාධනා කරන ලෙස ඉල්ලන්න, නැතහොත් MageRide සහායට කතා කරන්න.',

  /* ---- No organisation yet --------------------------------------------- */
  'fleet.org.none.title': 'ඔබේ ආයතනය සකසන්න',
  'fleet.org.none.body':
    'මෙම ගිණුමට ආයතනයක් සෑදිය හැක, නමුත් තවම එකකට අයත් නොවේ. වාහන හා රියදුරන් ලියාපදිංචි කිරීම ඇරඹීමට එය ලියාපදිංචි කරන්න.',

  /* ---- US-13.A7 · the verification gate --------------------------------- */
  'fleet.pending.title': 'ඔබේ ආයතනය තවමත් සත්‍යාපනය වෙමින් පවතී',
  'fleet.pending.body':
    'MageRide සත්‍යාපන නිලධාරියෙක් ඔබේ ලියාපදිංචිය හා ලේඛන සමාලෝචනය කරමින් සිටී. අනුමත වූ වහාම වාහන ලියාපදිංචිය හා රියදුරු පැවරීම විවෘත වේ.',
  'fleet.pending.next':
    'රැඳී සිටින අතරතුර ඔබට ආයතන පැතිකඩ සම්පූර්ණ කිරීම, බැංකු හා ගෙවීම් තොරතුරු එක් කිරීම සහ ඔබේ කණ්ඩායම ආරාධනා කිරීම කළ හැක.',
  'fleet.pending.blocked': 'වාහන හා රියදුරු පැවරීම තවම ලබා ගත නොහැක.',
  'fleet.rejected.title': 'ඔබේ ආයතනය අනුමත නොවීය',
  'fleet.rejected.body':
    'සත්‍යාපන නිලධාරියෙකුට මෙම ලියාපදිංචිය අනුමත කළ නොහැකි විය. පහත සඳහන් දේ නිවැරදි කරන්න; MageRide සහාය සමාලෝචනය නැවත විවෘත කරයි.',
  'fleet.rejected.reason': 'දෙන ලද හේතුව: {reason}',
  'fleet.banner.pending':
    'මෙම ආයතනය සත්‍යාපනය අපේක්ෂාවෙන් සිටී. අනුමත වන තෙක් සැකසුම් තිර පමණක් ලබා ගත හැක.',
  'fleet.banner.rejected':
    'මෙම ආයතනය අනුමත නොවීය. සමාලෝචනය නැවත විවෘත කිරීමට MageRide සහායට කතා කරන්න.',
  'fleet.banner.viewer': 'ඔබ නරඹන්නෙකු ලෙස පිවිස ඇත, එබැවින් මෙම සැසිය කියවීමට පමණි.',

  /* ---- The shell's placeholder ------------------------------------------ */
  'fleet.screen.pendingTitle': 'මෙම තිරය තවම නිර්මාණය කර නැත',
  'fleet.screen.pendingBody':
    'Fleet Portal රාමුව මෙම මාර්ගය හඳුනාගත් අතර ඔබේ භූමිකාව එයට අවසර දෙයි. තිරය පසුව එන ගොඩනැගීමේ අංගයක් සමඟ ලැබෙනු ඇත.',
  'fleet.screen.servedBy': 'API සපයන්නේ {service}',
  'fleet.screen.wireframe': 'රැහැන් රාමුව {screen}',
};
