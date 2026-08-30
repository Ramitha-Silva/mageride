import type { WwwMessages } from './en';

/**
 * Sinhala resources. `WwwMessages` is the literal shape of `wwwEn`, so this file
 * cannot be missing a key `en.ts` has, nor carry one it does not.
 *
 * D1' §283 makes Sinhala the platform's default and `DEFAULT_LOCALE` is `si`, so
 * this is the table a visitor whose browser says nothing about language gets — and
 * the table the root `/` redirect lands on. On this surface that matters more than
 * on any other: it is the first MageRide page a Sri Lankan reader who has never
 * heard of MageRide will open.
 */
export const wwwSi: WwwMessages = {
  'www.brand.name': 'MageRide',
  'www.brand.tagline': 'ශ්‍රී ලංකාව ගමන් කරන ආකාරය එක් සජීවී චිත්‍රයකින්',

  'www.nav.home': 'මුල් පිටුව',
  'www.nav.vision': 'දැක්ම',
  'www.nav.passengers': 'මගීන් සඳහා',
  'www.nav.drivers': 'රියදුරන් සඳහා',
  'www.nav.fleets': 'රථ හිමියන් සඳහා',
  'www.nav.screens': 'තිර',
  'www.nav.guide': 'MageRide භාවිත කරන ආකාරය',
  'www.nav.faq': 'ප්‍රශ්න',
  'www.nav.download': 'යෙදුම ලබා ගන්න',
  'www.nav.contact': 'සම්බන්ධ වන්න',
  'www.nav.legal.terms': 'සේවා කොන්දේසි',
  'www.nav.legal.privacy': 'රහස්‍යතා ප්‍රතිපත්තිය',
  'www.nav.legal.pdpa': 'ඔබේ දත්ත අයිතිවාසිකම්',

  // `www.scaffold.notice` was deleted in S18 with `StubPage` — the last five routes
  // that rendered it were written in that session, so the key had no caller left.

  'www.notFound.title': 'පිටුව හමු නොවීය',
  'www.notFound.body': 'එම ලිපිනය මෙම වෙබ් අඩවියේ නොපවතී.',
  'www.notFound.home': 'මුල් පිටුවට යන්න',
  'www.error.title': 'යම් දෝෂයක් සිදු විය',
  'www.error.body': 'මෙම පිටුව පෙන්විය නොහැකි විය. නැවත උත්සාහ කරන්න.',

  // The chrome (S14). Terminology from `src/content/glossary.si.ts`; the landmark
  // names follow the fleet portal's own Sinhala (`fleet.skipToContent`,
  // `fleet.nav.open`, `fleet.appearance.*`) so a reader who uses two MageRide
  // surfaces meets one vocabulary.
  'www.a11y.skipToContent': 'අන්තර්ගතයට යන්න',

  'www.nav.primary': 'ප්‍රධාන',
  'www.nav.footer': 'පාදකය',
  'www.nav.brandHome': 'MageRide — මුල් පිටුව',
  'www.nav.menu.open': 'මෙනුව',
  'www.nav.menu.close': 'මෙනුව වසන්න',
  'www.nav.menu.title': 'මෙනුව',

  // `{language}` is an endonym and arrives already in its own script — සිංහල,
  // தமிழ், English — so the sentence around it is Sinhala and the name inside it
  // is not translated. That is the point of an endonym.
  'www.language.label': 'භාෂාව',
  'www.language.current': '{language}, වත්මන් භාෂාව',
  'www.language.switchTo': 'මෙම පිටුව {language} බසින් කියවන්න',

  'www.appearance.dark': 'තද පෙනුම',

  'www.hero.label': 'MageRide කරන දේ',
  'www.hero.slideAnnouncement': 'දර්ශන {count}න් {index}: {headline}',

  // The screen showcase and its lightbox (S15).
  'www.showcase.label': 'MageRide යෙදුම්වල තිර',
  'www.showcase.open': 'විශාලව බලන්න: {caption}',
  'www.showcase.lightbox.title': 'තිරය',
  'www.showcase.lightbox.position': 'රූප {count}න් {index}',

  'www.motion.carousel.roleDescription': 'දර්ශන පෙරළිය',
  'www.motion.carousel.slideRoleDescription': 'දර්ශනය',
  'www.motion.carousel.slidePosition': 'දර්ශන {count}න් {index} වැන්න',
  'www.motion.carousel.goToSlide': '{index} වන දර්ශනය පෙන්වන්න',
  'www.motion.carousel.pause': 'දර්ශන පෙරළීම නවත්වන්න',
  'www.motion.carousel.play': 'දර්ශන පෙරළීම ආරම්භ කරන්න',

  // Screen captions — see the block comment in `en.ts`. First-pass Sinhala; S12
  // owns the native review.
  'www.screens.provenance':
    'මෙම රූප අනුමත කරන ලද MageRide අතුරුමුහුණත් නිර්මාණවලින් නිපදවා ඇති අතර, ' +
    'නිකුත් කළ යෙදුමකින් ගත් ඡායාරූප නොවේ.',

  'www.screens.pa001.caption': 'මගියෙකුගේ දුරකථනයේ MageRide විවෘත වීම',
  'www.screens.pa002.caption': 'සියල්ලට පෙර සිංහල, දෙමළ හෝ ඉංග්‍රීසි තෝරා ගැනීම',
  'www.screens.pa003.caption': 'දුරකථන අංකයකින් සහ එක් වරක් කේතයකින් පිවිසීම',
  'www.screens.pa004.caption': 'ඔබේ නම සහ ඡායාරූපය එක් කර පැතිකඩ සම්පූර්ණ කිරීම',
  'www.screens.pa005.caption': 'ඔබේ ස්ථානය භාවිතයට අවසර ඉල්ලීම, එය කුමකට දැයි කීම',
  'www.screens.pa006.caption': 'ප්‍රවාහන ක්‍රමය හා වාහන වර්ගය අනුව සිතියම පෙරහන් කිරීම',
  'www.screens.pa007.caption': 'වාහනයක් තට්ටු කර එහි මාර්ගය, වර්ගය හා පිහිටීම බැලීම',
  'www.screens.pa008.caption': 'නමින් ස්ථානයක් සෙවීම, නැතහොත් සිතියමේ ලකුණක් තැබීම',
  'www.screens.pa009.caption': 'වාහනයක් තෝරා, වෙන් කිරීමට පෙර ගාස්තුව දැකීම',
  'www.screens.pa010.caption': 'ඔබ අසල බස්, දුම්රිය සහ ත්‍රීරෝද — සැබෑ වේලාවට ගමන් කරමින්',
  'www.screens.pa011.caption': 'වෙනත් අයෙක් ඔබට ගමනක් වෙන් කළ විට පිකප් එක තහවුරු කිරීම',
  'www.screens.pa012.caption': 'පාර්සලයක් යැවීම — ප්‍රමාණය, ලබන්නා සහ බෙදාහැරීමේ කේතය',
  'www.screens.pa013.caption': 'දවසේ පසුවට හෝ සතියේ පසුවට ගමනක් වෙන් කිරීම',
  'www.screens.pa014.caption': 'ඔබේ ගමන අසල රියදුරන්ට ඉදිරිපත් වන අතරතුර රැඳී සිටීම',
  'www.screens.pa015.caption': 'රියදුරුගේ විස්තර ළඟ තබාගෙන ඔබේ ගමන අනුගමනය කිරීම',
  'www.screens.pa016.caption': 'ගෙවන ආකාරය තෝරාගැනීම — මුදල් හෝ රියදුරුගේ කේතය පරිලෝකනය',
  'www.screens.pa017.caption': 'ගමන අවසානයේ ගෙවීම',
  'www.screens.pa018.caption': 'ගාස්තුව බෙදා දක්වන ගමන් සාරාංශය',
  'www.screens.pa019.caption': 'ඔබේ රියදුරුට ශ්‍රේණිගත කිරීමක් හා සමාලෝචනයක් තැබීම',
  'www.screens.pa020.caption': 'ඔබේ පාර්සලය අදියර තුනක් හරහා ගමන් කරනු බැලීම',
  'www.screens.pa021.caption': 'පාර්සලයක් ලබන පුද්ගලයාට පෙනෙන දේ',
  'www.screens.pa022.caption': 'ඔබේ පසුගිය ගමන් සහ නියමිත ගමන් එකම ලැයිස්තුවක',
  'www.screens.pa024.caption': 'වාහන හිමියෙකුගෙන් එය අනුගමනය කිරීමට අවසර ඉල්ලීම',
  'www.screens.pa025.caption': 'ඔබ අනුගමනය කරන පෞද්ගලික වාහන සහ එක් එක් වියදම',
  'www.screens.pa025a.caption': 'වාහනයක් අනුගමනය කිරීමට මාසික දායකත්වයක් ගෙවීම',
  'www.screens.pa026.caption': 'ඔබ නිතර යන ස්ථාන සුරැකීම',
  'www.screens.pa027.caption': 'ඔබේ පැතිකඩ, භාෂාව සහ රහස්‍යතා සැකසුම්',
  'www.screens.pa029.caption': 'ගමනක් තුළ සිටම ළඟා විය හැකි හදිසි උපකාර',
  'www.screens.pa030.caption': 'උපකාර ලබා ගැනීම සහ අවශ්‍ය නම් ටිකට්පතක් යෙදීම',

  'www.screens.da001.caption': 'MageRide රියදුරු යෙදුම විවෘත වීම',
  'www.screens.da002.caption': 'ඔබේ භාෂාව සහ ඔබ රිය පදවන නගරය තෝරා ගැනීම',
  'www.screens.da003.caption': 'ඔබේ දුරකථන අංකයෙන් සහ එක් වරක් කේතයකින් පිවිසීම',
  'www.screens.da003a.caption': 'ඔබේ රියදුරු පැතිකඩ සකස් කිරීම',
  'www.screens.da004.caption': 'වාහනයක් ලියාපදිංචි කිරීම, එක් පියවරක් බැගින්',
  'www.screens.da004a.caption': 'ඔබේ රක්ෂණ විස්තර එක් කිරීම',
  'www.screens.da005.caption': 'ලේඛනයක් ඡායාරූප ගැනීම, කප්පාදුව ඔබේ පාලනයෙන්',
  'www.screens.da006.caption': 'ලේඛනයෙන් ලේඛනයට ඔබේ අනුමැතිය අනුගමනය කිරීම',
  'www.screens.da007.caption': 'රියදුරු යෙදුමට අවශ්‍ය අවසර, සහ ඒවා කුමකටද',
  'www.screens.da010.caption': 'ඔබේ උපකරණ පුවරුව — සූදානමට ගොස් ඉපැයීම අරඹන්න',
  'www.screens.da011.caption': 'නියමිත ගමනක් ආරම්භ කිරීම සහ අවසන් කිරීම',
  'www.screens.da013.caption': 'ඔබ යන දිශාව සැකසීම, එමගින් මගදී ගමන් ලැබේ',
  'www.screens.da014.caption': 'ගමන් ඉල්ලීමක්, පිළිගැනීමට තත්පර පහළොවක්',
  'www.screens.da015.caption': 'පිකප් සිට ගමනාන්තය දක්වා ගමනක් මෙහෙයවීම',
  'www.screens.da016a.caption': 'බෙදාහැරීමේ රැකියාවක් පිළිගැනීමට පෙර සමාලෝචනය',
  'www.screens.da016b.caption': 'පාර්සලය රැගෙන කේතය තහවුරු කිරීම',
  'www.screens.da016c.caption': 'ලැබුණු බවට සාක්ෂි සමඟ බෙදාහැරීම සම්පූර්ණ කිරීම',
  // Δ S10: was 'රැකියා පුවරුව — ඔබට ගත හැකි බෙදාහැරීම්' ("deliveries you can pick
  // up"). The job board carries scheduled rides, not deliveries — see the note on
  // this key in `en.ts`. Rebuilt from phrases already in this table rather than
  // translated afresh: 'ඔබ අසල' from pa010, 'කලින් වෙන් කළ ගමන්' from da018.
  'www.screens.da017.caption': 'රැකියා පුවරුව — ඔබ අසල, කලින් වෙන් කළ ගමන්',
  'www.screens.da018.caption': 'කලින් වෙන් කළ ගමන්, ඔබ එනතුරු බලා',
  'www.screens.da019.caption': 'ඔබේ රියදුරු මට්ටම, ශ්‍රේණිගත කිරීම සහ සංඛ්‍යාලේඛන',
  'www.screens.da020.caption': 'අද, මේ සතියේ සහ මේ මාසයේ ඔබ ඉපැයූ දේ',
  'www.screens.da021.caption': 'ඔබේ පසුම්බිය, සහ එයින් ගන්නා දෛනික ගාස්තුව',
  'www.screens.da022.caption': 'කාඩ්පත, OnePay හෝ LankaQR මගින් පසුම්බිය පිරවීම',
  'www.screens.da023.caption': 'රියදුරු හැඳුනුම්පතින් වෙනත් රියදුරෙකුගෙන් ණය ඉල්ලීම',
  'www.screens.da024.caption': 'ඉල්ලූ රියදුරෙකුට ණය මාරු කිරීම',
  'www.screens.da025.caption': 'ඔබ ගෙවා ඇති සෑම දෛනික ගාස්තුවක්ම ලැයිස්තුගත',
  // Δ S12: moved here from the driver-guide block, where S10 appended it. It is a
  // screen caption and `en.ts` has it between da025 and da027; the two files now
  // agree on order, which is how a missing key stays easy to spot by eye.
  'www.screens.da026.caption': 'මගේ වාහන — අනුමත වී ඇත්තේ කුමක්ද, අවසන් නොවී ඇත්තේ කුමක්ද',
  'www.screens.da027.caption': 'වාහනයකට GPS ට්‍රැකරයක් යුගල කිරීම',
  'www.screens.da028.caption': 'ඔබේ වාහනය අනුගමනය කිරීමට කාට අවසර දැයි තීරණය',
  'www.screens.da032.caption': 'රියදුරන් සඳහා හදිසි උපකාර, එක් එබීමකින්',
  'www.screens.da033.caption': 'සහාය, සහ දෛනික ගාස්තුවක් ආපසු ඉල්ලීම',

  'www.screens.fp001.caption': 'රථ සමූහ ද්වාරයේ ඔබේ සංවිධානය ලියාපදිංචි කිරීම',
  'www.screens.fp002.caption': 'ඔබේ සංවිධානයේ පැතිකඩ සහ එහි KYC ලේඛන',
  'www.screens.fp002a.caption': 'බැංකු හා ගෙවීම් විස්තර — දායක මුදල් ලැබෙන තැන',
  'www.screens.fp003.caption': 'ඔබේ මුළු රථ සමූහයම එක් උපකරණ පුවරුවක',
  'www.screens.fp004.caption': 'වාහනයක් එක් කිරීම — එකින් එක හෝ තොග වශයෙන්',
  'www.screens.fp005.caption': 'රියදුරන් ඔවුන් පදවන වාහනවලට පැවරීම',
  'www.screens.fp006.caption': 'වාහනයකට GPS ට්‍රැකරයක් බැඳීම',
  'www.screens.fp007.caption': 'ඔබ සතු සෑම වාහනයක්ම එක් සිතියමක සජීවීව',
  'www.screens.fp010.caption': 'ඔබේ රථ සමූහයේ බිල්පත් හා පසුම්බිය එකම තැනක',

  'www.screens.wt001.caption': 'SMS මගින් එන ලුහුබැඳීමේ සබැඳිය — යෙදුමක් නැත, ගිණුමක් නැත',
  'www.screens.wt002.caption': 'කිසිවක් ස්ථාපනය නොකර බ්‍රවුසරයේ පාර්සලයක් අනුගමනය කිරීම',
  'www.screens.wt003.caption': 'පිවිසීමකින් තොරව සබැඳියකින් පිකප් එකක් තහවුරු කිරීම',
  'www.screens.wt005.caption': 'පාර්සලය ලැබුණු බවට තහවුරු කිරීම',

  // =========================================================================
  // S07 · the marketing corpus — translated in S12.
  //
  // Terminology is fixed in `src/content/glossary.si.ts`, decided before any of
  // this prose was written and naming the app resource each term came from. A
  // Sinhala word here that contradicts that file is a defect rather than a
  // variant.
  //
  // **First pass, not native-reviewed.** Structural parity is guaranteed — the
  // key set is a compile error and `check-i18n-parity.mjs` compares placeholder
  // sets — but transport, payment and legal register is exactly where a first
  // pass is thinnest. See the S12 handoff in `build/progress.md`.
  // =========================================================================

  // The hero, set at 40–72px in three scripts.
  //
  // **Measured at 375px, in the browser, in the real `.text-hero` box (343px
  // wide): three lines.** English is two. A literal rendering — 'ශ්‍රී ලංකාව
  // ගමන් කරන ආකාරය සජීවීව බලන්න.' — measures three as well, so dropping 'ශ්‍රී'
  // and 'බලන්න' bought register, not a line; it is kept because it is the
  // shorter sentence, not because it changed the layout. S12's threshold is four
  // lines and this does not reach it, so the type was not shrunk and no fewer
  // words were forced.
  //
  // What Sinhala *did* need was leading: at 40px its ink is 51px in a 44px line
  // box and two hero lines overlapped by 6px. Fixed in `app/globals.css`'s
  // `@layer utilities`, for `html[lang='si']` and `html[lang='ta']` — not in a
  // token. The hero is therefore 168px tall here against English's 88px, which
  // is a real constraint on S14's hero layout rather than something to tune away
  // in this file.
  'www.vision.hero': 'ලංකාව ගමන් කරන ආකාරය — සජීවීව.',

  'www.vision.body.p1':
    'ශ්‍රී ලංකාව ගමන් කරන්නේ ඇස නොගැටෙන ප්‍රවාහනයකිනි. බසයක් එහි මාර්ගයේ කොහේ හෝ තිබේ. ' +
    'පාසල් වෑන් රථයක් ගෙදර සහ පාසල අතර කොහේ හෝ තිබේ. ත්‍රීරෝද රථයක් අසල තිබේ, නැත්නම් ' +
    'නැත. හැමෝම බලා සිටිති, කොපමණ වේලාවක්දැයි කිසිවෙකුට කිව නොහැක.',
  'www.vision.body.p2':
    'MageRide ඒවා එකම සිතියමකට ගෙන එයි. මහජන බසයක් හෝ දුම්රියක් පැමිණෙනු බලා සිටින්න. ' +
    'ඔබට බැලීමට අවසර දී ඇති පාසල් වෑන් රථයක් අනුගමනය කරන්න. ත්‍රීරෝද රථයක්, මෝටර් රථයක් ' +
    'හෝ වෑන් රථයක් වෙන් කරගෙන, එකඟ වීමට පෙරම ගාස්තුව දැනගන්න. නගරය හරහා පාර්සලයක් යවා, ' +
    'එය යනු බලා සිටින්න.',
  'www.vision.body.p3':
    'එක් යෙදුමක කරුණු තුනක්, සිංහල, දෙමළ සහ ඉංග්‍රීසි යන තුනෙන්ම — සහ මේ සියල්ල ' +
    'ක්‍රියාත්මක කරන රියදුරන්ගෙන් ගන්නා කිසිදු කොමිසයක් නැත.',

  'www.mission.statement':
    'රට ගමන් කරන ආකාරය පිළිබඳ එක් සජීවී චිත්‍රයක් ශ්‍රී ලංකාවට ලබා දීම සඳහා MageRide ' +
    'පවතී — බස්, දුම්රිය, ත්‍රීරෝද සහ වෑන් රථ එකම සිතියමක, පෞද්ගලික සේවාවක් ලෙස නොව ' +
    'මහජන යටිතල පහසුකමක් ලෙස ක්‍රියාත්මක වෙමින්.',

  // Required furniture wherever the mission renders (MCS-34 D1). A layout session
  // may move it; it may not drop it. In Sinhala as in English it is written as a
  // plain admission, not as a disclaimer.
  'www.mission.qualifier':
    'අපි ආරම්භ කර ඇත, අවසන් කර නැත. වාහනයක් සිතියමේ දිස් වන්නේ එහි මෙහෙයුම්කරු හෝ ' +
    'රියදුරු එක් වූ පසුව පමණි. එබැවින් මුල් කාලයේදී ඔබට පෙනෙන්නේ එක් වූ අය මිස රටේ ' +
    'සෑම වාහනයක්ම නොවේ. සිතියම වෙනත් දෙයක් ඇඟවීමට ඉඩ දෙනවාට වඩා අපි ඒ බව ඔබට කීම ' +
    'කැමැත්තෙමු.',

  'www.values.zeroCommission.title': 'රියදුරන්ට 100%ම',
  'www.values.zeroCommission.body':
    'කිසිදු ගාස්තුවකින් MageRide කොමිස් ගන්නේ නැත. මගියෙකුට පෙනෙන මිල රියදුරාට ලැබෙන ' +
    'මිලමයි — අපි කිසිවිටෙක ඔවුන් සහ මුදල අතරට එන්නේ නැත.',
  'www.values.passengersFree.title': 'මගීන් කිසිවක් ගෙවන්නේ නැත',
  'www.values.passengersFree.body':
    'දායකත්වයක් නැත, උසස් ස්තරයක් නැත, වෙන් කිරීමේ ගාස්තුවක් නැත. ඔබ ගමන වෙනුවෙන් ' +
    'රියදුරාට ගෙවනවා මිස අපට කිසිවක් ගෙවන්නේ නැත.',
  'www.values.firstTripFree.title': 'දිනකට පළමු ගමන නොමිලේ',
  'www.values.firstTripFree.body':
    'රියදුරන් ස්ථාවර දෛනික ගාස්තුවක් ගෙවන්නේ ඔවුන්ගේ දෙවන ගමනේ සිට පමණි, රිය නොපදවන ' +
    'දිනවල කිසිවක් නැත. ගමනකින් කැපීමක් කිසිදා නැත.',
  'www.values.trilingual.title': 'සිංහල, දෙමළ සහ ඉංග්‍රීසි',
  'www.values.trilingual.body':
    'පළමු එළිදැක්වීමේ සිටම සෑම තිරයක්ම භාෂා තුනෙන්ම. පසුව සිතූ දෙයක් නොවේ, අඩක් කළ ' +
    'පරිවර්තනයක්ද නොවේ — ඔබ කියවන්නේ කුමක් වුවත් එකම යෙදුමයි.',
  'www.values.openMapping.title': 'විවෘත සිතියම් මත ගොඩනැගී ඇත',
  'www.values.openMapping.body':
    'MageRide ක්‍රියාත්මක වන්නේ බලපත්‍ර ලත් වාණිජ සිතියමකින් නොව, තමන්ගේම සිතියම් ' +
    'සේවාදායකවලින් ලබා දෙන OpenStreetMap දත්ත මතය. එය එම වියදම රියදුරන්ගෙන් ඉවත් කරන ' +
    'අතර, සිතියම අපටම නිවැරදි කළ හැකි දෙයක් ලෙස තබා ගනී.',
  'www.values.yourData.title': 'ඔබේ දත්ත ඔබේම ය',
  'www.values.yourData.body':
    'ඔබ ගැන අප සතුව ඇති සියල්ලේ පිටපතක් ඉල්ලන්න, දින 30ක් ඇතුළත අපි එය එවන්නෙමු. එය ' +
    'මකා දමන ලෙස ඉල්ලන්න, වාර්තාවක් තබා ගැනීමට නීතියෙන් නියම කර ඇති තැන් හැර, දින 30ක් ' +
    'ඇතුළත අපි එසේ කරන්නෙමු.',

  // --- hero slides ---------------------------------------------------------
  'www.hero.track.headline': 'එය එනු බලා සිටින්න',
  'www.hero.track.sub': 'බස්, දුම්රිය, ත්‍රීරෝද සහ වෑන් රථ එකම සිතියමක සජීවීව.',
  'www.hero.book.headline': 'තත්පර කිහිපයකින් ගමනක්',
  'www.hero.book.sub':
    'වාහනයක් තෝරන්න, එකඟ වීමට පෙර ගාස්තුව බලන්න, රියදුරු ඔබේ දොරටම එනු අනුගමනය කරන්න.',
  'www.hero.drivers.headline': 'රියදුරන්ට 100%ම',
  'www.hero.drivers.sub':
    'කිසිදු ගාස්තුවකින් කොමිසයක් නැත. දෙවන ගමනේ සිට ස්ථාවර දෛනික ගාස්තුවක්, රිය නොපදවන ' +
    'දිනවල කිසිවක් නැත.',
  'www.hero.deliver.headline': 'නගරය හරහා පාර්සලයක් යවන්න',
  'www.hero.deliver.sub':
    'රියදුරෙකුට භාර දෙන්න, සබැඳියක් බෙදාගන්න, එය බාරගැනීමේ සිට දොරකඩ දක්වා යනු බලා ' +
    'සිටින්න.',

  // --- calls to action -----------------------------------------------------
  'www.cta.getTheApp': 'යෙදුම ලබා ගන්න',
  'www.cta.seeHowItWorks': 'ක්‍රියා කරන ආකාරය බලන්න',
  'www.cta.passengerGuide': 'මගී මාර්ගෝපදේශය කියවන්න',
  'www.cta.driveWithUs': 'MageRide සමඟ රිය පදවන්න',
  'www.cta.seeTheFees': 'දෛනික ගාස්තුව බලන්න',

  // --- the three modes -----------------------------------------------------
  // `ක්‍රමය` and not `ප්‍රකාරය` or `මාදිලි` — the platform disagrees with itself
  // and the glossary records why this site picked the plurality.
  'www.modes.a.name': 'A ක්‍රමය — මහජන ප්‍රවාහනය',
  'www.modes.a.tagline': 'බැලීමට සැමවිටම නොමිලේ',
  'www.modes.a.body':
    'මහජන බස් සහ දුම්රිය ස්ථාවර මාර්ග ඔස්සේ ඔවුන්ගේ සජීවී පිහිටීම බෙදා ගනී. ඕනෑම ' +
    'කෙනෙකුට ඒවා බැලිය හැක: MageRide හට ගාස්තුවක් නැත, දායකත්වයක් නැත, අවසරයක්ද අවශ්‍ය ' +
    'නැත. දිස් වීමට මෙහෙයුම්කරුවන් කිසිවක් ගෙවන්නේ නැත, සිතියමේ මෙම කොටස නිෂ්පාදනයක් ' +
    'නොව මහජන යටිතල පහසුකමක් කරන්නේ එයයි.',
  'www.modes.b.name': 'B ක්‍රමය — ඔබ අනුගමනය කරන පෞද්ගලික වාහන',
  'www.modes.b.tagline': 'අවසරයෙන් පමණි',
  'www.modes.b.body':
    'පාසල් වෑන් රථයක්, කාර්යාලයේ කාර්ය මණ්ඩල බසයක්, පවුලක් බෙදාගන්නා වාහනයක්. එය ඔබට ' +
    'පෙනෙන්නේ එහි හිමිකරු ඔබට අවසර දී ඇත්නම් පමණි, එය කාට දෙනවාද යන්න තීරණය කරන්නේ ' +
    'ඔවුන්ය. සමහරක් අනුගමනය කිරීම නොමිලේ; අනෙක් ඒවාට මාසික දායකත්වයක් ඇත. ඇතුළට ' +
    'ගැනීමකින් තොරව කිසිවෙකුට පෞද්ගලික වාහනයක් බැලිය නොහැක.',
  'www.modes.c.name': 'C ක්‍රමය — ගමන් සහ බෙදාහැරීම්',
  'www.modes.c.tagline': 'ඉල්ලුම මත, ගාස්තුව කලින් දන්වා',
  'www.modes.c.body':
    'දැන්ම යතුරුපැදියක්, ත්‍රීරෝද රථයක්, මෝටර් රථයක් හෝ වෑන් රථයක් කැඳවන්න, නැතහොත් ' +
    'නගරය හරහා පාර්සලයක් යවන්න. වෙන් කිරීමට පෙර ඔබට ගාස්තුව පෙනෙන අතර ඔබ ගෙවන්නේ ' +
    'රියදුරාට කෙළින්මය. ගමනකට ගාස්තුවක් ඇති එකම ක්‍රමය මෙයයි — එයින් MageRide කිසිවක් ' +
    'ගන්නේ නැත.',

  // --- how it works · passenger --------------------------------------------
  'www.how.p1.title': 'යෙදුම ලබා ගන්න',
  'www.how.p1.body':
    'MageRide ස්ථාපනය කරන්න, ඔබේ භාෂාව තෝරන්න, ඔබේ දුරකථන අංකයෙන් සහ එක් වරක් කේතයකින් ' +
    'පිවිසෙන්න.',
  'www.how.p2.title': 'සිතියම විවෘත කරන්න',
  'www.how.p2.body':
    'ඔබ අසල ගමන් කරන දේ බලන්න — බස්, දුම්රිය සහ ඔබට අනුගමනය කිරීමට අවසර ඇති පෞද්ගලික ' +
    'වාහන.',
  'www.how.p3.title': 'ගමනක් වෙන් කරන්න',
  'www.how.p3.body':
    'ඔබ යන්නේ කොහේදැයි කියන්න, වාහනයක් තෝරන්න, කිසිවක් තහවුරු කිරීමට පෙර ගාස්තුව බලන්න.',
  'www.how.p4.title': 'රියදුරාට ගෙවන්න',
  'www.how.p4.body':
    'මුදල්, නැතහොත් ගමන අවසානයේ රියදුරාගේ QR කේතය පරිලෝකනය කිරීම. මුදල් යන්නේ අප හරහා ' +
    'නොව රියදුරාටය.',

  // --- how it works · driver -----------------------------------------------
  'www.how.d1.title': 'ඔබේ වාහනය ලියාපදිංචි කරන්න',
  'www.how.d1.body':
    'පියවර හතරක්: වාහනය, රක්ෂණය, ආදායම් බලපත්‍රය සහ ඡායාරූප. ඔබ එක් එක් ලේඛනය යෙදුමෙන්ම ' +
    'ඡායාරූප ගන්නවා.',
  'www.how.d2.title': 'අනුමැතිය ලබා ගන්න',
  'www.how.d2.body':
    'බොහෝ විස්තර ස්වයංක්‍රීයව කියවා ගැනේ. පැහැදිලි නැති ඕනෑම දෙයක් පරීක්ෂා කිරීමට ' +
    'පුද්ගලයෙකු වෙත යන අතර, ප්‍රගතිය සිදු වන විටම ඔබට එය අනුගමනය කළ හැක.',
  'www.how.d3.title': 'සබැඳි වන්න',
  'www.how.d3.body':
    'වැඩ අවශ්‍ය විට සක්‍රීය කරන්න. ගෙදර යමින් නම් සහ මගදී රැකියා අවශ්‍ය නම් දිශාවක් ' +
    'සකසන්න.',
  'www.how.d4.title': 'රැකියාවක් පිළිගන්න',
  'www.how.d4.body':
    'ගමනක් ගැනීමට ඔබට තත්පර පහළොවක් ලැබේ. මුළු ගාස්තුවම තබා ගන්න; දෛනික ගාස්තුව අඩු ' +
    'වන්නේ ඔබේ දෙවන ගමනේ සිටය.',

  // --- feature splits -------------------------------------------------------
  'www.feature.liveMap.headline': 'එක් සිතියමක්, වාහන වර්ග දහයක්',
  'www.feature.liveMap.body':
    'බස්, දුම්රිය, ත්‍රීරෝද, යතුරුපැදි, මෝටර් රථ, වෑන්, මිනි වෑන්, ට්‍රක් සහ මිනි ට්‍රක් ' +
    'රථ එකින් එකට තමන්ගේම වර්ණයක් ලබා ගනී, එබැවින් එක බැල්මකින් එන්නේ කුමක්දැයි ඔබට ' +
    'කියවේ. සිතියම කාර්යබහුල වූ විට ක්‍රමය අනුව හෝ වාහන වර්ගය අනුව පෙරහන් කරන්න. ඔබ ' +
    'බසයක් බලමින් සිටියත්, වෙන් කළ ගමනක් එනතුරු බලා සිටියත් එකම සිතියමයි.',
  'www.feature.upfrontFare.headline': 'ඔබට පෙනෙන ගාස්තුව ඔබ ගෙවන ගාස්තුවයි',
  'www.feature.upfrontFare.body':
    'ඔබේ වාහනය තෝරන්න, වෙන් කිරීමට පෙර MageRide මිල පෙන්වයි. ඔබ බලා සිටින අතරතුර එය ' +
    'ඉහළ යන්නේ නැත, වැසි නිසා එය ඉහළ නංවන්නේ නැත, අවසානයේ කිසිවක් එකතු කරන්නේද නැත. ' +
    'රියදුරාට ලැබෙන්නේ හරියටම එම මුදලයි — එයින් MageRide කිසිවක් ගන්නේ නැත.',
  'www.feature.packages.headline': 'නගරය හරහා පාර්සලයක් යවන්න',
  'www.feature.packages.body':
    'ප්‍රමාණයක් තෝරන්න, ලබන්නා නම් කරන්න, රියදුරෙක් එය රැගෙන යයි. අදියර තුනම — ' +
    'බාරගත්තා, මගදී, බාරදුන්නා — කේතයකින් තහවුරු කෙරෙන අතර, එය ලබන පුද්ගලයාට කිසිවක් ' +
    'ස්ථාපනය නොකර හෝ ගිණුමක් නොසාදා බ්‍රවුසරයෙන් ගමන අනුගමනය කළ හැක.',
  'www.feature.safety.headline': 'උදව් එක් එබීමකින්',
  'www.feature.safety.body':
    'ඔබ විශ්වාස කරන කෙනෙකු සමඟ ඔබේ ගමන බෙදාගන්න, යෙදුම තුළින්ම රියදුරාට අමතන්න, ගමන් ' +
    'තිරයෙන් හදිසි උදව් ලබා ගන්න. සෑම ගමනක්ම, බලපත්‍රය සහ වාහන ලේඛන පරීක්ෂා කර ඇති ' +
    'රියදුරෙකුට එරෙහිව සටහන් වේ.',
  'www.feature.trilingual.headline': 'ආරම්භයේ සිටම ඔබේ භාෂාවෙන්',
  'www.feature.trilingual.body':
    'සිංහල, දෙමළ සහ ඉංග්‍රීසි යන තුනම ප්‍රථම පෙළේය. පිවිසීමටත් පෙර ඔබේ භාෂාව තෝරන්න, ' +
    'ඔබ කැමති විටෙක එය වෙනස් කරන්න, සෑම තිරයක්ම ඒ අනුව යයි. අඩක් පරිවර්තනය කළ මෙනු ' +
    'නැත, වැදගත්ම තිරයේ ඉංග්‍රීසියට වැටීමක්ද නැත.',

  // --- stats ----------------------------------------------------------------
  'www.stats.vehicleTypes': 'වාහන වර්ග',
  'www.stats.languages': 'භාෂා',
  'www.stats.commission': 'ඔබේ ගාස්තුවෙන් කොමිස්',
  'www.stats.firstTripFree': 'රියදුරන්ට, දිනපතා නොමිලේ ගමනක්',
  // A symbol, not prose. Identical in all three tables on purpose — see the
  // note beside the allow-list in `test/i18n.test.ts`.
  'www.stats.percentSuffix': '%',

  // --- the daily fee band ---------------------------------------------------
  // The six names are the driver app's own `vehicle_type_*` strings, because the
  // person reading this table is about to see them on that app's wallet screen.
  'www.fees.tier.motorbike': 'යතුරුපැදිය',
  'www.fees.tier.threeWheeler': 'ත්‍රීරෝද රථය',
  'www.fees.tier.flex': 'ෆ්ලෙක්ස්',
  'www.fees.tier.sedan': 'සෙඩාන් රථය',
  'www.fees.tier.miniVan': 'මිනි වෑන් රථය',
  'www.fees.tier.van': 'වෑන් රථය',
  'www.fees.modeA':
    'මහජන බස් සහ දුම්රිය කිසිසේත් කිසිවක් ගෙවන්නේ නැත. A ක්‍රමය ධාවනය කිරීමට නොමිලේ, ' +
    'බැලීමටද නොමිලේ.',
  // "approximately" is load-bearing in the URD and stays load-bearing here:
  // 'පමණ' is doing the same work as "around" and may not be dropped for rhythm.
  'www.fees.modeB':
    'පෞද්ගලික වාහන දෛනික ගාස්තුවක් වෙනුවට මාසික ගාස්තුවක් ගෙවයි — දැනට වාහනයකට රු. 300ක් ' +
    'පමණ, පළමු මාසය නොමිලේ.',

  // --- the language band ----------------------------------------------------
  // NOT a translation lookup. These three are the same three strings in all three
  // tables, each rendered inside its own `lang` block, because the point is that
  // the app speaks all three — which a reader who only ever sees their own
  // language cannot otherwise see. They are identical to `en.ts` by design.
  'www.languageBand.si': 'ශ්‍රී ලංකාව ගමන් කරන ආකාරය, එක් සජීවී සිතියමක.',
  'www.languageBand.ta': 'இலங்கை பயணிக்கும் விதம், ஒரே நேரடி வரைபடத்தில்.',
  'www.languageBand.en': 'How Sri Lanka moves, on one live map.',

  // --- footer ---------------------------------------------------------------
  'www.footer.explore': 'ගවේෂණය',
  'www.footer.support': 'සහාය',
  'www.footer.legal': 'නීතිමය',
  // A symbol and a brand name. Identical in all three tables — allow-listed.
  'www.footer.rights': '© MageRide',
  'www.footer.madeIn': 'ශ්‍රී ලංකාවේ ගොඩනැගුණි, ශ්‍රී ලංකාව සඳහා.',

  // --- FAQ ------------------------------------------------------------------
  'www.faq.passengerCost.q': 'මගියෙකු ලෙස MageRide මට කොපමණ වැය වේද?',
  'www.faq.passengerCost.a':
    'කිසිවක් නැත. දායකත්වයක් නැත, උසස් ස්තරයක් නැත, වෙන් කිරීමේ ගාස්තුවක්ද නැත. වෙන් ' +
    'කිරීමට පෙර ඔබට පෙනුණු ගාස්තුව රියදුරාට ගෙවන අතර, MageRide හට ඔබ කිසිසේත් කිසිවක් ' +
    'ගෙවන්නේ නැත.',
  'www.faq.whyFree.q':
    'මගීන් කිසිවක් නොගෙවා රියදුරන් මුළු ගාස්තුවම තබා ගන්නවා නම්, MageRide උපයන්නේ ' +
    'කෙසේද?',
  'www.faq.whyFree.a':
    'ඉල්ලුම මත ගමන් ගන්නා රියදුරන් ස්ථාවර දෛනික වේදිකා ගාස්තුවක් ගෙවයි — නමුත් එදින ' +
    'ඔවුන්ගේ දෙවන ගමනේ සිට පමණි, රිය නොපදවන දිනවල කිසිවක් නැත. එම ගාස්තුව මුළු ' +
    'ව්‍යාපාරික ආකෘතියම වේ. කිසිදු ගාස්තුවකින් කොමිසයක් නැත, මගීන්ට අයකිරීමක්ද නැත.',
  'www.faq.driverKeeps.q': 'ගාස්තුවෙන් කොපමණක් රියදුරෙකුට ලැබේද?',
  'www.faq.driverKeeps.a':
    'මුළු ගාස්තුවම. MageRide කොමිස් ගන්නේ නැත. වෙන් කිරීමට පෙර මගියාට පෙන්වූ ගාස්තුව ' +
    'හරියටම රියදුරාට ලැබෙන මුදලයි. කාඩ්පත් ගෙවීම්වලදී ගෙවීම් ද්වාරය කෙළින්ම රියදුරාගේම ' +
    'ගිණුමට බැර කරයි — ගමන් මුදල් MageRide කිසිවිටෙක තමන් සතුව තබා ගන්නේ නැත.',
  'www.faq.dailyFee.q': 'දෛනික වේදිකා ගාස්තුව යනු කුමක්ද?',
  'www.faq.dailyFee.a':
    'ඉල්ලුම මත රියදුරෙකු තම දෙවන ගමනේ සිට ගෙවන, වාහන වර්ගය අනුව සකසන ලද ස්ථාවර දෛනික ' +
    'මුදලකි. එය ගෙවූ පසු එදිනේ ඉතිරි ගමන් අසීමිත වන අතර MageRide තවත් කිසිවක් ගන්නේ ' +
    'නැත. වත්මන් අනුපාත රියදුරන්ගේ පිටුවේ දක්වා ඇත.',
  'www.faq.feeOffDays.q': 'රියදුරන් වැඩ නොකරන දිනවල ගෙවනවාද?',
  'www.faq.feeOffDays.a':
    'නැත. ගාස්තුව අය කරන්නේ රියදුරෙකු දෙවන ගමන ගන්නා දිනක පමණි. ගමන් නැත, ගාස්තුවක් ' +
    'නැත. ගමනක් නම්, ගාස්තුවක් නැත.',
  'www.faq.howToPay.q': 'ගමනකට මම ගෙවන්නේ කෙසේද?',
  'www.faq.howToPay.a':
    'මුදලින්, නැතහොත් ගමන අවසානයේ රියදුරාගේ QR කේතය පරිලෝකනය කිරීමෙන්. ඔබ තෝරන්නේ වෙන් ' +
    'කිරීමට පෙරය. කුමන ආකාරයෙන් වුවත් මුදල් යන්නේ MageRide හට නොව රියදුරාටය.',
  'www.faq.walletTopUp.q': 'රියදුරෙක් තම පසුම්බිය පුරවන්නේ කෙසේද?',
  'www.faq.walletTopUp.a':
    'රියදුරු යෙදුම තුළදීම, ණය හෝ හර කාඩ්පතක්, OnePay හෝ LankaQR භාවිතයෙන්. කිසිම දෙයකට ' +
    'රියදුරන්ට වෙබ් ද්වාරයක් විවෘත කිරීමට අවශ්‍ය නැත.',

  'www.faq.coverage.q': 'මට සෑම බසයක්ම සහ සෑම ත්‍රීරෝද රථයක්ම පෙනේවිද?',
  'www.faq.coverage.a':
    'පළමු දිනයේ නොවේ. මුළු රටම දරා ගැනීමට MageRide ගොඩනගා ඇත, නමුත් වාහනයක් දිස් වන්නේ ' +
    'එහි මෙහෙයුම්කරු හෝ රියදුරු එක් වූ පසුව පමණි. ආවරණය ක්‍රමයෙන් වර්ධනය වන අතර, ' +
    'ටික කලක් සමහර ස්ථානවල එය අනෙක් තැන්වලට වඩා තුනී වනු ඇත. සිතියම වෙනත් දෙයක් ' +
    'ඇඟවීමට ඉඩ දෙනවාට වඩා අපි ඒ බව පැහැදිලිව කීම කැමැත්තෙමු.',
  'www.faq.vehicleTypes.q': 'MageRide හි ඇත්තේ කුමන වර්ගයේ වාහනද?',
  'www.faq.vehicleTypes.a':
    'දහයක්. ගමන් සඳහා යතුරුපැදිය, ත්‍රීරෝද රථය, ෆ්ලෙක්ස්, සෙඩාන් රථය, මිනි වෑන් රථය සහ ' +
    'වෑන් රථය; බෙදාහැරීම් සඳහා ඊට අමතරව ට්‍රක් සහ මිනි ට්‍රක් රථ; මහජන ප්‍රවාහනය සඳහා ' +
    'බස් සහ දුම්රිය. සිතියමේ එක් එකකට තමන්ගේම වර්ණයක් ඇත.',
  'www.faq.modes.q': 'A ක්‍රමය, B ක්‍රමය සහ C ක්‍රමය යනු මොනවාද?',
  'www.faq.modes.a':
    'ඔබට කළ හැකි වෙනස් දේවල් තුනකි. A ක්‍රමය යනු ඕනෑම කෙනෙකුට නොමිලේ බැලිය හැකි මහජන ' +
    'බස් සහ දුම්රියයි. B ක්‍රමය යනු අනුගමනය කිරීමට ඔබට අවසර දී ඇති පෞද්ගලික වාහනයකි. ' +
    'C ක්‍රමය යනු දැන්ම ගමනක් හෝ බෙදාහැරීමක් වෙන් කිරීමයි. ඒවා වෙන් වෙන් සේවා මිස ' +
    'ස්විචයක් සහිත එකම විශේෂාංගයක් නොවේ.',
  'www.faq.modeBAccess.q': 'මගේ පෞද්ගලික වාහනය ඕනෑම කෙනෙකුට බැලිය හැකිද?',
  'www.faq.modeBAccess.a':
    'නැත. B ක්‍රමයේ වාහනයක් පෙනෙන්නේ එහි හිමිකරු එකින් එක ඉල්ලීම් අනුව අවසර දී ඇති ' +
    'අයට පමණි, එම අවසරය ඔවුන් කැමති විටෙක ඉවත් කර ගත හැක.',
  'www.faq.modeBPrice.q': 'පෞද්ගලික වාහනයක් අනුගමනය කිරීමට කොපමණ වැය වේද?',
  'www.faq.modeBPrice.a':
    'එය වාහනය අනුව වෙනස් වේ. සමහර හිමිකරුවන් නොමිලේ අවසර දෙයි — උදාහරණයක් ලෙස ' +
    'සමාගමක කාර්ය මණ්ඩල බසයක්. අනෙක් ඒවාට මාසික දායකත්වයක් ඇත, දැනට වාහනයකට රු. 300ක් ' +
    'පමණ, පළමු මාසය නොමිලේ. ඔබ දායක වීමට පෙර යෙදුම නිශ්චිත මුදල පෙන්වයි.',
  'www.faq.trains.q': 'දුම්රිය සිතියමේ තිබේද?',
  'www.faq.trains.a':
    'ඔව්, මහජන බස් සමඟම A ක්‍රමය ලෙස. දුම්රිය ලියාපදිංචි කරන්නේ රියදුරන් නොව MageRide ' +
    'පරිපාලකයන්ය. ඒවා වෙන් වශයෙන් පෙරහන් කළ හැකි අතර, ගමනාන්තයක් ඇතුළත් කළ විට ඔබේ ' +
    'විකල්ප අතරින්ද ඒවා දැකිය හැක.',

  'www.faq.signup.q': 'ලියාපදිංචි වීමට මට අවශ්‍ය කුමක්ද?',
  'www.faq.signup.a':
    'ශ්‍රී ලාංකික ජංගම දුරකථන අංකයක්. ඔබේ භාෂාව තෝරන්න, ඔබේ අංකය ඇතුළත් කරන්න, එක් ' +
    'වරක් කේතය තහවුරු කරන්න. මගීන්ට වෙන කිසිවක් අවශ්‍ය නැත.',
  'www.faq.becomeADriver.q': 'MageRide සමඟ රිය පැදවීම ආරම්භ කරන්නේ කෙසේද?',
  'www.faq.becomeADriver.a':
    'රියදුරු යෙදුම ස්ථාපනය කරන්න, ඔබේ දුරකථන අංකයෙන් පිවිසෙන්න, පියවර හතරකින් ඔබේ ' +
    'වාහනය ලියාපදිංචි කරන්න: වාහනය, රක්ෂණය, ආදායම් බලපත්‍රය සහ ඡායාරූප. ඔබ එක් එක් ' +
    'ලේඛනය යෙදුමෙන්ම ඡායාරූප ගන්නවා — බොහෝ විස්තර ස්වයංක්‍රීයව කියවා ගන්නා අතර, ' +
    'පැහැදිලි නැති ඕනෑම දෙයක් අනුමැතියට පෙර පුද්ගලයෙකු විසින් පරීක්ෂා කරයි.',
  'www.faq.languages.q': 'MageRide සහාය දක්වන භාෂා මොනවාද?',
  'www.faq.languages.a':
    'සිංහල, දෙමළ සහ ඉංග්‍රීසි, සෑම තැනකම. පිවිසීමට පෙර එකක් තෝරන අතර ඕනෑම වේලාවක එය ' +
    'වෙනස් කළ හැක.',

  // S18 · URD Epic 19. TalkBack is named because US-19.1 names it; no VoiceOver
  // claim, because the URD makes none.
  'www.faq.accessibility.q': 'තිර කියවනයක් සමඟ, හෝ විශාල අකුරු සමඟ MageRide ක්‍රියා කරයිද?',
  'www.faq.accessibility.a':
    'ඔව්. වැදගත්ම කාර්යයන් — ලියාපදිංචි වීම, සිතියම, ගමනක් වෙන් කිරීම සහ ගමන් සාරාංශය — ' +
    'Android හි ඇතුළත් තිර කියවනය වන TalkBack සමඟ ක්‍රියා කරයි. ඔබ ඔබේ දුරකථනයේ ' +
    'සැකසුම්වලින් අකුරු විශාල කර ඇත්නම්, යෙදුම එම සැකසුම අනුගමනය කරයි: වචන කපා ' +
    'දැමීමට වඩා පිරිසැලසුම එයට අනුව විශාල වේ.',

  // `/faq`'s first group heading. The other two are `www.nav.passengers` and
  // `www.nav.drivers`, so the page and the menu use the same words.
  'www.faq.group.everyone': 'හැමෝම අසන ප්‍රශ්න',

  'www.faq.safety.q': 'ආරක්ෂක විශේෂාංග මොනවාද?',
  'www.faq.safety.a':
    'ඔබ විශ්වාස කරන කෙනෙකු සමඟ ඔබේ සජීවී ගමන බෙදාගන්න, ගමන් තිරය තුළින්ම හදිසි උදව් ' +
    'ලබා ගන්න, පසුව රියදුරු ශ්‍රේණිගත කරන්න. රැකියාවක් පිළිගැනීමට පෙර සෑම රියදුරෙකුගේම ' +
    'බලපත්‍රය සහ වාහන ලේඛන පරීක්ෂා කෙරේ.',
  'www.faq.phoneNumber.q': 'රියදුරාට මගේ දුරකථන අංකය පෙනේද?',
  'www.faq.phoneNumber.a':
    'රියදුරෙක් ඔබේ ගමන පිළිගත් පසු, බාරගැනීම සම්බන්ධීකරණය කර ගැනීමට ඔබටත් රියදුරාටත් ' +
    'එකිනෙකාගේ අංක පෙනේ. ලියාපදිංචි වන විට මෙය ඔබට හෙළි කරනු ලැබේ. ඔබ වෙනත් අයෙකු ' +
    'වෙනුවෙන් වෙන් කරන්නේ නම්, රියදුරාට පෙනෙන්නේ ගමන් කරන්නාගේ අංකය මිස කිසිවිටෙක ' +
    'ඔබේ අංකය නොවේ.',
  'www.faq.myData.q': 'මගේ දත්ත ලබා ගත හැකිද, නැතහොත් මකා දැමිය හැකිද?',
  'www.faq.myData.a':
    'දෙකම. ඔබ ගැන MageRide සතුව ඇති සියල්ලේ පිටපතක් ඉල්ලා දින 30ක් ඇතුළත එය ලබා ගත ' +
    'හැක, ඔබේ ගිණුම සහ පෞද්ගලික දත්ත මකා දමන ලෙසත් ඉල්ලිය හැක — එයද දින 30ක් ඇතුළත, ' +
    'තබා ගැනීමට නීතියෙන් නියම කර ඇති වාර්තා හැර.',
  'www.faq.maps.q': 'MageRide භාවිත කරන්නේ කාගේ සිතියම්ද?',
  'www.faq.maps.a':
    'MageRide හි තමන්ගේම සිතියම් සහ සෙවුම් සේවාදායකවලින් ලබා දෙන OpenStreetMap දත්ත. ' +
    'වාණිජ සිතියම් බලපත්‍රයක් නැත, පරිශීලකයෙකුට ගාස්තුවක්ද නැත, එය එම වියදම රියදුරන්ගෙන් ' +
    'ඉවත් කරයි.',

  // =========================================================================
  // Page-level copy — the headers, intros and section furniture S14–S18 render.
  // =========================================================================

  // --- home -----------------------------------------------------------------
  'www.home.modes.heading': 'MageRide භාවිත කිරීමට ක්‍රම තුනක්',
  'www.home.modes.intro':
    'ඔබට බැලිය හැකි මහජන ප්‍රවාහනය, අනුගමනය කිරීමට අවසර ඇති පෞද්ගලික වාහන, සහ ඉල්ලුම ' +
    'මත ගමන් හා බෙදාහැරීම්. ඒවා වෙන් වෙන් සේවා — ඔබ ඉන් එකක් පමණක් භාවිත කර අනෙක් ඒවා ' +
    'කිසිදා භාවිත නොකළ හැක.',
  'www.home.how.heading': 'ක්‍රියා කරන ආකාරය',
  'www.home.how.passengerTab': 'මගීන් සඳහා',
  'www.home.how.driverTab': 'රියදුරන් සඳහා',
  'www.home.values.heading': 'අපි නොකරන දේ',
  'www.home.values.intro':
    'MageRide වෙනස් කරන දෙයින් බොහොමයක් යනු එය ඔබෙන් අය නොකරන දේවල ලැයිස්තුවකි.',
  'www.home.screens.heading': 'එය පෙනෙන ආකාරය',
  'www.home.faq.heading': 'මිනිසුන් මුලින්ම අසන ප්‍රශ්න',
  'www.home.faq.more': 'සියලු ප්‍රශ්න බලන්න',

  // --- /vision --------------------------------------------------------------
  'www.page.vision.title': 'අපගේ දැක්ම',
  'www.page.vision.intro':
    'MageRide පවතින්නේ ඇයි, එය වීමට උත්සාහ කරන්නේ කුමක්ද, සහ තවම එය නොවන්නේ කුමක්ද.',
  'www.page.vision.missionHeading': 'අපගේ මෙහෙවර',
  'www.page.vision.valuesHeading': 'ප්‍රායෝගිකව එයින් අදහස් වන්නේ',

  // --- /passengers ----------------------------------------------------------
  // Mirrors `www.nav.passengers` word for word: the nav label and the page
  // heading may not drift into saying different things about the same page.
  'www.page.passengers.title': 'මගීන් සඳහා',
  'www.page.passengers.intro':
    'ගමන් කරන දේ ලුහුබඳින්න, ඔබ එකඟ වූ මිලකට ගමනක් වෙන් කරන්න, නගරය හරහා පාර්සලයක් ' +
    'යවන්න. MageRide ඔබෙන් කිසිවක් අය නොකරයි.',
  'www.page.passengers.trackHeading': 'එන දේ බලන්න',
  'www.page.passengers.trackBody':
    'සිතියම විවෘත කර බස් සහ දුම්රිය ඔවුන්ගේ මාර්ග ඔස්සේ ගමන් කරනු බලන්න. පෞද්ගලික ' +
    'වාහනයකට — පාසල් වෑන් රථයකට, කාර්ය මණ්ඩල බසයකට — කවුරුන් හෝ ඔබට අවසර දී ඇත්නම් එයද ' +
    'එහි දිස් වේ. බොහෝ දේ සිදු වන විට ක්‍රමය හෝ වාහන වර්ගය අනුව පෙරහන් කරන්න.',
  'www.page.passengers.bookHeading': 'ඔබ එකඟ වූ මිලකට වෙන් කරන්න',
  'www.page.passengers.bookBody':
    'ඔබ යන්නේ කොහේදැයි ඇතුළත් කරන්න, වාහන වර්ගයක් තෝරන්න, ඔබ බැඳීමට පෙර MageRide ' +
    'ගාස්තුව පෙන්වයි. රියදුරාට මුදලින් හෝ ඔවුන්ගේ කේතය පරිලෝකනය කර ගෙවන්න. පසුව කිසිවක් ' +
    'එකතු කරන්නේ නැත, එයින් කිසිදු කොටසක් අප වෙත එන්නේද නැත.',
  'www.page.passengers.sendHeading': 'නගරය හරහා යමක් යවන්න',
  'www.page.passengers.sendBody':
    'ප්‍රමාණයක් තෝරන්න, එය ලබන්නේ කවුරුන්දැයි නම් කරන්න, රියදුරෙක් එය රැගෙන යයි. සෑම ' +
    'අදියරක්ම කේතයකින් තහවුරු කෙරෙන අතර, ලබන්නාට යෙදුම ස්ථාපනය නොකර බ්‍රවුසරයෙන් ගමන ' +
    'අනුගමනය කළ හැක.',
  'www.page.passengers.costHeading': 'ඔබට වැය වන්නේ',
  'www.page.passengers.costBody':
    'ගමන් සහ බෙදාහැරීම් සඳහා, ගාස්තුව — රියදුරාට ගෙවනු ලැබේ. මහජන ප්‍රවාහනය සඳහා, ' +
    'කිසිවක් නැත. පෞද්ගලික වාහනයක් සඳහා, එහි හිමිකරු සකසා ඇති දේ, ඔබ දායක වීමට පෙර ' +
    'පෙන්වනු ලැබේ. MageRide මගීන්ගෙන් කිසිසේත් කිසිවක් අය නොකරයි.',
  'www.page.passengers.guideCta': 'සම්පූර්ණ මගී මාර්ගෝපදේශය කියවන්න',

  // --- /drivers -------------------------------------------------------------
  'www.page.drivers.title': 'රියදුරන් සඳහා',
  'www.page.drivers.intro':
    'සෑම ගාස්තුවකම සෑම රුපියලක්ම තබා ගන්න. ඔබේ දෙවන ගමනේ සිට ස්ථාවර දෛනික ගාස්තුවක් ' +
    'ගෙවන්න, රිය නොපදවන දිනවල කිසිවක් නැත.',
  'www.page.drivers.earnHeading': 'ඔබට ඉතිරි වන දේ',
  'www.page.drivers.earnBody':
    'මුළු ගාස්තුවම. කිසිදු ගාස්තුවකින් කොමිසයක් නැත, ගමනකින් කැපීමක් නැත, ඉහළින් ගන්නා ' +
    'සේවා ගාස්තුවක්ද නැත. මගියා එකඟ වූ අංකයම ඔබට ලැබෙන අංකයයි.',
  'www.page.drivers.feeHeading': 'ඔබ ගෙවන දේ',
  'www.page.drivers.feeBody':
    'දිනකට එක් ස්ථාවර වේදිකා ගාස්තුවක්, එයද ඔබ දෙවන ගමන ගන්නා දිනක පමණි. එදිනට ඔබේ ' +
    'පළමු ගමන සැමවිටම නොමිලේ වන අතර, ගාස්තුව ගෙවූ පසු එදිනේ ඉතිරිය අසීමිතයි. අනුපාතය ' +
    'ඔබ පදවන්නේ කුමක්ද යන්න මත රඳා පවතී.',
  'www.page.drivers.feeTableHeading': 'දෛනික ගාස්තුව, වාහනය අනුව',
  'www.page.drivers.feeTableNote':
    'අනුපාත MageRide විසින් සමාලෝචනය කරන අතර වෙනස් විය හැක; යෙදුම සැමවිටම වත්මන් ' +
    'අනුපාතය පෙන්වයි.',
  'www.page.drivers.startHeading': 'ආරම්භ කිරීම',
  'www.page.drivers.startBody':
    'යෙදුමෙන්ම ඔබේ වාහනය ලියාපදිංචි කරන්න: වාහනය, රක්ෂණය, ආදායම් බලපත්‍රය සහ ඡායාරූප. ' +
    'එක් එක් ලේඛනයෙන් බොහෝ දේ ස්වයංක්‍රීයව කියවා ගන්නා අතර, පැහැදිලි නැති ඕනෑම දෙයක් ' +
    'පුද්ගලයෙකු පරීක්ෂා කරයි, ඔබ සිටින්නේ කුමන පියවරේදැයි හරියටම දැකිය හැක.',
  'www.page.drivers.directionalHeading': 'ගෙදර යනවාද? ඒ බව කියන්න',
  'www.page.drivers.directionalBody':
    'මුරයක් අවසානයේ දිශාවක් සකසන්න, එවිට ඔබට ලැබෙන්නේ එම දිශාවට යන රැකියා පමණි — ' +
    'සීමිත කාලයකට සහ දිනකට සීමිත වාර ගණනකට. දිනේ අවසන් ගමන ඔබව ගෙදරින් තව දුරට ඈත් ' +
    'නොකරන පිණිස එය එහි ඇත.',
  'www.page.drivers.guideCta': 'සම්පූර්ණ රියදුරු මාර්ගෝපදේශය කියවන්න',

  // The URD §1 quotation (S16). A translation of the *quoted words*, not of a
  // summary: 'සැමවිටම' carries "always", 'දෙවන ගමනේ සිට' carries "from the 2nd
  // trip", and 'ස්වයංක්‍රීයව අඩු කෙරේ' carries "auto-deducted". The six rates are
  // elided here as they are in the English, and render from the table beneath.
  'www.page.drivers.freeFirstTripQuote':
    'C ක්‍රමයේ (පොරොත්තු, ඉල්ලුම මත) රියදුරන්ට, දිනේ පළමු ගමන සැමවිටම නොමිලේය; ' +
    'දෙවන ගමනේ සිට, ස්ථාවර දෛනික වේදිකා ගාස්තුවක් (වාහන වර්ගය අනුව…) ඔවුන්ගේ ' +
    'පසුම්බියෙන් ස්වයංක්‍රීයව අඩු කෙරේ.',

  'www.page.fleets.guideCta': 'රථ සමූහ හිමිකරු මාර්ගෝපදේශය කියවන්න',

  // The guide (S17).
  'www.guide.stepCount': 'පියවර {count}ක්',
  'www.guide.chapterNumber': '{number} වන පරිච්ඡේදය',
  'www.guide.rail.label': 'මෙම මාර්ගෝපදේශයේ පරිච්ඡේද',
  'www.guide.rail.heading': 'මෙම මාර්ගෝපදේශයේ',
  'www.guide.toc.label': 'මෙම පරිච්ඡේදයේ පියවර',
  'www.guide.stepLabel': '{number} වන පියවර',
  'www.guide.related': 'ඊළඟට කියවන්න',
  'www.guide.questions': 'මේ ගැන ප්‍රශ්න',
  'www.guide.pager.label': 'පරිච්ඡේද සංචලනය',
  'www.guide.backToGuide': 'සියලු පරිච්ඡේද',

  // The four callout kinds, as text — WCAG 1.4.1. See the note in `en.ts`.
  'www.guide.callout.tip': 'ඉඟිය',
  'www.guide.callout.warning': 'පරෙස්සම්',
  'www.guide.callout.fee': 'මෙයට වැය වන දේ',
  'www.guide.callout.privacy': 'ඔබේ රහස්‍යතාව',

  // --- /fleets --------------------------------------------------------------
  // Mirrors `www.nav.fleets`, which S07 set to the shorter 'රථ හිමියන් සඳහා' for
  // nav width. Body prose below uses the platform's own term, වාහන සමූහය — the
  // divergence is recorded in `src/content/glossary.si.ts`.
  'www.page.fleets.title': 'රථ හිමියන් සඳහා',
  'www.page.fleets.intro':
    'පාසල් වෑන් සේවාවක්, කාර්ය මණ්ඩල ප්‍රවාහන මෙහෙයුමක් හෝ බස් මාර්ගයක් එකම තැනකින් ' +
    'ක්‍රියාත්මක කරන්න — වාහන, රියදුරන්, ට්‍රැකර් සහ බිල්පත්.',
  'www.page.fleets.manageHeading': 'ඔබේ මුළු වාහන සමූහයම එක් තිරයක',
  'www.page.fleets.manageBody':
    'වාහන එකින් එක හෝ තොග වශයෙන් එක් කරන්න, ඒවා පදවන රියදුරන් පවරන්න, GPS ට්‍රැකර් ' +
    'බැඳ තබන්න, සෑම වාහනයක්ම එකම සිතියමක සජීවීව බලන්න — ඔබේ ආයතනයට සීමා වූ පරිදි, ' +
    'වෙන කිසිවෙකුගේ නොවේ.',
  'www.page.fleets.accessHeading': 'බැලිය හැක්කේ කාටදැයි ඔබ තීරණය කරයි',
  'www.page.fleets.accessBody':
    'පෞද්ගලික වාහනයක් පෙනෙන්නේ ඔබ අනුමත කර ඇති අයට පමණි. ඉල්ලීම් එන්නේ ඔබ වෙතය, ඕනෑම ' +
    'වේලාවක ඔබට එම අවසරය ඉවත් කර ගත හැක.',
  'www.page.fleets.billingHeading': 'වාහන සමූහවලට බිල් කරන ආකාරය',
  'www.page.fleets.billingBody':
    'මහජන ප්‍රවාහන වාහන නොමිලේ. පෞද්ගලික වාහනවලට වාහනයකට මාසිකව බිල් කෙරේ. ඉල්ලුම මත ' +
    'රිය පැදවීම කිසිවිටෙක වාහන සමූහයකට බිල් නොකෙරේ — එම දෛනික ගාස්තුව එන්නේ එක් එක් ' +
    'රියදුරාගේම පසුම්බියෙනි.',
  'www.page.fleets.portalNote':
    'වාහන සමූහ හිමිකරුවන් වැඩ කරන්නේ fleet.mageride.lk හි වෙබ් ද්වාරයකය. රියදුරන්ට ' +
    'එය කිසිදා අවශ්‍ය නැත.',

  // --- /screens -------------------------------------------------------------
  'www.page.screens.title': 'තිරයෙන් තිරය',
  'www.page.screens.intro':
    'මගීන්ට, රියදුරන්ට සහ රථ හිමියන්ට MageRide ඇත්ත වශයෙන්ම පෙනෙන ආකාරය.',
  'www.page.screens.passengerHeading': 'මගී යෙදුම',
  'www.page.screens.driverHeading': 'රියදුරු යෙදුම',
  'www.page.screens.fleetHeading': 'රථ සමූහ ද්වාරය',
  'www.page.screens.webHeading': 'යෙදුමකින් තොරව ලුහුබැඳීම',

  // The gallery filter (S18). The chips themselves reuse the four headings above
  // and `www.modes.*.name`, so only the furniture is new.
  'www.screens.filter.legend': 'මේවා පටු කරන්න',
  'www.screens.filter.surface': 'යෙදුම',
  'www.screens.filter.mode': 'සේවාව',
  'www.screens.filter.chapter': 'මාර්ගෝපදේශ පරිච්ඡේදය',
  'www.screens.filter.showing': 'තිර {total}කින් {count}ක් පෙන්වයි',
  'www.screens.filter.clear': 'සියලු තිර පෙන්වන්න',
  'www.screens.empty': 'එම තුනටම ගැළපෙන තිරයක් නැත.',
  'www.screens.tile.inGuide': 'මාර්ගෝපදේශයේ:',

  // --- /guide ---------------------------------------------------------------
  'www.page.guide.title': 'MageRide භාවිත කරන ආකාරය',
  'www.page.guide.intro':
    'යෙදුම ස්ථාපනය කිරීමේ සිට ගෙවීම් ලැබීම දක්වා, මගීන් සහ රියදුරන් සඳහා පියවරෙන් ' +
    'පියවර මාර්ගෝපදේශ.',
  'www.page.guide.passengerHeading': 'මගී මාර්ගෝපදේශය',
  'www.page.guide.driverHeading': 'රියදුරු මාර්ගෝපදේශය',
  'www.page.guide.fleetHeading': 'රථ සමූහ හිමිකරු මාර්ගෝපදේශය',
  // The one placeholder in the S07–S11 corpus. `{count}` must survive verbatim.
  'www.page.guide.chapterCount': 'පරිච්ඡේද {count}ක්',
  'www.page.guide.readChapter': 'මෙම පරිච්ඡේදය කියවන්න',

  // --- /faq -----------------------------------------------------------------
  'www.page.faq.title': 'ප්‍රශ්න',
  'www.page.faq.intro':
    'ගමනක් හෝ ජීවනෝපායක් යෙදුමකට භාර දීමට පෙර මිනිසුන් අසන දේවල්. ඔබේ ප්‍රශ්නය මෙහි ' +
    'නැත්නම්, මාර්ගෝපදේශ වඩාත් විස්තරාත්මකව කරුණු දක්වයි.',

  // --- /download ------------------------------------------------------------
  'www.page.download.title': 'යෙදුම ලබා ගන්න',
  'www.page.download.intro':
    'MageRide ක්‍රියා කරන්නේ Android සහ iPhone මතය, සිංහල, දෙමළ සහ ඉංග්‍රීසි භාෂාවලින්.',
  'www.page.download.notYet': 'තවම වෙළඳසැල්වල නැත',
  'www.page.download.notYetBody':
    'MageRide තවම ප්‍රසිද්ධියේ එළිදක්වා නැත. යෙදුම් ප්‍රකාශයට පත් වූ විට සබැඳි මෙහි ' +
    'දිස් වනු ඇත — අද ස්ථාපනය කිරීමට කිසිවක් නැත, ලැයිස්තුවක් සඳහා ඔබේ විස්තර එකතු ' +
    'කරනවාට වඩා අපි ඒ බව කීම කැමැත්තෙමු.',
  'www.page.download.passengerApp': 'MageRide — මගී',
  'www.page.download.driverApp': 'MageRide — රියදුරු',

  // S18. Neither card needs a store URL, so both are publishable while D3 is open.
  'www.page.download.whichAppHeading': 'ඔබට අවශ්‍ය කුමන යෙදුමද?',
  'www.page.download.passengerAppBody':
    'ගමන් කිරීමට: බස් සහ දුම්රිය නරඹන්න, ඔබට ප්‍රවේශය ලබා දී ඇති වාහනයක් අනුගමනය ' +
    'කරන්න, ගමනක් වෙන් කරන්න, හෝ පාර්සලයක් යවන්න. බොහෝ දෙනාට අවශ්‍ය වන්නේ මෙයයි.',
  'www.page.download.driverAppBody':
    'උපයා ගැනීමට: ගමන් සහ බෙදාහැරීම් රැකියා ලබා ගන්න, නැතහොත් බස්, වෑන් හෝ පාසල් ' +
    'ගමනක මගීන් ප්‍රවාහනය කරන්න. ඔබේ වාහනය සහ ලේඛන ලියාපදිංචි කරන්නේ එය තුළය.',

  // URD NFR-22, cited on the page. No iOS minimum — no spec states one.
  'www.page.download.requirementsHeading': 'ඔබට අවශ්‍ය දේ',
  'www.page.download.androidMinimum':
    'Android මත, MageRide හට Android 8.0 හෝ ඊට නවතම අනුවාදයක් අවශ්‍යය. සිතියම සඳහා ' +
    'දත්ත සම්බන්ධතාවක් සහ ස්ථාන අවසරය අවශ්‍ය වේ; එක් එක් අවසරය කුමක් සඳහාදැයි යෙදුම ' +
    'ඉල්ලීමට පෙර ඔබට කියයි.',

  // --- /contact -------------------------------------------------------------
  'www.page.contact.title': 'සම්බන්ධ වන්න',
  'www.page.contact.intro':
    'MageRide හට ඇමතුම් මධ්‍යස්ථානයක් නැත. ගමනකට සම්බන්ධ ඕනෑම දෙයක් සඳහා සහාය ඇත්තේ ' +
    'යෙදුම තුළය, ඔබ අදහස් කරන ගමන අපට එහිදී දැකිය හැකි නිසාය.',
  'www.page.contact.inAppHeading': 'යෙදුම තුළ සහාය',
  'www.page.contact.inAppBody':
    'මෙනුවෙන් උදව් විවෘත කර ටිකට්පතක් යොදන්න. එය ඔබේ ගිණුමට සහ ඔබේ ගමන් ඉතිහාසයට ' +
    'අමුණා පැමිණේ, කවුරුන් හෝ එයට ඇත්තටම පිළිතුරු දිය හැක්කේ ඒ නිසාය.',
  'www.page.contact.questionsHeading': 'බොහෝ පිළිතුරු දැනටමත් ලියා ඇත',
  'www.page.contact.questionsBody':
    'ගාස්තු, දෛනික ගාස්තුව, ආවරණය, ආරක්ෂාව සහ ඔබේ දත්තවලට සිදු වන දේ — මේ සියල්ලට මෙහි ' +
    'පිළිතුරු ඇත. යෙදුම් කරන සියල්ල මාර්ගෝපදේශ පියවරෙන් පියවර පෙන්වයි.',
  'www.page.contact.emailHeading': 'අනෙක් සියල්ල',
  'www.page.contact.emailBody':
    'මාධ්‍ය, හවුල්කාරිත්ව, වාහන සමූහ විමසීම් සහ නිශ්චිත ගමනක් ගැන නොවන ඕනෑම දෙයක්.',
  // The sentence that stands in for the address MCS-34 D4 has not chosen.
  'www.page.contact.emailPending':
    'මේවා සඳහා ප්‍රසිද්ධ ලිපිනයක් තවම නැත. කිසිවෙකු නොකියවන ලිපිනයක් පළ කරනවාට වඩා අපි ' +
    'එය ඉවත් කර ඇත — තීරණය වූ විට එය මෙහි දිස් වනු ඇත.',

  // =========================================================================
  // The three legal documents (S18). MCS-34 D5 — counsel writes the text and no
  // session here writes any of it; what is below is the shell plus two factual
  // descriptions of software, each of which cites what it describes.
  // =========================================================================
  'www.legal.lastUpdatedLabel': 'අවසන් වරට යාවත්කාලීන කළේ',
  'www.legal.lastUpdatedNone': 'තවම ප්‍රකාශයට පත් කර නැත',
  'www.legal.status.heading': 'මෙම ලේඛනය සකස් වෙමින් පවතී',

  'www.legal.terms.intro': 'ඔබ MageRide භාවිත කරන විට එකඟ වන කොන්දේසි.',
  'www.legal.terms.status':
    'MageRide හි සේවා කොන්දේසි ලියමින් හා සමාලෝචනය වෙමින් පවතී. ඒවා තවම ප්‍රකාශයට පත් ' +
    'කර නැත, වෙනත් සමාගමක් විස්තර කරන ණයට ගත් වචන පෙන්වනවාට වඩා මෙහි කිසිවක් ' +
    'නොපෙන්වීම අපි කැමැත්තෙමු. යෙදුම් එළිදැක්වීමට පෙර ඒවා ප්‍රකාශයට පත් වන අතර, ඔබ ' +
    'ලියාපදිංචි වන විට ඒවාට එකඟ වන ලෙස ඔබෙන් අසනු ඇත.',

  'www.legal.privacy.intro':
    'ඔබ ගැන ඇති තොරතුරු සමඟ MageRide කරන දේ — සහ මෙම වෙබ් අඩවිය කරන දේ, එය බොහෝ දුරට ' +
    'කිසිවක් නොවේ.',
  'www.legal.privacy.status':
    'සම්පූර්ණ රහස්‍යතා ප්‍රතිපත්තිය ලියමින් හා සමාලෝචනය වෙමින් පවතී, එය තවම ප්‍රකාශයට ' +
    'පත් කර නැත. පහත කොටස් දෙක එම ප්‍රතිපත්තිය නොවේ: ඒවා මෙම වෙබ් අඩවිය සහ MageRide ' +
    'යෙදුම් අද හැසිරෙන ආකාරය ගැන සරල විස්තරයකි, ප්‍රතිපත්තිය පැමිණි විටත් ඒවා සත්‍ය ' +
    'වනු ඇත.',
  'www.legal.privacy.siteHeading': 'මෙම වෙබ් අඩවිය එකතු කරන දේ',
  'www.legal.privacy.siteBody':
    'කිසිවක් නැත. මෙම අඩවිය කුකී තබන්නේ නැත, විශ්ලේෂණ ධාවනය කරන්නේ නැත, කිසිම පිටුවක ' +
    'පෝරමයක් නැත, ඔබ මෙහි කරන කිසිවක් කිසිවෙකුට යවන්නේ නැත. එකඟතා දැන්වීමක් නැත්තේ ' +
    'එකඟ විය යුතු කිසිවක් නොමැති නිසාය.',
  'www.legal.privacy.siteLogs':
    'ඔබට මෙම පිටු ලබා දෙන සේවාදායකය සාමාන්‍ය වෙබ් සේවාදායක වාර්තා තබා ගනී — ඉල්ලූ ' +
    'ලිපිනය, වේලාව, සහ එය ඉල්ලූ අන්තර්ජාල ලිපිනය — සෑම වෙබ් සේවාදායකයක්ම කරන ' +
    'ආකාරයටමය. ඉන් ඔබ්බට කිසිවක් තබා නොගන්නා අතර, මෙහි කිසිවක් MageRide ගිණුමකට ' +
    'සම්බන්ධ නොකෙරේ.',
  'www.legal.privacy.siteTheme':
    'ඔබ මෙම අඩවිය ආලෝකමත් සහ අඳුරු අතර මාරු කළහොත්, ඊළඟ වතාවේ පිටුව වැරදි ආකාරයට ' +
    'දැල්වීම වළක්වා ගැනීමට එම තේරීම ඔබේම බ්‍රවුසරය මතක තබා ගනී. එය කිසිදා ඔබේ ' +
    'උපාංගයෙන් පිටතට නොයයි.',
  'www.legal.privacy.appsHeading': 'යෙදුම් වෙනම කරුණකි',
  'www.legal.privacy.appsBody':
    'මගී සහ රියදුරු යෙදුම් පෞද්ගලික තොරතුරු තබා ගනී — ඔබේ දුරකථන අංකය, ඔබේ ගමන්, සහ ' +
    'රියදුරන් සඳහා ඔබේ බලපත්‍රය සහ වාහන ලේඛන. ක්‍රියා කිරීමට ඒවාට එය අවශ්‍යය. තබා ' +
    'ගන්නේ කුමක්ද, කොපමණ කාලයක්ද, එය දැකිය හැක්කේ කාටද යන්න ඉහත ප්‍රතිපත්තිය පැහැදිලි ' +
    'කරනු ඇත; ඒ මත ඔබේ අයිතිවාසිකම් දත්ත අයිතිවාසිකම් පිටුවේ ඇත.',

  'www.legal.pdpa.intro':
    'ශ්‍රී ලංකාවේ පුද්ගල දත්ත ආරක්ෂණ පනත යටතේ, ඔබ ගැන MageRide සතුව ඇති තොරතුරු සමඟ ' +
    'කුමක් කරන ලෙස ඔබට ඉල්ලිය හැකිද යන්න.',
  'www.legal.pdpa.status':
    'විධිමත් දත්ත ආරක්ෂණ දැන්වීම ලියමින් හා සමාලෝචනය වෙමින් පවතී. එම දැන්වීම ප්‍රකාශයට ' +
    'පත් වීමෙන් පසුව පමණක් නොව ඊට පෙරත් මෙම පිටුව ප්‍රයෝජනවත් වන පරිදි, පහත කොටස් දෙක ' +
    'වේදිකාව දැනටමත් කරන දේ විස්තර කරයි.',
  'www.legal.pdpa.rightsHeading': 'ඔබේ දත්තවල පිටපතක්, නැතහොත් ඒවා මකා දැමීම',
  'www.legal.pdpa.rightsBody':
    'ඔබ ගැන MageRide සතුව ඇති සියල්ලේ පිටපතක් ඉල්ලිය හැක, ඔබේ ගිණුම සහ පෞද්ගලික දත්ත ' +
    'මකා දමන ලෙසත් ඉල්ලිය හැක. ඉල්ලීමක් කළ දිනයේ සිට දින 30ක් ඇතුළත එය ඉටු කළ යුතු ' +
    'අතර, MageRide එය ඉලක්කයක් ලෙස නොව නියමිත කාලසීමාවක් ලෙස සලකා නිරීක්ෂණය කරයි.',
  'www.legal.pdpa.rightsExceptions':
    'මකා දැමීමට එක් සීමාවක් ඇත, එය පැහැදිලිව කීම වටී: නීතියෙන් MageRide හට තබා ගැනීමට ' +
    'නියම කර ඇති වාර්තා — ප්‍රධාන වශයෙන් මූල්‍ය ඒවා — තබා ගැනේ. අවශ්‍ය නොවන සියල්ල ' +
    'ඉවත් කෙරේ.',
  'www.legal.pdpa.howHeading': 'ඉල්ලන්නේ කෙසේද',
  'www.legal.pdpa.howBody':
    'යෙදුම තුළින්, සැකසුම් යටතේ. එය මෙහි නොව එහි ඇත්තේ ඉල්ලීමක් ගිණුමකට බැඳී තිබිය ' +
    'යුතු නිසාත්, ඔබ පිවිසී ඇත්තේ කුමන ගිණුමටදැයි යෙදුම දැනටමත් දන්නා නිසාත්ය. මෙම ' +
    'පිටුවේ පෝරමයකට එය දැනගත නොහැක; විද්‍යුත් තැපෑලෙන් ඔබ කවුදැයි ඔප්පු කරන ලෙස ඉල්ලීම ' +
    'යනු ඔබට අඩුවෙන් දීම සඳහා ඔබ ගැන වැඩියෙන් එකතු කිරීමකි.',

  // --- shared page furniture ------------------------------------------------
  'www.common.learnMore': 'තව දැනගන්න',
  'www.common.backToTop': 'ඉහළට',
  'www.common.onThisPage': 'මෙම පිටුවේ',
  'www.common.sourceLabel': 'මූලාශ්‍රය',
  'www.common.previous': 'පෙර',
  'www.common.next': 'මීළඟ',

  // =========================================================================
  // S08 · the passenger guide, chapters 1–8 — translated in S12.
  //
  // A how-to guide for a transport platform, so the numbers matter and none of
  // them is in this file: every fee, count and limit is a constant in
  // `src/content/` with its spec anchor beside it, which is what makes a
  // translated step unable to damage one. What a translator *can* damage is a
  // step's order or its meaning, and that is what a native review is for.
  // =========================================================================

  // Chapter 1 · Install MageRide and sign in
  'www.guide.p01.title': 'MageRide ස්ථාපනය කර පිවිසෙන්න',
  'www.guide.p01.summary':
    'මුල් මිනිත්තු කිහිපය: ඔබට කියවීමට අවශ්‍ය භාෂාව තෝරා ගැනීම, කේතයකින් ඔබේ දුරකථන ' +
    'අංකය තහවුරු කිරීම, සහ ඔබේ නම එක් කිරීම. නිර්මාණය කිරීමට මුරපදයක් නැත, දීමට ' +
    'විද්‍යුත් තැපැල් ලිපිනයක්ද නැත.',
  'www.guide.p01.step1':
    'පළමු වරට MageRide විවෘත කරන්න, එය කරන දේ කෙටි ස්ලයිඩ තුනකින් හඳුන්වා දේ. ඒවාට ' +
    'පහළින් භාෂා තෝරනය ඇත — පේළියකට එකක් බැගින් කොටු තුනක්, ඉහළින්ම සිංහල, ඉන්පසු ' +
    'දෙමළ, ඉන්පසු ඉංග්‍රීසි, සිංහල දැනටමත් තෝරා ඇත. ඔබ කියවන එක තට්ටු කර, තිරයේ පහළින් ' +
    'ඇති ආරම්භ කරන්න තට්ටු කරන්න.',
  'www.guide.p01.step2':
    'ඔබේ ජංගම දුරකථන අංකය ඇතුළත් කරන්න. ක්ෂේත්‍රයේ දැනටමත් +94 ඇත, එබැවින් ඔබ ටයිප් ' +
    'කරන්නේ ඉන් පසුව එන ඉලක්කම් නවයයි. ඉදිරියට තට්ටු කරන්න, MageRide කෙටි පණිවිඩයකින් ' +
    'ඉලක්කම් හයක කේතයක් යවයි.',
  'www.guide.p01.step3':
    'කේතය කොටු හයට ටයිප් කරන්න. බොහෝ Android දුරකථන එය ඔබ වෙනුවෙන් පුරවයි. එය නොපැමිණේ ' +
    'නම්, තත්පර හැටකට පසු නැවත යවන්න සබැඳිය තට්ටු කළ හැකි වේ — ඔබට පැයකට ඉල්ලිය හැක්කේ ' +
    'උපරිම කේත පහකි.',
  'www.guide.p01.step3.note':
    'වැරදි කේතයක් කොටු රතු කර ඒ බව කියයි. කිසිවක් නැති වී නැත; නැවත ටයිප් කරන්න.',
  'www.guide.p01.step4':
    'ඔබේ නම එක් කරන්න, කැමති නම් ඡායාරූපයක්ද. MageRide හට ඔබට දැනුම්දීම් යැවිය හැකිද ' +
    'යන්නත් ඔබ කියන්නේ මෙම තිරයේදීමය. සුරකින්න සහ ඉදිරියට තට්ටු කරන්න.',
  'www.guide.p01.step5':
    'ඉන්පසු MageRide ඔබේ ස්ථානය භාවිත කිරීමට ඉල්ලා, ඔබේ දුරකථනයේම අවසර කොටුව දිස් ' +
    'වීමට පෙර ඒ ඇයිදැයි පැහැදිලි කරයි. 2 වන පරිච්ඡේදය එම තිරය නිසි ලෙස විස්තර කරයි.',
  'www.guide.p01.step6':
    'ඉන්පසු ඔබ සිටින තැන කේන්ද්‍ර කරගෙන සජීවී සිතියම විවෘත වේ. එය මුල් තිරය වන අතර, ' +
    'මෙම මාර්ගෝපදේශයේ අනෙක් සියල්ල ආරම්භ වන්නේ එතැනිනි.',
  'www.guide.p01.callout.notPublished':
    'MageRide තවම යෙදුම් වෙළඳසැල්වලට නිකුත් කර නැත, එබැවින් අද ස්ථාපනය කිරීමට කිසිවක් ' +
    'නැත. මෙම මාර්ගෝපදේශය විස්තර කරන්නේ යෙදුම නිර්මාණය කර අනුමත කර ඇති ආකාරයයි; ' +
    'යෙදුම් නිකුත් වූ විට බාගැනීමේ පිටුව ඒ බව කියයි.',
  'www.guide.p01.callout.phoneOnly':
    'මගී සහ රියදුරු යෙදුම්වලට පිවිසිය හැකි එකම මාර්ගය දුරකථන අංකයක් සහ කේතයකි. ' +
    'මුරපදයක් නැත, විද්‍යුත් තැපැල් ලිපිනයක් නැත, Google හෝ Apple පිවිසුමක්ද නැත — ' +
    'ඒවා ඇත්තේ මගියෙකුට කිසිදා අවශ්‍ය නොවන රථ සමූහ සහ පරිපාලන වෙබ් ද්වාර සඳහා පමණි.',
  'www.guide.p01.callout.oneDevice':
    'වරකට එක් දුරකථනයක්. නව දුරකථනයකින් මගී යෙදුමට පිවිසීම පැරණි එකෙන් වහාම ඉවත් කරන ' +
    'අතර, කවුරුන් හෝ නැවත පිවිසෙන තුරු එම දුරකථනයට කිසිවකට ළඟා විය නොහැක. රියදුරු ' +
    'යෙදුම වෙනම ගණන් ගන්නා බැවින් එක් අයෙකුට දෙකම භාවිත කළ හැක.',

  // Chapter 2 · The permissions MageRide asks for
  'www.guide.p02.title': 'MageRide ඉල්ලන අවසර',
  'www.guide.p02.summary':
    'මගී යෙදුම ඉල්ලන්නේ එක් දෙයකි: ඔබේ ස්ථානය, එයද ඔබ එය භාවිත කරන අතරතුර පමණි. එම ' +
    'අවසරය කරන දේ, නොකරන දේ, සහ පසුව ඔබේ අදහස වෙනස් කරන ආකාරය මෙම පරිච්ඡේදයයි.',
  'www.guide.p02.step1':
    'ඔබේ දුරකථනය කිසිවක් ඉල්ලීමට පෙර, MageRide තමන්ගේම තිරයක් පෙන්වා ඔබේ ස්ථානය අවශ්‍ය ' +
    'ඇයිදැයි කියයි — ඔබ අසල ගමන් කරන දේ පෙන්වීමට, සහ රියදුරෙක් පැමිණෙන ලක්ෂ්‍යය සැකසීමට. ' +
    'මෙම අදියරේදී කිසිවක් ඉල්ලා නැත.',
  'www.guide.p02.step2':
    'ස්ථානයට අවසර දෙන්න තට්ටු කරන්න. ඉන්පසු ඔබේ දුරකථනය එහිම අවසර කොටුව පෙන්වයි — ' +
    'කිසිදු යෙදුමකට වචන වෙනස් කළ නොහැකි එකයි — තේරීම කරන්නේ එහිදීය.',
  'www.guide.p02.step3':
    'ඔබ යෙදුම භාවිත කරන අතරතුර එයට අවසර දෙන විකල්පය තෝරන්න. මගී යෙදුම ඉල්ලන්නේ එයයි: ' +
    'Android හි පෙරබිමේ නිශ්චිත ස්ථානය, iPhone එකක නම් භාවිත කරන විට.',
  'www.guide.p02.step4':
    'ඔබ දැන් නොවේ තට්ටු කළොත්, හෝ අහම්බෙන් එය ප්‍රතික්ෂේප කළොත්, MageRide හට තනිවම ' +
    'ඔබෙන් නැවත ඉල්ලිය නොහැක — කිසිදු යෙදුමකට එය නොහැක. ඒ වෙනුවට එය සැකසුම් විවෘත ' +
    'කරන්න සබැඳියක් පෙන්වයි, එය ඔබව ඔබේ දුරකථනයේ සැකසුම්වල නිවැරදි පිටුවට ගෙන යයි.',
  'www.guide.p02.step5':
    'එය නොමැතිව සිතියමට කේන්ද්‍ර වීමට තැනක් නැත, ආරම්භ කිරීමට බාරගැනීමේ ලක්ෂ්‍යයක්ද ' +
    'නැත, එබැවින් යෙදුමට ඇත්තටම අවශ්‍ය එකම අවසරය මෙයයි. එය ඇත්නම්, ඔබේ දුරකථනය කෙතරම් ' +
    'නිශ්චිතද යන්න සලකුණු කරන කවයක් තුළ නිල් තිතක් ලෙස ඔබේම පිහිටීම පෙනෙන අතර, ඔබ ගමන් ' +
    'කරන විට සිතියම ඔබව අනුගමනය කරයි.',
  'www.guide.p02.step6':
    'දැනුම්දීම් යනු MageRide ඔබ මත පටවන දෙයක් නොව මනාපයකි. ඔබේ පැතිකඩ සාදන අතරතුර එය ' +
    'සකසන අතර පැතිකඩ සහ සැකසුම් තුළ එය වෙනස් කරයි. රියදුරෙක් පිළිගත් බව, රියදුරු ' +
    'පැමිණි බව, ගමන ආරම්භ වූ බව, ගෙවීමක් සාර්ථක වූ බව, හෝ කලින් වෙන් කළ ගමනක් ළං වන ' +
    'බව ඔබට කියන්නේ ඒවාය.',
  'www.guide.p02.step7':
    'යෙදුම ඉල්ලිය හැකි එකම අනෙක් දෙය ඔබේ සම්බන්ධතා ලැයිස්තුවයි, එයද ඔබ වෙනත් අයෙකුට ' +
    'ගමනක් වෙන් කරන අතරතුර සම්බන්ධතා තෝරනය තට්ටු කළොත් පමණි. නම සහ අංකය ඔබම ටයිප් ' +
    'කළොත් එය කිසිදා ඉල්ලන්නේ නැත.',
  'www.guide.p02.callout.noBackground':
    'මගී යෙදුම පසුබිම් ස්ථානය ඉල්ලන්නේ නැත. යෙදුම වසා ඇති විට එය ඔබ සිටින තැන වාර්තා ' +
    'කරන්නේ නැත. රියදුරු යෙදුම එහි විරුද්ධ පැත්තයි, එය තමන්ගේම අවසර තිරයේ ඒ බව කියයි ' +
    '— සජීවී සිතියම සෑදී ඇත්තේ රියදුරෙකුගේ පිහිටීමෙනි.',
  'www.guide.p02.callout.reenable':
    'අදහස වෙනස් වුණාද? MageRide තුළ ඇති කිසිවකට අවසරයක් නැවත සක්‍රීය කළ නොහැක. එය කළ ' +
    'හැක්කේ ඔබේ දුරකථනයේ සැකසුම්වලට පමණි, යෙදුමේ සැකසුම් විවෘත කරන්න සබැඳිය එතැනට යන ' +
    'කෙටිම මාර්ගයයි.',

  // Chapter 3 · Reading the live map
  'www.guide.p03.title': 'සජීවී සිතියම කියවීම',
  'www.guide.p03.summary':
    'සිතියම මුල් තිරය වන අතර, එහි ඇති බොහෝ දේ අර්ථයක් සහිත වර්ණයකි: වාහන වර්ග දහයකට ' +
    'එකක් බැගින් වර්ණ, පෞද්ගලික වාහනයකට අළු සලකුණක්, සහ නැවතත් වෙනස් වර්ණ කට්ටලයක් ' +
    'වන ක්‍රම ලාංඡන තුනක්.',
  'www.guide.p03.step1':
    'සිතියම විවෘත වන්නේ ඔබ මතය. සෑම සලකුණක්ම තම පිහිටීම යවන සැබෑ වාහනයක් වන අතර, එක් ' +
    'එකක් ගමන් කරන දිශාවට යොමු වී ඇත. වාහනයක චලනය තත්පර දෙකේ සිට අටක් ඇතුළත ඔබේ ' +
    'තිරයට ගෙන ඒමට වේදිකාව ගොඩනගා ඇත.',
  'www.guide.p03.step2':
    'එය කුමන වර්ගයේ වාහනයක්දැයි කියන්නේ වර්ණයයි, එම වර්ණම සලකුණුවලත්, පෙරහන් කූඤ්ඤවලත්, ' +
    'ගාස්තු කාඩ්පත්වලත් භාවිත වේ. බස් කොළ පාට, දුම්රිය රතු පාට — මහජන වර්ග දෙක. ' +
    'යතුරුපැදි දම් පාට, ත්‍රීරෝද කහ පාට, ෆ්ලෙක්ස් ටීල් පාට, සෙඩාන් නිල් පාට, මිනි වෑන් ' +
    'රෝස පාට සහ වෑන් තැඹිලි පාට. ට්‍රක් රථ දුඹුරු පාට සහ මිනි ට්‍රක් ඔලිව් පාට; ඒ දෙක ' +
    'මිනිසුන් නොව පාර්සල් ගෙන යයි.',
  'www.guide.p03.step3':
    'බසයක් හෝ දුම්රියක් තට්ටු කරන්න, එහි මාර්ගය, එය කොපමණ දුරින්ද, ඔබ වෙත ළඟා විය ' +
    'යුත්තේ කවදාද, එහි ලියාපදිංචි අංකය සහ රියදුරාගේ නම හා ඡායාරූපය සමඟ පුවරුවක් ඉහළට ' +
    'ලිස්සා එයි.',
  'www.guide.p03.step4':
    'අළු සලකුණක් යනු පෞද්ගලික වාහනයකි. සිතියමේ එකොළොස්වන වර්ණය අළු පාට වන අතර, එය ' +
    'වාහන වර්ගයක් නොවන එකයි — එය කියන්නේ "මෙය B ක්‍රමයේ වාහනයකි" කියාය, "මෙය විශේෂිත ' +
    'වෑන් රථ වර්ගයකි" කියා නොවේ. එකක් තට්ටු කිරීමෙන් ඉහත පුවරුව විවෘත වන්නේ නැත; එයින් ' +
    'විවෘත වන්නේ එහි හිමිකරුගෙන් අවසර ඉල්ලන තිරය වන අතර, එය 5 වන පරිච්ඡේදයයි.',
  'www.guide.p03.step5':
    'දුරට ගෙන යන්න, එවිට එකිනෙකට ළං සලකුණු එකට එකතු වී ඔබට නැවත ළං කළ හැකි එක් ' +
    'පොකුරක් වේ, එවිට නගරයක් සලකුණු බිත්තියක් නොවේ.',
  'www.guide.p03.step6':
    'ඉහළ දකුණේ ඇති පෙරහන් බොත්තමේ ක්‍රම තුනම ඇත — මහජන ප්‍රවාහනය, පෞද්ගලික වාහන සහ ' +
    'සබැඳිව සිටින ඉල්ලුම මත වාහන — සහ එහි සලකුණේම වර්ණවත් අයිකනය දරන, වාහන වර්ගයකට ' +
    'එකක් බැගින් කූඤ්ඤයක්. ඔබට අවශ්‍ය නැති දේ නිවා දමන්න, සිතියම වහාම නැවත අඳී.',
  'www.guide.p03.step7':
    'දේවල් දෙකක් හිතාමතාම නැත. දැනටමත් කවුරුන් හෝ රැගෙන යන ඉල්ලුම මත වාහනයක් මහජන ' +
    'සිතියමේ නැත. තව ද, තම පිහිටීම යැවීම නවත්වන වාහනයක් අවසන් වරට දුටු තැන තැබීම ' +
    'වෙනුවට ඉවත් කරයි, එබැවින් සලකුණක් යනු සැමවිටම ඇත්තටම එහි ඇති වාහනයකි.',
  'www.guide.p03.step8':
    'ඔබේ සම්බන්ධතාවය කැඩුණොත්, සලකුණු අඳුරු වන අතර, එය නැවත එනතුරු ඔබ බලන්නේ අවසන් ' +
    'වරට දන්නා පිහිටීම් බව බැනරයක් කියයි.',
  'www.guide.p03.callout.modeBadges':
    'ක්‍රම ලාංඡන යනු වෙනම වර්ණ තුනක කට්ටලයකි — මහජන සඳහා කොළ, පෞද්ගලික සඳහා අළු, ' +
    'ඉල්ලුම මත සඳහා තැඹිලි — ඒවා නම් කරන්නේ වාහනය නොව ක්‍රමයයි. කොළ ලාංඡනයක් සහ කොළ ' +
    'බස් සලකුණක් එකම දේ දෙවරක් කියන්නේ නැත.',
  'www.guide.p03.callout.coverage':
    'ඔබ ඉල්ලූ වර්ගයේ කිසිවක් අසල නැත්නම්, හිස් සිතියමක් සමඟ ඔබව තැබීම වෙනුවට යෙදුම ' +
    'පෙළක් තුළින් ඒ බව කියයි. තව ද, හිස් සිතියමක් යනු ඔබ අසල කිසිවෙක් තවම MageRide ' +
    'හා එක් වී නැති බවයි, කිසිවක් ගමන් නොකරන බව නොවේ.',

  // Chapter 4 · Tracking a bus or a train
  'www.guide.p04.title': 'බසයක් හෝ දුම්රියක් ලුහුබැඳීම',
  'www.guide.p04.summary':
    'මහජන බස් සහ දුම්රිය යනු A ක්‍රමයයි: බැලීමට නොමිලේ, වෙන් කිරීමට කිසිවක් නැත, ' +
    'ගෙවීමට කිසිවක්ද නැත. එකක් සොයා ගැනීමට ක්‍රම දෙකකි — සිතියමේ එය තට්ටු කරන්න, ' +
    'නැතහොත් ඔබ යන්නේ කොහේදැයි කියා එතැනට ගෙන යන මාර්ග MageRide ලැයිස්තුගත කරන්නට ' +
    'ඉඩ දෙන්න.',
  'www.guide.p04.step1':
    'ඉක්මන් ක්‍රමය නම් සිතියමේ කොළ බසයක් හෝ රතු දුම්රියක් තට්ටු කිරීමයි. පුවරුව ඔබට එහි ' +
    'මාර්ගය, එය කොපමණ දුරින්ද, එය පැමිණිය යුත්තේ කවදාද, එහි ලියාපදිංචිය සහ එහි රියදුරු ' +
    'ලබා දෙයි.',
  'www.guide.p04.step2':
    'අනෙක් ක්‍රමය ආරම්භ වන්නේ "කොහෙද යන්නේ?" යන්නෙනි. ස්ථානයක් හෝ ලිපිනයක් ටයිප් ' +
    'කරන්න. මෙහි මාර්ග අංකයක් ටයිප් කළ නොහැක, මන්ද MageRide මාර්ග ගණනය කරන්නේ ඊට ' +
    'ප්‍රතිවිරුද්ධව නොව ඔබේ ගමනාන්තයෙන් නිසාය.',
  'www.guide.p04.step3':
    'මීළඟ තිරය එතැනට ළඟා වන සෑම සෘජු මහජන මාර්ගයක්ම, එහි මාර්ග අංකය, එය ධාවනය වන ' +
    'තැන පිළිබඳ විස්තරයක් සහ එය මහජන ප්‍රවාහනය ලෙස සලකුණු කරන ලේබලයක් සමඟ ලැයිස්තුගත ' +
    'කරයි. මාරු වීමක් අවශ්‍ය මාර්ග ඊට පහළින් එසේ ලකුණු කර ලැයිස්තුගත වේ.',
  'www.guide.p04.step4':
    'මාර්ගයක් තෝරන්න, එවිට සිතියම දුරට ගොස් එය එම වාහනයේ වර්ණයෙන් අඳී, මාර්ගයේ ' +
    'පැමිණීමේ වේලාව සමඟ. ඔබ මාර්ගය මත සිටගෙන නැත්නම්, ඉරි සහිත නිල් රේඛාවක් ළඟම ' +
    'නැවතුමට ඇති ඇවිදීම සහ එය කොපමණ දුරද යන්න පෙන්වයි.',
  'www.guide.p04.step5':
    'මාර්ගය ලුහුබඳින්න තට්ටු කරන්න, එවිට සිතියම ඔබ වෙනුවෙන් එම මාර්ගය අනුගමනය කරයි. ' +
    'මහජන මාර්ගයකට වෙන් කරන්න බොත්තමක් නැත, ගාස්තුවක්ද නැත — A ක්‍රමය යනු ඔබ බලන ' +
    'දෙයක් මිස ඔබ මිලදී ගන්නා දෙයක් නොවේ.',
  'www.guide.p04.step6':
    'ඔබ අනුගමනය කරන බස් රථය තම පිහිටීම යැවීම නවත්වන්නේ නම්, එහි සලකුණ අවසන් වරට දුටු ' +
    'වේලාව පෙන්වා පසුව ඉවත් වේ. එම මාර්ගයේම වෙනත් වාහනයක් ලුහුබඳින්න.',
  'www.guide.p04.callout.free':
    'A ක්‍රමය ගැන කිසිවකට වියදමක් නැත. මගීන් බැලීමට කිසිවක් නොගෙවන අතර, දිස් වීමට ' +
    'මෙහෙයුම්කරුවන් MageRide හට කිසිවක් නොගෙවයි — දෛනික වේදිකා ගාස්තුවක් කිසිසේත් ' +
    'නොමැති වාහන වර්ග දෙක බස් සහ දුම්රියයි.',
  'www.guide.p04.callout.gtfs':
    'MageRide හට ලැයිස්තුගත කළ හැකි මාර්ග රඳා පවතින්නේ එයට ලබා දී ඇති කාලසටහන් දත්ත ' +
    'මතය. මාර්ග තොරතුරු එන්නේ පරිපාලකයන් පූරණය කර නැවුම් කරන ජාතික මහජන ප්‍රවාහන දත්ත ' +
    'ගොනුවකිනි; එම ගොනුවේ නැති මාර්ගයක් කොපමණ බස් ධාවනය කළත් ලැයිස්තුගත කළ නොහැක, ' +
    'තව ද එහි ඇති නමුත් කිසිවෙක් වාර්තා නොකරන මාර්ගයක් ඔබට පෙන්වන්නේ සිතියමේ රේඛාවක් ' +
    'මිස එහි වාහනයක් නොවේ.',
  'www.guide.p04.callout.trains':
    'දුම්රිය සිතියමේ ඇත්තේ බස් සමඟ එකම පදනමකය, ඔබට ඒවා වෙන් වශයෙන් පෙරහන් කළ හැක. ' +
    'ඒවා ලියාපදිංචි කරන්නේ රියදුරන් නොව MageRide පරිපාලකයන්ය.',

  // Chapter 5 · Following a private vehicle
  'www.guide.p05.title': 'පෞද්ගලික වාහනයක් අනුගමනය කිරීම',
  'www.guide.p05.summary':
    'පාසල් වෑන් රථයක්, කාර්ය මණ්ඩල බසයක්, පවුලක් බෙදාගන්නා වාහනයක්. B ක්‍රමය යනු එහි ' +
    'හිමිකරු ඔබට ඇතුළට එන්නට දී ඇති නිසාම පමණක් ඔබට අනුගමනය කළ හැකි වාහනයකි, ඔබ ඉල්ලන ' +
    'ආකාරය මෙම පරිච්ඡේදයයි.',
  'www.guide.p05.step1':
    'පෞද්ගලික වාහන සිතියමේ දිස් වන්නේ අළු සලකුණු ලෙසය. එකක් තට්ටු කරන්න, එවිට එම ' +
    'වාහනයේ හැඳුනුම දැනටමත් පුරවා ඇති ප්‍රවේශ ඉල්ලීම MageRide විවෘත කරයි.',
  'www.guide.p05.step2':
    'මෙනුවේ පෞද්ගලික ප්‍රවාහනය යටතේද එම තිරයටම ළඟා විය හැකි අතර, එහිදී වාහන හැඳුනුම ' +
    'ඔබම ටයිප් කළ හැක — වෑන් රථය එවේලේ ඔබේ තිරයේ නැති විට ඔබ කරන්නේ එයයි.',
  'www.guide.p05.step3':
    'ඉල්ලීම යවන්න. එය එම වාහනයේ හිමිකරුට, නැතහොත් එයට පවරා ඇති රියදුරාට යන අතර, ඉල්ලන්නේ ' +
    'කවුරුන්දැයි ඔවුන්ට කිව හැකි වන පරිදි ඔබේ නම සහ ඔබේ ජංගම දුරකථන අංකය ඔවුන්ට පෙන්වයි.',
  'www.guide.p05.step4':
    'ඉන්පසු තිරය ඉල්ලීම කොතැනද යන්න පෙන්වයි: ඔබ බලා සිටින අතරතුර පොරොත්තුවෙන්, ඉන්පසු ' +
    'පිළිගත්තා හෝ ප්‍රතික්ෂේප කළා. එය පිළිගත්තා යැයි කියන තුරු වාහනය ගැන කිසිවක් ඔබට ' +
    'නොපෙනේ.',
  'www.guide.p05.step5':
    'එය පිළිගත් පසු වාහනය ඔබේ සිතියමේ දිස් වන අතර ඔබ එය අන් ඕනෑම එකක් සේ අනුගමනය කරයි ' +
    '— වෑන් රථය පාසලෙන් යනු බලන්න, කාර්ය මණ්ඩල බසය පාරේ එනු බලන්න. එය කොහේද සහ කුමන ' +
    'දිශාවට යනවාද යන්න බැලීමට එය තට්ටු කරන්න.',
  'www.guide.p05.step6':
    'ප්‍රවේශය දෙනු ලබන්නේ මෙහෙයුම්කරුවකට නොව වාහනයකට වශයෙනි. වාහන සමූහයක් වෑන් රථ හයක් ' +
    'ධාවනය කරන්නේ නම් ඔබ ඉල්ලන්නේ ඔබේ දරුවා ගමන් කරන එකයි, හිමිකරු එම වාහනයේ ඉල්ලීම් ' +
    'එම වාහනය යටතේම හසුරුවයි.',
  'www.guide.p05.step7':
    'හිමිකරුට කැමති විටෙක ප්‍රවේශය ඉවත් කර ගත හැකි අතර එය වහාම ක්‍රියාත්මක වේ — වාහනය ' +
    'හුදෙක් ඔබේ සිතියමේ නොසිටී. එය නැවත ලබා ගැනීම යනු ඔවුන් පිළිගත යුතු නව ඉල්ලීමකි.',
  'www.guide.p05.step8':
    'පෞද්ගලික වාහනයක් තම පිහිටීම ප්‍රකාශ කරන්නේ එහි හිමිකරු සකසන කාලසටහනකට අනුව බැවින්, ' +
    'එහි වැඩ කරන වේලාවෙන් පිටත බැලීමට කිසිවක් නොතිබිය හැක.',
  'www.guide.p05.callout.permission':
    'B ක්‍රමයේ රහස්‍යතා ආකෘතිය මුළුමනින්ම මෙයයි, ඒ ගැන පැහැදිලිව කීම වටී. පෞද්ගලික ' +
    'වාහනයක්, එහි හිමිකරු එකින් එක ඉල්ලීම් අනුව අනුමත කර ඇති අය හැර සියලු දෙනාට ' +
    'නොපෙනේ. එහි සලකුණ තට්ටු කිරීමෙන් ඒ ගැන කිසිවක් හෙළි නොවේ — එයින් විවෘත වන්නේ ' +
    'ඉල්ලන පෝරමය පමණි.',
  'www.guide.p05.callout.identified':
    'ඉල්ලීම නිර්නාමික නොවේ. තීරණය කිරීමට පෙර හිමිකරුට ඔබේ නම, ඔබේ ජංගම දුරකථන අංකය සහ ' +
    'ඔබේ මගී හැඳුනුම පෙනේ, පාසල් වෑන් රථයක් ධාවනය කරන අයෙකුට දෙමාපියෙකු සහ ආගන්තුකයෙකු ' +
    'වෙන් කර හඳුනාගත හැක්කේ එලෙසිනි.',

  // Chapter 6 · Paying for a vehicle you follow
  'www.guide.p06.title': 'ඔබ අනුගමනය කරන වාහනයකට ගෙවීම',
  'www.guide.p06.summary':
    'සමහර පෞද්ගලික වාහන අනුගමනය කිරීම නොමිලේ, සමහරකට මාසික ගාස්තුවක් ඇත. එය කුමන ' +
    'එකද, කොපමණ වැය වේද, කවදා ගෙවිය යුතුද යන්න සකසන්නේ MageRide නොව මෙහෙයුම්කරුය — ' +
    'මුදලද යන්නේ අපට නොව ඔවුන්ටය.',
  'www.guide.p06.step1':
    'සෑම පෞද්ගලික වාහනයක්ම එය ධාවනය කරන අය විසින් නොමිලේ හෝ ගෙවුම් ලෙස සකසා ඇත. එම ' +
    'සැකසුම හැඳින්වෙන්නේ සේවා ගෙවීම ලෙසය. කාර්යාලයක් හෝ කාර්ය මණ්ඩල ප්‍රවාහනයක් ' +
    'සාමාන්‍යයෙන් තෝරන්නේ නොමිලේ යන්නයි: ඔබ වාහනය අනුගමනය කරයි, ගෙවීම් තිරයක් ' +
    'කිසිසේත් නැත.',
  'www.guide.p06.step2':
    'ගෙවුම් යනු දායකයෙකුට මාසික මුදලකි. එය සකසන්නේ මෙහෙයුම්කරු වන අතර, එකම වාහනයේ ' +
    'විවිධ පුද්ගලයන්ට විවිධ මුදල් සකසිය හැක, එබැවින් ඔබට කිව හැකි MageRide මිලක් නැත ' +
    '— ඔබට ගෙවීමට සිදු වීමට පෙර යෙදුම ඔබේ මුදල පෙන්වයි.',
  'www.guide.p06.step3':
    'ඔබේ දායකත්ව ඇත්තේ මෙනුවේ මගේ දායකත්ව යටතේය. සෑම කාඩ්පතක්ම වාහනය, එය ගෙවුම්ද ' +
    'නොමිලේද, මුදල සහ ඊළඟට ගෙවිය යුතු දිනය, ගෙවන්න බොත්තමක්, ඉතිහාස බොත්තමක් සහ ' +
    'දායකත්වයෙන් ඉවත් වීමට කුඩා කතිරයක් පෙන්වයි.',
  'www.guide.p06.step4':
    'බිල්පත් චක්‍රය මාසයේ පළමු දිනය හෝ ඔබ එක් වූ දිනයේ සංවත්සරයයි — ජූනි 5 වැනිදා ' +
    'දායක වන්න, ඊළඟ ගෙවීම ජූලි 6 වැනිදාට ගෙවිය යුතුය. ඔබට අදාළ වන්නේ කුමන එකදැයි ' +
    'කාඩ්පත කියයි.',
  'www.guide.p06.step5':
    'ගෙවන්න තට්ටු කර කෙසේදැයි තෝරන්න. LankaQR මුදල දැනටමත් පුරවා ඇතිව ඔබේ බැංකු යෙදුම ' +
    'විවෘත කරයි; ඒ වෙනුවට මෙහෙයුම්කරුගේ LankaQR කේතය පරිලෝකනය කළ හැක; නැතහොත් ' +
    'සාමාන්‍ය බැංකු මාරු කිරීමක් කර පත්‍රිකාවේ ඡායාරූපයක් අමුණා ගත හැක.',
  'www.guide.p06.step6':
    'මාරු කිරීමක් ඔවුන්ගේ පැත්තෙන් මෙහෙයුම්කරු එය තහවුරු කරන තුරු සත්‍යාපනය බලාපොරොත්තුවෙන් ' +
    'ලෙස පෙන්වයි. මුදල් යන්නේ එය එකතු කරන අයටය, එය ලැබුණු බව සලකුණු කළ හැක්කේ හිමිකරුට ' +
    'පමණි — ඉන්පසු ඔබේ කාඩ්පත ගෙවා ඇත යැයි කියන අතර ගෙවීම ඔබේ ඉතිහාසයේ දිස් වේ.',
  'www.guide.p06.step7':
    'ඉතිහාස බොත්තම සෑම මාසයක්ම ලැයිස්තුගත කරයි: දිනය, ක්‍රමය, මුදල සහ එය කොතැනද යන්න.',
  'www.guide.p06.step8':
    'දායකත්වයෙන් ඉවත් වීම යනු එම කුඩා කතිරයයි. එය තහවුරු කරන්න, එවිට වහාම පාහේ වාහනය ' +
    'ඔබට නොපෙනී යයි; නැවත පැමිණීම යනු 5 වන පරිච්ඡේදයේ මෙන් නැවුම් ඉල්ලීමක් යවා එය ' +
    'නැවත පිළිගන්නා තුරු බලා සිටීමයි.',
  'www.guide.p06.callout.passThrough':
    'මෙම මුදල MageRide හට අයිති නැත. දායකත්වයක් ගෙවනු ලබන්නේ වාහනයේ මෙහෙයුම්කරුටය — ' +
    'MageRide ගෙවීම ඔවුන්ගේ ගිණුමට යොමු කර එය සිදු වූ බව සටහන් කරයි, එයින් කිසිවක් ' +
    'ගන්නේ නැත.',
  'www.guide.p06.callout.firstMonth':
    'නව දායකයෙකුට පළමු මාසය නොමිලේ. එයට වෙනම, මෙහෙයුම්කරු එක් එක් පෞද්ගලික වාහනයක් ' +
    'සඳහා MageRide හට මසකට රු. 300ක් පමණ ගෙවයි; පිරිවිතරයේ කියන්නේ ආසන්න වශයෙන් ' +
    'බැවින්, මෙහි ලියා ඇත්තේද එලෙසය. එය ඔවුන්ගේ වියදම මිස ඔබේ එකට එකතු වන දෙයක් නොවේ.',

  // Chapter 7 · Booking a ride
  'www.guide.p07.title': 'ගමනක් වෙන් කිරීම',
  'www.guide.p07.summary':
    'ඉල්ලුම මත ගමන් යනු C ක්‍රමයයි: දැන් ඔබ වෙත එන යතුරුපැදියක්, ත්‍රීරෝද රථයක්, ' +
    'මෝටර් රථයක් හෝ වෑන් රථයක්. මෙම පරිච්ඡේදය ඔබ යන්නේ කොහේදැයි කීම ගැනය. ඊළඟ එක ' +
    'එන්නේ කුමක්ද, එයට කොපමණ වැය වේද යන්න තෝරා ගැනීම ගැනය.',
  'www.guide.p07.step1':
    'සිතියමේ පහළින් "කොහෙද යන්නේ?" ඇත, ගෙදර, රැකියාව සහ ඔබ මෑතකදී ගිය ස්ථාන සමඟ.',
  'www.guide.p07.step2':
    'එය තට්ටු කර ස්ථානයක් හෝ ලිපිනයක් ටයිප් කරන්න. මෙහි බස් මාර්ග අංකයක් MageRide ' +
    'භාර ගන්නේ නැත — ගමනාන්තයක් සැමවිටම ස්ථානයකි, ඔබව එතැනට ගෙන යා හැක්කේ කුමක්දැයි ' +
    'යෙදුම ගණනය කරන්නේ පසුවය.',
  'www.guide.p07.step3':
    'යෝජනා යනු MageRide හි තමන්ගේම සෙවුමෙන් සොයාගත් ස්ථාන, ඔබේ සුරැකි සහ මෑත ලිපින ' +
    'සමඟ මිශ්‍ර වූ ඒවාය. සෙවුම නොමැති නම්, ඒ වෙනුවට සිතියමේ ස්ථානය තෝරා ගැනීමට යෙදුම ' +
    'ඉඩ දෙයි.',
  'www.guide.p07.step4':
    'සුරැකි ස්ථාන ටයිප් කිරීම ඉතිරි කරයි. ගෙදර සහ රැකියාව සකසන්නේ සිතියමේ ලකුණක් ' +
    'තැබීමෙනි, වෙනත් ඕනෑම ස්ථානයක් ලිපින පේළි තුනකින් සහ ඔබේම ලේබලයකින් සුරැකිය හැක ' +
    '— "ව්‍යායාම්", "අම්මාගේ ගෙදර".',
  'www.guide.p07.step5':
    'ස්ථානයක් සැකසීමට හතරවන ක්‍රමයක් ඇත, කවුරුන් හෝ WhatsApp හරහා ඔබට ස්ථානයක් එවා ' +
    'ඇති විට ප්‍රයෝජනවත් වන්නේ එයයි: Google Maps සබැඳියක් අලවන්න. MageRide කෙටි ' +
    'සබැඳි තමන්ගේම සේවාදායකවල විසඳමින්, සබැඳියෙන්ම ඛණ්ඩාංක කියවා ගන්නා අතර, ඔබ ඊට ' +
    'බැඳීමට පෙර එය ගණනය කළ ලකුණ සහ ලිපිනය ඔබට පෙන්වයි. සබැඳිය කියවා ගත නොහැකි නම් ' +
    'එය ඒ බව කියා සිතියම ඉදිරිපත් කරයි.',
  'www.guide.p07.step5.note':
    'සබැඳියක් ඇලවීම ඉදිරිපත් වන්නේ ඔබ වෙනත් අයෙකුට ස්ථානයක් සකසන තැන්වලය — වෙනත් ' +
    'පුද්ගලයෙකු වෙනුවෙන් වෙන් කරන විට බාරගැනීමේ ස්ථානය, සහ පාර්සලයක කෙළවර දෙකම.',
  'www.guide.p07.step6':
    'ඔබේ බාරගැනීමේ ස්ථානය ආරම්භ වන්නේ ඔබ සිටගෙන සිටින තැනින් වන අතර එය ගෙන යා හැක. ' +
    'වෙන් කිරීමේ තිරය සිතියමේ ලක්ෂ්‍ය දෙක, මට හෝ වෙනත් අයෙකුට යන ටොගලයක්, සහ ' +
    'පුද්ගලයෙක් හෝ පාර්සලයක් යන ටොගලයක් පෙන්වයි.',
  'www.guide.p07.step7':
    'ඒවාට පහළින් එතැනට යන ක්‍රම ඇත: තිබේ නම් මුලින්ම මහජන මාර්ග, ඉන්පසු ඉල්ලුම මත ' +
    'වාහන ඒවායේ ගාස්තු සමඟ. ඒවා කියවන ආකාරය 8 වන පරිච්ඡේදයයි.',
  'www.guide.p07.step8':
    'දැන් වෙන් කරන්න රියදුරෙකු සෙවීම ආරම්භ කරයි, ඉන්පසු සිදු වන දේ 9 වන පරිච්ඡේදයයි.',
  'www.guide.p07.callout.openMaps':
    'සිතියම සහ සෙවුම් කොටුව යනු MageRide හි තමන්ගේම සේවාදායකවල ඇති OpenStreetMap ' +
    'දත්තයි. එය ඉතිරි කිරීමක් නොව තීරණයකි: වාණිජ සිතියම් බලපත්‍රයක් නැත, පරිශීලකයෙකුට ' +
    'ගාස්තුවක් නැත, ඔබේ ගමනාන්තය ටයිප් වන්නේ සිතියම් සමාගමකට නොව MageRide හි ' +
    'තමන්ගේම සෙවුමටය.',
  'www.guide.p07.callout.routeNumber':
    'මාර්ග අංකයක් යනු ගමනාන්තයක් නොවේ. ඔබ 138 අල්ලා ගැනීමට උත්සාහ කරන්නේ නම්, ඔබට ' +
    'ගොස් නැවතීමට අවශ්‍ය තැන ඇතුළත් කරන්න — 138 ද ඇතුළුව එයට සේවය කරන මහජන මාර්ග ' +
    'ලැයිස්තුගත කරන්නේ මීළඟ තිරයයි.',

  // Chapter 8 · Choosing a vehicle, and the fare
  'www.guide.p08.title': 'වාහනයක් තෝරා ගැනීම, සහ ගාස්තුව',
  'www.guide.p08.summary':
    'ඔබ යන්නේ කොහේදැයි MageRide දැනගත් පසු, ඔබව එතැනට ගෙන යා හැක්කේ කුමක්ද සහ එක් ' +
    'එකකට කොපමණ වැය වේද යන්න, ඔබ කිසිවක් වෙන් කිරීමට පෙර පෙන්වයි. එම අංකය සෑදී ඇත්තේ ' +
    'කුමකින්ද යන්න මෙම පරිච්ඡේදයයි.',
  'www.guide.p08.step1':
    'ඉල්ලුම මත විකල්ප යනු වාහන වර්ගයකට එකක් බැගින් කාඩ්පත් ය: යතුරුපැදිය, ත්‍රීරෝද රථය, ' +
    'ෆ්ලෙක්ස්, සෙඩාන් රථය, මිනි වෑන් රථය සහ වෑන් රථය. ට්‍රක් සහ මිනි ට්‍රක් රථද ඇත, ' +
    'නමුත් ඒවා මිනිසුන් නොව පාර්සල් ගෙන යයි.',
  'www.guide.p08.step2':
    'සෑම කාඩ්පතක්ම එක් අංකයක් දරයි, එය ආරම්භක මිලක් නොව මෙම ගමන සඳහා එම වාහනයේ මුළු ' +
    'ගාස්තුවයි.',
  'www.guide.p08.step3':
    'එම අංකය එන්නේ ප්‍රකාශිත ගාස්තු වගුවකිනි: පළමු කිලෝමීටරයට අයකිරීමක් සහ ඉන් පසු ' +
    'සෑම කිලෝමීටරයකටම අනුපාතයක්, දෙකම වාහන වර්ගය අනුව සකසා ඇත. අඩුම වැය වන්නේ ' +
    'යතුරුපැදියටය, වැඩිම වන්නේ වෑන් රථයටය.',
  'www.guide.p08.step4':
    'කාර්යබහුල වේලාවේ සහ රාත්‍රී අනුපාත දැනටමත් එම මුළු අගය තුළ ඇත. යෙදුම කොටස් ' +
    'ලැයිස්තුවක් වෙනුවට එක් අංකයක් පෙන්වයි; බෙදා දැක්වීමක් දිස් වන්නේ අවසානයේ ගමන් ' +
    'සාරාංශයේය.',
  'www.guide.p08.step5':
    'කාඩ්පත් හිතාමතාම නොපෙන්වන දෙය නම් "මිනිත්තු හතරක් දුරින්" හෝ ඔබ වෙත ඇති දුරයි, ' +
    'මන්ද තවම කිසිදු රියදුරෙකු ගැළපී නැති අතර එවැනි ඕනෑම දෙයක් අනුමානයක් වන නිසාය.',
  'www.guide.p08.step6':
    'එකම ගමනාන්තයට යන මහජන මාර්ග ඉල්ලුම මත කාඩ්පත්වලට ඉහළින් ඇත, ඒවාට කිසිසේත් ' +
    'ගාස්තුවක් නැත, මන්ද A ක්‍රමය නොමිලේ නිසාය — එය 4 වන පරිච්ඡේදයයි.',
  'www.guide.p08.step7':
    'වෙන් කිරීමට පෙර ඔබ ගෙවන ආකාරය තෝරන්න. පෙරනිමිය මුදල් ය. ඔබේ MageRide පසුම්බියේ ' +
    'ශේෂයෙන්ද ගෙවිය හැක, නැතහොත් ගමන අවසානයේ රියදුරාගේම බැංකු QR කේතය පරිලෝකනය කර ' +
    'ගෙවිය හැක, තුනෙන් කිසිවක් ගාස්තුවට කිසිවක් එකතු නොකරයි. ඒ වෙනුවට පාර්සලයක් ' +
    'භාරදීමේදී මුදල් ගෙවීමට යැවිය හැක, එවිට එය ළඟා වූ විට මුදල් එකතු කරයි.',
  'www.guide.p08.step8':
    'ගාස්තුව කුමක් වුවත්, එයින් MageRide කිසිවක් ගන්නේ නැත. ගාස්තුවකින් කොමිසයක් නැත: ' +
    'කාඩ්පතේ ඇති අංකය රියදුරාට ලැබෙන මුදලයි.',
  'www.guide.p08.callout.tariffChanges':
    'ගාස්තු වගුව සකසා සමාලෝචනය කරන්නේ MageRide පරිපාලකයන් වන අතර එය වෙනස් විය හැක. ' +
    'කාඩ්පතේ ඇති සංඛ්‍යාව ඔබ එය බලන මොහොතේ බලපැවැත්වෙන එකයි, ගමනකට රුපියල් ප්‍රමාණයක් ' +
    'මෙම පිටුව සඳහන් නොකරන්නේ ඒ නිසාය.',
  'www.guide.p08.callout.estimateVsFinal':
    'කාඩ්පතේ ගාස්තුව ගණනය කරන්නේ ඔබේ ලක්ෂ්‍ය දෙක අතර මාර්ගයේ දුර අනුවය. අවසාන ගාස්තුව ' +
    'යනු ඇත්තටම ගමන් කළ දුර මත එම ගාස්තු වගුවමය, වේදිකාවේ සීමාව ඉක්මවන වෙනසක් හුදෙක් ' +
    'අය කිරීම වෙනුවට සමාලෝචනය සඳහා පුද්ගලයෙකු ඉදිරියේ තබයි.',

  // =========================================================================
  // S09 · the passenger guide, chapters 9–16 — translated in S12.
  // =========================================================================

  // Chapter 9 · Waiting for a driver
  'www.guide.p09.title': 'රියදුරෙකු එනතුරු බලා සිටීම',
  'www.guide.p09.summary':
    'දැන් වෙන් කරන්න තට්ටු කිරීම සහ රියදුරෙකුගේ මුහුණ ඔබේ තිරයේ දිස් වීම අතර, MageRide ' +
    'තරමක් නිශ්චිත දෙයක් කරමින් සිටී. ඔබේ අවධානය මිනිත්තු දෙකක් වටී, මන්ද ඔබ එහි ' +
    'සිටගෙන සිටින විට ඔබට අපේක්ෂා කළ හැක්කේ කුමක්ද, නොහැක්කේ කුමක්ද යන්න එයින් ' +
    'පැහැදිලි වන නිසාය.',
  'www.guide.p09.step1':
    'දැන් වෙන් කරන්න සිතියම වෙනුවට රියදුරෙකු සොයන තිරයක් තබයි: ස්පන්දනයක්, ඔබ තෝරාගත් ' +
    'වාහන වර්ගය, සහ ආපසු ගණන් කිරීමක්.',
  'www.guide.p09.step2':
    'ඉල්ලීම යන්නේ එකවර සියලු දෙනාට නොව වරකට එක් රියදුරෙකුටය. MageRide අසල හොඳම ' +
    'අපේක්ෂකයා තෝරා එම රියදුරාට පමණක් ඉල්ලීම විවෘතව තබයි.',
  'www.guide.p09.step3':
    'එක් එක් රියදුරාට පිළිගැනීමට තත්පර පහළොවක් ඇත. ඔවුන් නොපිළිගන්නේ නම්, ඉල්ලීම ' +
    'කෙළින්ම ඊළඟ රියදුරාට යයි, ලැයිස්තුව දිගේ එසේ දිගටම යයි.',
  'www.guide.p09.step4':
    'ලැයිස්තුව පිළිවෙළට ඇත්තේ රියදුරු කොපමණ ළඟද, ඔවුන්ගේ රියදුරු මට්ටම, සහ ඔවුන්ගේ ' +
    'වාහනය ඔබ ඉල්ලූ වර්ගයද යන්න අනුවය. රියදුරන් ලංසු තබන්නේ නැත, ඔබ ගෙවන්නේ කුමක්දැයි ' +
    'ඔවුන් අතර වෙනස්ව දැකිය නොහැක — ඔබ වෙන් කරන්න තට්ටු කිරීමට පෙරම ගාස්තුව ස්ථිර විය.',
  'www.guide.p09.step5':
    'මුළු සෙවීම මිනිත්තු දෙකක් ධාවනය වේ. එවකට කිසිවෙක් පිළිගෙන නැත්නම් ඔබට ඒ බව කියන ' +
    'අතර, ඉල්ලීම තනිවම අවලංගු වී, යෙදුම නැවත උත්සාහ කිරීමට ඉදිරිපත් වේ.',
  'www.guide.p09.step5.note':
    'නැවත උත්සාහ කිරීම යනු පෝලිමේ ස්ථානයක් නොව නැවුම් සෙවීමකි. වෙනස් වාහන වර්ගයක් ' +
    'තෝරා ගැනීම බොහෝ විට උපකාරී වේ, මන්ද එය කුමන රියදුරන් සුදුසුකම් ලබනවාද යන්නම ' +
    'වෙනස් කරන නිසාය.',
  'www.guide.p09.step6':
    'තත්පර පහළොව ගෙවී යාමට ඉඩ දෙන, හෝ නැත තට්ටු කරන රියදුරෙකුට ඒ සඳහා දඬුවමක් නැත. ' +
    'ඔවුන් තවත් ගමනක් අවසන් කරමින් සිටිය හැක, නැතහොත් නිශ්චිත තැනකට යමින් සිටිය හැක ' +
    '— ගෙදර යන රියදුරෙකුට එකම දිශාවට යන කුලී පමණක් ගෙන එන පෙරහනක් සැකසිය හැක. ' +
    'ප්‍රතික්ෂේප කිරීමක් ඔබ ගැන වන්නේ කලාතුරකිනි.',
  'www.guide.p09.step7':
    '"මෙය මෙතරම් වේලා ගන්නේ ඇයි" යන්නට අවංක පිළිතුර සාමාන්‍යයෙන් වන්නේ එම මොහොතේ ඔබ ' +
    'අසල සුදුසු රියදුරන් ස්වල්පයක් සිටීමයි. දවසේ වේලාව සහ ඔබ කොපමණ ඈතද යන්න, තිරයේ ' +
    'ඔබට වෙනස් කළ හැකි ඕනෑම දෙයකට වඩා වැදගත් වේ.',
  'www.guide.p09.step8':
    'අවලංගු කරන්න මෙම තිරයේ ඇති අතර එය නොමිලේය. රියදුරෙකු කිසිදා නොසොයාගත් ගමනකට ' +
    'කිසිවක් අය නොකෙරේ.',
  'www.guide.p09.step9':
    'රියදුරෙක් පිළිගත් මොහොතේම මෙම තිරය ගමන් තිරය බවට පත් වේ — ඔවුන්ගේ නම, ඔවුන්ගේ ' +
    'වාහනය, සහ සජීවී සිතියමක්. එය ඊළඟ පරිච්ඡේදයයි.',
  'www.guide.p09.callout.twoMinutes':
    'මිනිත්තු දෙක යනු එක් රියදුරෙකුගේ වාරය නොව මුළු සෙවීමයි. ඉල්ලීම ඉදිරියට යාමට පෙර ' +
    'එක් එක් රියදුරාට පිළිතුරු දීමට ඇත්තේ තත්පර පහළොවකි. මිනිත්තු දෙක ඉකුත් වුවහොත්, ' +
    'ඉල්ලීම විවෘතව තැබීම වෙනුවට MageRide ඔබට කියා එය අවලංගු කරයි.',
  'www.guide.p09.callout.cancelFree':
    'රියදුරෙක් පිළිගැනීමට පෙර අවලංගු කිරීමට සෑම විටම කිසිවක් වැය නොවේ. රියදුරෙක් පිළිගත් ' +
    'පසු නීති වෙනස් වන අතර, 10 වන පරිච්ඡේදය එය හරියටම කෙසේදැයි දක්වයි.',

  // Chapter 10 · During the ride
  'www.guide.p10.title': 'ගමන අතරතුර',
  'www.guide.p10.summary':
    'රියදුරෙක් පිළිගත් මොහොතේ සිට ඔබ බැස යන තුරු, එක් තිරයක් සියල්ල දරයි: රථය කොහේද, ' +
    'එය පදවන්නේ කවුද, ඔවුන්ට ළඟා වන්නේ කෙසේද, සහ අනතුරු ඇඟවීමක් කරන්නේ කෙසේද.',
  'www.guide.p10.step1':
    'රියදුරු කාඩ්පත ඔවුන්ගේ ඡායාරූපය සහ නම, ඔවුන්ගේ ශ්‍රේණිගත කිරීම, වාහනය සහ එහි ' +
    'ලියාපදිංචි අංකය, සහ ඔවුන් කොපමණ මිනිත්තු ගණනක් දුරින්ද යන්න පෙන්වයි.',
  'www.guide.p10.step2':
    'ඊට යටින් ඇති සිතියම සජීවීය. රියදුරාගේ සලකුණ ඔවුන් ගමන් කරන විට, ඔබ වෙත එනතුරුත්, ' +
    'ඉන්පසු ඔබ යන තැනට යනතුරුත් ගමන් කරයි.',
  'www.guide.p10.step3':
    'කාඩ්පතේ කෙටි ආරම්භක කේතයක් ඇත. රියදුරු පැමිණි විට එය කියවා දෙන්න හෝ ඔවුන්ට ' +
    'පෙන්වන්න — ඔවුන් එය ඇතුළත් කළ පසුව පමණක් ගමන ආරම්භ වේ, ඔබ නිවැරදි වාහනයට නැගුණු ' +
    'බව යෙදුම දන්නේ එලෙසිනි.',
  'www.guide.p10.step4':
    'අමතන්න තට්ටු කිරීමෙන් ඔබට තේරීම් දෙකක් ලැබේ. නොමිලේ ඇමතුමක් යෙදුම හරහා අන්තර්ජාලයෙන් ' +
    'යන අතර මිනිත්තු භාවිත නොකරයි. සාමාන්‍ය ඇමතුමක් යනු රියදුරාගේම අංකයට කෙළින්ම ' +
    'ඇමතූ සාමාන්‍ය දුරකථන ඇමතුමකි.',
  'www.guide.p10.step4.note':
    'දුර්වල දත්ත මත නොමිලේ ඇමතුමක් සම්බන්ධ කළ නොහැකි නම්, ඒ වෙනුවට එම පුද්ගලයාටම ' +
    'සාමාන්‍ය ඇමතුම ගැනීමට යෙදුම ඉදිරිපත් වේ. ඔබ පසුගිය වර තෝරාගත් එක එය මතක තබා ' +
    'ගනී.',
  'www.guide.p10.step5':
    'රියදුරු පිළිගත් මොහොතේ සිට ඔබටත් රියදුරාටත් එකිනෙකාගේ සැබෑ ජංගම දුරකථන අංක පෙනේ. ' +
    'මෙය සඟවා හෝ වෙස්වලාගෙන නැති අතර, ඔබ ලියාපදිංචි වන විටත් ඔබේ පළමු ඇමතුමේදීත් ' +
    'MageRide ඔබට ඒ බව කියයි.',
  'www.guide.p10.step6':
    'හදිසි බොත්තම ගමන් කාඩ්පතේ ඇත. එය ඔබෙන් තහවුරු කිරීමක් ඉල්ලා, ඉන්පසු ඔබේ ස්ථානය ' +
    'සහ ගමන් විස්තර ඔබ සුරැකි හදිසි සම්බන්ධතාවට කෙටි පණිවිඩයකින් යවන අතර, ඒ සමඟම ' +
    'MageRide වෙතද අනතුරු ඇඟවීම නගයි.',
  'www.guide.p10.step7':
    'ඔබේ දුරකථනයේ සංඥාව නැති වුවහොත් රියදුරාගේ සලකුණ නතර වන අතර බැනරයක් ඒ බව කියයි. ' +
    'කිසිවක් නැති වී නැත — සම්බන්ධතාවය නැවත පැමිණි විට සිතියම යාවත්කාලීන වේ.',
  'www.guide.p10.step8':
    'රියදුරු ගමන අවසන් කළ විට ඔබ සාරාංශයටත්, ඉන්පසු ගෙවීමටත් යයි, එය ඊළඟ පරිච්ඡේදයයි.',
  'www.guide.p10.callout.realNumbers':
    'MageRide දුරකථන අංක සඟවන්නේ නැත. රියදුරෙක් පිළිගත් පසු, ඔබට ඔවුන්ගේ සැබෑ ජංගම ' +
    'දුරකථන අංකය පෙනෙන අතර ඔවුන්ට ඔබේ අංකය පෙනේ, එවිට වැරදුණු බාරගැනීමක් ඔබ දෙදෙනාගෙන් ' +
    'කෙනෙකුට හදාගත හැක. රියදුරෙකු පවරන්නට පෙර අවලංගු කළ ගමනකට අංක කිසිදා නොපෙන්වයි. ' +
    'ඔබ වෙනත් අයෙකුට ගමන වෙන් කළේ නම්, රියදුරාට පෙනෙන්නේ එම පුද්ගලයාගේ අංකය මිස ' +
    'කිසිවිටෙක ඔබේ අංකය නොවේ.',
  'www.guide.p10.callout.cancelAfterAccept':
    'රියදුරෙක් පිළිගත් පසු අවලංගු කිරීමට රු. 50ක් වැය වන අතර, එය එවේලේම අය කිරීම ' +
    'වෙනුවට ඔබේ ඊළඟ ගමනේ ගාස්තුවට එකතු කෙරේ. එය MageRide තබා ගන්නා ගාස්තුවක් නොවේ — ' +
    'එය යන්නේ ඔබ අවලංගු කළ, පිළිගත් ගමනේ රියදුරාටය. එකදිගට මෙවැනි තුනක් සිදු කළොත් ' +
    'ශේෂය පියවන තුරු වෙන් කිරීම නවත්වන අතර, ඔබ ගමනක් සම්පූර්ණ කළ මොහොතේ ගණන ශුන්‍ය ' +
    'වේ.',
  'www.guide.p10.callout.sosContact':
    'හදිසි බොත්තමට කිසිවක් යැවීමට පෙර ඔබේ පැතිකඩේ හදිසි සම්බන්ධතාවක් සුරැකිය යුතුය. ' +
    'ගමනක් අතරතුර නොව දැන් එකක් සකසන්න.',

  // Chapter 11 · Paying
  'www.guide.p11.title': 'ගමනට ගෙවීම',
  'www.guide.p11.summary':
    'මුදල්, ඔබේ MageRide ශේෂය, නැතහොත් රියදුරාගේම බැංකු QR කේතය. තුනෙන් කිසිවකට අනෙක් ' +
    'ඒවාට වඩා වැය නොවේ, මුදල් සියල්ල යන්නේ රියදුරාටය.',
  'www.guide.p11.step1':
    'ඔබ ගෙවන ආකාරය 8 වන පරිච්ඡේදයේදී, වෙන් කිරීමට පෙර තෝරාගත්තා. ගමන අවසානයේද එය ' +
    'තවමත් වෙනස් කළ හැක.',
  'www.guide.p11.step2':
    'මුදල් යනු පෙරනිමිය වන අතර එයට තිරයක් අවශ්‍යම නැත. ඔබ රියදුරාට මුදල් භාර දෙන අතර ' +
    'ගමන වැසේ.',
  'www.guide.p11.step3':
    'ඔබේ MageRide ශේෂයෙන් ගෙවීම එක් තට්ටුවකි. මුදල ඔබේ ශේෂයෙන් රියදුරාගේ ශේෂයට වහාම ' +
    'ගමන් කරයි, තහවුරු කිරීමට කිසිවක් නැත, බලා සිටීමට කිසිවක්ද නැත. ඔබට කැමති විටෙක ' +
    'යෙදුමෙන්, කාඩ්පතකින් එම ශේෂය පුරවා ගත හැක.',
  'www.guide.p11.step4':
    'තෙවන ක්‍රමය නම් රියදුරාගේම QR කේතය පරිලෝකනය කිරීමයි — මුද්‍රිත, ජනේල ස්ටිකරයක ' +
    'හෝ ඔවුන්ගේ තිරයේ — සහ ඕනෑම වෙළඳසැලකට ගෙවන ආකාරයටම ඔබේ බැංකු යෙදුමෙන් එයට ගෙවීමයි.',
  'www.guide.p11.step4.note':
    'එම කේතය අයිති රියදුරාගේම බැංකු ගිණුමටය. එතැනට යන අතරමගදී මුදල් කිසිවිටෙක MageRide ' +
    'හරහා යන්නේ නැත.',
  'www.guide.p11.step5':
    'එය බැංකුවෙන් බැංකුවට යන නිසා, එය ළඟා වූ බව MageRide කිසිදා දැනගන්නේ නැත. එබැවින් ' +
    'ගෙවීමෙන් පසු ඔබ "මම ගෙවුවා" තට්ටු කරයි — කැමති නම් වාර්තාවක් ලෙස ඔබේ බැංකුවේ ' +
    'රිසිට්පතේ තිර රුවක් අමුණාද ගත හැක.',
  'www.guide.p11.step6':
    'ඉන්පසු රියදුරු එය ලැබුණු බව තහවුරු කරන අතර ගමන වැසේ. ඔබ දැනටමත් ගොස් ඇත්නම් ' +
    'රියදුරෙකුට තනිවම තහවුරු කළ හැක. ඔවුන් තහවුරු නොකරන්නේ නම්, යෙදුම ඔවුන්ට මතක් ' +
    'කරන අතර, "උදව් ලබා ගන්න" සබැඳියක් ඔබේ තිර රුව අමුණා සහාය ටිකට්පතක් විවෘත කරයි.',
  'www.guide.p11.step7':
    'සාරාංශය මුළු මුදල, දුර, සහ ගාස්තුව සෑදුණු ආකාරය පෙන්වයි — පළමු කිලෝමීටරය, ' +
    'කිලෝමීටරයට වන කොටස, සහ කාර්යබහුල හෝ රාත්‍රී අනුපාතයක් තිබුණේ නම් එයද.',
  'www.guide.p11.step8':
    'සෑම ගමනක්ම එහි රිසිට්පත තබා ගනී. ඕනෑම වේලාවක ඔබේ ගමන් ඉතිහාසයෙන් එය නැවත විවෘත ' +
    'කළ හැක, එය 15 වන පරිච්ඡේදයයි.',
  'www.guide.p11.callout.noSurcharge':
    'ගමනකට ගෙවීමේ කිසිදු ක්‍රමයකට අමතර මුදලක් වැය නොවේ. මුදල්, ඔබේ ශේෂය සහ රියදුරාගේ ' +
    'QR යනු එකම අංකයයි, ඒ කිසිවකින් MageRide කොමිසයක් ගන්නේ නැත — ගාස්තුව යනු ' +
    'රියදුරාට ලැබෙන මුදලයි. කාඩ්පත් සැකසුම් ගාස්තුවක් අය කරන්නේ ඇත්තටම ගෙවීම ලබන්නා ' +
    'MageRide වන තැන්වල පමණි, උදාහරණයක් ලෙස ඔබේ ශේෂය පිරවීමේදී.',
  'www.guide.p11.callout.attestation':
    'QR ගෙවීමක් සමථයට පත් වන්නේ MageRide හට එන බැංකු පණිවිඩයකින් නොව ඔබ දෙදෙනාම ඒ බව ' +
    'කීමෙනි — මන්ද ඔබ වෙනත් අයෙකුගේ ගිණුමකට ගෙවන විට එවැනි පණිවිඩයක් නොපවතී. ඔබ ' +
    'ගෙවූ බව කියා රියදුරු මුදල් කිසිදා නොලැබුණු බව කියන්නේ නම්, ගමන MageRide සහායට ' +
    'යන අතර, එහිදී පුද්ගලයෙක් එයටත් ඔබේ රිසිට්පතටත් බලයි. ඒ අතරතුර MageRide කිසිදු ' +
    'මුදලක් ගෙන යන්නේ හෝ රඳවා ගන්නේ නැත, මන්ද එය MageRide සතුව කිසිදා තිබුණේ නැති ' +
    'නිසාය.',

  // Chapter 12 · Sending a package
  'www.guide.p12.title': 'පාර්සලයක් යැවීම',
  'www.guide.p12.summary':
    'පාර්සලයක් ගමන් කරන්නේ පුද්ගලයෙක් ගමන් කරන ආකාරයටමය — එකම රියදුරන්, එකම බෙදාහැරීම, ' +
    'එකම ගාස්තු. වෙනස වන්නේ තවත් පුද්ගලයන් දෙදෙනෙකු සම්බන්ධ වීමයි: එය භාර දෙන අය සහ ' +
    'එය ලබා ගන්නා අය.',
  'www.guide.p12.step1':
    'වෙන් කිරීමේ තිරයේ, පුද්ගලයෙක් සිට පාර්සලයක් වෙත මාරු වන්න.',
  'www.guide.p12.step2':
    'ප්‍රමාණයක් තෝරන්න. කුඩා යනු කිලෝ පහක් පමණ දක්වා වන අතර පිටු බෑගයකට හෝ යතුරුපැදි ' +
    'පෙට්ටියකට ගැළපේ; මධ්‍යම යනු විස්සක් පමණ දක්වා වන අතර ත්‍රීරෝද රථයක් හෝ මෝටර් රථ ' +
    'ඩිකියක් අවශ්‍යය; විශාල යනු විස්සට වැඩි වන අතර වෑන් රථයක් හෝ ට්‍රක් රථයක් අවශ්‍යය. ' +
    'එක් එක් ප්‍රමාණයට යටින් ඇති ඉඟිය එය ගෙන යා හැක්කේ කුමන වාහනවලටදැයි කියයි.',
  'www.guide.p12.step3':
    'ඇතුළත ඇත්තේ කුමක්දැයි විස්තර කරන්න, ඉන්පසු ලබන්නාගේ නම සහ දුරකථන අංකය එක් කර, ' +
    'ගමනේ කෙළවර දෙකම සකසන්න — බාරගැනීම සහ බාරදීම.',
  'www.guide.p12.step3.note':
    'කෙළවර දෙකෙන් ඕනෑම එකක් ටයිප් කළ හැක, ලකුණක් ලෙස තැබිය හැක, නැතහොත් Google Maps ' +
    'සබැඳියකින් අලවා ගත හැක. බාරදීම සඳහා, එය ලබන්නාගෙන්ම බෙදාගන්නා ලෙසද ඉල්ලිය හැක.',
  'www.guide.p12.step4':
    'එයට ගෙවන ආකාරය තෝරන්න. මුදල්, ඔබේ MageRide ශේෂය සහ රියදුරාගේ QR යන සියල්ල ගමනකට ' +
    'මෙන්ම ක්‍රියා කරයි. පාර්සල් තවත් එකක් එකතු කරයි: භාරදීමේදී මුදල් ගෙවීම, එහිදී එය ' +
    'ලබන පුද්ගලයා දොරකඩදී රියදුරාට ගෙවයි.',
  'www.guide.p12.step5':
    'රියදුරෙක් මගදී සිටින විට ඔබට ඉලක්කම් හතරක බාරගැනීමේ කේතයක් ලැබේ. ඔවුන් පාර්සලය ' +
    'රැගෙන යන විට එය ඔවුන්ට දෙන්න — එය නොමැතිව එය බාරගත් බව සලකුණු කළ නොහැක.',
  'www.guide.p12.step6':
    'එය බාරගත් මොහොතේම ලබන්නාට ඒ බව කියනු ලැබේ. ඔවුන්ට MageRide තිබේ නම් දැනුම්දීමක් ' +
    'ලැබේ; නැත්නම්, ඔවුන්ගේ බ්‍රවුසරයේ සරල ලුහුබැඳීමේ පිටුවක් විවෘත කරන සබැඳියක් සහිත ' +
    'කෙටි පණිවිඩයක් ලැබේ. ඒ දෙකෙන්ම සිතියම සහ ඔවුන්ගේම ඉලක්කම් හතරක බාරදීමේ කේතය ' +
    'පෙන්වයි.',
  'www.guide.p12.step7':
    'ඔබ දෙදෙනාම එකම අදියර හතර බලයි: බාරගැනීම පොරොත්තුවෙන්, බාරගත්තා, මගදී, බාරදුන්නා. ' +
    'එය අවසන් කිරීමට රියදුරු දොරකඩදී බාරදීමේ කේතය ඇතුළත් කරයි.',
  'www.guide.p12.step8':
    'එය ලබා ගැනීමට කිසිවෙක් නැත්නම්, කේතයක් වෙනුවට පාර්සලය තැබූ තැනේ ඡායාරූපයක් සමඟ ' +
    'රියදුරාට බාරදීම සම්පූර්ණ කළ හැක.',
  'www.guide.p12.callout.fiveAttempts':
    'බාරගැනීමේ සහ බාරදීමේ කේත ඉලක්කම් හතරක් වන අතර එක් එකට උත්සාහ පහක් ලැබේ. වැරදි ' +
    'උත්සාහ පහකට පසු එම පියවර අගුළු වැටී, පාර්සලයක් වැරදි පුද්ගලයෙකුට භාර දීමට ඉඩ ' +
    'දෙනවා වෙනුවට MageRide සහායට යයි. කේතය පරෙස්සමින් කියවා දෙන්න.',
  'www.guide.p12.callout.cod':
    'භාරදීමේදී මුදල් ගෙවීමේදී ලබන්නා ගෙවන්නේ ඔබට නොව රියදුරාටය, පාර්සලය භාර දුන් විට ' +
    'රියදුරු "බෙදාහැරීම සම්පූර්ණයි" තට්ටු කරයි. එම මුදල් දිනකට පසුවත් නොලැබී ඇත්නම්, ' +
    'MageRide සහාය එය බැලීම සඳහා බෙදාහැරීම සලකුණු කෙරේ.',
  'www.guide.p12.callout.noAppNeeded':
    'ඔබේ පාර්සලය ලබන පුද්ගලයාට MageRide ස්ථාපනය කර තිබීම අවශ්‍ය නැත, ගිණුමක්ද අවශ්‍ය ' +
    'නැත. ඔවුන්ගේ කෙටි පණිවිඩයේ සබැඳිය ඔවුන්ට සිතියම, තත්ත්වය සහ ඔවුන්ගේ බාරදීමේ කේතය ' +
    'පෙන්වන අතර, පාර්සලය ළඟා වී මොහොතකට පසු ක්‍රියා විරහිත වේ.',

  // Chapter 13 · Booking for someone else
  'www.guide.p13.title': 'වෙනත් අයෙකුට වෙන් කිරීම',
  'www.guide.p13.summary':
    'ඔබේ මවට, සගයෙකුට හෝ MageRide ගැන කිසිදා අසා නැති අමුත්තෙකුට ඔබට ගමනක් වෙන් කළ ' +
    'හැක. ඔවුන්ට යෙදුම, ගිණුමක්, හෝ ඒ දෙකෙන් එකක් ධාවනය කළ හැකි දුරකථනයක් අවශ්‍ය නැත.',
  'www.guide.p13.step1':
    'වෙන් කිරීමේ තිරයේ, මට සිට වෙනත් අයෙකුට වෙත මාරු වී, ඔවුන්ගේ නම සහ දුරකථන අංකය ' +
    'ඇතුළත් කරන්න, නැතහොත් ඔබේ සම්බන්ධතාවලින් ඔවුන් තෝරන්න.',
  'www.guide.p13.step2':
    'ඔවුන් රැගෙන යන්නේ කොහෙන්දැයි සකසන්න. එය ටයිප් කළ හැක, සිතියමේ ලකුණක් තැබිය හැක, ' +
    'නැතහොත් කවුරුන් හෝ එවූ Google Maps සබැඳියක් ඇලවිය හැක.',
  'www.guide.p13.step3':
    'හතරවන ක්‍රමයක් ඇත, නිවැරදිම එක එයයි: ඔවුන්ගෙන් අසන්න. MageRide ඔවුන්ට ඔවුන්ගේ ' +
    'බාරගැනීමේ ස්ථානය සඳහා ඉල්ලීමක් යවයි, ඔවුන් සිතියමක ලකුණක් සීරුමාරු කරයි, ඔවුන් ' +
    'තහවුරු කරන ලක්ෂ්‍යය ඔබේ තිරයේ පිරී යයි.',
  'www.guide.p13.step3.note':
    'ඔවුන් සතුව දැනටමත් MageRide ඇත්නම් ඉල්ලීම යෙදුමට එයි. නැත්නම්, එය සබැඳියක් සහිත ' +
    'කෙටි පණිවිඩයක් ලෙස පැමිණෙන අතර ඔවුන්ගේ බ්‍රවුසරයේද එලෙසම ක්‍රියා කරයි.',
  'www.guide.p13.step4':
    'පිළිතුරු දීමට ඔවුන්ට මිනිත්තු පහක් ඇති අතර, ඔවුන් හුදෙක් ප්‍රතික්ෂේප කළද හැක. ' +
    'ඔවුන් ප්‍රතික්ෂේප කළොත්, නොසලකා හැරියොත් හෝ කාලය ඉකුත් වුවහොත්, ඔබම ලකුණ තබා ' +
    'වෙන් කිරීම දිගටම කරගෙන යන්න.',
  'www.guide.p13.step5':
    'එතැන් සිට ඔබ ඔබටම වෙන් කරන ආකාරයටම හරියටම වෙන් කරයි — වාහනය, ගාස්තුව, දැන් වෙන් ' +
    'කරන්න. මෙය වෙනත් අයෙකුට කරන වෙන් කිරීමක් බව රියදුරාට කියන අතර ගමන් කරන්නාගේ නම ' +
    'ලබා දේ.',
  'www.guide.p13.step6':
    'රියදුරෙක් පිළිගත් වහාම, ගමන් කරන්නාට ලුහුබැඳීමේ සබැඳියක් සහිත කෙටි පණිවිඩයක් ' +
    'ලැබේ. එය රියදුරාගේ නම සහ ඡායාරූපය, වාහනය සහ එහි අංක තහඩුව, පැමිණීමේ වේලාව සහිත ' +
    'සජීවී සිතියමක්, සහ කියවා දිය යුතු ආරම්භක කේතය සහිත පිටුවක් විවෘත කරයි.',
  'www.guide.p13.step7':
    'එම පිටුවේ රියදුරාට තට්ටු කර අමතන බොත්තමක් සහ හදිසි බොත්තමක් ඇත. ඔවුන් හදිසි ' +
    'බොත්තම එබුවහොත්, අනතුරු ඇඟවීම කෙටි පණිවිඩයකින් එවන්නේ ඔබටය — ගමන සකස් කළ ' +
    'පුද්ගලයාටය.',
  'www.guide.p13.step8':
    'සබැඳිය එම එක් ගමනට බැඳී ඇති අතර ගමන අවසන් වූ විට ක්‍රියා විරහිත වේ. ඉන් පසුව එය ' +
    'කිසිසේත් කිසිවක් නොපෙන්වයි — මාර්ගයක් නැත, රියදුරෙක් නැත, ඉතිහාසයක්ද නැත.',
  'www.guide.p13.callout.declineSendsNothing':
    'ඔබ වෙන් කරන පුද්ගලයා තම ස්ථානය සඳහා වූ ඉල්ලීම ප්‍රතික්ෂේප කළොත්, MageRide ඔබට ' +
    'කිසිසේත් කිසිවක් යවන්නේ නැත. ආසන්න පිහිටීමක් නොවේ, අවසන් වරට දන්නා එකක්ද නොවේ — ' +
    'කිසිවක්ම නොවේ. ඔවුන්ට පෙනෙන පිටුව එය ඒ වචනවලින්ම කියන අතර, ඔවුන් යෙදුමෙන් ' +
    'පිළිතුරු දුන්නත් බ්‍රවුසරයකින් පිළිතුරු දුන්නත් එය එකම පොරොන්දුවයි.',
  'www.guide.p13.callout.riderNumberOnly':
    'එකිනෙකා සොයාගත හැකි වන පරිදි රියදුරාට ලබා දෙන්නේ ගමන් කරන්නාගේ දුරකථන අංකය මිස ' +
    'කිසිවිටෙක ඔබේ අංකය නොවේ. වෙන් කළේ ඔබ වුවත්, සමහර ගෙවීම් ක්‍රමවලදී අය කරන්නේ ' +
    'ඔබෙන් වුවත් එසේමය.',
  'www.guide.p13.callout.cashIsTheRiders':
    'ඔබ මුදල් තෝරන්නේ නම්, ගමන අවසානයේ රියදුරාට ගෙවන්නේ ගමන් කරන්නාය, ඔවුන් රථයට ' +
    'නැගීමට පෙර ඔවුන්ගේ ලුහුබැඳීමේ පිටුව එම මුදල ඔවුන්ට කියයි. දොරකඩදී ඔවුන්ට එය ' +
    'දැනගන්නට තැබීම වෙනුවට, වෙන් කිරීමට පෙර ඔවුන් සමඟ ඒ ගැන තීරණය කරන්න.',

  // Chapter 14 · Scheduling a ride
  'www.guide.p14.title': 'පසුවට ගමනක් වෙන් කිරීම',
  'www.guide.p14.summary':
    'නියමිත ගමනක් යනු උදේ හයේ ගුවන්තොටුපොළ ගමනට, රෝහල් වේලාවට, එය සිදු වන අතරතුර ' +
    'සකසමින් සිටීමට ඔබට අවශ්‍ය නැති දෙයටය.',
  'www.guide.p14.step1':
    'දැන් ගමනකට කරන ආකාරයටම හරියටම ඔබේ ගමනාන්තය සකසා වාහනයක් තෝරන්න. දැන් වෙන් කරන්න ' +
    'අසල කාලසටහන ඇත.',
  'www.guide.p14.step2':
    'කාලසටහන් තිරය සියල්ලට පෙර ගමනාන්තයක් ඉල්ලන අතර, ඔබ එකක් සකසන තුරු තහවුරු කරන්න ' +
    'අළු පැහැයෙන් තිබේ. එය දෝෂයක් නොව හිතාමතාය.',
  'www.guide.p14.step3':
    'ඔබේ බාරගැනීමේ ස්ථානය ආරම්භ වන්නේ ඔබ දැන් සිටගෙන සිටින තැනින් වන අතර ඕනෑම තැනකට ' +
    'වෙනස් කළ හැක — අද සවස සෝෆාවේ සිට හෙට උදෑසන සකසන විට ප්‍රයෝජනවත් වේ.',
  'www.guide.p14.step4':
    'දිනය සහ වේලාව තෝරන්න. දැනටමත් ගෙවී ගිය වේලාවන් තෝරාගත නොහැක. තහවුරු කරන්න එය ' +
    'සුරකියි.',
  'www.guide.p14.step4.note':
    'සුරැකි වෙන් කිරීම ඔබේ ගමන්වල නියමිත යටතේ දිස් වන අතර, එහිදී එය නැවත බැලිය හැක ' +
    'හෝ අවලංගු කළ හැක.',
  'www.guide.p14.step5':
    'MageRide ඔබට දෙවරක් මතක් කරයි — පැයකට පෙර සහ නැවතත් මිනිත්තු පහළොවකට පෙර.',
  'www.guide.p14.step6':
    'නියමිත ගමනක් යනු කලින් වෙන් කළ ඉල්ලුම මත වාහනයක් වන අතර, ගාස්තුව අන් ඕනෑම එකක් ' +
    'මෙන්ම ගණනය වේ — එකම ගාස්තු වගුව, එකම මාර්ගය මත, වාහන වර්ගය අනුව. කලින් වෙන් ' +
    'කිරීමෙන් වැඩිපුර වැය නොවේ, අඩුවෙන්ද වැය නොවේ.',
  'www.guide.p14.step7':
    'ඔබේ බාරගැනීමට හොඳ වේලාවකට පෙර එය සබැඳිව සිටින රියදුරන්ට පෙනෙන පුවරුවකට යන අතර, ' +
    'එහිදී ඔවුන් තමන්ට එය අවශ්‍ය බව කලින් කියයි. ඔවුන්ට එය පුවරුවෙන් ගත නොහැක — එය ' +
    'අත එසවීමේ ක්‍රමයක් මිස ඊට වඩා කිසිවක් නොවේ.',
  'www.guide.p14.step8':
    'ඔබේ බාරගැනීමට පැය භාගයකට පෙර, ගමන එම රියදුරන්ට ඉදිරිපත් වේ, ළඟම අය පළමුව සහ ' +
    'ඔවුන් අතරින් වඩා පළපුරුදු අය අනෙක් අයට පෙර. එතැන් සිට එය 9 වන පරිච්ඡේදයේ විස්තර ' +
    'කළ බලා සිටීම ඇතුළුව, දැන් වෙන් කළ ගමනක් මෙන්ම හරියටම ක්‍රියා කරයි.',
  'www.guide.p14.step9':
    'නියමිත ටැබයෙන් ඔබ අවලංගු කළොත්, වෙනත් සැලසුම් කර ගැනීමට කාලය ඇතිවම රියදුරාට ඒ බව ' +
    'කියනු ලැබේ.',
  'www.guide.p14.callout.notAReservation':
    'කාලසටහන්ගත කිරීම වේලාවක් රඳවා ගනී, වාහනයක් නොවේ. පැය භාගයකට පමණ පෙර වන තුරු ඔබේ ' +
    'වෙන් කිරීමට රියදුරෙකු පවරන්නේ නැත, එවිට කිසිවෙක් නොපිළිගන්නේ නම් ඔබට ඒ බව කියනු ' +
    'ලැබේ — එවේලේ වෙන් කරන ගමනකට මෙන්ම. ඔබට අත්හැරිය නොහැකි දෙයකට, දෙවන උත්සාහයකට ' +
    'ඉඩ ලැබෙන තරම් කලින් වෙන් කරන්න.',
  'www.guide.p14.callout.reminders':
    'මතක් කිරීම් දෙකක් ස්වයංක්‍රීයව එයි: පැයකට පෙර, සහ මිනිත්තු පහළොවකට පෙර. ඔබට ඒවා ' +
    'සැකසීමට අවශ්‍ය නැත.',

  // Chapter 15 · Saved places, ratings and your trips
  'www.guide.p15.title': 'සුරැකි ස්ථාන, ශ්‍රේණිගත කිරීම් සහ ඔබේ ගමන්',
  'www.guide.p15.summary':
    'කාලයත් සමඟ එකතු වන MageRide හි කොටස් — ඔබ ටයිප් කිරීම නවත්වන ලිපින, ඔබ තබන ' +
    'ශ්‍රේණිගත කිරීම්, සහ ඔබ ගිය සෑම තැනකම වාර්තාව.',
  'www.guide.p15.step1':
    'ගෙදර සහ රැකියාව සකසන්නේ ලිපිනයක් ටයිප් කිරීමෙන් නොව සිතියමේ ලකුණක් තැබීමෙනි. ඔබ ' +
    'තෝරාගත් ලක්ෂ්‍යයෙන් MageRide ලිපිනය ඔබට කියවා පෙන්වයි, එවිට එය ඔබ අදහස් කළ තැනටම ' +
    'වැටුණාදැයි ඔබට දැකිය හැක.',
  'www.guide.p15.step2':
    'වෙනත් ඕනෑම ස්ථානයක් එලෙසම, ලිපින පේළි තුනකින් සහ ඔබේම තේරීමේ ලේබලයකින් සුරැකේ — ' +
    '"ව්‍යායාම්", "අම්මාගේ ගෙදර", "කාර්යාලය".',
  'www.guide.p15.step3':
    'ගෙදර සහ රැකියාව ඇතුළුව ඒ සියල්ල පසුව සංස්කරණය කළ හැක, මකා දැමිය හැක. ඉන්පසු ඔබ ' +
    'වෙන් කරන සෑම විටම ඒවා එක් තට්ටුවක කෙටිමං ලෙස දිස් වේ.',
  'www.guide.p15.step4':
    'සුරැකි ස්ථාන අයිති ඔබේ දුරකථනයට නොව ඔබේ ගිණුමටය.',
  'www.guide.p15.step4.note':
    'නව දුරකථනයකින් පිවිසෙන්න, ඔබ සාමාන්‍යයෙන් ගෙවන ආකාරය සමඟම ඒවා දැනටමත් එහි ඇත.',
  'www.guide.p15.step5':
    'ගමනක් අවසන් වූ පසු රියදුරාට පහෙන් ශ්‍රේණිගත කිරීමක් දෙන ලෙස ඔබෙන් අසනු ලැබේ, ' +
    'තට්ටු කිරීමට ඉක්මන් හේතු කිහිපයක් සමඟ — පිරිසිදු, වේලාවට, ආචාරශීලී, ආරක්ෂිත රිය ' +
    'පැදවීම — සහ ඔබට අවශ්‍ය නම් අදහස් කොටුවක්. ඔබට එය මඟ හැරිය හැක, එසේ කිරීමෙන් ඔබට ' +
    'කිසිවක් වැය නොවේ.',
  'www.guide.p15.step5b':
    'ඔබ කැඳවූ ගමනකට මෙන්ම ඔබ අනුගමනය කරන පෞද්ගලික වාහනයකටද මෙය අදාළ වේ. පාසල් වෑන් ' +
    'රථයක රියදුරෙකු, ඔවුන්ට දායක වන දෙමාපියන්ට ශ්‍රේණිගත කළ හැක.',
  'www.guide.p15.step6':
    'රියදුරෙකු ගැන පැමිණිල්ලක් කිරීම අඩු ශ්‍රේණිගත කිරීමකට වඩා වෙනස් හා බරපතළ දෙයකි, ' +
    'එය එසේ විය යුතුමය: පැමිණිලි සමාලෝචනය කරන අතර, තුනක් එකතු කරගන්නා රියදුරෙකුගේ ' +
    'මට්ටම අඩු වී කාලයකට ලැයිස්තුවෙන් ඉවත් කෙරේ.',
  'www.guide.p15.step7':
    'ඔබේ ගමන් ලැයිස්තු තුනක තබා ගැනේ — පසුගිය, නියමිත, සහ පාර්සල් — එක් එකක් දිනය, ' +
    'මාර්ගය, දුර සහ ගාස්තුව සමඟ.',
  'www.guide.p15.step8':
    'පසුගිය ගමනක් විවෘත කිරීමෙන් ඔබට මාර්ගය, ගාස්තු බෙදා දැක්වීම සහ බාගත කළ හැකි ' +
    'රිසිට්පතක් ලැබේ. එය රියදුරාගේ නම සහ අංකයද ඇමතුම් බොත්තමක් සමඟ පෙන්වයි, රථයේ ' +
    'තැබූ දෙයක් ගැන ඔවුන්ට ළඟා වන්නේ එලෙසිනි.',
  'www.guide.p15.callout.whichStarsCount':
    'රියදුරෙකුගේ මට්ටමට ගණන් ගන්නේ තරු හතරේ සහ පහේ ශ්‍රේණිගත කිරීම් පමණි — තරු පහක් ' +
    'ලකුණු පහක් වටී, හතරක් ලකුණු හතරක් වටී, ලකුණු පන්සියයක් යනු මට්ටමකි. තරු දෙකක් ' +
    'සහ ඊට අඩු ඒවා අඩු කරනවා වෙනුවට කිසිවක් එකතු නොකරයි. රියදුරන් අසන්නේ ඒ නිසාය.',
  'www.guide.p15.callout.ratedBothWays':
    'රියදුරන් මගීන්වද ශ්‍රේණිගත කරයි, එකම තරු පහෙන්ම සහ එකම විකල්ප අදහසින්ම, ඔවුන්ගේම ' +
    'ගමන් ඉතිහාසයෙන්. ආචාරය දෙපැත්තටම යන බව දැනගැනීම වටී.',

  // Chapter 16 · Settings, help and your data
  'www.guide.p16.title': 'සැකසුම්, උදව්, සහ ඔබේ දත්ත',
  'www.guide.p16.summary':
    'ඔබ ගැන MageRide දන්නා දේ වෙනස් කරන්නේ කොහෙන්ද, ගැටලුවක් දෙස පුද්ගලයෙකු බලවා ' +
    'ගන්නේ කොහෙන්ද, සහ ඔබේ තොරතුරු සම්බන්ධයෙන් MageRide හට කිරීමට ඔබට බල කළ හැක්කේ ' +
    'කුමක්ද යන්නය.',
  'www.guide.p16.step1':
    'මෙනුව ස්ථාන හතරකට ළඟා වේ: ඔබ අනුගමනය කරන පෞද්ගලික වාහන, ඔබ දායක වන ඒවා, ඔබේ ' +
    'සුරැකි ලිපින, සහ ඔබේ පැතිකඩ සහ සැකසුම්.',
  'www.guide.p16.step2':
    'යෙදුම පුරාම බලපාන තේරීම් ඇත්තේ පැතිකඩ සහ සැකසුම් තුළය: ඔබේ භාෂාව, ඔබේ දැනුම්දීම්, ' +
    'ඔබේ සුරැකි ලිපින, ඔබ සාමාන්‍යයෙන් ගෙවන ආකාරය, සහ උදව් වෙත යන මාර්ගය. භාෂාව ' +
    'වෙනස් කිරීමෙන් මුළු යෙදුමම එකවර වෙනස් වන අතර, ඔබ කැමති තරම් වාරයක් එය වෙනස් ' +
    'කළ හැක.',
  'www.guide.p16.step3':
    'පැතිකඩ සංස්කරණය, වෙනම තිරයක්, යෙදුම ගැන නොව ඔබ ගැන වන දේවල් සඳහාය — ඔබේ නම, ' +
    'ඔබේ ඡායාරූපය, සහ ඔබේ හදිසි සම්බන්ධතා. 10 වන පරිච්ඡේදයේ හදිසි බොත්තම කෙටි පණිවිඩ ' +
    'යවන සම්බන්ධතාව ඔබ සකසන්නේ එහිදීය, එය ඔබට අවශ්‍ය වීමට පෙර කිරීම වටී.',
  'www.guide.p16.step4':
    'ඔබට රියදුරෙකු අවහිර කළ හැක. අවහිර කළ රියදුරෙක් ඔබේ සිතියමෙන් අතුරුදහන් වන අතර ' +
    'කිසිදා නැවත ඔබට එවිය නොහැක.',
  'www.guide.p16.step5':
    'උදව් සහ සහාය විවෘත වන්නේ පොදු ප්‍රශ්න ලැයිස්තුවකින් වන අතර, එය කිසිවෙකු සම්බන්ධ ' +
    'නොවී බොහෝ දේවලට පිළිතුරු දෙයි.',
  'www.guide.p16.step6':
    'එසේ නොවේ නම්, ටිකට්පතක් යොදන්න. ගැටලුව විස්තර කරන්න, එය සම්බන්ධ ගමන ඔබේ පසුගිය ' +
    'ගමන් ලැයිස්තුවෙන් අමුණන්න, උපකාරී නම් තිර රුවක්ද එක් කරන්න. පිළිතුරු ලැබෙන තුරු ' +
    'ඔබට ටිකට්පත අනුගමනය කළ හැක.',
  'www.guide.p16.step7':
    'එම තිරයෙන්ම ඔබ ගැන MageRide සතුව ඇති සියල්ලේ පිටපතක් ඉල්ලිය හැකි අතර, ඔබේ ගිණුම ' +
    'සහ පෞද්ගලික තොරතුරු මකා දමන ලෙසද ඉල්ලිය හැක.',
  'www.guide.p16.step7.note':
    'ඒ දෙකම ශ්‍රී ලංකාවේ පෞද්ගලික දත්ත ආරක්ෂණ නීතිය යටතේ ඇති අයිතිවාසිකම් මිස ' +
    'ත්‍යාග නොවේ. ඒ දෙකෙන් කිසිවකට ඔබ හේතුවක් දිය යුතු නැත.',
  'www.guide.p16.step8':
    'MageRide රැස් කරන දේ සහ මෙම ඉල්ලීම් හසුරුවන ආකාරය අපගේ දත්ත පිටුව දක්වයි.',
  'www.guide.p16.callout.thirtyDays':
    'ඔබේ දත්ත සඳහා වූ, හෝ ඒවා මැකීම සඳහා වූ ඉල්ලීමකට දින තිහක් ඇතුළත පිළිතුරු දේ. එය ' +
    'කරන විට ඔබට යොමු අංකයක් සහ නියමිත දිනයක් ලැබෙන අතර, එය කොතැනට ගොස් ඇත්දැයි ඔබට ' +
    'පරීක්ෂා කළ හැක.',
  'www.guide.p16.callout.whatIsKept':
    'මැකීම ඔබේ පෞද්ගලික තොරතුරු ඉවත් කරයි, නමුත් සියල්ල ඉවත් කළ නොහැක. තවමත් ධාවනය ' +
    'වන ගමනක්, තවමත් ආරවුලක ඇති ගෙවීමක්, සහ විගණන සටහනක් ලෙස තබා ගැනීමට MageRide හට ' +
    'නියම කර ඇති වාර්තා යන සියල්ල පවතී — ඒ අවසන් එක කිසිවෙකුට වෙනස් කළ නොහැක, එහි ' +
    'අරමුණම එයයි. ඔබේ ඉල්ලීමට මින් අදාළ වූයේ කුමක්දැයි ඔබට කියනු ලැබේ.',
  'www.guide.p16.callout.blockADriver':
    'අවහිර කිරීම අඩු ශ්‍රේණිගත කිරීමකට සමාන නොවේ, එයක් මත රඳාද නොපවතී. අවහිර කළ ' +
    'රියදුරෙක් ඔබේ සිතියමේ දිස්වීම නවත්වන අතර, ඔවුන්ගේ ශ්‍රේණිගත කිරීම කුමක් වුවත් ' +
    'ඔබට එවිය නොහැක.',

  // =========================================================================
  // S10 · the driver guide, chapters 1–9 — translated in S12.
  //
  // Every number in these chapters is a commercial claim made to somebody
  // deciding how to earn a living. None of them is in this file — the fee table,
  // the tier amounts and the counts are constants in `src/content/` with their
  // anchors — so a translator cannot damage one. Where the specs state no
  // consequence, the English says so and the Sinhala says so too: `නිශ්චිතව
  // කියා නැත` is a translation, not a hedge added here.
  // =========================================================================

  // Chapter 1 · Setting up the driver app
  'www.guide.d01.title': 'රියදුරු යෙදුම සකස් කිරීම',
  'www.guide.d01.summary':
    'භාෂාව, නගරය, ඔබේ දුරකථන අංකය, සහ සෑම මගියෙකුටම පෙනෙන පැතිකඩ. එයට මිනිත්තු ' +
    'කිහිපයක් සහ ඔබේ රියදුරු බලපත්‍රය අතේ තිබීම අවශ්‍ය වන අතර, ඔබට තවම වාහනයක් ' +
    'ලියාපදිංචි කර තිබීම අවශ්‍ය නැත.',
  'www.guide.d01.step1':
    'පළමු වරට රියදුරු යෙදුම විවෘත කරන්න, ඉහළින් ඇති ස්ලයිඩ තුනක් එය කරන දේ හඳුන්වා ' +
    'දේ — වාහනයක් ලියාපදිංචි කිරීම, තත්පර පහළොවේ ගමන් ඉල්ලීම, දිශානුගත ගමන්, සහ ' +
    'දෛනික ගාස්තුව අඩු වන යෙදුම තුළ පසුම්බිය. ඒවා පසෙකට ඇද දමන්න, නැතහොත් ඉබේ ' +
    'ඉදිරියට යාමට ඉඩ දෙන්න.',
  'www.guide.d01.step2':
    'ස්ලයිඩවලට පහළින්, පේළියකට එකක් බැගින් කොටු තුනකින් ඔබේ භාෂාව තෝරන්න — ඉහළින්ම ' +
    'සිංහල, දැනටමත් තෝරා ඇත, ඉන්පසු දෙමළ, ඉන්පසු ඉංග්‍රීසි — සහ ඔබ රිය පදවන නගරය.',
  'www.guide.d01.step2.note':
    'එම ලැයිස්තුවේ ඇති නගර යනු MageRide ආරම්භ කර ඇති ඒවා වන අතර, ඔබ යෙදුම විවෘත කරන ' +
    'විට එය ඒවා පූරණය කරයි. නව නගරයක් ඉබේම දිස් වේ; ඒ සඳහා ඔබ කිසිදා යෙදුම යාවත්කාලීන ' +
    'කරන්නේ නැත.',
  'www.guide.d01.step3':
    'ඔබේ ජංගම දුරකථන අංකයෙන් පිවිසෙන්න. ක්ෂේත්‍රයේ දැනටමත් +94 ඇත, එබැවින් ඔබ ටයිප් ' +
    'කරන්නේ ඉන් පසුව එන ඉලක්කම් නවයයි, ඉන්පසු කෙටි පණිවිඩයෙන් එන ඉලක්කම් හයේ කේතය. ' +
    'එය නොපැමිණේ නම්, තත්පර හැටකට පසු තවත් එකක් ඉල්ලන්න. මුරපදයක් නැත, විද්‍යුත් ' +
    'තැපැල් ලිපිනයක් නැත, Google පිවිසුමක්ද නැත — මගීන් සහ MageRide ඔබට ළඟා වන්නේ ' +
    'ඔබේ දුරකථන අංකයෙනි.',
  'www.guide.d01.step4':
    'ඊළඟට පැතිකඩ සැකසීම එන අතර, එය වාහනයක් ගැන නොව ඔබ ගැනය. ඔබේම ඡායාරූපයක් එක් ' +
    'කරන්න — එය අවශ්‍ය වන අතර මගීන්ට එය පෙනේ — සහ ඔබේ නම. ඡායාරූපය එහි එන තුරු ' +
    'සුරකින්න සහ ඉදිරියට අළු පැහැයෙන් තිබේ.',
  'www.guide.d01.step5':
    'ඉන්පසු ඔබේ රියදුරු බලපත්‍රය ඉදිරිපස සහ පිටුපස ඡායාරූප ගන්න. යෙදුම එයින් දේවල් ' +
    'හතරක් කියවා ඒවා ඔබට පෙන්වයි: බලපත්‍ර අංකය, එහි කල් ඉකුත්වීම, ඔබේ ජාතික හැඳුනුම්පත් ' +
    'අංකය, සහ ඔබට රිය පැදවීමට බලපත්‍රය ඇති වාහන පන්ති.',
  'www.guide.d01.step5.note':
    'එයට පැහැදිලිව කියවා ගත නොහැකි වූ දේ ඔබම ටයිප් කරන්න — ඔබ ටයිප් කරන ඕනෑම දෙයක්, ' +
    'විශ්වාස කිරීමට පෙර MageRide හි කවුරුන් හෝ පරීක්ෂා කිරීම සඳහා සලකුණු වේ. එමගින් ' +
    'පැහැදිලි ඡායාරූපයක් දෙවන උත්සාහයක් වටින දෙයක් වන අතර, 3 වන පරිච්ඡේදය මුළුමනින්ම ' +
    'ඒ ගැනය.',
  'www.guide.d01.step6':
    'ඊළඟ තිරයේ අවසර ලබා දෙන්න — එක් එකක් කුමකටදැයි 5 වන පරිච්ඡේදය විස්තර කරයි — ' +
    'එවිට උපකරණ පුවරුව විවෘත වේ.',
  'www.guide.d01.step7':
    'දැන් ඔබ වාහනයක් ලියාපදිංචි නොකර යෙදුම තුළ සිටී, එය එසේ විය යුතු ආකාරයයි. එකක් ' +
    'ලියාපදිංචි කිරීම ඊළඟ පරිච්ඡේදය වන අතර ඔබේ ලේඛන සූදානම් වූ විටෙක එය කළ හැක. ' +
    'වාහන සමූහයකට ඔබට බසයක් හෝ වෑන් රථයක් පවරාද, කිසිවක් ලියාපදිංචි නොකර එය පැදවිය ' +
    'හැක.',
  'www.guide.d01.callout.notPublished':
    'රියදුරු යෙදුම තවම යෙදුම් වෙළඳසැල්වලට නිකුත් කර නැත, එබැවින් අද ස්ථාපනය කිරීමට ' +
    'කිසිවක් නැත. මෙම මාර්ගෝපදේශය විස්තර කරන්නේ යෙදුම නිර්මාණය කර අනුමත කර ඇති ' +
    'ආකාරයයි; එය නිකුත් වූ විට බාගැනීමේ පිටුව ඒ බව කියයි.',
  'www.guide.d01.callout.noVehicleNeeded':
    'ලියාපදිංචිය අවසන් කිරීමට ඔබට වාහනයක් අවශ්‍ය නැත. උපකරණ පුවරුවට ළඟා වීමට ඔබේ නම, ' +
    'ඔබේ ඡායාරූපය සහ ඔබේ රියදුරු බලපත්‍රය ප්‍රමාණවත් — වාහනයක් ලියාපදිංචි කිරීම යනු ' +
    'ඔබ සූදානම් වූ විට ගන්නා වෙනම, විකල්ප පියවරකි.',
  'www.guide.d01.callout.oneDevicePerApp':
    'වරකට එක් දුරකථනයක්, යෙදුම අනුව ගණන් කෙරේ. නව දුරකථනයකින් රියදුරු යෙදුමට පිවිසීම ' +
    'පැරණි එකෙන් වහාම ඉවත් කරන අතර, ගමනක් මැද එසේ වුවහොත් නව දුරකථනය ගමන තිබූ තැනින්ම ' +
    'ගනී. මගී යෙදුම වෙනම ගණන් ගන්නා බැවින් ඔබට දෙකම භාවිත කළ හැක.',

  // Chapter 2 · Registering your vehicle
  'www.guide.d02.title': 'ඔබේ වාහනය ලියාපදිංචි කිරීම',
  'www.guide.d02.summary':
    'ඔබට අයිති පොරොත්තු වාහනයක් සඳහා, එකින් එක සුරැකෙන පියවර හතරක්. ප්ලස් බොත්තම ' +
    'කරන දේ, ඉදිරියට යන්න කරන දේ, සහ බසයක් මෙයින් කිසිසේත් නොයන්නේ ඇයි.',
  'www.guide.d02.step1':
    'රියදුරු යෙදුම ලියාපදිංචි කරන්නේ පොරොත්තු වාහන — ඔබම පදවා ඉල්ලුම මත කුලී ගන්නා ' +
    'වාහනයයි. යතුරුපැදිය, ත්‍රීරෝද රථය, ෆ්ලෙක්ස්, සෙඩාන් රථය, මිනි වෑන් රථය සහ වෑන් ' +
    'රථය, ඊට අමතරව බෙදාහැරීම් සඳහා ට්‍රක් සහ මිනි ට්‍රක් රථය.',
  'www.guide.d02.step1.note':
    'බසයක්, පාසල් වෑන් රථයක්, හෝ මාර්ග බලපත්‍රයක් දරන ඕනෑම දෙයක් ලියාපදිංචි කරන්නේ ' +
    'ඒ වෙනුවට එහි මෙහෙයුම්කරු විසින් රථ සමූහ ද්වාරයේය. මෙම කාර්ය මාලාවේ බලපත්‍ර ' +
    'තැනක් හෝ GPS ට්‍රැකර් ක්ෂේත්‍රයක් නැත, එය නැති වීමක් නොව හිතාමතාය.',
  'www.guide.d02.step2':
    'ඔබට ඇති සෑම වාහනයක්ම ඇත්තේ මගේ වාහන තුළ වන අතර, මෙය ආරම්භ වන්නේද එහිය. ඔබට ' +
    'කිසිවක් නැත්නම්, තිරය විවෘත කළ වහාම යෙදුම එකක් ලියාපදිංචි කිරීමට ඉදිරිපත් වේ.',
  'www.guide.d02.step3':
    '4 න් 1 වන පියවර දේවල් දෙකක් ඉල්ලයි: වාහන වර්ගය, සහ එහි ලියාපදිංචි අංකය. ' +
    'ඉදිරියට ඔබව රක්ෂණයට ගෙන යයි.',
  'www.guide.d02.step4':
    '2, 3 සහ 4 පියවර ඡායාරූප ය — ඔබේ රක්ෂණ සහතිකය, ඔබේ ආදායම් බලපත්‍රය, සහ අංක තහඩුව ' +
    'කියවිය හැකි ලෙස වාහනයම ඉදිරිපස සහ පිටුපස. උඩුගත වූ පසු එක් එකක් අවසන් යැයි ' +
    'පෙන්වයි.',
  'www.guide.d02.step5':
    'ඔබ අවසන් කරන විටම එක් එක් පියවර සුරැකේ. 2 වන පියවර මැදදී යෙදුම වසා හෙට නැවත ' +
    'ආ හැක; ඔබ දැනටමත් කර ඇති කිසිවක් නැති නොවේ.',
  'www.guide.d02.step6':
    'හතරම අවසන් වන තුරු වාහනය මගේ වාහන තුළ අසම්පූර්ණ ලෙස ඇති අතර, ඊළඟ පියවර කුමක්දැයි ' +
    'පෙන්වයි. හතරම පරීක්ෂා කර සම්මත වූ පසු එය අනුමතයි යැයි පෙන්වයි — සබැඳි කළ හැක්කේ ' +
    'අනුමත වාහනයකට පමණි.',
  'www.guide.d02.step7':
    'අවසන් නොකළ වාහනයකට නැවත පැමිණීම යනු එම වාහනයේම පේළියේ ඇති ඉදිරියට යන්න යන්නයි. ' +
    'එය එම වාහනය ආරම්භයේ නොව එහිම ඊළඟ පියවරෙන් විවෘත කරයි.',
  'www.guide.d02.step7.note':
    'ඉහළින් ඇති ප්ලස් බොත්තමේ තේරුම එක් කරන්න යන්නයි: වෙන කුමක් අවසන් නොවී තිබුණත්, ' +
    'එය සැමවිටම නව වාහනයකට නැවුම් 4 න් 1 වන පියවරක් ආරම්භ කරයි. මෙනුවේ ඇති වාහන ' +
    'ලියාපදිංචිය කිසිදු වාහනයක් නම් නොකරන බැවින්, එය ඔබව අවසන් නොකළ පළමු එකට ගෙන ' +
    'යයි.',
  'www.guide.d02.step8':
    'ඔබට කැමති තරම් වාහන ලියාපදිංචි කළ හැක, නමුත් වරකට සජීවී වන්නේ එකක් පමණි — මගේ ' +
    'වාහන තුළ ඔබ තෝරන එක තම පිහිටීම ප්‍රකාශ කරන, කුලී ගන්නා, සහ ඔබ ගෙවන දෛනික අනුපාතය ' +
    'තීරණය කරන එකයි.',
  'www.guide.d02.callout.threeDoors':
    'කාර්ය මාලාවට මාර්ග තුනක් සහ වෙනස් අර්ථ තුනක්. ප්ලස් නව වාහනයක් ආරම්භ කරයි. ' +
    'පේළියක ඇති ඉදිරියට යන්න එම වාහනය දිගටම කරගෙන යයි. මෙනුවේ ඇති වාහන ලියාපදිංචිය ' +
    'කිසිදු වාහනයක් නම් නොකරන බැවින්, එය ඔබව අවසන් නොකළ පළමු එකට ගෙන යයි. පළමු එකක් ' +
    'අවසන් නොවී තිබියදී දෙවන වාහනයක් එක් කිරීම සඳහාය ප්ලස් බොත්තම ඇත්තේ.',
  'www.guide.d02.callout.oneVehicleOnePhone':
    'වාහනයක් වරකට එක් ජංගම දුරකථන අංකයකට අයිතිය, තව ද සක්‍රීය වාහන අතර ලියාපදිංචි ' +
    'අංකයක් භාවිතයේ තිබිය හැක්කේ එක් වරක් පමණි. ඔබේ අංක තහඩුව දැනටමත් ලියාපදිංචි ' +
    'යැයි යෙදුම කියන්නේ නම්, එය වෙනත් ගිණුමක සක්‍රීයයි — නැතහොත් ඔබේම පැරණි එකක, ' +
    'එහිදී වාහනය ඉවත් කිරීමෙන් අංකය නිදහස් වේ.',
  'www.guide.d02.callout.fleetPortal':
    'බස්, පාසල් වෑන් සහ මාර්ග බලපත්‍රයක් අවශ්‍ය ඕනෑම දෙයක් ලියාපදිංචි කරන්නේ ඒවායේ ' +
    'මෙහෙයුම්කරු විසින් රථ සමූහ ද්වාරයේය, එහි ලියාපදිංචි පිටපත (CR පොත), රක්ෂණය, ' +
    'ආදායම් බලපත්‍රය සහ බලපත්‍රයම සඳහා තැන් ඇත. රියදුරෙකු ලෙස ඔබට එම වාහනවලින් එකක් ' +
    'පවරාගෙන, ඔබම කිසිවක් ලියාපදිංචි නොකර එය පැදවිය හැක.',

  // Chapter 3 · Photographing your documents
  'www.guide.d03.title': 'ඔබේ ලේඛන ඡායාරූප ගැනීම',
  'www.guide.d03.summary':
    'යෙදුමේ සෑම කැමරා තැනක්ම විවෘත කරන්නේ එකම පරිලෝකකයයි, එහි නිසි ලෙස ඉගෙන ගැනීම ' +
    'වටින එක් පාලනයක් ඇත. පැහැදිලි ඡායාරූපයක් යනු මිනිත්තු කිහිපයකින් අනුමත වන ' +
    'වාහනයක් සහ පුද්ගලයෙකු එනතුරු බලා සිටින එකක් අතර වෙනසයි.',
  'www.guide.d03.step1':
    'ඕනෑම ග්‍රහණ තැනක් තට්ටු කරන්න — බලපත්‍රය, රක්ෂණය, ආදායම් බලපත්‍රය, වාහන ඡායාරූපය ' +
    '— එවිට එකම ලේඛන පරිලෝකකය විවෘත වේ: එයට පෙනෙන දේ මත රාමුවක් ඇඳ ඇති සජීවී කැමරාවකි.',
  'www.guide.d03.step2':
    'ලේඛනයේ දාර කොහේදැයි යෙදුම අනුමාන කර ඒවා මත කොන් හතරක රාමුවක් තබයි. මුළු ලේඛනයම ' +
    'රාමුව තුළ වාඩි වී එය පුරවන පරිදි කොන් ඇද දමන්න.',
  'www.guide.d03.step2.note':
    'අනුමානය ආරම්භක ලක්ෂ්‍යයක් පමණි. එය කඩදාසිය වෙනුවට මේසය අල්ලාගෙන ඇත්නම්, කොන් ' +
    'ඔබම ගෙන යන්න — ඔබ රාමුව තුළ තබන දෙයම යවනු ලබන දෙයයි.',
  'www.guide.d03.step3':
    'ඡායාරූපය භාවිත කරන්න ඔබ රාමු කළ දේ කෙළින් කර කපා, එය උඩුගත කරයි. නැවත ගන්න ' +
    'ආරම්භයේ සිට පටන් ගන්නා අතර, අඳුරු මිදුලකට ෆ්ලෑෂ් එකක්ද ඇත. ඒ වෙනුවට ඔබේ ' +
    'ගැලරියෙන් පවතින ඡායාරූපයක් තෝරාගත හැක, එහෙත් නැවුම් ග්‍රහණයක් සාමාන්‍යයෙන් වඩා ' +
    'හොඳින් කියවේ.',
  'www.guide.d03.step4':
    'ඔබේ රියදුරු බලපත්‍රය, ඉදිරිපස සහ පිටුපස, බලපත්‍ර අංකය, කල් ඉකුත්වීම, ඔබේ ජාතික ' +
    'හැඳුනුම්පත් අංකය සහ ඔබට බලපත්‍රය ඇති පන්ති සඳහා කියවේ.',
  'www.guide.d03.step5':
    'ඔබේ රක්ෂණ සහතිකය එහි කල් ඉකුත්වන දිනය සඳහාද, ඔබේ ආදායම් බලපත්‍රය එහි අංකය සහ ' +
    'කල් ඉකුත්වන දිනය සඳහාද කියවේ.',
  'www.guide.d03.step6':
    'වාහන ඡායාරූප දෙක අංක තහඩුව සඳහා කියවෙන අතර, එය 1 වන පියවරේදී ඔබ ටයිප් කළ ' +
    'ලියාපදිංචි අංකයට එරෙහිව ගැළපේ. කියවා ගත නොහැකි, හෝ නොගැළපෙන අංක තහඩුවක් එම ' +
    'පියවර රඳවා ගනී.',
  'www.guide.d03.step7':
    'යෙදුම කියවන සියල්ල එය කොහේ හෝ යාමට පෙර ඔබට පෙන්වන අතර, ඔබට ඉන් ඕනෑම එකක් ' +
    'නිවැරදි කළ හැක.',
  'www.guide.d03.step7.note':
    'ක්ෂේත්‍රයක් නිවැරදි කිරීම අසාර්ථක වීමක් නොවේ. එය එම අගය, යෙදුමට තනිවම පිටුපසින් ' +
    'සිටිය හැකි එකක් වෙනුවට පුද්ගලයෙකු තහවුරු කරන එකක් බවට පත් කරයි, ඊට සිදු වන දේ ' +
    'ඊළඟ පරිච්ඡේදයයි.',
  'www.guide.d03.callout.whyItMatters':
    'මුළු ලියාපදිංචියේම වඩාත්ම වටිනා මිනිත්තු දෙක මෙයයි. ඡායාරූපය පැහැදිලි වන තරමට, ' +
    'යෙදුම තනිවම වැඩිපුර කියවන අතර, MageRide හි කවුරුන් හෝ අතින් පරීක්ෂා කරන තුරු ' +
    'බලා සිටින ක්ෂේත්‍ර අඩු වේ. දුර්වල ආලෝකය සහ ඇද කෝණයක් ඔබට තත්පර නොව දින ගණන් ' +
    'වැය කරයි.',
  'www.guide.d03.callout.insuranceMandatory':
    'MageRide හි සෑම වාහනයකටම, ක්‍රම තුනේදීම, වලංගු රක්ෂණ සහතිකයක් අවශ්‍ය වන අතර ' +
    'ආදායම් බලපත්‍රයක්ද එසේමය. ඒ දෙකෙන් එකක් කල් ඉකුත් වුවහොත්, අලුත් කළ ලේඛනය උඩුගත ' +
    'කරන තුරු එම වාහනයට කුලී ලැබීම නවතී.',
  'www.guide.d03.callout.whatIsRead':
    'එක් එක් ලේඛනය ඉහත නම් කළ ක්ෂේත්‍ර සඳහා කියවෙන අතර, සුරැකීමට පෙර සෑම අගයක්ම ඔබට ' +
    'පෙන්වයි. අගයක් ඔබේ ඡායාරූපයෙන් කියවුණාද නැතහොත් ඔබ විසින් ටයිප් කරන ලද්දක්ද ' +
    'යන්නද යෙදුම සටහන් කරයි, එවිට එය පරීක්ෂා කරන අයට කුමක් කුමක්ද යන්න දැනගත හැක.',

  // Chapter 4 · Getting approved
  'www.guide.d04.title': 'අනුමැතිය ලබා ගැනීම',
  'www.guide.d04.summary':
    'යෙදුම තනිවම සම්මත කරන දේ, පුද්ගලයෙකු වෙත යන දේ, සහ බලා සිටින අතරතුර ඔබට කළ ' +
    'හැකි දේ. එක් තිරයක තීන්දු හතරක්.',
  'www.guide.d04.step1':
    'හතරවන පියවර ඉදිරිපත් කිරීමෙන් පේළි හතරක් සහිත සමාලෝචන තිරයක් විවෘත වේ — වාහන ' +
    'විස්තර, රක්ෂණය, ආදායම් බලපත්‍රය, සහ ඉදිරිපස හා පිටුපස ඡායාරූප. එක් එකක් ' +
    'සත්‍යාපිත හෝ පොරොත්තුවෙන් වේ.',
  'www.guide.d04.step2':
    'යෙදුමට අවශ්‍ය දේ කියවා ගත් විට පේළියක් සත්‍යාපිත වේ: රක්ෂණයෙන් කල් ඉකුත්වන ' +
    'දිනයක්, ආදායම් බලපත්‍රයෙන් අංකයක් සහ කල් ඉකුත්වීමක්, ඔබ ටයිප් කළ ලියාපදිංචියට ' +
    'ගැළපෙන අංක තහඩුවක්, සහ ඔබම ඇතුළත් කළ වර්ගය හා ලියාපදිංචිය.',
  'www.guide.d04.step3':
    'හතරම සත්‍යාපිත ලෙස පැමිණියොත්, MageRide හි කිසිවෙකු සම්බන්ධ නොවී වාහනය ' +
    'ස්වයංක්‍රීයව අනුමත වේ. පිරිසිදු ඡායාරූප කට්ටලයක සාමාන්‍ය ප්‍රතිඵලය එයයි.',
  'www.guide.d04.step4':
    'හේතු තුනකින් එකක් නිසා පේළියක් පොරොත්තුවෙන් වේ: කියවූ දේ ගැන යෙදුමට විශ්වාසයක් ' +
    'නොතිබීම, අගය ඔබම ටයිප් කිරීම, හෝ ඔබේ ඡායාරූපවල අංක තහඩුව ලියාපදිංචි අංකයට ' +
    'නොගැළපීම.',
  'www.guide.d04.step4.note':
    'පොරොත්තුවෙන් යනු මුළු අයදුම්පත ගැන නොව එක් පියවරක් ගැනය. අනෙක් තුන සත්‍යාපිතව ' +
    'පවතින අතර ඔබ ඒවා නැවත ඡායාරූප ගන්නේ නැත.',
  'www.guide.d04.step5':
    'පොරොත්තුවෙන් ඇති පේළියක් සත්‍යාපන නිලධාරියෙකු වෙත යන අතර, ඔවුන් එහි ඇති දේ ' +
    'තහවුරු කරයි, නැතහොත් එය නිවැරදි කර එය තහවුරු කරයි. පොරොත්තුවෙන් එකක්වත් ඉතිරි ' +
    'නොවන තුරු වාහනය අනුමත නොවේ.',
  'www.guide.d04.step6':
    'ප්‍රතිඵලය දැනුම්දීමකින් සහ යෙදුම තුළින් ඔබට කියනු ලැබේ. යමක් ප්‍රතික්ෂේප වුවහොත් ' +
    'ඔබට හේතුව සහ එය නැවත ඡායාරූප ගැනීමට ක්‍රමයක් ලැබේ.',
  'www.guide.d04.step7':
    'ඔබ බලා සිටින අතරතුර යෙදුම ඔබ සතුවම පවතී. වාහන සමූහයක් විසින් ඔබට පවරන ලද බසයක් ' +
    'හෝ පෞද්ගලික වාහනයක් අද පැදවිය හැක. ඔබට කළ නොහැක්කේ මෙම විශේෂිත වාහනය සබැඳි ' +
    'කිරීමයි: සජීවී වීමට තෝරාගත හැක්කේ අනුමත වාහනයකට පමණි.',
  'www.guide.d04.callout.whileYouWait':
    'අනුමැතිය එනතුරු බලා සිටීම ඔබව අගුළු ලා නොතබයි. මේ කිසිවක් ආරම්භ වීමට පෙරම ඔබ ' +
    'උපකරණ පුවරුවට ළඟා විය, තව ද වාහන සමූහයක් ඔබට බෙදාගත් හෝ තාවකාලිකව පැවරූ වාහනයක් ' +
    'වහාම පැදවිය හැක — එය කිසිදා මෙම කාර්ය මාලාව හරහා යන්නේ නැත.',
  'www.guide.d04.callout.typedIsChecked':
    'ඔබ ඡායාරූප ගැනීම වෙනුවට ටයිප් කළ ඕනෑම දෙයක්, එය කෙතරම් පැහැදිලිව නිවැරදි වුවත්, ' +
    'සැමවිටම පුද්ගලයෙකු විසින් පරීක්ෂා කෙරේ. එය ඔබ ගැන සැකයක් නොව සාක්ෂි පිළිබඳ ' +
    'නීතියකි, ඡායාරූපයට අමතර මිනිත්තුව වැය කිරීමට ඇති හොඳම එකම හේතුවද එයයි.',

  // Chapter 5 · Permissions and driving in the background
  'www.guide.d05.title': 'අවසර සහ පසුබිමේ රිය පැදවීම',
  'www.guide.d05.summary':
    'රියදුරු යෙදුම ඉල්ලන දේවල් හතරක්, එක් එකින් ඔබට ඇත්තටම ලැබෙන දේ, සහ ඔබේ පිහිටීම ' +
    'ප්‍රකාශ වන්නේ කවදාද යන්නය.',
  'www.guide.d05.step1':
    'ඔබේ පැතිකඩ සුරැකීමෙන් පසු අවසර තිරය එක් වරක් එන අතර, ඔබේ දුරකථනයේම අවසර කොටු ' +
    'දිස් වීමට පෙර එය තමන්වම පැහැදිලි කරයි.',
  'www.guide.d05.step2':
    'සැමවිටම හෝ පසුබිම ලෙස සකසන ලද ස්ථානය, වඩාත්ම වැදගත් වන්නේ එයයි. ඔබේ පිහිටීම ' +
    'මගීන් වෙත යන්නේ එයින්ය, ඔබ අසල කුලියක් එන විට ඔබ කොහේදැයි බෙදාහැරීම දන්නේද ' +
    'එයින්ය.',
  'www.guide.d05.step2.note':
    'Android හි, ගමන් ඉල්ලීමක් ඔබේ තිරයේ ඇති දේට උඩින් දිස්විය හැකි වන පරිදි, යෙදුමට ' +
    'වෙනත් යෙදුම්වලට උඩින් පෙන්වීමට ඉඩ දෙන ලෙසද ඔබෙන් ඉල්ලයි. iPhone එකකට සමාන ' +
    'සැකසුමක් නැත: එහිදී ඔබෙන් ඉල්ලන්නේ සැමවිටම ක්‍රියාත්මක ස්ථානය සහ දැනුම්දීම් ' +
    'පමණි.',
  'www.guide.d05.step3':
    'ගමන් ඉල්ලීමක් ඔබ වෙත ළඟා වන්නේම දැනුම්දීම් නිසාය. ඉල්ලීමක් ශබ්දයකින් සහ ' +
    'කම්පනයකින් දුරකථනය අවදි කරයි; දැනුම්දීම් නිවා ඇත්නම් එය අවදි කිරීමට කිසිවක් නැත.',
  'www.guide.d05.step4':
    'රියදුරු යෙදුම සඳහා බැටරි ප්‍රශස්තිකරණය නිවා දැමීම හතරවන ඉල්ලීම වන අතර, පසෙකට ' +
    'දමා යාමට පහසුම එකද එයයි. එය පසෙකට නොදමන්න.',
  'www.guide.d05.step4.note':
    'එය ක්‍රියාත්මකව තැබුවහොත්, තිරය නිවා ඇති විට ඔබේ පිහිටීම ප්‍රකාශ කරන සහ ඉල්ලීම් ' +
    'සඳහා සවන් දෙන සේවාව වසා දැමීමට ඔබේ දුරකථනයට අයිතියක් ඇත. යෙදුම එම නිදහස් කිරීම ' +
    'ඉල්ලන්නේ එය කළ නොහැකි කිරීමට හරියටම ය.',
  'www.guide.d05.step5':
    'ඔබ සබැඳිව සිටින විට, හෝ ගමනක සිටින විට, යෙදුම ඔබේ පිහිටීම පසුබිමේ ප්‍රකාශ කරයි ' +
    '— තිරය නිවා, දුරකථනය සාක්කුවේ, වෙනත් යෙදුමක් විවෘතව. එය පෙරබිම් සේවාවක් ලෙස ' +
    'ධාවනය වේ, එය ක්‍රියාත්මක වන අතරතුර ඔබේ දුරකථනය දැනුම්දීමක් පෙන්වන්නේ ඒ නිසාය.',
  'www.guide.d05.step6':
    'නොබැඳි වීම, හෝ ගමනක් අවසන් කිරීම, එය නවත්වයි. පොරොත්තු වාහනයකට සවි කර ඇති GPS ' +
    'ට්‍රැකරයකටද එම නීතියම අදාළ වේ: එහි පිහිටීම් ගනු ලබන්නේ වාහනය සබැඳිව ඇති අතරතුර ' +
    'පමණි.',
  'www.guide.d05.callout.whenYouArePublished':
    'ඔබේ පිහිටීම ප්‍රකාශ වන්නේ ඔබ සබැඳිව හෝ ගමනක සිටින අතරතුරය, ඔබ නොබැඳි වූ විට හෝ ' +
    'ගමන අවසන් කළ විට ප්‍රකාශ කිරීම නවතී. ටොගලය කරන්නේ එයයි — එය හුදෙක් තිරයක ලේබලයක් ' +
    'නොවේ.',
  'www.guide.d05.callout.batteryOptimisation':
    'ඔබ සබැඳිව සිටිද්දී ඉල්ලීම් නොපැමිණේ නම්, පළමුව පරීක්ෂා කළ යුත්තේ බැටරි ' +
    'ප්‍රශස්තිකරණයයි. පසුබිමේ ධාවනය වන යෙදුමක් නැවැත්වීමට දුරකථනයකට අවසර ඇත, එසේ ' +
    'නොකරන ලෙස ඉල්ලන අවසරය මෙයයි.',
  'www.guide.d05.callout.ownVehicleOnly':
    'ඔබේම උපකරණ පුවරුවේ සිතියම පෙන්වන්නේ එක් වාහනයකි: ඔබේ එකයි. වෙනත් රියදුරන් එහි ' +
    'කිසිදා අඳින්නේ නැත. තව ද ඔබ මගියෙකු රැගෙන යන අතරතුර, අනෙක් මගීන් බලන මහජන ' +
    'සිතියමෙන් ඔබේ වාහනය ඉවත් වේ — ඔබව දැකිය හැක්කේ ඔබ රැගෙන යන මගියාට පමණි.',

  // Chapter 6 · Your dashboard
  'www.guide.d06.title': 'ඔබේ උපකරණ පුවරුව',
  'www.guide.d06.summary':
    'රියදුරු යෙදුමට මුල් තිර දෙකක් ඇති අතර, ඔබට ලැබෙන්නේ කුමන එකද යන්න ඔබ තෝරාගෙන ' +
    'ඇති වාහනය අනුව යයි. පොරොත්තු රියදුරන්ට සිතියමක් සහ ටොගලයක් ලැබේ; බස් සහ ' +
    'පෞද්ගලික වාහන රියදුරන්ට බොත්තම් දෙකක් ලැබේ.',
  'www.guide.d06.step1':
    'පොරොත්තු වාහනයක් තෝරාගෙන ඇති විට, මුල් තිරය යනු ඉහළින් ඔබේ විස්තර සහිත පූර්ණ ' +
    'තිර සිතියමකි: ඔබේ මට්ටම, ඔබේ ශ්‍රේණිගත කිරීම, ඔබේ පසුම්බියේ ශේෂය, සහ අද දෛනික ' +
    'ගාස්තුව — එය අඩු කර තිබේද, ඔබේ වාහනයට එය කොපමණද යන්න.',
  'www.guide.d06.step2':
    'සිතියම පෙන්වන්නේ එක් වාහනයකි, එය ඔබේය. වෙනත් රියදුරන් එහි කිසිදා අඳින්නේ නැත, ' +
    'එබැවින් නිස්කලංක ලෙස පෙනෙන සිතියමක් දෝෂයක් නොව සාමාන්‍ය සිතියමකි.',
  'www.guide.d06.step3':
    'ඉහළ කෙළවරේ මෙනු බොත්තමක් නැත. යෙදුමේ අනෙක් සියල්ල — වාහන, පසුම්බිය, රැකියා, ' +
    'ඉතිහාසය, සහාය — ඇත්තේ පහළ දිගේ ඇති මෙනු ටැබය පිටුපසය.',
  'www.guide.d06.step3.note':
    'මෙය පාහේ සියලු දෙනාම එක් වරක් අල්ලා ගනී. ඔබ යමක් සොයමින් සිටී නම්, ඉහළ වෙනුවට ' +
    'තිරයේ පහළ දෙස බලන්න.',
  'www.guide.d06.step4':
    'සිතියමට උඩින් ඇති පුවරුවේ පොරොත්තු ටොගලය, දැනට සජීවී වාහනය එහි ලියාපදිංචි අංකය ' +
    'සමඟ, අද ඔබේ පළමු ගමන තවමත් නොමිලේද යන්න, දිශානුගත ගමන් වෙත යන මාර්ගය, සහ අද ' +
    'මේ දක්වා ඔබේ ගමන් සහ ඉපැයීම් ඇත.',
  'www.guide.d06.step5':
    'ඒ වෙනුවට බසයක් හෝ පෞද්ගලික වාහනයක් තෝරන්න, එවිට මුල් තිරය සම්පූර්ණයෙන්ම වෙනස් ' +
    'තිරයකි. එහි මාර්ග කාඩ්පතක්, ධාවනය වන කාලයක් සහ දුරක්, කාඩ්පතට පහළින් වාහන වර්ගය ' +
    'සහ අංකය, සහ බොත්තම් දෙකක් ඇත: ගමන ආරම්භ කරන්න සහ ගමන අවසන් කරන්න. පොරොත්තු ' +
    'සිතියමක් නැත, ටොගලයක්ද නැත — එම වාහන කැඳවනු ලබන්නේ නැත.',
  'www.guide.d06.step6':
    'මහජන බසයක් දෛනික ගාස්තුවක් කිසිසේත් ගෙවන්නේ නැත. පෞද්ගලික වාහනයකට දෛනිකව නොව ' +
    'මාසිකව අය කෙරේ.',
  'www.guide.d06.step7':
    'GPS ට්‍රැකරයක් සවි කර ඇති අතර ඉග්නිෂන් ක්‍රියාත්මක නම්, ඔබ යෙදුම විවෘත කිරීමට ' +
    'පෙරම ගමන ආරම්භ වී ඇති අතර, තිරය ඔබට ගමන අවසන් කරන්න ඉදිරිපත් කරයි.',
  'www.guide.d06.step7.note':
    'උපකරණය ඔබව අගුළු ලා නොතබයි. ට්‍රැකරය කරන දේ කුමක් වුවත්, ගමන ආරම්භ කරන්න සහ ගමන ' +
    'අවසන් කරන්න මෙම තිරයෙන් දෙපැත්තටම ක්‍රියා කරයි.',
  'www.guide.d06.callout.whichHomeScreen':
    'ඔබට පෙනෙන මුල් තිරය රඳා පවතින්නේ ඔබට වෙනස් කළ හැකි සැකසුමක් මත නොව, ඔබ තෝරාගෙන ' +
    'ඇති වාහනය මතය. මගේ වාහන තුළ සජීවී වාහනය මාරු කරන්න, මුල් තිරයද එය සමඟ මාරු වේ.',
  'www.guide.d06.callout.whoPaysWhat':
    'මහජන බස් වේදිකා ගාස්තුවක් ගෙවන්නේ නැත. පෞද්ගලික වාහන මාසිකව රු. 300ක් පමණ ගෙවයි ' +
    '— පිරිවිතරයේ කියන්නේ ආසන්න වශයෙන් බැවින්, අපිද එසේම කියමු. පොරොත්තු වාහන දිනකට ' +
    'එක් ස්ථාවර ගාස්තුවක්, වාහන වර්ගය අනුව සකසා, ඔවුන් වැඩ කරන දිනවල පමණක් ගෙවයි. ' +
    'මුදල් ප්‍රමාණවලට වෙනම පරිච්ඡේදයක් ඇත.',
  'www.guide.d06.callout.ownVehicleOnly':
    'ඔබේ උපකරණ පුවරු සිතියම ඔබට සීමා වී ඇත: එහි අඳින එකම වාහනය ඔබේම සක්‍රීය වාහනය ' +
    'වන අතර, වෙනත් රියදුරන් එහි කිසිදා පෙන්වන්නේ නැත.',

  // Chapter 7 · Going on standby
  'www.guide.d07.title': 'සබැඳි වීම',
  'www.guide.d07.summary':
    'එක් ටොගලයක් — ඒ ගැන දැනගැනීමට වටින සියල්ලම කොන්දේසියකි. එය අළු පැහැති වන්නේ ' +
    'කවදාද, දිනේ පළමු ගමනට කොපමණ වැය වේද, සහ ඉල්ලීමක් ශබ්දයක් නොමැතිව ඔබව පසුකර ' +
    'යා හැක්කේ ඇයිද යන්නය.',
  'www.guide.d07.step1':
    'පොරොත්තුව යනු උපකරණ පුවරුවේ ඇති විශාල ටොගලයයි. එය සක්‍රීය කළ විට, පද්ධතියට කුලී ' +
    'යැවිය හැකි රියදුරන්ගේ සමූහයට ඔබ එක් වේ; නිවා දැමූ විට, අළු ආවරණයක් ඔබට ඒ බව ' +
    'කියයි.',
  'www.guide.d07.step2':
    'පදවා ගැනීමට වාහනයක් ලැබෙන තුරු ටොගලය අක්‍රීයව පවතී — අනුමත වූ ඔබට අයිති එකක්, ' +
    'නැතහොත් වාහන සමූහයක් ඔබට බෙදාගත් හෝ තාවකාලිකව පැවරූ එකක්.',
  'www.guide.d07.step2.note':
    'වාහනයක් කිසිසේත් නැත්නම්, එකක් ලියාපදිංචි කිරීමට යෙදුම ඉදිරිපත් වේ. වාහන සමූහ ' +
    'පැවරුමක් හුදෙක් එහිම දිනයේදී කල් ඉකුත් වන අතර ඔබෙන් කිසිවක් ඉල්ලන්නේ නැත.',
  'www.guide.d07.step3':
    'සෑම දිනකම පළමු ගමන නොමිලේ. ඕනෑම වාහනයක, ඕනෑම දිනක, ඒ සඳහා ඔබේ පසුම්බියෙන් ' +
    'කිසිවක් ගන්නේ නැත.',
  'www.guide.d07.step4':
    'දෛනික ගාස්තුව එදිනේ ඔබේ දෙවන ගමනට පෙර, ඔබේ වාහන වර්ගය අනුව සකසන ලද එක් ස්ථාවර ' +
    'මුදලක් ලෙස අඩු වේ. ඉන් පසුව එදින, පසුව එන ගමන් කොපමණ වුවත් ගෙවා අවසන් වන අතර, ' +
    'ඔබ කිසිදා සබැඳි නොවන දිනක ඔබෙන් කිසිසේත් කිසිවක් අය නොකෙරේ.',
  'www.guide.d07.step5':
    'දෙවන ඉල්ලීමක් එන විට ඔබේ පසුම්බියට එය දරාගත නොහැකි නම්, ඉල්ලීම ප්‍රතික්ෂේප වීම ' +
    'වෙනුවට මඟ හැරෙන අතර, ඒ ඇයිදැයි ඔබට කියනු ලැබේ. හිස් පසුම්බියක් දෝෂයක් සේ ' +
    'නොපෙනේ — එය නිස්කලංක සන්ධ්‍යාවක් සේ පෙනේ.',
  'www.guide.d07.step6':
    'ඔබේ ශේෂය රු. 200 දක්වා පහත වැටුණු විට MageRide ඔබට අනතුරු අඟවයි, ඊට කලින් ඒ ' +
    'ගැන අසන්නට කැමති නම් යෙදුමෙන් ඔබේම සංඛ්‍යාවක් සැකසිය හැක.',
  'www.guide.d07.step7':
    'වරකට සජීවී වන්නේ ඔබේ වාහනවලින් එකක් පමණි — මගේ වාහන තුළ තෝරාගත් එකයි. නොබැඳි ' +
    'වීමෙන් ඔබ ධාවනය කරමින් සිටි දිශානුගත ගමන් පෙරහනක් තිබුණේ නම් එයද ඉවත් වන අතර, ' +
    'එය නැවත සැකසීම එදිනේ තවත් වාරයක් වැය කරයි.',
  'www.guide.d07.callout.firstTripFree':
    'දිනේ පළමු ගමන නොමිලේ, ඉන්පසු මුළු දිනටම වාහන වර්ගය අනුව සකසන ලද එක් ස්ථාවර ' +
    'ගාස්තුවක්. කොමිසයක් නැත, ගමනකට අයකිරීමක්ද නැත — මගියෙක් ඔබට ගෙවන ගාස්තුව ඔබේය. ' +
    'ඔබ සබැඳි නොවන දිනක කිසිසේත් ගාස්තුවක් නැත.',
  'www.guide.d07.callout.lowBalance':
    'දෛනික ගාස්තුව දරාගත නොහැකි පසුම්බියක් ඔබව නොබැඳි කරන්නේ නැත; එය දෙවන ගමනේ සිට ' +
    'ඔබ වෙත ඉල්ලීම් ළඟා වීම නවත්වන අතර, ඊට ඇති එකම ලකුණ කිසිවක් නොපැමිණීමයි. දිනය ' +
    'අතරතුර නොව, ආරම්භ කිරීමට පෙර පසුම්බිය පුරවා ගන්න.',
  'www.guide.d07.callout.oneVehicleLive':
    'වරකට එක් වාහනයක් ප්‍රකාශ කරයි. මගේ වාහන තුළ ඔබ තෝරන එකයි සිතියමේ ඇත්තේ, කුලී ' +
    'ගන්නේද, ඔබෙන් අය කරන දෛනික අනුපාතය තීරණය කරන්නේද එයයි.',

  // Chapter 8 · The fifteen-second offer
  'www.guide.d08.title': 'තත්පර පහළොවේ ගමන් ඉල්ලීම',
  'www.guide.d08.summary':
    'ගමන් ඉල්ලීමක් ආපසු ගණන් කරන වළල්ලක් සමඟ තිරය අත්පත් කර ගනී. කාඩ්පතේ ඇත්තේ ' +
    'කුමක්ද, ඔබ පිළිගත් විට සිදු වන්නේ කුමක්ද — සහ එකක් යාමට ඉඩ දීමෙන් ඔබට ඇත්තටම ' +
    'වැය වන්නේ කුමක්ද යන්නය.',
  'www.guide.d08.step1':
    'ඉල්ලීමක් ශබ්දය සහ කම්පනය සමඟ පූර්ණ තිර අත්පත් කර ගැනීමක් ලෙස පැමිණෙන අතර, එය ' +
    'කිරීමට නිදන දුරකථනයක් අවදි කරයි.',
  'www.guide.d08.step2':
    'කාඩ්පත ගාස්තුව, බාරගැනීම කොපමණ දුරින්ද, මගියා ඉල්ලූ වාහන කාණ්ඩය, ඔවුන් ගෙවීමට ' +
    'තෝරාගෙන ඇති ආකාරය, සහ බාරගැනීම හා බැසීමේ ස්ථාන දරයි. එය එක් අයෙක් තවත් අයෙකුට ' +
    'කළ වෙන් කිරීමක්ද, ප්‍රමාණය සහිත පාර්සලයක්ද, නැතහොත් ඔබේ දිශානුගත පෙරහනට ගැළපුණු ' +
    'කුලියක්ද යන්න ලාංඡන ඔබට කියයි.',
  'www.guide.d08.step3':
    'වළල්ලක් තත්පර පහළොවේ සිට ආපසු ගණන් කරන අතර, අවසන් පහ රතු පැහැයෙන් ස්පන්දනය වේ.',
  'www.guide.d08.step3.note':
    'එය එදිනේ ඔබේ දෙවන ගමන නම්, ඔබ පිළිගත් මොහොතේම දෛනික ගාස්තුව ඔබේ පසුම්බියෙන් ' +
    'අඩු වන බව කාඩ්පතේ පේළියක් ඔබට කියයි.',
  'www.guide.d08.step4':
    'පිළිගන්න කුලිය ගෙන ගමන් තිරය විවෘත කරයි. ඉඳහිට ඒ වෙනුවට ඉල්ලීම ගෙන ඇති බව ඔබට ' +
    'කියනු ලැබේ — තට්ටු දෙකක් එකවර වැටිය හැකි අතර දිනිය හැක්කේ එකකට පමණි, යෙදුම ඔබව ' +
    'අඩක් පවරනවා වෙනුවට එය පැහැදිලිව කියයි.',
  'www.guide.d08.step5':
    'ප්‍රතික්ෂේප කරන්න කුලිය වහාම ඊළඟ සුදුසුකම් ලත් රියදුරාට යවයි. තත්පර පහළොව ගෙවී ' +
    'යාමට ඉඩ දීමද මොහොතකට පසු එයම කරයි.',
  'www.guide.d08.step6':
    'මඟ හැරුණු හෝ ප්‍රතික්ෂේප කළ ඉල්ලීමකට දඬුවමක් නැත — පළමු එකටත් නැත, ඒ කිසිවකට ' +
    'දඩයක්, තහනමක් හෝ සිසිල් වීමේ කාලයක් ප්‍රකාශිත නීතියක් අමුණන්නේද නැත. දිගින් ' +
    'දිගටම ප්‍රතික්ෂේප කිරීම වෙනස් කරන දෙය නම් ඔබේම මට්ටම් තිරයේ ඔබට පෙන්වන ඔබේ ' +
    'පිළිගැනීමේ අනුපාතයයි. එම සංඛ්‍යාව වැදගත් වීමට පටන් ගන්නේ කොතැනදැයි කිසිවක් ' +
    'නිශ්චිතව කියා නැත, එබැවින් මෙම මාර්ගෝපදේශය අනුමාන කරන්නේ නැත.',
  'www.guide.d08.step7':
    'කලින් වෙන් කළ ගමන් ඔබ වෙත ළඟා වන්නේ රැකියා පුවරුව හරහාය: කිලෝමීටර තිහක් ඇතුළත ' +
    'ඇති සෑම නියමිත ගමනක්ම, මට්ටම 2 සහ ඊට ඉහළ රියදුරන්ට විවෘතව. ඔබට අවශ්‍ය ඒවාට ඔබ ' +
    'කැමැත්ත දන්වයි. පුවරුවෙන්ම ඔබට පිළිගත නොහැක.',
  'www.guide.d08.step8':
    'ගමනට මිනිත්තු තිහකට පෙර එය කැමැත්ත දැන්වූ ළඟම රියදුරාට, මෙම එකම තත්පර පහළොවේ ' +
    'තිරයේම ඉදිරිපත් වේ — දෙදෙනෙක් සමානව ළං නම්, ඉහළ මට්ටමට මුලින්ම ඇමතේ. එය ' +
    'පිළිගන්නේ එහිදීය, තවමත් ප්‍රතික්ෂේප කළ හැක්කේද එහිදීය.',
  'www.guide.d08.callout.whatAMissCosts':
    'ඉල්ලීමක් යාමට ඉඩ දීමෙන් ඔබට කිසිවක් වැය නොවේ. එය ඊළඟ රියදුරාට යන අතර, පළමු ' +
    'එකටවත්, දෙවන එකටවත්, දහවන එකටවත් දඬුවමක් නැත. ප්‍රතික්ෂේප කිරීමේ රටාවක් ඔබේ ' +
    'මට්ටම් තිරයේ ඔබේ පිළිගැනීමේ අනුපාතය ලෙස දිස් වන අතර, එම අනුපාතය ඊට වඩා යමක් ' +
    'කරන ලක්ෂ්‍යයක් ප්‍රකාශිත කිසිවක් සකසා නැත.',
  'www.guide.d08.callout.secondTripFee':
    'දෛනික ගාස්තුව ඔබේ පසුම්බියෙන් යන්නේ එදිනේ දෙවන කුලිය ඔබ පිළිගත් මොහොතේය — එය ' +
    'අවසානයේ නොවේ, ඉන් පසුව එන ගමන්වලට නැවත නොවේ. මුදල ඔබේ වාහන වර්ගය අනුව යන අතර ' +
    'මුළු දිනටම එක් වරක් අය කෙරේ.',
  'www.guide.d08.callout.offerTaken':
    'ඉල්ලීම් යන්නේ එකවර සියලු දෙනාට නොව වරකට එක් රියදුරෙකුටය, "ඉල්ලීම ගෙන ඇත" ' +
    'දුර්ලභ වන්නේ ඒ නිසාය. එය දිස් වන විට, තවත් රියදුරෙකුගේ තට්ටුව මුලින්ම වැටී ඇත; ' +
    'ඔබට එරෙහිව කිසිවක් තබා නොගන්නා අතර ඊළඟ ඉල්ලීමට ඉන් බලපෑමක් නැත.',

  // Chapter 9 · Running a trip
  'www.guide.d09.title': 'ගමනක් මෙහෙයවීම',
  'www.guide.d09.summary':
    'පිළිගැනීමේ සිට අවසන් කිරීම දක්වා: බාරගැනීමේ ස්ථානයට යාම, ගමන ආරම්භ කරන කේතය, ' +
    'මගියාට ඇමතීම, සහ ඔබ අවසන් තට්ටු කළ විට සිදු වන දේ.',
  'www.guide.d09.step1':
    'පිළිගැනීමෙන් ගමන් තිරය විවෘත වේ — බාරගැනීමේ ස්ථානයට යන සංචාලන සිතියමක්, ඊට ඇති ' +
    'දුර සහ කාලය, මගියාගේ නම සහ ශ්‍රේණිගත කිරීම, සහ ඔවුන් යන්නේ කොහේද යන්න.',
  'www.guide.d09.step2':
    'බාරගැනීමේ ස්ථානයට රිය පදවන්න. ඔබ බාරගැනීමේ කලාපය තුළට පැමිණි පසු ගමන ඉබේම ' +
    'පැමිණියා තත්ත්වයට යයි; ඔබ එතැනට පැමිණි බව කීමට තට්ටු කිරීමට කිසිවක් නැත.',
  'www.guide.d09.step3':
    'මගියාගෙන් ඔවුන්ගේ ආරම්භක කේතය අසා එය ටයිප් කරන්න. කේතය ඇත්තේ ඔවුන්ගේ තිරයේය. ' +
    'එය ඇතුළත් කරන තුරු ගමන ආරම්භ නොවේ.',
  'www.guide.d09.step3.note':
    'වැරදි කේතයක් ඒ බව කියන අතර කිසිවක් නැති නොවේ — නැවත අසා එය නැවත ඇතුළත් කරන්න. ' +
    'ඔබ එය නොමැතිව රිය පදවා ගියොත්, ගමන ආරම්භ වී නැති අතර එය ක්‍රියාත්මක ලෙස සටහන් ' +
    'වන්නේද නැත.',
  'www.guide.d09.step4':
    'ඔවුන් එහි නැත්නම්, බලා සිටින්න. මිනිත්තු පහකට සහ මතක් කිරීමේ පණිවිඩ දෙකකට පසු ' +
    'ඔවුන් නොපැමිණීමක් ලෙස ගණන් ගැනේ: ඔවුන්ගෙන් රු. 100ක් අය කරන අතර, බලා සිටීම ' +
    'වෙනුවෙන් ඔබට වන්දි ලැබේ.',
  'www.guide.d09.step5':
    'මගියාට අමතන්න ගමන් තිරයේ ඇත. යෙදුම හරහා නොමිලේ ඇමතිය හැක, නැතහොත් ඔවුන්ගේ අංකයට ' +
    'සාමාන්‍ය ඇමතුමක් ගත හැක.',
  'www.guide.d09.step5.note':
    'අංක සඟවා නැත. ඔබ පිළිගත් පසු, ඔබටත් මගියාටත් එකිනෙකාගේ ජංගම දුරකථන අංක පෙනේ — ' +
    'තව ද එක් අයෙක් තවත් අයෙකුට කළ වෙන් කිරීමකදී, ඔබට පෙනෙන්නේ ඔබ රැගෙන යන ගමන් ' +
    'කරන්නා මිස කිසිවිටෙක වෙන් කළ පුද්ගලයා නොවේ.',
  'www.guide.d09.step6':
    'ගමනාන්තයට රිය පදවා අවසන් තට්ටු කරන්න. එම මොහොතේ ගාස්තුව අවසන් වශයෙන් තීරණය වන ' +
    'අතර, මගියා වෙන් කරන විට තෝරාගත් ක්‍රමයෙන් සමථයට පත් වේ.',
  'www.guide.d09.step7':
    'ඔවුන් ඔබේම QR කේතය පරිලෝකනය කර ගෙවුවේ නම්, එය ලැබුණු බව තහවුරු කරන ලෙස යෙදුම ' +
    'ඔබෙන් ඉල්ලයි. ඔබේ ඉපැයීම සටහන් වන්නේ ගමන අවසන් වූ මොහොතේ නොව ගෙවීම අවසන් වූ ' +
    'පසුය, අවසන් වූ ගමනක් තම මුදල ටිකක් පසුව පෙන්විය හැක්කේ ඒ නිසාය.',
  'www.guide.d09.step8':
    'කලින් වෙන් කළ ගමනක් හරියටම මෙය මෙන් ධාවනය වේ. එය ඔබේ නියමිත ගමන් ලැයිස්තුවේ ' +
    'රැඳී සිටින අතර, මිනිත්තු තිහකට පෙර ඔබට මතක් කරන අතර, ඉන්පසු එය අන් ඕනෑම ගමනක් ' +
    'මෙන් ආරම්භක කේතය, රිය පැදවීම සහ අවසන් කිරීමයි.',
  'www.guide.d09.callout.noCodeNoTrip':
    'මගියාගේ කේතය නොමැතිව ගමන ආරම්භ නොවේ. එය විකල්පයක් නොවේ, විධිමත් කටයුත්තක්ද නොවේ ' +
    '— නව රියදුරෙකු සහායට ඇමතීමට වැඩිම ඉඩක් ඇති දෙයද එයයි. ඔවුන්ට ආචාර කරන විටම ' +
    'එය ඉල්ලන්න.',
  'www.guide.d09.callout.realNumbers':
    'කුලිය පිළිගත් පසු ඔබටත් ඔබේ මගියාටත් එකිනෙකාගේ සැබෑ ජංගම දුරකථන අංක පෙනෙන අතර, ' +
    'ඔබ ලියාපදිංචි වන විට MageRide ඔබට ඒ බව කියයි. කිසිවෙකු පවරන්නට පෙර අවලංගු කළ ' +
    'ගමනකට අංක නොදෙන අතර, වෙනත් අයෙකුට කළ වෙන් කිරීමකදී ඔබට ලැබෙන්නේ ගමන් කරන්නාගේ ' +
    'අංකය මිස කිසිවිටෙක වෙන් කළ අයගේ අංකය නොවේ.',
  'www.guide.d09.callout.scheduledNoShow':
    'ඔබ පිළිගත් නියමිත ගමනකට නොපැමිණීම ඔබේ රියදුරු මට්ටම එකකින් පහත හෙළයි. එය එසේ ' +
    'කරන දේවල් දෙකෙන් එකකි — අනෙක මගී පැමිණිලි තුනක් එකතු කර ගැනීමයි — සාමාන්‍ය ' +
    'ඉල්ලීමක් යාමට ඉඩ දීම ඉන් එකක් නොවේ.',

  // =========================================================================
  // S11 · the driver guide, chapters 10–18 — translated in S12.
  //
  // S11's handoff named chapter 13 as the one to translate most carefully: it is
  // a commercial claim made to a driver, and its six rupee figures are *not* in
  // this table — they render from `DAILY_FEE_TIERS` — so a translator cannot
  // damage them. What a translator can damage is the sentence around them, and
  // in Sinhala the load-bearing words are `කොමිසයක් නැත` (zero commission),
  // `දිනකට` (per day, never per trip) and `පමණ` (approximately). None may be
  // dropped for rhythm.
  // =========================================================================

  // Chapter 10 · Directional travel
  // Every quantity below is a **default** and the Sinhala says so each time:
  // `පෙරනිමිය` / `සැකසුම්` where the English says "settings rather than fixed
  // rules". Printing them as rules would make the site wrong the day one changes.
  'www.guide.d10.title': 'ගෙදර දෙසට රිය පැදවීම',
  'www.guide.d10.summary':
    'දිශානුගත ගමන් ඔබට ලැබෙන කුලී, ඔබේ දිශාවට යන ඒවාට පමණක් සීමා කරයි. එය භාවිත ' +
    'කිරීමට පෙර තේරුම් ගැනීම වටී, මන්ද එය සීමිතය, එය කල් ඉකුත් වේ, තව ද කලින් නිවා ' +
    'දැමීමෙන්ද එදිනේ වාරයක් වැය වන නිසාය.',
  'www.guide.d10.step1':
    'ඔබ සබැඳිව සිටින අතරතුර, උපකරණ පුවරුවෙන් දිශානුගත ගමන් විවෘත කරන්න. එය පොරොත්තු ' +
    'ටොගලය අසල ඇති කූඤ්ඤයයි.',
  'www.guide.d10.step2':
    'ඔබ යන්නේ කොහේදැයි තෝරන්න — ලිපිනයක් සොයන්න, සිතියමේ ලකුණක් තබන්න, නැතහොත් ගෙදර ' +
    'සුරැකි ඇත්නම් එය තෝරන්න. ඔබ දැන් සිටින තැනින් එය ගණනය කරන දිශාව සලකුණක් පෙන්වයි.',
  'www.guide.d10.step3':
    'ඔබ එකකට බැඳීමට පෙර, අද ඔබට ඉතිරිව ඇති වාර කීයද සහ එක් වාරයක් කොපමණ කල් පවතීද ' +
    'යන්න තිරය කියයි. දිශාව සකසන්න තට්ටු කරන්න, එය ආරම්භ වේ.',
  'www.guide.d10.step3.note':
    'අද MageRide දිනකට දෙකක්, එක් එකක් පැය දෙකක් දක්වා ඉඩ දෙයි. ඒ දෙකම ස්ථිර නීති ' +
    'නොව සැකසුම් වන අතර, ඔබට අදාළ වන සංඛ්‍යා තිරය සැමවිටම පෙන්වයි.',
  'www.guide.d10.step4':
    'ඉන්පසු එය ක්‍රියාත්මක වන තාක් ඔබේ තිරයේ බැනරයක් රැඳී සිටින අතර, ඔබේ ගමනාන්තය, ' +
    'ඉතිරි කාලය, සහ ඔබ භාවිත නොකළ වාර පෙන්වයි. එය සක්‍රීය බව ඔබට අමතක කළ නොහැක, ' +
    'එය හිතාමතාය.',
  'www.guide.d10.step5':
    'එතැන් සිට ඔබට ලැබෙන්නේ ඔබේ දිශාවට යන කුලී පමණි: ගමන ඔබව ඔබේ ගමනාන්තය දෙසට ගෙන ' +
    'යා යුතුය, බාරගැනීම දළ වශයෙන් ඔබේ මාර්ගයේ තිබිය යුතුය, තව ද බැසීමේ ස්ථානය ' +
    'බාරගැනීමට වඩා ඔබව ඔබ යන තැනට ළං කළ යුතුය.',
  'www.guide.d10.step6':
    'එය කල් ඉකුත් වීමට මිනිත්තු දහයකට පෙර ඔබට මතක් කිරීමක් ලැබෙන අතර බැනරය ස්පන්දනය ' +
    'වීමට පටන් ගනී. එය කල් ඉකුත් වූ විට තමන්වම ඉවත් වන අතර, ඔබ සුදුසුකම් ලබන සියල්ල ' +
    'නැවත ලැබීමට පටන් ගනී.',
  'www.guide.d10.step6.note':
    'ගැළපෙන කුලියක් පිළිගැනීමෙන් එය අවසන් නොවේ — එකම දිශාවට තවත් එකක් අමුණා ගත හැකි ' +
    'වන පරිදි එය දිගටම ධාවනය වේ. ඒ වෙනුවට පළමු ගැළපුණු ගමනෙන් පසු එය ඉවත් වන ලෙස ' +
    'MageRide හට වින්‍යාස කළ හැකි අතර, ඔබේ එක සකසා ඇත්තේ කුමන ආකාරයටදැයි යෙදුම ඔබට ' +
    'කියයි.',
  'www.guide.d10.step7':
    'කලින් නිවා දැමීමෙන් එය වහාම අවසන් වේ — එහෙත් වාරය වැය වේම. නොබැඳි වීමද එසේමය, ' +
    'එය නොඅසාම එය ඉවත් කරයි. එය කරන්නේ කුමක්දැයි බැලීමට වෙනුවට, ඔබ එදිනට ඇත්තටම ' +
    'අවසන් කළ විට එය සකසන්න.',
  'www.guide.d10.step8':
    'මගී ගමන්වලට මෙන්ම පාර්සල් රැකියාවලටද එය එලෙසම ක්‍රියා කරන අතර, එය කිසිවක් ' +
    'අභිබවා යන්නේ නැත: කෙසේවත් ඔබට නොලැබෙන කුලියක්, ඔබ ඒ දිශාවට යන නිසාම ලැබෙන්නේ ' +
    'නැත.',
  'www.guide.d10.callout.turningItOffCosts':
    'දිශානුගත ගමන් කල් ඉකුත් වීමට පෙර නිවා දැමීමෙන් එම වාරය වැය වේම, එය ක්‍රියාත්මක ' +
    'වන අතරතුර නොබැඳි වීමෙන්ද එසේමය. දෛනික සීමාව වටා යාම නැවැත්වීමට එය හිතාමතාම ' +
    'එසේ ලියා ඇති අතර, රියදුරන් වැඩිපුරම හසු වන දෙයද එයයි.',
  'www.guide.d10.callout.narrowsNeverWidens':
    'දිශානුගත ගමන් ඉල්ලීම් ඉවත් කරයි පමණි; එය කිසිදා එකක් එකතු කරන්නේ නැත. එය ඔබේ ' +
    'ගාස්තුව, දෛනික ගාස්තුව, හෝ පෝලිමේ ඔබේ ස්ථානය වෙනස් කරන්නේ නැත — එය ඔබ වෙත ' +
    'ළඟා වන දේ පෙරහන් කරයි, ඊට වඩා කිසිවක් නොකරයි.',
  'www.guide.d10.callout.noPenalty':
    'එය ක්‍රියාත්මක වන අතරතුර ඔබේ දිශාවට කුලී නොපැමිණියොත්, ඔබට කිසිවක් සිදු නොවේ. ' +
    'ඔබේ රියදුරු මට්ටමට බලපෑමක් නැත, ඔබේ පිළිගැනීමේ අනුපාතයටද නැත — පෙරහන සක්‍රීයව ' +
    'ගත වන නිස්කලංක පැයකින් ඔබට වැය වන්නේ එම පැය පමණි.',

  // Chapter 11 · Package jobs
  // The payment rails are deliberately not enumerated here either — MCS-35 D3 is
  // open and the retired labels must not be printed. Cash on delivery is named
  // because the driver has to physically collect it.
  'www.guide.d11.title': 'පාර්සලයක් බෙදාහැරීම',
  'www.guide.d11.summary':
    'පාර්සලයක් පාර්සල් ලාංඡනයක් සහිත සාමාන්‍ය ඉල්ලීමක් ලෙස පැමිණෙන අතර, ඉන්පසු පත් ' +
    'තුනක් සහ කේත දෙකක් ලෙස ධාවනය වේ: සමාලෝචනය හා ආරම්භය, බාරගැනීම, බාරදීම.',
  'www.guide.d11.step1':
    'පාර්සල් රැකියා ඔබ වෙත එන්නේ ගමන් එන ආකාරයටමය — එකම තත්පර පහළොවේ ඉල්ලීම, පාර්සල් ' +
    'ලාංඡනයක්, ප්‍රමාණය, සහ ඇතුළත ඇත්තේ කුමක්ද යන කෙටි විස්තරයක් සමඟ. ඒ මත පදනම්ව ' +
    'ඔබට එය ප්‍රතික්ෂේප කළ හැක.',
  'www.guide.d11.step1.note':
    'බෙදාහැරීම් කිසිදු එක් වර්ගයක වාහනයකට සීමා නොවේ. යතුරුපැදියක් පාර්සල් ගෙන යයි; ' +
    'වෑන් රථයක්ද එසේමය. ට්‍රක් සහ මිනි ට්‍රක් රථ ඇත්තේ මගීන් ගෙන යන වාහන වෙනුවට නොව, ' +
    'ඒවාට අමතරව බෙදාහැරීම් සඳහාය.',
  'www.guide.d11.step2':
    'පිළිගැනීමෙන් පත් තුනෙන් පළමුවැන්න විවෘත වේ: සමාලෝචනය සහ ආරම්භය. එය බාරගැනීම ' +
    'කොපමණ දුරද, බාරදීම කොපමණ දුරද, බෙදාහැරීමට ගෙවන්නේ කෙසේද, සහ එවන්නාගේ හා ' +
    'ලබන්නාගේ දුරකථන අංක දෙකම, එක් එකට ඇමතුම් බොත්තමක් සමඟ පෙන්වයි.',
  'www.guide.d11.step3':
    'එය ඔබට ගැළපේ නම්, බෙදාහැරීම අරඹන්න තට්ටු කරන්න. නොගැළපේ නම්, අවලංගු කරන්න තට්ටු ' +
    'කරන්න — රැකියාව කෙළින්ම ඊළඟ සුදුසුකම් ලත් රියදුරාට යන අතර ඔබට එරෙහිව කිසිවක් ' +
    'තබා නොගනී.',
  'www.guide.d11.step3.note':
    'තීරණය කිරීමට ඇති මොහොත මෙයයි. මෙහිදී අවලංගු කිරීම සාමාන්‍ය දෙයකි; ඔබ දැනටමත් ' +
    'බාරගෙන ඇති පාර්සලයක් අත්හැරීම එසේ නොවේ, ඒ සඳහා පතක්ද නැත.',
  'www.guide.d11.step4':
    'දෙවන පත ඔබව එවන්නා වෙත ගෙන යයි. එහි සිතියම, එවන්නාට අමතන්න බොත්තමක්, SOS, සහ ' +
    'ඉලක්කම් හතරක බාරගැනීමේ කේතයක් ඇත.',
  'www.guide.d11.step5':
    'එවන්නාගෙන් එම කේතය අසා එය ඇතුළත් කරන්න. පාර්සලය බාරගත් තත්ත්වයට පත් වන අතර, එය ' +
    'මගදී බව ලබන්නාට කියනු ලැබේ, තෙවන පත විවෘත වේ. වැරදි කේතයක් ඒ බව කියයි; වැරදි ' +
    'උත්සාහ පහකට පසු එය අගුළු වැටී සහායට යයි.',
  'www.guide.d11.step6':
    'දොරකඩදී, ලබන්නාගෙන් ඔවුන්ගේම ඉලක්කම් හතරක බාරදීමේ කේතය අසා එය ඇතුළත් කරන්න. ' +
    'දුරකථන අංක දෙකම ඇමතුම් බොත්තම් සමඟ මෙම පතේද ඇත.',
  'www.guide.d11.step7':
    'ඔබට කේතයක් දීමට කිසිවෙක් නැත්නම්, ඒ වෙනුවට ඡායාරූප සාක්ෂි ඉදිරිපත් වේ. ඔබ ' +
    'පාර්සලය තැබූ තැන එය ඡායාරූප ගන්න — ඔබ එය බාර දුන් බවට වාර්තාව ලෙස, එහි පිහිටීම ' +
    'සමඟ එම පින්තූරය අමුණනු ලැබේ.',
  'www.guide.d11.step8':
    'ඉන්පසු බෙදාහැරීම සම්පූර්ණයි තට්ටු කරන්න. පාර්සලයට කලින් ගෙවා තිබුණත් දොරකඩදී ' +
    'මුදලින් ගෙවුවත්, එම එක් බොත්තම රැකියාව අවසන් කරයි — වෙනම "මුදල් ලැබුණා" ' +
    'බොත්තමක් තවදුරටත් නැත.',
  'www.guide.d11.callout.codAndAbsentRecipient':
    'භාරදීමේදී මුදල් ගෙවන පාර්සලයක, එය සම්පූර්ණ කිරීමට පෙර මුදල් එකතු කරන්න, ගෙවීමට ' +
    'එහි කිසිවෙක් නැත්නම්, පාර්සලය නොතබන්න, බෙදාහැරීමද සම්පූර්ණ නොකරන්න — සහාය ' +
    'අමතන්න. කිසිදා ගණන් නොගත් මුදල් දිනකට පසු බෙදාහැරීම ආරවුලක් බවට පත් කරයි.',
  'www.guide.d11.callout.sameFeeSameTariff':
    'බෙදාහැරීමක් මගියාගෙන් අය කරන්නේ ගමනකට මෙන් එකම ගාස්තු වගුවෙනි, එය ඔබේ දිනයටද ' +
    'එලෙසම ගණන් ගැනේ: එදිනේ ඔබේ පළමු රැකියාව, එය පාර්සලයක් වුවත් පුද්ගලයෙක් වුවත් ' +
    'නොමිලේ වන අතර, දෛනික ගාස්තුව සඳහා බෙදාහැරීම් සහ ගමන් එකට ගණන් ගැනේ.',
  'www.guide.d11.callout.cancelAtReview':
    'විස්තර දැක බලා නැත කීමට හැකි වන පිණිසය සමාලෝචන පත ඇත්තේ. එහිදී අවලංගු කරන්න, ' +
    'රැකියාව වහාම ඊළඟ රියදුරාට ඉදිරිපත් වේ — එය අසාර්ථක වීමක් නොව එම පත ක්‍රියා ' +
    'කිරීමයි.',

  // Chapter 12 · Your wallet
  'www.guide.d12.title': 'ඔබේ පසුම්බිය',
  'www.guide.d12.summary':
    'පසුම්බිය යනු ඔබේ ගාස්තු වැටෙන තැන නොවේ — එය ඔබ දමන, දෛනික වේදිකා ගාස්තුව අඩු ' +
    'වන මුදලයි. එය පිරවීමට ක්‍රම තුනක්, සියල්ලම යෙදුම තුළ.',
  'www.guide.d12.step1':
    'පසුම්බිය පහළ තීරුවේ ඇත. එය ශේෂයක්, ඔබ සජීවීව ඇති වාහනයේ දෛනික අනුපාතය, සහ අද ' +
    'ගාස්තුව අඩු කර තිබේද යන්න පෙන්වයි.',
  'www.guide.d12.step1.note':
    'ශේෂය යනු ඔබේ ඉපැයීම් නොවේ. මුදල් ගාස්තුවක් ඔබේ අතට යන අතර ඔබේම බැංකු QR එකට ' +
    'ගෙවූ ගාස්තුවක් ඔබේ බැංකුවට යයි — දෙකෙන් එකක්වත් මෙය හරහා යන්නේ නැත. ඔබේ මුදල් ' +
    'ඇත්තටම ළඟා වන්නේ කොහේද යන්න 14 වන පරිච්ඡේදයයි.',
  'www.guide.d12.step2':
    'මෙම තිරයේ ශේෂය කියවීමට පමණි. එය වෙනස් වන්නේ ඔබ පිරවූ විට, දෛනික ගාස්තුවක් අඩු ' +
    'වූ විට, සහ ඔබ සහ තවත් රියදුරෙකු අතර ණය ගමන් කළ විටය.',
  'www.guide.d12.step3':
    'ණය පිරවීම ගෙවීමට ක්‍රම තුනක් විවෘත කරයි: ණය හෝ හර කාඩ්පතක්, OnePay, හෝ LankaQR. ' +
    'තුනම යෙදුම තුළ ඇති අතර තුනම ඔබේ පසුම්බියට වහාම බැර කරයි.',
  'www.guide.d12.step4':
    'LankaQR අධිභාරයක් දරන්නේ නැත. කාඩ්පත සහ OnePay, OnePay හි සැකසුම් ගාස්තුව දරයි, ' +
    'එය ගෙවීම භාර ගැනීම සඳහා ගෙවීම් සමාගමේ අයකිරීම මිස MageRide කොමිසයක් නොවේ.',
  'www.guide.d12.step4.note':
    'බැංකු මාරු කිරීමේ විකල්පයක් නැත, කිසිදා නොවනු ඇත — එය වේදිකාවෙන් ඉවත් කරන ලදී. ' +
    'කවුරුන් හෝ ඔබට ගෙවීමට MageRide බැංකු ගිණුමක් දෙන්නේ නම්, එය MageRide නොවේ.',
  'www.guide.d12.step5':
    'එම තිරයම වට්ටමකට තොග ණය වවුචර් විකුණයි, එය 15 වන පරිච්ඡේදයයි. එකක් මිලදී ගැනීම ' +
    'හුදෙක් එහි මුහුණත වටිනාකමට වඩා අඩුවෙන් ඔබට වැය වන විශාල ණය පිරවීමකි.',
  'www.guide.d12.step6':
    'සෑම ණය පිරවීමක්ම යොමු අංකයක් සහිත තහවුරු කිරීමකින් අවසන් වන අතර, රිසිට්පත සුරැකිය ' +
    'හෝ බෙදාගත හැක. ඔබේ මුළු ඉතිහාසය ගෙවීම් ඉතිහාසය යටතේ ඇති අතර, ඕනෑම දින පරාසයකට ' +
    'ප්‍රකාශයක් බාගත කළ හැක.',
  'www.guide.d12.step7':
    'ශේෂය එහි අඩු ශේෂ මට්ටමට වැටුණු විට MageRide ඔබට අනතුරු අඟවන අතර, ඊට කලින් ඒ බව ' +
    'දැනගැනීමට කැමති නම් ඔබේම සංඛ්‍යාවක් සැකසිය හැක.',
  'www.guide.d12.step8':
    'ශේෂයක් ශුන්‍යයට වඩා පහළට යා හැක — MageRide කරන ගැළපීමක්, හෝ ඔබ පිළිගත් පසු ' +
    'අවලංගු කළ ගමනකට වන අයකිරීමක්, එය එතැනට ගෙන යා හැක. ඉන්පසු නැවත වැඩ ආරම්භ කිරීමට ' +
    'ඔබ එකතු කළ යුත්තේ කුමක්දැයි තිරය පෙන්වයි.',
  'www.guide.d12.callout.noBankTransfer':
    'MageRide පසුම්බියක් පිරවීමට ඇති එකම ක්‍රම කාඩ්පත, OnePay සහ LankaQR ය. බැංකු ' +
    'මාරු කිරීම ඉන් එකක් නොවේ — එය ඉවත් කරන ලදී — එබැවින් MageRide බැංකු ගිණුමකට ' +
    'මුදල් මාරු කරන ලෙස ඉල්ලීමක් එන්නේ MageRide වෙතින් නොවේ.',
  'www.guide.d12.callout.neverAWebPortal':
    'ඔබේ පසුම්බියට සම්බන්ධ සියල්ල සිදු වන්නේ රියදුරු යෙදුම තුළය. රියදුරෙකු ලෙස ඔබ ' +
    'පිවිසෙන MageRide වෙබ් අඩවියක් නැත, එබැවින් ඔබේ MageRide පිවිසුම ඉල්ලන පිටුවක්, ' +
    'එය පෙනෙන ආකාරය කුමක් වුවත්, අපගේ නොවේ.',
  'www.guide.d12.callout.whatTheWalletIsFor':
    'පසුම්බිය පවතින්නේ දෛනික වේදිකා ගාස්තුව ගෙවීමට වන අතර, MageRide කිසිදා ඔබෙන් ' +
    'ගන්නා එකම දෙයද එයයි. කිසිදු ගාස්තුවකින් කොමිසයක් නැත — මගියෙක් ඔබට ගෙවන දේ ' +
    'ඔබේය.',

  // Chapter 13 · The daily platform fee
  //
  // **No rupee figure appears in this chapter's copy, in any language.** The six
  // tiers render from `DAILY_FEE_TIERS` in `src/content/marketing.ts`, in minor
  // units, with the URD table named beside them and `test/content.test.ts`
  // asserting them. A number typed here would become three numbers across the
  // three tables, in a file no test reads.
  'www.guide.d13.title': 'දෛනික වේදිකා ගාස්තුව',
  'www.guide.d13.summary':
    'MageRide කොමිස් ගන්නේ නැත. මගීන් ඔවුන්ගේ ගාස්තු ඔබට කෙළින්ම ගෙවන අතර, වේදිකාව ' +
    'දිනකට ස්ථාවර ගාස්තුවක් අය කරයි — කිසිදා ගමනකට නොවේ. සෑම දිනකම ඔබේ පළමු ගමන ' +
    'නොමිලේය.',
  'www.guide.d13.step1':
    'සෑම ගාස්තුවකම සෑම රුපියලක්ම ඔබට ඉතිරි වේ. ඕනෑම වාහනයක, ඕනෑම ක්‍රමයක, ඕනෑම ' +
    'ගමනකින් MageRide කොමිසයක් හෝ කැපීමක් ගන්නේ නැත. ඉන්දියාවේ Namma Yatri ගොඩනැගූ ' +
    'එකම ආකෘතිය මෙයයි, වේදිකාවේ මුළු පදනමද එයයි.',
  'www.guide.d13.step2':
    'දිනේ පළමු ගමන සැමවිටම නොමිලේය. ඒ සඳහා ඔබේ පසුම්බියෙන් කිසිවක් ගන්නේ නැත.',
  'www.guide.d13.step3':
    'එදිනේ ඔබේ දෙවන ගමන පිළිගත් විට, ඔබේ පසුම්බියෙන් එක් ස්ථාවර ගාස්තුවක් අඩු වේ — ' +
    'එක් වරක්, මුළු දිනටම, ඉන් පසුව ගමන් කොපමණ තිබුණත්.',
  'www.guide.d13.step3.note':
    'අඩු කිරීම දෙවන ගමනේදී වැටෙන නිසා, එය හරියටම එක් වරක් ගමනකට වන අයකිරීමක් සේ ' +
    'පෙනිය හැක. එය එසේ නොවේ. MageRide හි කිසිම තැනක ගමනකට ගාස්තුවක් නැත.',
  'www.guide.d13.step4':
    'එම ස්ථාවර ගාස්තුව කුමක්ද යන්න රඳා පවතින්නේ ඔබ පදවන්නේ කුමක්ද යන්න මතය. සෑම ' +
    'වාහන වර්ගයකටම එහිම අනුපාතයක් ඇති අතර, මෙම පිටුවේ වගුව සම්පූර්ණ කට්ටලයයි. ඔබට ' +
    'දැනට සජීවීව ඇති වාහනයේ අනුපාතය යෙදුම සැමවිටම පෙන්වයි.',
  'www.guide.d13.step5':
    'මහජන ප්‍රවාහන බස් කිසිසේත් කිසිවක් ගෙවන්නේ නැත, පෞද්ගලික ප්‍රවාහන වාහනවලට ' +
    'ඒවායේ රියදුරාට දෛනිකව නොව හිමිකරුට මාසිකව බිල් කෙරේ. දෛනික ගාස්තුව යනු පොරොත්තු ' +
    'ඉල්ලුම මත ක්‍රමයට පමණක් අදාළ සැකසුමකි.',
  'www.guide.d13.step6':
    'ඔබ කිසිදා සබැඳි නොවන දිනක, ඔබෙන් කිසිවක් අය නොකෙරේ. මාසික අවම මුදලක් නැත, ඔබ ' +
    'රිය පදවුවත් නැතත් ධාවනය වන දායකත්වයක්ද නැත, ඔබ පාරෙන් ඉවතේ ගත කරන දිනකට ' +
    'අයකිරීමක්ද නැත.',
  'www.guide.d13.step6.note':
    'ඔබ වාහනයකට වඩා තබා ගන්නේ නම්, වරකට සජීවී වන්නේ එකක් පමණක් වන අතර ඔබ ගෙවන්නේ ' +
    'එම වාහනයේ අනුපාතයයි. එකම දිනක එකම වාහනයට ඔබෙන් කිසිදා දෙවරක් අය නොකෙරේ.',
  'www.guide.d13.step7':
    'ඔබේ දෙවන ගමන එන විට ඔබේ පසුම්බියට ගාස්තුව දරාගත නොහැකි නම්, එම ඉල්ලීම ප්‍රතික්ෂේප ' +
    'වීම වෙනුවට මඟ හැරෙන අතර, ඒ ඇයිදැයි ඔබට කියනු ලැබේ. දෙවන ගමනට ප්‍රමාණවත් නොමැති ' +
    'නම්, පළමු ගමන පිළිගන්නා විටදීම ඔබට අනතුරු අඟවනු ලැබේ.',
  'www.guide.d13.step8':
    'ගාස්තු ඉතිහාසය සෑම අඩු කිරීමක්ම පෙන්වයි — දිනය, වාහනය, මුදල, සහ එදින ඔබ ගමන් ' +
    'කීයක් කළාද යන්න — ඔබේ ණය පිරවීම් සහ මාරු කිරීම් අසල. ගාස්තුවක් වැරදීමකින් අඩු ' +
    'වී ඇත්නම්, එය ආපසු ඉල්ලන ආකාරය 18 වන පරිච්ඡේදයයි.',
  'www.guide.d13.callout.zeroCommission':
    'ශුන්‍ය කොමිස්, එය වචනාර්ථයෙන්ම අදහස් කෙරේ: මගීන් ඔවුන්ගේ ගාස්තු ඔබට කෙළින්ම ' +
    'ගෙවන අතර MageRide එයින් කිසිවක් ගන්නේ නැත. දෛනික වේදිකා ගාස්තුව යනු වේදිකාව ' +
    'රියදුරෙකුගෙන් අය කරන එකම මුදල වන අතර, එය ඔබ උපයන කිසිවකින් කොටසක් නොව දිනකට ' +
    'වන ගාස්තුවකි.',
  'www.guide.d13.callout.oncePerDay':
    'දිනකට එක් ස්ථාවර අයකිරීමක්, ඔබේ දෙවන ගමනට පෙර අඩු කෙරේ, වාහනය කුමක් වුවත් දිනය ' +
    'කොපමණ දිගු වුවත්. ඉන් පසුව අසීමිත ගමන්. ඔබ සබැඳි නොවන දිනවල කිසිවක් නැත, දිනේ ' +
    'පළමු ගමනට කිසිදා කිසිවක්ද නැත.',
  'www.guide.d13.callout.ratesAreConfigurable':
    'මෙම අනුපාත සකසන්නේ MageRide වන අතර ඒවා වෙනස් විය හැක. මෙහි ඇති වගුව අද ' +
    'පිරිවිතරය දක්වන දෙයයි; ඇත්තටම ඔබෙන් අය කරන අනුපාතය, ඔබට සජීවීව ඇති වාහනය සඳහා ' +
    'ඔබේ පසුම්බි තිරයේ පෙන්වන එකයි.',

  // Chapter 14 · Getting paid
  'www.guide.d14.title': 'ගෙවීම් ලැබීම',
  'www.guide.d14.summary':
    'ගමනක මුදල් ඇත්තටම යන්නේ කොහේද — ඔබේ අතට, ඔබේම බැංකු ගිණුමට, නැතහොත් ඔබේ ' +
    'MageRide පසුම්බියට — සහ ඉන් පසුව එයට සිදු වන්නේ කුමක්ද යන්නය.',
  'www.guide.d14.step1':
    'මගියෙක් වෙන් කරන විට ගෙවන ආකාරය තෝරන අතර අවසානයේ එය වෙනස් කළ හැක. ඔබට වෙනස් ' +
    'වන්නේ මුදල නොව, මුදල් ගොඩ බසින තැනයි.',
  'www.guide.d14.step2':
    'මුදල් සරලම සහ බහුලම එකයි. මගියා ඔබට ගාස්තුව භාර දෙයි, ඔබ අවසන් තට්ටු කරයි, එයින් ' +
    'අවසන්. එම මුදල වහාම ඔබේය — එය කිසිදා MageRide අසලටවත් යන්නේ නැත, එබැවින් බලා ' +
    'සිටීමට කිසිවක් නැත, ගෙවා ගැනීමට කිසිවක්ද නැත.',
  'www.guide.d14.step2.note':
    'MageRide එයින් කොමිසයක් ගන්නේ නැත, එය කිසිදා දකින්නේද නැත. වේදිකාව ඔබෙන් අය ' +
    'කරන එකම දෙය ඔබේ පසුම්බියෙන් යන දෛනික ගාස්තුවයි.',
  'www.guide.d14.step3':
    'දෙවන ක්‍රමය ඔබේම QR කේතයයි — MageRide හි ලියාපදිංචි කර මගියාට පරිලෝකනය කිරීමට ' +
    'පෙන්වන, ඔබේම බැංකුවේ කේතයයි. එම ගෙවීම බැංකුවෙන් බැංකුවට, කෙළින්ම ඔබේ ගිණුමට ' +
    'යන අතර, MageRide එයද කිසිදා හසුරුවන්නේ නැත.',
  'www.guide.d14.step4':
    'එය බැංකුවෙන් බැංකුවට යන නිසා, එය ළඟා වූ බව MageRide හට කිසිදා කියන්නේ නැත. ' +
    'එබැවින් ගමන වැසෙන්නේ ඔබ දෙදෙනාම ඒ බව කීමෙනි: මගියා "මම ගෙවුවා" තට්ටු කරයි, ඔබට ' +
    '"QR ගෙවීම ලැබුණාද?" යන ඉල්ලීම ලැබේ, ඔබ තහවුරු කරන්න තට්ටු කරයි.',
  'www.guide.d14.step4.note':
    'මගියා දැනටමත් ගොස් ඇත්නම් ඔබට තනිවම තහවුරු කළ හැක. ඔවුන් ගෙවූ බව කියා ඔබ තහවුරු ' +
    'නොකරන්නේ නම්, යෙදුම ඔබට මතක් කරන අතර, නොවිසඳුණු අවස්ථාවක් සහායට, ඉන්පසු MageRide ' +
    'හි මූල්‍ය කණ්ඩායමට යයි. එය සිදු වන අතරතුර කිසිදු මුදලක් කිසිදු දිශාවකට ගමන් ' +
    'නොකරයි, මන්ද MageRide කිසිවක් රඳවාගෙන නැති නිසාය.',
  'www.guide.d14.step5':
    'ඔබේ ඉපැයීම සටහන් වන්නේ ඔබ අවසන් තට්ටු කරන මොහොතේ නොව ගෙවීම සමථයට පත් වූ පසුය. ' +
    'අවසන් වූ ගමනක් තම මුදල ටිකක් පසුව පෙන්විය හැක්කේ ඒ නිසාය.',
  'www.guide.d14.step6':
    'තෙවන ක්‍රමය නම් මගියෙක් තමන්ගේම MageRide ශේෂයෙන් ගෙවීමයි. එය MageRide හරහා ' +
    'යයි: එය ඔවුන්ගේ ශේෂයෙන් ඔබේ පසුම්බියට වහාම ගමන් කරන අතර, ඔබේ බැංකුවට ගෙවා ' +
    'දෙන තුරු ඔබේ පසුම්බියේ රැඳී සිටී.',
  'www.guide.d14.step7':
    'එය ගෙවා ගැනීමට ඔබේ බැංකු විස්තර ගොනුවේ තිබිය යුතුය — බැංකුව, ශාඛාව, ගිණුම් අංකය ' +
    'සහ ගිණුමේ නම, බැංකු ප්‍රකාශයක් හෝ ඔබේ බැංකු පොතේ පළමු පිටුව සමඟ, සහ ඔබේ බැංකු ' +
    'යෙදුමේ QR රූපය. MageRide හි කවුරුන් හෝ එය පරීක්ෂා කරන අතර, ඕනෑම සංස්කරණයක් එය ' +
    'නැවත පරීක්ෂා කිරීමට යවයි.',
  'www.guide.d14.step7.note':
    'ඔබේ QR කේතය එන්නේ එම තිරයෙන්ම බැවින්, එය පිරවීමද ගෙවීම් ලැබීමේ දෙවන ක්‍රමය ' +
    'කළ හැකි කරන දෙයයි. එය අනුමත වන තුරු ඔබේ ශේෂය හුදෙක් එකතු වේ — එය ඔබ වෙනුවෙන් ' +
    'තබා ගන්නා අතර කිසිදා නැති නොවේ — නමුත් කිසිවක් පිටතට යන්නේ නැත.',
  'www.guide.d14.step8':
    'ඉපැයීම් අද, මේ සතිය සහ මේ මාසය පෙන්වයි: ඔබ ගාස්තුවලින් ලබාගත් දේ, දෛනික ගාස්තුව ' +
    'ඔබට වැය කළ දේ, සහ වෙනස. ඊට යටින් සෑම ගමනක්ම එහිම ගාස්තුව සමඟ ලැයිස්තුගත වේ.',
  'www.guide.d14.callout.cashIsYours':
    'මුදල් ගාස්තු සහ ඔබේම QR කේතයට ගෙවූ ගාස්තු කිසිදා MageRide හරහා යන්නේ නැත. ' +
    'ආපසු ගැනීමට කිසිවක් නැත, බලා සිටීමට කිසිවක් නැත, ඒ දෙකෙන් එකකින්වත් ගන්නා ' +
    'කොමිසයක්ද නැත — ගමන අවසන් වන මොහොතේම මුදල ඔබ සතුය.',
  'www.guide.d14.callout.qrIsAttested':
    'ඔබේම බැංකුවට එන QR ගෙවීමක් MageRide හට කිසිදු පණිවිඩයක් ඇති නොකරයි, ඔබෙන් එය ' +
    'තහවුරු කරන ලෙස අසන්නේ ඒ නිසාය. ඔබට ඇත්තටම ලැබුණු දේ පමණක් තහවුරු කරන්න: ගමන ' +
    'වසන්නේ ඔබේ තහවුරු කිරීමයි, ආරවුලක් වූ එකක් සමථයට පත් වන්නේ කිසිවෙකු රඳවා නොගත් ' +
    'ගෙවීමක් පද්ධතියක් ආපසු හරවා නොව, මිනිසුන් සාක්ෂි දෙස බැලීමෙනි.',
  'www.guide.d14.callout.payoutsCoverTheWallet':
    'ගෙවීමක් ආවරණය කරන්නේ ඔබේ MageRide පසුම්බියේ ඇති දේ පමණි — ඔබට දැනටමත් ළඟා වී ' +
    'ඇති ඔබේ මුදල් ගාස්තු කිසිදා නොවේ, ඔබේ QR ගාස්තුද කිසිදා නොවේ. එම ගෙවීම් ධාවනය ' +
    'සතිපතා පිටතට යාමටත් අවම මුදලක් නොමැතිව මුළු ශේෂයම ගෙවීමටත් නිර්මාණය කර ඇති ' +
    'අතර, ඊට පෙර ඔබේ බැංකු විස්තර අනුමත විය යුතුය. ගෙවීම් ආරම්භ වන විට MageRide ඒ ' +
    'බව තහවුරු කරයි; එතෙක්, එදිනම ඔබට විශ්වාස කළ හැකි මුදල ලෙස මුදල් සහ ඔබේම QR ' +
    'සලකන්න.',

  // Chapter 15 · Bulk credit and transfers
  'www.guide.d15.title': 'තොග ණය, සහ එය අන් අයට දීම',
  'www.guide.d15.summary':
    'ණය තොග වශයෙන් මිලදී ගැනීම එහි මුහුණත වටිනාකමට වඩා අඩුවෙන් වැය වන අතර, ඕනෑම ' +
    'රියදුරෙකුට ඕනෑම රියදුරෙකුට ණය යැවිය හැක. නැවත විකුණන ගිණුමක් නැත, කේතයක් නැත, ' +
    'කොමිසයක්ද නැත.',
  'www.guide.d15.step1':
    'පසුම්බි ණය ඇති ඕනෑම රියදුරෙකුට එය වෙනත් ඕනෑම රියදුරෙකුට මාරු කළ හැක. MageRide ' +
    'හි "නැවත විකුණන්නෙක්" යනු එපමණයි — ණය ලාභයට මිලදී ගත් රියදුරෙකි. විවෘත කිරීමට ' +
    'වෙනම ගිණුමක් නැත, ලබා ගැනීමට අවසරයක්ද නැත.',
  'www.guide.d15.step2':
    'තොග ණය වවුචර් ණය පිරවීමේ තිරයේ ඇත, රු. 1,000 සිට රු. 10,000 දක්වා ස්ථිර ' +
    'ප්‍රමාණ පහකින්. වවුචරය වටින මුදලට වඩා අඩුවෙන් ඔබ ගෙවන අතර, ඔබේ පසුම්බියට ' +
    'මුළු මුහුණත වටිනාකමම වහාම බැර වේ.',
  'www.guide.d15.step2.note':
    'වට්ටම සකසන්නේ MageRide වන අතර එය ප්‍රමාණය අනුව වෙනස් වේ, විශාල වවුචර් ' +
    'සාමාන්‍යයෙන් වැඩි වට්ටමක් ලබා දේ. වත්මන් අනුපාත ඇත්තේ ටයිල් මතය — මෙම පිටුව ' +
    'ඒවා සඳහන් නොකරන්නේ ඕනෑම වේලාවක ඒවා වෙනස් කළ හැකි නිසාය.',
  'www.guide.d15.step3':
    'මිදවා ගැනීමට කේතයක් නැත. ඔබ ගෙවන මොහොතේ සිටම ණය ඔබේම පසුම්බියේ ඇති අතර, එය ' +
    'ඔබේම දෛනික ගාස්තු සඳහා භාවිත කළ හැක, නැතහොත් අන් අයට දිය හැක.',
  'www.guide.d15.step4':
    'තවත් රියදුරෙකුගෙන් ණය ලබා ගැනීමට, ණය ඉල්ලන්න විවෘත කර ඔවුන්ගේ රියදුරු හැඳුනුම ' +
    'සහ මුදල ඇතුළත් කරන්න. ඔවුන්ට දැනුම්දීමක් ලැබෙන අතර එය අනුමත කරයි හෝ ප්‍රතික්ෂේප ' +
    'කරයි.',
  'www.guide.d15.step4.note':
    'රියදුරු හැඳුනුමක්, ටයිප් කරන ලද — කිසිවක් පරිලෝකනය නොකෙරේ. QR පරිලෝකනය මෙම ' +
    'තිරයෙන් ඉවත් කරන ලද අතර, ඇතුළත් කිරීමට විශේෂ නැවත විකුණන කේත නැත.',
  'www.guide.d15.step5':
    'අනෙක් පැත්තෙන්, එන ඉල්ලීම් දැනුම්දීම් ලෙස පැමිණෙන අතර, ඉල්ලන රියදුරාගේ නම, ' +
    'වාහනය සහ මුදල සමඟ ණය මාරු කිරීමේ තිරයේ දිස් වේ. එක් එක එක අනුමත කරන්න හෝ ' +
    'ප්‍රතික්ෂේප කරන්න.',
  'www.guide.d15.step6':
    'ඇසීමකින් තොරවද ණය යැවිය හැක — එම තිරයේම රියදුරු හැඳුනුමක් සහ මුදලක් ඇතුළත් කර ' +
    'එය යවන්න.',
  'www.guide.d15.step7':
    'එය කුමන දිශාවට ගියත්, නිශ්චිත මුදල එක් පසුම්බියකින් පිටව අනෙකට එයි. මාරු ' +
    'කිරීමකින් MageRide කොමිසයක් ගන්නේ නැත, කිසිදු ආකාරයක කැපීමක්ද නැත. පැති දෙකම ' +
    'සටහන් වන අතර, රියදුරන් දෙදෙනාටම තම ඉතිහාසයේ මාරු කිරීම දැකිය හැක.',
  'www.guide.d15.step8':
    'ඔබේ ශේෂයට එය දරාගත නොහැකි නම් මාරු කිරීමක් අවහිර වේ. තව ද සිදු වූ මාරු කිරීමක් ' +
    'ආපසු ගත නොහැක — ඔබ යවන්නේ කාටදැයි මුලින්ම පරීක්ෂා කරන්න.',
  'www.guide.d15.callout.noCommission':
    'රියදුරෙකුගෙන් රියදුරෙකුට යන මාරු කිරීමකින් කිසිවක් අඩු නොකෙරේ. රු. 1,000ක් ' +
    'යවන්න, රු. 1,000ක් ළඟා වේ. ණය නැවත විකුණන රියදුරෙකු තම ලාභය ලබන්නේ ඔබෙන් ගන්නා ' +
    'අයකිරීමකින් නොව, ඔවුන් මිලදී ගන්නා විට ලැබුණු වට්ටමෙනි.',
  'www.guide.d15.callout.noResellerAccount':
    'MageRide හි නැවත විකුණන ගිණුමක් නැත, නැවත විකුණන පිවිසුමක් නැත, නැවත විකුණන ' +
    'කේතයක්ද නැත. එවැන්නක් ඔබට විකිණීමට ඉදිරිපත් වන ඕනෑම කෙනෙක් ඔබට විකුණන්නේ ' +
    'නොපවතින දෙයකි.',
  'www.guide.d15.callout.checkTheDriverId':
    'ඉල්ලීමක් අනුමත කිරීමට හෝ ණය යැවීමට පෙර රියදුරු හැඳුනුම රියදුරාගෙන්ම තහවුරු ' +
    'කරගන්න. ණය වහාම සහ සම්පූර්ණයෙන් ගමන් කරන අතර, යෙදුමෙන් එය ආපසු හැරවීමට ක්‍රමයක් ' +
    'නැත.',

  // Chapter 16 · Mode A and Mode B driving
  'www.guide.d16.title': 'බසයක් හෝ පෞද්ගලික වාහනයක් පැදවීම',
  'www.guide.d16.summary':
    'කුලී වෙනුවට ගමන්, දෛනික ගාස්තුවක් නැත, සහ GPS උපකරණයක් සවි කළ පසු වෙනස් වන දේ ' +
    '— ඔබට තවදුරටත් ඔබම කිරීමට අවශ්‍ය නොවන කොටස් ඇතුළුව.',
  'www.guide.d16.step1':
    'මහජන බසයකට හෝ පෞද්ගලික වාහනයකට ගමන් ඉල්ලීම් කිසිසේත් නොලැබේ. ඔබේ තිරය යනු ගමන ' +
    'ආරම්භ කරන්න සහ ගමන අවසන් කරන්න, මාර්ගය, ඔබේ ධාවනය වන කාලය සහ දුර, සහ මාර්ග ' +
    'කාඩ්පතට පහළින් වාහන වර්ගය සහ අංකය සමඟ.',
  'www.guide.d16.step1.note':
    'දුම්රියද මහජන ප්‍රවාහනයයි, නමුත් දුම්රියක් ලියාපදිංචි කරන්නේ MageRide පමණි. ' +
    'රියදුරු යෙදුමේ කිසිම තැනක දුම්රිය විකල්පයක් නැත, එය නැති වීමක් නොව හිතාමතාය.',
  'www.guide.d16.step2':
    'ඔබ පිටත් වන විට ගමන ආරම්භ කරන්න, අවසන් කරන විට ගමන අවසන් කරන්න. දෛනික චර්යාව ' +
    'මුළුමනින්ම එපමණයි — සමථයට පත් කිරීමට ගාස්තුවක් නැත, ඇතුළත් කිරීමට කේතයක්ද නැත.',
  'www.guide.d16.step3':
    'එකක් අවසන් කිරීමට අමතක වුවහොත්, චලනයකින් තොරව මිනිත්තු තිහකට පසු එය ඉබේම අවසන් ' +
    'වී ඒ ඇයිදැයි ඔබට කියයි. එය වැරදි නම් නැවත ආරම්භ කිරීමට ඔබට මිනිත්තු පහක් ඇත. ' +
    'ඔබේ අවසන් ගමන නිම වූ තැනට ආපසු පැමිණි විට ස්වයංක්‍රීයව අවසන් වීමද සක්‍රීය කළ ' +
    'හැක.',
  'www.guide.d16.step3.note':
    'මේ කිසිවකට කිසිවක් වැය නොවේ. මහජන බසයක් දෛනික වේදිකා ගාස්තුවක් ගෙවන්නේ නැත, ' +
    'පෞද්ගලික වාහනයකට එය පදවන අයට දෛනිකව නොව එය අයිති අයට මාසිකව බිල් කෙරේ.',
  'www.guide.d16.step4':
    'GPS උපකරණයක් වාහනයේ පිහිටීම වාර්තා කරන්නේ කවුරුන්ද යන්න වෙනස් කරයි. GPS ට්‍රැකරය ' +
    'යුගල කරන්න යටතේ එකක් යුගල කරන්න: වාහනය තෝරන්න, ඉන්පසු එහි IMEI අංකය ටයිප් ' +
    'කරන්න, උපකරණයේ ඇති කේතය පරිලෝකනය කරන්න, නැතහොත් MageRide ඔබට දුන් බන්ධන කේතයක් ' +
    'ඇතුළත් කරන්න.',
  'www.guide.d16.step5':
    'එතැන් සිට එම වාහනයේ පිහිටීම ප්‍රකාශ කරන එකම දෙය උපකරණයයි — ඔබේ දුරකථනය එය ' +
    'යැවීම නවත්වයි. බසයක හෝ පෞද්ගලික වාහනයක, ඉන්පසු ගමන ආරම්භ වන්නේ එන්ජිම ක්‍රියාත්මක ' +
    'කළ විටය, අවසන් වන්නේ එය නිවා දැමූ විටය, යෙදුමක් කිසිසේත් සම්බන්ධ නොවේ.',
  'www.guide.d16.step6':
    'එබැවින් ඔබ යෙදුම විවෘත කර ගමන දැනටමත් ධාවනය වන බව දැකිය හැක. එය වැරැද්දක් නොව ' +
    'උපකරණය වාර්තා කිරීමයි — තව ද ඔබට තවමත් එය අභිබවා යා හැක. ඔබේ උපකරණ පුවරුවේ ගමන ' +
    'ආරම්භ කරන්න සහ ගමන අවසන් කරන්න, උපකරණය කරන දේ කුමක් වුවත් ක්‍රියා කරයි.',
  'www.guide.d16.step6.note':
    'පොරොත්තු වාහනයක එම උපකරණයම වෙනස් ලෙස හැසිරේ: එහි පිහිටීම භාවිත වන්නේ ඔබ සබැඳිව ' +
    'සිටින අතරතුර පමණි. වාහන දෙකක්, උපකරණයක්, හැසිරීම් දෙකක් — ඔබ දෙකම පදවන්නේ නම් ' +
    'දැනගැනීම වටී.',
  'www.guide.d16.step7':
    'පෞද්ගලික වාහනයක් අනුගමනය කළ හැක්කේ කාටදැයි බෙදාගැනීම පාලනය කරන අතර, ඔබ සතු ' +
    'එක් එක් වාහනයට එය වෙන වෙනම තබා ගැනේ. ඉහළින් වාහනය තෝරන්න, ඉන්පසු කල් ඉකුත්වන ' +
    'දිනයක් සමඟ පරිශීලක හැඳුනුමකට ප්‍රවේශය දෙන්න, නැතහොත් ඊට යටින් රැඳී ඇති ඉල්ලීම් ' +
    'පිළිගන්න.',
  'www.guide.d16.step8':
    'ඔබට හිමිකම් නොමැතිව වාහන සමූහයකට ඔබට වාහනයක් පැවරිය හැක. එය මගේ වාහන තුළ ' +
    '"තාවකාලිකව පවරන ලද" යටතේ දිස් වේ, ඔබ එය තෝරා පදවයි, පැවරුම අවසන් වූ විට එය ' +
    'ඉබේම අතුරුදහන් වේ.',
  'www.guide.d16.step8.note':
    'වාහන සමූහයක් පැවරුමක් ආපසු ගත්තොත්, වාහනය ඔබේ ලැයිස්තුවෙන් ඉවත් වේ. ඒ ගැන ' +
    'ඇසිය යුත්තේ MageRide සහායෙන් නොව එය පැවරූ වාහන සමූහයෙන්ය.',
  'www.guide.d16.callout.ignitionStartsIt':
    'බසයකට හෝ පෞද්ගලික වාහනයකට GPS උපකරණයක් සවි කර ඇති විට ඔබට ගමන ආරම්භ කිරීමට, එය ' +
    'අවසන් කිරීමට, හෝ යෙදුම විවෘතව තබා ගැනීමට අවශ්‍ය නැත — ඒ තුනම ඉග්නිෂන් කරයි. ඔබ ' +
    'රඳවා ගන්නා දෙය පාලනයයි: ඔබේ උපකරණ පුවරුවේ බොත්තම් දෙපැත්තටම උපකරණය අභිබවා යයි.',
  'www.guide.d16.callout.youSeeTheirNumber':
    'මගියෙක් ඔබේ පෞද්ගලික වාහනය අනුගමනය කිරීමට ඉල්ලන විට ඔබට ඔවුන්ගේ නම සහ ජංගම ' +
    'දුරකථන අංකය පෙන්වයි, එවිට ඔබ ඇතුළට දෙන්නේ කාටදැයි ඔබ දනී — ක්‍රියා කරන්නේ එලෙස ' +
    'බව ඔවුන්ටද කියනු ලැබේ. දැනට ප්‍රවේශය ඇති අයගේ ඔබේ ලැයිස්තුවේද එම විස්තර පවතී.',
  'www.guide.d16.callout.noDailyFeeHere':
    'මහජන බසයක දෛනික වේදිකා ගාස්තුවක් නැත, එකක් පැදවීමට පෙර පවත්වා ගත යුතු පසුම්බි ' +
    'ශේෂයක්ද නැත. දෛනික ගාස්තුව අයිති පොරොත්තු ඉල්ලුම මත රිය පැදවීමට මිස වෙන ' +
    'කිසිවකට නොවේ.',

  // Chapter 17 · Ratings and driver level
  'www.guide.d17.title': 'ශ්‍රේණිගත කිරීම් සහ ඔබේ රියදුරු මට්ටම',
  'www.guide.d17.summary':
    'තරු ශ්‍රේණිගත කිරීම් ලකුණු බවට පත් වන ආකාරය, මට්ටමක් වටින්නේ කුමක්ද, සහ මට්ටමක් ' +
    'ඉවත් කරන දේවල් දෙක — දෙකක් පමණි.',
  'www.guide.d17.step1':
    'සම්පූර්ණ කළ ගමනකට පසු මගියෙකුට ඔබට එකේ සිට පහ දක්වා තරු දී අදහසක් තැබිය හැක. ' +
    'ඔබේ සමස්ත ශ්‍රේණිගත කිරීම සහ සෑම ගමනකම ශ්‍රේණිගත කිරීම ඔබේ පැතිකඩේ ඇත.',
  'www.guide.d17.step2':
    'ඔබටද මගියා ශ්‍රේණිගත කළ හැක. එය බැසීමේ ස්ථානයේදී නොව ඔබේ ගමන් ඉතිහාසයේ එම ගමනේ ' +
    'පේළියෙන් විවෘත වේ — එකේ සිට පහ දක්වා තරු, අවශ්‍ය නම් අදහසක් සමඟ.',
  'www.guide.d17.step2.note':
    'ගමන අවසන් වන මොහොතේ එය ඉදිරිපත් නොවේ, එබැවින් ඔබ එය එහි සොයා නොදුටුවේ නම්, එය ' +
    'නැති වී නොව ඔබේ ඉතිහාසයේ ඇත.',
  'www.guide.d17.step3':
    'සෑම රියදුරෙක්ම ආරම්භ වන්නේ මට්ටම 3 සිටය. හොඳ ශ්‍රේණිගත කිරීම් ලකුණු බවට පත් වේ: ' +
    'තරු පහක් පහක් වටී, තරු හතරක් හතරක් වටී, තරු දෙකක් සහ ඊට අඩු ඒවා කිසිවක් වටින්නේ ' +
    'නැත, ලකුණු පන්සියයක් යනු එක් මට්ටමකි.',
  'www.guide.d17.step4':
    'මට්ටම් තිරය ඔබ සිටින තැන පෙන්වයි — ඔබේ ලාංඡනය, ඊළඟ මට්ටමට ඇති ලකුණු තීරුව, ඔබේ ' +
    'පිළිගැනීමේ අනුපාතය සහ ඔබේ නොපැමිණීම්.',
  'www.guide.d17.step5':
    'කුලියක් ඉදිරිපත් කරන්නේ කාටදැයි තීරණය කරන දේවලින් එකක් ඔබේ මට්ටමයි, බාරගැනීමේ ' +
    'ස්ථානයට ඔබ කොපමණ ළඟද යන්නත් මගියා ඉල්ලූ දේ ඔබ පදවනවාද යන්නත් සමඟ. ඒ එක් එකට ' +
    'කොපමණ බරක් ලැබේද යන්න සකසන්නේ MageRide වන අතර එය ප්‍රකාශයට පත් කර නැත, එබැවින් ' +
    'ගමන් අනුව මට්ටමක් වටින්නේ කුමක්දැයි කිසිවෙකුට ඔබට කිව නොහැක.',
  'www.guide.d17.step6':
    'මට්ටමක් ඉවත් කරන දේවල් දෙකක් ඇත, ඇත්තේ දෙකක් පමණි: ඔබ පිළිගත් නියමිත ගමනකට ' +
    'නොපැමිණීම, සහ මගී පැමිණිලි තුනක් එකතු කර ගැනීම — ඒ දෙවැන්න එය සොයා බලන අතරතුර ' +
    'ඔබව තාවකාලිකව ලැයිස්තුවෙන් ඉවත්ද කරයි.',
  'www.guide.d17.step6.note':
    'සාමාන්‍ය ඉල්ලීමක් යාමට ඉඩ දීම ඉන් එකක් නොවේ. එකක් ප්‍රතික්ෂේප කිරීමද එසේමය. එය ' +
    '8 වන පරිච්ඡේදය වන අතර, එය මෙහිදී වෙනස් වී නැත.',
  'www.guide.d17.step7':
    'මට්ටම 1 හිදී, ඔබ නැවත ඉහළට එනතුරු රැකියා පුවරුව සහ නියමිත ගමන් ඔබට අහිමි වේ. ' +
    'තවමත් සබැඳි වී එන එන කුලී ගත හැක — එය තහනමක් නොව කලින් වෙන් කිරීම මත සීමාවකි.',
  'www.guide.d17.step8':
    'කැමැත්ත දැන්වූ රියදුරන් දෙදෙනෙක් සමානව ළං වන නියමිත ගමනකදී, ඉහළ මට්ටමට එය ' +
    'මුලින්ම ඉදිරිපත් වේ. සම තත්ත්වයක් බිඳීමට මට්ටමක් යොදන බව නිශ්චිතව කියා ඇති එකම ' +
    'තැන එයයි.',
  'www.guide.d17.callout.whatLevelChanges':
    'කුලියක් ඉදිරිපත් වන්නේ කාටද යන්නට ඇති ආදාන තුනෙන් එකක් ඔබේ මට්ටමයි — දුර, මට්ටම ' +
    'සහ වාහන වර්ගය — නියමිත රැකියාවකට රියදුරන් දෙදෙනෙක් සමානව ළං වූ විට මුලින්ම ' +
    'ඇමතෙන්නේ කාටදැයි තීරණය කරන්නේද එයයි. එය කොපමණ වටීදැයි ප්‍රකාශිත කිසිවක් නොකියන ' +
    'අතර, මෙම මාර්ගෝපදේශය අනුමාන කරන්නේද නැත.',
  'www.guide.d17.callout.twoWaysToDrop':
    'මට්ටමක් පහත හෙළන්නේ දේවල් දෙකක් පමණි: ඔබ පිළිගත් නියමිත ගමනකට නොපැමිණීම, සහ ' +
    'මගී පැමිණිලි තුනක්. ඔබේ රිය පැදවීම ගැන අනෙක් සියල්ල බලපාන්නේ ඔබේ ශ්‍රේණිගත ' +
    'කිරීමට වන අතර, ඔබව නැවත ඉහළට ගෙන එන්නේ ඔබේ ශ්‍රේණිගත කිරීමයි.',
  'www.guide.d17.callout.acceptanceRate':
    'ඔබේ පිළිගැනීමේ අනුපාතය ඔබේ මට්ටම් තිරයේ ඔබට පෙන්වයි. ප්‍රකාශිත කිසිවක් ඊට ' +
    'කිසිදු ප්‍රතිවිපාකයක් අමුණන්නේ නැත — දඬුවමක් නැත, සීමාවක් නැත, සිසිල් වීමේ ' +
    'කාලයක්ද නැත — එබැවින් එය ඔබ බැඳී සිටින ඉලක්කයක් නොව ඔබේම රිය පැදවීම ගැන ' +
    'තොරතුරකි.',

  // Chapter 18 · Safety, support and updates
  'www.guide.d18.title': 'ආරක්ෂාව, උදව් සහ යාවත්කාලීන',
  'www.guide.d18.summary':
    'ඔබට අවශ්‍ය වීමට පෙර සැකසිය යුතු හදිසි සම්බන්ධතාව, SOS ඇත්තටම කරන දේ, ටිකට්පතක් ' +
    'යොදන ආකාරය, සහ යෙදුම සමහර විට යාවත්කාලීන කිරීම මත බලකරන්නේ ඇයි යන්නය.',
  'www.guide.d18.step1':
    'ඔබේ පැතිකඩේ හදිසි සම්බන්ධතාවක් එක් කරන්න — නමක් සහ දුරකථන අංකයක්, ඔබේ ' +
    'සම්බන්ධතාවලින් තෝරාගත් හෝ ටයිප් කළ. ඔබ කැමති විටෙක එය වෙනස් කළ හෝ ඉවත් කළ හැක.',
  'www.guide.d18.step1.note':
    'පසුව නොව දැන් එය කරන්න. SOS තම අනතුරු ඇඟවීම යවන්නේ එම සම්බන්ධතාවට වන අතර, ' +
    'සම්බන්ධතාවක් සුරැකි නැත්නම් එය ප්‍රතික්ෂේප කරයි — ඔබ එය එබූ මොහොතේ එය සොයා ' +
    'ගැනීමට ඔබට අවශ්‍ය නැත.',
  'www.guide.d18.step2':
    'ගමනක් ධාවනය වන අතරතුර SOS ගමන් තිරයේ ඇත. එය තට්ටු කිරීමෙන් ආපසු ගණන් කිරීමක් ' +
    'සහිත තහවුරු කිරීමක් විවෘත වන අතර, එමගින් අහම්බෙන් වූ එබීමක් අවලංගු කළ හැක.',
  'www.guide.d18.step3':
    'තහවුරු කළ විට, ඔබේ ස්ථානය සහ ගමන් විස්තර සහිත කෙටි පණිවිඩයක් තත්පර කිහිපයක් ' +
    'ඇතුළත ඔබේ හදිසි සම්බන්ධතාවට යන අතර, ඒ සමඟම MageRide හි ආරක්ෂක කණ්ඩායමටද අනතුරු ' +
    'අඟවනු ලැබේ.',
  'www.guide.d18.step4':
    'උදව් සහ සහාය තුළ මුලින්ම පොදු ප්‍රශ්නවලට පිළිතුරු ඇත — පසුම්බි ණය පිරවීම්, ' +
    'දෛනික ගාස්තුව, වාහනයක් ලියාපදිංචි කිරීම — ඒවාට පහළින් ඔබේ විවෘත ටිකට්පත් ඇත.',
  'www.guide.d18.step5':
    'ටිකට්පතක් යොදන්න කෙටි පෝරමයක් විවෘත කරයි: ගැටලුව විස්තර කරන්න, තිර රුවක් අමුණන්න, ' +
    'සහ එය සම්බන්ධ ගමන ඔබේ පසුගිය ගමන් ලැයිස්තුවෙන් තෝරන්න. ඉන්පසු ඔබට ටිකට්පත ' +
    'අනුගමනය කර එහි පිළිතුර දැකිය හැක.',
  'www.guide.d18.step5.note':
    'වැරදීමකින් අය කළ දෛනික ගාස්තුවක් සඳහා එම තිරයේම ඉක්මන් ක්‍රියාවක් ඇත — උදාහරණයක් ' +
    'ලෙස ඔබ සබැඳි වන විට යෙදුම කඩා වැටුණේ නම්. එය ආපසු ගෙවීමේ ඉල්ලීමක් වන අතර, එකක් ' +
    'ඉදිරිපත් කිරීමට නිවැරදි ක්‍රමයද එයයි.',
  'www.guide.d18.step6':
    'මගීන්ට ඔවුන්ගේම ආරක්ෂක මෙවලම් ඇති අතර ඒවා ඔබට බලපායි: මගියෙකුට වාහනයක් ගැන ' +
    'පැමිණිලි කළ හැකි අතර, පැමිණිලි තුනක් රියදුරෙකු සමාලෝචනයට සහ තාවකාලික ලැයිස්තුවෙන් ' +
    'ඉවත් කිරීමකට සලකුණු කරයි. මගියෙකුට රියදුරෙකු අවහිර කළද හැක, ඉන් පසුව ඔවුන් ' +
    'කිසිදා ඔවුන් සමඟ ගැළපෙන්නේ නැත.',
  'www.guide.d18.step7':
    'ගමන මැදදී සංඥාව නැති වුවහොත්, දිගටම රිය පදවන්න. ඔබේ පිහිටීම් දුරකථනයේ ගබඩා වන ' +
    'අතර සංඥාව නැවත එන විට පිළිවෙළට යවනු ලැබේ, එබැවින් මාර්ගය නිවැරදිව පවතී — ශීත ' +
    'වී ඇතැයි පෙනෙන ගමනක් සාමාන්‍යයෙන් වන්නේ ගමන අසාර්ථක වීම නොව සිතියම බලා සිටීමයි.',
  'www.guide.d18.step8':
    'MageRide වැදගත් යාවත්කාලීනයක් නිකුත් කරන විට, යෙදුමත් වේදිකාවත් තවමත් එකිනෙකා ' +
    'සමඟ එකඟ වන පරිදි, යෙදුම එය මත බලකර ඔබ එය ලබා ගන්නා තුරු ක්‍රියා කිරීම නවත්වයි. ' +
    'සාමාන්‍ය යාවත්කාලීනයක් යනු ඔබට ඉවත් කළ හැකි බැනරයක් පමණි.',
  'www.guide.d18.step8.note':
    'දෙකම ඔබව ඔබේ දුරකථනයේ යෙදුම් වෙළඳසැලට යවයි. රියදුරු යෙදුම තවම එහි නිකුත් කර ' +
    'නැත — නිකුත් වූ විට, මෙහි බාගැනීමේ පිටුව ඒ බව කියයි.',
  'www.guide.d18.callout.setTheContactFirst':
    'ඔබේ පැතිකඩේ හදිසි සම්බන්ධතාවක් සුරැකි නොමැතිව SOS යවන්නේ නැත. එය එක් කිරීමට ' +
    'මිනිත්තුවක් ගත වන අතර, ක්‍රියා කරන ආරක්ෂක බොත්තමක් සහ නරකම මොහොතේ ප්‍රතික්ෂේප ' +
    'කරන එකක් අතර වෙනස එයයි.',
  'www.guide.d18.callout.everySosIsLogged':
    'සෑම SOS එකක්ම එහි වේලාව, එහි පිහිටීම සහ එය සිදු වූ ගමන සමඟ සටහන් වන අතර, ' +
    'MageRide හට සමාලෝචනය කිරීමටත් අවශ්‍ය නම් බලධාරීන්ට භාර දීමටත් තබා ගැනේ. එය ඔබේම ' +
    'සම්බන්ධතාවටත් MageRide හටත් අනතුරු අඟවයි — එය පොලිසියට ඇමතුමක් නොවේ, එකක් ' +
    'වෙනුවට ආදේශකයක්ද නොවේ.',
  'www.guide.d18.callout.feeChargedInError':
    'දෛනික ගාස්තුවක් නොගත යුතු අවස්ථාවක ගෙන ඇත්නම්, එය දරාගැනීම වෙනුවට සහායෙන් එය ' +
    'ආපසු ඉල්ලන්න. හරියටම ඒ සඳහා නිශ්චිත ඉල්ලීමක් ඇති අතර, පිරිවිතරය දෙන උදාහරණය ' +
    'නම් ඔබ සබැඳි වන විට යෙදුම කඩා වැටීමයි.',

  // The fleet-owner guide (S23). First-pass Sinhala, not reviewed by a native
  // speaker — the same caveat S12 recorded for the other 34 chapters, and the
  // reason it is recorded rather than assumed. Terminology follows what is already
  // in this table: රථ සමූහය (fleet), සංවිධානය (organisation), පසුම්බිය (wallet),
  // A/B/C ක්‍රමය (the three modes), ට්‍රැකර් (tracker).

  'www.guide.f01.title': 'ඔබේ සංවිධානය ලියාපදිංචි කිරීම',
  'www.guide.f01.summary':
    'රථ සමූහ ද්වාරයට ලියාපදිංචි වීම, ඔබේ සංවිධානයේ KYC ඉදිරිපත් කිරීම, සහ මෙම මාර්ගෝපදේශයේ ' +
    'අනෙක් සියල්ල ක්‍රියාත්මක වීමට පෙර බලා සිටිය යුතු අනුමැතිය.',
  'www.guide.f01.step1':
    'බ්‍රවුසරයක fleet.mageride.lk විවෘත කරන්න. රථ සමූහ ද්වාරය යෙදුමක් නොව වෙබ් අඩවියකි, එය ' +
    'දුරකථන තිරයකට මෙන්ම ඩෙස්ක්ටොප් එකකටත් සකසා ඇත.',
  'www.guide.f01.step2':
    'විද්‍යුත් තැපැල් ලිපිනයක් සහ මුරපදයක් සමඟ, Google සමඟ, හෝ Apple සමඟ ලියාපදිංචි වන්න. ' +
    'තුනම යන්නේ එකම රථ සමූහ ගිණුමටය, පසුව අනෙක් ඒවා ඊට සම්බන්ධ කළ හැක.',
  'www.guide.f01.step2.note':
    'රියදුරන් පිවිසෙන ආකාරය මෙය නොවේ. රියදුරු යෙදුම දුරකථන අංකයක් සහ එක් වරක් කේතයක් භාවිත ' +
    'කරයි; රථ සමූහ ද්වාරය විද්‍යුත් තැපෑල, Google හෝ Apple භාවිත කරයි. ඔබ රථ සමූහයක් ' +
    'පවත්වාගෙන යනවා මෙන්ම රිය පදවනවා නම්, ඔබට වෙන් වෙන් ක්‍රම දෙකක් ඇත.',
  'www.guide.f01.step3':
    'ඔබේ විද්‍යුත් තැපැල් ලිපිනය තහවුරු කරන්න — එය කරන තුරු ඔබේ ප්‍රවේශය සීමිතය. අමතක වූ ' +
    'මුරපදයක් යළි සකසන්නේද එම තිරයෙන්මය.',
  'www.guide.f01.step4':
    'සංවිධානයේ පැතිකඩ පුරවා ඔබේ KYC ලේඛන උඩුගත කරන්න: ව්‍යාපාරයේ නම සහ ලියාපදිංචිය, ' +
    'සම්බන්ධතාවක්, සහ බලයලත් පුද්ගලයාගේ හැඳුනුම.',
  'www.guide.f01.step5':
    'එය ඉදිරිපත් කර බලා සිටින්න. රියදුරෙකුගේ වාහනයක් සමාලෝචනය කරන ආකාරයටම MageRide හි ' +
    'සත්‍යාපන නිලධාරියෙක් ඔබේ සංවිධානය සමාලෝචනය කරයි.',
  'www.guide.f01.step5.note':
    'ඔබ බලා සිටින අතරතුර ඔබේ සංවිධානය පොරොත්තු තත්ත්වයේ පවතී. ද්වාරය වටා බැලිය හැක, නමුත් තවම ' +
    'වාහනයක් එක් කිරීමට හෝ රියදුරෙකු පැවරීමට නොහැක.',
  'www.guide.f01.step6':
    'ඔබේ KYC ප්‍රතික්ෂේප වුවහොත් ඊට හේතුවක් ලැබේ. ලේඛන නිවැරදි කර නැවත ඉදිරිපත් කරන්න — ඔබ ' +
    'ඇතුළත් කළ අනෙක් කිසිවක් නැති නොවේ.',
  'www.guide.f01.step7':
    'අනුමැතිය ලැබුණු පසු ඔබේ කණ්ඩායම ආරාධනා කරන්න. එක් එක් සාමාජිකයා තමන්ගේම විද්‍යුත් තැපෑල, ' +
    'Google හෝ Apple අක්තපත්‍ර සමඟ පිවිසෙන අතර, ඔවුන් කළමනාකරුවෙක්ද නරඹන්නෙක්ද යන්න ඔබ තෝරයි.',
  'www.guide.f01.step8':
    'ඔබේ භාෂාව සකසන්න. MageRide හි අනෙක් සියල්ල මෙන්ම රථ සමූහ ද්වාරයද සිංහල, දෙමළ සහ ' +
    'ඉංග්‍රීසි භාෂාවලින් ඇත.',
  'www.guide.f01.callout.approvalGate':
    'අනුමැතිය යනු විධිමත් පිළිවෙතක් නොව දොරටුවකි. සත්‍යාපන නිලධාරියෙක් ඔබේ සංවිධානය අනුමත කරන ' +
    'තුරු වාහන එක් කිරීම සහ රියදුරන් පැවරීම ක්‍රියා විරහිතය. ආරම්භක දිනයක් සැලසුම් කරනවා නම්, ' +
    'මෙතැනින් පටන් ගෙන ඒ සඳහා කාලය තබා ගන්න.',
  'www.guide.f01.callout.threeSubRoles':
    'කණ්ඩායම් සාමාජිකයන් වර්ග තුනකි — හිමිකරු, කළමනාකරු සහ නරඹන්නා — ඔබ හිමිකරුය. මෙම ' +
    'මාර්ගෝපදේශයේ කුමන කොටස් මත මේ තිදෙනාට එක් එක් ලෙස ක්‍රියා කළ හැකිද යන්න 5 වන පරිච්ඡේදයේ ' +
    'දක්වා ඇත. කිසිවෙකු ආරාධනා කිරීමට පෙර එය කියවීම වටී.',
  'www.guide.f01.callout.whoSeesYourKyc':
    'ඔබේ සංවිධානයේ KYC සමාලෝචනය කරන්නේ MageRide සත්‍යාපන නිලධාරියෙකි — රියදුරෙකුගේ ලේඛන යන එම ' +
    'අනුමැති මාර්ගයෙන්මය. ප්‍රතික්ෂේපයක් නම් ඊට හේතුව සමඟ ඔබ වෙත එයි.',

  'www.guide.f02.title': 'KYC, සහ බැංකු හා ගෙවීම් පැතිකඩ',
  'www.guide.f02.summary':
    'B ක්‍රමයේ දායකයන්ගේ මුදල් ඇත්තටම ලැබෙන තැන, ද්වාරය ඔබෙන් ඉල්ලන ගොනු දෙක, සහ කිසිවෙකුගෙන් ' +
    'අය කිරීමට පෙර සමත් විය යුතු එකම සත්‍යාපනය.',
  'www.guide.f02.step1':
    'බැංකු හා ගෙවීම් විස්තර විවෘත කරන්න. එයට යන්නේ සංවිධාන සැකසුමෙන් වන අතර, විවෘත කළ හැක්කේ ' +
    'හිමිකරුට පමණි.',
  'www.guide.f02.step1.note':
    'කළමනාකරුවෙකුට මෙම තිරය කිසිසේත් නොපෙනේ. ඔබේ කණ්ඩායමේ කෙනෙක් සබැඳිය නැති බව කියනවා නම්, ' +
    'හේතුව එයයි.',
  'www.guide.f02.step2': 'ඔබේ බැංකුව, ශාඛාව, ගිණුම් අංකය සහ ගිණුම් හිමියාගේ නම ඇතුළත් කරන්න.',
  'www.guide.f02.step2.note':
    'ගිණුම් හිමියාගේ නම ඔබේ සංවිධානයේ KYC හි නම සමඟ ගැළපිය යුතුය. වෙනත් නමකින් ඇති පෞද්ගලික ' +
    'ගිණුමක් සත්‍යාපනය සමත් නොවේ.',
  'www.guide.f02.step3':
    'ඔබේ නවතම බැංකු ප්‍රකාශයේ පිටපතක්, නැතහොත් ඔබේ බැංකු පොතේ පළමු පිටුව උඩුගත කරන්න. ගිණුම ' +
    'සංවිධානයට අයත් බව පෙන්වන්නේ මෙයයි.',
  'www.guide.f02.step4':
    'ඔබේ බැංකු යෙදුම සාදන LankaQR කේත රූපය උඩුගත කරන්න. මෙය ලේඛන කටයුත්තක් නොවේ — දායකයෙක් ' +
    'ඔබට ගෙවන විට ස්කෑන් කරන්නේ හරියටම මෙම රූපයයි.',
  'www.guide.f02.step5':
    'ඉදිරිපත් කරන්න. පැතිකඩ සත්‍යාපනය පොරොත්තුවට යන අතර, ඔබේ සංවිධානය ඇති එම පෝලිමේම සත්‍යාපන ' +
    'නිලධාරියෙක් එය සමාලෝචනය කරයි. එය සත්‍යාපිත ලෙස, නැතහොත් හේතුවක් සමඟ ප්‍රතික්ෂේපිත ලෙස ' +
    'ආපසු එයි.',
  'www.guide.f02.step6':
    'සත්‍යාපිත වූ පසු, B ක්‍රමයේ දායක ගෙවීම් යන්නේ එතැනටය. දායකයෙකුගේ ගෙවීම් තිරයේ ස්කෑන් ' +
    'කිරීමට ඔබේ LankaQR කේතයද, සබැඳි මාරුවක් සඳහා ඔබේ සත්‍යාපිත ගිණුම් විස්තරද පෙන්වයි.',
  'www.guide.f02.step7':
    'පසුව ඔබ යමක් වෙනස් කළහොත් — ශාඛාවක්, ගිණුම් අංකයක්, QR රූපය — පැතිකඩ නැවත සත්‍යාපනය ' +
    'පොරොත්තුවට යයි.',
  'www.guide.f02.step7.note':
    'පොරොත්තුවේ තිබියදී කිසිවක් කැඩෙන්නේ නැත. ඔබට ගෙවන අය දිගටම දකින්නේ අවසන් වරට සත්‍යාපනය ' +
    'වූ විස්තර මිස අඩක් සංස්කරණය කළ ඒවා නොවේ.',
  'www.guide.f02.callout.paidNeedsVerified':
    'මෙම පැතිකඩ සත්‍යාපිත වන තුරු ඔබට වාහනයක සේවා ගෙවීම “ගෙවුම්” ලෙස සැකසිය නොහැක, ගෙවුම් ' +
    'දායකත්වයකට බිල් කිරීම ආරම්භ කළද නොහැක. වාහනයක් සඳහා අය කිරීමට අදහස් කරනවා නම්, කිසිවක් ' +
    'මිල කිරීමට හෝ එක් දායකයෙකු ආරාධනා කිරීමට පෙර මෙය අවසන් කරන්න.',
  'www.guide.f02.callout.passThrough':
    'ඔබේ B ක්‍රමයේ මගීන්ගෙන් එන දායක මුදල් යන්නේ ඔබ වෙතය, MageRide වෙත නොවේ. MageRide එය ' +
    'කිසිවිටෙක රඳවා නොගනී. ගිණුම මුලින්ම සත්‍යාපනය කරන්නේද ඒ නිසාය — වැරදි ගිණුම් අංකයක් ' +
    'අල්ලා ගැනීමට මැද කිසිවෙක් නැත.',
  'www.guide.f02.callout.whatSubscribersSee':
    'දායකයෙකුගේ ගෙවීම් තිරය මෙම පැතිකඩෙන් පෙන්වන්නේ දෙකකි: ඔබේ LankaQR රූපය සහ ඔබේ සත්‍යාපිත ' +
    'ගිණුම් විස්තර. ඔබ උඩුගත කරන ප්‍රකාශය හෝ බැංකු පොතේ පිටුව ඇත්තේ සත්‍යාපන නිලධාරියා සඳහා ' +
    'මිස එම තිරය සඳහා නොවේ.',

  'www.guide.f03.title': 'වාහන එක් කිරීම — එකින් එක, සහ තොග වශයෙන්',
  'www.guide.f03.summary':
    'ඔබේ රථ සමූහයට වාහන එක් කිරීම, කිසිවෙකුට දායක විය හැකි වීමට පෙර පෞද්ගලික වාහනයකට අවශ්‍ය ' +
    'සැකසුම, සහ තොග උඩුගත කිරීමක් අවසන් කරන දේ සහ නොකරන දේ.',
  'www.guide.f03.step1':
    'වාහන ඇතුළත් කිරීම විවෘත කරන්න. හිමිකරුවෙකුට හෝ කළමනාකරුවෙකුට මෙය කළ හැකි අතර, ඔබේ ' +
    'සංවිධානය මුලින්ම අනුමත විය යුතුය.',
  'www.guide.f03.step2':
    'වාහනයක් තනිව එක් කරන්න, නැතහොත් පැතුරුම්පතක් උඩුගත කර බොහෝ ගණනක් එකවර එක් කරන්න.',
  'www.guide.f03.step2.note':
    'රථ සමූහයක් ධාවනය කරන්නේ මහජන ප්‍රවාහන සහ පෞද්ගලික වාහන — A ක්‍රමය සහ B ක්‍රමය. ඉල්ලුම මත ' +
    'රිය පැදවීම, එනම් C ක්‍රමය, රථ සමූහයක් කරන දෙයක් නොවේ; එය එක් එක් රියදුරා සහ MageRide අතර ' +
    'ගිවිසුමකි.',
  'www.guide.f03.step3':
    'A ක්‍රමයේ වාහනයකට — මාර්ගයක බසයකට — මිල කිරීමට කිසිවක් නැත. මහජන ප්‍රවාහනය නොමිලේ.',
  'www.guide.f03.step4':
    'B ක්‍රමයේ වාහනයකට සේවා ගෙවීම් සැකසුමක් අවශ්‍යය: නොමිලේ හෝ ගෙවුම්. කාර්ය මණ්ඩල බසයක් හෝ ' +
    'කාර්යාල වෑන් රථයක් සාමාන්‍යයෙන් නොමිලේය. ගෙවුම් වාහනයකට පෙරනිමි මාසික ගාස්තුවක්ද ' +
    'අවශ්‍යය.',
  'www.guide.f03.step4.note':
    'ගෙවුම් තෝරාගත හැක්කේ ඔබේ බැංකු හා ගෙවීම් පැතිකඩ සත්‍යාපිත වූ පසුවය. එය 2 වන පරිච්ඡේදයයි.',
  'www.guide.f03.step5':
    'වාහන බොහෝ ගණනක් එකවර එක් කිරීමට පැතුරුම්පත උඩුගත කර වලංගු කිරීමට ඉඩ දෙන්න. ගැටලු ඇති ' +
    'පේළි නිශ්ශබ්දව ඉවත් කරනවා වෙනුවට සලකුණු කෙරේ.',
  'www.guide.f03.step6':
    'දෝෂ වාර්තාව බාගන්න. එය පේළිය සහ එහි ඇති වරද නම් කරන නිසා, අසමත් වූ පේළි පමණක් නිවැරදි කර ' +
    'නැවත උඩුගත කළ හැක.',
  'www.guide.f03.step7':
    'තොග වශයෙන් එක් කළ වාහන පැමිණෙන්නේ ලේඛන තවම ඉතිරිව තිබියදීය. ලේඛන වාහනයෙන් වාහනයට වන අතර, ' +
    'ඒවා 4 වන පරිච්ඡේදයයි.',
  'www.guide.f03.step8':
    'ඉන්පසු සෑම වාහනයක්ම MageRide හි වෙනත් ඕනෑම වාහනයක් යන එම අනුමැතියටම ගොස් පොරොත්තු, අනුමත ' +
    'හෝ ප්‍රතික්ෂේපිත ලෙස පෙන්වයි. වාහනයක් අක්‍රිය කිරීමට හෝ ඉවත් කිරීමටද හැකි අතර, එය රථ ' +
    'සමූහ හා මගී සිතියම්වලින් වහාම ඉවත් වේ.',
  'www.guide.f03.callout.modeAandBOnly':
    'රථ සමූහයක් යනු A ක්‍රමය සහ B ක්‍රමය පමණි. ඉල්ලුම මත වාහනයක් මෙතැනට එක් කිරීම ප්‍රතික්ෂේප ' +
    'වන අතර, එය සීමාවක් නොව හිතාමතාය: C ක්‍රමය ධාවනය වන්නේ එක් එක් රියදුරාගේම යෙදුමෙන් සහ ' +
    'ඔහුගේම පසුම්බියෙනි.',
  'www.guide.f03.callout.paidNeedsVerifiedProfile':
    'ගෙවුම් සඳහා සත්‍යාපිත ගෙවීම් පැතිකඩක් අවශ්‍යය. වාහන හතළිහක් සකසා අවසානයේ ඒ කිසිවකට අය කළ ' +
    'නොහැකි බව සොයාගැනීම මෙහි මිල අධික අනුවාදයයි. 2 වන පරිච්ඡේදය මුලින්ම කිරීම ලාභ අනුවාදයයි.',
  'www.guide.f03.callout.renamedLabel':
    'මෙම සැකසුම හඳුන්වන්නේ සේවා ගෙවීම ලෙසය. පෙර එය B ක්‍රමයේ වර්ගීකරණය ලෙස හැඳින්වූ අතර පැරණි ' +
    'ලියවිලිවල එම නම තවමත් හමු විය හැක. එකම සැකසුම, එකම අගයන් දෙක — නොමිලේ සහ ගෙවුම්.',

  'www.guide.f04.title': 'වාහන ලේඛන, සහ අනුමැතිය රඳවන දේ',
  'www.guide.f04.summary':
    'වාහනයකට අවශ්‍ය ලේඛන හතර, ඒවායින් කුමක් කුමන වර්ගයේ වාහනයකට අදාළ වේද, සහ අනෙක් සියල්ල ' +
    'පුරවා තිබියදීත් වාහනයක් පොරොත්තුවේ රැඳී සිටිය හැක්කේ ඇයි යන්න.',
  'www.guide.f04.step1':
    'වාහනය විවෘත කර එහි ලේඛන කුටි සොයාගන්න. කුටි හතරක් ඇති අතර, එකක් ඊට යන ලේඛනයෙන් නම් කර ' +
    'ඇත.',
  'www.guide.f04.step2':
    'ලියාපදිංචි පිටපත — CR පොත — රක්ෂණ සහතිකය සහ ආදායම් බලපත්‍රය උඩුගත කරන්න. සෑම වාහනයකටම මේ ' +
    'තුනම අවශ්‍යය.',
  'www.guide.f04.step2.note':
    'රක්ෂණය සෑම ක්‍රමයකටම අවශ්‍යය. ඉන් නිදහස් වූ MageRide වාහන වර්ගයක් නැත.',
  'www.guide.f04.step3':
    'වාහනය මහජන ප්‍රවාහනය සඳහා නම්, මාර්ග බලපත්‍රයද උඩුගත කරන්න. A ක්‍රමයට එය අවශ්‍යය; අනෙක් ' +
    'ක්‍රමවලට නොවේ.',
  'www.guide.f04.step4':
    'උඩුගත වන විට එක් එක් ලේඛනය ස්වයංක්‍රීයව කියවේ. ලියාපදිංචි අංකය තහඩුව සමඟ පරීක්ෂා කරන ' +
    'අතර, කල් ඉකුත්වන දින, බලපත්‍ර අංක සහ මාර්ග ඔබ වෙනුවෙන් වෙන් කර ගනී.',
  'www.guide.f04.step5':
    'ඉන්පසු එක් එක් කුටිය තමන්ගේම තත්ත්වයක් දරයි — සත්‍යාපිත, පොරොත්තු හෝ නොමැති. වාහනය පමණක් ' +
    'නොව කුටිද කියවන්න.',
  'www.guide.f04.step6':
    'අවශ්‍ය ලේඛනයක් නොමැති හෝ පොරොත්තු වන තුරු වාහනය අනුමත කළ නොහැක. ප්‍රතික්ෂේපිත ලෙස ආපසු ' +
    'එන ලේඛනයක් නැවත උඩුගත කරනු ලැබේ.',
  'www.guide.f04.step7':
    'කල් ඉකුත්වන දින නිරීක්ෂණය කරන්න. කල් ඉකුත් වූ අවශ්‍ය ලේඛනයක් නිසා එය ප්‍රතිස්ථාපනය කරන ' +
    'තුරු එම වාහනය සේවයෙන් ඉවත් වේ.',
  'www.guide.f04.callout.approvalIsGated':
    'සෑම ක්ෂේත්‍රයක්ම පුරවා එක් ලේඛනයක් තවම පොරොත්තුවේ ඇති වාහනයක් අනුමත නොවේ. වාහනයක් හිර වී ' +
    'ඇති විට පිළිතුර වාහනයේ තත්ත්වයේ නොව ලේඛන කුටියක තිබීම බොහෝ දුරට නියතය.',
  'www.guide.f04.callout.insuranceEveryMode':
    'රක්ෂණය සෑම ක්‍රමයකටම අනිවාර්යය — මහජන බසයකට, පෞද්ගලික වෑන් රථයකට, ඉල්ලුම මත කාරයකට. ' +
    'ක්‍රමයට විශේෂිත එකම ලේඛනය මාර්ග බලපත්‍රය වන අතර, එය අවශ්‍ය වන්නේ A ක්‍රමයට පමණි.',
  'www.guide.f04.callout.expiryStopsDispatch':
    'කල් ඉකුත් වූ ලේඛනයක් හුදෙක් අනතුරු ඇඟවීමක් පමණක් නොපෙන්වයි. ලේඛනය ප්‍රතිස්ථාපනය කරන තුරු ' +
    'වාහනය ස්වයංක්‍රීයව අත්හිටුවේ, එබැවින් සති අන්තයේ කල් ඉකුත් වන ආදායම් බලපත්‍රයක් සඳුදා ' +
    'වාහනයක් මාර්ගයෙන් ඉවත් කරයි.',

  'www.guide.f05.title': 'රියදුරන් පැවරීම, සහ ට්‍රැකර් බැඳීම',
  'www.guide.f05.summary':
    'රථ සමූහයේ වාහනයක් තමන් සිටින තැන වාර්තා කරන ක්‍රම දෙක, පැවරීමක් රියදුරාගේ පැත්තෙන් පෙනෙන ' +
    'ආකාරය, සහ ඔබේ කණ්ඩායමේ කාට මෙයින් කුමක් කළ හැකිද යන්න.',
  'www.guide.f05.step1':
    'රියදුරු පැවරීම විවෘත කර, රියදුරෙකුගේ පරිශීලක අංකයෙන් හෝ දුරකථන අංකයෙන් ඔහු වාහනයකට ' +
    'පවරන්න.',
  'www.guide.f05.step1.note':
    'රියදුරා දැනටමත් MageRide රියදුරෙකු විය යුතුය. ඔබ කරන්නේ පවතින රියදුරෙකු ඔබේ වාහනයට ' +
    'සම්බන්ධ කිරීම මිස ඔහුට ගිණුමක් සෑදීම නොවේ.',
  'www.guide.f05.step2':
    'රියදුරාගේ පැත්තෙන් එම වාහනය තමන්ගේම කණ්ඩායමක පෙනේ — තාවකාලිකව ඔවුන්ට පවරා ඇති වාහන — ' +
    'කුමන රථ සමූහය එය පැවරුවේද, කොපමණ කාලයකටද යන්න පෙන්වමින්. ඔවුන් එය තෝරාගෙන එයින් සබැඳි ' +
    'වේ, එකවර එක් වාහනයක් බැගින්.',
  'www.guide.f05.step3':
    'පැවරුම් ඉතිහාසය කුමන වාහනයේ කවුරුන් සිටියේද යන්න පෙන්වන නිසා, මතක තබාගනු වෙනුවට එය සොයා ' +
    'බැලිය හැක.',
  'www.guide.f05.step4':
    'පැවරුමක් අවසන් කිරීමට එය අවලංගු කරන්න. එම වාහනයේ නව සැසියක් ආරම්භ කිරීමේ හැකියාව ' +
    'රියදුරාට වහාම නැති වේ.',
  'www.guide.f05.step4.note':
    'දැනටමත් ක්‍රියාත්මක සැසියක් ඔබ තෝරන ආකාරය අනුව අවසන් වීමට ඉඩ දෙයි, නැතහොත් නවතී. පැවරුම් ' +
    'තමන්ගේම කාලයෙන් අවසන්ද වේ.',
  'www.guide.f05.step5':
    'ඒ වෙනුවට දෘඪාංග ට්‍රැකරයක් භාවිත කිරීමට, ට්‍රැකර් බැඳීම විවෘත කර උපාංගයේ IMEI හෝ MAC ' +
    'ලිපිනය ඇතුළත් කරන්න.',
  'www.guide.f05.step6':
    'ස්වයංක්‍රීය සැසි ක්‍රියාත්මක කරන්න. ට්‍රැකරයක් බැඳී ඇති වාහනයක් තමන්ගේම ගමන ආරම්භ කර ' +
    'අවසන් කරයි.',
  'www.guide.f05.step6.note':
    'ට්‍රැකරයක අරමුණ එයයි. ආරම්භය එබීමට කිසිවෙකුට මතක තබාගත යුතු නැති අතර, ඩිපෝවෙන් පිටවන ' +
    'බසයක් දැනටමත් වාර්තා කරමින් සිටී.',
  'www.guide.f05.step7':
    'ට්‍රැකරය කොපමණ වාරයක් වාර්තා කරයිද යන්න සකසන්න — ඔබේ ක්‍රියාකාරී පැයවලදී නිතර, ඉන් පිටත ' +
    'විරලව. එය සිතියම කෙතරම් නැවුම්ද යන්න සහ උපාංගය කොපමණ දත්ත භාවිත කරයිද යන්න අතර තුලනයකි.',
  'www.guide.f05.callout.whoCanDoWhat':
    'මෙම මාර්ගෝපදේශය හරහා කාට කුමක් කළ හැකිද යන්න. හිමිකරුවෙකුට පරිච්ඡේද හයම කළ හැක. ' +
    'කළමනාකරුවෙකුට 3, 4 සහ 5 පරිච්ඡේද — වාහන, ලේඛන, රියදුරන් සහ ට්‍රැකර් — කළ හැක, නමුත් 2 වන ' +
    'පරිච්ඡේදයේ ගෙවීම් පැතිකඩ හෝ 6 වන පරිච්ඡේදයේ බිල්පත් නොහැක. නරඹන්නෙක් ඉන් කිසිවක් මත ' +
    'ක්‍රියා නොකරයි: නරඹන්නෙක් සජීවී සිතියම සහ විශ්ලේෂණ කියවයි.',
  'www.guide.f05.callout.revoking':
    'අවලංගු කිරීම නව වැඩ සඳහා වහාම බලපායි, දැනටමත් ආරම්භ වී ඇති වැඩට අනිවාර්යයෙන් නොවේ. ' +
    'මාර්ගය මැද අවලංගු කර රියදුරාට එය සොයාගැනීමට ඉඩ දෙනවා වෙනුවට ඔහුට කියන්න.',
  'www.guide.f05.callout.scopedToYourOrg':
    'ඔබට පෙනෙන්නේ ඔබේම සංවිධානයේ වාහන මිස වෙන කිසිවෙකුගේ නොවේ. එය බලාත්මක වන්නේ තිරයෙන් නොව ' +
    'වේදිකාවෙනි — වෙනත් රථ සමූහයක වාහන ඔබේ සිතියමෙන් හුදෙක් සඟවා ඇත්තේ නොව, ඒවා ඊට ලබාගත ' +
    'නොහැක.',

  'www.guide.f06.title': 'බිල්පත් — පෞද්ගලික වාහනයකට මාසික ගාස්තුවක්',
  'www.guide.f06.summary':
    'ඔබේ රථ සමූහය MageRide වෙත ගෙවන දේ සහ නොගෙවන දේ, රථ සමූහ පසුම්බිය පුරවා තබාගන්නා ආකාරය, ' +
    'සහ ඔබේ දායකයන් ඔබට ගෙවන මුදල් මෙම ඉන්වොයිසියේ කිසිදා නොපෙනෙන්නේ ඇයි යන්න.',
  'www.guide.f06.step1':
    'උපකරණ පුවරුව ඔබ නිතර බලන අංක දෙකක් පෙන්වයි: ඔබේ රථ සමූහ පසුම්බියේ ශේෂය, සහ ඊළඟ මාසික ' +
    'ඉන්වොයිසිය.',
  'www.guide.f06.step2':
    'ඉන්වොයිසිය සඳහා බිල්පත් හා පසුම්බිය විවෘත කරන්න. එය එක් එක් වාහනයට පේළියක් සහිත, මුළු රථ ' +
    'සමූහයටම එක් ඉන්වොයිසියකි — එබැවින් ඔබ ගෙවන්නේ එක් වරක් වුවත් මුදල පැමිණි තැන තවමත් දැකිය ' +
    'හැක.',
  'www.guide.f06.step3':
    'ගාස්තු ලෙස පෙනෙන්නේ B ක්‍රමයේ වාහන පමණි. A ක්‍රමයේ වාහන — මහජන බස් — නොමිලේය.',
  'www.guide.f06.step3.note':
    'ඔබේ රථ සමූහයෙන් අඩක් බස් නම්, අඩකට කිසිවක් වැය නොවේ. ඉන්වොයිසිය වාහන ලැයිස්තුවට වඩා කෙටි ' +
    'වනු ඇත, එය නිවැරදිය.',
  'www.guide.f06.step4':
    'ණය හෝ හර කාඩ්පතකින්, OnePay මගින්, හෝ LankaQR මගින් රථ සමූහ පසුම්බිය පුරවන්න.',
  'www.guide.f06.step5':
    'බැංකු මාරුවක් නැත. ඔබ බලාපොරොත්තු වූයේ එය නම්, එය බිඳවැටීමක් නොවේ — එම ක්‍රමය වේදිකාවෙන් ' +
    'ඉවත් කර ඇත.',
  'www.guide.f06.step6': 'ඔබේම වාර්තා සඳහා ලදුපත බාගන්න.',
  'www.guide.f06.step7':
    'ඔබේ B ක්‍රමයේ දායකයන් ඔබට ගෙවන මුදල මුළුමනින්ම වෙනස් ගලායාමකි. එය යන්නේ 2 වන පරිච්ඡේදයේ ' +
    'සත්‍යාපිත ගෙවීම් පැතිකඩ වෙතය — MageRide වෙත නොවේ, මෙම ඉන්වොයිසියට එරෙහිවද නොවේ.',
  'www.guide.f06.step7.note':
    'දිශා දෙකක්, තැන් දෙකක්. ඔබ MageRide වෙත ගෙවන දේ මෙතැනය. ඔබේ දායකයන් ඔබට ගෙවන දේ දායකත්ව ' +
    'තිරවල ඇති අතර, එය ලැබෙන්නේ ඔබේම බැංකු ගිණුමටය.',
  'www.guide.f06.callout.whatTheFleetPays':
    'රථ සමූහයක් MageRide වෙත ගෙවන දේ, සම්පූර්ණයෙන්: B ක්‍රමයේ එක් එක් වාහනයට මාසික ගාස්තුවක්. ' +
    'මහජන ප්‍රවාහන වාහන නොමිලේය. ගමනකට ගාස්තුවක් හෝ කිසිවක් මත කොමිස් මුදලක් නැත.',
  'www.guide.f06.callout.modeCIsNotYours':
    'දෛනික වේදිකා ගාස්තුව ඔබ ගෙවිය යුත්තක් නොවේ. එය ඉල්ලුම මත ගාස්තුව වන අතර, එය එන්නේ ' +
    'රියදුරු යෙදුමේ එක් එක් රියදුරාගේම පසුම්බියෙන් වන අතර, එය කිසිවිටෙක රථ සමූහ පසුම්බියක් ' +
    'ස්පර්ශ නොකරයි — එම රියදුරා ඔබේ වාහනයක්ද පදවන විටෙක වුවද. ගාස්තු ආකෘති දෙකක්, ඒවා එකතු ' +
    'නොකෙරේ.',
  'www.guide.f06.callout.moneyInIsSeparate':
    'ඔබේ මගීන්ගෙන් එන දායක ගෙවීම් MageRide ගාස්තුවක් නොවන අතර මෙම ඉන්වොයිසියට එරෙහිව බැර ' +
    'නොකෙරේ. ඒවා ඔබට කෙලින්ම ගෙවනු ලබන්නේ, 2 වන පරිච්ඡේදයේ ඔබ සත්‍යාපනය කළ ගිණුමටය.',
};
