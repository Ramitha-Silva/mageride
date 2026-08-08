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

  /* ---- SCR-FP-001 · the sign-up half ----------------------------------- */
  'fleet.auth.tabs': 'Fleet Portal ප්‍රවේශය',
  'fleet.auth.tab.signIn': 'පිවිසෙන්න',
  'fleet.auth.tab.signUp': 'ගිණුමක් සාදන්න',
  'fleet.signUp.title': 'Fleet Portal ගිණුමක් සෑදෙන ආකාරය',
  'fleet.signUp.unavailable':
    'මෙම තිරයෙන් නව Fleet Portal ගිණුමක් විවෘත කළ නොහැක. එකක් ලබා ගැනීමට ක්‍රම දෙකක් ඇති අතර, දෙකම ආරම්භ වන්නේ වෙනත් තැනකිනි.',
  'fleet.signUp.byOwner':
    'ඔබේ ආයතනය දැනටමත් MageRide භාවිත කරයි නම් — ආයතන සැකසුම් තිරයෙන් ඔබේ කාර්යාල විද්‍යුත් තැපැල් ලිපිනය කණ්ඩායමට එක් කරන ලෙස එහි හිමිකරුගෙන් ඉල්ලන්න. එසේ කළ වහාම ඔබට පිවිසිය හැක.',
  'fleet.signUp.byMageRide':
    'ඔබේ ආයතනය MageRide සඳහා අලුත් නම් — ඔබ වෙනුවෙන් මෙහෙයුම්කරු ගිණුමක් විවෘත කිරීමට MageRide අමතන්න.',
  'fleet.signUp.thenOrg':
    'ඔබට පිවිසිය හැකි වූ පසු, ඔබට පෙන්වන පළමු තිරයේදී ආයතනය ලියාපදිංචි කරන්න.',
  'fleet.signUp.verification': 'විද්‍යුත් තැපෑල සත්‍යාපනය කරනවාද?',
  'fleet.signUp.verificationBody':
    'Fleet Portal ගිණුමක් සඳහා MageRide තවම සත්‍යාපන විද්‍යුත් තැපෑලක් නොයවයි, ස්වයං-සේවා මුරපද යළි සැකසීමක්ද නැත. ඔබේ ලිපිනය තහවුරු කරන්නේ ඔබව කණ්ඩායමට එක් කරන පුද්ගලයාය.',
  'fleet.signUp.identities': 'Google හෝ Apple භාවිත කරනවාද?',
  'fleet.signUp.identitiesBody':
    'ඔබේ ගිණුම පවතින වහාම, එම කාර්යාල විද්‍යුත් තැපැල් ලිපිනයම භාවිත කරන තාක්, Google සහ Apple ක්‍රියා කරයි. පසුව සැපයුම්කරුවෙකු සම්බන්ධ කිරීම හෝ ඉවත් කිරීම තවම ලබා ගත නොහැක.',

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
  'fleet.error.registrationExists':
    'එම ව්‍යාපාර ලියාපදිංචි අංකය දැනටමත් MageRide හි ලියාපදිංචි කර ඇත. එය ඔබට අයත් විය යුතු නම් MageRide සහායට කතා කරන්න.',
  'fleet.error.memberExists': 'එම විද්‍යුත් තැපැල් ලිපිනයට දැනටමත් මෙම ආයතනයේ ස්ථානයක් ඇත.',
  'fleet.error.payoutNotFound': 'මෙම ආයතනයට තවම බැංකු හා ගෙවීම් තොරතුරු නැත.',
  'fleet.error.payoutNotVerified':
    'ඒ සඳහා සත්‍යාපිත බැංකු හා ගෙවීම් පැතිකඩක් අවශ්‍යයි. ගිණුම් තොරතුරු හා ලේඛන එක් කරන්න; සත්‍යාපන නිලධාරියෙක් ඒවා අනුමත කරනු ඇත.',
  'fleet.error.fileTooLarge': 'එම ගොනුව මෙගාබයිට් {megabytes}ට වඩා විශාලයි. කුඩා පිටපතක් උඩුගත කරන්න.',
  'fleet.error.fileNotAccepted': 'එවැනි ගොනු වර්ගයක් මෙහි පිළිගනු නොලැබේ.',
  'fleet.error.vehicleRegistrationExists':
    'එම අංක තහඩුවෙන් වාහනයක් දැනටමත් MageRide හි ලියාපදිංචි කර ඇත. එය ඔබට විකුණා ඇත්නම්, MageRide සහාය අංශයට එය මාරු කළ හැක.',
  'fleet.error.invalidVehicleType': 'එය MageRide වාහන වර්ගයක් නොවේ. ලැයිස්තුවෙන් එකක් තෝරන්න.',
  'fleet.error.modeNotAllowed':
    'වාහන සමූහයක් කාලසටහන්ගත හා හවුල් පෞද්ගලික වාහන ධාවනය කරයි. දුම්රිය ලියාපදිංචි කරන්නේ MageRide මධ්‍යගතවය.',
  'fleet.error.vehicleNotFound': 'එම වාහනය ඔබේ සමූහයේ නැත.',
  'fleet.error.driverNotFound':
    'එම පරිශීලක ID එකට හෝ ජංගම අංකයට ගැළපෙන MageRide රියදුරු ගිණුමක් නැත. රියදුරු මුලින්ම Driver App හි ලියාපදිංචි විය යුතුය.',
  'fleet.error.imeiDuplicate':
    'එම IMEI අංකය දැනටමත් වාහනයකට බැඳී ඇත. පරිපාලකයෙක් විසඳන තෙක් උපාංග දෙකම රඳවා තබා ඇති අතර, ඒ දක්වා කිසිවක් තොරතුරු නොයවයි.',
  'fleet.error.csvInvalid': 'එම ගොනුව CSV එකක් ලෙස කියවිය නොහැකි විය. තීරු පරීක්ෂා කර නැවත උඩුගත කරන්න.',
  'fleet.error.tooManyRows': 'එම ගොනුවේ පේළි ඉතා වැඩියි. එය බෙදා කොටස් වශයෙන් උඩුගත කරන්න.',
  'fleet.error.bulkInProgress':
    'මෙම ආයතනය සඳහා ආයාත කිරීමක් දැනටමත් ක්‍රියාත්මක වේ. එය අවසන් වන තෙක් රැඳී නැවත උත්සාහ කරන්න.',
  'fleet.error.notOwner': 'එම උපාංගය වෙනත් ආයතනයකට අයත් වේ.',
  'fleet.error.attestationFailed':
    'මේ මොහොතේ MageRide මෙම ඉල්ලීම භාර ගන්නේ Android හා iOS යෙදුම් වලින් පමණි. කණ්ඩායම් ආයාතය සිදු කරන ලෙස MageRide සහාය අංශයෙන් ඉල්ලන්න.',
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

  /* ---- SCR-FP-002 · organisation setup --------------------------------- */
  'fleet.org.profile.heading': 'ආයතන පැතිකඩ හා KYC',
  'fleet.org.field.name': 'ආයතනයේ නම',
  'fleet.org.field.registrationNo': 'ව්‍යාපාර ලියාපදිංචි අංකය',
  'fleet.org.field.contactPhone': 'සම්බන්ධතා ජංගම දුරකථනය',
  'fleet.org.field.contactEmail': 'සම්බන්ධතා විද්‍යුත් තැපෑල',
  'fleet.org.field.address': 'ලිපිනය',
  'fleet.org.field.registered': 'MageRide හි ලියාපදිංචි වූයේ',
  'fleet.org.field.language': 'භාෂාව',
  'fleet.org.hint.registrationNo': 'ව්‍යාපාර ලියාපදිංචි සහතිකයේ මුද්‍රණය කර ඇති ආකාරයටම.',
  'fleet.org.hint.contactPhone': 'ශ්‍රී ලාංකික ජංගම දුරකථන අංකයක්, උදාහරණයක් ලෙස 0771234567.',
  'fleet.org.optional': 'අත්‍යවශ්‍ය නොවේ',
  'fleet.org.required': 'අවශ්‍යයි',
  'fleet.org.language.note':
    'මෙම බ්‍රව්සරයේ මෙම කොන්සෝලයේ භාෂාව සකසයි. ආයතනය සඳහාම භාෂාවක් MageRide ගබඩා නොකරයි.',
  'fleet.org.readOnly':
    'මෙම තොරතුරු සත්‍යාපන නිලධාරියෙක් කියවන වාර්තාව වන අතර, ඒවා තවම මෙම ද්වාරයෙන් සංස්කරණය කළ නොහැක. මෙහි යමක් නිවැරදි කිරීමට MageRide සහායට කතා කරන්න.',
  'fleet.org.kyc.heading': 'KYC හා සත්‍යාපනය',
  'fleet.org.kyc.gate':
    'වාහන ලියාපදිංචිය හා රියදුරු පැවරීම විවෘත වීමට පෙර MageRide සත්‍යාපන නිලධාරියෙක් ලියාපදිංචිය පරීක්ෂා කරයි. එතෙක් කියවීමේ ක්‍රියා පමණක් ලබා ගත හැක.',
  'fleet.org.kyc.unavailable':
    'ව්‍යාපාර ලියාපදිංචි සහතිකය සහ හිමිකරුගේ හැඳුනුම් ලේඛනය MageRide සහාය විසින් එකතු කරයි; ඒවා උඩුගත කිරීමට තවම ද්වාරයේ ස්ථානයක් නැත. පහත බැංකු ලේඛන බැංකු හා ගෙවීම් පැතිකඩට අමුණා ඇත.',
  'fleet.org.payout.link': 'බැංකු හා ගෙවීම් තොරතුරු',
  'fleet.org.payout.linkBody':
    'B ක්‍රමයේ දායකත්ව ගෙවීම් ලැබෙන ගිණුම සහ ඔබේ මගීන් ස්කෑන් කරන බැංකු යෙදුමේ QR කේතය. හිමිකරුට පමණි.',
  'fleet.org.register.heading': 'ඔබේ ආයතනය ලියාපදිංචි කරන්න',
  'fleet.org.register.body':
    'මෙම ගිණුමට ආයතනයක් සෑදිය හැක, නමුත් තවම එකකට අයත් නොවේ. වාහන හා රියදුරන් ලියාපදිංචි කිරීම ඇරඹීමට එය ලියාපදිංචි කරන්න.',
  'fleet.org.register.gate':
    'ආයතනය සත්‍යාපනය අපේක්ෂාවෙන් සෑදේ. MageRide සත්‍යාපන නිලධාරියෙක් එය සමාලෝචනය කරන අතර, ඔවුන් අනුමත කරන තෙක් කියවීමේ ක්‍රියා පමණක් ලබා ගත හැක.',
  'fleet.org.register.submit': 'ආයතනය ලියාපදිංචි කරන්න',
  'fleet.org.register.submitting': 'ලියාපදිංචි කරමින්…',
  'fleet.org.error.nameRequired': 'ආයතනයේ නම ඇතුළත් කරන්න',
  'fleet.org.error.registrationRequired': 'ව්‍යාපාර ලියාපදිංචි අංකය ඇතුළත් කරන්න',
  'fleet.org.error.phoneInvalid': 'ශ්‍රී ලාංකික ජංගම දුරකථන අංකයක් ඇතුළත් කරන්න, උදාහරණයක් ලෙස 0771234567',
  'fleet.org.error.emailInvalid': 'වලංගු විද්‍යුත් තැපැල් ලිපිනයක් ඇතුළත් කරන්න',

  /* ---- SCR-FP-002 · the team -------------------------------------------- */
  'fleet.team.heading': 'කණ්ඩායම් සාමාජිකයෝ',
  'fleet.team.caption': 'මෙම ආයතනය වෙනුවෙන් පිවිසිය හැකි අය සහ ඔවුන්ගේ භූමිකා',
  'fleet.team.column.member': 'සාමාජිකයා',
  'fleet.team.column.role': 'භූමිකාව',
  'fleet.team.you': '(ඔබ)',
  'fleet.team.empty': 'තවම කණ්ඩායම් සාමාජිකයන් නැත.',
  'fleet.team.backToOrg': 'ආයතන සැකසුම් වෙත ආපසු',
  'fleet.team.invite.heading': 'කණ්ඩායම් සාමාජිකයෙකු ආරාධනා කරන්න',
  'fleet.team.invite.email': 'කාර්යාල විද්‍යුත් තැපෑල',
  'fleet.team.invite.name': 'නම',
  'fleet.team.invite.role': 'භූමිකාව',
  'fleet.team.invite.submit': 'සාමාජිකයා ආරාධනා කරන්න',
  'fleet.team.invite.submitting': 'ආරාධනා කරමින්…',
  'fleet.team.invite.done': 'එම ලිපිනයට දැන් මෙම ආයතනයේ ස්ථානයක් ඇත.',
  'fleet.team.invite.noOwnerSeat':
    'දෙවන හිමිකරුවෙකු මෙහිදී එක් කළ නොහැක — ආයතනය අයත් වන්නේ එය ලියාපදිංචි කළ පුද්ගලයාටය.',
  'fleet.team.invite.noEmail':
    'MageRide තවම ආරාධනා විද්‍යුත් තැපෑලක් නොයවයි. ඔවුන්ගේ ලිපිනය එක් කර ඇති බව ඔබේ සගයාට කියන්න — ඉන්පසු ඔවුන්ට එය භාවිතයෙන් Google හෝ Apple සමඟ පිවිසිය හැක, නැතහොත් මුරපදයක් සැකසීමට MageRide සහායෙන් ඉල්ලිය හැක.',
  'fleet.team.invite.ownerOnlyNotice':
    'කණ්ඩායම් සාමාජිකයන් එක් කිරීමට හෝ වෙනස් කිරීමට හැක්කේ ආයතනයේ හිමිකරුට පමණි.',
  'fleet.team.error.ownerOnly': 'කණ්ඩායම් සාමාජිකයෙකු එක් කළ හැක්කේ ආයතනයේ හිමිකරුට පමණි.',
  'fleet.team.error.roleRequired': 'කළමනාකරු හෝ නරඹන්නා තෝරන්න',

  /* ---- SCR-FP-002a · bank & payout details ------------------------------ */
  'fleet.payout.title': 'බැංකු හා ගෙවීම් තොරතුරු',
  'fleet.payout.heading': 'බැංකු ගිණුම — B ක්‍රමයේ දායකත්ව ගෙවීම් ලැබේ',
  'fleet.payout.field.bank': 'බැංකුව',
  'fleet.payout.field.bankPlaceholder': 'ඔබේ බැංකුව තෝරන්න',
  'fleet.payout.field.branch': 'ශාඛාව',
  'fleet.payout.field.accountNo': 'ගිණුම් අංකය',
  'fleet.payout.field.holder': 'ගිණුම් හිමියාගේ නම',
  'fleet.payout.holderHint':
    'ආයතනයේ හෝ හිමිකරුගේ KYC නමට ගැළපිය යුතුය. සත්‍යාපන නිලධාරියෙක් එම දෙක සසඳයි.',
  'fleet.payout.editWarning':
    'මෙම තොරතුරු සුරැකීමෙන් ඒවා සත්‍යාපනය සඳහා යවනු ලැබේ. ඒවා දකුණේ ඇති ලේඛන සමඟ පරීක්ෂා කෙරේ.',
  'fleet.payout.editVerifiedWarning':
    'මෙම පැතිකඩ සත්‍යාපිතයි. වෙනසක් සුරැකීමෙන් නව තොරතුරු සත්‍යාපනය සඳහා යවනු ලබන අතර, නිලධාරියෙක් එම වෙනස අනුමත කරන තෙක් ඔබේ B ක්‍රමයේ දායකයින් අනුමත ගිණුමටම ගෙවති.',
  'fleet.payout.submit': 'බැංකු තොරතුරු සුරකින්න',
  'fleet.payout.submitting': 'සුරකිමින්…',
  'fleet.payout.saved': 'සුරකින ලදී. තොරතුරු දැන් සත්‍යාපනය අපේක්ෂාවෙන් සිටී.',
  'fleet.payout.backToOrg': 'ආයතන සැකසුම් වෙත ආපසු',
  'fleet.payout.status.none': 'ඉදිරිපත් කර නැත',
  'fleet.payout.status.pending': 'සත්‍යාපනය අපේක්ෂිතයි',
  'fleet.payout.status.verified': 'සත්‍යාපිතයි',
  'fleet.payout.status.rejected': 'ප්‍රතික්ෂේපිතයි',
  'fleet.payout.status.superseded': 'නව අනුවාදයකින් ප්‍රතිස්ථාපනය විය',
  'fleet.payout.rejectedReason': 'දෙන ලද හේතුව: {reason}',
  'fleet.payout.verifiedOn': '{date} දින සත්‍යාපනය විය.',
  'fleet.payout.gate.heading': 'මෙම පැතිකඩ මත රඳා පවතින දේ',
  'fleet.payout.gate.paid':
    'මෙම පැතිකඩ සත්‍යාපනය වන තෙක් B ක්‍රමයේ වාහනයක සේවා ගෙවීම "ගෙවුම්" ලෙස සැකසිය නොහැක.',
  'fleet.payout.gate.paidReady':
    'B ක්‍රමයේ වාහන මාසික ගාස්තුවක් සමඟ සේවා ගෙවීම "ගෙවුම්" ලෙස සැකසිය හැක.',
  'fleet.payout.gate.billing':
    'නිලධාරියෙක් මෙම තොරතුරු අනුමත කරන තෙක් ගෙවුම් දායකත්ව බිල් කිරීම ආරම්භ නොවන අතර, මගී ගෙවීම් පත්‍රයේ ගෙවීමට කිසිවක් නොපෙනේ.',
  'fleet.payout.gate.paySheetReady':
    'B ක්‍රමයේ දායකයින්ට මාරු කිරීමක් සඳහා මෙම ගිණුම් තොරතුරු සහ LankaQR ගෙවීමක් සඳහා පහත QR රූපය පෙනේ.',
  'fleet.payout.proof.heading': 'ගිණුමේ සාක්ෂිය',
  'fleet.payout.proof.which': 'මෙය කුමන ලේඛනයද?',
  'fleet.payout.proof.prompt': 'බැංකු ප්‍රකාශනය හෝ බැංකු පොතේ පිටුව උඩුගත කරන්න',
  'fleet.payout.proof.hint': 'PDF හෝ ඡායාරූපයක්, මෙගාබයිට් 8 දක්වා.',
  'fleet.payout.qr.heading': 'බැංකු යෙදුමේ LankaQR කේතය',
  'fleet.payout.qr.prompt': 'ඔබේ බැංකු යෙදුමෙන් LankaQR කේත රූපය උඩුගත කරන්න',
  'fleet.payout.qr.hint': 'ඡායාරූපයක් හෝ තිර රුවක්, මෙගාබයිට් 8 දක්වා.',
  'fleet.payout.qr.note':
    'මගී ගෙවීම් පත්‍රයේ B ක්‍රමයේ දායකයින්ට ස්කෑන් කිරීමට හෝ බැංකු යෙදුමේ විවෘත කිරීමට පෙන්වයි. මාරු කිරීමෙන් ගෙවන මගීන්ට ඒ වෙනුවට සත්‍යාපිත ගිණුම් තොරතුරු පෙනේ.',
  'fleet.payout.kind.bankStatement': 'නවතම බැංකු ප්‍රකාශනය',
  'fleet.payout.kind.passbook': 'බැංකු පොතේ පළමු පිටුව',
  'fleet.payout.kind.lankaqr': 'බැංකු යෙදුමේ LankaQR කේතය',
  'fleet.payout.doc.uploading': 'උඩුගත කරමින්…',
  'fleet.payout.doc.uploaded': 'උඩුගත කර ඇත',
  'fleet.payout.doc.missing': 'උඩුගත කර නැත',
  'fleet.payout.error.bankRequired': 'බැංකුව තෝරන්න',
  'fleet.payout.error.branchRequired': 'ශාඛාව ඇතුළත් කරන්න',
  'fleet.payout.error.accountRequired': 'ගිණුම් අංකය ඇතුළත් කරන්න',
  'fleet.payout.error.holderRequired': 'ගිණුම් හිමියාගේ නම ඇතුළත් කරන්න',
  'fleet.payout.error.kindRequired': 'මෙය කුමන ලේඛනයදැයි තෝරන්න',
  'fleet.payout.error.fileRequired': 'උඩුගත කිරීමට ගොනුවක් තෝරන්න',
  'fleet.payout.error.profileFirst':
    'පළමුව බැංකු තොරතුරු සුරකින්න — ලේඛනයක් අමුණන්නේ ගෙවීම් පැතිකඩකට මිස ආයතනයකට නොවේ.',

  /* ---- SCR-FP-004 · vehicle onboarding ---------------------------------- */
  'fleet.vehicles.title': 'වාහන ලියාපදිංචිය',
  'fleet.vehicles.modesOnly': 'A / B ක්‍රම පමණි',
  'fleet.vehicles.modesOnlyNote':
    'වාහන සමූහයක් කාලසටහන්ගත මහජන ප්‍රවාහන (A ක්‍රමය) හා හවුල් පෞද්ගලික වාහන (B ක්‍රමය) ධාවනය කරයි. ඉල්ලුම මත කුලී රථයක් යනු රියදුරාගේම වාහනයක් වන අතර එය Driver App හි ලියාපදිංචි කෙරේ.',
  'fleet.vehicles.tabs': 'වාහන එකතු කරන ආකාරය',
  'fleet.vehicles.tab.single': 'තනි වාහනයක්',
  'fleet.vehicles.tab.bulk': 'තොග CSV',
  'fleet.vehicles.viewerNotice':
    'ඔබ නරඹන්නෙකු ලෙස පිවිස ඇති බැවින්, මෙම තිරය වාහන ලැයිස්තුව පෙන්වන අතර කිසිවක් එකතු නොකරයි.',

  'fleet.vehicles.add.heading': 'වාහනයක් එකතු කරන්න',
  'fleet.vehicles.field.plate': 'ලියාපදිංචි අංකය',
  'fleet.vehicles.field.plateHint': 'අංක තහඩුවේ ඇති ආකාරයටම — උදාහරණයක් ලෙස NB-4521.',
  'fleet.vehicles.field.type': 'වාහන වර්ගය',
  'fleet.vehicles.field.mode': 'ක්‍රමය',
  'fleet.vehicles.mode.a': 'A ක්‍රමය — කාලසටහන්ගත මහජන ප්‍රවාහන',
  'fleet.vehicles.mode.b': 'B ක්‍රමය — හවුල් පෞද්ගලික වාහන',
  'fleet.vehicles.type.bus': 'බස්',
  'fleet.vehicles.type.van': 'වෑන්',
  'fleet.vehicles.type.mini_van': 'මිනි වෑන්',
  'fleet.vehicles.type.flex': 'ෆ්ලෙක්ස්',
  'fleet.vehicles.type.sedan': 'සෙඩාන්',
  'fleet.vehicles.type.three_wheeler': 'ත්‍රීරෝද රථ',
  'fleet.vehicles.type.motorbike': 'යතුරුපැදි',
  'fleet.vehicles.type.truck': 'ට්‍රක්',
  'fleet.vehicles.type.mini_truck': 'මිනි ට්‍රක්',
  'fleet.vehicles.type.noTrain': 'දුම්රිය මධ්‍යගතව MageRide විසින් ලියාපදිංචි කරන අතර මෙහිදී එකතු නොකෙරේ.',
  'fleet.vehicles.add.submit': 'වාහනය එකතු කරන්න',
  'fleet.vehicles.add.submitting': 'එකතු කරමින්…',
  'fleet.vehicles.add.added': '{plate} ලැයිස්තුවට එක් වී සමාලෝචනයේ පවතී. එහි ලේඛන පහතින් උඩුගත කරන්න.',

  'fleet.vehicles.field.servicePayment': 'සේවා ගෙවීම',
  'fleet.vehicles.field.servicePaymentHint':
    'B ක්‍රමයට පමණි. කාර්යාල හෝ කාර්ය මණ්ඩල ප්‍රවාහනයක් මගීන්ගෙන් කිසිවක් අය නොකරන බැවින් "නොමිලේ" වේ; අනෙක් ඒවා පෙරනිමි මාසික ගාස්තුවක් සමඟ "ගෙවුම්" වේ.',
  'fleet.vehicles.servicePayment.free': 'නොමිලේ',
  'fleet.vehicles.servicePayment.paid': 'ගෙවුම්',
  'fleet.vehicles.servicePayment.freeOffice': 'නොමිලේ (කාර්යාල)',
  'fleet.vehicles.servicePayment.notSet': 'සකසා නැත',
  'fleet.vehicles.servicePayment.notApplicable': '—',
  'fleet.vehicles.servicePayment.paidWithFare': 'ගෙවුම් · මසකට රු. {fare}',
  'fleet.vehicles.field.fare': 'පෙරනිමි මාසික ගාස්තුව (ගෙවුම්)',
  'fleet.vehicles.field.fareHint':
    'දායකයෙකුට මසකට රුපියල්. දායකත්ව තිරයේදී එක් එක් දායකයා සඳහා වෙනස් කළ හැක.',
  'fleet.vehicles.servicePayment.heading': 'සේවා ගෙවීම',
  'fleet.vehicles.servicePayment.save': 'සේවා ගෙවීම සුරකින්න',
  'fleet.vehicles.servicePayment.saving': 'සුරකිමින්…',
  'fleet.vehicles.servicePayment.saved': 'සුරකින ලදී.',
  'fleet.vehicles.servicePayment.modeANote':
    'සේවා ගෙවීම අදාළ වන්නේ B ක්‍රමයේ වාහන සඳහාය. A ක්‍රමයේ වාහනයකට දායකත්ව ගාස්තුවක් නැත.',

  'fleet.vehicles.docs.heading': 'වාහන ලේඛන',
  'fleet.vehicles.docs.forVehicle': 'වාහන ලේඛන · {plate}',
  'fleet.vehicles.docs.chooseVehicle':
    'ලේඛනයක් අමුණන්නේ වාහනයකටය. ඉහතින් වාහනය එකතු කරන්න, නැතහොත් ලැයිස්තුවෙන් එකක් තෝරන්න — එවිට එහි කොටු හතර මෙහි විවෘත වේ.',
  'fleet.vehicles.docs.extraction':
    'සෑම ලේඛනයක්ම AI මගින් කියවේ — ලියාපදිංචියට එරෙහිව අංක තහඩුව, රක්ෂණ හා ආදායම් බලපත්‍ර කල් ඉකුත්වීම, බලපත්‍ර අංකය හා මාර්ගය — සහ එයටම "සත්‍යාපිත / අපේක්ෂිත / නොමැති" ලේබලයක් ලැබේ.',
  'fleet.vehicles.docs.approvalGate':
    'අවශ්‍ය ලේඛනයක් නොමැති හෝ අපේක්ෂිත තත්ත්වයේ තිබියදී වාහනයකට "අනුමතයි" තත්ත්වයට පැමිණිය නොහැක.',
  'fleet.vehicles.docs.blocked': 'රැඳී සිටින්නේ: {slots}.',
  'fleet.vehicles.docs.ready':
    'අවශ්‍ය සියලු ලේඛන සත්‍යාපිතයි. තීරණය ගන්නේ සත්‍යාපන නිලධාරියෙකි.',
  'fleet.vehicles.docs.backToRoster': 'මුළු ලැයිස්තුව පෙන්වන්න',
  'fleet.vehicles.doc.registration': 'ලියාපදිංචි පිටපත (CR පොත)',
  'fleet.vehicles.doc.registrationHint': 'අංක තහඩුව CR පොතට එරෙහිව ගැළපේ.',
  'fleet.vehicles.doc.insurance': 'රක්ෂණ සහතිකය',
  'fleet.vehicles.doc.insuranceHint': 'කල් ඉකුත්වන දිනය සහතිකයෙන් කියවේ.',
  'fleet.vehicles.doc.revenueLicense': 'ආදායම් බලපත්‍රය',
  'fleet.vehicles.doc.revenueLicenseHint': 'බලපත්‍ර අංකය හා කල් ඉකුත්වන දිනය එයින් කියවේ.',
  'fleet.vehicles.doc.routePermit': 'මාර්ග බලපත්‍රය',
  'fleet.vehicles.doc.routePermitHint': 'බලපත්‍ර අංකය හා මාර්ගය එයින් කියවේ.',
  'fleet.vehicles.doc.upload': 'ගොනුව මෙහි දමන්න හෝ එකක් තෝරන්න',
  'fleet.vehicles.doc.accept': 'PDF හෝ ඡායාරූපයක්, මෙගාබයිට් {megabytes} දක්වා.',
  'fleet.vehicles.slot.verified': 'සත්‍යාපිතයි',
  'fleet.vehicles.slot.pending': 'අපේක්ෂිතයි',
  'fleet.vehicles.slot.missing': 'නොමැත',
  'fleet.vehicles.slot.required': 'අවශ්‍යයි',
  'fleet.vehicles.slot.optional': 'මෙම වාහනයට වෛකල්පිකයි',
  'fleet.vehicles.slot.permitModeA': 'A ක්‍රමයට අවශ්‍යයි.',
  'fleet.vehicles.slot.expires': '{date} දින කල් ඉකුත් වේ',
  'fleet.vehicles.slot.uploading': 'උඩුගත කර කියවමින්…',
  'fleet.vehicles.slot.replace': 'නව ගොනුවක් උඩුගත කිරීමෙන් මෙය ප්‍රතිස්ථාපනය වේ.',
  'fleet.vehicles.slot.extracted': 'කියවාගත් තොරතුරු',
  'fleet.vehicles.slot.fieldPending': 'නිලධාරියෙකු බලාපොරොත්තුවෙන්',
  'fleet.vehicles.slot.fieldUnread': 'කියවා නැත',
  'fleet.vehicles.field.expiry': 'කල් ඉකුත්වන දිනය',
  'fleet.vehicles.field.expiryHint': 'වෛකල්පිකයි. කියවීමෙන් දිනයක් නොලැබුණහොත් පමණක් භාවිත වේ.',
  'fleet.vehicles.extract.reg_no_match': 'අංක තහඩුව CR පොතට ගැළපේ',
  'fleet.vehicles.extract.plate_text': 'කියවාගත් අංක තහඩුව',
  'fleet.vehicles.extract.insurance_expiry': 'රක්ෂණය කල් ඉකුත්වීම',
  'fleet.vehicles.extract.revenue_no': 'ආදායම් බලපත්‍ර අංකය',
  'fleet.vehicles.extract.revenue_expiry': 'ආදායම් බලපත්‍රය කල් ඉකුත්වීම',
  'fleet.vehicles.extract.permit_no': 'බලපත්‍ර අංකය',
  'fleet.vehicles.extract.permit_route': 'මාර්ගය',
  'fleet.vehicles.extract.permit_expiry': 'බලපත්‍රය කල් ඉකුත්වීම',

  'fleet.vehicles.bulk.heading': 'තොග CSV',
  'fleet.vehicles.bulk.prompt': 'CSV මෙහි දමන්න හෝ ගොනුවක් තෝරන්න',
  'fleet.vehicles.bulk.hint': 'පේළි {rows} දක්වා, මෙගාබයිට් {megabytes}.',
  'fleet.vehicles.bulk.columns': 'තීරු: {columns}. ශීර්ෂ පේළියක් වෛකල්පිකයි.',
  'fleet.vehicles.bulk.docsPending':
    'ආයාත කරන සෑම පේළියක්ම ලේඛන අපේක්ෂිත තත්ත්වයෙන් නිර්මාණය වේ — CSV එකක ගොනු නොයන බැවින්, කොටු හතර පසුව එක් එක් වාහනයට පිරවිය යුතුය.',
  'fleet.vehicles.bulk.uploading': 'උඩුගත කරමින්…',
  'fleet.vehicles.bulk.processing': 'පේළි {total} ආයාත කරමින්…',
  'fleet.vehicles.bulk.imported': 'පේළි {total} න් {imported} ක් ආයාත විය.',
  'fleet.vehicles.bulk.someFailed': 'පේළි {failed} ක් ආයාත නොවීය.',
  'fleet.vehicles.bulk.allImported': 'සියලු පේළි ආයාත විය.',
  'fleet.vehicles.bulk.report': 'දෝෂ වාර්තාව බාගන්න',
  'fleet.vehicles.bulk.refresh': 'නැවත බලන්න',
  'fleet.vehicles.bulk.jobFailed': 'එම ආයාතය සැකසිය නොහැකි විය. ගොනුව පරීක්ෂා කර නැවත උඩුගත කරන්න.',

  'fleet.vehicles.table.heading': 'ලියාපදිංචි තත්ත්වය',
  'fleet.vehicles.table.caption': 'මෙම ආයතනයේ සෑම වාහනයක්ම, එහි ලේඛන හා අනුමත තත්ත්වය',
  'fleet.vehicles.column.plate': 'ලියාපදිංචි අංකය',
  'fleet.vehicles.column.type': 'වර්ගය',
  'fleet.vehicles.column.servicePayment': 'සේවා ගෙවීම',
  'fleet.vehicles.column.documents': 'ලේඛන',
  'fleet.vehicles.column.status': 'තත්ත්වය',
  'fleet.vehicles.table.empty': 'තවම වාහන නැත. ඉහතින් එකක් එකතු කරන්න, නැතහොත් CSV එකක් ආයාත කරන්න.',
  'fleet.vehicles.typeWithMode': '{type} ({mode})',
  'fleet.vehicles.docsCell.verified': '{required} න් {verified} ක් සත්‍යාපිතයි',
  'fleet.vehicles.docsCell.withPermit': '{required} න් {verified} ක් සත්‍යාපිතයි (මාර්ග බලපත්‍රය ඇතුළුව)',
  'fleet.vehicles.docsCell.outstanding': '{verified}/{required} — {slot} {status}',
  'fleet.vehicles.docsCell.pending': 'ලේඛන අපේක්ෂිතයි',
  'fleet.vehicles.docsCell.complete': 'ලේඛන සම්පූර්ණයි',
  'fleet.vehicles.manage': 'ලේඛන',
  'fleet.vehicles.status.pending': 'සමාලෝචනයේ',
  'fleet.vehicles.status.approved': 'අනුමතයි',
  'fleet.vehicles.status.rejected': 'ප්‍රතික්ෂේපිතයි',
  'fleet.vehicles.status.deactivated': 'අක්‍රියයි',

  'fleet.vehicles.error.plateRequired': 'අංක තහඩුව ඇතුළත් කරන්න',
  'fleet.vehicles.error.typeRequired': 'වාහන වර්ගය තෝරන්න',
  'fleet.vehicles.error.modeRequired': 'A ක්‍රමය හෝ B ක්‍රමය තෝරන්න',
  'fleet.vehicles.error.fareRequired': 'පෙරනිමි මාසික ගාස්තුව රුපියල් වලින් ඇතුළත් කරන්න',
  'fleet.vehicles.error.servicePaymentRequired': '"නොමිලේ" හෝ "ගෙවුම්" තෝරන්න',
  'fleet.vehicles.error.servicePaymentModeA':
    'සේවා ගෙවීම අදාළ වන්නේ B ක්‍රමයේ වාහන සඳහා පමණි. A ක්‍රමයේ වාහනයකට එය හිස්ව තබන්න.',
  'fleet.vehicles.error.vehicleRequired': 'පළමුව වාහනය තෝරන්න',
  'fleet.vehicles.error.kindRequired': 'එය ලේඛන කොටු හතරෙන් එකක් නොවේ',
  'fleet.vehicles.error.fileRequired': 'උඩුගත කිරීමට ගොනුවක් තෝරන්න',
  'fleet.vehicles.error.csvRequired': 'ආයාත කිරීමට CSV එකක් තෝරන්න',
  'fleet.vehicles.error.csvTooLarge':
    'එම ගොනුව මෙගාබයිට් {megabytes} ට වඩා විශාලයි. එය බෙදා කොටස් වශයෙන් ආයාත කරන්න.',

  /* ---- SCR-FP-005 · driver assignment ----------------------------------- */
  'fleet.drivers.title': 'රියදුරු පැවරුම',
  'fleet.drivers.assign.heading': 'රියදුරෙකු පවරන්න',
  'fleet.drivers.field.driver': 'පරිශීලක ID / දුරකථනයෙන් රියදුරු පවරන්න',
  'fleet.drivers.field.driverHint':
    'රියදුරාගේ MageRide පරිශීලක ID එක, නැතහොත් ඔහුන් Driver App හි භාවිත කරන ජංගම අංකය — උදාහරණයක් ලෙස 0771234567.',
  'fleet.drivers.field.vehicles': 'වාහන',
  'fleet.drivers.field.vehiclesHint': 'එක් රියදුරෙකු එකවර වාහන කිහිපයකට පැවරිය හැක.',
  'fleet.drivers.field.from': 'සිට',
  'fleet.drivers.field.fromHint': 'දැන් ආරම්භ කිරීමට හිස්ව තබන්න.',
  'fleet.drivers.field.to': 'දක්වා',
  'fleet.drivers.field.toHint':
    'කාලසීමාවක් නැති පැවරුමකට හිස්ව තබන්න. අවසන් දිනයක් එය තාවකාලික පැවරුමක් කරන අතර එය තනිවම කල් ඉකුත් වේ.',
  'fleet.drivers.assign.submit': 'පවරන්න',
  'fleet.drivers.assign.submitting': 'පවරමින්…',
  'fleet.drivers.assign.done': 'වාහන {count} කට පවරන ලදී.',
  'fleet.drivers.assign.doneOne': 'පවරන ලදී.',
  'fleet.drivers.assign.refused': '{plate}: {reason}',
  'fleet.drivers.temporary':
    'තාවකාලිකව බඳවාගත් රියදුරෙකු අවසන් දිනයක් සමඟ පවරනු ලැබේ; පැවරුම තනිවම කල් ඉකුත් වන අතර කිසිවක් අවලංගු කළ යුතු නැත.',
  'fleet.drivers.noVehicles': 'තවම පැවරීමට වාහන නැත. පළමුව වාහන තිරයේදී එකක් ලියාපදිංචි කරන්න.',
  'fleet.drivers.viewerNotice':
    'ඔබ නරඹන්නෙකු ලෙස පිවිස ඇති බැවින්, මෙම තිරය පැවරුම් පෙන්වන අතර ඒවා වෙනස් නොකරයි.',

  'fleet.drivers.table.heading': 'පැවරුම්',
  'fleet.drivers.table.caption': 'මෙම ආයතනයේ සෑම රියදුරු පැවරුමක්ම, ක්‍රියාකාරී ඒවා මුලින්',
  'fleet.drivers.column.driver': 'රියදුරු',
  'fleet.drivers.column.vehicle': 'වාහනය',
  'fleet.drivers.column.since': 'සිට',
  'fleet.drivers.column.until': 'දක්වා',
  'fleet.drivers.column.status': 'තත්ත්වය',
  'fleet.drivers.column.actions': 'ක්‍රියා',
  'fleet.drivers.table.empty': 'තවම කිසිදු රියදුරෙකු පවරා නැත.',
  'fleet.drivers.openEnded': 'කාලසීමාවක් නැත',
  'fleet.drivers.status.active': 'ක්‍රියාකාරී',
  'fleet.drivers.status.revoked': 'අවලංගුයි',
  'fleet.drivers.status.expired': 'අවසන් විය',
  'fleet.drivers.status.scheduled': 'පසුව ආරම්භ වේ',
  'fleet.drivers.revoke': 'අවලංගු කරන්න',
  'fleet.drivers.revoking': 'අවලංගු කරමින්…',
  'fleet.drivers.revokeNote':
    'අවලංගු කිරීමෙන් රියදුරාට එම වාහනයේ නව සැසියක් ආරම්භ කිරීම වහාම නවතී; දැනටමත් ආරම්භ කර ඇති ගමනක් අවසන් වීමට ඉඩ දෙයි.',
  'fleet.drivers.history': 'පැවරුම් ඉතිහාසය අවලංගු කළ හා කල් ඉකුත් වූ ඒවා ද සමඟ එක් එක් වාහනයට තබා ගැනේ.',
  'fleet.drivers.noInvite':
    'රියදුරෙකුට දැනටමත් MageRide Driver App ගිණුමක් තිබිය යුතුය. මෙතැනින් ඔවුන්ට ආරාධනයක් යැවිය නොහැක — Driver App හි ලියාපදිංචි වන ලෙස පවසා, පසුව ඔවුන්ගේ අංකයෙන් පවරන්න.',
  'fleet.drivers.error.driverRequired':
    'රියදුරාගේ පරිශීලක ID එක හෝ ශ්‍රී ලාංකික ජංගම අංකයක් ඇතුළත් කරන්න, උදාහරණයක් ලෙස 0771234567',
  'fleet.drivers.error.vehicleRequired': 'අවම වශයෙන් එක් වාහනයක් තෝරන්න',
  'fleet.drivers.error.windowInverted': 'අවසන් දිනය ආරම්භක දිනයට පසුව විය යුතුය',
  'fleet.drivers.error.assignmentRequired': 'එම පැවරුම තවදුරටත් නොපවතී',

  /* ---- SCR-FP-006 · tracker binding ------------------------------------- */
  'fleet.trackers.title': 'ට්‍රැකර් සම්බන්ධ කිරීම',
  'fleet.trackers.bind.heading': 'ට්‍රැකරයක් සම්බන්ධ කරන්න',
  'fleet.trackers.autoSession': 'ස්වයංක්‍රීය සැසි වින්‍යාසය',
  'fleet.trackers.field.imei': 'IMEI / MAC',
  'fleet.trackers.field.imeiHint': 'ST-901 හි මුද්‍රිත ඉලක්කම් 15. හිස්තැන් හා ඉරි නොසලකා හරී.',
  'fleet.trackers.field.vehicle': 'වාහනය',
  'fleet.trackers.field.autoStart': 'ට්‍රැකරයෙන් ගමන් ආරම්භ කර අවසන් කරන්න',
  'fleet.trackers.field.autoStartHint':
    'රියදුරෙක් යෙදුම විවෘත කර තිබුණත් නැතත් බසයක් එහි ට්‍රැකරයෙන් තොරතුරු යවන අතර, ගමන ඉග්නිෂන් සමඟ ආරම්භ වී අවසන් වේ.',
  'fleet.trackers.bind.submit': 'ට්‍රැකරය සම්බන්ධ කරන්න',
  'fleet.trackers.bind.submitting': 'සම්බන්ධ කරමින්…',
  'fleet.trackers.bind.done': '{imei} සම්බන්ධ වී එහි අක්තපත්‍රය නිකුත් කර ඇත.',
  'fleet.trackers.bind.pendingOrg':
    'සත්‍යාපන නිලධාරියෙක් මෙම ආයතනය අනුමත කළ පසු ට්‍රැකර් සම්බන්ධ කිරීම විවෘත වේ.',
  'fleet.trackers.noVehicles':
    'තවම ට්‍රැකරයක් සම්බන්ධ කිරීමට වාහන නැත. පළමුව වාහන තිරයේදී එකක් ලියාපදිංචි කරන්න.',
  'fleet.trackers.viewerNotice':
    'ඔබ නරඹන්නෙකු ලෙස පිවිස ඇති බැවින්, මෙම තිරය ට්‍රැකර් සෞඛ්‍යය පෙන්වන අතර කිසිවක් සම්බන්ධ නොකරයි.',

  'fleet.trackers.bulk.heading': 'තොග සම්බන්ධ කිරීම',
  'fleet.trackers.bulk.prompt': 'CSV මෙහි දමන්න හෝ ගොනුවක් තෝරන්න',
  'fleet.trackers.bulk.hint': 'පේළි {rows} දක්වා, මෙගාබයිට් {megabytes}.',
  'fleet.trackers.bulk.columns': 'තීරු: {columns}.',
  'fleet.trackers.bulk.credentialType': 'අක්තපත්‍රය',
  'fleet.trackers.bulk.credential.x509': 'සහතිකය (MQTT ට්‍රැකර්)',
  'fleet.trackers.bulk.credential.psk': 'පෙර-බෙදාගත් යතුර (පැරණි TCP ට්‍රැකර්)',
  'fleet.trackers.bulk.credentialHint':
    'මුළු කණ්ඩායමටම එක් තේරීමක් — වාහන සමූහයක් සාමාන්‍යයෙන් එකම දෘඪාංග පරම්පරාවකි.',
  'fleet.trackers.bulk.uploading': 'උඩුගත කරමින්…',
  'fleet.trackers.bulk.processing': 'ට්‍රැකර් {total} සම්බන්ධ කරමින්…',
  'fleet.trackers.bulk.bound': 'ට්‍රැකර් {total} න් {succeeded} ක් සම්බන්ධ විය.',
  'fleet.trackers.bulk.someFailed': 'පේළි {failed} ක් සම්බන්ධ නොවීය.',
  'fleet.trackers.bulk.report': 'පේළි වාර්තාව බාගන්න',
  'fleet.trackers.bulk.refresh': 'නැවත බලන්න',
  'fleet.trackers.bulk.jobFailed': 'එම කණ්ඩායම සැකසිය නොහැකි විය. ගොනුව පරීක්ෂා කර නැවත උත්සාහ කරන්න.',

  'fleet.trackers.table.heading': 'ST-901 ට්‍රැකර්',
  'fleet.trackers.table.caption':
    'මෙම ආයතනයට සම්බන්ධ සෑම ට්‍රැකරයක්ම, එහි වාහනය, තොරතුරු යවන වේගය හා සෞඛ්‍යය',
  'fleet.trackers.column.imei': 'IMEI / MAC',
  'fleet.trackers.column.vehicle': 'වාහනය',
  'fleet.trackers.column.cadence': 'යැවීමේ වේගය',
  'fleet.trackers.column.lastSeen': 'අවසන් වරට',
  'fleet.trackers.column.health': 'සෞඛ්‍යය',
  'fleet.trackers.column.credential': 'අක්තපත්‍රය',
  'fleet.trackers.table.empty': 'තවම මෙම ආයතනයේ වාහනයකට ට්‍රැකරයක් සම්බන්ධ කර නැත.',
  'fleet.trackers.state.online': 'සබැඳියි',
  'fleet.trackers.state.stale': 'පරණයි',
  'fleet.trackers.state.offline': 'නොබැඳියි',
  'fleet.trackers.state.decommissioned': 'ඉවත් කර ඇත',
  'fleet.trackers.credential.active': 'ක්‍රියාකාරී',
  'fleet.trackers.credential.revoked': 'අවලංගුයි',
  'fleet.trackers.counts': 'සබැඳි {online} · පරණ {stale} · නොබැඳි {offline}',
  'fleet.trackers.thresholds':
    'මිනිත්තු {stale} ක් සංඥාවක් නැත්නම් "පරණ"; මිනිත්තු {offline} ක් සංඥාවක් නැත්නම් "නොබැඳි".',
  'fleet.trackers.truncated':
    'මෙම ලැයිස්තුව සීමා කර ඇත. ඉහත ගණන් තවමත් සමූහයේ සෑම ට්‍රැකරයක්ම ආවරණය කරයි.',
  'fleet.trackers.asOf': '{time} වන විට',
  'fleet.trackers.never': 'කිසිදා නැත',
  'fleet.trackers.unknownVehicle': 'ලැයිස්තුවේ නැත',
  'fleet.trackers.cadence': 'ගමනේදී තත්පර {moving} · නැවතී ඇතිවිට තත්පර {stationary}',
  'fleet.trackers.cadenceNote':
    'මෙය සෑම A හා B ක්‍රමයේ සැසියක්ම තොරතුරු යවන වේගයයි. එක් එක් වාහනයට වෙනම වේගයක් තවම ද්වාරයෙන් සැකසිය නොහැක — වෙනසක් සඳහා MageRide සහාය අංශයෙන් ඉල්ලන්න.',
  'fleet.trackers.error.imeiInvalid': 'ට්‍රැකරයේ මුද්‍රිත ඉලක්කම් 15 ඇතුළත් කරන්න',
  'fleet.trackers.error.vehicleRequired': 'මෙම ට්‍රැකරය සවි කර ඇති වාහනය තෝරන්න',
  'fleet.trackers.error.csvRequired': 'ආයාත කිරීමට CSV එකක් තෝරන්න',

  /* ---- The shell's placeholder ------------------------------------------ */
  'fleet.screen.pendingTitle': 'මෙම තිරය තවම නිර්මාණය කර නැත',
  'fleet.screen.pendingBody':
    'Fleet Portal රාමුව මෙම මාර්ගය හඳුනාගත් අතර ඔබේ භූමිකාව එයට අවසර දෙයි. තිරය පසුව එන ගොඩනැගීමේ අංගයක් සමඟ ලැබෙනු ඇත.',
  'fleet.screen.servedBy': 'API සපයන්නේ {service}',
  'fleet.screen.wireframe': 'රැහැන් රාමුව {screen}',
};
