import type { WwwMessages } from './en';

/**
 * Tamil resources. `WwwMessages` is the literal shape of `wwwEn`, so this file
 * cannot be missing a key `en.ts` has, nor carry one it does not.
 *
 * **MCS-34 D2 defers Tamil to the release after launch**, and this file is what
 * "deferred, not dropped" means in practice: the table exists, it is complete, and
 * it stays a compile error to add a key to `en.ts` and not to here. What S13 owns
 * is a native review of the *strings*; what this file guarantees is that no key can
 * quietly go missing while that review is outstanding.
 *
 * The strings below are scaffold labels, not marketing copy — nav labels and a
 * placeholder notice. Nothing here has been through a native reviewer and nothing
 * here is a public claim; S13 is where that changes.
 */
export const wwwTa: WwwMessages = {
  'www.brand.name': 'MageRide',
  'www.brand.tagline': 'இலங்கை பயணிக்கும் விதத்தின் ஒரே நேரடிக் காட்சி',

  'www.nav.home': 'முகப்பு',
  'www.nav.vision': 'நோக்கு',
  'www.nav.passengers': 'பயணிகளுக்கு',
  'www.nav.drivers': 'சாரதிகளுக்கு',
  'www.nav.fleets': 'வாகன உரிமையாளர்களுக்கு',
  'www.nav.screens': 'திரைகள்',
  'www.nav.guide': 'MageRide பயன்படுத்தும் முறை',
  'www.nav.faq': 'கேள்விகள்',
  'www.nav.download': 'செயலியைப் பெறுங்கள்',
  'www.nav.contact': 'தொடர்பு',
  'www.nav.legal.terms': 'சேவை நிபந்தனைகள்',
  'www.nav.legal.privacy': 'தனியுரிமைக் கொள்கை',
  'www.nav.legal.pdpa': 'உங்கள் தரவு உரிமைகள்',

  // `www.scaffold.notice` was deleted in S18 with `StubPage`.

  'www.notFound.title': 'பக்கம் கிடைக்கவில்லை',
  'www.notFound.body': 'அந்த முகவரி இந்த இணையதளத்தில் இல்லை.',
  'www.notFound.home': 'TODO(ta) Go to the home page',
  'www.error.title': 'ஏதோ தவறு நடந்தது',
  'www.error.body': 'இந்தப் பக்கத்தைக் காட்ட முடியவில்லை. மீண்டும் முயற்சிக்கவும்.',

  // The chrome (S14). `TODO(ta)` per MCS-34 D2 — Tamil is deferred and `/ta` is
  // unpublished (S13), so no reader can reach these; the markers keep them
  // countable for the release that ships Tamil.
  'www.a11y.skipToContent': 'TODO(ta) Skip to content',

  'www.nav.primary': 'TODO(ta) Main',
  'www.nav.footer': 'TODO(ta) Footer',
  'www.nav.brandHome': 'TODO(ta) MageRide — home',
  'www.nav.menu.open': 'TODO(ta) Menu',
  'www.nav.menu.close': 'TODO(ta) Close the menu',
  'www.nav.menu.title': 'TODO(ta) Menu',

  'www.language.label': 'TODO(ta) Language',
  'www.language.current': 'TODO(ta) {language}, current language',
  'www.language.switchTo': 'TODO(ta) Read this page in {language}',

  'www.appearance.dark': 'TODO(ta) Dark appearance',

  'www.hero.label': 'TODO(ta) What MageRide does',
  'www.hero.slideAnnouncement': 'TODO(ta) Slide {index} of {count}: {headline}',

  // The screen showcase and its lightbox (S15).
  'www.showcase.label': 'TODO(ta) Screens from the MageRide apps',
  'www.showcase.open': 'TODO(ta) View larger: {caption}',
  'www.showcase.lightbox.title': 'TODO(ta) Screen',
  'www.showcase.lightbox.position': 'TODO(ta) Image {index} of {count}',

  'www.motion.carousel.roleDescription': 'சுழற்காட்சி',
  'www.motion.carousel.slideRoleDescription': 'காட்சி',
  'www.motion.carousel.slidePosition': '{count} இல் {index} ஆவது காட்சி',
  'www.motion.carousel.goToSlide': '{index} ஆவது காட்சியைக் காட்டு',
  'www.motion.carousel.pause': 'சுழற்காட்சியை நிறுத்து',
  'www.motion.carousel.play': 'சுழற்காட்சியைத் தொடங்கு',

  // Screen captions — see the block comment in `en.ts`. First-pass Tamil, and
  // MCS-34 D2 defers the native review to S13: these are complete, not reviewed.
  'www.screens.provenance':
    'இந்தப் படங்கள் அங்கீகரிக்கப்பட்ட MageRide இடைமுக வடிவமைப்புகளிலிருந்து ' +
    'உருவாக்கப்பட்டவை; வெளியிடப்பட்ட செயலியிலிருந்து எடுக்கப்பட்ட புகைப்படங்கள் அல்ல.',

  'www.screens.pa001.caption': 'ஒரு பயணியின் தொலைபேசியில் MageRide திறக்கிறது',
  'www.screens.pa002.caption': 'அனைத்திற்கும் முன் சிங்களம், தமிழ் அல்லது ஆங்கிலம் தேர்வு',
  'www.screens.pa003.caption': 'தொலைபேசி எண் மற்றும் ஒரு முறை குறியீட்டால் உள்நுழைதல்',
  'www.screens.pa004.caption': 'உங்கள் பெயரையும் புகைப்படத்தையும் சேர்த்து சுயவிவரம் நிறைவு',
  'www.screens.pa005.caption': 'உங்கள் இருப்பிடத்தைப் பயன்படுத்த அனுமதி கேட்பது, எதற்கெனச் சொல்வது',
  'www.screens.pa006.caption': 'போக்குவரத்து முறை, வாகன வகை அடிப்படையில் வரைபடத்தை வடிகட்டுதல்',
  'www.screens.pa007.caption': 'ஒரு வாகனத்தைத் தட்டி அதன் பாதை, வகை, இருப்பிடம் பார்ப்பது',
  'www.screens.pa008.caption': 'பெயரால் இடத்தைத் தேடுவது, அல்லது வரைபடத்தில் குறி வைப்பது',
  'www.screens.pa009.caption': 'வாகனத்தைத் தேர்ந்து, பதிவு செய்யும் முன் கட்டணத்தைப் பார்ப்பது',
  'www.screens.pa010.caption': 'உங்கள் அருகில் பேருந்து, ரயில், முச்சக்கரம் — நேரலையில் நகர்கின்றன',
  'www.screens.pa011.caption': 'வேறொருவர் உங்களுக்குப் பயணம் பதிவு செய்தால் ஏற்றத்தை உறுதிப்படுத்துதல்',
  'www.screens.pa012.caption': 'பொதி அனுப்புதல் — அளவு, பெறுநர், விநியோகக் குறியீடு',
  'www.screens.pa013.caption': 'அன்றைய பிற்பகுதிக்கோ வாரத்தின் பிற்பகுதிக்கோ பயணம் பதிவு',
  'www.screens.pa014.caption': 'உங்கள் பயணம் அருகிலுள்ள சாரதிகளுக்கு வழங்கப்படும்போது காத்திருத்தல்',
  'www.screens.pa015.caption': 'சாரதியின் விவரங்கள் கையருகில், உங்கள் பயணத்தைப் பின்தொடர்தல்',
  'www.screens.pa016.caption': 'பணம் செலுத்தும் முறை — ரொக்கம் அல்லது சாரதியின் குறியீட்டு ஸ்கேன்',
  'www.screens.pa017.caption': 'பயணத்தின் இறுதியில் பணம் செலுத்துதல்',
  'www.screens.pa018.caption': 'கட்டணம் பிரித்துக் காட்டப்பட்ட பயணச் சுருக்கம்',
  'www.screens.pa019.caption': 'உங்கள் சாரதிக்கு மதிப்பீடும் விமர்சனமும் அளித்தல்',
  'www.screens.pa020.caption': 'உங்கள் பொதி மூன்று நிலைகளைக் கடப்பதைப் பார்ப்பது',
  'www.screens.pa021.caption': 'பொதியைப் பெறுபவர் காண்பது',
  'www.screens.pa022.caption': 'உங்கள் கடந்த பயணங்களும் திட்டமிட்டவையும் ஒரே பட்டியலில்',
  'www.screens.pa024.caption': 'வாகன உரிமையாளரிடம் பின்தொடர அனுமதி கேட்பது',
  'www.screens.pa025.caption': 'நீங்கள் பின்தொடரும் தனியார் வாகனங்களும் அவற்றின் கட்டணமும்',
  'www.screens.pa025a.caption': 'வாகனம் ஒன்றைப் பின்தொடர மாதச் சந்தாவைச் செலுத்துதல்',
  'www.screens.pa026.caption': 'நீங்கள் அடிக்கடி செல்லும் இடங்களைச் சேமித்தல்',
  'www.screens.pa027.caption': 'உங்கள் சுயவிவரம், மொழி, தனியுரிமை அமைப்புகள்',
  'www.screens.pa029.caption': 'பயணத்திற்குள்ளிருந்தே அணுகக்கூடிய அவசர உதவி',
  'www.screens.pa030.caption': 'உதவி பெறுதல், தேவைப்பட்டால் கோரிக்கை பதிவு செய்தல்',

  'www.screens.da001.caption': 'MageRide சாரதி செயலி திறக்கிறது',
  'www.screens.da002.caption': 'உங்கள் மொழியையும் நீங்கள் ஓட்டும் நகரத்தையும் தேர்வு',
  'www.screens.da003.caption': 'உங்கள் தொலைபேசி எண், ஒரு முறை குறியீட்டால் உள்நுழைதல்',
  'www.screens.da003a.caption': 'உங்கள் சாரதி சுயவிவரத்தை அமைத்தல்',
  'www.screens.da004.caption': 'ஒரு வாகனத்தைப் பதிவு செய்தல், படிப்படியாக',
  'www.screens.da004a.caption': 'உங்கள் காப்பீட்டு விவரங்களைச் சேர்த்தல்',
  'www.screens.da005.caption': 'ஆவணத்தைப் புகைப்படம் எடுத்தல், வெட்டுதல் உங்கள் கட்டுப்பாட்டில்',
  'www.screens.da006.caption': 'ஆவணம் ஆவணமாக உங்கள் ஒப்புதலைப் பின்தொடர்தல்',
  'www.screens.da007.caption': 'சாரதி செயலிக்குத் தேவையான அனுமதிகள், அவை எதற்கு',
  'www.screens.da010.caption': 'உங்கள் கட்டுப்பாட்டுப் பலகை — தயார்நிலைக்குச் சென்று சம்பாதியுங்கள்',
  'www.screens.da011.caption': 'திட்டமிட்ட பயணத்தைத் தொடங்குதலும் முடித்தலும்',
  'www.screens.da013.caption': 'நீங்கள் செல்லும் திசையை அமைத்தல், வழியிலேயே பயணங்கள் கிடைக்க',
  'www.screens.da014.caption': 'ஒரு பயண வாய்ப்பு, ஏற்க பதினைந்து விநாடிகள்',
  'www.screens.da015.caption': 'ஏற்றுமிடத்திலிருந்து இறக்குமிடம் வரை பயணத்தை வழிநடத்துதல்',
  'www.screens.da016a.caption': 'விநியோகப் பணியை ஏற்கும் முன் மதிப்பாய்வு செய்தல்',
  'www.screens.da016b.caption': 'பொதியைப் பெற்று குறியீட்டை உறுதிப்படுத்துதல்',
  'www.screens.da016c.caption': 'சேர்ந்ததற்கான சான்றுடன் விநியோகத்தை நிறைவு செய்தல்',
  // Δ S10: was 'பணிப் பலகை — நீங்கள் எடுக்கக்கூடிய விநியோகங்கள்' ("deliveries you
  // can pick up"). The job board carries scheduled rides, not deliveries — see the
  // note on this key in `en.ts`. Rebuilt from phrases already in this table rather
  // than translated afresh: 'உங்கள் அருகில்' from pa010, 'முன்பே பதிவு செய்யப்பட்ட
  // பயணங்கள்' from da018.
  'www.screens.da017.caption': 'பணிப் பலகை — உங்கள் அருகில், முன்பே பதிவு செய்யப்பட்ட பயணங்கள்',
  'www.screens.da018.caption': 'முன்பே பதிவு செய்யப்பட்ட பயணங்கள், உங்களுக்காகக் காத்திருக்கின்றன',
  'www.screens.da019.caption': 'உங்கள் சாரதி நிலை, மதிப்பீடு, புள்ளிவிவரங்கள்',
  'www.screens.da020.caption': 'இன்று, இந்த வாரம், இந்த மாதம் நீங்கள் சம்பாதித்தது',
  'www.screens.da021.caption': 'உங்கள் பணப்பை, அதிலிருந்து எடுக்கப்படும் தினசரிக் கட்டணம்',
  'www.screens.da022.caption': 'அட்டை, OnePay அல்லது LankaQR மூலம் பணப்பையை நிரப்புதல்',
  'www.screens.da023.caption': 'சாரதி அடையாள எண் மூலம் மற்றொரு சாரதியிடம் கடன் கோருதல்',
  'www.screens.da024.caption': 'கேட்ட சாரதிக்குக் கடனை மாற்றுதல்',
  'www.screens.da025.caption': 'நீங்கள் செலுத்திய ஒவ்வொரு தினசரிக் கட்டணமும் பட்டியலிடப்பட்டது',
  'www.screens.da027.caption': 'ஒரு வாகனத்துடன் GPS கண்காணிப்பானை இணைத்தல்',
  'www.screens.da028.caption': 'உங்கள் வாகனத்தை யார் பின்தொடரலாம் என முடிவு செய்தல்',
  'www.screens.da032.caption': 'சாரதிகளுக்கான அவசர உதவி, ஒரே அழுத்தத்தில்',
  'www.screens.da033.caption': 'ஆதரவு, மற்றும் தினசரிக் கட்டணத்தைத் திரும்பக் கோருதல்',

  'www.screens.fp001.caption': 'வாகனத் தொகுதி வலைத்தளத்தில் உங்கள் நிறுவனத்தைப் பதிவு செய்தல்',
  'www.screens.fp002.caption':
    'TODO(ta) Your organisation profile and its KYC documents',
  'www.screens.fp002a.caption':
    'TODO(ta) Bank and payout details — where subscription money lands',
  'www.screens.fp003.caption': 'உங்கள் முழு வாகனத் தொகுதியும் ஒரே கட்டுப்பாட்டுப் பலகையில்',
  'www.screens.fp004.caption': 'வாகனம் சேர்த்தல் — ஒவ்வொன்றாக அல்லது மொத்தமாக',
  'www.screens.fp005.caption': 'சாரதிகளை அவர்கள் ஓட்டும் வாகனங்களுக்கு ஒதுக்குதல்',
  'www.screens.fp006.caption': 'TODO(ta) Binding a GPS tracker to a vehicle',
  'www.screens.fp007.caption': 'உங்களுக்குச் சொந்தமான ஒவ்வொரு வாகனமும் ஒரே வரைபடத்தில் நேரலையில்',
  'www.screens.fp010.caption': 'உங்கள் தொகுதியின் கட்டணமும் பணப்பையும் ஒரே இடத்தில்',

  'www.screens.wt001.caption': 'SMS-ல் வரும் கண்காணிப்பு இணைப்பு — செயலி இல்லை, கணக்கு இல்லை',
  'www.screens.wt002.caption': 'எதையும் நிறுவாமல் உலாவியில் பொதியைப் பின்தொடர்தல்',
  'www.screens.wt003.caption': 'உள்நுழையாமல் ஓர் இணைப்பிலிருந்து ஏற்றத்தை உறுதிப்படுத்துதல்',
  'www.screens.wt005.caption': 'பொதி சேர்ந்ததற்கான உறுதிப்படுத்தல்',

  // =========================================================================
  // S07 · marketing corpus — awaiting translation.
  //
  // Every value below is the English string prefixed `TODO(ta)`. The keys must exist
  // or the build fails (en.ts defines the key type), so "not translated yet" is spelled
  // as a visible marker rather than as a missing key. `check-i18n-parity.mjs` counts
  // these in every build; S13 replaces them and the count reaches zero.
  // =========================================================================
  'www.vision.hero': 'TODO(ta) See how Sri Lanka moves — live.',
  'www.vision.body.p1':
    'TODO(ta) Sri Lanka runs on transport that is hard to see. A bus is somewhere on its ' +
    'route. A school van is somewhere between home and school. A three-wheeler is nearby, ' +
    'or it is not. Everyone waits, and nobody can say for how long.',
  'www.vision.body.p2':
    'TODO(ta) MageRide puts them on one map. Watch a public bus or a train arrive. Follow ' +
    'a school van you have been given permission to see. Book a three-wheeler, a car or a ' +
    'van and know the fare before you agree to it. Send a package across town and watch ' +
    'it go.',
  'www.vision.body.p3':
    'TODO(ta) Three things in one app, in Sinhala, Tamil and English — and no commission ' +
    'taken from the drivers who make it work.',
  'www.mission.statement':
    'TODO(ta) MageRide exists to give Sri Lanka one live picture of how the country moves ' +
    '— buses, trains, three-wheelers and vans on a single map, run as public ' +
    'infrastructure rather than a private service.',
  'www.mission.qualifier':
    'TODO(ta) We are starting, not finished. A vehicle appears on the map only once its ' +
    'operator or driver has joined, so early on you will see the ones who have — not ' +
    'every vehicle in the country. We would rather tell you that than let the map imply ' +
    'otherwise.',
  'www.values.zeroCommission.title': 'TODO(ta) Drivers keep 100%',
  'www.values.zeroCommission.body':
    'TODO(ta) MageRide takes no commission on any fare. The price a passenger sees is the ' +
    'price the driver receives — we never sit between them and the money.',
  'www.values.passengersFree.title': 'TODO(ta) Passengers pay nothing',
  'www.values.passengersFree.body':
    'TODO(ta) No subscription, no premium tier, no booking fee. You pay the driver for ' +
    'the ride, and nothing at all to us.',
  'www.values.firstTripFree.title': 'TODO(ta) The first trip each day is free',
  'www.values.firstTripFree.body':
    'TODO(ta) Drivers pay a flat daily fee only from their second trip, and nothing on ' +
    'the days they do not drive. No per-trip cut, ever.',
  'www.values.trilingual.title': 'TODO(ta) Sinhala, Tamil and English',
  'www.values.trilingual.body':
    'TODO(ta) Every screen, in all three languages, from the first launch. Not an ' +
    'afterthought and not a partial translation — the same app whichever one you read.',
  'www.values.openMapping.title': 'TODO(ta) Built on open maps',
  'www.values.openMapping.body':
    'TODO(ta) MageRide runs on OpenStreetMap data served from its own map servers, not a ' +
    'licensed commercial map. That keeps the cost off drivers and keeps the map something ' +
    'we can fix ourselves.',
  'www.values.yourData.title': 'TODO(ta) Your data stays yours',
  'www.values.yourData.body':
    'TODO(ta) Ask for a copy of everything we hold about you and we will send it within ' +
    '30 days. Ask us to erase it and we will, within 30 days, except where the law ' +
    'requires us to keep a record.',
  'www.hero.track.headline': 'TODO(ta) Watch it arrive',
  'www.hero.track.sub': 'TODO(ta) Buses, trains, three-wheelers and vans, moving live on one map.',
  'www.hero.book.headline': 'TODO(ta) Book a ride in seconds',
  'www.hero.book.sub':
    'TODO(ta) Pick a vehicle, see the fare before you agree, and follow the driver to ' +
    'your door.',
  'www.hero.drivers.headline': 'TODO(ta) Drivers keep 100%',
  'www.hero.drivers.sub':
    'TODO(ta) No commission on any fare. A flat daily fee from your second trip, and ' +
    'nothing on the days you do not drive.',
  'www.hero.deliver.headline': 'TODO(ta) Send a package across town',
  'www.hero.deliver.sub':
    'TODO(ta) Hand it to a driver, share a link, and watch it travel from pickup to ' +
    'doorstep.',
  'www.cta.getTheApp': 'TODO(ta) Get the app',
  'www.cta.seeHowItWorks': 'TODO(ta) See how it works',
  'www.cta.passengerGuide': 'TODO(ta) Read the passenger guide',
  'www.cta.driveWithUs': 'TODO(ta) Drive with MageRide',
  'www.cta.seeTheFees': 'TODO(ta) See the daily fee',
  'www.modes.a.name': 'TODO(ta) Mode A — Public transport',
  'www.modes.a.tagline': 'TODO(ta) Free to watch, always',
  'www.modes.a.body':
    'TODO(ta) Public buses and trains share their live position along fixed routes. ' +
    'Anyone can watch them: no fare to MageRide, no subscription, no permission needed. ' +
    'Operators pay nothing to appear, which is what makes this part of the map public ' +
    'infrastructure rather than a product.',
  'www.modes.b.name': 'TODO(ta) Mode B — Private vehicles you follow',
  'www.modes.b.tagline': 'TODO(ta) By permission only',
  'www.modes.b.body':
    'TODO(ta) A school van, an office staff bus, a vehicle a family shares. You see it ' +
    'only if its owner has granted you access, and they decide who gets it. Some are free ' +
    'to follow; others carry a monthly subscription. Nobody can watch a private vehicle ' +
    'without being let in.',
  'www.modes.c.name': 'TODO(ta) Mode C — Rides and deliveries',
  'www.modes.c.tagline': 'TODO(ta) On demand, fare shown upfront',
  'www.modes.c.body':
    'TODO(ta) Hail a motorbike, three-wheeler, car or van right now, or send a package ' +
    'across town. You see the fare before you book and pay the driver directly. This is ' +
    'the only mode with a per-trip fare — and MageRide takes none of it.',
  'www.how.p1.title': 'TODO(ta) Get the app',
  'www.how.p1.body':
    'TODO(ta) Install MageRide, choose your language, and sign in with your phone number ' +
    'and a one-time code.',
  'www.how.p2.title': 'TODO(ta) Open the map',
  'www.how.p2.body':
    'TODO(ta) See what is moving near you — buses, trains, and the private vehicles you ' +
    'are allowed to follow.',
  'www.how.p3.title': 'TODO(ta) Book a ride',
  'www.how.p3.body':
    'TODO(ta) Say where you are going, pick a vehicle, and see the fare before you ' +
    'confirm anything.',
  'www.how.p4.title': 'TODO(ta) Pay the driver',
  'www.how.p4.body':
    'TODO(ta) Cash, or scan the driver’s QR code at the end of the trip. The money goes ' +
    'to the driver, not through us.',
  'www.how.d1.title': 'TODO(ta) Register your vehicle',
  'www.how.d1.body':
    'TODO(ta) Four steps: the vehicle, insurance, revenue licence and photos. You ' +
    'photograph each document in the app.',
  'www.how.d2.title': 'TODO(ta) Get approved',
  'www.how.d2.body':
    'TODO(ta) Most details are read automatically. Anything unclear goes to a person to ' +
    'check, and you can follow the progress as it happens.',
  'www.how.d3.title': 'TODO(ta) Go on standby',
  'www.how.d3.body':
    'TODO(ta) Switch on when you want work. Set a direction if you are heading home and ' +
    'want jobs along the way.',
  'www.how.d4.title': 'TODO(ta) Accept a job',
  'www.how.d4.body':
    'TODO(ta) You get fifteen seconds to take a ride. Keep the whole fare; the daily fee ' +
    'comes off from your second trip.',
  'www.feature.liveMap.headline': 'TODO(ta) One map, ten kinds of vehicle',
  'www.feature.liveMap.body':
    'TODO(ta) Buses, trains, three-wheelers, motorbikes, cars, vans, mini vans, trucks ' +
    'and mini trucks each get their own colour, so a glance tells you what is coming. ' +
    'Filter by mode or by vehicle type when the map gets busy. It is the same map whether ' +
    'you are watching a bus or waiting for a ride you booked.',
  'www.feature.upfrontFare.headline': 'TODO(ta) The fare you see is the fare you pay',
  'www.feature.upfrontFare.body':
    'TODO(ta) Choose your vehicle and MageRide shows the price before you book. It does ' +
    'not climb while you wait, it does not surge because it is raining, and nothing is ' +
    'added at the end. The driver receives exactly that amount — MageRide takes none of ' +
    'it.',
  'www.feature.packages.headline': 'TODO(ta) Send a parcel across town',
  'www.feature.packages.body':
    'TODO(ta) Choose a size, name a recipient, and a driver collects it. The three stages ' +
    '— collected, on the way, delivered — are each confirmed with a code, and the person ' +
    'receiving it can follow the journey in a browser without installing anything or ' +
    'creating an account.',
  'www.feature.safety.headline': 'TODO(ta) Help is one press away',
  'www.feature.safety.body':
    'TODO(ta) Share your trip with someone you trust, call the driver from inside the ' +
    'app, and reach emergency help from the ride screen. Every trip is recorded against a ' +
    'driver whose licence and vehicle documents have been checked.',
  'www.feature.trilingual.headline': 'TODO(ta) In your language, from the start',
  'www.feature.trilingual.body':
    'TODO(ta) Sinhala, Tamil and English are all first-class. Choose your language before ' +
    'you even sign in, change it whenever you like, and every screen follows. No ' +
    'half-translated menus, no English fallback on the screen that matters.',
  'www.stats.vehicleTypes': 'TODO(ta) Vehicle types',
  'www.stats.languages': 'TODO(ta) Languages',
  'www.stats.commission': 'TODO(ta) Commission on your fare',
  'www.stats.firstTripFree': 'TODO(ta) Free trip every day, for drivers',
  'www.stats.percentSuffix': 'TODO(ta) %',
  'www.fees.tier.motorbike': 'TODO(ta) Motorbike',
  'www.fees.tier.threeWheeler': 'TODO(ta) Three-wheeler',
  'www.fees.tier.flex': 'TODO(ta) Flex',
  'www.fees.tier.sedan': 'TODO(ta) Sedan',
  'www.fees.tier.miniVan': 'TODO(ta) Mini van',
  'www.fees.tier.van': 'TODO(ta) Van',
  'www.fees.modeA':
    'TODO(ta) Public buses and trains pay nothing at all. Mode A is free to run and free ' +
    'to watch.',
  'www.fees.modeB':
    'TODO(ta) Private vehicles pay a monthly charge instead of a daily one — currently ' +
    'around Rs 300 per vehicle, with the first month free.',
  'www.languageBand.si': 'TODO(ta) ශ්‍රී ලංකාව ගමන් කරන ආකාරය, එක් සජීවී සිතියමක.',
  'www.languageBand.ta': 'TODO(ta) இலங்கை பயணிக்கும் விதம், ஒரே நேரடி வரைபடத்தில்.',
  'www.languageBand.en': 'TODO(ta) How Sri Lanka moves, on one live map.',
  'www.footer.explore': 'TODO(ta) Explore',
  'www.footer.support': 'TODO(ta) Support',
  'www.footer.legal': 'TODO(ta) Legal',
  'www.footer.rights': 'TODO(ta) © MageRide',
  'www.footer.madeIn': 'TODO(ta) Built in Sri Lanka, for Sri Lanka.',
  'www.faq.passengerCost.q': 'TODO(ta) What does MageRide cost me as a passenger?',
  'www.faq.passengerCost.a':
    'TODO(ta) Nothing. There is no subscription, no premium tier and no booking fee. You ' +
    'pay the driver the fare you saw before booking, and you pay MageRide nothing at all.',
  'www.faq.whyFree.q':
    'TODO(ta) If passengers pay nothing and drivers keep the whole fare, how does ' +
    'MageRide earn?',
  'www.faq.whyFree.a':
    'TODO(ta) Drivers taking on-demand rides pay a flat daily platform fee — but only ' +
    'from their second trip of the day, and nothing on days they do not drive. That fee ' +
    'is the entire business model. There is no commission on any fare and no charge to ' +
    'passengers.',
  'www.faq.driverKeeps.q': 'TODO(ta) How much of the fare does a driver keep?',
  'www.faq.driverKeeps.a':
    'TODO(ta) All of it. MageRide takes no commission. The fare shown to the passenger ' +
    'before booking is exactly what the driver receives, and for card payments the ' +
    'gateway settles straight to the driver’s own account — MageRide never holds ride ' +
    'money.',
  'www.faq.dailyFee.q': 'TODO(ta) What is the daily platform fee?',
  'www.faq.dailyFee.a':
    'TODO(ta) A flat daily amount an on-demand driver pays from their second trip onward, ' +
    'set by vehicle type. Once it is paid, the rest of that day’s trips are unlimited and ' +
    'MageRide takes nothing further. The current rates are listed on the drivers page.',
  'www.faq.feeOffDays.q': 'TODO(ta) Do drivers pay on days they do not work?',
  'www.faq.feeOffDays.a':
    'TODO(ta) No. The fee is charged only on a day a driver takes a second trip. No ' +
    'trips, no fee. One trip, no fee.',
  'www.faq.howToPay.q': 'TODO(ta) How do I pay for a ride?',
  'www.faq.howToPay.a':
    'TODO(ta) Cash, or by scanning the driver’s QR code at the end of the trip. You ' +
    'choose before you book. Either way the money goes to the driver, not to MageRide.',
  'www.faq.walletTopUp.q': 'TODO(ta) How does a driver top up their wallet?',
  'www.faq.walletTopUp.a':
    'TODO(ta) Inside the driver app, using a credit or debit card, OnePay, or LankaQR. ' +
    'Drivers never need to open a web portal for anything.',
  'www.faq.coverage.q': 'TODO(ta) Will I see every bus and every three-wheeler?',
  'www.faq.coverage.a':
    'TODO(ta) Not on day one. MageRide is built to hold the whole country, but a vehicle ' +
    'appears only once its operator or driver has joined. Coverage grows steadily, and ' +
    'for a while it will be thinner in some places than others. We would rather say so ' +
    'plainly than let the map imply otherwise.',
  'www.faq.vehicleTypes.q': 'TODO(ta) What kinds of vehicle are on MageRide?',
  'www.faq.vehicleTypes.a':
    'TODO(ta) Ten. Motorbike, three-wheeler, Flex, sedan, mini van and van for rides; ' +
    'truck and mini truck additionally for deliveries; and buses and trains for public ' +
    'transport. Each has its own colour on the map.',
  'www.faq.modes.q': 'TODO(ta) What are Mode A, Mode B and Mode C?',
  'www.faq.modes.a':
    'TODO(ta) Three different things you can do. Mode A is public buses and trains, free ' +
    'for anyone to watch. Mode B is a private vehicle you have been given permission to ' +
    'follow. Mode C is booking a ride or a delivery right now. They are separate ' +
    'services, not one feature with a switch on it.',
  'www.faq.modeBAccess.q': 'TODO(ta) Can anyone watch my private vehicle?',
  'www.faq.modeBAccess.a':
    'TODO(ta) No. A Mode B vehicle is visible only to people its owner has granted access ' +
    'to, one request at a time, and the owner can withdraw that access whenever they ' +
    'choose.',
  'www.faq.modeBPrice.q': 'TODO(ta) What does following a private vehicle cost?',
  'www.faq.modeBPrice.a':
    'TODO(ta) It depends on the vehicle. Some owners share access free — a company staff ' +
    'bus, for instance. Others carry a monthly subscription, currently around Rs 300 per ' +
    'vehicle with the first month free. The app shows the exact amount before you ' +
    'subscribe.',
  'www.faq.trains.q': 'TODO(ta) Are trains on the map?',
  'www.faq.trains.a':
    'TODO(ta) Yes, as Mode A alongside public buses. Trains are registered by MageRide ' +
    'administrators rather than by drivers. You can filter for them on their own, or see ' +
    'them among your options when you enter a destination.',
  'www.faq.signup.q': 'TODO(ta) What do I need to sign up?',
  'www.faq.signup.a':
    'TODO(ta) A Sri Lankan mobile number. Choose your language, enter your number, and ' +
    'confirm the one-time code. Passengers need nothing else.',
  'www.faq.becomeADriver.q': 'TODO(ta) How do I start driving with MageRide?',
  'www.faq.becomeADriver.a':
    'TODO(ta) Install the driver app, sign in with your phone number, and register your ' +
    'vehicle in four steps: the vehicle, insurance, revenue licence and photos. You ' +
    'photograph each document in the app — most details are read automatically, and ' +
    'anything unclear is checked by a person before approval.',
  'www.faq.languages.q': 'TODO(ta) Which languages does MageRide support?',
  'www.faq.languages.a':
    'TODO(ta) Sinhala, Tamil and English, everywhere. You pick one before signing in and ' +
    'can change it at any time.',
  'www.faq.accessibility.q':
    'TODO(ta) Does MageRide work with a screen reader, or with larger text?',
  'www.faq.accessibility.a':
    'TODO(ta) Yes. The flows that matter most — signing up, the map, booking a ride and ' +
    'the trip summary — work with TalkBack, the screen reader built into Android. And if ' +
    'you have made the text bigger in your phone’s settings, the app follows that ' +
    'setting: the layout grows with it rather than cutting words off.',
  'www.faq.group.everyone': 'TODO(ta) Everyone asks these',
  'www.faq.safety.q': 'TODO(ta) What safety features are there?',
  'www.faq.safety.a':
    'TODO(ta) Share your live trip with someone you trust, reach emergency help from ' +
    'inside the ride screen, and rate the driver afterwards. Every driver’s licence and ' +
    'vehicle documents are checked before they can accept a job.',
  'www.faq.phoneNumber.q': 'TODO(ta) Will the driver see my phone number?',
  'www.faq.phoneNumber.a':
    'TODO(ta) Once a driver accepts your ride, you and the driver can see each other’s ' +
    'numbers so you can coordinate the pickup. This is disclosed when you sign up. If you ' +
    'book on someone else’s behalf, the driver sees the rider’s number and never yours.',
  'www.faq.myData.q': 'TODO(ta) Can I get my data, or have it deleted?',
  'www.faq.myData.a':
    'TODO(ta) Both. You can ask for a copy of everything MageRide holds about you and ' +
    'receive it within 30 days, and you can ask for your account and personal data to be ' +
    'erased, also within 30 days — except for records the law requires us to keep.',
  'www.faq.maps.q': 'TODO(ta) Whose maps does MageRide use?',
  'www.faq.maps.a':
    'TODO(ta) OpenStreetMap data, served from MageRide’s own map and search servers. ' +
    'There is no commercial map licence and no per-user fee, which keeps that cost off ' +
    'drivers.',

  // =========================================================================
  // S07 · marketing corpus — awaiting translation.
  //
  // Every value below is the English string prefixed `TODO(ta)`. The keys must exist
  // or the build fails (en.ts defines the key type), so "not translated yet" is spelled
  // as a visible marker rather than as a missing key. `check-i18n-parity.mjs` counts
  // these in every build; S13 replaces them and the count reaches zero.
  // =========================================================================
  'www.home.modes.heading': 'TODO(ta) Three ways to use MageRide',
  'www.home.modes.intro':
    'TODO(ta) Public transport you can watch, private vehicles you are allowed to follow, ' +
    'and rides and deliveries on demand. They are separate services — you might use one ' +
    'of them and never the others.',
  'www.home.how.heading': 'TODO(ta) How it works',
  'www.home.how.passengerTab': 'TODO(ta) For passengers',
  'www.home.how.driverTab': 'TODO(ta) For drivers',
  'www.home.values.heading': 'TODO(ta) What we will not do',
  'www.home.values.intro':
    'TODO(ta) Most of what makes MageRide different is a list of things it does not ' +
    'charge you for.',
  'www.home.screens.heading': 'TODO(ta) What it looks like',
  'www.home.faq.heading': 'TODO(ta) Questions people ask first',
  'www.home.faq.more': 'TODO(ta) See all questions',
  'www.page.vision.title': 'TODO(ta) Our vision',
  'www.page.vision.intro':
    'TODO(ta) Why MageRide exists, what it is trying to be, and what it is not yet.',
  'www.page.vision.missionHeading': 'TODO(ta) Our mission',
  'www.page.vision.valuesHeading': 'TODO(ta) What that means in practice',
  'www.page.passengers.title': 'TODO(ta) For passengers',
  'www.page.passengers.intro':
    'TODO(ta) Track what is moving, book a ride at a price you agreed to, and send a ' +
    'package across town. MageRide costs you nothing.',
  'www.page.passengers.trackHeading': 'TODO(ta) See what is coming',
  'www.page.passengers.trackBody':
    'TODO(ta) Open the map and watch buses and trains move along their routes. If someone ' +
    'has given you access to a private vehicle — a school van, a staff bus — it appears ' +
    'there too. Filter by mode or vehicle type when there is a lot happening.',
  'www.page.passengers.bookHeading': 'TODO(ta) Book at a price you agreed to',
  'www.page.passengers.bookBody':
    'TODO(ta) Enter where you are going, pick a vehicle type, and MageRide shows the fare ' +
    'before you commit. Pay the driver in cash or by scanning their code. Nothing is ' +
    'added afterwards and no part of it comes to us.',
  'www.page.passengers.sendHeading': 'TODO(ta) Send something across town',
  'www.page.passengers.sendBody':
    'TODO(ta) Choose a size, name who is receiving it, and a driver collects it. Each ' +
    'stage is confirmed with a code, and the recipient can follow the journey in a ' +
    'browser without installing the app.',
  'www.page.passengers.costHeading': 'TODO(ta) What it costs you',
  'www.page.passengers.costBody':
    'TODO(ta) For rides and deliveries, the fare — paid to the driver. For public ' +
    'transport, nothing. For a private vehicle, whatever its owner has set, shown before ' +
    'you subscribe. MageRide itself charges passengers nothing at all.',
  'www.page.passengers.guideCta': 'TODO(ta) Read the full passenger guide',
  'www.page.drivers.title': 'TODO(ta) For drivers',
  'www.page.drivers.intro':
    'TODO(ta) Keep every rupee of every fare. Pay a flat daily fee from your second trip, ' +
    'and nothing on the days you do not drive.',
  'www.page.drivers.earnHeading': 'TODO(ta) What you keep',
  'www.page.drivers.earnBody':
    'TODO(ta) All of it. There is no commission on any fare, no per-trip cut, and no ' +
    'service charge taken off the top. The number the passenger agreed to is the number ' +
    'you receive.',
  'www.page.drivers.feeHeading': 'TODO(ta) What you pay',
  'www.page.drivers.feeBody':
    'TODO(ta) One flat platform fee per day, and only on a day you take a second trip. ' +
    'Your first trip each day is always free, and after the fee is paid the rest of the ' +
    'day is unlimited. The rate depends on what you drive.',
  'www.page.drivers.feeTableHeading': 'TODO(ta) The daily fee, by vehicle',
  'www.page.drivers.feeTableNote':
    'TODO(ta) Rates are reviewed by MageRide and can change; the app always shows the ' +
    'current one.',
  'www.page.drivers.startHeading': 'TODO(ta) Getting started',
  'www.page.drivers.startBody':
    'TODO(ta) Register your vehicle in the app: the vehicle itself, insurance, revenue ' +
    'licence and photos. Most of each document is read automatically, anything unclear is ' +
    'checked by a person, and you can see exactly which step you are on.',
  'www.page.drivers.directionalHeading': 'TODO(ta) Driving home? Say so',
  'www.page.drivers.directionalBody':
    'TODO(ta) Set a direction at the end of a shift and you will only be offered jobs ' +
    'heading that way, for a limited time and a limited number of times a day. It is ' +
    'there so the last trip of the day does not take you further from home.',
  'www.page.drivers.guideCta': 'TODO(ta) Read the full driver guide',

  'www.page.drivers.freeFirstTripQuote':
    'TODO(ta) For Mode C (Standby On-Demand) drivers, the first trip of the day is ' +
    'always free; from the 2nd trip, a flat daily platform fee (vehicle-type ' +
    'dependent…) is auto-deducted from their wallet.',

  'www.page.fleets.guideCta': 'TODO(ta) Read the fleet owner guide',

  // The guide (S17).
  'www.guide.stepCount': 'TODO(ta) {count} steps',
  'www.guide.chapterNumber': 'TODO(ta) Chapter {number}',
  'www.guide.rail.label': 'TODO(ta) Chapters in this guide',
  'www.guide.rail.heading': 'TODO(ta) In this guide',
  'www.guide.toc.label': 'TODO(ta) Steps in this chapter',
  'www.guide.stepLabel': 'TODO(ta) Step {number}',
  'www.guide.related': 'TODO(ta) Read next',
  'www.guide.questions': 'TODO(ta) Questions about this',
  'www.guide.pager.label': 'TODO(ta) Chapter navigation',
  'www.guide.backToGuide': 'TODO(ta) All chapters',

  'www.guide.callout.tip': 'TODO(ta) Tip',
  'www.guide.callout.warning': 'TODO(ta) Careful',
  'www.guide.callout.fee': 'TODO(ta) What this costs',
  'www.guide.callout.privacy': 'TODO(ta) Your privacy',
  'www.page.fleets.title': 'TODO(ta) For fleet owners',
  'www.page.fleets.intro':
    'TODO(ta) Run a school van service, a staff transport operation or a bus route from ' +
    'one place — vehicles, drivers, trackers and billing.',
  'www.page.fleets.manageHeading': 'TODO(ta) Your whole fleet on one screen',
  'www.page.fleets.manageBody':
    'TODO(ta) Add vehicles one at a time or in bulk, assign the drivers who drive them, ' +
    'bind GPS trackers, and see every vehicle live on a single map — scoped to your ' +
    'organisation and nobody else’s.',
  'www.page.fleets.accessHeading': 'TODO(ta) You decide who can watch',
  'www.page.fleets.accessBody':
    'TODO(ta) A private vehicle is visible only to the people you have approved. Requests ' +
    'come to you, and you can withdraw access at any time.',
  'www.page.fleets.billingHeading': 'TODO(ta) How fleets are billed',
  'www.page.fleets.billingBody':
    'TODO(ta) Public transport vehicles are free. Private vehicles are billed monthly per ' +
    'vehicle. On-demand driving is never billed to a fleet — that daily fee comes from ' +
    'the individual driver’s own wallet.',
  'www.page.fleets.portalNote':
    'TODO(ta) Fleet owners work in a web portal at fleet.mageride.lk. Drivers never need ' +
    'it.',
  'www.page.screens.title': 'TODO(ta) Screen by screen',
  'www.page.screens.intro':
    'TODO(ta) What MageRide actually looks like, for passengers, drivers and fleet ' +
    'owners.',
  'www.page.screens.passengerHeading': 'TODO(ta) The passenger app',
  'www.page.screens.driverHeading': 'TODO(ta) The driver app',
  'www.page.screens.fleetHeading': 'TODO(ta) The fleet portal',
  'www.page.screens.webHeading': 'TODO(ta) Tracking without the app',

  // The gallery filter (S18).
  'www.screens.filter.legend': 'TODO(ta) Narrow these down',
  'www.screens.filter.surface': 'TODO(ta) App',
  'www.screens.filter.mode': 'TODO(ta) Service',
  'www.screens.filter.chapter': 'TODO(ta) Guide chapter',
  'www.screens.filter.showing': 'TODO(ta) Showing {count} of {total} screens',
  'www.screens.filter.clear': 'TODO(ta) Show every screen',
  'www.screens.empty': 'TODO(ta) No screen matches all three of those.',
  'www.screens.tile.inGuide': 'TODO(ta) In the guide:',
  'www.page.guide.title': 'TODO(ta) How to use MageRide',
  'www.page.guide.intro':
    'TODO(ta) Step-by-step guides for passengers and drivers, from installing the app to ' +
    'getting paid.',
  'www.page.guide.passengerHeading': 'TODO(ta) Passenger guide',
  'www.page.guide.driverHeading': 'TODO(ta) Driver guide',
  'www.page.guide.fleetHeading': 'TODO(ta) Fleet owner guide',
  'www.page.guide.chapterCount': 'TODO(ta) {count} chapters',
  'www.page.guide.readChapter': 'TODO(ta) Read this chapter',
  'www.page.faq.title': 'TODO(ta) Questions',
  'www.page.faq.intro':
    'TODO(ta) The things people ask before they trust an app with a journey or a ' +
    'livelihood. If your question is not here, the guides go into more detail.',
  'www.page.download.title': 'TODO(ta) Get the app',
  'www.page.download.intro':
    'TODO(ta) MageRide runs on Android and iPhone, in Sinhala, Tamil and English.',
  'www.page.download.notYet': 'TODO(ta) Not in the stores yet',
  'www.page.download.notYetBody':
    'TODO(ta) MageRide has not launched publicly. When the apps are published, the links ' +
    'will appear here — there is nothing to install today, and we would rather say so ' +
    'than collect your details for a list.',
  'www.page.download.passengerApp': 'TODO(ta) MageRide — Passenger',
  'www.page.download.driverApp': 'TODO(ta) MageRide — Driver',
  'www.page.download.whichAppHeading': 'TODO(ta) Which app do you want?',
  'www.page.download.passengerAppBody':
    'TODO(ta) For travelling: watch buses and trains, follow a vehicle you have been given ' +
    'access to, book a ride, or send a package. This is the one most people want.',
  'www.page.download.driverAppBody':
    'TODO(ta) For earning: take ride and delivery jobs, or carry passengers on a bus, van ' +
    'or school run. You register your vehicle and documents inside it.',
  'www.page.download.requirementsHeading': 'TODO(ta) What you need',
  'www.page.download.androidMinimum':
    'TODO(ta) On Android, MageRide needs Android 8.0 or newer. A data connection and ' +
    'location permission are needed for the map; the app tells you what each permission ' +
    'is for before it asks.',
  'www.page.contact.title': 'TODO(ta) Contact',
  'www.page.contact.intro':
    'TODO(ta) MageRide has no call centre. Support for anything to do with a trip lives ' +
    'inside the app, where we can see the trip you mean.',
  'www.page.contact.inAppHeading': 'TODO(ta) Support inside the app',
  'www.page.contact.inAppBody':
    'TODO(ta) Open Help from the menu and raise a ticket. It arrives attached to your ' +
    'account and your trip history, which is what lets somebody actually answer it.',
  'www.page.contact.questionsHeading': 'TODO(ta) Most answers are already written down',
  'www.page.contact.questionsBody':
    'TODO(ta) Fares, the daily fee, coverage, safety and what happens to your data all have ' +
    'answers here. The guides go step by step through everything the apps do.',
  'www.page.contact.emailHeading': 'TODO(ta) Everything else',
  'www.page.contact.emailBody':
    'TODO(ta) Press, partnerships, fleet enquiries and anything that is not about a ' +
    'specific trip.',
  'www.page.contact.emailPending':
    'TODO(ta) There is no published address for these yet. Rather than print one that ' +
    'nobody reads, we have left it out — it will be here when it is decided.',

  // The three legal documents (S18). MCS-34 D5 — no session in C134 authors legal
  // text; these are the shell and two factual descriptions of software.
  'www.legal.lastUpdatedLabel': 'TODO(ta) Last updated',
  'www.legal.lastUpdatedNone': 'TODO(ta) not yet published',
  'www.legal.status.heading': 'TODO(ta) This document is being prepared',

  'www.legal.terms.intro': 'TODO(ta) The terms you agree to when you use MageRide.',
  'www.legal.terms.status':
    'TODO(ta) MageRide’s terms of service are being written and reviewed. They are not ' +
    'published yet, and we would rather show you nothing here than borrowed wording that ' +
    'describes a different company. They will be published before the apps are, and you ' +
    'will be asked to agree to them when you sign up.',

  'www.legal.privacy.intro':
    'TODO(ta) What MageRide does with information about you — and what this website does, ' +
    'which is almost nothing.',
  'www.legal.privacy.status':
    'TODO(ta) The full privacy policy is being written and reviewed, and it is not ' +
    'published yet. The two sections below are not that policy: they are a plain ' +
    'description of how this website and the MageRide apps behave today, and they will ' +
    'still be true when the policy arrives.',
  'www.legal.privacy.siteHeading': 'TODO(ta) What this website collects',
  'www.legal.privacy.siteBody':
    'TODO(ta) Nothing. This site sets no cookies, runs no analytics, has no form on any ' +
    'page, and sends nothing you do here to anybody. There is no consent banner because ' +
    'there is nothing to consent to.',
  'www.legal.privacy.siteLogs':
    'TODO(ta) The server that hands you these pages keeps ordinary web-server records — ' +
    'the address requested, the time, and the internet address it was requested from — in ' +
    'the same way every web server does. Nothing beyond that is kept, and nothing here is ' +
    'joined to a MageRide account.',
  'www.legal.privacy.siteTheme':
    'TODO(ta) If you switch this site between light and dark, that choice is remembered by ' +
    'your own browser so the page does not flash the wrong way round next time. It never ' +
    'leaves your device.',
  'www.legal.privacy.appsHeading': 'TODO(ta) The apps are a different matter',
  'www.legal.privacy.appsBody':
    'TODO(ta) The passenger and driver apps do hold personal information — your phone ' +
    'number, your trips, and for drivers your licence and vehicle documents. They need it ' +
    'to work. What is held, for how long, and who can see it is what the policy above will ' +
    'set out, and your rights over it are on the data rights page.',

  'www.legal.pdpa.intro':
    'TODO(ta) What you can ask MageRide to do with the information it holds about you, ' +
    'under Sri Lanka’s Personal Data Protection Act.',
  'www.legal.pdpa.status':
    'TODO(ta) The formal data-protection notice is being written and reviewed. The two ' +
    'sections below describe what the platform already does, so that this page is useful ' +
    'before that notice is published rather than after.',
  'www.legal.pdpa.rightsHeading': 'TODO(ta) A copy of your data, or its erasure',
  'www.legal.pdpa.rightsBody':
    'TODO(ta) You can ask for a copy of everything MageRide holds about you, and you can ' +
    'ask for your account and personal data to be erased. Either request is due within 30 ' +
    'days of being made, and MageRide tracks that deadline rather than treating it as a ' +
    'target.',
  'www.legal.pdpa.rightsExceptions':
    'TODO(ta) Erasure has one limit, and it is worth stating plainly: records the law ' +
    'requires MageRide to keep — the financial ones, mainly — are kept. Everything that is ' +
    'not required is removed.',
  'www.legal.pdpa.howHeading': 'TODO(ta) How to ask',
  'www.legal.pdpa.howBody':
    'TODO(ta) From inside the app, under settings. It is there rather than here because a ' +
    'request has to be tied to an account, and the app already knows which account you are ' +
    'signed in to. A form on this page could not know that, and asking you to prove who ' +
    'you are by email would mean collecting more about you in order to give you less.',
  'www.common.learnMore': 'TODO(ta) Learn more',
  'www.common.backToTop': 'TODO(ta) Back to top',
  'www.common.onThisPage': 'TODO(ta) On this page',
  'www.common.sourceLabel': 'TODO(ta) Source',
  'www.common.previous': 'TODO(ta) Previous',
  'www.common.next': 'TODO(ta) Next',

  // =========================================================================
  // S08 · passenger guide 1–8 — awaiting translation.
  //
  // Same arrangement as the S07 block above: the English string behind a
  // `TODO(ta)` marker, so the key exists, the build stays green and the debt is
  // visible in every `check-i18n-parity.mjs` run. S13 replaces these.
  // =========================================================================

  // Chapter 1 · Install MageRide and sign in
  'www.guide.p01.title': 'TODO(ta) Install MageRide and sign in',
  'www.guide.p01.summary':
    'TODO(ta) The first few minutes: choosing the language you want to read, proving your ' +
    'phone number with a code, and adding your name. There is no password to invent and ' +
    'no email address to give.',
  'www.guide.p01.step1':
    'TODO(ta) Open MageRide for the first time and three short slides introduce what it ' +
    'does. Under them is the language chooser — three boxes stacked one per row, Sinhala ' +
    'at the top, then Tamil, then English, with Sinhala already selected. Tap the one you ' +
    'read, then Get Started at the bottom of the screen.',
  'www.guide.p01.step2':
    'TODO(ta) Enter your mobile number. The field already carries +94, so you type the ' +
    'nine digits after it. Tap Continue and MageRide sends a six-digit code by text ' +
    'message.',
  'www.guide.p01.step3':
    'TODO(ta) Type the code into the six boxes. Most Android phones fill it in for you. ' +
    'If it does not arrive, the Resend link becomes tappable after sixty seconds — you ' +
    'can ask for at most five codes in an hour.',
  'www.guide.p01.step3.note':
    'TODO(ta) A wrong code turns the boxes red and says so. Nothing is lost; type it ' +
    'again.',
  'www.guide.p01.step4':
    'TODO(ta) Add your name, and a photo if you want one. This screen is also where you ' +
    'say whether MageRide may send you notifications. Tap Save and continue.',
  'www.guide.p01.step5':
    'TODO(ta) MageRide then asks to use your location and explains why before your ' +
    'phone’s own permission box appears. Chapter 2 goes through that screen properly.',
  'www.guide.p01.step6':
    'TODO(ta) The live map opens, centred on where you are. That is the home screen, and ' +
    'everything else in this guide starts from it.',
  'www.guide.p01.callout.notPublished':
    'TODO(ta) MageRide has not been published to the app stores yet, so there is nothing ' +
    'to install today. This guide describes the app as it has been designed and approved; ' +
    'when the apps are released, the download page will say so.',
  'www.guide.p01.callout.phoneOnly':
    'TODO(ta) A phone number and a code are the only way into the passenger and driver ' +
    'apps. There is no password, no email address and no Google or Apple sign-in — those ' +
    'exist only for the fleet and administration web portals, which a passenger never ' +
    'needs.',
  'www.guide.p01.callout.oneDevice':
    'TODO(ta) One phone at a time. Signing in to the passenger app on a new phone signs ' +
    'the old one out immediately, and that phone can reach nothing until somebody signs ' +
    'in again. The driver app counts separately, so one person can run both.',

  // Chapter 2 · The permissions MageRide asks for
  'www.guide.p02.title': 'TODO(ta) The permissions MageRide asks for',
  'www.guide.p02.summary':
    'TODO(ta) The passenger app asks for one thing: your location, and only while you are ' +
    'using it. This chapter is what that permission does, what it does not do, and how to ' +
    'change your mind afterwards.',
  'www.guide.p02.step1':
    'TODO(ta) Before your phone asks anything, MageRide shows a screen of its own saying ' +
    'why it wants your location — to show what is moving near you, and to set the point a ' +
    'driver comes to. Nothing has been requested at this stage.',
  'www.guide.p02.step2':
    'TODO(ta) Tap Allow location. Your phone then shows its own permission box, the one ' +
    'no app can reword, and the choice is made there.',
  'www.guide.p02.step3':
    'TODO(ta) Choose the option that allows it while you are using the app. That is what ' +
    'the passenger app asks for: precise location in the foreground on Android, When In ' +
    'Use on an iPhone.',
  'www.guide.p02.step4':
    'TODO(ta) If you tap Not now, or refuse it by accident, MageRide cannot ask you again ' +
    'itself — no app can. It shows an Open Settings link instead, which takes you to the ' +
    'right page of your phone’s settings.',
  'www.guide.p02.step5':
    'TODO(ta) Without it the map has nowhere to centre and no pickup point to start from, ' +
    'so this is the one permission the app genuinely needs. With it, your own position ' +
    'shows as a blue dot inside a circle marking how precise your phone is being, and the ' +
    'map follows you as you move.',
  'www.guide.p02.step6':
    'TODO(ta) Notifications are a preference rather than something MageRide holds over ' +
    'you. You set it while creating your profile and change it in Profile and settings. ' +
    'They are what tell you that a driver has accepted, that the driver has arrived, that ' +
    'the trip has started, that a payment went through, or that a ride you booked in ' +
    'advance is coming up.',
  'www.guide.p02.step7':
    'TODO(ta) The only other thing the app may ask for is your contact list, and only if ' +
    'you tap the contact picker while booking a ride for somebody else. Type the name and ' +
    'number in yourself and it never asks.',
  'www.guide.p02.callout.noBackground':
    'TODO(ta) The passenger app does not ask for background location. When the app is ' +
    'closed it is not reporting where you are. The driver app is the opposite and says so ' +
    'on its own permission screen — a driver’s position is the thing the live map is made ' +
    'of.',
  'www.guide.p02.callout.reenable':
    'TODO(ta) Changed your mind? Nothing inside MageRide can turn a permission back on. ' +
    'Only your phone’s settings can, and the app’s Open Settings link is the short way ' +
    'there.',

  // Chapter 3 · Reading the live map
  'www.guide.p03.title': 'TODO(ta) Reading the live map',
  'www.guide.p03.summary':
    'TODO(ta) The map is the home screen, and nearly everything on it is a colour with a ' +
    'meaning: ten vehicle types with a colour each, a grey marker for a private vehicle, ' +
    'and three mode badges that are a different set of colours again.',
  'www.guide.p03.step1':
    'TODO(ta) The map opens on you. Every marker is a real vehicle sending its position, ' +
    'and each one points the way it is travelling. The platform is built to have a ' +
    'vehicle’s movement on your screen within two to eight seconds.',
  'www.guide.p03.step2':
    'TODO(ta) Colour says what kind of vehicle it is, and the same colours are used on ' +
    'the markers, on the filter chips and on the fare cards. Buses are green and trains ' +
    'red — the two public types. Motorbikes are purple, three-wheelers yellow, Flex teal, ' +
    'sedans blue, mini vans pink and vans orange. Trucks are brown and mini trucks olive; ' +
    'those two carry packages rather than people.',
  'www.guide.p03.step3':
    'TODO(ta) Tap a bus or a train and a panel slides up with its route, how far away it ' +
    'is, when it should reach you, its registration number and the driver’s name and ' +
    'photo.',
  'www.guide.p03.step4':
    'TODO(ta) A grey marker is a private vehicle. Grey is the eleventh colour on the map ' +
    'and the one that is not a vehicle type — it says “this is a Mode B vehicle”, not ' +
    '“this is a particular kind of van”. Tapping one does not open the panel above; it ' +
    'opens the screen where you ask its owner for permission, which is chapter 5.',
  'www.guide.p03.step5':
    'TODO(ta) Zoom out and markers that are close together gather into one cluster you ' +
    'can zoom back into, so a city does not become a wall of pins.',
  'www.guide.p03.step6':
    'TODO(ta) The filter button at the top right has the three modes — public transport, ' +
    'private vehicles and on-demand vehicles on standby — and a chip per vehicle type ' +
    'carrying the same coloured icon as its marker. Turn off what you do not want and the ' +
    'map redraws at once.',
  'www.guide.p03.step7':
    'TODO(ta) Two things are missing on purpose. An on-demand vehicle already carrying ' +
    'somebody is not on the public map. And a vehicle that stops sending its position is ' +
    'removed rather than left where it was last seen, so a marker is always a vehicle ' +
    'that is really there.',
  'www.guide.p03.step8':
    'TODO(ta) If your connection drops, the markers dim and a banner tells you that you ' +
    'are looking at the last known positions until it comes back.',
  'www.guide.p03.callout.modeBadges':
    'TODO(ta) The mode badges are a separate set of three colours — green for public, ' +
    'grey for private, orange for on-demand — and they label the mode rather than the ' +
    'vehicle. A green badge and a green bus marker are not saying the same thing twice.',
  'www.guide.p03.callout.coverage':
    'TODO(ta) If nothing of the type you asked for is nearby, the app says so in a line ' +
    'of text rather than leaving you with an empty map. And an empty map means nobody ' +
    'near you has joined MageRide yet, not that nothing is moving.',

  // Chapter 4 · Tracking a bus or a train
  'www.guide.p04.title': 'TODO(ta) Tracking a bus or a train',
  'www.guide.p04.summary':
    'TODO(ta) Public buses and trains are Mode A: free to watch, with nothing to book and ' +
    'nothing to pay. There are two ways to find one — tap it on the map, or say where you ' +
    'are going and let MageRide list the routes that get you there.',
  'www.guide.p04.step1':
    'TODO(ta) The quick way is to tap a green bus or a red train on the map. The panel ' +
    'gives you its route, how far away it is, when it should arrive, its registration and ' +
    'its driver.',
  'www.guide.p04.step2':
    'TODO(ta) The other way starts at “Where to?”. Type a place or an address. You cannot ' +
    'type a route number here, because MageRide works the routes out from your ' +
    'destination rather than the other way round.',
  'www.guide.p04.step3':
    'TODO(ta) The next screen lists every direct public route that reaches it, each with ' +
    'its route number, a description of where it runs, and a label marking it as public ' +
    'transport. Routes that need a change are listed underneath and tagged as such.',
  'www.guide.p04.step4':
    'TODO(ta) Choose a route and the map zooms out and draws it in that vehicle’s colour, ' +
    'with the route’s arrival time. If you are not standing on the route, a dashed blue ' +
    'line shows the walk to the nearest halt and how far it is.',
  'www.guide.p04.step5':
    'TODO(ta) Tap Track Route and the map follows that route for you. A public route has ' +
    'no Book button and no fare — Mode A is something you watch, not something you buy.',
  'www.guide.p04.step6':
    'TODO(ta) If the bus you are following stops sending its position, its marker shows ' +
    'when it was last seen and then goes. Track another vehicle on the same route.',
  'www.guide.p04.callout.free':
    'TODO(ta) Nothing about Mode A costs anything. Passengers pay nothing to watch, and ' +
    'operators pay MageRide nothing to appear — buses and trains are the two vehicle ' +
    'types with no daily platform fee at all.',
  'www.guide.p04.callout.gtfs':
    'TODO(ta) Which routes MageRide can list depends on the timetable data it has been ' +
    'given. Route information comes from a national public-transport data file that ' +
    'administrators load and refresh; a route missing from that file cannot be listed ' +
    'however many buses run it, and a route that is in it with nobody reporting shows you ' +
    'a line on the map and no vehicle on it.',
  'www.guide.p04.callout.trains':
    'TODO(ta) Trains are on the map on the same footing as buses, and you can filter for ' +
    'them on their own. They are registered by MageRide administrators rather than by ' +
    'drivers.',

  // Chapter 5 · Following a private vehicle
  'www.guide.p05.title': 'TODO(ta) Following a private vehicle',
  'www.guide.p05.summary':
    'TODO(ta) A school van, a staff bus, a vehicle a family shares. Mode B is a vehicle ' +
    'you can follow only because its owner has let you in, and this chapter is how you ' +
    'ask.',
  'www.guide.p05.step1':
    'TODO(ta) Private vehicles appear on the map as grey markers. Tap one and MageRide ' +
    'opens the access request with that vehicle’s ID already filled in.',
  'www.guide.p05.step2':
    'TODO(ta) You can also reach the same screen from the menu, under Private transport, ' +
    'and type the Vehicle ID in yourself — which is what you do when the van is not on ' +
    'your screen at the time.',
  'www.guide.p05.step3':
    'TODO(ta) Send the request. It goes to that vehicle’s owner, or to the driver ' +
    'assigned to it, and it shows them your name and your mobile number so they can tell ' +
    'who is asking.',
  'www.guide.p05.step4':
    'TODO(ta) The screen then shows where the request stands: Pending while you wait, ' +
    'then Accepted or Rejected. Nothing about the vehicle is visible to you until it says ' +
    'Accepted.',
  'www.guide.p05.step5':
    'TODO(ta) Once it is accepted the vehicle appears on your map and you follow it like ' +
    'any other — watch the van leave the school, watch the staff bus come down the road. ' +
    'Tap it to see where it is and which way it is going.',
  'www.guide.p05.step6':
    'TODO(ta) Access is granted per vehicle rather than per operator. If a fleet runs six ' +
    'vans you ask for the one your child travels on, and the owner deals with that ' +
    'vehicle’s requests under that vehicle.',
  'www.guide.p05.step7':
    'TODO(ta) The owner can withdraw access whenever they choose and it takes effect at ' +
    'once — the vehicle simply stops being on your map. Getting it back means a new ' +
    'request that they have to accept.',
  'www.guide.p05.step8':
    'TODO(ta) A private vehicle publishes its position on a schedule its owner sets, so ' +
    'outside its working hours there may be nothing to see.',
  'www.guide.p05.callout.permission':
    'TODO(ta) This is the whole of Mode B’s privacy model, and it is worth being plain ' +
    'about. A private vehicle is invisible to everybody except the people its owner has ' +
    'approved, one request at a time. Tapping its marker reveals nothing about it — it ' +
    'only opens the form that asks.',
  'www.guide.p05.callout.identified':
    'TODO(ta) The request is not anonymous. The owner sees your name, your mobile number ' +
    'and your passenger ID before deciding, which is how somebody running a school van ' +
    'can tell a parent from a stranger.',

  // Chapter 6 · Paying for a vehicle you follow
  'www.guide.p06.title': 'TODO(ta) Paying for a vehicle you follow',
  'www.guide.p06.summary':
    'TODO(ta) Some private vehicles are free to follow and some carry a monthly charge. ' +
    'Which one it is, what it costs and when it falls due are set by the operator rather ' +
    'than by MageRide — and the money goes to them, not to us.',
  'www.guide.p06.step1':
    'TODO(ta) Every private vehicle is set to Free or Paid by whoever runs it. The ' +
    'setting is called Service payment. Free is what an office or a staff transport ' +
    'usually chooses: you follow the vehicle and there is no payment screen at all.',
  'www.guide.p06.step2':
    'TODO(ta) Paid means a monthly amount per subscriber. The operator sets it, and may ' +
    'set a different amount for different people on the same vehicle, so there is no ' +
    'MageRide price to quote you — the app shows your amount before you owe it.',
  'www.guide.p06.step3':
    'TODO(ta) Your subscriptions live under My subscriptions in the menu. Each card shows ' +
    'the vehicle, whether it is Paid or Free, the amount and the date it is next due, a ' +
    'Pay button, a history button and a small cross for unsubscribing.',
  'www.guide.p06.step4':
    'TODO(ta) The billing cycle is either the first of the month or the anniversary of ' +
    'the day you joined — subscribe on 5 June and the next payment is due on 6 July. The ' +
    'card says which one applies to you.',
  'www.guide.p06.step5':
    'TODO(ta) Tap Pay and choose how. LankaQR opens your banking app with the amount ' +
    'already filled in; you can scan the operator’s LankaQR code instead; or you can make ' +
    'an ordinary bank transfer and attach a photograph of the slip.',
  'www.guide.p06.step6':
    'TODO(ta) A transfer shows as awaiting verification until the operator confirms it at ' +
    'their end. Cash goes to whoever collects it, and only the owner can mark it received ' +
    '— after which your card says Paid and the payment appears in your history.',
  'www.guide.p06.step7':
    'TODO(ta) The history button lists every month: the date, the method, the amount and ' +
    'where it stands.',
  'www.guide.p06.step8':
    'TODO(ta) Unsubscribing is that small cross. Confirm it and you lose sight of the ' +
    'vehicle almost immediately; coming back means sending a fresh request and waiting ' +
    'for it to be accepted again, as in chapter 5.',
  'www.guide.p06.callout.passThrough':
    'TODO(ta) This money is not MageRide’s. A subscription is paid to the operator of the ' +
    'vehicle — MageRide routes the payment to their account and records that it happened, ' +
    'and takes none of it.',
  'www.guide.p06.callout.firstMonth':
    'TODO(ta) The first month is free for a new subscriber. Separately, an operator pays ' +
    'MageRide about Rs 300 a month for each private vehicle; the specification says ' +
    'approximately, so that is how it is written here. That is their cost and not ' +
    'something added to yours.',

  // Chapter 7 · Booking a ride
  'www.guide.p07.title': 'TODO(ta) Booking a ride',
  'www.guide.p07.summary':
    'TODO(ta) On-demand rides are Mode C: a motorbike, three-wheeler, car or van that ' +
    'comes to you now. This chapter is about saying where you are going. The next one is ' +
    'about choosing what turns up, and what it costs.',
  'www.guide.p07.step1':
    'TODO(ta) At the bottom of the map is “Where to?”, with Home, Work and the places you ' +
    'have been to recently.',
  'www.guide.p07.step2':
    'TODO(ta) Tap it and type a place or an address. MageRide will not take a bus route ' +
    'number here — a destination is always a place, and the app works out afterwards what ' +
    'can take you to it.',
  'www.guide.p07.step3':
    'TODO(ta) The suggestions are places found by MageRide’s own search, mixed in with ' +
    'your saved and recent addresses. If the search is unavailable the app offers to let ' +
    'you pick the spot on the map instead.',
  'www.guide.p07.step4':
    'TODO(ta) Saved places save you the typing. Home and Work are set by dropping a pin ' +
    'on the map, and any other place can be saved with three address lines and a label of ' +
    'your own — “Gym”, “Mum’s house”.',
  'www.guide.p07.step5':
    'TODO(ta) There is a fourth way to set a location, and it is the useful one when ' +
    'somebody has sent you a spot on WhatsApp: paste a Google Maps link. MageRide reads ' +
    'the coordinates out of the link itself, resolving short links on its own servers, ' +
    'and shows you the pin and the address it worked out before you commit to it. If it ' +
    'cannot read the link it says so and offers the map.',
  'www.guide.p07.step5.note':
    'TODO(ta) Pasting a link is offered where you are setting a location for somebody ' +
    'else — the pickup when you book on another person’s behalf, and both ends of a ' +
    'package.',
  'www.guide.p07.step6':
    'TODO(ta) Your pickup starts as where you are standing and can be moved. The booking ' +
    'screen shows the two points on the map, a For me or For someone else toggle, and a ' +
    'Person or Package toggle.',
  'www.guide.p07.step7':
    'TODO(ta) Below them are the ways of getting there: public routes first where there ' +
    'are any, then the on-demand vehicles with their fares. Chapter 8 is how to read ' +
    'those.',
  'www.guide.p07.step8':
    'TODO(ta) Book Now begins the search for a driver, and what happens then is chapter ' +
    '9.',
  'www.guide.p07.callout.openMaps':
    'TODO(ta) The map and the search box are OpenStreetMap data on MageRide’s own ' +
    'servers. That is a decision rather than a saving: there is no commercial map ' +
    'licence, no per-user fee, and your destination is typed into MageRide’s own search ' +
    'rather than a mapping company’s.',
  'www.guide.p07.callout.routeNumber':
    'TODO(ta) A route number is not a destination. If you are trying to catch the 138, ' +
    'put in where you want to end up — the public routes that serve it, the 138 among ' +
    'them, are what the next screen lists.',

  // Chapter 8 · Choosing a vehicle, and the fare
  'www.guide.p08.title': 'TODO(ta) Choosing a vehicle, and the fare',
  'www.guide.p08.summary':
    'TODO(ta) Once MageRide knows where you are going it shows what can take you there ' +
    'and what each one costs, before you book anything. This chapter is what that number ' +
    'is made of.',
  'www.guide.p08.step1':
    'TODO(ta) The on-demand options are a card each, one per vehicle type: motorbike, ' +
    'three-wheeler, Flex, sedan, mini van and van. Trucks and mini trucks exist as well, ' +
    'but they carry packages rather than people.',
  'www.guide.p08.step2':
    'TODO(ta) Each card carries one number, and it is the total fare for that vehicle for ' +
    'this trip rather than a price it starts from.',
  'www.guide.p08.step3':
    'TODO(ta) The number comes from a published tariff: a charge for the first kilometre ' +
    'plus a rate for every kilometre after it, both set per vehicle type. A motorbike ' +
    'costs the least and a van the most.',
  'www.guide.p08.step4':
    'TODO(ta) Peak-hour and night rates are already inside that total. The app shows one ' +
    'number rather than a list of parts; the trip summary at the end is where a breakdown ' +
    'appears.',
  'www.guide.p08.step5':
    'TODO(ta) What the cards deliberately do not show is “four minutes away” or a ' +
    'distance to you, because no driver has been matched yet and anything of that sort ' +
    'would be a guess.',
  'www.guide.p08.step6':
    'TODO(ta) Public routes to the same destination sit above the on-demand cards with no ' +
    'fare on them at all, because Mode A is free — that is chapter 4.',
  'www.guide.p08.step7':
    'TODO(ta) Choose how you will pay before you book. Cash is the default. You can also ' +
    'pay from your MageRide wallet balance, or by scanning the driver’s own bank QR code ' +
    'at the end of the trip, and none of the three adds anything to the fare. A package ' +
    'can be sent cash on delivery instead, with the cash collected when it arrives.',
  'www.guide.p08.step8':
    'TODO(ta) Whatever the fare is, MageRide takes none of it. There is no commission on ' +
    'a fare: the number on the card is what the driver receives.',
  'www.guide.p08.callout.tariffChanges':
    'TODO(ta) The tariff is set and reviewed by MageRide administrators and can change. ' +
    'The figure on the card is the one in force at the moment you look at it, which is ' +
    'why this page quotes no rupee amount for a ride.',
  'www.guide.p08.callout.estimateVsFinal':
    'TODO(ta) The fare on the card is worked out from the distance of the route between ' +
    'your two points. The final fare is the same tariff over the distance actually ' +
    'travelled, and a difference beyond the platform’s threshold is put in front of a ' +
    'person to review rather than simply charged.',

  // Chapter 9 · Waiting for a driver
  'www.guide.p09.title': 'TODO(ta) Waiting for a driver',
  'www.guide.p09.summary':
    'TODO(ta) Between tapping Book Now and a driver’s face appearing on your screen, MageRide ' +
    'is doing something quite specific. It is worth two minutes of your attention, because it ' +
    'explains what you can and cannot expect while you stand there.',
  'www.guide.p09.step1':
    'TODO(ta) Book Now replaces the map with a finding-a-driver screen: a pulse, the vehicle ' +
    'type you chose, and a countdown.',
  'www.guide.p09.step2':
    'TODO(ta) The request goes to one driver at a time, not to everybody at once. MageRide ' +
    'picks the best candidate nearby and holds the offer open for that driver alone.',
  'www.guide.p09.step3':
    'TODO(ta) Each driver has fifteen seconds to accept. If they do not, the offer moves ' +
    'straight to the next driver, and so on down the list.',
  'www.guide.p09.step4':
    'TODO(ta) The list is ordered by how close the driver is, their driver level, and whether ' +
    'their vehicle is the type you asked for. Drivers do not bid and they cannot see what you ' +
    'are paying differently from one another — the fare was fixed before you tapped Book.',
  'www.guide.p09.step5':
    'TODO(ta) The whole search runs for two minutes. If nobody has accepted by then you are ' +
    'told so, the request cancels itself, and the app offers to try again.',
  'www.guide.p09.step5.note':
    'TODO(ta) Trying again is a fresh search, not a queue position. Picking a different ' +
    'vehicle type often helps, because it changes which drivers are eligible at all.',
  'www.guide.p09.step6':
    'TODO(ta) A driver who lets the fifteen seconds pass, or taps no, is not penalised for it. ' +
    'They may be finishing another trip, or heading somewhere specific — a driver on their way ' +
    'home can set a filter that only brings them hires going the same way. A decline is almost ' +
    'never about you.',
  'www.guide.p09.step7':
    'TODO(ta) Which is why the honest answer to “why is this taking so long” is usually that ' +
    'there are few suitable drivers near you at that moment. Time of day and how far out you ' +
    'are matter more than anything you can change on the screen.',
  'www.guide.p09.step8':
    'TODO(ta) Cancel is on this screen and it is free. Nothing is charged for a ride that ' +
    'never found a driver.',
  'www.guide.p09.step9':
    'TODO(ta) The moment a driver accepts, this screen becomes the ride screen — their name, ' +
    'their vehicle, and a live map. That is the next chapter.',
  'www.guide.p09.callout.twoMinutes':
    'TODO(ta) Two minutes is the whole search, not one driver’s turn. Fifteen seconds is how ' +
    'long each driver has to answer before the offer passes on. If the two minutes run out, ' +
    'MageRide tells you and cancels the request rather than leaving it open.',
  'www.guide.p09.callout.cancelFree':
    'TODO(ta) Cancelling before a driver accepts costs nothing, every time. Once a driver has ' +
    'accepted the rules change, and chapter 10 sets out exactly how.',

  // Chapter 10 · During the ride
  'www.guide.p10.title': 'TODO(ta) During the ride',
  'www.guide.p10.summary':
    'TODO(ta) From the moment a driver accepts until you step out, one screen carries ' +
    'everything: where the car is, who is driving it, how to reach them, and how to raise an ' +
    'alarm.',
  'www.guide.p10.step1':
    'TODO(ta) The driver card shows their photo and name, their rating, the vehicle and its ' +
    'registration number, and how many minutes away they are.',
  'www.guide.p10.step2':
    'TODO(ta) The map underneath is live. The driver’s marker moves as they move, all the way ' +
    'to you and then all the way to where you are going.',
  'www.guide.p10.step3':
    'TODO(ta) There is a short start code on the card. Read it out or show it to the driver ' +
    'when they arrive — the trip only begins once they have entered it, which is how the app ' +
    'knows you got into the right vehicle.',
  'www.guide.p10.step4':
    'TODO(ta) Tapping Call gives you a choice of two. A free call goes over the internet ' +
    'through the app and uses no minutes. A normal call is an ordinary phone call, dialled ' +
    'directly to the driver’s own number.',
  'www.guide.p10.step4.note':
    'TODO(ta) If a free call cannot connect on poor data, the app offers to place the normal ' +
    'call to the same person instead. It remembers whichever you chose last time.',
  'www.guide.p10.step5':
    'TODO(ta) You and the driver can see each other’s real mobile numbers from the moment they ' +
    'accept. This is not hidden or disguised, and MageRide tells you so when you sign up and ' +
    'again on your first call.',
  'www.guide.p10.step6':
    'TODO(ta) The emergency button is on the ride card. It asks you to confirm, then sends ' +
    'your location and the trip details by text to the emergency contact you saved, and raises ' +
    'the alert with MageRide at the same time.',
  'www.guide.p10.step7':
    'TODO(ta) If your phone loses signal the driver’s marker stops and a banner says so. ' +
    'Nothing is lost — the map catches up when the connection returns.',
  'www.guide.p10.step8':
    'TODO(ta) When the driver ends the trip you go to the summary, and then to paying, which ' +
    'is the next chapter.',
  'www.guide.p10.callout.realNumbers':
    'TODO(ta) MageRide does not mask phone numbers. Once a driver accepts, you can see their ' +
    'real mobile number and they can see yours, so that either of you can sort out a pickup ' +
    'that has gone wrong. Numbers are never shown for a ride that was cancelled before a ' +
    'driver was assigned. If you booked the ride for somebody else, the driver sees that ' +
    'person’s number and never yours.',
  'www.guide.p10.callout.cancelAfterAccept':
    'TODO(ta) Cancelling after a driver has accepted costs Rs 50, and it is added to the fare ' +
    'of your next ride rather than charged on the spot. It is not a fee MageRide keeps — it ' +
    'goes to the driver whose accepted ride you cancelled. Three of these in a row switches ' +
    'booking off until the balance is cleared, and the count returns to zero the moment you ' +
    'complete a ride.',
  'www.guide.p10.callout.sosContact':
    'TODO(ta) The emergency button needs an emergency contact saved in your profile before it ' +
    'will send anything. Set one now rather than during a ride.',

  // Chapter 11 · Paying
  'www.guide.p11.title': 'TODO(ta) Paying for the ride',
  'www.guide.p11.summary':
    'TODO(ta) Cash, your MageRide balance, or the driver’s own bank QR code. None of the three ' +
    'costs more than the others, and all of the money goes to the driver.',
  'www.guide.p11.step1':
    'TODO(ta) You chose how you would pay before you booked, in chapter 8. You can still ' +
    'change it at the end of the trip.',
  'www.guide.p11.step2':
    'TODO(ta) Cash is the default and needs no screen at all. You hand the money to the driver ' +
    'and the trip closes.',
  'www.guide.p11.step3':
    'TODO(ta) Paying from your MageRide balance is a single tap. The money moves from your ' +
    'balance to the driver’s straight away, with nothing to confirm and nothing to wait for. ' +
    'You top that balance up in the app, by card, whenever it suits you.',
  'www.guide.p11.step4':
    'TODO(ta) The third way is to scan the driver’s own QR code — printed, on a window ' +
    'sticker, or on their screen — and pay it from your banking app, exactly as you would pay ' +
    'any shop.',
  'www.guide.p11.step4.note':
    'TODO(ta) That code belongs to the driver’s own bank account. The money never passes ' +
    'through MageRide on its way there.',
  'www.guide.p11.step5':
    'TODO(ta) Because it goes bank to bank, MageRide never hears that it arrived. So after ' +
    'paying you tap “I’ve paid” — and you can attach a screenshot of your bank’s receipt if ' +
    'you want one on record.',
  'www.guide.p11.step6':
    'TODO(ta) The driver then confirms they received it, and the trip closes. A driver can ' +
    'confirm on their own if you have already walked off. If they do not confirm, the app ' +
    'nudges them, and a “Get help” link opens a support ticket with your screenshot attached.',
  'www.guide.p11.step7':
    'TODO(ta) The summary shows the total, the distance, and how the fare was made up — first ' +
    'kilometre, the per-kilometre part, and any peak or night rate.',
  'www.guide.p11.step8':
    'TODO(ta) Every trip keeps its receipt. You can open it again from your trip history at ' +
    'any time, which is chapter 15.',
  'www.guide.p11.callout.noSurcharge':
    'TODO(ta) No way of paying for a ride costs extra. Cash, your balance and the driver’s QR ' +
    'are the same number, and MageRide takes no commission from any of them — the fare is what ' +
    'the driver receives. Card processing is only charged where MageRide is genuinely the one ' +
    'being paid, such as topping up your balance.',
  'www.guide.p11.callout.attestation':
    'TODO(ta) A QR payment is settled by both of you saying so, not by a bank message to ' +
    'MageRide — because no such message exists when you pay into somebody else’s account. If ' +
    'you say you paid and the driver says the money never arrived, the trip goes to MageRide ' +
    'support, where a person looks at it and at your receipt. No money is moved or held by ' +
    'MageRide in the meantime, because MageRide never had it.',

  // Chapter 12 · Sending a package
  'www.guide.p12.title': 'TODO(ta) Sending a package',
  'www.guide.p12.summary':
    'TODO(ta) A parcel travels the same way a person does — the same drivers, the same ' +
    'dispatch, the same fares. What is different is that two other people are involved: ' +
    'whoever hands it over and whoever receives it.',
  'www.guide.p12.step1': 'TODO(ta) On the booking screen, switch from Person to Package.',
  'www.guide.p12.step2':
    'TODO(ta) Pick a size. Small is up to about five kilos and fits a backpack or a motorbike ' +
    'box; medium is up to about twenty and wants a three-wheeler or a car boot; large is over ' +
    'twenty and needs a van or a truck. The hint under each size tells you which vehicles can ' +
    'take it.',
  'www.guide.p12.step3':
    'TODO(ta) Describe what is inside, then add the recipient’s name and phone number, and set ' +
    'both ends of the journey — pickup and drop-off.',
  'www.guide.p12.step3.note':
    'TODO(ta) Either end can be typed, dropped as a pin, or pasted from a Google Maps link. ' +
    'For the drop-off you can also ask the recipient to share it themselves.',
  'www.guide.p12.step4':
    'TODO(ta) Choose how it is paid for. Cash, your MageRide balance and the driver’s QR all ' +
    'work as they do for a ride. Packages add one more: cash on delivery, where the person ' +
    'receiving it pays the driver at the door.',
  'www.guide.p12.step5':
    'TODO(ta) Once a driver is on the way you get a four-digit pickup code. Give it to them ' +
    'when they collect the parcel — they cannot mark it collected without it.',
  'www.guide.p12.step6':
    'TODO(ta) The recipient is told the moment it is collected. If they have MageRide they get ' +
    'a notification; if they do not, they get a text with a link that opens a plain tracking ' +
    'page in their browser. Either one shows the map and their own four-digit delivery code.',
  'www.guide.p12.step7':
    'TODO(ta) Both of you watch the same four stages: pickup pending, picked up, in transit, ' +
    'delivered. The driver enters the delivery code at the door to finish it.',
  'www.guide.p12.step8':
    'TODO(ta) If nobody is there to receive it, the driver can complete the delivery with a ' +
    'photograph of where the parcel was left instead of a code.',
  'www.guide.p12.callout.fiveAttempts':
    'TODO(ta) The pickup and delivery codes are four digits and each allows five attempts. ' +
    'After five wrong tries that step locks and goes to MageRide support rather than letting a ' +
    'parcel be handed to the wrong person. Read the code out carefully.',
  'www.guide.p12.callout.cod':
    'TODO(ta) On cash on delivery the recipient pays the driver, not you, and the driver taps ' +
    '“Delivery completed” when the parcel is handed over. If that cash is still outstanding a ' +
    'day later the delivery is flagged for MageRide support to look at.',
  'www.guide.p12.callout.noAppNeeded':
    'TODO(ta) The person receiving your parcel does not need MageRide installed and does not ' +
    'need an account. The link in their text message shows them the map, the status and their ' +
    'delivery code, and stops working shortly after the parcel arrives.',

  // Chapter 13 · Booking for someone else
  'www.guide.p13.title': 'TODO(ta) Booking for someone else',
  'www.guide.p13.summary':
    'TODO(ta) You can book a ride for your mother, a colleague or a guest who has never heard ' +
    'of MageRide. They do not need the app, an account, or a phone that can run either.',
  'www.guide.p13.step1':
    'TODO(ta) On the booking screen, switch from For me to For someone else, then enter their ' +
    'name and phone number or pick them from your contacts.',
  'www.guide.p13.step2':
    'TODO(ta) Set where they are being collected from. You can type it, drop a pin on the map, ' +
    'or paste a Google Maps link somebody sent you.',
  'www.guide.p13.step3':
    'TODO(ta) There is a fourth way, and it is the accurate one: ask them. MageRide sends them ' +
    'a request for their pickup spot, they adjust a pin on a map, and the point they confirm ' +
    'fills in on your screen.',
  'www.guide.p13.step3.note':
    'TODO(ta) If they already have MageRide the request arrives in the app. If they do not, it ' +
    'arrives as a text message with a link, and works the same way in their browser.',
  'www.guide.p13.step4':
    'TODO(ta) They have five minutes to answer, and they may simply decline. If they decline, ' +
    'ignore it or run out of time, you place the pin yourself and carry on booking.',
  'www.guide.p13.step5':
    'TODO(ta) From there you book exactly as you would for yourself — vehicle, fare, Book Now. ' +
    'The driver is told this is a booking for somebody else and is given the rider’s name.',
  'www.guide.p13.step6':
    'TODO(ta) As soon as a driver accepts, the rider gets a text with a tracking link. It ' +
    'opens a page with the driver’s name and photo, the vehicle and its number plate, a live ' +
    'map with an arrival time, and the start code to read out.',
  'www.guide.p13.step7':
    'TODO(ta) That page has a tap-to-call button for the driver and an emergency button. If ' +
    'they press the emergency button, the alert is texted to you — the person who arranged the ' +
    'ride.',
  'www.guide.p13.step8':
    'TODO(ta) The link is tied to that one ride and stops working when the trip ends. After ' +
    'that it shows nothing at all — no route, no driver, no history.',
  'www.guide.p13.callout.declineSendsNothing':
    'TODO(ta) If the person you are booking for declines the request for their location, ' +
    'MageRide sends you nothing at all. Not an approximate position, not a last known one — ' +
    'nothing. The page they see says so in as many words, and it is the same promise whether ' +
    'they answer in the app or in a browser.',
  'www.guide.p13.callout.riderNumberOnly':
    'TODO(ta) The driver is given the rider’s phone number so they can find each other, and ' +
    'never yours. That is the case even though you are the one who booked and, on some ways of ' +
    'paying, the one being charged.',
  'www.guide.p13.callout.cashIsTheRiders':
    'TODO(ta) If you choose cash, the rider pays the driver at the end of the trip, and their ' +
    'tracking page tells them the amount before they get in. Decide that with them before you ' +
    'book, rather than leaving them to find out at the door.',

  // Chapter 14 · Scheduling a ride
  'www.guide.p14.title': 'TODO(ta) Booking a ride for later',
  'www.guide.p14.summary':
    'TODO(ta) A scheduled ride is for the six in the morning airport run, the hospital ' +
    'appointment, the thing you do not want to be arranging while it is happening.',
  'www.guide.p14.step1':
    'TODO(ta) Set your destination and pick a vehicle exactly as you would for a ride now. ' +
    'Beside Book Now there is Schedule.',
  'www.guide.p14.step2':
    'TODO(ta) The schedule screen asks for a destination before anything else, and Confirm ' +
    'stays greyed out until you have set one. That is deliberate, not a fault.',
  'www.guide.p14.step3':
    'TODO(ta) Your pickup starts as where you are standing now and can be changed to anywhere ' +
    '— useful when you are arranging tomorrow morning from the sofa this evening.',
  'www.guide.p14.step4':
    'TODO(ta) Pick the date and the time. Times that have already passed cannot be selected. ' +
    'Confirm saves it.',
  'www.guide.p14.step4.note':
    'TODO(ta) The saved booking appears under Scheduled in your trips, where you can look at ' +
    'it again or cancel it.',
  'www.guide.p14.step5':
    'TODO(ta) MageRide reminds you twice — an hour before and again fifteen minutes before.',
  'www.guide.p14.step6':
    'TODO(ta) A scheduled ride is an on-demand vehicle booked ahead, and the fare works out ' +
    'the same way as any other — the same tariff, over the same route, per vehicle type. ' +
    'Booking early does not cost more and does not cost less.',
  'www.guide.p14.step7':
    'TODO(ta) Well before your pickup it goes onto a board that standby drivers can see, where ' +
    'they say in advance that they want it. They cannot take it from the board — it is a way ' +
    'of putting their hand up, nothing more.',
  'www.guide.p14.step8':
    'TODO(ta) Half an hour before your pickup, the ride is offered to those drivers, closest ' +
    'first and the more experienced of them ahead of the rest. From that point it works ' +
    'exactly like a ride booked now, including the wait described in chapter 9.',
  'www.guide.p14.step9':
    'TODO(ta) If you cancel from the Scheduled tab, the driver is told in time to make other ' +
    'plans.',
  'www.guide.p14.callout.notAReservation':
    'TODO(ta) Scheduling holds a time, not a vehicle. No driver is assigned to your booking ' +
    'until about half an hour beforehand, and if none accepts it then, you are told — the same ' +
    'as a ride you book on the spot. For something you cannot miss, book early enough to have ' +
    'a second try.',
  'www.guide.p14.callout.reminders':
    'TODO(ta) Two reminders come automatically: one hour before, and fifteen minutes before. ' +
    'You do not have to set them.',

  // Chapter 15 · Saved places, ratings and reviews
  'www.guide.p15.title': 'TODO(ta) Saved places, ratings and your trips',
  'www.guide.p15.summary':
    'TODO(ta) The parts of MageRide that build up over time — the addresses you stop typing, ' +
    'the ratings you leave, and the record of everywhere you have been.',
  'www.guide.p15.step1':
    'TODO(ta) Home and Work are set by dropping a pin on the map rather than by typing an ' +
    'address. MageRide reads the address back to you from the point you chose, so you can see ' +
    'it landed where you meant.',
  'www.guide.p15.step2':
    'TODO(ta) Any other place is saved the same way, with three address lines and a label of ' +
    'your own choosing — “Gym”, “Mum’s house”, “the office”.',
  'www.guide.p15.step3':
    'TODO(ta) All of them can be edited or deleted afterwards, Home and Work included. They ' +
    'then appear as one-tap shortcuts every time you book.',
  'www.guide.p15.step4':
    'TODO(ta) Saved places belong to your account rather than to your handset.',
  'www.guide.p15.step4.note':
    'TODO(ta) Sign in on a new phone and they are already there, along with how you usually ' +
    'pay.',
  'www.guide.p15.step5':
    'TODO(ta) After a trip ends you are asked to rate the driver out of five, with a few quick ' +
    'reasons to tap — clean, on time, polite, safe driving — and a comment box if you want ' +
    'one. You can skip it, and skipping costs you nothing.',
  'www.guide.p15.step5b':
    'TODO(ta) This applies to a private vehicle you follow as well as to a ride you hailed. A ' +
    'school van driver can be rated by the parents who subscribe to them.',
  'www.guide.p15.step6':
    'TODO(ta) Reporting a driver is a separate and heavier thing than a low rating, and it is ' +
    'meant to be: reports are reviewed, and a driver who collects three has their level cut ' +
    'and is delisted for a period.',
  'www.guide.p15.step7':
    'TODO(ta) Your trips are kept in three lists — past, scheduled, and packages — each with ' +
    'the date, the route, the distance and the fare.',
  'www.guide.p15.step8':
    'TODO(ta) Opening a past trip gives you the route, the fare breakdown and a receipt you ' +
    'can download. It also shows the driver’s name and number with a call button, which is how ' +
    'you reach them about something left in the car.',
  'www.guide.p15.callout.whichStarsCount':
    'TODO(ta) Only four- and five-star ratings count towards a driver’s level — five stars are ' +
    'worth five points, four are worth four, and five hundred points is a level. Two stars and ' +
    'below add nothing rather than subtracting. That is why drivers ask.',
  'www.guide.p15.callout.ratedBothWays':
    'TODO(ta) Drivers rate passengers too, out of the same five stars and with the same ' +
    'optional comment, from their own trip history. It is worth knowing that the courtesy runs ' +
    'in both directions.',

  // Chapter 16 · Settings, help and your data
  'www.guide.p16.title': 'TODO(ta) Settings, help, and your data',
  'www.guide.p16.summary':
    'TODO(ta) Where to change what MageRide knows about you, where to get a person to look at ' +
    'a problem, and what you can require MageRide to do with your information.',
  'www.guide.p16.step1':
    'TODO(ta) The menu reaches four places: private vehicles you follow, the ones you ' +
    'subscribe to, your saved addresses, and your profile and settings.',
  'www.guide.p16.step2':
    'TODO(ta) Profile and settings is where the app-wide choices live: your language, your ' +
    'notifications, your saved addresses, the way you usually pay, and the way through to ' +
    'help. Changing the language changes the whole app at once, and you can change it as often ' +
    'as you like.',
  'www.guide.p16.step3':
    'TODO(ta) Edit profile, a separate screen, is for the things that are about you rather ' +
    'than about the app — your name, your photo, and your emergency contacts. That is where ' +
    'you set the contact the emergency button in chapter 10 texts, and it is worth doing ' +
    'before you need it.',
  'www.guide.p16.step4':
    'TODO(ta) You can block a driver. A blocked driver disappears from your map and can never ' +
    'be sent to you again.',
  'www.guide.p16.step5':
    'TODO(ta) Help and support opens on a list of common questions, which answers most things ' +
    'without anybody being involved.',
  'www.guide.p16.step6':
    'TODO(ta) If it does not, raise a ticket. Describe the problem, attach the trip it is ' +
    'about from a list of your past trips, and add a screenshot if it helps. You can follow ' +
    'the ticket until it is answered.',
  'www.guide.p16.step7':
    'TODO(ta) From the same screen you can ask for a copy of everything MageRide holds about ' +
    'you, and you can ask for your account and personal information to be erased.',
  'www.guide.p16.step7.note':
    'TODO(ta) Both are rights under Sri Lanka’s personal data protection law, not favours. You ' +
    'do not have to give a reason for either.',
  'www.guide.p16.step8':
    'TODO(ta) Our data page sets out what MageRide collects and how these requests are ' +
    'handled.',
  'www.guide.p16.callout.thirtyDays':
    'TODO(ta) A request for your data, or to erase it, is answered within thirty days. You are ' +
    'given a reference and a due date when you make it, and you can check where it has got to.',
  'www.guide.p16.callout.whatIsKept':
    'TODO(ta) Erasure removes your personal information but cannot remove everything. A ride ' +
    'that is still running, a payment still in dispute, and the records MageRide is required ' +
    'to keep as an audit trail all stay — that last one cannot be altered by anybody, which is ' +
    'the point of it. You are told which of these applied to your request.',
  'www.guide.p16.callout.blockADriver':
    'TODO(ta) Blocking is not the same as a low rating and does not depend on one. A blocked ' +
    'driver stops appearing on your map and cannot be dispatched to you, whatever their rating ' +
    'is.',

  // ---------------------------------------------------------------------------
  // Driver guide, chapters 1–9 (S10). Untranslated: every entry below carries the
  // English text behind a TODO(ta) marker, which is what makes the gap
  // countable rather than invisible — `scripts/check-i18n-parity.mjs` reports them
  // on every build and does not fail (MCS-34 D2).
  // ---------------------------------------------------------------------------
  'www.screens.da026.caption':
    'TODO(ta) My Vehicles — what is approved, and what is unfinished',
  'www.guide.d01.title': 'TODO(ta) Setting up the driver app',
  'www.guide.d01.summary':
    'TODO(ta) Language, city, your phone number, and the profile every passenger sees. It ' +
    'takes a few minutes and your driving licence in your hand, and it does not need you to ' +
    'have a vehicle registered yet.',
  'www.guide.d01.step1':
    'TODO(ta) Open the driver app for the first time and three slides across the top ' +
    'introduce what it does — registering a vehicle, the fifteen-second ride offer, ' +
    'directional travel, and the in-app wallet the daily fee comes out of. Swipe them or let ' +
    'them advance.',
  'www.guide.d01.step2':
    'TODO(ta) Under the slides, choose your language from three boxes stacked one to a row — ' +
    'Sinhala at the top and already selected, then Tamil, then English — and the city you ' +
    'drive in.',
  'www.guide.d01.step2.note':
    'TODO(ta) The cities in that list are the ones MageRide has launched in, and the app ' +
    'loads them when you open it. A new city appears on its own; you never update the app ' +
    'for one.',
  'www.guide.d01.step3':
    'TODO(ta) Sign in with your mobile number. The field already carries +94, so you type ' +
    'the nine digits after it, then the six-digit code that arrives by text. If it does not ' +
    'come, ask for another after sixty seconds. There is no password, no email address and ' +
    'no Google sign-in — your phone number is how passengers and MageRide reach you.',
  'www.guide.d01.step4':
    'TODO(ta) Profile setup comes next, and it is about you rather than about a vehicle. Add ' +
    'a photo of yourself — it is required, and passengers see it — and your name. Save and ' +
    'continue stays greyed out until the photo is there.',
  'www.guide.d01.step5':
    'TODO(ta) Then photograph your driving licence, front and back. The app reads four ' +
    'things off it and shows them back to you: the licence number, its expiry, your NIC ' +
    'number, and the vehicle classes you are licensed to drive.',
  'www.guide.d01.step5.note':
    'TODO(ta) Whatever it could not read clearly, you type in yourself — and anything you ' +
    'type is marked for someone at MageRide to check before it is trusted. That makes a ' +
    'clear photograph worth a second attempt, which chapter 3 is entirely about.',
  'www.guide.d01.step6':
    'TODO(ta) Grant the permissions on the following screen — chapter 5 goes through what ' +
    'each one is for — and the dashboard opens.',
  'www.guide.d01.step7':
    'TODO(ta) You are now in the app with no vehicle registered, which is how it is meant to ' +
    'go. Registering one is the next chapter and you can do it whenever your paperwork is ' +
    'ready. You can also be assigned a bus or a van by a fleet and drive that without ' +
    'registering anything at all.',
  'www.guide.d01.callout.notPublished':
    'TODO(ta) The driver app has not been published to the app stores yet, so there is ' +
    'nothing to install today. This guide describes the app as it has been designed and ' +
    'approved; when it is released, the download page will say so.',
  'www.guide.d01.callout.noVehicleNeeded':
    'TODO(ta) You do not need a vehicle to finish signing up. Your name, your photo and your ' +
    'driving licence are enough to reach the dashboard — registering a vehicle is a ' +
    'separate, optional step you take when you are ready for it.',
  'www.guide.d01.callout.oneDevicePerApp':
    'TODO(ta) One phone at a time, counted per app. Signing in to the driver app on a new ' +
    'phone signs the old one out at once, and if that happens mid-trip the new phone picks ' +
    'the trip up where it was. The passenger app counts separately, so you can run both.',
  'www.guide.d02.title': 'TODO(ta) Registering your vehicle',
  'www.guide.d02.summary':
    'TODO(ta) Four steps, saved one at a time, for a standby vehicle you own. What the plus ' +
    'button does, what Resume does, and why a bus does not go through here at all.',
  'www.guide.d02.step1':
    'TODO(ta) The driver app registers standby vehicles — the vehicle you drive yourself and ' +
    'take on-demand hires with. Motorbike, three-wheeler, Flex, sedan, mini van and van, ' +
    'plus truck and mini truck for deliveries.',
  'www.guide.d02.step1.note':
    'TODO(ta) A bus, a school van, or anything carrying a route permit is registered by its ' +
    'operator in the fleet portal instead. There is no permit slot and no GPS-tracker field ' +
    'in this wizard, and that is deliberate rather than missing.',
  'www.guide.d02.step2':
    'TODO(ta) My Vehicles is where every vehicle you have lives, and where this starts. If ' +
    'you have none at all, the app offers to register one as soon as you open the screen.',
  'www.guide.d02.step3':
    'TODO(ta) Step 1 of 4 asks for two things: the vehicle type, and its registration ' +
    'number. Continue takes you to insurance.',
  'www.guide.d02.step4':
    'TODO(ta) Steps 2, 3 and 4 are photographs — your insurance certificate, your revenue ' +
    'licence, and the vehicle itself front and back with the number plate readable. Each one ' +
    'shows Done once it has uploaded.',
  'www.guide.d02.step5':
    'TODO(ta) Each step is saved as you finish it. You can close the app halfway through ' +
    'step 2 and come back tomorrow; nothing you have already done is lost.',
  'www.guide.d02.step6':
    'TODO(ta) Until all four are done the vehicle sits in My Vehicles as Incomplete, showing ' +
    'which step is next. Once all four are checked and cleared it shows Approved — and only ' +
    'an Approved vehicle can be taken online.',
  'www.guide.d02.step7':
    'TODO(ta) Coming back to an unfinished vehicle is Resume, on that vehicle’s own row. It ' +
    'opens that vehicle at its own next step, not at the beginning.',
  'www.guide.d02.step7.note':
    'TODO(ta) The plus button at the top means add: it always starts a fresh Step 1 of 4 for ' +
    'a new vehicle, whatever else is unfinished. Vehicle Onboarding in the menu names no ' +
    'vehicle, so it returns you to the first unfinished one.',
  'www.guide.d02.step8':
    'TODO(ta) You can register as many vehicles as you like, but only one is live at a time ' +
    '— the one you select in My Vehicles is the one that publishes its position, takes ' +
    'hires, and sets the daily rate you pay.',
  'www.guide.d02.callout.threeDoors':
    'TODO(ta) Three ways into the wizard and three different meanings. Plus starts a new ' +
    'vehicle. Resume on a row continues that vehicle. Vehicle Onboarding in the menu names ' +
    'no vehicle, so it returns you to the first unfinished one. Adding a second vehicle ' +
    'while a first is unfinished is what the plus button is for.',
  'www.guide.d02.callout.oneVehicleOnePhone':
    'TODO(ta) A vehicle belongs to one mobile number at a time, and a registration number ' +
    'can only be in use once among active vehicles. If the app says your plate is already ' +
    'registered, it is active on another account — or on an older one of yours, where ' +
    'removing the vehicle releases the number.',
  'www.guide.d02.callout.fleetPortal':
    'TODO(ta) Buses, school vans and anything needing a route permit are onboarded by their ' +
    'operator in the fleet portal, which has slots for the registration book, insurance, ' +
    'revenue licence and the permit itself. As a driver you can be assigned one of those ' +
    'vehicles and drive it without registering anything yourself.',
  'www.guide.d03.title': 'TODO(ta) Photographing your documents',
  'www.guide.d03.summary':
    'TODO(ta) Every camera slot in the app opens the same scanner, and it has one control ' +
    'worth learning properly. A clear photograph is the difference between a vehicle ' +
    'approved in minutes and one waiting on a person.',
  'www.guide.d03.step1':
    'TODO(ta) Tap any capture slot — licence, insurance, revenue licence, vehicle photo — ' +
    'and the same document scanner opens: a live camera with a frame drawn over what it can ' +
    'see.',
  'www.guide.d03.step2':
    'TODO(ta) The app guesses where the edges of the document are and puts a four-cornered ' +
    'frame on them. Drag the corners so the whole document sits inside the frame and fills ' +
    'it.',
  'www.guide.d03.step2.note':
    'TODO(ta) The guess is only a starting point. If it has caught the table rather than the ' +
    'paper, move the corners yourself — what you leave inside the frame is exactly what gets ' +
    'sent.',
  'www.guide.d03.step3':
    'TODO(ta) Use photo straightens and crops what you framed, and uploads that. Retake ' +
    'starts over, and there is a flash for a dark yard. You can pick an existing photo from ' +
    'your gallery instead, though a fresh capture usually reads better.',
  'www.guide.d03.step4':
    'TODO(ta) Your driving licence, front and back, is read for the licence number, the ' +
    'expiry, your NIC number and the classes you are licensed for.',
  'www.guide.d03.step5':
    'TODO(ta) Your insurance certificate is read for its expiry date, and your revenue ' +
    'licence for its number and its expiry date.',
  'www.guide.d03.step6':
    'TODO(ta) The two vehicle photographs are read for the number plate, which is matched ' +
    'against the registration number you typed at step 1. A plate that cannot be read, or ' +
    'that does not match, holds that step up.',
  'www.guide.d03.step7':
    'TODO(ta) Everything the app reads is shown back to you before it goes anywhere, and you ' +
    'can correct any of it.',
  'www.guide.d03.step7.note':
    'TODO(ta) Correcting a field is not a failure. It makes that value one a person will ' +
    'confirm rather than one the app could stand behind on its own, and the next chapter is ' +
    'what happens to it.',
  'www.guide.d03.callout.whyItMatters':
    'TODO(ta) This is the highest-value two minutes in the whole sign-up. The clearer the ' +
    'photograph, the more the app reads on its own, and the fewer fields wait for somebody ' +
    'at MageRide to check by hand. Bad light and a crooked angle cost you days, not seconds.',
  'www.guide.d03.callout.insuranceMandatory':
    'TODO(ta) A valid insurance certificate is required for every vehicle on MageRide, in ' +
    'all three modes, and so is a revenue licence. If either expires, that vehicle stops ' +
    'receiving hires until the renewed document is uploaded.',
  'www.guide.d03.callout.whatIsRead':
    'TODO(ta) Each document is read for the fields named above, and every value is shown to ' +
    'you before it is saved. The app also records whether a value was read from your ' +
    'photograph or typed in by you, so that whoever checks it knows which is which.',
  'www.guide.d04.title': 'TODO(ta) Getting approved',
  'www.guide.d04.summary':
    'TODO(ta) What the app clears on its own, what goes to a person, and what you can do ' +
    'while you wait. Four verdicts on one screen.',
  'www.guide.d04.step1':
    'TODO(ta) Submitting the fourth step opens a review screen with four lines on it — ' +
    'vehicle details, insurance, revenue licence, and the front and back photographs. Each ' +
    'is either Verified or Pending.',
  'www.guide.d04.step2':
    'TODO(ta) A line is Verified when the app read what it needed: an expiry date off the ' +
    'insurance, a number and expiry off the revenue licence, a number plate matching the ' +
    'registration you typed, and the type and registration you entered yourself.',
  'www.guide.d04.step3':
    'TODO(ta) If all four come back Verified, the vehicle is approved automatically with ' +
    'nobody at MageRide involved. That is the ordinary outcome of a clean set of ' +
    'photographs.',
  'www.guide.d04.step4':
    'TODO(ta) A line goes Pending for one of three reasons: the app was not confident of ' +
    'what it read, you typed the value in yourself, or the plate in your photographs did not ' +
    'match the registration number.',
  'www.guide.d04.step4.note':
    'TODO(ta) Pending is about one step, not the whole application. The other three stay ' +
    'Verified and you do not photograph them again.',
  'www.guide.d04.step5':
    'TODO(ta) A Pending line goes to a verification officer, who either confirms what is ' +
    'there or corrects it and confirms that. The vehicle is not approved until none are left ' +
    'pending.',
  'www.guide.d04.step6':
    'TODO(ta) You are told the outcome by notification and in the app. If something is ' +
    'rejected you are given the reason and a way to photograph it again.',
  'www.guide.d04.step7':
    'TODO(ta) While you wait you keep the app. A bus or a private vehicle assigned to you by ' +
    'a fleet can be driven today. What you cannot do is take this particular vehicle online: ' +
    'only an Approved vehicle can be selected to go live.',
  'www.guide.d04.callout.whileYouWait':
    'TODO(ta) Waiting on approval does not lock you out. You reached the dashboard before ' +
    'any of this began, and a vehicle shared or temporarily assigned to you by a fleet can ' +
    'be driven straight away — it never goes through this wizard.',
  'www.guide.d04.callout.typedIsChecked':
    'TODO(ta) Anything you typed rather than photographed is always checked by a person, ' +
    'however obviously right it is. That is a rule about evidence rather than a suspicion ' +
    'about you, and it is the best single reason to spend the extra minute on the ' +
    'photograph.',
  'www.guide.d05.title': 'TODO(ta) Permissions and driving in the background',
  'www.guide.d05.summary':
    'TODO(ta) Four things the driver app asks for, what each one actually buys you, and when ' +
    'your position is being published.',
  'www.guide.d05.step1':
    'TODO(ta) The permissions screen comes up once, after your profile is saved, and ' +
    'explains itself before your phone’s own permission boxes appear.',
  'www.guide.d05.step2':
    'TODO(ta) Location, set to always or background, is the one that matters most. It is how ' +
    'your position reaches passengers and how dispatch knows where you are when a hire comes ' +
    'up near you.',
  'www.guide.d05.step2.note':
    'TODO(ta) On Android you are also asked to let the app display over other apps, so a ' +
    'ride offer can appear over whatever is on your screen. An iPhone has no equivalent ' +
    'setting: there you are asked for always-on location and for notifications, and that is ' +
    'all.',
  'www.guide.d05.step3':
    'TODO(ta) Notifications are how a ride offer reaches you at all. An offer wakes the ' +
    'phone with a sound and a vibration; with notifications off there is nothing to wake it.',
  'www.guide.d05.step4':
    'TODO(ta) Turning battery optimisation off for the driver app is the fourth ask, and the ' +
    'easiest to skip past. Do not skip it.',
  'www.guide.d05.step4.note':
    'TODO(ta) Left on, your phone is entitled to shut down the service that publishes your ' +
    'position and listens for offers while the screen is off. The app asks for the exemption ' +
    'precisely so that it cannot.',
  'www.guide.d05.step5':
    'TODO(ta) While you are online, or on a journey, the app publishes your position in the ' +
    'background — screen off, phone in your pocket, another app open. It runs as a ' +
    'foreground service, which is why your phone shows a notice while it is working.',
  'www.guide.d05.step6':
    'TODO(ta) Going offline, or ending a journey, stops it. The same rule covers a GPS ' +
    'tracker fitted to a standby vehicle: its positions are only taken while the vehicle is ' +
    'online.',
  'www.guide.d05.callout.whenYouArePublished':
    'TODO(ta) Your position is published while you are online or on a journey, and ' +
    'publishing stops when you go offline or end the journey. That is what the toggle does — ' +
    'it is not simply a label on a screen.',
  'www.guide.d05.callout.batteryOptimisation':
    'TODO(ta) If you are online and offers are not arriving, battery optimisation is the ' +
    'first thing to check. A phone is allowed to stop an app running in the background, and ' +
    'this is the permission that asks it not to.',
  'www.guide.d05.callout.ownVehicleOnly':
    'TODO(ta) The map on your own dashboard shows one vehicle: yours. Other drivers are ' +
    'never drawn on it. And while you are carrying a passenger, your vehicle comes off the ' +
    'public map other passengers browse — only the passenger you are carrying can see you.',
  'www.guide.d06.title': 'TODO(ta) Your dashboard',
  'www.guide.d06.summary':
    'TODO(ta) The driver app has two home screens, and which one you get follows the vehicle ' +
    'you have selected. Standby drivers get a map and a toggle; bus and private-vehicle ' +
    'drivers get two buttons.',
  'www.guide.d06.step1':
    'TODO(ta) With a standby vehicle selected, home is a full-screen map with your details ' +
    'across the top: your level, your rating, your wallet balance, and today’s daily fee — ' +
    'whether it has been taken, and how much it is for your vehicle.',
  'www.guide.d06.step2':
    'TODO(ta) The map shows one vehicle, and it is yours. Other drivers are never drawn on ' +
    'it, so a quiet-looking map is a normal map rather than a fault.',
  'www.guide.d06.step3':
    'TODO(ta) There is no menu button in the top corner. Everything else in the app — ' +
    'vehicles, wallet, jobs, history, support — is behind the Menu tab along the bottom.',
  'www.guide.d06.step3.note':
    'TODO(ta) This catches nearly everybody once. If you are hunting for something, look at ' +
    'the bottom of the screen rather than the top.',
  'www.guide.d06.step4':
    'TODO(ta) The panel over the map carries the standby toggle, the vehicle currently live ' +
    'with its registration number, whether your first trip today is still free, the way into ' +
    'directional travel, and your trips and earnings so far today.',
  'www.guide.d06.step5':
    'TODO(ta) Select a bus or a private vehicle instead and home is a different screen ' +
    'altogether. It has a route card, a running duration and distance, the vehicle type and ' +
    'number below the card, and two buttons: Start Journey and End Journey. No standby map, ' +
    'no toggle — those vehicles are not hailed.',
  'www.guide.d06.step6':
    'TODO(ta) A public bus pays no daily fee at all. A private vehicle is charged monthly ' +
    'rather than daily.',
  'www.guide.d06.step7':
    'TODO(ta) If a GPS tracker is fitted and the ignition is on, the journey has already ' +
    'started before you open the app, and the screen offers you End Journey.',
  'www.guide.d06.step7.note':
    'TODO(ta) The device does not lock you out. Start Journey and End Journey work from this ' +
    'screen whatever the tracker is doing, in both directions.',
  'www.guide.d06.callout.whichHomeScreen':
    'TODO(ta) Which home screen you see follows the vehicle you have selected, not a setting ' +
    'you can change. Switch the live vehicle in My Vehicles and the home screen switches ' +
    'with it.',
  'www.guide.d06.callout.whoPaysWhat':
    'TODO(ta) Public buses pay no platform fee. Private vehicles pay a monthly charge of ' +
    'about Rs 300 — the specification says approximately, so we do too. Standby vehicles pay ' +
    'one flat fee a day, set by vehicle type, and only on the days they work. The amounts ' +
    'have a chapter of their own.',
  'www.guide.d06.callout.ownVehicleOnly':
    'TODO(ta) Your dashboard map is scoped to you: your own active vehicle is the only ' +
    'vehicle drawn on it, and other drivers are never shown there.',
  'www.guide.d07.title': 'TODO(ta) Going on standby',
  'www.guide.d07.summary':
    'TODO(ta) One toggle — and everything worth knowing about it is a condition. When it is ' +
    'grey, what the first trip of the day costs, and why an offer can pass you by without a ' +
    'sound.',
  'www.guide.d07.step1':
    'TODO(ta) Standby is the large toggle on the dashboard. Turned on, you join the pool of ' +
    'drivers the system can send hires to; turned off, a grey overlay tells you so.',
  'www.guide.d07.step2':
    'TODO(ta) The toggle stays disabled until you have a vehicle available to drive — one ' +
    'you own that has been approved, or one shared or temporarily assigned to you by a ' +
    'fleet.',
  'www.guide.d07.step2.note':
    'TODO(ta) With no vehicle at all, the app offers to register one. A fleet assignment ' +
    'simply expires on its own date and asks nothing of you.',
  'www.guide.d07.step3':
    'TODO(ta) The first trip of every day is free. Nothing is taken from your wallet for it, ' +
    'on any vehicle, on any day.',
  'www.guide.d07.step4':
    'TODO(ta) The daily fee is taken before your second trip of the day, as one flat amount ' +
    'set by your vehicle type. After that the day is paid for however many trips follow, and ' +
    'on a day you never go online you are charged nothing at all.',
  'www.guide.d07.step5':
    'TODO(ta) If your wallet cannot cover it when a second request comes up, the request is ' +
    'missed rather than refused, and you are told that is why. An empty wallet does not look ' +
    'like an error — it looks like a quiet afternoon.',
  'www.guide.d07.step6':
    'TODO(ta) MageRide warns you when your balance falls to Rs 200, and you can set your own ' +
    'figure in the app if you would rather hear about it earlier.',
  'www.guide.d07.step7':
    'TODO(ta) Only one of your vehicles is live at a time — the one selected in My Vehicles. ' +
    'Going offline also clears a directional-travel filter if you had one running, and ' +
    'setting it again spends another of that day’s uses.',
  'www.guide.d07.callout.firstTripFree':
    'TODO(ta) First trip of the day free, then one flat fee for the whole day, set by ' +
    'vehicle type. There is no commission and no per-trip charge — the fare a passenger pays ' +
    'you is yours. On a day you do not go online there is no fee at all.',
  'www.guide.d07.callout.lowBalance':
    'TODO(ta) A wallet that cannot cover the daily fee does not take you offline; it stops ' +
    'requests reaching you from the second trip onwards, and the only sign of it is that ' +
    'nothing arrives. Top up before you start the day rather than during it.',
  'www.guide.d07.callout.oneVehicleLive':
    'TODO(ta) One vehicle publishes at a time. Whichever you select in My Vehicles is the ' +
    'one on the map, the one taking hires, and the one whose daily rate you are charged.',
  'www.guide.d08.title': 'TODO(ta) The fifteen-second offer',
  'www.guide.d08.summary':
    'TODO(ta) A ride offer takes over the screen with a countdown ring on it. What is on the ' +
    'card, what happens when you accept — and what it actually costs you to let one go.',
  'www.guide.d08.step1':
    'TODO(ta) An offer arrives as a full-screen takeover, with sound and vibration, and it ' +
    'will wake a sleeping phone to do it.',
  'www.guide.d08.step2':
    'TODO(ta) The card carries the fare, how far away the pickup is, the vehicle category ' +
    'the passenger asked for, how they have chosen to pay, and the pickup and drop-off. ' +
    'Badges tell you when it is a booking made by one person for another, a package with its ' +
    'size, or a hire that matched your directional filter.',
  'www.guide.d08.step3':
    'TODO(ta) A ring counts down from fifteen seconds, and the last five pulse red.',
  'www.guide.d08.step3.note':
    'TODO(ta) If it is your second trip of the day, a line on the card tells you the daily ' +
    'fee will come out of your wallet the moment you accept.',
  'www.guide.d08.step4':
    'TODO(ta) Accept takes the hire and opens the trip screen. Occasionally you will be told ' +
    'the offer was taken instead — two taps can land at once and only one can win, and the ' +
    'app says so plainly rather than half-assigning you.',
  'www.guide.d08.step5':
    'TODO(ta) Reject passes the hire to the next eligible driver immediately. Letting the ' +
    'fifteen seconds run out does the same thing a moment later.',
  'www.guide.d08.step6':
    'TODO(ta) A missed or rejected offer carries no penalty — not the first, and no ' +
    'published rule attaches a fine, a suspension or a cooling-off period to any of them. ' +
    'What a run of declines does change is your acceptance rate, shown to you on your own ' +
    'level screen. Nothing states where that number starts to matter, so this guide does not ' +
    'guess.',
  'www.guide.d08.step7':
    'TODO(ta) Rides booked in advance reach you through the job board: every scheduled ride ' +
    'within thirty kilometres, open to drivers at level 2 and above. You post intent on the ' +
    'ones you want. You cannot accept from the board itself.',
  'www.guide.d08.step8':
    'TODO(ta) Thirty minutes before the ride it is offered to the closest driver who posted ' +
    'intent, on this same fifteen-second screen — and where two are equally close, the ' +
    'higher level is rung first. That is where it is accepted, and where it can still be ' +
    'turned down.',
  'www.guide.d08.callout.whatAMissCosts':
    'TODO(ta) Letting an offer pass costs you nothing. It goes to the next driver, and there ' +
    'is no penalty for the first, the second or the tenth. A pattern of declines shows up as ' +
    'your acceptance rate on your level screen, and nothing published sets a point at which ' +
    'that rate does anything more than that.',
  'www.guide.d08.callout.secondTripFee':
    'TODO(ta) The daily fee leaves your wallet the moment you accept the second hire of the ' +
    'day — not at the end of it, and not again for the trips after it. The amount follows ' +
    'your vehicle type and is charged once for the whole day.',
  'www.guide.d08.callout.offerTaken':
    'TODO(ta) Offers go to one driver at a time rather than to everybody at once, which is ' +
    'why “offer taken” is rare. When it does appear, another driver’s tap landed first; ' +
    'nothing is held against you and the next offer is unaffected.',
  'www.guide.d09.title': 'TODO(ta) Running a trip',
  'www.guide.d09.summary':
    'TODO(ta) From accepting to ending: navigating to the pickup, the code that starts the ' +
    'trip, calling the passenger, and what happens when you tap End.',
  'www.guide.d09.step1':
    'TODO(ta) Accepting opens the trip screen — a navigation map to the pickup, the distance ' +
    'and the time to it, the passenger’s name and rating, and where they are going.',
  'www.guide.d09.step2':
    'TODO(ta) Drive to the pickup. Once you are within the pickup area the trip moves to ' +
    'arrived on its own; there is nothing to tap to say you have got there.',
  'www.guide.d09.step3':
    'TODO(ta) Ask the passenger for their start code and type it in. The code is on their ' +
    'screen. The trip does not start until it is entered.',
  'www.guide.d09.step3.note':
    'TODO(ta) A wrong code says so and nothing is lost — ask again and re-enter it. If you ' +
    'drive off without it, the trip has not started and is not recorded as in progress.',
  'www.guide.d09.step4':
    'TODO(ta) If they are not there, wait. After five minutes and two reminder messages they ' +
    'count as a no-show: they are charged Rs 100 and you are compensated for the wait.',
  'www.guide.d09.step5':
    'TODO(ta) Call rider is on the trip screen. You can call free through the app, or place ' +
    'an ordinary call to their number.',
  'www.guide.d09.step5.note':
    'TODO(ta) Numbers are not hidden. Once you have accepted, you and the passenger can see ' +
    'each other’s mobile numbers — and on a booking one person made for another, you see the ' +
    'rider you are collecting and never the person who booked.',
  'www.guide.d09.step6':
    'TODO(ta) Drive to the destination and tap End. The fare is finalised at that point and ' +
    'settled by whichever way the passenger chose when they booked.',
  'www.guide.d09.step7':
    'TODO(ta) If they paid by scanning your own QR code, the app asks you to confirm you ' +
    'received it. Your earning is posted once the payment is final rather than the moment ' +
    'the trip ends, which is why a finished trip can show its money a little afterwards.',
  'www.guide.d09.step8':
    'TODO(ta) A ride booked in advance runs exactly like this one. It waits in your ' +
    'scheduled rides list, you are reminded thirty minutes before, and then it is the start ' +
    'code, the drive, and End, like any other trip.',
  'www.guide.d09.callout.noCodeNoTrip':
    'TODO(ta) The trip will not start without the passenger’s code. It is not optional and ' +
    'it is not a formality — it is also the thing a new driver is most likely to call ' +
    'support about. Ask for it as you greet them.',
  'www.guide.d09.callout.realNumbers':
    'TODO(ta) You and your passenger can see each other’s real mobile numbers once you have ' +
    'accepted the hire, and MageRide tells you so when you sign up. Numbers are withheld for ' +
    'a ride cancelled before anybody was assigned, and on a booking made for someone else ' +
    'you get the rider’s number and never the booker’s.',
  'www.guide.d09.callout.scheduledNoShow':
    'TODO(ta) Failing to turn up for a scheduled ride you accepted drops your driver level ' +
    'by one. It is one of only two things that does — the other is collecting three ' +
    'passenger reports — and letting an ordinary offer pass is not one of them.',

  'www.guide.d10.title': 'TODO(ta) Driving towards home',
  'www.guide.d10.summary':
    'TODO(ta) Directional travel narrows the hires you are offered to the ones going your ' +
    'way. It is worth understanding before you use it, because it is limited, it expires, ' +
    'and switching it off early still spends one of the day’s turns.',
  'www.guide.d10.step1':
    'TODO(ta) While you are online, open Directional Travel from the dashboard. It is the ' +
    'chip beside the standby toggle.',
  'www.guide.d10.step2':
    'TODO(ta) Choose where you are heading — search for an address, drop a pin on the ' +
    'map, or pick Home if you have saved one. A marker shows the direction it works out ' +
    'from where you are now.',
  'www.guide.d10.step3':
    'TODO(ta) The screen tells you how many turns you have left today and how long a turn ' +
    'lasts before you commit to one. Tap Set Direction and it starts.',
  'www.guide.d10.step3.note':
    'TODO(ta) Today MageRide allows two a day, each lasting up to two hours. Both are ' +
    'settings rather than fixed rules, and the screen always shows the numbers that apply ' +
    'to you.',
  'www.guide.d10.step4':
    'TODO(ta) A banner then stays on your screen for as long as it is running, showing ' +
    'your destination, the time left, and the turns you have not used. You cannot forget ' +
    'it is on, which is deliberate.',
  'www.guide.d10.step5':
    'TODO(ta) From then on you are only offered hires that head your way: the trip has to ' +
    'move you towards your destination, the pickup has to be roughly on your route, and ' +
    'the drop-off has to leave you closer to where you are going than the pickup did.',
  'www.guide.d10.step6':
    'TODO(ta) Ten minutes before it expires you get a reminder, and the banner starts ' +
    'pulsing. When it does expire it clears itself and you go back to receiving ' +
    'everything you are eligible for.',
  'www.guide.d10.step6.note':
    'TODO(ta) Accepting a matching hire does not end it — it keeps running so you can ' +
    'chain another in the same direction. MageRide can configure it to clear after the ' +
    'first matched trip instead, and the app tells you which way yours is set.',
  'www.guide.d10.step7':
    'TODO(ta) Turning it off early ends it immediately — and still spends the turn. So ' +
    'does going offline, which clears it without asking. Set it when you are genuinely ' +
    'done for the day rather than to see what it does.',
  'www.guide.d10.step8':
    'TODO(ta) It works the same way for package jobs as for passenger rides, and it never ' +
    'overrides anything else: a hire you would not have been offered anyway does not ' +
    'become available because you are heading that way.',
  'www.guide.d10.callout.turningItOffCosts':
    'TODO(ta) Switching directional travel off before it expires still uses up that turn, ' +
    'and so does going offline while it is running. It is written that way on purpose, to ' +
    'stop the daily limit being worked around, and it is the thing drivers are most often ' +
    'caught by.',
  'www.guide.d10.callout.narrowsNeverWidens':
    'TODO(ta) Directional travel only removes offers; it never adds one. It does not ' +
    'change your fare, the daily fee, or your place in the queue — it filters what ' +
    'reaches you and nothing else.',
  'www.guide.d10.callout.noPenalty':
    'TODO(ta) If no hires in your direction come up while it is running, nothing happens ' +
    'to you. There is no effect on your driver level and none on your acceptance rate — a ' +
    'quiet hour with the filter on costs you nothing but the hour.',
  'www.guide.d11.title': 'TODO(ta) Delivering a package',
  'www.guide.d11.summary':
    'TODO(ta) A parcel arrives as an ordinary offer with a package badge on it, and then ' +
    'runs as three sheets and two codes: review and start, collect, deliver.',
  'www.guide.d11.step1':
    'TODO(ta) Package jobs reach you the same way rides do — the same fifteen-second ' +
    'offer, with a Package badge, the size, and a short description of what is inside. ' +
    'You can turn it down on the strength of that.',
  'www.guide.d11.step1.note':
    'TODO(ta) Deliveries are not limited to any one kind of vehicle. A motorbike carries ' +
    'parcels; so does a van. Trucks and mini trucks exist for deliveries in addition to ' +
    'the vehicles that carry passengers, not instead of them.',
  'www.guide.d11.step2':
    'TODO(ta) Accepting opens the first of three sheets: review and start. It shows how ' +
    'far the pickup is, how far the drop is, how the delivery is being paid for, and both ' +
    'the sender’s and the recipient’s phone numbers, each with a call button.',
  'www.guide.d11.step3':
    'TODO(ta) If it suits you, tap Start delivery. If it does not, tap Cancel — the job ' +
    'goes straight to the next eligible driver and nothing is held against you.',
  'www.guide.d11.step3.note':
    'TODO(ta) This is the moment to decide. Cancelling here is ordinary; abandoning a ' +
    'parcel you have already collected is not, and there is no sheet for it.',
  'www.guide.d11.step4':
    'TODO(ta) The second sheet takes you to the sender. It carries the map, a Call sender ' +
    'button, SOS, and a four-digit pickup code.',
  'www.guide.d11.step5':
    'TODO(ta) Ask the sender for that code and enter it. The parcel becomes collected, ' +
    'the recipient is told it is on its way, and the third sheet opens. A wrong code says ' +
    'so; after five wrong attempts it locks and goes to support.',
  'www.guide.d11.step6':
    'TODO(ta) At the door, ask the recipient for their own four-digit delivery code and ' +
    'enter it. Both phone numbers are on this sheet too, with call buttons.',
  'www.guide.d11.step7':
    'TODO(ta) If nobody is there to give you a code, photo proof stands in for it. ' +
    'Photograph the parcel where you left it — the picture is attached, with its ' +
    'location, as the record that you delivered it.',
  'www.guide.d11.step8':
    'TODO(ta) Then tap Delivery completed. That one button finishes the job, whether the ' +
    'parcel was paid for in advance or in cash at the door — there is no separate “cash ' +
    'received” button any more.',
  'www.guide.d11.callout.codAndAbsentRecipient':
    'TODO(ta) On a cash-on-delivery parcel, collect the cash before you complete it, and ' +
    'if there is nobody there to pay, do not leave the parcel and do not complete the ' +
    'delivery — contact support. Cash that is never accounted for turns the delivery into ' +
    'a dispute after a day.',
  'www.guide.d11.callout.sameFeeSameTariff':
    'TODO(ta) A delivery is charged to the passenger on the same tariff as a ride, and it ' +
    'counts towards your day the same way: your first job of the day is free whether it ' +
    'is a parcel or a person, and deliveries and rides are counted together for the daily ' +
    'fee.',
  'www.guide.d11.callout.cancelAtReview':
    'TODO(ta) The review sheet exists so you can say no after seeing the details. Cancel ' +
    'there and the job is offered to the next driver immediately — that is the sheet ' +
    'working, not a failure.',
  'www.guide.d12.title': 'TODO(ta) Your wallet',
  'www.guide.d12.summary':
    'TODO(ta) The wallet is not where your fares land — it is money you put in, that the ' +
    'daily platform fee comes out of. Three ways to top it up, all inside the app.',
  'www.guide.d12.step1':
    'TODO(ta) Wallet is on the bottom bar. It shows a balance, the daily rate for the ' +
    'vehicle you have live, and whether today’s fee has been taken yet.',
  'www.guide.d12.step1.note':
    'TODO(ta) The balance is not your earnings. A cash fare goes into your hand and a ' +
    'fare paid to your own bank QR goes into your bank — neither passes through here. ' +
    'Chapter 14 is about where your money actually arrives.',
  'www.guide.d12.step2':
    'TODO(ta) The balance is read-only on this screen. It changes when you top up, when a ' +
    'daily fee is taken, and when credit moves between you and another driver.',
  'www.guide.d12.step3':
    'TODO(ta) Top Up opens three ways to pay: a credit or debit card, OnePay, or LankaQR. ' +
    'All three are inside the app and all three credit your wallet immediately.',
  'www.guide.d12.step4':
    'TODO(ta) LankaQR carries no surcharge. Card and OnePay carry OnePay’s processing ' +
    'fee, which is the payment company’s charge for taking the payment and not a MageRide ' +
    'commission.',
  'www.guide.d12.step4.note':
    'TODO(ta) There is no bank transfer option, and there never will be one — it was ' +
    'removed from the platform. If someone gives you a MageRide bank account to pay into, ' +
    'it is not MageRide.',
  'www.guide.d12.step5':
    'TODO(ta) The same screen sells bulk credit vouchers at a discount, which is chapter ' +
    '15. Buying one is simply a larger top-up that costs you less than its face value.',
  'www.guide.d12.step6':
    'TODO(ta) Every top-up ends with a confirmation carrying a reference number, and you ' +
    'can save or share the receipt. Your whole history is under Payment history, and you ' +
    'can download a statement for any date range.',
  'www.guide.d12.step7':
    'TODO(ta) MageRide warns you when the balance drops to its low-balance level, and you ' +
    'can set your own figure if you would rather be told earlier.',
  'www.guide.d12.step8':
    'TODO(ta) A balance can go below zero — an adjustment made by MageRide, or a charge ' +
    'for a ride cancelled after you accepted it, can take it there. The screen then shows ' +
    'what you need to add to start working again.',
  'www.guide.d12.callout.noBankTransfer':
    'TODO(ta) Card, OnePay and LankaQR are the only ways to top up a MageRide wallet. ' +
    'Bank transfer is not one of them — it was removed — so a request to transfer money ' +
    'to a MageRide bank account is not coming from MageRide.',
  'www.guide.d12.callout.neverAWebPortal':
    'TODO(ta) Everything to do with your wallet happens inside the driver app. There is ' +
    'no MageRide website you log into as a driver, so a page asking for your MageRide ' +
    'sign-in is not ours, whatever it looks like.',
  'www.guide.d12.callout.whatTheWalletIsFor':
    'TODO(ta) The wallet exists to pay the daily platform fee, and it is the only thing ' +
    'MageRide ever takes from you. There is no commission on any fare — what a passenger ' +
    'pays you is yours.',
  'www.guide.d13.title': 'TODO(ta) The daily platform fee',
  'www.guide.d13.summary':
    'TODO(ta) MageRide takes no commission. Passengers pay their fares directly to you, ' +
    'and the platform charges a flat fee for the day — never for the trip. Your first ' +
    'trip of every day is free.',
  'www.guide.d13.step1':
    'TODO(ta) You keep every rupee of every fare. MageRide takes no commission and no cut ' +
    'of any trip, on any vehicle, in any mode. This is the same model Namma Yatri built ' +
    'in India, and it is the whole basis of the platform.',
  'www.guide.d13.step2':
    'TODO(ta) The first trip of the day is always free. Nothing is taken from your wallet ' +
    'for it.',
  'www.guide.d13.step3':
    'TODO(ta) When you accept your second trip of the day, one flat fee is taken from ' +
    'your wallet — once, for the whole day, however many trips follow it.',
  'www.guide.d13.step3.note':
    'TODO(ta) Because the deduction lands at the second trip, it can look like a per-trip ' +
    'charge exactly once. It is not. There is no per-trip fee anywhere on MageRide.',
  'www.guide.d13.step4':
    'TODO(ta) What that flat fee is depends on what you drive. Each vehicle type has its ' +
    'own rate, and the table on this page is the full set. The app always shows the rate ' +
    'for the vehicle you currently have live.',
  'www.guide.d13.step5':
    'TODO(ta) Public transport buses pay nothing at all, and private transport vehicles ' +
    'are billed monthly to their owner rather than daily to their driver. The daily fee ' +
    'is a standby on-demand arrangement only.',
  'www.guide.d13.step6':
    'TODO(ta) On a day you never go online, you are charged nothing. There is no monthly ' +
    'minimum, no subscription that runs whether you drive or not, and no charge for a day ' +
    'you spend off the road.',
  'www.guide.d13.step6.note':
    'TODO(ta) If you keep more than one vehicle, only one is live at a time and you pay ' +
    'that vehicle’s rate. You are never charged twice for the same vehicle on the same ' +
    'day.',
  'www.guide.d13.step7':
    'TODO(ta) If your wallet cannot cover the fee when your second trip comes up, that ' +
    'request is missed rather than refused, and you are told that is why. You are also ' +
    'warned when you accept the first trip if you do not have enough for the second.',
  'www.guide.d13.step8':
    'TODO(ta) Fee history shows every deduction — the date, the vehicle, the amount, and ' +
    'how many trips you did that day — beside your top-ups and transfers. If a fee was ' +
    'taken in error, chapter 18 is how you ask for it back.',
  'www.guide.d13.callout.zeroCommission':
    'TODO(ta) Zero commission, and it is meant literally: passengers pay their fares ' +
    'directly to you and MageRide takes none of it. The daily platform fee is the only ' +
    'money the platform charges a driver, and it is a fee for the day rather than a share ' +
    'of anything you earn.',
  'www.guide.d13.callout.oncePerDay':
    'TODO(ta) One flat charge per day, taken before your second trip, whatever the ' +
    'vehicle and however long the day. Unlimited trips after it. Nothing on days you do ' +
    'not go online, and nothing ever for the first trip of a day.',
  'www.guide.d13.callout.ratesAreConfigurable':
    'TODO(ta) These rates are set by MageRide and can change. The table here is what the ' +
    'specification states today; the rate that will actually be charged to you is the one ' +
    'shown on your wallet screen for the vehicle you have live.',
  'www.guide.d14.title': 'TODO(ta) Getting paid',
  'www.guide.d14.summary':
    'TODO(ta) Where the money for a trip actually goes — into your hand, into your own ' +
    'bank account, or into your MageRide wallet — and what happens to it after that.',
  'www.guide.d14.step1':
    'TODO(ta) A passenger chooses how to pay when they book, and can change it at the ' +
    'end. What changes for you is not the amount but where the money lands.',
  'www.guide.d14.step2':
    'TODO(ta) Cash is the simplest and the most common. The passenger hands you the fare, ' +
    'you tap End, and it is done. That money is yours immediately — it never goes near ' +
    'MageRide, so there is nothing to wait for and nothing to be paid out.',
  'www.guide.d14.step2.note':
    'TODO(ta) MageRide takes no commission from it and never sees it. The only thing the ' +
    'platform charges you is the daily fee, out of your wallet.',
  'www.guide.d14.step3':
    'TODO(ta) The second way is your own QR code — your own bank’s code, registered with ' +
    'MageRide and shown to the passenger to scan. That payment goes bank to bank, ' +
    'straight into your account, and MageRide never handles it either.',
  'www.guide.d14.step4':
    'TODO(ta) Because it goes bank to bank, MageRide is never told it arrived. So the ' +
    'trip is closed by the two of you saying so: the passenger taps “I’ve paid”, you get ' +
    'a “QR payment received?” prompt, and you tap Confirm.',
  'www.guide.d14.step4.note':
    'TODO(ta) You can confirm on your own if the passenger has already left. If they say ' +
    'they paid and you do not confirm, the app reminds you, and an unresolved case goes ' +
    'to support and then to MageRide’s finance team. No money is moved either way while ' +
    'that happens, because MageRide is not holding any.',
  'www.guide.d14.step5':
    'TODO(ta) Your earning is posted once the payment is settled rather than the moment ' +
    'you tap End. That is why a finished trip can show its money a little afterwards.',
  'www.guide.d14.step6':
    'TODO(ta) The third way is a passenger paying from their own MageRide balance. That ' +
    'one does pass through MageRide: it moves from their balance to your wallet ' +
    'immediately, and it sits in your wallet until it is paid out to your bank.',
  'www.guide.d14.step7':
    'TODO(ta) Paying that out needs your bank details on file — bank, branch, account ' +
    'number and the name on the account, with a bank statement or the first page of your ' +
    'passbook, and your bank app’s QR image. Someone at MageRide checks it, and any edit ' +
    'sends it back to be checked again.',
  'www.guide.d14.step7.note':
    'TODO(ta) That same screen is where your QR code comes from, so filling it in is also ' +
    'what makes the second way of being paid possible. Until it is approved your balance ' +
    'simply builds up — it is held for you and never lost — but nothing leaves.',
  'www.guide.d14.step8':
    'TODO(ta) Earnings shows today, this week and this month: what you took in fares, ' +
    'what the daily fee cost you, and the difference. Every trip is listed underneath it ' +
    'with its own fare.',
  'www.guide.d14.callout.cashIsYours':
    'TODO(ta) Cash fares and fares paid to your own QR code never pass through MageRide. ' +
    'There is nothing to withdraw, nothing to wait for, and no commission taken from ' +
    'either — the money is with you the moment the trip ends.',
  'www.guide.d14.callout.qrIsAttested':
    'TODO(ta) A QR payment into your own bank produces no message to MageRide, which is ' +
    'why you are asked to confirm it. Confirm only what you have actually received: your ' +
    'confirmation is what closes the trip, and a disputed one is settled by people ' +
    'looking at the evidence rather than by a system reversing a payment nobody holds.',
  'www.guide.d14.callout.payoutsCoverTheWallet':
    'TODO(ta) A payout only ever covers what is sitting in your MageRide wallet — never ' +
    'your cash fares and never your QR fares, which have already reached you. The run is ' +
    'designed to go out weekly and to pay the whole balance with no minimum, and it needs ' +
    'your bank details approved first. MageRide will confirm when payouts start; until ' +
    'then, treat cash and your own QR as the money you can count on the same day.',
  'www.guide.d15.title': 'TODO(ta) Bulk credit, and passing it on',
  'www.guide.d15.summary':
    'TODO(ta) Buying credit in bulk costs less than its face value, and any driver can ' +
    'send credit to any other driver. There is no reseller account, no code, and no ' +
    'commission.',
  'www.guide.d15.step1':
    'TODO(ta) Any driver holding wallet credit can transfer it to any other driver. That ' +
    'is all a “reseller” is on MageRide — a driver who bought credit cheaply. There is no ' +
    'separate account to open and no permission to be granted.',
  'www.guide.d15.step2':
    'TODO(ta) Bulk credit vouchers are on the top-up screen, in five fixed sizes from Rs ' +
    '1,000 up to Rs 10,000. You pay less than the voucher is worth and your wallet is ' +
    'credited the full face value straight away.',
  'www.guide.d15.step2.note':
    'TODO(ta) The discount is set by MageRide, differs by size, and larger vouchers ' +
    'usually earn a bigger one. The current rates are on the tiles — this page does not ' +
    'quote them, because they can be changed at any time.',
  'www.guide.d15.step3':
    'TODO(ta) There is no code to redeem. The credit is in your own wallet from the ' +
    'moment you pay, and you can use it for your own daily fees or pass it on.',
  'www.guide.d15.step4':
    'TODO(ta) To get credit from another driver, open Request credit and enter their ' +
    'Driver ID and the amount. They get a notification and either approve or decline it.',
  'www.guide.d15.step4.note':
    'TODO(ta) A Driver ID, typed in — nothing is scanned. QR scanning was removed from ' +
    'this screen, and there are no special reseller codes to enter.',
  'www.guide.d15.step5':
    'TODO(ta) On the other side, incoming requests arrive as notifications and appear on ' +
    'the Credit transfer screen with the requesting driver’s name, vehicle and amount. ' +
    'Approve or decline each one.',
  'www.guide.d15.step6':
    'TODO(ta) You can also send credit without being asked — enter a Driver ID and an ' +
    'amount on the same screen and send it.',
  'www.guide.d15.step7':
    'TODO(ta) Whichever way it goes, the exact amount leaves one wallet and arrives in ' +
    'the other. MageRide takes no commission on a transfer and no cut of any kind. Both ' +
    'sides are recorded, and both drivers can see the transfer in their history.',
  'www.guide.d15.step8':
    'TODO(ta) A transfer is blocked if your balance cannot cover it. And a transfer that ' +
    'has gone through cannot be pulled back — check who you are sending to first.',
  'www.guide.d15.callout.noCommission':
    'TODO(ta) Nothing is deducted from a driver-to-driver transfer. Send Rs 1,000 and Rs ' +
    '1,000 arrives. A driver who resells credit makes their margin on the discount they ' +
    'got when they bought it, not from a charge on you.',
  'www.guide.d15.callout.noResellerAccount':
    'TODO(ta) There is no reseller account, no reseller login and no reseller code on ' +
    'MageRide. Anybody offering to sell you one is selling you something that does not ' +
    'exist.',
  'www.guide.d15.callout.checkTheDriverId':
    'TODO(ta) Confirm a Driver ID with the driver themselves before approving a request ' +
    'or sending credit. Credit moves immediately and in full, and there is no way to ' +
    'reverse it from the app.',
  'www.guide.d16.title': 'TODO(ta) Driving a bus or a private vehicle',
  'www.guide.d16.summary':
    'TODO(ta) Journeys instead of hires, no daily fee, and what changes once a GPS device ' +
    'is fitted — including the parts you no longer have to do yourself.',
  'www.guide.d16.step1':
    'TODO(ta) A public bus or a private vehicle does not receive ride offers at all. Your ' +
    'screen is Start Journey and End Journey, with the route, your running time and ' +
    'distance, and the vehicle type and number below the route card.',
  'www.guide.d16.step1.note':
    'TODO(ta) Trains are public transport too, but only MageRide registers a train. There ' +
    'is no train option anywhere in the driver app, and that is deliberate rather than ' +
    'missing.',
  'www.guide.d16.step2':
    'TODO(ta) Start Journey when you set off and End Journey when you finish. That is the ' +
    'whole of the daily routine — there is no fare to settle and no code to enter.',
  'www.guide.d16.step3':
    'TODO(ta) If you forget to end one, it ends itself after thirty minutes without ' +
    'movement and tells you why. You have five minutes to restart it if that was wrong. ' +
    'You can also switch on ending automatically when you arrive back where your last ' +
    'journey finished.',
  'www.guide.d16.step3.note':
    'TODO(ta) None of this costs anything. A public bus pays no daily platform fee, and a ' +
    'private vehicle is billed monthly to whoever owns it rather than daily to whoever ' +
    'drives it.',
  'www.guide.d16.step4':
    'TODO(ta) A GPS device changes who reports the vehicle’s position. Pair one from Pair ' +
    'GPS tracker: choose the vehicle, then type its IMEI number, scan the code on the ' +
    'device, or enter a bind code MageRide gave you.',
  'www.guide.d16.step5':
    'TODO(ta) From then on the device is the only thing publishing that vehicle’s ' +
    'position — your phone stops sending it. On a bus or a private vehicle the journey ' +
    'then starts when the engine is switched on and ends when it is switched off, with no ' +
    'app involved at all.',
  'www.guide.d16.step6':
    'TODO(ta) So you may open the app and find the journey already running. That is the ' +
    'device reporting, not a mistake — and you can still override it. Start Journey and ' +
    'End Journey on your dashboard work whatever the device is doing.',
  'www.guide.d16.step6.note':
    'TODO(ta) On a standby vehicle the same device behaves differently: its position is ' +
    'only used while you are online. Two vehicles, one device, two behaviours — worth ' +
    'knowing if you drive both.',
  'www.guide.d16.step7':
    'TODO(ta) Sharing controls who can follow a private vehicle, and it is kept ' +
    'separately for each vehicle you hold. Pick the vehicle at the top, then grant access ' +
    'by User ID with an expiry date, or accept the requests waiting under it.',
  'www.guide.d16.step8':
    'TODO(ta) A fleet can also assign you a vehicle without your owning it. It appears in ' +
    'My Vehicles under “temporarily assigned”, you select it and drive, and it disappears ' +
    'on its own when the assignment runs out.',
  'www.guide.d16.step8.note':
    'TODO(ta) If a fleet takes an assignment back, the vehicle leaves your list. The ' +
    'fleet that assigned it is who to ask about that, not MageRide support.',
  'www.guide.d16.callout.ignitionStartsIt':
    'TODO(ta) With a GPS device fitted to a bus or a private vehicle you do not have to ' +
    'start the journey, end it, or keep the app open — the ignition does all three. What ' +
    'you do keep is control: the buttons on your dashboard override the device in both ' +
    'directions.',
  'www.guide.d16.callout.youSeeTheirNumber':
    'TODO(ta) When a passenger asks to follow your private vehicle you are shown their ' +
    'name and mobile number, so you know who you are letting in — and they are told that ' +
    'is how it works. The same details stay on your list of who currently has access.',
  'www.guide.d16.callout.noDailyFeeHere':
    'TODO(ta) There is no daily platform fee on a public bus, and no wallet balance to ' +
    'keep up before you can drive one. The daily fee belongs to standby on-demand driving ' +
    'and to nothing else.',
  'www.guide.d17.title': 'TODO(ta) Ratings and your driver level',
  'www.guide.d17.summary':
    'TODO(ta) How the star ratings become points, what a level is worth, and the two ' +
    'things — only two — that take a level away.',
  'www.guide.d17.step1':
    'TODO(ta) After a completed trip a passenger can rate you one to five stars and leave ' +
    'a comment. Your overall rating and every trip’s rating are on your profile.',
  'www.guide.d17.step2':
    'TODO(ta) You can rate the passenger too. It opens from the trip’s row in your ride ' +
    'history rather than at the drop-off — one to five stars, with a comment if you want ' +
    'one.',
  'www.guide.d17.step2.note':
    'TODO(ta) It is not offered the moment a trip ends, so if you looked for it there and ' +
    'found nothing, it is in your history rather than missing.',
  'www.guide.d17.step3':
    'TODO(ta) Every driver starts at level 3. Good ratings become points: a five-star is ' +
    'worth five and a four-star is worth four, two stars and below are worth nothing, and ' +
    'five hundred points is one level.',
  'www.guide.d17.step4':
    'TODO(ta) The level screen shows where you are — your badge, the points bar to the ' +
    'next level, your acceptance rate and your no-shows.',
  'www.guide.d17.step5':
    'TODO(ta) Your level is one of the things that decides who is offered a hire, ' +
    'alongside how close you are to the pickup and whether you drive what the passenger ' +
    'asked for. How much weight each carries is set by MageRide and is not published, so ' +
    'no one can tell you what a level is worth in rides.',
  'www.guide.d17.step6':
    'TODO(ta) Two things take a level away, and there are only two: not turning up for a ' +
    'scheduled ride you accepted, and collecting three passenger reports — which also ' +
    'delists you temporarily while it is looked at.',
  'www.guide.d17.step6.note':
    'TODO(ta) Letting an ordinary offer pass is not one of them. Neither is turning one ' +
    'down. That is chapter 8, and it has not changed here.',
  'www.guide.d17.step7':
    'TODO(ta) At level 1 you lose the job board and scheduled rides until you climb back. ' +
    'You can still go online and take hires as they come — it is a restriction on booking ' +
    'ahead, not a suspension.',
  'www.guide.d17.step8':
    'TODO(ta) On a scheduled ride where two drivers who posted intent are equally close, ' +
    'the higher level is offered it first. That is the one place a level is stated to ' +
    'break a tie.',
  'www.guide.d17.callout.whatLevelChanges':
    'TODO(ta) Your level is one of three inputs to who gets offered a hire — distance, ' +
    'level and vehicle type — and it decides who is rung first when two drivers are ' +
    'equally close to a scheduled job. Nothing published says how much it is worth, and ' +
    'this guide will not guess.',
  'www.guide.d17.callout.twoWaysToDrop':
    'TODO(ta) Only two things drop a level: failing to appear for a scheduled ride you ' +
    'accepted, and three passenger reports. Everything else about your driving affects ' +
    'your rating, and your rating is what climbs you back up.',
  'www.guide.d17.callout.acceptanceRate':
    'TODO(ta) Your acceptance rate is shown to you on your level screen. Nothing ' +
    'published attaches any consequence to it — no penalty, no threshold, no cooling-off ' +
    'period — so it is information about your own driving rather than a target you are ' +
    'being held to.',
  'www.guide.d18.title': 'TODO(ta) Safety, help and updates',
  'www.guide.d18.summary':
    'TODO(ta) The emergency contact to set before you need it, what SOS actually does, ' +
    'how to raise a ticket, and why the app sometimes insists on updating.',
  'www.guide.d18.step1':
    'TODO(ta) Add an emergency contact in your profile — a name and a phone number, ' +
    'picked from your contacts or typed in. You can change or remove it whenever you ' +
    'like.',
  'www.guide.d18.step1.note':
    'TODO(ta) Do it now rather than later. SOS sends its alert to that contact, and with ' +
    'no contact saved it will refuse — which you do not want to discover at the moment ' +
    'you press it.',
  'www.guide.d18.step2':
    'TODO(ta) SOS is on the trip screen while a trip is running. Tapping it opens a ' +
    'confirmation with a countdown, so an accidental press can be cancelled.',
  'www.guide.d18.step3':
    'TODO(ta) On confirming, a text message with your location and the trip details goes ' +
    'to your emergency contact within seconds, and MageRide’s safety team is alerted at ' +
    'the same time.',
  'www.guide.d18.step4':
    'TODO(ta) Help and support has the answers to the common questions first — wallet ' +
    'top-ups, the daily fee, registering a vehicle — and your open tickets below them.',
  'www.guide.d18.step5':
    'TODO(ta) Raise a ticket opens a short form: describe the problem, attach a ' +
    'screenshot, and pick the trip it is about from a list of your past trips. You can ' +
    'then follow the ticket and see the reply on it.',
  'www.guide.d18.step5.note':
    'TODO(ta) There is a quick action on the same screen for a daily fee charged in error ' +
    '— for example if the app crashed as you went online. That is a refund request, and ' +
    'it is the right way to raise one.',
  'www.guide.d18.step6':
    'TODO(ta) Passengers have their own safety tools and they affect you: a passenger can ' +
    'report a vehicle, and three reports flag a driver for review and a temporary ' +
    'delisting. A passenger can also block a driver, after which they are never matched ' +
    'with them again.',
  'www.guide.d18.step7':
    'TODO(ta) If you lose signal mid-trip, keep driving. Your positions are stored on the ' +
    'phone and sent in order when the signal comes back, so the route stays correct — a ' +
    'trip that looks frozen is usually the map waiting rather than the trip failing.',
  'www.guide.d18.step8':
    'TODO(ta) When MageRide releases an important update the app will insist on it and ' +
    'stop working until you have it, so that it and the platform still agree with each ' +
    'other. An ordinary update is only a banner you can dismiss.',
  'www.guide.d18.step8.note':
    'TODO(ta) Both send you to your phone’s app store. The driver app is not published ' +
    'there yet — when it is, the download page here will say so.',
  'www.guide.d18.callout.setTheContactFirst':
    'TODO(ta) SOS will not send without an emergency contact saved in your profile. It ' +
    'takes a minute to add and it is the difference between a working safety button and ' +
    'one that refuses at the worst possible moment.',
  'www.guide.d18.callout.everySosIsLogged':
    'TODO(ta) Every SOS is recorded with its time, its location and the trip it happened ' +
    'on, and kept for MageRide to review and to hand to the authorities if it is needed. ' +
    'It alerts your own contact and MageRide — it is not a call to the police, and it ' +
    'does not replace one.',
  'www.guide.d18.callout.feeChargedInError':
    'TODO(ta) If a daily fee was taken when it should not have been, ask for it back from ' +
    'Support rather than absorbing it. There is a specific request for exactly that, and ' +
    'the example the specification gives is the app crashing as you went online.',

  // The fleet-owner guide (S23), deferred with the rest of Tamil by MCS-34 D2.
  // English behind a TODO marker: the keys are present, so `en.ts` and this table
  // stay in step and the compiler still refuses a gap, and `check-i18n-parity.mjs`
  // counts these among the outstanding strings on every build. What is deferred is
  // the quality of the translation, not its presence — and `/ta` is not published,
  // so none of this is served under a Tamil `lang` attribute.

  'www.guide.f01.title': 'TODO(ta) Registering your organisation',
  'www.guide.f01.summary':
    'TODO(ta) Signing up for the fleet portal, submitting your organisation’s KYC, and the ' +
    'approval you have to wait for before anything else in this guide will work.',
  'www.guide.f01.step1':
    'TODO(ta) Open fleet.mageride.lk in a browser. The fleet portal is a website rather than ' +
    'an app, and it is built for a phone screen as well as a desktop, so either will do.',
  'www.guide.f01.step2':
    'TODO(ta) Sign up with an email address and a password, with Google, or with Apple. All ' +
    'three lead to the same fleet account, and you can link the others to it later.',
  'www.guide.f01.step2.note':
    'TODO(ta) This is not how drivers sign in. The driver app uses a phone number and a ' +
    'one-time code; the fleet portal uses email, Google or Apple. If you drive as well as run ' +
    'a fleet, you have two separate ways in.',
  'www.guide.f01.step3':
    'TODO(ta) Confirm your email address — until you do, your access is limited. The same ' +
    'screen is where you reset a password you have forgotten.',
  'www.guide.f01.step4':
    'TODO(ta) Fill in the organisation profile and upload your KYC documents: the business ' +
    'name and registration, a contact, and identification for the authorised person.',
  'www.guide.f01.step5':
    'TODO(ta) Submit it, and wait. A Verification Officer at MageRide reviews your ' +
    'organisation the same way a driver’s own vehicle is reviewed.',
  'www.guide.f01.step5.note':
    'TODO(ta) While you wait, your organisation is Pending. You can look around the portal, ' +
    'but you cannot onboard a vehicle or assign a driver yet.',
  'www.guide.f01.step6':
    'TODO(ta) If your KYC is rejected you are given a reason. Correct the documents and ' +
    'submit again — nothing else you have entered is lost.',
  'www.guide.f01.step7':
    'TODO(ta) Once you are approved, invite your team. Each member signs in with their own ' +
    'email, Google or Apple credentials, and you choose whether they are a Manager or a ' +
    'Viewer.',
  'www.guide.f01.step8':
    'TODO(ta) Set your language. The fleet portal is in Sinhala, Tamil and English, the same ' +
    'as the rest of MageRide.',
  'www.guide.f01.callout.approvalGate':
    'TODO(ta) Approval is a gate, not a formality. Until a Verification Officer approves your ' +
    'organisation, adding vehicles and assigning drivers are switched off. If you are ' +
    'planning a start date, begin here and leave time for it.',
  'www.guide.f01.callout.threeSubRoles':
    'TODO(ta) There are three kinds of team member — Owner, Manager and Viewer — and you are ' +
    'the Owner. Chapter 5 sets out which parts of this guide each of the three can actually ' +
    'act on. It is worth reading before you invite anyone.',
  'www.guide.f01.callout.whoSeesYourKyc':
    'TODO(ta) Your organisation’s KYC is reviewed by a MageRide Verification Officer, on the ' +
    'same approval path a driver’s own documents take. A rejection comes back to you with a ' +
    'reason for it.',

  'www.guide.f02.title': 'TODO(ta) KYC, and the bank and payout profile',
  'www.guide.f02.summary':
    'TODO(ta) Where the money from Mode B subscribers actually lands, the two files the ' +
    'portal asks you to upload, and the one verification that has to pass before you can ' +
    'charge anybody.',
  'www.guide.f02.step1':
    'TODO(ta) Open Bank and payout details. It is reached from organisation setup, and only ' +
    'the Owner can open it.',
  'www.guide.f02.step1.note':
    'TODO(ta) A Manager cannot see this screen at all. If somebody on your team says the link ' +
    'is not there, that is why.',
  'www.guide.f02.step2':
    'TODO(ta) Enter your bank, your branch, your account number and the account holder’s ' +
    'name.',
  'www.guide.f02.step2.note':
    'TODO(ta) The account holder’s name has to match the name on your organisation’s KYC. A ' +
    'personal account in a different name will not pass verification.',
  'www.guide.f02.step3':
    'TODO(ta) Upload a copy of your latest bank statement, or the first page of your ' +
    'passbook. This is what shows the account belongs to the organisation.',
  'www.guide.f02.step4':
    'TODO(ta) Upload the LankaQR code image your bank app generates. This one is not ' +
    'paperwork — it is the exact image a subscriber will scan when they pay you.',
  'www.guide.f02.step5':
    'TODO(ta) Submit. The profile goes to Pending verification and a Verification Officer ' +
    'reviews it in the same queue as your organisation. It comes back Verified, or Rejected ' +
    'with a reason.',
  'www.guide.f02.step6':
    'TODO(ta) Once it is Verified, that is where Mode B subscription payments go. A ' +
    'subscriber’s payment screen shows your LankaQR code to scan, and your verified account ' +
    'details for an online transfer.',
  'www.guide.f02.step7':
    'TODO(ta) If you change anything later — a branch, an account number, the QR image — the ' +
    'profile returns to Pending verification.',
  'www.guide.f02.step7.note':
    'TODO(ta) Nothing breaks while it is pending. People paying you keep seeing the details ' +
    'that were last verified, never a half-finished edit.',
  'www.guide.f02.callout.paidNeedsVerified':
    'TODO(ta) Until this profile is Verified you cannot set a vehicle’s service payment to ' +
    'Paid, and a Paid subscription cannot start billing. If you intend to charge for a ' +
    'vehicle, finish this before you price anything or invite a single subscriber.',
  'www.guide.f02.callout.passThrough':
    'TODO(ta) Subscription money from your Mode B passengers goes to you, not to MageRide. ' +
    'MageRide never holds it. That is also why the account is verified first — there is ' +
    'nobody in the middle to catch a wrong account number.',
  'www.guide.f02.callout.whatSubscribersSee':
    'TODO(ta) A subscriber’s payment screen shows two things from this profile: your LankaQR ' +
    'image, and your verified account details. The statement or passbook page you upload is ' +
    'for the Verification Officer, not for that screen.',

  'www.guide.f03.title': 'TODO(ta) Adding vehicles — one at a time, and in bulk',
  'www.guide.f03.summary':
    'TODO(ta) Putting vehicles into your fleet, the setting a private vehicle needs before ' +
    'anyone can subscribe to it, and what a bulk upload does and does not finish.',
  'www.guide.f03.step1':
    'TODO(ta) Open Vehicle onboarding. An Owner or a Manager can do this, and your ' +
    'organisation has to be approved first.',
  'www.guide.f03.step2':
    'TODO(ta) Add a vehicle on its own, or upload a spreadsheet and add many at once.',
  'www.guide.f03.step2.note':
    'TODO(ta) A fleet runs public transport and private vehicles — Mode A and Mode B. ' +
    'On-demand driving, Mode C, is not something a fleet does at all; that is an arrangement ' +
    'between an individual driver and MageRide.',
  'www.guide.f03.step3':
    'TODO(ta) A Mode A vehicle — a bus on a route — has nothing to price. Public transport is ' +
    'free.',
  'www.guide.f03.step4':
    'TODO(ta) A Mode B vehicle needs a service payment setting: Free or Paid. A staff bus or ' +
    'an office van is usually Free. A Paid vehicle also needs a default monthly fare.',
  'www.guide.f03.step4.note':
    'TODO(ta) You can only choose Paid once your bank and payout profile is Verified. That is ' +
    'chapter 2.',
  'www.guide.f03.step5':
    'TODO(ta) To add many vehicles at once, upload the spreadsheet and let it validate. Rows ' +
    'with problems are flagged rather than silently dropped.',
  'www.guide.f03.step6':
    'TODO(ta) Download the error report. It names the row and what is wrong with it, so you ' +
    'fix and re-upload only the rows that failed.',
  'www.guide.f03.step7':
    'TODO(ta) Vehicles added in bulk arrive with their documents still outstanding. Documents ' +
    'are per vehicle, and they are chapter 4.',
  'www.guide.f03.step8':
    'TODO(ta) Every vehicle then goes through the same approval as any other vehicle on ' +
    'MageRide and shows Pending, Approved or Rejected. You can also deactivate or remove one, ' +
    'which takes it off the fleet and passenger maps immediately.',
  'www.guide.f03.callout.modeAandBOnly':
    'TODO(ta) A fleet is Mode A and Mode B only. Adding an on-demand vehicle here is refused, ' +
    'and that is deliberate rather than a limitation: Mode C runs from an individual driver’s ' +
    'own app and their own wallet.',
  'www.guide.f03.callout.paidNeedsVerifiedProfile':
    'TODO(ta) Paid needs a Verified payout profile. Setting up forty vehicles and finding at ' +
    'the end that none of them can charge is the expensive version of this. Chapter 2 first ' +
    'is the cheap one.',
  'www.guide.f03.callout.renamedLabel':
    'TODO(ta) The setting is called service payment. It used to be called the Mode B ' +
    'classification and you may still meet that name in older material. Same setting, same ' +
    'two values — Free and Paid.',

  'www.guide.f04.title': 'TODO(ta) Vehicle documents, and what gates approval',
  'www.guide.f04.summary':
    'TODO(ta) The four documents a vehicle needs, which of them apply to which kind of ' +
    'vehicle, and why a vehicle can sit at Pending with everything else filled in.',
  'www.guide.f04.step1':
    'TODO(ta) Open the vehicle and find its document slots. There are four, each named for ' +
    'the document that goes in it.',
  'www.guide.f04.step2':
    'TODO(ta) Upload the registration copy — the CR book — the insurance certificate, and the ' +
    'revenue licence. Every vehicle needs all three.',
  'www.guide.f04.step2.note':
    'TODO(ta) Insurance is required for every mode. There is no kind of MageRide vehicle that ' +
    'is exempt from it.',
  'www.guide.f04.step3':
    'TODO(ta) If the vehicle runs public transport, upload the route permit as well. Mode A ' +
    'needs one; the other modes do not.',
  'www.guide.f04.step4':
    'TODO(ta) Each document is read automatically as it uploads. The registration number is ' +
    'checked against the plate, and expiry dates, permit numbers and routes are picked out ' +
    'for you.',
  'www.guide.f04.step5':
    'TODO(ta) Each slot then carries its own status — Verified, Pending or Missing. Read the ' +
    'slots, not only the vehicle.',
  'www.guide.f04.step6':
    'TODO(ta) The vehicle cannot be approved while a required document is Missing or Pending. ' +
    'A document that comes back Rejected is uploaded again.',
  'www.guide.f04.step7':
    'TODO(ta) Watch the expiry dates. A required document that expires takes that vehicle out ' +
    'of service until it is replaced.',
  'www.guide.f04.callout.approvalIsGated':
    'TODO(ta) A vehicle with every field filled in and one document still Pending will not be ' +
    'approved. When a vehicle is stuck, the answer is almost always in a document slot rather ' +
    'than in the vehicle’s own status.',
  'www.guide.f04.callout.insuranceEveryMode':
    'TODO(ta) Insurance is mandatory for every mode — a public bus, a private van, an ' +
    'on-demand car. The route permit is the one document that is mode-specific, and only Mode ' +
    'A needs it.',
  'www.guide.f04.callout.expiryStopsDispatch':
    'TODO(ta) An expired document does not merely show a warning. The vehicle is suspended ' +
    'automatically until the document is replaced, so a revenue licence that lapses over a ' +
    'weekend takes a vehicle off the road on Monday.',

  'www.guide.f05.title': 'TODO(ta) Assigning drivers, and binding trackers',
  'www.guide.f05.summary':
    'TODO(ta) The two ways a fleet vehicle reports where it is, how an assignment looks from ' +
    'the driver’s side, and which of your team members can do any of it.',
  'www.guide.f05.step1':
    'TODO(ta) Open Driver assignment and assign a driver to a vehicle by their user ID or ' +
    'their phone number.',
  'www.guide.f05.step1.note':
    'TODO(ta) The driver has to be a MageRide driver already. You are linking an existing ' +
    'driver to your vehicle, not creating an account for them.',
  'www.guide.f05.step2':
    'TODO(ta) On the driver’s side, the vehicle appears in a group of its own — vehicles ' +
    'temporarily assigned to them — showing which fleet assigned it and for how long. They ' +
    'select it and go online with it, one vehicle at a time.',
  'www.guide.f05.step3':
    'TODO(ta) The assignment history shows who has been on which vehicle, so you can look it ' +
    'up rather than remember it.',
  'www.guide.f05.step4':
    'TODO(ta) To end an assignment, revoke it. The driver immediately loses the ability to ' +
    'start a new session on that vehicle.',
  'www.guide.f05.step4.note':
    'TODO(ta) A session already running is either allowed to finish or ended, depending on ' +
    'how you choose to handle it. Assignments also expire on their own.',
  'www.guide.f05.step5':
    'TODO(ta) To use a hardware tracker instead, open Tracker binding and enter the device’s ' +
    'IMEI or MAC address.',
  'www.guide.f05.step6':
    'TODO(ta) Turn on automatic sessions. A vehicle with a tracker bound to it starts and ' +
    'ends its journey by itself.',
  'www.guide.f05.step6.note':
    'TODO(ta) That is the point of a tracker. Nobody has to remember to press Start, and a ' +
    'bus that leaves the depot is already reporting.',
  'www.guide.f05.step7':
    'TODO(ta) Set how often the tracker reports — often during your operating hours, sparsely ' +
    'outside them. It is a balance between how fresh the map is and how much data the device ' +
    'uses.',
  'www.guide.f05.callout.whoCanDoWhat':
    'TODO(ta) Who can do what, across this guide. An Owner can do all six chapters. A Manager ' +
    'can do chapters 3, 4 and 5 — vehicles, documents, drivers and trackers — but not the ' +
    'payout profile in chapter 2 or the billing in chapter 6. A Viewer acts on none of them: ' +
    'a Viewer reads the live map and the analytics.',
  'www.guide.f05.callout.revoking':
    'TODO(ta) Revoking is immediate for new work and not necessarily for work already under ' +
    'way. Tell the driver, rather than revoking mid-route and leaving them to discover it.',
  'www.guide.f05.callout.scopedToYourOrg':
    'TODO(ta) You see your own organisation’s vehicles and nobody else’s. That is enforced by ' +
    'the platform rather than by the screen — another fleet’s vehicles are not merely hidden ' +
    'from your map, they are not available to it.',

  'www.guide.f06.title': 'TODO(ta) Billing — a monthly charge per private vehicle',
  'www.guide.f06.summary':
    'TODO(ta) What your fleet pays MageRide and what it does not, how to keep the fleet ' +
    'wallet funded, and why the money your subscribers pay you never appears on this invoice.',
  'www.guide.f06.step1':
    'TODO(ta) The dashboard shows two numbers you will look at often: your fleet wallet ' +
    'balance, and the next monthly invoice.',
  'www.guide.f06.step2':
    'TODO(ta) Open Billing and wallet for the invoice itself. It is one invoice for the whole ' +
    'fleet with a line for each vehicle, so you pay once and can still see where the amount ' +
    'came from.',
  'www.guide.f06.step3':
    'TODO(ta) Only Mode B vehicles appear as charges. Mode A vehicles — public buses — are ' +
    'free.',
  'www.guide.f06.step3.note':
    'TODO(ta) If half your fleet is buses, half your fleet costs nothing. The invoice will be ' +
    'shorter than the vehicle list, and that is correct.',
  'www.guide.f06.step4':
    'TODO(ta) Top up the fleet wallet with a credit or debit card, with OnePay, or with ' +
    'LankaQR.',
  'www.guide.f06.step5':
    'TODO(ta) There is no bank transfer. If that is what you were expecting, it is not an ' +
    'outage — the method was removed from the platform.',
  'www.guide.f06.step6': 'TODO(ta) Download the receipt for your own records.',
  'www.guide.f06.step7':
    'TODO(ta) The money your Mode B subscribers pay you is a different flow entirely. It goes ' +
    'to the verified payout profile from chapter 2 — not to MageRide, and not against this ' +
    'invoice.',
  'www.guide.f06.step7.note':
    'TODO(ta) Two directions, two places. What you pay MageRide is here. What your ' +
    'subscribers pay you is in the subscription screens, and it lands in your own bank ' +
    'account.',
  'www.guide.f06.callout.whatTheFleetPays':
    'TODO(ta) What a fleet pays MageRide, in full: a monthly charge for each Mode B vehicle. ' +
    'Public transport vehicles are free. There is no per-trip fee and no commission on ' +
    'anything.',
  'www.guide.f06.callout.modeCIsNotYours':
    'TODO(ta) The daily platform fee is not yours to pay. That is the on-demand fee, it comes ' +
    'out of an individual driver’s own wallet in the driver app, and it never touches a fleet ' +
    'wallet — not even when that driver also drives one of your vehicles. Two different fee ' +
    'models, and they are not added together.',
  'www.guide.f06.callout.moneyInIsSeparate':
    'TODO(ta) Subscription payments from your passengers are not a MageRide charge and are ' +
    'not credited against this invoice. They are paid to you directly, into the account you ' +
    'verified in chapter 2.',
};
