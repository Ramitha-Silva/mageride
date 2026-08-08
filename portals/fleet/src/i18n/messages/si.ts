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

  /* ---- The shell's placeholder ------------------------------------------ */
  'fleet.screen.pendingTitle': 'මෙම තිරය තවම නිර්මාණය කර නැත',
  'fleet.screen.pendingBody':
    'Fleet Portal රාමුව මෙම මාර්ගය හඳුනාගත් අතර ඔබේ භූමිකාව එයට අවසර දෙයි. තිරය පසුව එන ගොඩනැගීමේ අංගයක් සමඟ ලැබෙනු ඇත.',
  'fleet.screen.servedBy': 'API සපයන්නේ {service}',
  'fleet.screen.wireframe': 'රැහැන් රාමුව {screen}',
};
